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
/// The nonce is <c>[u8 sender role][u8 stream][u16 zero][u64 counter]</c>. Each stream (one per
/// transport channel, plus one for disconnect data) has its own counter, so a (key, nonce) pair
/// is never reused, and a packet cannot be moved to another stream without failing
/// authentication.</para>
/// <para>Each stream has its own replay window. Per-stream counters matter for reliable
/// channels: the transport resends a lost packet with its original bytes, possibly after many
/// later packets. With one shared counter that resend would look ancient and be rejected as a
/// replay, and the transport, having seen it acknowledged, would never send it again. Within one
/// stream, reliable-ordered packets reach us in order and reliable-unordered ones within the
/// transport's window, which <see cref="ReplayWindow.Width"/> covers.</para>
/// <para>One instance per connection, used from one thread.</para>
/// </remarks>
public sealed class SessionCipher : IDisposable
{
    public const int KeyBytes = 32;
    public const int CounterBytes = 8;
    public const int TagBytes = 16;
    public const int Overhead = CounterBytes + TagBytes;

    /// <summary>Stream ids 0..N-1 are transport channels; this one seals disconnect messages.</summary>
    public const byte DisconnectStream = 255;

    private const int StreamCount = 256;

    private readonly ChaCha20Poly1305 _aead;
    private readonly PeerRole _self;
    private readonly PeerRole _remote;
    private readonly ulong[] _sendCounters = new ulong[StreamCount];
    private readonly ReplayWindow?[] _replay = new ReplayWindow?[StreamCount];

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

    /// <summary>Seals <paramref name="plaintext"/> for <paramref name="stream"/>; returns the bytes written.</summary>
    public int Seal(byte stream, ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        var total = plaintext.Length + Overhead;
        if (destination.Length < total)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }
        var counter = ++_sendCounters[stream];
        BinaryPrimitives.WriteUInt64LittleEndian(destination, counter);
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(nonce, _self, stream, counter);
        _aead.Encrypt(
            nonce,
            plaintext,
            destination.Slice(CounterBytes, plaintext.Length),
            destination.Slice(CounterBytes + plaintext.Length, TagBytes));
        return total;
    }

    /// <summary>
    /// Opens a packet that arrived on <paramref name="stream"/>. Returns the plaintext length, or
    /// -1 if it is malformed, forged, from another stream, or a replay. Never throws for bad input.
    /// </summary>
    public int Open(byte stream, ReadOnlySpan<byte> sealedPacket, Span<byte> destination)
    {
        var length = sealedPacket.Length - Overhead;
        if (length < 0 || destination.Length < length)
        {
            Rejected++;
            return -1;
        }
        var counter = BinaryPrimitives.ReadUInt64LittleEndian(sealedPacket);
        var window = _replay[stream] ??= new ReplayWindow();
        if (!window.CanAccept(counter))
        {
            Rejected++;
            return -1;
        }
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(nonce, _remote, stream, counter);
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
        window.Accept(counter);
        return length;
    }

    public void Dispose() => _aead.Dispose();

    private static void MakeNonce(Span<byte> nonce, PeerRole sender, byte stream, ulong counter)
    {
        nonce[0] = (byte)sender;
        nonce[1] = stream;
        nonce[2] = 0;
        nonce[3] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }
}

/// <summary>Sliding-window replay filter over 64-bit packet counters (RFC 6479 style).</summary>
public sealed class ReplayWindow
{
    /// <summary>How far behind the newest counter a packet may still arrive.</summary>
    public const int Width = 1_024;

    private const int Words = Width / 64;
    private readonly ulong[] _seen = new ulong[Words];
    private ulong _highest;

    /// <summary>True if <paramref name="counter"/> is new and not too old. Does not record it.</summary>
    public bool CanAccept(ulong counter)
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
        return age < Width && !IsSet(counter);
    }

    /// <summary>Records <paramref name="counter"/>. Call only after <see cref="CanAccept"/> returned true.</summary>
    public void Accept(ulong counter)
    {
        if (counter > _highest)
        {
            // Clear the bits for counters we skipped over (they were never seen).
            var gap = counter - _highest;
            if (gap >= Width)
            {
                Array.Clear(_seen);
            }
            else
            {
                for (var c = _highest + 1; c < counter; c++)
                {
                    Clear(c);
                }
            }
            _highest = counter;
        }
        Set(counter);
    }

    private bool IsSet(ulong c) => (_seen[(int)(c / 64 % Words)] & (1UL << (int)(c % 64))) != 0;

    private void Set(ulong c) => _seen[(int)(c / 64 % Words)] |= 1UL << (int)(c % 64);

    private void Clear(ulong c) => _seen[(int)(c / 64 % Words)] &= ~(1UL << (int)(c % 64));
}
