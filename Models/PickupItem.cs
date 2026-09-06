using System;

namespace ExileStats;

/// <summary>
/// One item picked up into the player's backpack during a map run. Detected by diffing the main inventory
/// each Tick (new item entity, or a grown stack). Appended to the instance's pickups.json. Shared item data
/// lives in <see cref="ItemRecord"/>.
/// </summary>
public class PickupItem : ItemRecord
{
    public DateTime PickedUpAt { get; set; }
    public double ElapsedSeconds { get; set; }   // since this visit started, not the instance's first EnteredAt
    public int ZoneSwitchId { get; set; }

    /// <summary>Amount gained by this pickup event: full stack for a new item, or the delta when an
    /// existing stack grew (e.g. currency merging onto a stack already in the bag).</summary>
    public int StackCount { get; set; }

    // Player grid position at the moment of pickup (where the marker is drawn in the viewer).
    public float PlayerGridX { get; set; }
    public float PlayerGridY { get; set; }
}
