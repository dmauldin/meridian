using Meridian.Core.Stats;

namespace Meridian.Core.Tests;

public class StatBlockTests
{
    private static readonly ModifierSource SrcA = new(SourceKind.Debug, 1);
    private static readonly ModifierSource SrcB = new(SourceKind.Debug, 2);

    [Fact]
    public void Base_value_is_returned_when_no_modifiers()
    {
        var s = new StatBlock();
        s.SetBase(Stat.Rifle, 120);
        Assert.Equal(120, s.Get(Stat.Rifle));
    }

    [Fact]
    public void Flat_and_percent_combine_as_base_plus_flat_times_percent()
    {
        var s = new StatBlock();
        s.SetBase(Stat.Rifle, 100);
        s.AddModifier(Modifier.Flat(Stat.Rifle, 50, SrcA));
        s.AddModifier(Modifier.Percent(Stat.Rifle, 1_000, SrcA)); // +10%
        s.AddModifier(Modifier.Percent(Stat.Rifle, 1_000, SrcB)); // +10% more, summed
        Assert.Equal(180, s.Get(Stat.Rifle)); // (100 + 50) * 1.2
    }

    [Fact]
    public void Percent_math_truncates_toward_zero()
    {
        var s = new StatBlock();
        s.SetBase(Stat.Rifle, 7);
        s.AddModifier(Modifier.Percent(Stat.Rifle, 1_500, SrcA)); // 7 * 1.15 = 8.05
        Assert.Equal(8, s.Get(Stat.Rifle));
    }

    [Fact]
    public void Percent_below_minus_100_clamps_to_zero()
    {
        var s = new StatBlock();
        s.SetBase(Stat.RunSpeed, 300);
        s.AddModifier(Modifier.Percent(Stat.RunSpeed, -15_000, SrcA));
        Assert.Equal(0, s.Get(Stat.RunSpeed));
    }

    [Fact]
    public void Highest_override_wins_and_ignores_other_modifiers()
    {
        var s = new StatBlock();
        s.SetBase(Stat.RunSpeed, 300);
        s.AddModifier(Modifier.Flat(Stat.RunSpeed, 100, SrcA));
        s.AddModifier(Modifier.Override(Stat.RunSpeed, 0, SrcA));
        s.AddModifier(Modifier.Override(Stat.RunSpeed, 50, SrcB));
        Assert.Equal(50, s.Get(Stat.RunSpeed));

        s.RemoveSource(SrcB);
        Assert.Equal(0, s.Get(Stat.RunSpeed));
    }

    [Fact]
    public void Removing_a_source_removes_all_its_modifiers_across_stats()
    {
        var s = new StatBlock();
        s.SetBase(Stat.Rifle, 100);
        s.SetBase(Stat.Agility, 50);
        s.AddModifier(Modifier.Flat(Stat.Rifle, 10, SrcA));
        s.AddModifier(Modifier.Flat(Stat.Rifle, 5, SrcA));
        s.AddModifier(Modifier.Flat(Stat.Agility, 7, SrcA));
        s.AddModifier(Modifier.Flat(Stat.Rifle, 1, SrcB));

        Assert.Equal(3, s.RemoveSource(SrcA));
        Assert.Equal(101, s.Get(Stat.Rifle));
        Assert.Equal(50, s.Get(Stat.Agility));
        Assert.False(s.HasSource(SrcA));
        Assert.Equal(0, s.RemoveSource(SrcA));
    }

    [Fact]
    public void Derived_stat_tracks_its_inputs()
    {
        var s = new StatBlock();
        s.SetBase(Stat.Level, 10);
        s.SetBase(Stat.Psyche, 40);
        Assert.Equal(10 + 20 + 20, s.Get(Stat.LatticeCapacity));

        s.SetBase(Stat.Psyche, 60);
        Assert.Equal(10 + 20 + 30, s.Get(Stat.LatticeCapacity));
    }

