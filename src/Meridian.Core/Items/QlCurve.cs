namespace Meridian.Core.Items;

/// <summary>A value pinned at a quality level, e.g. "Rifle requirement 420 at QL 150".</summary>
public readonly record struct QlAnchor(int Ql, int Value);

/// <summary>
/// Piecewise-linear integer interpolation across quality levels 1–300. Content defines a few
/// anchors per item; every QL in between is interpolated so a QL 147 rifle sits between the
/// QL 100 and QL 200 versions.
/// </summary>
public sealed class QlCurve
{
    public const int MinQl = 1;
    public const int MaxQl = 300;

    private readonly QlAnchor[] _anchors;

    public QlCurve(params QlAnchor[] anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        if (anchors.Length == 0)
        {
            throw new ArgumentException("A curve needs at least one anchor.", nameof(anchors));
        }
        _anchors = anchors.OrderBy(a => a.Ql).ToArray();
        for (var i = 0; i < _anchors.Length; i++)
        {
            var ql = _anchors[i].Ql;
            if (ql is < MinQl or > MaxQl)
            {
                throw new ArgumentOutOfRangeException(nameof(anchors), ql, $"QL must be {MinQl}–{MaxQl}.");
            }
            if (i > 0 && _anchors[i - 1].Ql == ql)
            {
                throw new ArgumentException($"Duplicate anchor at QL {ql}.", nameof(anchors));
            }
        }
    }

    public IReadOnlyList<QlAnchor> Anchors => _anchors;

    /// <summary>
    /// The value at <paramref name="ql"/>. Outside the anchored range the nearest anchor's value
    /// is used (no extrapolation). Between anchors, rounds half away from zero.
    /// </summary>
    public int At(int ql)
    {
        if (ql <= _anchors[0].Ql)
        {
            return _anchors[0].Value;
        }
        if (ql >= _anchors[^1].Ql)
        {
            return _anchors[^1].Value;
        }

        var hi = 1;
        while (_anchors[hi].Ql < ql)
        {
            hi++;
        }
        var a = _anchors[hi - 1];
        var b = _anchors[hi];
        long numerator = (long)(b.Value - a.Value) * (ql - a.Ql);
        long span = b.Ql - a.Ql;
        return a.Value + (int)RoundedDivide(numerator, span);
    }

    private static long RoundedDivide(long n, long d)
    {
        // d > 0 here. Round half away from zero.
        var q = Math.DivRem(Math.Abs(n), d, out var r);
        if (2 * r >= d)
        {
            q++;
        }
        return n < 0 ? -q : q;
    }
}
