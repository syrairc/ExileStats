using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace ExileStats;

public class ExileStatsSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Show monster counter", "Draw the on-screen monster tally (only in maps, or while this config is open)")]
    public ToggleNode ShowCounter { get; set; } = new ToggleNode(true);

    [Menu("Split by rarity", "Break the tally into White / Magic / Rare / Unique lines")]
    public ToggleNode SplitByRarity { get; set; } = new ToggleNode(true);

    [Menu("Log per-map counts to file",
        "When you leave a map back to your hideout, append that map's stats (timestamped) to " +
        "map_monster_log.json in the plugin folder")]
    public ToggleNode LogToFile { get; set; } = new ToggleNode(true);

    [Menu("Counter X")]
    public RangeNode<int> PositionX { get; set; } = new RangeNode<int>(15, 0, 4000);

    [Menu("Counter Y")]
    public RangeNode<int> PositionY { get; set; } = new RangeNode<int>(250, 0, 2160);

    public ColorNode TextColor { get; set; } = new ColorNode(Color.White);
}
