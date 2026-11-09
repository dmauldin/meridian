namespace Meridian.Core.Stats;

/// <summary>How a modifier combines with a stat's base value.</summary>
public enum ModifierKind : byte
{
    /// <summary>Added to the base value.</summary>
    Flat,

    /// <summary>
    /// Percentage of (base + flat), in basis points: 1000 = +10%, -2500 = -25%.
    /// All percent modifiers on a stat are summed before being applied once.
    /// </summary>
    Percent,

    /// <summary>
    /// Replaces the computed value. If several overrides are active, the highest wins.
    /// Used for effects like "root: run speed = 0" (with a negative-stacking guard in content).
    /// </summary>
    Override,
}

/// <summary>What kind of thing a modifier came from. Used for tooltips and bulk removal.</summary>
public enum SourceKind : byte
{
    Item,
    Implant,
    Protocol,
    Perk,
    Origin,
    Debug,
}

/// <summary>
/// Identifies the origin of a group of modifiers, e.g. "the item in slot 7" or
/// "active Protocol instance 42". Removing a source removes all of its modifiers.
/// </summary>
public readonly record struct ModifierSource(SourceKind Kind, long Id)
{
    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>A single change to one stat.</summary>
public readonly record struct Modifier(Stat Stat, ModifierKind Kind, int Value, ModifierSource Source)
{
    public static Modifier Flat(Stat stat, int value, ModifierSource source) => new(stat, ModifierKind.Flat, value, source);

    public static Modifier Percent(Stat stat, int basisPoints, ModifierSource source) => new(stat, ModifierKind.Percent, basisPoints, source);

    public static Modifier Override(Stat stat, int value, ModifierSource source) => new(stat, ModifierKind.Override, value, source);
}

/// <summary>A modifier without a source yet: what content data (items, Protocols) declares.</summary>
public readonly record struct StatEffect(Stat Stat, ModifierKind Kind, int Value)
{
    public Modifier WithSource(ModifierSource source) => new(Stat, Kind, Value, source);
}
