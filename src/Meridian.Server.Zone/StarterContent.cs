using Meridian.Core.Combat;
using Meridian.Core.Stats;

namespace Meridian.Server.Zone;

/// <summary>A kind of creature the zone can spawn.</summary>
/// <param name="Id">Content id; also the client's model selector (EntityInfo.templateId).</param>
/// <param name="Name">Display name.</param>
/// <param name="Level">Level shown to players.</param>
/// <param name="ApplyStats">Sets base stats on a fresh <see cref="StatBlock"/>.</param>
/// <param name="Attack">Its natural attack.</param>
/// <param name="AggroRadiusMm">Attacks players who come this close. 0 = only fights back.</param>
/// <param name="LeashMm">Gives up and goes home past this distance from its spawn.</param>
/// <param name="SpeedMmPerSecond">Run speed while chasing; wanders at half.</param>
/// <param name="RespawnTicks">Delay before it reappears after death.</param>
public sealed record MobTemplate(
    ushort Id,
    string Name,
    int Level,
    Action<StatBlock> ApplyStats,
    WeaponProfile Attack,
    int AggroRadiusMm,
    int LeashMm,
    int SpeedMmPerSecond,
    int RespawnTicks);

/// <summary>
/// Hand-written M1 content. Moves to YAML content packs with the content compiler; numbers here
/// are first-pass tuning: two level-1 players kill a skitter in roughly 15 s, one alone in ~30 s.
/// </summary>
public static class StarterContent
{
    public static WeaponProfile IssueRifle { get; } = new(
        "Combine Issue Carbine", Stat.Rifle, Stat.RangedInit, Stat.EvadeRanged,
        DamageType.Projectile, MinDamage: 8, MaxDamage: 20, BaseAttackTimeMs: 1_500, RangeMm: 25_000);

    public static MobTemplate GlasswindSkitter { get; } = new(
        Id: 1,
        Name: "Glasswind Skitter",
        Level: 3,
        ApplyStats: s =>
        {
            s.SetBase(Stat.Level, 3);
            s.SetBase(Stat.BodyDevelopment, 30);
            s.SetBase(Stat.Stamina, 30);
            s.SetBase(Stat.Unarmed, 40);
            s.SetBase(Stat.MeleeInit, 0);
            s.SetBase(Stat.EvadeRanged, 20);
            s.SetBase(Stat.EvadeClose, 20);
            s.SetBase(Stat.ProjectileAC, 15);
            s.SetBase(Stat.MeleeAC, 15);
        },
        Attack: new WeaponProfile(
            "Glass Mandibles", Stat.Unarmed, Stat.MeleeInit, Stat.EvadeClose,
            DamageType.Melee, MinDamage: 5, MaxDamage: 12, BaseAttackTimeMs: 2_000, RangeMm: 3_000),
        AggroRadiusMm: 8_000,
        LeashMm: 40_000,
        SpeedMmPerSecond: 4_000,
        RespawnTicks: 200);

    /// <summary>Base stats for a new level-1 character until character creation exists.</summary>
    public static void ApplyNewPlayerStats(StatBlock s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.SetBase(Stat.Level, 1);
        s.SetBase(Stat.RunSpeed, 5_000); // mm/s
        s.SetBase(Stat.BodyDevelopment, 10);
        s.SetBase(Stat.Stamina, 20);
        s.SetBase(Stat.Rifle, 30);
        s.SetBase(Stat.RangedInit, 10);
        s.SetBase(Stat.EvadeRanged, 20);
        s.SetBase(Stat.EvadeClose, 20);
        s.SetBase(Stat.ProjectileAC, 10);
        s.SetBase(Stat.MeleeAC, 10);
    }
}
