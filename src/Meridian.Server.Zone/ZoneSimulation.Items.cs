using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Requirements;
using Meridian.Wire;

namespace Meridian.Server.Zone;

/// <summary>Inventory, equipment and loot.</summary>
public sealed partial class ZoneSimulation
{
    public void OnEquipItem(ClientRef context, in EquipItem message)
    {
        var p = _byClient[context];
        var item = p.Outfit.InventoryAt(message.Position);
        var failures = new List<RequirementFailure>();
        var result = p.Outfit.Equip(message.Position, failures);
        if (result != ItemResult.Done)
        {
            SendNotice(p, NoticeRefused, result switch
            {
                ItemResult.RequirementsNotMet => $"You cannot use {Describe(item)}: {Explain(failures)}.",
                ItemResult.InventoryFull => "No room in your inventory for what you would take off.",
                ItemResult.NotEquippable => $"{Describe(item)} cannot be worn.",
                _ => "There is nothing there.",
            });
        }
        SettleItems(p);
    }

    public void OnUnequipItem(ClientRef context, in UnequipItem message)
    {
        var p = _byClient[context];
        var result = p.Outfit.Unequip((EquipSlot)message.Slot);
        if (result != ItemResult.Done)
        {
            SendNotice(p, NoticeRefused, result == ItemResult.InventoryFull ? "Your inventory is full." : "Nothing is worn there.");
        }
        SettleItems(p);
    }

    public void OnDiscardItem(ClientRef context, in DiscardItem message)
    {
        var p = _byClient[context];
        if (p.Outfit.Discard(message.Position) is { } item)
        {
            SendNotice(p, NoticeInfo, $"Discarded {Describe(item)}.");
        }
    }

    /// <summary>
    /// Re-checks worn items whenever the player's stats changed (training, a buff landing or
    /// running out, an item coming off) and tells them what turned inactive or active again.
    /// Settling can itself change stats (an item's bonuses come or go), so it loops until quiet;
    /// <see cref="Outfit.Reevaluate"/> is bounded, so this is too.
    /// </summary>
    private void SettleItems(Player p)
    {
        for (var pass = 0; pass < 4 && p.LastCheckedStatsRevision != p.Character.Stats.Revision; pass++)
        {
            p.LastCheckedStatsRevision = p.Character.Stats.Revision;
            foreach (var (slot, active) in p.Outfit.Reevaluate())
            {
                var name = p.Outfit.Worn.Where(w => w.Slot == slot).Select(w => Describe(w.Item)).FirstOrDefault() ?? slot.ToString();
                SendNotice(p, NoticeInfo, active
                    ? $"{name} is active again."
                    : $"{name} is inactive: you no longer meet its requirements.");
            }
        }
        p.Health = Math.Min(p.Health, p.MaxHealth);
    }

    private void SendInventoryIfChanged(Player p)
    {
        if (p.LastSentOutfitRevision == p.Outfit.Revision)
        {
            return;
        }
        p.LastSentOutfitRevision = p.Outfit.Revision;
        Send(p, new InventoryState
        {
            Size = (byte)p.Outfit.InventorySize,
            Carried = [.. p.Outfit.Inventory.Select(i => new CarriedItem { Position = (byte)i.Position, TemplateId = (uint)i.Item.TemplateId, Ql = (ushort)i.Item.Ql })],
            Worn = [.. p.Outfit.Worn.Select(w => new WornItem { Slot = (byte)w.Slot, TemplateId = (uint)w.Item.TemplateId, Ql = (ushort)w.Item.Ql, Active = w.Active })],
        });
    }

    /// <summary>
    /// Rolls each of the mob's loot entries and gives what drops to whoever built the most threat
    /// (ties go to the lowest entity id, so it is deterministic). QL is the mob's level plus or
    /// minus the entry's spread.
    /// </summary>
    private void DropLoot(Mob mob)
    {
        Player? winner = null;
        var best = long.MinValue;
        foreach (var (id, threat) in mob.Threat.OrderBy(t => t.Key))
        {
            if (threat > best && Find(id) is Player p)
            {
                winner = p;
                best = threat;
            }
        }
        foreach (var loot in mob.Template.Loot)
        {
            // Roll even with no winner, so the random stream does not depend on who is online.
            var dropped = _rng.Chance(loot.ChanceBp);
            var ql = mob.Template.Level + _rng.NextInt(-loot.QlSpread, loot.QlSpread);
            if (!dropped || winner is null)
            {
                continue;
            }
            var template = _content.Items[loot.ItemKey];
            var item = new OwnedItem(template.Id, template.ClampQl(ql));
            SendNotice(winner, NoticeInfo, winner.Outfit.Add(item) >= 0
                ? $"You found {Describe(item)}."
                : $"Your inventory is full: {Describe(item)} was lost.");
        }
    }

    /// <summary>Slash commands the server handles. Unknown ones get a notice, never a broadcast.</summary>
    private void Command(Player p, string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case ["/give", var key, var qlText] when _options.DevCommands:
                if (!_content.Items.TryGet(key, out var template)
                    || !int.TryParse(qlText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ql))
                {
                    SendNotice(p, NoticeRefused, "Usage: /give <item key> <ql>");
                    return;
                }
                var item = new OwnedItem(template.Id, template.ClampQl(ql));
                var at = p.Outfit.Add(item);
                SendNotice(p, at >= 0 ? NoticeInfo : NoticeRefused, at >= 0 ? $"Given {Describe(item)}." : "Your inventory is full.");
                return;
            default:
                SendNotice(p, NoticeRefused, $"Unknown command {parts[0]}.");
                return;
        }
    }

    private string Describe(OwnedItem? item) =>
        item is { } i && _content.Items.TryGet(i.TemplateId, out var t) ? $"{t.Name} (QL {i.Ql})" : "that item";

    /// <summary>"Rifle 35/40, profession must be Operative or Trooper".</summary>
    private static string Explain(List<RequirementFailure> failures) =>
        failures.Count == 0 ? "requirements not met" : string.Join(", ", failures.Select(Explain));

    private static string Explain(RequirementFailure f) => f switch
    {
        StatShortfall s => $"{s.Stat} {s.Actual}/{s.Required}",
        ProfessionNotAllowed p => $"profession must be {string.Join(" or ", p.Allowed.Order())}",
        OriginNotAllowed o => $"origin must be {string.Join(" or ", o.Allowed.Order())}",
        NoAlternativeMet n => string.Join(" or ", n.Alternatives.Select(a => string.Join(", ", a.Select(f => Explain(f))))),
        _ => f.ToString() ?? "requirement",
    };
}
