using Meridian.Net.Client;
using Meridian.Wire;

namespace Meridian.ClientState;

/// <summary>
/// One logged-in play session: the connection, the world model, prediction and input pacing.
/// The engine calls <see cref="Update"/> once per frame from its main thread.
/// </summary>
public sealed class GameSession : IDisposable
{
    private readonly InputPacer _pacer = new();
    private double _lastUpdateMs = double.NaN;

    private GameSession(GameConnection connection, ClientWorld world, string characterName)
    {
        Connection = connection;
        World = world;
        CharacterName = characterName;
    }

    /// <summary>The logged-in character's name (M1: the username).</summary>
    public string CharacterName { get; }

    public GameConnection Connection { get; }

    public ClientWorld World { get; }

    public bool Connected => Connection.Connected;

    public string? DisconnectReason => Connection.DisconnectReason;

    /// <summary>Logs in over HTTP and starts connecting to the gateway it names.</summary>
    /// <exception cref="HttpRequestException">Login was refused or the auth service is unreachable.</exception>
    public static Task<GameSession> StartAsync(HttpClient http, string username, string password, CancellationToken cancellationToken = default) =>
        StartAsync(http, new LoginRequest(username, password), cancellationToken);

    /// <summary>Logs in with a full request (including the development profession/origin choice).</summary>
    public static async Task<GameSession> StartAsync(HttpClient http, LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var login = await new AuthClient(http).LoginAsync(request, cancellationToken).ConfigureAwait(false);
        var world = new ClientWorld();
        var connection = new GameConnection(world);
        connection.Connect(login.GatewayHost, login.GatewayPort, login);
        return new GameSession(connection, world, request.Username);
    }

    /// <summary>
    /// Processes network messages, advances local prediction and sends movement input.
    /// <paramref name="dirX"/>/<paramref name="dirZ"/> are the desired direction scaled to ±32767.
    /// </summary>
    public void Update(double nowMs, short dirX, short dirZ)
    {
        var elapsed = double.IsNaN(_lastUpdateMs) ? 0 : Math.Clamp(nowMs - _lastUpdateMs, 0, 250);
        _lastUpdateMs = nowMs;

        World.NowMs = nowMs;
        World.RoundTripMs = Connection.RoundTripTimeMs;
        Connection.Poll();

        if (!World.InWorld)
        {
            return;
        }
        if (World.Status.Dead)
        {
            dirX = dirZ = 0;
        }
        World.Self.Advance(nowMs, elapsed, dirX, dirZ, World.Status.RunSpeed);
        if (_pacer.Update(nowMs, dirX, dirZ) is { } intent)
        {
            Connection.Send(intent);
        }
    }

    public void SetTarget(uint entityId) => Connection.Send(new SetTarget { EntityId = entityId });

    public void SetAttack(bool enabled) => Connection.Send(new SetAttack { Enabled = enabled });

    /// <summary>Raises <paramref name="stat"/> by <paramref name="points"/> (the server checks cost and cap).</summary>
    public void Train(Meridian.Core.Stats.Stat stat, int points) =>
        Connection.Send(new TrainSkill { Stat = (ushort)stat, Points = (ushort)Math.Clamp(points, 0, ushort.MaxValue) });

    /// <summary>
    /// Sends a chat line, trimmed and cut to the schema's 256-byte limit. Lines starting with
    /// <c>/</c> are commands (see <see cref="TryCommand"/>) and are not sent as chat.
    /// </summary>
    public void Say(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (TryCommand(text.Trim(), out var feedback))
        {
            if (feedback is not null)
            {
                World.AddLocalNotice(feedback);
            }
            return;
        }
        text = TruncateUtf8(text.Trim(), 256);
        if (text.Length > 0)
        {
            Connection.Send(new ChatSay { Text = text });
        }
    }

    /// <summary>
    /// Chat commands until the real UI exists (M2.5): <c>/train Rifle 10</c>. Returns false if
    /// the line is not a command. <paramref name="feedback"/> is a message for the player, if any.
    /// </summary>
    public bool TryCommand(string line, out string? feedback)
    {
        ArgumentNullException.ThrowIfNull(line);
        feedback = null;
        if (!line.StartsWith('/'))
        {
            return false;
        }
        if (TryParseTrain(line, out var stat, out var points))
        {
            if (World.Build is { } build)
            {
                feedback = $"Training {stat} by {points} ({points * build.CostPerPoint(stat)} points; cap {build.Cap(stat)}).";
            }
            Train(stat, points);
            return true;
        }
        feedback = "Commands: /train <Stat> <points>   e.g. /train Rifle 10";
        return true;
    }

    /// <summary>Parses <c>/train &lt;Stat&gt; &lt;points&gt;</c>; stat names are case-insensitive, numbers are not stats.</summary>
    public static bool TryParseTrain(string line, out Meridian.Core.Stats.Stat stat, out int points)
    {
        ArgumentNullException.ThrowIfNull(line);
        stat = default;
        points = 0;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["/train", var name, var amount]
            && !char.IsDigit(name[0])
            && Enum.TryParse(name, ignoreCase: true, out stat)
            && Enum.IsDefined(stat)
            && int.TryParse(amount, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out points)
            && points > 0;
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes on a character boundary.</summary>
    public static string TruncateUtf8(string text, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }
        var bytes = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var end = 0;
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            bytes += System.Text.Encoding.UTF8.GetByteCount(element);
            if (bytes > maxBytes)
            {
                break;
            }
            end = e.ElementIndex + element.Length;
        }
        return text[..end];
    }

    public void Dispose()
    {
        Connection.Disconnect();
        Connection.Dispose();
    }
}
