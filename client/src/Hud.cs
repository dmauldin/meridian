using Godot;

namespace Meridian.Client;

/// <summary>
/// Screen overlay: own health, target frame, chat, a centre banner, controls help and a small
/// network readout. Everything is set from <see cref="GameView"/>; the HUD holds no game state.
/// </summary>
public partial class Hud : CanvasLayer
{
    private readonly Label _selfName = new();
    private readonly ProgressBar _selfHealth = Bar(new Color(0.3f, 0.85f, 0.45f));
    private readonly Label _selfHealthText = new();
    private readonly Label _selfProgress = new() { Modulate = new Color(0.75f, 0.85f, 1f) };
    private readonly ProgressBar _selfXp = Bar(new Color(0.55f, 0.45f, 0.95f));
    private readonly ProgressBar _selfEnergy = Bar(new Color(0.35f, 0.6f, 1f));
    private readonly Label _selfEnergyText = new() { Modulate = new Color(0.7f, 0.82f, 1f) };
    private readonly HFlowContainer _buffs = new();
    private string _buffSignature = "";
    private readonly PanelContainer _targetPanel = new() { Visible = false };
    private readonly Label _targetName = new();
    private readonly ProgressBar _targetHealth = Bar(new Color(0.9f, 0.35f, 0.3f));
    private readonly Label _attackState = new();
    private readonly RichTextLabel _chatLog = new() { BbcodeEnabled = true, ScrollFollowing = true, FitContent = false };
    private readonly Label _banner = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Label _netInfo = new();
    private readonly Label _prompt = new() { HorizontalAlignment = HorizontalAlignment.Center };

    public LineEdit ChatInput { get; } = new() { PlaceholderText = "Press Enter to chat", MaxLength = 200 };

