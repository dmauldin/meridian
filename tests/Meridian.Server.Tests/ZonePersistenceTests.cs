using Meridian.Core.Characters;
using Meridian.Core.Stats;
using Meridian.Core.World;
using Meridian.Persistence;
using Meridian.Server.Zone;
using Meridian.Wire;

namespace Meridian.Server.Tests;

public class ZoneCheckpointTests
{
    private static readonly ClientRef A = new(1, 1);
    private static readonly ClientRef B = new(1, 2);
    private static readonly Meridian.Core.Content.ContentPack Pack = Meridian.Content.ContentPacks.Embedded;

    private static ZoneSimulation Zone(int checkpointEvery = 600) =>
        new(new ZoneOptions { MobCount = 0, Seed = 3, CheckpointEveryTicks = checkpointEvery }, new RecordingOutbox());

    private static CharacterRecord Stored(ulong id = 7, Placement? placement = null) =>
        CharacterRecords.New(Pack, 1, "Rook", Profession.Operative, Origin.Baseline) with { Id = id, Version = 3, Placement = placement };

    [Fact]
    public void A_character_saved_alive_here_comes_back_where_it_was()
    {
        var sim = Zone();
        var p = sim.Join(A, Stored(placement: new Placement(sim.Zone.Key, new Position(4_321, -1_234), 40)))!;
        Assert.Equal(new Position(4_321, -1_234), p.Position);
        Assert.Equal(40, p.Health);
    }

    [Theory]
    [InlineData(0, "glasswind_flats")]       // saved dead
    [InlineData(40, "some_other_zone")]      // saved elsewhere
    public void Otherwise_it_starts_at_a_spawn_point_with_full_health(int health, string zone)
    {
        var sim = Zone();
        var p = sim.Join(A, Stored(placement: new Placement(zone, new Position(4_321, -1_234), health)))!;
        Assert.NotEqual(new Position(4_321, -1_234), p.Position);
        Assert.Equal(p.MaxHealth, p.Health);
    }

    [Fact]
    public void Saved_health_never_exceeds_the_current_maximum()
    {
        var sim = Zone();
        var p = sim.Join(A, Stored(placement: new Placement(sim.Zone.Key, default, 9_999)))!;
        Assert.Equal(p.MaxHealth, p.Health);
    }

    [Fact]
    public void A_character_content_no_longer_defines_is_refused()
    {
        Assert.Null(Zone().Join(A, Stored() with { Profession = Profession.Sentinel }));
    }

    [Fact]
    public void One_character_cannot_be_in_the_zone_twice()
    {
        var sim = Zone();
        sim.Join(A, Stored());
        Assert.Throws<InvalidOperationException>(() => sim.Join(B, Stored()));
        Assert.NotNull(sim.FindCharacter(7));
    }

    [Fact]
    public void Leaving_always_checkpoints_with_the_loaded_version()
    {
        var sim = Zone();
        sim.Join(A, Stored());
        sim.Leave(A);
        var saved = Assert.Single(sim.DrainCheckpoints());
        Assert.Equal((7UL, 3L), (saved.Id, saved.Version));
        Assert.Equal(sim.Zone.Key, saved.Placement!.Zone);
        Assert.Null(sim.FindCharacter(7));
        Assert.Empty(sim.DrainCheckpoints());
    }

    [Fact]
    public void Only_changed_characters_are_checkpointed_on_schedule()
    {
        var sim = Zone(checkpointEvery: 10);
        var p = sim.Join(A, Stored())!;
        for (var i = 0; i < 30; i++)
        {
            sim.Step();
        }
        Assert.Empty(sim.DrainCheckpoints()); // idle: nothing to save

        sim.HandleClientPacket(A, Envelope.ToArray(new TrainSkill { Stat = (ushort)Stat.Rifle, Points = 3 }));
        for (var i = 0; i < 10; i++)
        {
            sim.Step();
        }
        var saved = Assert.Single(sim.DrainCheckpoints());
        Assert.Contains(new KeyValuePair<Stat, int>(Stat.Rifle, 3), saved.Trained);
        Assert.Equal(p.Build.UnspentPoints, saved.UnspentPoints);
    }

