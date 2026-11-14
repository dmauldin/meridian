using Meridian.Core.Characters;
using Meridian.Core.Combat;
using Meridian.Core.Content;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;

namespace Meridian.Core.Protocols;

public enum CastResult : byte
{
    /// <summary>A new line became active on the target.</summary>
    Applied = 1,

    /// <summary>A stronger member replaced a weaker one.</summary>
    Replaced = 2,

    /// <summary>The same Protocol was running; its duration restarted.</summary>
    Renewed = 3,

    NotKnown = 10,
    SelfOnly = 11,
    OutOfRange = 12,
    RequirementsNotMet = 13,
    NotEnoughEnergy = 14,

    /// <summary>A stronger (or a different, equal) member of the line is running on the target.</summary>
    StrongerRunning = 15,

    /// <summary>The target's lattice capacity would be exceeded.</summary>
    LatticeFull = 16,
    TargetDead = 17,
}

public enum UploadResult : byte
{
    Learned = 1,
    AlreadyKnown = 2,
    NotAProtocol = 3,
    RequirementsNotMet = 4,
}

/// <summary>
/// A character's neural lattice: the Protocols it knows, its energy, and the Protocols running on
/// it. Pure rules shared by zone and client; the zone supplies distance and time.
/// <list type="bullet">
/// <item>Uploading needs the Protocol's cast requirements (design doc: "you can only upload one
/// if you meet its skill requirements") and is permanent.</item>
/// <item>Casting checks, in order: known, target allowed, range, requirements, energy, then the
/// target's line and capacity rules. Energy is spent only when the Protocol lands.</item>
/// <item>Energy is tracked in thousandths and regenerates by <see cref="Formulas.EnergyRegenMilliPerTick"/>.</item>
/// </list>
/// <see cref="Revision"/> bumps when known or running Protocols change (not on energy ticks, which
/// travel in the frequent self status).
/// </summary>
public sealed class Lattice
{
    private readonly ContentPack _pack;
    private readonly Character _owner;
    private readonly int _tickMs;
    private readonly SortedSet<int> _known = [];
    private readonly Dictionary<int, ProtocolDef> _defs = [];

