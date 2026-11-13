using System.Net.Http.Json;

namespace Meridian.Net.Client;

/// <summary>Request body for <c>POST /v1/login</c>.</summary>
/// <param name="Profession">Development only: the profession for a new character (e.g. "Medic").
/// Null picks the default. Replaced by stored characters in M2.3.</param>
/// <param name="Origin">Development only, like <paramref name="Profession"/>.</param>
public sealed record LoginRequest(string Username, string Password, string? Profession = null, string? Origin = null);

/// <summary>Response body for <c>POST /v1/login</c>.</summary>
/// <param name="AccountId">The account the ticket is for.</param>
/// <param name="GatewayHost">Where to connect.</param>
/// <param name="GatewayPort">UDP port of the gateway.</param>
/// <param name="SessionKey">Base64 32-byte key for <see cref="SessionCipher"/>. Keep secret.</param>
/// <param name="Ticket">Base64 sealed ticket to present to the gateway.</param>
public sealed record LoginResponse(ulong AccountId, string GatewayHost, int GatewayPort, string SessionKey, string Ticket);

/// <summary>Talks to the auth service. In production this runs over HTTPS.</summary>
public sealed class AuthClient(HttpClient http)
{
    /// <summary>Logs in. Throws <see cref="HttpRequestException"/> on rejection or network failure.</summary>
    public Task<LoginResponse> LoginAsync(string username, string password, CancellationToken cancellationToken = default) =>
        LoginAsync(new LoginRequest(username, password), cancellationToken);

    /// <summary>Logs in with a full request (including the development profession/origin choice).</summary>
    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri("v1/login", UriKind.Relative), request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("Empty login response.");
    }
}
