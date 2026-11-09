using System.Diagnostics;
using System.Globalization;
using System.Text;
using LiteNetLib;
using Meridian.Net;
using Meridian.Wire;

namespace Meridian.Server.Gateway;

public sealed record GatewayOptions
{
    public int ClientPort { get; init; } = NetDefaults.GatewayPort;

    public string ZoneHost { get; init; } = "127.0.0.1";

    public int ZonePort { get; init; } = NetDefaults.ZonePort;

    public required string InternalKey { get; init; }

    public required byte[] TicketKey { get; init; }

    /// <summary>Sustained packets per second a client may send; bursts up to twice this.</summary>
    public int MaxPacketsPerSecond { get; init; } = 40;

    /// <summary>Failed decryptions tolerated before a client is dropped.</summary>
    public int MaxRejectedPackets { get; init; } = 50;

    public TimeSpan MetricsInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The client-facing edge: validates tickets, decrypts and shape-checks client packets,
/// rate-limits, and relays between clients and the zone. Runs on the thread that calls
/// <see cref="Run"/>.
/// </summary>
public sealed class GatewayHost : IInternalHandler<NetPeer>, IDisposable
{
    private readonly GatewayOptions _options;
    private readonly Action<string> _log;
    private readonly NetManager _clients;
    private readonly NetManager _zones;
    private readonly TicketSealer _tickets;
    private readonly UsedTicketSet _usedTickets = new();
    private readonly Dictionary<int, ClientSession> _byPeer = [];
    private readonly Dictionary<uint, ClientSession> _byConnection = [];
    private readonly byte[] _plain = new byte[Envelope.MaxPacketBytes];
    private readonly byte[] _sealed = new byte[Envelope.MaxPacketBytes + SessionCipher.Overhead];
    private readonly byte[] _internal = new byte[Envelope.MaxPacketBytes + 64];
    private NetPeer? _zone;
    private long _nextZoneAttempt;
    private uint _nextConnectionId = 1;
    private long _packetsIn, _packetsOut, _dropped;

    public GatewayHost(GatewayOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _log = log ?? Console.WriteLine;
        _tickets = new TicketSealer(options.TicketKey);

        var clientEvents = new EventBasedNetListener();
        clientEvents.ConnectionRequestEvent += OnConnectionRequest;
        clientEvents.PeerDisconnectedEvent += OnClientDisconnected;
        clientEvents.NetworkReceiveEvent += OnClientPacket;
        _clients = NetDefaults.Configure(new NetManager(clientEvents));

        var zoneEvents = new EventBasedNetListener();
        zoneEvents.PeerConnectedEvent += p => _log($"connected to zone at {p}");
        zoneEvents.PeerDisconnectedEvent += OnZoneDisconnected;
        zoneEvents.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            try
            {
                WireDispatch.DispatchInternal(reader.GetRemainingBytesSpan(), this, peer);
            }
            catch (WireFormatException e)
            {
                _log($"bad packet from zone: {e.Message}");
            }
        };
        _zones = NetDefaults.Configure(new NetManager(zoneEvents));
    }

    public int Port => _clients.LocalPort;

    public int ClientCount => _byConnection.Count;

    public bool ZoneConnected => _zone?.ConnectionState == ConnectionState.Connected;

    public void Start()
    {
        if (!_clients.Start(_options.ClientPort))
        {
            throw new InvalidOperationException($"Could not bind UDP port {_options.ClientPort}.");
        }
        _zones.Start();
        _log($"gateway listening on udp/{Port}, zone at {_options.ZoneHost}:{_options.ZonePort}");
    }

