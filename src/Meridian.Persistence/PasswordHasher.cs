using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Meridian.Persistence;

public enum PasswordCheck
{
    /// <summary>Wrong password, or a hash this build cannot read.</summary>
    Invalid = 0,
    Valid = 1,

    /// <summary>Correct, but hashed with an older algorithm or weaker parameters: store a fresh hash.</summary>
    ValidNeedsRehash = 2,
}

/// <summary>
/// Password hashes as self-describing PHC strings, so the algorithm and its cost can change
/// without a migration: an old hash still verifies, and <see cref="PasswordCheck.ValidNeedsRehash"/>
/// tells the caller to replace it.
///
/// Today: PBKDF2-HMAC-SHA256 (in the .NET base library), <c>$pbkdf2-sha256$i=N$salt$hash</c>.
/// Argon2id (memory-hard, the better choice) replaces it as the preferred algorithm once its
/// package can be restored; existing PBKDF2 hashes then upgrade on login.
/// </summary>
public sealed class PasswordHasher
{
    /// <summary>OWASP's 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Bounds the work an attacker can ask for: longer passwords are refused, not hashed.</summary>
    public const int MaxPasswordBytes = 256;

    private const string Pbkdf2Id = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaxIterations = 10_000_000;

    public PasswordHasher(int iterations = DefaultIterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);
        Iterations = iterations;
    }

    public int Iterations { get; }

    /// <summary>True if <paramref name="password"/> can be hashed at all (non-empty, within the byte limit).</summary>
    public static bool IsAcceptableLength(string? password) =>
        !string.IsNullOrEmpty(password) && Encoding.UTF8.GetByteCount(password) <= MaxPasswordBytes;

    public string Hash(string password)
    {
        var bytes = PasswordBytes(password) ?? throw new ArgumentException("Password is empty or too long.", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(bytes, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        CryptographicOperations.ZeroMemory(bytes);
        return string.Create(CultureInfo.InvariantCulture, $"${Pbkdf2Id}$i={Iterations}${B64(salt)}${B64(hash)}");
    }

    /// <summary>Checks a password against a stored hash in time independent of where they differ.</summary>
    public PasswordCheck Verify(string? password, string? stored)
    {
        var bytes = PasswordBytes(password);
        if (bytes is null || !TryParsePbkdf2(stored, out var iterations, out var salt, out var expected))
        {
            return PasswordCheck.Invalid;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        CryptographicOperations.ZeroMemory(bytes);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordCheck.Invalid;
        }
        return iterations < Iterations ? PasswordCheck.ValidNeedsRehash : PasswordCheck.Valid;
    }

    /// <summary>
    /// Burns the same work as a real check. Call it when the account does not exist, so response
    /// time does not reveal which usernames are registered.
    /// </summary>
    public void VerifyNothing(string? password) =>
        Verify(password ?? string.Empty, $"${Pbkdf2Id}$i={Iterations}$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

    private static byte[]? PasswordBytes(string? password) =>
        IsAcceptableLength(password) ? Encoding.UTF8.GetBytes(password!) : null;

    private static bool TryParsePbkdf2(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = [];
        // "", id, "i=N", salt, hash
        var parts = stored?.Split('$');
        if (parts is not ["", Pbkdf2Id, var cost, var saltText, var hashText]
            || !cost.StartsWith("i=", StringComparison.Ordinal)
            || !int.TryParse(cost.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations is < 1 or > MaxIterations
            || !TryB64(saltText, out salt) || salt.Length is < 8 or > 64
            || !TryB64(hashText, out hash) || hash.Length is < 16 or > 64)
        {
            return false;
        }
        return true;
    }

    // PHC strings use standard base64 without padding.
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryB64(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length is 0 or > 128 || text.Contains('=', StringComparison.Ordinal))
        {
            return false;
        }
        var padded = text + new string('=', (4 - text.Length % 4) % 4);
        var buffer = new byte[padded.Length];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
        {
            return false;
        }
        bytes = buffer[..written];
        return true;
    }
}
