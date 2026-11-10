using Meridian.Core.World;

namespace Meridian.ClientState;

/// <summary>What <c>EntityInfo</c> told us about an entity. Null fields until it arrives.</summary>
public sealed record EntityDescription(byte Kind, ushort TemplateId, string Name, int Level)
{
    public const byte KindPlayer = 1;
    public const byte KindMob = 2;

    public bool IsMob => Kind == KindMob;
}

/// <summary>One snapshot's worth of state for an entity, stamped with server time.</summary>
public readonly record struct EntitySample(double ServerMs, Position Position, byte Yaw, byte HealthPercent, byte Flags)
{
    public bool Dead => (Flags & 1) != 0;

    public bool InCombat => (Flags & 2) != 0;
}

/// <summary>The interpolated pose to draw this frame.</summary>
public readonly record struct EntityPose(double X, double Z, double YawRadians, byte HealthPercent, bool Dead, bool InCombat);

/// <summary>
/// Another entity as the client sees it: a short buffer of server samples, drawn slightly in the
/// past so there are always two samples to interpolate between.
/// </summary>
public sealed class RemoteEntity(uint entityId)
{
    private const int MaxSamples = 32;
    private readonly List<EntitySample> _samples = [];

    public uint EntityId { get; } = entityId;

    public EntityDescription? Description { get; set; }

    public bool HasSamples => _samples.Count > 0;

    public EntitySample Latest => _samples[^1];

    public void AddSample(EntitySample sample)
    {
        // Unreliable snapshots can arrive out of order; keep the buffer sorted, drop duplicates.
        var i = _samples.Count;
        while (i > 0 && _samples[i - 1].ServerMs > sample.ServerMs)
        {
            i--;
        }
        if (i > 0 && _samples[i - 1].ServerMs == sample.ServerMs)
        {
            return;
        }
        _samples.Insert(i, sample);
        if (_samples.Count > MaxSamples)
        {
            _samples.RemoveAt(0);
        }
    }

    /// <summary>Marks the latest sample dead right away, ahead of the next snapshot.</summary>
    public void MarkDead()
    {
        if (_samples.Count > 0)
        {
            var s = _samples[^1];
            _samples[^1] = s with { Flags = (byte)(s.Flags | 1), HealthPercent = 0 };
        }
    }

    /// <summary>
    /// The pose at <paramref name="renderServerMs"/>: linear between the two samples around it,
    /// clamped to the newest sample (no extrapolation) and the oldest.
    /// </summary>
    public EntityPose PoseAt(double renderServerMs)
    {
        if (_samples.Count == 0)
        {
            return default;
        }
        if (renderServerMs <= _samples[0].ServerMs || _samples.Count == 1)
        {
            return ToPose(_samples[0]);
        }
        if (renderServerMs >= _samples[^1].ServerMs)
        {
            return ToPose(_samples[^1]);
        }

        var b = 1;
        while (_samples[b].ServerMs < renderServerMs)
        {
            b++;
        }
        var from = _samples[b - 1];
        var to = _samples[b];
        var t = (renderServerMs - from.ServerMs) / (to.ServerMs - from.ServerMs);
        return new EntityPose(
            Lerp(from.Position.X, to.Position.X, t),
            Lerp(from.Position.Z, to.Position.Z, t),
            LerpYaw(from.Yaw, to.Yaw, t),
            to.HealthPercent,
            to.Dead,
            to.InCombat);
    }

    private static EntityPose ToPose(EntitySample s) =>
        new(s.Position.X, s.Position.Z, YawToRadians(s.Yaw), s.HealthPercent, s.Dead, s.InCombat);

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    /// <summary>Interpolates along the shorter way around the circle.</summary>
    private static double LerpYaw(byte a, byte b, double t)
    {
        var delta = ((b - a + 128) & 0xFF) - 128; // -128..127
        return YawToRadians(a) + (delta * t * Math.PI * 2 / 256);
    }

    /// <summary>Yaw byte to radians (0 = facing +Z, increasing toward +X).</summary>
    public static double YawToRadians(byte yaw) => yaw * Math.PI * 2 / 256;
}
