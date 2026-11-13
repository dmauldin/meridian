using Meridian.Core.World;
using Meridian.Wire;
using Meridian.Wire.Delta;

namespace Meridian.ClientState.Tests;

public class RemoteEntityTests
{
    private static RemoteEntity WithSamples(params (double Ms, int X, byte Yaw)[] samples)
    {
        var e = new RemoteEntity(7);
        foreach (var (ms, x, yaw) in samples)
        {
            e.AddSample(new EntitySample(ms, new Position(x, 0), yaw, 100, 0));
        }
        return e;
    }

    [Fact]
    public void Interpolates_linearly_between_samples()
    {
        var e = WithSamples((0, 0, 0), (100, 1_000, 0));
        Assert.Equal(250, e.PoseAt(25).X, 6);
        Assert.Equal(1_000, e.PoseAt(100).X, 6);
    }

    [Fact]
    public void Clamps_instead_of_extrapolating()
    {
        var e = WithSamples((0, 0, 0), (100, 1_000, 0));
        Assert.Equal(0, e.PoseAt(-50).X, 6);
        Assert.Equal(1_000, e.PoseAt(500).X, 6);
    }

    [Fact]
    public void Out_of_order_and_duplicate_samples_are_handled()
    {
        var e = WithSamples((100, 1_000, 0), (0, 0, 0), (100, 9_999, 0), (50, 500, 0));
        Assert.Equal(250, e.PoseAt(25).X, 6);
        Assert.Equal(1_000, e.Latest.Position.X);
    }

    [Fact]
    public void Yaw_takes_the_short_way_around()
    {
        var e = WithSamples((0, 0, 250), (100, 0, 6)); // 250 → 6 crosses zero: +12 steps, not -244
        var mid = e.PoseAt(50).YawRadians;
        Assert.Equal(RemoteEntity.YawToRadians(250) + (6 * Math.PI * 2 / 256), mid, 6);
    }

    [Fact]
    public void Mark_dead_updates_the_latest_sample()
    {
        var e = WithSamples((0, 0, 0));
        e.MarkDead();
        Assert.True(e.Latest.Dead);
        Assert.Equal(0, e.Latest.HealthPercent);
    }
}

public class LocalPlayerTests
{
    private const short Unit = Movement.DirectionScale;

    [Fact]
    public void Prediction_matches_the_server_step_for_the_same_input()
    {
        var p = new LocalPlayer();
        p.Teleport(new Position(0, 0), 0);
        for (var frame = 1; frame <= 60; frame++)
        {
            p.Advance(frame * 16.667, 16.667, Unit, 0, 5_000); // ~1 s at 60 fps
        }
        // 1000 ms at 5 m/s, fractional frame times carried over: within a millisecond of travel.
        Assert.InRange(p.Position.X, 4_995, 5_005);
    }

    [Fact]
    public void Small_errors_blend_and_large_errors_snap()
    {
        var p = new LocalPlayer();
        p.Teleport(new Position(0, 0), 0);

        p.Reconcile(new Position(1_000, 0), 10, 0);
        Assert.Equal(150, p.Position.X); // 15% of a 1 m error
        Assert.Equal(1_000, p.LastErrorMm);

        p.Reconcile(new Position(10_150, 0), 20, 0);
        Assert.Equal(10_150, p.Position.X); // 10 m: snap
    }

    [Fact]
    public void Reconcile_compares_with_the_prediction_from_one_round_trip_ago()
    {
        var p = new LocalPlayer();
        p.Teleport(new Position(0, 0), 0);
        for (var t = 10; t <= 500; t += 10)
        {
            p.Advance(t, 10, Unit, 0, 5_000);
        }
        // Server confirms where we were at t=300 (RTT 200 ms): no correction needed.
        var at300 = p.PredictedAt(300);
        var before = p.Position;
        p.Reconcile(at300, 500, 200);
        Assert.Equal(0, p.LastErrorMm);
        Assert.Equal(before, p.Position);
    }

    [Fact]
    public void Teleport_clears_history()
    {
        var p = new LocalPlayer();
        p.Teleport(new Position(0, 0), 0);
        p.Advance(100, 100, Unit, 0, 5_000);
        p.Teleport(new Position(50_000, 50_000), 200);
        Assert.Equal(new Position(50_000, 50_000), p.PredictedAt(0));
    }
}

public class InputPacerTests
{
    [Fact]
    public void Sends_on_change_then_every_100_ms_while_moving()
    {
        var pacer = new InputPacer();
        Assert.NotNull(pacer.Update(0, 100, 0));
        Assert.Null(pacer.Update(50, 100, 0));
        Assert.NotNull(pacer.Update(100, 100, 0));
        Assert.NotNull(pacer.Update(110, 0, 100)); // direction change: immediately
        Assert.Equal(3u, pacer.Sequence);
    }

    [Fact]
    public void Sends_one_stop_then_goes_quiet()
    {
        var pacer = new InputPacer();
        Assert.Null(pacer.Update(0, 0, 0)); // never moved: nothing to say
        pacer.Update(10, 100, 0);
        var stop = pacer.Update(20, 0, 0);
        Assert.NotNull(stop);
        Assert.Equal(0, stop.Value.DirX);
        Assert.Null(pacer.Update(500, 0, 0));
    }
}

