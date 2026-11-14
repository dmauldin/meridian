using Meridian.Content;
using Meridian.Core.Items;
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

    /// <summary>Starts connecting to the gateway <paramref name="play"/> names, as the character it was issued for.</summary>
    public static GameSession Start(PlayResponse play, string characterName)
    {
        ArgumentNullException.ThrowIfNull(play);
        var world = new ClientWorld();
        var connection = new GameConnection(world);
        connection.Connect(play.GatewayHost, play.GatewayPort, play);
        return new GameSession(connection, world, characterName);
    }

    /// <summary>
    /// Development shortcut (headless autopilot, tests): registers if needed, logs in, creates the
    /// character if the account lacks it, and connects. See <see cref="AuthClient.QuickPlayAsync"/>.
    /// </summary>
    /// <exception cref="AuthException">The auth service refused a step.</exception>
    public static async Task<GameSession> QuickStartAsync(
        HttpClient http, Credentials credentials, string characterName, string profession, string origin, CancellationToken cancellationToken = default)
    {
        var play = await new AuthClient(http).QuickPlayAsync(credentials, characterName, profession, origin, cancellationToken).ConfigureAwait(false);
        return Start(play, characterName);
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
    /// Chat commands until the real UI exists (M2.5). Returns false if the line is not a local
    /// command: plain chat, or a slash command for the server (such as the development
    /// <c>/give</c>), which is sent as chat and never broadcast. <paramref name="feedback"/> is a
    /// message for the player, if any.
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
        switch (ParseItemCommand(line))
        {
            case (ItemCommand.List, _):
                feedback = DescribeInventory(World.Inventory);
                return true;
            case (ItemCommand.Equip, var position):
                Connection.Send(new EquipItem { Position = (byte)position });
                return true;
            case (ItemCommand.Unequip, var slot):
                Connection.Send(new UnequipItem { Slot = (byte)slot });
                return true;
            case (ItemCommand.Discard, var position):
                Connection.Send(new DiscardItem { Position = (byte)position });
                return true;
            case (ItemCommand.Help, _):
                feedback = "Commands: /train <Stat> <points>, /inv, /equip <#>, /unequip <slot>, /discard <#>";
                return true;
            default:
                return false; // the server's to answer
        }
    }

    public enum ItemCommand
    {
        None,
        Help,
        List,
        Equip,
        Unequip,
        Discard,
    }

    /// <summary>Parses the item commands. The argument is an inventory position, or a slot id for unequip (by name, any case).</summary>
    public static (ItemCommand Command, int Argument) ParseItemCommand(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts switch
        {
            ["/help"] or ["/?"] => (ItemCommand.Help, 0),
            ["/inv"] or ["/inventory"] => (ItemCommand.List, 0),
            ["/equip", var n] when Position(n) is { } p => (ItemCommand.Equip, p),
            ["/discard", var n] when Position(n) is { } p => (ItemCommand.Discard, p),
            ["/unequip", var name] when !char.IsDigit(name[0]) && Enum.TryParse<EquipSlot>(name, ignoreCase: true, out var slot) && Enum.IsDefined(slot)
                => (ItemCommand.Unequip, (int)slot),
            ["/train", ..] or ["/equip", ..] or ["/discard", ..] or ["/unequip", ..] => (ItemCommand.Help, 0),
            _ => (ItemCommand.None, 0),
        };

        static int? Position(string text) =>
            int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) && p <= byte.MaxValue ? p : null;
    }

    /// <summary>One line per item: "#3 Kestrel Mk2 QL 3" and worn items with their state.</summary>
    public static string DescribeInventory(ClientInventory? inventory)
    {
        if (inventory is null)
        {
            return "Inventory not received yet.";
        }
        var pack = ContentPacks.Embedded;
        string Name(OwnedItem i) => pack.Items.TryGet(i.TemplateId, out var t) ? $"{t.Name} QL {i.Ql}" : $"item {i.TemplateId}";
        var lines = new List<string> { $"Carrying {inventory.Carried.Count}/{inventory.Size}:" };
        lines.AddRange(inventory.Carried.Select(c => $"  #{c.Position} {Name(c.Item)}"));
        lines.Add("Worn:");
        lines.AddRange(inventory.Worn.Select(w => $"  {w.Slot}: {Name(w.Item)}{(w.Active ? "" : " (inactive)")}"));
        return string.Join('\n', lines);
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
