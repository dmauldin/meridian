using Meridian.Core.Characters;
using Meridian.Core.Stats;
using Meridian.Core.World;

namespace Meridian.Persistence;

/// <summary>
/// The store rules in memory, shared by <see cref="InMemoryGameStore"/> and
/// <see cref="JsonFileGameStore"/> so both behave identically. Not thread-safe; callers lock.
/// The Postgres store implements the same rules in SQL and runs the same contract tests.
/// </summary>
internal sealed class StoreModel
{
    private readonly SortedDictionary<ulong, AccountRecord> _accounts = [];
    private readonly Dictionary<string, ulong> _accountsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedDictionary<ulong, CharacterRecord> _characters = [];
    private readonly HashSet<string> _characterNames = new(StringComparer.OrdinalIgnoreCase);
    private ulong _lastAccountId;
    private ulong _lastCharacterId;

    public AccountRecord? CreateAccount(string username, string passwordHash)
    {
        if (_accountsByName.ContainsKey(username))
        {
            return null;
        }
        var account = new AccountRecord(++_lastAccountId, username, passwordHash);
        AddAccount(account);
        return account;
    }

    public AccountRecord? FindAccount(string username) =>
        _accountsByName.TryGetValue(username, out var id) ? _accounts[id] : null;

    public void SetPasswordHash(ulong accountId, string passwordHash)
    {
        if (_accounts.TryGetValue(accountId, out var account))
        {
            _accounts[accountId] = account with { PasswordHash = passwordHash };
        }
    }

    public IReadOnlyList<CharacterRecord> ListCharacters(ulong accountId) =>
        [.. _characters.Values.Where(c => c.AccountId == accountId)];

    public (CharacterRecord? Created, CreateCharacterError Error) CreateCharacter(CharacterRecord draft, int maxPerAccount)
    {
        if (!_accounts.ContainsKey(draft.AccountId))
        {
            return (null, CreateCharacterError.NoSuchAccount);
        }
        if (_characterNames.Contains(draft.Name))
        {
            return (null, CreateCharacterError.NameTaken);
        }
        if (_characters.Values.Count(c => c.AccountId == draft.AccountId) >= maxPerAccount)
        {
            return (null, CreateCharacterError.TooManyCharacters);
        }
        var created = draft with { Id = ++_lastCharacterId, Version = 1, Trained = [.. draft.Trained], Items = [.. draft.Items] };
        AddCharacter(created);
        return (created, CreateCharacterError.None);
    }

    public CharacterRecord? LoadCharacter(ulong id) => _characters.GetValueOrDefault(id);

    public List<SaveOutcome> SaveCharacters(IReadOnlyList<CharacterRecord> records)
    {
        var outcomes = new List<SaveOutcome>(records.Count);
        foreach (var r in records)
        {
            if (!_characters.TryGetValue(r.Id, out var stored))
            {
                outcomes.Add(new(r.Id, false, null));
                continue;
            }
            if (stored.Version != r.Version)
            {
                outcomes.Add(new(r.Id, false, stored.Version));
                continue;
            }
            // Only progression and placement: a save can never rename, re-parent or re-class.
            var saved = stored with
            {
                Level = r.Level,
                Xp = r.Xp,
                UnspentPoints = r.UnspentPoints,
                Trained = [.. r.Trained],
                Placement = r.Placement,
                Items = [.. r.Items],
                Version = stored.Version + 1,
            };
            _characters[r.Id] = saved;
            outcomes.Add(new(r.Id, true, saved.Version));
        }
        return outcomes;
    }

    private void AddAccount(AccountRecord account)
    {
        _accounts.Add(account.Id, account);
        _accountsByName.Add(account.Username, account.Id);
        _lastAccountId = Math.Max(_lastAccountId, account.Id);
    }

    private void AddCharacter(CharacterRecord character)
    {
        _characters.Add(character.Id, character);
        _characterNames.Add(character.Name);
        _lastCharacterId = Math.Max(_lastCharacterId, character.Id);
    }

    // ------------------------------------------------------------------ file form

    /// <summary>The model as plain rows, shaped like the database tables (stat ids, not names).</summary>
    public StoreFile ToFile() => new(
        _lastAccountId,
        _lastCharacterId,
        [.. _accounts.Values.Select(a => new AccountRow(a.Id, a.Username, a.PasswordHash))],
        [.. _characters.Values.Select(c => new CharacterRow(
            c.Id, c.AccountId, c.Name, (int)c.Profession, (int)c.Origin, c.Level, c.Xp, c.UnspentPoints,
            c.Trained.ToDictionary(t => (int)t.Key, t => t.Value),
            c.Placement?.Zone, c.Placement?.Position.X, c.Placement?.Position.Z, c.Placement?.Health,
            c.Version,
            [.. c.Items.Select(i => new ItemRow((int)i.Location, i.Position, i.TemplateId, i.Ql))]))]);

    public static StoreModel FromFile(StoreFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var model = new StoreModel();
        foreach (var a in file.Accounts)
        {
            model.AddAccount(new AccountRecord(a.Id, a.Username, a.PasswordHash));
        }
        foreach (var c in file.Characters)
        {
            var placement = c.Zone is null ? null : new Placement(c.Zone, new Position(c.X ?? 0, c.Z ?? 0), c.Health ?? 0);
            model.AddCharacter(new CharacterRecord(
                c.Id, c.AccountId, c.Name, (Profession)c.Profession, (Origin)c.Origin, c.Level, c.Xp, c.UnspentPoints,
                [.. c.Skills.OrderBy(s => s.Key).Select(s => new KeyValuePair<Stat, int>((Stat)s.Key, s.Value))],
                placement,
                c.Version,
                [.. (c.Items ?? []).Select(i => new StoredItem((ItemLocation)i.Location, i.Position, i.TemplateId, i.Ql))]));
        }
        model._lastAccountId = Math.Max(model._lastAccountId, file.LastAccountId);
        model._lastCharacterId = Math.Max(model._lastCharacterId, file.LastCharacterId);
        return model;
    }
}

internal sealed record StoreFile(ulong LastAccountId, ulong LastCharacterId, List<AccountRow> Accounts, List<CharacterRow> Characters);

internal sealed record AccountRow(ulong Id, string Username, string PasswordHash);

internal sealed record CharacterRow(
    ulong Id,
    ulong AccountId,
    string Name,
    int Profession,
    int Origin,
    int Level,
    long Xp,
    int UnspentPoints,
    Dictionary<int, int> Skills,
    string? Zone,
    int? X,
    int? Z,
    int? Health,
    long Version,
    List<ItemRow>? Items);

/// <summary>A row of <c>character_items</c>.</summary>
internal sealed record ItemRow(int Location, int Position, int TemplateId, int Ql);
