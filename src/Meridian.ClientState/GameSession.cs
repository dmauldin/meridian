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
    public static async Task<GameSession> StartAsync(HttpClient http, string username, string password, CancellationToken cancellationToken = default)
    {
        var login = await new AuthClient(http).LoginAsync(username, password, cancellationToken).ConfigureAwait(false);
        var world = new ClientWorld();
        var connection = new GameConnection(world);
        connection.Connect(login.GatewayHost, login.GatewayPort, login);
        return new GameSession(connection, world, username);
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

    /// <summary>Sends a chat line, trimmed and cut to the schema's 256-byte limit.</summary>
    public void Say(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = TruncateUtf8(text.Trim(), 256);
        if (text.Length > 0)
        {
            Connection.Send(new ChatSay { Text = text });
        }
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
