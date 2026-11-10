using Godot;
using Meridian.ClientState;

namespace Meridian.Client;

/// <summary>
/// Root of the client: shows the login screen, starts a <see cref="GameSession"/>, then swaps in
/// the <see cref="GameView"/>. With <c>-- --autopilot</c> it skips the UI and plays by itself,
/// which is how the headless end-to-end check runs.
/// </summary>
public partial class Main : Node
{
    private LoginScreen? _login;
    private GameView? _game;
    private GameSession? _pendingSession;
    private System.Net.Http.HttpClient? _http;
    private AutoPilotOptions? _autopilot;

    public override void _Ready()
    {
        Controls.Register();
        _autopilot = AutoPilotOptions.Parse(OS.GetCmdlineUserArgs());
        if (_autopilot is not null)
        {
            GD.Print($"autopilot: logging in as {_autopilot.Username} via {_autopilot.AuthUrl}");
            StartLogin(_autopilot.AuthUrl, _autopilot.Username, _autopilot.Password);
            return;
        }
        ShowLogin(null);
    }

    public override void _Process(double delta)
    {
        // Between login and Welcome: keep polling so the connection completes, then enter the world.
        if (_pendingSession is not { } session)
        {
            return;
        }
        session.Update(NowMs(), 0, 0);
        if (session.World.InWorld)
        {
            _pendingSession = null;
            EnterWorld(session);
        }
        else if (session.DisconnectReason is { } reason)
        {
            _pendingSession = null;
            session.Dispose();
            Fail($"Could not join: {reason}");
        }
    }

    public override void _ExitTree()
    {
        _pendingSession?.Dispose();
        _http?.Dispose();
    }

    /// <summary>Monotonic milliseconds for the client model.</summary>
    public static double NowMs() => Time.GetTicksUsec() / 1000.0;

    private void ShowLogin(string? message)
    {
        _game?.QueueFree();
        _game = null;
        _login = new LoginScreen();
        _login.LoginRequested += StartLogin;
        AddChild(_login);
        if (message is not null)
        {
            _login.ShowStatus(message, isError: true);
        }
    }

    private async void StartLogin(string authUrl, string username, string password)
    {
        _login?.ShowStatus("Logging in...", isError: false);
        try
        {
            _http?.Dispose();
            _http = new System.Net.Http.HttpClient { BaseAddress = new Uri(authUrl.EndsWith('/') ? authUrl : authUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
            // Godot's synchronization context resumes this on the main thread.
            _pendingSession = await GameSession.StartAsync(_http, username, password);
            _login?.ShowStatus("Connecting to the zone...", isError: false);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Fail($"Login failed: {e.Message}");
        }
    }

    private void EnterWorld(GameSession session)
    {
        _login?.QueueFree();
        _login = null;
        _game = new GameView(session);
        _game.SessionEnded += reason => CallDeferred(MethodName.OnSessionEnded, reason);
        AddChild(_game);
        if (_autopilot is not null)
        {
            _game.AddChild(new AutoPilot(_game, _autopilot));
        }
    }

    private void OnSessionEnded(string reason)
    {
        if (_autopilot is not null)
        {
            GD.PrintErr($"autopilot: session ended: {reason}");
            GetTree().Quit(1);
            return;
        }
        ShowLogin(reason);
    }

    private void Fail(string message)
    {
        if (_autopilot is not null)
        {
            GD.PrintErr($"autopilot: {message}");
            GetTree().Quit(1);
            return;
        }
        if (_login is null)
        {
            ShowLogin(message);
        }
        else
        {
            _login.ShowStatus(message, isError: true);
        }
    }
}
