using Meridian.Content;
using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.World;
using Meridian.Persistence;
using Meridian.Server.Zone;
using Meridian.Wire;

namespace Meridian.Server.Tests;

/// <summary>Buying, selling and credit drops, in Freeport Ashgate as content defines it.</summary>
public class ZoneShopTests
{
    private static readonly ClientRef A = new(1, 1);
    private static readonly ContentPack Pack = ContentPacks.Embedded;
    private static readonly ShopDef Outfitter = Pack.Shops["shop.ashgate_outfitter"];
    private static readonly ShopDef Exchange = Pack.Shops["shop.lattice_exchange"];
    private static readonly ItemTemplate Carbine = Pack.Items["rifle.combine_carbine"];

    private static (ZoneSimulation Sim, RecordingOutbox Out) Ashgate()
    {
        var outbox = new RecordingOutbox();
        return (new ZoneSimulation(new ZoneOptions { ZoneKey = "freeport_ashgate", Seed = 3 }, outbox), outbox);
    }

    private static void Send<T>(ZoneSimulation sim, in T message)
        where T : struct, IWireMessage<T> => sim.HandleClientPacket(A, Envelope.ToArray(message));

    /// <summary>A stored Operative with <paramref name="credits"/>, standing at <paramref name="at"/> (default: beside the Outfitter).</summary>
    private static Player Shopper(ZoneSimulation sim, long credits = 1_000, Position? at = null)
    {
        var p = sim.Join(A, CharacterRecords.New(Pack, 1, "Rook", Profession.Operative, Origin.Baseline) with { Id = 7, Version = 1, Credits = credits })!;
        p.Position = at ?? Outfitter.At with { X = Outfitter.At.X + 2_000 };
        sim.Step();
        return p;
    }

    private static string LastNotice(RecordingOutbox outbox) => outbox.Last<Notice>(A).Text!;

    [Fact]
    public void Buying_takes_the_price_and_gives_the_item()
    {
        var (sim, outbox) = Ashgate();
        var p = Shopper(sim);
        Send(sim, new BuyItem { ShopId = (uint)Outfitter.Id, StockIndex = 0 }); // carbine QL 4, 276
        sim.Step();
        Assert.Equal(1_000 - 276, p.Credits);
        Assert.Contains(p.Outfit.Inventory, i => i.Item == new OwnedItem(Carbine.Id, 4));
        Assert.Equal("Bought Combine Issue Carbine (QL 4) for 276 credits.", LastNotice(outbox));
        var inv = outbox.Last<InventoryState>(A);
        Assert.Equal(724, inv.Credits);
        Assert.Contains(inv.Carried!, c => c.TemplateId == (uint)Carbine.Id && c.Ql == 4);
    }

    [Fact]
    public void Selling_pays_a_tenth_of_the_value_and_takes_the_item()
    {
        var (sim, outbox) = Ashgate();
        var p = Shopper(sim, credits: 0);
        var at = p.Outfit.Add(new OwnedItem(Carbine.Id, 4));
        Send(sim, new SellItem { ShopId = (uint)Outfitter.Id, Position = (byte)at });
        sim.Step();
        Assert.Equal(27, p.Credits);
        Assert.Null(p.Outfit.InventoryAt(at));
        Assert.Equal("Sold Combine Issue Carbine (QL 4) for 27 credits.", LastNotice(outbox));
        Assert.Equal(27, outbox.Last<InventoryState>(A).Credits);
    }

    [Fact]
    public void Any_shop_buys_any_item_with_a_value()
    {
        var (sim, _) = Ashgate();
        var p = Shopper(sim, credits: 0, at: Exchange.At);
        var at = p.Outfit.Add(new OwnedItem(Carbine.Id, 4));
        Send(sim, new SellItem { ShopId = (uint)Exchange.Id, Position = (byte)at }); // the Protocol shop takes a rifle
        sim.Step();
        Assert.Equal(27, p.Credits);
    }

    [Fact]
    public void A_purchase_you_cannot_afford_changes_nothing()
    {
        var (sim, outbox) = Ashgate();
        var p = Shopper(sim, credits: 275);
        Send(sim, new BuyItem { ShopId = (uint)Outfitter.Id, StockIndex = 0 });
        sim.Step();
        Assert.Equal(275, p.Credits);
        Assert.DoesNotContain(p.Outfit.Inventory, i => i.Item.TemplateId == Carbine.Id);
        Assert.Equal("Combine Issue Carbine (QL 4) costs 276 credits; you have 275.", LastNotice(outbox));
    }

