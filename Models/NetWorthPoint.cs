using System;

namespace ExileStats;

/// <summary>
/// One compact net-worth time-series row, appended to <c>stash/networth.json</c> while the stash panel is
/// open. Net worth = Σ of every known tab's latest exalted total (+ the backpack when included). Divine
/// figures are null when NinjaPricer can't supply a Divine rate.
/// </summary>
public class NetWorthPoint
{
    public DateTime At { get; set; }
    public double TotalExalted { get; set; }
    public double? TotalDivine { get; set; }
    public double? DivineRate { get; set; }   // exalted per divine at capture time
    public int TabCount { get; set; }
    public int ItemCount { get; set; }
    public bool IncludesInventory { get; set; }
}