    public void Run(CancellationToken stop)
    {
        var metricsTicks = (long)(_options.MetricsInterval.TotalSeconds * Stopwatch.Frequency);
        var nextReport = Stopwatch.GetTimestamp() + metricsTicks;
        while (!stop.IsCancellationRequested)
        {
            PollOnce();
            Thread.Sleep(1);
            var now = Stopwatch.GetTimestamp();
            if (now >= nextReport)
            {
                _log(string.Create(CultureInfo.InvariantCulture,
                    $"GATEWAY clients={ClientCount} zone={(ZoneConnected ? "up" : "down")} in={_packetsIn} out={_packetsOut} dropped={_dropped}"));
                nextReport = now + metricsTicks;
            }
        }
        _clients.DisconnectAll();
        _clients.PollEvents();
    }

    /// <summary>One iteration of network processing. Exposed for tests that drive the loop themselves.</summary>
    public void PollOnce()
    {
        EnsureZoneLink();
        _zones.PollEvents();
        _clients.PollEvents();
    }

    public void Dispose()
    {
        _clients.Stop();
        _zones.Stop();
        _tickets.Dispose();
        foreach (var s in _byConnection.Values)
        {
            s.Cipher.Dispose();
        }
    }

    public void OnToClient(NetPeer context, in ToClient message)
    {
        if (!_byConnection.TryGetValue(message.ConnectionId, out var session) || message.Payload is null)
        {
            return; // client left while the packet was in flight
        }
        if (message.DeliveryMode > (byte)Delivery.ReliableUnordered)
        {
            _log($"zone sent unknown delivery mode {message.DeliveryMode}");
            return;
        }
        var length = session.Cipher.Seal(message.Payload, _sealed);
        var (channel, method) = NetDefaults.Map((Delivery)message.DeliveryMode);
        session.Peer.Send(_sealed, 0, length, channel, method);
        _packetsOut++;
    }

    public void OnPlayerJoin(NetPeer context, in PlayerJoin message) => Unexpected(nameof(PlayerJoin));

    public void OnPlayerLeave(NetPeer context, in PlayerLeave message) => Unexpected(nameof(PlayerLeave));

    public void OnFromClient(NetPeer context, in FromClient message) => Unexpected(nameof(FromClient));

    private void Unexpected(string name) => _log($"zone sent {name}, which only flows gateway → zone");

    private void EnsureZoneLink()
    {
        if (_zone is { ConnectionState: ConnectionState.Connected or ConnectionState.Outgoing })
        {
            return;
        }
        var now = Stopwatch.GetTimestamp();
        if (now < _nextZoneAttempt)
        {
            return;
        }
        _nextZoneAttempt = now + Stopwatch.Frequency; // retry once a second
        _zone = _zones.Connect(_options.ZoneHost, _options.ZonePort, _options.InternalKey);
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (!ZoneConnected)
        {
            Reject(request, "Zone unavailable, try again shortly.");
            return;
        }

        ConnectRequest hello;
        try
        {
            hello = Envelope.Decode<ConnectRequest>(request.Data.GetRemainingBytesSpan());
        }
        catch (WireFormatException)
        {
            Reject(request, "Malformed connection request.");
            return;
        }
        if (hello.SchemaHash != WireSchema.Hash)
        {
            Reject(request, "Client version does not match the server. Please update.");
            return;
        }

        var ticket = hello.Ticket is null ? null : _tickets.TryOpen(hello.Ticket);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (ticket is null || ticket.ExpiresAt < nowUnix)
        {
            Reject(request, "Login expired. Please log in again.");
            return;
        }
        if (!_usedTickets.TryUse(ticket, nowUnix))
        {
            Reject(request, "Login already used. Please log in again.");
            return;
        }

        var peer = request.Accept();
        var session = new ClientSession(_nextConnectionId++, peer, ticket, _options.MaxPacketsPerSecond);
        _byPeer[peer.Id] = session;
        _byConnection[session.ConnectionId] = session;
        SendToZone(new PlayerJoin { ConnectionId = session.ConnectionId, AccountId = ticket.AccountId, Name = ticket.CharacterName });
    }

