using Meridian.ClientState.Views;
using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Stats;

namespace Meridian.ClientState.Tests;

public class ViewTests
{
    private static readonly ContentPack Pack = Meridian.Content.ContentPacks.Embedded;

    private static (CharacterBuild Build, Dictionary<Stat, int> Effective) Operative()
    {
        var b = CharacterBuild.Create(Pack, Profession.Operative, Origin.Baseline);
        return (b, StatIndex.All.ToDictionary(s => s, b.Character.Get));
    }

    [Theory]
    [InlineData(Stat.KineticFlux, "Kinetic Flux")]
    [InlineData(Stat.MaxHealth, "Max Health")]
    [InlineData(Stat.Rifle, "Rifle")]
    public void Stat_names_are_spaced(Stat stat, string name) => Assert.Equal(name, StatNames.Display(stat));

    [Fact]
    public void The_sheet_groups_every_trainable_stat_and_shows_server_costs_and_caps()
    {
        var (b, eff) = Operative();
        var groups = CharacterSheetView.Build(b, eff);
        Assert.Equal(Skills.Trainable.Count, groups.Where(g => g.Title != "Derived").Sum(g => g.Rows.Count));
        var rifle = groups.SelectMany(g => g.Rows).Single(r => r.Stat == Stat.Rifle);
        Assert.Equal((30, 30, b.CostPerPoint(Stat.Rifle), b.Cap(Stat.Rifle), true), (rifle.Base, rifle.Effective, rifle.CostPerPoint, rifle.Cap, rifle.CanTrain));
        Assert.Equal("Derived", groups[^1].Title);
        Assert.All(groups[^1].Rows, r => Assert.False(r.Trainable));
    }

    [Fact]
    public void Effective_values_include_bonuses_the_base_does_not()
    {
        var (b, eff) = Operative();
        eff[Stat.Rifle] = 45; // a buff
        var rifle = CharacterSheetView.Build(b, eff).SelectMany(g => g.Rows).Single(r => r.Stat == Stat.Rifle);
        Assert.Equal((30, 45), (rifle.Base, rifle.Effective));
    }

    [Fact]
    public void Affordable_points_respect_the_cap_and_unspent_points()
    {
        var (b, _) = Operative();
        var room = b.Cap(Stat.Rifle) - b.Character.Stats.GetBase(Stat.Rifle);
        Assert.Equal(Math.Min(5, room), CharacterSheetView.Affordable(b, Stat.Rifle, 5));
        Assert.Equal(room, CharacterSheetView.Affordable(b, Stat.Rifle, 1_000));
        Assert.Equal(0, CharacterSheetView.Affordable(b, Stat.Rifle, 0));
        b.Train(Stat.Rifle, room);
        Assert.Equal(0, CharacterSheetView.Affordable(b, Stat.Rifle, 1));
        Assert.False(CharacterSheetView.Build(b, StatIndex.All.ToDictionary(s => s, b.Character.Get)).SelectMany(g => g.Rows).Single(r => r.Stat == Stat.Rifle).CanTrain);
    }

    [Fact]
    public void Item_details_show_requirements_against_your_stats()
    {
        var (_, eff) = Operative();
        var kestrel = new OwnedItem(Pack.Items["rifle.kestrel_mk2"].Id, 1);
        var d = ItemView.Describe(Pack, kestrel, eff, Profession.Operative);
        Assert.Equal("Kestrel Mk2 QL 1", d.Title);
        Assert.Equal("Two hands", d.Kind);
        Assert.Contains(new RequirementLine("Rifle 40 (you: 30)", false), d.Requirements);
        Assert.Contains(new RequirementLine("Agility 30 (you: 20)", false), d.Requirements);
        Assert.Contains(d.Requirements, r => r.Text.StartsWith("Profession:", StringComparison.Ordinal) && r.Met);
        Assert.NotNull(d.Weapon);
        Assert.Contains("uses Rifle", d.Weapon, StringComparison.Ordinal);
    }

    [Fact]
    public void Protocol_items_describe_what_uploading_teaches()
    {
        var (_, eff) = Operative();
        var item = new OwnedItem(Pack.Items["protocol.reflex_boost_1"].Id, 1);
        var d = ItemView.Describe(Pack, item, eff, Profession.Operative);
        Assert.Equal("Protocol: upload to learn Reflex Boost I", d.Kind);
        Assert.Contains(d.Requirements, r => r.Text == "Profession: Medic" && !r.Met);
        Assert.Contains("+20 Agility", d.Effects);
        Assert.Equal([ItemAction.Upload, ItemAction.Discard], ItemView.ActionsFor(Pack, item));
        Assert.Equal([ItemAction.Equip, ItemAction.Discard], ItemView.ActionsFor(Pack, new OwnedItem(Pack.Items["armor.marksman_gloves"].Id, 1)));
    }

    [Theory]
    [InlineData(EquipSlot.ImplantEye, "Implant (eye)")]
    [InlineData(EquipSlot.TwoHand, "Two hands")]
    [InlineData(EquipSlot.Hands, "Hands")]
    public void Slot_names(EquipSlot slot, string name) => Assert.Equal(name, ItemView.SlotName(slot));

    [Fact]
    public void The_protocol_bar_numbers_known_protocols_and_greys_unaffordable_ones()
    {
        var re = Pack.Protocols["buff.rifle_enhancement_1"];
        var protocols = new ClientProtocols([re.Id], [], 0, 22);
        var button = Assert.Single(ProtocolBarView.Buttons(Pack, protocols, energy: re.EnergyCost));
        Assert.Equal((1, true), (button.Hotkey, button.Affordable));
        Assert.False(ProtocolBarView.Buttons(Pack, protocols, energy: re.EnergyCost - 1)[0].Affordable);
        Assert.Empty(ProtocolBarView.Buttons(Pack, null, 100));
    }

    [Fact]
    public void Buff_timers_count_down_from_the_last_report()
    {
        var rb = Pack.Protocols["buff.reflex_boost_1"];
        var protocols = new ClientProtocols([], [(rb.Id, 65_000)], 5, 22);
        var chip = Assert.Single(ProtocolBarView.Buffs(Pack, protocols, elapsedSinceReportMs: 5_000));
        Assert.Equal(("Reflex Boost I", 60_000, "1:00"), (chip.Name, chip.RemainingMs, chip.Countdown));
        Assert.Equal(0, ProtocolBarView.Buffs(Pack, protocols, 999_999)[0].RemainingMs);
    }
}
