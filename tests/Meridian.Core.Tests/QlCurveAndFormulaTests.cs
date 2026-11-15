using Meridian.Core.Combat;
using Meridian.Core.Items;
using Meridian.Core.Randomness;

namespace Meridian.Core.Tests;

public class QlCurveTests
{
    private static readonly QlCurve RifleReq = new(new QlAnchor(1, 4), new QlAnchor(100, 400), new QlAnchor(300, 1150));

    [Theory]
    [InlineData(1, 4)]
    [InlineData(100, 400)]
    [InlineData(300, 1150)]
    [InlineData(50, 200)]     // 4 + 396 * 49/99 = 200 exactly
    [InlineData(147, 576)]    // 400 + 750 * 47/200 = 576.25
    [InlineData(200, 775)]    // 400 + 375
    public void Interpolates_between_anchors(int ql, int expected)
    {
        Assert.Equal(expected, RifleReq.At(ql));
    }

    [Fact]
    public void Clamps_outside_anchors()
    {
        var c = new QlCurve(new QlAnchor(50, 10), new QlAnchor(60, 20));
        Assert.Equal(10, c.At(1));
        Assert.Equal(20, c.At(300));
    }

    [Fact]
    public void Rounds_half_away_from_zero_for_decreasing_curves()
    {
        var c = new QlCurve(new QlAnchor(1, 0), new QlAnchor(3, -1)); // QL 2 = -0.5
        Assert.Equal(-1, c.At(2));
    }

    [Fact]
    public void Is_monotonic_when_anchors_are()
    {
        var prev = int.MinValue;
        for (var ql = 1; ql <= 300; ql++)
        {
            var v = RifleReq.At(ql);
            Assert.True(v >= prev);
            prev = v;
        }
    }

    [Fact]
    public void Rejects_bad_anchors()
    {
        Assert.Throws<ArgumentException>(() => new QlCurve());
        Assert.Throws<ArgumentOutOfRangeException>(() => new QlCurve(new QlAnchor(301, 1)));
        Assert.Throws<ArgumentException>(() => new QlCurve(new QlAnchor(5, 1), new QlAnchor(5, 2)));
    }
}

public class FormulaTests
{
    [Theory]
    [InlineData(0, 0, 6_666)]
    [InlineData(500, 500, 6_666)]
    [InlineData(1_000, 500, 7_998)]
    [InlineData(10, 10_000, 500)]   // floor
    [InlineData(10_000, 10, 9_500)] // ceiling
    public void Hit_chance_golden_values(int attack, int defence, int expectedBp)
    {
        Assert.Equal(expectedBp, Formulas.HitChance(attack, defence));
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 1_500, 50)]
    [InlineData(100, 4_500, 25)]
    [InlineData(1, 100_000, 1)] // never below 1
    [InlineData(0, 0, 0)]
    public void Mitigation_golden_values(int damage, int ac, int expected)
    {
        Assert.Equal(expected, Formulas.Mitigate(damage, ac));
    }

    [Theory]
    [InlineData(2_000, 0, 2_000)]
    [InlineData(2_000, 300, 1_400)]
    [InlineData(2_000, 5_000, 1_000)] // capped at half
    public void Action_time_golden_values(int baseMs, int init, int expected)
    {
        Assert.Equal(expected, Formulas.ActionTimeMs(baseMs, init));
    }

    [Theory]
    [InlineData(100, 0, 100)]          // threat = damage
    [InlineData(100, 5_000, 150)]      // tank stance
    [InlineData(100, -3_000, 70)]      // stealth buff
    [InlineData(100, -10_000, 0)]      // fully suppressed
    [InlineData(100, -20_000, 0)]      // never negative
    [InlineData(0, 0, 1)]              // a miss still counts
    [InlineData(0, 10_000, 2)]
    public void Threat_golden_values(int damage, int modifierBp, long expected)
    {
        Assert.Equal(expected, Formulas.Threat(damage, modifierBp));
    }

    [Fact]
    public void Same_seed_gives_identical_combat()
    {
        static List<AttackOutcome> Fight(ulong seed)
        {
            var rng = new DeterministicRandom(seed);
            var results = new List<AttackOutcome>();
            for (var i = 0; i < 500; i++)
            {
                results.Add(Formulas.ResolveAttack(rng, 600, 500, 40, 120, 10, 1_000, 800));
            }
            return results;
        }
        Assert.Equal(Fight(7), Fight(7));
        Assert.NotEqual(Fight(7), Fight(8));
    }

    [Fact]
    public void Attack_outcomes_stay_in_range()
    {
        var rng = new DeterministicRandom(1);
        for (var i = 0; i < 10_000; i++)
        {
            var o = Formulas.ResolveAttack(rng, 600, 500, 40, 120, 10, 1_000, 0);
            if (o.Hit)
            {
                Assert.InRange(o.Damage, 50, o.Critical ? 260 : 130);
            }
            else
            {
                Assert.Equal(0, o.Damage);
            }
        }
    }
}

