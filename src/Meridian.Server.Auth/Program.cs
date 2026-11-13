using System.Globalization;
using System.Threading.RateLimiting;
using Meridian.Net;
using Meridian.Net.Client;
using Meridian.Persistence;
using Meridian.Server.Auth;

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

var builder = WebApplication.CreateSlimBuilder(args);
var port = int.Parse(Env("MERIDIAN_AUTH_PORT", NetDefaults.AuthPort.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

// M0 serves plain HTTP for local development. Production terminates TLS in front of this
// service (or configures Kestrel certificates); session keys must never cross the network in clear.
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));

var loginsPerMinute = int.Parse(Env("MERIDIAN_LOGINS_PER_MINUTE_PER_IP", "30"), CultureInfo.InvariantCulture);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var options = new AuthOptions
{
    PublicGatewayHost = Env("MERIDIAN_PUBLIC_GATEWAY_HOST", "127.0.0.1"),
    PublicGatewayPort = int.Parse(Env("MERIDIAN_PUBLIC_GATEWAY_PORT", NetDefaults.GatewayPort.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
};
// Lowering iterations is for load tests only (they register hundreds of bots at once).
var iterations = int.Parse(Env("MERIDIAN_PBKDF2_ITERATIONS", PasswordHasher.DefaultIterations.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
if (iterations < PasswordHasher.DefaultIterations)
{
    Console.Error.WriteLine($"WARNING: password hashing at {iterations} PBKDF2 iterations (default {PasswordHasher.DefaultIterations}); never in production.");
}
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new TicketSealer(DevSecrets.TicketKeyFromEnvironment("MERIDIAN_TICKET_KEY", Console.Error)));
builder.Services.AddSingleton(Meridian.Content.ContentPacks.Embedded);
builder.Services.AddSingleton(_ => GameStores.FromEnvironment(Environment.GetEnvironmentVariable, Console.Out));
builder.Services.AddSingleton(new PasswordHasher(iterations));
builder.Services.AddSingleton<AuthService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AuthJsonContext.Default));

var app = builder.Build();
app.UseRateLimiter();

static string? Bearer(HttpRequest request) =>
    request.Headers.Authorization.ToString() is { } h && h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..].Trim() : null;

static IResult Reply<T>(AuthResult<T> result) =>
    result.Error is null
        ? Results.Json(result.Value, statusCode: (int)result.Status)
        : Results.Json(result.Error, statusCode: (int)result.Status);

app.MapGet("/healthz", () => Results.Ok("ok"));

// Register and login hash a password, so both share the per-IP limit.
app.MapPost("/v1/register", async (Credentials request, AuthService auth, CancellationToken ct) =>
        Reply(await auth.RegisterAsync(request, ct)))
    .RequireRateLimiting("login");
app.MapPost("/v1/login", async (Credentials request, AuthService auth, CancellationToken ct) =>
        Reply(await auth.LoginAsync(request, ct)))
    .RequireRateLimiting("login");
app.MapGet("/v1/characters", async (HttpRequest http, AuthService auth, CancellationToken ct) =>
    Reply(await auth.ListCharactersAsync(Bearer(http), ct)));
app.MapPost("/v1/characters", async (CreateCharacterRequest request, HttpRequest http, AuthService auth, CancellationToken ct) =>
    Reply(await auth.CreateCharacterAsync(Bearer(http), request, ct)));
app.MapPost("/v1/play", async (PlayRequest request, HttpRequest http, AuthService auth, CancellationToken ct) =>
    Reply(await auth.PlayAsync(Bearer(http), request, ct)));

app.Run();
