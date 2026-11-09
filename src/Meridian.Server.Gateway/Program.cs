using System.Globalization;
using Meridian.Net;
using Meridian.Server.Gateway;

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

var options = new GatewayOptions
{
    ClientPort = int.Parse(Env("MERIDIAN_GATEWAY_PORT", NetDefaults.GatewayPort.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
    ZoneHost = Env("MERIDIAN_ZONE_HOST", "127.0.0.1"),
    ZonePort = int.Parse(Env("MERIDIAN_ZONE_PORT", NetDefaults.ZonePort.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
    InternalKey = Environment.GetEnvironmentVariable("MERIDIAN_INTERNAL_KEY") ?? DevSecrets.InternalKeyWithWarning(Console.Error),
    TicketKey = DevSecrets.TicketKeyFromEnvironment("MERIDIAN_TICKET_KEY", Console.Error),
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

using var host = new GatewayHost(options);
host.Start();
host.Run(cts.Token);
Console.WriteLine("gateway stopped");
