using System;
using System.Collections.Generic;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;

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
    // (col,row) -> (path, size). _curSlots is refilled each scan and folded into the _prevSlots baseline
    private readonly Dictionary<(int, int), (string Path, int Size)> _prevSlots = new();
    private readonly Dictionary<(int, int), (string Path, int Size)> _curSlots = new();
    private readonly Dictionary<(int, int), Entity> _itemsBySlot = new();
    // slots that read absent or smaller last scan. a mid-refresh item reads size 1 or no path for a tick,
    // and believing it made the next good read a fake whole-stack pickup. shrink needs two agreeing scans
    private readonly HashSet<(int, int)> _shrunkOnce = new();
    private readonly List<(int, int)> _gone = new();
    private int _zoneSwitchId;
    private bool _seeded;

    /// <summary>Reset for a freshly-entered area. The next <see cref="Scan"/> seeds the baseline without
    /// logging.</summary>
    public void SetArea(int zoneSwitchId)
    {
        _prevSlots.Clear();
        _curSlots.Clear();
        _shrunkOnce.Clear();
        _zoneSwitchId = zoneSwitchId;
        _seeded = false;
    }

    /// <summary>Diff the main inventory against the previous Tick and return new pickups (already folded
    /// into the baseline).</summary>
    public List<PickupItem> Scan(GameController gc, DateTime enteredAt)
    {
        var picks = new List<PickupItem>();
        var slots = InventoryScan.MainInventory(gc)?.InventorySlotItems;
        if (slots == null)
            return picks;

        var elapsed = Math.Round((DateTime.UtcNow - enteredAt.ToUniversalTime()).TotalSeconds, 1);
        var playerPos = gc.Player?.GridPos ?? default;

        // own pass first: InventorySlotItems can yield a slot twice per tick, the dict collapses repeats
        _curSlots.Clear();
        _itemsBySlot.Clear();
        foreach (var slot in slots)
        {
            var item = slot?.Item;
            if (item == null || string.IsNullOrEmpty(item.Path))
                continue;
            var key = ((int)slot.PosX, (int)slot.PosY);
            var size = item.TryGetComponent<Stack>(out var stack) ? stack.Size : 1;
            _curSlots[key] = (item.Path, size);
            _itemsBySlot[key] = item;
        }

        if (_seeded)
        {
            foreach (var (key, cur) in _curSlots)
            {
                int gained;
                if (!_prevSlots.TryGetValue(key, out var prev))
                    gained = cur.Size;
                else if (prev.Path != cur.Path)
                    gained = cur.Size;
                else if (cur.Size > prev.Size)
                    gained = cur.Size - prev.Size;
                else
                    continue;

                var pick = ItemRecord.Read<PickupItem>(_itemsBySlot[key], gc);
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
        }
        _seeded = true;

        // fold into the baseline. grew/changed is believed at once, shrank/vanished needs a second scan
        foreach (var (key, cur) in _curSlots)
        {
            if (_prevSlots.TryGetValue(key, out var prev) && prev.Path == cur.Path
                && cur.Size < prev.Size && _shrunkOnce.Add(key))
                continue;
            _shrunkOnce.Remove(key);
            _prevSlots[key] = cur;
        }
        _gone.Clear();
        foreach (var key in _prevSlots.Keys)
            if (!_curSlots.ContainsKey(key) && !_shrunkOnce.Add(key))
                _gone.Add(key);
        foreach (var key in _gone)
        {
            _prevSlots.Remove(key);
            _shrunkOnce.Remove(key);
        }

        return picks;
    }
}
