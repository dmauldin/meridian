using System.Text.RegularExpressions;

namespace Meridian.Persistence;

/// <summary>What account and character names may look like. The database checks the same patterns.</summary>
public static partial class NameRules
{
    public const int MaxNameLength = 24;

    /// <summary>A letter, then 2-23 letters or digits.</summary>
    public static bool IsValidUsername(string? name) => name is not null && Pattern().IsMatch(name);

    /// <summary>Same shape as usernames, so names fit the login ticket and the database checks.</summary>
    public static bool IsValidCharacterName(string? name) => name is not null && Pattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{2,23}\z")] // \z, not $: $ also matches before a trailing newline
    private static partial Regex Pattern();
}
