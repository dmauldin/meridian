using Godot;
using Meridian.ClientState;
using Meridian.ClientState.Views;
using Meridian.Content;
using Meridian.Core.Content;

namespace Meridian.Client.Ui;

/// <summary>
/// The stock of the shop terminal in reach: price, Buy, and whether you can afford and use it
/// (red: you could not equip it now). Selling is from the inventory window while a shop is in
/// reach. The zone decides every trade; this only asks.
/// </summary>
public partial class ShopWindow : GameWindow
{
    private readonly Label _shopName = UiStyle.Label("", UiStyle.Accent, 16);
    private readonly Label _credits = UiStyle.Label("", UiStyle.Text, 14);
    private readonly GridContainer _rows = new() { Columns = 3 };
    private GameSession? _session;

    public ShopWindow()
        : base("Shop", "F", new Vector2(520, 520))
    {
        var header = new HBoxContainer();
        header.AddChild(_shopName);
        header.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        header.AddChild(_credits);
        Body.AddChild(header);
        _rows.AddThemeConstantOverride("h_separation", 12);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_rows);
        Body.AddChild(scroll);
        var hint = UiStyle.Label("To sell, open your inventory (I) and pick an item. Shops pay a tenth of an item's value.", UiStyle.Muted, 12);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Body.AddChild(hint);
    }

    /// <summary>The shop shown, while one is.</summary>
    public ShopDef? Shop { get; private set; }

    public void Bind(GameSession session) => _session = session;

    public void Open(ShopDef shop)
    {
        Shop = shop;
        Visible = true;
        Refresh();
    }

    public void Close()
    {
        Shop = null;
        Visible = false;
    }

    public void Refresh()
    {
        UiStyle.Clear(_rows);
        if (Shop is not { } shop || _session?.World is not { Build: { } build } world)
        {
            return;
        }
        var credits = world.Inventory?.Credits ?? 0;
        _shopName.Text = shop.Name;
        _credits.Text = ShopView.Credits(credits);
        foreach (var row in ShopView.Rows(ContentPacks.Embedded, shop, credits, world.EffectiveStats, build.Profession.Id))
        {
            var name = UiStyle.Label(row.Name, row.Usable ? UiStyle.Text : UiStyle.Bad, 13);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.TooltipText = row.Usable ? "" : "You do not meet its requirements now.";
            name.MouseFilter = MouseFilterEnum.Pass;
            _rows.AddChild(name);
            _rows.AddChild(UiStyle.Label(ShopView.Credits(row.Price), row.Affordable ? UiStyle.Text : UiStyle.Muted, 13, HorizontalAlignment.Right));
            var buy = UiStyle.Button("Buy", row.Affordable ? null : "Not enough credits");
            buy.Disabled = !row.Affordable;
            var index = row.StockIndex;
            buy.Pressed += () => _session.Buy(shop.Id, index);
            _rows.AddChild(buy);
        }
    }
}
