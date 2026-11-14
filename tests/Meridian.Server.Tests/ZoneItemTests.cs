using Meridian.Content;
using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Stats;
using Meridian.Persistence;
using Meridian.Server.Zone;
using Meridian.Wire;

namespace Meridian.Server.Tests;

public class ZoneItemTests
{
    private static readonly ClientRef A = new(1, 1);
    private static readonly ContentPack Pack = ContentPacks.Embedded;
    private static readonly int Gloves = Pack.Items["armor.marksman_gloves"].Id;
    private static readonly int Kestrel = Pack.Items["rifle.kestrel_mk2"].Id;

    private static (ZoneSimulation Sim, RecordingOutbox Out) Zone(bool dev = false, ContentPack? content = null, int mobs = 0)
    {
        var outbox = new RecordingOutbox();
        return (new ZoneSimulation(new ZoneOptions { MobCount = mobs, Seed = 3, DevCommands = dev }, outbox, content), outbox);
    }

    private static void Send<T>(ZoneSimulation sim, in T message)
        where T : struct, IWireMessage<T> => sim.HandleClientPacket(A, Envelope.ToArray(message));

    /// <summary>An Operative held at Rifle 35 and Agility 30 (QL 1 Kestrel needs Rifle 40).</summary>
    private static Player Operative(ZoneSimulation sim, CharacterRecord? record = null)
    {
        var p = sim.Join(A, record ?? CharacterRecords.New(Pack, 1, "Rook", Profession.Operative, Origin.Baseline) with { Id = 7, Version = 1 })!;
        p.Character.Stats.SetBase(Stat.Rifle, 35);
        p.Character.Stats.SetBase(Stat.Agility, 30);
        sim.Step();
        return p;
    }

    [Fact]
    public void A_new_character_wears_its_kit_and_fights_with_it()
    {
        var (sim, outbox) = Zone();
        var p = Operative(sim);
        var inv = outbox.Last<InventoryState>(A);
        Assert.Equal(30, inv.Size);
        var worn = Assert.Single(inv.Worn!);
        Assert.Equal((uint)Pack.Items["rifle.combine_carbine"].Id, worn.TemplateId);
        Assert.True(worn.Active);
        Assert.Equal("Combine Issue Carbine", p.Weapon.Name);
    }

    [Fact]
    public void Characters_saved_before_items_existed_get_their_kit()
    {
        var (sim, _) = Zone();
        var record = CharacterRecords.New(Pack, 1, "Old", Profession.Warden, Origin.Baseline) with { Id = 9, Items = [] };
        var p = sim.Join(A, record)!;
        Assert.Equal("Shock Maul", p.Weapon.Name);
    }

    [Fact]
    public void Twinking_over_the_wire_and_losing_the_twink()
    {
        var (sim, outbox) = Zone();
        var p = Operative(sim);
        var gloves = p.Outfit.Add(new OwnedItem(Gloves, 1));
        var kestrel = p.Outfit.Add(new OwnedItem(Kestrel, 1));

        Send(sim, new EquipItem { Position = (byte)kestrel });
        sim.Step();
        Assert.Contains("Rifle 35/40", outbox.Last<Notice>(A).Text, StringComparison.Ordinal);

        Send(sim, new EquipItem { Position = (byte)gloves });   // +5 Rifle
        Send(sim, new EquipItem { Position = (byte)kestrel });  // now 40/40; the carbine comes off
        sim.Step();
        Assert.Equal("Kestrel Mk2", p.Weapon.Name);
        var inv = outbox.Last<InventoryState>(A);
        Assert.Contains(inv.Worn!, w => w.TemplateId == (uint)Kestrel && w.Active);
        Assert.Contains(inv.Carried!, c => c.TemplateId == (uint)Pack.Items["rifle.combine_carbine"].Id);

        Send(sim, new UnequipItem { Slot = (byte)EquipSlot.Hands });
        sim.Step();
        Assert.Equal("Fists", p.Weapon.Name);                    // worn but inactive: bare hands
        Assert.Contains(outbox.Of<Notice>(A), n => n.Text!.Contains("Kestrel Mk2 (QL 1) is inactive", StringComparison.Ordinal));
        Assert.Contains(outbox.Last<InventoryState>(A).Worn!, w => w.TemplateId == (uint)Kestrel && !w.Active);
    }

    /// <summary>The sequence the PR's "try it" section gives, on an untouched new Operative.</summary>
    [Fact]
    public void A_fresh_operative_twinks_a_ql9_carbine_with_gloves()
    {
        var (sim, outbox) = Zone(dev: true);
        var p = sim.Join(A, CharacterRecords.New(Pack, 1, "Rook", Profession.Operative, Origin.Baseline) with { Id = 7, Version = 1 })!;
        Assert.Equal(30, p.Character.Get(Stat.Rifle));
        foreach (var line in new[] { "/give rifle.combine_carbine 9", "/give armor.marksman_gloves 1" })
        {
            sim.HandleClientPacket(A, Envelope.ToArray(new ChatSay { Text = line }));
        }
        var carbine = p.Outfit.Inventory.Single(i => i.Item.Ql == 9).Position;
        var gloves = p.Outfit.Inventory.Single(i => i.Item.TemplateId == Gloves).Position;

        Send(sim, new EquipItem { Position = (byte)carbine });
        sim.Step();
        Assert.Contains("Rifle 30/35", outbox.Last<Notice>(A).Text, StringComparison.Ordinal);

        Send(sim, new EquipItem { Position = (byte)gloves });
        Send(sim, new EquipItem { Position = (byte)carbine });
        sim.Step();
        Assert.Contains(p.Outfit.Worn, w => w.Item.Ql == 9 && w.Active);

        Send(sim, new UnequipItem { Slot = (byte)EquipSlot.Hands });
        sim.Step();
        Assert.Equal("Fists", p.Weapon.Name);
    }

