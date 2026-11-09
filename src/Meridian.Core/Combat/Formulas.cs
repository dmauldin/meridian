using Meridian.Core.Randomness;

namespace Meridian.Core.Combat;

/// <summary>Damage types. Each maps to one armor-class stat.</summary>
public enum DamageType : byte
{
    Projectile,
    Melee,
    Energy,
    Chemical,
    Radiation,
    Cold,
    Fire,
    Poison,
}

/// <summary>The result of one resolved attack.</summary>
public readonly record struct AttackOutcome(bool Hit, bool Critical, int Damage);

/// <summary>
/// Combat math. All integer, all deterministic given the same <see cref="DeterministicRandom"/>.
/// First-pass tuning constants live here; changing one should change a golden test.
/// </summary>
public static class Formulas
{
    /// <summary>Floor and ceiling on hit chance, in basis points.</summary>
    public const int MinHitChance = 500, MaxHitChance = 9_500;

    /// <summary>Armor class at which incoming damage is halved.</summary>
    public const int ArmorHalvingPoint = 1_500;

    /// <summary>Critical hits deal this many percent of normal damage.</summary>
    public const int CriticalPercent = 200;

    /// <summary>
    /// Hit chance in basis points from attack skill vs. defence skill. Ratio-based so it behaves
    /// the same at level 1 (skills ~10) and level 200 (skills ~1500): equal skills give 66.67%,
    /// attack double the defence gives 80%.
    /// </summary>
    public static int HitChance(int attackSkill, int defenceSkill)
    {
        long a = Math.Max(0, attackSkill) + 1;
        long d = Math.Max(0, defenceSkill) + 1;
        var bp = 10_000 * a * 2 / ((2 * a) + d);
        return (int)Math.Clamp(bp, MinHitChance, MaxHitChance);
    }

    /// <summary>Damage left after armor: halved at <see cref="ArmorHalvingPoint"/>, never below 1.</summary>
    public static int Mitigate(int damage, int armorClass)
    {
        if (damage <= 0)
        {
            return 0;
        }
        long ac = Math.Max(0, armorClass);
        var after = damage * (long)ArmorHalvingPoint / (ArmorHalvingPoint + ac);
        return (int)Math.Max(1, after);
    }

    /// <summary>
    /// Effective attack or cast time in milliseconds. Every 1 point of initiative removes 2 ms,
    /// down to half the base time.
    /// </summary>
    public static int ActionTimeMs(int baseTimeMs, int initiative)
    {
        var reduced = baseTimeMs - (2L * Math.Max(0, initiative));
        return (int)Math.Max(baseTimeMs / 2, reduced);
    }

    /// <summary>
    /// Resolves one weapon attack: hit roll, damage roll, critical roll, then mitigation.
    /// The roll order is fixed (hit, damage, crit) so replays stay in sync.
    /// </summary>
    public static AttackOutcome ResolveAttack(
        DeterministicRandom rng,
        int attackSkill,
        int defenceSkill,
        int minDamage,
        int maxDamage,
        int addDamage,
        int critChanceBp,
        int armorClass)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (!rng.Chance(HitChance(attackSkill, defenceSkill)))
        {
            return new AttackOutcome(false, false, 0);
        }
        var raw = rng.NextInt(minDamage, maxDamage) + addDamage;
        var crit = rng.Chance(critChanceBp);
        if (crit)
        {
            raw = raw * CriticalPercent / 100;
        }
        return new AttackOutcome(true, crit, Mitigate(raw, armorClass));
    }
}