    [Fact]
    public void Unstored_characters_are_never_checkpointed()
    {
        var sim = Zone(checkpointEvery: 1);
        sim.Join(A, 1, "temp");
        sim.HandleClientPacket(A, Envelope.ToArray(new TrainSkill { Stat = (ushort)Stat.Rifle, Points = 3 }));
        sim.Step();
        sim.CheckpointAll();
        sim.Leave(A);
        Assert.Empty(sim.DrainCheckpoints());
    }

    [Fact]
    public void Shutdown_checkpoints_everyone_who_changed()
    {
        var sim = Zone();
        var p = sim.Join(A, Stored())!;
        sim.Join(B, Stored(id: 8) with { Name = "Wren" });
        p.Position = new Position(1, 1);
        sim.CheckpointAll();
        Assert.Equal([7UL], sim.DrainCheckpoints().Select(r => r.Id));
    }
}

public sealed class CharacterSaverTests : IDisposable
{
    private readonly FlakyStore _store = new();
    private readonly List<string> _log = [];
    private readonly CharacterSaver _saver;

    public CharacterSaverTests() => _saver = new CharacterSaver(_store, _log.Add) { RetryDelay = TimeSpan.Zero };

    public void Dispose() => _saver.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<CharacterRecord> Created(string name = "Rook")
    {
        var account = await _store.CreateAccountAsync("Alice" + name, "$pbkdf2-sha256$i=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA", Ct);
        var draft = CharacterRecords.New(Meridian.Content.ContentPacks.Embedded, account!.Id, name, Profession.Operative, Origin.Baseline);
        var created = (await _store.CreateCharacterAsync(draft, 8, Ct)).Created!;
        _saver.Loaded(created.Id, created.Version);
        return created;
    }

