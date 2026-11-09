using Meridian.Core.Stats;

namespace Meridian.Core.Protocols;

/// <summary>Outcome of trying to apply a Protocol to a target.</summary>
public enum ApplyResult : byte
{
    /// <summary>A new line became active.</summary>
    Applied,

    /// <summary>A stronger member replaced a weaker one in the same line.</summary>
    Replaced,

    /// <summary>The same Protocol was already active; its duration was renewed.</summary>
    Refreshed,

    /// <summary>A stronger or different equal-strength member of the line is active.</summary>
    RejectedWeaker,

    /// <summary>The target's lattice capacity would be exceeded.</summary>
    RejectedCapacity,
}

/// <summary>One running Protocol on a target.</summary>
public sealed record ActiveProtocol(ProtocolDef Def, long ExpiresAtTick, ModifierSource Source);

/// <summary>
/// The Protocols running on one target, enforcing line stacking and lattice capacity.
/// </summary>
/// <remarks>
/// Capacity is checked only when applying. If capacity later drops (for example a Psyche buff
/// expires), running Protocols are kept until they expire; this avoids cascades where one
/// expiry knocks off a chain of others.
/// </remarks>
public sealed class ActiveProtocols
{
    private readonly StatBlock _target;
    private readonly Dictionary<ProtocolLine, ActiveProtocol> _byLine = [];
    private long _nextInstanceId = 1;

    public ActiveProtocols(StatBlock target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    /// <summary>Total lattice capacity used by running Protocols.</summary>
    public int UsedCapacity { get; private set; }

    public IReadOnlyCollection<ActiveProtocol> Running => _byLine.Values;

    public ActiveProtocol? InLine(ProtocolLine line) => _byLine.GetValueOrDefault(line);

    /// <summary>
    /// Applies <paramref name="def"/> to the target if line and capacity rules allow it.
    /// The caster's requirement is not checked here; see <see cref="ProtocolDef.CanCast"/>.
    /// </summary>
    public ApplyResult TryApply(ProtocolDef def, long nowTick)
    {
        ArgumentNullException.ThrowIfNull(def);
        var expires = nowTick + def.DurationTicks;
        _byLine.TryGetValue(def.Line, out var current);

        if (current is not null)
        {
            if (current.Def.Id == def.Id)
            {
                _byLine[def.Line] = current with { ExpiresAtTick = expires };
                return ApplyResult.Refreshed;
            }
            if (def.Strength <= current.Def.Strength)
            {
                return ApplyResult.RejectedWeaker;
            }
        }

        var freed = current?.Def.LatticeCost ?? 0;
        if (UsedCapacity - freed + def.LatticeCost > _target.Get(Stat.LatticeCapacity))
        {
            return ApplyResult.RejectedCapacity;
        }

        if (current is not null)
        {
            Remove(current);
        }

        var source = new ModifierSource(SourceKind.Protocol, _nextInstanceId++);
        var active = new ActiveProtocol(def, expires, source);
        _byLine[def.Line] = active;
        UsedCapacity += def.LatticeCost;
        _target.AddEffects(def.Effects, source);
        return current is null ? ApplyResult.Applied : ApplyResult.Replaced;
    }

    /// <summary>Removes every Protocol whose expiry is at or before <paramref name="nowTick"/>.</summary>
    /// <returns>The expired Protocols, in the order they were applied.</returns>
    public IReadOnlyList<ActiveProtocol> Expire(long nowTick)
    {
        List<ActiveProtocol>? expired = null;
        foreach (var a in _byLine.Values)
        {
            if (a.ExpiresAtTick <= nowTick)
            {
                (expired ??= []).Add(a);
            }
        }
        if (expired is null)
        {
            return [];
        }
        // Deterministic order (application order) so recorded replays reproduce exactly.
        expired.Sort(static (a, b) => a.Source.Id.CompareTo(b.Source.Id));
        foreach (var a in expired)
        {
            Remove(a);
        }
        return expired;
    }

    /// <summary>Cancels the Protocol in <paramref name="line"/>, if any.</summary>
    public bool Cancel(ProtocolLine line)
    {
        if (!_byLine.TryGetValue(line, out var a))
        {
            return false;
        }
        Remove(a);
        return true;
    }

    private void Remove(ActiveProtocol a)
    {
        _byLine.Remove(a.Def.Line);
        UsedCapacity -= a.Def.LatticeCost;
        _target.RemoveSource(a.Source);
    }
}
