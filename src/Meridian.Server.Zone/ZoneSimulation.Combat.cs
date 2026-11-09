using Meridian.Core.Characters;
using Meridian.Core.Combat;
using Meridian.Core.Randomness;
using Meridian.Core.World;
using Meridian.Wire;

namespace Meridian.Server.Zone;

/// <summary>Mob AI, auto-attack combat, death and respawn.</summary>
public sealed partial class ZoneSimulation
{
    /// <summary>Mobs close to within this fraction of their attack range before stopping.</summary>
    private const int ApproachPercent = 80;

    private const int WanderRadiusMm = 8_000;

    private void SpawnMobs()
    {
        // A separate stream for layout, so adding a mob never shifts combat rolls. Integer
        // rejection sampling (not sin/cos) keeps placement bit-identical on every platform.
        var layout = new DeterministicRandom(_options.Seed ^ 0x5350415745UL);
        long inner2 = (long)_options.MobRingInnerMm * _options.MobRingInnerMm;
        long outer2 = (long)_options.MobRingOuterMm * _options.MobRingOuterMm;
        var origin = new Position(0, 0);
        for (var i = 0; i < _options.MobCount; i++)
        {
            Position spawn;
            do
            {
                spawn = new Position(
                    layout.NextInt(-_options.MobRingOuterMm, _options.MobRingOuterMm),
                    layout.NextInt(-_options.MobRingOuterMm, _options.MobRingOuterMm));
            }
            while (spawn.DistanceSquared(origin) < inner2 || spawn.DistanceSquared(origin) > outer2);

            var template = StarterContent.GlasswindSkitter;
            var character = new Character(Profession.Warden, Origin.Baseline);
            template.ApplyStats(character.Stats);
            var mob = new Mob(_nextEntityId++, template, _options.Bounds.Clamp(spawn), character);
            _mobs.Add(mob);
            Add(mob);
        }
    }

    // ------------------------------------------------------------------ respawn

    private void RespawnPlayers()
    {
        foreach (var p in _players)
        {
            if (!p.Dead || Tick < p.RespawnAtTick)
            {
                continue;
            }
            p.Dead = false;
            p.Health = p.MaxHealth;
            p.Position = SpawnPoint(p.Client); // the reclamation bay
            p.DirX = p.DirZ = 0;
            Send(p, new Respawned { X = p.Position.X, Z = p.Position.Z });
        }
    }

    // ------------------------------------------------------------------ mob AI

    private void StepMobs()
    {
        foreach (var mob in _mobs)
        {
            switch (mob.State)
            {
                case MobState.Dead:
                    if (Tick >= mob.RespawnAtTick)
                    {
                        Reset(mob, MobState.Idle);
                        mob.Position = mob.Spawn;
                    }
                    break;
                case MobState.Returning:
                    mob.Attacking = false;
                    if (Steer(mob, mob.Spawn, 1_000))
                    {
                        Reset(mob, MobState.Idle);
                    }
                    break;
                default:
                    ThinkAlive(mob);
                    break;
            }
        }
    }

    private void ThinkAlive(Mob mob)
    {
        var target = PickTarget(mob);
        if (target is null)
        {
            mob.TargetId = 0;
            mob.Attacking = false;
            if (mob.State == MobState.Chasing)
            {
                mob.State = MobState.Returning; // lost everyone: go home and heal
                return;
            }
            Wander(mob);
            return;
        }

        if (mob.Position.DistanceSquared(mob.Spawn) > (long)mob.Template.LeashMm * mob.Template.LeashMm)
        {
            mob.State = MobState.Returning;
            mob.Threat.Clear();
            mob.TargetId = 0;
            mob.Attacking = false;
            mob.DirX = mob.DirZ = 0;
            return;
        }

        mob.State = MobState.Chasing;
        mob.TargetId = target.EntityId;
        mob.Attacking = true;
        Steer(mob, target.Position, mob.Weapon.RangeMm * ApproachPercent / 100);
    }

    /// <summary>Highest threat among living attackers, else the nearest player in aggro range.</summary>
    private Entity? PickTarget(Mob mob)
    {
        Entity? best = null;
        long bestThreat = -1;
        foreach (var (id, threat) in mob.Threat)
        {
            if (Find(id) is { Dead: false } e && (threat > bestThreat || (threat == bestThreat && e.EntityId < best!.EntityId)))
            {
                best = e;
                bestThreat = threat;
            }
        }
        if (best is not null || mob.Template.AggroRadiusMm == 0)
        {
            return best;
        }

        var r2 = (long)mob.Template.AggroRadiusMm * mob.Template.AggroRadiusMm;
        long nearest = long.MaxValue;
        foreach (var p in _players)
        {
            var d = p.Position.DistanceSquared(mob.Position);
            if (!p.Dead && d <= r2 && d < nearest)
            {
                best = p;
                nearest = d;
            }
        }
        if (best is not null)
        {
            mob.Threat[best.EntityId] = 0;
        }
        return best;
    }

