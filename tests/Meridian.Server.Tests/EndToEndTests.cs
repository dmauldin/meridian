using System.Diagnostics;
using System.Security.Cryptography;
using Meridian.Core.World;
using Meridian.Net;
using Meridian.Net.Client;
using Meridian.Persistence;
using Meridian.Server.Auth;
using Meridian.Server.Gateway;
using Meridian.Server.Zone;
using Meridian.Wire;
using Meridian.Wire.Delta;

namespace Meridian.Server.Tests;

/// <summary>
/// Zone + gateway on real UDP sockets in this process, with clients built on the same
/// <see cref="GameConnection"/> the bots and the game client use.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private const string InternalKey = "test-internal-key";
    private static readonly byte[] TicketKey = RandomNumberGenerator.GetBytes(32);

    private readonly CancellationTokenSource _stop = new();
    private readonly ZoneHost _zone;
    private readonly GatewayHost _gateway;
    private readonly Thread _zoneThread;
    private readonly Thread _gatewayThread;
    private readonly InMemoryGameStore _store = new();
    private readonly TicketSealer _sealer = new(TicketKey);
    private readonly AuthService _auth;

    public EndToEndTests()
    {
        _zone = new ZoneHost(new ZoneHostOptions { Port = 0, InternalKey = InternalKey }, _store, _ => { });
        _zone.Start();
        _zoneThread = new Thread(() => _zone.Run(_stop.Token)) { IsBackground = true };
        _zoneThread.Start();

        _gateway = new GatewayHost(
            new GatewayOptions { ClientPort = 0, ZonePort = _zone.Port, InternalKey = InternalKey, TicketKey = TicketKey },
            _ => { });
        _gateway.Start();
        _gatewayThread = new Thread(() => _gateway.Run(_stop.Token)) { IsBackground = true };
        _gatewayThread.Start();

        _auth = new AuthService(
            new AuthOptions { PublicGatewayPort = _gateway.Port },
            _store,
            new PasswordHasher(1_000),
            _sealer,
            TimeProvider.System,
            Meridian.Content.ContentPacks.Embedded);

        WaitUntil(() => _gateway.ZoneConnected, "gateway to reach zone");
    }

    [Fact]
    public void Client_logs_in_moves_and_receives_snapshots()
    {
        var login = Play("tester");
        var client = new TestClient();
        using var conn = new GameConnection(client);
        conn.Connect("127.0.0.1", login.GatewayPort, login);

        Pump(conn, () => client.Welcome is not null, "welcome");
        conn.Send(new MoveIntent { Sequence = 1, DirX = 32_767 });
        Pump(conn, () => client.Snapshots.Count >= 5, "snapshots");

        var self = client.Snapshots[^1].Entities.Single(e => e.EntityId == client.Welcome!.Value.EntityId);
        Assert.True(self.X > client.Welcome!.Value.X, "player should have moved +X");
        Assert.Equal(1u, client.Snapshots[^1].AckedInputSequence);
    }

    [Fact]
    public void Two_clients_see_and_hear_each_other()
    {
        var a = new TestClient();
        var b = new TestClient();
        using var ca = Connect(a, "alice");
        using var cb = Connect(b, "bob");

        Pump([ca, cb], () => b.Welcome is not null && a.Snapshots.LastOrDefault()?.Entities.Any(e => e.EntityId == b.Welcome.Value.EntityId) == true, "a to see b");
        Assert.Equal("bob", a.Info[b.Welcome!.Value.EntityId].Name);
        ca.Send(new ChatSay { Text = "hello bob" });
        Pump([ca, cb], () => b.Chat.Contains("alice: hello bob"), "b to hear a");
    }

    /// <summary>The M1 gate: two players kill a mob together over the network.</summary>
    [Fact]
    public void Two_players_kill_a_mob_together_over_the_network()
    {
        var a = new TestClient();
        var b = new TestClient();
        using var ca = Connect(a, "hunterA");
        using var cb = Connect(b, "hunterB");
        Pump([ca, cb], () => a.Welcome is not null && b.Welcome is not null && a.Snapshots.Count > 0, "both in world");

        // Both pick the mob nearest to player A, so they fight the same one.
        var mobId = a.Info.Values.Where(i => i.Kind == 2)
            .Select(i => i.EntityId)
            .OrderBy(id => a.DistanceTo(id))
            .First();
        foreach (var conn in new[] { ca, cb })
        {
            conn.Send(new SetTarget { EntityId = mobId });
        }

        uint seq = 0;
        Pump([ca, cb], () =>
        {
            foreach (var (client, conn) in new[] { (a, ca), (b, cb) })
            {
                var (dx, dz) = client.DistanceTo(mobId) > 18_000 ? client.DirectionTo(mobId) : ((short)0, (short)0);
                conn.Send(new MoveIntent { Sequence = ++seq, DirX = dx, DirZ = dz });
                if (client.Status is { Attacking: false } && !client.Deaths.Any(d => d.EntityId == mobId))
                {
                    conn.Send(new SetAttack { Enabled = true });
                }
            }
            return a.Deaths.Any(d => d.EntityId == mobId);
        }, "the mob to die", seconds: 90);

        var aId = a.Welcome!.Value.EntityId;
        var bId = b.Welcome!.Value.EntityId;
        Assert.Contains(a.Combat, e => e.AttackerId == aId && e.TargetId == mobId && e.Outcome != 0);
        Assert.Contains(a.Combat, e => e.AttackerId == bId && e.TargetId == mobId && e.Outcome != 0);
        Assert.Contains(a.Deaths.Single(d => d.EntityId == mobId).KillerId, new[] { aId, bId });
    }

    [Fact]
    public void The_character_plays_as_the_profession_it_was_created_with()
    {
        var client = new TestClient();
        using var conn = new GameConnection(client);
        var login = Play("healer", "medic", "nightborn");
        conn.Connect("127.0.0.1", login.GatewayPort, login);
        Pump(conn, () => client.Sheet is not null, "character sheet");
        Assert.Equal((byte)Meridian.Core.Characters.Profession.Medic, client.Sheet!.Value.Profession);
        Assert.Equal((byte)Meridian.Core.Characters.Origin.Nightborn, client.Sheet.Value.Origin);
    }

    /// <summary>The M2.3 promise: what you did is still there when you come back.</summary>
    [Fact]
    public async Task Training_and_position_survive_logging_out_and_back_in()
    {
        var first = new TestClient();
        var play = Play("keeper");
        CharacterSheet trained;
        int x;
        using (var conn = new GameConnection(first))
        {
            conn.Connect("127.0.0.1", _gateway.Port, play);
            Pump(conn, () => first.Sheet is not null, "sheet");
            conn.Send(new TrainSkill { Stat = (ushort)Meridian.Core.Stats.Stat.Rifle, Points = 5 });
            conn.Send(new MoveIntent { Sequence = 1, DirX = 32_767 });
            Pump(conn, () => first.Sheet!.Value.UnspentPoints < 60 && first.Snapshots.Count > 10, "training and movement");
            conn.Send(new MoveIntent { Sequence = 2 });
            Pump(conn, () => first.Snapshots.Count > 15, "stop");
            trained = first.Sheet!.Value;
            x = first.Snapshots[^1].Entities.Single(e => e.EntityId == first.Welcome!.Value.EntityId).X;
            conn.Disconnect();
        }
        // Wait for the store itself, so the relog below exercises a real load, not the saver's queue.
        WaitUntil(() => _store.LoadCharacterAsync(play.CharacterId).GetAwaiter().GetResult()!.UnspentPoints == trained.UnspentPoints, "the save on leave");

        var again = new TestClient();
        using var back = new GameConnection(again);
        back.Connect("127.0.0.1", _gateway.Port, Play("keeper"));
        Pump(back, () => again.Sheet is not null, "sheet after relog");
        Assert.Equal(trained.UnspentPoints, again.Sheet!.Value.UnspentPoints);
        Assert.Equal(Stat(trained, Meridian.Core.Stats.Stat.Rifle), Stat(again.Sheet.Value, Meridian.Core.Stats.Stat.Rifle));
        Assert.InRange(again.Welcome!.Value.X, x - 1_000, x + 1_000);

        var stored = (await _store.LoadCharacterAsync(play.CharacterId, TestContext.Current.CancellationToken))!;
        Assert.Equal(trained.UnspentPoints, stored.UnspentPoints);
        Assert.Contains(new KeyValuePair<Meridian.Core.Stats.Stat, int>(Meridian.Core.Stats.Stat.Rifle, 5), stored.Trained);
        Assert.True(stored.Version >= 2, "the zone saved over the created record");
    }

    [Fact]
    public void An_instant_reconnect_keeps_progress_that_is_not_saved_yet()
    {
        var first = new TestClient();
        int points;
        using (var conn = new GameConnection(first))
        {
            conn.Connect("127.0.0.1", _gateway.Port, Play("hasty"));
            Pump(conn, () => first.Sheet is not null, "sheet");
            conn.Send(new TrainSkill { Stat = (ushort)Meridian.Core.Stats.Stat.Rifle, Points = 7 });
            Pump(conn, () => first.Sheet!.Value.UnspentPoints < 60, "training");
            points = first.Sheet!.Value.UnspentPoints;
            conn.Disconnect();
        }

        // No waiting for the save: the zone must use its unsaved copy (or the live player, if the
        // join beats the leave), never the store's older one.
        var again = new TestClient();
        using var back = new GameConnection(again);
        back.Connect("127.0.0.1", _gateway.Port, Play("hasty"));
        Pump(back, () => again.Sheet is not null, "sheet after reconnect");
        Assert.Equal(points, again.Sheet!.Value.UnspentPoints);
    }

    [Fact]
    public void Logging_in_again_takes_over_and_drops_the_old_session()
    {
        var oldClient = new TestClient();
        using var old = new GameConnection(oldClient);
        old.Connect("127.0.0.1", _gateway.Port, Play("twice"));
        Pump(old, () => oldClient.Welcome is not null, "first session");

        var newClient = new TestClient();
        using var fresh = new GameConnection(newClient);
        fresh.Connect("127.0.0.1", _gateway.Port, Play("twice"));
        Pump([old, fresh], () => newClient.Welcome is not null && old.DisconnectReason is not null, "takeover");
        Assert.Equal("Logged in from another session.", old.DisconnectReason);
        Assert.Single(_zone.Simulation.Players, p => p.Name == "twice");
    }

    [Fact]
    public void Forged_ticket_is_rejected_with_a_reason()
    {
        var login = Play("mallory");
        var forged = login with { Ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(100)) };
        using var conn = new GameConnection(new TestClient());
        conn.Connect("127.0.0.1", login.GatewayPort, forged);
        Pump(conn, () => conn.DisconnectReason is not null, "rejection");
        Assert.Equal("Login expired. Please log in again.", conn.DisconnectReason);
    }

    [Fact]
    public void A_ticket_works_only_once()
    {
        var login = Play("replayer");
        var first = new TestClient();
        using var c1 = new GameConnection(first);
        c1.Connect("127.0.0.1", login.GatewayPort, login);
        Pump(c1, () => first.Welcome is not null, "first welcome");

        using var c2 = new GameConnection(new TestClient());
        c2.Connect("127.0.0.1", login.GatewayPort, login);
        Pump(c2, () => c2.DisconnectReason is not null, "replay rejection");
        Assert.Equal("Login already used. Please log in again.", c2.DisconnectReason);
    }

    [Fact]
    public void Disconnecting_removes_the_player_from_the_zone()
    {
        var client = new TestClient();
        using var conn = Connect(client, "leaver");
        Pump(conn, () => client.Welcome is not null, "welcome");
        WaitUntil(() => _zone.Simulation.PlayerCount >= 1, "zone to count player");
        var before = _zone.Simulation.PlayerCount;
        conn.Disconnect();
        WaitUntil(() => _zone.Simulation.PlayerCount == before - 1, "zone to drop player");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _zoneThread.Join();
        _gatewayThread.Join();
        _gateway.Dispose();
        _zone.Dispose();
        _sealer.Dispose();
        _stop.Dispose();
    }

    /// <summary>Registers (if needed), logs in, creates the character (if needed) and gets a ticket: account and character share a name.</summary>
    private PlayResponse Play(string name, string profession = "operative", string origin = "baseline")
    {
        var credentials = new Credentials(name, "password1");
        _auth.RegisterAsync(credentials).GetAwaiter().GetResult();
        var login = _auth.LoginAsync(credentials).GetAwaiter().GetResult().Value!;
        var character = login.Characters.FirstOrDefault(c => c.Name == name)
            ?? _auth.CreateCharacterAsync(login.SessionToken, new CreateCharacterRequest(name, profession, origin)).GetAwaiter().GetResult().Value!;
        return _auth.PlayAsync(login.SessionToken, new PlayRequest(character.Id)).GetAwaiter().GetResult().Value!;
    }

    private static int Stat(CharacterSheet sheet, Meridian.Core.Stats.Stat stat) => sheet.Stats!.Single(s => s.Stat == (ushort)stat).BaseValue;

    private GameConnection Connect(TestClient client, string name)
    {
        var login = Play(name);
        var conn = new GameConnection(client);
        conn.Connect("127.0.0.1", login.GatewayPort, login);
        return conn;
    }

    private static void Pump(GameConnection conn, Func<bool> done, string what) => Pump([conn], done, what);

    private static void Pump(GameConnection[] conns, Func<bool> done, string what, int seconds = 10)
    {
        var deadline = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
        while (!done())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, $"timed out waiting for {what}");
            foreach (var c in conns)
            {
                c.Poll();
            }
            Thread.Sleep(2);
        }
    }

    private static void WaitUntil(Func<bool> done, string what)
    {
        var deadline = Stopwatch.GetTimestamp() + (10 * Stopwatch.Frequency);
        while (!done())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, $"timed out waiting for {what}");
            Thread.Sleep(5);
        }
    }

    private sealed class TestClient : IClientBoundHandler<GameConnection>
    {
        public Welcome? Welcome { get; private set; }

        private readonly SnapshotAssembler _assembler = new();

        public List<RebuiltSnapshot> Snapshots { get; } = [];

        public List<string> Chat { get; } = [];

        public void OnWelcome(GameConnection context, in Welcome message) => Welcome = message;

        public void OnDeltaSnapshot(GameConnection context, in DeltaSnapshot message)
        {
            if (_assembler.TryApply(message, out var entities))
            {
                context.Send(new SnapshotAck { Tick = message.Tick });
                Snapshots.Add(new RebuiltSnapshot(message.Tick, message.AckedInputSequence, [.. entities]));
            }
        }

        public void OnChatMessage(GameConnection context, in ChatMessage message) => Chat.Add($"{message.FromName}: {message.Text}");

        public void OnPong(GameConnection context, in Pong message)
        {
        }

        public void OnDisconnecting(GameConnection context, in Disconnecting message)
        {
        }

        public Dictionary<uint, EntityInfo> Info { get; } = [];

        private Position Self => Find(Welcome!.Value.EntityId);

        public long DistanceTo(uint id) => Movement.Distance(Self, Find(id));

        public (short X, short Z) DirectionTo(uint id) => Movement.DirectionTowards(Self, Find(id));

        private Position Find(uint id)
        {
            for (var i = Snapshots.Count - 1; i >= 0; i--)
            {
                foreach (var e in Snapshots[i].Entities)
                {
                    if (e.EntityId == id)
                    {
                        return new Position(e.X, e.Z);
                    }
                }
            }
            return Welcome is { } w && w.EntityId == id ? new Position(w.X, w.Z) : default;
        }

        public List<CombatEvent> Combat { get; } = [];

        public List<EntityDied> Deaths { get; } = [];

        public SelfStatus? Status { get; private set; }

        public void OnEntityInfo(GameConnection context, in EntityInfo message) => Info[message.EntityId] = message;

        public void OnCombatEvent(GameConnection context, in CombatEvent message) => Combat.Add(message);

        public void OnEntityDied(GameConnection context, in EntityDied message) => Deaths.Add(message);

        public void OnSelfStatus(GameConnection context, in SelfStatus message) => Status = message;

        public void OnRespawned(GameConnection context, in Respawned message)
        {
        }

        public CharacterSheet? Sheet { get; private set; }

        public List<XpGained> Experience { get; } = [];

        public List<string> Notices { get; } = [];

        public void OnCharacterSheet(GameConnection context, in CharacterSheet message) => Sheet = message;

        public void OnXpGained(GameConnection context, in XpGained message) => Experience.Add(message);

        public void OnNotice(GameConnection context, in Notice message) => Notices.Add(message.Text ?? "");
    }
}
