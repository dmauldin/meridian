namespace Meridian.Persistence;

/// <summary>A store in process memory: tests, and single-process tools. Thread-safe.</summary>
public sealed class InMemoryGameStore : IGameStore
{
    private readonly StoreModel _model = new();
    private readonly Lock _lock = new();

    public Task<AccountRecord?> CreateAccountAsync(string username, string passwordHash, CancellationToken cancellationToken = default) =>
        Run(() => _model.CreateAccount(username, passwordHash));

    public Task<AccountRecord?> FindAccountAsync(string username, CancellationToken cancellationToken = default) =>
        Run(() => _model.FindAccount(username));

    public Task SetPasswordHashAsync(ulong accountId, string passwordHash, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            _model.SetPasswordHash(accountId, passwordHash);
            return true;
        });

    public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(ulong accountId, CancellationToken cancellationToken = default) =>
        Run(() => _model.ListCharacters(accountId));

    public Task<(CharacterRecord? Created, CreateCharacterError Error)> CreateCharacterAsync(
        CharacterRecord draft, int maxPerAccount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Run(() => _model.CreateCharacter(draft, maxPerAccount));
    }

    public Task<CharacterRecord?> LoadCharacterAsync(ulong characterId, CancellationToken cancellationToken = default) =>
        Run(() => _model.LoadCharacter(characterId));

    public Task<IReadOnlyList<SaveOutcome>> SaveCharactersAsync(IReadOnlyList<CharacterRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Run<IReadOnlyList<SaveOutcome>>(() => _model.SaveCharacters(records));
    }

    private Task<T> Run<T>(Func<T> op)
    {
        lock (_lock)
        {
            return Task.FromResult(op());
        }
    }
}
