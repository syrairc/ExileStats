using System;
using ExileCore2;
using ExileCore2.Shared.Nodes;
using ExileImGui;
using ImGuiNET;
// UseWindowsForms puts a global `Windows` namespace in scope, which shadows the ExileImGui class.
using EWindows = ExileImGui.Windows;

namespace ExileStats;

// Custom settings UI for the plugin entry (partial of ExileStats). The auto-generated [Menu] list was a flat,
// out-of-order wall of ~60 controls, so the screen is hand-drawn: a tab bar over grouped cards, built on the
// ExileImGui control kit (Controls / Card / Windows). base.DrawSettings() is intentionally NOT called -
// persistence is the core serializing the Settings object, not the drawer's job.
public partial class ExileStats
{
    // Slider width. Full-width sliders in a card look like progress bars; this keeps them readable.
    private const float SliderW = 240f;

    // ExileCore calls DrawSettings only while this plugin's settings page is open, so the timestamp doubles
    // as "is the config visible" for the on-screen overlays.
    public override void DrawSettings()
    {
        _lastSettingsDraw = DateTime.Now;

        using var _ = new Controls.PanelStyleScope();

        // Always-visible top row: master enable + the stats-window hotkey rebind.
        Controls.Toggle("Enable plugin", "enable", Settings.Enable);
        ImGui.SameLine();
        if (Settings.OpenStatsKey.DrawPickerButton($"Open map statistics: {Settings.OpenStatsKey.Value}"))
            Input.RegisterKey(Settings.OpenStatsKey.Value);   // backstop; OnValueChanged also re-registers

        ImGui.Separator();

        EWindows.TabBar("settings",
            ("General", TabGeneral),
            ("Overlay", TabOverlay),
            ("Logging", TabLogging),
            ("Map", TabMap),
            ("Net worth", TabNetWorth),
            ("Performance", TabPerformance),
            ("Tools", TabTools));
    }

    // ---- Tabs ----

    private bool TabGeneral()
    {
        bool d = false;
        d |= Card.Draw("counter", "Monster counter", () =>
        {
            bool c = T(Settings.CountMonsters, "Count monsters",
                "Scan monsters each tick for the counter + per-map monster stats. Off stops the monster scan " +
                "entirely (kept on automatically while you log runs or snapshots)");
            c |= Controls.ToggleGrid("countergrid",
            [
                new Controls.ToggleItem("Show counter", Settings.ShowCounter,
                    "Draw the on-screen monster tally (only in tracked areas, or while this config is open)"),
                new Controls.ToggleItem("Split by rarity", Settings.SplitByRarity,
                    "Break the tally into White / Magic / Rare / Unique lines"),
            ]);
            c |= SI(Settings.PositionX, "Counter X");
            c |= SI(Settings.PositionY, "Counter Y");
            c |= Controls.Color("Counter text color", "ctc", Settings.TextColor);
            return c;
        }).Changed;

        return d;
    }

