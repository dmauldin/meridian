using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Protocols;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;

namespace Meridian.Content.Tests;

public class LatticeTests
{
    private const int TickMs = 50;
    private static readonly ContentPack Pack = ContentPacks.Embedded;
    private static readonly ProtocolTemplate RifleEnh1 = Pack.Protocols["buff.rifle_enhancement_1"];
    private static readonly ProtocolTemplate RifleEnh2 = Pack.Protocols["buff.rifle_enhancement_2"];
    private static readonly ProtocolTemplate ReflexBoost = Pack.Protocols["buff.reflex_boost_1"];
    private static readonly ProtocolTemplate Presence = Pack.Protocols["buff.intimidating_presence"];

    /// <summary>A fresh character as content builds it, knowing its starting Protocols.</summary>
    private static (CharacterBuild Build, Lattice Lattice) Fresh(Profession profession)
    {
        var build = CharacterBuild.Create(Pack, profession, Origin.Baseline);
        var lattice = new Lattice(Pack, build.Character, TickMs);
        foreach (var key in build.Profession.StartingProtocols)
        {
            lattice.Learn(Pack.Protocols[key].Id);
        }
        return (build, lattice);
    }

    [Fact]
    public void A_fresh_character_has_full_energy_and_its_starting_protocols()
    {
        var (b, l) = Fresh(Profession.Medic);
        Assert.Equal(b.Character.Get(Stat.MaxEnergy), l.Energy);
        Assert.Equal([ReflexBoost.Id], l.Known);
    }

    [Fact]
    public void A_buff_lands_on_another_player_costs_energy_and_raises_their_stats()
    {
        var (_, medic) = Fresh(Profession.Medic);
        var (op, target) = Fresh(Profession.Operative);
        var energy = medic.Energy;
        Assert.Equal(CastResult.Applied, medic.Cast(ReflexBoost, target, true, 25_000L * 25_000, 0));
        Assert.Equal(energy - ReflexBoost.EnergyCost, medic.Energy);
        Assert.Equal(40, op.Character.Get(Stat.Agility));
        Assert.Equal(CastResult.Renewed, medic.Cast(ReflexBoost, target, true, 25_000L * 25_000, 10));
    }

    [Fact]
    public void Refusals_come_in_a_fixed_order_and_cost_nothing()
    {
        var (_, medic) = Fresh(Profession.Medic);
        var (_, op) = Fresh(Profession.Operative);
        var (_, warden) = Fresh(Profession.Warden);
        var energy = medic.Energy;
        Assert.Equal(CastResult.NotKnown, medic.Cast(RifleEnh1, op, true, 0, 0));
        Assert.Equal(CastResult.SelfOnly, warden.Cast(Presence, op, true, 0, 0));
        Assert.Equal(CastResult.TargetDead, medic.Cast(ReflexBoost, op, false, 0, 0));
        Assert.Equal(CastResult.OutOfRange, medic.Cast(ReflexBoost, op, true, (ReflexBoost.RangeMm + 1L) * (ReflexBoost.RangeMm + 1), 0));
        medic.SetEnergy(ReflexBoost.EnergyCost - 1);
        Assert.Equal(CastResult.NotEnoughEnergy, medic.Cast(ReflexBoost, op, true, 0, 0));
        Assert.Equal(ReflexBoost.EnergyCost - 1, medic.Energy);
        Assert.True(energy > medic.Energy);
    }

    [Fact]
    public void Self_casts_ignore_range_and_self_only_protocols_work_on_yourself()
    {
        var (w, warden) = Fresh(Profession.Warden);
        w.Character.Stats.SetBase(Stat.MindCraft, 15);
        Assert.Equal(CastResult.Applied, warden.Cast(Presence, warden, true, 999_999, 0));
    }

    [Fact]
    public void Cast_requirements_include_the_casters_skills()
    {
        var (b, op) = Fresh(Profession.Operative);
        op.Learn(RifleEnh2.Id);
        var failures = new List<RequirementFailure>();
        Assert.Equal(CastResult.RequirementsNotMet, op.Cast(RifleEnh2, op, true, 0, 0, failures));
        Assert.Contains(new StatShortfall(Stat.KineticFlux, b.Character.Get(Stat.KineticFlux), 120), failures);
    }

