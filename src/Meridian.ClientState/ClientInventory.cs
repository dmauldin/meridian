using Meridian.Core.Items;

namespace Meridian.ClientState;

/// <summary>
/// The player's items as the server reported them. Read-only: the client asks the server to
/// equip or move things and waits for the next update; it never edits this itself.
/// </summary>
/// <param name="Size">Inventory positions available.</param>
/// <param name="Credits">The character's balance.</param>
public sealed record ClientInventory(
    int Size,
    IReadOnlyList<(int Position, OwnedItem Item)> Carried,
    IReadOnlyList<(EquipSlot Slot, OwnedItem Item, bool Active)> Worn,
    long Credits = 0)
{
    public OwnedItem? At(int position) =>
        Carried.FirstOrDefault(c => c.Position == position) is { Item.TemplateId: > 0 } hit ? hit.Item : null;
}