    private bool TabOverlay()
    {
        bool d = T(Settings.ShowStatsOverlay, "Show statistics overlay",
            "Draw the on-screen statistics panel (xp bar and the rate lines below)");
        ImGui.SameLine();
        d |= T(Settings.OverlayLocked, "Locked",
            "Stop the panel being dragged. Unlock it to drag the body, and pull its right edge to resize");

        d |= Card.Draw("ovlines", "Lines", () =>
        {
            bool c = Controls.ToggleGrid("ovlinegrid",
            [
                new Controls.ToggleItem("XP bar", Settings.OverlayShowXpBar,
                    "Level progress bar at the top of the panel"),
                new Controls.ToggleItem("XP / hour", Settings.OverlayShowXpHour,
                    "Experience per hour over the averaging window"),
                new Controls.ToggleItem("XP / map", Settings.OverlayShowXpMap,
                    "Average experience per map over the averaging window"),
                new Controls.ToggleItem("Maps / hour", Settings.OverlayShowMapsHour,
                    "Maps completed per hour over the averaging window (town time included)"),
                new Controls.ToggleItem("Value / hour", Settings.OverlayShowValueHour,
                    "Worth of everything you looted per hour over the averaging window. Needs NinjaPricer"),
                new Controls.ToggleItem("Value / map", Settings.OverlayShowValueMap,
                    "Average worth of everything you looted per map over the averaging window. Needs NinjaPricer"),
                new Controls.ToggleItem("Time to next level", Settings.OverlayShowTimeToLevel,
                    "Projected time to the next character level at the current xp/hour"),
                new Controls.ToggleItem("Maps to next level", Settings.OverlayShowMapsToLevel,
                    "How many more maps the next level needs at the current xp/map"),
                new Controls.ToggleItem("No-omen warning", Settings.OverlayShowOmenWarning,
                    "Warn in red when no Omen of Amelioration is in your backpack and you are past the " +
                    "threshold below - that omen is what cuts the map death xp penalty"),
            ]);
            c |= SI(Settings.OverlayAvgRuns, "Average over N maps",
                "How many recent map runs the xp/map, maps/hour and xp/hour figures average over. A run = a " +
                "map plus any sub-areas entered from it");
            c |= SI(Settings.OverlayOmenThreshold, "Omen warning at %",
                "Only warn about the missing omen once you are this far into the level");
            return c;
        }).Changed;

        d |= Card.Draw("ovlook", "Placement and colors", () =>
        {
            bool c = SI(Settings.OverlayPositionX, "Overlay X", "Or just drag the panel while unlocked");
            c |= SI(Settings.OverlayPositionY, "Overlay Y", "Or just drag the panel while unlocked");
            c |= SI(Settings.OverlayWidth, "Overlay width", "Or drag the panel's right edge while unlocked");
            c |= SF(Settings.OverlayScale, "Text scale", "Font scale of the overlay text and xp bar", "%.2f");
            c |= Controls.ColorGrid("ovcolors",
            [
                new Controls.ColorItem("Text", () => Settings.OverlayTextColor.Value,
                    v => Settings.OverlayTextColor.Value = v),
                new Controls.ColorItem("Background", () => Settings.OverlayBgColor.Value,
                    v => Settings.OverlayBgColor.Value = v),
                new Controls.ColorItem("XP bar", () => Settings.OverlayBarColor.Value,
                    v => Settings.OverlayBarColor.Value = v),
                new Controls.ColorItem("Warning", () => Settings.OverlayWarnColor.Value,
                    v => Settings.OverlayWarnColor.Value = v),
            ]);
            return c;
        }).Changed;

        return d;
    }