    [Fact]
    public void Lines_keep_only_the_strongest_and_capacity_is_enforced()
    {
        var (b, op) = Fresh(Profession.Operative);
        b.Character.Stats.SetBase(Stat.KineticFlux, 120);
        op.Learn(RifleEnh2.Id);
        Assert.Equal(CastResult.Applied, op.Cast(RifleEnh1, op, true, 0, 0));
        Assert.Equal(CastResult.Replaced, op.Cast(RifleEnh2, op, true, 0, 1));
        Assert.Equal(CastResult.StrongerRunning, op.Cast(RifleEnh1, op, true, 0, 2));

        var (_, medic) = Fresh(Profession.Medic);
        var (tiny, target) = Fresh(Profession.Operative);
        tiny.Character.Stats.SetBase(Stat.Psyche, 0); // capacity 10 + 2 + 0 = 12
        target.Running.TryApply(RifleEnh2.ToDef(TickMs), 0); // 8 used
        Assert.Equal(CastResult.LatticeFull, medic.Cast(ReflexBoost, target, true, 0, 0)); // needs 5
    }

    [Fact]
    public void Energy_regenerates_and_buffs_expire_on_tick()
    {
        var (_, medic) = Fresh(Profession.Medic);
        var (op, target) = Fresh(Profession.Operative);
        medic.Cast(ReflexBoost, target, true, 0, 0);
        var spent = medic.Energy;
        medic.Tick(1);
        Assert.True(medic.EnergyMilli > (long)spent * 1000);

        var lastTick = (ReflexBoost.DurationMs / TickMs) - 1;
        Assert.Empty(target.Tick(lastTick));
        var before = target.Revision;
        Assert.Single(target.Tick(lastTick + 1));
        Assert.True(target.Revision > before);
        Assert.Equal(20, op.Character.Get(Stat.Agility));
    }

    [Fact]
    public void Uploading_needs_the_cast_requirements_and_is_permanent()
    {
        var (b, op) = Fresh(Profession.Operative);
        Assert.Equal(UploadResult.AlreadyKnown, op.Upload(Pack.Items["protocol.rifle_enhancement_1"]));
        Assert.Equal(UploadResult.RequirementsNotMet, op.Upload(Pack.Items["protocol.reflex_boost_1"])); // Medic only
        Assert.Equal(UploadResult.RequirementsNotMet, op.Upload(Pack.Items["protocol.rifle_enhancement_2"])); // KineticFlux 120
        Assert.Equal(UploadResult.NotAProtocol, op.Upload(Pack.Items["armor.marksman_gloves"]));
        b.Character.Stats.SetBase(Stat.KineticFlux, 120);
        Assert.Equal(UploadResult.Learned, op.Upload(Pack.Items["protocol.rifle_enhancement_2"]));
        Assert.Contains(RifleEnh2.Id, op.Known);
    }

    [Fact]
    public void Saved_buffs_restore_with_their_remaining_time_under_the_same_rules()
    {
        var (op, l) = Fresh(Profession.Operative);
        Assert.True(l.Restore(ReflexBoost.Id, remainingMs: 1_000, nowTick: 100));
        Assert.Equal([(ReflexBoost.Id, 1_000)], l.RunningWithRemaining(100));
        Assert.Equal(40, op.Character.Get(Stat.Agility));
        Assert.False(l.Restore(99_999, 1_000, 100));
        Assert.False(l.Restore(RifleEnh1.Id, 0, 100));
    }

    [Fact]
    public void Death_clears_everything_running()
    {
        var (op, l) = Fresh(Profession.Operative);
        l.Cast(RifleEnh1, l, true, 0, 0);
        l.ClearRunning();
        Assert.Empty(l.Running.Running);
        Assert.Equal(30, op.Character.Get(Stat.Rifle));
    }

    /// <summary>
    /// The M2 gate as pure rules: a fresh Operative cannot wear a QL 1 Kestrel (Rifle 40,
    /// Agility 30) until a Medic's Reflex Boost (+20 Agility) joins its own Rifle Enhancement
    /// (+15 Rifle); when the Medic's buff runs out the rifle goes inactive.
    /// </summary>
    [Fact]
    public void Twinking_a_rifle_with_another_players_buff()
    {
        var (_, medic) = Fresh(Profession.Medic);
        var (op, lattice) = Fresh(Profession.Operative);
        var outfit = new Outfit(Pack, op.Character, 30);
        var kestrel = outfit.Add(new OwnedItem(Pack.Items["rifle.kestrel_mk2"].Id, 1));

        lattice.Cast(RifleEnh1, lattice, true, 0, 0);                          // Rifle 45, Agility 20
        Assert.Equal(ItemResult.RequirementsNotMet, outfit.Equip(kestrel));

        Assert.Equal(CastResult.Applied, medic.Cast(ReflexBoost, lattice, true, 10_000L * 10_000, 0)); // Agility 40
        Assert.Equal(ItemResult.Done, outfit.Equip(kestrel));
        outfit.Reevaluate();

        lattice.Cancel(ReflexBoost.Id);                                       // the Medic's buff ends
        Assert.Equal([(EquipSlot.TwoHand, false)], outfit.Reevaluate());
        Assert.Null(outfit.ActiveWeapon);
    }
}
