using System;

namespace ExileStats;

/// <summary>
/// One ground item seen on the floor during a map run. Logged once per item, deduped by
/// <see cref="Fingerprint"/> (item path + rounded grid position) so portaling out and back into the same
/// instance does not re-log it. Appended to the instance's loot.json. Shared item data lives in
/// <see cref="ItemRecord"/>.
/// </summary>
public class LootItem : ItemRecord
{
    // Identity / dedup
    public string Fingerprint { get; set; }

    // Where/when it was first seen
    public float GridX { get; set; }
    public float GridY { get; set; }
    public DateTime FirstSeenAt { get; set; }

    // Which visit (ZoneSwitchId) first saw this item — lets the viewer show loot per run. 0 for items
    // logged before this field existed.
    public int ZoneSwitchId { get; set; }
}
