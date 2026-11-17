using System.Collections.Frozen;
using Meridian.Core.Combat;
using Meridian.Core.Items;

namespace Meridian.Core.Content;

/// <summary>
/// All game data, validated: items, Protocols, mobs, zones. Built by the content compiler,
/// loaded identically by server and client. Lookups by numeric id (wire) and text key (content).
/// </summary>
public sealed class ContentPack
{
    public ContentPack(
        IEnumerable<ItemTemplate> items,
        IEnumerable<ProtocolLineDef> lines,
        IEnumerable<ProtocolTemplate> protocols,
        IEnumerable<MobDef> mobs,
        IEnumerable<ZoneDef> zones,
        IEnumerable<ProfessionDef> professions,
        IEnumerable<OriginDef> origins,
        ProgressionDef progression,
        NewCharacterDef newCharacter,
        EconomyDef economy,
        ulong hash)
    {
        Items = Index(items, i => i.Id, i => i.Key, "item");
        Lines = Index(lines, l => l.Id, l => l.Key, "protocol line");
        Protocols = Index(protocols, p => p.Id, p => p.Key, "protocol");
        Mobs = Index(mobs, m => m.Id, m => m.Key, "mob");
        Zones = Index(zones, z => (int)z.Id, z => z.Key, "zone");
        Professions = Index(professions, p => (int)p.Id, p => p.Key, "profession");
        Origins = Index(origins, o => (int)o.Id, o => o.Key, "origin");
        Progression = progression;
        NewCharacter = newCharacter;
        Economy = economy;
        Shops = Index(Zones.All.SelectMany(z => z.Shops), s => s.Id, s => s.Key, "shop");
        _shopZones = Zones.All.SelectMany(z => z.Shops.Select(s => (s.Id, z.Id))).ToFrozenDictionary(p => p.Item1, p => p.Item2);
        Hash = hash;
        Validate();
    }

    public Catalog<ItemTemplate> Items { get; }

    public Catalog<ProtocolLineDef> Lines { get; }

    public Catalog<ProtocolTemplate> Protocols { get; }

    public Catalog<MobDef> Mobs { get; }

    public Catalog<ZoneDef> Zones { get; }

    /// <summary>Playable professions; ids are <see cref="Characters.Profession"/> values.</summary>
    public Catalog<ProfessionDef> Professions { get; }

    /// <summary>Playable origins; ids are <see cref="Characters.Origin"/> values.</summary>
    public Catalog<OriginDef> Origins { get; }

    public ProgressionDef Progression { get; }

    public NewCharacterDef NewCharacter { get; }

    public EconomyDef Economy { get; }

    /// <summary>Every zone's shops, by their pack-wide id.</summary>
    public Catalog<ShopDef> Shops { get; }

    /// <summary>The zone a shop stands in.</summary>
    public uint ZoneOfShop(int shopId) => _shopZones[shopId];

    private readonly FrozenDictionary<int, uint> _shopZones;

    /// <summary>The weapon used when nothing active is in hand (validated when the pack loads).</summary>
    public WeaponProfile UnarmedWeapon => Items[NewCharacter.UnarmedKey].WeaponAt(1)!;

    /// <summary>Identity of this data; client and server must agree.</summary>
    public ulong Hash { get; }

