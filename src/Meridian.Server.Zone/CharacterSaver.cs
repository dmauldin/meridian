using Meridian.Persistence;

namespace Meridian.Server.Zone;

/// <summary>
/// Write-behind saving for one zone. The tick thread hands over records (<see cref="Enqueue"/>);
/// a background loop writes them in batches. Per character the newest record wins and saves run
/// in order, so the saver is the one place that tracks the version each character is at. That
/// keeps a slow save from ever turning the next one into a version conflict.
///
/// A real conflict means something else saved the character (another zone, an admin tool): the
/// saver stops saving it, never overwrites, and reports it through <see cref="OwnershipLost"/>.
/// A store failure keeps the record queued and retries, so a database blip loses nothing.
/// </summary>
public sealed class CharacterSaver(IGameStore store, Action<string> log) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, CharacterRecord> _pending = [];   // newest record per character, not yet written
    private readonly Dictionary<ulong, CharacterRecord> _inFlight = [];  // being written now
    private readonly Dictionary<ulong, long> _versions = [];             // version the store holds, as far as we know
    private readonly HashSet<ulong> _lost = [];
    private readonly SemaphoreSlim _wake = new(0);
    private TaskCompletionSource _idle = NewIdle();

    /// <summary>Raised on the saver's thread when a character turns out to be owned elsewhere.</summary>
    public event Action<ulong>? OwnershipLost;

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count + _inFlight.Count;
            }
        }
    }

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Notes the version a character was loaded at. A newer version we already know wins.</summary>
    public void Loaded(ulong characterId, long version)
    {
        lock (_lock)
        {
            if (!_versions.TryGetValue(characterId, out var known) || version > known)
            {
                _versions[characterId] = version;
            }
            _lost.Remove(characterId); // freshly loaded from the store: ours again
        }
    }

    /// <summary>The newest record not yet confirmed written, if any. A join uses it instead of the store's older copy.</summary>
    public CharacterRecord? Latest(ulong characterId)
    {
        lock (_lock)
        {
            return _pending.GetValueOrDefault(characterId) ?? _inFlight.GetValueOrDefault(characterId);
        }
    }

    public void Enqueue(CharacterRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            if (record.Id == 0 || _lost.Contains(record.Id))
            {
                return;
            }
            if (_pending.Count == 0 && _inFlight.Count == 0)
            {
                _idle = NewIdle();
            }
            _pending[record.Id] = record;
        }
        _wake.Release();
    }

    /// <summary>Completes when everything queued so far is written (or <paramref name="timeout"/> passes).</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        Task idle;
        lock (_lock)
        {
            if (_pending.Count == 0 && _inFlight.Count == 0)
            {
                return true;
            }
            idle = _idle.Task;
        }
        return await Task.WhenAny(idle, Task.Delay(timeout)).ConfigureAwait(false) == idle;
    }

    /// <summary>The write loop. Run it on a background task; it ends when <paramref name="stop"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await WriteBatchAsync(stop).ConfigureAwait(false);
        }
    }

    /// <summary>Writes everything pending once. Public so tests can drive the saver without a loop.</summary>
    public async Task WriteBatchAsync(CancellationToken cancellationToken = default)
    {
        List<CharacterRecord> batch;
        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return;
            }
            batch = [];
            foreach (var (id, record) in _pending)
            {
                var version = _versions.GetValueOrDefault(id, record.Version);
                var stamped = record with { Version = version };
                _inFlight[id] = stamped;
                batch.Add(stamped);
            }
            _pending.Clear();
        }

        IReadOnlyList<SaveOutcome> outcomes;
        try
        {
            outcomes = await store.SaveCharactersAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"save of {batch.Count} characters failed, retrying: {e.Message}");
            lock (_lock)
            {
                foreach (var r in batch)
                {
                    _inFlight.Remove(r.Id);
                    _pending.TryAdd(r.Id, r); // a newer record queued meanwhile wins
                }
            }
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            _wake.Release();
            return;
        }

        var lost = new List<ulong>();
        lock (_lock)
        {
            foreach (var outcome in outcomes)
            {
                _inFlight.Remove(outcome.Id);
                if (outcome.Saved)
                {
                    _versions[outcome.Id] = outcome.Version!.Value;
                    continue;
                }
                _lost.Add(outcome.Id);
                _pending.Remove(outcome.Id);
                _versions.Remove(outcome.Id);
                lost.Add(outcome.Id);
                log(outcome.Version is { } v
                    ? $"character {outcome.Id} was saved elsewhere (store has version {v}); this zone stops saving it"
                    : $"character {outcome.Id} no longer exists; dropping its save");
            }
            if (_pending.Count == 0 && _inFlight.Count == 0)
            {
                _idle.TrySetResult();
            }
        }
        foreach (var id in lost)
        {
            OwnershipLost?.Invoke(id);
        }
    }

    public void Dispose() => _wake.Dispose();

    private static TaskCompletionSource NewIdle() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
