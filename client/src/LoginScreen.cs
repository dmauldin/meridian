using Godot;
using Meridian.Net.Client;

namespace Meridian.Client;

/// <summary>
/// Account login or registration, then character select and create. Built in code; styling
/// comes with real UI art. Talks to the auth service itself and raises <see cref="PlayReady"/>
/// with a gateway ticket for the chosen character.
/// </summary>
public partial class LoginScreen : CanvasLayer
{
    // Account stage
    private readonly LineEdit _auth = new() { Text = "http://127.0.0.1:5080/", PlaceholderText = "Auth server URL" };
    private readonly LineEdit _user = new() { PlaceholderText = "Username", MaxLength = 24 };
    private readonly LineEdit _pass = new() { PlaceholderText = "Password (8+ characters)", Secret = true };
    private readonly Button _login = new() { Text = "Log in" };
    private readonly Button _register = new() { Text = "Create account" };

    // Character stage
    private readonly ItemList _characters = new() { CustomMinimumSize = new Vector2(0, 160) };
    private readonly Button _play = new() { Text = "Play", Disabled = true };
    private readonly LineEdit _newName = new() { PlaceholderText = "New character name", MaxLength = 24 };
    private readonly OptionButton _profession = new();
    private readonly OptionButton _origin = new();
    private readonly Button _create = new() { Text = "Create character" };
    private readonly Button _back = new() { Text = "Log out" };

    private readonly Label _status = new() { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly VBoxContainer _accountBox = new();
    private readonly VBoxContainer _characterBox = new();
    private readonly Meridian.Core.Content.ContentPack _pack = Meridian.Content.ContentPacks.Embedded;

    private System.Net.Http.HttpClient? _http;
    private AccountSession? _session;
    private bool _busy;

    /// <summary>A ticket for the chosen character, and its name.</summary>
    public event Action<PlayResponse, string>? PlayReady;

    public override void _Ready()
    {
        var backdrop = new ColorRect { Color = new Color(0.04f, 0.05f, 0.08f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(backdrop);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0) };
        box.AddThemeConstantOverride("separation", 10);
        center.AddChild(box);

        var title = new Label { Text = "MERIDIAN", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 40);
        box.AddChild(title);

        _accountBox.AddThemeConstantOverride("separation", 10);
        _accountBox.AddChild(_auth);
        _accountBox.AddChild(_user);
        _accountBox.AddChild(_pass);
        var accountButtons = new HBoxContainer();
        accountButtons.AddChild(_login);
        accountButtons.AddChild(_register);
        _accountBox.AddChild(accountButtons);
        box.AddChild(_accountBox);

        _characterBox.AddThemeConstantOverride("separation", 10);
        _characterBox.AddChild(new Label { Text = "Your characters" });
        _characterBox.AddChild(_characters);
        _characterBox.AddChild(_play);
        _characterBox.AddChild(new HSeparator());
        _characterBox.AddChild(_newName);
        foreach (var p in _pack.Professions.All)
        {
            _profession.AddItem(p.Name);
        }
        foreach (var o in _pack.Origins.All)
        {
            _origin.AddItem(o.Name);
        }
        var picks = new HBoxContainer();
        picks.AddChild(new Label { Text = "Profession" });
        picks.AddChild(_profession);
        picks.AddChild(new Label { Text = "  Origin" });
        picks.AddChild(_origin);
        _characterBox.AddChild(picks);
        _characterBox.AddChild(_create);
        _characterBox.AddChild(_back);
        _characterBox.Visible = false;
        box.AddChild(_characterBox);

        box.AddChild(_status);

        _login.Pressed += () => Run(LogInAsync);
        _register.Pressed += () => Run(RegisterAsync);
        _pass.TextSubmitted += _ => Run(LogInAsync);
        _create.Pressed += () => Run(CreateAsync);
        _newName.TextSubmitted += _ => Run(CreateAsync);
        _play.Pressed += () => Run(PlayAsync);
        _characters.ItemActivated += _ => Run(PlayAsync);
        _characters.ItemSelected += _ => _play.Disabled = false;
        _back.Pressed += ShowAccount;
        _user.GrabFocus();
    }

    public override void _ExitTree() => _http?.Dispose();

    public void ShowStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.Modulate = isError ? new Color(1f, 0.45f, 0.4f) : new Color(0.8f, 0.85f, 1f);
    }

    private AuthClient Auth()
    {
        var url = _auth.Text.Trim();
        _http?.Dispose();
        _http = new System.Net.Http.HttpClient { BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/"), Timeout = TimeSpan.FromSeconds(10) };
        return new AuthClient(_http);
    }

    private Credentials Credentials() => new(_user.Text.Trim(), _pass.Text);

    private async Task RegisterAsync()
    {
        ShowStatus("Creating account...", isError: false);
        await Auth().RegisterAsync(Credentials());
        await LogInAsync();
    }

    private async Task LogInAsync()
    {
        ShowStatus("Logging in...", isError: false);
        _session = await Auth().LoginAsync(Credentials());
        ShowCharacters();
        ShowStatus(_session.Characters.Count == 0 ? "Create your first character." : "", isError: false);
    }

    private async Task CreateAsync()
    {
        if (_session is null)
        {
            return;
        }
        var created = await _session.CreateCharacterAsync(new CreateCharacterRequest(
            _newName.Text.Trim(),
            _profession.GetItemText(_profession.Selected),
            _origin.GetItemText(_origin.Selected)));
        _newName.Text = "";
        ShowCharacters();
        _characters.Select(_session.Characters.Count - 1);
        _play.Disabled = false;
        ShowStatus($"Created {created.Name}.", isError: false);
    }

    private async Task PlayAsync()
    {
        if (_session is null || _characters.GetSelectedItems() is not [var index] || index >= _session.Characters.Count)
        {
            return;
        }
        var character = _session.Characters[index];
        ShowStatus($"Entering the world as {character.Name}...", isError: false);
        var play = await _session.PlayAsync(character.Id);
        PlayReady?.Invoke(play, character.Name);
    }

    private void ShowCharacters()
    {
        _accountBox.Visible = false;
        _characterBox.Visible = true;
        _characters.Clear();
        foreach (var c in _session!.Characters)
        {
            var profession = _pack.Professions.TryGet(c.Profession, out var p) ? p.Name : c.Profession;
            var origin = _pack.Origins.TryGet(c.Origin, out var o) ? o.Name : c.Origin;
            _characters.AddItem($"{c.Name}    level {c.Level} {origin} {profession}");
        }
        _play.Disabled = _session.Characters.Count == 0;
        if (_session.Characters.Count > 0)
        {
            _characters.Select(0);
        }
        _newName.GrabFocus();
    }

    private void ShowAccount()
    {
        _session = null;
        _characterBox.Visible = false;
        _accountBox.Visible = true;
        ShowStatus("", isError: false);
        _pass.GrabFocus();
    }

    /// <summary>Runs one auth step at a time and turns failures into a readable status line.</summary>
    private async void Run(Func<Task> step)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        try
        {
            await step(); // Godot's synchronization context resumes on the main thread
        }
        catch (AuthException e)
        {
            ShowStatus(e.Message, isError: true);
            if (e.Code == AuthErrorCodes.NotLoggedIn)
            {
                ShowAccount();
            }
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or UriFormatException)
        {
            ShowStatus($"Cannot reach the auth server: {e.Message}", isError: true);
        }
        finally
        {
            _busy = false;
        }
    }
}
