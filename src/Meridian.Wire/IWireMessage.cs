namespace Meridian.Wire;

/// <summary>Who sends a message. Generated handler interfaces are split by direction.</summary>
public enum Direction : byte
{
    /// <summary>Client to server.</summary>
    ServerBound,

    /// <summary>Server to client.</summary>
    ClientBound,

    /// <summary>Between server processes (gateway ↔ zone). Never accepted from a client.</summary>
    Internal,

    /// <summary>Connection setup data, read by the transport before a session exists. Never dispatched.</summary>
    Handshake,
}

/// <summary>How a message should be delivered. Mapped onto transport channels by Meridian.Net.</summary>
public enum Delivery : byte
{
    /// <summary>Latest-wins state such as movement and snapshots.</summary>
    UnreliableSequenced,

    /// <summary>Events whose order matters: combat, inventory, stats.</summary>
    ReliableOrdered,

    /// <summary>Must arrive, order irrelevant: chat, UI data.</summary>
    ReliableUnordered,
}

/// <summary>Implemented by every generated struct or message.</summary>
public interface IWireStruct<TSelf>
    where TSelf : IWireStruct<TSelf>
{
    void Write(ref WireWriter writer);

    static abstract TSelf Read(ref WireReader reader);
}

/// <summary>Implemented by every generated top-level message.</summary>
public interface IWireMessage<TSelf> : IWireStruct<TSelf>
    where TSelf : IWireMessage<TSelf>
{
    static abstract ushort MessageId { get; }

    static abstract Direction Direction { get; }

    static abstract Delivery Delivery { get; }
}

/// <summary>Packet framing: a u16 message id followed by the message body.</summary>
public static class Envelope
{
    /// <summary>Largest packet the game will build. Unreliable packets must also fit the transport MTU.</summary>
    public const int MaxPacketBytes = 16 * 1024;

    /// <summary>Writes the id and body of <paramref name="message"/>.</summary>
    public static void Write<T>(ref WireWriter writer, in T message)
        where T : struct, IWireMessage<T>
    {
        writer.WriteU16(T.MessageId);
        message.Write(ref writer);
    }

    /// <summary>Encodes <paramref name="message"/> into <paramref name="buffer"/> and returns the bytes used.</summary>
    public static ReadOnlySpan<byte> Encode<T>(Span<byte> buffer, in T message)
        where T : struct, IWireMessage<T>
    {
        var w = new WireWriter(buffer);
        Write(ref w, message);
        return buffer[..w.Position];
    }

    /// <summary>Encodes into a new array. Convenient for tests and cold paths.</summary>
    public static byte[] ToArray<T>(in T message)
        where T : struct, IWireMessage<T>
    {
        Span<byte> buffer = stackalloc byte[MaxPacketBytes];
        return Encode(buffer, message).ToArray();
    }

    /// <summary>Reads the message id without consuming the packet.</summary>
    public static ushort PeekId(ReadOnlySpan<byte> packet) =>
        packet.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet) :
        throw new WireFormatException("Packet shorter than its message id.");

    /// <summary>Decodes a packet that must hold exactly one <typeparamref name="T"/>.</summary>
    public static T Decode<T>(ReadOnlySpan<byte> packet)
        where T : struct, IWireMessage<T>
    {
        var r = new WireReader(packet);
        var id = r.ReadU16();
        if (id != T.MessageId)
        {
            throw new WireFormatException($"Expected message {T.MessageId} ({typeof(T).Name}), got {id}.");
        }
        var m = T.Read(ref r);
        r.EnsureEnd();
        return m;
    }
}
