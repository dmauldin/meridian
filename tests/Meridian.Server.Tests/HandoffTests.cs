using System.Diagnostics;
using System.Security.Cryptography;
using Meridian.ClientState;
using Meridian.Content;
using Meridian.Core.World;
using Meridian.Net;
using Meridian.Net.Client;
using Meridian.Persistence;
using Meridian.Server.Auth;
using Meridian.Server.Gateway;
using Meridian.Server.Zone;

namespace Meridian.Server.Tests;

/// <summary>
/// Zone hand-off through the real stack: two zone processes (hosts) behind one gateway, a real
/// client session, and the shared store. The client never reconnects; its items and buffs
/// travel with it, and logging in again resumes in the zone it was saved in.
/// </summary>
public sealed class HandoffTests : IDisposable
{
    private const string InternalKey = "test-internal-key";
    private static readonly byte[] TicketKey = RandomNumberGenerator.GetBytes(32);
    private static readonly uint Glasswind = ContentPacks.Embedded.Zones["glasswind_flats"].Id;
    private static readonly uint Rustfall = ContentPacks.Embedded.Zones["rustfall_basin"].Id;

    private readonly CancellationTokenSource _stop = new();
    private readonly InMemoryGameStore _store = new();
    private readonly TicketSealer _sealer = new(TicketKey);
    private readonly List<ZoneHost> _zones = [];
    private readonly List<Thread> _threads = [];
    private readonly GatewayHost _gateway;
    private readonly AuthService _auth;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public HandoffTests()
    {
        foreach (var key in new[] { "glasswind_flats", "rustfall_basin" })
        {
            var zone = new ZoneHost(new ZoneHostOptions { Port = 0, InternalKey = InternalKey, Zone = new ZoneOptions { ZoneKey = key, MobCount = 0, DevCommands = true } }, _store, _ => { });
            zone.Start();
            _zones.Add(zone);
            Run(() => zone.Run(_stop.Token));
        }
        _gateway = new GatewayHost(
            new GatewayOptions
            {
                ClientPort = 0,
                Zones = [new ZoneEndpoint(Glasswind, "127.0.0.1", _zones[0].Port), new ZoneEndpoint(Rustfall, "127.0.0.1", _zones[1].Port)],
                InternalKey = InternalKey,
                TicketKey = TicketKey,
            },
            _ => { });
        _gateway.Start();
        Run(() => _gateway.Run(_stop.Token));
        _auth = new AuthService(new AuthOptions { PublicGatewayPort = _gateway.Port }, _store, new PasswordHasher(1_000), _sealer, TimeProvider.System, ContentPacks.Embedded);
        Until(() => _gateway.ZoneConnected, "gateway to reach both zones");
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var t in _threads)
        {
            t.Join(TimeSpan.FromSeconds(5));
        }
        _gateway.Dispose();
        foreach (var z in _zones)
        {
            z.Dispose();
        }
        _sealer.Dispose();
        _stop.Dispose();
    }

    [Fact]
    public void A_player_walks_into_the_next_zone_and_back_keeping_everything()
    {
        using var session = GameSession.Start(Play("walker"), "walker");
        var events = new List<GameEvent>();
        Pump(session, events, () => session.World.InWorld && session.World.Inventory is not null && session.World.Protocols is not null, "joined");
        Assert.Equal(Glasswind, session.World.ZoneId);

        var rifle = ContentPacks.Embedded.Protocols["buff.rifle_enhancement_1"].Id;
        session.Cast(rifle, onSelf: true);
        Pump(session, events, () => session.World.Protocols!.Running.Any(r => r.ProtocolId == rifle), "own buff");
        var worn = session.World.Inventory!.Worn.Single().Item;

        Wait(session, events, TimeSpan.FromMilliseconds((ZoneSimulation.ExitGraceTicks * 50) + 200)); // the join grace
        session.Say("/goto 62 0");
        Pump(session, events, () => events.OfType<ZoneEntered>().Any(e => e.ZoneId == Rustfall) && session.World.Protocols is not null, "arrival in Rustfall");

        Assert.Contains(events, e => e is ZoneChanging { ToZoneId: var to } && to == Rustfall);
        Assert.True(session.Connected, "the connection survives the hand-off");
        Assert.Equal(new Position(-50_000, 0), session.World.Self.Position);
        Pump(session, events, () => session.World.Inventory?.Worn.Count == 1 && session.World.Protocols!.Running.Count == 1, "items and buffs resent");
        Assert.Equal(worn, session.World.Inventory!.Worn.Single().Item);
        Assert.Contains(session.World.Protocols!.Running, r => r.ProtocolId == rifle && r.RemainingMs > 500_000);
        Assert.Equal(1, _gateway.Transfers);

        // And back again, from Rustfall's own exit.
        events.Clear();
        Wait(session, events, TimeSpan.FromMilliseconds((ZoneSimulation.ExitGraceTicks * 50) + 200));
        session.Say("/goto -62 0");
        Pump(session, events, () => events.OfType<ZoneEntered>().Any(e => e.ZoneId == Glasswind), "arrival back in Glasswind");
        Assert.Equal(new Position(50_000, 0), session.World.Self.Position);
        Assert.Equal(2, _gateway.Transfers);
    }

    [Fact]
    public void Logging_in_again_resumes_in_the_zone_the_character_was_saved_in()
    {
        var events = new List<GameEvent>();
        using (var first = GameSession.Start(Play("roamer"), "roamer"))
        {
            Pump(first, events, () => first.World.InWorld, "joined");
            Wait(first, events, TimeSpan.FromMilliseconds((ZoneSimulation.ExitGraceTicks * 50) + 200));
            first.Say("/goto 62 0");
            Pump(first, events, () => first.World.ZoneId == Rustfall && first.World.InWorld, "arrival in Rustfall");
            first.Connection.Disconnect();
        }
        Until(() => _zones[1].Simulation.PlayerCount == 0 && _zones[1].Saver.PendingCount == 0, "Rustfall to save the leaver");

        using var again = GameSession.Start(Play("roamer"), "roamer");
        events.Clear();
        Pump(again, events, () => again.World.InWorld, "rejoined");
        Assert.Equal(Rustfall, again.World.ZoneId); // straight to Rustfall: the ticket said so
        Assert.Equal(1, _gateway.Transfers);       // only the first trip was a transfer; the relog went straight there
    }

    private PlayResponse Play(string name)
    {
        var credentials = new Credentials(name, "password1");
        _auth.RegisterAsync(credentials).GetAwaiter().GetResult();
        var login = _auth.LoginAsync(credentials).GetAwaiter().GetResult().Value!;
        var character = login.Characters.FirstOrDefault(c => c.Name == name)
            ?? _auth.CreateCharacterAsync(login.SessionToken, new CreateCharacterRequest(name, "operative", "baseline")).GetAwaiter().GetResult().Value!;
        return _auth.PlayAsync(login.SessionToken, new PlayRequest(character.Id)).GetAwaiter().GetResult().Value!;
    }

    private void Run(Action body)
    {
        var t = new Thread(() => body()) { IsBackground = true };
        t.Start();
        _threads.Add(t);
    }

    private void Pump(GameSession session, List<GameEvent> events, Func<bool> done, string what)
    {
        var deadline = _clock.Elapsed + TimeSpan.FromSeconds(15);
        while (!done())
        {
            Assert.True(_clock.Elapsed < deadline, $"timed out waiting for {what}");
            Assert.True(session.Connected || session.DisconnectReason is null, $"disconnected: {session.DisconnectReason}");
            session.Update(_clock.Elapsed.TotalMilliseconds, 0, 0);
            events.AddRange(session.World.DrainEvents());
            Thread.Sleep(5);
        }
    }

    private void Wait(GameSession session, List<GameEvent> events, TimeSpan time)
    {
        var until = _clock.Elapsed + time;
        Pump(session, events, () => _clock.Elapsed >= until, "time to pass");
    }

    private static void Until(Func<bool> done, string what)
    {
        var deadline = Stopwatch.GetTimestamp() + (10 * Stopwatch.Frequency);
        while (!done())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, $"timed out waiting for {what}");
            Thread.Sleep(5);
        }
    }
}
