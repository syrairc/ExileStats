using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ExileCore2;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace ExileStats;

/// <summary>
/// In-game "Map Statistics" window. Enumerates the per-instance <c>maps/</c> folders, lets the user pick a
/// completed map visit, and draws a time graph (enter→leave) with toggleable Gold / XP / Monster lines,
/// per-snapshot vitals, content-completion status, and red death markers; plus a loot list and a map-image
/// pane that overlays the player's path and death locations. Pure rendering — reads the JSON files written
/// by the rest of the plugin.
/// </summary>
public class MapStatsWindow
{
    private List<RunGroup> _runs;            // all runs (grouped by RunId), newest first
    private RunGroup _selected;              // selected run
    private RunEntry _selectedArea;          // selected area within the run (drives the map / loot / timeline)
    private List<Snapshot> _snapshots = new();
    private List<PathPoint> _path = new();   // dense player-position track (preferred over snapshots for the path)
    private List<Death> _deaths = new();
    private List<LootItem> _loot = new();
    private string _lootFilter = "All";       // selected loot category filter
    private LootItem _selectedLoot;           // loot row clicked -> marked on the map
    private List<PickupItem> _pickups = new();
    private PickupItem _selectedPickup;       // pickup row clicked -> marked on the map (at pickup position)

    // rows prepared once per selection / filter change. label + color + category, sorted by value
    private sealed class ItemRow { public ItemRecord Item; public string Label; public string Category; public Vector4 Color; }
    private readonly List<ItemRow> _lootRows = new();
    private readonly List<ItemRow> _pickupRows = new();
    private readonly List<string> _categories = new();
    private readonly List<ItemRow> _lootShown = new();
    private readonly List<ItemRow> _pickupShown = new();
    private string _shownFilter;   // filter the *_Shown lists were built for
    private List<ContentSighting> _content = new();   // map content (ritual/breach/strongbox/…) for the visit
    private bool _showContent = true;          // draw content icons on the map
    private float _contentIconSize = 20f;      // content icon pixel size in the pane
    private List<WispEncounter> _wisps = new();   // resolved wisp encounters for the visit (victim markers)
    private bool _showWisps = true;            // draw wisp victim markers on the map
    private List<MonsterSighting> _monsters = new();   // logged monster first-seen positions for the visit
    private bool _showMonsters = true;         // draw monster dots on the map
    private float _monsterDotSize = 5f;        // monster dot radius in the pane

    // Series toggles
    private bool _showGold = true;
    private bool _showXp = true;
    private bool _showSeen = true;
    private bool _showLife;
    private bool _showEs;
    private bool _showMana;

    // Map-image pane state (loaded once per selected instance). Prefer the vector SVG; fall back to PNG.
    private MapSvg _svg;                      // parsed vector map, or null
    private string _mapTextureId;            // Graphics image key (PNG fallback), or null
    private bool _mapMissing;                // selected run has neither svg nor png
    private float _mapZoom = 1f;             // user zoom (1 = fit-to-pane)
    private Vector2 _mapPan;                 // image top-left offset within the pane (screen px)
    // screen-space copies of the terrain loops / routes / path, rebuilt only when the transform changes
    private Vector2 _cacheImgPos = new(float.NaN, float.NaN);
    private Vector2 _cacheSize;
    private readonly List<Vector2[]> _screenLoops = new();
    private readonly List<(Vector2[] pts, uint color)> _screenRoutes = new();
    private readonly List<Vector2[]> _screenPath = new();   // one polyline per non-teleport segment
    private Vector2[] _pathGrid = Array.Empty<Vector2>();
    private float _jumpSq;
    // Explored-area tint: row-merged rectangles (grid units, min/max) of the bubble∩walkable cells this
    // run covered. Computed once per selected run; drawn under the path overlay (toggle/color in settings).
    private readonly List<(Vector2 min, Vector2 max)> _exploredRects = new();
    private float _exploredRectsRadius = -1f;   // reveal radius the rects were built at (rebuild on change)
    private long _exploredDirtySince;           // tick the radius started drifting from _exploredRectsRadius, 0 = settled
    private double? _exploredLivePercent;        // explored% recomputed live at the current reveal radius

    // One area visit within a run (instance folder + which visit). Record loaded lazily on select.
    private sealed class RunEntry
    {
        public string Folder;          // instance folder path
        public int ZoneSwitchId;       // which visit within the instance
        public int RunId;              // run this area belongs to (groups map + sub-areas)
        public bool IsMapArea;         // atlas map vs sub-area (from the index / AreaId)
        public string AreaName;        // display name for the run row + area sub-picker
        public DateTime LoggedAt;
        public bool Archived;          // mirrors RunIndexEntry.Archived
        public MapRunRecord Record;    // one visit (loaded lazily on select)
    }

    // One run = a map plus any sub-areas entered from within it (Abyssal Depths, boss arenas, …), grouped by
    // RunId. The picker shows one row per run; the headline (the atlas map, else the first area) drives the
    // row label and the default detail view. Summary fields are aggregated across the areas on select.
    private sealed class RunGroup
    {
        public int RunId;
        public List<RunEntry> Areas = new();
        public RunEntry Headline;
        public string Label;
        public DateTime LoggedAt;
        public int Monsters;
        public long GoldGained;
        public long XpGained;
        public double DurationSeconds;
        public bool Archived;          // true when all member areas are archived
    }

    private bool _showArchived;         // show archived runs in the picker
    private RunGroup _pendingPurge;    // run awaiting delete confirmation
    private bool _purgeModalOpen;      // tracks the modal's open state
    private bool _batchArchiveModalOpen;
    private bool _batchPurgeModalOpen;
    private int _batchRunCount;        // count shown in batch confirmation modals
    private int _batchAgeDays = 7;     // age threshold last selected in the cleanup popup
    private int _batchAgeIndex = 1;    // combo index: 0=1d 1=7d 2=14d 3=30d 4=60d 5=90d
    private string _exportStatus;      // status/error for the "Export to HTML" button in DrawDetail
    private string _cleanupStatus;     // status/error for archive/unarchive/purge, shown next to the run list
    private string _pluginDirectory;   // cached for archive/purge ops

    private Graphics _graphics;
    private ExileStatsSettings _settings;

    // Activity-report tab state: path of the last generated report (drives the "open folder" button) and a
    // status/error line shown inline (the window has no LogMessage/LogError, so feedback lives in the UI).
    private string _lastReportPath;
    private string _reportStatus;

    private int _lastDrawFrame = -10;   // frame this window last drew; a gap means it was just reopened

    // run + area selected before a reopen reload, so the pick can come back once the list reloads
    private HashSet<(string folder, int zsid)> _pendingRestoreRunKeys;
    private (string folder, int zsid)? _pendingRestoreAreaKey;

    public void Draw(Graphics graphics, string pluginDirectory, ExileStatsSettings settings,
        GameController gameController, double divRate, TrackerProfiler profiler, ref bool open)
    {
        _graphics = graphics;
        _settings = settings;
        _pluginDirectory = pluginDirectory;
        _divRate = divRate;   // drives FmtCur ex/div display (5-div rule); owned by the plugin's Slow tracker

        // draw only runs while open, so a gap in the frame counter means the window was just reopened
        var frame = ImGui.GetFrameCount();
        if (frame > _lastDrawFrame + 1)
        {
            // stash the current pick before Refresh wipes it, so it can come back after the reload
            if (_selected != null)
            {
                _pendingRestoreRunKeys = RunGroupKeys(_selected);
                _pendingRestoreAreaKey = _selectedArea != null
                    ? (Path.GetFileName(_selectedArea.Folder), _selectedArea.ZoneSwitchId)
                    : null;
            }
            _runs = null;
        }
        _lastDrawFrame = frame;

        if (!ImGui.Begin("Map Statistics", ref open))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("mapstats"))
        {
            if (ImGui.BeginTabItem("Runs"))
            {
                DrawRunsTab(pluginDirectory);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Activity Report"))
            {
                DrawActivityReportTab(pluginDirectory, gameController);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Performance"))
            {
                DrawPerformanceTab(profiler, settings);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // Performance tab: per-tracker timing from the dispatcher's profiler (last / avg / max ms + call count),
    // so the cost of each per-tick monitor — and any newly added one — is visible at a glance.
    private void DrawPerformanceTab(TrackerProfiler profiler, ExileStatsSettings settings)
    {
        bool on = settings.EnableProfiler.Value;
        if (ImGui.Checkbox("Enable profiling", ref on))
            settings.EnableProfiler.Value = on;
        ImGui.SameLine();
        if (ImGui.Button("Reset"))
            profiler?.Reset();
        ImGui.SameLine();
        ImGui.TextDisabled("times each per-tick tracker");

        if (profiler == null || profiler.Stats.Count == 0)
        {
            ImGui.TextDisabled(on ? "Collecting... enter a map." : "Profiling is off.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                      ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("trackerPerf", 5, flags, new Vector2(0, 320)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Tracker");
            ImGui.TableSetupColumn("Last ms");
            ImGui.TableSetupColumn("Avg ms");
            ImGui.TableSetupColumn("Max ms");
            ImGui.TableSetupColumn("Calls");
            ImGui.TableHeadersRow();

            foreach (var kv in profiler.Stats.OrderByDescending(k => k.Value.AvgMs))
            {
                var s = kv.Value;
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(kv.Key);
                ImGui.TableNextColumn(); ImGui.Text(s.LastMs.ToString("0"));
                ImGui.TableNextColumn(); ImGui.Text(s.AvgMs.ToString("0"));
                ImGui.TableNextColumn(); ImGui.Text(s.MaxMs.ToString("0"));
                ImGui.TableNextColumn(); ImGui.Text(s.Calls.ToString());
            }
            ImGui.EndTable();
        }
    }

    // Runs tab: the run picker (left) + selected-run detail/graph (right) — the original window body.
    private void DrawRunsTab(string pluginDirectory)
    {
        if (_runs == null)
        {
            Refresh(pluginDirectory);
            RestorePendingSelection();
        }

        if (ImGui.Button("Refresh"))
            Refresh(pluginDirectory);
        ImGui.SameLine();
        if (ImGui.Checkbox("Show archived", ref _showArchived)) { /* filter applied in display loop */ }
        ImGui.SameLine();
        var displayRuns = _showArchived ? _runs : _runs.Where(r => !r.Archived).ToList();
        ImGui.TextDisabled($"{displayRuns.Count} run(s)");
        ImGui.SameLine();
        bool openCleanupPopup = false;
        if (ImGui.Button("Cleanup..."))
        {
            openCleanupPopup = true;
            _cleanupStatus = null;
        }
        if (!string.IsNullOrEmpty(_cleanupStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_cleanupStatus);
        }

        ImGui.Separator();

        // Left: run picker.
        ImGui.BeginChild("runs", new Vector2(260, 0), ImGuiChildFlags.Border);
        bool openPurgeModal = false;
        RunGroup exportRun = null;
        foreach (var run in displayRuns)
        {
            var label = run.Archived ? "[A] " + run.Label : run.Label;
            if (run.Archived)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1f));

            bool clicked = ImGui.Selectable(label, ReferenceEquals(run, _selected));

            if (run.Archived)
                ImGui.PopStyleColor();

            if (clicked)
                SelectGroup(run);

            if (ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem("Export to HTML"))
                {
                    exportRun = run;
                    if (!ReferenceEquals(run, _selected)) SelectGroup(run);
                }
                ImGui.Separator();
                if (!run.Archived && ImGui.MenuItem("Archive"))
                    SetRunArchived(run, true);
                if (run.Archived && ImGui.MenuItem("Unarchive"))
                    SetRunArchived(run, false);
                ImGui.Separator();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.35f, 0.35f, 1f));
                if (ImGui.MenuItem("Purge (delete files)"))
                {
                    _pendingPurge = run;
                    openPurgeModal = true;
                }
                ImGui.PopStyleColor();
                ImGui.EndPopup();
            }
        }
        ImGui.EndChild();

