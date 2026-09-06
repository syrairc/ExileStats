using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace ExileStats;

public partial class ExileStats : BaseSettingsPlugin<ExileStatsSettings>
{
    private readonly MonsterCounter _monsterCounter = new();
    private readonly LootTracker _lootTracker = new();
    private readonly PickupTracker _pickupTracker = new();
    private readonly PathTracker _pathTracker = new();
    private readonly ContentTracker _contentTracker = new();
    private readonly RitualRewards _ritualRewards = new();
    private readonly WispTracker _wispTracker = new();
    private readonly StashTracker _stashTracker = new();
    private MapRunRecord _currentArea;
    // when this visit started. MapRunRecord.EnteredAt is the instance's first entry, which swallows a
    // hideout round trip into the map's duration
    private DateTime _areaEnteredUtc = DateTime.UtcNow;

    private double ElapsedSeconds() => (DateTime.UtcNow - _areaEnteredUtc).TotalSeconds;

    // Current run-grouping id (see MapRunRecord.RunId): a run spans from leaving a town/hideout until
    // returning to one, so a map + its sub-areas (Abyssal Depths, boss arenas, …) share one id. 0 = no
    // active run (currently in a town/hideout).
    private int _currentRunId;
    private int _lastRunId;         // most recent run opened, so re-entering its instance resumes it
    private long _lastRunHash;
    private DateTime _lastSettingsDraw;
    private IngameUIElements _ingameUi;
    private bool _overlaysVisible;   // computed once per Tick, read by every on-screen readout

    // Pending Radar map-image capture for the current map run.
    private bool _capturePending;
    private DateTime _captureAt;        // earliest time to attempt (entry + delay)
    private DateTime _captureDeadline;  // give up after this (captureAt + 10s)

    // Map objectives/content are read from the side panel once it's populated, then frozen for the run.
    private bool _mapInfoCaptured;

    // Drives every per-Tick tracker: gates each by toggle + tracked-area + interval, builds one shared
    // entity-bucket view per tick, and times each (profiler). Built in Initialise; see ExileStats.Dispatch.cs.
    private TrackerDispatcher _dispatcher;

    // Death detection. _wasAlive is the last observed alive state (player valid + CurHP > 0); a true->false
    // edge while in a map is a death. _lastPlayerPos is the most recent alive position (the player entity
    // can go null the instant you die, so we record the death there). Reset on area entry.
    private bool _wasAlive;
    private Vector2 _lastPlayerPos;

    // Map Statistics window state.
    private readonly MapStatsWindow _statsWindow = new();
    private bool _statsOpen;

    // Statistics overlay (xp bar + rates). _runRates holds the rolling per-run history the averages read;
    // _hasOmen + _divRate are refreshed by the Slow tracker so Render does no scanning.
    private readonly StatsOverlay _statsOverlay = new();
    private readonly RunRates _runRates = new();
    private bool _hasOmen;
    private double _divRate;

    // Cached result of the in-settings layout-classifier validation run (null until "Run validation").
    private List<LayoutValidation.Row> _validationRows;
    private string _dashboardStatus;

    // Net-worth (stash) logging state. Throttle the writes while the stash is open; only append a new
    // time-series point when the value moved meaningfully.
    private DateTime _nextNetWorthWriteAt;
    private double _lastNetWorthExalted;
    private double _netWorthPeakExalted;   // session peak; partial-tab reads crater far below it -> skip
    private bool _netWorthInitialized;

    // Net worth below this fraction of the session peak is a partial-tab read (only some tabs streamed in) -
    // never recorded; net worth doesn't realistically crater to near-zero. Mirrors the report-side filter.
    private const double MinNetWorthFraction = 0.25;

