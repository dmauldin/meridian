using System.Diagnostics;
using Meridian.Core.World;
using Meridian.Net.Client;
using Meridian.Wire;

namespace Meridian.Bots;

/// <summary>A scripted client: wanders, pings and occasionally talks. Driven by <see cref="Tick"/>.</summary>
internal sealed class Bot : IClientBoundHandler<GameConnection>, IDisposable
{
    private readonly Random _rng;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _nextMove, _nextTurn, _nextPing, _nextChat;
    private short _dirX, _dirZ;
    private uint _sequence;

    public Bot(int index, LoginResponse login, string host, int port)
    {
        Index = index;
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

    public List<int> RttSamplesMs { get; } = [];

    public void Tick(long now)
    {
        Connection.Poll();
        if (!Connection.Connected || !Welcomed)
        {
            return;
        }
        var ms = Stopwatch.Frequency / 1000;

        if (now >= _nextTurn)
        {
            var angle = _rng.NextDouble() * Math.PI * 2;
            var walking = _rng.Next(5) == 0; // sometimes stand still
            _dirX = walking ? (short)0 : (short)(Math.Sin(angle) * Movement.DirectionScale);
            _dirZ = walking ? (short)0 : (short)(Math.Cos(angle) * Movement.DirectionScale);
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
            if (_nextChat != 0)
            {
                Connection.Send(new ChatSay { Text = $"bot {Index} checking in" });
            }
            _nextChat = now + (_rng.Next(20_000, 40_000) * ms);
        }
    }

    public void OnWelcome(GameConnection context, in Welcome message)
    {
        EntityId = message.EntityId;
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
    }

    public void OnChatMessage(GameConnection context, in ChatMessage message) => ChatsHeard++;

    public void OnPong(GameConnection context, in Pong message) =>
        RttSamplesMs.Add((int)(ElapsedMs(Stopwatch.GetTimestamp()) - message.ClientTimeMs));

    public void OnDisconnecting(GameConnection context, in Disconnecting message)
    {
    }

    public void Dispose() => Connection.Dispose();

    private uint ElapsedMs(long now) => (uint)((now - _startTimestamp) * 1000 / Stopwatch.Frequency);
}
