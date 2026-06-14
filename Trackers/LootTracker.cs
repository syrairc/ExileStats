using System;
using System.Collections.Generic;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace ExileStats;

/// <summary>
/// Tracks ground loot in the current map instance. Mirrors <see cref="MonsterCounter"/>: each Tick it
/// scans <c>WorldItem</c> entities and records ones it hasn't seen before. Identity is a position-stable
/// fingerprint (item path + rounded grid position) so a dropped item is logged exactly once — even after
/// portaling out and back into the same instance. The seen-set is seeded from the instance's loot.json on
/// entry, so dedup survives re-entry and game restarts.
/// </summary>
public class LootTracker
{
    private readonly HashSet<string> _seen = new();
    private int _zoneSwitchId;   // current visit; stamped onto newly-seen items

    /// <summary>Reset the seen-set and seed it from already-logged fingerprints for the instance we're
    /// entering. <paramref name="zoneSwitchId"/> tags items first seen this visit.</summary>
    public void SetArea(IEnumerable<string> existingFingerprints, int zoneSwitchId)
    {
        _seen.Clear();
        _zoneSwitchId = zoneSwitchId;
        if (existingFingerprints != null)
            foreach (var fp in existingFingerprints)
                _seen.Add(fp);
    }

    /// <summary>Scan the entity list and return loot items not seen before this instance (already added to
    /// the seen-set).</summary>
    public List<LootItem> Scan(IEnumerable<Entity> entities, GameController gc)
    {
        var newItems = new List<LootItem>();
        if (entities == null)
            return newItems;

        foreach (var e in entities)
        {
            // Skip invalid (distant/not-yet-streamed) ground entities — their components aren't safe to
            // read yet. They become valid as you approach, and we re-scan every Tick, so they're logged then.
            if (e is not { Type: EntityType.WorldItem, IsValid: true })
                continue;

            // The ground entity wraps the real item entity (name, mods, etc.).
            var itemEntity = e.GetComponent<WorldItem>()?.ItemEntity;
            if (itemEntity == null || string.IsNullOrEmpty(itemEntity.Path))
                continue;

            // Gold piles drop constantly and carry no rarity/mods — not worth logging.
            if (itemEntity.Path == "Metadata/Items/Currency/GoldCoin")
                continue;

            var pos = e.GridPos;
            var fingerprint = $"{itemEntity.Path}@{(int)Math.Round(pos.X)}:{(int)Math.Round(pos.Y)}";
            if (!_seen.Add(fingerprint))
                continue; // already logged for this instance

            var item = ItemRecord.Read<LootItem>(itemEntity, gc);
            item.Fingerprint = fingerprint;
            item.GridX = pos.X;
            item.GridY = pos.Y;
            item.FirstSeenAt = DateTime.Now;
            item.ZoneSwitchId = _zoneSwitchId;

            newItems.Add(item);
        }

        return newItems;
    }
}
