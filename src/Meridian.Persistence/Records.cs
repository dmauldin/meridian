using Meridian.Core.Characters;
using Meridian.Core.Stats;
using Meridian.Core.World;

namespace Meridian.Persistence;

/// <summary>An account. <paramref name="PasswordHash"/> is a PHC string (see <see cref="PasswordHasher"/>).</summary>
public sealed record AccountRecord(ulong Id, string Username, string PasswordHash);

/// <summary>Where a character was when last saved.</summary>
/// <param name="Zone">Zone key.</param>
/// <param name="Position">Millimetres on the ground plane.</param>
/// <param name="Health">At most the character's maximum; zero or less means dead (respawns on load).</param>
public sealed record Placement(string Zone, Position Position, int Health);

/// <summary>
/// A stored character. Identity (id, account, name, profession, origin) is fixed at creation;
/// progression and placement change and are what a save writes.
/// </summary>
/// <param name="Trained">Points raised above starting value per trainable stat (see <c>CharacterBuild.Trained</c>).</param>
/// <param name="Placement">Null until a zone first places the character.</param>
/// <param name="Version">Optimistic-concurrency version; a save must name the version it read.</param>
public sealed record CharacterRecord(
    ulong Id,
    ulong AccountId,
    string Name,
    Profession Profession,
    Origin Origin,
    int Level,
    long Xp,
    int UnspentPoints,
    IReadOnlyList<KeyValuePair<Stat, int>> Trained,
    Placement? Placement,
    long Version);

public enum CreateCharacterError
{
    None = 0,
    NameTaken = 1,
    TooManyCharacters = 2,
    NoSuchAccount = 3,
}

/// <summary>The result of saving one character.</summary>
/// <param name="Saved">False when the stored version no longer matches (someone else saved it) or it was deleted.</param>
/// <param name="Version">The version now stored, or null if the character no longer exists.</param>
public readonly record struct SaveOutcome(ulong Id, bool Saved, long? Version);
