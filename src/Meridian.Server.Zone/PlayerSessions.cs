using System.Collections.Concurrent;
using Meridian.Persistence;

namespace Meridian.Server.Zone;

/// <summary>
/// Who is joining, playing and leaving, and how their characters get to and from the store.
/// Everything except the load itself runs on the tick thread: loads run on the thread pool and
/// post their result back (<see cref="RunQueued"/>), so store latency never stalls a tick.
/// No sockets here, so the ordering rules are testable directly.
/// </summary>
public sealed class PlayerSessions
{
    private readonly ZoneSimulation _sim;
    private readonly IGameStore _store;
    private readonly CharacterSaver _saver;
    private readonly Action<ClientRef, string> _drop;
    private readonly Action<ClientRef, uint> _transfer;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Action> _onTickThread = new();
    private readonly Dictionary<ClientRef, ulong> _pendingJoins = [];

    /// <param name="drop">Ends a client's session with a reason it can show (sent via its gateway).</param>
    /// <param name="transfer">Asks the client's gateway to route it to another zone (its character is saved there).</param>
    public PlayerSessions(ZoneSimulation sim, IGameStore store, CharacterSaver saver, Action<ClientRef, string> drop, Action<string> log, Action<ClientRef, uint>? transfer = null)
    {
        ArgumentNullException.ThrowIfNull(saver);
        _sim = sim;
        _store = store;
        _saver = saver;
        _drop = drop;
        _log = log;
        _transfer = transfer ?? ((_, _) => { });
        saver.OwnershipLost += id => _onTickThread.Enqueue(() =>
        {
            if (_sim.FindCharacter(id) is { } p)
            {
                _drop(p.Client, "Your character was saved by another server. Please log in again.");
                Leave(p.Client);
            }
        });
    }

    public int PendingJoins => _pendingJoins.Count;

    /// <summary>How long a departure waits for its save before the client is dropped instead.</summary>
    public TimeSpan DepartureSaveTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Hands each departure to the destination: save the character (already placed there) and,
    /// once the store confirms it, ask the gateway to re-route the client. The destination then
    /// loads exactly that save. If the save cannot be confirmed the client is dropped: logging in
    /// again puts them wherever the store says. Call on the tick thread after each step.
    /// </summary>
    public void ProcessDepartures()
    {
        foreach (var departure in _sim.DrainDepartures())
        {
            var saved = _saver.EnqueueAndConfirm(departure.Record);
            _ = Task.Run(async () =>
            {
                var finished = await Task.WhenAny(saved, Task.Delay(DepartureSaveTimeout)).ConfigureAwait(false);
                var ok = finished == saved && await saved.ConfigureAwait(false);
                _onTickThread.Enqueue(() =>
                {
                    if (ok)
                    {
                        _transfer(departure.Client, departure.ToZoneId);
                    }
                    else
                    {
                        _log($"hand-off of character {departure.Record.Id} to zone {departure.ToZoneId} could not be saved");
                        _drop(departure.Client, "Travel failed. Please log in again.");
                    }
                });
            });
        }
    }

    /// <summary>Runs work posted from other threads (finished loads). Call on the tick thread.</summary>
    public void RunQueued()
    {
        while (_onTickThread.TryDequeue(out var work))
        {
            work();
        }
    }

    /// <summary>Hands the simulation's checkpoints to the saver. Call after every step and every leave.</summary>
    public void QueueCheckpoints()
    {
        foreach (var record in _sim.DrainCheckpoints())
        {
            _saver.Enqueue(record);
        }
    }

    /// <summary>
    /// Starts loading the character for <paramref name="client"/>. The newest unsaved copy in the
    /// saver beats the store's (a quick reconnect must not roll back progress).
    /// </summary>
    public void BeginJoin(ClientRef client, ulong accountId, ulong characterId)
    {
        if (characterId == 0 || !_pendingJoins.TryAdd(client, characterId))
        {
            _log($"rejected join for {client}: no character, or a join already pending");
            return;
        }
        // Anything a leave left in the simulation must be in the saver before the load looks there,
        // or a reconnect in the same tick would read the store's older copy.
        QueueCheckpoints();
        _ = Task.Run(async () =>
        {
            CharacterRecord? record = null;
            string? failure = null;
            try
            {
                record = _saver.Latest(characterId) ?? await _store.LoadCharacterAsync(characterId).ConfigureAwait(false);
                if (record is not null)
                {
                    _saver.Loaded(record.Id, record.Version);
                }
            }
#pragma warning disable CA1031 // any store failure becomes a polite refusal, never a crash
            catch (Exception e)
#pragma warning restore CA1031
            {
                failure = e.Message;
            }
            _onTickThread.Enqueue(() => CompleteJoin(client, accountId, characterId, record, failure));
        });
    }

    public void Leave(ClientRef client)
    {
        _pendingJoins.Remove(client);
        _sim.Leave(client);
        QueueCheckpoints(); // the final save goes to the saver now, not after the next tick
    }

    /// <summary>Everyone (playing or still loading) who came through a gateway that went away.</summary>
    public int LeaveGateway(int gatewayId)
    {
        foreach (var pending in _pendingJoins.Keys.Where(c => c.GatewayId == gatewayId).ToList())
        {
            _pendingJoins.Remove(pending);
        }
        var dropped = _sim.LeaveGateway(gatewayId);
        QueueCheckpoints();
        return dropped;
    }

    private void CompleteJoin(ClientRef client, ulong accountId, ulong characterId, CharacterRecord? loaded, string? failure)
    {
        if (!_pendingJoins.Remove(client, out var pendingId) || pendingId != characterId)
        {
            return; // the client left while we were loading
        }
        CharacterRecord record;
        if (_sim.FindCharacter(characterId) is { } online)
        {
            // Same character, new session: the live state is the truth. The old session goes.
            record = _sim.Snapshot(online);
            _drop(online.Client, "Logged in from another session.");
            Leave(online.Client);
        }
        else if (failure is not null)
        {
            _log($"loading character {characterId} for {client} failed: {failure}");
            _drop(client, "Could not load your character. Please try again.");
            return;
        }
        else if (loaded is null || loaded.AccountId != accountId)
        {
            _log($"rejected join for {client}: character {characterId} is missing or not account {accountId}'s");
            _drop(client, "Character not found.");
            return;
        }
        else
        {
            record = loaded;
        }

        if (_sim.Join(client, record) is null)
        {
            _drop(client, "This character's profession or origin is no longer playable.");
        }
    }
}
