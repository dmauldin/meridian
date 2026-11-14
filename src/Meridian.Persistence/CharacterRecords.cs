using Meridian.Core.Characters;
using Meridian.Core.Content;
using Meridian.Core.Items;

namespace Meridian.Persistence;

/// <summary>Converts between stored characters and the rules' <see cref="CharacterBuild"/>.</summary>
public static class CharacterRecords
{
    /// <summary>
    /// A new, unplaced level-1 character as content defines it, wearing its profession's kit
    /// weapon. Id and version are set by the store.
    /// </summary>
    public static CharacterRecord New(ContentPack pack, ulong accountId, string name, Profession profession, Origin origin)
    {
        var build = CharacterBuild.Create(pack, profession, origin);
        var kit = pack.Items[build.Profession.StartingWeaponKey];
        StoredItem[] items = [new(ItemLocation.Worn, (int)kit.Slot, kit.Id, kit.ClampQl(build.Profession.StartingWeaponQl))];
        int[] known = [.. build.Profession.StartingProtocols.Select(k => pack.Protocols[k].Id).Order()];
        return new CharacterRecord(0, accountId, name, profession, origin, build.Level, build.Xp, build.UnspentPoints, build.Trained(), null, 0, items, known, []);
    }

    /// <summary>Fills <paramref name="outfit"/> from the record. Returns what it could not place (see <see cref="Outfit.Load"/>).</summary>
    public static IReadOnlyList<OwnedItem> LoadItems(this CharacterRecord record, Outfit outfit)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(outfit);
        return outfit.Load(
            record.Items.Where(i => i.Location == ItemLocation.Inventory).Select(i => (i.Position, new OwnedItem(i.TemplateId, i.Ql))),
            record.Items.Where(i => i.Location == ItemLocation.Worn).Select(i => ((EquipSlot)i.Position, new OwnedItem(i.TemplateId, i.Ql))));
    }

    /// <summary>The outfit as stored rows: worn items in slot order, then the inventory in position order.</summary>
    public static IReadOnlyList<StoredItem> ItemsOf(Outfit outfit)
    {
        ArgumentNullException.ThrowIfNull(outfit);
        return
        [
            .. outfit.Worn.Select(w => new StoredItem(ItemLocation.Worn, (int)w.Slot, w.Item.TemplateId, w.Item.Ql)),
            .. outfit.Inventory.Select(i => new StoredItem(ItemLocation.Inventory, i.Position, i.Item.TemplateId, i.Item.Ql)),
        ];
    }

    /// <summary>
    /// Rebuilds the character's rules state. Returns null if content no longer defines its
    /// profession or origin (the caller refuses the login rather than guessing).
    /// </summary>
    public static CharacterBuild? ToBuild(this CharacterRecord record, ContentPack pack)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(pack);
        if (!pack.Professions.ContainsId((int)record.Profession) || !pack.Origins.ContainsId((int)record.Origin))
        {
            return null;
        }
        return CharacterBuild.Load(pack, record.Profession, record.Origin, record.Level, record.Xp, record.UnspentPoints, record.Trained);
    }

    /// <summary>The record with new progression, placement, items and Protocols; identity and version unchanged.</summary>
    public static CharacterRecord WithProgress(
        this CharacterRecord record, CharacterBuild build, Placement placement, IReadOnlyList<StoredItem> items,
        IReadOnlyList<int> knownProtocols, IReadOnlyList<SavedBuff> buffs)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(build);
        return record with
        {
            Level = build.Level,
            Xp = build.Xp,
            UnspentPoints = build.UnspentPoints,
            Trained = build.Trained(),
            Placement = placement,
            Items = items,
            KnownProtocols = knownProtocols,
            Buffs = buffs,
        };
    }
}
