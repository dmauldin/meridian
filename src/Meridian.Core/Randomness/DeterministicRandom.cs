namespace Meridian.Core.Randomness;

/// <summary>
/// xoshiro256** with SplitMix64 seeding. Fast, small, and identical on every platform, so a
/// zone simulation seeded the same way replays the same rolls.
/// Not cryptographically secure; never use it for keys or tokens.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public DeterministicRandom(ulong seed)
    {
        _s0 = SplitMix(ref seed);
        _s1 = SplitMix(ref seed);
        _s2 = SplitMix(ref seed);
        _s3 = SplitMix(ref seed);
    }

    public ulong NextUInt64()
    {
        var result = RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform integer in [<paramref name="minInclusive"/>, <paramref name="maxInclusive"/>], without modulo bias.</summary>
    public int NextInt(int minInclusive, int maxInclusive)
    {
        if (maxInclusive < minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInclusive), "max < min");
        }
        var range = (ulong)((long)maxInclusive - minInclusive) + 1;
        // Lemire's nearly-divisionless method.
        var x = NextUInt64();
        var m = Math.BigMul(x, range, out var low);
        if (low < range)
        {
            var threshold = (0 - range) % range;
            while (low < threshold)
            {
                x = NextUInt64();
                m = Math.BigMul(x, range, out low);
            }
        }
        return (int)(minInclusive + (long)m);
    }

    /// <summary>True with probability <paramref name="basisPoints"/> / 10000.</summary>
    public bool Chance(int basisPoints) => NextInt(0, 9_999) < basisPoints;

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));

    private static ulong SplitMix(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
