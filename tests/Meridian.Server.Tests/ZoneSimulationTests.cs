using Meridian.Core.World;
using Meridian.Server.Zone;
using Meridian.Wire;

namespace Meridian.Server.Tests;

/// <summary>Captures everything the simulation sends.</summary>
internal sealed class RecordingOutbox : IZoneOutbox
{
    public List<(ClientRef Client, Delivery Delivery, byte[] Packet)> Sent { get; } = [];

    public void Send(ClientRef client, Delivery delivery, ReadOnlySpan<byte> packet) =>
        Sent.Add((client, delivery, packet.ToArray()));

    public List<T> Of<T>(ClientRef client)
        where T : struct, IWireMessage<T> =>
        Sent.Where(s => s.Client == client && Envelope.PeekId(s.Packet) == T.MessageId)
            .Select(s => Envelope.Decode<T>(s.Packet))
            .ToList();

    public T Last<T>(ClientRef client)
        where T : struct, IWireMessage<T> => Of<T>(client)[^1];
}

public class ZoneSimulationTests
{
    private static readonly ClientRef A = new(1, 1);
    private static readonly ClientRef B = new(1, 2);

    private static (ZoneSimulation Sim, RecordingOutbox Out) NewZone(ZoneOptions? options = null)
    {
        var outbox = new RecordingOutbox();
        return (new ZoneSimulation(options ?? new ZoneOptions(), outbox), outbox);
    }

    private static void Send<T>(ZoneSimulation sim, ClientRef from, in T message)
        where T : struct, IWireMessage<T> => sim.HandleClientPacket(from, Envelope.ToArray(message));

    [Fact]
    public void Joining_sends_welcome_with_spawn_position()
    {
        var (sim, outbox) = NewZone();
        var p = sim.Join(A, 42, "Ýrsa");
        var welcome = outbox.Last<Welcome>(A);
        Assert.Equal(p.EntityId, welcome.EntityId);
        Assert.Equal(20, welcome.TickRateHz);
        Assert.Equal(new Position(welcome.X, welcome.Z), p.Position);
    }

    [Fact]
    public void Joining_twice_is_rejected()
    {
        var (sim, _) = NewZone();
        sim.Join(A, 1, "a");
        Assert.Throws<InvalidOperationException>(() => sim.Join(A, 1, "a"));
    }

    [Fact]
    public void Move_intent_moves_at_run_speed()
    {
        var (sim, _) = NewZone();
        var p = sim.Join(A, 1, "a");
        var start = p.Position;
        for (var i = 0; i < 20; i++)
        {
            if (i % 2 == 0)
            {
                // Clients resend input at 10 Hz while a key is held.
                Send(sim, A, new MoveIntent { Sequence = (uint)i + 1, DirX = Movement.DirectionScale });
            }
            sim.Step(); // 20 ticks x 50 ms = 1 s
        }
        Assert.Equal(start with { X = start.X + 5_000 }, p.Position);
    }

    [Fact]
    public void Stale_intents_are_ignored()
    {
        var (sim, _) = NewZone();
        var p = sim.Join(A, 1, "a");
        Send(sim, A, new MoveIntent { Sequence = 5, DirX = 100 });
        Send(sim, A, new MoveIntent { Sequence = 4, DirX = -100 });
        Assert.Equal(100, p.DirX);
        Assert.Equal(5u, p.LastInputSequence);
    }

    [Fact]
    public void Player_stops_when_intents_stop_arriving()
    {
        var (sim, _) = NewZone(new ZoneOptions { IntentTimeoutTicks = 3 });
        var p = sim.Join(A, 1, "a");
        Send(sim, A, new MoveIntent { Sequence = 1, DirZ = Movement.DirectionScale });
        for (var i = 0; i < 10; i++)
        {
            sim.Step();
        }
        var stopped = p.Position;
        sim.Step();
        Assert.Equal(stopped, p.Position);
        Assert.Equal(0, p.DirZ);
    }

    [Fact]
    public void Each_player_gets_a_snapshot_every_other_tick_including_itself()
    {
        var (sim, outbox) = NewZone();
        var a = sim.Join(A, 1, "a");
        sim.Join(B, 2, "b");
        for (var i = 0; i < 20; i++)
        {
            sim.Step();
        }
        var snaps = outbox.Of<Snapshot>(A);
        Assert.Equal(10, snaps.Count);
        Assert.Contains(snaps[^1].Entities!, e => e.EntityId == a.EntityId);
        Assert.Equal(2, snaps[^1].Entities!.Length);
        Assert.Equal(10, outbox.Of<Snapshot>(B).Count);
    }

    [Fact]
    public void Snapshots_report_the_last_applied_input()
    {
        var (sim, outbox) = NewZone();
        sim.Join(A, 1, "a");
        Send(sim, A, new MoveIntent { Sequence = 77 });
        sim.Step();
        sim.Step();
        Assert.Equal(77u, outbox.Last<Snapshot>(A).AckedInputSequence);
    }

