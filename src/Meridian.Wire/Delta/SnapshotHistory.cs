namespace Meridian.Wire.Delta;

/// <summary>
/// Server side, one per client: the entity states sent in recent snapshots, and which one the
/// client has acknowledged. The acknowledged snapshot is the baseline for the next delta.
/// </summary>
/// <remarks>
/// A fixed ring of reusable dictionaries, so steady-state snapshot building does not allocate.
/// At 10 snapshots/s the default depth covers 3.2 s; a client whose acks fall further behind
/// simply gets full snapshots until it catches up.
/// </remarks>
public sealed class SnapshotHistory
{
    public const int DefaultDepth = 32;

    private readonly uint[] _ticks;
    private readonly Dictionary<uint, EntityState>[] _states;
    private int _next;

    public SnapshotHistory(int depth = DefaultDepth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 2);
        _ticks = new uint[depth];
        _states = new Dictionary<uint, EntityState>[depth];
        for (var i = 0; i < depth; i++)
        {
            _states[i] = new Dictionary<uint, EntityState>(EntityDeltaCodec.MaxEntities);
        }
    }

    /// <summary>Newest acknowledged tick still in the ring, or 0.</summary>
    public uint AckedTick { get; private set; }

    /// <summary>Records a client ack. Older acks and ticks we never sent (or have dropped) are ignored.</summary>
    public void Acknowledge(uint tick)
    {
        if (tick > AckedTick && IndexOf(tick) >= 0)
        {
            AckedTick = tick;
        }
    }

    /// <summary>
    /// The baseline to delta the next snapshot against, or null for a full snapshot. The returned
    /// dictionary is reused: finish encoding before calling <see cref="Record"/>.
    /// </summary>
    public IReadOnlyDictionary<uint, EntityState>? Baseline(out uint baselineTick)
    {
        var i = AckedTick == 0 ? -1 : IndexOf(AckedTick);
        if (i < 0)
        {
            baselineTick = 0;
            return null;
        }
        baselineTick = AckedTick;
        return _states[i];
    }

    /// <summary>Stores what was sent at <paramref name="tick"/>, reusing the oldest slot.</summary>
    public void Record(uint tick, ReadOnlySpan<EntityState> sent)
    {
        if (_ticks[_next] == AckedTick)
        {
            AckedTick = 0; // the baseline is being overwritten
        }
        var slot = _states[_next];
        slot.Clear();
        foreach (var e in sent)
        {
            slot[e.EntityId] = e;
        }
        _ticks[_next] = tick;
        _next = (_next + 1) % _ticks.Length;
    }

    private int IndexOf(uint tick)
    {
        for (var i = 0; i < _ticks.Length; i++)
        {
            if (_ticks[i] == tick && tick != 0)
            {
                return i;
            }
        }
        return -1;
    }
}
