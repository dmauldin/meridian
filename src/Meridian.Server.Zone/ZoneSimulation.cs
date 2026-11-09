using Meridian.Core.Characters;
using Meridian.Core.Randomness;
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

    /// <summary>Seeds combat rolls and mob placement. Same seed + same inputs = same outcome.</summary>
    public ulong Seed { get; init; } = 0x4D45524944494E;

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

    /// <summary>Skitters scattered in a ring around the spawn area.</summary>
    public int MobCount { get; init; } = 40;

    /// <summary>Mob spawns fall between these distances from the origin.</summary>
    public int MobRingInnerMm { get; init; } = 40_000;

    public int MobRingOuterMm { get; init; } = 120_000;

    public int PlayerRespawnTicks { get; init; } = 100;

    /// <summary>The "in combat" flag stays set this long after the last hit.</summary>
    public int CombatFlagTicks { get; init; } = 100;

    public int TickMs => 1_000 / TickRateHz;
}

/// <summary>
/// The authoritative simulation of one zone. Pure and single-threaded: it takes intents, advances
/// time in fixed ticks and writes packets to an <see cref="IZoneOutbox"/>. No sockets, so tests
/// can drive it directly.
/// </summary>
/// <remarks>
/// Tick order: player respawns, mob AI, movement, combat, status updates, snapshots. Entities are
/// processed in a stable order (join/spawn order) so a seeded zone replays identically.
/// </remarks>
public sealed partial class ZoneSimulation : IServerBoundHandler<ClientRef>
{
    private readonly ZoneOptions _options;
    private readonly IZoneOutbox _outbox;
    private readonly DeterministicRandom _rng;
    private readonly Dictionary<ClientRef, Player> _byClient = [];
    private readonly Dictionary<uint, Entity> _byId = [];
    private readonly List<Entity> _entities = [];
    private readonly List<Player> _players = [];
    private readonly List<Mob> _mobs = [];
    private readonly SpatialGrid _grid;
    private readonly byte[] _packet = new byte[Envelope.MaxPacketBytes];
    private readonly List<int> _candidates = [];
    private readonly List<(long Distance, Entity Entity)> _visible = [];
    private readonly HashSet<uint> _visibleIds = [];
    private uint _nextEntityId = 1;
    private uint _gridTick = uint.MaxValue;

