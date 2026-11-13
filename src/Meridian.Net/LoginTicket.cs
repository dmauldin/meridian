using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Meridian.Net;

/// <summary>What the auth service vouches for. Opaque to the client.</summary>
/// <param name="TicketId">Random id; the gateway accepts each ticket once.</param>
/// <param name="AccountId">The authenticated account.</param>
/// <param name="CharacterName">Display name for M0 (characters arrive with persistence).</param>
/// <param name="SessionKey">32-byte key the client also received over TLS.</param>
/// <param name="ExpiresAt">Unix seconds after which the gateway refuses the ticket.</param>
/// <param name="Profession">The character's profession id (sealed so the client cannot change it).</param>
/// <param name="Origin">The character's origin id.</param>
public sealed record LoginTicket(ulong TicketId, ulong AccountId, string CharacterName, byte[] SessionKey, long ExpiresAt, byte Profession, byte Origin);

/// <summary>
/// Seals tickets with a key shared only by auth and gateway (ChaCha20-Poly1305, random nonce).
/// The client carries the sealed ticket to the gateway but can neither read nor alter it.
/// </summary>
public sealed class TicketSealer : IDisposable
{
    public const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxNameBytes = 32;

    /// <summary>ticket id, account, expiry (8 each), profession, origin, name length (1 each).</summary>
    private const int HeaderBytes = 8 + 8 + 8 + 1 + 1 + 1;

    private readonly ChaCha20Poly1305 _aead;

    public TicketSealer(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"Ticket key must be {KeyBytes} bytes.", nameof(key));
        }
        _aead = new ChaCha20Poly1305(key);
    }

    public byte[] Seal(LoginTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var name = Encoding.UTF8.GetBytes(ticket.CharacterName);
        if (name.Length > MaxNameBytes || ticket.SessionKey.Length != SessionCipher.KeyBytes)
        {
            throw new ArgumentException("Ticket fields out of range.", nameof(ticket));
        }

        var plain = new byte[HeaderBytes + name.Length + SessionCipher.KeyBytes];
        var p = plain.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(p, ticket.TicketId);
        BinaryPrimitives.WriteUInt64LittleEndian(p[8..], ticket.AccountId);
        BinaryPrimitives.WriteInt64LittleEndian(p[16..], ticket.ExpiresAt);
        p[24] = ticket.Profession;
        p[25] = ticket.Origin;
        p[26] = (byte)name.Length;
        name.CopyTo(p[HeaderBytes..]);
        ticket.SessionKey.CopyTo(p[(HeaderBytes + name.Length)..]);

        var sealedTicket = new byte[NonceBytes + plain.Length + TagBytes];
        var s = sealedTicket.AsSpan();
        RandomNumberGenerator.Fill(s[..NonceBytes]);
        _aead.Encrypt(s[..NonceBytes], plain, s.Slice(NonceBytes, plain.Length), s[^TagBytes..]);
        CryptographicOperations.ZeroMemory(plain);
        return sealedTicket;
    }

    /// <summary>Opens a sealed ticket. Returns null if it is forged, corrupt or malformed.</summary>
    public LoginTicket? TryOpen(ReadOnlySpan<byte> sealedTicket)
    {
        var length = sealedTicket.Length - NonceBytes - TagBytes;
        if (length < HeaderBytes + SessionCipher.KeyBytes)
        {
            return null;
        }
        var plain = new byte[length];
        try
        {
            _aead.Decrypt(sealedTicket[..NonceBytes], sealedTicket.Slice(NonceBytes, length), sealedTicket[^TagBytes..], plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }

        var p = plain.AsSpan();
        int nameLength = p[26];
        if (HeaderBytes + nameLength + SessionCipher.KeyBytes != length)
        {
            return null;
        }
        var ticket = new LoginTicket(
            BinaryPrimitives.ReadUInt64LittleEndian(p),
            BinaryPrimitives.ReadUInt64LittleEndian(p[8..]),
            Encoding.UTF8.GetString(p.Slice(HeaderBytes, nameLength)),
            p.Slice(HeaderBytes + nameLength, SessionCipher.KeyBytes).ToArray(),
            BinaryPrimitives.ReadInt64LittleEndian(p[16..]),
            p[24],
            p[25]);
        CryptographicOperations.ZeroMemory(plain);
        return ticket;
    }

    public void Dispose() => _aead.Dispose();
}

/// <summary>Remembers ticket ids until they expire so each ticket works once.</summary>
public sealed class UsedTicketSet
{
    private readonly Dictionary<ulong, long> _used = [];
    private long _lastSweep;

    /// <summary>Records the ticket. Returns false if it was already used.</summary>
    public bool TryUse(LoginTicket ticket, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (nowUnix - _lastSweep > 60)
        {
            _lastSweep = nowUnix;
            foreach (var (id, expiry) in _used.ToArray())
            {
                if (expiry < nowUnix)
                {
                    _used.Remove(id);
                }
            }
        }
        return _used.TryAdd(ticket.TicketId, ticket.ExpiresAt);
    }
}
