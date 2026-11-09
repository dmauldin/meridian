using System.Collections.Frozen;

namespace Meridian.Core.Stats;

/// <summary>Read access to effective stat values.</summary>
public interface IStatReader
{
    /// <summary>The effective value: base (or derived base) plus all modifiers.</summary>
    int Get(Stat stat);
}

/// <summary>
/// A stat whose base value is computed from other stats rather than stored.
/// Modifiers still apply on top of the computed base.
/// </summary>
/// <param name="Stat">The derived stat.</param>
/// <param name="Inputs">Every stat the formula reads. Must be complete: it drives cache invalidation.</param>
/// <param name="Formula">Computes the base value from effective input values.</param>
public sealed record DerivedStat(Stat Stat, IReadOnlyList<Stat> Inputs, Func<IStatReader, int> Formula);

/// <summary>
/// The set of derived-stat formulas and the dependency graph between stats.
/// Immutable and shareable between every <see cref="StatBlock"/>.
/// </summary>
public sealed class StatCatalog
{
    private readonly DerivedStat?[] _derived;
    private readonly Stat[][] _dependents;

    public StatCatalog(IEnumerable<DerivedStat> derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        _derived = new DerivedStat?[StatIndex.Capacity];
        var dependents = new List<Stat>[StatIndex.Capacity];

        foreach (var d in derived)
        {
            if (_derived[(int)d.Stat] is not null)
            {
                throw new ArgumentException($"Stat {d.Stat} has more than one formula.", nameof(derived));
            }
            _derived[(int)d.Stat] = d;
            foreach (var input in d.Inputs)
            {
                (dependents[(int)input] ??= []).Add(d.Stat);
            }
        }

        _dependents = new Stat[StatIndex.Capacity][];
        for (var i = 0; i < _dependents.Length; i++)
        {
            _dependents[i] = dependents[i]?.ToArray() ?? [];
        }

        ThrowIfCyclic();
    }

    /// <summary>The formula for a derived stat, or null if the stat is stored.</summary>
    public DerivedStat? DerivedFor(Stat stat) => _derived[(int)stat];

    /// <summary>Stats whose formulas read <paramref name="stat"/> directly.</summary>
    public ReadOnlySpan<Stat> DirectDependents(Stat stat) => _dependents[(int)stat];

    /// <summary>Every derived stat in the catalog.</summary>
    public IEnumerable<DerivedStat> DerivedStats => _derived.Where(d => d is not null)!;

    private void ThrowIfCyclic()
    {
        // 0 = unvisited, 1 = on stack, 2 = done
        var state = new byte[StatIndex.Capacity];
        foreach (var d in DerivedStats)
        {
            Visit(d.Stat);
        }

        void Visit(Stat s)
        {
            if (state[(int)s] == 2)
            {
                return;
            }
            if (state[(int)s] == 1)
            {
                throw new InvalidOperationException($"Derived stat formulas form a cycle through {s}.");
            }
            state[(int)s] = 1;
            foreach (var dep in _dependents[(int)s])
            {
                Visit(dep);
            }
            state[(int)s] = 2;
        }
    }

    /// <summary>
    /// The default game formulas. Numbers are first-pass tuning values; balance changes
    /// belong here (and in the golden tests) so they show up as reviewable diffs.
    /// </summary>
    public static StatCatalog Default { get; } = new(
    [
        new DerivedStat(
            Stat.MaxHealth,
            [Stat.Level, Stat.BodyDevelopment, Stat.Stamina],
            s => 50 + (8 * s.Get(Stat.Level)) + (3 * s.Get(Stat.BodyDevelopment)) + s.Get(Stat.Stamina)),
        new DerivedStat(
            Stat.MaxEnergy,
            [Stat.Level, Stat.Psyche, Stat.Intellect],
            s => 40 + (5 * s.Get(Stat.Level)) + (3 * s.Get(Stat.Psyche)) + s.Get(Stat.Intellect)),
        new DerivedStat(
            Stat.LatticeCapacity,
            [Stat.Level, Stat.Psyche],
            s => 10 + (2 * s.Get(Stat.Level)) + (s.Get(Stat.Psyche) / 2)),
    ]);

    /// <summary>Stats with a formula in <see cref="Default"/>; handy for tests.</summary>
    public static FrozenSet<Stat> DefaultDerivedStats { get; } =
        Default.DerivedStats.Select(d => d.Stat).ToFrozenSet();
}