    [Fact]
    public void Items_and_their_state_survive_a_save_and_reload()
    {
        var (sim, _) = Zone();
        var p = Operative(sim);
        var gloves = p.Outfit.Add(new OwnedItem(Gloves, 1));
        var kestrel = p.Outfit.Add(new OwnedItem(Kestrel, 1));
        Assert.Equal(ItemResult.Done, p.Outfit.Equip(gloves));
        Assert.Equal(ItemResult.Done, p.Outfit.Equip(kestrel));
        sim.Step();
        var saved = sim.Snapshot(p);
        sim.Leave(A);

        // Without the gloves the Kestrel comes back worn but inactive.
        var noGloves = saved with { Items = [.. saved.Items.Where(i => i.TemplateId != Gloves)] };
        var (sim2, _) = Zone();
        var back = Operative(sim2, noGloves);
        Assert.True(back.Outfit.IsWorn(EquipSlot.TwoHand));
        Assert.False(back.Outfit.IsActive(EquipSlot.TwoHand));
        Assert.Equal(saved.Items.Count(i => i.TemplateId != Gloves), CharacterRecords.ItemsOf(back.Outfit).Count);
    }

    [Fact]
    public void Changing_items_triggers_a_checkpoint()
    {
        var outbox = new RecordingOutbox();
        var sim = new ZoneSimulation(new ZoneOptions { MobCount = 0, Seed = 3, CheckpointEveryTicks = 1 }, outbox);
        var p = sim.Join(A, CharacterRecords.New(Pack, 1, "Rook", Profession.Operative, Origin.Baseline) with { Id = 7, Version = 1 })!;
        sim.Step();
        sim.DrainCheckpoints();
        p.Outfit.Add(new OwnedItem(Gloves, 1));
        sim.Step();
        Assert.Contains(Assert.Single(sim.DrainCheckpoints()).Items, i => i.TemplateId == Gloves);
    }

    [Fact]
    public void Bad_positions_and_slots_are_refused_politely()
    {
        var (sim, outbox) = Zone();
        Operative(sim);
        Send(sim, new EquipItem { Position = 250 });
        Send(sim, new UnequipItem { Slot = 99 });
        Send(sim, new DiscardItem { Position = 250 });
        sim.Step();
        Assert.Equal(["There is nothing there.", "Nothing is worn there."], outbox.Of<Notice>(A).Select(n => n.Text));
    }

    [Fact]
    public void Discarding_removes_the_item()
    {
        var (sim, outbox) = Zone();
        var p = Operative(sim);
        var at = p.Outfit.Add(new OwnedItem(Gloves, 4));
        Send(sim, new DiscardItem { Position = (byte)at });
        sim.Step();
        Assert.Null(p.Outfit.InventoryAt(at));
        Assert.Equal("Discarded Marksman Gloves (QL 4).", outbox.Last<Notice>(A).Text);
    }

    [Fact]
    public void Give_is_a_development_command_and_commands_are_never_broadcast()
    {
        var (off, offOut) = Zone(dev: false);
        Operative(off);
        off.HandleClientPacket(A, Envelope.ToArray(new ChatSay { Text = "/give rifle.kestrel_mk2 5" }));
        Assert.Equal("Unknown command /give.", offOut.Last<Notice>(A).Text);
        Assert.Empty(offOut.Of<ChatMessage>(A));

        var (on, onOut) = Zone(dev: true);
        var p = Operative(on);
        on.HandleClientPacket(A, Envelope.ToArray(new ChatSay { Text = "/give rifle.kestrel_mk2 5" }));
        on.HandleClientPacket(A, Envelope.ToArray(new ChatSay { Text = "/give nothing 5" }));
        Assert.Contains(p.Outfit.Inventory, i => i.Item == new OwnedItem(Kestrel, 5));
        Assert.Equal(["Given Kestrel Mk2 (QL 5).", "Usage: /give <item key> <ql>"], onOut.Of<Notice>(A).Select(n => n.Text));
        Assert.Empty(onOut.Of<ChatMessage>(A));
    }

    [Fact]
    public void Loot_goes_to_the_top_threat_player_at_a_ql_near_the_mobs_level()
    {
        var dto = ContentPacks.ToDto(Pack);
        var skitter = dto.Mobs.Single(m => m.Key == "mob.glasswind_skitter");
        dto.Mobs[dto.Mobs.IndexOf(skitter)] = skitter with { Loot = [new LootDto("armor.marksman_gloves", 10_000, 2)] };
        var content = ContentPacks.ToModel(dto, 1);

        var (sim, outbox) = Zone(content: content, mobs: 1);
        var mob = sim.Mobs[0];
        var p = sim.Join(A, 1, "hunter");
        p.Position = mob.Spawn with { X = mob.Spawn.X + 15_000 };
        Send(sim, new SetTarget { EntityId = mob.EntityId });
        Send(sim, new SetAttack { Enabled = true });
        for (var i = 0; i < 2_000 && !mob.Dead; i++)
        {
            sim.Step();
        }
        Assert.True(mob.Dead);
        var found = Assert.Single(p.Outfit.Inventory);
        Assert.Equal(Gloves, found.Item.TemplateId);
        Assert.InRange(found.Item.Ql, mob.Template.Level - 2, mob.Template.Level + 2);
        Assert.Contains(outbox.Of<Notice>(A), n => n.Text!.StartsWith("You found Marksman Gloves", StringComparison.Ordinal));
    }
}
