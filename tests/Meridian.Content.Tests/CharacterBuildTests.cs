using Meridian.Content;
using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Stats;

namespace Meridian.Content.Tests;

public class CharacterBuildTests
{
    private static readonly ContentPack Pack = ContentPacks.Embedded;

    private static CharacterBuild New(Profession p = Profession.Operative, Origin o = Origin.Baseline) => CharacterBuild.Create(Pack, p, o);

    [Fact]
    public void A_baseline_operative_starts_exactly_where_M1_tuned_the_player()
    {
        var b = New();
        var s = b.Character.Stats;
        Assert.Equal(1, b.Level);
        Assert.Equal(30, s.Get(Stat.Rifle));         // common 20 + Operative 10
        Assert.Equal(20, s.Get(Stat.KineticFlux));
        Assert.Equal(20, s.Get(Stat.Agility));        // Baseline attribute
        Assert.Equal(108, s.Get(Stat.MaxHealth));     // 50 + 8 + 3x10 + 20, as in M1
        Assert.Equal(Pack.Progression.StartingPoints, b.UnspentPoints);
        Assert.Equal("rifle.combine_carbine", b.Profession.StartingWeaponKey);
        Assert.Equal(Pack.Items["rifle.combine_carbine"].WeaponAt(b.Profession.StartingWeaponQl), b.StartingWeapon(Pack));
    }

    [Fact]
    public void Origins_set_attributes_and_professions_add_skills()
    {
        var b = New(Profession.Overload, Origin.Nightborn);
        Assert.Equal(26, b.Character.Get(Stat.Psyche));
        Assert.Equal(14, b.Character.Get(Stat.Strength));
        Assert.Equal(25, b.Character.Get(Stat.MatterShaping));
        Assert.Equal(25, b.Character.Get(Stat.Pistol)); // common 10 + Overload 15
        Assert.Equal("pistol.combine_sidearm", b.Profession.StartingWeaponKey);
    }

    [Theory]
    [InlineData(Profession.Operative, Origin.Baseline, Stat.Rifle, CostTier.Cheap)]       // group: Ranged cheap
    [InlineData(Profession.Operative, Origin.Baseline, Stat.HeavyWeapons, CostTier.Expensive)] // exception
    [InlineData(Profession.Operative, Origin.Baseline, Stat.Blunt, CostTier.Expensive)]
    [InlineData(Profession.Operative, Origin.Baseline, Stat.EvadeRanged, CostTier.Normal)] // unlisted group
    [InlineData(Profession.Operative, Origin.Baseline, Stat.Agility, CostTier.Normal)]     // attribute: origin prices it
    [InlineData(Profession.Operative, Origin.Nightborn, Stat.Psyche, CostTier.Cheap)]
    [InlineData(Profession.Operative, Origin.Nightborn, Stat.Strength, CostTier.Expensive)]
    [InlineData(Profession.Warden, Origin.Baseline, Stat.MindCraft, CostTier.Normal)]      // exception inside an expensive group
    [InlineData(Profession.Medic, Origin.Baseline, Stat.MaxHealth, CostTier.Locked)]       // derived stats never train
    public void Tiers_come_from_profession_groups_exceptions_and_origin(Profession p, Origin o, Stat stat, CostTier expected)
    {
        Assert.Equal(expected, New(p, o).TierOf(stat));
    }

    [Fact]
    public void Training_spends_points_at_the_tier_price_up_to_the_cap()
    {
        var b = New();
        // Rifle is cheap for Operatives: 1 point each, cap = start 30 + level 1 x 8.
        Assert.Equal(38, b.Cap(Stat.Rifle));
        Assert.Equal(TrainResult.AboveCap, b.Train(Stat.Rifle, 9));
        Assert.Equal(TrainResult.Trained, b.Train(Stat.Rifle, 8));
        Assert.Equal(38, b.Character.Get(Stat.Rifle));
        Assert.Equal(52, b.UnspentPoints);

        // Agility is normal for Baseline: 2 points each.
        Assert.Equal(TrainResult.Trained, b.Train(Stat.Agility, 6));
        Assert.Equal(40, b.UnspentPoints);
    }

