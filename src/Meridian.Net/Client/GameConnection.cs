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
            DisconnectReason ??= ExplainDisconnect(info);
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
    public void Connect(string host, int port, PlayResponse login)
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
        var (channel, method) = NetDefaults.Map(T.Delivery);
        var length = _cipher.Seal(channel, plain, _sealed);
        _peer.Send(_sealed, 0, length, channel, method);
    }

    /// <summary>Ends the session for a client-side reason (shown to the player as <see cref="DisconnectReason"/>).</summary>
    public void Abort(string reason)
    {
        DisconnectReason ??= reason;
        Disconnect();
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

    /// <summary>
    /// Turns LiteNetLib's disconnect info into a message for the player. Rejections carry a plain
    /// UTF-8 reason (no session exists yet); kicks carry a sealed <see cref="Disconnecting"/>.
    /// </summary>
    private string ExplainDisconnect(DisconnectInfo info)
    {
        var data = info.AdditionalData is null ? default : info.AdditionalData.GetRemainingBytesSpan();
        if (data.IsEmpty)
        {
            return info.Reason.ToString();
        }
        if (info.Reason == LiteNetLib.DisconnectReason.ConnectionRejected)
        {
            return System.Text.Encoding.UTF8.GetString(data);
        }
        var length = _cipher?.Open(SessionCipher.DisconnectStream, data, _plain) ?? -1;
        if (length > 0)
        {
            try
            {
                return Envelope.Decode<Disconnecting>(_plain.AsSpan(0, length)).Reason ?? info.Reason.ToString();
            }
            catch (WireFormatException)
            {
            }
        }
        return info.Reason.ToString();
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var data = reader.GetRemainingBytesSpan();
        PacketsReceived++;
        BytesReceived += data.Length;
        var length = _cipher!.Open(channel, data, _plain);
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
