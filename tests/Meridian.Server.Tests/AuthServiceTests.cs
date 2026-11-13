using System.Net;
using Meridian.Core.Characters;
using Meridian.Net;
using Meridian.Net.Client;
using Meridian.Persistence;
using Meridian.Server.Auth;

namespace Meridian.Server.Tests;

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class AuthServiceTests : IDisposable
{
    private static readonly byte[] TicketKey = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly InMemoryGameStore _store = new();
    private readonly TicketSealer _sealer = new(TicketKey);
    private readonly AuthService _auth;

    public AuthServiceTests() =>
        _auth = new AuthService(new AuthOptions { MaxCharactersPerAccount = 2 }, _store, new PasswordHasher(1_000), _sealer, _clock, Meridian.Content.ContentPacks.Embedded);

    public void Dispose() => _sealer.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> LoggedIn(string user = "Alice", string password = "password1")
    {
        await _auth.RegisterAsync(new Credentials(user, password), Ct);
        return (await _auth.LoginAsync(new Credentials(user, password), Ct)).Value!.SessionToken;
    }

    [Theory]
    [InlineData("Alice", "password1", HttpStatusCode.Created, null)]
    [InlineData("A", "password1", HttpStatusCode.BadRequest, AuthErrorCodes.InvalidUsername)]
    [InlineData("Alice\n", "password1", HttpStatusCode.BadRequest, AuthErrorCodes.InvalidUsername)]
    [InlineData("Alice", "short", HttpStatusCode.BadRequest, AuthErrorCodes.InvalidPassword)]
    [InlineData("Alice", null, HttpStatusCode.BadRequest, AuthErrorCodes.InvalidPassword)]
    public async Task Registration_validates_names_and_passwords(string user, string? password, HttpStatusCode status, string? code)
    {
        var r = await _auth.RegisterAsync(new Credentials(user, password!), Ct);
        Assert.Equal((status, code), (r.Status, r.Error?.Code));
    }

    [Fact]
    public async Task Overlong_passwords_are_refused_without_hashing()
    {
        var r = await _auth.RegisterAsync(new Credentials("Alice", new string('x', 10_000)), Ct);
        Assert.Equal(AuthErrorCodes.InvalidPassword, r.Error?.Code);
    }

    [Fact]
    public async Task A_username_registers_once_ignoring_case()
    {
        await _auth.RegisterAsync(new Credentials("Alice", "password1"), Ct);
        var again = await _auth.RegisterAsync(new Credentials("aLICE", "password2"), Ct);
        Assert.Equal((HttpStatusCode.Conflict, AuthErrorCodes.UsernameTaken), (again.Status, again.Error?.Code));
    }

    [Fact]
    public async Task Passwords_are_stored_hashed()
    {
        await _auth.RegisterAsync(new Credentials("Alice", "password1"), Ct);
        var stored = (await _store.FindAccountAsync("Alice", Ct))!.PasswordHash;
        Assert.StartsWith("$pbkdf2-sha256$", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("password1", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_look_the_same()
    {
        await _auth.RegisterAsync(new Credentials("Alice", "password1"), Ct);
        var wrong = await _auth.LoginAsync(new Credentials("Alice", "password2"), Ct);
        var nobody = await _auth.LoginAsync(new Credentials("Nobody", "password1"), Ct);
        Assert.Equal(wrong.Status, nobody.Status);
        Assert.Equal(wrong.Error, nobody.Error);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
    }

    [Fact]
    public async Task Weak_hashes_are_upgraded_on_login()
    {
        await _store.CreateAccountAsync("Alice", new PasswordHasher(10).Hash("password1"), Ct);
        Assert.NotNull((await _auth.LoginAsync(new Credentials("alice", "password1"), Ct)).Value);
        Assert.StartsWith("$pbkdf2-sha256$i=1000$", (await _store.FindAccountAsync("Alice", Ct))!.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Characters_are_created_listed_and_limited()
    {
        var token = await LoggedIn();
        var rook = await _auth.CreateCharacterAsync(token, new CreateCharacterRequest("Rook", "Operative", "baseline"), Ct);
        Assert.Equal(HttpStatusCode.Created, rook.Status);
        Assert.Equal(new CharacterSummary(rook.Value!.Id, "Rook", "operative", "baseline", 1), rook.Value);
        await _auth.CreateCharacterAsync(token, new CreateCharacterRequest("Wren", "medic", "Nightborn"), Ct);

        var third = await _auth.CreateCharacterAsync(token, new CreateCharacterRequest("Moth", "warden", "baseline"), Ct);
        Assert.Equal(AuthErrorCodes.TooManyCharacters, third.Error?.Code);
        Assert.Equal(["Rook", "Wren"], (await _auth.ListCharactersAsync(token, Ct)).Value!.Select(c => c.Name));
        Assert.Equal(["Rook", "Wren"], (await _auth.LoginAsync(new Credentials("Alice", "password1"), Ct)).Value!.Characters.Select(c => c.Name));
    }

    [Theory]
    [InlineData("Rook", "Wizard", "baseline", AuthErrorCodes.UnknownProfession)]
    [InlineData("Rook", "Sentinel", "baseline", AuthErrorCodes.UnknownProfession)] // reserved id, not in content yet
    [InlineData("Rook", "medic", "Heavyworlder", AuthErrorCodes.UnknownOrigin)]
    [InlineData("R", "medic", "baseline", AuthErrorCodes.InvalidName)]
    [InlineData(null, "medic", "baseline", AuthErrorCodes.InvalidName)]
    public async Task Character_creation_validates_everything(string? name, string profession, string origin, string code)
    {
        var token = await LoggedIn();
        var r = await _auth.CreateCharacterAsync(token, new CreateCharacterRequest(name!, profession, origin), Ct);
        Assert.Equal((HttpStatusCode.BadRequest, code), (r.Status, r.Error?.Code));
    }

    [Fact]
    public async Task Character_names_are_unique_across_accounts()
    {
        await _auth.CreateCharacterAsync(await LoggedIn("Alice"), new CreateCharacterRequest("Rook", "medic", "baseline"), Ct);
        var r = await _auth.CreateCharacterAsync(await LoggedIn("Bobby"), new CreateCharacterRequest("rook", "medic", "baseline"), Ct);
        Assert.Equal(AuthErrorCodes.NameTaken, r.Error?.Code);
    }

    [Fact]
    public async Task Play_seals_the_character_into_the_ticket()
    {
        var token = await LoggedIn();
        var rook = (await _auth.CreateCharacterAsync(token, new CreateCharacterRequest("Rook", "medic", "baseline"), Ct)).Value!;
        var play = (await _auth.PlayAsync(token, new PlayRequest(rook.Id), Ct)).Value!;
        var ticket = _sealer.TryOpen(Convert.FromBase64String(play.Ticket))!;
        Assert.Equal((rook.Id, "Rook", play.AccountId), (ticket.CharacterId, ticket.CharacterName, ticket.AccountId));
        Assert.Equal(Convert.FromBase64String(play.SessionKey), ticket.SessionKey);
    }

    [Fact]
    public async Task You_cannot_play_someone_elses_character()
    {
        var alice = await LoggedIn("Alice");
        var rook = (await _auth.CreateCharacterAsync(alice, new CreateCharacterRequest("Rook", "medic", "baseline"), Ct)).Value!;
        var bobby = await LoggedIn("Bobby");
        var stolen = await _auth.PlayAsync(bobby, new PlayRequest(rook.Id), Ct);
        var missing = await _auth.PlayAsync(bobby, new PlayRequest(9_999), Ct);
        Assert.Equal((HttpStatusCode.NotFound, AuthErrorCodes.NoSuchCharacter), (stolen.Status, stolen.Error?.Code));
        Assert.Equal(stolen.Error, missing.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("forged")]
    public async Task Character_endpoints_need_a_valid_session(string? token)
    {
        await LoggedIn();
        Assert.Equal(AuthErrorCodes.NotLoggedIn, (await _auth.ListCharactersAsync(token, Ct)).Error?.Code);
        Assert.Equal(AuthErrorCodes.NotLoggedIn, (await _auth.PlayAsync(token, new PlayRequest(1), Ct)).Error?.Code);
        Assert.Equal(AuthErrorCodes.NotLoggedIn,
            (await _auth.CreateCharacterAsync(token, new CreateCharacterRequest("Rook", "medic", "baseline"), Ct)).Error?.Code);
    }

    [Fact]
    public async Task Sessions_expire()
    {
        var token = await LoggedIn();
        _clock.Now += new AuthOptions().SessionLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(AuthErrorCodes.NotLoggedIn, (await _auth.ListCharactersAsync(token, Ct)).Error?.Code);
    }
}
