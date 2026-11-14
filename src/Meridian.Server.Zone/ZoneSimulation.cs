using Meridian.Content;
using Meridian.Core.Characters;
using Meridian.Core.Combat;
using Meridian.Core.Content;
using Meridian.Core.Stats;
using Meridian.Core.Randomness;
using Meridian.Core.Items;
using Meridian.Core.World;
using Meridian.Persistence;
using Meridian.Wire;
using Meridian.Wire.Delta;

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
    /// <summary>Which zone from the content pack this simulation runs.</summary>
    public string ZoneKey { get; init; } = "glasswind_flats";

    /// <summary>Seeds combat rolls and mob placement. Same seed + same inputs = same outcome.</summary>
    public ulong Seed { get; init; } = 0x4D45524944494E;

    public int TickRateHz { get; init; } = 20;

    /// <summary>Send a snapshot every N ticks (2 at 20 Hz = 10 snapshots per second).</summary>
    public int SnapshotEveryTicks { get; init; } = 2;


    public int InterestRadiusMm { get; init; } = 100_000;

    public int MaxEntitiesPerSnapshot { get; init; } = 64;

    public int ChatRadiusMm { get; init; } = 50_000;

    public int GridCellMm { get; init; } = 32_000;

    /// <summary>Movement intents older than this many ticks without a refresh stop the player.</summary>
    public int IntentTimeoutTicks { get; init; } = 10;

    /// <summary>
    /// Test hook: replaces the zone's spawn list with this many of its first spawn group's mob.
    /// Null uses the content as authored.
    /// </summary>
    public int? MobCount { get; init; }

    public int PlayerRespawnTicks { get; init; } = 100;

    /// <summary>The "in combat" flag stays set this long after the last hit.</summary>
    public int CombatFlagTicks { get; init; } = 100;

    /// <summary>
    /// A changed character is checkpointed (queued for saving) at most this often: 600 ticks = 30 s
    /// at 20 Hz. Staggered by entity id like snapshots. Leaving always checkpoints.
    /// </summary>
    public int CheckpointEveryTicks { get; init; } = 600;

    /// <summary>
    /// Development commands such as <c>/give</c>. Off by default; never enable on a server real
    /// players use (there is no permission model yet).
    /// </summary>
    public bool DevCommands { get; init; }

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
    private readonly ContentPack _content;
    private readonly ZoneDef _zone;
    private readonly DeterministicRandom _rng;
    private readonly Dictionary<ClientRef, Player> _byClient = [];
    private readonly Dictionary<uint, Entity> _byId = [];
    private readonly List<Entity> _entities = [];
    private readonly List<Player> _players = [];
    private readonly Dictionary<ulong, Player> _byCharacter = [];
    private List<CharacterRecord> _checkpoints = [];
    private readonly List<Mob> _mobs = [];
    private readonly SpatialGrid _grid;
    private readonly byte[] _packet = new byte[Envelope.MaxPacketBytes];
    private readonly List<int> _candidates = [];
    private readonly List<(long Distance, Entity Entity)> _visible = [];
    private readonly HashSet<uint> _visibleIds = [];
    private readonly EntityState[] _stateBuffer = new EntityState[EntityDeltaCodec.MaxEntities];
    private readonly byte[] _deltaBuffer = new byte[EntityDeltaCodec.MaxEntities * EntityDeltaCodec.MaxEntryBytes + 8];
    private readonly List<uint> _removed = [];
    /// <summary>Schema limit on DeltaSnapshot.removed.</summary>
    private const int MaxRemovedPerSnapshot = 128;

    private uint _nextEntityId = 1;
    private uint _gridTick = uint.MaxValue;

    public ZoneSimulation(ZoneOptions options, IZoneOutbox outbox, ContentPack? content = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outbox);
        _options = options;
        _outbox = outbox;
        _content = content ?? ContentPacks.Embedded;
        _zone = _content.Zones[options.ZoneKey];

        _rng = new DeterministicRandom(options.Seed);
        _grid = new SpatialGrid(options.GridCellMm);
        SpawnMobs();
    }

    public uint Tick { get; private set; }

    public int PlayerCount => _players.Count;

    public IReadOnlyList<Player> Players => _players;

    public IReadOnlyList<Mob> Mobs => _mobs;

    public ZoneDef Zone => _zone;

    public ContentPack Content => _content;

    public Entity? Find(uint entityId) => _byId.GetValueOrDefault(entityId);

    /// <summary>
    /// Test and tool convenience: joins a fresh, unstored character (id 0, never checkpointed).
    /// Ids content does not define fall back to Operative/Baseline.
    /// </summary>
    public Player Join(ClientRef client, ulong accountId, string name, Profession profession = Profession.Operative, Origin origin = Origin.Baseline)
    {
        if (!_content.Professions.ContainsId((int)profession))
        {
            profession = Profession.Operative;
        }
        if (!_content.Origins.ContainsId((int)origin))
        {
            origin = Origin.Baseline;
        }
        return Join(client, CharacterRecords.New(_content, accountId, name, profession, origin))!;
    }

    /// <summary>
    /// Adds a stored character and sends them <see cref="Welcome"/>. A character saved in this zone
    /// alive comes back where it was with the health it had; otherwise it starts at a spawn point
    /// with full health. Returns null if content no longer defines its profession or origin.
    /// The character must not already be in the zone (see <see cref="FindCharacter"/>).
    /// </summary>
    public Player? Join(ClientRef client, CharacterRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_byClient.ContainsKey(client))
        {
            throw new InvalidOperationException($"{client} already joined.");
        }
        if (record.Id != 0 && _byCharacter.ContainsKey(record.Id))
        {
            throw new InvalidOperationException($"character {record.Id} is already in the zone.");
        }
        if (record.ToBuild(_content) is not { } build)
        {
            return null;
        }
        var outfit = new Outfit(_content, build.Character, _content.NewCharacter.InventorySlots);
        if (record.Items.Count == 0)
        {
            // Characters created before items existed (M2.3 development stores) get their kit.
            record = record with { Items = CharacterRecords.New(_content, record.AccountId, record.Name, record.Profession, record.Origin).Items };
        }
        var skipped = record.LoadItems(outfit);
        var player = new Player(_nextEntityId++, client, record, build, outfit, _content.UnarmedWeapon);
        if (record.Placement is { Health: > 0 } saved && saved.Zone == _zone.Key)
        {
            player.Position = saved.Position;
            player.Health = Math.Min(saved.Health, player.MaxHealth);
        }
        else
        {
            player.Position = SpawnPoint(client);
            player.Health = player.MaxHealth; // after items: they can raise the maximum
        }
        player.LastCheckedStatsRevision = player.Character.Stats.Revision;
        player.LastSavedKey = player.SaveKey;
        if (record.Id != 0)
        {
            _byCharacter.Add(record.Id, player);
        }
        _byClient.Add(client, player);
        _players.Add(player);
        Add(player);

        Send(player, new Welcome
        {
            EntityId = player.EntityId,
            ZoneId = _zone.Id,
            ServerTick = Tick,
            TickRateHz = (ushort)_options.TickRateHz,
            X = player.Position.X,
            Z = player.Position.Z,
            ContentHash = _content.Hash,
        });
        SendStatusIfChanged(player);
        SendSheetIfChanged(player);
        SendInventoryIfChanged(player);
        if (skipped.Count > 0)
        {
            // Content removed or reshaped an item since the save: say so rather than vanish it silently.
            SendNotice(player, NoticeInfo, $"{skipped.Count} saved item(s) no longer exist and were removed.");
        }
        return player;
    }

    /// <summary>The player playing this stored character, if it is in the zone.</summary>
    public Player? FindCharacter(ulong characterId) => _byCharacter.GetValueOrDefault(characterId);

    /// <summary>The player's character as it would be saved now. Version is the one it was loaded with.</summary>
    public CharacterRecord Snapshot(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var health = player.Dead ? 0 : player.Health;
        return player.Record.WithProgress(player.Build, new Placement(_zone.Key, player.Position, health), CharacterRecords.ItemsOf(player.Outfit));
    }

    /// <summary>
    /// Characters to save since the last call: changed players at their checkpoint, and everyone
    /// who left. The host hands them to the store; the simulation does no I/O.
    /// </summary>
    public IReadOnlyList<CharacterRecord> DrainCheckpoints()
    {
        if (_checkpoints.Count == 0)
        {
            return [];
        }
        var drained = _checkpoints;
        _checkpoints = [];
        return drained;
    }

    /// <summary>Checkpoints every changed character now (shutdown).</summary>
    public void CheckpointAll()
    {
        foreach (var p in _players)
        {
            CheckpointIfChanged(p);
        }
    }

    private void CheckpointIfChanged(Player p)
    {
        var key = p.SaveKey;
        if (p.CharacterId == 0 || key == p.LastSavedKey)
        {
            return;
        }
        p.LastSavedKey = key;
        _checkpoints.Add(Snapshot(p));
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
        if (player.CharacterId != 0)
        {
            _byCharacter.Remove(player.CharacterId);
            _checkpoints.Add(Snapshot(player)); // always save on leave, changed or not
        }
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
            SettleItems(p);
            SendInventoryIfChanged(p);
            SendStatusIfChanged(p);
            SendSheetIfChanged(p);
        }

        var checkpointEvery = (uint)_options.CheckpointEveryTicks;
        foreach (var p in _players)
        {
            if ((Tick + p.EntityId) % checkpointEvery == 0)
            {
                CheckpointIfChanged(p);
            }
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
        if (text.StartsWith('/'))
        {
            Command(speaker, text); // commands are never broadcast
            return;
        }
        var chat = new ChatMessage { FromEntityId = speaker.EntityId, FromName = speaker.Name, Text = text };
        Broadcast(speaker.Position, _options.ChatRadiusMm, chat);
    }

    public void OnTrainSkill(ClientRef context, in TrainSkill message)
    {
        var p = _byClient[context];
        var stat = (Stat)message.Stat;
        var result = Enum.IsDefined(stat) ? p.Build.Train(stat, message.Points) : TrainResult.NotTrainable;
        if (result != TrainResult.Trained)
        {
            SendNotice(p, NoticeRefused, result switch
            {
                TrainResult.NotEnoughPoints => "Not enough skill points.",
                TrainResult.AboveCap => $"{stat} cannot go above {p.Build.Cap(stat)} at your level.",
                TrainResult.Locked => $"Your profession cannot train {stat}.",
                TrainResult.InvalidAmount => "Invalid amount.",
                _ => "That cannot be trained.",
            });
        }
        // Health pools may have grown (BodyDevelopment, Stamina); clamp in case they shrank.
        p.Health = Math.Min(p.Health, p.MaxHealth);
    }

    public void OnSnapshotAck(ClientRef context, in SnapshotAck message) =>
        _byClient[context].History.Acknowledge(message.Tick);

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
            e.Position = Movement.Step(e.Position, e.DirX, e.DirZ, e.CurrentSpeed, dt, _zone.Bounds);
            e.Yaw = Movement.YawByte(e.DirX, e.DirZ, e.Yaw);
        }
    }

    // ------------------------------------------------------------------ status, snapshots

    private void SendStatusIfChanged(Player p)
    {
        var key = new SelfStatusKey(p.Health, p.MaxHealth, p.TargetId, p.Attacking, p.Dead, p.CurrentSpeed);
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
            RunSpeed = p.CurrentSpeed,
        });
    }

    private const byte NoticeRefused = 1, NoticeInfo = 2;

    private void SendNotice(Player to, byte kind, string text) =>
        Send(to, new Notice { Kind = kind, Text = text.Length > 150 ? text[..150] : text });

    /// <summary>Sends the full character sheet when stats, experience or points changed.</summary>
    private void SendSheetIfChanged(Player p)
    {
        var build = p.Build;
        var key = (p.Character.Stats.Revision, build.Xp, build.UnspentPoints);
        if (p.LastSentSheet == key)
        {
            return;
        }
        p.LastSentSheet = key;
        var stats = new StatValue[StatIndex.All.Count];
        for (var i = 0; i < stats.Length; i++)
        {
            var stat = StatIndex.All[i];
            stats[i] = new StatValue { Stat = (ushort)stat, BaseValue = p.Character.Stats.GetBase(stat), Value = p.Character.Get(stat) };
        }
        Send(p, new CharacterSheet
        {
            Profession = (byte)p.Character.Profession,
            Origin = (byte)p.Character.Origin,
            Level = (ushort)build.Level,
            Xp = (uint)Math.Min(uint.MaxValue, build.Xp),
            XpToNext = (uint)Math.Min(uint.MaxValue, build.XpToNext),
            UnspentPoints = build.UnspentPoints,
            Stats = stats,
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

        var entities = _stateBuffer.AsSpan(0, count);
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
        viewer.LastVisible.Clear();
        viewer.LastVisible.UnionWith(_visibleIds);

        // Delta against the newest snapshot this client acknowledged; full state if none.
        var baseline = viewer.History.Baseline(out var baselineTick);
        _removed.Clear();
        if (baseline is not null)
        {
            foreach (var id in baseline.Keys)
            {
                if (!_visibleIds.Contains(id))
                {
                    _removed.Add(id);
                }
            }
            if (_removed.Count > MaxRemovedPerSnapshot)
            {
                // A huge view change (teleport): cheaper and simpler to resend everything.
                baseline = null;
                baselineTick = 0;
                _removed.Clear();
            }
        }
        var length = EntityDeltaCodec.Encode(entities, baseline, _deltaBuffer);
        viewer.History.Record(Tick, entities);

        Send(viewer, new DeltaSnapshot
        {
            Tick = Tick,
            BaselineTick = baselineTick,
            AckedInputSequence = viewer.LastInputSequence,
            Entities = _deltaBuffer.AsSpan(0, length).ToArray(),
            Removed = [.. _removed],
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
