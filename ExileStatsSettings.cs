using System.Drawing;
using System.Windows.Forms;
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
        "When you leave a map back to your hideout, append that map's stats to run.json in the map's " +
        "instance folder under maps/")]
    public ToggleNode LogToFile { get; set; } = new ToggleNode(true);

    [Menu("Log all areas (not just maps)",
        "Also track and log every non-town/non-hideout area you visit (campaign / act zones), each to its " +
        "own instance folder under maps/. Off = maps only (the original behavior)")]
    public ToggleNode LogAllAreas { get; set; } = new ToggleNode(true);

    [Menu("Min area duration (s)",
        "When 'Log all areas' is on, skip non-map areas you left in under this many seconds OR with zero " +
        "monsters seen - drops trivial transit corridors. Maps are always logged regardless")]
    public RangeNode<int> MinTrackedAreaSeconds { get; set; } = new RangeNode<int>(15, 0, 600);

    [Menu("Log ground loot",
        "While in a map, record ground loot (deduped by position) to loot.json in the map's instance " +
        "folder under maps/")]
    public ToggleNode LogLoot { get; set; } = new ToggleNode(true);

    [Menu("Log looted items",
        "While in a map, detect items looted into your backpack (incl. stack growth) and record them to " +
        "pickups.json in the map's instance folder")]
    public ToggleNode LogPickups { get; set; } = new ToggleNode(true);

    [Menu("Log periodic snapshots",
        "While in a map, append a timestamped snapshot (position, vitals, XP/gold, monster count) to " +
        "snapshots.json in the map's instance folder")]
    public ToggleNode LogSnapshots { get; set; } = new ToggleNode(true);

    [Menu("Snapshot interval (s)", "Seconds between periodic snapshots")]
    public RangeNode<int> SnapshotIntervalSeconds { get; set; } = new RangeNode<int>(5, 1, 300);

    [Menu("Snapshot player stats",
        "Include the full Player.Stats sheet (~360 values) in each snapshot. Off shrinks snapshots.json")]
    public ToggleNode SnapshotStats { get; set; } = new ToggleNode(false);

    [Menu("Snapshot player buffs",
        "Include active player buffs (name, stacks, remaining time) in each snapshot")]
    public ToggleNode SnapshotBuffs { get; set; } = new ToggleNode(false);

    [Menu("Log player path",
        "While in a map, record the player position at a high rate to path.json for an accurate map path " +
        "(separate from, and denser than, snapshots)")]
    public ToggleNode LogPath { get; set; } = new ToggleNode(true);

    [Menu("Path step (grid units)", "Record a path point each time the player moves this many grid units - " +
        "keeps the path evenly dense in fast-traversed corridors, not just slow rooms")]
    public RangeNode<int> PathStepUnits { get; set; } = new RangeNode<int>(6, 1, 40);

    [Menu("Path min interval (ms)", "Minimum milliseconds between path points (anti-spam floor; the spacing " +
        "is set by Path step)")]
    public RangeNode<int> PathIntervalMs { get; set; } = new RangeNode<int>(50, 0, 2000);

    [Menu("Save map image", "When you enter a map, ask the Radar plugin for a PNG of the map and save it " +
        "to the maps/ subfolder of the plugin directory")]
    public ToggleNode SaveMapImage { get; set; } = new ToggleNode(true);

    [Menu("Map image delay (s)", "Seconds to wait after entering a map before grabbing the image, so the " +
        "area has finished loading and Radar has explored it")]
    public RangeNode<int> MapImageDelaySeconds { get; set; } = new RangeNode<int>(12, 0, 60);

    [Menu("Map target overlay", "Include Radar's routes/targets overlay in the saved image")]
    public ToggleNode MapImageOverlay { get; set; } = new ToggleNode(false);

    [Menu("Log map content",
        "While in a map, detect content (ritual / breach / strongbox / essence / boss / ...), record where + " +
        "when each appears (and its opened/used state) to content.json, and draw its icon on the map in the " +
        "Map Statistics window and the activity report")]
    public ToggleNode LogContent { get; set; } = new ToggleNode(true);

    [Menu("Log wisp encounters",
        "While in a map, track Tormented Spirit / Azmeri wisp encounters - count buffed monsters slain " +
        "before the rare is possessed, and mark the possessed rare's location on the map")]
    public ToggleNode LogWisps { get; set; } = new ToggleNode(true);

    [Menu("Log monster positions",
        "While in a map, record the first-seen grid position of each distinct monster (at or above the min " +
        "rarity below) to monsters.json in the map's instance folder. Needs 'Count monsters' on")]
    public ToggleNode LogMonsterPositions { get; set; } = new ToggleNode(true);

    [Menu("Monster position min rarity",
        "Lowest rarity to log positions for: 0=White 1=Magic 2=Rare 3=Unique. Higher = far fewer rows " +
        "(Rare+Unique is a handful per map; White logs everything - hundreds)")]
    public RangeNode<int> MonsterPositionMinRarity { get; set; } = new RangeNode<int>(2, 0, 3);

    [Menu("Monster positions: detailed",
        "Include extra per-monster fields (type key, name, path) in monsters.json. Off keeps rows slim " +
        "(rarity + position + timing only)")]
    public ToggleNode MonsterPositionDetailed { get; set; } = new ToggleNode(true);

    [Menu("Report: show monsters on maps",
        "Overlay logged monster positions (rarity-colored dots) on the per-run maps in the generated HTML " +
        "activity report")]
    public ToggleNode ReportShowMonsters { get; set; } = new ToggleNode(false);

    [Menu("Report: show net worth",
        "Include the net worth cards and chart in the HTML activity report")]
    public ToggleNode ReportShowNetWorth { get; set; } = new ToggleNode(true);

    [Menu("Report: show income chart",
        "Include the income-over-time chart in the HTML activity report")]
    public ToggleNode ReportShowIncomeChart { get; set; } = new ToggleNode(true);

    [Menu("Report: show map chart",
        "Include the exalted-per-hour-by-map bar chart in the HTML activity report")]
    public ToggleNode ReportShowMapChart { get; set; } = new ToggleNode(true);

    [Menu("Report: show top items",
        "Include the top 10 most valuable looted items table in the HTML activity report")]
    public ToggleNode ReportShowTopItems { get; set; } = new ToggleNode(true);

    [Menu("Report: show top runs",
        "Include the most profitable runs table in the HTML activity report")]
    public ToggleNode ReportShowTopRuns { get; set; } = new ToggleNode(true);

    [Menu("Report: show efficiency",
        "Include the efficiency stats table (income per map, avg duration, etc.) in the HTML activity report")]
    public ToggleNode ReportShowEfficiency { get; set; } = new ToggleNode(true);

    [Menu("Report: show best maps",
        "Include the best maps table (sorted by exalted/hour) in the HTML activity report")]
    public ToggleNode ReportShowBestMaps { get; set; } = new ToggleNode(true);

    [Menu("Report: show best layouts",
        "Include the best layout types table in the HTML activity report")]
    public ToggleNode ReportShowBestLayouts { get; set; } = new ToggleNode(true);

    [Menu("Report: show loot composition",
        "Include the loot composition table and notes (rarity, top drops) in the HTML activity report")]
    public ToggleNode ReportShowLootComposition { get; set; } = new ToggleNode(true);

    [Menu("Report: show run log",
        "Include the collapsible per-run log with maps and loot details in the HTML activity report")]
    public ToggleNode ReportShowRunLog { get; set; } = new ToggleNode(true);

    [Menu("Report: draw player path on maps",
        "Overlay the player path (with start/end + death markers) on the per-run maps in the HTML activity report")]
    public ToggleNode ReportShowPath { get; set; } = new ToggleNode(true);

    [Menu("Report: draw explored area on maps",
        "Shade the explored area (reveal radius within walkable along your path) on the per-run maps in the " +
        "HTML activity report. Uses the Map reveal radius + Explored tint color")]
    public ToggleNode ReportShowExplored { get; set; } = new ToggleNode(true);

    [Menu("Report: top items count",
        "How many rows in the 'Top items' table of the HTML activity report")]
    public RangeNode<int> ReportTopItemsCount { get; set; } = new RangeNode<int>(10, 1, 100);

    [Menu("Report: top runs count",
        "How many rows in the 'Most profitable runs' table of the HTML activity report")]
    public RangeNode<int> ReportTopRunsCount { get; set; } = new RangeNode<int>(5, 1, 100);

    [Menu("Report: best maps count",
        "How many rows in the 'Best maps' table of the HTML activity report")]
    public RangeNode<int> ReportBestMapsCount { get; set; } = new RangeNode<int>(10, 1, 100);

    [Menu("Report: best layouts count",
        "How many rows in the 'Best layout types' table of the HTML activity report")]
    public RangeNode<int> ReportBestLayoutsCount { get; set; } = new RangeNode<int>(8, 1, 100);

    [Menu("Log deaths",
        "While in a map, detect when your life hits 0 and append a death (position + nearby monsters) to " +
        "deaths.json in the map's instance folder")]
    public ToggleNode LogDeaths { get; set; } = new ToggleNode(true);

    [Menu("Death nearby range", "Grid distance to scan for hostile monsters around you when you die")]
    public RangeNode<int> DeathNearbyRange { get; set; } = new RangeNode<int>(80, 10, 400);

    [Menu("Log map exploration %",
        "At map end, compute how much of the walkable map (from map.svg) was explored within the reveal radii " +
        "below, and the density area. Stored on run.json.")]
    public ToggleNode LogExploration { get; set; } = new ToggleNode(true);

    [Menu("Map reveal radius (grid units)",
        "How far terrain is uncovered around you - drives exploration % and the map-view tint. Tune live so " +
        "the tint matches what you actually walked (the Map Statistics window shows explored % live).")]
    public RangeNode<float> MapRevealRadius { get; set; } =
        new RangeNode<float>(MapCoverage.DefaultRevealRadius, 20f, 600f);

    [Menu("Monster reveal radius (grid units)",
        "Wider radius at which monsters appear around you (~2x terrain reveal). Density = monsters / the " +
        "walkable area within this radius of your path. Set it to where the first monster icon shows.")]
    public RangeNode<float> MonsterRevealRadius { get; set; } =
        new RangeNode<float>(MapCoverage.DefaultMonsterRevealRadius, 40f, 1200f);

    [Menu("Show explored tint", "Shade the explored area (reveal radius within walkable along your path) on the " +
        "Map Statistics map view")]
    public ToggleNode ShowExploredTint { get; set; } = new ToggleNode(true);

    [Menu("Explored tint color", "Color + opacity of the explored-area shading on the map view " +
        "(alpha = opacity)")]
    public ColorNode ExploredTintColor { get; set; } = new ColorNode(Color.FromArgb(64, 0, 200, 0));

    [Menu("Track stash net worth",
        "While the stash panel is open, scan every loaded stash tab (and the backpack) and log your net " +
        "worth to the stash/ folder. Only tabs you've opened this session are in memory and can be read")]
    public ToggleNode TrackNetWorth { get; set; } = new ToggleNode(true);

    [Menu("Net worth: include backpack",
        "Include your main inventory items in the net-worth total (logged as the 'Inventory' tab)")]
    public ToggleNode NetWorthIncludeInventory { get; set; } = new ToggleNode(true);

    [Menu("Net worth log interval (s)",
        "Seconds between net-worth snapshots written to stash/networth.json while the stash is open")]
    public RangeNode<int> NetWorthIntervalSeconds { get; set; } = new RangeNode<int>(30, 5, 600);

    [Menu("Show net worth on screen",
        "Draw a net-worth readout (exalts + divines) while the stash panel is open")]
    public ToggleNode ShowNetWorthReadout { get; set; } = new ToggleNode(true);

    [Menu("Net worth X", "Screen X of the net-worth readout (keep clear of the left-side stash panel)")]
    public RangeNode<int> NetWorthPositionX { get; set; } = new RangeNode<int>(601, 0, 4000);

    [Menu("Net worth Y", "Screen Y of the net-worth readout")]
    public RangeNode<int> NetWorthPositionY { get; set; } = new RangeNode<int>(1026, 0, 2160);

    [Menu("Count monsters",
        "Scan monsters each tick for the on-screen counter and per-map monster stats. Off stops the monster " +
        "scan entirely (counter + run monster counts go blank). Kept on automatically while you log runs or " +
        "snapshots, since those need the count")]
    public ToggleNode CountMonsters { get; set; } = new ToggleNode(true);

    [Menu("Loot scan interval (ms)",
        "Minimum milliseconds between ground-loot scans (0 = every tick). Higher = less CPU; loot is deduped " +
        "so nothing is missed, it's just logged up to this many ms later")]
    public RangeNode<int> LootScanIntervalMs { get; set; } = new RangeNode<int>(0, 0, 1000);

    [Menu("Content scan interval (ms)",
        "Minimum milliseconds between map-content scans (0 = every tick). Content is deduped, so higher just " +
        "delays detection slightly")]
    public RangeNode<int> ContentScanIntervalMs { get; set; } = new RangeNode<int>(0, 0, 1000);

    [Menu("Wisp scan interval (ms)",
        "Minimum milliseconds between wisp-encounter scans (0 = every tick)")]
    public RangeNode<int> WispScanIntervalMs { get; set; } = new RangeNode<int>(0, 0, 1000);

    [Menu("Pickup scan interval (ms)",
        "Minimum milliseconds between backpack pickup scans (0 = every tick)")]
    public RangeNode<int> PickupScanIntervalMs { get; set; } = new RangeNode<int>(0, 0, 1000);

    [Menu("Profile tracker performance",
        "Time each per-tick tracker (last / average / max ms) - view it in the Map Statistics 'Performance' " +
        "tab. Tiny overhead; useful when adding new trackers")]
    public ToggleNode EnableProfiler { get; set; } = new ToggleNode(false);

    [Menu("Show profiler overlay",
        "Draw the per-tracker timing overlay on screen (needs 'Profile tracker performance' on)")]
    public ToggleNode ShowProfiler { get; set; } = new ToggleNode(false);

    [Menu("Profiler overlay X", "Screen X of the per-tracker timing overlay")]
    public RangeNode<int> ProfilerPositionX { get; set; } = new RangeNode<int>(15, 0, 4000);

    [Menu("Profiler overlay Y", "Screen Y of the per-tracker timing overlay")]
    public RangeNode<int> ProfilerPositionY { get; set; } = new RangeNode<int>(250, 0, 2160);

    [Menu("Open map statistics", "Hotkey to toggle the in-game Map Statistics window")]
    public HotkeyNodeV2 OpenStatsKey { get; set; } = new HotkeyNodeV2(Keys.F11);

    [Menu("Activity report: hours back",
        "Timeframe (last N hours, up to 24) covered by the HTML activity report generated from the " +
        "'Activity report' section at the bottom of these settings")]
    public RangeNode<int> ReportHours { get; set; } = new RangeNode<int>(4, 1, 24);

    [Menu("Counter X")]
    public RangeNode<int> PositionX { get; set; } = new RangeNode<int>(2621, 0, 4000);

    [Menu("Counter Y")]
    public RangeNode<int> PositionY { get; set; } = new RangeNode<int>(988, 0, 2160);

    public ColorNode TextColor { get; set; } = new ColorNode(Color.White);
}
