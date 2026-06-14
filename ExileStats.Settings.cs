using System;
using ExileCore2;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ImGuiNET;

namespace ExileStats;

// Custom settings UI for the plugin entry (partial of ExileStats). Split into its own file to keep the
// hand-drawn ImGui settings screen separate from the per-Tick/area logic in ExileStats.cs.
public partial class ExileStats
{
    // ExileCore calls this only while this plugin's settings page is open; timestamp it so Render can
    // tell whether the config is currently visible.
    // Custom settings UI: the auto-generated [Menu] list was a flat, out-of-order wall of ~35 controls, so
    // we draw each node by hand (ImGui) grouped into collapsing-header categories. base.DrawSettings() is
    // intentionally NOT called - persistence is handled by the core serializing the Settings object, not by
    // the drawer. The Activity report moved to a tab in the Map Statistics window; layout validation stays.
    public override void DrawSettings()
    {
        _lastSettingsDraw = DateTime.Now;

        // Always-visible top row: master enable + the stats-window hotkey rebind.
        Toggle(Settings.Enable, "Enable plugin");
        ImGui.SameLine();
        if (Settings.OpenStatsKey.DrawPickerButton($"Open map statistics: {Settings.OpenStatsKey.Value}"))
            Input.RegisterKey(Settings.OpenStatsKey.Value);   // backstop; OnValueChanged also re-registers

        ImGui.Separator();

        if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Toggle(Settings.CountMonsters, "Count monsters",
                "Scan monsters each tick for the counter + per-map monster stats. Off stops the monster scan " +
                "entirely (kept on automatically while you log runs or snapshots)");
            Toggle(Settings.ShowCounter, "Show monster counter",
                "Draw the on-screen monster tally (only in maps, or while this config is open)");
            Toggle(Settings.SplitByRarity, "Split by rarity",
                "Break the tally into White / Magic / Rare / Unique lines");
            SliderInt(Settings.PositionX, "Counter X");
            SliderInt(Settings.PositionY, "Counter Y");
            ColorEdit(Settings.TextColor, "Counter text color");
        }

        if (ImGui.CollapsingHeader("Area logging"))
        {
            Toggle(Settings.LogToFile, "Log per-map counts to file",
                "When you leave a map, append that map's stats to run.json in the map's instance folder under maps/");
            Toggle(Settings.LogAllAreas, "Log all areas (not just maps)",
                "Also track and log every non-town/non-hideout area you visit (campaign / act zones), each to its " +
                "own instance folder under maps/. Off = maps only (the original behavior)");
            SliderInt(Settings.MinTrackedAreaSeconds, "Min area duration (s)",
                "When 'Log all areas' is on, skip non-map areas you left in under this many seconds OR with zero " +
                "monsters seen. Maps are always logged regardless");
            Toggle(Settings.LogContent, "Log map content",
                "While in a map, detect content (ritual / breach / strongbox / essence / boss / ...), record where + " +
                "when each appears (and its opened/used state) to content.json, and draw its icon on the map");
            Toggle(Settings.LogWisps, "Log wisp encounters",
                "While in a map, track Tormented Spirit / Azmeri wisp encounters - count buffed monsters slain " +
                "before the rare is possessed, and mark the possessed rare's location on the map");
            Toggle(Settings.LogMonsterPositions, "Log monster positions",
                "While in a map, record each distinct monster's first-seen grid position (at or above the min " +
                "rarity below) to monsters.json. Needs 'Count monsters' on");
            SliderInt(Settings.MonsterPositionMinRarity, "Monster position min rarity",
                "Lowest rarity to log: 0=White 1=Magic 2=Rare 3=Unique. Higher = far fewer rows (Rare+Unique " +
                "is a handful per map; White logs hundreds)");
            Toggle(Settings.MonsterPositionDetailed, "Monster positions: detailed",
                "Include extra per-monster fields (type key, name, path). Off keeps rows slim");
            Toggle(Settings.LogDeaths, "Log deaths",
                "While in a map, detect when your life hits 0 and append a death (position + nearby monsters) to " +
                "deaths.json in the map's instance folder");
            SliderInt(Settings.DeathNearbyRange, "Death nearby range",
                "Grid distance to scan for hostile monsters around you when you die");
        }

        if (ImGui.CollapsingHeader("Loot & snapshots"))
        {
            Toggle(Settings.LogLoot, "Log ground loot",
                "While in a map, record ground loot (deduped by position) to loot.json in the map's instance folder");
            Toggle(Settings.LogPickups, "Log looted items",
                "While in a map, detect items looted into your backpack (incl. stack growth) and record them to " +
                "pickups.json in the map's instance folder");
            ImGui.Spacing();
            Toggle(Settings.LogSnapshots, "Log periodic snapshots",
                "While in a map, append a timestamped snapshot (position, vitals, XP/gold, monster count) to " +
                "snapshots.json in the map's instance folder");
            SliderInt(Settings.SnapshotIntervalSeconds, "Snapshot interval (s)",
                "Seconds between periodic snapshots");
            Toggle(Settings.SnapshotStats, "Snapshot player stats",
                "Include the full Player.Stats sheet (~360 values) in each snapshot. Off shrinks snapshots.json");
            Toggle(Settings.SnapshotBuffs, "Snapshot player buffs",
                "Include active player buffs (name, stacks, remaining time) in each snapshot");
            ImGui.Spacing();
            Toggle(Settings.LogPath, "Log player path",
                "While in a map, record the player position at a high rate to path.json for an accurate map path " +
                "(separate from, and denser than, snapshots)");
            SliderInt(Settings.PathStepUnits, "Path step (grid units)",
                "Record a path point each time the player moves this many grid units - keeps the path evenly " +
                "dense in fast-traversed corridors, not just slow rooms");
            SliderInt(Settings.PathIntervalMs, "Path min interval (ms)",
                "Minimum milliseconds between path points (anti-spam floor; the spacing is set by Path step)");
        }

