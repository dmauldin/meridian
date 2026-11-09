using System.Net.Http.Json;

namespace Meridian.Net.Client;

/// <summary>Request body for <c>POST /v1/login</c>.</summary>
public sealed record LoginRequest(string Username, string Password);

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
    public async Task<LoginResponse> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri("v1/login", UriKind.Relative), new LoginRequest(username, password), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("Empty login response.");
    }
}
