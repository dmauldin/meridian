using System.Globalization;
using System.Threading.RateLimiting;
using Meridian.Net;
using Meridian.Net.Client;
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
    DevPassword = Env("MERIDIAN_DEV_PASSWORD", "dev"),
};
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new TicketSealer(DevSecrets.TicketKeyFromEnvironment("MERIDIAN_TICKET_KEY", Console.Error)));
builder.Services.AddSingleton<LoginService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AuthJsonContext.Default));

var app = builder.Build();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapPost("/v1/login", (LoginRequest request, LoginService logins) =>
        logins.TryLogin(request) is { } response ? Results.Ok(response) : Results.Unauthorized())
    .RequireRateLimiting("login");

app.Run();
