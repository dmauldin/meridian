using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meridian.Persistence;

/// <summary>
/// Development store: one JSON file that several local processes (auth and zone) share, so the
/// game runs without a database. Every operation takes an exclusive lock file, reads the file,
/// applies the change and replaces the file atomically. Correct across processes, but it rewrites
/// the whole file per change: fine for a developer and a few hundred bots, not for production.
/// </summary>
public sealed class JsonFileGameStore : IGameStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private readonly string _path;
    private readonly string _lockPath;

    public JsonFileGameStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "store.json");
        _lockPath = Path.Combine(directory, "store.lock");
    }

    public string FilePath => _path;

    public Task<AccountRecord?> CreateAccountAsync(string username, string passwordHash, CancellationToken cancellationToken = default) =>
        Write(m => m.CreateAccount(username, passwordHash), cancellationToken);

    public Task<AccountRecord?> FindAccountAsync(string username, CancellationToken cancellationToken = default) =>
        Read(m => m.FindAccount(username), cancellationToken);

    public Task SetPasswordHashAsync(ulong accountId, string passwordHash, CancellationToken cancellationToken = default) =>
        Write(m =>
        {
            m.SetPasswordHash(accountId, passwordHash);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(ulong accountId, CancellationToken cancellationToken = default) =>
        Read(m => m.ListCharacters(accountId), cancellationToken);

    public Task<(CharacterRecord? Created, CreateCharacterError Error)> CreateCharacterAsync(
        CharacterRecord draft, int maxPerAccount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Write(m => m.CreateCharacter(draft, maxPerAccount), cancellationToken);
    }

    public Task<CharacterRecord?> LoadCharacterAsync(ulong characterId, CancellationToken cancellationToken = default) =>
        Read(m => m.LoadCharacter(characterId), cancellationToken);

    public Task<IReadOnlyList<SaveOutcome>> SaveCharactersAsync(IReadOnlyList<CharacterRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Write<IReadOnlyList<SaveOutcome>>(m => m.SaveCharacters(records), cancellationToken);
    }

    private Task<T> Read<T>(Func<StoreModel, T> op, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var held = AcquireLock(cancellationToken);
            return op(LoadModel());
        }, cancellationToken);

    private Task<T> Write<T>(Func<StoreModel, T> op, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var held = AcquireLock(cancellationToken);
            var model = LoadModel();
            var result = op(model);
            SaveModel(model);
            return result;
        }, cancellationToken);

    private StoreModel LoadModel()
    {
        if (!File.Exists(_path))
        {
            return new StoreModel();
        }
        using var stream = File.OpenRead(_path);
        var file = JsonSerializer.Deserialize(stream, StoreJson.Default.StoreFile)
            ?? throw new InvalidDataException($"{_path} is empty.");
        return StoreModel.FromFile(file);
    }

    private void SaveModel(StoreModel model)
    {
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, model.ToFile(), StoreJson.Default.StoreFile);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, overwrite: true); // atomic replace: readers see old or new, never half
    }

    private FileStream AcquireLock(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(2);
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StoreFile))]
internal sealed partial class StoreJson : JsonSerializerContext;
