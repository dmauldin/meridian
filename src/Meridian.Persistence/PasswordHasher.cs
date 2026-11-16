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
/// New hashes are Argon2id (RFC 9106; libsodium through Geralt):
/// <c>$argon2id$v=19$m=KiB,t=passes,p=1$salt$hash</c>. Hashes from before Argon2id,
/// <c>$pbkdf2-sha256$i=N$salt$hash</c>, still verify and always ask to be replaced.
///
/// Each check holds <see cref="MemoryKib"/> of memory while it runs, so callers bound how many
/// run at once (the auth service does).
/// </summary>
public sealed class PasswordHasher
{
    /// <summary>
    /// 64 MiB and 3 passes: RFC 9106's second recommended setting (its first wants 2 GiB), and
    /// well above OWASP's floor of 19 MiB with 2 passes. About 90 ms on one core of the build machine.
    /// </summary>
    public const int DefaultMemoryKib = 65_536;

    public const int DefaultPasses = 3;

    /// <summary>Bounds the work an attacker can ask for: longer passwords are refused, not hashed.</summary>
    public const int MaxPasswordBytes = 256;

    /// <summary>libsodium's floor.</summary>
    public const int MinMemoryKib = 8;

    /// <summary>The most a stored hash may ask for. A hash that wants more is refused, not computed.</summary>
    public const int MaxMemoryKib = 262_144;

    public const int MaxPasses = 16;

    private const string Argon2Id = "argon2id";
    private const string Pbkdf2Id = "pbkdf2-sha256";
    private const int SaltBytes = 16;   // libsodium takes exactly 16
    private const int HashBytes = 32;
    private const int MaxPbkdf2Iterations = 10_000_000;

    public PasswordHasher(int memoryKib = DefaultMemoryKib, int passes = DefaultPasses)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryKib, MinMemoryKib);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(memoryKib, MaxMemoryKib);
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(passes, MaxPasses);
        MemoryKib = memoryKib;
        Passes = passes;
    }

    public int MemoryKib { get; }

    public int Passes { get; }

    /// <summary>True if <paramref name="password"/> can be hashed at all (non-empty, within the byte limit).</summary>
    public static bool IsAcceptableLength(string? password) =>
        !string.IsNullOrEmpty(password) && Encoding.UTF8.GetByteCount(password) <= MaxPasswordBytes;

    public string Hash(string password)
    {
        var bytes = PasswordBytes(password) ?? throw new ArgumentException("Password is empty or too long.", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = new byte[HashBytes];
        Geralt.Argon2id.DeriveKey(hash, bytes, salt, Passes, MemoryKib * 1024);
        CryptographicOperations.ZeroMemory(bytes);
        return string.Create(CultureInfo.InvariantCulture, $"${Argon2Id}$v=19$m={MemoryKib},t={Passes},p=1${B64(salt)}${B64(hash)}");
    }

    /// <summary>Checks a password against a stored hash in time independent of where they differ.</summary>
    public PasswordCheck Verify(string? password, string? stored)
    {
        var bytes = PasswordBytes(password);
        if (bytes is null)
        {
            return PasswordCheck.Invalid;
        }
        try
        {
            if (TryParseArgon2(stored, out var memoryKib, out var passes, out var salt, out var expected))
            {
                var actual = new byte[expected.Length];
                Geralt.Argon2id.DeriveKey(actual, bytes, salt, passes, memoryKib * 1024);
                return !CryptographicOperations.FixedTimeEquals(actual, expected) ? PasswordCheck.Invalid
                    : memoryKib < MemoryKib || passes < Passes ? PasswordCheck.ValidNeedsRehash
                    : PasswordCheck.Valid;
            }
            if (TryParsePbkdf2(stored, out var iterations, out salt, out expected))
            {
                var actual = Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected) ? PasswordCheck.ValidNeedsRehash : PasswordCheck.Invalid;
            }
            return PasswordCheck.Invalid;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Burns the same work as a real check. Call it when the account does not exist, so response
    /// time does not reveal which usernames are registered.
    /// </summary>
    public void VerifyNothing(string? password) =>
        Verify(password ?? string.Empty, string.Create(CultureInfo.InvariantCulture,
            $"${Argon2Id}$v=19$m={MemoryKib},t={Passes},p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));

    private static byte[]? PasswordBytes(string? password) =>
        IsAcceptableLength(password) ? Encoding.UTF8.GetBytes(password!) : null;

    private static bool TryParseArgon2(string? stored, out int memoryKib, out int passes, out byte[] salt, out byte[] hash)
    {
        memoryKib = passes = 0;
        salt = hash = [];
        // "", id, "v=19", "m=..,t=..,p=1", salt, hash. Only what this build writes: version 19,
        // one lane (libsodium has no others), a 16-byte salt.
        var parts = stored?.Split('$');
        if (parts is not ["", Argon2Id, "v=19", var cost, var saltText, var hashText]
            || cost.Split(',') is not [var m, var t, "p=1"]
            || !TryCost(m, "m=", MinMemoryKib, MaxMemoryKib, out memoryKib)
            || !TryCost(t, "t=", 1, MaxPasses, out passes)
            || !TryB64(saltText, out salt) || salt.Length != SaltBytes
            || !TryB64(hashText, out hash) || hash.Length is < 16 or > 64)
        {
            return false;
        }
        return true;
    }

    private static bool TryParsePbkdf2(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = [];
        // "", id, "i=N", salt, hash
        var parts = stored?.Split('$');
        if (parts is not ["", Pbkdf2Id, var cost, var saltText, var hashText]
            || !TryCost(cost, "i=", 1, MaxPbkdf2Iterations, out iterations)
            || !TryB64(saltText, out salt) || salt.Length is < 8 or > 64
            || !TryB64(hashText, out hash) || hash.Length is < 16 or > 64)
        {
            return false;
        }
        return true;
    }

    /// <summary>Parses "<paramref name="key"/>N" with N in range: digits only, no sign or spaces.</summary>
    private static bool TryCost(string text, string key, int min, int max, out int value)
    {
        value = 0;
        return text.StartsWith(key, StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(key.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max;
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
