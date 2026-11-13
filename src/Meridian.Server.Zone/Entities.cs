using Meridian.Core.Characters;
using Meridian.Core.Combat;
using Meridian.Core.Content;
using Meridian.Core.Stats;
using Meridian.Core.World;
using Meridian.Persistence;

namespace Meridian.Server.Zone;

/// <summary>Wire value of EntityInfo.kind.</summary>
public enum EntityKind : byte
{
    Player = 1,
    Mob = 2,
}

/// <summary>Anything with a position, stats and health: players and mobs alike.</summary>
public abstract class Entity
{
    protected Entity(uint entityId, EntityKind kind, string name, Character character, WeaponProfile weapon)
    {
        EntityId = entityId;
        Kind = kind;
        Name = name;
        Character = character;
        Weapon = weapon;
        Health = character.Get(Stat.MaxHealth);
    }

    public uint EntityId { get; }

    public EntityKind Kind { get; }

    public string Name { get; }

    public Character Character { get; }

    public WeaponProfile Weapon { get; }

    public int Level => Character.Get(Stat.Level);

    public int MaxHealth => Character.Get(Stat.MaxHealth);

    public int Health { get; set; }

    public bool Dead { get; set; }

    public Position Position { get; set; }

    public byte Yaw { get; set; }

    public short DirX { get; set; }

    public short DirZ { get; set; }

    /// <summary>Movement speed this tick, mm/s. Players use RunSpeed; mobs vary by state.</summary>
    public abstract int CurrentSpeed { get; }

    /// <summary>0 = no target.</summary>
    public uint TargetId { get; set; }

    public bool Attacking { get; set; }

    /// <summary>Counts down by the tick length; the next swing happens at or below zero.</summary>
    public int AttackCooldownMs { get; set; }

    /// <summary>Last tick this entity hit or was hit; drives the "in combat" flag.</summary>
    public uint LastCombatTick { get; set; }

    public uint RespawnAtTick { get; set; }

    /// <summary>Health as a percentage for other viewers; 1% minimum while alive.</summary>
    public byte HealthPercent => Dead || Health <= 0 ? (byte)0 : (byte)Math.Clamp(((Health * 100) + MaxHealth - 1) / MaxHealth, 1, 100);
}

/// <summary>A connected player's simulation state.</summary>
public sealed class Player : Entity
{
    public Player(uint entityId, ClientRef client, CharacterRecord record, CharacterBuild build, WeaponProfile weapon)
        : base(entityId, EntityKind.Player, record.Name, build.Character, weapon)
    {
        Client = client;
        Record = record;
        Build = build;
    }

    /// <summary>The stored character as loaded: identity and the version saves are made against.</summary>
    public CharacterRecord Record { get; }

    /// <summary>The stored character's id; 0 for an unstored test character.</summary>
    public ulong CharacterId => Record.Id;

    /// <summary>What a save would capture; when it differs from <see cref="LastSavedKey"/> a checkpoint is due.</summary>
    internal SaveKey SaveKey => new(Character.Stats.Revision, Build.Xp, Build.UnspentPoints, Position, Dead ? 0 : Health);

    internal SaveKey LastSavedKey { get; set; }

    public ClientRef Client { get; }

    /// <summary>Level, experience, skill points and training rules.</summary>
    public CharacterBuild Build { get; }

    public ulong AccountId => Record.AccountId;

    public uint LastInputSequence { get; set; }

    public uint IntentTick { get; set; }

    public override int CurrentSpeed => Character.Get(Stat.RunSpeed);

    internal HashSet<uint> LastVisible { get; } = [];

    /// <summary>What recent snapshots sent, and which one the client acknowledged.</summary>
    internal Meridian.Wire.Delta.SnapshotHistory History { get; } = new();

    internal SelfStatusKey? LastSentStatus { get; set; }

    /// <summary>What the last CharacterSheet reflected, to send it only on change.</summary>
    internal (long StatsRevision, long Xp, int Points)? LastSentSheet { get; set; }
}

/// <summary>What <c>SelfStatus</c> last told the player, to send only changes.</summary>
internal readonly record struct SaveKey(long StatsRevision, long Xp, int Points, Position Position, int Health);

internal readonly record struct SelfStatusKey(int Health, int MaxHealth, uint TargetId, bool Attacking, bool Dead, int RunSpeed);

public enum MobState : byte
{
    Idle,
    Chasing,
    Returning,
    Dead,
}

/// <summary>A spawned creature and its AI state.</summary>
public sealed class Mob : Entity
{
    public Mob(uint entityId, MobDef template, Position spawn, Character character)
        : base(entityId, EntityKind.Mob, template.Name, character, template.Attack)
    {
        Template = template;
        Spawn = spawn;
        Position = spawn;
        WanderTarget = spawn;
    }

    public MobDef Template { get; }

    public Position Spawn { get; }

    public MobState State { get; set; }

    public Position WanderTarget { get; set; }

    public uint NextWanderTick { get; set; }

    /// <summary>Damage dealt to this mob by each attacker; the highest is its target.</summary>
    public Dictionary<uint, long> Threat { get; } = [];

    public override int CurrentSpeed => State switch
    {
        MobState.Chasing or MobState.Returning => Template.SpeedMmPerSecond,
        MobState.Idle => Template.SpeedMmPerSecond / 2,
        _ => 0,
    };
}
