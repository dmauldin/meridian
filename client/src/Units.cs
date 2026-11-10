using Godot;
using Meridian.Core.World;

namespace Meridian.Client;

/// <summary>
/// Conversions between server space (integer millimetres, yaw byte) and Godot space (metres,
/// radians). Axes map one to one: server X → Godot X, server Z → Godot Z, Godot Y is up.
/// </summary>
public static class Units
{
    public const float MetresPerMm = 0.001f;

    public static Vector3 ToGodot(double xMm, double zMm, float y = 0) =>
        new((float)(xMm * MetresPerMm), y, (float)(zMm * MetresPerMm));

    public static Vector3 ToGodot(Position p, float y = 0) => ToGodot(p.X, p.Z, y);

    /// <summary>Server facing (0 = +Z, increasing toward +X) equals Godot's rotation about Y.</summary>
    public static float YawToGodot(double yawRadians) => (float)yawRadians;

    /// <summary>A horizontal Godot direction as the ±32767 vector the server expects.</summary>
    public static (short X, short Z) ToWireDirection(Vector3 direction)
    {
        var flat = new Vector2(direction.X, direction.Z);
        if (flat.LengthSquared() < 1e-6f)
        {
            return (0, 0);
        }
        flat = flat.Normalized();
        return ((short)Mathf.Round(flat.X * Movement.DirectionScale), (short)Mathf.Round(flat.Y * Movement.DirectionScale));
    }
}
