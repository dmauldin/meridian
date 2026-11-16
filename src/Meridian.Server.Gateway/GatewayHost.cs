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

    /// <summary>The single zone (id 1) used when <see cref="Zones"/> is empty.</summary>
    public string ZoneHost { get; init; } = "127.0.0.1";

    /// <summary>The single zone's port, used when <see cref="Zones"/> is empty.</summary>
    public int ZonePort { get; init; } = NetDefaults.ZonePort;

    /// <summary>Every zone this gateway routes to. Empty means one zone, id 1, at ZoneHost:ZonePort.</summary>
    public IReadOnlyList<ZoneEndpoint> Zones { get; init; } = [];

    internal IReadOnlyList<ZoneEndpoint> EffectiveZones => Zones.Count > 0 ? Zones : [new ZoneEndpoint(1, ZoneHost, ZonePort)];

    public required string InternalKey { get; init; }

    public required byte[] TicketKey { get; init; }

    /// <summary>Sustained packets per second a client may send; bursts up to twice this.</summary>
    public int MaxPacketsPerSecond { get; init; } = 40;

    /// <summary>Failed decryptions tolerated before a client is dropped.</summary>
    public int MaxRejectedPackets { get; init; } = 50;

    public TimeSpan MetricsInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Where one zone process listens.</summary>
