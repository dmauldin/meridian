using Godot;
using Meridian.ClientState;
using Meridian.Core.World;

namespace Meridian.Client;

/// <summary>Command-line options for <see cref="AutoPilot"/>, after Godot's <c>--</c> separator.</summary>
/// <remarks>
/// <c>godot --headless --path client -- --autopilot --user Ann --pass autopilot1 --auth http://host:5080/ --timeout 120</c>
/// The account and its character are both named after <c>--user</c> and created on first run.
/// Add <c>--screenshot shot.png</c> (with a real or virtual display) to save a frame 2 s into the fight,
/// and <c>--profession Medic</c> to play another profession (default Operative). <c>--show-ui</c> opens the
/// windows for that screenshot.
/// </remarks>
/// <param name="ShowUi">Before the screenshot, open the character and inventory windows, cast a Protocol on
/// yourself and give yourself an item to inspect (needs the zone's MERIDIAN_DEV_COMMANDS=1).</param>
/// <param name="Travel">Instead of fighting, walk into the zone's exit and stop 2 s after arriving in the next
/// zone (screenshots: the pad on the way, as <c>name.pad.png</c>, and the arrival). Needs that zone running.</param>
/// <param name="Shop">Walk to the nearest zone with shops, open the first, buy the cheapest item you can
/// afford and sell it back from the inventory window, then exit 0 (screenshots: the terminal as
/// <c>name.terminal.png</c>, the shop as <c>name.shop.png</c>, and the inventory with its Sell button). Needs that
/// zone running.</param>
public sealed record AutoPilotOptions(string AuthUrl, string Username, string Password, double TimeoutSeconds, string? ScreenshotPath, string Profession, bool ShowUi, bool Travel = false, bool Shop = false)
{
    public static AutoPilotOptions? Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.Contains("--autopilot"))
        {
            return null;
        }
        string Arg(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        return new AutoPilotOptions(
            Arg("--auth", "http://127.0.0.1:5080/"),
            Arg("--user", $"Auto{System.Environment.ProcessId % 10_000}"),
            Arg("--pass", "autopilot1"),
            double.Parse(Arg("--timeout", "120"), System.Globalization.CultureInfo.InvariantCulture),
            Arg("--screenshot", "") is { Length: > 0 } shot ? shot : null,
            Arg("--profession", "Operative"),
            args.Contains("--show-ui"),
            args.Contains("--travel"),
            args.Contains("--shop"));
    }
}

/// <summary>
/// Plays the M1 gate scenario through the real client code paths: targets the mob nearest the
/// zone origin (so two autopilots pick the same one), walks into rifle range, auto-attacks until
/// it dies, then exits 0. Prints one line per milestone for the gate script to read.
/// </summary>
public partial class AutoPilot(GameView view, AutoPilotOptions options) : Node
{
    private const int EngageMm = 18_000;
    private double _elapsed;
    private uint _quarry;
    private int _hits;
    private EntityKilled? _kill;
    private double _firstHitAt = -1;
    private bool _shotTaken;
    private bool _uiShown;
    private bool _itemSelected;
    private uint? _travelFrom;
    private double _arrivedAt = -1;
    private bool _padShot;
    private int _pointsBefore = -1;
    private int _shopStep;
    private double _shopStepAt;
    private long _creditsBefore;
    private long _creditsBought;
    private Meridian.Core.Content.ShopStock? _bought;
    private int _boughtAt = -1;
    private bool _trainSeen;

    public override void _Ready() => view.WorldEvent += OnWorldEvent;

    public override void _ExitTree() => view.WorldEvent -= OnWorldEvent;

    /// <summary>Walks onto the first exit; reports arriving in its zone, with screenshots of the pad and the arrival.</summary>
    private void Travel(ClientWorld world)
    {
        if (_arrivedAt >= 0)
        {
            view.PilotDirection = ((short)0, (short)0);
            if (_elapsed - _arrivedAt > 2)
            {
                Shoot(options.ScreenshotPath);
                GD.Print($"AUTOPILOT TRAVEL arrived zone={world.ZoneId} at {world.Self.Position.X / 1000} m, {world.Self.Position.Z / 1000} m seconds={_elapsed:0.0}");
                GetTree().Quit(0);
            }
            return;
        }
        if (!world.InWorld || !Meridian.Content.ContentPacks.Embedded.Zones.TryGet((int)world.ZoneId, out var zone) || zone.Exits.Count == 0)
        {
            view.PilotDirection = ((short)0, (short)0);
            return;
        }
        _travelFrom ??= world.ZoneId;
        var pad = zone.Exits[0].Area.Center;
        if (!_padShot && Movement.Distance(world.Self.Position, pad) < 12_000)
        {
            _padShot = true;
            Shoot(options.ScreenshotPath is { } path ? System.IO.Path.ChangeExtension(path, ".pad.png") : null);
        }
        view.PilotDirection = Movement.DirectionTowards(world.Self.Position, pad);
    }

