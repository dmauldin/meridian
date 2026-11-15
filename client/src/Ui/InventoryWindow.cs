using Godot;
using Meridian.ClientState;
using Meridian.ClientState.Views;
using Meridian.Content;
using Meridian.Core.Items;

namespace Meridian.Client.Ui;

/// <summary>
/// Worn slots and carried items. Click one to see its requirements (against your current
/// stats) and bonuses, and to equip, unequip, upload or discard it. Inactive worn items show red.
/// </summary>
public partial class InventoryWindow : GameWindow
{
    private readonly GridContainer _worn = new() { Columns = 2 };
    private readonly GridContainer _carried = new() { Columns = 2 };
    private readonly Label _count = UiStyle.Label("", UiStyle.Muted, 12);
    private readonly VBoxContainer _details = new();
    private GameSession? _session;
    private (bool Worn, int Key)? _selected;

    public InventoryWindow()
        : base("Inventory", "I", new Vector2(560, 560))
    {
        var split = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 12);
        var lists = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(240, 0) };
        lists.AddChild(UiStyle.Label("Worn", UiStyle.Accent, 15));
        lists.AddChild(_worn);
        lists.AddChild(new HSeparator());
        var carriedHeader = new HBoxContainer();
        carriedHeader.AddChild(UiStyle.Label("Carried", UiStyle.Accent, 15));
        carriedHeader.AddChild(_count);
        lists.AddChild(carriedHeader);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _carried.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_carried);
        lists.AddChild(scroll);
        split.AddChild(lists);
        _details.CustomMinimumSize = new Vector2(200, 0);
        _details.SizeFlagsHorizontal = SizeFlags.Fill;
        split.AddChild(_details);
        Body.AddChild(split);
    }

    public void Bind(GameSession session) => _session = session;

    /// <summary>Selects a carried position (the autopilot uses this for screenshots).</summary>
    public void SelectCarried(int position)
    {
        _selected = (false, position);
        Refresh();
    }

    public void Refresh()
    {
        UiStyle.Clear(_worn);
        UiStyle.Clear(_carried);
        if (_session?.World.Inventory is not { } inventory)
        {
            _count.Text = "  waiting...";
            return;
        }
        var pack = ContentPacks.Embedded;
        // Only occupied slots: empty ones have nothing to click (you equip from the carried list).
        foreach (var w in inventory.Worn)
        {
            _worn.AddChild(UiStyle.Label(ItemView.SlotName(w.Slot), UiStyle.Muted, 12));
            var name = ItemView.Name(pack, w.Item) + (w.Active ? "" : " (inactive)");
            _worn.AddChild(ItemButton(name, w.Active ? UiStyle.Text : UiStyle.Bad, (true, (int)w.Slot)));
        }
        var empty = ItemView.Slots.Count - inventory.Worn.Count;
        _worn.AddChild(UiStyle.Label("", UiStyle.Muted, 12));
        _worn.AddChild(UiStyle.Label($"{empty} empty slots", UiStyle.Muted, 12));

        _count.Text = $"  {inventory.Carried.Count}/{inventory.Size}";
        foreach (var (position, item) in inventory.Carried)
        {
            _carried.AddChild(UiStyle.Label($"#{position}", UiStyle.Muted, 12));
            _carried.AddChild(ItemButton(ItemView.Name(pack, item), UiStyle.Text, (false, position)));
        }
        ShowDetails(inventory);
    }

    private Button ItemButton(string text, Color color, (bool Worn, int Key) key)
    {
        var button = UiStyle.NameButton(text, color);
        button.ToggleMode = true;
        button.ButtonPressed = _selected == key;
        button.Pressed += () =>
        {
            _selected = key;
            Refresh();
        };
        return button;
    }

    private void ShowDetails(ClientInventory inventory)
    {
        UiStyle.Clear(_details);
        var pack = ContentPacks.Embedded;
        OwnedItem? item = _selected switch
        {
            (true, var slot) => inventory.Worn.Where(w => (int)w.Slot == slot).Select(w => (OwnedItem?)w.Item).FirstOrDefault(),
            (false, var position) => inventory.At(position),
            _ => null,
        };
        if (item is not { } chosen || _session?.World.Build is not { } build)
        {
            _details.AddChild(UiStyle.Label("Select an item.", UiStyle.Muted));
            return;
        }
        var d = ItemView.Describe(pack, chosen, _session.World.EffectiveStats, build.Profession.Id);
        var title = UiStyle.Label(d.Title, UiStyle.Accent, 15);
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _details.AddChild(title);
        _details.AddChild(UiStyle.Label(d.Kind, UiStyle.Muted, 12));
        if (d.Weapon is { } weapon)
        {
            var w = UiStyle.Label(weapon, UiStyle.Text, 12);
            w.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _details.AddChild(w);
        }
        if (d.Requirements.Count > 0)
        {
            _details.AddChild(UiStyle.Label("Requires", UiStyle.Muted, 12));
            foreach (var r in d.Requirements)
            {
                _details.AddChild(UiStyle.Label((r.Met ? "✓ " : "✗ ") + r.Text, r.Met ? UiStyle.Good : UiStyle.Bad, 12));
            }
        }
        if (d.Effects.Count > 0)
        {
            _details.AddChild(UiStyle.Label("Gives", UiStyle.Muted, 12));
            foreach (var e in d.Effects)
            {
                var label = UiStyle.Label(e, UiStyle.Text, 12);
                label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _details.AddChild(label);
            }
        }
        _details.AddChild(new HSeparator());
        var actions = new HBoxContainer();
        var (worn, key) = _selected!.Value;
        if (worn)
        {
            actions.AddChild(Action("Unequip", () => _session.Unequip((EquipSlot)key)));
        }
        else
        {
            foreach (var action in ItemView.ActionsFor(pack, chosen))
            {
                actions.AddChild(action switch
                {
                    ItemAction.Equip => Action("Equip", () => _session.Equip(key)),
                    ItemAction.Upload => Action("Upload", () => _session.Upload(key)),
                    _ => Action("Discard", () =>
                    {
                        _session.Discard(key);
                        _selected = null;
                    }),
                });
            }
        }
        _details.AddChild(actions);
    }

    private static Button Action(string text, Action act)
    {
        var button = UiStyle.Button(text);
        button.Pressed += act;
        return button;
    }
}
