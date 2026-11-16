using Godot;
using Meridian.Client.Ui;
using Meridian.ClientState;
using Meridian.ClientState.Views;
using Meridian.Content;

namespace Meridian.Client;

/// <summary>
/// The in-world scene: draws <see cref="ClientWorld"/> every frame, turns input into intents, and
/// feeds the HUD. All game state lives in <see cref="GameSession"/>; this class only presents it.
/// </summary>
public partial class GameView : Node3D
{
    /// <summary>Name plates further than this are hidden (the target's always shows).</summary>
    private const float PlateRangeM = 40;

    private readonly Dictionary<uint, EntityView> _views = [];
    private readonly HashSet<uint> _described = [];
    private readonly CameraRig _camera = new();
    private readonly Hud _hud = new();
    private readonly CanvasLayer _windows = new() { Layer = 2 };
    private readonly CharacterWindow _character = new();
    private readonly InventoryWindow _inventory = new();
    private readonly ProtocolBar _protocolBar = new();
    private EntityView? _selfView;
    private uint _target;
    private bool _ended;
    private double _netInfoTimer;

    public GameView(GameSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        Name = "GameView";
    }

    /// <summary>Raised once when the connection ends, with a reason for the player.</summary>
    public event Action<string>? SessionEnded;

    /// <summary>Every world event after the view has shown it (the autopilot listens here).</summary>
    public event Action<GameEvent>? WorldEvent;

    public GameSession Session { get; }

    public ClientWorld World => Session.World;

    /// <summary>The locally selected target (shown at once; the server confirms via SelfStatus).</summary>
    public uint TargetId => _target;

    /// <summary>When set, replaces keyboard movement (used by the autopilot).</summary>
    public (short X, short Z)? PilotDirection { get; set; }

    public override void _Ready()
    {
        WorldDressing.Build(this);
        AddChild(_camera);
        AddChild(_hud);
        _hud.ChatInput.TextSubmitted += OnChatSubmitted;

        AddChild(_windows);
        _character.Bind(Session);
        _inventory.Bind(Session);
        _protocolBar.Bind(Session);
        _windows.AddChild(_character);
        _windows.AddChild(_inventory);
        _windows.AddChild(_protocolBar);
        _protocolBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, 16);
        LayoutWindows();
        GetViewport().SizeChanged += LayoutWindows;