    [Fact]
    public void Each_refusal_changes_nothing()
    {
        var b = New();
        // Spend down to 8 points (caps at level 1 keep any single request under 60).
        Assert.Equal(TrainResult.Trained, b.Train(Stat.Rifle, 8));        // cheap: 8
        Assert.Equal(TrainResult.Trained, b.Train(Stat.Agility, 6));      // normal: 12
        Assert.Equal(TrainResult.Trained, b.Train(Stat.KineticFlux, 8));  // cheap: 8
        Assert.Equal(TrainResult.Trained, b.Train(Stat.Blunt, 4));        // expensive: 16
        Assert.Equal(TrainResult.Trained, b.Train(Stat.AimedShot, 8));    // cheap: 8
        Assert.Equal(8, b.UnspentPoints);

        var before = (b.UnspentPoints, b.Character.Stats.Revision);
        Assert.Equal(TrainResult.InvalidAmount, b.Train(Stat.Rifle, 0));
        Assert.Equal(TrainResult.InvalidAmount, b.Train(Stat.Rifle, -5));
        Assert.Equal(TrainResult.InvalidAmount, b.Train(Stat.Rifle, CharacterBuild.MaxPointsPerRequest + 1));
        Assert.Equal(TrainResult.NotTrainable, b.Train(Stat.MaxHealth, 1));
        Assert.Equal(TrainResult.AboveCap, b.Train(Stat.Rifle, 1));
        Assert.Equal(TrainResult.NotEnoughPoints, b.Train(Stat.EvadeRanged, 5)); // normal: 10 > 8
        Assert.Equal(before, (b.UnspentPoints, b.Character.Stats.Revision));
    }

    [Fact]
    public void Locked_skills_cannot_be_trained()
    {
        var dto = ContentPacks.ToDto(Pack);
        var operative = dto.Professions.Single(p => p.Key == "operative");
        operative.SkillTiers["Blades"] = "Locked";
        var pack = ContentPacks.ToModel(dto, 1);
        var b = CharacterBuild.Create(pack, Profession.Operative, Origin.Baseline);
        Assert.Equal(TrainResult.Locked, b.Train(Stat.Blades, 1));
        Assert.Equal(0, b.CostPerPoint(Stat.Blades));
    }

    [Fact]
    public void Experience_levels_up_grants_points_and_raises_caps()
    {
        var b = New();
        var toLevel2 = b.XpToNext;
        Assert.Equal(300, toLevel2);
        Assert.Equal(0, b.AddXp(299));
        Assert.Equal(1, b.AddXp(1));
        Assert.Equal(2, b.Level);
        Assert.Equal(0, b.Xp);
        // points_per_level at level 2 is 40, plus Baseline's +1.
        Assert.Equal(60 + 41, b.UnspentPoints);
        Assert.Equal(30 + (2 * 8), b.Cap(Stat.Rifle));
        Assert.Equal(116, b.Character.Get(Stat.MaxHealth)); // level feeds MaxHealth
    }

    [Fact]
    public void Experience_carries_over_across_several_levels_and_stops_at_the_cap()
    {
        var b = New();
        Assert.True(b.AddXp(5_000) >= 3);
        var levelBefore = b.Level;
        b.AddXp(long.MaxValue / 4);
        Assert.Equal(Pack.Progression.MaxLevel, b.Level);
        Assert.Equal(0, b.Xp);
        Assert.Equal(0, b.XpToNext);
        Assert.Equal(0, b.AddXp(1_000));
        Assert.True(b.Level > levelBefore);
    }