public class DeterministicRandomTests
{
    [Fact]
    public void Golden_sequence_is_stable_across_platforms_and_versions()
    {
        // If this changes, every recorded replay breaks. Change it only on purpose.
        var rng = new DeterministicRandom(12345);
        var first = Enumerable.Range(0, 3).Select(_ => rng.NextUInt64()).ToArray();
        var again = new DeterministicRandom(12345);
        Assert.Equal(first, Enumerable.Range(0, 3).Select(_ => again.NextUInt64()).ToArray());
        Assert.Equal(GoldenFirstValue, first[0]);
    }

    // Cross-checked against an independent reference implementation of SplitMix64 + xoshiro256**.
    private const ulong GoldenFirstValue = 13_720_838_825_685_603_483;

    [Fact]
    public void NextInt_covers_the_full_inclusive_range_evenly()
    {
        var rng = new DeterministicRandom(99);
        var counts = new int[6];
        for (var i = 0; i < 60_000; i++)
        {
            counts[rng.NextInt(0, 5)]++;
        }
        Assert.All(counts, c => Assert.InRange(c, 9_500, 10_500));
    }

    [Fact]
    public void NextInt_handles_extreme_ranges()
    {
        var rng = new DeterministicRandom(3);
        for (var i = 0; i < 1_000; i++)
        {
            Assert.InRange(rng.NextInt(int.MinValue, int.MaxValue), int.MinValue, int.MaxValue);
            Assert.Equal(5, rng.NextInt(5, 5));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(2, 1));
    }
}

public class WeaponFormulaTests
{
    private static readonly Meridian.Core.Combat.WeaponProfile Rifle = new(
        "Test Rifle", Meridian.Core.Stats.Stat.Rifle, Meridian.Core.Stats.Stat.RangedInit, Meridian.Core.Stats.Stat.EvadeRanged,
        DamageType.Projectile, 10, 20, 1_500, 25_000);

    [Fact]
    public void Profile_overload_reads_the_right_stats()
    {
        var attacker = new Meridian.Core.Stats.StatBlock();
        attacker.SetBase(Meridian.Core.Stats.Stat.Rifle, 600);
        attacker.SetBase(Meridian.Core.Stats.Stat.AddDamage, 5);
        attacker.SetBase(Meridian.Core.Stats.Stat.RangedInit, 250);
        var defender = new Meridian.Core.Stats.StatBlock();
        defender.SetBase(Meridian.Core.Stats.Stat.EvadeRanged, 500);
        defender.SetBase(Meridian.Core.Stats.Stat.ProjectileAC, 800);

        var viaProfile = new DeterministicRandom(9);
        var viaRaw = new DeterministicRandom(9);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(
                Formulas.ResolveAttack(viaRaw, 600, 500, 10, 20, 5, 0, 800),
                Formulas.ResolveAttack(viaProfile, Rifle, attacker, defender));
        }
        Assert.Equal(1_000, Formulas.AttackTimeMs(Rifle, attacker));
    }

    [Fact]
    public void Every_damage_type_maps_to_an_armor_stat()
    {
        foreach (var t in Enum.GetValues<DamageType>())
        {
            Assert.EndsWith("AC", Formulas.ArmorFor(t).ToString(), StringComparison.Ordinal);
        }
    }

    // Golden values: changing Formulas.EnergyRegenBpPerSecond must change these.
    [Theory]
    [InlineData(125, 50, 125)]     // a fresh level-1 character: 2.5 energy per second at 20 Hz
    [InlineData(1_000, 50, 1_000)]
    [InlineData(0, 50, 0)]
    [InlineData(-5, 50, 0)]
    public void Energy_regenerates_two_percent_of_maximum_per_second(int max, int tickMs, long milliPerTick)
    {
        Assert.Equal(milliPerTick, Formulas.EnergyRegenMilliPerTick(max, tickMs));
    }
}
