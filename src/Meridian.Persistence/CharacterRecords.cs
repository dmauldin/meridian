using Meridian.Core.Characters;
using Meridian.Core.Content;

namespace Meridian.Persistence;

/// <summary>Converts between stored characters and the rules' <see cref="CharacterBuild"/>.</summary>
public static class CharacterRecords
{
    /// <summary>A new, unplaced level-1 character as content defines it. Id and version are set by the store.</summary>
    public static CharacterRecord New(ContentPack pack, ulong accountId, string name, Profession profession, Origin origin)
    {
        var build = CharacterBuild.Create(pack, profession, origin);
        return new CharacterRecord(0, accountId, name, profession, origin, build.Level, build.Xp, build.UnspentPoints, build.Trained(), null, 0);
    }

    /// <summary>
    /// Rebuilds the character's rules state. Returns null if content no longer defines its
    /// profession or origin (the caller refuses the login rather than guessing).
    /// </summary>
    public static CharacterBuild? ToBuild(this CharacterRecord record, ContentPack pack)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(pack);
        if (!pack.Professions.ContainsId((int)record.Profession) || !pack.Origins.ContainsId((int)record.Origin))
        {
            return null;
        }
        return CharacterBuild.Load(pack, record.Profession, record.Origin, record.Level, record.Xp, record.UnspentPoints, record.Trained);
    }

    /// <summary>The record with progression from <paramref name="build"/> and the given placement; identity and version unchanged.</summary>
    public static CharacterRecord WithProgress(this CharacterRecord record, CharacterBuild build, Placement placement)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(build);
        return record with
        {
            Level = build.Level,
            Xp = build.Xp,
            UnspentPoints = build.UnspentPoints,
            Trained = build.Trained(),
            Placement = placement,
        };
    }
}