        _selfView = new EntityView(World.SelfId, isSelf: true);
        AddChild(_selfView);
        _selfView.SetSelfName("You");
        _selfView.Position = Units.ToGodot(World.Self.Position);
        _camera.GlobalPosition = _selfView.Position;
        _hud.AddChat("[color=#8af]Connected. Find a Glasswind Skitter (Tab), press Q to attack.[/color]");
    }

    public override void _Process(double delta)
    {
        var (dirX, dirZ) = PilotDirection ?? KeyboardDirection();
        Session.Update(Main.NowMs(), dirX, dirZ);

        if (!Session.Connected && !_ended)
        {
            _ended = true;
            SessionEnded?.Invoke(Session.DisconnectReason ?? "Disconnected.");
            return;
        }

        SyncEntities();
        SyncSelf(delta);
        ShowEvents();
        UpdateHud(delta);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_camera.HandleInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
        {
            PickAt(click.Position);
        }
        else if (@event.IsActionPressed(Controls.TargetNext))
        {
            Target(World.NextMobTarget(_target));
        }
        else if (@event.IsActionPressed(Controls.ClearTarget))
        {
            Target(0);
        }
        else if (@event.IsActionPressed(Controls.ToggleAttack))
        {
            SetAttack(!World.Status.Attacking);
        }
        else if (@event.IsActionPressed(Controls.OpenChat))
        {
            _hud.ChatInput.GrabFocus();
        }
        else if (@event.IsActionPressed(Controls.ToggleCharacter))
        {
            ToggleCharacter();
        }
        else if (@event.IsActionPressed(Controls.ToggleInventory))
        {
            ToggleInventory();
        }
        else if (Enumerable.Range(1, 9).FirstOrDefault(i => @event.IsActionPressed(Controls.Protocol(i))) is > 0 and var number)
        {
            _protocolBar.CastHotkey(number);
        }
        else
        {
            return;
        }
        GetViewport().SetInputAsHandled();
    }

    public void Target(uint entityId)
    {
        _target = entityId;
        Session.SetTarget(entityId);
    }

    public void SetAttack(bool enabled) => Session.SetAttack(enabled);

    public void ToggleCharacter()
    {
        _character.Toggle();
        _character.Refresh();
    }

    public void ToggleInventory()
    {
        _inventory.Toggle();
        _inventory.Refresh();
    }

    /// <summary>
    /// Character window on the left between the self frame and the chat; inventory on the right
    /// below the net readout and above the help text. Both shrink (and scroll) on small screens,
    /// where the character window may also cover the chat log.
    /// </summary>
    private void LayoutWindows()
    {
        var screen = GetViewport().GetVisibleRect().Size;
        const float Top = 230, Margin = 16, ChatHeight = 270, HelpHeight = 90;
        // On short screens the character window may cover the chat log (never the chat input).
        var characterHeight = screen.Y - Top - ChatHeight;
        if (characterHeight < 380)
        {
            characterHeight = screen.Y - Top - 60;
        }
        _character.Place(new Vector2(Margin, Top), new Vector2(Math.Max(420, screen.X / 3), characterHeight));
        var inventoryWidth = Math.Min(_inventory.PreferredSize.X, screen.X / 2.5f);
        _inventory.Place(new Vector2(screen.X - inventoryWidth - Margin, 90), new Vector2(inventoryWidth, screen.Y - 90 - HelpHeight));
    }

    /// <summary>The character window, for the autopilot's UI check.</summary>
    public CharacterWindow CharacterWindow => _character;

    /// <summary>The inventory window, for the autopilot's screenshots.</summary>
    public InventoryWindow InventoryWindow => _inventory;

    private (short X, short Z) KeyboardDirection()
    {
        if (_hud.ChatInput.HasFocus())
        {
            return (0, 0);
        }
        var forward = Input.GetActionStrength(Controls.MoveForward) - Input.GetActionStrength(Controls.MoveBack);
        var right = Input.GetActionStrength(Controls.MoveRight) - Input.GetActionStrength(Controls.MoveLeft);
        return Units.ToWireDirection((_camera.Forward * forward) + (_camera.Right * right));
    }

    private void SyncEntities()
    {
        var renderMs = World.RenderServerMs;
        foreach (var (id, entity) in World.Entities)
        {
            if (!entity.HasSamples)
            {
                continue; // info arrived before the first snapshot
            }
            if (!_views.TryGetValue(id, out var view))
            {
                view = new EntityView(id, isSelf: false);
                _views.Add(id, view);
                AddChild(view);
            }
            if (entity.Description is { } description && _described.Add(id))
            {
                view.Describe(description);
            }
            var pose = entity.PoseAt(renderMs);
            view.ApplyPose(pose, Units.YawToGodot(pose.YawRadians));
            view.SetTargeted(id == _target);
            view.SetPlatesVisible(id == _target || view.Position.DistanceSquaredTo(_camera.GlobalPosition) < PlateRangeM * PlateRangeM);
        }

        foreach (var id in _views.Keys.Where(id => !World.Entities.ContainsKey(id)).ToList())
        {
            _views[id].QueueFree();
            _views.Remove(id);
            _described.Remove(id);
        }
    }

    private void SyncSelf(double delta)
    {
        var status = World.Status;
        _selfView!.Position = Units.ToGodot(World.Self.Position);
        _selfView.Rotation = new Vector3(0, Units.YawToGodot(RemoteEntity.YawToRadians(World.Self.Yaw)), 0);
        _selfView.ShowHealth(status.MaxHealth > 0 ? status.Health * 100 / status.MaxHealth : 100, status.Dead, inCombat: false);
        _camera.Follow(_selfView.Position, delta);
    }

    private void ShowEvents()
    {
        foreach (var e in World.DrainEvents())
        {
            WorldEvent?.Invoke(e);
            switch (e)
            {
                case CombatText hit:
                    ShowCombatText(hit);
                    break;
                case EntityKilled kill:
                    var line = kill.EntityId == World.SelfId
                        ? $"[color=#f66]You were killed by {World.NameOf(kill.KillerId)}.[/color]"
                        : $"[color=#fc6]{World.NameOf(kill.KillerId)} killed {World.NameOf(kill.EntityId)}.[/color]";
                    _hud.AddChat(line);
                    break;
                case ChatLine chat:
                    _hud.AddChat($"[b]{Escape(chat.From)}:[/b] {Escape(chat.Text)}");
                    break;
                case SystemNotice notice:
                    _hud.AddChat($"[color=#8af]{Escape(notice.Text)}[/color]");
                    break;
                case CharacterChanged:
                    if (_character.Visible)
                    {
                        _character.Refresh();
                    }
                    if (_inventory.Visible)
                    {
                        _inventory.Refresh(); // requirement checks use current stats
                    }
                    break;
                case InventoryChanged when _inventory.Visible:
                    _inventory.Refresh();
                    break;
                case ExperienceGained xp:
                    FloatingText.Spawn(this, _selfView!.Position, $"+{xp.Amount} XP", new Color(0.7f, 0.6f, 1f));
                    if (xp.LevelsGained > 0)
                    {
                        _hud.AddChat($"[color=#c9f][b]Level {xp.Level}![/b] New skill points to spend: press C.[/color]");
                    }
                    break;
            }
        }
    }

    private void ShowCombatText(CombatText hit)
    {
        Vector3 at;
        if (hit.TargetId == World.SelfId)
        {
            at = _selfView!.Position;
        }
        else if (_views.TryGetValue(hit.TargetId, out var view))
        {
            at = view.Position;
        }
        else
        {
            return;
        }
        var mine = hit.AttackerId == World.SelfId;
        var onMe = hit.TargetId == World.SelfId;
        if (!mine && !onMe)
        {
            return; // other people's fights stay quiet
        }
        var (text, color) = hit.Outcome switch
        {
            HitOutcome.Miss => ("Miss", new Color(0.75f, 0.75f, 0.75f)),
            HitOutcome.Evaded => ("Evaded", new Color(0.5f, 0.7f, 1f)),
            HitOutcome.Critical => ($"{hit.Damage}!", new Color(1f, 0.85f, 0.2f)),
            _ => (hit.Damage.ToString(System.Globalization.CultureInfo.InvariantCulture), onMe ? new Color(1f, 0.35f, 0.3f) : Colors.White),
        };
        FloatingText.Spawn(this, at, text, color, big: hit.Outcome == HitOutcome.Critical);
    }

    private void UpdateHud(double delta)
    {
        var status = World.Status;
        _hud.SetSelf(Session.CharacterName, status.Health, status.MaxHealth);
        _hud.SetEnergy(status.Energy, status.MaxEnergy);
        _hud.SetBuffs(ProtocolBarView.Buffs(ContentPacks.Embedded, World.Protocols, World.NowMs - World.ProtocolsReceivedMs), Session.CancelProtocol);
        _protocolBar.Refresh();
        if (World.Build is { } build)
        {
            _hud.SetProgress(build.Level, build.Profession.Name, build.Xp, build.XpToNext, build.UnspentPoints);
        }
        _hud.SetBanner(status.Dead ? "You are dead. Reclamation in progress..." : "");

        if (_target != 0 && World.Entities.TryGetValue(_target, out var target) && target.HasSamples)
        {
            var name = target.Description is { } d ? $"{d.Name}  [{d.Level}]" : $"#{_target}";
            _hud.SetTarget(name, target.Latest.HealthPercent, status.Attacking, target.Latest.Dead);
        }
        else
        {
            _hud.SetTarget(null, 0, false, false);
        }

        _netInfoTimer -= delta;
        if (_netInfoTimer <= 0)
        {
            _netInfoTimer = 0.5;
            _hud.SetNetInfo($"{Engine.GetFramesPerSecond():0} fps   rtt {Session.Connection.RoundTripTimeMs} ms\ncorrection {World.Self.LastErrorMm} mm");
        }
    }

    private void PickAt(Vector2 screen)
    {
        var from = _camera.Camera.ProjectRayOrigin(screen);
        var to = from + (_camera.Camera.ProjectRayNormal(screen) * 500);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (hit.Count > 0 && hit["collider"].AsGodotObject() is Node node && node.HasMeta(EntityView.EntityIdMeta))
        {
            var id = (uint)node.GetMeta(EntityView.EntityIdMeta).AsInt64();
            if (id != World.SelfId)
            {
                Target(id);
            }
        }
    }

    private void OnChatSubmitted(string text)
    {
        Session.Say(text);
        _hud.ChatInput.Text = "";
        _hud.ChatInput.ReleaseFocus();
    }

    private static string Escape(string text) => text.Replace("[", "[lb]", StringComparison.Ordinal);
}