    [Fact]
    public void Buff_on_an_input_flows_through_to_derived_stat_and_back_out()
    {
        var s = new StatBlock();
        s.SetBase(Stat.Level, 1);
        s.SetBase(Stat.Psyche, 10);
        var before = s.Get(Stat.LatticeCapacity);

        s.AddModifier(Modifier.Flat(Stat.Psyche, 20, SrcA));
        Assert.Equal(before + 10, s.Get(Stat.LatticeCapacity));

        s.RemoveSource(SrcA);
        Assert.Equal(before, s.Get(Stat.LatticeCapacity));
    }

    [Fact]
    public void Modifiers_apply_on_top_of_a_derived_base()
    {
        var s = new StatBlock();
        s.SetBase(Stat.Level, 1);
        var baseHp = s.Get(Stat.MaxHealth);
        s.AddModifier(Modifier.Flat(Stat.MaxHealth, 100, SrcA));
        Assert.Equal(baseHp + 100, s.Get(Stat.MaxHealth));
        Assert.Equal(baseHp, s.GetBase(Stat.MaxHealth));
    }

    [Fact]
    public void Setting_a_derived_stat_is_rejected()
    {
        var s = new StatBlock();
        Assert.Throws<InvalidOperationException>(() => s.SetBase(Stat.MaxHealth, 1));
    }

    [Fact]
    public void Revision_changes_on_every_mutation_but_not_on_reads()
    {
        var s = new StatBlock();
        var r0 = s.Revision;
        s.SetBase(Stat.Rifle, 5);
        var r1 = s.Revision;
        _ = s.Get(Stat.Rifle);
        Assert.Equal(r1, s.Revision);
        s.AddModifier(Modifier.Flat(Stat.Rifle, 1, SrcA));
        Assert.True(r1 > r0 && s.Revision > r1);
    }

    [Fact]
    public void GetExcluding_ignores_one_source_only()
    {
        var s = new StatBlock();
        s.SetBase(Stat.Agility, 100);
        s.AddModifier(Modifier.Flat(Stat.Agility, 20, SrcA));
        s.AddModifier(Modifier.Flat(Stat.Agility, 5, SrcB));
        Assert.Equal(105, s.GetExcluding(Stat.Agility, SrcA));
        Assert.Equal(125, s.Get(Stat.Agility));
    }

    [Fact]
    public void Cache_stays_correct_under_random_mutation()
    {
        // Compares the cached block against a freshly built one after every random change.
        var rng = new Randomness.DeterministicRandom(42);
        var stats = new[] { Stat.Level, Stat.Psyche, Stat.Stamina, Stat.BodyDevelopment, Stat.Intellect };
        var s = new StatBlock();
        var baseValues = new Dictionary<Stat, int>();
        var mods = new List<Modifier>();

        for (var step = 0; step < 2_000; step++)
        {
            var stat = stats[rng.NextInt(0, stats.Length - 1)];
            switch (rng.NextInt(0, 2))
            {
                case 0:
                    var v = rng.NextInt(0, 500);
                    s.SetBase(stat, v);
                    baseValues[stat] = v;
                    break;
                case 1:
                    var m = Modifier.Flat(stat, rng.NextInt(-50, 50), new ModifierSource(SourceKind.Debug, rng.NextInt(1, 8)));
                    s.AddModifier(m);
                    mods.Add(m);
                    break;
                default:
                    var src = new ModifierSource(SourceKind.Debug, rng.NextInt(1, 8));
                    s.RemoveSource(src);
                    mods.RemoveAll(x => x.Source == src);
                    break;
            }

            var fresh = new StatBlock();
            foreach (var (k, v) in baseValues)
            {
                fresh.SetBase(k, v);
            }
            foreach (var m in mods)
            {
                fresh.AddModifier(m);
            }
            foreach (var check in new[] { Stat.MaxHealth, Stat.MaxEnergy, Stat.LatticeCapacity, stat })
            {
                Assert.Equal(fresh.Get(check), s.Get(check));
            }
        }
    }
}
