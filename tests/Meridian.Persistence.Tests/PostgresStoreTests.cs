using Meridian.Core.Characters;
using Npgsql;

namespace Meridian.Persistence.Tests;

/// <summary>
/// The store contract against a real Postgres database, each test in a fresh clone of a migrated
/// template. scripts/db-test.sh sets MERIDIAN_TEST_DATABASE_URL to that template; without it these
/// tests are skipped (and that script fails if any are).
/// </summary>
public sealed class PostgresStoreTests : StoreContractTests
{
    private readonly string _server;
    private readonly string _database = "meridian_t_" + Guid.NewGuid().ToString("N");
    private readonly List<PostgresGameStore> _opened = [];
    private readonly PostgresGameStore _store;

    public PostgresStoreTests()
    {
        var template = Database.TemplateOrSkip();
        _server = Database.On(template, "postgres");
        Database.Run(_server, $"CREATE DATABASE {_database} TEMPLATE {new NpgsqlConnectionStringBuilder(template).Database}");
        _store = OpenAgain();
    }

    protected override IGameStore Store => _store;

    protected override PostgresGameStore OpenAgain()
    {
        var store = PostgresGameStore.Open(Database.On(_server, _database));
        _opened.Add(store);
        return store;
    }

    public override void Dispose()
    {
        foreach (var store in _opened)
        {
            store.Dispose();
        }
        Database.Run(_server, $"DROP DATABASE IF EXISTS {_database} WITH (FORCE)");
        base.Dispose();
    }

    [Fact]
    public void A_database_behind_this_build_is_refused()
    {
        var empty = "meridian_e_" + Guid.NewGuid().ToString("N");
        Database.Run(_server, $"CREATE DATABASE {empty}");
        try
        {
            var unmigrated = Assert.Throws<InvalidOperationException>(() => PostgresGameStore.Open(Database.On(_server, empty)));
            Assert.Contains("db-migrate.sh", unmigrated.Message, StringComparison.Ordinal);

            Database.Run(Database.On(_server, empty), "CREATE TABLE schema_migrations (version integer PRIMARY KEY, name text NOT NULL); INSERT INTO schema_migrations VALUES (2, 'x')");
            var behind = Assert.Throws<InvalidOperationException>(() => PostgresGameStore.Open(Database.On(_server, empty)));
            Assert.Contains($"version 2; this build needs {PostgresGameStore.SchemaVersion}", behind.Message, StringComparison.Ordinal);
        }
        finally
        {
            Database.Run(_server, $"DROP DATABASE IF EXISTS {empty} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task Saves_write_rows_the_schema_comments_describe()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = (await _store.CreateAccountAsync("Alice", "$argon2id$v=19$m=1024,t=1,p=1$AAECAwQFBgcICQoLDA0ODw$WHgvyW8Gptf87EcoCZ9qeI7pigT0zY+gonr224t6Rrg", ct))!;
        var draft = CharacterRecords.New(Meridian.Content.ContentPacks.Embedded, a.Id, "Rook", Profession.Operative, Origin.Baseline);
        var rook = (await _store.CreateCharacterAsync(draft, 8, ct)).Created!;
        await _store.SaveCharactersAsync([rook with { Placement = new Placement("rustfall_basin", new(1_500, -2_250), 90) }], ct);
        var row = Database.Query(Database.On(_server, _database),
            "SELECT zone || ' ' || pos_x || ' ' || pos_y || ' ' || pos_z || ' ' || health || ' v' || version || ' ' || jsonb_typeof(skills) FROM characters");
        Assert.Equal("rustfall_basin 1500 0 -2250 90 v2 object", row);
    }
}

/// <summary>Store behaviour that needs no database.</summary>
public class PostgresConnectionTests
{
    [Theory]
    [InlineData("postgres://postgres:postgres@localhost:5432/meridian", "Host=localhost;Port=5432;Database=meridian;Username=postgres;Password=postgres")]
    [InlineData("postgresql://app@db.example/meridian?sslmode=require", "Host=db.example;Port=5432;Database=meridian;Username=app;SSL Mode=Require")]
    [InlineData("postgres://a%40b:p%3Aw@h:6000/d", "Host=h;Port=6000;Database=d;Username=a@b;Password=p:w")]
    [InlineData("host=/tmp/pg port=20001 user=postgres dbname=meridian", "Host=/tmp/pg;Port=20001;Username=postgres;Database=meridian")] // libpq, as the scripts write it
    [InlineData("host=db password=pw sslmode=require", "Host=db;Password=pw;SSL Mode=Require")]
    [InlineData("Host=x;Database=y", "Host=x;Database=y")] // Npgsql strings pass through
    public void Database_urls_become_npgsql_connection_strings(string url, string expected)
    {
        Assert.Equal(new NpgsqlConnectionStringBuilder(expected).ConnectionString, PostgresGameStore.ToConnectionString(url));
    }

    [Fact]
    public void Options_it_does_not_understand_are_refused_not_dropped()
    {
        Assert.Throws<ArgumentException>(() => PostgresGameStore.ToConnectionString("postgres://h/d?options=-csearch_path%3Dx"));
        // Npgsql alone would read this as a host named "h connect_timeout=5".
        Assert.Throws<ArgumentException>(() => PostgresGameStore.ToConnectionString("host=h connect_timeout=5"));
        Assert.Throws<ArgumentException>(() => PostgresGameStore.ToConnectionString("host=h password='a b'"));
    }

    [Fact]
    public void The_schema_version_is_the_newest_migration()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Meridian.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repo root not found");
        }
        var newest = Directory.GetFiles(Path.Combine(dir, "db", "migrations"), "*.sql").Max(f => int.Parse(Path.GetFileName(f)[..4], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(PostgresGameStore.SchemaVersion, newest);
    }
}

/// <summary>The test server, from MERIDIAN_TEST_DATABASE_URL (the migrated template; any form the store accepts).</summary>
internal static class Database
{
    public static string TemplateOrSkip()
    {
        var url = Environment.GetEnvironmentVariable("MERIDIAN_TEST_DATABASE_URL");
        Assert.SkipWhen(string.IsNullOrEmpty(url), "No test database: run scripts/db-test.sh.");
        return PostgresGameStore.ToConnectionString(url!);
    }

    /// <summary>The same server, another database.</summary>
    public static string On(string connection, string database) =>
        new NpgsqlConnectionStringBuilder(connection) { Database = database, Pooling = false }.ConnectionString;

    public static void Run(string connection, string sql)
    {
        using var c = new NpgsqlConnection(connection);
        c.Open();
        using var command = new NpgsqlCommand(sql, c);
        command.ExecuteNonQuery();
    }

    public static string? Query(string connection, string sql)
    {
        using var c = new NpgsqlConnection(connection);
        c.Open();
        using var command = new NpgsqlCommand(sql, c);
        return command.ExecuteScalar() as string;
    }
}
