using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Stats;
using Meridian.Core.World;
using Meridian.Net.Client;
using Meridian.Wire;
using Meridian.Wire.Delta;

namespace Meridian.ClientState;

/// <summary>
/// Everything the client knows about the zone, built from server messages. The Godot layer reads
/// it every frame; nothing here touches the engine.
/// </summary>
/// <remarks>
/// Time: callers set <see cref="NowMs"/> (a monotonic local clock) before delivering messages, so
/// the model is deterministic in tests. Server time is estimated from snapshot ticks.
/// </remarks>
public sealed class ClientWorld : IClientBoundHandler<GameConnection>
{
    /// <summary>Remote entities are drawn this far behind the newest server time.</summary>
    public const double InterpolationDelayMs = 100;

    private readonly Dictionary<uint, RemoteEntity> _entities = [];
    private readonly List<GameEvent> _events = [];
    private readonly SnapshotAssembler _assembler = new();
    private readonly HashSet<uint> _seen = [];
    private double _serverOffsetMs = double.NaN;

    /// <summary>Local monotonic time in ms; set by the owner before polling the connection.</summary>
    public double NowMs { get; set; }

    /// <summary>Latest round-trip estimate, used to reconcile prediction.</summary>
    public int RoundTripMs { get; set; }

    public bool InWorld { get; private set; }

    public uint SelfId { get; private set; }

    public int TickMs { get; private set; } = 50;

    public LocalPlayer Self { get; } = new();

    public SelfStatus Status { get; private set; }

    /// <summary>
    /// The player's character, rebuilt from the latest <c>CharacterSheet</c> with the same rules
    /// the server uses, so the UI can show exact costs and caps. Null until the first sheet.
    /// </summary>
    public CharacterBuild? Build { get; private set; }

    /// <summary>The player's lattice as the server last sent it; null until the first update.</summary>
    public ClientProtocols? Protocols { get; private set; }

    /// <summary>The player's items as the server last sent them; null until the first update.</summary>
    public ClientInventory? Inventory { get; private set; }

    /// <summary>Effective stat values from the latest sheet (base values live in <see cref="Build"/>).</summary>
    public IReadOnlyDictionary<Stat, int> EffectiveStats => _effective;

    private readonly Dictionary<Stat, int> _effective = [];

    public IReadOnlyDictionary<uint, RemoteEntity> Entities => _entities;

    /// <summary>Server time to draw remote entities at.</summary>
    public double RenderServerMs => double.IsNaN(_serverOffsetMs) ? 0 : NowMs + _serverOffsetMs - InterpolationDelayMs;

    /// <summary>Queues a client-side message for the chat log (command feedback).</summary>
    public void AddLocalNotice(string text) => _events.Add(new SystemNotice(text));

    /// <summary>Returns and clears the events since the last call, oldest first.</summary>
    public IReadOnlyList<GameEvent> DrainEvents()
    {
        if (_events.Count == 0)
        {
            return [];
        }
        var copy = _events.ToArray();
        _events.Clear();
        return copy;
    }

    /// <summary>A display name for an entity id, for chat and combat text.</summary>
    public string NameOf(uint entityId) =>
        entityId == SelfId ? "You" : _entities.GetValueOrDefault(entityId)?.Description?.Name ?? $"#{entityId}";

