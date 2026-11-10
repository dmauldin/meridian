namespace Meridian.Wire.Delta;

/// <summary>
/// Client side: rebuilds full entity state from <see cref="DeltaSnapshot"/>s and remembers recent
/// results as future baselines. Every consumer (game client, bots, tests) uses this one class.
/// </summary>
public sealed class SnapshotAssembler
{
    public const int DefaultDepth = 64;

    private readonly int _depth;
    private readonly Dictionary<uint, Dictionary<uint, EntityState>> _byTick = [];
    private readonly Queue<uint> _order = new();

    public SnapshotAssembler(int depth = DefaultDepth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 2);
        _depth = depth;
    }

    /// <summary>The newest tick rebuilt so far (what to acknowledge).</summary>
    public uint LatestTick { get; private set; }

    /// <summary>Snapshots dropped because their baseline was unknown.</summary>
    public long MissingBaselines { get; private set; }

    /// <summary>
    /// Rebuilds <paramref name="snapshot"/>. Returns false (and changes nothing) for stale or
    /// duplicate snapshots and for deltas against a baseline we no longer have; the server keeps
    /// deltaing against our last ack, so the next snapshot recovers.
    /// </summary>
    /// <exception cref="WireFormatException">The entity stream is malformed.</exception>
    public bool TryApply(in DeltaSnapshot snapshot, out IReadOnlyCollection<EntityState> entities)
    {
        entities = [];
        if (snapshot.Tick == 0 || snapshot.Tick <= LatestTick)
        {
            return false;
        }

        Dictionary<uint, EntityState> states;
        if (snapshot.BaselineTick == 0)
        {
            states = new Dictionary<uint, EntityState>(EntityDeltaCodec.MaxEntities);
        }
        else if (_byTick.TryGetValue(snapshot.BaselineTick, out var baseline))
        {
            states = new Dictionary<uint, EntityState>(baseline);
            foreach (var id in snapshot.Removed ?? [])
            {
                states.Remove(id);
            }
        }
        else
        {
            MissingBaselines++;
            return false;
        }

        EntityDeltaCodec.Decode(snapshot.Entities, states);
        if (states.Count > EntityDeltaCodec.MaxEntities)
        {
            throw new WireFormatException($"Rebuilt snapshot has {states.Count} entities, over the limit.");
        }

        _byTick[snapshot.Tick] = states;
        _order.Enqueue(snapshot.Tick);
        while (_order.Count > _depth)
        {
            _byTick.Remove(_order.Dequeue());
        }
        LatestTick = snapshot.Tick;
        entities = states.Values;
        return true;
    }
}
