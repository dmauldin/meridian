using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;

namespace Meridian.Content.Tests;

public class OutfitTests
{
    private static readonly ContentPack Pack = ContentPacks.Embedded;
    private static readonly int Gloves = Pack.Items["armor.marksman_gloves"].Id;
    private static readonly int Kestrel = Pack.Items["rifle.kestrel_mk2"].Id;
    private static readonly int Carbine = Pack.Items["rifle.combine_carbine"].Id;
    private static readonly int Sidearm = Pack.Items["pistol.combine_sidearm"].Id;
    private static readonly int Implant = Pack.Items["implant.ocular_reflex"].Id;

    /// <summary>An Operative with Rifle 35 and Agility 30: a QL 1 Kestrel (Rifle 40) is just out of reach.</summary>
    private static (Character Who, Outfit Outfit) Operative(int inventory = 10, Profession profession = Profession.Operative)
    {
        var who = new Character(profession, Origin.Baseline);
        who.Stats.SetBase(Stat.Rifle, 35);
        who.Stats.SetBase(Stat.Agility, 30);
        who.Stats.SetBase(Stat.Pistol, 10);
        return (who, new Outfit(Pack, who, inventory));
    }

    [Fact]
    public void Items_fill_the_first_free_position_until_full()
    {
        var (_, o) = Operative(inventory: 2);
        Assert.Equal(0, o.Add(new(Gloves, 1)));
        Assert.Equal(1, o.Add(new(Gloves, 2)));
        Assert.Equal(-1, o.Add(new(Gloves, 3)));
        Assert.Equal(new OwnedItem(Gloves, 1), o.Discard(0));
        Assert.Equal(0, o.Add(new(Gloves, 3)));
    }

    [Fact]
    public void Quality_is_clamped_to_the_items_range()
    {
        var (_, o) = Operative();
        o.Add(new(Gloves, 999));
        Assert.Equal(300, o.InventoryAt(0)!.Value.Ql);
    }

    [Fact]
    public void Equipping_moves_the_item_and_applies_its_bonuses()
    {
        var (who, o) = Operative();
        o.Add(new(Gloves, 1));
        var before = o.Revision;
        Assert.Equal(ItemResult.Done, o.Equip(0));
        Assert.Null(o.InventoryAt(0));
        Assert.True(o.IsActive(EquipSlot.Hands));
        Assert.Equal(40, who.Get(Stat.Rifle)); // +5 at QL 1
        Assert.True(o.Revision > before);
    }

    [Fact]
    public void A_refused_equip_says_why_and_changes_nothing()
    {
        var (who, o) = Operative();
        o.Add(new(Kestrel, 1));
        var failures = new List<RequirementFailure>();
        var before = o.Revision;
        Assert.Equal(ItemResult.RequirementsNotMet, o.Equip(0, failures));
        Assert.Contains(new StatShortfall(Stat.Rifle, 35, 40), failures);
        Assert.Equal(new OwnedItem(Kestrel, 1), o.InventoryAt(0));
        Assert.Equal(before, o.Revision);
        Assert.Equal(35, who.Get(Stat.Rifle));
    }

    [Fact]
    public void Twinking_another_items_bonus_works_and_losing_it_deactivates_the_twinked_item()
    {
        var (who, o) = Operative();
        o.Add(new(Gloves, 1));
        o.Add(new(Kestrel, 1));
        Assert.Equal(ItemResult.Done, o.Equip(0));          // Rifle 35 -> 40
        Assert.Equal(ItemResult.Done, o.Equip(1));          // now meets the Kestrel's Rifle 40
        Assert.Equal("Kestrel Mk2", o.ActiveWeapon!.Name);
        Assert.Empty(o.Reevaluate());                       // nothing changed since

        Assert.Equal(ItemResult.Done, o.Unequip(EquipSlot.Hands));
        Assert.Equal([(EquipSlot.TwoHand, false)], o.Reevaluate());
        Assert.True(o.IsWorn(EquipSlot.TwoHand));           // stays worn...
        Assert.Null(o.ActiveWeapon);                        // ...but cannot be used
        Assert.Equal(35, who.Get(Stat.Rifle));

        Assert.Equal(ItemResult.Done, o.Equip(o.Inventory.Single(i => i.Item.TemplateId == Gloves).Position));
        Assert.Equal([(EquipSlot.TwoHand, true)], o.Reevaluate());
        Assert.NotNull(o.ActiveWeapon);
    }

