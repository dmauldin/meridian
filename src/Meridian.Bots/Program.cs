using System.Diagnostics;
using System.Globalization;
using Meridian.Bots;
using Meridian.Net.Client;

// Usage: Meridian.Bots [--count 100] [--duration 60] [--auth http://127.0.0.1:5080/] [--password botpassword]
//                      [--min-snapshot-hz 8] [--hunt]
//   --hunt  bots target the nearest mob, close in and auto-attack (combat load).
// Exit code 0 when every bot connected and received snapshots at the required rate.
var count = IntArg("--count", 100);
var durationSeconds = IntArg("--duration", 60);
var minSnapshotHz = IntArg("--min-snapshot-hz", 8);
var authUrl = StringArg("--auth", "http://127.0.0.1:5080/");
var password = StringArg("--password", "botpassword");
var hunt = args.Contains("--hunt");

using var http = new HttpClient { BaseAddress = new Uri(authUrl) };
var auth = new AuthClient(http);

Console.WriteLine($"logging in {count} bots via {authUrl}");
// Each bot is account botNNNN playing character BotNNNN, registered and created on first run.
var logins = await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
    auth.QuickPlayAsync(new Credentials($"bot{i:D4}", password), $"Bot{i:D4}", "operative", "baseline")));

var bots = logins.Select((login, i) => new Bot(i, login, login.GatewayHost, login.GatewayPort, hunt)).ToList();
var stopwatch = Stopwatch.StartNew();
var end = Stopwatch.GetTimestamp() + (durationSeconds * Stopwatch.Frequency);
var nextStatus = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);

while (Stopwatch.GetTimestamp() < end)
{
    var now = Stopwatch.GetTimestamp();
    foreach (var bot in bots)
    {
        bot.Tick(now);
    }
    if (now >= nextStatus)
    {
        Console.WriteLine($"t={stopwatch.Elapsed.TotalSeconds:F0}s connected={bots.Count(b => b.Connection.Connected)}/{count} welcomed={bots.Count(b => b.Welcomed)} snapshots={bots.Sum(b => b.Snapshots)}");
        nextStatus = now + (5 * Stopwatch.Frequency);
    }
    Thread.Sleep(1);
}

// ----- report
var endTimestamp = Stopwatch.GetTimestamp();
var rates = bots
    .Select(b => b.Snapshots > 1 ? (b.Snapshots - 1) / ((endTimestamp - b.FirstSnapshotTimestamp) / (double)Stopwatch.Frequency) : 0)
    .Order()
    .ToList();
var rtts = bots.SelectMany(b => b.RttSamplesMs).Order().ToList();
var connected = bots.Count(b => b.Connection.Connected && b.Welcomed);
var failures = bots.Where(b => !b.Connection.Connected).GroupBy(b => b.Connection.DisconnectReason ?? "never connected").ToList();

Console.WriteLine();
Console.WriteLine("BOTS RESULT");
Console.WriteLine($"  connected at end      {connected}/{count}");
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  snapshot Hz per bot   min={Pct(rates, 0):F2} p50={Pct(rates, 0.5):F2} max={Pct(rates, 1):F2}"));
Console.WriteLine($"  rtt ms                p50={Pct(rtts, 0.5)} p99={Pct(rtts, 0.99)} max={Pct(rtts, 1)}");
Console.WriteLine($"  max entities in view  {bots.Max(b => b.MaxEntitiesSeen)}");
Console.WriteLine($"  chat lines heard      {bots.Sum(b => b.ChatsHeard)}");
Console.WriteLine($"  combat                hits={bots.Sum(b => b.Hits)} kills={bots.Sum(b => b.Kills)} deaths={bots.Sum(b => b.Deaths)}");
Console.WriteLine($"  progression           xp={bots.Sum(b => b.Xp)} max_level={bots.Max(b => b.Level)}");
Console.WriteLine($"  bytes received        {bots.Sum(b => b.Connection.BytesReceived) / 1024} KiB");
foreach (var g in failures)
{
    Console.WriteLine($"  disconnected: {g.Count()} x {g.Key}");
}

var pass = connected == count && Pct(rates, 0) >= minSnapshotHz;
Console.WriteLine(pass ? "PASS" : "FAIL");

foreach (var b in bots)
{
    b.Connection.Disconnect();
    b.Dispose();
}
return pass ? 0 : 1;

static T Pct<T>(List<T> sorted, double p) =>
    sorted.Count == 0 ? default! : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

int IntArg(string name, int fallback) =>
    int.Parse(StringArg(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

string StringArg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