        if (ImGui.CollapsingHeader("Map image"))
        {
            Toggle(Settings.SaveMapImage, "Save map image",
                "When you enter a map, ask the Radar plugin for an image of the map and save it to the maps/ folder");
            SliderInt(Settings.MapImageDelaySeconds, "Map image delay (s)",
                "Seconds to wait after entering a map before grabbing the image, so the area has finished loading " +
                "and Radar has explored it");
            Toggle(Settings.MapImageOverlay, "Map target overlay",
                "Include Radar's routes/targets overlay in the saved image");
        }

        if (ImGui.CollapsingHeader("Exploration"))
        {
            Toggle(Settings.LogExploration, "Log map exploration %",
                "At map end, compute how much of the walkable map (from map.svg) was explored within the reveal " +
                "radii below, and the density area. Stored on run.json");
            SliderFloat(Settings.MapRevealRadius, "Map reveal radius",
                "How far terrain is uncovered around you - drives exploration % and the map-view tint. Tune live " +
                "so the tint matches what you actually walked");
            SliderFloat(Settings.MonsterRevealRadius, "Monster reveal radius",
                "Wider radius at which monsters appear around you (~2x terrain reveal). Density = monsters / the " +
                "walkable area within this radius of your path");
            Toggle(Settings.ShowExploredTint, "Show explored tint",
                "Shade the explored area (reveal radius within walkable along your path) on the Map Statistics map view");
            ColorEdit(Settings.ExploredTintColor, "Explored tint color",
                "Color + opacity of the explored-area shading on the map view (alpha = opacity)");
        }

        if (ImGui.CollapsingHeader("Net worth"))
        {
            Toggle(Settings.TrackNetWorth, "Track stash net worth",
                "While the stash panel is open, scan every loaded stash tab (and the backpack) and log your net " +
                "worth to the stash/ folder. Only tabs you've opened this session are in memory and can be read");
            Toggle(Settings.NetWorthIncludeInventory, "Include backpack",
                "Include your main inventory items in the net-worth total (logged as the 'Inventory' tab)");
            SliderInt(Settings.NetWorthIntervalSeconds, "Net worth log interval (s)",
                "Seconds between net-worth snapshots written to stash/networth.json while the stash is open");
            Toggle(Settings.ShowNetWorthReadout, "Show net worth on screen",
                "Draw a net-worth readout (exalts + divines) while the stash panel is open");
            SliderInt(Settings.NetWorthPositionX, "Readout X",
                "Screen X of the net-worth readout (keep clear of the left-side stash panel)");
            SliderInt(Settings.NetWorthPositionY, "Readout Y",
                "Screen Y of the net-worth readout");
        }

        if (ImGui.CollapsingHeader("Performance"))
        {
            ImGui.TextDisabled("Per-tick scan throttles (ms; 0 = every tick - no change).");
            SliderInt(Settings.LootScanIntervalMs, "Loot scan interval (ms)",
                "Minimum ms between ground-loot scans. Loot is deduped, so higher just delays logging slightly");
            SliderInt(Settings.ContentScanIntervalMs, "Content scan interval (ms)",
                "Minimum ms between map-content scans (deduped - higher just delays detection slightly)");
            SliderInt(Settings.WispScanIntervalMs, "Wisp scan interval (ms)",
                "Minimum ms between wisp-encounter scans");
            SliderInt(Settings.PickupScanIntervalMs, "Pickup scan interval (ms)",
                "Minimum ms between backpack pickup scans");
            ImGui.Spacing();
            Toggle(Settings.EnableProfiler, "Profile tracker performance",
                "Time each per-tick tracker; view it in the Map Statistics 'Performance' tab. Tiny overhead");
            Toggle(Settings.ShowProfiler, "Show profiler overlay",
                "Draw the per-tracker timing overlay on screen (needs profiling on)");
            SliderInt(Settings.ProfilerPositionX, "Profiler overlay X",
                "Screen X of the per-tracker timing overlay");
            SliderInt(Settings.ProfilerPositionY, "Profiler overlay Y",
                "Screen Y of the per-tracker timing overlay");
        }

        DrawLayoutValidation();
    }

    // ---- Custom settings node helpers ----
    // Each binds an ImGui control to a settings node (read .Value -> draw -> write back on change) and shows
    // the node's help text as a hover tooltip. ColorEdit is named to avoid clashing with System.Drawing.Color.

    private static void Toggle(ToggleNode n, string label, string tip = null)
    {
        var v = n.Value;
        if (ImGui.Checkbox(label, ref v)) n.Value = v;
        Tip(tip);
    }

    private static void SliderInt(RangeNode<int> n, string label, string tip = null)
    {
        var v = n.Value;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt(label, ref v, n.Min, n.Max)) n.Value = v;
        Tip(tip);
    }

    private static void SliderFloat(RangeNode<float> n, string label, string tip = null)
    {
        var v = n.Value;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat(label, ref v, n.Min, n.Max)) n.Value = v;
        Tip(tip);
    }

    private static void ColorEdit(ColorNode n, string label, string tip = null)
    {
        var c = n.Value.ToImguiVec4();
        if (ImGui.ColorEdit4(label, ref c, ImGuiColorEditFlags.AlphaBar |
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf))
            n.Value = c.ToColor();
        Tip(tip);
    }

    private static void Tip(string tip)
    {
        if (!string.IsNullOrEmpty(tip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);
    }
}