    /// <summary>Cross-references that individual records cannot check on their own.</summary>
    private void Validate()
    {
        foreach (var p in Protocols.All)
        {
            if (!Lines.ContainsId(p.LineId))
            {
                throw new InvalidDataException($"protocol '{p.Key}': line {p.LineId} does not exist.");
            }
        }
        foreach (var z in Zones.All)
        {
            foreach (var s in z.Spawns)
            {
                if (!Mobs.ContainsKey(s.MobKey))
                {
                    throw new InvalidDataException($"zone '{z.Key}': spawn references unknown mob '{s.MobKey}'.");
                }
            }
        }
        if (!Items.TryGet(NewCharacter.UnarmedKey, out var unarmed) || unarmed.Weapon is null)
        {
            throw new InvalidDataException($"new_character: unarmed '{NewCharacter.UnarmedKey}' is not a weapon item.");
        }
        if (!Zones.ContainsKey(NewCharacter.StartZoneKey))
        {
            throw new InvalidDataException($"new_character: unknown start_zone '{NewCharacter.StartZoneKey}'.");
        }
        foreach (var zone in Zones.All)
        {
            foreach (var exit in zone.Exits)
            {
                if (!Zones.TryGet(exit.ToZoneKey, out var to) || to.Id == zone.Id)
                {
                    throw new InvalidDataException($"zone '{zone.Key}': exit to unknown or same zone '{exit.ToZoneKey}'.");
                }
                if (!exit.Area.Within(zone.Bounds))
                {
                    throw new InvalidDataException($"zone '{zone.Key}': exit to '{to.Key}' lies outside the zone.");
                }
                // Arriving inside an exit would bounce the player straight back.
                if (!to.Bounds.Contains(exit.Arrive) || to.Exits.Any(e => e.Area.Contains(exit.Arrive)))
                {
                    throw new InvalidDataException($"zone '{zone.Key}': arrival in '{to.Key}' must be inside it and outside its exits.");
                }
            }
        }
        if (NewCharacter.InventorySlots is < 1 or > 120)
        {
            throw new InvalidDataException("new_character: inventory_slots must be 1..120.");
        }
        if (NewCharacter.Credits is < 0 or > EconomyDef.MaxCredits)
        {
            throw new InvalidDataException($"new_character: credits must be 0..{EconomyDef.MaxCredits}.");
        }
        if (Economy.SellBackBp is < 0 or > 10_000 || Economy.ShopRangeMm is < 1_000 or > 50_000)
        {
            throw new InvalidDataException("economy: sell_back_bp must be 0..10000 and shop_range_m 1..50.");
        }
        foreach (var zone in Zones.All)
        {
            foreach (var shop in zone.Shops)
            {
                if (!zone.Bounds.Contains(shop.At) || zone.Exits.Any(e => e.Area.Contains(shop.At)))
                {
                    throw new InvalidDataException($"zone '{zone.Key}': shop '{shop.Key}' must stand inside the zone and off its exits.");
                }
                if (shop.Stock.Count is 0 or > 64)
                {
                    throw new InvalidDataException($"shop '{shop.Key}': stock must list 1..64 items.");
                }
                foreach (var s in shop.Stock)
                {
                    if (!Items.TryGet(s.ItemKey, out var t) || t.Value is null || s.Ql != t.ClampQl(s.Ql))
                    {
                        throw new InvalidDataException($"shop '{shop.Key}': '{s.ItemKey}' QL {s.Ql} is unknown, has no value or is outside its QL range.");
                    }
                }
            }
        }
        foreach (var m in Mobs.All)
        {
            if (m.Credits is { } c && (c.Min < 0 || c.Max < c.Min || c.Max > 1_000_000))
            {
                throw new InvalidDataException($"mob '{m.Key}': credits need 0 <= min <= max <= 1000000.");
            }
            foreach (var l in m.Loot)
            {
                if (!Items.ContainsKey(l.ItemKey))
                {
                    throw new InvalidDataException($"mob '{m.Key}': loot references unknown item '{l.ItemKey}'.");
                }
                if (l.ChanceBp is < 1 or > 10_000 || l.QlSpread is < 0 or > QlCurve.MaxQl)
                {
                    throw new InvalidDataException($"mob '{m.Key}': loot '{l.ItemKey}' needs chance 1..10000 bp and a spread 0..{QlCurve.MaxQl}.");
                }
            }
        }
        foreach (var item in Items.All)
        {
            if (item.Value is { } value && Enumerable.Range(item.MinQl, item.MaxQl - item.MinQl + 1).Any(ql => value.At(ql) < 1))
            {
                throw new InvalidDataException($"item '{item.Key}': value must be at least 1 credit at every QL.");
            }
            if (item.IsProtocol != (item.Slot == EquipSlot.None))
            {
                throw new InvalidDataException($"item '{item.Key}': an item has a slot or a protocol, never both or neither.");
            }
            if (item.ProtocolKey is { } key && !Protocols.ContainsKey(key))
            {
                throw new InvalidDataException($"item '{item.Key}': unknown protocol '{key}'.");
            }
        }
        foreach (var p in Protocols.All)
        {
            if (p.RangeMm is < 1_000 or > 100_000)
            {
                throw new InvalidDataException($"protocol '{p.Key}': range_m must be 1..100.");
            }
        }
        foreach (var p in Professions.All)
        {
            foreach (var key in p.StartingProtocols)
            {
                if (!Protocols.TryGet(key, out var proto) || (proto.Professions.Count > 0 && !proto.Professions.Contains(p.Id)))
                {
                    throw new InvalidDataException($"profession '{p.Key}': starting protocol '{key}' is unknown or not castable by it.");
                }
            }
        }
        foreach (var p in Professions.All)
        {
            if (!Items.TryGet(p.StartingWeaponKey, out var weapon) || weapon.Weapon is null)
            {
                throw new InvalidDataException($"profession '{p.Key}': starting weapon '{p.StartingWeaponKey}' is not a weapon item.");
            }
            if (p.GroupTiers.ContainsKey(Characters.SkillGroup.Attribute))
            {
                throw new InvalidDataException($"profession '{p.Key}': attributes are priced by origin, not profession.");
            }
            foreach (var stat in p.SkillTiers.Keys.Concat(p.StartingSkills.Keys))
            {
                if (!Characters.Skills.IsTrainable(stat) || Characters.Skills.GroupOf(stat) == Characters.SkillGroup.Attribute)
                {
                    throw new InvalidDataException($"profession '{p.Key}': {stat} is not a profession skill.");
                }
            }
        }
        foreach (var o in Origins.All)
        {
            foreach (var stat in o.Attributes.Keys.Concat(o.AttributeTiers.Keys))
            {
                if (!Characters.Skills.IsTrainable(stat) || Characters.Skills.GroupOf(stat) != Characters.SkillGroup.Attribute)
                {
                    throw new InvalidDataException($"origin '{o.Key}': {stat} is not an attribute.");
                }
            }
        }
        foreach (var tier in new[] { Characters.CostTier.Cheap, Characters.CostTier.Normal, Characters.CostTier.Expensive })
        {
            if (!Progression.Tiers.TryGetValue(tier, out var rule) || rule.CostPerPoint <= 0 || rule.CapPerLevel <= 0)
            {
                throw new InvalidDataException($"progression: tier {tier} needs a positive cost and cap.");
            }
        }
        if (Progression.Tiers.ContainsKey(Characters.CostTier.Locked))
        {
            throw new InvalidDataException("progression: the Locked tier cannot have a rule.");
        }
        if (Progression.MaxLevel is < 2 or > QlCurve.MaxQl)
        {
            throw new InvalidDataException($"progression: max_level must be 2..{QlCurve.MaxQl}.");
        }
    }

