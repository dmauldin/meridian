using System.Globalization;
using Meridian.Content;
using Meridian.Core.Content;
using Meridian.Core.Items;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Meridian.ContentCompiler;

/// <summary>The result of compiling a content directory: the pack JSON, or every error found.</summary>
public sealed record CompileResult(string? Json, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Json is not null && Errors.Count == 0;
}

/// <summary>
/// Turns the YAML in <c>content/</c> into a canonical pack. Converts units (metres → mm,
/// seconds → ms), resolves key references, checks everything the Core model cannot check on its
/// own, then round-trips through <see cref="ContentPacks.ToModel(PackDto, ulong)"/> so the server's own loader
/// has validated the result before it is written.
/// </summary>
public static class Compiler
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static CompileResult Compile(string contentDirectory)
    {
        var errors = new List<string>();
        var items = new List<ItemDto>();
        var lines = new List<LineDto>();
        var protocols = new List<ProtocolDto>();
        var mobs = new List<MobDto>();
        var zones = new List<ZoneDto>();
        NewCharacterDto? newCharacter = null;

        var lineYaml = Load<List<LineYaml>>(Path.Combine(contentDirectory, "protocols", "lines.yaml"), errors) ?? [];
        foreach (var l in lineYaml)
        {
            lines.Add(new LineDto(l.Id, l.Key, l.Name));
        }
        var lineIds = lineYaml.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        foreach (var file in Files(contentDirectory, "items"))
        {
            foreach (var item in Load<List<ItemYaml>>(file, errors) ?? [])
            {
                Guard(errors, file, item.Key, () => items.Add(Checked(Item(item), d => ContentPacks.ToModel(d))));
            }
        }
        foreach (var file in Files(contentDirectory, "protocols").Where(f => Path.GetFileName(f) != "lines.yaml"))
        {
            foreach (var p in Load<List<ProtocolYaml>>(file, errors) ?? [])
            {
                Guard(errors, file, p.Key, () => protocols.Add(Checked(Protocol(p, lineIds), d => ContentPacks.ToModel(d))));
            }
        }
        foreach (var file in Files(contentDirectory, "mobs"))
        {
            foreach (var m in Load<List<MobYaml>>(file, errors) ?? [])
            {
                Guard(errors, file, m.Key, () => mobs.Add(Checked(Mob(m), d => ContentPacks.ToModel(d))));
            }
        }
        foreach (var file in Files(contentDirectory, "zones"))
        {
            if (Load<ZoneYaml>(file, errors) is { } z)
            {
                Guard(errors, file, z.Key, () => zones.Add(Zone(z)));
            }
        }
        var newCharacterFile = Path.Combine(contentDirectory, "new_character.yaml");
        if (Load<NewCharacterYaml>(newCharacterFile, errors) is { } nc)
        {
            newCharacter = new NewCharacterDto(new SortedDictionary<string, int>(nc.Stats, StringComparer.Ordinal), nc.StartingWeapon.Item, nc.StartingWeapon.Ql);
        }

        if (errors.Count > 0 || newCharacter is null)
        {
            return new CompileResult(null, errors);
        }

        var dto = new PackDto(
            ContentPacks.FormatVersion,
            [.. items.OrderBy(i => i.Id)],
            [.. lines.OrderBy(l => l.Id)],
            [.. protocols.OrderBy(p => p.Id)],
            [.. mobs.OrderBy(m => m.Id)],
            [.. zones.OrderBy(z => z.Id)],
            newCharacter);

        // Validate through the same path the server uses to load, then emit canonical JSON.
        try
        {
            var pack = ContentPacks.ToModel(dto, 0);
            CheckPack(pack, errors);
            if (errors.Count > 0)
            {
                return new CompileResult(null, errors);
            }
            return new CompileResult(ContentPacks.Serialize(ContentPacks.ToDto(pack)), errors);
        }
        catch (InvalidDataException e)
        {
            errors.Add(e.Message);
            return new CompileResult(null, errors);
        }
    }

    // ------------------------------------------------------------------ per-record conversion

    private static ItemDto Item(ItemYaml y)
    {
        Require(y.Id > 0, "id must be positive");
        Require(y.Ql is [var lo, var hi] && lo >= QlCurve.MinQl && hi <= QlCurve.MaxQl && lo <= hi, $"ql must be [min, max] within {QlCurve.MinQl}–{QlCurve.MaxQl}");
        return new ItemDto(
            y.Id, y.Key, y.Name, y.Slot, y.Ql[0], y.Ql[1],
            y.Requirements.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new RequirementDto(kv.Key, Anchors(kv.Value))).ToList(),
            [.. y.Professions],
            y.Effects.Select(e => { var (kind, value) = KindAndValue(e); return new ScaledEffectDto(e.Stat, kind, CurveOf(value)); }).ToList(),
            y.Weapon is { } w
                ? new WeaponDto(w.AttackSkill, w.Initiative, w.Defence, w.DamageType, Positive(w.AttackTimeMs, "attack_time_ms"),
                    Mm(w.RangeMetres, "range_m"), Anchors(w.MinDamage), Anchors(w.MaxDamage))
                : null);
    }

    private static ProtocolDto Protocol(ProtocolYaml y, Dictionary<string, int> lineIds)
    {
        Require(y.Id > 0, "id must be positive");
        Require(lineIds.ContainsKey(y.Line), $"line '{y.Line}' is not defined in protocols/lines.yaml");
        Require(y.Strength > 0, "strength must be positive");
        Require(y.LatticeCost >= 0 && y.EnergyCost >= 0, "costs cannot be negative");
        var target = y.Target switch
        {
            "self" => nameof(ProtocolTarget.Self),
            "any_player" => nameof(ProtocolTarget.AnyPlayer),
            _ => throw new InvalidDataException($"target must be self or any_player, not '{y.Target}'"),
        };
        return new ProtocolDto(
            y.Id, y.Key, y.Name, lineIds[y.Line], y.Strength, y.LatticeCost, y.EnergyCost,
            Ms(y.DurationSeconds, "duration_s"), target, [.. y.Professions],
            new SortedDictionary<string, int>(y.Requirements, StringComparer.Ordinal),
            y.Effects.Select(e => { var (kind, value) = KindAndValue(e); return new FlatEffectDto(e.Stat, kind, Integer(value)); }).ToList());
    }

    private static MobDto Mob(MobYaml y)
    {
        Require(y.Level > 0, "level must be positive");
        Require(y.AggroMetres <= y.LeashMetres, "aggro_m cannot exceed leash_m");
        var a = y.Attack;
        Require(a.MinDamage >= 0 && a.MinDamage <= a.MaxDamage, "attack damage must satisfy 0 <= min <= max");
        return new MobDto(
            y.Id, y.Key, y.Name, y.Level, new SortedDictionary<string, int>(y.Stats, StringComparer.Ordinal),
            new AttackDto(a.Name, a.AttackSkill, a.Initiative, a.Defence, a.DamageType, Positive(a.AttackTimeMs, "attack_time_ms"),
                Mm(a.RangeMetres, "range_m"), a.MinDamage, a.MaxDamage),
            Mm(y.AggroMetres, "aggro_m", allowZero: true), Mm(y.LeashMetres, "leash_m"), Mm(y.SpeedMetresPerSecond, "speed_m_s"), Ms(y.RespawnSeconds, "respawn_s"));
    }

    private static ZoneDto Zone(ZoneYaml y)
    {
        var b = new BoundsDto(Mm(y.Bounds.MinX, "bounds_m", signed: true), Mm(y.Bounds.MinZ, "bounds_m", signed: true),
            Mm(y.Bounds.MaxX, "bounds_m", signed: true), Mm(y.Bounds.MaxZ, "bounds_m", signed: true));
        Require(b.MinX < b.MaxX && b.MinZ < b.MaxZ, "bounds_m must have min < max");
        return new ZoneDto(y.Id, y.Key, y.Name, b, y.Spawns.Select(s =>
        {
            Require(s.Count >= 0, "spawn count cannot be negative");
            Require(s.Ring.Inner <= s.Ring.Outer, "ring_m inner cannot exceed outer");
            return new SpawnDto(s.Mob, s.Count, Mm(s.Center.X, "center_m", signed: true), Mm(s.Center.Z, "center_m", signed: true),
                Mm(s.Ring.Inner, "ring_m", allowZero: true), Mm(s.Ring.Outer, "ring_m"));
        }).ToList());
    }

    /// <summary>Checks that need the whole, typed pack.</summary>
    private static void CheckPack(ContentPack pack, List<string> errors)
    {
        foreach (var item in pack.Items.All)
        {
            if (item.Weapon is { } w)
            {
                for (var ql = item.MinQl; ql <= item.MaxQl; ql++)
                {
                    if (w.MinDamage.At(ql) > w.MaxDamage.At(ql) || w.MinDamage.At(ql) < 0)
                    {
                        errors.Add($"item '{item.Key}': at QL {ql} damage is {w.MinDamage.At(ql)}–{w.MaxDamage.At(ql)}; need 0 <= min <= max.");
                        break;
                    }
                }
            }
            if (item.Slot is not (EquipSlot.MainHand or EquipSlot.OffHand or EquipSlot.TwoHand) && item.Weapon is not null)
            {
                errors.Add($"item '{item.Key}': a weapon must use a hand slot.");
            }
        }
        foreach (var zone in pack.Zones.All)
        {
            foreach (var s in zone.Spawns)
            {
                var c = s.Center;
                var b = zone.Bounds;
                if (c.X - s.OuterRadiusMm < b.MinX || c.X + s.OuterRadiusMm > b.MaxX || c.Z - s.OuterRadiusMm < b.MinZ || c.Z + s.OuterRadiusMm > b.MaxZ)
                {
                    errors.Add($"zone '{zone.Key}': spawn ring for '{s.MobKey}' extends outside the zone bounds.");
                }
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<string> Files(string root, string folder)
    {
        var dir = Path.Combine(root, folder);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.yaml").Order(StringComparer.Ordinal)
            : [];
    }

    private static T? Load<T>(string path, List<string> errors)
        where T : class
    {
        if (!File.Exists(path))
        {
            errors.Add($"{path}: file is missing.");
            return null;
        }
        try
        {
            return Yaml.Deserialize<T>(File.ReadAllText(path));
        }
        catch (YamlException e)
        {
            var inner = e.InnerException is { } i ? $" ({i.Message})" : "";
            errors.Add($"{path}:{e.Start.Line}: {e.Message}{inner}");
            return null;
        }
    }

    /// <summary>Runs the loader's own per-record validation now, so errors carry file and key.</summary>
    private static T Checked<T>(T dto, Func<T, object> validate)
    {
        validate(dto);
        return dto;
    }

    private static void Guard(List<string> errors, string file, string key, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException e)
        {
            errors.Add($"{file}: '{key}': {e.Message}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static (string Kind, object Value) KindAndValue(EffectYaml e)
    {
        var set = new (string Kind, object? Value)[] { ("Flat", e.Flat), ("Percent", e.Percent), ("Override", e.Override) }
            .Where(x => x.Value is not null).ToList();
        Require(set.Count == 1, $"effect on '{e.Stat}' needs exactly one of flat, percent, override");
        return (set[0].Kind, set[0].Value!);
    }

    /// <summary>A scalar becomes a one-anchor (constant) curve; a map is a QL curve.</summary>
    private static List<AnchorDto> CurveOf(object value) => value switch
    {
        string s => [new AnchorDto(QlCurve.MinQl, Integer(s))],
        Dictionary<object, object> map => Anchors(map.ToDictionary(kv => Integer(kv.Key), kv => Integer(kv.Value))),
        _ => throw new InvalidDataException("effect value must be a number or a QL map"),
    };

    private static List<AnchorDto> Anchors(Dictionary<int, int> map)
    {
        Require(map.Count > 0, "QL curve needs at least one anchor");
        return map.OrderBy(kv => kv.Key).Select(kv => new AnchorDto(kv.Key, kv.Value)).ToList();
    }

    private static int Integer(object value) =>
        int.TryParse(value as string ?? value.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new InvalidDataException($"'{value}' is not a whole number");

    private static int Positive(int value, string field)
    {
        Require(value > 0, $"{field} must be positive");
        return value;
    }

    private static int Mm(double metres, string field, bool allowZero = false, bool signed = false)
    {
        Require(signed || metres > 0 || (allowZero && metres == 0), $"{field} must be {(allowZero ? "zero or more" : "positive")}");
        Require(Math.Abs(metres) <= 2_000_000, $"{field} is out of range");
        return (int)Math.Round(metres * 1000, MidpointRounding.AwayFromZero);
    }

    private static int Ms(double seconds, string field)
    {
        Require(seconds > 0 && seconds <= 86_400 * 7, $"{field} must be positive and at most a week");
        return (int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
    }
}