    private bool TabLogging()
    {
        bool d = Card.Draw("areas", "Areas", () =>
        {
            bool c = Controls.ToggleGrid("areagrid",
            [
                new Controls.ToggleItem("Log runs to file", Settings.LogToFile,
                    "When you leave a tracked area, append its stats to run.json in the instance folder under maps/"),
                new Controls.ToggleItem("Log all areas", Settings.LogAllAreas,
                    "Also track every non-town/non-hideout area (campaign / act zones), not just atlas maps"),
                new Controls.ToggleItem("Log map content", Settings.LogContent,
                    "Detect content (ritual / breach / strongbox / essence / boss / ...) and record where + when " +
                    "each appears, plus its opened/used state, to content.json"),
                new Controls.ToggleItem("Log wisp encounters", Settings.LogWisps,
                    "Track Tormented Spirit / Azmeri wisp encounters and mark the possessed rare on the map"),
                new Controls.ToggleItem("Log deaths", Settings.LogDeaths,
                    "Detect when your life hits 0 and append a death (position + nearby monsters) to deaths.json"),
            ]);
            c |= SI(Settings.MinTrackedAreaSeconds, "Min area duration (s)",
                "With 'Log all areas' on, skip non-map areas you left in under this many seconds OR with zero " +
                "monsters seen. Maps are always logged regardless");
            c |= SI(Settings.DeathNearbyRange, "Death nearby range",
                "Grid distance to scan for hostile monsters around you when you die");
            return c;
        }).Changed;

        d |= Card.Draw("monsterpos", "Monster positions", () =>
        {
            bool c = Controls.ToggleGrid("mpgrid",
            [
                new Controls.ToggleItem("Log positions", Settings.LogMonsterPositions,
                    "Record each distinct monster's first-seen grid position to monsters.json. Needs 'Count monsters'"),
                new Controls.ToggleItem("Detailed rows", Settings.MonsterPositionDetailed,
                    "Include extra per-monster fields (type key, name, path). Off keeps rows slim"),
            ]);
            c |= SI(Settings.MonsterPositionMinRarity, "Min rarity",
                "Lowest rarity to log: 0=White 1=Magic 2=Rare 3=Unique. Higher = far fewer rows (Rare+Unique " +
                "is a handful per map; White logs hundreds)");
            return c;
        }).Changed;

        d |= Card.Draw("loot", "Loot", () =>
            Controls.ToggleGrid("lootgrid",
            [
                new Controls.ToggleItem("Log ground loot", Settings.LogLoot,
                    "Record ground loot (deduped by position) to loot.json in the map's instance folder"),
                new Controls.ToggleItem("Log looted items", Settings.LogPickups,
                    "Record items looted into your backpack (incl. stack growth) to pickups.json"),
            ])).Changed;

        d |= Card.Draw("snap", "Snapshots and path", () =>
        {
            bool c = Controls.ToggleGrid("snapgrid",
            [
                new Controls.ToggleItem("Periodic snapshots", Settings.LogSnapshots,
                    "Append a timestamped snapshot (position, vitals, XP/gold, monster count) to snapshots.json"),
                new Controls.ToggleItem("Include player stats", Settings.SnapshotStats,
                    "Include the full Player.Stats sheet (~360 values) in each snapshot. Off shrinks the file"),
                new Controls.ToggleItem("Include buffs", Settings.SnapshotBuffs,
                    "Include active player buffs (name, stacks, remaining time) in each snapshot"),
                new Controls.ToggleItem("Log player path", Settings.LogPath,
                    "Record the player position at a high rate to path.json (denser than snapshots)"),
            ]);
            c |= SI(Settings.SnapshotIntervalSeconds, "Snapshot interval (s)", "Seconds between snapshots");
            c |= SI(Settings.PathStepUnits, "Path step (grid units)",
                "Record a path point each time you move this many grid units - keeps the path evenly dense in " +
                "fast-traversed corridors, not just slow rooms");
            c |= SI(Settings.PathIntervalMs, "Path min interval (ms)",
                "Minimum milliseconds between path points (anti-spam floor; spacing is set by Path step)");
            return c;
        }).Changed;

        return d;
    }

    private bool TabMap()
    {
        bool d = Card.Draw("mapimg", "Map image", () =>
        {
            bool c = Controls.ToggleGrid("mapimggrid",
            [
                new Controls.ToggleItem("Save map image", Settings.SaveMapImage,
                    "On area entry, ask the Radar plugin for the map (svg, else png) and save it under maps/"),
                new Controls.ToggleItem("Include target overlay", Settings.MapImageOverlay,
                    "Include Radar's routes/targets overlay in the saved image"),
            ]);
            c |= SI(Settings.MapImageDelaySeconds, "Capture delay (s)",
                "Seconds to wait after entering before grabbing the image, so the area has loaded and Radar " +
                "has explored it");
            return c;
        }).Changed;

        d |= Card.Draw("explore", "Exploration", () =>
        {
            bool c = Controls.ToggleGrid("expgrid",
            [
                new Controls.ToggleItem("Log exploration %", Settings.LogExploration,
                    "At area end, compute how much of the walkable map (from map.svg) was explored within the " +
                    "reveal radii below. Stored on run.json"),
                new Controls.ToggleItem("Show explored tint", Settings.ShowExploredTint,
                    "Shade the explored area on the Map Statistics map view"),
            ]);
            c |= SF(Settings.MapRevealRadius, "Map reveal radius",
                "How far terrain is uncovered around you - drives exploration % and the map-view tint. Tune " +
                "live so the tint matches what you actually walked");
            c |= SF(Settings.MonsterRevealRadius, "Monster reveal radius",
                "Wider radius at which monsters appear (~2x terrain reveal). Density = monsters / the walkable " +
                "area within this radius of your path");
            c |= Controls.Color("Explored tint color", "etc", Settings.ExploredTintColor);
            Controls.Tip("Color + opacity of the explored-area shading on the map view (alpha = opacity)");
            return c;
        }).Changed;

        return d;
    }

