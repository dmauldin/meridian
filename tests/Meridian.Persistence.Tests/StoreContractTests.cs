using Meridian.Core.Characters;
using Meridian.Core.Stats;
using Meridian.Core.World;

namespace Meridian.Persistence.Tests;

/// <summary>
/// The behaviour every <see cref="IGameStore"/> must have. Each store gets a subclass; the
/// Postgres store will run the same tests against a real database.
/// </summary>
public abstract class StoreContractTests : IDisposable
{
    private const string Hash = "$pbkdf2-sha256$i=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA";
    private static readonly Meridian.Core.Content.ContentPack Pack = Meridian.Content.ContentPacks.Embedded;

    protected abstract IGameStore Store { get; }

    /// <summary>A second handle onto the same data (another process, for the file store).</summary>
    protected abstract IGameStore OpenAgain();

    public virtual void Dispose() => GC.SuppressFinalize(this);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<AccountRecord> Account(string name = "Alice") =>
        (await Store.CreateAccountAsync(name, Hash, Ct))!;

    private static CharacterRecord Draft(ulong account, string name, Profession p = Profession.Operative) =>
        CharacterRecords.New(Pack, account, name, p, Origin.Baseline);

    [Fact]
    public async Task Usernames_are_unique_ignoring_case_but_keep_their_spelling()
    {
        var a = await Account("Alice");
        Assert.Null(await Store.CreateAccountAsync("ALICE", Hash, Ct));
        var found = await Store.FindAccountAsync("aLiCe", Ct);
        Assert.Equal(a, found);
        Assert.Equal("Alice", found!.Username);
        Assert.Null(await Store.FindAccountAsync("Bob", Ct));
    }

    [Fact]
    public async Task Password_hashes_can_be_replaced()
    {
        var a = await Account();
        await Store.SetPasswordHashAsync(a.Id, Hash + "x", Ct);
        Assert.Equal(Hash + "x", (await Store.FindAccountAsync("Alice", Ct))!.PasswordHash);
    }

    [Fact]
    public async Task Characters_are_created_at_version_one_and_listed_oldest_first()
    {
        var a = await Account();
        var (rook, e1) = await Store.CreateCharacterAsync(Draft(a.Id, "Rook"), 8, Ct);
        var (wren, e2) = await Store.CreateCharacterAsync(Draft(a.Id, "Wren", Profession.Medic), 8, Ct);
        Assert.Equal((CreateCharacterError.None, CreateCharacterError.None), (e1, e2));
        Assert.Equal(1, rook!.Version);
        Assert.NotEqual(rook.Id, wren!.Id);
        Assert.Null(rook.Placement);
        Assert.Equal(["Rook", "Wren"], (await Store.ListCharactersAsync(a.Id, Ct)).Select(c => c.Name));
        Assert.Equal(Profession.Medic, (await Store.LoadCharacterAsync(wren.Id, Ct))!.Profession);
    }

    [Fact]
    public async Task Character_names_are_unique_across_accounts_ignoring_case()
    {
        var a = await Account("Alice");
        var b = await Account("Bobby");
        await Store.CreateCharacterAsync(Draft(a.Id, "Rook"), 8, Ct);
        var (created, error) = await Store.CreateCharacterAsync(Draft(b.Id, "ROOK"), 8, Ct);
        Assert.Null(created);
        Assert.Equal(CreateCharacterError.NameTaken, error);
    }

    [Fact]
    public async Task Accounts_have_a_character_limit_and_must_exist()
    {
        var a = await Account();
        await Store.CreateCharacterAsync(Draft(a.Id, "Rook"), 2, Ct);
        await Store.CreateCharacterAsync(Draft(a.Id, "Wren"), 2, Ct);
        Assert.Equal(CreateCharacterError.TooManyCharacters, (await Store.CreateCharacterAsync(Draft(a.Id, "Moth"), 2, Ct)).Error);
        Assert.Equal(CreateCharacterError.NoSuchAccount, (await Store.CreateCharacterAsync(Draft(999, "Moth"), 2, Ct)).Error);
    }

    [Fact]
    public async Task A_save_writes_progress_and_placement_and_bumps_the_version()
    {
        var a = await Account();
        var rook = (await Store.CreateCharacterAsync(Draft(a.Id, "Rook"), 8, Ct)).Created!;
        var progressed = rook with
        {
            Level = 3,
            Xp = 120,
            UnspentPoints = 7,
            Trained = [new(Stat.Rifle, 12), new(Stat.Agility, 2)],
            Placement = new Placement("glasswind_flats", new Position(1_500, -2_250), 90),
        };
        var outcome = Assert.Single(await Store.SaveCharactersAsync([progressed], Ct));
        Assert.Equal(new SaveOutcome(rook.Id, true, 2), outcome);

        var loaded = (await OpenAgain().LoadCharacterAsync(rook.Id, Ct))!;
        Assert.Equal((3, 120L, 7, 2L), (loaded.Level, loaded.Xp, loaded.UnspentPoints, loaded.Version));
        Assert.Equal(progressed.Placement, loaded.Placement);
        Assert.Equal(progressed.Trained.OrderBy(t => t.Key), loaded.Trained.OrderBy(t => t.Key));
    }

