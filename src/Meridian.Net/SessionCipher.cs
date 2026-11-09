using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Meridian.Net;

/// <summary>Which side sealed a packet. Part of the nonce, so the two directions never share one.</summary>
public enum PeerRole : byte
{
    Client = 0,
    Server = 1,
}

/// <summary>
/// Seals and opens game packets with ChaCha20-Poly1305 under a per-session key.
/// </summary>
/// <remarks>
/// <para>Sealed layout: <c>[u64 counter][ciphertext][16-byte tag]</c>, 24 bytes of overhead.
/// The nonce is <c>[u32 sender role][u64 counter]</c>; counters only go up, so a (key, nonce)
/// pair is never reused.</para>
/// <para>Replays are rejected with a 64-packet sliding window, which tolerates the reordering
/// that unreliable channels produce.</para>
/// <para>One instance per connection, used from one thread.</para>
/// </remarks>
public sealed class SessionCipher : IDisposable
{
    public const int KeyBytes = 32;
    public const int CounterBytes = 8;
    public const int TagBytes = 16;
    public const int Overhead = CounterBytes + TagBytes;

    private readonly ChaCha20Poly1305 _aead;
    private readonly PeerRole _self;
    private readonly PeerRole _remote;
    private ulong _sendCounter;
    private ReplayWindow _replay;

    public SessionCipher(ReadOnlySpan<byte> key, PeerRole self)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"Session key must be {KeyBytes} bytes.", nameof(key));
        }
        _aead = new ChaCha20Poly1305(key);
        _self = self;
        _remote = self == PeerRole.Client ? PeerRole.Server : PeerRole.Client;
    }

    /// <summary>Packets dropped because they failed authentication or were replays.</summary>
    public long Rejected { get; private set; }

    /// <summary>Seals <paramref name="plaintext"/> into <paramref name="destination"/>; returns the bytes written.</summary>
    public int Seal(ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        var total = plaintext.Length + Overhead;
        if (destination.Length < total)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }
        var counter = ++_sendCounter;
        BinaryPrimitives.WriteUInt64LittleEndian(destination, counter);
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(nonce, _self, counter);
        _aead.Encrypt(
            nonce,
            plaintext,
            destination.Slice(CounterBytes, plaintext.Length),
            destination.Slice(CounterBytes + plaintext.Length, TagBytes));
        return total;
    }

    /// <summary>
    /// Opens a sealed packet into <paramref name="destination"/>. Returns the plaintext length,
    /// or -1 if the packet is malformed, forged, or a replay. Never throws for bad input.
    /// </summary>
    public int Open(ReadOnlySpan<byte> sealedPacket, Span<byte> destination)
    {
        var length = sealedPacket.Length - Overhead;
        if (length < 0 || destination.Length < length)
        {
            Rejected++;
            return -1;
        }
        var counter = BinaryPrimitives.ReadUInt64LittleEndian(sealedPacket);
        if (!_replay.CanAccept(counter))
        {
            Rejected++;
            return -1;
        }
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(nonce, _remote, counter);
        try
        {
            _aead.Decrypt(
                nonce,
                sealedPacket.Slice(CounterBytes, length),
                sealedPacket.Slice(CounterBytes + length, TagBytes),
                destination[..length]);
        }
        catch (AuthenticationTagMismatchException)
        {
            Rejected++;
            return -1;
        }
        // Only mark as seen after authentication, so forged counters can't poison the window.
        _replay.Accept(counter);
        return length;
    }

    public void Dispose() => _aead.Dispose();

    private static void MakeNonce(Span<byte> nonce, PeerRole sender, ulong counter)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, (uint)sender);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }
}

/// <summary>Sliding-window replay filter over 64-bit packet counters (RFC 6479 style, 64 wide).</summary>
public struct ReplayWindow
{
    private const int Width = 64;
    private ulong _highest;
    private ulong _seen; // bit i set = counter (_highest - i) seen

    /// <summary>True if <paramref name="counter"/> is new and not too old. Does not record it.</summary>
    public readonly bool CanAccept(ulong counter)
    {
        if (counter == 0)
        {
            return false; // senders start at 1
        }
        if (counter > _highest)
        {
            return true;
        }
        var age = _highest - counter;
        return age < Width && (_seen & (1UL << (int)age)) == 0;
    }

    /// <summary>Records <paramref name="counter"/>. Call only after <see cref="CanAccept"/> returned true.</summary>
    public void Accept(ulong counter)
    {
        if (counter > _highest)
        {
            var shift = counter - _highest;
            _seen = shift >= Width ? 0 : _seen << (int)shift;
            _seen |= 1;
            _highest = counter;
        }
        else
        {
            _seen |= 1UL << (int)(_highest - counter);
        }
    }
}
