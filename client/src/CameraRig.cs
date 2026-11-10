using Godot;

namespace Meridian.Client;

/// <summary>
/// Third-person orbit camera: follows a point, right-drag to orbit, wheel to zoom. Movement is
/// relative to the camera's yaw, so <see cref="Forward"/> and <see cref="Right"/> drive WASD.
/// </summary>
public partial class CameraRig : Node3D
{
    private const float MinDistance = 4, MaxDistance = 40, MinPitch = -80, MaxPitch = -5;
    private const float OrbitSensitivity = 0.25f;

    private readonly Node3D _pitch = new();
    private float _distance = 14;

    public Camera3D Camera { get; } = new() { Fov = 65, Far = 800 };

    public Vector3 Forward => -GlobalBasis.Z with { Y = 0 };

    public Vector3 Right => GlobalBasis.X with { Y = 0 };

    public override void _Ready()
    {
        AddChild(_pitch);
        _pitch.RotationDegrees = new Vector3(-28, 0, 0);
        _pitch.AddChild(Camera);
        ApplyZoom();
        Camera.MakeCurrent();
    }

    /// <summary>Eases toward <paramref name="target"/> so prediction corrections never jerk the view.</summary>
    public void Follow(Vector3 target, double delta)
    {
        var t = 1 - Mathf.Exp(-12 * (float)delta);
        GlobalPosition = GlobalPosition.Lerp(target + new Vector3(0, 1.6f, 0), t);
    }

    /// <summary>Handles orbit and zoom. Returns true if the event was consumed.</summary>
    public bool HandleInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion motion when Input.IsMouseButtonPressed(MouseButton.Right):
                RotateY(Mathf.DegToRad(-motion.Relative.X * OrbitSensitivity));
                var pitch = Mathf.Clamp(_pitch.RotationDegrees.X - (motion.Relative.Y * OrbitSensitivity), MinPitch, MaxPitch);
                _pitch.RotationDegrees = new Vector3(pitch, 0, 0);
                return true;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _distance = Mathf.Max(MinDistance, _distance * 0.9f);
                ApplyZoom();
                return true;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _distance = Mathf.Min(MaxDistance, _distance * 1.1f);
                ApplyZoom();
                return true;
            default:
                return false;
        }
    }

    private void ApplyZoom() => Camera.Position = new Vector3(0, 0, _distance);
}
