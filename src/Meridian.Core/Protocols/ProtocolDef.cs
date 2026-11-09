using Meridian.Core.Characters;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;

namespace Meridian.Core.Protocols;

/// <summary>
/// A stacking family of Protocols, e.g. "Rifle Enhancement". Only the strongest member of a
/// line can be active on a target; different lines always stack.
/// </summary>
public readonly record struct ProtocolLine(int Id);

/// <summary>
/// Static definition of a buff/debuff Protocol (the game's spells). Loaded from content data.
/// </summary>
/// <param name="Id">Content id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Line">Stacking line.</param>
/// <param name="Strength">Rank within the line. Higher replaces lower.</param>
/// <param name="LatticeCost">Capacity the buff occupies on its target while active.</param>
/// <param name="EnergyCost">Energy the caster spends.</param>
/// <param name="DurationTicks">How long it lasts, in simulation ticks.</param>
/// <param name="CastRequirement">What the caster needs to upload and cast it.</param>
/// <param name="Effects">Stat changes applied to the target.</param>
public sealed record ProtocolDef(
    int Id,
    string Name,
    ProtocolLine Line,
    int Strength,
    int LatticeCost,
    int EnergyCost,
    int DurationTicks,
    Requirement CastRequirement,
    IReadOnlyList<StatEffect> Effects)
{
    /// <summary>True if <paramref name="caster"/> meets the cast requirement.</summary>
    public bool CanCast(ICharacterView caster) => CastRequirement.Evaluate(caster);
}
