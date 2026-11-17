using System.Globalization;
using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Stats;

namespace Meridian.ClientState.Views;

/// <summary>One line of a shop's stock as the player sees it.</summary>
/// <param name="Affordable">The player has the credits now.</param>
/// <param name="Usable">The player meets the item's requirements now (worn items: to equip it; Protocols: to upload it).</param>
public sealed record ShopRow(int StockIndex, string Name, long Price, bool Affordable, bool Usable);

/// <summary>What the shop window shows. Prices come from content; the zone decides every trade.</summary>
public static class ShopView
{
    public static IReadOnlyList<ShopRow> Rows(ContentPack pack, ShopDef shop, long credits, IReadOnlyDictionary<Stat, int> effective, Profession profession)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(shop);
        return [.. shop.Stock.Select((s, i) =>
        {
            var template = pack.Items[s.ItemKey];
            var item = new OwnedItem(template.Id, s.Ql);
            var price = template.ValueAt(s.Ql) ?? 0;
            var usable = ItemView.Describe(pack, item, effective, profession).Requirements.All(r => r.Met);
            return new ShopRow(i, ItemView.Name(pack, item), price, price <= credits, usable);
        })];
    }

    /// <summary>What any shop pays for <paramref name="item"/>, or null if it will not buy it.</summary>
    public static long? SellPrice(ContentPack pack, OwnedItem item)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return pack.Items.TryGet(item.TemplateId, out var t) && pack.Economy.SellPrice(t, item.Ql) is > 0 and var price ? price : null;
    }

    /// <summary>"1,234 cr".</summary>
    public static string Credits(long credits) => credits.ToString("N0", CultureInfo.InvariantCulture) + " cr";
}
