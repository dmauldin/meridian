using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.World;
using Meridian.Wire;

namespace Meridian.Server.Zone;

/// <summary>
/// Shops and credits. Both trades are intents: the zone checks the shop stands in this zone
/// and in range, and the price, balance and inventory, then applies the whole trade in one
/// step (no state where the credits or the item exist twice, or neither).
/// </summary>
public sealed partial class ZoneSimulation
{
    public void OnBuyItem(ClientRef context, in BuyItem message)
    {
        var p = _byClient[context];
        if (ShopInReach(p, (int)message.ShopId) is not { } shop)
        {
            return;
        }
        if (message.StockIndex >= shop.Stock.Count)
        {
            SendNotice(p, NoticeRefused, $"{shop.Name} does not sell that.");
            return;
        }
        var stock = shop.Stock[message.StockIndex];
        var template = _content.Items[stock.ItemKey];
        var item = new OwnedItem(template.Id, stock.Ql);
        var price = template.ValueAt(stock.Ql)!.Value; // the pack refuses stock without a value
        if (p.Credits < price)
        {
            SendNotice(p, NoticeRefused, $"{Describe(item)} costs {price} credits; you have {p.Credits}.");
            return;
        }
        if (p.Outfit.Add(item) < 0)
        {
            SendNotice(p, NoticeRefused, "Your inventory is full.");
            return;
        }
        p.Credits -= price;
        SendNotice(p, NoticeInfo, $"Bought {Describe(item)} for {price} credits.");
    }

    public void OnSellItem(ClientRef context, in SellItem message)
    {
        var p = _byClient[context];
        if (ShopInReach(p, (int)message.ShopId) is not { } shop)
        {
            return;
        }
        if (p.Outfit.InventoryAt(message.Position) is not { } item)
        {
            SendNotice(p, NoticeRefused, "There is nothing there.");
            return;
        }
        var price = _content.Items.TryGet(item.TemplateId, out var template) ? _content.Economy.SellPrice(template, item.Ql) : null;
        if (price is not > 0)
        {
            SendNotice(p, NoticeRefused, $"{shop.Name} will not buy {Describe(item)}.");
            return;
        }
        p.Outfit.Discard(message.Position);
        p.Credits = EconomyDef.AddCredits(p.Credits, price.Value);
        SendNotice(p, NoticeInfo, $"Sold {Describe(item)} for {price} credits.");
    }

    /// <summary>The shop, if it stands in this zone and the living player is within reach; otherwise says why not.</summary>
    private ShopDef? ShopInReach(Player p, int shopId)
    {
        if (!_content.Shops.TryGet(shopId, out var shop) || _content.ZoneOfShop(shopId) != _zone.Id)
        {
            SendNotice(p, NoticeRefused, "There is no such shop here.");
            return null;
        }
        if (p.Dead)
        {
            SendNotice(p, NoticeRefused, "You cannot trade while dead.");
            return null;
        }
        if (Movement.Distance(p.Position, shop.At) > _content.Economy.ShopRangeMm)
        {
            SendNotice(p, NoticeRefused, $"You are too far from {shop.Name}.");
            return null;
        }
        return shop;
    }

    /// <summary>
    /// Credits the mob carries, to the looter. Rolled even with no looter, like loot, so the
    /// random stream does not depend on who is online.
    /// </summary>
    private void DropCredits(Mob mob, Player? looter)
    {
        if (mob.Template.Credits is not { } drop)
        {
            return;
        }
        var amount = _rng.NextInt(drop.Min, drop.Max);
        if (looter is null || amount == 0)
        {
            return;
        }
        looter.Credits = EconomyDef.AddCredits(looter.Credits, amount);
        SendNotice(looter, NoticeInfo, $"You found {amount} credits.");
    }
}
