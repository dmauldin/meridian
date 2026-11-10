using Godot;
using Meridian.ClientState;
using Meridian.Core.World;

namespace Meridian.Client;

/// <summary>Command-line options for <see cref="AutoPilot"/>, after Godot's <c>--</c> separator.</summary>
/// <remarks>
/// <c>godot --headless --path client -- --autopilot --user Ann --pass dev --auth http://host:5080/ --timeout 120</c>
/// Add <c>--screenshot shot.png</c> (with a real or virtual display) to save a frame 2 s into the fight.
/// </remarks>
public sealed record AutoPilotOptions(string AuthUrl, string Username, string Password, double TimeoutSeconds, string? ScreenshotPath)
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
            Arg("--pass", "dev"),
            double.Parse(Arg("--timeout", "120"), System.Globalization.CultureInfo.InvariantCulture),
            Arg("--screenshot", "") is { Length: > 0 } shot ? shot : null);
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

    public override void _Ready() => view.WorldEvent += OnWorldEvent;

    public override void _ExitTree() => view.WorldEvent -= OnWorldEvent;

    private void OnWorldEvent(GameEvent e)
    {
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
