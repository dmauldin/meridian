using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meridian.Core.Characters;
using Meridian.Core.Combat;
using Meridian.Core.Content;
using Meridian.Core.Items;
using Meridian.Core.Stats;
using Meridian.Core.World;

namespace Meridian.Content;

/// <summary>Reads and writes content packs, and loads the one embedded in this assembly.</summary>
public static class ContentPacks
{
    public const int FormatVersion = 1;
    private const string ResourceName = "meridian.pack.json";

    private static readonly Lazy<ContentPack> EmbeddedPack = new(() =>
    {
        using var stream = typeof(ContentPacks).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("No embedded content pack. Run: dotnet run --project src/Meridian.ContentCompiler");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>The pack built into this assembly. Parsed once, shared by every caller.</summary>
    public static ContentPack Embedded => EmbeddedPack.Value;

    /// <summary>Parses pack JSON and validates it into a <see cref="ContentPack"/>.</summary>
    /// <exception cref="InvalidDataException">The JSON is malformed or the content is invalid.</exception>
    public static ContentPack Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        PackDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, PackJsonContext.Default.PackDto);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Content pack JSON is invalid: {e.Message}", e);
        }
        if (dto is null || dto.Format != FormatVersion)
        {
            throw new InvalidDataException($"Content pack format must be {FormatVersion}.");
        }
        return ToModel(dto, HashOf(json));
    }

    /// <summary>Serializes a pack canonically: same content, same bytes, same hash.</summary>
    public static string Serialize(PackDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return JsonSerializer.Serialize(dto, PackJsonContext.Default.PackDto).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>First 8 bytes of SHA-256 over the UTF-8 JSON, line endings normalized.</summary>
    public static ulong HashOf(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(json.ReplaceLineEndings("\n")));
        return BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }

    /// <summary>Converts a pack DTO into the validated Core model.</summary>
    public static ContentPack ToModel(PackDto dto, ulong hash)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(dto.NewCharacter);
        return new ContentPack(
            dto.Items.Select(ToModel),
            dto.Lines.Select(l => new ProtocolLineDef(l.Id, l.Key, l.Name)),
            dto.Protocols.Select(ToModel),
            dto.Mobs.Select(ToModel),
            dto.Zones.Select(ToModel),
            new NewCharacterDef(Stats(dto.NewCharacter.Stats, "new_character"), dto.NewCharacter.StartingWeapon, dto.NewCharacter.StartingWeaponQl),
            hash);
    }

    /// <summary>Converts the Core model back into its canonical DTO (sorted by id and key).</summary>
    public static PackDto ToDto(ContentPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return new PackDto(
            FormatVersion,
            pack.Items.All.Select(ToDto).ToList(),
            pack.Lines.All.Select(l => new LineDto(l.Id, l.Key, l.Name)).ToList(),
            pack.Protocols.All.Select(ToDto).ToList(),
            pack.Mobs.All.Select(ToDto).ToList(),
            pack.Zones.All.Select(ToDto).ToList(),
            new NewCharacterDto(Sorted(pack.NewCharacter.Stats), pack.NewCharacter.StartingWeaponKey, pack.NewCharacter.StartingWeaponQl));
    }

    // ------------------------------------------------------------------ DTO -> model

    /// <summary>Validates and converts one item; the compiler calls this per record for precise errors.</summary>
    public static ItemTemplate ToModel(ItemDto d)
    {
        var where = $"item '{d.Key}'";
        return new ItemTemplate(
            d.Id, d.Key, d.Name,
            Parse<EquipSlot>(d.Slot, where),
            d.MinQl, d.MaxQl,
            d.Requirements.Select(r => new ScaledRequirement(Parse<Stat>(r.Stat, where), Curve(r.Curve, where))).ToList(),
            d.Professions.Select(p => Parse<Profession>(p, where)).ToList(),
            d.Effects.Select(e => new ScaledEffect(Parse<Stat>(e.Stat, where), Parse<ModifierKind>(e.Kind, where), Curve(e.Curve, where))).ToList(),
            d.Weapon is { } w
                ? new WeaponTemplate(Parse<Stat>(w.AttackSkill, where), Parse<Stat>(w.Initiative, where), Parse<Stat>(w.Defence, where),
                    Parse<DamageType>(w.DamageType, where), w.AttackTimeMs, w.RangeMm, Curve(w.MinDamage, where), Curve(w.MaxDamage, where))
                : null);
    }

    /// <summary>Validates and converts one Protocol.</summary>
    public static ProtocolTemplate ToModel(ProtocolDto d)
    {
        var where = $"protocol '{d.Key}'";
        return new ProtocolTemplate(
            d.Id, d.Key, d.Name, d.LineId, d.Strength, d.LatticeCost, d.EnergyCost, d.DurationMs,
            Parse<ProtocolTarget>(d.Target, where),
            d.Professions.Select(p => Parse<Profession>(p, where)).ToList(),
            Stats(d.Requirements, where),
            d.Effects.Select(e => new StatEffect(Parse<Stat>(e.Stat, where), Parse<ModifierKind>(e.Kind, where), e.Value)).ToList());
    }

    /// <summary>Validates and converts one mob.</summary>
    public static MobDef ToModel(MobDto d)
    {
        var where = $"mob '{d.Key}'";
        var a = d.Attack;
        if (d.Id is < 1 or > ushort.MaxValue)
        {
            throw new InvalidDataException($"{where}: id must be 1..{ushort.MaxValue}.");
        }
        return new MobDef(
            (ushort)d.Id, d.Key, d.Name, d.Level, Stats(d.Stats, where),
            new WeaponProfile(a.Name, Parse<Stat>(a.AttackSkill, where), Parse<Stat>(a.Initiative, where), Parse<Stat>(a.Defence, where),
                Parse<DamageType>(a.DamageType, where), a.MinDamage, a.MaxDamage, a.AttackTimeMs, a.RangeMm),
            d.AggroRadiusMm, d.LeashMm, d.SpeedMmPerSecond, d.RespawnMs);
    }

    /// <summary>Converts one zone.</summary>
    public static ZoneDef ToModel(ZoneDto d) => new(
        d.Id, d.Key, d.Name,
        new ZoneBounds(d.Bounds.MinX, d.Bounds.MinZ, d.Bounds.MaxX, d.Bounds.MaxZ),
        d.Spawns.Select(s => new SpawnGroup(s.MobKey, s.Count, new Position(s.CenterX, s.CenterZ), s.InnerRadiusMm, s.OuterRadiusMm)).ToList());

    // ------------------------------------------------------------------ model -> DTO

    private static ItemDto ToDto(ItemTemplate t) => new(
        t.Id, t.Key, t.Name, t.Slot.ToString(), t.MinQl, t.MaxQl,
        t.Requirements.Select(r => new RequirementDto(r.Stat.ToString(), Anchors(r.Minimum))).ToList(),
        t.Professions.Select(p => p.ToString()).ToList(),
        t.Effects.Select(e => new ScaledEffectDto(e.Stat.ToString(), e.Kind.ToString(), Anchors(e.Value))).ToList(),
        t.Weapon is { } w
            ? new WeaponDto(w.AttackSkill.ToString(), w.InitiativeSkill.ToString(), w.DefenceSkill.ToString(), w.DamageType.ToString(),
                w.AttackTimeMs, w.RangeMm, Anchors(w.MinDamage), Anchors(w.MaxDamage))
            : null);

    private static ProtocolDto ToDto(ProtocolTemplate p) => new(
        p.Id, p.Key, p.Name, p.LineId, p.Strength, p.LatticeCost, p.EnergyCost, p.DurationMs, p.Target.ToString(),
        p.Professions.Select(x => x.ToString()).ToList(),
        Sorted(p.Requirements),
        p.Effects.Select(e => new FlatEffectDto(e.Stat.ToString(), e.Kind.ToString(), e.Value)).ToList());

    private static MobDto ToDto(MobDef m)
    {
        var a = m.Attack;
        return new MobDto(
            m.Id, m.Key, m.Name, m.Level, Sorted(m.Stats),
            new AttackDto(a.Name, a.AttackSkill.ToString(), a.InitiativeSkill.ToString(), a.DefenceSkill.ToString(), a.DamageType.ToString(),
                a.BaseAttackTimeMs, a.RangeMm, a.MinDamage, a.MaxDamage),
            m.AggroRadiusMm, m.LeashMm, m.SpeedMmPerSecond, m.RespawnMs);
    }

    private static ZoneDto ToDto(ZoneDef z) => new(
        z.Id, z.Key, z.Name,
        new BoundsDto(z.Bounds.MinX, z.Bounds.MinZ, z.Bounds.MaxX, z.Bounds.MaxZ),
        z.Spawns.Select(s => new SpawnDto(s.MobKey, s.Count, s.Center.X, s.Center.Z, s.InnerRadiusMm, s.OuterRadiusMm)).ToList());

    // ------------------------------------------------------------------ helpers

    /// <summary>Strict enum parsing: exact, defined names only (no numbers, no case folding).</summary>
    public static T Parse<T>(string text, string where)
        where T : struct, Enum
    {
        if (!string.IsNullOrEmpty(text) && !char.IsDigit(text[0]) && text[0] != '-'
            && Enum.TryParse<T>(text, ignoreCase: false, out var value) && Enum.IsDefined(value))
        {
            return value;
        }
        throw new InvalidDataException($"{where}: '{text}' is not a valid {typeof(T).Name}.");
    }

    private static QlCurve Curve(List<AnchorDto> anchors, string where)
    {
        try
        {
            return new QlCurve(anchors.Select(a => new QlAnchor(a.Ql, a.Value)).ToArray());
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"{where}: {e.Message}", e);
        }
    }

    private static List<AnchorDto> Anchors(QlCurve curve) => curve.Anchors.Select(a => new AnchorDto(a.Ql, a.Value)).ToList();

    private static Dictionary<Stat, int> Stats(IDictionary<string, int> source, string where) =>
        source.ToDictionary(kv => Parse<Stat>(kv.Key, where), kv => kv.Value);

    private static SortedDictionary<string, int> Sorted(IReadOnlyDictionary<Stat, int> stats) =>
        new(stats.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), StringComparer.Ordinal);
}
