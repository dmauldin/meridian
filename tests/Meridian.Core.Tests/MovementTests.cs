using Meridian.Core.World;

namespace Meridian.Core.Tests;

public class MovementTests
{
    private static readonly ZoneBounds Big = new(-1_000_000, -1_000_000, 1_000_000, 1_000_000);
    private const int Unit = Movement.DirectionScale;

    [Fact]
    public void Full_speed_along_an_axis()
    {
        var p = Movement.Step(new Position(0, 0), Unit, 0, speedMmPerSecond: 5_000, elapsedMs: 50, Big);
        Assert.Equal(new Position(250, 0), p);
    }

    [Fact]
    public void Oversized_diagonal_input_is_normalized_so_speed_hacks_fail()
    {
        var p = Movement.Step(new Position(0, 0), Unit, Unit, 5_000, 1_000, Big);
        var distance = Math.Sqrt(p.DistanceSquared(new Position(0, 0)));
        Assert.InRange(distance, 4_990, 5_000);
    }

    [Fact]
    public void Half_vector_walks_at_half_speed()
    {
        var p = Movement.Step(new Position(0, 0), 0, -Unit / 2, 5_000, 1_000, Big);
        Assert.InRange(p.Z, -2_501, -2_499);
    }

    [Fact]
    public void Clamped_to_zone_bounds()
    {
        var small = new ZoneBounds(0, 0, 100, 100);
        Assert.Equal(new Position(100, 50), Movement.Step(new Position(90, 50), Unit, 0, 5_000, 1_000, small));
    }

    [Fact]
    public void Zero_input_does_not_move()
    {
        Assert.Equal(new Position(7, 7), Movement.Step(new Position(7, 7), 0, 0, 5_000, 50, Big));
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 64)]
    [InlineData(0, -1, 128)]
    [InlineData(-1, 0, 192)]
    public void Yaw_byte_quadrants(int x, int z, byte expected)
    {
        Assert.Equal(expected, Movement.YawByte(x, z, 0));
    }
}

public class SteeringTests
{
    [Fact]
    public void Direction_towards_is_unit_length_and_points_the_right_way()
    {
        var (x, z) = Movement.DirectionTowards(new Position(0, 0), new Position(3_000, 4_000));
        Assert.InRange(x, 19_658, 19_662); // 0.6 * 32767
        Assert.InRange(z, 26_211, 26_215); // 0.8 * 32767
    }

    [Fact]
    public void Direction_towards_self_is_zero()
    {
        Assert.Equal((0, 0), Movement.DirectionTowards(new Position(5, 5), new Position(5, 5)));
    }

    [Fact]
    public void Direction_towards_handles_extreme_distances()
    {
        var (x, z) = Movement.DirectionTowards(new Position(int.MinValue, 0), new Position(int.MaxValue, 0));
        Assert.Equal((short)Movement.DirectionScale, x);
        Assert.Equal(0, z);
    }

    [Fact]
    public void Distance_is_exact_for_integer_triangles()
    {
        Assert.Equal(5_000, Movement.Distance(new Position(0, 0), new Position(3_000, 4_000)));
    }
}