    public override void _Ready()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // Own frame, top-left.
        var self = new PanelContainer { CustomMinimumSize = new Vector2(300, 0) };
        root.AddChild(self);
        self.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft, Control.LayoutPresetMode.Minsize, 16);
        var selfBox = new VBoxContainer();
        self.AddChild(selfBox);
        selfBox.AddChild(_selfName);
        selfBox.AddChild(_selfHealth);
        selfBox.AddChild(_selfHealthText);
        _selfEnergy.CustomMinimumSize = new Vector2(0, 10);
        selfBox.AddChild(_selfEnergy);
        selfBox.AddChild(_selfEnergyText);
        selfBox.AddChild(_selfProgress);
        _selfXp.CustomMinimumSize = new Vector2(0, 6);
        selfBox.AddChild(_selfXp);
        _buffs.AddThemeConstantOverride("h_separation", 4);
        selfBox.AddChild(_buffs);

        // Target frame, top-centre.
        _targetPanel.CustomMinimumSize = new Vector2(300, 0);
        root.AddChild(_targetPanel);
        _targetPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.Minsize, 16);
        var targetBox = new VBoxContainer();
        _targetPanel.AddChild(targetBox);
        targetBox.AddChild(_targetName);
        targetBox.AddChild(_targetHealth);
        targetBox.AddChild(_attackState);

        // Chat, bottom-left.
        var chat = new VBoxContainer { CustomMinimumSize = new Vector2(480, 240) };
        _chatLog.CustomMinimumSize = new Vector2(480, 200);
        _chatLog.AddThemeColorOverride("default_color", new Color(0.92f, 0.92f, 0.95f));
        chat.AddChild(_chatLog);
        chat.AddChild(ChatInput);
        root.AddChild(chat);
        chat.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 16);

        // Banner, centre.
        _banner.CustomMinimumSize = new Vector2(700, 60);
        _banner.AddThemeFontSizeOverride("font_size", 30);
        root.AddChild(_banner);
        _banner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);

        // Interaction prompt, above the Protocol bar.
        _prompt.AddThemeFontSizeOverride("font_size", 18);
        _prompt.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.5f));
        _prompt.AddThemeConstantOverride("outline_size", 6);
        _prompt.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        root.AddChild(_prompt);
        // A centred band 600 wide, clear of the Protocol bar below it.
        _prompt.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        (_prompt.OffsetLeft, _prompt.OffsetRight, _prompt.OffsetTop, _prompt.OffsetBottom) = (-300, 300, -150, -120);

        // Help, bottom-right; net readout, top-right.
        var help = new Label { Text = Controls.HelpText, Modulate = new Color(1, 1, 1, 0.55f), HorizontalAlignment = HorizontalAlignment.Right };
        root.AddChild(help);
        help.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize, 16);
        _netInfo.Modulate = new Color(1, 1, 1, 0.55f);
        _netInfo.HorizontalAlignment = HorizontalAlignment.Right;
        _netInfo.CustomMinimumSize = new Vector2(220, 0);
        root.AddChild(_netInfo);
        _netInfo.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 16);
    }

    public void SetSelf(string name, int health, int maxHealth)
    {
        _selfName.Text = name;
        _selfHealth.MaxValue = Math.Max(1, maxHealth);
        _selfHealth.Value = Math.Max(0, health);
        _selfHealthText.Text = $"{Math.Max(0, health)} / {maxHealth}";
    }

    public void SetEnergy(int energy, int maxEnergy)
    {
        _selfEnergy.MaxValue = Math.Max(1, maxEnergy);
        _selfEnergy.Value = Math.Max(0, energy);
        _selfEnergyText.Text = $"Energy {Math.Max(0, energy)} / {maxEnergy}";
    }

    /// <summary>Running Protocols as chips with a countdown; right-click one to drop it.</summary>
    public void SetBuffs(IReadOnlyList<Meridian.ClientState.Views.BuffChip> buffs, Action<int> cancel)
    {
        ArgumentNullException.ThrowIfNull(buffs);
        var signature = string.Join(',', buffs.Select(b => $"{b.ProtocolId}:{b.RemainingMs / 1000}"));
        if (signature == _buffSignature)
        {
            return;
        }
        _buffSignature = signature;
        foreach (var child in _buffs.GetChildren())
        {
            _buffs.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var b in buffs)
        {
            var chip = new Button
            {
                Text = $"{b.Name} {b.Countdown}",
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = "Right-click to drop",
                ButtonMask = MouseButtonMask.Right,
            };
            chip.AddThemeFontSizeOverride("font_size", 12);
            chip.AddThemeColorOverride("font_color", b.RemainingMs < 30_000 ? new Color(1f, 0.75f, 0.4f) : new Color(0.75f, 0.9f, 1f));
            var id = b.ProtocolId;
            chip.Pressed += () => cancel(id);
            _buffs.AddChild(chip);
        }
    }

    /// <summary>Level, profession, XP progress and unspent skill points under the health bar.</summary>
    public void SetProgress(int level, string profession, long xp, long xpToNext, int unspentPoints)
    {
        var points = unspentPoints > 0 ? $"   {unspentPoints} skill points (C)" : "";
        _selfProgress.Text = $"Level {level} {profession}{points}";
        _selfXp.MaxValue = Math.Max(1, xpToNext);
        _selfXp.Value = xpToNext == 0 ? _selfXp.MaxValue : xp;
    }

    public void SetTarget(string? name, int healthPercent, bool attacking, bool dead)
    {
        _targetPanel.Visible = name is not null;
        if (name is null)
        {
            return;
        }
        _targetName.Text = dead ? $"{name} (dead)" : name;
        _targetHealth.Value = healthPercent;
        _attackState.Text = attacking ? "Auto-attack: ON" : "Auto-attack: off  (Q)";
        _attackState.Modulate = attacking ? new Color(1f, 0.6f, 0.4f) : new Color(1, 1, 1, 0.6f);
    }

    public void AddChat(string bbcode) => _chatLog.AppendText(bbcode + "\n");

    public void SetBanner(string text) => _banner.Text = text;

    /// <summary>A short "press F to..." line above the Protocol bar; empty hides it.</summary>
    public void SetPrompt(string text) => _prompt.Text = text;

    public void SetNetInfo(string text) => _netInfo.Text = text;

    private static ProgressBar Bar(Color fill)
    {
        var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 18) };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = fill });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.1f, 0.1f, 0.12f, 0.8f) });
        return bar;
    }
}
