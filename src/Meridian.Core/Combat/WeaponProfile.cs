using Meridian.Core.Stats;

namespace Meridian.Core.Combat;

/// <summary>
/// What an attack uses: which skill rolls to hit, which skill the defender evades with, which
/// initiative speeds it up, and its damage band. Weapons and natural attacks (claws, bites) alike.
/// </summary>
/// <param name="Name">Display name.</param>
/// <param name="AttackSkill">Skill rolled to hit, e.g. Rifle.</param>
/// <param name="InitiativeSkill">Skill that shortens the attack time, e.g. RangedInit.</param>
/// <param name="DefenceSkill">Defender's evade skill, e.g. EvadeRanged.</param>
/// <param name="DamageType">Selects the defender's armor class.</param>
/// <param name="MinDamage">Low end of the damage roll.</param>
/// <param name="MaxDamage">High end of the damage roll.</param>
/// <param name="BaseAttackTimeMs">Time between attacks before initiative.</param>
/// <param name="RangeMm">Maximum distance to the target.</param>
public sealed record WeaponProfile(
    string Name,
    Stat AttackSkill,
    Stat InitiativeSkill,
    Stat DefenceSkill,
    DamageType DamageType,
    int MinDamage,
    int MaxDamage,
    int BaseAttackTimeMs,
    int RangeMm)
{
    /// <summary>The armor-class stat that mitigates this weapon's damage.</summary>
    public Stat ArmorStat => Formulas.ArmorFor(DamageType);
}
