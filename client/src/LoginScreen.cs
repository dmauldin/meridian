using Godot;

namespace Meridian.Client;

/// <summary>Server address, username and password. Built in code; styling comes with real UI art.</summary>
public partial class LoginScreen : CanvasLayer
{
    private readonly LineEdit _auth = new() { Text = "http://127.0.0.1:5080/", PlaceholderText = "Auth server URL" };
    private readonly LineEdit _user = new() { PlaceholderText = "Username", MaxLength = 24 };
    private readonly LineEdit _pass = new() { PlaceholderText = "Password", Secret = true, Text = "dev" };
    private readonly Button _go = new() { Text = "Log in" };
    private readonly Label _status = new() { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };

    public event Action<string, string, string>? LoginRequested;

    public override void _Ready()
    {
        var backdrop = new ColorRect { Color = new Color(0.04f, 0.05f, 0.08f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(backdrop);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
        box.AddThemeConstantOverride("separation", 10);
        center.AddChild(box);

        var title = new Label { Text = "MERIDIAN", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 40);
        box.AddChild(title);
        box.AddChild(new Label { Text = "Development build. Any username works with the dev password.", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.6f) });
        box.AddChild(_auth);
        box.AddChild(_user);
        box.AddChild(_pass);
        box.AddChild(_go);
        box.AddChild(_status);

        _user.Text = $"Pilot{GD.Randi() % 1000:D3}";
        _go.Pressed += Submit;
        _user.TextSubmitted += _ => Submit();
        _pass.TextSubmitted += _ => Submit();
        _user.GrabFocus();
    }

    public void ShowStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.Modulate = isError ? new Color(1f, 0.45f, 0.4f) : new Color(0.8f, 0.85f, 1f);
        _go.Disabled = !isError && message.EndsWith("...", StringComparison.Ordinal);
    }

    private void Submit()
    {
        if (_go.Disabled)
        {
            return;
        }
        LoginRequested?.Invoke(_auth.Text.Trim(), _user.Text.Trim(), _pass.Text);
    }
}