    [Fact]
    public async Task A_new_character_wears_its_kit_weapon()
    {
        var a = await Account();
        var rook = (await Store.CreateCharacterAsync(Draft(a.Id, "Rook", Profession.Warden), 8, Ct)).Created!;
        var kit = Pack.Items[Pack.Professions[(int)Profession.Warden].StartingWeaponKey];
        Assert.Equal([new StoredItem(ItemLocation.Worn, (int)kit.Slot, kit.Id, Pack.Professions[(int)Profession.Warden].StartingWeaponQl)],
            (await Store.LoadCharacterAsync(rook.Id, Ct))!.Items);
    }

    [Fact]
    public async Task A_save_replaces_the_characters_items()
    {
        var a = await Account();
        var rook = (await Store.CreateCharacterAsync(Draft(a.Id, "Rook"), 8, Ct)).Created!;
        StoredItem[] items = [new(ItemLocation.Inventory, 4, 2001, 17), new(ItemLocation.Worn, 13, 2001, 3)];
        await Store.SaveCharactersAsync([rook with { Items = items }], Ct);
        Assert.Equal(items, (await OpenAgain().LoadCharacterAsync(rook.Id, Ct))!.Items);

        await Store.SaveCharactersAsync([rook with { Version = 2, Items = [] }], Ct);
        Assert.Empty((await Store.LoadCharacterAsync(rook.Id, Ct))!.Items);
    }

    [Fact]
    public async Task A_save_never_changes_identity()
    {
        var a = await Account();
        var rook = (await Store.CreateCharacterAsync(Draft(a.Id, "Rook"), 8, Ct)).Created!;
        await Store.SaveCharactersAsync([rook with { Name = "Evil", AccountId = 77, Profession = Profession.Medic }], Ct);
        var loaded = (await Store.LoadCharacterAsync(rook.Id, Ct))!;
        Assert.Equal(("Rook", a.Id, Profession.Operative), (loaded.Name, loaded.AccountId, loaded.Profession));
    }

    [Fact]
    public async Task A_stale_save_is_refused_and_reports_the_stored_version()
    {
        var a = await Account();
        var rook = (await Store.CreateCharacterAsync(Draft(a.Id, "Rook"), 8, Ct)).Created!;
        await Store.SaveCharactersAsync([rook with { Xp = 10 }], Ct);                  // now version 2
        var stale = Assert.Single(await Store.SaveCharactersAsync([rook with { Xp = 999 }], Ct)); // still says 1
        Assert.Equal(new SaveOutcome(rook.Id, false, 2), stale);
        Assert.Equal(10, (await Store.LoadCharacterAsync(rook.Id, Ct))!.Xp);
    }

    [Fact]
    public async Task Saves_in_one_batch_are_independent()
    {
        var a = await Account();
        var rook = (await Store.CreateCharacterAsync(Draft(a.Id, "Rook"), 8, Ct)).Created!;
        var wren = (await Store.CreateCharacterAsync(Draft(a.Id, "Wren"), 8, Ct)).Created!;
        var outcomes = await Store.SaveCharactersAsync([rook with { Version = 5 }, wren with { Xp = 1 }, rook with { Id = 404 }], Ct);
        Assert.Equal([new(rook.Id, false, 1), new(wren.Id, true, 2), new(404, false, null)], outcomes);
    }

    [Fact]
    public async Task Concurrent_creates_of_one_name_have_exactly_one_winner()
    {
        var a = await Account();
        var other = OpenAgain();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            (i % 2 == 0 ? Store : other).CreateCharacterAsync(Draft(a.Id, "Rook"), 100, Ct)));
        Assert.Single(results, r => r.Error == CreateCharacterError.None);
        Assert.Equal(15, results.Count(r => r.Error == CreateCharacterError.NameTaken));
    }
}

public sealed class InMemoryStoreTests : StoreContractTests
{
    private readonly InMemoryGameStore _store = new();

    protected override IGameStore Store => _store;

    protected override IGameStore OpenAgain() => _store;
}

public sealed class JsonFileStoreTests : StoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "meridian-store-" + Guid.NewGuid().ToString("N"));
    private readonly JsonFileGameStore _store;

    public JsonFileStoreTests() => _store = new JsonFileGameStore(_dir);

    protected override IGameStore Store => _store;

    protected override IGameStore OpenAgain() => new JsonFileGameStore(_dir);

    public override void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        base.Dispose();
    }

    [Fact]
    public async Task The_file_is_shaped_like_the_database_rows()
    {
        var a = (await _store.CreateAccountAsync("Alice", "$pbkdf2-sha256$i=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA", TestContext.Current.CancellationToken))!;
        var draft = CharacterRecords.New(Meridian.Content.ContentPacks.Embedded, a.Id, "Rook", Profession.Operative, Origin.Baseline);
        var rook = (await _store.CreateCharacterAsync(draft with { Trained = [new(Stat.Rifle, 4)] }, 8, TestContext.Current.CancellationToken)).Created!;
        var text = await File.ReadAllTextAsync(_store.FilePath, TestContext.Current.CancellationToken);
        Assert.Contains("\"unspent_points\"", text, StringComparison.Ordinal);
        Assert.Contains($"\"{(int)Stat.Rifle}\": 4", text, StringComparison.Ordinal); // stat ids, like the jsonb column
        Assert.Equal(1, rook.Version);
    }
}
