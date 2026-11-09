namespace Meridian.Core.Stats;

/// <summary>
/// One character's stats: stored base values, applied modifiers and a lazily computed,
/// cached effective value per stat.
/// </summary>
/// <remarks>
/// <para>Effective value = override ?? (base + Σflat) × (10000 + Σpercent) / 10000.
/// Integer math only, truncating toward zero, so client and server always agree.</para>
/// <para>Caching invariant: if a stat is dirty, every stat that (transitively) depends on it
/// is dirty too. Computing a dependent always cleans its inputs first, so marking a stat dirty
/// can stop early when it is already dirty.</para>
/// <para>Not thread-safe. A zone simulation owns its entities on one thread.</para>
/// </remarks>
public sealed class StatBlock : IStatReader
{
    private readonly StatCatalog _catalog;
    private readonly int[] _base = new int[StatIndex.Capacity];
    private readonly int[] _effective = new int[StatIndex.Capacity];
    private readonly bool[] _dirty = new bool[StatIndex.Capacity];
    private readonly Aggregate[] _aggregates = new Aggregate[StatIndex.Capacity];
    private readonly List<Modifier>?[] _modifiers = new List<Modifier>?[StatIndex.Capacity];
    private readonly Dictionary<ModifierSource, List<Stat>> _statsBySource = [];

    public StatBlock(StatCatalog? catalog = null)
    {
        _catalog = catalog ?? StatCatalog.Default;
        Array.Fill(_dirty, true);
    }

    /// <summary>Increments on every change. Lets callers cheaply detect "anything changed".</summary>
    public long Revision { get; private set; }

    /// <summary>The effective value of <paramref name="stat"/>.</summary>
    public int Get(Stat stat)
    {
        var i = (int)stat;
        if (_dirty[i])
        {
            _effective[i] = Compute(stat);
            _dirty[i] = false;
        }
        return _effective[i];
    }

    /// <summary>
    /// The value before modifiers: the stored value, or the formula result for a derived stat.
    /// </summary>
    public int GetBase(Stat stat)
    {
        var formula = _catalog.DerivedFor(stat);
        return formula is null ? _base[(int)stat] : formula.Formula(this);
    }

    /// <summary>Sets a stored base value. Derived stats cannot be set.</summary>
    public void SetBase(Stat stat, int value)
    {
        if (_catalog.DerivedFor(stat) is not null)
        {
            throw new InvalidOperationException($"{stat} is derived; change its inputs instead.");
        }
        if (_base[(int)stat] == value)
        {
            return;
        }
        _base[(int)stat] = value;
        Invalidate(stat);
    }

    /// <summary>Applies a modifier. The same source may add several modifiers, even to one stat.</summary>
    public void AddModifier(in Modifier modifier)
    {
        var i = (int)modifier.Stat;
        (_modifiers[i] ??= []).Add(modifier);
        _aggregates[i].Add(modifier);

        if (!_statsBySource.TryGetValue(modifier.Source, out var stats))
        {
            stats = [];
            _statsBySource.Add(modifier.Source, stats);
        }
        stats.Add(modifier.Stat);
        Invalidate(modifier.Stat);
    }

    /// <summary>Applies several effects under one source.</summary>
    public void AddEffects(IEnumerable<StatEffect> effects, ModifierSource source)
    {
        ArgumentNullException.ThrowIfNull(effects);
        foreach (var e in effects)
        {
            AddModifier(e.WithSource(source));
        }
    }

    /// <summary>Removes every modifier from <paramref name="source"/>. Returns how many were removed.</summary>
    public int RemoveSource(ModifierSource source)
    {
        if (!_statsBySource.Remove(source, out var stats))
        {
            return 0;
        }

        var removed = 0;
        foreach (var stat in stats.Distinct())
        {
            var i = (int)stat;
            var list = _modifiers[i]!;
            removed += list.RemoveAll(m => m.Source == source);
            _aggregates[i] = Aggregate.Of(list);
            Invalidate(stat);
        }
        return removed;
    }

    /// <summary>True if <paramref name="source"/> currently contributes any modifier.</summary>
    public bool HasSource(ModifierSource source) => _statsBySource.ContainsKey(source);

    /// <summary>The modifiers currently applied to <paramref name="stat"/>, for tooltips and debugging.</summary>
    public IReadOnlyList<Modifier> ModifiersOn(Stat stat) => _modifiers[(int)stat] ?? (IReadOnlyList<Modifier>)[];

    /// <summary>
    /// The effective value of <paramref name="stat"/> as if <paramref name="excluded"/> were not applied.
    /// Used for "an item's own bonus does not count toward its own requirement".
    /// Only the stat itself is recomputed; derived inputs keep the excluded source's effect.
    /// </summary>
    public int GetExcluding(Stat stat, ModifierSource excluded)
    {
        var list = _modifiers[(int)stat];
        if (list is null || !_statsBySource.ContainsKey(excluded))
        {
            return Get(stat);
        }
        var agg = default(Aggregate);
        foreach (var m in list)
        {
            if (m.Source != excluded)
            {
                agg.Add(m);
            }
        }
        return agg.Apply(GetBase(stat));
    }

    private int Compute(Stat stat) => _aggregates[(int)stat].Apply(GetBase(stat));

    private void Invalidate(Stat stat)
    {
        Revision++;
        MarkDirty(stat);
    }

    private void MarkDirty(Stat stat)
    {
        if (_dirty[(int)stat])
        {
            return; // invariant: dependents are already dirty
        }
        _dirty[(int)stat] = true;
        foreach (var dep in _catalog.DirectDependents(stat))
        {
            MarkDirty(dep);
        }
    }

    private struct Aggregate
    {
        private long _flat;
        private long _percent;
        private int _override;
        private int _overrideCount;

        public void Add(in Modifier m)
        {
            switch (m.Kind)
            {
                case ModifierKind.Flat:
                    _flat += m.Value;
                    break;
                case ModifierKind.Percent:
                    _percent += m.Value;
                    break;
                case ModifierKind.Override:
                    _override = _overrideCount == 0 ? m.Value : Math.Max(_override, m.Value);
                    _overrideCount++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(m), m.Kind, "Unknown modifier kind.");
            }
        }

        public static Aggregate Of(List<Modifier> modifiers)
        {
            var a = default(Aggregate);
            foreach (var m in modifiers)
            {
                a.Add(m);
            }
            return a;
        }

        public readonly int Apply(int baseValue)
        {
            if (_overrideCount > 0)
            {
                return _override;
            }
            var factor = Math.Max(0, 10_000 + _percent);
            var value = (baseValue + _flat) * factor / 10_000;
            return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        }
    }
}
