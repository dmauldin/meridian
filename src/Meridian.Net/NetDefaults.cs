using LiteNetLib;
using Meridian.Wire;

namespace Meridian.Net;

/// <summary>Shared transport settings and the mapping from <see cref="Delivery"/> to LiteNetLib channels.</summary>
public static class NetDefaults
{
    public const int GatewayPort = 7777;
    public const int ZonePort = 7800;
    public const int AuthPort = 5080;

    /// <summary>One channel per <see cref="Delivery"/> value.</summary>
    public const byte ChannelCount = 3;

    /// <summary>
    /// Largest plaintext a client may send in one packet. Keeps everything inside one UDP datagram
    /// after encryption and LiteNetLib headers, so the gateway never reassembles client data.
    /// </summary>
    public const int MaxClientPacketBytes = 1_200;

    public static (byte Channel, DeliveryMethod Method) Map(Delivery delivery) => delivery switch
    {
        Delivery.UnreliableSequenced => (0, DeliveryMethod.Sequenced),
        Delivery.ReliableOrdered => (1, DeliveryMethod.ReliableOrdered),
        Delivery.ReliableUnordered => (2, DeliveryMethod.ReliableUnordered),
        _ => throw new ArgumentOutOfRangeException(nameof(delivery), delivery, null),
    };

    /// <summary>Applies the settings every Meridian NetManager uses.</summary>
    public static NetManager Configure(NetManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        manager.ChannelsCount = ChannelCount;
        manager.AutoRecycle = true;
        manager.UpdateTime = 5;
        manager.DisconnectTimeout = 10_000;
        return manager;
    }
}
