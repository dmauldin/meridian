using System.Diagnostics;
using Meridian.ClientState;
using Meridian.Core.Items;
using Meridian.Core.World;
using Meridian.Net.Client;

namespace Meridian.Bots;

/// <summary>
/// The M2 gate: "twink a rifle with another player's buff", played by two real clients through
/// auth, gateway and zone, using the same session code and chat commands as the game client.
///
/// A fresh Operative (Rifle 30, Agility 20) gets a QL 1 Kestrel Mk2 (Rifle 40, Agility 30). With
/// its own Rifle Enhancement it still lacks Agility, and the equip is refused. A Medic walks over
/// and casts Reflex Boost (+20 Agility) on it; now the equip succeeds and the rifle is active.
/// The Operative then walks into the next zone: the rifle and both buffs arrive with it, still
/// active, on the same connection. When it drops the Medic's buff there, the rifle stays worn but
/// goes inactive. Needs both zones running, with MERIDIAN_DEV_COMMANDS=1 (for /give).
/// </summary>
public static class TwinkGate
{
    public static async Task<int> RunAsync(HttpClient http, string password, TimeSpan timeout)
    {
        var suffix = Environment.ProcessId % 10_000;
        var clock = Stopwatch.StartNew();
        using var medic = await GameSession.QuickStartAsync(http, new Credentials($"gatemedic{suffix}", password), $"Mira{suffix}", "medic", "baseline");
        using var op = await GameSession.QuickStartAsync(http, new Credentials($"gateop{suffix}", password), $"Oren{suffix}", "operative", "baseline");
        var notices = new List<string>();
        var steps = new List<string>();

        (short, short) opSteer = (0, 0);
        void Pump(short medicX = 0, short medicZ = 0)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            medic.Update(now, medicX, medicZ);
            op.Update(now, opSteer.Item1, opSteer.Item2);
            medic.World.DrainEvents();
            foreach (var e in op.World.DrainEvents())
            {
                if (e is SystemNotice n)
                {
                    notices.Add(n.Text);
                }
            }
        }

        async Task<bool> Until(Func<bool> done, string what, Func<(short, short)>? steer = null)
        {
            var deadline = clock.Elapsed + timeout;
            while (!done())
            {
                if (clock.Elapsed > deadline || !medic.Connected && medic.DisconnectReason is not null || !op.Connected && op.DisconnectReason is not null)
                {
                    Console.WriteLine($"M2 GATE: FAIL waiting for {what}");
                    foreach (var s in steps)
                    {
                        Console.WriteLine($"  done: {s}");
                    }
                    Console.WriteLine($"  operative notices: {string.Join(" | ", notices)}");
                    return false;
                }
                var (x, z) = steer?.Invoke() ?? ((short)0, (short)0);
                Pump(x, z);
                await Task.Delay(16);
            }
            steps.Add($"{what} ({clock.Elapsed.TotalSeconds:F1} s)");
            return true;
        }

        var kestrelId = Meridian.Content.ContentPacks.Embedded.Items["rifle.kestrel_mk2"].Id;
        OwnedItem? Kestrel() => op.World.Inventory?.Carried.Where(c => c.Item.TemplateId == kestrelId).Select(c => (OwnedItem?)c.Item).FirstOrDefault();
        int KestrelPosition() => op.World.Inventory!.Carried.First(c => c.Item.TemplateId == kestrelId).Position;
        bool? KestrelWornActive() => op.World.Inventory?.Worn.Where(w => w.Item.TemplateId == kestrelId).Select(w => (bool?)w.Active).FirstOrDefault();

        if (!await Until(() => medic.World.InWorld && op.World.InWorld && op.World.Inventory is not null && op.World.Protocols is not null, "both in the world"))
        {
            return 1;
        }

        op.Say("/give rifle.kestrel_mk2 1");
        if (!await Until(() => Kestrel() is not null, "operative given a QL 1 Kestrel Mk2"))
        {
            return 1;
        }

