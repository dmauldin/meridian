using Godot;
using Meridian.ClientState;

namespace Meridian.Client;

/// <summary>
/// How one entity is drawn: a placeholder body, a name plate, a health plate, a target ring and a
/// pick collider. Placeholder geometry until real models; mobs and players are clearly distinct.
/// </summary>
public partial class EntityView : Node3D
{
    /// <summary>Metadata key on the pick collider holding the entity id.</summary>
    public const string EntityIdMeta = "entity_id";

    private static readonly Color PlayerColor = new(0.35f, 0.62f, 0.95f);
    private static readonly Color SelfColor = new(0.40f, 0.90f, 0.70f);
    private static readonly Color MobColor = new(0.55f, 0.85f, 0.95f);

    private readonly Node3D _body = new();
    private readonly Label3D _name = Plate(56, 2.45f);
    private readonly Label3D _health = Plate(40, 2.1f);
    private readonly MeshInstance3D _ring = new();
    private readonly bool _isSelf;
    private string _baseName = "";
    private bool _isMob;
    private bool _wasDead;

    public EntityView(uint entityId, bool isSelf)
    {
        EntityId = entityId;
        _isSelf = isSelf;
        Name = $"Entity{entityId}";
    }

    public uint EntityId { get; }

    public override void _Ready()
    {
        AddChild(_body);
        AddChild(_name);
        AddChild(_health);

        _ring.Mesh = new TorusMesh { InnerRadius = 0.9f, OuterRadius = 1.05f, Rings = 32 };
        _ring.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.35f, 0.25f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _ring.Position = new Vector3(0, 0.05f, 0);
        _ring.Visible = false;
        AddChild(_ring);

        // Pick collider: a generous capsule so clicking near an entity selects it.
        var body = new StaticBody3D();
        body.SetMeta(EntityIdMeta, (long)EntityId);
        body.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.9f, Height = 2.4f }, Position = new Vector3(0, 1.2f, 0) });
        AddChild(body);

        BuildPlayerBody(_isSelf ? SelfColor : PlayerColor);
    }

    /// <summary>Applies what <c>EntityInfo</c> said; switches to the mob model when needed.</summary>
    public void Describe(EntityDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _baseName = description.Level > 0 ? $"{description.Name}  [{description.Level}]" : description.Name;
        _name.Text = _baseName;
        if (description.IsMob && !_isMob)
        {
            _isMob = true;
            foreach (var child in _body.GetChildren())
            {
                child.QueueFree();
            }
            BuildSkitterBody();
        }
    }

    public void SetSelfName(string name) => _name.Text = name;

    public void SetTargeted(bool targeted) => _ring.Visible = targeted;

    public void SetPlatesVisible(bool visible)
    {
        _name.Visible = visible;
        _health.Visible = visible;
    }

    public void ApplyPose(EntityPose pose, float yaw)
    {
        Position = Units.ToGodot(pose.X, pose.Z);
        _body.Rotation = new Vector3(0, yaw, 0);
        ShowHealth(pose.HealthPercent, pose.Dead, pose.InCombat);
    }

    public void ShowHealth(int percent, bool dead, bool inCombat)
    {
        _health.Text = dead ? "" : $"{new string('|', Math.Clamp(percent / 5, 0, 20))}{new string('.', 20 - Math.Clamp(percent / 5, 0, 20))}";
        _health.Modulate = percent > 50 ? new Color(0.5f, 1f, 0.5f) : percent > 20 ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 0.35f, 0.3f);
        _name.Modulate = inCombat ? new Color(1f, 0.75f, 0.6f) : Colors.White;
        if (dead != _wasDead)
        {
            _wasDead = dead;
            // Corpses lie down and go grey.
            _body.RotationDegrees = _body.RotationDegrees with { Z = dead ? 90 : 0 };
            _body.Position = dead ? new Vector3(0, 0.4f, 0) : Vector3.Zero;
            _name.Text = dead ? $"{_baseName} (dead)" : _baseName;
            foreach (var mesh in _body.GetChildren().OfType<MeshInstance3D>())
            {
                mesh.Transparency = dead ? 0.45f : 0f;
            }
        }
    }

    private void BuildPlayerBody(Color color)
    {
        var material = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.5f, Metallic = 0.2f };
        _body.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.4f, Height = 1.8f }, Position = new Vector3(0, 0.9f, 0), MaterialOverride = material });
        // A visor so facing reads at a glance (+Z is forward).
        _body.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.15f, 0.2f) },
            Position = new Vector3(0, 1.5f, 0.35f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.12f), EmissionEnabled = true, Emission = new Color(0.4f, 0.9f, 1f), EmissionEnergyMultiplier = 2f },
        });
    }

    /// <summary>A glassy, low, six-legged creature: placeholder for the Glasswind Skitter.</summary>
    private void BuildSkitterBody()
    {
        var shell = new StandardMaterial3D
        {
            AlbedoColor = MobColor with { A = 0.85f },
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Metallic = 0.6f,
            Roughness = 0.15f,
            EmissionEnabled = true,
            Emission = new Color(0.2f, 0.6f, 0.8f),
            EmissionEnergyMultiplier = 0.6f,
        };
        var legMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.22f, 0.26f), Roughness = 0.6f };
        _body.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.8f, Height = 0.9f }, Position = new Vector3(0, 0.7f, -0.2f), Scale = new Vector3(1, 0.8f, 1.4f), MaterialOverride = shell });
        _body.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.4f, Height = 0.6f }, Position = new Vector3(0, 0.75f, 0.9f), MaterialOverride = shell });
        for (var side = -1; side <= 1; side += 2)
        {
            for (var i = 0; i < 3; i++)
            {
                _body.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.03f, Height = 1.2f },
                    Position = new Vector3(side * 0.85f, 0.4f, -0.6f + (i * 0.55f)),
                    RotationDegrees = new Vector3(0, 0, side * 55),
                    MaterialOverride = legMaterial,
                });
            }
        }
    }

    private static Label3D Plate(int fontSize, float height) => new()
    {
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        NoDepthTest = true,
        FontSize = fontSize,
        OutlineSize = 8,
        PixelSize = 0.006f,
        Position = new Vector3(0, height, 0),
        FixedSize = false,
    };
}
