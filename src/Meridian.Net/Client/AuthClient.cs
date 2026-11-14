using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Meridian.Net.Client;

// ---------------------------------------------------------------- contracts (JSON bodies)

/// <summary>Body of <c>POST /v1/register</c> and <c>POST /v1/login</c>.</summary>
public sealed record Credentials(string Username, string Password);

/// <summary>Response to <c>POST /v1/register</c>.</summary>
public sealed record RegisterResponse(ulong AccountId);

/// <summary>A character as the character-select screen shows it.</summary>
/// <param name="Profession">Content key, e.g. "medic".</param>
/// <param name="Origin">Content key, e.g. "nightborn".</param>
public sealed record CharacterSummary(ulong Id, string Name, string Profession, string Origin, int Level);

/// <summary>Response to <c>POST /v1/login</c>.</summary>
/// <param name="SessionToken">Bearer token for the character endpoints. Short-lived; keep secret.</param>
public sealed record LoginResponse(ulong AccountId, string SessionToken, IReadOnlyList<CharacterSummary> Characters);

/// <summary>Body of <c>POST /v1/characters</c>. Profession and origin are content keys or names, any case.</summary>
public sealed record CreateCharacterRequest(string Name, string Profession, string Origin);

/// <summary>Body of <c>POST /v1/play</c>.</summary>
public sealed record PlayRequest(ulong CharacterId);

/// <summary>Response to <c>POST /v1/play</c>: everything needed to connect to a gateway as one character.</summary>
/// <param name="SessionKey">Base64 32-byte key for <see cref="SessionCipher"/>. Keep secret.</param>
/// <param name="Ticket">Base64 sealed ticket to present to the gateway.</param>
public sealed record PlayResponse(ulong AccountId, ulong CharacterId, string GatewayHost, int GatewayPort, string SessionKey, string Ticket);

/// <summary>Error body. <paramref name="Code"/> is stable for programs; <paramref name="Message"/> is for people.</summary>
public sealed record AuthError(string Code, string Message);

/// <summary>Stable <see cref="AuthError.Code"/> values.</summary>
public static class AuthErrorCodes
{
    public const string InvalidUsername = "invalid_username";
    public const string InvalidPassword = "invalid_password";
    public const string UsernameTaken = "username_taken";
    public const string BadCredentials = "bad_credentials";
    public const string NotLoggedIn = "not_logged_in";
    public const string InvalidName = "invalid_name";
    public const string NameTaken = "name_taken";
    public const string TooManyCharacters = "too_many_characters";
    public const string UnknownProfession = "unknown_profession";
    public const string UnknownOrigin = "unknown_origin";
    public const string NoSuchCharacter = "no_such_character";
}

/// <summary>The auth service refused a request.</summary>
public sealed class AuthException : Exception
{
    public AuthException(HttpStatusCode status, AuthError? error)
        : base(error?.Message ?? $"Auth service returned {(int)status}.")
    {
        Status = status;
        Code = error?.Code;
    }

    public AuthException()
    {
    }

    public AuthException(string message)
        : base(message)
    {
    }

    public AuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public HttpStatusCode Status { get; }

    /// <summary>An <see cref="AuthErrorCodes"/> value, or null if the service sent none.</summary>
    public string? Code { get; }
}

// ---------------------------------------------------------------- client

/// <summary>Talks to the auth service. In production this runs over HTTPS.</summary>
public sealed class AuthClient(HttpClient http)
{
    /// <summary>Creates an account. Throws <see cref="AuthException"/> (e.g. <see cref="AuthErrorCodes.UsernameTaken"/>).</summary>
    public Task<RegisterResponse> RegisterAsync(Credentials credentials, CancellationToken cancellationToken = default) =>
        PostAsync<RegisterResponse>("v1/register", credentials, null, cancellationToken);

    /// <summary>Logs in to an account; the session lists and creates characters and starts play.</summary>
    public async Task<AccountSession> LoginAsync(Credentials credentials, CancellationToken cancellationToken = default)
    {
        var login = await PostAsync<LoginResponse>("v1/login", credentials, null, cancellationToken).ConfigureAwait(false);
        return new AccountSession(this, login);
    }

    /// <summary>
    /// Development shortcut used by bots, the headless autopilot and tests: registers the account if
    /// it does not exist, logs in, creates the named character if the account lacks it, and starts
    /// play as it.
    /// </summary>
    public async Task<PlayResponse> QuickPlayAsync(
        Credentials credentials, string characterName, string profession, string origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        try
        {
            await RegisterAsync(credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthException e) when (e.Code == AuthErrorCodes.UsernameTaken)
        {
            // Existing account: log in below.
        }
        var session = await LoginAsync(credentials, cancellationToken).ConfigureAwait(false);
        var character = session.Characters.FirstOrDefault(c => string.Equals(c.Name, characterName, StringComparison.OrdinalIgnoreCase))
            ?? await session.CreateCharacterAsync(new CreateCharacterRequest(characterName, profession, origin), cancellationToken).ConfigureAwait(false);
        return await session.PlayAsync(character.Id, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<T> GetAsync<T>(string path, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<T> PostAsync<T>(string path, object body, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType()),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            AuthError? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<AuthError>(cancellationToken).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                // No error body (e.g. a 429 from the rate limiter).
            }
            throw new AuthException(response.StatusCode, error);
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
            ?? throw new AuthException(response.StatusCode, new AuthError("empty", "Empty response from auth."));
    }
}

/// <summary>A logged-in account: list and create characters, then play one.</summary>
public sealed class AccountSession
{
    private readonly AuthClient _auth;

    internal AccountSession(AuthClient auth, LoginResponse login)
    {
        _auth = auth;
        AccountId = login.AccountId;
        Token = login.SessionToken;
        Characters = login.Characters;
    }

    public ulong AccountId { get; }

    /// <summary>The characters as of login or the last <see cref="RefreshAsync"/>.</summary>
    public IReadOnlyList<CharacterSummary> Characters { get; private set; }

    private string Token { get; }

    public async Task<IReadOnlyList<CharacterSummary>> RefreshAsync(CancellationToken cancellationToken = default) =>
        Characters = await _auth.GetAsync<List<CharacterSummary>>("v1/characters", Token, cancellationToken).ConfigureAwait(false);

    public async Task<CharacterSummary> CreateCharacterAsync(CreateCharacterRequest request, CancellationToken cancellationToken = default)
    {
        var created = await _auth.PostAsync<CharacterSummary>("v1/characters", request, Token, cancellationToken).ConfigureAwait(false);
        Characters = [.. Characters, created];
        return created;
    }

    public Task<PlayResponse> PlayAsync(ulong characterId, CancellationToken cancellationToken = default) =>
        _auth.PostAsync<PlayResponse>("v1/play", new PlayRequest(characterId), Token, cancellationToken);
}
