using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Meridian.Core.Content;
using Meridian.Net;
using Meridian.Net.Client;
using Meridian.Persistence;

namespace Meridian.Server.Auth;

public sealed record AuthOptions
{
    /// <summary>Gateway address handed to clients.</summary>
    public string PublicGatewayHost { get; init; } = "127.0.0.1";

    public int PublicGatewayPort { get; init; } = NetDefaults.GatewayPort;

    /// <summary>How long a gateway ticket stays valid. Short: clients connect right after play.</summary>
    public TimeSpan TicketLifetime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a login lasts for the character endpoints.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(30);

    public int MinPasswordLength { get; init; } = 8;

    public int MaxCharactersPerAccount { get; init; } = 8;
}

/// <summary>Either a value or an HTTP status with an error body.</summary>
public readonly record struct AuthResult<T>(T? Value, HttpStatusCode Status, AuthError? Error);

public static class AuthResult
{
    public static AuthResult<T> Ok<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(value, status, null);

    public static AuthResult<T> Fail<T>(HttpStatusCode status, string code, string message) => new(default, status, new AuthError(code, message));
}

/// <summary>
/// Accounts, logins, characters and gateway tickets. Thread-safe: ASP.NET calls it concurrently.
/// Everything a client sends is validated here before it reaches the store.
/// </summary>
public sealed class AuthService(AuthOptions options, IGameStore store, PasswordHasher hasher, TicketSealer sealer, TimeProvider clock, ContentPack content)
{
    private readonly ConcurrentDictionary<string, (ulong AccountId, DateTimeOffset Expires)> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _sealLock = new();
    private long _lastSweepTicks;

    public async Task<AuthResult<RegisterResponse>> RegisterAsync(Credentials? request, CancellationToken cancellationToken = default)
    {
        if (!NameRules.IsValidUsername(request?.Username))
        {
            return AuthResult.Fail<RegisterResponse>(HttpStatusCode.BadRequest, AuthErrorCodes.InvalidUsername,
                "Usernames are 3-24 letters or digits and start with a letter.");
        }
        if (request!.Password is not { } password || password.Length < options.MinPasswordLength || !PasswordHasher.IsAcceptableLength(password))
        {
            return AuthResult.Fail<RegisterResponse>(HttpStatusCode.BadRequest, AuthErrorCodes.InvalidPassword,
                $"Passwords are at least {options.MinPasswordLength} characters and at most {PasswordHasher.MaxPasswordBytes} bytes.");
        }
        var account = await store.CreateAccountAsync(request.Username, hasher.Hash(password), cancellationToken).ConfigureAwait(false);
        return account is null
            ? AuthResult.Fail<RegisterResponse>(HttpStatusCode.Conflict, AuthErrorCodes.UsernameTaken, "That username is taken.")
            : AuthResult.Ok<RegisterResponse>(new RegisterResponse(account.Id), HttpStatusCode.Created);
    }

    public async Task<AuthResult<LoginResponse>> LoginAsync(Credentials? request, CancellationToken cancellationToken = default)
    {
        var refused = AuthResult.Fail<LoginResponse>(HttpStatusCode.Unauthorized, AuthErrorCodes.BadCredentials, "Wrong username or password.");
        if (!NameRules.IsValidUsername(request?.Username) || !PasswordHasher.IsAcceptableLength(request!.Password))
        {
            return refused;
        }
        var account = await store.FindAccountAsync(request.Username, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            hasher.VerifyNothing(request.Password); // same cost as a real check: no username probing by timing
            return refused;
        }
        var check = hasher.Verify(request.Password, account.PasswordHash);
        if (check == PasswordCheck.Invalid)
        {
            return refused;
        }
        if (check == PasswordCheck.ValidNeedsRehash)
        {
            await store.SetPasswordHashAsync(account.Id, hasher.Hash(request.Password), cancellationToken).ConfigureAwait(false);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _sessions[token] = (account.Id, clock.GetUtcNow() + options.SessionLifetime);
        SweepSessions();
        var characters = await store.ListCharactersAsync(account.Id, cancellationToken).ConfigureAwait(false);
        return AuthResult.Ok<LoginResponse>(new LoginResponse(account.Id, token, [.. characters.Select(Summary)]));
    }

    public async Task<AuthResult<List<CharacterSummary>>> ListCharactersAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (AccountFor(token) is not { } accountId)
        {
            return AuthResult.Fail<List<CharacterSummary>>(HttpStatusCode.Unauthorized, AuthErrorCodes.NotLoggedIn, "Log in again.");
        }
        var characters = await store.ListCharactersAsync(accountId, cancellationToken).ConfigureAwait(false);
        return AuthResult.Ok<List<CharacterSummary>>([.. characters.Select(Summary)]);
    }