    [Fact]
    public void Restore_reproduces_a_trained_character()
    {
        var original = New(Profession.Medic, Origin.Nightborn);
        original.AddXp(700);
        original.Train(Stat.BioWeaving, 10);
        original.Train(Stat.Psyche, 5);

        var trained = Skills.Trainable.Select(s => new KeyValuePair<Stat, int>(s, original.Character.Stats.GetBase(s)));
        var copy = CharacterBuild.Restore(Pack, Profession.Medic, Origin.Nightborn, original.Level, original.Xp, original.UnspentPoints, trained);

        Assert.Equal((original.Level, original.Xp, original.UnspentPoints), (copy.Level, copy.Xp, copy.UnspentPoints));
        foreach (var stat in StatIndex.All)
        {
            Assert.Equal(original.Character.Get(stat), copy.Character.Get(stat));
        }
        Assert.Equal(original.Cap(Stat.BioWeaving), copy.Cap(Stat.BioWeaving));
    }

    [Fact]
    public void Every_trainable_stat_has_a_group_and_derived_stats_are_not_trainable()
    {
        foreach (var stat in Skills.Trainable)
        {
            _ = Skills.GroupOf(stat);
            Assert.False(StatCatalog.DefaultDerivedStats.Contains(stat), $"{stat} is derived");
        }
        Assert.False(Skills.IsTrainable(Stat.Level));
    }

    [Fact]
    public void Every_playable_profession_and_origin_can_be_created()
    {
        foreach (var p in Pack.Professions.All)
        {
            foreach (var o in Pack.Origins.All)
            {
                var b = CharacterBuild.Create(Pack, p.Id, o.Id);
                Assert.NotNull(b.StartingWeapon(Pack));
                Assert.True(b.Character.Get(Stat.MaxHealth) > 0);
            }
        }
    }
}

public class CharacterBuildStorageTests
{
    private static readonly ContentPack Pack = Meridian.Content.ContentPacks.Embedded;

    [Fact]
    public void Trained_raises_round_trip_through_load()
    {
        var b = CharacterBuild.Create(Pack, Profession.Medic, Origin.Nightborn);
        b.AddXp(5_000);
        Assert.Equal(TrainResult.Trained, b.Train(Stat.BioWeaving, 7));
        Assert.Equal(TrainResult.Trained, b.Train(Stat.Psyche, 3));

        Assert.Equal([new(Stat.Psyche, 3), new(Stat.BioWeaving, 7)], b.Trained().OrderBy(t => t.Key).ToList());
        var loaded = CharacterBuild.Load(Pack, Profession.Medic, Origin.Nightborn, b.Level, b.Xp, b.UnspentPoints, b.Trained());
        foreach (var stat in Meridian.Core.Stats.StatIndex.All)
        {
            Assert.Equal(b.Character.Get(stat), loaded.Character.Get(stat));
        }
        Assert.Equal((b.Level, b.Xp, b.UnspentPoints), (loaded.Level, loaded.Xp, loaded.UnspentPoints));
    }

    [Fact]
    public void A_fresh_character_has_nothing_trained()
    {
        Assert.Empty(CharacterBuild.Create(Pack, Profession.Warden, Origin.Baseline).Trained());
    }

    [Fact]
    public void Load_drops_raises_it_cannot_trust()
    {
        var loaded = CharacterBuild.Load(Pack, Profession.Operative, Origin.Baseline, level: 999, xp: -5, unspentPoints: -1,
            [new(Stat.MaxHealth, 500), new(Stat.Rifle, -10), new(Stat.Pistol, 4)]);
        var clean = CharacterBuild.Load(Pack, Profession.Operative, Origin.Baseline, level: 999, xp: 0, unspentPoints: 0, [new(Stat.Pistol, 4)]);
        foreach (var stat in Meridian.Core.Stats.StatIndex.All)
        {
            Assert.Equal(clean.Character.Get(stat), loaded.Character.Get(stat)); // MaxHealth (derived) and Rifle (negative) ignored
        }
        Assert.Equal(CharacterBuild.Create(Pack, Profession.Operative, Origin.Baseline).Character.Get(Stat.Pistol) + 4, loaded.Character.Get(Stat.Pistol));
        Assert.Equal(Pack.Progression.MaxLevel, loaded.Level);
        Assert.Equal((0L, 0), (loaded.Xp, loaded.UnspentPoints));
    }
}