    public override bool Initialise()
    {
        _dispatcher = BuildDispatcher();

        Input.RegisterKey(Settings.OpenStatsKey.Value);
        Settings.OpenStatsKey.OnValueChanged += () => Input.RegisterKey(Settings.OpenStatsKey.Value);

        // Register the shared game icon sheet so the Map Statistics window can draw content icons from it
        // (works even if MinimapIcons isn't loaded). Best-effort.
        try { Graphics.InitImage("Icons.png"); }
        catch { /* icon sheet unavailable; content icons just won't draw */ }

        // Seed net worth from the last saved per-tab snapshot so it shows immediately after a reload
        // (each tab is then refreshed as the player reopens it).
        try { _stashTracker.Seed(StashLog.ReadTabs(DirectoryFullName)); }
        catch { /* no prior stash data */ }
        // Seed the session peak from the restored tabs so a reload doesn't accept a partial read until the
        // first full rescan rebuilds the peak.
        _netWorthPeakExalted = _stashTracker.TotalExalted;

        // Warm the overlay's rate averages from recent history so they read correctly right after a reload.
        _runRates.Seed(DirectoryFullName, 6);

        // Prime the divine rate once up front so it's not 0 for the ~1s before the Slow tracker's first run.
        try { _divRate = ItemPricer.GetDivineRate(GameController) ?? 0; }
        catch { /* pricing plugin not loaded yet */ }

        // Capture the current area now so the counter works when the plugin loads mid-map.
        try
        {
            if (GameController?.Area?.CurrentArea is { } cur)
                EnterArea(cur, coldStart: true);
        }
        catch { /* not in an area yet */ }

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        // Flush any path points still buffered for the area we're leaving (uses the tracker's stored
        // old-area identity, so it must happen before SetArea below points it at the new area).
        FlushPath();

        // Leaving any tracked area (not town/hideout) into anything: log the finished area's stats + counts.
        // Campaign zones go zone->zone (never via a hideout), so we log on every exit, not only Map->Hideout.
        if (Settings.LogToFile && IsTracked(_currentArea))
            TryLogMapRun();

        // Drain the leaving area's buffered monster positions to the OLD instance before Reset clears them.
        if (Settings.LogMonsterPositions && IsTracked(_currentArea))
            FlushMonsters(_currentArea.AreaId, _currentArea.InstanceHash);

        _monsterCounter.Reset();

        // Snapshot the new area's metadata now, while its memory is valid, so the next transition can
        // log it after we've left it.
        EnterArea(area);
    }

    // everything that happens on entering an area. Initialise (mid-map load) and AreaChange both come here
    private void EnterArea(AreaInstance area, bool coldStart = false)
    {
        _currentArea = CaptureArea(area);
        // live path starts the visit now, cold start backdates to the game's area-entry time
        if (coldStart)
        {
            var entered = area.TimeEntered.ToUniversalTime();
            _areaEnteredUtc = entered > DateTime.UtcNow ? DateTime.UtcNow : entered;
        }
        else
        {
            _areaEnteredUtc = DateTime.UtcNow;
        }
        AssignRunId(area);
        ScheduleMapImageCapture();
        BeginLootForArea();
        BeginContentForArea();
        BeginWispForArea();
        BeginMonstersForArea();
        _pickupTracker.SetArea(_currentArea.ZoneSwitchId);
        _pathTracker.SetArea(_currentArea.AreaId, _currentArea.InstanceHash, _currentArea.ZoneSwitchId);
        _mapInfoCaptured = false;
        _dispatcher.OnAreaChange(Settings);
        _wasAlive = false;
        _lastPlayerPos = default;
    }

    public override void Tick()
    {
        _ingameUi = GameController.Game.IngameState.IngameUi;
        _overlaysVisible = ComputeOverlaysVisible();

        // Keep the area's monster level fresh (ServerData can be stale at the AreaChange tick).
        if (IsTracked(_currentArea))
        {
            try { _currentArea.MonsterLevel = GameController.IngameState.ServerData.MonsterLevel; }
            catch { /* server data not ready */ }
        }

        if (_capturePending && DateTime.Now >= _captureAt)
            TryCaptureMapImage();

        // All per-Tick scanning runs through the dispatcher: one shared entity-bucket view, each tracker
        // gated by its toggle + tracked-area + interval, isolated in its own try/catch. See ExileStats.Dispatch.cs.
        var elapsed = _currentArea == null ? 0 : ElapsedSeconds();
        _dispatcher.Tick(this, GameController, Settings, _currentArea, elapsed, IsTracked(_currentArea));
    }

    public override void Render()
    {
        if (!GameController.InGame)
            return;

        if (Settings.OpenStatsKey.PressedOnce())
            _statsOpen = !_statsOpen;

        if (_statsOpen)
            _statsWindow.Draw(Graphics, DirectoryFullName, Settings, GameController, _divRate,
                _dispatcher.Profiler, ref _statsOpen);

        DrawMonsterCount();
        DrawNetWorth();
        DrawStatsOverlay();
        DrawProfiler();
    }

    // ---- On-screen statistics overlay ----

    // XP bar + xp/hour, xp/map, maps/hour, time + maps to the next level, and the missing-omen warning.
    // Same visibility rule as the monster counter (in a tracked area, or while the config is open, and never
    // over a large/fullscreen panel). All game reads happen here so StatsOverlay stays UI-only.
    private void DrawStatsOverlay()
    {
        if (!Settings.ShowStatsOverlay)
            return;
        if (!_overlaysVisible)
            return;

        int level;
        long xp;
        try
        {
            var p = GameController.Player?.GetComponent<Player>();
            if (p == null)
                return;
            level = p.Level;
            xp = p.XP;
        }
        catch { return; }   // player not readable this frame

        var data = new OverlayData(level, XpLevels.Progress(level, xp), XpLevels.Remaining(level, xp),
            _runRates.Compute(Settings.OverlayAvgRuns.Value), _hasOmen, _divRate);
        _statsOverlay.Draw(Settings, data);
    }

