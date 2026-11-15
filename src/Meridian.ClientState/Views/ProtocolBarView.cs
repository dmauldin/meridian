using Meridian.Core.Content;

namespace Meridian.ClientState.Views;

/// <summary>A button on the Protocol bar.</summary>
/// <param name="Hotkey">1-9 for the first nine known Protocols, else 0.</param>
public sealed record ProtocolButton(int ProtocolId, string Name, int EnergyCost, bool SelfOnly, bool Affordable, int Hotkey, string Tooltip);

/// <summary>A Protocol running on the player, with time left as of now.</summary>
public sealed record BuffChip(int ProtocolId, string Name, int RemainingMs)
{
    /// <summary>"9:52", or "0:05".</summary>
    public string Countdown => $"{RemainingMs / 60_000}:{RemainingMs / 1000 % 60:D2}";
}

public static class ProtocolBarView
{
    public static IReadOnlyList<ProtocolButton> Buttons(ContentPack pack, ClientProtocols? protocols, int energy)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (protocols is null)
        {
            return [];
        }
        var hotkey = 0;
        return
        [
            .. protocols.Known
                .Select(id => pack.Protocols.TryGet(id, out var p) ? p : null)
                .OfType<ProtocolTemplate>()
                .Select(p =>
                {
                    hotkey++;
                    var self = p.Target == Meridian.Core.Content.ProtocolTarget.Self;
                    var effects = string.Join(", ", p.Effects.Select(e => $"+{e.Value} {StatNames.Display(e.Stat)}"));
                    var on = self ? "yourself" : "your player target, or yourself";
                    return new ProtocolButton(p.Id, p.Name, p.EnergyCost, self, energy >= p.EnergyCost, hotkey <= 9 ? hotkey : 0,
                        $"{p.Name}: {effects}. {p.EnergyCost} energy, {p.DurationMs / 60_000} min. Casts on {on}.");
                }),
        ];
    }

    /// <summary>Running Protocols counted down by the time since the server's last report.</summary>
    public static IReadOnlyList<BuffChip> Buffs(ContentPack pack, ClientProtocols? protocols, double elapsedSinceReportMs)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (protocols is null)
        {
            return [];
        }
        var elapsed = (int)Math.Clamp(elapsedSinceReportMs, 0, int.MaxValue);
        return
        [
            .. protocols.Running.Select(r => new BuffChip(
                r.ProtocolId,
                pack.Protocols.TryGet(r.ProtocolId, out var p) ? p.Name : $"Protocol {r.ProtocolId}",
                Math.Max(0, r.RemainingMs - elapsed))),
        ];
    }
}