    private static Catalog<T> Index<T>(IEnumerable<T> source, Func<T, int> id, Func<T, string> key, string what)
        where T : class
    {
        var byId = new Dictionary<int, T>();
        var byKey = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (!byId.TryAdd(id(item), item))
            {
                throw new InvalidDataException($"Duplicate {what} id {id(item)}.");
            }
            if (!byKey.TryAdd(key(item), item))
            {
                throw new InvalidDataException($"Duplicate {what} key '{key(item)}'.");
            }
        }
        return new Catalog<T>(byId.ToFrozenDictionary(), byKey.ToFrozenDictionary(StringComparer.Ordinal));
    }
}

/// <summary>Read-only lookup of one kind of content by id and by key.</summary>
public sealed class Catalog<T>(FrozenDictionary<int, T> byId, FrozenDictionary<string, T> byKey)
    where T : class
{
    /// <summary>Every entry, ordered by id for deterministic iteration.</summary>
    public IReadOnlyList<T> All { get; } = byId.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();

    public int Count => byId.Count;

    public T this[int id] => byId.TryGetValue(id, out var v) ? v : throw new KeyNotFoundException($"No {typeof(T).Name} with id {id}.");

    public T this[string key] => byKey.TryGetValue(key, out var v) ? v : throw new KeyNotFoundException($"No {typeof(T).Name} with key '{key}'.");

    public bool ContainsId(int id) => byId.ContainsKey(id);

    public bool ContainsKey(string key) => byKey.ContainsKey(key);

    public bool TryGet(int id, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value) => byId.TryGetValue(id, out value);

    public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value) => byKey.TryGetValue(key, out value);
}
