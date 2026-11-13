using YamlDotNet.Serialization;

namespace Meridian.ContentCompiler;

// The authoring format: friendly units (metres, seconds), keys instead of ids for references,
// QL curves as maps. Unknown fields are errors (YamlDotNet's default), which catches typos.
#pragma warning disable CA1812 // instantiated by YamlDotNet
#pragma warning disable CA2227 // YAML DTOs need settable collections

internal sealed class ItemYaml
{
    public int Id { get; set; }

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public string Slot { get; set; } = "";

    public List<int> Ql { get; set; } = [];

    public List<string> Professions { get; set; } = [];

    public Dictionary<string, Dictionary<int, int>> Requirements { get; set; } = [];

    public List<EffectYaml> Effects { get; set; } = [];

    public WeaponYaml? Weapon { get; set; }
}

/// <summary>Exactly one of flat/percent/override. Items use QL curves; Protocols use plain numbers.</summary>
internal sealed class EffectYaml
{
    public string Stat { get; set; } = "";

    public object? Flat { get; set; }

    public object? Percent { get; set; }

    public object? Override { get; set; }
}

internal sealed class WeaponYaml
{
    public string AttackSkill { get; set; } = "";

    public string Initiative { get; set; } = "";

    public string Defence { get; set; } = "";

    public string DamageType { get; set; } = "";

    public int AttackTimeMs { get; set; }

    [YamlMember(Alias = "range_m")]
    public double RangeMetres { get; set; }

    public Dictionary<int, int> MinDamage { get; set; } = [];

    public Dictionary<int, int> MaxDamage { get; set; } = [];
}

internal sealed class LineYaml
{
    public int Id { get; set; }

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";
}

internal sealed class ProtocolYaml
{
    public int Id { get; set; }

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public string Line { get; set; } = "";

    public int Strength { get; set; }

    public int LatticeCost { get; set; }

    public int EnergyCost { get; set; }

    [YamlMember(Alias = "duration_s")]
    public double DurationSeconds { get; set; }

    public string Target { get; set; } = "";

    public List<string> Professions { get; set; } = [];

    public Dictionary<string, int> Requirements { get; set; } = [];

    public List<EffectYaml> Effects { get; set; } = [];
}

internal sealed class AttackYaml
{
    public string Name { get; set; } = "";

    public string AttackSkill { get; set; } = "";

    public string Initiative { get; set; } = "";

    public string Defence { get; set; } = "";

    public string DamageType { get; set; } = "";

    public int AttackTimeMs { get; set; }

    [YamlMember(Alias = "range_m")]
    public double RangeMetres { get; set; }

    public int MinDamage { get; set; }

    public int MaxDamage { get; set; }
}

internal sealed class MobYaml
{
    public int Id { get; set; }

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public int Level { get; set; }

    public Dictionary<string, int> Stats { get; set; } = [];

    public AttackYaml Attack { get; set; } = new();

    [YamlMember(Alias = "aggro_m")]
    public double AggroMetres { get; set; }

    [YamlMember(Alias = "leash_m")]
    public double LeashMetres { get; set; }

    [YamlMember(Alias = "speed_m_s")]
    public double SpeedMetresPerSecond { get; set; }

    [YamlMember(Alias = "respawn_s")]
    public double RespawnSeconds { get; set; }
}

internal sealed class BoundsYaml
{
    public double MinX { get; set; }

    public double MinZ { get; set; }

    public double MaxX { get; set; }

    public double MaxZ { get; set; }
}

internal sealed class PointYaml
{
    public double X { get; set; }

    public double Z { get; set; }
}

internal sealed class RingYaml
{
    public double Inner { get; set; }

    public double Outer { get; set; }
}

internal sealed class SpawnYaml
{
    public string Mob { get; set; } = "";

    public int Count { get; set; }

    [YamlMember(Alias = "center_m")]
    public PointYaml Center { get; set; } = new();

    [YamlMember(Alias = "ring_m")]
    public RingYaml Ring { get; set; } = new();
}

internal sealed class ZoneYaml
{
    public uint Id { get; set; }

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    [YamlMember(Alias = "bounds_m")]
    public BoundsYaml Bounds { get; set; } = new();

    public List<SpawnYaml> Spawns { get; set; } = [];
}

internal sealed class StartingWeaponYaml
{
    public string Item { get; set; } = "";

    public int Ql { get; set; }
}

internal sealed class NewCharacterYaml
{
    public Dictionary<string, int> Stats { get; set; } = [];

    public StartingWeaponYaml StartingWeapon { get; set; } = new();
}
