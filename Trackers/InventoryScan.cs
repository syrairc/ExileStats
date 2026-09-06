using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace ExileStats;

/// <summary>Cheap main-inventory lookups that don't warrant their own tracker state. Used by the statistics
/// overlay's "no omen" warning.</summary>
public static class InventoryScan
{
    // the backpack. enum compare, no per-holder ToString
    public static ServerInventory MainInventory(GameController gc)
    {
        try
        {
            var holders = gc?.IngameState?.ServerData?.PlayerInventories;
            if (holders == null) return null;
            foreach (var h in holders)
                if (h != null && h.TypeId == InventoryNameE.MainInventory1)
                    return h.Inventory;
        }
        catch { /* server data not readable this tick */ }
        return null;
    }

    /// <summary>True when the backpack holds an item whose base name matches <paramref name="baseName"/>.
    /// Never throws; an unreadable inventory reads as "not found".</summary>
    public static bool HasBaseItem(GameController gc, string baseName)
    {
        try
        {
            var slots = MainInventory(gc)?.InventorySlotItems;
            if (slots == null) return false;

            foreach (var slot in slots)
            {
                var item = slot?.Item;
                if (item == null || string.IsNullOrEmpty(item.Path)) continue;
                var bt = gc.Files.BaseItemTypes.Translate(item.Path);
                if (bt != null && string.Equals(bt.BaseName, baseName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* inventory not readable this tick */ }
        return false;
    }
}