    /// <summary>Walks to a shop (through an exit if this zone has none), buys, then sells it back; see <see cref="AutoPilotOptions.Shop"/>.</summary>
    private void Shop(ClientWorld world)
    {
        var pack = Meridian.Content.ContentPacks.Embedded;
        if (!world.InWorld || world.Inventory is not { } inventory || !pack.Zones.TryGet((int)world.ZoneId, out var zone))
        {
            view.PilotDirection = ((short)0, (short)0);
            return;
        }
        var shot = options.ScreenshotPath;
        switch (_shopStep)
        {
            case 0 when world.ShopInReach() is null:
                // Head for this zone's first shop, or the pad to a zone that has one.
                var goal = zone.Shops.Count > 0
                    ? zone.Shops[0].At
                    : zone.Exits.FirstOrDefault(e => pack.Zones[e.ToZoneKey].Shops.Count > 0)?.Area.Center;
                view.PilotDirection = goal is { } g ? Movement.DirectionTowards(world.Self.Position, g) : ((short)0, (short)0);
                return;
            case 0:
                view.PilotDirection = ((short)0, (short)0);
                (_shopStep, _shopStepAt) = (5, _elapsed); // settle a moment so the prompt is drawn
                return;
            case 5 when _elapsed - _shopStepAt > 0.5:
                Shoot(shot is null ? null : System.IO.Path.ChangeExtension(shot, ".terminal.png"));
                var shop = world.ShopInReach()!;
                view.ToggleShop();
                var row = Meridian.ClientState.Views.ShopView.Rows(pack, shop, inventory.Credits, world.EffectiveStats, world.Build!.Profession.Id)
                    .Where(r => r.Affordable).OrderBy(r => r.Price).FirstOrDefault();
                if (row is null)
                {
                    GD.PrintErr($"AUTOPILOT SHOP nothing affordable with {inventory.Credits} credits");
                    GetTree().Quit(3);
                    return;
                }
                _creditsBefore = inventory.Credits;
                _bought = shop.Stock[row.StockIndex];
                view.Session.Buy(shop.Id, row.StockIndex);
                GD.Print($"AUTOPILOT SHOP at {shop.Name}: buying {row.Name} for {row.Price}");
                _shopStep = 1;
                return;
            case 1 when inventory.Credits < _creditsBefore:
                _creditsBought = inventory.Credits;
                var template = pack.Items[_bought!.ItemKey];
                _boughtAt = inventory.Carried.Where(c => c.Item == new Meridian.Core.Items.OwnedItem(template.Id, _bought.Ql)).Select(c => c.Position).DefaultIfEmpty(-1).Last();
                (_shopStep, _shopStepAt) = (2, _elapsed);
                return;
            case 2 when _elapsed - _shopStepAt > 1:
                Shoot(shot is null ? null : System.IO.Path.ChangeExtension(shot, ".shop.png"));
                view.ToggleInventory();
                view.InventoryWindow.SelectCarried(_boughtAt); // shows its Sell button
                (_shopStep, _shopStepAt) = (3, _elapsed);
                return;
            case 3 when _elapsed - _shopStepAt > 1:
                Shoot(shot);
                view.Session.Sell(world.ShopInReach()!.Id, _boughtAt);
                _shopStep = 4;
                return;
            case 4 when inventory.Credits > _creditsBought:
                GD.Print($"AUTOPILOT SHOP ok: credits {_creditsBefore} -> {_creditsBought} (bought) -> {inventory.Credits} (sold) seconds={_elapsed:0.0}");
                GetTree().Quit(0);
                return;
        }
    }

