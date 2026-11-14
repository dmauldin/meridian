using Meridian.Core.Characters;
using Meridian.Core.Combat;
using Meridian.Core.Content;
using Meridian.Core.Requirements;

namespace Meridian.Core.Items;

/// <summary>An item a character owns: a template at a quality level. Where it sits is its identity for now.</summary>
public readonly record struct OwnedItem(int TemplateId, int Ql);

public enum ItemResult : byte
{
    Done = 0,

    /// <summary>The inventory position is empty or out of range.</summary>
    NoItemThere = 1,

    /// <summary>Nothing is worn in that slot.</summary>
    NothingWorn = 2,

    /// <summary>Content does not define the item, or it cannot be worn.</summary>
    NotEquippable = 3,

    /// <summary>No free inventory space for what would come off.</summary>
    InventoryFull = 4,

    RequirementsNotMet = 5,
}

/// <summary>
/// A character's inventory and worn equipment. Pure rules shared by the zone and the client.
/// <list type="bullet">
/// <item>Equipping swaps: whatever the new item displaces (the slot's item, or both hands for a
/// two-hander) goes back into the inventory, starting where the new item came from.</item>
/// <item>The requirement is checked without the displaced items' bonuses, so an item cannot
/// lean on the item it replaces.</item>
/// <item>Worn items follow <see cref="Equipment"/>'s twinking rules: an item whose requirement
/// later fails stays worn but inactive.</item>
/// </list>
/// Every change bumps <see cref="Revision"/>, so callers can tell when to resend or save.
/// </summary>
public sealed class Outfit
{
    private readonly ContentPack _pack;
    private readonly OwnedItem?[] _inventory;
    private readonly Equipment _equipment;
    private readonly Dictionary<EquipSlot, OwnedItem> _worn = [];
    private readonly Dictionary<EquipSlot, bool> _lastActive = [];
    private long _nextInstanceId = 1;

