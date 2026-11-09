using LiteNetLib;
using Meridian.Wire;

namespace Meridian.Net.Client;

/// <summary>
/// The client side of a game session: connects to a gateway with a login ticket, seals outgoing
/// messages, opens incoming ones and dispatches them to an <see cref="IClientBoundHandler{TContext}"/>.
/// Used by the headless bots now and by the Godot client in M1.
/// </summary>
/// <remarks>Single-threaded: call <see cref="Poll"/> from the thread that owns the connection.</remarks>
public sealed class GameConnection : IDisposable
{
    private readonly IClientBoundHandler<GameConnection> _handler;
    private readonly NetManager _net;
    private readonly byte[] _plain = new byte[Envelope.MaxPacketBytes];
    private readonly byte[] _sealed = new byte[Envelope.MaxPacketBytes + SessionCipher.Overhead];
    private SessionCipher? _cipher;
    private NetPeer? _peer;

    public GameConnection(IClientBoundHandler<GameConnection> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
        var listener = new EventBasedNetListener();
        listener.PeerConnectedEvent += _ => Connected = true;
        listener.PeerDisconnectedEvent += (_, info) =>
        {
            Connected = false;
            DisconnectReason ??= info.Reason.ToString();
        };
        listener.NetworkReceiveEvent += OnReceive;
        _net = NetDefaults.Configure(new NetManager(listener));
    }

    public bool Connected { get; private set; }

    /// <summary>Set once the connection ends, with LiteNetLib's reason or a protocol error.</summary>
    public string? DisconnectReason { get; private set; }

    public int RoundTripTimeMs => _peer?.RoundTripTime ?? 0;

    public long PacketsReceived { get; private set; }

    public long BytesReceived { get; private set; }

    /// <summary>Starts connecting. Completion shows up as <see cref="Connected"/> after a few polls.</summary>
    public void Connect(string host, int port, LoginResponse login)
    {
        ArgumentNullException.ThrowIfNull(login);
        if (_peer is not null)
        {
            throw new InvalidOperationException("Already connecting or connected.");
        }
        _cipher = new SessionCipher(Convert.FromBase64String(login.SessionKey), PeerRole.Client);
        _net.Start();
        var request = Envelope.ToArray(new ConnectRequest
        {
            SchemaHash = WireSchema.Hash,
            Ticket = Convert.FromBase64String(login.Ticket),
        });
        _peer = _net.Connect(NetUtils.MakeEndPoint(host, port), request);
    }

    /// <summary>Processes network events and dispatches received messages.</summary>
    public void Poll() => _net.PollEvents();

    /// <summary>Seals and sends a client-to-server message.</summary>
    public void Send<T>(in T message)
        where T : struct, IWireMessage<T>
    {
        if (T.Direction != Direction.ServerBound)
        {
            throw new InvalidOperationException($"{typeof(T).Name} is not a server-bound message.");
        }
        if (_peer is null || _cipher is null || !Connected)
        {
            return;
        }
        var plain = Envelope.Encode(_plain, message);
        var length = _cipher.Seal(plain, _sealed);
        var (channel, method) = NetDefaults.Map(T.Delivery);
        _peer.Send(_sealed, 0, length, channel, method);
    }

    public void Disconnect()
    {
        _peer?.Disconnect();
        _net.PollEvents();
    }

    public void Dispose()
    {
        _net.Stop();
        _cipher?.Dispose();
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var data = reader.GetRemainingBytesSpan();
        PacketsReceived++;
        BytesReceived += data.Length;
        var length = _cipher!.Open(data, _plain);
        if (length < 0)
        {
            return; // forged or replayed; counted in the cipher
        }
        try
        {
            WireDispatch.DispatchClientBound(_plain.AsSpan(0, length), _handler, this);
        }
        catch (WireFormatException e)
        {
            DisconnectReason = $"Protocol error from server: {e.Message}";
            peer.Disconnect();
        }
    }
}
