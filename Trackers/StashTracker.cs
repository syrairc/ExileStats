using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2;

namespace ExileStats;

/// <summary>
/// Scans the player's stash (and optionally the backpack) to value their net worth. Each Tick the stash
/// panel is open it reads every stash tab currently loaded into memory, prices each item via
/// <see cref="ItemRecord.Read{T}"/> (NinjaPricer bridge), and keeps the latest snapshot per tab keyed by name.
///
/// Limitation: PoE2 streams a tab's contents only after it's been viewed, so
/// <c>StashElement.AllInventories[i]</c> is null until the player opens tab i this session. Net worth is
/// therefore "best known so far" — it grows as the player clicks through tabs. Keeping the latest per-tab
/// snapshot means closing the stash (or switching tabs) doesn't drop tabs already seen. Never throws (the
/// caller also guards), and silently skips any tab whose memory isn't readable this tick.
/// </summary>
public class StashTracker
{
    // Tab name -> its latest snapshot. The backpack (when included) is stored under "Inventory".
    private readonly Dictionary<string, StashTabSnapshot> _tabs = new();

    public IReadOnlyDictionary<string, StashTabSnapshot> Tabs => _tabs;
    private double _totalExalted;
    private int _itemCount;
    public double TotalExalted => _totalExalted;
    public int ItemCount => _itemCount;
    public int TabCount => _tabs.Count;

    // recomputed after every tab change so the per-frame readout reads two fields
    private void Recalc()
    {
        double ex = 0;
        int n = 0;
        foreach (var t in _tabs.Values) { ex += t.TotalExalted; n += t.ItemCount; }
        _totalExalted = ex;
        _itemCount = n;
    }

    /// <summary>Seed the in-memory tabs from a previously-saved tabs.json so net worth survives a plugin
    /// reload (each tab is then refreshed as the player reopens it).</summary>
    public void Seed(Dictionary<string, StashTabSnapshot> saved)
    {
        if (saved == null)
            return;
        foreach (var kv in saved)
            if (kv.Value != null)
                _tabs[kv.Key] = kv.Value;
        Recalc();
    }

    /// <summary>Rescan every loaded stash tab (and the backpack if requested), refreshing per-tab snapshots.
    /// A tab whose memory isn't loaded yet is left untouched (its prior snapshot, if any, is kept).</summary>
    public void Update(GameController gc, bool includeInventory)
    {
        var now = DateTime.Now;

        var stash = gc.IngameState.IngameUi.StashElement;
        if (stash is { IsVisible: true })
        {
            // AllInventories/AllStashNames are flagged obsolete ("use Inventories") but remain the working
            // way to reach every loaded tab + its name (this is what the Stashie plugin uses); suppress the
            // warning rather than gamble on the undocumented replacement's shape.
#pragma warning disable CS0618
            int total = (int)stash.TotalStashes;
            var names = stash.AllStashNames;
            for (int i = 0; i < total; i++)
            {
                try
                {
                    var inv = stash.AllInventories?[i];
                    if (inv == null)
                        continue;   // tab not loaded this session — keep any prior snapshot

                    var name = names != null && i < names.Count && !string.IsNullOrEmpty(names[i])
                        ? names[i]
                        : $"Tab {i}";

                    var snap = new StashTabSnapshot
                    {
                        TabName = name,
                        TabIndex = i,
                        InvType = inv.InvType.ToString(),
                        CapturedAt = now,
                    };

                    var items = inv.VisibleInventoryItems;
                    if (items != null)
                    {
                        foreach (var ni in items)
                        {
                            var e = ni?.Item;
                            if (e == null || string.IsNullOrEmpty(e.Path))
                                continue;
                            var rec = ItemRecord.Read<StashItem>(e, gc);
                            rec.TabName = name;
                            rec.TabIndex = i;
                            rec.InvType = snap.InvType;
                            rec.SeenAt = now;
                            snap.Items.Add(rec);
                        }
                    }

                    snap.ItemCount = snap.Items.Count;
                    snap.TotalExalted = snap.Items.Sum(x => x.ChaosValue ?? 0);

                    // A tab with items but a 0 total = NinjaPricer not ready yet (unpriced), not a worthless
                    // tab — don't let it overwrite a prior good snapshot (that's what craters net worth).
                    if (snap.ItemCount > 0 && snap.TotalExalted <= 0 &&
                        _tabs.TryGetValue(name, out var prevSnap) && prevSnap.TotalExalted > 0)
                        continue;

                    _tabs[name] = snap;   // overwrite-latest
                }
                catch { /* this tab not readable this tick — leave its prior snapshot in place */ }
            }
#pragma warning restore CS0618
        }

        if (includeInventory)
            ScanInventory(gc, now);

        Recalc();
    }

    // The main inventory (backpack), stored as the pseudo-tab "Inventory". Same access PickupTracker uses.
    private void ScanInventory(GameController gc, DateTime now)
    {
        var slots = InventoryScan.MainInventory(gc)?.InventorySlotItems;
        if (slots == null)
            return;

        const string name = "Inventory";
        var snap = new StashTabSnapshot
        {
            TabName = name,
            TabIndex = -1,
            InvType = "MainInventory1",
            CapturedAt = now,
        };

        foreach (var slot in slots)
        {
            var e = slot?.Item;
            if (e == null || string.IsNullOrEmpty(e.Path))
                continue;
            var rec = ItemRecord.Read<StashItem>(e, gc);
            rec.TabName = name;
            rec.TabIndex = -1;
            rec.InvType = snap.InvType;
            rec.SeenAt = now;
            rec.GridX = slot.PosX;
            rec.GridY = slot.PosY;
            snap.Items.Add(rec);
        }

        snap.ItemCount = snap.Items.Count;
        snap.TotalExalted = snap.Items.Sum(x => x.ChaosValue ?? 0);

        // Same unpriced-read guard as the stash tabs: don't let an items-present-but-0 read clobber a prior
        // good backpack snapshot.
        if (snap.ItemCount > 0 && snap.TotalExalted <= 0 &&
            _tabs.TryGetValue(name, out var prevSnap) && prevSnap.TotalExalted > 0)
            return;

        _tabs[name] = snap;
    }
}