    public Lattice(ContentPack pack, Character owner, int tickMs)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfLessThan(tickMs, 1);
        _pack = pack;
        _owner = owner;
        _tickMs = tickMs;
        Running = new ActiveProtocols(owner.Stats);
        EnergyMilli = (long)MaxEnergy * 1000;
    }

    public ActiveProtocols Running { get; }

    public long Revision { get; private set; }

    public IReadOnlyCollection<int> Known => _known;

    public long EnergyMilli { get; private set; }

    public int Energy => (int)(EnergyMilli / 1000);

    public int MaxEnergy => _owner.Get(Stat.MaxEnergy);

    public bool Knows(int protocolId) => _known.Contains(protocolId);

    /// <summary>Learns without checks (starting Protocols, loading a save). Unknown ids are ignored.</summary>
    public void Learn(int protocolId)
    {
        if (_pack.Protocols.ContainsId(protocolId) && _known.Add(protocolId))
        {
            Revision++;
        }
    }

    public UploadResult Upload(ItemTemplate item, List<RequirementFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ProtocolKey is not { } key || !_pack.Protocols.TryGet(key, out var protocol))
        {
            return UploadResult.NotAProtocol;
        }
        if (_known.Contains(protocol.Id))
        {
            return UploadResult.AlreadyKnown;
        }
        if (!protocol.CastRequirement().Evaluate(_owner, failures))
        {
            return UploadResult.RequirementsNotMet;
        }
        _known.Add(protocol.Id);
        Revision++;
        return UploadResult.Learned;
    }

    /// <summary>Casts <paramref name="protocol"/> from this lattice onto <paramref name="target"/> (which may be this one).</summary>
    /// <param name="distanceSquaredMm">Squared distance between caster and target (integer, no square root); ignored for a self-cast.</param>
    public CastResult Cast(ProtocolTemplate protocol, Lattice target, bool targetAlive, long distanceSquaredMm, long nowTick, List<RequirementFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(target);
        var self = ReferenceEquals(target, this);
        if (!_known.Contains(protocol.Id))
        {
            return CastResult.NotKnown;
        }
        if (protocol.Target == ProtocolTarget.Self && !self)
        {
            return CastResult.SelfOnly;
        }
        if (!targetAlive)
        {
            return CastResult.TargetDead;
        }
        if (!self && distanceSquaredMm > (long)protocol.RangeMm * protocol.RangeMm)
        {
            return CastResult.OutOfRange;
        }
        if (!protocol.CastRequirement().Evaluate(_owner, failures))
        {
            return CastResult.RequirementsNotMet;
        }
        var cost = (long)protocol.EnergyCost * 1000;
        if (EnergyMilli < cost)
        {
            return CastResult.NotEnoughEnergy;
        }
        var result = target.Receive(Def(protocol), nowTick, durationTicks: null);
        if (result is CastResult.Applied or CastResult.Replaced or CastResult.Renewed)
        {
            EnergyMilli -= cost;
        }
        return result;
    }

    /// <summary>Regenerates energy and ends expired Protocols. Returns what expired.</summary>
    public IReadOnlyList<ActiveProtocol> Tick(long nowTick)
    {
        var max = (long)MaxEnergy * 1000;
        EnergyMilli = Math.Min(max, EnergyMilli + Formulas.EnergyRegenMilliPerTick(MaxEnergy, _tickMs));
        var expired = Running.Expire(nowTick);
        if (expired.Count > 0)
        {
            Revision++;
        }
        return expired;
    }

    /// <summary>Ends a running Protocol on this character (anyone may drop a buff they carry).</summary>
    public bool Cancel(int protocolId)
    {
        if (Running.Running.FirstOrDefault(a => a.Def.Id == protocolId) is not { } running)
        {
            return false;
        }
        Running.Cancel(running.Def.Line);
        Revision++;
        return true;
    }

    /// <summary>Ends everything running (death).</summary>
    public void ClearRunning()
    {
        foreach (var line in Running.Running.Select(a => a.Def.Line).ToList())
        {
            Running.Cancel(line);
        }
        Revision++;
    }

    /// <summary>
    /// Restores a saved buff with its remaining time. Line and capacity rules still apply, so a
    /// save made under different content cannot smuggle in an illegal stack. Unknown ids are skipped.
    /// </summary>
    public bool Restore(int protocolId, int remainingMs, long nowTick)
    {
        if (!_pack.Protocols.TryGet(protocolId, out var protocol) || remainingMs <= 0)
        {
            return false;
        }
        return Receive(Def(protocol), nowTick, Math.Max(1, remainingMs / _tickMs)) is CastResult.Applied or CastResult.Replaced or CastResult.Renewed;
    }

    /// <summary>Running Protocols with their remaining time in milliseconds, in id order.</summary>
    public IReadOnlyList<(int ProtocolId, int RemainingMs)> RunningWithRemaining(long nowTick) =>
        [.. Running.Running
            .Select(a => (a.Def.Id, (int)Math.Min(int.MaxValue, Math.Max(0, (a.ExpiresAtTick - nowTick) * _tickMs))))
            .OrderBy(r => r.Id)];

    /// <summary>Sets energy (tests, loading); clamped to 0..max.</summary>
    public void SetEnergy(int energy) => EnergyMilli = Math.Clamp((long)energy, 0, MaxEnergy) * 1000;

    private CastResult Receive(ProtocolDef def, long nowTick, int? durationTicks)
    {
        var result = Running.TryApply(def, nowTick, durationTicks) switch
        {
            ApplyResult.Applied => CastResult.Applied,
            ApplyResult.Replaced => CastResult.Replaced,
            ApplyResult.Refreshed => CastResult.Renewed,
            ApplyResult.RejectedCapacity => CastResult.LatticeFull,
            _ => CastResult.StrongerRunning,
        };
        if (result is CastResult.Applied or CastResult.Replaced or CastResult.Renewed)
        {
            Revision++;
        }
        return result;
    }

    private ProtocolDef Def(ProtocolTemplate t)
    {
        if (!_defs.TryGetValue(t.Id, out var def))
        {
            def = t.ToDef(_tickMs);
            _defs[t.Id] = def;
        }
        return def;
    }
}
