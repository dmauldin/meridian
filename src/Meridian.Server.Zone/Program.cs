using System.Globalization;
using Meridian.Net;
using Meridian.Server.Zone;

// Configuration via environment, so containers and the dev script set it the same way.
var port = int.Parse(Environment.GetEnvironmentVariable("MERIDIAN_ZONE_PORT") ?? NetDefaults.ZonePort.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
var key = Environment.GetEnvironmentVariable("MERIDIAN_INTERNAL_KEY") ?? DevSecrets.InternalKeyWithWarning(Console.Error);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

using var host = new ZoneHost(new ZoneHostOptions { Port = port, InternalKey = key });
host.Start();
host.Run(cts.Token);
Console.WriteLine("zone stopped");
