using Meridian.Wire.Delta;

namespace Meridian.Wire.Tests;

public class EntityDeltaCodecTests
{
    private static EntityState E(uint id, int x, int z = 0, byte yaw = 0, byte hp = 100, byte flags = 0) =>
        new() { EntityId = id, X = x, Z = z, Yaw = yaw, HealthPercent = hp, Flags = flags };

    private static Dictionary<uint, EntityState> Map(params EntityState[] states) => states.ToDictionary(s => s.EntityId);

    private static byte[] Encode(EntityState[] current, Dictionary<uint, EntityState>? baseline)
    {
        var buf = new byte[2_000];
        return buf[..EntityDeltaCodec.Encode(current, baseline, buf)];
    }

    private static Dictionary<uint, EntityState> Apply(byte[] data, Dictionary<uint, EntityState> start)
    {
        var copy = new Dictionary<uint, EntityState>(start);
        EntityDeltaCodec.Decode(data, copy);
        return copy;
    }

    [Fact]
    public void Full_encoding_round_trips()
    {
        EntityState[] current = [E(1, 100, -5, 3, 90, 2), E(70_000, int.MinValue, int.MaxValue)];
        Assert.Equal(Map(current), Apply(Encode(current, null), []));
    }

    [Fact]
    public void Unchanged_entities_cost_nothing()
    {
        EntityState[] states = [E(1, 100), E(2, 200), E(3, 300)];
        var data = Encode(states, Map(states));
        Assert.Equal([0], data); // just a zero count
    }

    [Fact]
    public void A_moving_entity_costs_a_few_bytes()
    {
        var baseline = Map(E(5, 1_000_000, -500_000));
        var data = Encode([E(5, 1_000_250, -500_000)], baseline); // moved 25 cm in X
        // count(1) + id(1) + mask(1) + dx varint(2) + dz varint(1)
        Assert.Equal(6, data.Length);
        Assert.Equal(1_000_250, Apply(data, baseline)[5].X);
    }

    [Fact]
    public void Only_changed_fields_are_sent()
    {
        var baseline = Map(E(5, 0, 0, yaw: 10, hp: 100));
        var data = Encode([E(5, 0, 0, yaw: 10, hp: 64)], baseline);
        Assert.Equal(1 + 1 + 1 + 1, data.Length); // count, id, mask, health
        Assert.Equal(64, Apply(data, baseline)[5].HealthPercent);
    }

    [Fact]
    public void Offsets_too_large_for_an_int_fall_back_to_absolute()
    {
        var baseline = Map(E(5, int.MinValue));
        var current = E(5, int.MaxValue);
        Assert.Equal(current, Apply(Encode([current], baseline), baseline)[5]);
    }

    [Fact]
    public void Too_many_entities_is_refused()
    {
        var many = Enumerable.Range(1, 65).Select(i => E((uint)i, i)).ToArray();
        Assert.Throws<WireFormatException>(() => Encode(many, null));
    }

    [Fact]
    public void Worst_case_snapshot_fits_the_schema_byte_limit()
    {
        var worst = Enumerable.Range(0, 64).Select(i => E(uint.MaxValue - (uint)i, int.MinValue, int.MaxValue, 255, 255, 255)).ToArray();
        Assert.True(Encode(worst, null).Length <= 1_300);
    }

    [Theory]
    [InlineData(new byte[] { 1, 5, 0x01, 2, 2 }, "not in the baseline")]          // relative entry, unknown entity
    [InlineData(new byte[] { 2, 9, 0x00, 9, 0x00 }, "twice")]                       // duplicate id (9 is in the baseline)
    [InlineData(new byte[] { 1, 9, 0x40 }, "mask")]                                  // unknown mask bit
    [InlineData(new byte[] { 1, 9, 0x81, 0, 0, 0, 0, 0, 0, 0, 0 }, "every field")]  // absolute but partial
    [InlineData(new byte[] { 0, 0 }, "trailing")]                                    // trailing byte
    [InlineData(new byte[] { 65 }, "limit")]                                          // count over the limit
    public void Malformed_streams_are_rejected(byte[] data, string expected)
    {
        var ex = Assert.Throws<WireFormatException>(() => EntityDeltaCodec.Decode(data, Map(E(9, 0))));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Offset_overflow_is_a_wire_error()
    {
        var baseline = Map(E(9, int.MaxValue));
        // id 9, mask position, dx = +1 (zig-zag 2), dz = 0
        Assert.Throws<WireFormatException>(() => EntityDeltaCodec.Decode(new byte[] { 1, 9, 0x01, 2, 0 }, new Dictionary<uint, EntityState>(baseline)));
    }

    [Fact]
    public void Random_garbage_only_ever_produces_WireFormatException()
    {
        var rng = new Random(77);
        var baseline = Map(E(1, 0), E(2, 5_000), E(3, -5_000));
        for (var i = 0; i < 50_000; i++)
        {
            var data = new byte[rng.Next(0, 30)];
            rng.NextBytes(data);
            if (data.Length > 0)
            {
                data[0] = (byte)rng.Next(0, 4); // plausible counts so entries get parsed
            }
            try
            {
                EntityDeltaCodec.Decode(data, new Dictionary<uint, EntityState>(baseline));
            }
            catch (WireFormatException)
            {
            }
        }
    }
}

public class SnapshotPipelineTests
{
    private static EntityState E(uint id, int x) => new() { EntityId = id, X = x, HealthPercent = 100 };

