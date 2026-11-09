using Meridian.Core.Characters;
using Meridian.Core.Stats;
using Meridian.Core.World;
using Meridian.Wire;

namespace Meridian.Server.Zone;

/// <summary>Identifies a client: the gateway it is connected through plus that gateway's connection id.</summary>
public readonly record struct ClientRef(int GatewayId, uint ConnectionId);

/// <summary>Where the simulation sends encoded client-bound packets.</summary>
public interface IZoneOutbox
{
    void Send(ClientRef client, Delivery delivery, ReadOnlySpan<byte> packet);
}

public sealed record ZoneOptions
{
    public uint ZoneId { get; init; } = 1;

    public int TickRateHz { get; init; } = 20;

    /// <summary>Send a snapshot every N ticks (2 at 20 Hz = 10 snapshots per second).</summary>
    public int SnapshotEveryTicks { get; init; } = 2;

    public ZoneBounds Bounds { get; init; } = new(-2_000_000, -2_000_000, 2_000_000, 2_000_000);

    public int InterestRadiusMm { get; init; } = 100_000;

    public int MaxEntitiesPerSnapshot { get; init; } = 64;

    public int ChatRadiusMm { get; init; } = 50_000;

    public int GridCellMm { get; init; } = 32_000;

    /// <summary>Movement intents older than this many ticks without a refresh stop the player.</summary>
    public int IntentTimeoutTicks { get; init; } = 10;

    public int TickMs => 1_000 / TickRateHz;
}

/// <summary>
/// The authoritative simulation of one zone. Pure and single-threaded: it takes intents, advances
/// time in fixed ticks and writes packets to an <see cref="IZoneOutbox"/>. No sockets, so tests
/// can drive it directly.
/// </summary>
public sealed class ZoneSimulation : IServerBoundHandler<ClientRef>
{
    private readonly ZoneOptions _options;
    private readonly IZoneOutbox _outbox;
    private readonly Dictionary<ClientRef, Player> _byClient = [];
    private readonly List<Player> _players = [];
    private readonly SpatialGrid _grid;
    private readonly byte[] _packet = new byte[Envelope.MaxPacketBytes];
    private readonly List<int> _candidates = [];
    private readonly List<(long Distance, Player Player)> _visible = [];
    private readonly HashSet<uint> _visibleIds = [];
    private uint _nextEntityId = 1;