        op.Say("/cast rifle_enhancement_1 me");
        if (!await Until(() => op.World.Protocols!.Running.Any(r => r.ProtocolId == GameSession.FindProtocol("rifle_enhancement_1")!.Id), "operative's own Rifle Enhancement"))
        {
            return 1;
        }

        op.Say($"/equip {KestrelPosition()}");
        if (!await Until(() => notices.Any(n => n.Contains("Agility 20/30", StringComparison.Ordinal)), "equip refused for Agility without the Medic's buff"))
        {
            return 1;
        }

        // The Medic walks into range of the Operative, targets it and casts Reflex Boost.
        Position? OperativeSeenByMedic() =>
            medic.World.Entities.TryGetValue(op.World.SelfId, out var e) && e.HasSamples ? e.Latest.Position : null;
        (short, short) Steer()
        {
            if (OperativeSeenByMedic() is not { } to)
            {
                return (0, 0);
            }
            var from = medic.World.Self.Position;
            double dx = to.X - from.X, dz = to.Z - from.Z, len = Math.Sqrt((dx * dx) + (dz * dz));
            return len < 15_000 ? ((short)0, (short)0) : ((short)(dx / len * Movement.DirectionScale), (short)(dz / len * Movement.DirectionScale));
        }
        bool InRange() => OperativeSeenByMedic() is { } at && Movement.Distance(at, medic.World.Self.Position) < 20_000;
        if (!await Until(InRange, "medic walks within 20 m", Steer))
        {
            return 1;
        }
        medic.SetTarget(op.World.SelfId);
        if (!await Until(() => medic.World.Status.TargetId == op.World.SelfId, "medic targets the operative"))
        {
            return 1;
        }
        medic.Say("/cast reflex_boost_1");
        if (!await Until(() => notices.Any(n => n.EndsWith("cast Reflex Boost I on you.", StringComparison.Ordinal)), "medic's Reflex Boost lands on the operative"))
        {
            return 1;
        }

        op.Say($"/equip {KestrelPosition()}");
        if (!await Until(() => KestrelWornActive() == true, "operative wears the Kestrel, active, twinked with the Medic's buff"))
        {
            return 1;
        }

        // Zone hand-off: walk the twinked Operative onto the transit pad into Rustfall Basin. The
        // rifle, its own buff and the Medic's buff must all arrive with it, still active.
        var pack = Meridian.Content.ContentPacks.Embedded;
        var startZone = op.World.ZoneId;
        // The gate runs Glasswind and Rustfall only, so it takes the pad to Rustfall.
        var exit = pack.Zones.TryGet((int)startZone, out var here) ? here.Exits.FirstOrDefault(e => e.ToZoneKey == "rustfall_basin") : null;
        if (exit is null)
        {
            Console.WriteLine("M2 GATE: FAIL (the start zone has no exit)");
            return 1;
        }
        var destination = pack.Zones[exit.ToZoneKey].Id;
        var reflexId = GameSession.FindProtocol("reflex_boost_1")!.Id;
        var arrived = await Until(
            () =>
            {
                opSteer = op.World.InWorld && op.World.ZoneId == startZone ? Movement.DirectionTowards(op.World.Self.Position, exit.Area.Center) : ((short)0, (short)0);
                return op.World.InWorld && op.World.ZoneId == destination;
            },
            $"operative walks onto the pad and arrives in {pack.Zones[exit.ToZoneKey].Name}");
        opSteer = (0, 0);
        if (!arrived)
        {
            return 1;
        }
        if (!await Until(() => KestrelWornActive() == true && op.World.Protocols!.Running.Any(r => r.ProtocolId == reflexId) && op.Connected,
            "Kestrel still worn and active, Medic's buff still running, same connection"))
        {
            return 1;
        }

        op.Say("/cancel reflex_boost_1");
        if (!await Until(() => KestrelWornActive() == false, "Kestrel inactive once the Medic's buff is gone"))
        {
            return 1;
        }

        foreach (var s in steps)
        {
            Console.WriteLine($"  {s}");
        }
        Console.WriteLine("M2 GATE: PASS (twinked a rifle with another player's buff, and carried it into another zone)");
        return 0;
    }
}
