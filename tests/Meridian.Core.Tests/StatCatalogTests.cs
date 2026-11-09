using Meridian.Core.Stats;

namespace Meridian.Core.Tests;

public class StatCatalogTests
{
    /// <summary>Records which stats a formula reads.</summary>
    private sealed class TrackingReader : IStatReader
    {
        public HashSet<Stat> Read { get; } = [];

        public int Get(Stat stat)
        {
            Read.Add(stat);
            return 10;
        }
    }

    [Fact]
    public void Every_default_formula_declares_exactly_the_inputs_it_reads()
    {
        // If a formula reads an undeclared input, its cache would go stale silently.
        foreach (var d in StatCatalog.Default.DerivedStats)
        {
            var reader = new TrackingReader();
            d.Formula(reader);
            Assert.Equal(d.Inputs.ToHashSet(), reader.Read);
        }
    }

    [Fact]
    public void Cyclic_formulas_are_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new StatCatalog(
        [
            new DerivedStat(Stat.MaxHealth, [Stat.MaxEnergy], s => s.Get(Stat.MaxEnergy)),
            new DerivedStat(Stat.MaxEnergy, [Stat.MaxHealth], s => s.Get(Stat.MaxHealth)),
        ]));
        Assert.Contains("cycle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_formulas_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new StatCatalog(
        [
            new DerivedStat(Stat.MaxHealth, [], _ => 1),
            new DerivedStat(Stat.MaxHealth, [], _ => 2),
        ]));
    }

    [Fact]
    public void Chained_derived_stats_invalidate_transitively()
    {
        var catalog = new StatCatalog(
        [
            new DerivedStat(Stat.MaxEnergy, [Stat.Psyche], s => s.Get(Stat.Psyche) * 2),
            new DerivedStat(Stat.LatticeCapacity, [Stat.MaxEnergy], s => s.Get(Stat.MaxEnergy) + 1),
        ]);
        var block = new StatBlock(catalog);
        block.SetBase(Stat.Psyche, 5);
        Assert.Equal(11, block.Get(Stat.LatticeCapacity));
        block.SetBase(Stat.Psyche, 6);
        Assert.Equal(13, block.Get(Stat.LatticeCapacity));
    }

    [Fact]
    public void Default_formulas_golden_values_at_level_100()
    {
        // Tuning changes must update these numbers on purpose.
        var s = new StatBlock();
        s.SetBase(Stat.Level, 100);
        s.SetBase(Stat.BodyDevelopment, 300);
        s.SetBase(Stat.Stamina, 250);
        s.SetBase(Stat.Psyche, 200);
        s.SetBase(Stat.Intellect, 180);

        Assert.Equal(2_000, s.Get(Stat.MaxHealth));   // 50 + 800 + 900 + 250
        Assert.Equal(1_320, s.Get(Stat.MaxEnergy));   // 40 + 500 + 600 + 180
        Assert.Equal(310, s.Get(Stat.LatticeCapacity)); // 10 + 200 + 100
    }
}
