using System.Globalization;
using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Stats;

namespace Meridian.ClientState.Views;

/// <summary>A requirement line, coloured by whether the player meets it now.</summary>
public sealed record RequirementLine(string Text, bool Met);

/// <summary>What the inventory window shows for one item.</summary>
/// <param name="Kind">"Two-handed rifle", "Implant (eye)", "Protocol" and so on.</param>
/// <param name="Weapon">Damage and speed for a weapon, else null.</param>
public sealed record ItemDetails(string Title, string Kind, IReadOnlyList<RequirementLine> Requirements, IReadOnlyList<string> Effects, string? Weapon);

public enum ItemAction
{
    Equip,
    Unequip,
    Upload,
    Discard,
}

/// <summary>Item descriptions and the actions offered for them. Display only: the server decides.</summary>
public static class ItemView
{
    public static string Name(ContentPack pack, OwnedItem item)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return pack.Items.TryGet(item.TemplateId, out var t) ? (t.IsProtocol ? t.Name : $"{t.Name} QL {item.Ql}") : $"Unknown item {item.TemplateId}";
    }

    /// <summary>Requirements are checked against <paramref name="effective"/> (the player's current values).</summary>
    public static ItemDetails Describe(ContentPack pack, OwnedItem item, IReadOnlyDictionary<Stat, int> effective, Profession profession)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(effective);
        if (!pack.Items.TryGet(item.TemplateId, out var t))
        {
            return new ItemDetails(Name(pack, item), "Unknown", [], [], null);
        }
        int Value(Stat s) => effective.GetValueOrDefault(s);
        var requirements = new List<RequirementLine>();
        var effects = new List<string>();
        string kind;
        string? weapon = null;

        if (t.ProtocolKey is { } key && pack.Protocols.TryGet(key, out var protocol))
        {
            kind = "Protocol: upload to learn " + protocol.Name;
            requirements.AddRange(protocol.Requirements.OrderBy(r => r.Key).Select(r =>
                new RequirementLine($"{StatNames.Display(r.Key)} {r.Value} (you: {Value(r.Key)})", Value(r.Key) >= r.Value)));
            if (protocol.Professions.Count > 0)
            {
                requirements.Add(new RequirementLine($"Profession: {string.Join(" or ", protocol.Professions)}", protocol.Professions.Contains(profession)));
            }
            effects.AddRange(protocol.Effects.Select(e => Signed(e.Stat, e.Value)));
            effects.Add($"{protocol.EnergyCost} energy, {protocol.DurationMs / 60_000} min, range {protocol.RangeMm / 1000} m");
        }
        else
        {
            kind = SlotName(t.Slot);
            requirements.AddRange(t.Requirements.Select(r =>
            {
                var need = r.Minimum.At(item.Ql);
                return new RequirementLine($"{StatNames.Display(r.Stat)} {need} (you: {Value(r.Stat)})", Value(r.Stat) >= need);
            }));
            if (t.Professions.Count > 0)
            {
                requirements.Add(new RequirementLine($"Profession: {string.Join(" or ", t.Professions)}", t.Professions.Contains(profession)));
            }
            effects.AddRange(t.Effects.Select(e => Signed(e.Stat, e.Value.At(item.Ql))));
            if (t.WeaponAt(item.Ql) is { } w)
            {
                weapon = string.Create(CultureInfo.InvariantCulture,
                    $"{w.MinDamage}-{w.MaxDamage} {w.DamageType} damage, {w.BaseAttackTimeMs / 1000.0:0.0} s, {w.RangeMm / 1000} m, uses {StatNames.Display(w.AttackSkill)}");
            }
        }
        return new ItemDetails(Name(pack, item), kind, requirements, effects, weapon);
    }

    /// <summary>Actions offered for a carried item (worn items offer <see cref="ItemAction.Unequip"/>).</summary>
    public static IReadOnlyList<ItemAction> ActionsFor(ContentPack pack, OwnedItem item)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (!pack.Items.TryGet(item.TemplateId, out var t))
        {
            return [ItemAction.Discard];
        }
        return t.IsProtocol ? [ItemAction.Upload, ItemAction.Discard] : [ItemAction.Equip, ItemAction.Discard];
    }

    public static string SlotName(EquipSlot slot) => slot switch
    {
        EquipSlot.TwoHand => "Two hands",
        EquipSlot.MainHand => "Main hand",
        EquipSlot.OffHand => "Off hand",
        EquipSlot.None => "Not wearable",
        _ when slot.ToString().StartsWith("Implant", StringComparison.Ordinal) => $"Implant ({StatNames.Spaced(slot.ToString()[7..]).ToLowerInvariant()})",
        _ => StatNames.Spaced(slot.ToString()),
    };

    /// <summary>Slots in display order for the equipment panel.</summary>
    public static IReadOnlyList<EquipSlot> Slots { get; } =
        [.. Enum.GetValues<EquipSlot>().Where(s => s != EquipSlot.None).OrderBy(s => s)];

    private static string Signed(Stat stat, int value) =>
        string.Create(CultureInfo.InvariantCulture, $"{(value >= 0 ? "+" : "")}{value} {StatNames.Display(stat)}");
}
