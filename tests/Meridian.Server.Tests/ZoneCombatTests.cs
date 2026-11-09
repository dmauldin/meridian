using Meridian.Core.World;
using Meridian.Server.Zone;
using Meridian.Wire;

namespace Meridian.Server.Tests;

public class ZoneCombatTests
{
    private static readonly ClientRef A = new(1, 1);
    private static readonly ClientRef B = new(1, 2);

    /// <summary>One skitter; players placed relative to its spawn by each test.</summary>
    private static (ZoneSimulation Sim, RecordingOutbox Out, Mob Mob) OneMob(ulong seed = 1, int playerRespawnTicks = 100)
    {
        var outbox = new RecordingOutbox();
        var sim = new ZoneSimulation(new ZoneOptions { MobCount = 1, Seed = seed, PlayerRespawnTicks = playerRespawnTicks }, outbox);
        return (sim, outbox, sim.Mobs[0]);
    }

    private static void Send<T>(ZoneSimulation sim, ClientRef from, in T message)
        where T : struct, IWireMessage<T> => sim.HandleClientPacket(from, Envelope.ToArray(message));

    private static Player JoinAt(ZoneSimulation sim, ClientRef client, string name, Position at)
    {
        var p = sim.Join(client, client.ConnectionId, name);
        p.Position = at;
        return p;
    }

    private static void Attack(ZoneSimulation sim, ClientRef who, Entity target)
    {
        Send(sim, who, new SetTarget { EntityId = target.EntityId });
        Send(sim, who, new SetAttack { Enabled = true });
    }

    private static void Steps(ZoneSimulation sim, int n)
    {
        for (var i = 0; i < n; i++)
        {
            sim.Step();
        }
    }