    public ZoneSimulation(ZoneOptions options, IZoneOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outbox);
        _options = options;
        _outbox = outbox;
        _grid = new SpatialGrid(options.GridCellMm);
    }

    public uint Tick { get; private set; }

    public int PlayerCount => _players.Count;

    public IReadOnlyList<Player> Players => _players;

    /// <summary>Adds a player at a spawn point and sends them <see cref="Welcome"/>.</summary>
    public Player Join(ClientRef client, ulong accountId, string name)
    {
        if (_byClient.ContainsKey(client))
        {
            throw new InvalidOperationException($"{client} already joined.");
        }
        var character = new Character(Profession.Operative, Origin.Baseline);
        character.Stats.SetBase(Stat.Level, 1);
        character.Stats.SetBase(Stat.RunSpeed, 5_000); // mm/s; a stat so buffs can change it later

        var player = new Player(_nextEntityId++, client, accountId, name, character)
        {
            Position = SpawnPoint(client),
        };
        _byClient.Add(client, player);
        _players.Add(player);

        Send(player, new Welcome
        {
            EntityId = player.EntityId,
            ZoneId = _options.ZoneId,
            ServerTick = Tick,
            TickRateHz = (ushort)_options.TickRateHz,
            X = player.Position.X,
            Z = player.Position.Z,
        });
        return player;
    }

    public bool Leave(ClientRef client)
    {
        if (!_byClient.Remove(client, out var player))
        {
            return false;
        }
        _players.Remove(player);
        return true;
    }

    /// <summary>Removes every player that came through <paramref name="gatewayId"/> (the gateway went away).</summary>
    public int LeaveGateway(int gatewayId)
    {
        var gone = _players.Where(p => p.Client.GatewayId == gatewayId).Select(p => p.Client).ToList();
        foreach (var c in gone)
        {
            Leave(c);
        }
        return gone.Count;
    }

    /// <summary>Decodes a client packet forwarded by a gateway and applies it.</summary>
    /// <exception cref="WireFormatException">The packet is malformed; the caller should drop the client.</exception>
    public void HandleClientPacket(ClientRef client, ReadOnlySpan<byte> packet)
    {
        if (_byClient.ContainsKey(client))
        {
            WireDispatch.DispatchServerBound(packet, this, client);
        }
    }

    /// <summary>Advances the simulation one fixed step and sends snapshots when due.</summary>
    public void Step()
    {
        Tick++;
        var dt = _options.TickMs;

        _grid.Clear();
        for (var i = 0; i < _players.Count; i++)
        {
            var p = _players[i];
            if (Tick - p.IntentTick > _options.IntentTimeoutTicks)
            {
                p.DirX = p.DirZ = 0; // client went quiet: stop rather than run forever
            }
            p.Position = Movement.Step(p.Position, p.DirX, p.DirZ, p.Character.Get(Stat.RunSpeed), dt, _options.Bounds);
            p.Yaw = Movement.YawByte(p.DirX, p.DirZ, p.Yaw);
            _grid.Insert(i, p.Position);
        }

        // Stagger snapshots across ticks (by entity id) so the cost is spread evenly instead of
        // landing on every Nth tick. Each player still gets one every SnapshotEveryTicks ticks.
        var every = (uint)_options.SnapshotEveryTicks;
        foreach (var p in _players)
        {
            if ((Tick + p.EntityId) % every == 0)
            {
                SendSnapshot(p);
            }
        }
    }

    public void OnMoveIntent(ClientRef context, in MoveIntent message)
    {
        var p = _byClient[context];
        // Unreliable channel: ignore anything older than what we already applied.
        if (message.Sequence <= p.LastInputSequence && p.LastInputSequence != 0)
        {
            return;
        }
        p.LastInputSequence = message.Sequence;
        p.DirX = message.DirX;
        p.DirZ = message.DirZ;
        p.IntentTick = Tick;
    }

    public void OnChatSay(ClientRef context, in ChatSay message)
    {
        var speaker = _byClient[context];
        var text = message.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var chat = new ChatMessage { FromEntityId = speaker.EntityId, FromName = speaker.Name, Text = text };
        var packet = Envelope.Encode(_packet, chat);
        var r2 = (long)_options.ChatRadiusMm * _options.ChatRadiusMm;
        foreach (var p in _players)
        {
            if (p.Position.DistanceSquared(speaker.Position) <= r2)
            {
                _outbox.Send(p.Client, Delivery.ReliableUnordered, packet);
            }
        }
    }

    public void OnPing(ClientRef context, in Ping message) =>
        Send(_byClient[context], new Pong { ClientTimeMs = message.ClientTimeMs, ServerTick = Tick });

    private void SendSnapshot(Player viewer)
    {
        // Gather nearby entities, nearest first, capped by the per-snapshot budget.
        _candidates.Clear();
        _grid.Query(viewer.Position, _options.InterestRadiusMm, _candidates);
        _visible.Clear();
        var r2 = (long)_options.InterestRadiusMm * _options.InterestRadiusMm;
        foreach (var index in _candidates)
        {
            var other = _players[index];
            var d = other.Position.DistanceSquared(viewer.Position);
            if (d <= r2)
            {
                _visible.Add((d, other));
            }
        }
        _visible.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Player.EntityId.CompareTo(b.Player.EntityId));
        var count = Math.Min(_visible.Count, _options.MaxEntitiesPerSnapshot);

        var entities = new EntityState[count];
        _visibleIds.Clear();
        for (var i = 0; i < count; i++)
        {
            var e = _visible[i].Player;
            _visibleIds.Add(e.EntityId);
            entities[i] = new EntityState
            {
                EntityId = e.EntityId,
                X = e.Position.X,
                Z = e.Position.Z,
                Yaw = e.Yaw,
                HealthPercent = 100,
            };
        }

        // Entities the viewer saw last time but no longer sees. Overflow waits for the next snapshot.
        var removed = new List<uint>();
        foreach (var id in viewer.LastVisible)
        {
            if (!_visibleIds.Contains(id))
            {
                if (removed.Count == _options.MaxEntitiesPerSnapshot)
                {
                    _visibleIds.Add(id); // keep it pending
                    continue;
                }
                removed.Add(id);
            }
        }
        viewer.LastVisible.Clear();
        viewer.LastVisible.UnionWith(_visibleIds);

        Send(viewer, new Snapshot
        {
            Tick = Tick,
            AckedInputSequence = viewer.LastInputSequence,
            Entities = entities,
            Removed = [.. removed],
        });
    }

    private void Send<T>(Player to, in T message)
        where T : struct, IWireMessage<T> =>
        _outbox.Send(to.Client, T.Delivery, Envelope.Encode(_packet, message));

    /// <summary>
    /// Spreads spawns over a 60 m square near the origin. Uses a fixed integer mix rather than
    /// <see cref="HashCode"/>, which is randomized per process and would break replays.
    /// </summary>
    private static Position SpawnPoint(ClientRef client)
    {
        var h = ((ulong)(uint)client.GatewayId << 32) | client.ConnectionId;
        h = (h ^ (h >> 33)) * 0xFF51AFD7ED558CCDUL;
        h = (h ^ (h >> 33)) * 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return new Position((int)(h % 60_000) - 30_000, (int)((h >> 32) % 60_000) - 30_000);
    }
}

/// <summary>A connected player's simulation state.</summary>
public sealed class Player(uint entityId, ClientRef client, ulong accountId, string name, Character character)
{
    public uint EntityId { get; } = entityId;

    public ClientRef Client { get; } = client;

    public ulong AccountId { get; } = accountId;

    public string Name { get; } = name;

    public Character Character { get; } = character;

    public Position Position { get; set; }

    public byte Yaw { get; set; }

    public short DirX { get; set; }

    public short DirZ { get; set; }

    public uint LastInputSequence { get; set; }

    public uint IntentTick { get; set; }

    internal HashSet<uint> LastVisible { get; } = [];
}