    private void OnClientPacket(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (!_byPeer.TryGetValue(peer.Id, out var session))
        {
            return;
        }
        _packetsIn++;
        var data = reader.GetRemainingBytesSpan();
        if (data.Length > NetDefaults.MaxClientPacketBytes + SessionCipher.Overhead || !session.Bucket.TryTake(Stopwatch.GetTimestamp()))
        {
            _dropped++;
            return;
        }

        var length = session.Cipher.Open(data, _plain);
        if (length < 0)
        {
            _dropped++;
            if (session.Cipher.Rejected > _options.MaxRejectedPackets)
            {
                Kick(session, "Too many invalid packets.");
            }
            return;
        }

        var plain = _plain.AsSpan(0, length);
        try
        {
            // Full decode against the schema: only well-formed, client-legal messages reach the zone.
            WireDispatch.DispatchServerBound(plain, ShapeCheck.Instance, 0);
        }
        catch (WireFormatException e)
        {
            Kick(session, $"Protocol violation: {e.Message}");
            return;
        }
        SendToZone(new FromClient { ConnectionId = session.ConnectionId, Payload = plain.ToArray() });
    }

    private void OnClientDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_byPeer.Remove(peer.Id, out var session))
        {
            return;
        }
        _byConnection.Remove(session.ConnectionId);
        session.Cipher.Dispose();
        SendToZone(new PlayerLeave { ConnectionId = session.ConnectionId });
    }

    private void OnZoneDisconnected(NetPeer peer, DisconnectInfo info)
    {
        _log($"zone link lost ({info.Reason}); disconnecting {ClientCount} clients");
        foreach (var s in _byConnection.Values.ToList())
        {
            Kick(s, "Zone went offline.");
        }
    }

    private void Kick(ClientSession session, string reason)
    {
        var bye = Envelope.Encode(_plain, new Disconnecting { Reason = reason });
        var length = session.Cipher.Seal(bye, _sealed);
        session.Peer.Disconnect(_sealed.AsSpan(0, length));
    }

    private void SendToZone<T>(in T message)
        where T : struct, IWireMessage<T>
    {
        if (_zone is null)
        {
            return;
        }
        var bytes = Envelope.Encode(_internal, message);
        _zone.Send(bytes, 1, DeliveryMethod.ReliableOrdered);
    }

    private static void Reject(ConnectionRequest request, string reason) =>
        request.Reject(Encoding.UTF8.GetBytes(reason));

    /// <summary>Accepts every server-bound message; decoding it is the check.</summary>
    private sealed class ShapeCheck : IServerBoundHandler<int>
    {
        public static readonly ShapeCheck Instance = new();

        public void OnMoveIntent(int context, in MoveIntent message)
        {
        }

        public void OnChatSay(int context, in ChatSay message)
        {
        }

        public void OnPing(int context, in Ping message)
        {
        }
    }
}

/// <summary>Per-client state held by the gateway.</summary>
internal sealed class ClientSession(uint connectionId, NetPeer peer, LoginTicket ticket, int packetsPerSecond)
{
    public uint ConnectionId { get; } = connectionId;

    public NetPeer Peer { get; } = peer;

    public LoginTicket Ticket { get; } = ticket;

    public SessionCipher Cipher { get; } = new(ticket.SessionKey, PeerRole.Server);

    public TokenBucket Bucket { get; } = new(packetsPerSecond, packetsPerSecond * 2);
}

/// <summary>Classic token bucket on Stopwatch timestamps.</summary>
internal sealed class TokenBucket(int ratePerSecond, int burst)
{
    private double _tokens = burst;
    private long _last = Stopwatch.GetTimestamp();

    public bool TryTake(long now)
    {
        var elapsed = (now - _last) / (double)Stopwatch.Frequency;
        _last = now;
        _tokens = Math.Min(burst, _tokens + (elapsed * ratePerSecond));
        if (_tokens < 1)
        {
            return false;
        }
        _tokens -= 1;
        return true;
    }
}
