namespace Meridian.Persistence;

/// <summary>Chooses the store from configuration, the same way in every service.</summary>
public static class GameStores
{
    /// <summary>
    /// <c>MERIDIAN_STORE</c>: <c>file</c> (default; a JSON file shared by local processes, in
    /// <c>MERIDIAN_STORE_DIR</c> or the user's local app-data folder) or <c>memory</c> (one process
    /// only, nothing kept). <c>postgres</c> arrives with the Npgsql store.
    /// </summary>
    public static IGameStore FromEnvironment(Func<string, string?> env, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(log);
        var kind = (env("MERIDIAN_STORE") ?? "file").Trim().ToLowerInvariant();
        switch (kind)
        {
            case "file":
                var dir = env("MERIDIAN_STORE_DIR") is { Length: > 0 } d
                    ? d
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meridian", "dev-store");
                log.WriteLine($"store: development JSON file in {dir}");
                return new JsonFileGameStore(dir);
            case "memory":
                log.WriteLine("store: in memory (this process only; nothing is kept)");
                return new InMemoryGameStore();
            default:
                throw new InvalidOperationException($"MERIDIAN_STORE='{kind}' is not supported (use 'file' or 'memory').");
        }
    }
}
