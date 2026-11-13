using System.Diagnostics;
using LiteNetLib;
using Meridian.Net;
using Meridian.Wire;

namespace Meridian.Server.Zone;

public sealed record ZoneHostOptions
{
    public int Port { get; init; } = NetDefaults.ZonePort;

    /// <summary>Shared secret gateways must present. Internal network only.</summary>
    public required string InternalKey { get; init; }

    public TimeSpan MetricsInterval { get; init; } = TimeSpan.FromSeconds(5);

    public ZoneOptions Zone { get; init; } = new();
}

/// <summary>
/// Runs a <see cref="ZoneSimulation"/> at a fixed tick rate and connects it to gateways.
/// Everything happens on the thread that calls <see cref="Run"/>.
/// </summary>
public sealed class ZoneHost : IZoneOutbox, IInternalHandler<NetPeer>, IDisposable
{
    private readonly ZoneHostOptions _options;
    private readonly NetManager _net;
    private readonly byte[] _buffer = new byte[Envelope.MaxPacketBytes + 64];
    private readonly Dictionary<int, NetPeer> _gateways = [];
    private readonly Action<string> _log;
    private TickMetrics? _metrics;

    public ZoneHost(ZoneHostOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _log = log ?? Console.WriteLine;
        Simulation = new ZoneSimulation(options.Zone, this);

        var listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += r => r.AcceptIfKey(_options.InternalKey);
        listener.PeerConnectedEvent += p =>
        {
            _gateways[p.Id] = p;
            _log($"gateway {p.Id} connected from {p}");
        };
        listener.PeerDisconnectedEvent += (p, info) =>
        {
            _gateways.Remove(p.Id);
            var dropped = Simulation.LeaveGateway(p.Id);
            _log($"gateway {p.Id} disconnected ({info.Reason}); removed {dropped} players");
        };
        listener.NetworkReceiveEvent += OnReceive;
        _net = NetDefaults.Configure(new NetManager(listener));
    }

    public ZoneSimulation Simulation { get; }

    /// <summary>The bound port (useful when configured with 0 for tests).</summary>
    public int Port => _net.LocalPort;

    /// <summary>Latest metrics window, for tests and health checks.</summary>
    public TickReport? LastReport { get; private set; }

    public void Start()
    {
        if (!_net.Start(_options.Port))
        {
            throw new InvalidOperationException($"Could not bind UDP port {_options.Port}.");
        }
        _log($"zone {Simulation.Zone.Key} (content {Simulation.Content.Hash:X16}) listening on udp/{Port} at {_options.Zone.TickRateHz} Hz");
    }

    /// <summary>Runs the fixed-rate loop until <paramref name="stop"/> is cancelled.</summary>
    public void Run(CancellationToken stop)
    {
        var tickTicks = Stopwatch.Frequency / _options.Zone.TickRateHz;
        var metricsTicks = (long)(_options.MetricsInterval.TotalSeconds * Stopwatch.Frequency);
        var now = Stopwatch.GetTimestamp();
        var nextTick = now + tickTicks;
        var nextReport = now + metricsTicks;
        var metrics = new TickMetrics(now);
        _metrics = metrics;

        while (!stop.IsCancellationRequested)
        {
            _net.PollEvents();
            now = Stopwatch.GetTimestamp();
            if (now < nextTick)
            {
                var waitMs = (int)((nextTick - now) * 1000 / Stopwatch.Frequency);
                Thread.Sleep(Math.Clamp(waitMs, 0, 1));
                continue;
            }

            var start = Stopwatch.GetTimestamp();
            Simulation.Step();
            var end = Stopwatch.GetTimestamp();
            var workMs = (end - start) * 1000.0 / Stopwatch.Frequency;
            var overran = end > nextTick + tickTicks;
            metrics.RecordTick(workMs, overran);

            nextTick += tickTicks;
            if (end - nextTick > 5 * tickTicks)
            {
                // Far behind (debugger, machine stall): resync instead of fast-forwarding.
                nextTick = end + tickTicks;
            }

            if (end >= nextReport)
            {
                LastReport = metrics.Report(end, Simulation.PlayerCount);
                _log(LastReport.ToLogLine());
                nextReport = end + metricsTicks;
            }
        }
    }

    public void Send(ClientRef client, Delivery delivery, ReadOnlySpan<byte> packet)
    {
        if (!_gateways.TryGetValue(client.GatewayId, out var gateway))
        {
            return;
        }
        // Written by hand to avoid copying the payload into a ToClient.Payload array; a test
        // checks the bytes match the generated encoding exactly.
        var bytes = WriteToClient(_buffer, client.ConnectionId, delivery, packet);
        gateway.Send(bytes, 1, DeliveryMethod.ReliableOrdered);
        _metrics?.RecordBytesOut(packet.Length);
    }

    /// <summary>Encodes a <see cref="ToClient"/> envelope around <paramref name="payload"/> without copying it first.</summary>
    public static ReadOnlySpan<byte> WriteToClient(Span<byte> buffer, uint connectionId, Delivery delivery, ReadOnlySpan<byte> payload)
    {
        var w = new WireWriter(buffer);
        w.WriteU16(ToClient.Id);
        w.WriteU32(connectionId);
        w.WriteU8((byte)delivery);
        w.WriteBytes(payload, 16_000);
        return buffer[..w.Position];
    }

    public void OnPlayerJoin(NetPeer context, in PlayerJoin message)
    {
        var client = new ClientRef(context.Id, message.ConnectionId);
        try
        {
            Simulation.Join(client, message.AccountId, message.Name ?? "?");
        }
        catch (InvalidOperationException e)
        {
            // A gateway bug must not take the whole zone down.
            _log($"rejected join for {client}: {e.Message}");
        }
    }

    public void OnPlayerLeave(NetPeer context, in PlayerLeave message) =>
        Simulation.Leave(new ClientRef(context.Id, message.ConnectionId));

    public void OnFromClient(NetPeer context, in FromClient message)
    {
        var client = new ClientRef(context.Id, message.ConnectionId);
        try
        {
            Simulation.HandleClientPacket(client, message.Payload);
        }
        catch (WireFormatException e)
        {
            // The gateway already shape-checks, so this means a version mismatch or a bug.
            _log($"bad packet from {client}: {e.Message}");
        }
    }

    public void OnToClient(NetPeer context, in ToClient message) =>
        _log($"gateway {context.Id} sent ToClient, which only flows zone → gateway");

    public void Dispose() => _net.Stop();

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            WireDispatch.DispatchInternal(reader.GetRemainingBytesSpan(), this, peer);
        }
        catch (WireFormatException e)
        {
            _log($"bad internal packet from gateway {peer.Id}: {e.Message}");
        }
    }
}
