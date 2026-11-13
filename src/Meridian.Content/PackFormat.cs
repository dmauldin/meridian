using System.Text.Json.Serialization;

namespace Meridian.Content;

// The compiled pack's JSON shape: primitives only, sorted, so the same data always produces the
// same bytes (and the same hash). Distances in millimetres, times in milliseconds.

public sealed record PackDto(
    int Format,
    List<ItemDto> Items,
    List<LineDto> Lines,
    List<ProtocolDto> Protocols,
    List<MobDto> Mobs,
    List<ZoneDto> Zones,
    NewCharacterDto NewCharacter);

public sealed record AnchorDto(int Ql, int Value);

public sealed record RequirementDto(string Stat, List<AnchorDto> Curve);

public sealed record ScaledEffectDto(string Stat, string Kind, List<AnchorDto> Curve);

public sealed record WeaponDto(
    string AttackSkill,
    string Initiative,
    string Defence,
    string DamageType,
    int AttackTimeMs,
    int RangeMm,
    List<AnchorDto> MinDamage,
    List<AnchorDto> MaxDamage);

public sealed record ItemDto(
    int Id,
    string Key,
    string Name,
    string Slot,
    int MinQl,
    int MaxQl,
    List<RequirementDto> Requirements,
    List<string> Professions,
    List<ScaledEffectDto> Effects,
    WeaponDto? Weapon);

public sealed record LineDto(int Id, string Key, string Name);

public sealed record FlatEffectDto(string Stat, string Kind, int Value);

public sealed record ProtocolDto(
    int Id,
    string Key,
    string Name,
    int LineId,
    int Strength,
    int LatticeCost,
    int EnergyCost,
    int DurationMs,
    string Target,
    List<string> Professions,
    SortedDictionary<string, int> Requirements,
    List<FlatEffectDto> Effects);

public sealed record AttackDto(
    string Name,
    string AttackSkill,
    string Initiative,
    string Defence,
    string DamageType,
    int AttackTimeMs,
    int RangeMm,
    int MinDamage,
    int MaxDamage);

public sealed record MobDto(
    int Id,
    string Key,
    string Name,
    int Level,
    SortedDictionary<string, int> Stats,
    AttackDto Attack,
    int AggroRadiusMm,
    int LeashMm,
    int SpeedMmPerSecond,
    int RespawnMs);

public sealed record BoundsDto(int MinX, int MinZ, int MaxX, int MaxZ);

public sealed record SpawnDto(string MobKey, int Count, int CenterX, int CenterZ, int InnerRadiusMm, int OuterRadiusMm);

public sealed record ZoneDto(uint Id, string Key, string Name, BoundsDto Bounds, List<SpawnDto> Spawns);

public sealed record NewCharacterDto(SortedDictionary<string, int> Stats, string StartingWeapon, int StartingWeaponQl);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PackDto))]
internal sealed partial class PackJsonContext : JsonSerializerContext;
