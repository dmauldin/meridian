using System.Collections.Frozen;

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
        NewCharacterDef newCharacter,
        ulong hash)
    {
        Items = Index(items, i => i.Id, i => i.Key, "item");
        Lines = Index(lines, l => l.Id, l => l.Key, "protocol line");
        Protocols = Index(protocols, p => p.Id, p => p.Key, "protocol");
        Mobs = Index(mobs, m => m.Id, m => m.Key, "mob");
        Zones = Index(zones, z => (int)z.Id, z => z.Key, "zone");
        NewCharacter = newCharacter;
        Hash = hash;
        Validate();
    }

    public Catalog<ItemTemplate> Items { get; }

    public Catalog<ProtocolLineDef> Lines { get; }

    public Catalog<ProtocolTemplate> Protocols { get; }

    public Catalog<MobDef> Mobs { get; }

    public Catalog<ZoneDef> Zones { get; }

    public NewCharacterDef NewCharacter { get; }

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
        if (!Items.TryGet(NewCharacter.StartingWeaponKey, out var weapon) || weapon.Weapon is null)
        {
            throw new InvalidDataException($"new_character: starting weapon '{NewCharacter.StartingWeaponKey}' is not a weapon item.");
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
