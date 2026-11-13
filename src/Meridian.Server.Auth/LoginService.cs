using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Meridian.Net;
using Meridian.Core.Content;
using Meridian.Net.Client;

namespace Meridian.Server.Auth;

public sealed record AuthOptions
{
    /// <summary>Gateway address handed to clients.</summary>
    public string PublicGatewayHost { get; init; } = "127.0.0.1";

    public int PublicGatewayPort { get; init; } = NetDefaults.GatewayPort;

    /// <summary>How long a ticket stays valid. Short: clients connect right after login.</summary>
    public TimeSpan TicketLifetime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// M0 has no account database: any valid username logs in with this password.
    /// Replaced by real accounts (Postgres, hashed passwords) in M2.
    /// </summary>
    public required string DevPassword { get; init; }
}

/// <summary>Issues session keys and sealed tickets. Thread-safe; ASP.NET calls it concurrently.</summary>
public sealed partial class LoginService(AuthOptions options, TicketSealer sealer, TimeProvider clock, ContentPack content)
{
    public const string DefaultProfession = "Operative";
    public const string DefaultOrigin = "Baseline";

    private readonly ConcurrentDictionary<string, ulong> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private long _nextAccountId;
    private readonly Lock _sealLock = new();

    /// <summary>Returns a login response, or null if the credentials are rejected.</summary>
    public LoginResponse? TryLogin(LoginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Username is null || !UsernamePattern().IsMatch(request.Username))
        {
            return null;
        }
        var expected = System.Text.Encoding.UTF8.GetBytes(options.DevPassword);
        var given = System.Text.Encoding.UTF8.GetBytes(request.Password ?? string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(expected, given))
        {
            return null;
        }

        if (!TryChooseProfession(request.Profession, out var profession) || !TryChooseOrigin(request.Origin, out var origin))
        {
            return null;
        }

        var accountId = _accounts.GetOrAdd(request.Username, _ => (ulong)Interlocked.Increment(ref _nextAccountId));
        var sessionKey = RandomNumberGenerator.GetBytes(SessionCipher.KeyBytes);
        var ticket = new LoginTicket(
            BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)),
            accountId,
            request.Username,
            sessionKey,
            clock.GetUtcNow().Add(options.TicketLifetime).ToUnixTimeSeconds(),
            (byte)profession,
            (byte)origin);

        byte[] sealedTicket;
        lock (_sealLock)
        {
            // ChaCha20Poly1305 instances are not safe for concurrent use.
            sealedTicket = sealer.Seal(ticket);
        }

        return new LoginResponse(
            accountId,
            options.PublicGatewayHost,
            options.PublicGatewayPort,
            Convert.ToBase64String(sessionKey),
            Convert.ToBase64String(sealedTicket));
    }

    // Development-only character choice until characters are stored (M2.3): only what content
    // defines is playable; names are matched case-insensitively against content keys.
    private bool TryChooseProfession(string? requested, out int id)
    {
        var found = content.Professions.TryGet(KeyOf(requested, DefaultProfession), out var def);
        id = found ? (int)def!.Id : 0;
        return found;
    }

    private bool TryChooseOrigin(string? requested, out int id)
    {
        var found = content.Origins.TryGet(KeyOf(requested, DefaultOrigin), out var def);
        id = found ? (int)def!.Id : 0;
        return found;
    }

    private static string KeyOf(string? requested, string fallback) =>
        (string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim()).ToLowerInvariant();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{2,23}$")]
    private static partial Regex UsernamePattern();
}
