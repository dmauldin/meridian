using Godot;
using Meridian.ClientState;
using Meridian.ClientState.Views;
using Meridian.Content;

namespace Meridian.Client.Ui;

/// <summary>
/// The known Protocols as buttons (keys 1-9), bottom centre. Each casts on your player target,
/// or yourself; greyed when you lack the energy.
/// </summary>
public partial class ProtocolBar : PanelContainer
{
    private readonly HBoxContainer _buttons = new();
    private readonly List<ProtocolButton> _shown = [];
    private GameSession? _session;
    private string _signature = "";

    public ProtocolBar()
    {
        MouseFilter = MouseFilterEnum.Stop;
        GrowHorizontal = GrowDirection.Both;  // stay centred
        GrowVertical = GrowDirection.Begin;   // and grow upwards from the bottom edge
        AddThemeStyleboxOverride("panel", UiStyle.Panel(0.75f));
        _buttons.AddThemeConstantOverride("separation", 6);
        AddChild(_buttons);
    }

    public void Bind(GameSession session) => _session = session;

    /// <summary>Casts the Protocol on hotkey <paramref name="number"/> (1-9), if there is one.</summary>
    public void CastHotkey(int number)
    {
        if (_shown.FirstOrDefault(b => b.Hotkey == number) is { } button)
        {
            _session?.Cast(button.ProtocolId);
        }
    }

    /// <summary>Rebuilds only when the set or affordability changes (energy ticks often).</summary>
    public void Refresh()
    {
        if (_session is null)
        {
            return;
        }
        var buttons = ProtocolBarView.Buttons(ContentPacks.Embedded, _session.World.Protocols, _session.World.Status.Energy);
        var signature = string.Join(',', buttons.Select(b => $"{b.ProtocolId}:{b.Affordable}"));
        if (signature == _signature)
        {
            return;
        }
        _signature = signature;
        _shown.Clear();
        _shown.AddRange(buttons);
        UiStyle.Clear(_buttons);
        if (buttons.Count == 0)
        {
            _buttons.AddChild(UiStyle.Label("No Protocols known", UiStyle.Muted, 12));
            return;
        }
        foreach (var b in buttons)
        {
            var button = UiStyle.Button($"{(b.Hotkey > 0 ? $"{b.Hotkey}  " : "")}{b.Name}\n{b.EnergyCost} energy{(b.SelfOnly ? ", self" : "")}", b.Tooltip);
            button.CustomMinimumSize = new Vector2(150, 46);
            button.Modulate = b.Affordable ? Colors.White : new Color(1, 1, 1, 0.45f);
            var id = b.ProtocolId;
            button.Pressed += () => _session.Cast(id);
            _buttons.AddChild(button);
        }
    }
}
