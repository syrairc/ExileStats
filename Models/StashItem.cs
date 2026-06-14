using System;

namespace ExileStats;

/// <summary>
/// One item observed in a stash tab (or the backpack) during a net-worth scan. Shared item data
/// (base/rarity/stack/ChaosValue/etc.) lives in <see cref="ItemRecord"/>; this adds where it sat and when.
/// </summary>
public class StashItem : ItemRecord
{
    public string TabName { get; set; }
    public int TabIndex { get; set; }       // stash tab index; -1 for the backpack
    public string InvType { get; set; }
    public float GridX { get; set; }
    public float GridY { get; set; }
    public DateTime SeenAt { get; set; }
}