    // Shared gate for the on-screen readouts: visible in a tracked area, or while this plugin's config page
    // is open (so panels can be positioned), but never on top of a large/fullscreen or side panel.
    private bool ComputeOverlaysVisible()
    {
        if ((DateTime.Now - _lastSettingsDraw).TotalMilliseconds < 250)
            return true;
        if (!IsTracked(_currentArea))
            return false;
        return _ingameUi == null ||
               !(_ingameUi.FullscreenPanels.Any(x => x.IsVisible) ||
                 _ingameUi.LargePanels.Any(x => x.IsVisible) ||
                 _ingameUi.OpenLeftPanel.Address != 0 ||
                 _ingameUi.OpenRightPanel.Address != 0);
    }

    // ---- On-screen tracker profiler overlay ----

    // Per-tracker timing (last / avg / max ms), gated by ShowProfiler + active profiling. A dev aid to spot
    // which monitor costs the most as new trackers are added.
    private void DrawProfiler()
    {
        if (!Settings.ShowProfiler || _dispatcher?.Profiler is not { Enabled: true } prof)
            return;
        var stats = prof.Stats;
        if (stats.Count == 0)
            return;

        var pos = new Vector2(Settings.ProfilerPositionX.Value, Settings.ProfilerPositionY.Value);
        // Header uses the exact same column widths as the rows (monospace font) so the labels sit over their
        // columns. Columns separated by '|'.
        var header = $"  {"Tracker",-10}{"last",5} | {"avg",5} | {"max",5}  (ms)";
        pos.Y += Graphics.DrawText(header, pos, Color.White).Y;
        foreach (var kv in stats)
        {
            var s = kv.Value;
            // Rounded to whole ms (sub-ms churns/frame). Fixed-width columns so the digit count never shifts
            // the following text: name left-padded, each number right-aligned.
            var name = kv.Key + ":";
            var line = $"  {name,-10}{s.LastMs,5:0} | {s.AvgMs,5:0} | {s.MaxMs,5:0} ms";
            pos.Y += Graphics.DrawText(line, pos, Color.FromArgb(255, 220, 120)).Y;
        }
    }

    // DrawSettings (custom grouped ImGui settings screen) + its node helpers live in ExileStats.Settings.cs.

