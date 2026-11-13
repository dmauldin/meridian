using Meridian.Core.Stats;

namespace Meridian.Core.Characters;

/// <summary>The design doc's skill groups. Professions price skills by group.</summary>
public enum SkillGroup : byte
{
    /// <summary>Strength, Agility, Stamina, Intellect, Psyche, Sense. Priced by origin.</summary>
    Attribute = 1,
    Body = 2,
    Melee = 3,
    Ranged = 4,
    Initiative = 5,
    Defence = 6,
    Lattice = 7,
    Tech = 8,
}

/// <summary>How expensive a skill is to raise for a given profession or origin.</summary>
public enum CostTier : byte
{
    Cheap = 1,
    Normal = 2,
    Expensive = 3,

    /// <summary>Cannot be raised with skill points at all.</summary>
    Locked = 4,
}

/// <summary>
/// Which stats can be trained with skill points, and their group. Structure, not tuning: the
/// numbers (costs, caps) live in content.
/// </summary>
public static class Skills
{
    private static readonly Dictionary<Stat, SkillGroup> Groups = new()
    {
        [Stat.Strength] = SkillGroup.Attribute,
        [Stat.Agility] = SkillGroup.Attribute,
        [Stat.Stamina] = SkillGroup.Attribute,
        [Stat.Intellect] = SkillGroup.Attribute,
        [Stat.Psyche] = SkillGroup.Attribute,
        [Stat.Sense] = SkillGroup.Attribute,

        [Stat.BodyDevelopment] = SkillGroup.Body,
        [Stat.Adventuring] = SkillGroup.Body,

        [Stat.Blades] = SkillGroup.Melee,
        [Stat.Blunt] = SkillGroup.Melee,
        [Stat.Unarmed] = SkillGroup.Melee,
        [Stat.MeleeEnergy] = SkillGroup.Melee,
        [Stat.Parry] = SkillGroup.Melee,

        [Stat.Pistol] = SkillGroup.Ranged,
        [Stat.Rifle] = SkillGroup.Ranged,
        [Stat.Shotgun] = SkillGroup.Ranged,
        [Stat.HeavyWeapons] = SkillGroup.Ranged,
        [Stat.Grenade] = SkillGroup.Ranged,
        [Stat.Burst] = SkillGroup.Ranged,
        [Stat.FullAuto] = SkillGroup.Ranged,
        [Stat.AimedShot] = SkillGroup.Ranged,

        [Stat.MeleeInit] = SkillGroup.Initiative,
        [Stat.RangedInit] = SkillGroup.Initiative,
        [Stat.PhysicalInit] = SkillGroup.Initiative,
        [Stat.ProtocolInit] = SkillGroup.Initiative,

        [Stat.EvadeClose] = SkillGroup.Defence,
        [Stat.EvadeRanged] = SkillGroup.Defence,
        [Stat.DuckExplosives] = SkillGroup.Defence,
        [Stat.Deflect] = SkillGroup.Defence,

        [Stat.MatterShaping] = SkillGroup.Lattice,
        [Stat.BioWeaving] = SkillGroup.Lattice,
        [Stat.MindCraft] = SkillGroup.Lattice,
        [Stat.KineticFlux] = SkillGroup.Lattice,
        [Stat.SignalOps] = SkillGroup.Lattice,
        [Stat.TimeDistortion] = SkillGroup.Lattice,

        [Stat.Treatment] = SkillGroup.Tech,
        [Stat.FirstAid] = SkillGroup.Tech,
        [Stat.Mechanical] = SkillGroup.Tech,
        [Stat.Electrical] = SkillGroup.Tech,
    };

    /// <summary>Every trainable stat, in id order.</summary>
    public static IReadOnlyList<Stat> Trainable { get; } = Groups.Keys.Order().ToArray();

    public static bool IsTrainable(Stat stat) => Groups.ContainsKey(stat);

    public static SkillGroup GroupOf(Stat stat) =>
        Groups.TryGetValue(stat, out var g) ? g : throw new ArgumentOutOfRangeException(nameof(stat), stat, "Not a trainable stat.");
}