public class ClientWorldTests
{
    private static ClientWorld Joined()
    {
        var w = new ClientWorld { NowMs = 1_000 };
        w.OnWelcome(null!, new Welcome { EntityId = 1, TickRateHz = 20, ServerTick = 100, X = 0, Z = 0 });
        return w;
    }

    /// <summary>Delivers a full-state snapshot (no baseline), the way the server sends before any ack.</summary>
    private static void Snap(ClientWorld w, uint tick, params EntityState[] entities)
    {
        var buffer = new byte[2_000];
        var length = EntityDeltaCodec.Encode(entities, null, buffer);
        w.OnDeltaSnapshot(null!, new DeltaSnapshot { Tick = tick, Entities = buffer[..length] });
    }

    private static EntityState State(uint id, int x, byte flags = 0) =>
        new() { EntityId = id, X = x, Z = 0, HealthPercent = 100, Flags = flags };

    [Fact]
    public void Welcome_places_the_player_and_starts_the_server_clock()
    {
        var w = Joined();
        Assert.True(w.InWorld);
        Assert.Equal(1u, w.SelfId);
        Assert.Equal(5_000 - ClientWorld.InterpolationDelayMs, w.RenderServerMs, 6); // tick 100 x 50 ms
    }

    [Fact]
    public void Snapshots_build_remote_entities_and_removals_drop_them()
    {
        var w = Joined();
        w.OnEntityInfo(null!, new EntityInfo { EntityId = 9, Kind = 2, Name = "Glasswind Skitter", Level = 3 });
        Snap(w, 101, State(1, 0), State(9, 5_000));
        Assert.False(w.Entities.ContainsKey(1)); // self is not a remote entity
        Assert.Equal("Glasswind Skitter", w.NameOf(9));
        Assert.Equal("You", w.NameOf(1));

        Snap(w, 102, State(1, 0)); // 9 is no longer in view
        Assert.Empty(w.Entities);
    }

    [Fact]
    public void Tab_target_cycles_living_mobs_nearest_first()
    {
        var w = Joined();
        foreach (var (id, x) in new[] { (10u, 30_000), (11u, 10_000), (12u, 20_000) })
        {
            w.OnEntityInfo(null!, new EntityInfo { EntityId = id, Kind = 2, Name = "m" });
        }
        w.OnEntityInfo(null!, new EntityInfo { EntityId = 13, Kind = 1, Name = "player" });
        w.OnEntityInfo(null!, new EntityInfo { EntityId = 14, Kind = 2, Name = "corpse" });
        Snap(w, 101, State(10, 30_000), State(11, 10_000), State(12, 20_000), State(13, 1_000), State(14, 500, flags: 1));

        Assert.Equal(11u, w.NextMobTarget(0));
        Assert.Equal(12u, w.NextMobTarget(11));
        Assert.Equal(10u, w.NextMobTarget(12));
        Assert.Equal(11u, w.NextMobTarget(10)); // wraps
        Assert.Equal(11u, w.NextMobTarget(10, maxRangeMm: 15_000));
    }

    [Fact]
    public void Events_are_drained_once_in_order()
    {
        var w = Joined();
        w.OnCombatEvent(null!, new CombatEvent { AttackerId = 1, TargetId = 9, Outcome = 2, Damage = 30 });
        w.OnEntityDied(null!, new EntityDied { EntityId = 9, KillerId = 1 });
        w.OnChatMessage(null!, new ChatMessage { FromEntityId = 5, FromName = "bob", Text = "hi" });
        Assert.Equal(
            [new CombatText(1, 9, HitOutcome.Critical, 30), new EntityKilled(9, 1), new ChatLine(5, "bob", "hi")],
            w.DrainEvents());
        Assert.Empty(w.DrainEvents());
    }

    [Fact]
    public void Entity_info_that_arrives_before_the_first_snapshot_survives()
    {
        var w = Joined();
        Snap(w, 101, State(1, 0));
        w.OnEntityInfo(null!, new EntityInfo { EntityId = 9, Kind = 2, Name = "early" });
        Snap(w, 102, State(1, 0)); // 9 not in view yet
        Snap(w, 103, State(1, 0), State(9, 100));
        Assert.Equal("early", w.NameOf(9));
    }

    [Fact]
    public void Stale_snapshots_are_ignored()
    {
        var w = Joined();
        Snap(w, 105, State(1, 0), State(9, 100));
        Snap(w, 104, State(1, 0)); // older: dropped, so 9 stays
        Assert.True(w.Entities.ContainsKey(9));
    }

    [Fact]
    public void Respawn_teleports_without_blending()
    {
        var w = Joined();
        w.OnRespawned(null!, new Respawned { X = 40_000, Z = -2_000 });
        Assert.Equal(new Position(40_000, -2_000), w.Self.Position);
    }

    [Fact]
    public void Self_snapshot_reconciles_prediction()
    {
        var w = Joined();
        Snap(w, 101, State(1, 1_000));
        Assert.Equal(150, w.Self.Position.X);
    }

    [Fact]
    public void Chat_is_truncated_on_a_character_boundary()
    {
        var text = new string('é', 200); // 2 bytes each
        var cut = GameSession.TruncateUtf8(text, 256);
        Assert.Equal(128, cut.Length);
        Assert.Equal("hello", GameSession.TruncateUtf8("hello", 256));
        Assert.Equal("👍", GameSession.TruncateUtf8("👍👍", 5)); // 4-byte emoji never split
    }
}