    public async Task<AuthResult<CharacterSummary>> CreateCharacterAsync(string? token, CreateCharacterRequest? request, CancellationToken cancellationToken = default)
    {
        if (AccountFor(token) is not { } accountId)
        {
            return AuthResult.Fail<CharacterSummary>(HttpStatusCode.Unauthorized, AuthErrorCodes.NotLoggedIn, "Log in again.");
        }
        if (!NameRules.IsValidCharacterName(request?.Name))
        {
            return AuthResult.Fail<CharacterSummary>(HttpStatusCode.BadRequest, AuthErrorCodes.InvalidName,
                "Names are 3-24 letters or digits and start with a letter.");
        }
        if (!content.Professions.TryGet(Key(request!.Profession), out var profession))
        {
            return AuthResult.Fail<CharacterSummary>(HttpStatusCode.BadRequest, AuthErrorCodes.UnknownProfession,
                $"Playable professions: {string.Join(", ", content.Professions.All.Select(p => p.Name))}.");
        }
        if (!content.Origins.TryGet(Key(request.Origin), out var origin))
        {
            return AuthResult.Fail<CharacterSummary>(HttpStatusCode.BadRequest, AuthErrorCodes.UnknownOrigin,
                $"Playable origins: {string.Join(", ", content.Origins.All.Select(o => o.Name))}.");
        }

        var draft = CharacterRecords.New(content, accountId, request.Name, profession!.Id, origin!.Id);
        var (created, error) = await store.CreateCharacterAsync(draft, options.MaxCharactersPerAccount, cancellationToken).ConfigureAwait(false);
        return error switch
        {
            CreateCharacterError.None => AuthResult.Ok<CharacterSummary>(Summary(created!), HttpStatusCode.Created),
            CreateCharacterError.NameTaken => AuthResult.Fail<CharacterSummary>(HttpStatusCode.Conflict, AuthErrorCodes.NameTaken, "That name is taken."),
            CreateCharacterError.TooManyCharacters => AuthResult.Fail<CharacterSummary>(HttpStatusCode.Conflict, AuthErrorCodes.TooManyCharacters,
                $"An account can have at most {options.MaxCharactersPerAccount} characters."),
            _ => AuthResult.Fail<CharacterSummary>(HttpStatusCode.Unauthorized, AuthErrorCodes.NotLoggedIn, "Log in again."),
        };
    }

    public async Task<AuthResult<PlayResponse>> PlayAsync(string? token, PlayRequest? request, CancellationToken cancellationToken = default)
    {
        if (AccountFor(token) is not { } accountId)
        {
            return AuthResult.Fail<PlayResponse>(HttpStatusCode.Unauthorized, AuthErrorCodes.NotLoggedIn, "Log in again.");
        }
        var character = request is null ? null : await store.LoadCharacterAsync(request.CharacterId, cancellationToken).ConfigureAwait(false);
        if (character is null || character.AccountId != accountId)
        {
            // Same answer for "missing" and "someone else's": ids are not secret, but ownership is not advertised.
            return AuthResult.Fail<PlayResponse>(HttpStatusCode.NotFound, AuthErrorCodes.NoSuchCharacter, "No such character on this account.");
        }

        var sessionKey = RandomNumberGenerator.GetBytes(SessionCipher.KeyBytes);
        var ticket = new LoginTicket(
            BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)),
            accountId,
            character.Name,
            sessionKey,
            clock.GetUtcNow().Add(options.TicketLifetime).ToUnixTimeSeconds(),
            character.Id);
        byte[] sealedTicket;
        lock (_sealLock)
        {
            sealedTicket = sealer.Seal(ticket); // ChaCha20Poly1305 instances are not safe for concurrent use
        }
        return AuthResult.Ok<PlayResponse>(new PlayResponse(
            accountId,
            character.Id,
            options.PublicGatewayHost,
            options.PublicGatewayPort,
            Convert.ToBase64String(sessionKey),
            Convert.ToBase64String(sealedTicket)));
    }

    private ulong? AccountFor(string? token)
    {
        if (token is null || token.Length > 64 || !_sessions.TryGetValue(token, out var session))
        {
            return null;
        }
        if (session.Expires <= clock.GetUtcNow())
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        return session.AccountId;
    }

    private void SweepSessions()
    {
        var now = clock.GetUtcNow();
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < TimeSpan.TicksPerMinute || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
        {
            return;
        }
        foreach (var (token, session) in _sessions)
        {
            if (session.Expires <= now)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }

    private CharacterSummary Summary(CharacterRecord c) => new(
        c.Id,
        c.Name,
        content.Professions.TryGet((int)c.Profession, out var p) ? p!.Key : c.Profession.ToString().ToLowerInvariant(),
        content.Origins.TryGet((int)c.Origin, out var o) ? o!.Key : c.Origin.ToString().ToLowerInvariant(),
        c.Level);

    private static string Key(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();
}