    private void Shoot(string? path)
    {
        if (path is not null)
        {
            var error = GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"AUTOPILOT SCREENSHOT {path} ({error})");
        }
    }

    private void OnWorldEvent(GameEvent e)
    {
        if (e is ZoneEntered entered && _travelFrom is { } from && entered.ZoneId != from && _arrivedAt < 0)
        {
            _arrivedAt = _elapsed;
            return;
        }
        var self = view.World.SelfId;
        if (e is CombatText { Outcome: HitOutcome.Hit or HitOutcome.Critical } hit && hit.AttackerId == self && hit.TargetId == _quarry)
        {
            _hits++;
            if (_firstHitAt < 0)
            {
                _firstHitAt = _elapsed;
            }
        }
        else if (e is EntityKilled kill && kill.EntityId == _quarry)
        {
            _kill = kill;
        }
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_elapsed > options.TimeoutSeconds)
        {
            GD.PrintErr($"AUTOPILOT TIMEOUT after {options.TimeoutSeconds:0}s (hits={_hits})");
            GetTree().Quit(1);
            return;
        }

        var world = view.World;
        if (options.Travel)
        {
            Travel(world);
            return;
        }
        if (options.Shop)
        {
            Shop(world);
            return;
        }
        if (options.ShowUi && !_uiShown && _firstHitAt >= 0)
        {
            _uiShown = true;
            view.Session.Say("/give rifle.kestrel_mk2 1");
            if (world.Protocols is { Known.Count: > 0 } known)
            {
                view.Session.Cast(known.Known[0], onSelf: true);
            }
            view.ToggleCharacter();
            view.ToggleInventory();
        }
        if (_uiShown && _pointsBefore < 0 && world.Build is { } build && view.CharacterWindow.Click(Meridian.Core.Stats.Stat.Agility, 1))
        {
            _pointsBefore = build.UnspentPoints; // the server's next sheet must show the point spent
        }
        if (_pointsBefore >= 0 && !_trainSeen && world.Build is { } after && after.UnspentPoints < _pointsBefore)
        {
            _trainSeen = true;
            GD.Print($"AUTOPILOT UI TRAIN ok: +1 Agility button, points {_pointsBefore} -> {after.UnspentPoints}");
        }
        if (_uiShown && !_itemSelected && world.Inventory?.Carried.Count > 0)
        {
            _itemSelected = true; // a frame or more before the shot, so the selection is drawn
            view.InventoryWindow.SelectCarried(world.Inventory.Carried[^1].Position);
        }
        if (options.ScreenshotPath is { } path && !_shotTaken && _firstHitAt >= 0 && _elapsed - _firstHitAt > 2)
        {
            _shotTaken = true;
            var error = GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"AUTOPILOT SCREENSHOT {path} ({error})");
        }
        if (_kill is { } kill)
        {
            GD.Print($"AUTOPILOT KILL quarry={_quarry} killer={world.NameOf(kill.KillerId)} my_hits={_hits} seconds={_elapsed:0.0}");
            GetTree().Quit(_hits > 0 ? 0 : 2);
            return;
        }

        if (_quarry == 0)
        {
            _quarry = PickQuarry(world);
            if (_quarry != 0)
            {
                GD.Print($"AUTOPILOT TARGET {_quarry} ({world.NameOf(_quarry)})");
                view.Target(_quarry);
            }
            return;
        }

        if (!world.Entities.TryGetValue(_quarry, out var quarry) || !quarry.HasSamples)
        {
            view.PilotDirection = ((short)0, (short)0);
            return;
        }
        var there = quarry.Latest.Position;
        view.PilotDirection = Movement.Distance(world.Self.Position, there) > EngageMm
            ? Movement.DirectionTowards(world.Self.Position, there)
            : ((short)0, (short)0);
        if (!world.Status.Attacking && !quarry.Latest.Dead && world.Status.TargetId == _quarry)
        {
            view.SetAttack(true);
        }
    }

    /// <summary>The living mob nearest the zone origin, so independent pilots converge on one.</summary>
    private static uint PickQuarry(ClientWorld world) =>
        world.Entities.Values
            .Where(e => e.Description?.IsMob == true && e.HasSamples && !e.Latest.Dead)
            .OrderBy(e => e.Latest.Position.DistanceSquared(new Position(0, 0)))
            .ThenBy(e => e.EntityId)
            .Select(e => e.EntityId)
            .FirstOrDefault();
}
