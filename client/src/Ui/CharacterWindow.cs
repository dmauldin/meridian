using Godot;
using Meridian.ClientState;
using Meridian.ClientState.Views;
using Meridian.Core.Characters;
using Meridian.Core.Content;

namespace Meridian.Client.Ui;

/// <summary>
/// Level, experience and skill points, and every stat by group with its trained and effective
/// value, cost per point and cap. +1 and +5 train (capped by what is affordable); the server
/// decides and the sheet updates when it answers.
/// </summary>
public partial class CharacterWindow : GameWindow
{
    private readonly Label _summary = UiStyle.Label();
    private readonly ProgressBar _xp = UiStyle.Bar(new Color(0.55f, 0.45f, 0.95f), 8);
    private readonly VBoxContainer _rows = new();
    private readonly Dictionary<(Meridian.Core.Stats.Stat, int), Button> _trainButtons = [];
    private GameSession? _session;

    public CharacterWindow()
        : base("Character", "C", new Vector2(470, 640))
    {
        Body.AddChild(_summary);
        Body.AddChild(_xp);
        Body.AddChild(UiStyle.Label("Base is what you trained; Now includes items and buffs.", UiStyle.Muted, 12));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_rows);
        _rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Body.AddChild(scroll);
    }

    public void Bind(GameSession session) => _session = session;

    /// <summary>Presses a real train button, as a click would (the autopilot's UI check). False if absent or disabled.</summary>
    public bool Click(Meridian.Core.Stats.Stat stat, int step)
    {
        if (!_trainButtons.TryGetValue((stat, step), out var button) || button.Disabled)
        {
            return false;
        }
        button.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    /// <summary>Rebuilds from the latest sheet. Cheap enough to call on every CharacterChanged.</summary>
    public void Refresh()
    {
        if (_session?.World.Build is not { } build)
        {
            _summary.Text = "Waiting for your character sheet...";
            return;
        }
        _summary.Text = $"{_session.CharacterName}   Level {build.Level} {build.Origin.Name} {build.Profession.Name}\n"
            + $"XP {build.Xp} / {build.XpToNext}     Skill points: {build.UnspentPoints}";
        _xp.MaxValue = Math.Max(1, build.XpToNext);
        _xp.Value = build.XpToNext == 0 ? _xp.MaxValue : build.Xp;

        UiStyle.Clear(_rows);
        _trainButtons.Clear();
        foreach (var group in CharacterSheetView.Build(build, _session.World.EffectiveStats))
        {
            _rows.AddChild(UiStyle.Label(group.Title, UiStyle.Accent, 15));
            var grid = new GridContainer { Columns = 7, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 10);
            foreach (var header in new[] { "", "Base", "Now", group.Rows[0].Trainable ? "Cost" : "", group.Rows[0].Trainable ? "Cap" : "", "", "" })
            {
                grid.AddChild(UiStyle.Label(header, UiStyle.Muted, 12));
            }
            foreach (var row in group.Rows)
            {
                AddRow(grid, build, row);
            }
            _rows.AddChild(grid);
        }
    }

    private void AddRow(GridContainer grid, CharacterBuild build, StatRow row)
    {
        var name = UiStyle.Label(row.Name);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(name);
        grid.AddChild(UiStyle.Label(row.Base.ToString(System.Globalization.CultureInfo.InvariantCulture), UiStyle.Muted, align: HorizontalAlignment.Right));
        var boosted = row.Effective != row.Base;
        grid.AddChild(UiStyle.Label(row.Effective.ToString(System.Globalization.CultureInfo.InvariantCulture),
            boosted ? (row.Effective > row.Base ? UiStyle.Good : UiStyle.Bad) : UiStyle.Text, align: HorizontalAlignment.Right));
        if (!row.Trainable)
        {
            for (var i = 0; i < 4; i++)
            {
                grid.AddChild(new Control());
            }
            return;
        }
        if (row.Tier == CostTier.Locked)
        {
            grid.AddChild(UiStyle.Label("locked", UiStyle.Muted, 12));
            for (var i = 0; i < 3; i++)
            {
                grid.AddChild(new Control());
            }
            return;
        }
        grid.AddChild(UiStyle.Label(row.CostPerPoint.ToString(System.Globalization.CultureInfo.InvariantCulture), TierColor(row.Tier), align: HorizontalAlignment.Right));
        grid.AddChild(UiStyle.Label(row.Cap.ToString(System.Globalization.CultureInfo.InvariantCulture), UiStyle.Muted, align: HorizontalAlignment.Right));
        foreach (var step in new[] { 1, 5 })
        {
            var points = CharacterSheetView.Affordable(build, row.Stat, step);
            var button = UiStyle.Button($"+{step}", $"Train {row.Name} by {points} ({points * row.CostPerPoint} points)");
            button.Disabled = points == 0;
            button.Pressed += () => _session!.Train(row.Stat, CharacterSheetView.Affordable(_session.World.Build!, row.Stat, step));
            _trainButtons[(row.Stat, step)] = button;
            grid.AddChild(button);
        }
    }

    private static Color TierColor(CostTier tier) => tier switch
    {
        CostTier.Cheap => UiStyle.Good,
        CostTier.Expensive => UiStyle.Bad,
        _ => UiStyle.Text,
    };
}
