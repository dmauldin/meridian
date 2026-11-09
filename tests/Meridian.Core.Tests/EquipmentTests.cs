using Meridian.Core.Characters;
using Meridian.Core.Items;
using Meridian.Core.Protocols;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;

namespace Meridian.Core.Tests;

/// <summary>The twinking rules from the design doc, as executable examples.</summary>
public class EquipmentTests
{
    private static Character NewOperative()
    {
        var c = new Character(Profession.Operative, Origin.Splice);
        c.Stats.SetBase(Stat.Level, 30);
        c.Stats.SetBase(Stat.Psyche, 60);
        c.Stats.SetBase(Stat.Agility, 180);
        c.Stats.SetBase(Stat.Rifle, 380);
        return c;
    }

    // +30 Agility eye implant, needs Agility 150.
    private static readonly EquippableItem AgilityImplant = new(
        1, "Ocular Reflex Implant", EquipSlot.ImplantEye,
        Requirement.AtLeast(Stat.Agility, 150),
        [new StatEffect(Stat.Agility, ModifierKind.Flat, 30)]);

    // Armor that needs Agility 210 and gives +40 Rifle.
    private static readonly EquippableItem MarksmanGloves = new(
        2, "Marksman Gloves", EquipSlot.Hands,
        Requirement.AtLeast(Stat.Agility, 210),
        [new StatEffect(Stat.Rifle, ModifierKind.Flat, 40)]);

    // The goal: a rifle that needs Rifle 420.
    private static readonly EquippableItem Kestrel = new(
        3, "Kestrel Mk2", EquipSlot.TwoHand,
        Requirement.AtLeast(Stat.Rifle, 420),
        [new StatEffect(Stat.AddDamage, ModifierKind.Flat, 25)]);

    [Fact]
    public void Twink_chain_reaches_a_rifle_the_character_could_not_equip_alone()
    {
        var c = NewOperative();
        var gear = new Equipment(c);

        Assert.Equal(EquipResult.RequirementsNotMet, gear.TryEquip(Kestrel));
        Assert.Equal(EquipResult.RequirementsNotMet, gear.TryEquip(MarksmanGloves));

        Assert.Equal(EquipResult.Equipped, gear.TryEquip(AgilityImplant));   // Agility 180 → 210
        Assert.Equal(EquipResult.Equipped, gear.TryEquip(MarksmanGloves));   // Rifle 380 → 420
        Assert.Equal(EquipResult.Equipped, gear.TryEquip(Kestrel));
        Assert.Equal(25, c.Get(Stat.AddDamage));
    }

    [Fact]
    public void An_items_own_bonus_never_counts_toward_its_own_requirement()
    {
        var c = NewOperative();
        c.Stats.SetBase(Stat.Agility, 100);
        // Needs Agility 120 and gives +50 Agility: without the rule it would hold itself up.
        var bootstrap = new EquippableItem(9, "Bootstrap Boots", EquipSlot.Feet,
            Requirement.AtLeast(Stat.Agility, 120), [new StatEffect(Stat.Agility, ModifierKind.Flat, 50)]);
        var buffSource = new ModifierSource(SourceKind.Protocol, 77);
        c.Stats.AddModifier(Modifier.Flat(Stat.Agility, 20, buffSource));

        var gear = new Equipment(c);
        Assert.Equal(EquipResult.Equipped, gear.TryEquip(bootstrap));
        Assert.Equal(170, c.Get(Stat.Agility));

        c.Stats.RemoveSource(buffSource);
        gear.Reevaluate();
        Assert.False(gear.IsActive(EquipSlot.Feet));
        Assert.Equal(100, c.Get(Stat.Agility));
    }

    [Fact]
    public void Losing_a_buff_deactivates_dependent_gear_and_rebuffing_restores_it()
    {
        var c = NewOperative();
        var buffs = new ActiveProtocols(c.Stats);
        var agilityBuff = new ProtocolDef(50, "Reflex Booster", new ProtocolLine(2), 1, 5, 10, 100,
            Requirement.None, [new StatEffect(Stat.Agility, ModifierKind.Flat, 30)]);
        var gear = new Equipment(c);

        buffs.TryApply(agilityBuff, 0);             // Agility 210 via buff instead of implant
        gear.TryEquip(MarksmanGloves);               // Rifle 420
        gear.TryEquip(Kestrel);
        Assert.True(gear.IsActive(EquipSlot.TwoHand));

        buffs.Expire(100);
        Assert.Equal(2, gear.Reevaluate());          // gloves drop, then the rifle they enabled
        Assert.False(gear.IsActive(EquipSlot.Hands));
        Assert.False(gear.IsActive(EquipSlot.TwoHand));
        Assert.Equal(0, c.Get(Stat.AddDamage));
        Assert.Equal(2, gear.Items.Count());         // still worn, just inactive

        buffs.TryApply(agilityBuff, 200);
        Assert.Equal(2, gear.Reevaluate());
        Assert.True(gear.IsActive(EquipSlot.TwoHand));
        Assert.Equal(25, c.Get(Stat.AddDamage));
    }

    [Fact]
    public void Unequipping_an_enabler_deactivates_what_depended_on_it()
    {
        var c = NewOperative();
        var gear = new Equipment(c);
        gear.TryEquip(AgilityImplant);
        gear.TryEquip(MarksmanGloves);
        gear.TryEquip(Kestrel);

        Assert.Same(AgilityImplant, gear.Unequip(EquipSlot.ImplantEye));
        Assert.False(gear.IsActive(EquipSlot.Hands));
        Assert.False(gear.IsActive(EquipSlot.TwoHand));
        Assert.Equal(380, c.Get(Stat.Rifle));
    }

    [Fact]
    public void Occupied_slot_is_refused()
    {
        var c = NewOperative();
        var gear = new Equipment(c);
        gear.TryEquip(AgilityImplant);
        Assert.Equal(EquipResult.SlotOccupied, gear.TryEquip(AgilityImplant with { InstanceId = 10 }));
    }

    [Fact]
    public void Failed_equip_reports_what_is_missing()
    {
        var gear = new Equipment(NewOperative());
        var failures = new List<RequirementFailure>();
        gear.TryEquip(Kestrel, failures);
        Assert.Equal(new StatShortfall(Stat.Rifle, 380, 420), Assert.Single(failures));
    }

    [Fact]
    public void Reevaluate_terminates_with_self_defeating_content()
    {
        // Two items that each penalize the stat the other needs: a potential oscillation.
        var c = NewOperative();
        c.Stats.SetBase(Stat.Sense, 100);
        c.Stats.SetBase(Stat.Intellect, 100);
        var a = new EquippableItem(20, "A", EquipSlot.Head, Requirement.AtLeast(Stat.Sense, 100),
            [new StatEffect(Stat.Intellect, ModifierKind.Flat, -10)]);
        var b = new EquippableItem(21, "B", EquipSlot.Back, Requirement.AtLeast(Stat.Intellect, 100),
            [new StatEffect(Stat.Sense, ModifierKind.Flat, -10)]);
        var gear = new Equipment(c);
        gear.TryEquip(a);
        gear.TryEquip(b);
        gear.Reevaluate(); // must return, whatever state it settles in
    }
}