        if (openPurgeModal)
        {
            _purgeModalOpen = true;
            ImGui.OpenPopup("##purge_confirm");
        }
        if (openCleanupPopup)
            ImGui.OpenPopup("##batch_cleanup");

        // Purge confirmation modal.
        if (ImGui.BeginPopupModal("##purge_confirm", ref _purgeModalOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Delete all files for:");
            ImGui.TextDisabled(_pendingPurge?.Label ?? "");
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "This cannot be undone.");
            ImGui.Spacing();
            if (ImGui.Button("Delete", new Vector2(100, 0)))
            {
                if (_pendingPurge != null)
                    PurgeRunGroup(_pendingPurge);
                _pendingPurge = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                _pendingPurge = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!_purgeModalOpen) _pendingPurge = null;

        // Batch cleanup popup (age picker + action buttons).
        bool openBatchArchive = false, openBatchPurge = false;
        DrawBatchCleanupPopup(out openBatchArchive, out openBatchPurge);
        if (openBatchArchive) { _batchArchiveModalOpen = true; ImGui.OpenPopup("##batch_archive_confirm"); }
        if (openBatchPurge) { _batchPurgeModalOpen = true; ImGui.OpenPopup("##batch_purge_confirm"); }

        // Batch archive confirmation modal.
        if (ImGui.BeginPopupModal("##batch_archive_confirm", ref _batchArchiveModalOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"Archive {_batchRunCount} run(s) older than {_batchAgeDays} day(s)?");
            ImGui.Spacing();
            ImGui.TextDisabled("Archived runs are hidden (use Show archived to view).");
            ImGui.Spacing();
            if (ImGui.Button("Archive", new Vector2(110, 0)))
            {
                BatchArchiveOldRuns(DateTime.Now.AddDays(-_batchAgeDays));
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(110, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        // Batch purge confirmation modal.
        if (ImGui.BeginPopupModal("##batch_purge_confirm", ref _batchPurgeModalOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"Delete files for {_batchRunCount} run(s) older than {_batchAgeDays} day(s)?");
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "This cannot be undone.");
            ImGui.Spacing();
            if (ImGui.Button("Delete", new Vector2(110, 0)))
            {
                BatchPurgeOldRuns(DateTime.Now.AddDays(-_batchAgeDays));
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(110, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (exportRun != null)
            ExportRun(exportRun);

        ImGui.SameLine();

        // Right: details + graph.
        ImGui.BeginChild("detail", new Vector2(0, 0), ImGuiChildFlags.Border);
        if (_selected == null)
            ImGui.TextDisabled("Select a map run on the left.");
        else
            DrawDetail();
        ImGui.EndChild();
    }

    // Batch cleanup popup: age-threshold picker + Archive / Purge action buttons. Returns flags signalling
    // which confirmation modal to open next (both false = user closed without acting).
    private void DrawBatchCleanupPopup(out bool openArchive, out bool openPurge)
    {
        openArchive = false;
        openPurge = false;
        if (!ImGui.BeginPopup("##batch_cleanup"))
            return;

        ImGui.Text("Batch cleanup by age");
        ImGui.Separator();
        var idx = _batchAgeIndex;
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("Older than##age", ref idx, "1 day\07 days\014 days\030 days\060 days\090 days\0", 6))
            _batchAgeIndex = idx;
        var days = _batchAgeIndex switch { 0 => 1, 1 => 7, 2 => 14, 3 => 30, 4 => 60, 5 => 90, _ => 7 };
        var cutoff = DateTime.Now.AddDays(-days);
        var count = _runs?.Count(g => g.LoggedAt < cutoff) ?? 0;
        ImGui.TextDisabled($"{count} run(s) would be affected");
        ImGui.Spacing();

        if (ImGui.Button("Archive older runs"))
        {
            _batchRunCount = count;
            _batchAgeDays = days;
            if (count > 0) openArchive = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.35f, 0.35f, 1f));
        if (ImGui.Button("Purge older runs"))
        {
            _batchRunCount = count;
            _batchAgeDays = days;
            if (count > 0) openPurge = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleColor();
        ImGui.EndPopup();
    }

    // Export the selected run to a standalone HTML file (reports/run_<name>_<date>.html) and open it.
    private void ExportRun(RunGroup grp)
    {
        if (grp == null) return;
        try
        {
            var areas = grp.Areas
                .Select(a => (folder: Path.GetFileName(a.Folder), zone: a.ZoneSwitchId))
                .ToList();
            var opts = new ReportOptions
            {
                ShowMonstersOnMaps  = _settings.ReportShowMonsters.Value,
                ShowPathOnMaps      = _settings.ReportShowPath.Value,
                ShowExploredOnMaps  = _settings.ReportShowExplored.Value,
                RevealRadius        = _settings.MapRevealRadius.Value,
                ExploredTintHex     = ContentCatalog.ToHex(_settings.ExploredTintColor.Value),
                ExploredTintOpacity = _settings.ExploredTintColor.Value.A / 255.0,
            };
            var path = ActivityReportGenerator.GenerateRunReport(
                _pluginDirectory, areas, grp.Headline.AreaName, _divRate, opts);
            if (!string.IsNullOrEmpty(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                _exportStatus = "Exported: " + Path.GetFileName(path);
            }
        }
        catch (Exception ex)
        {
            _exportStatus = "Export failed: " + ex.Message;
        }
    }

    // Archive all runs whose latest log time is before the cutoff.
    private void BatchArchiveOldRuns(DateTime cutoff) =>
        SetRunsArchived(_runs.Where(g => g.LoggedAt < cutoff && !g.Archived).ToList(), true);

    // Delete all files for runs whose latest log time is before the cutoff.
    private void BatchPurgeOldRuns(DateTime cutoff) =>
        PurgeRunGroups(_runs.Where(g => g.LoggedAt < cutoff).ToList());

    private void SetRunArchived(RunGroup grp, bool archived) => SetRunsArchived(new List<RunGroup> { grp }, archived);
    private void PurgeRunGroup(RunGroup grp) => PurgeRunGroups(new List<RunGroup> { grp });

    // one index read-modify-write for the whole batch, guarded so a bad index can't throw out of Render
    private void SetRunsArchived(List<RunGroup> groups, bool archived)
    {
        if (groups.Count == 0) return;
        var keys = new HashSet<(string folder, int zsid)>();
        foreach (var g in groups) keys.UnionWith(RunGroupKeys(g));
        try
        {
            RunIndex.Modify(_pluginDirectory, list =>
            {
                foreach (var e in list)
                    if (keys.Contains((e.Folder, e.ZoneSwitchId)))
                        e.Archived = archived;
            });
        }
        catch (Exception ex)
        {
            _cleanupStatus = (archived ? "Archive failed: " : "Unarchive failed: ") + ex.Message;
            return;
        }
        _cleanupStatus = null;
        foreach (var g in groups)
        {
            g.Archived = archived;
            foreach (var a in g.Areas) a.Archived = archived;
        }
    }

    // one index read-modify-write for the whole batch, guarded so a bad index can't throw out of Render
    private void PurgeRunGroups(List<RunGroup> groups)
    {
        if (groups.Count == 0) return;
        var keys = new HashSet<(string folder, int zsid)>();
        foreach (var g in groups) keys.UnionWith(RunGroupKeys(g));
        var root = Path.Combine(_pluginDirectory, InstanceStore.RootFolder);
        List<string> foldersToDelete = null;
        try
        {
            RunIndex.Modify(_pluginDirectory, list =>
            {
                list.RemoveAll(e => keys.Contains((e.Folder, e.ZoneSwitchId)));
                var stillReferenced = list.Select(e => e.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foldersToDelete = keys.Select(k => k.folder).Distinct()
                    .Where(f => !stillReferenced.Contains(f))
                    .ToList();
            });
        }
        catch (Exception ex)
        {
            _cleanupStatus = "Purge failed: " + ex.Message;
            return;
        }
        _cleanupStatus = null;
        foreach (var f in foldersToDelete ?? new List<string>())
        {
            var fullPath = Path.Combine(root, f);
            if (Directory.Exists(fullPath))
                try { Directory.Delete(fullPath, recursive: true); } catch { /* best-effort */ }
        }
        foreach (var g in groups)
        {
            if (ReferenceEquals(g, _selected)) { _selected = null; _selectedArea = null; }
            _runs.Remove(g);
        }
    }

    private static HashSet<(string folder, int zsid)> RunGroupKeys(RunGroup grp) =>
        grp.Areas
            .Select(a => (folder: Path.GetFileName(a.Folder), zsid: a.ZoneSwitchId))
            .ToHashSet();

    // Activity Report tab: generate a self-contained HTML summary of every map run in the last
    // Settings.ReportHours hours (read straight off the maps/ JSON files), then open it in the browser.
    private void DrawActivityReportTab(string pluginDirectory, GameController gameController)
    {
        ImGui.TextWrapped("Self-contained HTML summary of recent runs - gold/xp/exalted per hour, top items, " +
                          "best maps/layouts, deaths, and net worth.");
        ImGui.Spacing();

        var hrs = _settings.ReportHours.Value;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("Hours back", ref hrs, _settings.ReportHours.Min, _settings.ReportHours.Max))
            _settings.ReportHours.Value = hrs;

        var showMon = _settings.ReportShowMonsters.Value;
        if (ImGui.Checkbox("Show monsters on maps", ref showMon))
            _settings.ReportShowMonsters.Value = showMon;

        if (ImGui.CollapsingHeader("Report sections"))
        {
            void Sec(ToggleNode n, string label)
            {
                var v = n.Value;
                if (ImGui.Checkbox(label, ref v)) n.Value = v;
            }
            ImGui.Columns(2, "repsec", false);
            Sec(_settings.ReportShowNetWorth,        "Net worth");
            ImGui.NextColumn();
            Sec(_settings.ReportShowRunLog,          "Run log");
            ImGui.NextColumn();
            Sec(_settings.ReportShowIncomeChart,     "Income chart");
            ImGui.NextColumn();
            Sec(_settings.ReportShowLootComposition, "Loot composition");
            ImGui.NextColumn();
            Sec(_settings.ReportShowMapChart,        "Map chart");
            ImGui.NextColumn();
            Sec(_settings.ReportShowEfficiency,      "Efficiency");
            ImGui.NextColumn();
            Sec(_settings.ReportShowTopItems,        "Top items");
            ImGui.NextColumn();
            Sec(_settings.ReportShowBestMaps,        "Best maps");
            ImGui.NextColumn();
            Sec(_settings.ReportShowTopRuns,         "Top runs");
            ImGui.NextColumn();
            Sec(_settings.ReportShowBestLayouts,     "Best layouts");
            ImGui.NextColumn();
            Sec(_settings.ReportShowPath,            "Player path on maps");
            ImGui.NextColumn();
            Sec(_settings.ReportShowExplored,        "Explored area on maps");
            ImGui.Columns(1);
        }

        if (ImGui.CollapsingHeader("Report row counts"))
        {
            void Cnt(RangeNode<int> n, string label)
            {
                var v = n.Value;
                ImGui.SetNextItemWidth(180);
                if (ImGui.SliderInt(label, ref v, n.Min, n.Max)) n.Value = v;
            }
            Cnt(_settings.ReportTopItemsCount,    "Top items");
            Cnt(_settings.ReportTopRunsCount,     "Top runs");
            Cnt(_settings.ReportBestMapsCount,    "Best maps");
            Cnt(_settings.ReportBestLayoutsCount, "Best layouts");
        }

        if (ImGui.Button($"Generate report (last {hrs}h)"))
        {
            try
            {
                var divineRate = _divRate;
                var opts = new ReportOptions
                {
                    ShowNetWorth        = _settings.ReportShowNetWorth.Value,
                    ShowIncomeChart     = _settings.ReportShowIncomeChart.Value,
                    ShowMapChart        = _settings.ReportShowMapChart.Value,
                    ShowTopItems        = _settings.ReportShowTopItems.Value,
                    ShowTopRuns         = _settings.ReportShowTopRuns.Value,
                    ShowEfficiency      = _settings.ReportShowEfficiency.Value,
                    ShowBestMaps        = _settings.ReportShowBestMaps.Value,
                    ShowBestLayouts     = _settings.ReportShowBestLayouts.Value,
                    ShowLootComposition = _settings.ReportShowLootComposition.Value,
                    ShowRunLog          = _settings.ReportShowRunLog.Value,
                    ShowMonstersOnMaps  = _settings.ReportShowMonsters.Value,
                    ShowPathOnMaps      = _settings.ReportShowPath.Value,
                    ShowExploredOnMaps  = _settings.ReportShowExplored.Value,
                    RevealRadius        = _settings.MapRevealRadius.Value,
                    ExploredTintHex     = ContentCatalog.ToHex(_settings.ExploredTintColor.Value),
                    ExploredTintOpacity = _settings.ExploredTintColor.Value.A / 255.0,
                    TopItemsCount       = _settings.ReportTopItemsCount.Value,
                    TopRunsCount        = _settings.ReportTopRunsCount.Value,
                    BestMapsCount       = _settings.ReportBestMapsCount.Value,
                    BestLayoutsCount    = _settings.ReportBestLayoutsCount.Value,
                };
                _lastReportPath = ActivityReportGenerator.Generate(pluginDirectory, hrs, divineRate, opts);
                if (!string.IsNullOrEmpty(_lastReportPath))
                {
                    Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true });
                    _reportStatus = _lastReportPath;
                }
                else
                {
                    _reportStatus = $"No map runs in the last {hrs}h.";
                }
            }
            catch (Exception ex)
            {
                _reportStatus = "Failed: " + ex.Message;
            }
        }

        if (!string.IsNullOrEmpty(_lastReportPath))
        {
            ImGui.SameLine();
            if (ImGui.Button("Open report folder"))
            {
                try
                {
                    var folder = Path.GetDirectoryName(_lastReportPath);
                    if (!string.IsNullOrEmpty(folder))
                        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                }
                catch { /* best-effort */ }
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled(string.IsNullOrEmpty(_reportStatus)
            ? "writes reports/activity_<time>.html and opens it"
            : _reportStatus);
    }

    private void Refresh(string pluginDirectory)
    {
        var root = Path.Combine(pluginDirectory, InstanceStore.RootFolder);
        _runs = new List<RunGroup>();
        _selected = null;
        _selectedArea = null;
        _snapshots = new();
        _path = new();
        _deaths = new();
        _loot = new();
        _pickups = new();
        _content = new();
        _svg = null;
        _mapTextureId = null;
        _mapMissing = true;
        _mapZoom = 1f;
        _mapPan = default;

        var entries = new List<RunEntry>();

        // Fast path: the master index lists every visit (incl. RunId) without opening per-instance files.
        var indexPath = Path.Combine(root, InstanceStore.IndexFile);
        if (File.Exists(indexPath))
        {
            List<RunIndexEntry> index = null;
            try { index = JsonConvert.DeserializeObject<List<RunIndexEntry>>(File.ReadAllText(indexPath)); }
            catch { index = null; }

            if (index is { Count: > 0 })
            {
                foreach (var e in index)
                    entries.Add(new RunEntry
                    {
                        Folder = Path.Combine(root, e.Folder),
                        ZoneSwitchId = e.ZoneSwitchId,
                        RunId = e.RunId,
                        IsMapArea = e.IsMapArea,
                        AreaName = e.MapName,
                        LoggedAt = e.LoggedAt,
                        Archived = e.Archived,
                    });
                _runs = GroupEntries(entries);
                return;
            }
        }

        // Fallback (pre-index data): walk each instance folder's run.json.
        if (!Directory.Exists(root))
            return;

        foreach (var folder in Directory.GetDirectories(root))
        {
            var runPath = Path.Combine(folder, InstanceStore.RunFile);
            if (!File.Exists(runPath))
                continue;
            List<MapRunRecord> visits;
            try { visits = JsonConvert.DeserializeObject<List<MapRunRecord>>(File.ReadAllText(runPath)); }
            catch { continue; }
            if (visits == null)
                continue;

            foreach (var v in visits)
            {
                var name = string.IsNullOrEmpty(v.DisplayName) ? v.Name : v.DisplayName;
                entries.Add(new RunEntry
                {
                    Folder = folder,
                    ZoneSwitchId = v.ZoneSwitchId,
                    RunId = v.RunId,
                    IsMapArea = InstanceStore.IsMapAreaId(v.AreaId),
                    AreaName = name,
                    LoggedAt = v.LoggedAt,
                    Record = v,
                });
            }
        }

        _runs = GroupEntries(entries);
    }

    // Group area visits into runs via RunGrouping (0 / legacy data = its own solo run). Newest run first.
    private static List<RunGroup> GroupEntries(List<RunEntry> entries)
    {
        var order = new List<RunGroup>();
        foreach (var members in RunGrouping.Group(entries, e => e.RunId, e => e.Folder, e => e.ZoneSwitchId, e => e.LoggedAt))
        {
            var grp = new RunGroup { RunId = members[0].RunId, Areas = members };
            grp.Headline = RunGrouping.Headline(members, a => a.IsMapArea);
            grp.LoggedAt = members[^1].LoggedAt;
            grp.Archived = members.TrueForAll(a => a.Archived);
            var extra = members.Count - 1;
            grp.Label = extra > 0
                ? $"{grp.Headline.AreaName}  -  {grp.LoggedAt:MM-dd HH:mm}  (+{extra})"
                : $"{grp.Headline.AreaName}  -  {grp.LoggedAt:MM-dd HH:mm}";
            order.Add(grp);
        }
        order.Sort((a, b) => b.LoggedAt.CompareTo(a.LoggedAt));
        return order;
    }

    // re-matches the pre-reload pick against the fresh run list (via RunGroupKeys); no match = no selection
    private void RestorePendingSelection()
    {
        var keys = _pendingRestoreRunKeys;
        var areaKey = _pendingRestoreAreaKey;
        _pendingRestoreRunKeys = null;
        _pendingRestoreAreaKey = null;
        if (keys == null || _runs == null)
            return;

        var grp = _runs.FirstOrDefault(g => RunGroupKeys(g).Overlaps(keys));
        if (grp == null)
            return;   // purged (or otherwise gone) -> no selection, which is correct

        SelectGroup(grp);

        if (areaKey != null)
        {
            var area = grp.Areas.FirstOrDefault(a =>
                Path.GetFileName(a.Folder) == areaKey.Value.folder && a.ZoneSwitchId == areaKey.Value.zsid);
            if (area != null)
                SelectArea(area);
        }
    }

    // Select a run: aggregate its areas' summary (loading each area's record lazily), then show the headline
    // area's map / loot / timeline.
    private void SelectGroup(RunGroup grp)
    {
        _exportStatus = null;
        _selected = grp;
        grp.Monsters = 0;
        grp.GoldGained = 0;
        grp.XpGained = 0;
        grp.DurationSeconds = 0;
        foreach (var a in grp.Areas)
        {
            a.Record ??= LoadList<MapRunRecord>(a.Folder, InstanceStore.RunFile)
                .FirstOrDefault(v => v.ZoneSwitchId == a.ZoneSwitchId);
            if (a.Record == null) continue;
            grp.Monsters += a.Record.MonstersTotal;
            grp.GoldGained += a.Record.GoldGained;
            grp.XpGained += a.Record.XpGained;
            grp.DurationSeconds += a.Record.DurationSeconds;
        }
        SelectArea(grp.Headline);
    }

    // Load one area visit's data (map, path, snapshots, deaths, loot, pickups, content) for the detail panes.
    private void SelectArea(RunEntry run)
    {
        _selectedArea = run;

        // The index-driven list defers loading the full record until selection.
        run.Record ??= LoadList<MapRunRecord>(run.Folder, InstanceStore.RunFile)
            .FirstOrDefault(v => v.ZoneSwitchId == run.ZoneSwitchId);

        _svg = null;
        _mapTextureId = null;
        _mapMissing = true;
        _mapZoom = 1f;
        _mapPan = default;

        if (run.Record == null)
        {
            _snapshots = new();
            _path = new();
            _deaths = new();
            _loot = new();
            _pickups = new();
            _content = new();
            _wisps = new();
            _monsters = new();
            _exploredRects.Clear();
            BuildItemRows();
            BuildPathGrid();
            return;
        }

        var visit = run.Record.ZoneSwitchId;
        _snapshots = LoadList<Snapshot>(run.Folder, InstanceStore.SnapshotFile)
            .Where(s => s.ZoneSwitchId == visit)
            .OrderBy(s => s.ElapsedSeconds)
            .ToList();
        _path = PathLog.ReadFolder(run.Folder)
            .Where(p => p.Z == visit)
            .OrderBy(p => p.T)
            .ToList();
        _deaths = LoadList<Death>(run.Folder, InstanceStore.DeathsFile)
            .Where(d => d.ZoneSwitchId == visit)
            .ToList();
        // Loot is deduped per instance; show this visit's items (older items lack a visit tag -> 0).
        _loot = LoadList<LootItem>(run.Folder, InstanceStore.LootFile)
            .Where(l => l.ZoneSwitchId == visit)
            .ToList();
        _selectedLoot = null;
        _pickups = LoadList<PickupItem>(run.Folder, InstanceStore.PickupsFile)
            .Where(p => p.ZoneSwitchId == visit)
            .ToList();
        _selectedPickup = null;

        // A picked-up item was on the ground a moment earlier, so it lands in BOTH loot.json (drop) and
        // pickups.json (pickup) — there's no shared id to link them. Show each looted item only in the
        // Looted list: remove from the drops list one ground item per pickup, matched by path + rarity
        // (both read the same entity). Consuming match (not "remove every drop of this path"), so a second
        // identical pile left on the floor still shows as a drop, and a crafted/manual pickup with no
        // matching ground item removes nothing.
        if (_loot.Count > 0 && _pickups.Count > 0)
        {
            var pickedCounts = new Dictionary<string, int>();
            foreach (var p in _pickups)
            {
                var k = LootKey(p);
                pickedCounts[k] = pickedCounts.TryGetValue(k, out var c) ? c + 1 : 1;
            }
            _loot = _loot.Where(l =>
            {
                var k = LootKey(l);
                if (pickedCounts.TryGetValue(k, out var c) && c > 0)
                {
                    pickedCounts[k] = c - 1;
                    return false;   // this drop was looted -> show it in the Looted list only
                }
                return true;
            }).ToList();
        }
        _content = LoadList<ContentSighting>(run.Folder, InstanceStore.ContentFile)
            .Where(c => c.ZoneSwitchId == visit)
            .ToList();
        _wisps = LoadList<WispEncounter>(run.Folder, InstanceStore.WispFile)
            .Where(w => w.ZoneSwitchId == visit)
            .ToList();
        _monsters = MonsterLog.ReadFolder(run.Folder)
            .Where(m => m.ZoneSwitchId == visit)
            .ToList();

        LoadMapImage(run.Folder);
        FilterPathToWalkable();
        BuildExploredOverlay();
        BuildItemRows();
        BuildPathGrid();
    }

    // grid-space path points (dense path.json, else sparse snapshots) plus the teleport-jump threshold,
    // recomputed once per selection instead of every frame; forces a screen-cache rebuild too.
    private void BuildPathGrid()
    {
        _pathGrid = _path.Count > 0
            ? _path.ConvertAll(p => new Vector2(p.X, p.Y)).ToArray()
            : _snapshots.ConvertAll(s => new Vector2(s.GridX, s.GridY)).ToArray();
        var r = _selectedArea?.Record;
        var w = r?.AreaWidth ?? 0;
        var h = r?.AreaHeight ?? 0;
        var jump = 0.2f * (float)Math.Sqrt((double)w * w + (double)h * h);
        _jumpSq = jump * jump;
        _cacheImgPos = new Vector2(float.NaN, float.NaN);   // force a rebuild
    }

    // Loot/pickup rows depend only on _loot/_pickups, so rebuild them once here instead of every draw.
    private void BuildItemRows()
    {
        _lootRows.Clear();
        _pickupRows.Clear();
        _categories.Clear();
        _categories.Add("All");
        // categories in load order, before sorting - matches the old per-frame walk order over _loot then _pickups
        foreach (var l in _loot) { var cat = Category(l); if (!_categories.Contains(cat)) _categories.Add(cat); }
        foreach (var p in _pickups) { var cat = Category(p); if (!_categories.Contains(cat)) _categories.Add(cat); }
        // OrderByDescending is a stable sort (List.Sort isn't) - ties keep load order, same as the old per-frame LINQ
        _lootRows.AddRange(_loot.OrderByDescending(l => l.ChaosValue ?? 0).Select(l => Row(l, l.StackSize)));
        _pickupRows.AddRange(_pickups.OrderByDescending(p => p.ChaosValue ?? 0).Select(p => Row(p, p.StackCount)));
        if (!_categories.Contains(_lootFilter)) _lootFilter = "All";
        _shownFilter = null;
    }

    private static ItemRow Row(ItemRecord it, int count)
    {
        var label = string.IsNullOrEmpty(it.UniqueName) ? it.BaseName : it.UniqueName;
        if (string.IsNullOrEmpty(label)) label = it.ClassName;
        if (string.IsNullOrEmpty(label)) label = it.Path;
        if (count > 1) label += " x" + count;
        if (it.ChaosValue is { } v && v > 0) label += "  (" + FmtCur(v) + ")";
        return new ItemRow { Item = it, Label = label, Category = Category(it), Color = RarityColor(it.Rarity) };
    }

    // re-buckets the prepared rows into the shown lists only when the category filter actually changed
    private void ApplyFilter()
    {
        if (_shownFilter == _lootFilter) return;
        _shownFilter = _lootFilter;
        _lootShown.Clear();
        _pickupShown.Clear();
        foreach (var r in _lootRows) if (_lootFilter == "All" || r.Category == _lootFilter) _lootShown.Add(r);
        foreach (var r in _pickupRows) if (_lootFilter == "All" || r.Category == _lootFilter) _pickupShown.Add(r);
    }

    // The player is always on walkable terrain, so a path point outside every terrain loop is a transition
    // artifact — the game reports one bogus position as an area unloads (a 10s-stale read 400+ units away,
    // off the map), which otherwise draws the end marker + a long line shooting into empty space. Drop those
    // here (even-odd point-in-polygon against the parsed terrain loops). Guarded: only when loops are
    // available, and skipped if it would remove a large fraction (an unreliable terrain parse), so a bad SVG
    // never blanks the path.
    private void FilterPathToWalkable()
    {
        var loops = _svg?.TerrainLoops;
        if (loops == null || loops.Count == 0 || _path.Count == 0) return;
        var keep = new HashSet<Vector2>(MapGeometry.WalkablePath(_path.ConvertAll(p => new Vector2(p.X, p.Y)), loops));
        if (keep.Count == _path.Count) return;
        _path = _path.FindAll(p => keep.Contains(new Vector2(p.X, p.Y)));
    }

    // Recompute the selected run's explored cell mask at the current MAP-REVEAL radius (live — so tuning the
    // slider updates the tint, and old runs logged at a wrong radius still display correctly), cache it as
    // row-merged rectangles (grid units) for the map-pane tint, and stash the live explored%. Same loop
    // parser as the stored value. No svg / empty path / R<=0 / no settings -> no tint.
    private void BuildExploredOverlay()
    {
        _exploredRects.Clear();
        _exploredLivePercent = null;
        _exploredRectsRadius = _settings?.MapRevealRadius.Value ?? -1f;
        _exploredDirtySince = 0;   // just rebuilt, whatever drag was pending is settled

        var record = _selectedArea?.Record;
        if (record == null || _path.Count == 0 || _settings == null) return;
        var r = _settings.MapRevealRadius.Value;
        if (r <= 0f || record.AreaWidth <= 0 || record.AreaHeight <= 0) return;

        var loops = _svg?.TerrainLoops;
        if (loops == null || loops.Count == 0) return;

        var pathPts = _path.Select(p => new Vector2(p.X, p.Y)).ToList();
        if (MapCoverage.ComputeMask(record.AreaWidth, record.AreaHeight, loops, pathPts, r) is not { } m)
            return;

        _exploredLivePercent = m.Total > 0 ? Math.Round(100.0 * m.ExploredCount / m.Total, 1) : (double?)null;

        // Row-merge consecutive explored cells into rectangles so the per-frame draw is a few hundred
        // AddRectFilled calls, not one per 5-unit cell.
        var cell = m.Cell;
        for (var gy = 0; gy < m.Gh; gy++)
        {
            var gx = 0;
            while (gx < m.Gw)
            {
                if (!m.Explored[gy * m.Gw + gx]) { gx++; continue; }
                var start = gx;
                while (gx < m.Gw && m.Explored[gy * m.Gw + gx]) gx++;
                _exploredRects.Add((
                    new Vector2(start * cell, gy * cell),
                    new Vector2(gx * cell, (gy + 1) * cell)));
            }
        }
    }

    private static Vector4 ToVec4(System.Drawing.Color c) =>
        new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    // Load the instance's map for the pane: prefer the vector map.svg (infinitely scalable), fall back to
    // the raster map.png. Both only exist with the forked Radar; absence is handled gracefully by the pane.
    private void LoadMapImage(string folder)
    {
        _svg = null;
        _mapTextureId = null;
        _mapMissing = true;

        // 1) Vector SVG (preferred).
        var svgPath = Path.Combine(folder, InstanceStore.SvgFile);
        if (File.Exists(svgPath))
        {
            _svg = MapSvg.Load(svgPath);
            if (_svg != null)
            {
                _mapMissing = false;
                return;
            }
        }

        // 2) PNG fallback (older runs).
        var pngPath = Path.Combine(folder, InstanceStore.ImageFile);
        if (!File.Exists(pngPath))
            return;

        // Unique key per instance folder so re-selecting a different map reloads the right image.
        var key = "exilestats_map_" + Path.GetFileName(folder);
        try
        {
            if (_graphics != null && _graphics.InitImage(key, pngPath))
            {
                _mapTextureId = key;
                _mapMissing = false;
            }
        }
        catch { /* stays missing */ }
    }

    private static List<T> LoadList<T>(string folder, string file)
    {
        var path = Path.Combine(folder, file);
        if (!File.Exists(path))
            return new List<T>();
        try { return JsonConvert.DeserializeObject<List<T>>(File.ReadAllText(path)) ?? new List<T>(); }
        catch { return new List<T>(); }
    }

    private void DrawDetail()
    {
        var grp = _selected;
        var r = _selectedArea?.Record;
        if (r == null)
        {
            ImGui.TextDisabled("Run record not found on disk.");
            return;
        }

        // Run header: totals aggregated across the run's areas (the map + any sub-areas).
        ImGui.Text($"{grp.Headline.AreaName}   (lvl {r.AreaLevel}, {grp.DurationSeconds:0}s total)");
        ImGui.TextDisabled(
            $"XP {Sign(grp.XpGained)}   Gold {Sign(grp.GoldGained)}   " +
            $"Monsters {grp.Monsters}   Areas {grp.Areas.Count}   Deaths {_deaths.Count}");

        // Area sub-picker (multi-area runs only): switch which area's map / loot / timeline is shown below.
        if (grp.Areas.Count > 1)
        {
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("##area", _selectedArea.AreaName))
            {
                foreach (var a in grp.Areas)
                {
                    var secs = a.Record?.DurationSeconds ?? 0;
                    if (ImGui.Selectable($"{a.AreaName}  ({secs:0}s)", ReferenceEquals(a, _selectedArea)))
                        SelectArea(a);
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("area");
        }

        // Selected area's own stats.
        ImGui.TextDisabled(
            $"This area - Monsters {r.MonstersTotal}   Dropped {_loot.Count}   Looted {_pickups.Count}   Deaths {_deaths.Count}");

        // Prefer the run record's persisted totals; fall back to summing the loaded (visit-filtered) lists.
        var dropped = r.LootValue > 0 ? r.LootValue : _loot.Sum(l => l.ChaosValue ?? 0);
        var got = r.PickupValue > 0 ? r.PickupValue : _pickups.Sum(p => p.ChaosValue ?? 0);
        ImGui.TextDisabled($"Left {FmtCur(dropped)} on ground   Looted {FmtCur(got)}");
        // Prefer the live recompute (current reveal radius) over the stored value (may be from a stale radius).
        var explPct = _exploredLivePercent ?? r.ExploredPercent;
        var explStr = explPct.HasValue ? $"{explPct.Value:0.0}%" : "-";
        ImGui.TextDisabled($"Explored {explStr}");

        // Ritual tribute earned in this area (sum across the area's ritual sites).
        var tribute = _content.Where(c => c.Type == "Ritual").Sum(c => c.TributeGained ?? 0);
        if (tribute > 0)
            ImGui.TextDisabled($"Ritual tribute {tribute:N0}");

        // Ritual favours: one map-wide list replicated onto every ritual site, so read it from one sighting.
        var favours = _content.FirstOrDefault(c => c.Type == "Ritual" && c.RitualFavours is { Count: > 0 })
            ?.RitualFavours;
        if (favours != null)
        {
            var bought = favours.Count(f => f.Purchased);
            var rr = _content.Where(c => c.Type == "Ritual").Select(c => c.RitualRerolls ?? 0).DefaultIfEmpty(0).Max();
            ImGui.TextDisabled($"Ritual favours {bought}/{favours.Count} bought" + (rr > 0 ? $", {rr} reroll(s)" : ""));
            foreach (var f in favours.Where(f => f.Purchased))
            {
                var name = string.IsNullOrEmpty(f.UniqueName) ? f.BaseName : f.UniqueName;
                if (f.StackSize > 1) name += $" x{f.StackSize}";
                var val = f.ChaosValue is { } v && v > 0 ? $"  {FmtCur(v)}" : "";
                ImGui.TextDisabled($"  bought: {name}{val}");
            }
        }

        // Expedition: rune count + top offered reward (else top computed) for each runestone in this area.
        foreach (var exp in _content.Where(c => c.Type == "Expedition"))
        {
            var rewards = exp.OfferedRewards ?? exp.RewardPool;
            var top = rewards is { Count: > 0 } ? rewards[0] : null;
            var line = $"Expedition - {exp.RuneCount ?? 0} runes";
            if (top != null)
            {
                line += $", {(exp.OfferedRewards != null ? "offered" : "best")} {top.Name} x{top.Count}";
                if (top.Value is { } v && v > 0) line += $" ({FmtCur(v)})";
            }
            ImGui.TextDisabled(line);
        }

        if (r.MapObjectives is { Count: > 0 })
            ImGui.TextDisabled("Objectives: " + string.Join(", ", r.MapObjectives));

        // Export button + inline status.
        if (ImGui.Button("Export to HTML"))
            ExportRun(_selected);
        ImGui.SameLine();
        ImGui.TextDisabled(string.IsNullOrEmpty(_exportStatus)
            ? "export this run to a standalone HTML file"
            : _exportStatus);

        ImGui.Separator();

        // Main body: full-height loot column (left) + tabbed Map/Timeline pane (right). The map and loot
        // are what the user reviews, so they get the space; the timeline graph lives behind a tab.
        var bodyH = Math.Max(ImGui.GetContentRegionAvail().Y, 200f);

        ImGui.BeginChild("loot", new Vector2(280, bodyH), ImGuiChildFlags.Border);
        DrawLootFilter();
        ImGui.Separator();
        DrawPickupList();
        ImGui.Separator();
        DrawLootList();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("rightpane", new Vector2(0, bodyH), ImGuiChildFlags.Border);
        if (ImGui.BeginTabBar("statstabs"))
        {
            if (ImGui.BeginTabItem("Map"))
            {
                DrawMapPane(r);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Timeline"))
            {
                DrawTimelineTab(r);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.EndChild();
    }

    // Timeline tab: content indicator row, series toggles, the time graph (with green completion lines).
    private void DrawTimelineTab(MapRunRecord r)
    {
        CollectContent(out var names, out var completedAt);

        if (names.Count > 0)
        {
            for (var i = 0; i < names.Count; i++)
            {
                var n = names[i];
                var done = completedAt.ContainsKey(n); // I had a unicode checkmark here but it doesn't render
                ImGui.TextColored(done ? ContentDoneColor : ContentTodoColor, n);
                if (i < names.Count - 1) ImGui.SameLine();
            }
            ImGui.Separator();
        }

        ImGui.Checkbox("Gold", ref _showGold); ImGui.SameLine();
        ImGui.Checkbox("XP", ref _showXp); ImGui.SameLine();
        ImGui.Checkbox("Monsters", ref _showSeen); ImGui.SameLine();
        ImGui.Checkbox("Life", ref _showLife); ImGui.SameLine();
        ImGui.Checkbox("ES", ref _showEs); ImGui.SameLine();
        ImGui.Checkbox("Mana", ref _showMana);

        ImGui.Separator();

        if (_snapshots.Count >= 2)
            DrawGraph(r, completedAt);
        else
            ImGui.TextDisabled("Not enough snapshots to graph this run.");
    }

    // ---- Loot filter ----

    // Shared category combo at the top of the column; filters BOTH the looted and ground-loot lists.
    // "All" + the categories present across either list this run.
    private void DrawLootFilter()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##lootfilter", _lootFilter))
        {
            foreach (var c in _categories)
                if (ImGui.Selectable(c, c == _lootFilter))
                    _lootFilter = c;
            ImGui.EndCombo();
        }
        ApplyFilter();
    }

    // ---- Loot list ----

    private void DrawLootList()
    {
        if (_loot.Count == 0)
        {
            ImGui.Text("Dropped (0)");
            ImGui.Separator();
            ImGui.TextDisabled("No dropped (unlooted) items logged for this run.");
            return;
        }

        var shown = _lootShown;
        ImGui.Text("Dropped (" + shown.Count + ")");
        ImGui.SameLine();
        ImGui.TextDisabled("(click to locate)");
        ImGui.Separator();

        for (var i = 0; i < shown.Count; i++)
        {
            var row = shown[i];
            var l = (LootItem)row.Item;
            ImGui.PushStyleColor(ImGuiCol.Text, row.Color);
            ImGui.PushID(i);
            if (ImGui.Selectable(row.Label, ReferenceEquals(l, _selectedLoot)))
                _selectedLoot = ReferenceEquals(l, _selectedLoot) ? null : l;
            ImGui.PopID();
            ImGui.PopStyleColor();
        }
    }

    // ---- Pickup list ----

    private void DrawPickupList()
    {
        if (_pickups.Count == 0)
        {
            ImGui.Text("Looted (0)");
            ImGui.TextDisabled("No looted items logged for this run.");
            return;
        }

        var shown = _pickupShown;
        ImGui.Text("Looted (" + shown.Count + ")");
        ImGui.SameLine();
        ImGui.TextDisabled("(click to locate)");
        ImGui.Separator();

        for (var i = 0; i < shown.Count; i++)
        {
            var row = shown[i];
            var p = (PickupItem)row.Item;
            ImGui.PushStyleColor(ImGuiCol.Text, row.Color);
            ImGui.PushID(i);
            if (ImGui.Selectable(row.Label, ReferenceEquals(p, _selectedPickup)))
                _selectedPickup = ReferenceEquals(p, _selectedPickup) ? null : p;
            ImGui.PopID();
            ImGui.PopStyleColor();
        }
    }

    // Ex-per-divine rate for the open window (0 = unknown / NinjaPricer absent). Set each frame in Draw.
    private static double _divRate;

    // Adaptive currency display (the 5-div rule): render in divine once worth > 5 div, else exalted.
    // Falls back to exalted when the divine rate is unknown.
    private static string FmtCur(double ex) => Currency.Format(ex, _divRate);

    // Match key linking a ground drop to its pickup: both are read from the same item entity, so path +
    // rarity agree. Used to drop looted items from the drops list (see LoadRun).
    private static string LootKey(ItemRecord r) => $"{r.Path}|{r.Rarity}";

    // Broad item category from the item's class name (PoE2 `ClassName` from BaseItemTypes), for the loot
    // filter. Keyword-matched so new/unknown classes fall through to "Other".
    // internal so the activity report (ActivityReportGenerator) buckets pickups the same way.
    internal static string Category(ItemRecord l)
    {
        var c = (l.ClassName ?? "").ToLowerInvariant();
        if (c.Length == 0)
            return "Other";

        if (c.Contains("currency")) return "Currency";
        if (c.Contains("gem")) return "Gems";
        if (c.Contains("jewel")) return "Jewels";
        if (c.Contains("flask") || c.Contains("charm")) return "Flasks";
        if (c.Contains("map") || c.Contains("waystone") || c.Contains("tablet") ||
            c.Contains("fragment") || c.Contains("breachstone")) return "Maps";
        if (c.Contains("rune") || c.Contains("soul core") || c.Contains("talisman") ||
            c.Contains("omen") || c.Contains("relic")) return "Crafting";

        // Wearables + weapons → Equipment.
        if (c.Contains("armour") || c.Contains("helmet") || c.Contains("glove") || c.Contains("boot") ||
            c.Contains("shield") || c.Contains("ring") || c.Contains("amulet") || c.Contains("belt") ||
            c.Contains("quiver") || c.Contains("focus") ||
            c.Contains("sword") || c.Contains("axe") || c.Contains("mace") || c.Contains("bow") ||
            c.Contains("wand") || c.Contains("staff") || c.Contains("stave") || c.Contains("sceptre") ||
            c.Contains("spear") || c.Contains("dagger") || c.Contains("claw") || c.Contains("flail") ||
            c.Contains("crossbow") || c.Contains("buckler"))
            return "Equipment";

        return "Other";
    }

    private static Vector4 RarityColor(string rarity) => RarityPalette.Vec(rarity);

    // ---- Map pane (player path + death overlays) ----

    private void DrawMapPane(MapRunRecord r)
    {
        var hasSvg = _svg is { Width: > 0, Height: > 0 };
        if (_mapMissing || (!hasSvg && _mapTextureId == null))
        {
            ImGui.TextWrapped("No map for this run.");
            ImGui.TextDisabled("Map view requires syrairc's Radar fork.");
            return;
        }

        // Area grid dimensions for the coord transform: from the SVG viewBox, else the run record.
        var areaW = hasSvg ? _svg.Width : r.AreaWidth;
        var areaH = hasSvg ? _svg.Height : r.AreaHeight;

        // Content-icon controls (only when this run has content). Drawn above the canvas so it keeps space.
        if (_content.Count > 0)
        {
            ImGui.Checkbox("Content icons", ref _showContent);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.SliderFloat("size##content", ref _contentIconSize, 10f, 40f);
        }
        if (_wisps.Count > 0)
        {
            ImGui.Checkbox("Wisps", ref _showWisps);
        }
        if (_monsters.Count > 0)
        {
            ImGui.Checkbox("Monsters", ref _showMonsters);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.SliderFloat("size##monsters", ref _monsterDotSize, 2f, 12f);
        }

        // The whole pane is the canvas. An invisible button over it captures wheel (zoom) + drag (pan).
        var paneOrigin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var paneSize = new Vector2(Math.Max(avail.X, 50f), Math.Max(avail.Y, 50f));

        ImGui.InvisibleButton("mapcanvas", paneSize);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var io = ImGui.GetIO();

        // Fit-to-pane base size (zoom == 1), preserving the area aspect.
        float aspect = areaW > 0 && areaH > 0 ? areaH / (float)areaW : 1f;
        var fitW = paneSize.X;
        var fitH = fitW * aspect;
        if (fitH > paneSize.Y) { fitH = paneSize.Y; fitW = aspect > 0 ? fitH / aspect : fitW; }
        var fit = new Vector2(fitW, fitH);

        // Wheel zooms toward the cursor (keep the point under the mouse fixed).
        if (hovered && io.MouseWheel != 0)
        {
            var oldZoom = _mapZoom;
            _mapZoom = Math.Clamp(_mapZoom * (1f + io.MouseWheel * 0.1f), 0.2f, 40f);
            var ratio = _mapZoom / oldZoom;
            var rel = io.MousePos - paneOrigin;
            _mapPan = rel - (rel - _mapPan) * ratio;
        }

        // Left-drag pans; double-click resets the view.
        if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            _mapPan += io.MouseDelta;
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _mapZoom = 1f;
            _mapPan = default;
        }

        var size = fit * _mapZoom;
        var imgPos = paneOrigin + _mapPan;

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(paneOrigin, paneOrigin + paneSize, true);

        string contentHover = null;  // set when the mouse is over a content icon; shown as a tooltip below
        string wispHover = null;     // set when the mouse is over a wisp victim marker

        // grid -> screen transform.
        var sx = areaW > 0 ? size.X / areaW : 0f;
        var sy = areaH > 0 ? size.Y / areaH : 0f;
        Vector2 ToScreen(float gx, float gy) => new(imgPos.X + gx * sx, imgPos.Y + gy * sy);

        if (imgPos != _cacheImgPos || size != _cacheSize)
        {
            _cacheImgPos = imgPos;
            _cacheSize = size;
            RebuildScreenCache(sx, sy, imgPos);
        }

        if (hasSvg)
            DrawSvg(dl, ToScreen);
        else
        {
            try { dl.AddImage(_graphics.GetTextureId(_mapTextureId), imgPos, imgPos + size); }
            catch { /* texture gone */ }
        }

        // Overlays (player path + deaths) need the grid transform — skip if dimensions are unknown.
        if (areaW > 0 && areaH > 0)
        {
            // rebuild once the slider has sat still for 200ms, not on every drag frame
            if (_settings != null && _settings.MapRevealRadius.Value != _exploredRectsRadius)
            {
                if (_exploredDirtySince == 0) _exploredDirtySince = Environment.TickCount64;
                else if (Environment.TickCount64 - _exploredDirtySince > 200)
                {
                    BuildExploredOverlay();
                    _exploredDirtySince = 0;
                }
            }

            // Explored-area tint (under the path/markers): the reveal ∩ walkable cells this run covered,
            // as row-merged rects in grid units. Same reveal R + loop parser as the live explored %.
            if (_settings != null && _settings.ShowExploredTint.Value && _exploredRects.Count > 0)
            {
                var col = ImGui.GetColorU32(ToVec4(_settings.ExploredTintColor.Value));
                foreach (var (mn, mx) in _exploredRects)
                    dl.AddRectFilled(ToScreen(mn.X, mn.Y), ToScreen(mx.X, mx.Y), col);
            }

            // Player path: cached screen-space segments, split on teleport jumps (see RebuildScreenCache).
            foreach (var seg in _screenPath)
                dl.AddPolyline(ref seg[0], seg.Length, PathColor, ImDrawFlags.None, 2f);
            if (_pathGrid.Length > 0)
            {
                dl.AddCircleFilled(ToScreen(_pathGrid[0].X, _pathGrid[0].Y), 4f, StartColor);
                dl.AddCircleFilled(ToScreen(_pathGrid[^1].X, _pathGrid[^1].Y), 4f, EndColor);
            }

            foreach (var d in _deaths)
            {
                var p = ToScreen(d.GridX, d.GridY);
                dl.AddCircleFilled(p, 5f, DeathColor);
                dl.AddCircle(p, 7f, 0xFFFFFFFF, 0, 1.5f);
            }

            // Pickup positions: faint green dots where each item was picked up (player position at the time).
            foreach (var pk in _pickups)
                dl.AddCircleFilled(ToScreen(pk.PlayerGridX, pk.PlayerGridY), 3f, PickupDotColor);

            // Selected loot marker: a crosshair so it stands out against the path/death dots.
            if (_selectedLoot != null)
            {
                var p = ToScreen(_selectedLoot.GridX, _selectedLoot.GridY);
                dl.AddCircle(p, 8f, LootMarkerColor, 0, 2f);
                dl.AddLine(new Vector2(p.X - 11, p.Y), new Vector2(p.X - 4, p.Y), LootMarkerColor, 2f);
                dl.AddLine(new Vector2(p.X + 4, p.Y), new Vector2(p.X + 11, p.Y), LootMarkerColor, 2f);
                dl.AddLine(new Vector2(p.X, p.Y - 11), new Vector2(p.X, p.Y - 4), LootMarkerColor, 2f);
                dl.AddLine(new Vector2(p.X, p.Y + 4), new Vector2(p.X, p.Y + 11), LootMarkerColor, 2f);
            }

            // Selected pickup marker: a green crosshair at the pickup location.
            if (_selectedPickup != null)
            {
                var p = ToScreen(_selectedPickup.PlayerGridX, _selectedPickup.PlayerGridY);
                dl.AddCircle(p, 8f, PickupMarkerColor, 0, 2f);
                dl.AddLine(new Vector2(p.X - 11, p.Y), new Vector2(p.X - 4, p.Y), PickupMarkerColor, 2f);
                dl.AddLine(new Vector2(p.X + 4, p.Y), new Vector2(p.X + 11, p.Y), PickupMarkerColor, 2f);
                dl.AddLine(new Vector2(p.X, p.Y - 11), new Vector2(p.X, p.Y - 4), PickupMarkerColor, 2f);
                dl.AddLine(new Vector2(p.X, p.Y + 4), new Vector2(p.X, p.Y + 11), PickupMarkerColor, 2f);
            }

            // Monster first-seen positions: a small rarity-colored dot per distinct monster. Drawn under the
            // content/wisp markers so those interactables stay on top of the density dots.
            if (_showMonsters && _monsters.Count > 0)
            {
                foreach (var m in _monsters)
                {
                    var p = ToScreen(m.GridX, m.GridY);
                    dl.AddCircleFilled(p, _monsterDotSize, RarityColorU32(m.Rarity));
                }
            }

            // Map content icons (ritual / breach / strongbox / …) drawn from the shared Icons.png sheet at
            // their world position; completed content is dimmed. Hovering one queues a tooltip.
            if (_showContent && _content.Count > 0)
            {
                var texId = ContentTexId();
                if (texId != IntPtr.Zero)
                {
                    var half = _contentIconSize * 0.5f;
                    foreach (var c in _content)
                    {
                        ExileCore2.Shared.RectangleF uv;
                        try { uv = SpriteHelper.GetUV((MapIconsIndex)c.Icon); }
                        catch { continue; }

                        var p = ToScreen(c.GridX, c.GridY);
                        var a = new Vector2(p.X - half, p.Y - half);
                        var b = new Vector2(p.X + half, p.Y + half);
                        dl.AddImage(texId, a, b,
                            new Vector2(uv.X, uv.Y), new Vector2(uv.X + uv.Width, uv.Y + uv.Height),
                            ContentTintU32(c.Type, c.Completed));

                        if (hovered &&
                            io.MousePos.X >= a.X && io.MousePos.X <= b.X &&
                            io.MousePos.Y >= a.Y && io.MousePos.Y <= b.Y)
                            contentHover = ContentTooltip(c);
                    }
                }
            }

            // Wisp encounters: a diamond marker at the possessed rare's location, labelled with the slain
            // count; hover shows the full breakdown. Drawn over the content icons so it stands out.
            if (_showWisps && _wisps.Count > 0)
            {
                foreach (var w in _wisps)
                {
                    var p = ToScreen(w.VictimGridX, w.VictimGridY);
                    const float s = 7f;
                    var top = new Vector2(p.X, p.Y - s);
                    var right = new Vector2(p.X + s, p.Y);
                    var bot = new Vector2(p.X, p.Y + s);
                    var left = new Vector2(p.X - s, p.Y);
                    dl.AddQuadFilled(top, right, bot, left, WispMarkerFill);
                    dl.AddQuad(top, right, bot, left, WispMarkerEdge, 1.5f);
                    dl.AddText(new Vector2(p.X + s + 2, p.Y - s - 1), WispMarkerEdge,
                        w.SlainBuffedCount.ToString());

                    if (hovered &&
                        io.MousePos.X >= left.X && io.MousePos.X <= right.X &&
                        io.MousePos.Y >= top.Y && io.MousePos.Y <= bot.Y)
                        wispHover = WispTooltip(w);
                }
            }
        }

        dl.PopClipRect();

        var hover = wispHover ?? contentHover;
        if (hover != null)
            ImGui.SetTooltip(hover);

        // Hint (top-left of the pane).
        dl.AddText(paneOrigin + new Vector2(4, 4), 0x88FFFFFF, "scroll: zoom | drag: pan | dbl-click: reset");
    }

    // Draw the cached screen-space terrain loops, routes and target circles. Reprojected only when the
    // pan/zoom transform actually moves (see RebuildScreenCache), not every frame.
    private void DrawSvg(ImDrawListPtr dl, Func<float, float, Vector2> toScreen)
    {
        foreach (var loop in _screenLoops)
            if (loop.Length >= 2)
                dl.AddPolyline(ref loop[0], loop.Length, _svg.TerrainColor, ImDrawFlags.Closed, 1f);
        foreach (var (pts, color) in _screenRoutes)
            if (pts.Length >= 2)
                dl.AddPolyline(ref pts[0], pts.Length, color, ImDrawFlags.None, 1f);
        foreach (var (center, _, color) in _svg.Circles)
            dl.AddCircleFilled(toScreen(center.X, center.Y), 3f, color);
    }

    // Reprojects terrain/routes/path from grid to screen space for the current pan/zoom. Called only when
    // imgPos/size changed (DrawMapPane) or the selection changed (BuildPathGrid forces a rebuild).
    private void RebuildScreenCache(float sx, float sy, Vector2 imgPos)
    {
        _screenLoops.Clear();
        _screenRoutes.Clear();
        _screenPath.Clear();
        if (_svg != null)
        {
            foreach (var loop in _svg.TerrainLoops)
                _screenLoops.Add(Project(loop, sx, sy, imgPos));
            foreach (var (pts, color) in _svg.Routes)
                _screenRoutes.Add((Project(pts, sx, sy, imgPos), color));
        }
        // split the path on teleport jumps, each run becomes one polyline
        var seg = new List<Vector2>();
        for (var i = 0; i < _pathGrid.Length; i++)
        {
            if (i > 0 && Vector2.DistanceSquared(_pathGrid[i - 1], _pathGrid[i]) > _jumpSq)
            {
                if (seg.Count > 1) _screenPath.Add(seg.ToArray());
                seg.Clear();
            }
            seg.Add(new Vector2(imgPos.X + _pathGrid[i].X * sx, imgPos.Y + _pathGrid[i].Y * sy));
        }
        if (seg.Count > 1) _screenPath.Add(seg.ToArray());
    }

    private static Vector2[] Project(Vector2[] grid, float sx, float sy, Vector2 imgPos)
    {
        var o = new Vector2[grid.Length];
        for (var i = 0; i < grid.Length; i++)
            o[i] = new Vector2(imgPos.X + grid[i].X * sx, imgPos.Y + grid[i].Y * sy);
        return o;
    }

    // ---- Content icons ----

    // ImGui texture id for the shared game icon sheet (registered by the plugin in Initialise). IntPtr.Zero
    // when unavailable, in which case content icons are skipped.
    private IntPtr ContentTexId()
    {
        try { return _graphics?.GetTextureId("Icons.png") ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    // Per-type tint (from ContentCatalog) as an ImGui color; dimmed when the content is completed.
    private static uint ContentTintU32(string type, bool? completed)
    {
        var c = ContentCatalog.ForType(type)?.Tint ?? System.Drawing.Color.Gray;
        var v = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
        if (completed == true) { v.X *= 0.45f; v.Y *= 0.45f; v.Z *= 0.45f; v.W = 0.7f; }
        return ImGui.GetColorU32(v);
    }

    private string ContentTooltip(ContentSighting c)
    {
        var s = $"{c.Type} | seen {Clock(c.ElapsedSeconds)}";
        if (c.Completed == true)
            s += c.CompletedSeconds is { } cs ? $" | done {Clock(cs)}" : " | done";
        else if (c.Completed == false)
            s += " | pending";
        if (c.TributeGained is { } trib)
            s += $" | tribute {trib:N0}";
        if (c.Type == "Ritual" && c.RitualFavours is { Count: > 0 })
        {
            var bought = c.RitualFavours.Count(f => f.Purchased);
            s += $" | favours {bought}/{c.RitualFavours.Count}";
            if (c.RitualRerolls is { } rr && rr > 0)
                s += $", {rr} reroll{(rr == 1 ? "" : "s")}";
            var lines = c.RitualFavours
                .OrderByDescending(f => f.ChaosValue ?? 0)
                .Take(8)
                .Select(f =>
                {
                    var name = string.IsNullOrEmpty(f.UniqueName) ? f.BaseName : f.UniqueName;
                    if (f.StackSize > 1) name += $" x{f.StackSize}";
                    var val = f.ChaosValue is { } v && v > 0 ? $"  {FmtCur(v)}" : "";
                    return $"  {(f.Purchased ? "[bought] " : "")}{name}{val}";
                });
            s += "\noffered:\n" + string.Join("\n", lines);
        }
        if (c.Type == "Expedition")
        {
            if (c.RuneCount is { } rc)
                s += $" | {rc} runes";
            var rewards = c.OfferedRewards ?? c.RewardPool;
            if (rewards is { Count: > 0 })
            {
                var label = c.OfferedRewards != null ? "offered" : "pool";
                var lines = rewards.Take(5).Select(r =>
                    $"  {r.Name} x{r.Count}" + (r.Value is { } v && v > 0 ? $"  {FmtCur(v)}" : ""));
                s += $"\n{label}:\n" + string.Join("\n", lines);
            }
        }
        return s;
    }

    private static string WispTooltip(WispEncounter w)
    {
        var s = $"Wisp ({w.Variant}) | {w.SlainBuffedCount} slain of {w.BuffedCount} buffed" +
                $" | possessed {Clock(w.PossessedSeconds)}";
        if (w.DoublePossessed)
            s += " | double-possessed";
        return s;
    }

    private static string Clock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = (int)seconds;
        return $"{t / 60}:{t % 60:00}";
    }

    private static readonly uint LootMarkerColor = ImGui.GetColorU32(new Vector4(1.0f, 0.95f, 0.2f, 1f));
    private static readonly uint WispMarkerFill = ImGui.GetColorU32(new Vector4(0.7f, 0.3f, 1.0f, 0.5f));
    private static readonly uint WispMarkerEdge = ImGui.GetColorU32(new Vector4(0.85f, 0.6f, 1.0f, 1f));
    private static readonly uint PickupMarkerColor = ImGui.GetColorU32(new Vector4(0.2f, 1.0f, 0.4f, 1f));
    private static readonly uint PickupDotColor = ImGui.GetColorU32(new Vector4(0.2f, 1.0f, 0.4f, 0.5f));
    private static readonly uint PathColor = ImGui.GetColorU32(new Vector4(0.3f, 0.7f, 1.0f, 0.8f));
    private static readonly uint StartColor = ImGui.GetColorU32(new Vector4(0.2f, 1.0f, 0.3f, 1f));
    private static readonly uint EndColor = ImGui.GetColorU32(new Vector4(1.0f, 0.6f, 0.1f, 1f));

    // Monster dots, colored by rarity (White / Magic / Rare / Unique).
    // precomputed off the draw path: this runs per monster per frame
    private static readonly uint MonsterWhite = ImGui.GetColorU32(RarityPalette.Vec("White", 0.9f));
    private static readonly uint MonsterMagic = ImGui.GetColorU32(RarityPalette.Vec("Magic", 0.9f));
    private static readonly uint MonsterRare = ImGui.GetColorU32(RarityPalette.Vec("Rare", 0.9f));
    private static readonly uint MonsterUnique = ImGui.GetColorU32(RarityPalette.Vec("Unique", 0.9f));

    private static uint RarityColorU32(string rarity) => rarity switch
    {
        "Magic" => MonsterMagic,
        "Rare" => MonsterRare,
        "Unique" => MonsterUnique,
        _ => MonsterWhite,
    };

    // ---- Graph ----

    private static readonly uint AxisColor = ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.6f));
    private static readonly uint GoldColor = ImGui.GetColorU32(new Vector4(1.0f, 0.84f, 0.0f, 1f));
    private static readonly uint XpColor = ImGui.GetColorU32(new Vector4(0.4f, 0.8f, 1.0f, 1f));
    private static readonly uint SeenColor = ImGui.GetColorU32(new Vector4(1.0f, 0.4f, 0.4f, 1f));
    private static readonly uint DeathColor = ImGui.GetColorU32(new Vector4(1.0f, 0.1f, 0.1f, 0.9f));
    private static readonly uint LifeColor = ImGui.GetColorU32(new Vector4(0.9f, 0.2f, 0.2f, 0.8f));
    private static readonly uint EsColor = ImGui.GetColorU32(new Vector4(0.8f, 0.8f, 0.9f, 0.8f));
    private static readonly uint ManaColor = ImGui.GetColorU32(new Vector4(0.3f, 0.4f, 1.0f, 0.8f));
    private static readonly uint ContentLineColor = ImGui.GetColorU32(new Vector4(0.2f, 0.8f, 0.3f, 0.9f));
    private static readonly Vector4 ContentDoneColor = new(0.2f, 0.8f, 0.3f, 1f);
    private static readonly Vector4 ContentTodoColor = new(0.55f, 0.55f, 0.55f, 1f);

    private void DrawGraph(MapRunRecord r, Dictionary<string, double> completedAt)
    {
        const float pad = 8f;
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var size = new Vector2(Math.Max(avail.X, 100f), Math.Min(Math.Max(avail.Y - 140f, 120f), 320f));
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(0.07f, 0.07f, 0.08f, 1f)));

        var x0 = origin.X + pad;
        var x1 = origin.X + size.X - pad;
        var y0 = origin.Y + pad;             // top (frac = 1)
        var y1 = origin.Y + size.Y - pad;    // bottom (frac = 0)

        // Axes.
        dl.AddLine(new Vector2(x0, y1), new Vector2(x1, y1), AxisColor);
        dl.AddLine(new Vector2(x0, y0), new Vector2(x0, y1), AxisColor);

        var maxT = Math.Max(_snapshots[^1].ElapsedSeconds, r.DurationSeconds);
        if (maxT <= 0) maxT = 1;

        float TimeX(double t) => x0 + (float)(t / maxT) * (x1 - x0);
        float FracY(double f) => y1 - (float)f * (y1 - y0);

        if (_showGold)
            DrawSeries(dl, GoldColor, TimeX, FracY, s => s.Gold);
        if (_showXp)
            DrawSeries(dl, XpColor, TimeX, FracY, s => s.Xp);
        if (_showSeen)
            DrawSeries(dl, SeenColor, TimeX, FracY, s => s.MonstersSeen);

        // Vitals as fraction-of-max lines (0..1 mapped directly to the y-axis — full = top).
        if (_showLife)
            DrawFracSeries(dl, LifeColor, TimeX, FracY, s => Frac(s.Life, s.MaxLife));
        if (_showEs)
            DrawFracSeries(dl, EsColor, TimeX, FracY, s => Frac(s.EnergyShield, s.MaxEnergyShield));
        if (_showMana)
            DrawFracSeries(dl, ManaColor, TimeX, FracY, s => Frac(s.Mana, s.MaxMana));

        // Death markers.
        foreach (var d in _deaths)
        {
            var dx = TimeX(d.ElapsedSeconds);
            dl.AddLine(new Vector2(dx, y0), new Vector2(dx, y1), DeathColor, 2f);
        }

        // Content-completion lines (green, labeled), staggered vertically so near-simultaneous
        // completions don't overprint their labels.
        var ci = 0;
        foreach (var kv in completedAt)
        {
            var cx = TimeX(kv.Value);
            dl.AddLine(new Vector2(cx, y0), new Vector2(cx, y1), ContentLineColor, 1.5f);
            dl.AddText(new Vector2(cx + 2, y0 + 16 + (ci % 3) * 14f), ContentLineColor, kv.Key);
            ci++;
        }

        // Legend.
        var lx = x0 + 4;
        var ly = y0 + 2;
        if (_showGold) { dl.AddText(new Vector2(lx, ly), GoldColor, "Gold"); lx += 40; }
        if (_showXp) { dl.AddText(new Vector2(lx, ly), XpColor, "XP"); lx += 28; }
        if (_showSeen) { dl.AddText(new Vector2(lx, ly), SeenColor, "Monsters"); lx += 70; }
        if (_showLife) { dl.AddText(new Vector2(lx, ly), LifeColor, "Life"); lx += 36; }
        if (_showEs) { dl.AddText(new Vector2(lx, ly), EsColor, "ES"); lx += 26; }
        if (_showMana) { dl.AddText(new Vector2(lx, ly), ManaColor, "Mana"); }

        ImGui.Dummy(size);
    }

    private void DrawSeries(ImDrawListPtr dl, uint color, Func<double, float> timeX, Func<double, float> fracY,
        Func<Snapshot, double> value)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in _snapshots)
        {
            var v = value(s);
            if (v < min) min = v;
            if (v > max) max = v;
        }
        var span = max - min;

        Vector2? prev = null;
        foreach (var s in _snapshots)
        {
            var frac = span <= 0 ? 0.5 : (value(s) - min) / span;
            var p = new Vector2(timeX(s.ElapsedSeconds), fracY(frac));
            if (prev is { } pp)
                dl.AddLine(pp, p, color, 1.5f);
            dl.AddCircleFilled(p, 2f, color);
            prev = p;
        }
    }

    // A vitals series (life / ES / mana) plotted as a fraction-of-max line: the fraction (0..1) maps
    // straight to the y-axis (full = top), unlike DrawSeries which min/max-normalizes per series.
    private void DrawFracSeries(ImDrawListPtr dl, uint color, Func<double, float> timeX,
        Func<double, float> fracY, Func<Snapshot, float> frac)
    {
        Vector2? prev = null;
        foreach (var s in _snapshots)
        {
            var p = new Vector2(timeX(s.ElapsedSeconds), fracY(frac(s)));
            if (prev is { } pp)
                dl.AddLine(pp, p, color, 1.5f);
            dl.AddCircleFilled(p, 2f, color);
            prev = p;
        }
    }

    // ---- Content collection ----

    // Distinct content names seen across this run's snapshots, plus the earliest elapsed-seconds at which
    // each was marked Completed. Drives the Timeline tab's indicator row and the graph's green lines.
    private void CollectContent(out List<string> names, out Dictionary<string, double> completedAt)
    {
        names = new List<string>();
        completedAt = new Dictionary<string, double>();
        foreach (var s in _snapshots)
        {
            if (s.Content == null) continue;
            foreach (var c in s.Content)
            {
                if (!names.Contains(c.Name)) names.Add(c.Name);
                if (c.Completed && !completedAt.ContainsKey(c.Name))
                    completedAt[c.Name] = s.ElapsedSeconds;
            }
        }
    }

    // ---- helpers ----

    private static float Frac(int cur, int max) => max <= 0 ? 0f : Math.Clamp(cur / (float)max, 0f, 1f);
    private static string Sign(long v) => v >= 0 ? $"+{v:N0}" : $"{v:N0}";
}
