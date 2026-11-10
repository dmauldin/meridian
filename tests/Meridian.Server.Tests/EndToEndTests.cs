using System.Diagnostics;
using System.Security.Cryptography;
using Meridian.Core.World;
using Meridian.Net;
using Meridian.Net.Client;
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
    private readonly LoginService _logins;

    public EndToEndTests()
    {
        _zone = new ZoneHost(new ZoneHostOptions { Port = 0, InternalKey = InternalKey }, _ => { });
        _zone.Start();
        _zoneThread = new Thread(() => _zone.Run(_stop.Token)) { IsBackground = true };
        _zoneThread.Start();

        _gateway = new GatewayHost(
            new GatewayOptions { ClientPort = 0, ZonePort = _zone.Port, InternalKey = InternalKey, TicketKey = TicketKey },
            _ => { });
        _gateway.Start();
        _gatewayThread = new Thread(() => _gateway.Run(_stop.Token)) { IsBackground = true };
        _gatewayThread.Start();

        _logins = new LoginService(
            new AuthOptions { DevPassword = "pw", PublicGatewayPort = _gateway.Port },
            new TicketSealer(TicketKey),
            TimeProvider.System);

        WaitUntil(() => _gateway.ZoneConnected, "gateway to reach zone");
    }

    [Fact]
    public void Client_logs_in_moves_and_receives_snapshots()
    {
        var login = _logins.TryLogin(new LoginRequest("tester", "pw"))!;
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
    public void Forged_ticket_is_rejected_with_a_reason()
    {
        var login = _logins.TryLogin(new LoginRequest("mallory", "pw"))!;
        var forged = login with { Ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(100)) };
        using var conn = new GameConnection(new TestClient());
        conn.Connect("127.0.0.1", login.GatewayPort, forged);
        Pump(conn, () => conn.DisconnectReason is not null, "rejection");
        Assert.Equal("Login expired. Please log in again.", conn.DisconnectReason);
    }

    [Fact]
    public void A_ticket_works_only_once()
    {
        var login = _logins.TryLogin(new LoginRequest("replayer", "pw"))!;
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
    public void Wrong_password_gets_no_ticket()
    {
        Assert.Null(_logins.TryLogin(new LoginRequest("tester", "nope")));
        Assert.Null(_logins.TryLogin(new LoginRequest("x", "pw"))); // name too short
        Assert.Null(_logins.TryLogin(new LoginRequest("bad name", "pw")));
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
        _stop.Dispose();
    }

    private GameConnection Connect(TestClient client, string name)
    {
        var login = _logins.TryLogin(new LoginRequest(name, "pw"))!;
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
    }
}
