using Meridian.Core.Stats;

namespace Meridian.Core.Characters;

/// <summary>The 14 professions from the design doc. Values are stable ids.</summary>
public enum Profession : byte
{
    Warden = 1,
    Medic = 2,
    Rigger = 3,
    Weaver = 4,
    Envoy = 5,
    Trooper = 6,
    Operative = 7,
    Revenant = 8,
    Smuggler = 9,
    Arbitrageur = 10,
    Pathfinder = 11,
    Ascetic = 12,
    Overload = 13,
    Sentinel = 14,
}

/// <summary>The four origins (breeds). Values are stable ids.</summary>
public enum Origin : byte
{
    Baseline = 1,
    Heavyworlder = 2,
    Splice = 3,
    Nightborn = 4,
}

/// <summary>Everything a requirement can ask about a character.</summary>
public interface ICharacterView : IStatReader
{
    Profession Profession { get; }

    Origin Origin { get; }
}

/// <summary>A character's identity plus its <see cref="StatBlock"/>.</summary>
public sealed class Character(Profession profession, Origin origin, StatBlock? stats = null) : ICharacterView
{
    public Profession Profession { get; } = profession;

    public Origin Origin { get; } = origin;

    public StatBlock Stats { get; } = stats ?? new StatBlock();

    public int Get(Stat stat) => Stats.Get(stat);
}
