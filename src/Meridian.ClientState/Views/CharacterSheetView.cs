using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Stats;

namespace Meridian.ClientState.Views;

/// <summary>One stat on the character window.</summary>
/// <param name="Base">Trained value (what training raises).</param>
/// <param name="Effective">With items and buffs.</param>
/// <param name="Trainable">False for derived stats, which only show values.</param>
/// <param name="CanTrain">At least one point can be bought now (not locked, under the cap, affordable).</param>
public sealed record StatRow(Stat Stat, string Name, int Base, int Effective, bool Trainable, CostTier Tier, int CostPerPoint, int Cap, bool CanTrain);

public sealed record StatGroup(string Title, IReadOnlyList<StatRow> Rows);

/// <summary>What the character window shows, computed with the same rules the server uses.</summary>
public static class CharacterSheetView
{
    /// <summary>Derived stats shown for reference at the end of the sheet.</summary>
    public static IReadOnlyList<Stat> Derived { get; } =
        [Stat.MaxHealth, Stat.MaxEnergy, Stat.LatticeCapacity, Stat.RunSpeed];

    public static IReadOnlyList<StatGroup> Build(CharacterBuild build, IReadOnlyDictionary<Stat, int> effective)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(effective);
        var groups = Skills.Trainable
            .GroupBy(Skills.GroupOf)
            .OrderBy(g => g.Key)
            .Select(g => new StatGroup(StatNames.Spaced(g.Key.ToString()), [.. g.Select(s => Row(build, effective, s))]))
            .ToList();
        groups.Add(new StatGroup("Derived", [.. Derived.Select(s => new StatRow(
            s, StatNames.Display(s), build.Character.Stats.GetBase(s), effective.GetValueOrDefault(s, build.Character.Get(s)),
            Trainable: false, CostTier.Locked, 0, 0, CanTrain: false))]));
        return groups;
    }

    /// <summary>How many of <paramref name="wanted"/> points can be bought now: limited by the cap and unspent points.</summary>
    public static int Affordable(CharacterBuild build, Stat stat, int wanted)
    {
        ArgumentNullException.ThrowIfNull(build);
        var cost = build.CostPerPoint(stat);
        if (wanted <= 0 || cost <= 0 || build.TierOf(stat) == CostTier.Locked)
        {
            return 0;
        }
        var room = build.Cap(stat) - build.Character.Stats.GetBase(stat);
        return Math.Max(0, Math.Min(wanted, Math.Min(room, build.UnspentPoints / cost)));
    }

    private static StatRow Row(CharacterBuild build, IReadOnlyDictionary<Stat, int> effective, Stat stat)
    {
        var tier = build.TierOf(stat);
        return new StatRow(
            stat,
            StatNames.Display(stat),
            build.Character.Stats.GetBase(stat),
            effective.GetValueOrDefault(stat, build.Character.Get(stat)),
            Trainable: true,
            tier,
            tier == CostTier.Locked ? 0 : build.CostPerPoint(stat),
            tier == CostTier.Locked ? 0 : build.Cap(stat),
            Affordable(build, stat, 1) > 0);
    }
}