    private bool TabNetWorth()
    {
        return Card.Draw("networth", "Stash net worth", () =>
        {
            bool c = Controls.ToggleGrid("nwgrid",
            [
                new Controls.ToggleItem("Track net worth", Settings.TrackNetWorth,
                    "While the stash panel is open, scan every loaded stash tab (and the backpack) and log your " +
                    "net worth to the stash/ folder. Only tabs opened this session are in memory"),
                new Controls.ToggleItem("Include backpack", Settings.NetWorthIncludeInventory,
                    "Include your main inventory items in the total (logged as the 'Inventory' tab)"),
                new Controls.ToggleItem("Show readout", Settings.ShowNetWorthReadout,
                    "Draw a net-worth readout (exalts + divines) while the stash panel is open"),
            ]);
            c |= SI(Settings.NetWorthIntervalSeconds, "Log interval (s)",
                "Seconds between net-worth snapshots written to stash/networth.json while the stash is open");
            c |= SI(Settings.NetWorthPositionX, "Readout X",
                "Screen X of the readout (keep clear of the left-side stash panel)");
            c |= SI(Settings.NetWorthPositionY, "Readout Y", "Screen Y of the readout");
            return c;
        }).Changed;
    }

    private bool TabPerformance()
    {
        bool d = Card.Draw("throttle", "Per-tick scan throttles", () =>
        {
            ImGui.TextDisabled("Minimum ms between scans; 0 = every tick (no change).");
            bool c = SI(Settings.LootScanIntervalMs, "Loot scan (ms)",
                "Loot is deduped, so a higher value just delays logging slightly");
            c |= SI(Settings.ContentScanIntervalMs, "Content scan (ms)",
                "Content is deduped, so a higher value just delays detection slightly");
            c |= SI(Settings.WispScanIntervalMs, "Wisp scan (ms)");
            c |= SI(Settings.PickupScanIntervalMs, "Pickup scan (ms)");
            return c;
        }).Changed;

        d |= Card.Draw("profiler", "Profiler", () =>
        {
            bool c = Controls.ToggleGrid("profgrid",
            [
                new Controls.ToggleItem("Profile trackers", Settings.EnableProfiler,
                    "Time each per-tick tracker; view it in the Map Statistics 'Performance' tab. Tiny overhead"),
                new Controls.ToggleItem("Show overlay", Settings.ShowProfiler,
                    "Draw the per-tracker timing overlay on screen (needs profiling on)"),
            ]);
            c |= SI(Settings.ProfilerPositionX, "Overlay X");
            c |= SI(Settings.ProfilerPositionY, "Overlay Y");
            return c;
        }).Changed;

        return d;
    }

    private bool TabTools()
    {
        DrawDashboardTool();
        DrawLayoutValidation();
        return false;
    }

    // ---- Node helpers ----
    // Thin wrappers over the ExileImGui controls that add the node's help text as a hover tooltip and pin a
    // sane slider width. The label doubles as the ImGui id (unique within its card).

    private static bool T(ToggleNode n, string label, string tip = null)
    {
        var c = Controls.Toggle(label, label, n);
        Controls.Tip(tip);
        return c;
    }

    private static bool SI(RangeNode<int> n, string label, string tip = null)
    {
        ImGui.SetNextItemWidth(SliderW);
        var c = Controls.SliderInt(label, label, n);
        Controls.Tip(tip);
        return c;
    }

    private static bool SF(RangeNode<float> n, string label, string tip = null, string fmt = "%.1f")
    {
        ImGui.SetNextItemWidth(SliderW);
        var c = Controls.SliderFloat(label, label, n, fmt);
        Controls.Tip(tip);
        return c;
    }
}
