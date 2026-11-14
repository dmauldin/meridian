namespace Meridian.Persistence;

/// <summary>
/// Accounts and characters. Implementations are thread-safe. Inputs are assumed validated by the
/// caller (see <see cref="NameRules"/>); stores enforce only uniqueness, limits and versions
/// (the Postgres schema also enforces shapes as a backstop).
/// </summary>
public interface IGameStore
{
    /// <summary>Creates an account, or returns null if the username is taken (ignoring case).</summary>
    Task<AccountRecord?> CreateAccountAsync(string username, string passwordHash, CancellationToken cancellationToken = default);

    /// <summary>Finds an account by username, ignoring case.</summary>
    Task<AccountRecord?> FindAccountAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Replaces a password hash (used to upgrade hashes on login).</summary>
    Task SetPasswordHashAsync(ulong accountId, string passwordHash, CancellationToken cancellationToken = default);

    /// <summary>The account's characters, oldest first.</summary>
    Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(ulong accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new character. <paramref name="draft"/>'s id and version are ignored; the stored
    /// record (with its id and version 1) is returned.
    /// </summary>
    Task<(CharacterRecord? Created, CreateCharacterError Error)> CreateCharacterAsync(
        CharacterRecord draft, int maxPerAccount, CancellationToken cancellationToken = default);

    Task<CharacterRecord?> LoadCharacterAsync(ulong characterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves progression, placement and items (replacing all of the character's items) for each
    /// record whose <see cref="CharacterRecord.Version"/> matches the stored one, bumping the
    /// version, atomically per character. Identity fields are never changed by a save.
    /// Records are independent: one conflict does not stop the others.
    /// </summary>
    Task<IReadOnlyList<SaveOutcome>> SaveCharactersAsync(IReadOnlyList<CharacterRecord> records, CancellationToken cancellationToken = default);
}
