using System.Globalization;
using Meridian.Net;
using Meridian.Persistence;
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
// SIGTERM (docker stop, systemd, the gate scripts) must end the loop rather than the process, so
// the shutdown checkpoint reaches the store. ProcessExit alone lets the runtime exit first.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        cts.Cancel();
    });

var store = GameStores.FromEnvironment(Environment.GetEnvironmentVariable, Console.Out);
var devCommands = Environment.GetEnvironmentVariable("MERIDIAN_DEV_COMMANDS") == "1";
if (devCommands)
{
    Console.Error.WriteLine("WARNING: development commands (/give) are enabled; never on a public server.");
}
using var host = new ZoneHost(new ZoneHostOptions { Port = port, InternalKey = key, Zone = new ZoneOptions { DevCommands = devCommands } }, store);
host.Start();
host.Run(cts.Token);
Console.WriteLine("zone stopped");
