using Meridian.Core.Protocols;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;

namespace Meridian.Core.Tests;

public class ActiveProtocolsTests
{
    private static readonly ProtocolLine RifleLine = new(1);
    private static readonly ProtocolLine AgilityLine = new(2);

    private static ProtocolDef Rifle(int id, int strength, int bonus, int cost) =>
        new(id, $"Rifle Enhancement {strength}", RifleLine, strength, cost, EnergyCost: 10, DurationTicks: 100,
            Requirement.None, [new StatEffect(Stat.Rifle, ModifierKind.Flat, bonus)]);

    private static ProtocolDef Agility(int cost) =>
        new(100, "Reflex Booster", AgilityLine, 1, cost, 10, 50, Requirement.None,
            [new StatEffect(Stat.Agility, ModifierKind.Flat, 25)]);

    /// <summary>Level 10, Psyche 40 → lattice capacity 10 + 20 + 20 = 50.</summary>
    private static (StatBlock Stats, ActiveProtocols Buffs) Target()
    {
        var s = new StatBlock();
        s.SetBase(Stat.Level, 10);
        s.SetBase(Stat.Psyche, 40);
        s.SetBase(Stat.Rifle, 100);
        s.SetBase(Stat.Agility, 100);
        return (s, new ActiveProtocols(s));
    }

    [Fact]
    public void Applying_adds_its_effects_and_uses_capacity()
    {
        var (s, buffs) = Target();
        Assert.Equal(ApplyResult.Applied, buffs.TryApply(Rifle(1, 1, 15, 4), nowTick: 0));
        Assert.Equal(115, s.Get(Stat.Rifle));
        Assert.Equal(4, buffs.UsedCapacity);
    }

    [Fact]
    public void Different_lines_stack()
    {
        var (s, buffs) = Target();
        buffs.TryApply(Rifle(1, 1, 15, 4), 0);
        Assert.Equal(ApplyResult.Applied, buffs.TryApply(Agility(5), 0));
        Assert.Equal(115, s.Get(Stat.Rifle));
        Assert.Equal(125, s.Get(Stat.Agility));
        Assert.Equal(9, buffs.UsedCapacity);
    }

    [Fact]
    public void Stronger_member_replaces_weaker_one()
    {
        var (s, buffs) = Target();
        buffs.TryApply(Rifle(1, 1, 15, 4), 0);
        Assert.Equal(ApplyResult.Replaced, buffs.TryApply(Rifle(3, 3, 45, 12), 0));
        Assert.Equal(145, s.Get(Stat.Rifle));
        Assert.Equal(12, buffs.UsedCapacity);
        Assert.Single(buffs.Running);
    }

    [Theory]
    [InlineData(1)] // weaker
    [InlineData(3)] // equal strength, different Protocol
    public void Weaker_or_equal_member_is_rejected_and_changes_nothing(int strength)
    {
        var (s, buffs) = Target();
        buffs.TryApply(Rifle(3, 3, 45, 12), 0);
        Assert.Equal(ApplyResult.RejectedWeaker, buffs.TryApply(Rifle(99, strength, 15, 4), 0));
        Assert.Equal(145, s.Get(Stat.Rifle));
    }

    [Fact]
    public void Recasting_the_same_Protocol_refreshes_duration_without_doubling_effects()
    {
        var (s, buffs) = Target();
        buffs.TryApply(Rifle(1, 1, 15, 4), 0);
        Assert.Equal(ApplyResult.Refreshed, buffs.TryApply(Rifle(1, 1, 15, 4), 60));
        Assert.Equal(115, s.Get(Stat.Rifle));
        Assert.Equal(160, buffs.InLine(RifleLine)!.ExpiresAtTick);
    }

    [Fact]
    public void Capacity_limit_rejects_a_buff_that_does_not_fit()
    {
        var (s, buffs) = Target(); // capacity 50
        buffs.TryApply(Agility(30), 0);
        Assert.Equal(ApplyResult.RejectedCapacity, buffs.TryApply(Rifle(1, 1, 15, 21), 0));
        Assert.Equal(100, s.Get(Stat.Rifle));
        Assert.Equal(ApplyResult.Applied, buffs.TryApply(Rifle(1, 1, 15, 20), 0)); // exactly full is fine
    }

    [Fact]
    public void Replacing_counts_the_freed_capacity()
    {
        var (_, buffs) = Target(); // capacity 50
        buffs.TryApply(Agility(30), 0);
        buffs.TryApply(Rifle(1, 1, 15, 15), 0); // 45 used
        // Upgrade costs 20: 45 - 15 + 20 = 50, fits only because the old one is freed.
        Assert.Equal(ApplyResult.Replaced, buffs.TryApply(Rifle(2, 2, 30, 20), 0));
        Assert.Equal(50, buffs.UsedCapacity);
    }

    [Fact]
    public void Expiry_removes_effects_and_frees_capacity()
    {
        var (s, buffs) = Target();
        buffs.TryApply(Rifle(1, 1, 15, 4), 0);   // expires at 100
        buffs.TryApply(Agility(5), 0);            // expires at 50

        Assert.Empty(buffs.Expire(49));
        var gone = Assert.Single(buffs.Expire(50));
        Assert.Equal(100, gone.Def.Id);
        Assert.Equal(100, s.Get(Stat.Agility));
        Assert.Equal(115, s.Get(Stat.Rifle));
        Assert.Equal(4, buffs.UsedCapacity);
    }

    [Fact]
    public void Cancel_removes_the_line()
    {
        var (s, buffs) = Target();
        buffs.TryApply(Rifle(1, 1, 15, 4), 0);
        Assert.True(buffs.Cancel(RifleLine));
        Assert.False(buffs.Cancel(RifleLine));
        Assert.Equal(100, s.Get(Stat.Rifle));
        Assert.Equal(0, buffs.UsedCapacity);
    }
}