    [Fact]
    public async Task The_newest_record_wins_and_versions_chain_across_batches()
    {
        var rook = await Created();
        _saver.Enqueue(rook with { Xp = 1 });
        _saver.Enqueue(rook with { Xp = 2 });           // replaces the first before it was written
        await _saver.WriteBatchAsync(Ct);
        _saver.Enqueue(rook with { Xp = 3 });           // still says version 1; the saver knows better
        await _saver.WriteBatchAsync(Ct);

        var stored = (await _store.LoadCharacterAsync(rook.Id, Ct))!;
        Assert.Equal((3L, 3L), (stored.Xp, stored.Version));
        Assert.Equal(2, _store.SaveCalls);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task Latest_returns_what_is_queued_until_it_is_written()
    {
        var rook = await Created();
        _saver.Enqueue(rook with { Xp = 50 });
        Assert.Equal(50, _saver.Latest(rook.Id)!.Xp);
        await _saver.WriteBatchAsync(Ct);
        Assert.Null(_saver.Latest(rook.Id));
        Assert.Equal(0, _saver.PendingCount);
    }

    [Fact]
    public async Task A_store_failure_keeps_the_record_and_retries()
    {
        var rook = await Created();
        _store.FailNextSave = true;
        _saver.Enqueue(rook with { Xp = 9 });
        await _saver.WriteBatchAsync(Ct);
        Assert.Equal(9, _saver.Latest(rook.Id)!.Xp);
        Assert.Contains(_log, l => l.Contains("retrying", StringComparison.Ordinal));

        await _saver.WriteBatchAsync(Ct);
        Assert.Equal(9, (await _store.LoadCharacterAsync(rook.Id, Ct))!.Xp);
    }

    [Fact]
    public async Task Someone_else_saving_the_character_stops_us_overwriting_it()
    {
        var rook = await Created();
        var lost = new List<ulong>();
        _saver.OwnershipLost += lost.Add;
        await _store.SaveCharactersAsync([rook with { Xp = 777 }], Ct); // another writer: now version 2

        _saver.Enqueue(rook with { Xp = 1 });
        await _saver.WriteBatchAsync(Ct);
        _saver.Enqueue(rook with { Xp = 2 });                            // ignored from now on
        await _saver.WriteBatchAsync(Ct);

        Assert.Equal([rook.Id], lost);
        Assert.Equal(777, (await _store.LoadCharacterAsync(rook.Id, Ct))!.Xp);
        Assert.Equal(0, _saver.PendingCount);
    }

    [Fact]
    public async Task Loading_again_makes_a_lost_character_ours_again()
    {
        var rook = await Created();
        await _store.SaveCharactersAsync([rook with { Xp = 777 }], Ct);
        _saver.Enqueue(rook);
        await _saver.WriteBatchAsync(Ct);                                // lost

        var reloaded = (await _store.LoadCharacterAsync(rook.Id, Ct))!;
        _saver.Loaded(reloaded.Id, reloaded.Version);
        _saver.Enqueue(reloaded with { Xp = 800 });
        await _saver.WriteBatchAsync(Ct);
        Assert.Equal(800, (await _store.LoadCharacterAsync(rook.Id, Ct))!.Xp);
    }

    [Fact]
    public async Task Flush_waits_for_the_background_loop()
    {
        var rook = await Created();
        using var stop = new CancellationTokenSource();
        var loop = Task.Run(() => _saver.RunAsync(stop.Token), Ct);
        _saver.Enqueue(rook with { Xp = 5 });
        Assert.True(await _saver.FlushAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(5, (await _store.LoadCharacterAsync(rook.Id, Ct))!.Xp);
        await stop.CancelAsync();
        await loop;
    }

    /// <summary>An in-memory store whose next save can be made to throw.</summary>
    private sealed class FlakyStore : IGameStore
    {
        private readonly InMemoryGameStore _inner = new();

        public bool FailNextSave { get; set; }

        public int SaveCalls { get; private set; }

        public Task<AccountRecord?> CreateAccountAsync(string username, string passwordHash, CancellationToken cancellationToken = default) =>
            _inner.CreateAccountAsync(username, passwordHash, cancellationToken);

        public Task<AccountRecord?> FindAccountAsync(string username, CancellationToken cancellationToken = default) =>
            _inner.FindAccountAsync(username, cancellationToken);

        public Task SetPasswordHashAsync(ulong accountId, string passwordHash, CancellationToken cancellationToken = default) =>
            _inner.SetPasswordHashAsync(accountId, passwordHash, cancellationToken);

        public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(ulong accountId, CancellationToken cancellationToken = default) =>
            _inner.ListCharactersAsync(accountId, cancellationToken);

        public Task<(CharacterRecord? Created, CreateCharacterError Error)> CreateCharacterAsync(CharacterRecord draft, int maxPerAccount, CancellationToken cancellationToken = default) =>
            _inner.CreateCharacterAsync(draft, maxPerAccount, cancellationToken);

        public Task<CharacterRecord?> LoadCharacterAsync(ulong characterId, CancellationToken cancellationToken = default) =>
            _inner.LoadCharacterAsync(characterId, cancellationToken);

        public Task<IReadOnlyList<SaveOutcome>> SaveCharactersAsync(IReadOnlyList<CharacterRecord> records, CancellationToken cancellationToken = default)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("database went away");
            }
            SaveCalls++;
            return _inner.SaveCharactersAsync(records, cancellationToken);
        }
    }
}

public sealed class PlayerSessionTests : IDisposable
{
    private static readonly ClientRef A = new(1, 1);
    private static readonly ClientRef B = new(1, 2);
    private static readonly ClientRef C = new(2, 1);
    private readonly InMemoryGameStore _store = new();
    private readonly CharacterSaver _saver;
    private readonly ZoneSimulation _sim = new(new ZoneOptions { MobCount = 0, Seed = 3 }, new RecordingOutbox());
    private readonly List<(ClientRef Client, string Reason)> _dropped = [];
    private readonly PlayerSessions _sessions;

    public PlayerSessionTests()
    {
        _saver = new CharacterSaver(_store, _ => { });
        _sessions = new PlayerSessions(_sim, _store, _saver, (c, r) => _dropped.Add((c, r)), _ => { });
    }

    public void Dispose() => _saver.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<CharacterRecord> Created(string name = "Rook")
    {
        var account = (await _store.FindAccountAsync("Alice", Ct))
            ?? (await _store.CreateAccountAsync("Alice", "$pbkdf2-sha256$i=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA", Ct))!;
        var draft = CharacterRecords.New(Meridian.Content.ContentPacks.Embedded, account.Id, name, Profession.Operative, Origin.Baseline);
        return (await _store.CreateCharacterAsync(draft, 8, Ct)).Created!;
    }

