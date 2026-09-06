using System.Collections.Generic;
using System.Linq;
using System.Text;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements.InventoryElements;

namespace ExileStats;

/// <summary>The current map-wide ritual-favour state: the union of favours offered across rerolls (each
/// flagged purchased) and the reroll count. Returned by <see cref="RitualRewards.Update"/> only when it
/// changed; <see cref="ContentTracker"/> replicates it onto every ritual sighting.</summary>
public class RitualRewardState
{
    public List<RitualReward> Favours { get; set; }
    public int Rerolls { get; set; }
}

/// <summary>
/// Stateful, map-level tracker for the Ritual reward window (<c>IngameUi.RitualWindow.Items</c>). While the
/// window is open it snapshots the offered favours each tick and:
/// <list type="bullet">
/// <item>adds newly-seen favours to a union (so rerolled-away favours are still recorded),</item>
/// <item>flags a favour <b>Purchased</b> when it leaves the window and a matching item Path appears in the
/// main inventory (one removed + an inventory gain),</item>
/// <item>counts a <b>reroll</b> when >=2 favours leave at once with no inventory gain (the user's
/// "significant inventory change" heuristic).</item>
/// </list>
/// Favours are read via <see cref="ItemRecord.Read{T}"/> (same extraction + pricing as loot/stash). All reads
/// are wrapped - never throws. Mirrors <see cref="PickupTracker"/>'s inventory-diff approach.
/// </summary>
public class RitualRewards
{
    private readonly Dictionary<string, RitualReward> _union = new();  // favour key -> favour (union across rerolls)
    private int _rerolls;
    private HashSet<string> _prevKeys = new();                         // favour keys in the window last tick
    private string _sig;                                               // cheap window signature, see Update
    private Dictionary<string, int> _invBaseline;                      // main-inventory Path -> count last tick

    /// <summary>Reset for a freshly-entered area; seed the union from an existing ritual sighting so re-entry
    /// / restart keeps what was already offered.</summary>
    public void SetArea(IDictionary<string, ContentSighting> known)
    {
        _union.Clear();
        _rerolls = 0;
        _prevKeys = new HashSet<string>();
        _invBaseline = null;
        _sig = null;

        var seed = known?.Values.FirstOrDefault(c => c.Type == "Ritual" && c.RitualFavours is { Count: > 0 });
        if (seed != null)
        {
            foreach (var f in seed.RitualFavours)
                _union[f.Key()] = f;
            _rerolls = seed.RitualRerolls ?? 0;
        }
    }

    /// <summary>Sample the reward window this tick; returns the updated state only when it changed.</summary>
    public RitualRewardState Update(GameController gc)
    {
        try
        {
            var ui = gc?.IngameState?.IngameUi;
            var inv = ReadInventoryCounts(gc);

            // Window closed: reset the per-open snapshot (so reopening re-snapshots) but keep the union; keep
            // the inventory baseline fresh so reopening doesn't read a stale gain as a purchase.
            if (ui?.RitualWindow is not { IsVisible: true } window)
            {
                _prevKeys = new HashSet<string>();
                _sig = null;                  // reopening re-reads, which repopulates _prevKeys
                _invBaseline = inv;
                return null;
            }

            var items = window.Items;

            // Cheap identity of what's on offer. The full read below prices every favour through NinjaPricer,
            // and each of those rebuilds a whole CustomItem from memory - far too slow to redo every tick for
            // a set that only moves on a reroll or a purchase. Path + stack size is what Key() keys on.
            var sig = Signature(items);
            if (sig == _sig)
            {
                _invBaseline = inv;
                return null;
            }
            _sig = sig;

            // Current favours offered in the window.
            var cur = new Dictionary<string, RitualReward>();
            if (items != null)
            {
                foreach (var nii in items)
                {
                    var item = nii?.Item;
                    if (item is not { IsValid: true } || string.IsNullOrEmpty(item.Path))
                        continue;
                    RitualReward r;
                    try { r = ItemRecord.Read<RitualReward>(item, gc); } catch { continue; }
                    cur[r.Key()] = r;
                }
            }

            var changed = false;

            // Newly-offered favours -> add to the union.
            foreach (var kv in cur)
                if (!_union.ContainsKey(kv.Key))
                {
                    _union[kv.Key] = kv.Value;
                    changed = true;
                }

            // Favours that left the window since last tick -> purchase or reroll.
            var removed = _prevKeys.Where(k => !cur.ContainsKey(k)).ToList();
            if (removed.Count > 0)
            {
                var gained = GainedPaths(_invBaseline, inv);
                if (removed.Count == 1)
                {
                    // One favour gone + its item appeared in the bag = purchase.
                    if (_union.TryGetValue(removed[0], out var r) && gained.Contains(r.Path) && !r.Purchased)
                    {
                        r.Purchased = true;
                        changed = true;
                    }
                }
                else if (gained.Count == 0)
                {
                    // The whole set churned without a pickup = reroll.
                    _rerolls++;
                    changed = true;
                }
            }

            _prevKeys = new HashSet<string>(cur.Keys);
            _invBaseline = inv;

            return changed
                ? new RitualRewardState { Favours = _union.Values.ToList(), Rerolls = _rerolls }
                : null;
        }
        catch
        {
            return null;
        }
    }

    // Path + stack size of every favour on offer, in window order. Reads one component per item instead of
    // the full priced record, so it's safe to run every tick.
    private static string Signature(IEnumerable<NormalInventoryItem> items)
    {
        if (items == null)
            return "";
        var sb = new StringBuilder();
        foreach (var nii in items)
        {
            var item = nii?.Item;
            if (item is not { IsValid: true } || string.IsNullOrEmpty(item.Path))
                continue;
            sb.Append(item.Path);
            if (item.TryGetComponent<Stack>(out var st))
                sb.Append('|').Append(st.Size);
            sb.Append(';');
        }
        return sb.ToString();
    }

    // Main-inventory item count per path (stack-aware), to spot a favour landing in the bag.
    private static Dictionary<string, int> ReadInventoryCounts(GameController gc)
    {
        var d = new Dictionary<string, int>();
        try
        {
            var slots = InventoryScan.MainInventory(gc)?.InventorySlotItems;
            if (slots == null)
                return d;
            foreach (var slot in slots)
            {
                var it = slot?.Item;
                if (it == null || string.IsNullOrEmpty(it.Path))
                    continue;
                var size = it.TryGetComponent<Stack>(out var st) ? st.Size : 1;
                d[it.Path] = (d.TryGetValue(it.Path, out var c) ? c : 0) + size;
            }
        }
        catch { /* inventory not readable this tick */ }
        return d;
    }

    // Paths whose count rose vs the baseline (a new/grown stack landed in the bag). Empty when no baseline yet.
    private static HashSet<string> GainedPaths(Dictionary<string, int> baseline, Dictionary<string, int> cur)
    {
        var g = new HashSet<string>();
        if (cur == null || baseline == null)
            return g;
        foreach (var kv in cur)
            if (!baseline.TryGetValue(kv.Key, out var b) || kv.Value > b)
                g.Add(kv.Key);
        return g;
    }
}
