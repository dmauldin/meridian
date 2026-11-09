namespace Meridian.Net;

/// <summary>
/// Fixed secrets for local development only. Every service prints a warning when it falls back
/// to these; production sets MERIDIAN_INTERNAL_KEY and MERIDIAN_TICKET_KEY.
/// </summary>
public static class DevSecrets
{
    private const string InternalKey = "meridian-dev-internal-key";

    // 32 bytes, base64. Public in the repo on purpose: never use outside development.
    private const string TicketKeyBase64 = "bWVyaWRpYW4tZGV2LXRpY2tldC1rZXktMzJieXRlcyE=";

    public static string InternalKeyWithWarning(TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.WriteLine("WARNING: MERIDIAN_INTERNAL_KEY not set; using the public development key.");
        return InternalKey;
    }

    public static byte[] TicketKeyWithWarning(TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.WriteLine("WARNING: MERIDIAN_TICKET_KEY not set; using the public development key.");
        return Convert.FromBase64String(TicketKeyBase64);
    }

    /// <summary>Reads a base64 32-byte key from <paramref name="variable"/>, or the dev key with a warning.</summary>
    public static byte[] TicketKeyFromEnvironment(string variable, TextWriter log)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value))
        {
            return TicketKeyWithWarning(log);
        }
        var key = Convert.FromBase64String(value);
        if (key.Length != TicketSealer.KeyBytes)
        {
            throw new InvalidOperationException($"{variable} must be {TicketSealer.KeyBytes} bytes of base64.");
        }
        return key;
    }
}