    // Regenerates map_dashboard.html from the maps/ run.json files + layouts.json. Offline analysis tool,
    // same family as the layout validation below; the generator reads the json itself, no live state needed.
    private void DrawDashboardTool()
    {
        if (!ImGui.CollapsingHeader("Map dashboard"))
            return;

        if (ImGui.Button("Generate dashboard"))
        {
            try
            {
                var outPath = Path.Combine(DirectoryFullName, "map_dashboard.html");
                DashboardGenerator.Generate(
                    Path.Combine(DirectoryFullName, "maps"),
                    Path.Combine(DirectoryFullName, "layouts.json"),
                    outPath);
                _dashboardStatus = "Wrote " + Path.GetFileName(outPath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(outPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _dashboardStatus = "Failed: " + ex.Message;
                LogError($"ExileStats -> Dashboard generation failed: {ex}");
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_dashboardStatus ?? "rebuilds map_dashboard.html from maps/ + layouts.json");
    }

    // In-settings layout-classifier validation: classify every captured map.svg under maps/ and compare
    // to the hand-labelled layouts.json. Lets the layout classifier be checked in-game without rebuilding.
    private void DrawLayoutValidation()
    {
        if (!ImGui.CollapsingHeader("Layout classifier validation"))
            return;

        if (ImGui.Button("Run validation"))
            _validationRows = LayoutValidation.Run(DirectoryFullName);

        if (_validationRows == null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("classifies maps/*/map.svg vs layouts.json");
            return;
        }

        if (_validationRows.Count == 0)
        {
            ImGui.TextDisabled("No map.svg files found under maps/.");
            return;
        }

        var labelled = _validationRows.Where(r => r.Label != "(none)" && r.Label != "unclassified").ToList();
        int agree = labelled.Count(r => r.Agree);
        ImGui.SameLine();
        ImGui.Text(labelled.Count > 0
            ? $"agreement: {agree}/{labelled.Count} ({100.0 * agree / labelled.Count:0.0}%)"
            : "no hand labels to compare against");

        var green = new Vector4(0.45f, 0.85f, 0.45f, 1f);
        var red = new Vector4(0.90f, 0.45f, 0.40f, 1f);
        var grey = new Vector4(0.65f, 0.65f, 0.62f, 1f);

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                      ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("layoutValidation", 5, flags, new Vector2(0, 320)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("MapId");
            ImGui.TableSetupColumn("Predicted");
            ImGui.TableSetupColumn("Conf");
            ImGui.TableSetupColumn("Hand-labelled");
            ImGui.TableSetupColumn("OK");
            ImGui.TableHeadersRow();

            foreach (var r in _validationRows)
            {
                bool hasLabel = r.Label != "(none)" && r.Label != "unclassified";
                ImGui.TableNextRow();

                ImGui.TableNextColumn(); ImGui.Text(r.MapId);
                ImGui.TableNextColumn(); ImGui.Text(r.Predicted);
                ImGui.TableNextColumn(); ImGui.Text(r.PredConf.ToString("0.00"));
                ImGui.TableNextColumn(); ImGui.Text(string.IsNullOrEmpty(r.LabelConf) ? r.Label : $"{r.Label} ({r.LabelConf})");
                ImGui.TableNextColumn();
                if (!hasLabel) ImGui.TextColored(grey, "-");
                else if (r.Agree) ImGui.TextColored(green, "Y");
                else ImGui.TextColored(red, "X");
            }
            ImGui.EndTable();
        }

        // Flat list of disagreements, with the extracted features, to help diagnose/tune misses.
        var misses = labelled.Where(r => !r.Agree).ToList();
        if (misses.Count > 0 && ImGui.TreeNode($"Disagreements ({misses.Count})"))
        {
            foreach (var r in misses)
                ImGui.TextWrapped($"{r.MapId}: predicted {r.Predicted}, labelled {r.Label}  |  {r.Features}");
            ImGui.TreePop();
        }
    }

    // ---- Area capture & logging ----

    // An area we track + log: not a town, not a hideout, and a real area (non-empty WorldArea id). When
    // "Log all areas" is off this narrows to maps only (the original behavior). Drives every per-Tick
    // tracker gate and the run-logging trigger, replacing the old IsMapArea-only check.
    private bool IsTracked(MapRunRecord a) =>
        a is { IsTown: false, IsHideout: false } && a.AreaId.Length > 0 &&
        (Settings.LogAllAreas.Value || a.IsMapArea);

    // Update the run-grouping id after entering a new area, then stamp it onto _currentArea. A run is
    // bounded by town/hideout stays: entering a tracked area from a town/hideout (or at cold start) resumes
    // the last run if it's the same instance (a stash trip or death didn't end the run), else opens a new
    // run keyed by that area's ZoneSwitchId; a sub-area entered from within the run keeps the id (so a
    // map and its Abyssal Depths / boss arena group together); a town/hideout closes the run. Mirrors how a
    // map run always begins and ends in the hideout.
    private void AssignRunId(AreaInstance area)
    {
        if (area.IsTown || area.IsHideout)
        {
            _currentRunId = 0;
        }
        else if (_currentRunId == 0)
        {
            // back into the instance the last run started in: same run, not a new one
            if (_lastRunId != 0 && _currentArea.InstanceHash == _lastRunHash)
            {
                _currentRunId = _lastRunId;
            }
            else
            {
                _currentRunId = _currentArea.ZoneSwitchId;
                _lastRunId = _currentRunId;
                _lastRunHash = _currentArea.InstanceHash;
            }
        }
        _currentArea.RunId = _currentRunId;
    }

    private MapRunRecord CaptureArea(AreaInstance area)
    {
        var wa = area.Area;
        var areaId = wa?.Id ?? "";
        var record = new MapRunRecord
        {
            EnteredAt = area.TimeEntered,
            Name = area.Name,
            DisplayName = area.DisplayName,
            Act = area.Act,
            RealLevel = area.RealLevel,
            IsTown = area.IsTown,
            IsHideout = area.IsHideout,
            IsPeaceful = area.IsPeaceful,
            HasWaypoint = area.HasWaypoint,
            InstanceHash = area.Hash,
            ZoneSwitchId = area.ZoneSwitchId,
            AreaId = areaId,
            Index = wa?.Index ?? 0,
            AreaLevel = wa?.AreaLevel ?? 0,
            WorldAreaId = wa?.WorldAreaId ?? 0,
            IsUnique = wa?.IsUnique ?? false,
            IsMapArea = InstanceStore.IsMapAreaId(areaId),
        };

        // Area grid dimensions (for the map-view coord transform). Saved per instance — varies per map.
        try
        {
            var dim = GameController.IngameState.Data.AreaDimensions;
            record.AreaWidth = dim.X;
            record.AreaHeight = dim.Y;
        }
        catch { /* data not ready */ }

        // Session/server baselines (Gold & XP are account-wide → captured here for an end-of-run delta).
        try
        {
            var sd = GameController.IngameState.ServerData;
            record.League = sd.League;
            record.MonsterLevel = sd.MonsterLevel;
            record.CharacterLevel = sd.CharacterLevel;
            record.ServerInstanceId = sd.InstanceId;
            record.GoldStart = sd.Gold;
            record.XpStart = GameController.Player?.GetComponent<Player>()?.XP ?? 0;
        }
        catch { /* server data not ready yet */ }

        return record;
    }

    private void TryLogMapRun()
    {
        try
        {
            var record = _currentArea;
            record.LoggedAt = DateTime.Now;
            record.DurationSeconds = Math.Round(ElapsedSeconds(), 1);

            // End-of-run server values + deltas.
            try
            {
                var sd = GameController.IngameState.ServerData;
                record.GoldEnd = sd.Gold;
                record.GoldGained = record.GoldEnd - record.GoldStart;
                record.XpEnd = GameController.Player?.GetComponent<Player>()?.XP ?? 0;
                record.XpGained = record.XpEnd - record.XpStart;
            }
            catch { /* server data unavailable */ }

            record.MonstersTotal = _monsterCounter.SeenTotal;
            record.White = _monsterCounter.Seen(MonsterRarity.White);
            record.Magic = _monsterCounter.Seen(MonsterRarity.Magic);
            record.Rare = _monsterCounter.Seen(MonsterRarity.Rare);
            record.Unique = _monsterCounter.Seen(MonsterRarity.Unique);
            record.MonstersByType = _monsterCounter.SeenByTypeSnapshot();
            record.UniqueMonsters = _monsterCounter.UniqueNamesSnapshot();

            // Skip trivial transit areas: when "Log all areas" surfaces a non-map zone, only log it if we
            // lingered (>= MinTrackedAreaSeconds) AND saw at least one monster. Drops load-corridors you just
            // ran through. Maps (IsMapArea) are always logged, no matter how short.
            if (!record.IsMapArea &&
                (record.DurationSeconds < Settings.MinTrackedAreaSeconds.Value || record.MonstersTotal < 1))
                return;

            // Total NinjaPricer value of loot dropped / picked up this visit (0 if pricing was unavailable).
            try
            {
                var zid = record.ZoneSwitchId;
                record.LootValue = LootLog.Read(DirectoryFullName, record.AreaId, record.InstanceHash)
                    .Where(l => l.ZoneSwitchId == zid).Sum(l => l.ChaosValue ?? 0);
                record.PickupValue = PickupLog.Read(DirectoryFullName, record.AreaId, record.InstanceHash)
                    .Where(p => p.ZoneSwitchId == zid).Sum(p => p.ChaosValue ?? 0);
            }
            catch { /* value totals are best-effort */ }

            // Auto-classify the layout from the captured walkable geometry. map.svg is grabbed during the
            // run (Tick, ~MapImageDelaySeconds after entry) and overwritten each visit, so by this
            // map->hideout transition the current visit's svg is on disk. Sub-delay exits leave it null.
            try
            {
                var svgPath = InstanceStore.FilePath(
                    DirectoryFullName, record.AreaId, record.InstanceHash, InstanceStore.SvgFile);
                if (File.Exists(svgPath) && record.AreaWidth > 0 && record.AreaHeight > 0)
                {
                    var loops = LayoutClassifier.ParseTerrainLoops(File.ReadAllText(svgPath));
                    var (type, conf) = LayoutClassifier.Classify(loops, record.AreaWidth, record.AreaHeight,
                        new RunContext(record.MonstersTotal, record.MapObjectives, record.AreaId));
                    record.LayoutType = LayoutTypeNames.ToSnake(type);
                    record.LayoutConfidence = Math.Round(conf, 3);
                }
            }
            catch (Exception ex) { LogError($"ExileStats -> Layout classify failed: {ex}"); }

            // Map exploration %: walkable coverage of the travelled path at the slider-set reveal radii.
            try
            {
                if (Settings.LogExploration)
                {
                    // Radii come straight from the settings sliders — no per-Tick measurement / calibration.
                    // Exploration uses the tight map-reveal radius (terrain you uncovered); density uses the
                    // wider monster-reveal radius (the corridor in which monsters are actually seen).
                    var rReveal = Settings.MapRevealRadius.Value;
                    record.RevealRadiusUsed = rReveal;

                    var svgPath = InstanceStore.FilePath(
                        DirectoryFullName, record.AreaId, record.InstanceHash, InstanceStore.SvgFile);
                    if (rReveal > 0f && File.Exists(svgPath) && record.AreaWidth > 0 && record.AreaHeight > 0)
                    {
                        var loops = LayoutClassifier.ParseTerrainLoops(File.ReadAllText(svgPath));
                        var pathPts = PathLog.Read(DirectoryFullName, record.AreaId, record.InstanceHash)
                            .Where(pp => pp.Z == record.ZoneSwitchId)
                            .Select(pp => new Vector2(pp.X, pp.Y))
                            .ToList();
                        pathPts = MapGeometry.WalkablePath(pathPts, loops);
                        var cov = MapCoverage.Compute(record.AreaWidth, record.AreaHeight, loops, pathPts, rReveal);
                        if (cov is { } c)
                        {
                            record.ExploredPercent = Math.Round(c.Percent, 1);
                            record.ExploredWalkable = c.ExploredWalkable;
                            record.TotalWalkable = c.TotalWalkable;
                        }
                        // Density denominator: explored walkable within the monster-reveal radius.
                        var rMonster = Settings.MonsterRevealRadius.Value;
                        record.MonsterRadiusUsed = rMonster;
                        var covD = MapCoverage.Compute(record.AreaWidth, record.AreaHeight, loops, pathPts, rMonster);
                        if (covD is { } cd)
                            record.DensityWalkable = cd.ExploredWalkable;
                    }
                }
            }
            catch (Exception ex) { LogError($"ExileStats -> Exploration calc failed: {ex}"); }

            MapMonsterLog.Append(DirectoryFullName, record.AreaId, record.InstanceHash, record);
            RunIndex.Append(DirectoryFullName, record);
            _runRates.Add(record);   // feeds the overlay's xp/map + maps/hour averages
        }
        catch (Exception ex)
        {
            LogError($"ExileStats -> Failed to log map run: {ex}");
        }
    }

    // ---- Radar map image capture ----

    // Arm a delayed capture when we enter a map. The image is grabbed in Tick once the delay elapses,
    // retrying each tick (Radar may not have explored the area yet) until success or the deadline.
    private void ScheduleMapImageCapture()
    {
        _capturePending = false;
        if (!Settings.SaveMapImage || !IsTracked(_currentArea))
            return;

        var now = DateTime.Now;
        _captureAt = now.AddSeconds(Settings.MapImageDelaySeconds.Value);
        _captureDeadline = _captureAt.AddSeconds(10);
        _capturePending = true;
    }

    private void TryCaptureMapImage()
    {
        var pastDeadline = DateTime.Now >= _captureDeadline;
        try
        {
            // Prefer the infinitely-scalable SVG (forked Radar). Fall back to the PNG if SVG is unavailable.
            var getSvg = GameController.PluginBridge.GetMethod<Func<bool, string>>("Radar.GetMapSvg");
            var svg = getSvg?.Invoke(Settings.MapImageOverlay.Value);

            if (!string.IsNullOrEmpty(svg))
            {
                var rec = _currentArea;
                rec.MapImageFile = MapImageStore.SaveSvg(
                    DirectoryFullName, rec.AreaId, rec.InstanceHash, svg);
                _capturePending = false;
                return;
            }

            var getPng = GameController.PluginBridge.GetMethod<Func<bool, byte[]>>("Radar.GetMapImage");
            var png = getPng?.Invoke(Settings.MapImageOverlay.Value);

            if (png is { Length: > 0 })
            {
                var rec = _currentArea;
                rec.MapImageFile = MapImageStore.Save(
                    DirectoryFullName, rec.AreaId, rec.InstanceHash, png);
                _capturePending = false;
                return;
            }

            // Not ready yet (Radar not loaded, or map not generated). Keep retrying until the deadline.
            if (pastDeadline)
            {
                _capturePending = false;
                LogMessage(getSvg == null && getPng == null
                    ? "ExileStats -> Radar map methods unavailable (Radar plugin not loaded?); skipped map image."
                    : "ExileStats -> Radar returned no map before the deadline; skipped.");
            }
        }
        catch (Exception ex)
        {
            _capturePending = false;
            LogError($"ExileStats -> Failed to capture map image: {ex}");
        }
    }

    // Flush buffered player-path points to the instance's path.json (uses the tracker's own stored area
    // identity, so it's safe to call right before re-pointing the tracker at a new area).
    private void FlushPath()
    {
        try
        {
            var pts = _pathTracker.Drain();
            if (pts is { Count: > 0 })
                PathLog.Append(DirectoryFullName, _pathTracker.AreaId, _pathTracker.InstanceHash, pts);
        }
        catch (Exception ex)
        {
            LogError($"ExileStats -> Failed to flush player path: {ex}");
        }
    }

    // Flush buffered monster first-seen positions to the given instance's monsters.json. Pass the OLD
    // area's identity when called on area exit (before the counter is reset).
    private void FlushMonsters(string areaId, long instanceHash)
    {
        try
        {
            var newM = _monsterCounter.DrainSightings();
            if (newM.Count > 0)
                MonsterLog.Append(DirectoryFullName, areaId, instanceHash, newM);
        }
        catch (Exception ex)
        {
            LogError($"ExileStats -> Failed to flush monster positions: {ex}");
        }
    }

    // On entering a map, seed the monster collector's fingerprint dedup-set from the instance's existing
    // monsters.json (so re-entry / restart doesn't re-log) and apply the logging settings.
    private void BeginMonstersForArea()
    {
        if (!Settings.LogMonsterPositions || !IsTracked(_currentArea))
            return;

        try
        {
            _monsterCounter.SetArea(
                MonsterLog.LoadFingerprints(DirectoryFullName, _currentArea.AreaId, _currentArea.InstanceHash),
                _currentArea.ZoneSwitchId, Settings.LogMonsterPositions, Settings.MonsterPositionMinRarity.Value,
                Settings.MonsterPositionDetailed.Value);
        }
        catch (Exception ex)
        {
            LogError($"ExileStats -> Failed to seed monster collector: {ex}");
        }
    }

    // ---- Ground loot ----

    // On entering a map, seed the loot tracker's dedup set from the instance's existing loot.json so
    // re-entering the same instance (or restarting the game) doesn't re-log items already on file.
    private void BeginLootForArea()
    {
        if (!Settings.LogLoot || !IsTracked(_currentArea))
            return;

        try
        {
            _lootTracker.SetArea(
                LootLog.LoadFingerprints(DirectoryFullName, _currentArea.AreaId, _currentArea.InstanceHash),
                _currentArea.ZoneSwitchId);
        }
        catch (Exception ex)
        {
            LogError($"ExileStats -> Failed to seed loot tracker: {ex}");
        }
    }

    // ---- Map content ----

    // On entering a map, seed the content tracker from the instance's existing content.json so re-entry /
    // restart doesn't re-log content (and so terminal state keeps upgrading from where it left off).
    private void BeginContentForArea()
    {
        if (!Settings.LogContent || !IsTracked(_currentArea))
            return;

        try
        {
            var known = ContentLog.LoadKnown(DirectoryFullName, _currentArea.AreaId, _currentArea.InstanceHash);
            _contentTracker.SetArea(known, _currentArea.ZoneSwitchId);
            _ritualRewards.SetArea(known);
        }
        catch (Exception ex)
        {
            LogError($"ExileStats -> Failed to seed content tracker: {ex}");
        }
    }

    // ---- Wisp encounters ----

    // On entering a map, seed the wisp tracker from the instance's existing wisps.json so re-entry / restart
    // doesn't re-log a resolved encounter.
    private void BeginWispForArea()
    {
        if (!Settings.LogWisps || !IsTracked(_currentArea))
            return;

        try
        {
            _wispTracker.SetArea(
                WispLog.LoadKnown(DirectoryFullName, _currentArea.AreaId, _currentArea.InstanceHash),
                _currentArea.ZoneSwitchId);
        }
        catch (Exception ex)
        {
            LogError($"ExileStats -> Failed to seed wisp tracker: {ex}");
        }
    }

    // ---- Periodic snapshots ----

    private void TryLogSnapshot()
    {
        try
        {
            var rec = _currentArea;
            var player = GameController.Player;
            var pos = player?.GridPos ?? default;
            var life = player?.GetComponent<Life>();

            var snap = new Snapshot
            {
                At = DateTime.Now,
                ElapsedSeconds = Math.Round(ElapsedSeconds(), 1),
                ZoneSwitchId = rec.ZoneSwitchId,
                GridX = pos.X,
                GridY = pos.Y,
                Xp = player?.GetComponent<Player>()?.XP ?? 0,
                Gold = GameController.IngameState.ServerData.Gold,
                Life = life?.CurHP ?? 0,
                MaxLife = life?.MaxHP ?? 0,
                EnergyShield = life?.CurES ?? 0,
                MaxEnergyShield = life?.MaxES ?? 0,
                Mana = life?.CurMana ?? 0,
                MaxMana = life?.MaxMana ?? 0,
                MonstersSeen = _monsterCounter.SeenTotal,
                MonstersAlive = _monsterCounter.AliveTotal,
            };

            // Best-effort content-completion sample (empty if the side panel isn't readable right now).
            try
            {
                var (_, content) = MapSideInfo.Read(GameController);
                if (content.Count > 0)
                    snap.Content = content
                        .Select(c => new SnapshotContent { Name = c.Name, Completed = c.Completed })
                        .ToList();
            }
            catch { /* panel not readable this tick */ }

            // Full player stat sheet (~360 entries) — best-effort, optional.
            if (Settings.SnapshotStats && player != null)
            {
                try { snap.Stats = player.Stats?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value); }
                catch { /* stats not readable this tick */ }
            }

            // Active player buffs — best-effort, optional.
            if (Settings.SnapshotBuffs && player != null)
            {
                try
                {
                    snap.Buffs = player.Buffs?
                        .Where(b => b != null)
                        .Select(b => new SnapshotBuff
                        {
                            Name = b.Name,
                            DisplayName = b.DisplayName,
                            Charges = b.BuffCharges,
                            Stacks = b.BuffStacks,
                            Timer = CleanBuffTime(b.Timer),
                            MaxTime = CleanBuffTime(b.MaxTime),
                        })
                        .ToList();
                }
                catch { /* buffs not readable this tick */ }
            }

            SnapshotLog.Append(DirectoryFullName, rec.AreaId, rec.InstanceHash, snap);
        }
        catch (Exception ex)
        {
            LogError($"ExileStats -> Failed to log snapshot: {ex}");
        }
    }

    // Buff Timer/MaxTime are reported as infinity for permanent buffs; store those as null (valid JSON).
    private static float? CleanBuffTime(float f) => float.IsInfinity(f) || float.IsNaN(f) ? (float?)null : f;

    // ---- Death detection ----

    // Detect the alive -> dead edge each Tick and log a death at the last known position with the hostile
    // monsters nearby (likely killers). Robust to the player entity going null/invalid the instant you die
    // (we treat that as dead rather than letting an exception swallow the transition). Reset on area entry.
    private void TryLogDeath(IReadOnlyList<Entity> monsters)
    {
        bool alive = false;
        long xp = 0;
        try
        {
            var player = GameController.Player;
            if (player != null && player.IsValid &&
                player.TryGetComponent<Life>(out var life) && life.CurHP > 0)
            {
                alive = true;
                _lastPlayerPos = player.GridPos;
                xp = player.GetComponent<Player>()?.XP ?? 0;
            }
        }
        catch { alive = false; }

        if (_wasAlive && !alive)
        {
            try
            {
                var rec = _currentArea;
                var death = new Death
                {
                    At = DateTime.Now,
                    ElapsedSeconds = Math.Round(ElapsedSeconds(), 1),
                    ZoneSwitchId = rec.ZoneSwitchId,
                    GridX = _lastPlayerPos.X,
                    GridY = _lastPlayerPos.Y,
                    Xp = xp,
                    NearbyMonsters = ScanNearbyMonsters(_lastPlayerPos, monsters),
                };
                DeathLog.Append(DirectoryFullName, rec.AreaId, rec.InstanceHash, death);
                LogMessage($"ExileStats -> Death logged at ({death.GridX:0},{death.GridY:0}); " +
                           $"{death.NearbyMonsters.Count} monster(s) nearby.");
            }
            catch (Exception ex)
            {
                LogError($"ExileStats -> Failed to log death: {ex}");
            }
        }

        _wasAlive = alive;
    }

    // Hostile monsters within DeathNearbyRange of a grid position, nearest-first. Consumes the shared
    // valid-Monster bucket (Type==Monster already guaranteed). The likely killers at the moment of death.
    private System.Collections.Generic.List<NearbyMonster> ScanNearbyMonsters(
        Vector2 playerPos, IReadOnlyList<Entity> monsters)
    {
        var range = Settings.DeathNearbyRange.Value;
        var result = new System.Collections.Generic.List<NearbyMonster>();

        foreach (var e in monsters)
        {
            if (e is not { IsHostile: true })
                continue;
            if (e.HasComponent<DiesAfterTime>())
                continue;

            var dist = Vector2.Distance(e.GridPos, playerPos);
            if (dist > range)
                continue;

            result.Add(new NearbyMonster
            {
                Name = string.IsNullOrEmpty(e.RenderName) ? e.Path : e.RenderName.Split(',')[0],
                Rarity = e.Rarity.ToString(),
                Distance = (float)Math.Round(dist, 1),
            });
        }

        result.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return result;
    }

    // ---- On-screen counter ----

    private static readonly (MonsterRarity Rarity, string Label, Color Color)[] CounterRows =
    [
        (MonsterRarity.White, "White", RarityPalette.White),
        (MonsterRarity.Magic, "Magic", RarityPalette.Magic),
        (MonsterRarity.Rare, "Rare", RarityPalette.Rare),
        (MonsterRarity.Unique, "Unique", RarityPalette.Unique),
    ];

    private void DrawMonsterCount()
    {
        if (!Settings.ShowCounter)
            return;

        // Show only inside tracked areas, or while the config is open (so it can be positioned); never on
        // top of large/fullscreen panels (inventory, atlas, etc.).
        if (!_overlaysVisible)
            return;

        var pos = new Vector2(Settings.PositionX.Value, Settings.PositionY.Value);
        var header = $"Monsters: {_monsterCounter.SeenTotal}  (alive {_monsterCounter.AliveTotal})";
        pos.Y += Graphics.DrawText(header, pos, Settings.TextColor.Value).Y;

        if (!Settings.SplitByRarity.Value)
            return;

        foreach (var (rarity, label, color) in CounterRows)
        {
            var line = $"  {label}: {_monsterCounter.Seen(rarity)}  (alive {_monsterCounter.Alive(rarity)})";
            pos.Y += Graphics.DrawText(line, pos, color).Y;
        }
    }

    // ---- On-screen net-worth readout ----

    // While the stash panel is open, show the running net worth (exalts + divines). The total accumulates
    // as the player clicks through tabs (only loaded tabs are readable); divine figure needs NinjaPricer.
    private void DrawNetWorth()
    {
        if (!Settings.ShowNetWorthReadout)
            return;
        if (_ingameUi?.StashElement is not { IsVisible: true })
            return;

        var ex = _stashTracker.TotalExalted;
        if (ex <= 0)
            return;

        var text = "Net worth: " + Currency.FormatWithDivine(ex, _divRate);
        // own position node: this only shows with the stash open, so it needs a spot the panel leaves free
        var pos = new Vector2(Settings.NetWorthPositionX.Value, Settings.NetWorthPositionY.Value);
        Graphics.DrawText(text, pos, Settings.TextColor.Value);
    }
}