public sealed record ZoneEndpoint(uint ZoneId, string Host, int Port)
{
    /// <summary>Parses <c>1@127.0.0.1:7800,2@127.0.0.1:7801</c>.</summary>
    public static IReadOnlyList<ZoneEndpoint> ParseList(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            .. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(entry =>
            {
                var at = entry.IndexOf('@', StringComparison.Ordinal);
                var colon = entry.LastIndexOf(':');
                if (at <= 0 || colon <= at + 1
                    || !uint.TryParse(entry.AsSpan(0, at), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || !int.TryParse(entry.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                {
                    throw new FormatException($"Zone '{entry}' is not id@host:port.");
                }
                return new ZoneEndpoint(id, entry[(at + 1)..colon], port);
            }),
        ];
    }
}

/// <summary>
/// The client-facing edge: validates tickets, decrypts and shape-checks client packets,
/// rate-limits, and relays between each client and the zone it is in. Clients keep their
/// connection when they change zones: the zone they leave sends <see cref="TransferClient"/>
/// and the gateway re-routes them. Runs on the thread that calls <see cref="Run"/>.
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
    private readonly Dictionary<uint, ZoneLink> _links = [];
    private readonly Dictionary<int, ZoneLink> _linkByPeer = [];
    private uint _nextConnectionId = 1;
    private long _packetsIn, _packetsOut, _dropped, _stale, _transfers;

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

        foreach (var zone in options.EffectiveZones)
        {
            _links.Add(zone.ZoneId, new ZoneLink(zone));
        }
        var zoneEvents = new EventBasedNetListener();
        zoneEvents.PeerConnectedEvent += p => _log($"link up to {p}; waiting for its ZoneHello");
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

    /// <summary>True when every configured zone is connected and has confirmed which zone it is.</summary>
    public bool ZoneConnected => _links.Values.All(l => l.Ready);

    /// <summary>Clients moved between zones since start.</summary>
    public long Transfers => _transfers;

    public void Start()
    {
        if (!_clients.Start(_options.ClientPort))
        {
            throw new InvalidOperationException($"Could not bind UDP port {_options.ClientPort}.");
        }
        _zones.Start();
        _log($"gateway listening on udp/{Port}, zones {string.Join(", ", _links.Values.Select(l => $"{l.Endpoint.ZoneId}@{l.Endpoint.Host}:{l.Endpoint.Port}"))}");
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
                    $"GATEWAY clients={ClientCount} zones_up={_links.Values.Count(l => l.Ready)}/{_links.Count} in={_packetsIn} out={_packetsOut} dropped={_dropped} stale={_stale} transfers={_transfers}"));
                nextReport = now + metricsTicks;
            }
        }
        _clients.DisconnectAll();
        _clients.PollEvents();
    }

    /// <summary>One iteration of network processing. Exposed for tests that drive the loop themselves.</summary>
    public void PollOnce()
    {
        EnsureZoneLinks();
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

    public void OnDropClient(NetPeer context, in DropClient message)
    {
        if (Owned(context, message.ConnectionId) is { } session)
        {
            _log($"zone dropped connection {message.ConnectionId}: {message.Reason}");
            Kick(session, message.Reason ?? "Disconnected by the server.");
        }
    }

    public void OnToClient(NetPeer context, in ToClient message)
    {
        if (message.Payload is null || Owned(context, message.ConnectionId) is not { } session)
        {
            return; // the client left, or moved to another zone, while the packet was in flight
        }
        if (message.DeliveryMode > (byte)Delivery.ReliableUnordered)
        {
            _log($"zone sent unknown delivery mode {message.DeliveryMode}");
            return;
        }
        var (channel, method) = NetDefaults.Map((Delivery)message.DeliveryMode);
        var length = session.Cipher.Seal(channel, message.Payload, _sealed);
        session.Peer.Send(_sealed, 0, length, channel, method);
        _packetsOut++;
    }

    /// <summary>
    /// The client's zone saved its character into another zone: route the client there. Only the
    /// zone that owns the client may move it; a destination that is down ends the session (the
    /// character is already saved there, so logging in again resumes it).
    /// </summary>
    public void OnTransferClient(NetPeer context, in TransferClient message)
    {
        if (Owned(context, message.ConnectionId) is not { } session)
        {
            return;
        }
        if (!_links.TryGetValue(message.ZoneId, out var to) || !to.Ready)
        {
            _log($"connection {session.ConnectionId}: zone {message.ZoneId} unavailable for a transfer");
            Kick(session, "That area is unavailable right now. Please log in again.");
            return;
        }
        session.ZoneId = message.ZoneId;
        _transfers++;
        SendToZone(session.ZoneId, JoinFor(session));
    }

    /// <summary>A zone says which zone it is; a mismatch with this gateway's configuration drops the link.</summary>
    public void OnZoneHello(NetPeer context, in ZoneHello message)
    {
        if (!_linkByPeer.TryGetValue(context.Id, out var link))
        {
            return;
        }
        if (message.ZoneId != link.Endpoint.ZoneId)
        {
            _log($"{link.Endpoint.Host}:{link.Endpoint.Port} says it is zone {message.ZoneId}, configured as {link.Endpoint.ZoneId}; dropping the link");
            context.Disconnect();
            return;
        }
        link.Verified = true;
        _log($"connected to zone {message.ZoneId} at {context}");
    }

    public void OnPlayerJoin(NetPeer context, in PlayerJoin message) => Unexpected(nameof(PlayerJoin));

    public void OnPlayerLeave(NetPeer context, in PlayerLeave message) => Unexpected(nameof(PlayerLeave));

    public void OnFromClient(NetPeer context, in FromClient message) => Unexpected(nameof(FromClient));

    private void Unexpected(string name) => _log($"zone sent {name}, which only flows gateway → zone");

    private void EnsureZoneLinks()
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var link in _links.Values)
        {
            if (link.Peer is { ConnectionState: ConnectionState.Connected or ConnectionState.Outgoing } || now < link.NextAttempt)
            {
                continue;
            }
            link.NextAttempt = now + Stopwatch.Frequency; // retry once a second
            link.Verified = false;
            link.Peer = _zones.Connect(link.Endpoint.Host, link.Endpoint.Port, _options.InternalKey);
            if (link.Peer is not null)
            {
                _linkByPeer[link.Peer.Id] = link;
            }
        }
    }

    /// <summary>The session for <paramref name="connectionId"/> if the zone at <paramref name="zone"/> owns it.</summary>
    private ClientSession? Owned(NetPeer zone, uint connectionId)
    {
        if (!_byConnection.TryGetValue(connectionId, out var session))
        {
            return null;
        }
        if (!_linkByPeer.TryGetValue(zone.Id, out var link) || link.Endpoint.ZoneId != session.ZoneId)
        {
            _stale++; // e.g. a last snapshot from the zone the client just left
            return null;
        }
        return session;
    }

    private static PlayerJoin JoinFor(ClientSession session) => new()
    {
        ConnectionId = session.ConnectionId,
        AccountId = session.Ticket.AccountId,
        Name = session.Ticket.CharacterName,
        CharacterId = session.Ticket.CharacterId,
    };

    private void OnConnectionRequest(ConnectionRequest request)
    {
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
        if (!_links.TryGetValue(ticket.ZoneId, out var zone) || !zone.Ready)
        {
            Reject(request, "Zone unavailable, try again shortly."); // before using the ticket, so a retry works
            return;
        }
        if (!_usedTickets.TryUse(ticket, nowUnix))
        {
            Reject(request, "Login already used. Please log in again.");
            return;
        }

        var peer = request.Accept();
        var session = new ClientSession(_nextConnectionId++, peer, ticket, _options.MaxPacketsPerSecond) { ZoneId = ticket.ZoneId };
        _byPeer[peer.Id] = session;
        _byConnection[session.ConnectionId] = session;
        SendToZone(session.ZoneId, JoinFor(session));
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

        var length = session.Cipher.Open(channel, data, _plain);
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
        SendToZone(session.ZoneId, new FromClient { ConnectionId = session.ConnectionId, Payload = plain.ToArray() });
    }

    private void OnClientDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_byPeer.Remove(peer.Id, out var session))
        {
            return;
        }
        _byConnection.Remove(session.ConnectionId);
        session.Cipher.Dispose();
        SendToZone(session.ZoneId, new PlayerLeave { ConnectionId = session.ConnectionId });
    }

    private void OnZoneDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_linkByPeer.Remove(peer.Id, out var link))
        {
            return;
        }
        link.Verified = false;
        var inZone = _byConnection.Values.Where(s => s.ZoneId == link.Endpoint.ZoneId).ToList();
        _log($"zone {link.Endpoint.ZoneId} link lost ({info.Reason}); disconnecting its {inZone.Count} clients");
        foreach (var s in inZone)
        {
            Kick(s, "Zone went offline.");
        }
    }

    private void Kick(ClientSession session, string reason)
    {
        var bye = Envelope.Encode(_plain, new Disconnecting { Reason = reason });
        var length = session.Cipher.Seal(SessionCipher.DisconnectStream, bye, _sealed);
        session.Peer.Disconnect(_sealed.AsSpan(0, length));
    }

    private void SendToZone<T>(uint zoneId, in T message)
        where T : struct, IWireMessage<T>
    {
        if (!_links.TryGetValue(zoneId, out var link) || link.Peer is null)
        {
            return;
        }
        var bytes = Envelope.Encode(_internal, message);
        link.Peer.Send(bytes, 1, DeliveryMethod.ReliableOrdered);
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

        public void OnSetTarget(int context, in SetTarget message)
        {
        }

        public void OnSetAttack(int context, in SetAttack message)
        {
        }

        public void OnSnapshotAck(int context, in SnapshotAck message)
        {
        }

        public void OnTrainSkill(int context, in TrainSkill message)
        {
        }

        // Positions and slots are bounded by their u8 fields; the zone checks they exist.
        public void OnEquipItem(int context, in EquipItem message)
        {
        }

        public void OnUnequipItem(int context, in UnequipItem message)
        {
        }

        public void OnDiscardItem(int context, in DiscardItem message)
        {
        }

        public void OnUploadProtocol(int context, in UploadProtocol message)
        {
        }

        public void OnCastProtocol(int context, in CastProtocol message)
        {
        }

        public void OnCancelProtocol(int context, in CancelProtocol message)
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

    /// <summary>The zone the client is in; changes on a transfer.</summary>
    public uint ZoneId { get; set; }
}

/// <summary>The gateway's link to one zone process.</summary>
internal sealed class ZoneLink(ZoneEndpoint endpoint)
{
    public ZoneEndpoint Endpoint { get; } = endpoint;

    public NetPeer? Peer { get; set; }

    /// <summary>The zone's ZoneHello matched <see cref="Endpoint"/>.</summary>
    public bool Verified { get; set; }

    public long NextAttempt { get; set; }

    public bool Ready => Verified && Peer?.ConnectionState == ConnectionState.Connected;
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