    /// <summary>
    /// Living mobs ordered by distance from the player; used for Tab-targeting. Returns the next
    /// one after <paramref name="current"/> so repeated presses cycle.
    /// </summary>
    public uint NextMobTarget(uint current, int maxRangeMm = 50_000)
    {
        var self = Self.Position;
        var r2 = (long)maxRangeMm * maxRangeMm;
        var candidates = _entities.Values
            .Where(e => e.Description?.IsMob == true && e.HasSamples && !e.Latest.Dead)
            .Select(e => (e.EntityId, Distance: e.Latest.Position.DistanceSquared(self)))
            .Where(c => c.Distance <= r2)
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.EntityId)
            .Select(c => c.EntityId)
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }
        var i = candidates.IndexOf(current);
        return candidates[(i + 1) % candidates.Count];
    }

    /// <summary>The content this client was built with; must match the server's.</summary>
    public ulong ContentHash { get; init; } = Meridian.Content.ContentPacks.Embedded.Hash;

    public void OnWelcome(GameConnection context, in Welcome message)
    {
        if (message.ContentHash != ContentHash)
        {
            // Different item stats, Protocols or zones would make every tooltip and prediction lie.
            context?.Abort($"Game data does not match the server (client {ContentHash:X16}, server {message.ContentHash:X16}). Please update.");
            return;
        }
        SelfId = message.EntityId;
        TickMs = 1_000 / Math.Max(1, (int)message.TickRateHz);
        _serverOffsetMs = (message.ServerTick * (double)TickMs) - NowMs;
        Self.Teleport(new Position(message.X, message.Z), NowMs);
        InWorld = true;
    }

    public void OnDeltaSnapshot(GameConnection context, in DeltaSnapshot message)
    {
        if (!_assembler.TryApply(message, out var entities))
        {
            return; // stale, duplicate, or its baseline is gone: the next one recovers
        }
        // Acknowledge so the server deltas against this one. The context is null in unit tests.
        context?.Send(new SnapshotAck { Tick = message.Tick });

        var serverMs = message.Tick * (double)TickMs;
        TrackServerClock(serverMs);

        _seen.Clear();
        foreach (var e in entities)
        {
            _seen.Add(e.EntityId);
            var position = new Position(e.X, e.Z);
            if (e.EntityId == SelfId)
            {
                if (!Status.Dead)
                {
                    Self.Reconcile(position, NowMs, RoundTripMs);
                }
                continue;
            }
            if (!_entities.TryGetValue(e.EntityId, out var remote))
            {
                remote = new RemoteEntity(e.EntityId);
                _entities.Add(e.EntityId, remote);
            }
            remote.AddSample(new EntitySample(serverMs, position, e.Yaw, e.HealthPercent, e.Flags));
        }

        // The rebuilt snapshot is the complete visible set: anything else has left view.
        foreach (var id in _entities.Keys.Where(id => !_seen.Contains(id)).ToList())
        {
            if (_entities[id].HasSamples)
            {
                _entities.Remove(id);
            }
        }
    }

    public void OnEntityInfo(GameConnection context, in EntityInfo message)
    {
        if (message.EntityId == SelfId)
        {
            return;
        }
        if (!_entities.TryGetValue(message.EntityId, out var remote))
        {
            remote = new RemoteEntity(message.EntityId);
            _entities.Add(message.EntityId, remote);
        }
        remote.Description = new EntityDescription(message.Kind, message.TemplateId, message.Name ?? "?", message.Level);
    }

    public void OnCombatEvent(GameConnection context, in CombatEvent message) =>
        _events.Add(new CombatText(message.AttackerId, message.TargetId, (HitOutcome)Math.Min(message.Outcome, (byte)3), (int)message.Damage));

    public void OnEntityDied(GameConnection context, in EntityDied message)
    {
        _entities.GetValueOrDefault(message.EntityId)?.MarkDead();
        _events.Add(new EntityKilled(message.EntityId, message.KillerId));
    }

    public void OnChatMessage(GameConnection context, in ChatMessage message) =>
        _events.Add(new ChatLine(message.FromEntityId, message.FromName ?? "?", message.Text ?? string.Empty));

    public void OnSelfStatus(GameConnection context, in SelfStatus message)
    {
        if (message.Dead && !Status.Dead)
        {
            _events.Add(new SystemNotice("You died. Reclamation in progress..."));
        }
        Status = message;
    }

    public void OnRespawned(GameConnection context, in Respawned message)
    {
        Self.Teleport(new Position(message.X, message.Z), NowMs);
        _events.Add(new SystemNotice("You wake in a reclamation bay."));
    }

    public void OnPong(GameConnection context, in Pong message)
    {
    }

    public void OnCharacterSheet(GameConnection context, in CharacterSheet message)
    {
        var bases = new List<KeyValuePair<Stat, int>>();
        _effective.Clear();
        foreach (var s in message.Stats ?? [])
        {
            var stat = (Stat)s.Stat;
            if (Enum.IsDefined(stat))
            {
                bases.Add(new(stat, s.BaseValue));
                _effective[stat] = s.Value;
            }
        }
        var pack = Meridian.Content.ContentPacks.Embedded;
        var profession = (Profession)message.Profession;
        var origin = (Origin)message.Origin;
        if (pack.Professions.ContainsId((int)profession) && pack.Origins.ContainsId((int)origin))
        {
            Build = CharacterBuild.Restore(pack, profession, origin, message.Level, message.Xp, message.UnspentPoints, bases);
        }
    }

    public void OnXpGained(GameConnection context, in XpGained message) =>
        _events.Add(new ExperienceGained((int)message.Amount, message.Level, message.LevelsGained));

    public void OnNotice(GameConnection context, in Notice message) =>
        _events.Add(new SystemNotice(message.Text ?? ""));

    public void OnProtocolState(GameConnection context, in ProtocolState message)
    {
        Protocols = new ClientProtocols(
            [.. (message.Known ?? []).Select(id => (int)id)],
            [.. (message.Running ?? []).Select(r => (ProtocolId: (int)r.ProtocolId, RemainingMs: (int)r.RemainingMs))],
            message.LatticeUsed,
            message.LatticeCapacity);
        _events.Add(new ProtocolsChanged());
    }

    public void OnInventoryState(GameConnection context, in InventoryState message)
    {
        Inventory = new ClientInventory(
            message.Size,
            [.. (message.Carried ?? []).Select(c => (Position: (int)c.Position, Item: new OwnedItem((int)c.TemplateId, c.Ql)))],
            [.. (message.Worn ?? []).Select(w => (Slot: (EquipSlot)w.Slot, Item: new OwnedItem((int)w.TemplateId, w.Ql), w.Active))]);
        _events.Add(new InventoryChanged());
    }

    public void OnDisconnecting(GameConnection context, in Disconnecting message) =>
        _events.Add(new SystemNotice(message.Reason ?? "Disconnected."));

    /// <summary>
    /// Keeps an offset from local to server time. The least-delayed snapshot gives the best
    /// estimate, so a higher sample is taken at once and lower ones only pull it down slowly
    /// (which also absorbs clock drift).
    /// </summary>
    private void TrackServerClock(double serverMs)
    {
        var sample = serverMs - NowMs;
        if (double.IsNaN(_serverOffsetMs) || sample > _serverOffsetMs)
        {
            _serverOffsetMs = sample;
        }
        else
        {
            _serverOffsetMs += (sample - _serverOffsetMs) * 0.02;
        }
    }
}
