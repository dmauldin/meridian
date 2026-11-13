using Meridian.Core.Characters;
using Meridian.Core.Stats;
using Meridian.Server.Zone;
using Meridian.Wire;

namespace Meridian.Server.Tests;

public class ZoneCharacterTests
{
    private static readonly ClientRef A = new(1, 1);
    private static readonly ClientRef B = new(1, 2);

    private static (ZoneSimulation Sim, RecordingOutbox Out) Zone(int mobs = 0)
    {
        var outbox = new RecordingOutbox();
        return (new ZoneSimulation(new ZoneOptions { MobCount = mobs, Seed = 3 }, outbox), outbox);
    }

    private static void Send<T>(ZoneSimulation sim, ClientRef from, in T message)
        where T : struct, IWireMessage<T> => sim.HandleClientPacket(from, Envelope.ToArray(message));

    private static int SheetValue(CharacterSheet sheet, Stat stat) => sheet.Stats!.Single(s => s.Stat == (ushort)stat).BaseValue;

    [Fact]
    public void Joining_creates_the_requested_profession_and_sends_its_sheet()
    {
        var (sim, outbox) = Zone();
        var p = sim.Join(A, 1, "a", Profession.Medic, Origin.Nightborn);
        Assert.Equal(Profession.Medic, p.Character.Profession);
        var sheet = outbox.Last<CharacterSheet>(A);
        Assert.Equal((byte)Profession.Medic, sheet.Profession);
        Assert.Equal((byte)Origin.Nightborn, sheet.Origin);
        Assert.Equal(1, sheet.Level);
        Assert.Equal(60, sheet.UnspentPoints);
        Assert.Equal(25, SheetValue(sheet, Stat.BioWeaving));
        Assert.Equal(StatIndex.All.Count, sheet.Stats!.Length);
    }

    [Fact]
    public void An_unplayable_profession_falls_back_to_the_default()
    {
        var (sim, _) = Zone();
        var p = sim.Join(A, 1, "a", Profession.Sentinel, (Origin)99); // not in content yet
        Assert.Equal((Profession.Operative, Origin.Baseline), (p.Character.Profession, p.Character.Origin));
    }

    [Fact]
    public void Training_over_the_wire_updates_the_sheet()
    {
        var (sim, outbox) = Zone();
        sim.Join(A, 1, "a");
        Send(sim, A, new TrainSkill { Stat = (ushort)Stat.Rifle, Points = 5 });
        sim.Step();
        var sheet = outbox.Last<CharacterSheet>(A);
        Assert.Equal(35, SheetValue(sheet, Stat.Rifle));
        Assert.Equal(55, sheet.UnspentPoints);
        Assert.Empty(outbox.Of<Notice>(A));
    }

    [Fact]
    public void Refused_training_explains_why_and_changes_nothing()
    {
        var (sim, outbox) = Zone();
        sim.Join(A, 1, "a");
        var sheets = outbox.Of<CharacterSheet>(A).Count;
        Send(sim, A, new TrainSkill { Stat = (ushort)Stat.Rifle, Points = 50 });       // above cap
        Send(sim, A, new TrainSkill { Stat = (ushort)Stat.MaxHealth, Points = 1 });    // derived
        Send(sim, A, new TrainSkill { Stat = 9_999, Points = 1 });                     // not a stat
        sim.Step();
        var notices = outbox.Of<Notice>(A).Select(n => n.Text).ToList();
        Assert.Equal(3, notices.Count);
        Assert.Contains("cannot go above 38", notices[0], StringComparison.Ordinal);
        Assert.Equal(sheets, outbox.Of<CharacterSheet>(A).Count); // nothing changed, nothing resent
    }

    [Fact]
    public void The_sheet_is_resent_only_when_something_changed()
    {
        var (sim, outbox) = Zone();
        sim.Join(A, 1, "a");
        for (var i = 0; i < 40; i++)
        {
            sim.Step();
        }
        Assert.Single(outbox.Of<CharacterSheet>(A));
    }

    [Fact]
    public void Everyone_who_fought_a_mob_gets_its_full_experience()
    {
        var (sim, outbox) = Zone(mobs: 1);
        var mob = sim.Mobs[0];
        var a = sim.Join(A, 1, "a");
        var b = sim.Join(B, 2, "b");
        a.Position = mob.Spawn with { X = mob.Spawn.X + 20_000 };
        b.Position = mob.Spawn with { X = mob.Spawn.X - 20_000 };
        foreach (var who in new[] { A, B })
        {
            Send(sim, who, new SetTarget { EntityId = mob.EntityId });
            Send(sim, who, new SetAttack { Enabled = true });
        }
        var ticks = 0;
        while (!mob.Dead && ticks++ < 2_000)
        {
            sim.Step();
        }
        Assert.True(mob.Dead);

        foreach (var (client, player) in new[] { (A, a), (B, b) })
        {
            var xp = Assert.Single(outbox.Of<XpGained>(client));
            Assert.Equal((uint)mob.Template.Xp, xp.Amount);
            Assert.Equal(mob.Template.Xp, player.Build.Xp);
        }
    }

    [Fact]
    public void Levelling_up_grants_points_and_heals_to_the_new_maximum()
    {
        var (sim, outbox) = Zone(mobs: 1);
        var mob = sim.Mobs[0];
        var a = sim.Join(A, 1, "a");
        a.Position = mob.Spawn with { X = mob.Spawn.X + 20_000 };
        a.Build.AddXp(a.Build.XpToNext - 1); // one XP short of level 2
        a.Health = 10;

        Send(sim, A, new SetTarget { EntityId = mob.EntityId });
        Send(sim, A, new SetAttack { Enabled = true });
        mob.Health = 1;
        var ticks = 0;
        while (!mob.Dead && ticks++ < 2_000)
        {
            sim.Step();
        }
        sim.Step();

        var xp = outbox.Last<XpGained>(A);
        Assert.Equal(1, xp.LevelsGained);
        Assert.Equal(2, xp.Level);
        Assert.Equal(a.MaxHealth, a.Health);
        Assert.Equal(116, a.MaxHealth);
        Assert.Equal(2, outbox.Last<CharacterSheet>(A).Level);
    }
}
