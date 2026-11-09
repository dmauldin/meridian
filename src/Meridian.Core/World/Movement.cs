namespace Meridian.Core.World;

/// <summary>A position in zone space, in millimetres. Y (height) arrives with terrain in M1.</summary>
public readonly record struct Position(int X, int Z)
{
    public long DistanceSquared(Position other)
    {
        long dx = X - other.X;
        long dz = Z - other.Z;
        return (dx * dx) + (dz * dz);
    }
}

/// <summary>An axis-aligned zone boundary in millimetres, inclusive.</summary>
public readonly record struct ZoneBounds(int MinX, int MinZ, int MaxX, int MaxZ)
{
    public Position Clamp(Position p) => new(Math.Clamp(p.X, MinX, MaxX), Math.Clamp(p.Z, MinZ, MaxZ));
}

/// <summary>
/// Movement integration shared by the server simulation and client-side prediction, so both
/// produce the same position from the same inputs. Integer math only.
/// </summary>
public static class Movement
{
    /// <summary>Scale of a unit direction component on the wire (i16).</summary>
    public const int DirectionScale = 32_767;

    /// <summary>
    /// Advances <paramref name="from"/> by <paramref name="elapsedMs"/> along a direction whose
    /// components are scaled by <see cref="DirectionScale"/>. Directions longer than a unit vector
    /// are scaled down, so a client cannot run faster by sending a bigger vector; shorter ones
    /// move proportionally slower (walking).
    /// </summary>
    /// <param name="from">Start position.</param>
    /// <param name="dirX">X component, −32767..32767.</param>
    /// <param name="dirZ">Z component, −32767..32767.</param>
    /// <param name="speedMmPerSecond">Full speed, from the RunSpeed stat.</param>
    /// <param name="elapsedMs">Simulation step.</param>
    /// <param name="bounds">Zone limits.</param>
    public static Position Step(Position from, int dirX, int dirZ, int speedMmPerSecond, int elapsedMs, ZoneBounds bounds)
    {
        long dx = Math.Clamp(dirX, -DirectionScale, DirectionScale);
        long dz = Math.Clamp(dirZ, -DirectionScale, DirectionScale);
        var lengthSquared = (dx * dx) + (dz * dz);
        if (lengthSquared == 0 || speedMmPerSecond <= 0 || elapsedMs <= 0)
        {
            return from;
        }

        const long unitSquared = (long)DirectionScale * DirectionScale;
        long length = DirectionScale;
        if (lengthSquared > unitSquared)
        {
            // Normalize diagonal or oversized input down to unit length.
            length = ISqrt(lengthSquared);
        }

        long travel = (long)speedMmPerSecond * elapsedMs; // mm * 1000
        var moveX = dx * travel / (length * 1000);
        var moveZ = dz * travel / (length * 1000);
        var x = Math.Clamp(from.X + moveX, int.MinValue, int.MaxValue);
        var z = Math.Clamp(from.Z + moveZ, int.MinValue, int.MaxValue);
        return bounds.Clamp(new Position((int)x, (int)z));
    }

    /// <summary>Facing as a byte (0 = +Z, 64 = +X, 128 = −Z, 192 = −X), for compact snapshots.</summary>
    /// <remarks>
    /// Presentation only: uses floating-point atan2, so a value exactly on a rounding boundary
    /// could differ between platforms. Nothing in the rules may depend on it.
    /// </remarks>
    public static byte YawByte(int dirX, int dirZ, byte previous)
    {
        if (dirX == 0 && dirZ == 0)
        {
            return previous;
        }
        var radians = Math.Atan2(dirX, dirZ);
        var turns = radians / (2 * Math.PI);
        return (byte)((int)Math.Round(turns * 256) & 0xFF);
    }

    private static long ISqrt(long n)
    {
        var r = (long)Math.Sqrt(n);
        while (r * r > n)
        {
            r--;
        }
        while ((r + 1) * (r + 1) <= n)
        {
            r++;
        }
        return r;
    }
}
