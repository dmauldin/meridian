using System.Globalization;
using System.Text.Json;
using Meridian.Core.Characters;
using Meridian.Core.Stats;
using Meridian.Core.World;
using Npgsql;

namespace Meridian.Persistence;

/// <summary>
/// The production store: one Postgres database, schema from <c>db/migrations</c> (applied by
/// <c>scripts/db-migrate.sh</c>, never by this class). Thread-safe; connections are pooled.
///
/// Same rules as <see cref="StoreModel"/>, enforced in SQL, and the same contract tests. Each
/// character write is one transaction: the character row and its item, Protocol and buff rows
/// can never disagree, and a save that names a stale version changes nothing.
/// </summary>
public sealed class PostgresGameStore : IGameStore, IDisposable
{
    /// <summary>The newest migration this build knows. Opening refuses an older database.</summary>
    public const int SchemaVersion = 4;

    private readonly NpgsqlDataSource _db;

    private PostgresGameStore(NpgsqlDataSource db) => _db = db;

    /// <summary>
    /// Connects and checks the schema is migrated far enough. <paramref name="connection"/> is
    /// anything <see cref="ToConnectionString"/> accepts.
    /// </summary>
    public static PostgresGameStore Open(string connection)
    {
        var db = NpgsqlDataSource.Create(ToConnectionString(connection));
        try
        {
            using var command = db.CreateCommand("SELECT coalesce(max(version), 0) FROM schema_migrations");
            var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version < SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The database is at schema version {version}; this build needs {SchemaVersion}. Run scripts/db-migrate.sh.");
            }
            return new PostgresGameStore(db);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            db.Dispose();
            throw new InvalidOperationException("The database has no schema_migrations table. Run scripts/db-migrate.sh.", e);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The three ways a database is usually named, as an Npgsql connection string: a
    /// <c>postgres://user:password@host:port/db?sslmode=...</c> URL (CI and most hosts), libpq
    /// <c>host=... port=... user=... dbname=...</c> (what psql and the db scripts use; a socket
    /// directory works as the host), or an Npgsql string, passed through. Options it does not
    /// know are refused rather than dropped.
    /// </summary>
    public static string ToConnectionString(string connection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        if (LibpqPairs(connection) is { } pairs)
        {
            var b = new NpgsqlConnectionStringBuilder();
            foreach (var (key, value) in pairs)
            {
                switch (key)
                {
                    case "host": b.Host = value; break;
                    case "port": b.Port = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); break;
                    case "user": b.Username = value; break;
                    case "password": b.Password = value; break;
                    case "dbname": b.Database = value; break;
                    default: b.SslMode = Enum.Parse<SslMode>(value, ignoreCase: true); break; // sslmode
                }
            }
            return b.ConnectionString;
        }
        if (!connection.StartsWith("postgres://", StringComparison.Ordinal) && !connection.StartsWith("postgresql://", StringComparison.Ordinal))
        {
            return connection;
        }
        var uri = new Uri(connection);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')) is { Length: > 0 } db ? db : null,
        };
        if (uri.UserInfo.Split(':', 2) is [var user, ..] && user.Length > 0)
        {
            builder.Username = Uri.UnescapeDataString(user);
        }
        if (uri.UserInfo.Split(':', 2) is [_, var password])
        {
            builder.Password = Uri.UnescapeDataString(password);
        }
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = pair.Split('=', 2) is [var k, var v] ? (k, v) : (pair, "");
            if (key == "sslmode")
            {
                builder.SslMode = Enum.Parse<SslMode>(Uri.UnescapeDataString(value), ignoreCase: true);
            }
            else
            {
                throw new ArgumentException($"Unsupported database URL option '{key}'; use an Npgsql connection string instead.", nameof(connection));
            }
        }
        return builder.ConnectionString;
    }

    /// <summary>
    /// The pairs of a libpq "key=value key=value" string, or null if it is not one (no ';', and
    /// it starts with a lowercase key): Npgsql strings use ';', and Npgsql would read a libpq
    /// string as one long host name. Keys beyond the six above, and quoting, are refused.
    /// </summary>
    private static List<(string Key, string Value)>? LibpqPairs(string connection)
    {
        var tokens = connection.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (connection.Contains(';', StringComparison.Ordinal)
            || tokens is not [var first, ..]
            || first.Split('=', 2) is not [var firstKey, _] || firstKey.Length == 0 || !firstKey.All(c => c is (>= 'a' and <= 'z') or '_'))
        {
            return null;
        }
        var pairs = new List<(string, string)>();
        foreach (var token in tokens)
        {
            var (key, value) = token.Split('=', 2) is [var k, var v] ? (k, v) : ("", "");
            if (key is not ("host" or "port" or "user" or "password" or "dbname" or "sslmode") || value.Length == 0 || value.Contains('\'', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unsupported connection option '{key}' (or a quoted value); use an Npgsql connection string instead.", nameof(connection));
            }
            pairs.Add((key, value));
        }
        return pairs;
    }

    public void Dispose() => _db.Dispose();

    // ------------------------------------------------------------------ accounts

    public async Task<AccountRecord?> CreateAccountAsync(string username, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var command = _db.CreateCommand(
            "INSERT INTO accounts (username, password_hash) VALUES ($1, $2) ON CONFLICT DO NOTHING RETURNING id");
        command.Parameters.Add(new() { Value = username });
        command.Parameters.Add(new() { Value = passwordHash });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long id
            ? new AccountRecord((ulong)id, username, passwordHash)
            : null;
    }

    public async Task<AccountRecord?> FindAccountAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var command = _db.CreateCommand("SELECT id, username, password_hash FROM accounts WHERE lower(username) = lower($1)");
        command.Parameters.Add(new() { Value = username });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AccountRecord((ulong)reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task SetPasswordHashAsync(ulong accountId, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var command = _db.CreateCommand("UPDATE accounts SET password_hash = $2 WHERE id = $1");
        command.Parameters.Add(new() { Value = (long)accountId });
        command.Parameters.Add(new() { Value = passwordHash });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ characters

    public async Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(ulong accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, "account_id = $1", (long)accountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CharacterRecord?> LoadCharacterAsync(ulong characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var found = await LoadAsync(connection, "id = $1", (long)characterId, cancellationToken).ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    public async Task<(CharacterRecord? Created, CreateCharacterError Error)> CreateCharacterAsync(
        CharacterRecord draft, int maxPerAccount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Locking the account row serialises creates per account, so the limit holds under races.
        if (await ScalarAsync(connection, "SELECT 1 FROM accounts WHERE id = $1 FOR UPDATE", (long)draft.AccountId, cancellationToken).ConfigureAwait(false) is null)
        {
            return (null, CreateCharacterError.NoSuchAccount);
        }
        // Same order of checks as StoreModel: a taken name is reported before the limit.
        if (await ScalarAsync(connection, "SELECT 1 FROM characters WHERE lower(name) = lower($1)", draft.Name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return (null, CreateCharacterError.NameTaken);
        }
        var count = (long)(await ScalarAsync(connection, "SELECT count(*) FROM characters WHERE account_id = $1", (long)draft.AccountId, cancellationToken).ConfigureAwait(false))!;
        if (count >= maxPerAccount)
        {
            return (null, CreateCharacterError.TooManyCharacters);
        }
        // Another account can still take the name between the check and here: the unique index decides.
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO characters (account_id, name, profession, origin, level, xp, unspent_points, skills, zone, pos_x, pos_y, pos_z, health, credits)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9, $10, $11, $12, $13, $14)
            ON CONFLICT DO NOTHING RETURNING id
            """, connection);
        insert.Parameters.Add(new() { Value = (long)draft.AccountId });
        insert.Parameters.Add(new() { Value = draft.Name });
        insert.Parameters.Add(new() { Value = (short)draft.Profession });
        insert.Parameters.Add(new() { Value = (short)draft.Origin });
        insert.Parameters.Add(new() { Value = draft.Level });
        insert.Parameters.Add(new() { Value = draft.Xp });
        insert.Parameters.Add(new() { Value = draft.UnspentPoints });
        AddSkillsAndPlacement(insert, draft);
        insert.Parameters.Add(new() { Value = draft.Credits });
        if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long id)
        {
            return (null, CreateCharacterError.NameTaken);
        }
        await WriteChildrenAsync(connection, id, draft, replace: false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        var created = await LoadAsync(connection, "id = $1", id, cancellationToken).ConfigureAwait(false);
        return (created[0], CreateCharacterError.None);
    }

    public async Task<IReadOnlyList<SaveOutcome>> SaveCharactersAsync(IReadOnlyList<CharacterRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var outcomes = new List<SaveOutcome>(records.Count);
        await using var connection = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var r in records)
        {
            outcomes.Add(await SaveAsync(connection, r, cancellationToken).ConfigureAwait(false));
        }
        return outcomes;
    }

    /// <summary>One character in one transaction. Only progression, placement and children change; identity never does.</summary>
    private static async Task<SaveOutcome> SaveAsync(NpgsqlConnection connection, CharacterRecord r, CancellationToken cancellationToken)
    {
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var update = new NpgsqlCommand(
            """
            UPDATE characters
               SET level = $1, xp = $2, unspent_points = $3, skills = $4::jsonb,
                   zone = $5, pos_x = $6, pos_y = $7, pos_z = $8, health = $9, credits = $12,
                   version = version + 1, saved_at = now()
             WHERE id = $10 AND version = $11
            RETURNING version
            """, connection);
        update.Parameters.Add(new() { Value = r.Level });
        update.Parameters.Add(new() { Value = r.Xp });
        update.Parameters.Add(new() { Value = r.UnspentPoints });
        AddSkillsAndPlacement(update, r);
        update.Parameters.Add(new() { Value = (long)r.Id });
        update.Parameters.Add(new() { Value = r.Version });
        update.Parameters.Add(new() { Value = r.Credits });
        if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long version)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var stored = await ScalarAsync(connection, "SELECT version FROM characters WHERE id = $1", (long)r.Id, cancellationToken).ConfigureAwait(false);
            return new SaveOutcome(r.Id, false, stored as long?);
        }
        await WriteChildrenAsync(connection, (long)r.Id, r, replace: true, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SaveOutcome(r.Id, true, version);
    }

    /// <summary>Skills as jsonb, then the five placement columns (all null when unplaced, which the schema requires).</summary>
    private static void AddSkillsAndPlacement(NpgsqlCommand command, CharacterRecord r)
    {
        command.Parameters.Add(new() { Value = SkillsJson(r.Trained) });
        var p = r.Placement;
        command.Parameters.Add(new() { Value = (object?)p?.Zone ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = p is null ? DBNull.Value : (object)p.Position.X, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        command.Parameters.Add(new() { Value = p is null ? DBNull.Value : (object)0, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer }); // ground plane: y is not tracked yet
        command.Parameters.Add(new() { Value = p is null ? DBNull.Value : (object)p.Position.Z, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        command.Parameters.Add(new() { Value = p is null ? DBNull.Value : (object)p.Health, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
    }

    /// <summary>Items, known Protocols and running buffs, in one round trip. Normalised as StoreModel does.</summary>
    private static async Task WriteChildrenAsync(NpgsqlConnection connection, long id, CharacterRecord r, bool replace, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection);
        if (replace)
        {
            foreach (var table in (string[])["character_items", "character_protocols", "character_buffs"])
            {
                var delete = new NpgsqlBatchCommand($"DELETE FROM {table} WHERE character_id = $1");
                delete.Parameters.Add(new() { Value = id });
                batch.BatchCommands.Add(delete);
            }
        }
        var items = new NpgsqlBatchCommand(
            "INSERT INTO character_items (character_id, location, position, template_id, ql) SELECT $1, * FROM unnest($2::int[], $3::int[], $4::int[], $5::int[])");
        items.Parameters.Add(new() { Value = id });
        items.Parameters.Add(new() { Value = r.Items.Select(i => (int)i.Location).ToArray() });
        items.Parameters.Add(new() { Value = r.Items.Select(i => i.Position).ToArray() });
        items.Parameters.Add(new() { Value = r.Items.Select(i => i.TemplateId).ToArray() });
        items.Parameters.Add(new() { Value = r.Items.Select(i => i.Ql).ToArray() });
        batch.BatchCommands.Add(items);

        var known = new NpgsqlBatchCommand(
            "INSERT INTO character_protocols (character_id, protocol_id) SELECT DISTINCT $1::bigint, unnest($2::int[])");
        known.Parameters.Add(new() { Value = id });
        known.Parameters.Add(new() { Value = r.KnownProtocols.ToArray() });
        batch.BatchCommands.Add(known);

        // A buff already over is not saved; the first entry for a Protocol wins, as in StoreModel.
        var buffs = new NpgsqlBatchCommand(
            """
            INSERT INTO character_buffs (character_id, protocol_id, remaining_ms)
            SELECT $1, p, ms FROM unnest($2::int[], $3::int[]) WITH ORDINALITY AS b(p, ms, n)
             WHERE ms > 0 ORDER BY n
            ON CONFLICT DO NOTHING
            """);
        buffs.Parameters.Add(new() { Value = id });
        buffs.Parameters.Add(new() { Value = r.Buffs.Select(b => b.ProtocolId).ToArray() });
        buffs.Parameters.Add(new() { Value = r.Buffs.Select(b => b.RemainingMs).ToArray() });
        batch.BatchCommands.Add(buffs);
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Characters matching <paramref name="where"/> (one parameter, $1), oldest first, with their children.</summary>
    private static async Task<IReadOnlyList<CharacterRecord>> LoadAsync(NpgsqlConnection connection, string where, long key, CancellationToken cancellationToken)
    {
        // Four result sets in one round trip; children are matched by id.
        await using var batch = new NpgsqlBatch(connection);
        var match = $"SELECT id FROM characters WHERE {where}";
        foreach (var sql in (string[])[
            $"SELECT id, account_id, name, profession, origin, level, xp, unspent_points, skills::text, zone, pos_x, pos_z, health, version, credits FROM characters WHERE {where} ORDER BY id",
            $"SELECT character_id, location, position, template_id, ql FROM character_items WHERE character_id IN ({match}) ORDER BY character_id, location, position",
            $"SELECT character_id, protocol_id FROM character_protocols WHERE character_id IN ({match}) ORDER BY character_id, protocol_id",
            $"SELECT character_id, protocol_id, remaining_ms FROM character_buffs WHERE character_id IN ({match}) ORDER BY character_id, protocol_id"])
        {
            var command = new NpgsqlBatchCommand(sql);
            command.Parameters.Add(new() { Value = key });
            batch.BatchCommands.Add(command);
        }
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<CharacterRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var placement = reader.IsDBNull(9)
                ? null
                : new Placement(reader.GetString(9), new Position(reader.GetInt32(10), reader.GetInt32(11)), reader.GetInt32(12));
            rows.Add(new CharacterRecord(
                (ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), reader.GetString(2),
                (Profession)reader.GetInt16(3), (Origin)reader.GetInt16(4),
                reader.GetInt32(5), reader.GetInt64(6), reader.GetInt32(7),
                ParseSkills(reader.GetString(8)), placement, reader.GetInt64(13), [], [], [], reader.GetInt64(14)));
        }
        var items = new Dictionary<long, List<StoredItem>>();
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Child(items, reader.GetInt64(0)).Add(new((ItemLocation)reader.GetInt16(1), reader.GetInt16(2), reader.GetInt32(3), reader.GetInt16(4)));
        }
        var known = new Dictionary<long, List<int>>();
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Child(known, reader.GetInt64(0)).Add(reader.GetInt32(1));
        }
        var buffs = new Dictionary<long, List<SavedBuff>>();
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Child(buffs, reader.GetInt64(0)).Add(new(reader.GetInt32(1), reader.GetInt32(2)));
        }
        return [.. rows.Select(c => c with
        {
            Items = items.GetValueOrDefault((long)c.Id) ?? [],
            KnownProtocols = known.GetValueOrDefault((long)c.Id) ?? [],
            Buffs = buffs.GetValueOrDefault((long)c.Id) ?? [],
        })];
    }

    private static List<T> Child<T>(Dictionary<long, List<T>> map, long id) =>
        map.TryGetValue(id, out var list) ? list : map[id] = [];

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, object value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new() { Value = value });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>{"71": 35, ...}: stat ids as keys, like the file store and the column comment.</summary>
    private static string SkillsJson(IReadOnlyList<KeyValuePair<Stat, int>> trained) =>
        JsonSerializer.Serialize(trained.ToDictionary(t => ((int)t.Key).ToString(CultureInfo.InvariantCulture), t => t.Value), SkillsContext.Default.DictionaryStringInt32);

    private static KeyValuePair<Stat, int>[] ParseSkills(string json) =>
        [.. (JsonSerializer.Deserialize(json, SkillsContext.Default.DictionaryStringInt32) ?? [])
            .Select(s => new KeyValuePair<Stat, int>((Stat)int.Parse(s.Key, NumberStyles.None, CultureInfo.InvariantCulture), s.Value))
            .OrderBy(s => s.Key)];
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class SkillsContext : System.Text.Json.Serialization.JsonSerializerContext;
