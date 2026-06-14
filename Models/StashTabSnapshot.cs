using System;
using System.Collections.Generic;

namespace ExileStats;

/// <summary>
/// Latest scan of a single stash tab (or the backpack, keyed "Inventory"): its priced items plus the tab's
/// total exalted value. Only tabs loaded into memory this session can be scanned, so the latest snapshot per
/// tab is kept (closing/reopening the stash doesn't drop tabs already seen).
/// </summary>
public class StashTabSnapshot
{
    public string TabName { get; set; }
    public int TabIndex { get; set; }
    public string InvType { get; set; }
    public DateTime CapturedAt { get; set; }
    public int ItemCount { get; set; }
    public double TotalExalted { get; set; }
    public List<StashItem> Items { get; set; } = new();
}