    public ZoneSimulation(ZoneOptions options, IZoneOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outbox);
        _options = options;
        _outbox = outbox;
        _rng = new DeterministicRandom(options.Seed);
        _grid = new SpatialGrid(options.GridCellMm);
        SpawnMobs();
    }

    public uint Tick { get; private set; }

    public int PlayerCount => _players.Count;

    public IReadOnlyList<Player> Players => _players;

    public IReadOnlyList<Mob> Mobs => _mobs;

    public Entity? Find(uint entityId) => _byId.GetValueOrDefault(entityId);

    /// <summary>Adds a player at a spawn point and sends them <see cref="Welcome"/>.</summary>
    public Player Join(ClientRef client, ulong accountId, string name)
    {
        if (_byClient.ContainsKey(client))
        {
            throw new InvalidOperationException($"{client} already joined.");
        }
        var character = new Character(Profession.Operative, Origin.Baseline);
        StarterContent.ApplyNewPlayerStats(character.Stats);

        var player = new Player(_nextEntityId++, client, accountId, name, character)
        {
            Position = SpawnPoint(client),
        };
        _byClient.Add(client, player);
        _players.Add(player);
        Add(player);

        Send(player, new Welcome
        {
            EntityId = player.EntityId,
            ZoneId = _options.ZoneId,
            ServerTick = Tick,
            TickRateHz = (ushort)_options.TickRateHz,
            X = player.Position.X,
            Z = player.Position.Z,
        });
        SendStatusIfChanged(player);
        return player;
    }

    public bool Leave(ClientRef client)
    {
        if (!_byClient.Remove(client, out var player))
        {
            return false;
        }
        _players.Remove(player);
        _entities.Remove(player);
        _byId.Remove(player.EntityId);
        foreach (var mob in _mobs)
        {
            mob.Threat.Remove(player.EntityId);
        }
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

    /// <summary>Advances the simulation one fixed step and sends updates.</summary>
    public void Step()
    {
        Tick++;
        RespawnPlayers();
        StepMobs();
        MoveEntities();
        StepCombat();

        foreach (var p in _players)
        {
            SendStatusIfChanged(p);
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

    // ------------------------------------------------------------------ intents

    public void OnMoveIntent(ClientRef context, in MoveIntent message)
    {
        var p = _byClient[context];
        // Unreliable channel: ignore anything older than what we already applied.
        if (message.Sequence <= p.LastInputSequence && p.LastInputSequence != 0)
        {
            return;
        }
        p.LastInputSequence = message.Sequence;
        if (p.Dead)
        {
            return;
        }
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
        Broadcast(speaker.Position, _options.ChatRadiusMm, chat);
    }

    public void OnPing(ClientRef context, in Ping message) =>
        Send(_byClient[context], new Pong { ClientTimeMs = message.ClientTimeMs, ServerTick = Tick });

    public void OnSetTarget(ClientRef context, in SetTarget message)
    {
        var p = _byClient[context];
        var target = message.EntityId == 0 ? null : Find(message.EntityId);
        p.TargetId = target is null || target == p ? 0 : target.EntityId;
        if (p.TargetId == 0)
        {
            p.Attacking = false;
        }
    }

    public void OnSetAttack(ClientRef context, in SetAttack message)
    {
        var p = _byClient[context];
        p.Attacking = message.Enabled && !p.Dead && Find(p.TargetId) is Mob { Dead: false };
    }

    // ------------------------------------------------------------------ movement

    private void MoveEntities()
    {
        var dt = _options.TickMs;
        foreach (var e in _entities)
        {
            if (e is Player p && Tick - p.IntentTick > _options.IntentTimeoutTicks)
            {
                p.DirX = p.DirZ = 0; // client went quiet: stop rather than run forever
            }
            if (e.Dead)
            {
                continue;
            }
            e.Position = Movement.Step(e.Position, e.DirX, e.DirZ, e.CurrentSpeed, dt, _options.Bounds);
            e.Yaw = Movement.YawByte(e.DirX, e.DirZ, e.Yaw);
        }
    }

    // ------------------------------------------------------------------ status, snapshots

    private void SendStatusIfChanged(Player p)
    {
        var key = new SelfStatusKey(p.Health, p.MaxHealth, p.TargetId, p.Attacking, p.Dead);
        if (p.LastSentStatus == key)
        {
            return;
        }
        p.LastSentStatus = key;
        Send(p, new SelfStatus
        {
            Health = p.Health,
            MaxHealth = p.MaxHealth,
            TargetId = p.TargetId,
            Attacking = p.Attacking,
            Dead = p.Dead,
        });
    }

    private void SendSnapshot(Player viewer)
    {
        // Rebuilding per snapshot is wasteful but simple; profiling at M0 scale says it's cheap.
        RebuildGridIfStale();

        _candidates.Clear();
        _grid.Query(viewer.Position, _options.InterestRadiusMm, _candidates);
        _visible.Clear();
        var r2 = (long)_options.InterestRadiusMm * _options.InterestRadiusMm;
        foreach (var index in _candidates)
        {
            var other = _entities[index];
            var d = other.Position.DistanceSquared(viewer.Position);
            if (d <= r2)
            {
                _visible.Add((d, other));
            }
        }
        _visible.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Entity.EntityId.CompareTo(b.Entity.EntityId));
        var count = Math.Min(_visible.Count, _options.MaxEntitiesPerSnapshot);

        var entities = new EntityState[count];
        _visibleIds.Clear();
        for (var i = 0; i < count; i++)
        {
            var e = _visible[i].Entity;
            _visibleIds.Add(e.EntityId);
            if (!viewer.LastVisible.Contains(e.EntityId))
            {
                // First sight: names and appearance go once, reliably.
                Send(viewer, new EntityInfo
                {
                    EntityId = e.EntityId,
                    Kind = (byte)e.Kind,
                    TemplateId = e is Mob m ? m.Template.Id : (ushort)0,
                    Name = e.Name,
                    Level = (ushort)e.Level,
                });
            }
            entities[i] = new EntityState
            {
                EntityId = e.EntityId,
                X = e.Position.X,
                Z = e.Position.Z,
                Yaw = e.Yaw,
                HealthPercent = e.HealthPercent,
                Flags = (byte)((e.Dead ? 1 : 0) | (InCombat(e) ? 2 : 0)),
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

    private void RebuildGridIfStale()
    {
        if (_gridTick == Tick)
        {
            return;
        }
        _gridTick = Tick;
        _grid.Clear();
        for (var i = 0; i < _entities.Count; i++)
        {
            _grid.Insert(i, _entities[i].Position);
        }
    }

    private bool InCombat(Entity e) => e.LastCombatTick != 0 && Tick - e.LastCombatTick < _options.CombatFlagTicks;

    // ------------------------------------------------------------------ plumbing

    private void Add(Entity e)
    {
        _entities.Add(e);
        _byId.Add(e.EntityId, e);
    }

    private void Send<T>(Player to, in T message)
        where T : struct, IWireMessage<T> =>
        _outbox.Send(to.Client, T.Delivery, Envelope.Encode(_packet, message));

    /// <summary>Sends to every player within <paramref name="radiusMm"/> of <paramref name="center"/>.</summary>
    private void Broadcast<T>(Position center, int radiusMm, in T message)
        where T : struct, IWireMessage<T>
    {
        var packet = Envelope.Encode(_packet, message);
        var r2 = (long)radiusMm * radiusMm;
        foreach (var p in _players)
        {
            if (p.Position.DistanceSquared(center) <= r2)
            {
                _outbox.Send(p.Client, T.Delivery, packet);
            }
        }
    }

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
