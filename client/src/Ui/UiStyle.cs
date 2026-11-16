using Godot;

namespace Meridian.Client.Ui;

/// <summary>Shared look for in-game windows until real UI art exists: dark glass panels, one accent.</summary>
public static class UiStyle
{
    public static readonly Color Text = new(0.92f, 0.93f, 0.96f);
    public static readonly Color Muted = new(0.62f, 0.66f, 0.74f);
    public static readonly Color Accent = new(0.45f, 0.75f, 1f);
    public static readonly Color Good = new(0.45f, 0.9f, 0.55f);
    public static readonly Color Bad = new(1f, 0.45f, 0.4f);
    public static readonly Color Energy = new(0.35f, 0.6f, 1f);

    public static StyleBoxFlat Panel(float alpha = 0.96f) => new()
    {
        BgColor = new Color(0.06f, 0.07f, 0.1f, alpha),
        BorderColor = new Color(0.45f, 0.75f, 1f, 0.35f),
        BorderWidthLeft = 1,
        BorderWidthTop = 1,
        BorderWidthRight = 1,
        BorderWidthBottom = 1,
        CornerRadiusTopLeft = 6,
        CornerRadiusTopRight = 6,
        CornerRadiusBottomLeft = 6,
        CornerRadiusBottomRight = 6,
        ContentMarginLeft = 10,
        ContentMarginRight = 10,
        ContentMarginTop = 8,
        ContentMarginBottom = 8,
    };

    public static Label Label(string text = "", Color? color = null, int size = 0, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label { Text = text, HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Pass };
        label.AddThemeColorOverride("font_color", color ?? Text);
        if (size > 0)
        {
            label.AddThemeFontSizeOverride("font_size", size);
        }
        return label;
    }

    /// <summary>A small button that never takes keyboard focus (so WASD and hotkeys keep working).</summary>
    public static Button Button(string text, string? tooltip = null)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, TooltipText = tooltip ?? "" };
        return button;
    }

    /// <summary>A left-aligned button for a long name that trims with an ellipsis instead of growing.</summary>
    public static Button NameButton(string text, Color color)
    {
        var button = Button(text, text);
        button.Alignment = HorizontalAlignment.Left;
        button.ClipText = true;
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.CustomMinimumSize = new Vector2(60, 0);
        button.AddThemeColorOverride("font_color", color);
        return button;
    }

    public static ProgressBar Bar(Color fill, int height = 18)
    {
        var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, height) };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = fill });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.1f, 0.1f, 0.12f, 0.8f) });
        return bar;
    }

    public static void Clear(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }
}

/// <summary>A titled panel with a close button; content goes in <see cref="Body"/>. Toggled by a key.</summary>
public partial class GameWindow : PanelContainer
{
    public GameWindow(string title, string hotkey, Vector2 size)
    {
        PreferredSize = size;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop; // clicks on a window never reach the world
        AddThemeStyleboxOverride("panel", UiStyle.Panel());
        Visible = false;
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        AddChild(column);
        var header = new HBoxContainer();
        header.AddChild(UiStyle.Label(title, UiStyle.Accent, 18));
        header.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        header.AddChild(UiStyle.Label($"[{hotkey}]", UiStyle.Muted));
        var close = UiStyle.Button("×", "Close");
        close.Pressed += () => Visible = false;
        header.AddChild(close);
        column.AddChild(header);
        column.AddChild(new HSeparator());
        Body = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        Body.AddThemeConstantOverride("separation", 4);
        column.AddChild(Body);
    }

    public VBoxContainer Body { get; }

    /// <summary>The size the window wants; <see cref="Place"/> shrinks it to fit the screen.</summary>
    public Vector2 PreferredSize { get; }

    public void Toggle() => Visible = !Visible;

    /// <summary>Puts the window at a fixed spot and size (its content scrolls inside).</summary>
    public void Place(Vector2 position, Vector2 maxSize)
    {
        SetAnchorsPreset(LayoutPreset.TopLeft);
        Position = position;
        Size = new Vector2(Math.Min(PreferredSize.X, maxSize.X), Math.Min(PreferredSize.Y, maxSize.Y));
    }
}