    [Fact]
    public void Entities_outside_the_interest_radius_are_not_sent_and_leaving_view_is_reported()
    {
        var (sim, outbox) = NewZone(new ZoneOptions { InterestRadiusMm = 10_000, IntentTimeoutTicks = 1_000 });
        var a = sim.Join(A, 1, "a");
        var b = sim.Join(B, 2, "b");
        a.Position = new Position(0, 0);
        b.Position = new Position(5_000, 0);
        sim.Step();
        sim.Step();
        Assert.Contains(outbox.Last<Snapshot>(A).Entities!, e => e.EntityId == b.EntityId);

        b.Position = new Position(50_000, 0);
        sim.Step();
        sim.Step();
        var snap = outbox.Last<Snapshot>(A);
        Assert.DoesNotContain(snap.Entities!, e => e.EntityId == b.EntityId);
        Assert.Contains(b.EntityId, snap.Removed!);

        sim.Step();
        sim.Step();
        Assert.Empty(outbox.Last<Snapshot>(A).Removed!); // reported once
    }

    [Fact]
    public void Snapshot_is_capped_to_the_nearest_entities()
    {
        var (sim, outbox) = NewZone(new ZoneOptions { MaxEntitiesPerSnapshot = 3 });
        var players = Enumerable.Range(1, 6).Select(i => sim.Join(new ClientRef(1, (uint)i), (ulong)i, $"p{i}")).ToList();
        for (var i = 0; i < players.Count; i++)
        {
            players[i].Position = new Position(i * 1_000, 0);
        }
        sim.Step();
        sim.Step();
        var ids = outbox.Last<Snapshot>(players[0].Client).Entities!.Select(e => e.EntityId);
        Assert.Equal(players.Take(3).Select(p => p.EntityId), ids);
    }

    [Fact]
    public void Leaving_removes_the_player_from_other_views()
    {
        var (sim, outbox) = NewZone();
        sim.Join(A, 1, "a");
        var b = sim.Join(B, 2, "b");
        sim.Step();
        sim.Step();
        Assert.True(sim.Leave(B));
        Assert.False(sim.Leave(B));
        sim.Step();
        sim.Step();
        Assert.Contains(b.EntityId, outbox.Last<Snapshot>(A).Removed!);
    }

    [Fact]
    public void Chat_reaches_players_in_range_only()
    {
        var (sim, outbox) = NewZone(new ZoneOptions { ChatRadiusMm = 10_000 });
        var a = sim.Join(A, 1, "Ýrsa");
        var b = sim.Join(B, 2, "b");
        var c = sim.Join(new ClientRef(1, 3), 3, "c");
        a.Position = new Position(0, 0);
        b.Position = new Position(9_000, 0);
        c.Position = new Position(20_000, 0);

        Send(sim, A, new ChatSay { Text = "  hello  " });
        var heardByB = Assert.Single(outbox.Of<ChatMessage>(B));
        Assert.Equal("hello", heardByB.Text);
        Assert.Equal("Ýrsa", heardByB.FromName);
        Assert.Single(outbox.Of<ChatMessage>(A));
        Assert.Empty(outbox.Of<ChatMessage>(c.Client));
    }

    [Fact]
    public void Blank_chat_is_dropped()
    {
        var (sim, outbox) = NewZone();
        sim.Join(A, 1, "a");
        Send(sim, A, new ChatSay { Text = "   " });
        Assert.Empty(outbox.Of<ChatMessage>(A));
    }

    [Fact]
    public void Ping_is_answered_with_the_client_time()
    {
        var (sim, outbox) = NewZone();
        sim.Join(A, 1, "a");
        sim.Step();
        Send(sim, A, new Ping { ClientTimeMs = 1234 });
        var pong = outbox.Last<Pong>(A);
        Assert.Equal(1234u, pong.ClientTimeMs);
        Assert.Equal(1u, pong.ServerTick);
    }

    [Fact]
    public void Non_client_messages_from_a_client_are_rejected()
    {
        var (sim, _) = NewZone();
        sim.Join(A, 1, "a");
        Assert.Throws<WireFormatException>(() => sim.HandleClientPacket(A, Envelope.ToArray(new PlayerLeave { ConnectionId = 1 })));
    }

    [Fact]
    public void Packets_from_unknown_clients_are_ignored()
    {
        var (sim, outbox) = NewZone();
        sim.HandleClientPacket(A, Envelope.ToArray(new Ping()));
        Assert.Empty(outbox.Sent);
    }

    [Fact]
    public void Gateway_loss_removes_only_its_players()
    {
        var (sim, _) = NewZone();
        sim.Join(new ClientRef(1, 1), 1, "a");
        sim.Join(new ClientRef(1, 2), 2, "b");
        sim.Join(new ClientRef(2, 1), 3, "c");
        Assert.Equal(2, sim.LeaveGateway(1));
        Assert.Equal(1, sim.PlayerCount);
    }
}