    [Fact]
    public void A_full_inventory_keeps_your_credits()
    {
        var (sim, outbox) = Ashgate();
        var p = Shopper(sim);
        while (p.Outfit.Add(new OwnedItem(Carbine.Id, 1)) >= 0)
        {
        }
        Send(sim, new BuyItem { ShopId = (uint)Outfitter.Id, StockIndex = 0 });
        sim.Step();
        Assert.Equal(1_000, p.Credits);
        Assert.Equal("Your inventory is full.", LastNotice(outbox));
    }

    [Theory]
    [InlineData(5_000, true)]   // exactly at shop_range_m
    [InlineData(5_001, false)]
    public void You_must_stand_within_range_of_the_terminal(int distanceMm, bool trades)
    {
        var (sim, outbox) = Ashgate();
        var p = Shopper(sim, at: Outfitter.At with { X = Outfitter.At.X + distanceMm });
        Send(sim, new BuyItem { ShopId = (uint)Outfitter.Id, StockIndex = 0 });
        sim.Step();
        Assert.Equal(trades, p.Credits < 1_000);
        if (!trades)
        {
            Assert.Equal("You are too far from Ashgate Outfitter.", LastNotice(outbox));
        }
    }

    [Theory]
    [InlineData(99u)]   // no such shop
    [InlineData(0u)]
    public void Unknown_shops_are_refused(uint shopId)
    {
        var (sim, outbox) = Ashgate();
        var p = Shopper(sim);
        Send(sim, new BuyItem { ShopId = shopId, StockIndex = 0 });
        Send(sim, new SellItem { ShopId = shopId, Position = 0 });
        sim.Step();
        Assert.Equal(1_000, p.Credits);
        Assert.Equal("There is no such shop here.", LastNotice(outbox));
    }

    [Fact]
    public void A_shop_in_another_zone_is_refused_even_at_the_same_spot()
    {
        // Glasswind Flats has no shops; the Outfitter's id names a shop in Ashgate.
        var outbox = new RecordingOutbox();
        var sim = new ZoneSimulation(new ZoneOptions { MobCount = 0, Seed = 3 }, outbox);
        var p = Shopper(sim, at: Outfitter.At);
        Send(sim, new BuyItem { ShopId = (uint)Outfitter.Id, StockIndex = 0 });
        sim.Step();
        Assert.Equal(1_000, p.Credits);
        Assert.Equal("There is no such shop here.", LastNotice(outbox));
    }

    [Fact]
    public void Bad_stock_indices_and_empty_positions_are_refused()
    {
        var (sim, outbox) = Ashgate();
        var p = Shopper(sim);
        Send(sim, new BuyItem { ShopId = (uint)Outfitter.Id, StockIndex = (byte)Outfitter.Stock.Count });
        sim.Step();
        Assert.Equal("Ashgate Outfitter does not sell that.", LastNotice(outbox));
        Send(sim, new SellItem { ShopId = (uint)Outfitter.Id, Position = 29 });
        sim.Step();
        Assert.Equal("There is nothing there.", LastNotice(outbox));
        Assert.Equal(1_000, p.Credits);
    }

    [Fact]
    public void Items_without_a_value_cannot_be_sold()
    {
        var (sim, outbox) = Ashgate();
        var p = Shopper(sim);
        var at = p.Outfit.Add(new OwnedItem(Pack.Items["unarmed.fists"].Id, 1));
        Send(sim, new SellItem { ShopId = (uint)Outfitter.Id, Position = (byte)at });
        sim.Step();
        Assert.Equal(1_000, p.Credits);
        Assert.NotNull(p.Outfit.InventoryAt(at));
        Assert.Equal("Ashgate Outfitter will not buy Fists (QL 1).", LastNotice(outbox));
    }

    [Fact]
    public void Selling_never_overflows_the_cap()
    {
        var (sim, _) = Ashgate();
        var p = Shopper(sim, credits: EconomyDef.MaxCredits - 1);
        var at = p.Outfit.Add(new OwnedItem(Carbine.Id, 300));
        Send(sim, new SellItem { ShopId = (uint)Outfitter.Id, Position = (byte)at });
        sim.Step();
        Assert.Equal(EconomyDef.MaxCredits, p.Credits);
    }

    [Fact]
    public void A_trade_checkpoints_the_character()
    {
        var (sim, _) = Ashgate();
        var p = Shopper(sim);
        sim.DrainCheckpoints();
        Send(sim, new BuyItem { ShopId = (uint)Outfitter.Id, StockIndex = 0 });
        sim.Step();
        // Checkpoints are scheduled; leaving forces one with the new balance.
        sim.Leave(A);
        var saved = Assert.Single(sim.DrainCheckpoints());
        Assert.Equal(724, saved.Credits);
        Assert.Contains(saved.Items, i => i.TemplateId == Carbine.Id && i.Ql == 4);
    }
}