    [Fact]
    public void An_item_cannot_lean_on_the_item_it_replaces()
    {
        var (_, o) = Operative();
        o.Add(new(Gloves, 1));
        o.Add(new(Kestrel, 1));
        o.Add(new(Carbine, 1));
        o.Equip(0);                                        // gloves
        o.Equip(1);                                        // Kestrel, via the gloves
        // A second pair of gloves at QL 300 needs Agility 520: refused, and the first pair stays on.
        var pos = o.Add(new(Gloves, 300));
        Assert.Equal(ItemResult.RequirementsNotMet, o.Equip(pos));
        Assert.True(o.IsActive(EquipSlot.Hands));
        Assert.True(o.IsActive(EquipSlot.TwoHand));
    }

    [Fact]
    public void Equipping_over_a_worn_item_swaps_it_into_the_freed_position()
    {
        var (_, o) = Operative();
        o.Add(new(Gloves, 1));
        o.Add(new(Gloves, 2));
        o.Equip(0);
        Assert.Equal(ItemResult.Done, o.Equip(1));
        Assert.Equal(new OwnedItem(Gloves, 1), o.InventoryAt(1));
        Assert.Equal(new OwnedItem(Gloves, 2), o.Worn.Single().Item);
    }

    [Fact]
    public void A_two_hander_takes_both_hands_and_needs_room_for_what_comes_off()
    {
        var (_, o) = Operative(inventory: 2);
        o.Add(new(Sidearm, 1));
        o.Equip(0);                                        // main hand
        o.Add(new(Carbine, 1));
        Assert.Equal(ItemResult.Done, o.Equip(0));         // carbine displaces the sidearm into position 0
        Assert.True(o.IsWorn(EquipSlot.TwoHand));
        Assert.False(o.IsWorn(EquipSlot.MainHand));
        Assert.Equal(new OwnedItem(Sidearm, 1), o.InventoryAt(0));

        Assert.Equal(ItemResult.Done, o.Equip(0));         // and back: the sidearm displaces the carbine
        Assert.True(o.IsWorn(EquipSlot.MainHand));
        Assert.Equal(new OwnedItem(Carbine, 1), o.InventoryAt(0));
    }

    [Fact]
    public void Unequipping_needs_a_free_position()
    {
        var (_, o) = Operative(inventory: 1);
        o.Add(new(Gloves, 1));
        o.Equip(0);
        o.Add(new(Gloves, 2));
        Assert.Equal(ItemResult.InventoryFull, o.Unequip(EquipSlot.Hands));
        Assert.Equal(ItemResult.NothingWorn, o.Unequip(EquipSlot.Feet));
    }

    [Fact]
    public void Profession_locked_items_refuse_other_professions()
    {
        var (who, o) = Operative(profession: Profession.Medic);
        who.Stats.SetBase(Stat.Rifle, 500);
        o.Add(new(Kestrel, 1));
        var failures = new List<RequirementFailure>();
        Assert.Equal(ItemResult.RequirementsNotMet, o.Equip(0, failures));
        Assert.Contains(failures, f => f is ProfessionNotAllowed);
    }

    [Fact]
    public void Bad_positions_and_unknown_items_are_refused()
    {
        var (_, o) = Operative();
        Assert.Equal(ItemResult.NoItemThere, o.Equip(-1));
        Assert.Equal(ItemResult.NoItemThere, o.Equip(99));
        Assert.Equal(ItemResult.NoItemThere, o.Equip(0));
    }

    [Fact]
    public void Implants_wear_in_their_own_slots()
    {
        var (who, o) = Operative();
        o.Add(new(Implant, 1));
        Assert.Equal(ItemResult.Done, o.Equip(0));
        Assert.True(o.IsActive(EquipSlot.ImplantEye));
        Assert.Equal(33, who.Get(Stat.Agility));
    }

    [Fact]
    public void Loading_keeps_a_twinked_item_worn_but_inactive_and_skips_what_it_cannot_trust()
    {
        var (_, o) = Operative();
        var skipped = o.Load(
            [(0, new OwnedItem(Gloves, 5)), (1, new OwnedItem(99_999, 1)), (99, new OwnedItem(Gloves, 1))],
            [(EquipSlot.TwoHand, new OwnedItem(Kestrel, 1)), (EquipSlot.Feet, new OwnedItem(Gloves, 1))]);
        Assert.Equal(3, skipped.Count);                        // unknown item, bad position, gloves on feet
        Assert.True(o.IsWorn(EquipSlot.TwoHand));
        Assert.False(o.IsActive(EquipSlot.TwoHand));           // Rifle 35 < 40: worn, inactive
        Assert.Equal(new OwnedItem(Gloves, 5), o.InventoryAt(0));
        Assert.Empty(o.Reevaluate());                          // load already settled
    }

    [Fact]
    public void Fists_are_the_unarmed_weapon()
    {
        Assert.Equal("Fists", Pack.UnarmedWeapon.Name);
        Assert.Equal(Stat.Unarmed, Pack.UnarmedWeapon.AttackSkill);
    }
}