    public Outfit(ContentPack pack, Character owner, int inventorySize)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentOutOfRangeException.ThrowIfLessThan(inventorySize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inventorySize, byte.MaxValue);
        _pack = pack;
        _inventory = new OwnedItem?[inventorySize];
        _equipment = new Equipment(owner);
    }

    public long Revision { get; private set; }

    public int InventorySize => _inventory.Length;

    public OwnedItem? InventoryAt(int position) => position >= 0 && position < _inventory.Length ? _inventory[position] : null;

    /// <summary>Occupied inventory positions in order.</summary>
    public IEnumerable<(int Position, OwnedItem Item)> Inventory
    {
        get
        {
            for (var i = 0; i < _inventory.Length; i++)
            {
                if (_inventory[i] is { } item)
                {
                    yield return (i, item);
                }
            }
        }
    }

    /// <summary>Worn items in slot order, with whether each is active.</summary>
    public IEnumerable<(EquipSlot Slot, OwnedItem Item, bool Active)> Worn =>
        _worn.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value, _equipment.IsActive(kv.Key)));

    public bool IsWorn(EquipSlot slot) => _worn.ContainsKey(slot);

    public bool IsActive(EquipSlot slot) => _equipment.IsActive(slot);

    public int FreeSlots => _inventory.Count(i => i is null);

    /// <summary>The active worn weapon (two-hander first, then main hand), or null.</summary>
    public WeaponProfile? ActiveWeapon
    {
        get
        {
            foreach (var slot in (ReadOnlySpan<EquipSlot>)[EquipSlot.TwoHand, EquipSlot.MainHand])
            {
                if (_worn.TryGetValue(slot, out var item) && _equipment.IsActive(slot)
                    && _pack.Items.TryGet(item.TemplateId, out var t) && t.WeaponAt(item.Ql) is { } weapon)
                {
                    return weapon;
                }
            }
            return null;
        }
    }

    /// <summary>Puts an item in the first free position. Returns the position, or -1 if full.</summary>
    public int Add(OwnedItem item)
    {
        var free = Array.IndexOf(_inventory, null);
        if (free < 0)
        {
            return -1;
        }
        _inventory[free] = Clamp(item);
        Revision++;
        return free;
    }

    /// <summary>Removes and returns the item at <paramref name="position"/>.</summary>
    public OwnedItem? Discard(int position)
    {
        if (InventoryAt(position) is not { } item)
        {
            return null;
        }
        _inventory[position] = null;
        Revision++;
        return item;
    }

    public ItemResult Equip(int position, List<RequirementFailure>? failures = null)
    {
        if (InventoryAt(position) is not { } item)
        {
            return ItemResult.NoItemThere;
        }
        if (!_pack.Items.TryGet(item.TemplateId, out var template))
        {
            return ItemResult.NotEquippable;
        }
        var displaced = _equipment.Conflicts(template.Slot);
        // The new item frees its own position; anything beyond the first displaced item needs more.
        if (displaced.Count - 1 > FreeSlots)
        {
            return ItemResult.InventoryFull;
        }

        var removed = new List<(EquipSlot Slot, OwnedItem Item, EquippableItem Resolved)>(displaced.Count);
        foreach (var slot in displaced)
        {
            removed.Add((slot, _worn[slot], _equipment.Unequip(slot)!));
            _worn.Remove(slot);
        }

        var resolved = template.Resolve(_nextInstanceId++, item.Ql);
        if (_equipment.TryEquip(resolved, failures) != EquipResult.Equipped)
        {
            foreach (var (slot, owned, back) in removed)
            {
                _equipment.Wear(back); // put back exactly as it was (inactive stays inactive)
                _worn[slot] = owned;
            }
            return ItemResult.RequirementsNotMet;
        }

        _worn[template.Slot] = item;
        _inventory[position] = null;
        var next = position;
        foreach (var (_, owned, _) in removed)
        {
            while (_inventory[next] is not null)
            {
                next = (next + 1) % _inventory.Length;
            }
            _inventory[next] = owned;
        }
        Revision++;
        return ItemResult.Done;
    }

    public ItemResult Unequip(EquipSlot slot)
    {
        if (!_worn.TryGetValue(slot, out var item))
        {
            return ItemResult.NothingWorn;
        }
        var free = Array.IndexOf(_inventory, null);
        if (free < 0)
        {
            return ItemResult.InventoryFull;
        }
        _equipment.Unequip(slot);
        _worn.Remove(slot);
        _inventory[free] = item;
        Revision++;
        return ItemResult.Done;
    }

    /// <summary>
    /// Settles worn items after stats changed (a buff landed or ran out). Returns the slots that
    /// flipped, with their new state, so the caller can tell the player.
    /// </summary>
    public IReadOnlyList<(EquipSlot Slot, bool Active)> Reevaluate()
    {
        _equipment.Reevaluate();
        List<(EquipSlot, bool)>? flipped = null;
        foreach (var slot in _worn.Keys.Order())
        {
            var active = _equipment.IsActive(slot);
            if (_lastActive.TryGetValue(slot, out var was) && was != active)
            {
                (flipped ??= []).Add((slot, active));
            }
            _lastActive[slot] = active;
        }
        foreach (var gone in _lastActive.Keys.Where(s => !_worn.ContainsKey(s)).ToList())
        {
            _lastActive.Remove(gone);
        }
        if (flipped is null)
        {
            return [];
        }
        Revision++;
        return flipped;
    }

    /// <summary>
    /// Restores saved items. Items content no longer defines, and worn items that no longer fit
    /// their slot, are skipped (and returned) rather than trusted. Worn items come back without a
    /// requirement check: one twinked with a buff that has gone is worn but inactive.
    /// </summary>
    public IReadOnlyList<OwnedItem> Load(IEnumerable<(int Position, OwnedItem Item)> inventory, IEnumerable<(EquipSlot Slot, OwnedItem Item)> worn)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(worn);
        var skipped = new List<OwnedItem>();
        foreach (var (slot, item) in worn.OrderBy(w => w.Slot))
        {
            if (!_pack.Items.TryGet(item.TemplateId, out var t) || t.Slot != slot || _equipment.Conflicts(slot).Count > 0)
            {
                skipped.Add(item);
                continue;
            }
            var owned = Clamp(item);
            _equipment.Wear(t.Resolve(_nextInstanceId++, owned.Ql));
            _worn[slot] = owned;
        }
        foreach (var (position, item) in inventory)
        {
            if (!_pack.Items.ContainsId(item.TemplateId) || position < 0 || position >= _inventory.Length || _inventory[position] is not null)
            {
                skipped.Add(item);
                continue;
            }
            _inventory[position] = Clamp(item);
        }
        Reevaluate();
        Revision++;
        return skipped;
    }

    private OwnedItem Clamp(OwnedItem item) =>
        _pack.Items.TryGet(item.TemplateId, out var t) ? item with { Ql = t.ClampQl(item.Ql) } : item;
}
