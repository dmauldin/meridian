using Meridian.Core.Content;
using Meridian.Persistence;
using Meridian.Wire;

namespace Meridian.Server.Zone;

/// <summary>A player leaving for another zone: their final save, placed in the destination.</summary>
/// <param name="Record">Saved before the gateway re-routes the client, so the destination loads exactly this.</param>
public sealed record Departure(ClientRef Client, CharacterRecord Record, uint ToZoneId);

/// <summary>Zone exits and departures.</summary>
public sealed partial class ZoneSimulation
{
    /// <summary>After joining or arriving, exits are ignored this long (40 ticks = 2 s), so nobody ping-pongs.</summary>
    public const int ExitGraceTicks = 40;

    private List<Departure> _departures = [];

    /// <summary>
    /// Players who stepped into an exit since the last call. The simulation has already removed
    /// them and told their client; the host saves each record and then asks the gateway to
    /// re-route the client to the destination zone.
    /// </summary>
    public IReadOnlyList<Departure> DrainDepartures()
    {
        if (_departures.Count == 0)
        {
            return [];
        }
        var drained = _departures;
        _departures = [];
        return drained;
    }

    /// <summary>
    /// Sends players standing in an exit to its zone. Dead players stay (they respawn here), and
    /// unstored characters cannot travel: the destination loads characters from the store.
    /// </summary>
    private void CheckExits()
    {
        if (_zone.Exits.Count == 0)
        {
            return;
        }
        for (var i = _players.Count - 1; i >= 0; i--)
        {
            var p = _players[i];
            if (p.Dead || p.CharacterId == 0 || Tick < p.ExitGraceUntilTick)
            {
                continue;
            }
            foreach (var exit in _zone.Exits)
            {
                if (exit.Area.Contains(p.Position))
                {
                    Depart(p, exit);
                    break;
                }
            }
        }
    }

    private void Depart(Player p, ZoneExit exit)
    {
        var to = _content.Zones[exit.ToZoneKey];
        Send(p, new ZoneChange { ZoneId = to.Id }); // before anything else: the client clears its world
        // The final save already places the character at the arrival point in the destination, so
        // whatever happens next (the hand-off, or a crash before it) the character is there.
        var record = Snapshot(p) with { Placement = new Placement(to.Key, exit.Arrive, p.Health) };
        Remove(p, finalSave: null);
        _departures.Add(new Departure(p.Client, record, to.Id));
    }
}