    /// <summary>Server side of one snapshot, the way the zone builds it.</summary>
    private static DeltaSnapshot Build(SnapshotHistory history, uint tick, EntityState[] visible)
    {
        var baseline = history.Baseline(out var baselineTick);
        var buffer = new byte[1_300];
        var length = EntityDeltaCodec.Encode(visible, baseline, buffer);
        var visibleIds = visible.Select(v => v.EntityId).ToHashSet();
        var removed = baseline?.Keys.Where(id => !visibleIds.Contains(id)).ToArray() ?? [];
        history.Record(tick, visible);
        return new DeltaSnapshot { Tick = tick, BaselineTick = baselineTick, Entities = buffer[..length], Removed = removed };
    }

    [Fact]
    public void Client_rebuilds_exactly_what_the_server_sent_despite_loss_reordering_and_late_acks()
    {
        var rng = new Random(2026);
        var history = new SnapshotHistory();
        var assembler = new SnapshotAssembler();
        var world = new Dictionary<uint, int>(); // entity id -> x
        var sent = new Dictionary<uint, Dictionary<uint, EntityState>>();
        var inFlight = new List<(int DeliverAt, DeltaSnapshot Snapshot)>();
        var acks = new List<(int DeliverAt, uint Tick)>();
        var applied = 0;

        for (var step = 1; step <= 3_000; step++)
        {
            // The world changes: entities move, appear and vanish.
            foreach (var id in world.Keys.ToList())
            {
                if (rng.Next(10) < 6)
                {
                    world[id] += rng.Next(-400, 400);
                }
                if (rng.Next(200) == 0)
                {
                    world.Remove(id);
                }
            }
            if (world.Count < 60 && rng.Next(4) == 0)
            {
                world[(uint)rng.Next(1, 500)] = rng.Next(-100_000, 100_000);
            }

            var tick = (uint)step;
            var visible = world.Select(kv => E(kv.Key, kv.Value)).ToArray();
            sent[tick] = visible.ToDictionary(v => v.EntityId);
            var snapshot = Build(history, tick, visible);
            if (rng.Next(10) != 0) // 10% loss
            {
                inFlight.Add((step + rng.Next(1, 6), snapshot)); // 1-5 steps of jitter: reordering
            }

            foreach (var (_, s) in inFlight.Where(f => f.DeliverAt == step).ToList())
            {
                // Unreliable *sequenced*: older than the newest delivered is dropped by the transport.
                if (s.Tick > assembler.LatestTick && assembler.TryApply(s, out var rebuilt))
                {
                    applied++;
                    Assert.Equal(sent[s.Tick], rebuilt.ToDictionary(e => e.EntityId));
                    if (rng.Next(10) != 0)
                    {
                        acks.Add((step + rng.Next(1, 8), s.Tick)); // acks are lossy and slow too
                    }
                }
            }
            inFlight.RemoveAll(f => f.DeliverAt <= step);
            foreach (var (_, ackTick) in acks.Where(a => a.DeliverAt == step))
            {
                history.Acknowledge(ackTick);
            }
            acks.RemoveAll(a => a.DeliverAt <= step);
        }

        // ~10% loss plus reordering (late ones are dropped as stale) still delivers most snapshots,
        // and the server never deltas against a baseline the client lacks.
        Assert.True(applied > 1_500, $"only {applied} snapshots applied");
        Assert.Equal(0, assembler.MissingBaselines);
    }

    [Fact]
    public void Delta_against_an_ack_is_much_smaller_than_full_state()
    {
        var history = new SnapshotHistory();
        var visible = Enumerable.Range(1, 64).Select(i => E((uint)i, i * 1_000)).ToArray();
        var full = Build(history, 1, visible);
        history.Acknowledge(1);
        // Only 8 of 64 entities moved.
        var next = visible.Select(v => v.EntityId <= 8 ? v with { X = v.X + 250 } : v).ToArray();
        var delta = Build(history, 2, next);

        Assert.Equal(0u, full.BaselineTick);
        Assert.Equal(1u, delta.BaselineTick);
        Assert.True(delta.Entities!.Length * 10 < full.Entities!.Length, $"{delta.Entities.Length} vs {full.Entities.Length}");
    }

    [Fact]
    public void Removed_lists_entities_missing_since_the_baseline()
    {
        var history = new SnapshotHistory();
        Build(history, 1, [E(1, 0), E(2, 0), E(3, 0)]);
        history.Acknowledge(1);
        var s = Build(history, 2, [E(1, 0)]);
        Assert.Equal([2u, 3u], s.Removed!.Order());
    }

    [Fact]
    public void History_ignores_unknown_and_older_acks_and_forgets_evicted_baselines()
    {
        var history = new SnapshotHistory(depth: 4);
        for (uint t = 1; t <= 3; t++)
        {
            history.Record(t, [E(1, (int)t)]);
        }
        history.Acknowledge(99);
        Assert.Equal(0u, history.AckedTick);
        history.Acknowledge(3);
        history.Acknowledge(2);
        Assert.Equal(3u, history.AckedTick);

        for (uint t = 4; t <= 7; t++)
        {
            history.Record(t, [E(1, (int)t)]); // tick 3 is overwritten at t = 7
        }
        Assert.Null(history.Baseline(out var baseline));
        Assert.Equal(0u, baseline);
    }

    [Fact]
    public void Assembler_drops_stale_duplicate_and_unanchored_snapshots()
    {
        var history = new SnapshotHistory();
        var assembler = new SnapshotAssembler();
        var first = Build(history, 1, [E(1, 0)]);
        Assert.True(assembler.TryApply(first, out _));
        Assert.False(assembler.TryApply(first, out _)); // duplicate

        var orphan = new DeltaSnapshot { Tick = 5, BaselineTick = 4, Entities = [0] };
        Assert.False(assembler.TryApply(orphan, out _));
        Assert.Equal(1, assembler.MissingBaselines);
        Assert.Equal(1u, assembler.LatestTick);
    }
}
