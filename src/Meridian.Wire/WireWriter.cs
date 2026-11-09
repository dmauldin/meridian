using System.Buffers.Binary;
using System.Text;

namespace Meridian.Wire;

/// <summary>Thrown when a buffer cannot be read or written as the schema says. Treat as a protocol violation.</summary>
public sealed class WireFormatException : Exception
{
    public WireFormatException()
    {
    }

    public WireFormatException(string message)
        : base(message)
    {
    }

    public WireFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Writes little-endian primitives into a caller-owned buffer. No allocation, no growth:
/// running out of space throws, because an oversized packet is a bug, not a condition to handle.
/// </summary>
public ref struct WireWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    public WireWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public readonly int Position => _position;

    /// <summary>The bytes written so far.</summary>
    public readonly ReadOnlySpan<byte> Written => _buffer[.._position];

    public void WriteU8(byte v) => Take(1)[0] = v;

    public void WriteBool(bool v) => WriteU8(v ? (byte)1 : (byte)0);

    public void WriteU16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);

    public void WriteI16(short v) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), v);

    public void WriteU32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);

    public void WriteI32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);

    public void WriteU64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);

    public void WriteI64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);

    /// <summary>LEB128 unsigned varint: 1 byte below 128, up to 5 bytes for a uint.</summary>
    public void WriteVarU32(uint v)
    {
        while (v >= 0x80)
        {
            WriteU8((byte)(v | 0x80));
            v >>= 7;
        }
        WriteU8((byte)v);
    }

    /// <summary>Zig-zag encoded signed varint, so small negative numbers stay small.</summary>
    public void WriteVarI32(int v) => WriteVarU32((uint)((v << 1) ^ (v >> 31)));

    /// <summary>UTF-8 string with a varint byte-length prefix. <paramref name="maxBytes"/> is a schema limit.</summary>
    public void WriteString(string? value, int maxBytes)
    {
        value ??= string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > maxBytes)
        {
            throw new WireFormatException($"String of {byteCount} bytes exceeds schema limit {maxBytes}.");
        }
        WriteVarU32((uint)byteCount);
        Encoding.UTF8.GetBytes(value, Take(byteCount));
    }

    public void WriteBytes(ReadOnlySpan<byte> value, int maxBytes)
    {
        if (value.Length > maxBytes)
        {
            throw new WireFormatException($"Byte field of {value.Length} bytes exceeds schema limit {maxBytes}.");
        }
        WriteVarU32((uint)value.Length);
        value.CopyTo(Take(value.Length));
    }

    /// <summary>Writes a list length after checking it against the schema limit.</summary>
    public void WriteCount(int count, int maxCount)
    {
        if ((uint)count > (uint)maxCount)
        {
            throw new WireFormatException($"List of {count} items exceeds schema limit {maxCount}.");
        }
        WriteVarU32((uint)count);
    }

    private Span<byte> Take(int n)
    {
        if (_position + n > _buffer.Length)
        {
            throw new WireFormatException($"Write of {n} bytes overflows buffer ({_position}/{_buffer.Length}).");
        }
        var span = _buffer.Slice(_position, n);
        _position += n;
        return span;
    }
}
