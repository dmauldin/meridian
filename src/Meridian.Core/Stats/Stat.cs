namespace Meridian.Core.Stats;

/// <summary>
/// Every number a character has. Attributes, skills, derived pools and armor classes share
/// one id space so that modifiers, requirements and the network layer can treat them alike.
/// </summary>
/// <remarks>
/// Values are stable wire and database ids: append new stats, never renumber.
/// M0 carries the subset the vertical slice needs; the full ~85 skills arrive in M2.
/// </remarks>
public enum Stat : ushort
{
    // Core
    Level = 0,

    // Attributes
    Strength = 10,
    Agility = 11,
    Stamina = 12,
    Intellect = 13,
    Psyche = 14,
    Sense = 15,

    // Derived pools (base value comes from StatCatalog formulas)
    MaxHealth = 30,
    MaxEnergy = 31,
    LatticeCapacity = 32,

    // Body
    BodyDevelopment = 50,
    Adventuring = 51,
    RunSpeed = 52,

    // Melee
    Blades = 60,
    Blunt = 61,
    Unarmed = 62,
    MeleeEnergy = 63,
    Parry = 64,

    // Ranged
    Pistol = 70,
    Rifle = 71,
    Shotgun = 72,
    HeavyWeapons = 73,
    Grenade = 74,
    Burst = 75,
    FullAuto = 76,
    AimedShot = 77,

    // Initiative
    MeleeInit = 90,
    RangedInit = 91,
    PhysicalInit = 92,
    ProtocolInit = 93,

    // Defence
    EvadeClose = 100,
    EvadeRanged = 101,
    DuckExplosives = 102,
    Deflect = 103,

    // Lattice schools
    MatterShaping = 120,
    BioWeaving = 121,
    MindCraft = 122,
    KineticFlux = 123,
    SignalOps = 124,
    TimeDistortion = 125,

    // Tech and utility
    Treatment = 140,
    FirstAid = 141,
    Mechanical = 142,
    Electrical = 143,

    // Armor classes
    ProjectileAC = 160,
    MeleeAC = 161,
    EnergyAC = 162,
    ChemicalAC = 163,
    RadiationAC = 164,
    ColdAC = 165,
    FireAC = 166,
    PoisonAC = 167,

    // Combat modifiers
    AddDamage = 180,
    /// <summary>Critical-hit chance in basis points (100 = 1%).</summary>
    CritChance = 181,

    /// <summary>
    /// Adjusts the threat this character generates, in basis points: +5000 = 150% threat
    /// (a tank's stance), -3000 = 70% (a stealthy buff). Base 0. Protocols modify it.
    /// </summary>
    ThreatModifier = 182,
}

/// <summary>Helpers for mapping the sparse <see cref="Stat"/> ids onto dense arrays.</summary>
public static class StatIndex
{
    /// <summary>One past the largest <see cref="Stat"/> value; arrays indexed by stat use this length.</summary>
    public static readonly int Capacity = ComputeCapacity();

    /// <summary>All defined stats in ascending id order.</summary>
    public static readonly IReadOnlyList<Stat> All = Enum.GetValues<Stat>();

    private static int ComputeCapacity()
    {
        var max = 0;
        foreach (var s in Enum.GetValues<Stat>())
        {
            max = Math.Max(max, (int)s);
        }
        return max + 1;
    }
}
