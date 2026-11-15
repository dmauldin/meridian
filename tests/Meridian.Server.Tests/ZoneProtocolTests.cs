using Meridian.Content;
using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Stats;
using Meridian.Core.World;
using Meridian.Persistence;
using Meridian.Server.Zone;
using Meridian.Wire;

namespace Meridian.Server.Tests;

public class ZoneProtocolTests
{
    private static readonly ClientRef MedicClient = new(1, 1);
    private static readonly ClientRef OperativeClient = new(1, 2);
    private static readonly ContentPack Pack = ContentPacks.Embedded;
    private static readonly ProtocolTemplate ReflexBoost = Pack.Protocols["buff.reflex_boost_1"];
    private static readonly ProtocolTemplate RifleEnh = Pack.Protocols["buff.rifle_enhancement_1"];

    private static (ZoneSimulation Sim, RecordingOutbox Out, Player Medic, Player Operative) TwoPlayers(int apartMm = 10_000)
    {
        var outbox = new RecordingOutbox();
        var sim = new ZoneSimulation(new ZoneOptions { MobCount = 0, Seed = 3 }, outbox);
        var medic = sim.Join(MedicClient, Record(1, "Mira", Profession.Medic))!;
        var op = sim.Join(OperativeClient, Record(2, "Oren", Profession.Operative))!;
        medic.Position = new Position(0, 0);
        op.Position = new Position(apartMm, 0);
        sim.Step();
        return (sim, outbox, medic, op);
    }

    private static CharacterRecord Record(ulong id, string name, Profession profession) =>
        CharacterRecords.New(Pack, id, name, profession, Origin.Baseline) with { Id = id, Version = 1 };

    private static void Cast(ZoneSimulation sim, ClientRef from, ProtocolTemplate protocol, Player? on)
    {
        sim.HandleClientPacket(from, Envelope.ToArray(new CastProtocol { ProtocolId = (uint)protocol.Id, TargetId = on?.EntityId ?? 0 }));
        sim.Step();
    }

    [Fact]
    public void Joining_sends_known_protocols_and_energy()
    {
        var (_, outbox, medic, _) = TwoPlayers();
        Assert.Equal([(uint)ReflexBoost.Id], outbox.Last<ProtocolState>(MedicClient).Known!);
        var status = outbox.Last<SelfStatus>(MedicClient);
        Assert.Equal(medic.Lattice.MaxEnergy, status.MaxEnergy);
        Assert.True(status.Energy > 0);
    }

    [Fact]
    public void A_medic_buffs_another_player()
    {
        var (sim, outbox, medic, op) = TwoPlayers();
        var energy = medic.Lattice.Energy;
        Cast(sim, MedicClient, ReflexBoost, op);
        Assert.Equal(40, op.Character.Get(Stat.Agility));
        Assert.True(medic.Lattice.Energy < energy);
        Assert.Equal("Reflex Boost I lands on Oren.", outbox.Last<Notice>(MedicClient).Text);
        Assert.Equal("Mira cast Reflex Boost I on you.", outbox.Last<Notice>(OperativeClient).Text);
        var running = Assert.Single(outbox.Last<ProtocolState>(OperativeClient).Running!);
        Assert.Equal((uint)ReflexBoost.Id, running.ProtocolId);
    }

    [Fact]
    public void Refusals_explain_themselves()
    {
        var (sim, outbox, medic, op) = TwoPlayers(apartMm: 31_000);
        Cast(sim, MedicClient, ReflexBoost, op);
        Assert.Equal("Oren is out of range (30 m).", outbox.Last<Notice>(MedicClient).Text);
        Cast(sim, MedicClient, RifleEnh, op);
        Assert.Equal("You do not know Rifle Enhancement I.", outbox.Last<Notice>(MedicClient).Text);
        sim.HandleClientPacket(MedicClient, Envelope.ToArray(new CastProtocol { ProtocolId = 999_999, TargetId = 0 }));
        Assert.Equal("Unknown Protocol.", outbox.Last<Notice>(MedicClient).Text);
        sim.HandleClientPacket(MedicClient, Envelope.ToArray(new CastProtocol { ProtocolId = (uint)ReflexBoost.Id, TargetId = 99_999 }));
        Assert.Equal("Reflex Boost I can only be cast on players.", outbox.Last<Notice>(MedicClient).Text);
        Assert.Equal(medic.Lattice.MaxEnergy, medic.Lattice.Energy); // refusals are free
    }

    /// <summary>The M2 gate, in the zone: twink a rifle with another player's buff, lose it when the buff ends.</summary>
    [Fact]
    public void Twinking_a_rifle_with_another_players_buff_and_losing_it_when_the_buff_wears_off()
    {
        var (sim, outbox, _, op) = TwoPlayers();
        var kestrel = op.Outfit.Add(new OwnedItem(Pack.Items["rifle.kestrel_mk2"].Id, 1));
        Cast(sim, OperativeClient, RifleEnh, null);                      // own buff: Rifle 45
        sim.HandleClientPacket(OperativeClient, Envelope.ToArray(new EquipItem { Position = (byte)kestrel }));
        sim.Step();
        Assert.Contains("Agility 20/30", outbox.Last<Notice>(OperativeClient).Text, StringComparison.Ordinal);

        Cast(sim, MedicClient, ReflexBoost, op);                         // the Medic's buff: Agility 40
        sim.HandleClientPacket(OperativeClient, Envelope.ToArray(new EquipItem { Position = (byte)kestrel }));
        sim.Step();
        Assert.Equal("Kestrel Mk2", op.Weapon.Name);

        // Make the Medic's buff about to run out, then let it.
        op.Lattice.Cancel(ReflexBoost.Id);
        op.Lattice.Restore(ReflexBoost.Id, remainingMs: 200, sim.Tick);
        sim.Step();
        for (var i = 0; i < 10; i++)
        {
            sim.Step();
        }
        var notices = outbox.Of<Notice>(OperativeClient).Select(n => n.Text).ToList();
        Assert.Contains("Reflex Boost I has worn off.", notices);
        Assert.Contains("Kestrel Mk2 (QL 1) is inactive: you no longer meet its requirements.", notices);
        Assert.Equal("Fists", op.Weapon.Name);
        Assert.Contains(outbox.Last<InventoryState>(OperativeClient).Worn!, w => w.Slot == (byte)EquipSlot.TwoHand && !w.Active);
    }

