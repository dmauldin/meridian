using Meridian.Core.Characters;
using Meridian.Core.Requirements;
using Meridian.Core.Stats;

namespace Meridian.Core.Items;

/// <summary>Where an item is worn. Implant slots are listed separately from gear.</summary>
public enum EquipSlot : byte
{
    MainHand = 1,
    OffHand = 2,
    TwoHand = 3,
    Head = 10,
    Body = 11,
    Arms = 12,
    Hands = 13,
    Legs = 14,
    Feet = 15,
    Back = 16,
    ImplantEye = 40,
    ImplantHead = 41,
    ImplantEar = 42,
    ImplantChest = 43,
    ImplantWaist = 44,
    ImplantLegs = 45,
    ImplantFeet = 46,
    ImplantRightArm = 47,
    ImplantLeftArm = 48,
    ImplantRightWrist = 49,
    ImplantLeftWrist = 50,
    ImplantRightHand = 51,
    ImplantLeftHand = 52,
}

/// <summary>An item instance resolved at its QL: what equipment rules need to know.</summary>
public sealed record EquippableItem(
    long InstanceId,
    string Name,
    EquipSlot Slot,
    Requirement Requirement,
    IReadOnlyList<StatEffect> Effects)
{
    public ModifierSource Source => new(SourceKind.Item, InstanceId);
}

public enum EquipResult : byte
{
    Equipped,
    SlotOccupied,
    RequirementsNotMet,
}

/// <summary>
/// Worn items, with the twinking rules from the design doc:
/// <list type="bullet">
/// <item>Requirements are checked when equipping, counting buffs and other items.</item>
/// <item>An item's own bonuses never count toward its own requirement.</item>
/// <item>If a requirement later fails, the item stays worn but goes <em>inactive</em> (its
/// bonuses stop) until it is met again. <see cref="Reevaluate"/> settles this.</item>
/// </list>
/// </summary>
public sealed class Equipment
{
    private readonly Character _owner;
    private readonly SortedDictionary<EquipSlot, Worn> _worn = [];

    public Equipment(Character owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public IEnumerable<(EquipSlot Slot, EquippableItem Item, bool Active)> Items =>
        _worn.Select(kv => (kv.Key, kv.Value.Item, kv.Value.Active));

    public bool IsActive(EquipSlot slot) => _worn.TryGetValue(slot, out var w) && w.Active;

    public EquippableItem? In(EquipSlot slot) => _worn.TryGetValue(slot, out var w) ? w.Item : null;

    /// <summary>
    /// Worn slots an item for <paramref name="slot"/> would displace: the slot itself, and the
    /// hands for a two-handed item (or the two-hander for a one-handed item).
    /// </summary>
    public IReadOnlyList<EquipSlot> Conflicts(EquipSlot slot)
    {
        var conflicts = new List<EquipSlot>(2);
        foreach (var worn in _worn.Keys)
        {
            if (worn == slot || IsHandConflict(slot, worn))
            {
                conflicts.Add(worn);
            }
        }
        return conflicts;
    }

    /// <summary>
    /// Wears an item without checking its requirement, then settles every item. Used to restore
    /// saved equipment: an item twinked with a buff that has since gone comes back worn but
    /// inactive, rather than being refused. The slot (and any conflict) must be free.
    /// </summary>
    public void Wear(EquippableItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Conflicts(item.Slot).Count > 0)
        {
            throw new InvalidOperationException($"{item.Slot} is occupied.");
        }
        _worn.Add(item.Slot, new Worn(item) { Active = true });
        _owner.Stats.AddEffects(item.Effects, item.Source);
        Reevaluate();
    }

    public EquipResult TryEquip(EquippableItem item, List<RequirementFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Conflicts(item.Slot).Count > 0)
        {
            return EquipResult.SlotOccupied;
        }
        if (!item.Requirement.Evaluate(_owner, failures))
        {
            return EquipResult.RequirementsNotMet;
        }
        _worn.Add(item.Slot, new Worn(item) { Active = true });
        _owner.Stats.AddEffects(item.Effects, item.Source);
        Reevaluate();
        return EquipResult.Equipped;
    }

    public EquippableItem? Unequip(EquipSlot slot)
    {
        if (!_worn.Remove(slot, out var w))
        {
            return null;
        }
        _owner.Stats.RemoveSource(w.Item.Source);
        Reevaluate();
        return w.Item;
    }

    /// <summary>
    /// Re-checks every worn item until nothing changes: deactivates items whose requirement
    /// now fails and reactivates items whose requirement holds again. Call after anything that
    /// can lower stats (a buff expiring, a debuff landing). Visits slots in a fixed order, so
    /// the result is deterministic.
    /// </summary>
    /// <returns>How many items changed state.</returns>
    public int Reevaluate()
    {
        var changes = 0;
        // Each pass changes at least one item or stops. Bounding passes guarantees termination
        // even with content that has negative bonuses (which could otherwise oscillate).
        var maxPasses = (2 * _worn.Count) + 1;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            var changedThisPass = false;
            foreach (var w in _worn.Values)
            {
                var view = new ExcludingView(_owner, w.Item.Source);
                var ok = w.Item.Requirement.Evaluate(view);
                if (w.Active && !ok)
                {
                    w.Active = false;
                    _owner.Stats.RemoveSource(w.Item.Source);
                    changedThisPass = true;
                    changes++;
                }
                else if (!w.Active && ok)
                {
                    w.Active = true;
                    _owner.Stats.AddEffects(w.Item.Effects, w.Item.Source);
                    changedThisPass = true;
                    changes++;
                }
            }
            if (!changedThisPass)
            {
                return changes;
            }
        }
        return changes;
    }

    private static bool IsHandConflict(EquipSlot a, EquipSlot b) =>
        (a == EquipSlot.TwoHand && b is EquipSlot.MainHand or EquipSlot.OffHand)
        || (b == EquipSlot.TwoHand && a is EquipSlot.MainHand or EquipSlot.OffHand);

    private sealed class Worn(EquippableItem item)
    {
        public EquippableItem Item { get; } = item;

        public bool Active { get; set; }
    }

    /// <summary>A view of the owner with one source's modifiers ignored.</summary>
    private sealed class ExcludingView(Character owner, ModifierSource excluded) : ICharacterView
    {
        public Profession Profession => owner.Profession;

        public Origin Origin => owner.Origin;

        public int Get(Stat stat) => owner.Stats.GetExcluding(stat, excluded);
    }
}
