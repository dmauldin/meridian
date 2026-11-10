using System.Diagnostics;
using Meridian.Core.World;
using Meridian.Net.Client;
using Meridian.Wire;

namespace Meridian.Bots;

/// <summary>
/// A scripted client driven by <see cref="Tick"/>. Wanders and chats by default; in hunt mode it
/// targets the nearest living mob, closes to rifle range and auto-attacks.
/// </summary>
internal sealed class Bot : IClientBoundHandler<GameConnection>, IDisposable
{
    private const int HuntRadiusMm = 100_000;
    private const int EngageRangeMm = 20_000;

    private readonly Random _rng;
    private readonly bool _hunt;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly Dictionary<uint, Known> _world = [];
    private long _nextMove, _nextTurn, _nextPing, _nextChat, _nextThink;
    private short _dirX, _dirZ;
    private uint _sequence;
    private Position _position;
    private uint _target;
    private bool _attacking;

    public Bot(int index, LoginResponse login, string host, int port, bool hunt)
    {
        Index = index;
        _hunt = hunt;
        _rng = new Random(index);
        Connection = new GameConnection(this);
        Connection.Connect(host, port, login);
    }

    public int Index { get; }

    public GameConnection Connection { get; }

    public uint EntityId { get; private set; }

    public bool Welcomed { get; private set; }

    public long Snapshots { get; private set; }

    public long FirstSnapshotTimestamp { get; private set; }

    public int MaxEntitiesSeen { get; private set; }

    public long ChatsHeard { get; private set; }

    public long Hits { get; private set; }

    public long Kills { get; private set; }

    public long Deaths { get; private set; }

    public List<int> RttSamplesMs { get; } = [];

    public void Tick(long now)
    {
        Connection.Poll();
        if (!Connection.Connected || !Welcomed)
        {
            return;
        }
        var ms = Stopwatch.Frequency / 1000;

        if (_hunt)
        {
            if (now >= _nextThink)
            {
                Hunt();
                _nextThink = now + (500 * ms);
            }
        }
        else if (now >= _nextTurn)
        {
            var angle = _rng.NextDouble() * Math.PI * 2;
            var standing = _rng.Next(5) == 0;
            _dirX = standing ? (short)0 : (short)(Math.Sin(angle) * Movement.DirectionScale);
            _dirZ = standing ? (short)0 : (short)(Math.Cos(angle) * Movement.DirectionScale);
            _nextTurn = now + (_rng.Next(2_000, 5_000) * ms);
        }

        if (now >= _nextMove)
        {
            // 10 Hz input, matching what a real client sends while a key is held.
            Connection.Send(new MoveIntent { Sequence = ++_sequence, DirX = _dirX, DirZ = _dirZ });
            _nextMove = now + (100 * ms);
        }
        if (now >= _nextPing)
        {
            Connection.Send(new Ping { ClientTimeMs = ElapsedMs(now) });
            _nextPing = now + (1_000 * ms);
        }
        if (now >= _nextChat)
        {
            if (_nextChat != 0 && !_hunt)
            {
                Connection.Send(new ChatSay { Text = $"bot {Index} checking in" });
            }
            _nextChat = now + (_rng.Next(20_000, 40_000) * ms);
        }
    }

    private void Hunt()
    {
        // Keep the current target while it lives and stays near; otherwise pick the nearest mob.
        if (!_world.TryGetValue(_target, out var current) || current.Dead || current.Position.DistanceSquared(_position) > (long)HuntRadiusMm * HuntRadiusMm)
        {
            var best = 0u;
            var bestDistance = long.MaxValue;
            foreach (var (id, k) in _world)
            {
                var d = k.Position.DistanceSquared(_position);
                if (k.Kind == 2 && !k.Dead && d < bestDistance && d <= (long)HuntRadiusMm * HuntRadiusMm)
                {
                    best = id;
                    bestDistance = d;
                }
            }
            if (best != _target)
            {
                _target = best;
                Connection.Send(new SetTarget { EntityId = best });
            }
        }

        if (_target == 0 || !_world.TryGetValue(_target, out var target))
        {
            _dirX = _dirZ = 0;
            return;
        }
        if (target.Position.DistanceSquared(_position) > (long)EngageRangeMm * EngageRangeMm)
        {
            (_dirX, _dirZ) = Movement.DirectionTowards(_position, target.Position);
        }
        else
        {
            _dirX = _dirZ = 0;
        }
        if (!_attacking)
        {
            Connection.Send(new SetAttack { Enabled = true });
        }
    }

    public void OnWelcome(GameConnection context, in Welcome message)
    {
        EntityId = message.EntityId;
        _position = new Position(message.X, message.Z);
        Welcomed = true;
    }

    public void OnSnapshot(GameConnection context, in Snapshot message)
    {
        if (Snapshots == 0)
        {
            FirstSnapshotTimestamp = Stopwatch.GetTimestamp();
        }
        Snapshots++;
        MaxEntitiesSeen = Math.Max(MaxEntitiesSeen, message.Entities?.Length ?? 0);
        foreach (var e in message.Entities ?? [])
        {
            var pos = new Position(e.X, e.Z);
            if (e.EntityId == EntityId)
            {
                _position = pos;
            }
            var kind = _world.TryGetValue(e.EntityId, out var k) ? k.Kind : (byte)0;
            _world[e.EntityId] = new Known(kind, pos, (e.Flags & 1) != 0);
        }
        foreach (var id in message.Removed ?? [])
        {
            _world.Remove(id);
        }
    }

    public void OnEntityInfo(GameConnection context, in EntityInfo message)
    {
        var known = _world.GetValueOrDefault(message.EntityId);
        _world[message.EntityId] = known with { Kind = message.Kind };
    }

    public void OnCombatEvent(GameConnection context, in CombatEvent message)
    {
        if (message.AttackerId == EntityId && message.Outcome != 0)
        {
            Hits++;
        }
    }

    public void OnEntityDied(GameConnection context, in EntityDied message)
    {
        if (message.KillerId == EntityId)
        {
            Kills++;
        }
        if (message.EntityId == EntityId)
        {
            Deaths++;
        }
        if (_world.TryGetValue(message.EntityId, out var k))
        {
            _world[message.EntityId] = k with { Dead = true };
        }
    }

    public void OnSelfStatus(GameConnection context, in SelfStatus message) => _attacking = message.Attacking;

    public void OnRespawned(GameConnection context, in Respawned message) => _position = new Position(message.X, message.Z);

    public void OnChatMessage(GameConnection context, in ChatMessage message) => ChatsHeard++;

    public void OnPong(GameConnection context, in Pong message) =>
        RttSamplesMs.Add((int)(ElapsedMs(Stopwatch.GetTimestamp()) - message.ClientTimeMs));

    public void OnDisconnecting(GameConnection context, in Disconnecting message)
    {
    }

    public void Dispose() => Connection.Dispose();

    private uint ElapsedMs(long now) => (uint)((now - _startTimestamp) * 1000 / Stopwatch.Frequency);

    private readonly record struct Known(byte Kind, Position Position, bool Dead);
}
