using System.Drawing;
using System.Windows.Forms;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace ExileStats;

public class ExileStatsSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    public ToggleNode ShowCounter { get; set; } = new ToggleNode(true);

    public ToggleNode SplitByRarity { get; set; } = new ToggleNode(true);

    public ToggleNode LogToFile { get; set; } = new ToggleNode(true);

    public ToggleNode LogAllAreas { get; set; } = new ToggleNode(true);

    public RangeNode<int> MinTrackedAreaSeconds { get; set; } = new RangeNode<int>(15, 0, 600);

    public ToggleNode LogLoot { get; set; } = new ToggleNode(true);

    public ToggleNode LogPickups { get; set; } = new ToggleNode(true);

    public ToggleNode LogSnapshots { get; set; } = new ToggleNode(true);

    public RangeNode<int> SnapshotIntervalSeconds { get; set; } = new RangeNode<int>(5, 1, 300);

    public ToggleNode SnapshotStats { get; set; } = new ToggleNode(false);

    public ToggleNode SnapshotBuffs { get; set; } = new ToggleNode(false);

    public ToggleNode LogPath { get; set; } = new ToggleNode(true);

    public RangeNode<int> PathStepUnits { get; set; } = new RangeNode<int>(6, 1, 40);

    public RangeNode<int> PathIntervalMs { get; set; } = new RangeNode<int>(50, 0, 2000);

    public ToggleNode SaveMapImage { get; set; } = new ToggleNode(true);

    public RangeNode<int> MapImageDelaySeconds { get; set; } = new RangeNode<int>(12, 0, 60);

    public ToggleNode MapImageOverlay { get; set; } = new ToggleNode(false);

    public ToggleNode LogContent { get; set; } = new ToggleNode(true);

    public ToggleNode LogWisps { get; set; } = new ToggleNode(true);

    public ToggleNode LogMonsterPositions { get; set; } = new ToggleNode(true);

    public RangeNode<int> MonsterPositionMinRarity { get; set; } = new RangeNode<int>(2, 0, 3);

    public ToggleNode MonsterPositionDetailed { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowMonsters { get; set; } = new ToggleNode(false);

    public ToggleNode ReportShowNetWorth { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowIncomeChart { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowMapChart { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowTopItems { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowTopRuns { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowEfficiency { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowBestMaps { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowBestLayouts { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowLootComposition { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowRunLog { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowPath { get; set; } = new ToggleNode(true);

    public ToggleNode ReportShowExplored { get; set; } = new ToggleNode(true);

    public RangeNode<int> ReportTopItemsCount { get; set; } = new RangeNode<int>(10, 1, 100);

    public RangeNode<int> ReportTopRunsCount { get; set; } = new RangeNode<int>(5, 1, 100);

    public RangeNode<int> ReportBestMapsCount { get; set; } = new RangeNode<int>(10, 1, 100);

    public RangeNode<int> ReportBestLayoutsCount { get; set; } = new RangeNode<int>(8, 1, 100);

    public ToggleNode LogDeaths { get; set; } = new ToggleNode(true);

    public RangeNode<int> DeathNearbyRange { get; set; } = new RangeNode<int>(80, 10, 400);

    public ToggleNode LogExploration { get; set; } = new ToggleNode(true);

    public RangeNode<float> MapRevealRadius { get; set; } =
        new RangeNode<float>(MapCoverage.DefaultRevealRadius, 20f, 600f);

    public RangeNode<float> MonsterRevealRadius { get; set; } =
        new RangeNode<float>(MapCoverage.DefaultMonsterRevealRadius, 40f, 1200f);

    public ToggleNode ShowExploredTint { get; set; } = new ToggleNode(true);

    public ColorNode ExploredTintColor { get; set; } = new ColorNode(MapCoverage.DefaultTint);

    public ToggleNode TrackNetWorth { get; set; } = new ToggleNode(true);

    public ToggleNode NetWorthIncludeInventory { get; set; } = new ToggleNode(true);

    public RangeNode<int> NetWorthIntervalSeconds { get; set; } = new RangeNode<int>(30, 5, 600);

    public ToggleNode ShowNetWorthReadout { get; set; } = new ToggleNode(true);

    public RangeNode<int> NetWorthPositionX { get; set; } = new RangeNode<int>(601, 0, 4000);

    public RangeNode<int> NetWorthPositionY { get; set; } = new RangeNode<int>(1026, 0, 2160);

    public ToggleNode CountMonsters { get; set; } = new ToggleNode(true);

    public RangeNode<int> LootScanIntervalMs { get; set; } = new RangeNode<int>(0, 0, 1000);

    public RangeNode<int> ContentScanIntervalMs { get; set; } = new RangeNode<int>(0, 0, 1000);

    public RangeNode<int> WispScanIntervalMs { get; set; } = new RangeNode<int>(0, 0, 1000);

    public RangeNode<int> PickupScanIntervalMs { get; set; } = new RangeNode<int>(0, 0, 1000);

    public ToggleNode EnableProfiler { get; set; } = new ToggleNode(false);

    public ToggleNode ShowProfiler { get; set; } = new ToggleNode(false);

    public RangeNode<int> ProfilerPositionX { get; set; } = new RangeNode<int>(15, 0, 4000);

    public RangeNode<int> ProfilerPositionY { get; set; } = new RangeNode<int>(250, 0, 2160);

    public HotkeyNodeV2 OpenStatsKey { get; set; } = new HotkeyNodeV2(Keys.F11);

    public RangeNode<int> ReportHours { get; set; } = new RangeNode<int>(4, 1, 24);

    // ---- Statistics overlay (xp bar + per-hour / per-map rates) ----

    public ToggleNode ShowStatsOverlay { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayLocked { get; set; } = new ToggleNode(false);

    public RangeNode<int> OverlayPositionX { get; set; } = new RangeNode<int>(30, 0, 4000);

    public RangeNode<int> OverlayPositionY { get; set; } = new RangeNode<int>(300, 0, 2160);

    public RangeNode<int> OverlayWidth { get; set; } = new RangeNode<int>(230, 120, 800);

    public RangeNode<float> OverlayScale { get; set; } = new RangeNode<float>(1.2f, 0.6f, 3f);

    public RangeNode<int> OverlayAvgRuns { get; set; } = new RangeNode<int>(5, 1, 20);

    public ToggleNode OverlayShowXpBar { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayShowXpHour { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayShowXpMap { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayShowMapsHour { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayShowValueHour { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayShowValueMap { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayShowTimeToLevel { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayShowMapsToLevel { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayShowOmenWarning { get; set; } = new ToggleNode(true);

    public RangeNode<int> OverlayOmenThreshold { get; set; } = new RangeNode<int>(25, 0, 100);

    public ColorNode OverlayTextColor { get; set; } = new ColorNode(Color.FromArgb(255, 235, 235, 225));

    public ColorNode OverlayBgColor { get; set; } = new ColorNode(Color.FromArgb(170, 12, 12, 14));

    public ColorNode OverlayBarColor { get; set; } = new ColorNode(Color.FromArgb(220, 120, 90, 200));

    public ColorNode OverlayWarnColor { get; set; } = new ColorNode(Color.FromArgb(255, 235, 70, 70));

    public RangeNode<int> PositionX { get; set; } = new RangeNode<int>(2621, 0, 4000);

    public RangeNode<int> PositionY { get; set; } = new RangeNode<int>(988, 0, 2160);

    public ColorNode TextColor { get; set; } = new ColorNode(Color.White);
}
