using System.Collections.Frozen;
using Meridian.Core.Characters;
using Meridian.Core.Combat;
using Meridian.Core.Items;
using Meridian.Core.Protocols;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;
using Meridian.Core.World;

namespace Meridian.Core.Content;

/// <summary>A stat minimum that scales with item quality level.</summary>
public sealed record ScaledRequirement(Stat Stat, QlCurve Minimum);

/// <summary>A stat change that scales with item quality level.</summary>
public sealed record ScaledEffect(Stat Stat, ModifierKind Kind, QlCurve Value);

/// <summary>The weapon part of an item template; damage scales with QL.</summary>
public sealed record WeaponTemplate(
    Stat AttackSkill,
    Stat InitiativeSkill,
    Stat DefenceSkill,
    DamageType DamageType,
    int AttackTimeMs,
    int RangeMm,
    QlCurve MinDamage,
    QlCurve MaxDamage);

/// <summary>
/// An item kind, valid across a range of quality levels. A concrete item is a template at one QL:
/// <see cref="Resolve"/> turns it into the requirement and effects the equipment rules use.
/// </summary>
/// <param name="Id">Stable numeric id: wire and database. Append-only.</param>
/// <param name="Key">Stable text id used by content references, e.g. <c>rifle.combine_carbine</c>.</param>
public sealed record ItemTemplate(
    int Id,
    string Key,
    string Name,
    EquipSlot Slot,
    int MinQl,
    int MaxQl,
    IReadOnlyList<ScaledRequirement> Requirements,
    IReadOnlyList<Profession> Professions,
    IReadOnlyList<ScaledEffect> Effects,
    WeaponTemplate? Weapon,
    string? ProtocolKey = null,
    QlCurve? Value = null)
{
    /// <summary>True for an item that teaches a Protocol when uploaded (it has no slot).</summary>
    public bool IsProtocol => ProtocolKey is not null;

    /// <summary>What a shop charges for this item at <paramref name="ql"/>, or null if it has no price (cannot be bought or sold).</summary>
    public long? ValueAt(int ql) => Value?.At(ClampQl(ql));

    /// <summary>Clamps <paramref name="ql"/> into this template's range.</summary>
    public int ClampQl(int ql) => Math.Clamp(ql, MinQl, MaxQl);

    /// <summary>The requirement at <paramref name="ql"/>.</summary>
    public Requirement RequirementAt(int ql)
    {
        ql = ClampQl(ql);
        var clauses = new List<Requirement>(Requirements.Count + 1);
        foreach (var r in Requirements)
        {
            clauses.Add(Requirement.AtLeast(r.Stat, r.Minimum.At(ql)));
        }
        if (Professions.Count > 0)
        {
            clauses.Add(new ProfessionIn(Professions.ToFrozenSet()));
        }
        return clauses.Count == 0 ? Requirement.None : new AllOf(clauses);
    }

    /// <summary>The effects at <paramref name="ql"/>.</summary>
    public IReadOnlyList<StatEffect> EffectsAt(int ql)
    {
        ql = ClampQl(ql);
        return Effects.Select(e => new StatEffect(e.Stat, e.Kind, e.Value.At(ql))).ToArray();
    }

    /// <summary>The weapon profile at <paramref name="ql"/>, or null if this item is not a weapon.</summary>
    public WeaponProfile? WeaponAt(int ql)
    {
        if (Weapon is not { } w)
        {
            return null;
        }
        ql = ClampQl(ql);
        return new WeaponProfile(Name, w.AttackSkill, w.InitiativeSkill, w.DefenceSkill, w.DamageType,
            w.MinDamage.At(ql), w.MaxDamage.At(ql), w.AttackTimeMs, w.RangeMm);
    }

    /// <summary>A concrete item instance at <paramref name="ql"/> for the equipment rules.</summary>
    public EquippableItem Resolve(long instanceId, int ql) =>
        new(instanceId, $"{Name} (QL {ClampQl(ql)})", Slot, RequirementAt(ql), EffectsAt(ql));
}

/// <summary>Who a Protocol can be cast on.</summary>
public enum ProtocolTarget : byte
{
    Self = 1,
    /// <summary>The caster or any other player (team buffs, trade buffs).</summary>
    AnyPlayer = 2,
}

/// <summary>A named stacking family (see <see cref="ProtocolLine"/>).</summary>
public sealed record ProtocolLineDef(int Id, string Key, string Name);

/// <summary>A buff Protocol as authored; <see cref="ToDef"/> converts durations to ticks.</summary>
public sealed record ProtocolTemplate(
    int Id,
    string Key,
    string Name,
    int LineId,
    int Strength,
    int LatticeCost,
    int EnergyCost,
    int DurationMs,
    ProtocolTarget Target,
    IReadOnlyList<Profession> Professions,
    IReadOnlyDictionary<Stat, int> Requirements,
    IReadOnlyList<StatEffect> Effects,
    int RangeMm)
{
    public Requirement CastRequirement()
    {
        var clauses = Requirements.OrderBy(kv => kv.Key).Select(kv => Requirement.AtLeast(kv.Key, kv.Value)).ToList();
        if (Professions.Count > 0)
        {
            clauses.Add(new ProfessionIn(Professions.ToFrozenSet()));
        }
        return clauses.Count == 0 ? Requirement.None : new AllOf(clauses);
    }

    public ProtocolDef ToDef(int tickMs) =>
        new(Id, Name, new ProtocolLine(LineId), Strength, LatticeCost, EnergyCost,
            Math.Max(1, DurationMs / tickMs), CastRequirement(), Effects);
}