    /// <summary>Runs posted work until <paramref name="done"/>, as the tick thread would.</summary>
    private void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            _sessions.RunQueued();
            Thread.Sleep(1);
        }
    }

    [Fact]
    public async Task A_join_completes_on_the_tick_thread()
    {
        var rook = await Created();
        _sessions.BeginJoin(A, rook.AccountId, rook.Id);
        Assert.Equal(1, _sessions.PendingJoins);
        Assert.Null(_sim.FindCharacter(rook.Id)); // nothing touches the simulation off the tick thread
        PumpUntil(() => _sim.FindCharacter(rook.Id) is not null);
        Assert.Equal(0, _sessions.PendingJoins);
    }

    [Fact]
    public async Task Leaving_and_rejoining_in_the_same_tick_keeps_unsaved_progress()
    {
        var rook = await Created();
        _sessions.BeginJoin(A, rook.AccountId, rook.Id);
        PumpUntil(() => _sim.FindCharacter(rook.Id) is not null);
        _sim.HandleClientPacket(A, Envelope.ToArray(new TrainSkill { Stat = (ushort)Stat.Rifle, Points = 4 }));
        var points = _sim.FindCharacter(rook.Id)!.Build.UnspentPoints;

        _sessions.Leave(A);                                   // no Step, no save loop running
        _sessions.BeginJoin(B, rook.AccountId, rook.Id);
        PumpUntil(() => _sim.FindCharacter(rook.Id) is not null);
        Assert.Equal(points, _sim.FindCharacter(rook.Id)!.Build.UnspentPoints);
        Assert.Equal(60, (await _store.LoadCharacterAsync(rook.Id, Ct))!.UnspentPoints); // the store never saw it
    }

    [Fact]
    public async Task A_second_session_takes_over_from_the_live_player()
    {
        var rook = await Created();
        _sessions.BeginJoin(A, rook.AccountId, rook.Id);
        PumpUntil(() => _sim.FindCharacter(rook.Id) is not null);
        _sim.FindCharacter(rook.Id)!.Build.AddXp(42);

        _sessions.BeginJoin(C, rook.AccountId, rook.Id);
        PumpUntil(() => _sim.FindCharacter(rook.Id)?.Client == C);
        Assert.Equal(42, _sim.FindCharacter(rook.Id)!.Build.Xp);
        Assert.Equal([(A, "Logged in from another session.")], _dropped);
        Assert.Equal(1, _sim.PlayerCount);
    }

    [Fact]
    public async Task Leaving_while_loading_cancels_the_join()
    {
        var rook = await Created();
        _sessions.BeginJoin(A, rook.AccountId, rook.Id);
        _sessions.Leave(A);
        await Task.Delay(50, Ct);
        _sessions.RunQueued();
        Assert.Null(_sim.FindCharacter(rook.Id));
        Assert.Empty(_dropped);
    }

    [Fact]
    public async Task Another_accounts_character_or_a_missing_one_is_refused()
    {
        var rook = await Created();
        _sessions.BeginJoin(A, rook.AccountId + 1, rook.Id);
        _sessions.BeginJoin(B, rook.AccountId, 9_999);
        PumpUntil(() => _dropped.Count == 2);
        Assert.All(_dropped, d => Assert.Equal("Character not found.", d.Reason));
        Assert.Equal(0, _sim.PlayerCount);
    }

    [Fact]
    public async Task Losing_ownership_drops_the_player()
    {
        var rook = await Created();
        _sessions.BeginJoin(A, rook.AccountId, rook.Id);
        PumpUntil(() => _sim.FindCharacter(rook.Id) is not null);
        await _store.SaveCharactersAsync([rook with { Xp = 1 }], Ct); // someone else wrote it
        _sim.FindCharacter(rook.Id)!.Build.AddXp(5);
        _sim.CheckpointAll();
        _sessions.QueueCheckpoints();
        await _saver.WriteBatchAsync(Ct);
        PumpUntil(() => _dropped.Count == 1);
        Assert.Equal(A, _dropped[0].Client);
        Assert.Null(_sim.FindCharacter(rook.Id));
    }
}