    private void Wander(Mob mob)
    {
        mob.State = MobState.Idle;
        if (Tick >= mob.NextWanderTick)
        {
            mob.WanderTarget = new Position(
                mob.Spawn.X + _rng.NextInt(-WanderRadiusMm, WanderRadiusMm),
                mob.Spawn.Z + _rng.NextInt(-WanderRadiusMm, WanderRadiusMm));
            mob.NextWanderTick = Tick + (uint)_rng.NextInt(60, 160);
        }
        Steer(mob, mob.WanderTarget, 500);
    }

    /// <summary>Points the mob at <paramref name="goal"/>; stops within <paramref name="stopWithinMm"/>. True when arrived.</summary>
    private static bool Steer(Mob mob, Position goal, int stopWithinMm)
    {
        if (mob.Position.DistanceSquared(goal) <= (long)stopWithinMm * stopWithinMm)
        {
            mob.DirX = mob.DirZ = 0;
            return true;
        }
        (mob.DirX, mob.DirZ) = Movement.DirectionTowards(mob.Position, goal);
        return false;
    }

    private static void Reset(Mob mob, MobState state)
    {
        mob.State = state;
        mob.Dead = false;
        mob.Health = mob.MaxHealth;
        mob.Threat.Clear();
        mob.TargetId = 0;
        mob.Attacking = false;
        mob.AttackCooldownMs = 0;
        mob.DirX = mob.DirZ = 0;
    }

    // ------------------------------------------------------------------ combat

    private void StepCombat()
    {
        var dt = _options.TickMs;
        foreach (var attacker in _entities)
        {
            if (attacker.AttackCooldownMs > 0)
            {
                attacker.AttackCooldownMs = Math.Max(0, attacker.AttackCooldownMs - dt);
            }
            if (!attacker.Attacking || attacker.Dead)
            {
                continue;
            }
            if (Find(attacker.TargetId) is not { Dead: false } target)
            {
                attacker.Attacking = false;
                continue;
            }
            var range = attacker.Weapon.RangeMm;
            if (attacker.AttackCooldownMs > 0 || attacker.Position.DistanceSquared(target.Position) > (long)range * range)
            {
                continue; // recharging, or out of range: keep auto-attack on, swing when possible
            }
            Swing(attacker, target);
        }
    }

    private void Swing(Entity attacker, Entity target)
    {
        attacker.AttackCooldownMs = Formulas.AttackTimeMs(attacker.Weapon, attacker.Character);
        var outcome = Formulas.ResolveAttack(_rng, attacker.Weapon, attacker.Character, target.Character);
        attacker.LastCombatTick = Tick;
        target.LastCombatTick = Tick;

        if (target is Mob mob)
        {
            // Misses still register, so an attacker who never lands a hit still draws aggro.
            mob.Threat[attacker.EntityId] = mob.Threat.GetValueOrDefault(attacker.EntityId) + Math.Max(1, outcome.Damage);
        }

        Broadcast(target.Position, _options.InterestRadiusMm, new CombatEvent
        {
            AttackerId = attacker.EntityId,
            TargetId = target.EntityId,
            Outcome = (byte)(!outcome.Hit ? 0 : outcome.Critical ? 2 : 1),
            Damage = (uint)outcome.Damage,
        });

        if (!outcome.Hit)
        {
            return;
        }
        target.Health -= outcome.Damage;
        if (target.Health <= 0)
        {
            Kill(target, attacker);
        }
    }

    private void Kill(Entity victim, Entity killer)
    {
        victim.Health = 0;
        victim.Dead = true;
        victim.Attacking = false;
        victim.DirX = victim.DirZ = 0;
        Broadcast(victim.Position, _options.InterestRadiusMm, new EntityDied { EntityId = victim.EntityId, KillerId = killer.EntityId });

        switch (victim)
        {
            case Mob mob:
                mob.State = MobState.Dead;
                mob.Threat.Clear();
                mob.TargetId = 0;
                mob.RespawnAtTick = Tick + (uint)mob.Template.RespawnTicks;
                break;
            case Player player:
                player.RespawnAtTick = Tick + (uint)_options.PlayerRespawnTicks;
                foreach (var m in _mobs)
                {
                    m.Threat.Remove(player.EntityId);
                }
                break;
        }

        // Everyone fighting the victim stops swinging; they keep it targeted (the corpse).
        foreach (var e in _entities)
        {
            if (e.TargetId == victim.EntityId)
            {
                e.Attacking = false;
            }
        }
    }
}
