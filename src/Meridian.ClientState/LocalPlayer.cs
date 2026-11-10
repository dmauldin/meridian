using Meridian.Core.World;

namespace Meridian.ClientState;

/// <summary>
/// The player's own character, moved immediately from input (client-side prediction) with the
/// same <see cref="Movement.Step"/> the server runs, and pulled toward the server's answer.
/// </summary>
/// <remarks>
/// <para>Reconciliation: the server's position in a snapshot reflects inputs sent about one round
/// trip ago, so it is compared with where the client predicted itself one RTT ago. Small errors
/// are blended in a little per snapshot (invisible); large ones (blocked, knocked back, server
/// clamp) snap.</para>
/// <para>This is simpler than replaying unacknowledged inputs and is enough while movement is
/// unobstructed. Input replay is the upgrade path once terrain collision exists.</para>
/// </remarks>
public sealed class LocalPlayer
{
    /// <summary>Errors larger than this snap instead of blending.</summary>
    public const int SnapThresholdMm = 3_000;

    /// <summary>Fraction of a small error corrected per snapshot.</summary>
    public const double CorrectionFactor = 0.15;

    private const double HistoryMs = 2_000;

    /// <summary>Zone limits are the server's; the client only needs something wide.</summary>
    private static readonly ZoneBounds Unbounded = new(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);

    private readonly List<(double LocalMs, Position Position)> _history = [];
    private double _pendingMs;

    public Position Position { get; private set; }

    public byte Yaw { get; private set; }

    /// <summary>Size of the last correction, for debug overlays and tests.</summary>
    public int LastErrorMm { get; private set; }

    /// <summary>Moves the player locally. Whole milliseconds are integrated; fractions carry over.</summary>
    public void Advance(double nowMs, double elapsedMs, short dirX, short dirZ, int runSpeedMmPerSecond)
    {
        _pendingMs += elapsedMs;
        var whole = (int)_pendingMs;
        _pendingMs -= whole;
        if (whole > 0)
        {
            Position = Movement.Step(Position, dirX, dirZ, runSpeedMmPerSecond, whole, Unbounded);
            Yaw = Movement.YawByte(dirX, dirZ, Yaw);
        }
        Record(nowMs);
    }

    /// <summary>Places the player without blending (join, respawn).</summary>
    public void Teleport(Position position, double nowMs)
    {
        Position = position;
        _history.Clear();
        _pendingMs = 0;
        Record(nowMs);
    }

    /// <summary>Corrects the prediction with an authoritative position.</summary>
    public void Reconcile(Position server, double nowMs, int roundTripMs)
    {
        var then = PredictedAt(nowMs - roundTripMs);
        long ex = server.X - then.X;
        long ez = server.Z - then.Z;
        var error = Movement.Distance(then, server);
        LastErrorMm = error;
        if (error == 0)
        {
            return;
        }

        double fx, fz;
        if (error > SnapThresholdMm)
        {
            fx = ex;
            fz = ez;
        }
        else
        {
            fx = ex * CorrectionFactor;
            fz = ez * CorrectionFactor;
        }
        var shift = new Position((int)Math.Round(fx), (int)Math.Round(fz));
        Position = Shift(Position, shift);
        for (var i = 0; i < _history.Count; i++)
        {
            _history[i] = (_history[i].LocalMs, Shift(_history[i].Position, shift));
        }
    }

    /// <summary>Where the client thought it was at <paramref name="localMs"/> (nearest recorded frame).</summary>
    public Position PredictedAt(double localMs)
    {
        if (_history.Count == 0)
        {
            return Position;
        }
        var best = _history[^1];
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].LocalMs <= localMs)
            {
                best = _history[i];
                break;
            }
            best = _history[i];
        }
        return best.Position;
    }

    private void Record(double nowMs)
    {
        _history.Add((nowMs, Position));
        var cutoff = nowMs - HistoryMs;
        var drop = 0;
        while (drop < _history.Count - 1 && _history[drop].LocalMs < cutoff)
        {
            drop++;
        }
        _history.RemoveRange(0, drop);
    }

    private static Position Shift(Position p, Position by) => new(p.X + by.X, p.Z + by.Z);
}
