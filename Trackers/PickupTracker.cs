using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory.Components;

namespace ExileStats;

/// <summary>
/// Detects items picked up into the player's main inventory during a map run. Each Tick it reads the
/// backpack's slot grid and diffs it against the previous Tick, keyed by inventory <b>slot position</b>
/// (column,row): a slot that became occupied (or now holds a different item) is a pickup of its full stack;
/// a slot whose item kept the same path but whose <c>Stack.Size</c> grew is a partial pickup (e.g. currency
/// merging onto a stack already in the bag) and only the delta is logged.
///
/// Identity is the slot, NOT <c>Entity.Id</c>: the Id of an inventory item entity rotates tick-to-tick
/// (same unreliable-identity issue as ground <c>WorldItem</c>s), so an Id-keyed diff re-logged every
/// already-held item as a fake pickup, every tick. Slot position is stable while an item sits in place.
///
/// The first scan after entering an area only seeds the baseline (so items already in the bag at entry are
/// not logged). Caveats: manually moving/splitting a stack into a new slot reads as a pickup there; crafting
/// output also counts. Both are rare during normal mapping.
/// </summary>
public class PickupTracker
{
    // (col,row) -> (item path, stack size). Slot position is stable across ticks. Two dicts are double-
    // buffered (fill _curSlots, swap with _prevSlots) so a tick allocates no dictionary and copies nothing.
    private Dictionary<(int, int), (string Path, int Size)> _prevSlots = new();
    private Dictionary<(int, int), (string Path, int Size)> _curSlots = new();
    private int _zoneSwitchId;
    private bool _seeded;

    /// <summary>Reset for a freshly-entered area. The next <see cref="Scan"/> seeds the baseline without
    /// logging.</summary>
    public void SetArea(int zoneSwitchId)
    {
        _prevSlots.Clear();
        _curSlots.Clear();
        _zoneSwitchId = zoneSwitchId;
        _seeded = false;
    }

    /// <summary>Diff the main inventory against the previous Tick and return new pickups (already folded
    /// into the baseline).</summary>
    public List<PickupItem> Scan(GameController gc, DateTime enteredAt)
    {
        var picks = new List<PickupItem>();

        var holder = gc.IngameState.ServerData.PlayerInventories
            ?.FirstOrDefault(h => h?.TypeId.ToString() == "MainInventory1");
        var slots = holder?.Inventory?.InventorySlotItems;
        if (slots == null)
            return picks;

        var elapsed = Math.Round((DateTime.UtcNow - enteredAt.ToUniversalTime()).TotalSeconds, 1);
        var playerPos = gc.Player?.GridPos ?? default;

        // Current slot occupancy, rebuilt into the reused _curSlots each tick (a vacated slot just drops out).
        _curSlots.Clear();
        foreach (var slot in slots)
        {
            var item = slot?.Item;
            if (item == null || string.IsNullOrEmpty(item.Path))
                continue;

            var key = ((int)slot.PosX, (int)slot.PosY);
            var size = item.TryGetComponent<Stack>(out var stack) ? stack.Size : 1;
            _curSlots[key] = (item.Path, size);

            // Don't log on the seeding pass — items already in the bag at entry aren't pickups.
            if (!_seeded)
                continue;

            int gained;
            if (!_prevSlots.TryGetValue(key, out var prev))
                gained = size;                   // slot was empty -> an item landed here
            else if (prev.Path != item.Path)
                gained = size;                   // a different item now occupies this slot
            else if (size > prev.Size)
                gained = size - prev.Size;       // same stack grew
            else
                continue;                        // unchanged (or shrank) -> not a pickup

            var pick = ItemRecord.Read<PickupItem>(item, gc);
            // ItemRecord.Read prices the whole entity stack; a merge-pickup only gained part of it, so
            // scale to the gained amount (perUnit * gained). Full-stack pickups (gained == StackSize) are
            // unchanged.
            if (pick.ChaosValue is { } full && pick.StackSize > 0 && gained != pick.StackSize)
                pick.ChaosValue = full / pick.StackSize * gained;
            pick.PickedUpAt = DateTime.Now;
            pick.ElapsedSeconds = elapsed;
            pick.ZoneSwitchId = _zoneSwitchId;
            pick.StackCount = gained;
            pick.PlayerGridX = playerPos.X;
            pick.PlayerGridY = playerPos.Y;
            picks.Add(pick);
        }

        // Make the current snapshot the new baseline by swapping buffers; next tick clears + refills the old
        // baseline as _curSlots. No allocation, no copy.
        (_prevSlots, _curSlots) = (_curSlots, _prevSlots);
        _seeded = true;

        return picks;
    }
}
