using System.Buffers.Binary;
using System.Text;

namespace Meridian.Wire;

/// <summary>
/// Reads what <see cref="WireWriter"/> wrote. Every read is bounds-checked and every length is
/// checked against its schema limit before anything is allocated, because the input comes from
/// untrusted clients.
/// </summary>
public ref struct WireReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public WireReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public readonly int Position => _position;

    public readonly int Remaining => _buffer.Length - _position;

    public byte ReadU8() => Take(1)[0];

    public bool ReadBool() => ReadU8() switch
    {
        0 => false,
        1 => true,
        var b => throw new WireFormatException($"Invalid bool byte {b}."),
    };

    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short ReadI16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int ReadI32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public ulong ReadU64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public long ReadI64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public uint ReadVarU32()
    {
        uint result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = ReadU8();
            if (shift == 28 && b > 0x0F)
            {
                throw new WireFormatException("Varint overflows 32 bits.");
            }
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }
        throw new WireFormatException("Varint too long.");
    }

    public int ReadVarI32()
    {
        var u = ReadVarU32();
        return (int)(u >> 1) ^ -(int)(u & 1);
    }

    public string ReadString(int maxBytes)
    {
        var length = ReadLength(maxBytes, "string");
        try
        {
            return StrictUtf8.GetString(Take(length));
        }
        catch (DecoderFallbackException e)
        {
            throw new WireFormatException("Invalid UTF-8 in string field.", e);
        }
    }

    public byte[] ReadBytes(int maxBytes) => Take(ReadLength(maxBytes, "byte field")).ToArray();

    /// <summary>Reads a list length and rejects it before allocation if it exceeds the schema limit.</summary>
    public int ReadCount(int maxCount) => ReadLength(maxCount, "list");

    /// <summary>Throws unless every byte was consumed. Trailing garbage is a protocol violation.</summary>
    public readonly void EnsureEnd()
    {
        if (_position != _buffer.Length)
        {
            throw new WireFormatException($"{Remaining} unexpected trailing bytes.");
        }
    }

    private int ReadLength(int max, string what)
    {
        var n = ReadVarU32();
        if (n > (uint)max)
        {
            throw new WireFormatException($"{what} length {n} exceeds schema limit {max}.");
        }
        if (n > (uint)Remaining)
        {
            throw new WireFormatException($"{what} length {n} exceeds remaining {Remaining} bytes.");
        }
        return (int)n;
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n > Remaining)
        {
            throw new WireFormatException($"Read of {n} bytes past end ({_position}/{_buffer.Length}).");
        }
        var span = _buffer.Slice(_position, n);
        _position += n;
        return span;
    }
}