    [Fact]
    public void Uploading_consumes_the_item_only_when_learned()
    {
        var (sim, outbox, _, op) = TwoPlayers();
        var wrong = op.Outfit.Add(new OwnedItem(Pack.Items["protocol.reflex_boost_1"].Id, 1));
        sim.HandleClientPacket(OperativeClient, Envelope.ToArray(new UploadProtocol { Position = (byte)wrong }));
        var refusal = outbox.Last<Notice>(OperativeClient).Text!;
        Assert.StartsWith("You cannot upload Reflex Boost I:", refusal, StringComparison.Ordinal);
        Assert.Contains("BioWeaving 0/25", refusal, StringComparison.Ordinal);
        Assert.Contains("profession must be Medic", refusal, StringComparison.Ordinal);
        Assert.NotNull(op.Outfit.InventoryAt(wrong));

        op.Character.Stats.SetBase(Stat.KineticFlux, 120);
        var right = op.Outfit.Add(new OwnedItem(Pack.Items["protocol.rifle_enhancement_2"].Id, 1));
        sim.HandleClientPacket(OperativeClient, Envelope.ToArray(new UploadProtocol { Position = (byte)right }));
        sim.Step();
        Assert.Equal("Uploaded Rifle Enhancement II.", outbox.Last<Notice>(OperativeClient).Text);
        Assert.Null(op.Outfit.InventoryAt(right));
        Assert.Contains((uint)Pack.Protocols["buff.rifle_enhancement_2"].Id, outbox.Last<ProtocolState>(OperativeClient).Known!);
    }

    [Fact]
    public void Known_protocols_and_buffs_survive_a_save_with_their_remaining_time()
    {
        var (sim, _, _, op) = TwoPlayers();
        Cast(sim, MedicClient, ReflexBoost, op);
        var kestrel = op.Outfit.Add(new OwnedItem(Pack.Items["rifle.kestrel_mk2"].Id, 1));
        Cast(sim, OperativeClient, RifleEnh, null);
        op.Outfit.Equip(kestrel);
        var saved = sim.Snapshot(op);
        Assert.Equal(2, saved.Buffs.Count);
        Assert.All(saved.Buffs, b => Assert.InRange(b.RemainingMs, 590_000, 600_000));

        var (fresh, _, _, _) = TwoPlayers();
        fresh.Leave(OperativeClient);
        var back = fresh.Join(OperativeClient, saved)!;
        Assert.Equal(40, back.Character.Get(Stat.Agility));
        Assert.True(back.Outfit.IsActive(EquipSlot.TwoHand)); // buffs restored before items: the twink holds
    }

    [Fact]
    public void Characters_saved_before_protocols_existed_learn_their_starting_set()
    {
        var outbox = new RecordingOutbox();
        var sim = new ZoneSimulation(new ZoneOptions { MobCount = 0, Seed = 3 }, outbox);
        var p = sim.Join(MedicClient, Record(5, "Old", Profession.Medic) with { KnownProtocols = [] })!;
        Assert.True(p.Lattice.Knows(ReflexBoost.Id));
    }

    [Fact]
    public void Cancelling_your_own_buff_ends_it()
    {
        var (sim, outbox, _, op) = TwoPlayers();
        Cast(sim, OperativeClient, RifleEnh, null);
        sim.HandleClientPacket(OperativeClient, Envelope.ToArray(new CancelProtocol { ProtocolId = (uint)RifleEnh.Id }));
        Assert.Equal("Rifle Enhancement I ended.", outbox.Last<Notice>(OperativeClient).Text);
        Assert.Empty(op.Lattice.Running.Running);
        Assert.Equal(30, op.Character.Get(Stat.Rifle));
    }

    [Fact]
    public void Dying_ends_every_buff()
    {
        var outbox = new RecordingOutbox();
        var sim = new ZoneSimulation(new ZoneOptions { MobCount = 1, Seed = 3 }, outbox);
        var mob = sim.Mobs[0];
        var op = sim.Join(OperativeClient, Record(2, "Oren", Profession.Operative))!;
        op.Position = mob.Position with { X = mob.Position.X + 2_000 };
        Cast(sim, OperativeClient, RifleEnh, null);
        op.Health = 1; // one bite from death
        for (var i = 0; i < 1_000 && !op.Dead; i++)
        {
            sim.Step();
        }
        Assert.True(op.Dead);
        Assert.Empty(op.Lattice.Running.Running);
        Assert.Equal(30, op.Character.Get(Stat.Rifle));
        Assert.Contains(outbox.Of<Notice>(OperativeClient), n => n.Text == "Your Protocols ended when you died.");
    }
}