/// <summary>A creature kind.</summary>
/// <param name="Id">Stable id; also the client's model selector.</param>
/// <param name="Xp">Experience each player who fought it earns when it dies.</param>
public sealed record MobDef(
    ushort Id,
    string Key,
    string Name,
    int Level,
    IReadOnlyDictionary<Stat, int> Stats,
    WeaponProfile Attack,
    int AggroRadiusMm,
    int LeashMm,
    int SpeedMmPerSecond,
    int RespawnMs,
    int Xp,
    IReadOnlyList<LootDef> Loot,
    CreditDrop? Credits = null)
{
    public void ApplyStats(StatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        block.SetBase(Stat.Level, Level);
        foreach (var (stat, value) in Stats)
        {
            block.SetBase(stat, value);
        }
    }
}

/// <summary>Mobs of one kind spawned in a ring around a point.</summary>
public sealed record SpawnGroup(string MobKey, int Count, Position Center, int InnerRadiusMm, int OuterRadiusMm);

public sealed record ZoneDef(uint Id, string Key, string Name, ZoneBounds Bounds, IReadOnlyList<SpawnGroup> Spawns, IReadOnlyList<ZoneExit> Exits, IReadOnlyList<ShopDef> Shops);

/// <summary>
/// A shop terminal standing at <paramref name="At"/>: sells its stock at full value and buys any
/// item with a value at <see cref="EconomyDef.SellBackBp"/> of it, to players within
/// <see cref="EconomyDef.ShopRangeMm"/>.
/// </summary>
/// <param name="Id">Stable id across all zones: the wire names a shop by it. Append-only.</param>
public sealed record ShopDef(int Id, string Key, string Name, Position At, IReadOnlyList<ShopStock> Stock);

/// <summary>One item a shop always has, at a fixed QL.</summary>
public sealed record ShopStock(string ItemKey, int Ql);

/// <summary>Credits a mob drops to its looter: uniform in <paramref name="Min"/>..<paramref name="Max"/>.</summary>
public sealed record CreditDrop(int Min, int Max);

/// <summary>Rules for credits that are not about one item.</summary>
/// <param name="SellBackBp">What a shop pays for an item, in basis points of its value (the design doc: 10% or less).</param>
/// <param name="ShopRangeMm">How close a player must stand to a shop terminal to trade with it.</param>
public sealed record EconomyDef(int SellBackBp, int ShopRangeMm)
{
    /// <summary>The most credits one character can hold: far beyond play, far from overflowing a long.</summary>
    public const long MaxCredits = 1_000_000_000_000;

    /// <summary>What a shop pays for <paramref name="template"/> at <paramref name="ql"/> (rounded down), or null if it has no value.</summary>
    public long? SellPrice(ItemTemplate template, int ql)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.ValueAt(ql) is { } value ? value * SellBackBp / 10_000 : null;
    }

    /// <summary><paramref name="balance"/> plus <paramref name="amount"/>, capped at <see cref="MaxCredits"/>.</summary>
    public static long AddCredits(long balance, long amount) =>
        amount >= MaxCredits - balance ? MaxCredits : balance + amount;
}

/// <summary>
/// A way out of a zone: stepping into <paramref name="Area"/> hands the player to
/// <paramref name="ToZoneKey"/>, arriving at <paramref name="Arrive"/> there.
/// </summary>
public sealed record ZoneExit(string ToZoneKey, ZoneBounds Area, Position Arrive);

/// <summary>Stats every new character starts with, before origin and profession add theirs.</summary>
/// <param name="Stats">Common starting stats (origin and profession are applied on top).</param>
/// <param name="UnarmedKey">The weapon item used when no active weapon is worn.</param>
/// <param name="InventorySlots">How many items a character can carry besides what it wears.</param>
/// <param name="StartZoneKey">Where a new character first enters the world.</param>
/// <param name="Credits">What a new character starts with.</param>
public sealed record NewCharacterDef(IReadOnlyDictionary<Stat, int> Stats, string UnarmedKey, int InventorySlots, string StartZoneKey, long Credits);

/// <summary>
/// One possible drop. Each entry rolls on its own when the mob dies. The QL is the mob's level
/// plus or minus <paramref name="QlSpread"/>, clamped to the item's range (the design doc's
/// "loot drops at a QL near the mob's level").
/// </summary>
/// <param name="ChanceBp">Chance in basis points, 1..10000.</param>
public sealed record LootDef(string ItemKey, int ChanceBp, int QlSpread);