    [Fact]
    public void Attacking_requires_a_living_mob_target()
    {
        var (sim, _, mob) = OneMob();
        var a = JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 20_000 });
        var b = JoinAt(sim, B, "b", mob.Spawn with { X = mob.Spawn.X + 21_000 });

        Send(sim, A, new SetAttack { Enabled = true });
        Assert.False(a.Attacking); // no target

        Send(sim, A, new SetTarget { EntityId = b.EntityId });
        Send(sim, A, new SetAttack { Enabled = true });
        Assert.False(a.Attacking); // players are not attackable in M1

        Send(sim, A, new SetTarget { EntityId = 999_999 });
        Assert.Equal(0u, a.TargetId); // unknown ids clear the target

        Attack(sim, A, mob);
        Assert.True(a.Attacking);

        Send(sim, A, new SetTarget { EntityId = 0 });
        Assert.False(a.Attacking); // clearing the target stops the attack
    }

    [Fact]
    public void Out_of_range_attacks_wait_until_in_range()
    {
        var (sim, outbox, mob) = OneMob();
        var a = JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 30_000 }); // rifle range is 25 m
        Attack(sim, A, mob);
        Steps(sim, 20);
        Assert.Empty(outbox.Of<CombatEvent>(A));
        Assert.True(a.Attacking);

        a.Position = mob.Spawn with { X = mob.Spawn.X + 20_000 };
        sim.Step();
        Assert.Single(outbox.Of<CombatEvent>(A));
    }

    [Fact]
    public void Swings_follow_the_attack_time()
    {
        var (sim, outbox, mob) = OneMob();
        JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 20_000 });
        Attack(sim, A, mob);
        // Carbine 1500 ms, RangedInit 10 → 1480 ms ≈ 30 ticks. Swings at ticks 1, 31, 61.
        Steps(sim, 61);
        var mine = outbox.Of<CombatEvent>(A).Where(e => e.TargetId == mob.EntityId).ToList();
        Assert.Equal(3, mine.Count);
    }

    [Fact]
    public void Being_shot_makes_the_mob_chase_and_bite_back()
    {
        var (sim, outbox, mob) = OneMob();
        var a = JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 20_000 }); // outside aggro radius
        Steps(sim, 5);
        Assert.Equal(MobState.Idle, mob.State);

        Attack(sim, A, mob);
        Steps(sim, 200); // 10 s: plenty to close 20 m at 4 m/s
        Assert.Equal(a.EntityId, mob.TargetId);
        Assert.Contains(outbox.Of<CombatEvent>(A), e => e.AttackerId == mob.EntityId && e.TargetId == a.EntityId);
    }

    [Fact]
    public void Walking_into_aggro_range_starts_a_fight()
    {
        var (sim, _, mob) = OneMob();
        var a = JoinAt(sim, A, "a", mob.Position with { X = mob.Position.X + 5_000 }); // aggro radius 8 m
        sim.Step();
        Assert.Equal(MobState.Chasing, mob.State);
        Assert.Equal(a.EntityId, mob.TargetId);
    }

    [Fact]
    public void Two_players_kill_a_mob_together_and_it_respawns_later()
    {
        var (sim, outbox, mob) = OneMob();
        var a = JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 20_000 });
        var b = JoinAt(sim, B, "b", mob.Spawn with { X = mob.Spawn.X - 20_000 });
        Attack(sim, A, mob);
        Attack(sim, B, mob);

        var ticks = 0;
        while (!mob.Dead && ticks++ < 2_000)
        {
            sim.Step();
        }
        Assert.True(mob.Dead, "mob should die within 100 s");

        var died = Assert.Single(outbox.Of<EntityDied>(A));
        Assert.Equal(mob.EntityId, died.EntityId);
        Assert.Contains(died.KillerId, new[] { a.EntityId, b.EntityId });
        var events = outbox.Of<CombatEvent>(A);
        Assert.Contains(events, e => e.AttackerId == a.EntityId && e.Outcome != 0);
        Assert.Contains(events, e => e.AttackerId == b.EntityId && e.Outcome != 0);
        Assert.False(a.Attacking);
        Assert.Equal(mob.EntityId, a.TargetId); // the corpse stays targeted

        // Attacking a corpse is refused.
        Send(sim, A, new SetAttack { Enabled = true });
        Assert.False(a.Attacking);

        Steps(sim, mob.Template.RespawnTicks);
        Assert.False(mob.Dead);
        Assert.Equal(mob.MaxHealth, mob.Health);
        Assert.Equal(mob.Spawn, mob.Position);
    }

    [Fact]
    public void Kiting_past_the_leash_sends_the_mob_home_healed()
    {
        var (sim, _, mob) = OneMob();
        var a = JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 20_000 });
        Attack(sim, A, mob);
        Steps(sim, 2);
        Assert.Equal(MobState.Chasing, mob.State);
        Assert.True(mob.Threat.ContainsKey(a.EntityId));

        // Drag it far away: put the player 80 m out and let the mob follow past its 40 m leash.
        Send(sim, A, new SetAttack { Enabled = false });
        a.Position = mob.Spawn with { X = mob.Spawn.X + 80_000 };
        var ticks = 0;
        while (mob.State != MobState.Returning && ticks++ < 1_000)
        {
            sim.Step();
        }
        Assert.Equal(MobState.Returning, mob.State);
        Assert.True(mob.Position.DistanceSquared(mob.Spawn) > 40_000L * 40_000, "it should have chased past the leash");
        Assert.Equal(0u, mob.TargetId);
        Assert.False(mob.Attacking);

        ticks = 0;
        while (mob.State != MobState.Idle && ticks++ < 1_000)
        {
            sim.Step();
        }
        Assert.Equal(MobState.Idle, mob.State);
        Assert.Equal(mob.MaxHealth, mob.Health);
        Assert.Empty(mob.Threat);
    }

    [Fact]
    public void A_dead_player_cannot_act_and_respawns_at_full_health()
    {
        var (sim, outbox, mob) = OneMob(playerRespawnTicks: 40);
        var a = JoinAt(sim, A, "a", mob.Position with { X = mob.Position.X + 2_000 });
        a.Health = 1; // one bite from death
        var ticks = 0;
        while (!a.Dead && ticks++ < 1_000)
        {
            sim.Step();
        }
        Assert.True(a.Dead);
        Assert.Contains(outbox.Of<EntityDied>(A), d => d.EntityId == a.EntityId && d.KillerId == mob.EntityId);
        Assert.True(outbox.Last<SelfStatus>(A).Dead);
        Assert.Empty(mob.Threat);

        var where = a.Position;
        Send(sim, A, new MoveIntent { Sequence = 99, DirX = Movement.DirectionScale });
        Attack(sim, A, mob);
        sim.Step();
        Assert.Equal(where, a.Position);
        Assert.False(a.Attacking);

        Steps(sim, 40);
        Assert.False(a.Dead);
        Assert.Equal(a.MaxHealth, a.Health);
        var respawn = outbox.Last<Respawned>(A);
        Assert.Equal(a.Position, new Position(respawn.X, respawn.Z));
        Assert.False(outbox.Last<SelfStatus>(A).Dead);
    }

    [Fact]
    public void Entity_info_is_sent_once_when_something_comes_into_view()
    {
        var (sim, outbox, mob) = OneMob();
        JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 50_000 });
        Steps(sim, 10);
        var infos = outbox.Of<EntityInfo>(A).Where(i => i.EntityId == mob.EntityId).ToList();
        var info = Assert.Single(infos);
        Assert.Equal((byte)EntityKind.Mob, info.Kind);
        Assert.Equal("Glasswind Skitter", info.Name);
        Assert.Equal(mob.Template.Id, info.TemplateId);
        Assert.Equal(3, info.Level);
    }

    [Fact]
    public void Self_status_is_sent_only_on_change()
    {
        var (sim, outbox, mob) = OneMob();
        JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 50_000 });
        Steps(sim, 20);
        Assert.Single(outbox.Of<SelfStatus>(A)); // the one sent on join
        Send(sim, A, new SetTarget { EntityId = mob.EntityId });
        Steps(sim, 5);
        Assert.Equal(2, outbox.Of<SelfStatus>(A).Count);
        Assert.Equal(mob.EntityId, outbox.Last<SelfStatus>(A).TargetId);
    }

    [Fact]
    public void Snapshots_flag_dead_and_in_combat_entities()
    {
        var (sim, outbox, mob) = OneMob();
        JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 20_000 });
        Attack(sim, A, mob);
        Steps(sim, 4);
        var state = outbox.Last<Snapshot>(A).Entities!.Single(e => e.EntityId == mob.EntityId);
        Assert.Equal(2, state.Flags & 2);
        mob.Health = 1;
        while (!mob.Dead)
        {
            sim.Step();
        }
        Steps(sim, 2);
        state = outbox.Last<Snapshot>(A).Entities!.Single(e => e.EntityId == mob.EntityId);
        Assert.Equal(1, state.Flags & 1);
        Assert.Equal(0, state.HealthPercent);
    }

    [Fact]
    public void Same_seed_and_inputs_replay_byte_for_byte()
    {
        static List<byte[]> Run(ulong seed)
        {
            var (sim, outbox, mob) = OneMob(seed);
            JoinAt(sim, A, "a", mob.Spawn with { X = mob.Spawn.X + 20_000 });
            JoinAt(sim, B, "b", mob.Spawn with { Z = mob.Spawn.Z + 15_000 });
            Attack(sim, A, mob);
            Steps(sim, 50);
            Attack(sim, B, mob);
            Steps(sim, 400);
            return outbox.Sent.Select(s => s.Packet).ToList();
        }

        var first = Run(7);
        Assert.Equal(first, Run(7));
        Assert.NotEqual(first, Run(8));
    }

    [Fact]
    public void Mob_layout_is_deterministic_and_inside_the_ring()
    {
        var options = new ZoneOptions { MobCount = 40 };
        var one = new ZoneSimulation(options, new RecordingOutbox()).Mobs.Select(m => m.Spawn).ToList();
        var two = new ZoneSimulation(options, new RecordingOutbox()).Mobs.Select(m => m.Spawn).ToList();
        Assert.Equal(one, two);
        Assert.All(one, p =>
        {
            var d = Movement.Distance(p, new Position(0, 0));
            Assert.InRange(d, options.MobRingInnerMm, options.MobRingOuterMm);
        });
    }
}
