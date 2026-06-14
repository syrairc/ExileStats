using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using Newtonsoft.Json;
using Vector2 = System.Numerics.Vector2;

namespace ExileStats;

public struct ReportOptions
{
    public bool ShowNetWorth;
    public bool ShowIncomeChart;
    public bool ShowMapChart;
    public bool ShowTopItems;
    public bool ShowTopRuns;
    public bool ShowEfficiency;
    public bool ShowBestMaps;
    public bool ShowBestLayouts;
    public bool ShowLootComposition;
    public bool ShowRunLog;
    public bool ShowMonstersOnMaps;
    public bool ShowPathOnMaps;
    public bool ShowExploredOnMaps;
    public float RevealRadius;
    public string ExploredTintHex;     // "#RRGGBB"
    public double ExploredTintOpacity;  // 0..1 (alpha of the tint color)
    public int TopItemsCount;
    public int TopRunsCount;
    public int BestMapsCount;
    public int BestLayoutsCount;
}

/// <summary>
/// Generates a self-contained <c>reports/activity_&lt;time&gt;.html</c> summarizing every map run in a chosen
/// timeframe (last N hours, up to 24). Re-reads the JSON the plugin already writes under <c>maps/</c>
/// (index.json → per-instance run.json / pickups.json / loot.json / deaths.json / path.json) so it can run
/// any time, no live game state needed. Same generation style as <see cref="DashboardGenerator"/>: string-built
/// HTML, embedded CSS (light/dark), data inlined as JS literals, Chart.js from CDN.
///
/// Value is in "exalted" (NinjaPricer reports PoE2 exalted under its legacy "chaos" field). A run's profit =
/// picked-up exalted value + gold-gained converted via a rough display divisor (gold and exalted are also
/// shown separately so the headline numbers stay honest).
/// </summary>
public static class ActivityReportGenerator
{
    // Rough display-only divisor to fold gold into a single "profit" sort key. Gold and exalted are reported
    // separately too, so this only affects the combined ranking, not the headline figures.
    private const double GoldPerExalted = 30000.0;

    // Net-worth snapshots below this fraction of the all-time peak are partial-tab reads (only some stash
    // tabs streamed in) — net worth never realistically craters to near-zero, so drop them as bad data.
    private const double MinNetWorthFraction = 0.25;

    // Drop partial-tab net-worth reads: keep only points >= MinNetWorthFraction of the series peak. No-op on
    // null/short series or when the peak is non-positive.
    private static List<NetWorthPoint> FilterNetWorth(List<NetWorthPoint> points)
    {
        if (points == null || points.Count < 2)
            return points;
        var peak = points.Max(p => p.TotalExalted);
        if (peak <= 0)
            return points;
        var floor = peak * MinNetWorthFraction;
        var kept = points.Where(p => p.TotalExalted >= floor).ToList();
        return kept.Count > 0 ? kept : points;
    }

    private sealed class RunData
    {
        public MapRunRecord Record;
        public string Folder;
        public string MapName;
        public List<PickupItem> Pickups = new();
        public List<LootItem> Loot = new();
        public List<Death> Deaths = new();
        public List<PathPoint> Path = new();
        public List<Snapshot> Snapshots = new();
        public List<ContentSighting> Content = new();
        public List<MonsterSighting> MonsterPositions = new();

        public double DurationSec;
        public double PickupValue;     // Σ pickup ChaosValue
        public double UnitsTravelled;
        public double Profit;          // PickupValue + GoldGained / GoldPerExalted
        public long GoldGained;
        public long XpGained;
        public int Monsters;
    }

    /// <summary>One *run* = a map plus any sub-areas entered from within it (Abyssal Depths, boss arenas, …),
    /// grouped by <see cref="MapRunRecord.RunId"/>. A run begins when you leave a town/hideout and ends when
    /// you return, so all its area visits share a RunId. Aggregates the members' stats; the headline is the
    /// run's atlas map (the visit whose AreaId starts with "Map"), else its first area.</summary>
    private sealed class RunGroup
    {
        public List<RunData> Members = new();
        public RunData Headline;
        public string MapName;
        public DateTime LoggedAt;       // newest member log (ordering / chart x)
        public DateTime EnteredAt;      // earliest member entry
        public double DurationSec;
        public double PickupValue;
        public long GoldGained;
        public long XpGained;
        public int Monsters;
        public int DeathCount;
        public double UnitsTravelled;
        public double Profit;

        public IEnumerable<PickupItem> Pickups => Members.SelectMany(m => m.Pickups);
        public IEnumerable<Death> Deaths => Members.SelectMany(m => m.Deaths);
    }

    // IsMapArea isn't persisted to run.json ([JsonIgnore]), so derive "is an atlas map" from the AreaId here.
    private static bool IsMapId(string areaId) =>
        areaId != null && areaId.StartsWith("Map", StringComparison.Ordinal);

    // Group the per-visit RunData into runs by RunId (0 / legacy data = its own solo run). Members ordered by
    // entry time; aggregates summed (deltas are additive across a run's contiguous areas). Newest run first.
    private static List<RunGroup> GroupRuns(List<RunData> visits)
    {
        var byKey = new Dictionary<string, RunGroup>();
        var order = new List<RunGroup>();
        foreach (var v in visits)
        {
            var rid = v.Record.RunId;
            var key = rid != 0 ? "run:" + rid : "solo:" + v.Folder + ":" + v.Record.ZoneSwitchId;
            if (!byKey.TryGetValue(key, out var g))
            {
                g = new RunGroup();
                byKey[key] = g;
                order.Add(g);
            }
            g.Members.Add(v);
        }

        foreach (var g in order)
        {
            g.Members = g.Members.OrderBy(m => m.Record.EnteredAt).ToList();
            g.Headline = g.Members.FirstOrDefault(m => IsMapId(m.Record.AreaId)) ?? g.Members[0];
            g.MapName = g.Headline.MapName;
            g.LoggedAt = g.Members.Max(m => m.Record.LoggedAt);
            g.EnteredAt = g.Members.Min(m => m.Record.EnteredAt);
            g.DurationSec = g.Members.Sum(m => m.DurationSec);
            g.PickupValue = g.Members.Sum(m => m.PickupValue);
            g.GoldGained = g.Members.Sum(m => m.GoldGained);
            g.XpGained = g.Members.Sum(m => m.XpGained);
            g.Monsters = g.Members.Sum(m => m.Monsters);
            g.DeathCount = g.Members.Sum(m => m.Deaths.Count);
            g.UnitsTravelled = g.Members.Sum(m => m.UnitsTravelled);
            g.Profit = g.Members.Sum(m => m.Profit);
        }

        return order.OrderByDescending(g => g.LoggedAt).ToList();
    }

    public static string Generate(string pluginDirectory, double hours, double divineRate, ReportOptions opts)
    {
        var runs = LoadRuns(pluginDirectory, hours);

        // Resolve the shared game icon sheet once so map-content icons can be cropped + inlined as base64.
        PrepareIcons(pluginDirectory);

        // Account-global net worth (latest snapshot is shown regardless of the run window).
        List<NetWorthPoint> netWorth;
        try { netWorth = StashLog.ReadNetWorth(pluginDirectory); }
        catch { netWorth = new List<NetWorthPoint>(); }

        var dir = Path.Combine(pluginDirectory, "reports");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"activity_{DateTime.Now:yyyyMMdd_HHmm}.html");

        var html = runs.Count == 0 ? BuildEmptyHtml(hours) : BuildHtml(hours, runs, divineRate, netWorth, opts);
        File.WriteAllText(outPath, html, new UTF8Encoding(false));
        DisposeIcons();
        return outPath;
    }

    /// <summary>Export a single run (one or more areas sharing a RunId) to a self-contained HTML file at
    /// <c>reports/run_&lt;mapName&gt;_&lt;date&gt;.html</c>. Returns the path, or null when no records
    /// were found.</summary>
    public static string GenerateRunReport(
        string pluginDirectory,
        List<(string folder, int zone)> areas,
        string mapName,
        double divineRate,
        ReportOptions opts)
    {
        var root = Path.Combine(pluginDirectory, InstanceStore.RootFolder);
        var visits = new List<RunData>();

        foreach (var (folder, zone) in areas)
        {
            var fullFolder = Path.Combine(root, folder);
            var record = LoadList<MapRunRecord>(fullFolder, InstanceStore.RunFile)
                .FirstOrDefault(v => v.ZoneSwitchId == zone);
            if (record == null)
                continue;

            var rd = new RunData
            {
                Record = record,
                Folder = fullFolder,
                MapName = string.IsNullOrEmpty(record.DisplayName) ? record.Name : record.DisplayName,
                Pickups = LoadList<PickupItem>(fullFolder, InstanceStore.PickupsFile).Where(p => p.ZoneSwitchId == zone).ToList(),
                Loot = LoadList<LootItem>(fullFolder, InstanceStore.LootFile).Where(l => l.ZoneSwitchId == zone).ToList(),
                Deaths = LoadList<Death>(fullFolder, InstanceStore.DeathsFile).Where(d => d.ZoneSwitchId == zone).ToList(),
                Path = LoadList<PathPoint>(fullFolder, InstanceStore.PathFile).Where(p => p.Z == zone).OrderBy(p => p.T).ToList(),
                Snapshots = LoadList<Snapshot>(fullFolder, InstanceStore.SnapshotFile).Where(s => s.ZoneSwitchId == zone).OrderBy(s => s.ElapsedSeconds).ToList(),
                Content = LoadList<ContentSighting>(fullFolder, InstanceStore.ContentFile).Where(c => c.ZoneSwitchId == zone).ToList(),
                MonsterPositions = LoadList<MonsterSighting>(fullFolder, InstanceStore.MonstersFile).Where(m => m.ZoneSwitchId == zone).ToList(),
            };
            rd.DurationSec = Duration(record);
            rd.PickupValue = record.PickupValue > 0 ? record.PickupValue : rd.Pickups.Sum(p => p.ChaosValue ?? 0);
            rd.GoldGained = record.GoldGained;
            rd.XpGained = record.XpGained;
            rd.Monsters = record.MonstersTotal;
            rd.UnitsTravelled = TravelDistance(rd);
            rd.Profit = rd.PickupValue + rd.GoldGained / GoldPerExalted;
            visits.Add(rd);
        }

        if (visits.Count == 0)
            return null;

        PrepareIcons(pluginDirectory);

        var grp = new RunGroup();
        grp.Members = visits.OrderBy(v => v.Record.EnteredAt).ToList();
        grp.Headline = grp.Members.FirstOrDefault(m => IsMapId(m.Record.AreaId)) ?? grp.Members[0];
        grp.MapName = string.IsNullOrEmpty(mapName) ? grp.Headline.MapName : mapName;
        grp.LoggedAt = grp.Members.Max(m => m.Record.LoggedAt);
        grp.EnteredAt = grp.Members.Min(m => m.Record.EnteredAt);
        grp.DurationSec = grp.Members.Sum(m => m.DurationSec);
        grp.PickupValue = grp.Members.Sum(m => m.PickupValue);
        grp.GoldGained = grp.Members.Sum(m => m.GoldGained);
        grp.XpGained = grp.Members.Sum(m => m.XpGained);
        grp.Monsters = grp.Members.Sum(m => m.Monsters);
        grp.DeathCount = grp.Members.Sum(m => m.Deaths.Count);
        grp.UnitsTravelled = grp.Members.Sum(m => m.UnitsTravelled);
        grp.Profit = grp.Members.Sum(m => m.Profit);

        var dir = Path.Combine(pluginDirectory, "reports");
        Directory.CreateDirectory(dir);
        var safe = InstanceStore.Sanitize(grp.MapName);
        var outPath = Path.Combine(dir, $"run_{safe}_{DateTime.Now:yyyyMMdd_HHmm}.html");

        var html = BuildSingleRunHtml(grp, divineRate, opts);
        File.WriteAllText(outPath, html, new UTF8Encoding(false));
        DisposeIcons();
        return outPath;
    }

    // Self-contained HTML for a single run (no charts, no net worth, just the run's stats + map(s)). Maps are
    // immediately visible (not in a <details> collapse), so pan-zoom inits directly via SingleRunPanZoomScript.
    private static string BuildSingleRunHtml(RunGroup g, double divineRate, ReportOptions opts)
    {
        _divRate = divineRate;
        _showMonstersOnMap = opts.ShowMonstersOnMaps;
        _showPathOnMap = opts.ShowPathOnMaps;
        _showExploredOnMap = opts.ShowExploredOnMaps;
        _revealRadius = opts.RevealRadius;
        _tintHex = string.IsNullOrEmpty(opts.ExploredTintHex) ? "#00C800" : opts.ExploredTintHex;
        _tintOpacity = opts.ExploredTintOpacity;

        var head = g.Headline.Record;
        var generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Ci);
        var divNote = divineRate > 0 ? $" · 1 div = {N(divineRate)} ex" : " · divine rate unavailable";

        var sb = new StringBuilder();
        // Swap the generic activity-report title for a run-specific one.
        sb.Append(TemplateHead.Replace("<title>ExileStats activity report</title>",
            $"<title>ExileStats - {Html(g.MapName)}</title>"));
        sb.Append($"  <h1>{Html(g.MapName)}</h1>\n");
        sb.Append($"  <p class=\"sub\">generated {generated} · " +
                  $"{g.DurationSec:0}s · {g.Monsters} monsters · {g.DeathCount} death(s) · " +
                  $"level {head.AreaLevel}</p>\n");

        sb.Append("  <div class=\"cards\">\n");
        sb.Append(Card("Looted", V(g.PickupValue)));
        sb.Append(Card("Gold", Abbrev(g.GoldGained)));
        sb.Append(Card("XP", SignAbbrev(g.XpGained)));
        sb.Append(Card("Monsters", g.Monsters.ToString(Ci)));
        sb.Append(Card("Deaths", g.DeathCount.ToString(Ci)));
        sb.Append(Card("Duration", $"{g.DurationSec:0}s"));
        sb.Append(Card("Area level", head.AreaLevel.ToString(Ci)));
        sb.Append(Card("Monster level", head.MonsterLevel.ToString(Ci)));
        sb.Append("  </div>\n");

        sb.Append("  <div class=\"run-body\">\n");
        sb.Append(BuildRunGroupInner(g, collapsibleLoot: true));
        sb.Append("  </div>\n");

        sb.Append($"  <footer>ExileStats run report{divNote}</footer>\n");
        sb.Append("</div>\n");

        // Maps are not collapsed, so init pan-zoom immediately (no <details> toggle needed).
        sb.Append("<script src=\"https://cdn.jsdelivr.net/npm/svg-pan-zoom@3.6.1/dist/svg-pan-zoom.min.js\"></script>\n");
        sb.Append("<script>\n(function(){\n");
        sb.Append(SingleRunPanZoomScript);
        sb.Append("})();\n</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    // ---------- data load ----------

    private static List<RunData> LoadRuns(string pluginDirectory, double hours)
    {
        var root = Path.Combine(pluginDirectory, InstanceStore.RootFolder);
        var cutoff = DateTime.Now.AddHours(-hours);
        var result = new List<RunData>();

        // (folder, zoneSwitchId) pairs in window — from the master index (fast path), else walk run.json.
        var refs = new List<(string Folder, int Zone)>();

        var indexPath = Path.Combine(root, InstanceStore.IndexFile);
        if (File.Exists(indexPath))
        {
            List<RunIndexEntry> index = null;
            try { index = JsonConvert.DeserializeObject<List<RunIndexEntry>>(File.ReadAllText(indexPath)); }
            catch { index = null; }
            if (index != null)
                foreach (var e in index.Where(e => e.LoggedAt >= cutoff))
                    refs.Add((Path.Combine(root, e.Folder), e.ZoneSwitchId));
        }

        if (refs.Count == 0 && Directory.Exists(root))
        {
            // Fallback (pre-index data): walk each instance folder's run.json.
            foreach (var folder in Directory.GetDirectories(root))
            {
                foreach (var v in LoadList<MapRunRecord>(folder, InstanceStore.RunFile))
                    if (v.LoggedAt >= cutoff)
                        refs.Add((folder, v.ZoneSwitchId));
            }
        }

        foreach (var (folder, zone) in refs)
        {
            var record = LoadList<MapRunRecord>(folder, InstanceStore.RunFile)
                .FirstOrDefault(v => v.ZoneSwitchId == zone);
            if (record == null || record.LoggedAt < cutoff)
                continue;

            var rd = new RunData
            {
                Record = record,
                Folder = folder,
                MapName = string.IsNullOrEmpty(record.DisplayName) ? record.Name : record.DisplayName,
                Pickups = LoadList<PickupItem>(folder, InstanceStore.PickupsFile).Where(p => p.ZoneSwitchId == zone).ToList(),
                Loot = LoadList<LootItem>(folder, InstanceStore.LootFile).Where(l => l.ZoneSwitchId == zone).ToList(),
                Deaths = LoadList<Death>(folder, InstanceStore.DeathsFile).Where(d => d.ZoneSwitchId == zone).ToList(),
                Path = LoadList<PathPoint>(folder, InstanceStore.PathFile).Where(p => p.Z == zone).OrderBy(p => p.T).ToList(),
                Snapshots = LoadList<Snapshot>(folder, InstanceStore.SnapshotFile).Where(s => s.ZoneSwitchId == zone).OrderBy(s => s.ElapsedSeconds).ToList(),
                Content = LoadList<ContentSighting>(folder, InstanceStore.ContentFile).Where(c => c.ZoneSwitchId == zone).ToList(),
                MonsterPositions = LoadList<MonsterSighting>(folder, InstanceStore.MonstersFile).Where(m => m.ZoneSwitchId == zone).ToList(),
            };

            rd.DurationSec = Duration(record);
            rd.PickupValue = record.PickupValue > 0 ? record.PickupValue : rd.Pickups.Sum(p => p.ChaosValue ?? 0);
            rd.GoldGained = record.GoldGained;
            rd.XpGained = record.XpGained;
            rd.Monsters = record.MonstersTotal;
            rd.UnitsTravelled = TravelDistance(rd);
            rd.Profit = rd.PickupValue + rd.GoldGained / GoldPerExalted;
            result.Add(rd);
        }

        return result.OrderByDescending(r => r.Record.LoggedAt).ToList();
    }

    private static List<T> LoadList<T>(string folder, string file)
    {
        var path = Path.Combine(folder, file);
        if (!File.Exists(path))
            return new List<T>();
        try { return JsonConvert.DeserializeObject<List<T>>(File.ReadAllText(path)) ?? new List<T>(); }
        catch { return new List<T>(); }
    }

    // tz-aware (TimeEntered is UTC, LoggedAt local) recompute; fall back to the stored value.
    private static double Duration(MapRunRecord r)
    {
        var d = (r.LoggedAt.ToUniversalTime() - r.EnteredAt.ToUniversalTime()).TotalSeconds;
        return d > 0 ? d : r.DurationSeconds;
    }

    // Sum grid distance along the dense path (fall back to snapshot positions), skipping checkpoint
    // teleports — a gap > 20% of the grid diagonal isn't running. Mirrors MapStatsWindow's jump filter.
    private static double TravelDistance(RunData rd)
    {
        var pts = rd.Path.Count > 0
            ? rd.Path.Select(p => new Vector2(p.X, p.Y)).ToList()
            : rd.Snapshots.Select(s => new Vector2(s.GridX, s.GridY)).ToList();
        if (pts.Count < 2)
            return 0;

        var w = rd.Record.AreaWidth;
        var h = rd.Record.AreaHeight;
        var jump = w > 0 && h > 0 ? 0.2 * Math.Sqrt((double)w * w + (double)h * h) : double.MaxValue;
        var jumpSq = jump * jump;

        double total = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            var seg = Vector2.DistanceSquared(pts[i - 1], pts[i]);
            if (seg <= jumpSq)
                total += Math.Sqrt(seg);
        }
        return total;
    }

    // ---------- HTML ----------

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private static string Html(string s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Js(string s) => (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'");

    private static string N(double v) => v >= 100 || v <= -100 ? v.ToString("N0", Ci) : v.ToString("0.#", Ci);
    private static string Sign(double v) => (v >= 0 ? "+" : "") + N(v);
    private static string Ex(double v) => N(v) + " ex";

    // Ex-per-divine rate for the current report (0 = unknown / NinjaPricer absent). Set at BuildHtml start.
    private static double _divRate;
    // Overlay logged monster positions on the per-run maps. Set at BuildHtml start.
    private static bool _showMonstersOnMap;
    // Path / explored-area overlay config for the per-run maps. Set at BuildHtml start.
    private static bool _showPathOnMap;
    private static bool _showExploredOnMap;
    private static float _revealRadius;
    private static string _tintHex = "#00C800";
    private static double _tintOpacity = 0.25;
    // Adaptive value: render in divine once the value exceeds 5 div, else exalted. Falls back to exalted
    // when the divine rate is unknown.
    private static string V(double ex)
    {
        if (_divRate > 0 && ex / _divRate > 5) return N(ex / _divRate) + " div";
        return Ex(ex);
    }

    // Gold piles: skipped at logging (LootTracker drops Metadata/Items/Currency/GoldCoin), but guard the
    // report too so any legacy row doesn't pollute the "most dropped" tally.
    private static bool IsGoldPile(LootItem l) =>
        (l.Path?.IndexOf("GoldCoin", StringComparison.OrdinalIgnoreCase) >= 0) ||
        string.Equals(l.BaseName, "Gold", StringComparison.OrdinalIgnoreCase);

    // Compact magnitude (1.2B / 45.6M / 789K) so huge values (XP, gold, units) don't overflow the cards.
    private static string Abbrev(double v)
    {
        var a = Math.Abs(v);
        if (a >= 1e12) return (v / 1e12).ToString("0.##", Ci) + "T";
        if (a >= 1e9) return (v / 1e9).ToString("0.##", Ci) + "B";
        if (a >= 1e6) return (v / 1e6).ToString("0.##", Ci) + "M";
        if (a >= 1e3) return (v / 1e3).ToString("0.#", Ci) + "K";
        return v.ToString("0", Ci);
    }

    private static string SignAbbrev(double v) => (v >= 0 ? "+" : "") + Abbrev(v);

    private static string BuildHtml(double hours, List<RunData> runs, double divineRate, List<NetWorthPoint> netWorth,
        ReportOptions opts)
    {
        _divRate = divineRate;   // drives V() adaptive ex/div formatting across the report
        _showMonstersOnMap = opts.ShowMonstersOnMaps;
        _showPathOnMap = opts.ShowPathOnMaps;
        _showExploredOnMap = opts.ShowExploredOnMaps;
        _revealRadius = opts.RevealRadius;
        _tintHex = string.IsNullOrEmpty(opts.ExploredTintHex) ? "#00C800" : opts.ExploredTintHex;
        _tintOpacity = opts.ExploredTintOpacity;
        // ----- aggregates -----
        // Group visits into runs (a map + its sub-areas share a RunId). Period totals still sum every visit
        // (deltas are additive), but run-level figures — run count, per-run rates, top runs, the run log —
        // use the grouped runs so a map + its Abyssal Depths read as one run.
        var groups = GroupRuns(runs);
        var runCount = groups.Count;
        var totalSec = runs.Sum(r => r.DurationSec);
        var totalHours = totalSec / 3600.0;
        double Hr(double v) => totalHours > 0 ? v / totalHours : 0;

        var goldGained = runs.Sum(r => (double)r.GoldGained);
        var xpGained = runs.Sum(r => (double)r.XpGained);
        var exGained = runs.Sum(r => r.PickupValue);
        var units = runs.Sum(r => r.UnitsTravelled);
        var monsters = runs.Sum(r => (double)r.Monsters);
        var deaths = runs.Sum(r => r.Deaths.Count);
        var pickupCount = runs.Sum(r => r.Pickups.Count);

        // wall-span (oldest entry → newest log) for an uptime %.
        var oldestEntry = runs.Min(r => r.Record.EnteredAt.ToUniversalTime());
        var newestLog = runs.Max(r => r.Record.LoggedAt.ToUniversalTime());
        var wallHours = (newestLog - oldestEntry).TotalHours;
        var uptime = wallHours > 0 ? totalHours / wallHours * 100.0 : 0;

        // ----- summary cards -----
        // ----- net worth (account-global; latest snapshot, even if outside the run window) -----
        // Drop partial-tab reads: when only some stash tabs have streamed in, the logged total craters far
        // below the real net worth. Anything under a fraction of the all-time peak is treated as bad data.
        netWorth = FilterNetWorth(netWorth);
        var nwCutoff = DateTime.Now.AddHours(-hours);
        var nwInWindow = netWorth?.Where(p => p.At >= nwCutoff).OrderBy(p => p.At).ToList() ?? new List<NetWorthPoint>();
        var latestNw = netWorth != null && netWorth.Count > 0 ? netWorth[netWorth.Count - 1] : null;
        var nwCards = "";
        if (latestNw != null)
        {
            var nwEx = latestNw.TotalExalted;
            var nwDiv = latestNw.TotalDivine ?? (divineRate > 0 ? nwEx / divineRate : (double?)null);
            // One unit only: divine once net worth > 5 div, else exalted.
            bool useDiv = nwDiv is { } dv && dv > 5;
            nwCards = Card("Net worth", useDiv ? N(nwDiv.Value) + " div" : Ex(nwEx));
            if (nwInWindow.Count >= 2)
            {
                var deltaEx = latestNw.TotalExalted - nwInWindow[0].TotalExalted;
                nwCards += useDiv && divineRate > 0
                    ? Card($"Net worth Δ {hours:0}h", Sign(deltaEx / divineRate) + " div")
                    : Card($"Net worth Δ {hours:0}h", Sign(deltaEx) + " ex");
            }
        }

        // Net worth time-series for the graph: prefer in-window points; if too sparse, fall back to full history.
        var nwSeries = nwInWindow.Count >= 2
            ? nwInWindow
            : (netWorth ?? new List<NetWorthPoint>()).OrderBy(p => p.At).ToList();
        var hasNwChart = nwSeries.Count >= 2;
        var nwLabels = string.Join(",", nwSeries.Select(p => $"'{Js(p.At.ToString("MM-dd HH:mm", Ci))}'"));
        var nwExData = string.Join(",", nwSeries.Select(p => p.TotalExalted.ToString("0.##", Ci)));
        double? NwDiv(NetWorthPoint p) => p.TotalDivine ?? (divineRate > 0 ? p.TotalExalted / divineRate : (double?)null);
        var hasNwDiv = hasNwChart && nwSeries.All(p => NwDiv(p) != null);
        var nwDivData = hasNwDiv
            ? string.Join(",", nwSeries.Select(p => NwDiv(p).Value.ToString("0.###", Ci)))
            : "";
        // One unit only (same rule as the card): divine once the latest point is > 5 div, else exalted.
        var nwUseDiv = hasNwDiv && NwDiv(nwSeries[nwSeries.Count - 1]).Value > 5;

        var cards =
            (opts.ShowNetWorth ? nwCards : "") +
            Card("Runs", runCount.ToString(Ci)) +
            Card("Time mapped", $"{totalHours:0.0} h") +
            Card("Income", V(exGained)) +
            Card("Income / h", V(Hr(exGained))) +
            Card("Gold", Abbrev(goldGained)) +
            Card("Gold / h", Abbrev(Hr(goldGained))) +
            Card("XP", SignAbbrev(xpGained)) +
            Card("XP / h", SignAbbrev(Hr(xpGained))) +
            Card("Units travelled", Abbrev(units)) +
            Card("Units / h", Abbrev(Hr(units))) +
            Card("Deaths", deaths.ToString(Ci)) +
            Card("Maps / h", $"{Hr(runCount):0.0}");

        var divNote = divineRate > 0 ? $" · 1 div = {N(divineRate)} ex" : " · divine rate unavailable (NinjaPricer)";

        // ----- efficiency -----
        var eff =
            Row2("Income per map", V(runCount > 0 ? exGained / runCount : 0)) +
            Row2("Income per 100 monsters", V(monsters > 0 ? exGained / monsters * 100 : 0)) +
            Row2("Avg map duration", $"{(runCount > 0 ? totalSec / runCount : 0):0} s") +
            Row2("Maps per hour", $"{Hr(runCount):0.00}") +
            Row2("Items looted / hour", $"{Hr(pickupCount):0.0}") +
            Row2("Monsters per map", $"{(runCount > 0 ? monsters / runCount : 0):0}") +
            Row2("Deaths per hour", $"{Hr(deaths):0.00}") +
            Row2("Mapping uptime", $"{uptime:0}% (of {wallHours:0.0} h span)");

        // ----- top items -----
        var topItems = runs
            .SelectMany(r => r.Pickups.Select(p => (Item: p, Run: r)))
            .Where(x => (x.Item.ChaosValue ?? 0) > 0)
            .OrderByDescending(x => x.Item.ChaosValue ?? 0)
            .Take(opts.TopItemsCount).ToList();
        var topItemsRows = string.Concat(topItems.Select(x =>
        {
            var p = x.Item;
            var name = string.IsNullOrEmpty(p.UniqueName) ? p.BaseName : p.UniqueName;
            if (string.IsNullOrEmpty(name)) name = p.ClassName;
            if (p.StackCount > 1) name += $" x{p.StackCount}";
            return "<tr>" +
                $"<td class=\"r-{RarityClass(p.Rarity)}\">{Html(name)}</td>" +
                $"<td class=\"num\">{V(p.ChaosValue ?? 0)}</td>" +
                $"<td>{Html(x.Run.MapName)}</td></tr>\n";
        }));

        // ----- top runs -----
        var topRuns = groups.OrderByDescending(g => g.Profit).Take(opts.TopRunsCount).ToList();
        var topRunsRows = string.Concat(topRuns.Select(g =>
            "<tr>" +
            $"<td>{Html(g.MapName)}{(g.Members.Count > 1 ? $" <span class=\"dim\">+{g.Members.Count - 1}</span>" : "")}</td>" +
            $"<td class=\"num\">{V(g.PickupValue)}</td>" +
            $"<td class=\"num\">{Abbrev(g.GoldGained)}</td>" +
            $"<td class=\"num\">{SignAbbrev(g.XpGained)}</td>" +
            $"<td class=\"num\">{g.DurationSec:0}s</td></tr>\n"));

        // ----- best maps + best layouts -----
        string GroupTable(IEnumerable<IGrouping<string, RunData>> groups, int take = 0)
        {
            var rows = groups
                .Select(g => new
                {
                    Name = g.Key,
                    Count = g.Count(),
                    ExHr = g.Sum(r => r.DurationSec) > 0 ? g.Sum(r => r.PickupValue) / (g.Sum(r => r.DurationSec) / 3600.0) : 0,
                    Mon = g.Average(r => (double)r.Monsters),
                })
                .OrderByDescending(x => x.ExHr);
            var limited = take > 0 ? rows.Take(take) : rows.AsEnumerable();
            return string.Concat(limited
                .Select(x => "<tr>" +
                    $"<td>{Html(string.IsNullOrEmpty(x.Name) ? "—" : x.Name)}</td>" +
                    $"<td class=\"num\">{x.Count}</td>" +
                    $"<td class=\"num\">{V(x.ExHr)}</td>" +
                    $"<td class=\"num\">{x.Mon:0}</td></tr>\n"));
        }

        // Best maps groups whole runs by headline map (so a run's sub-areas roll into its map); best layouts
        // stays per-area (layout is classified per area's geometry).
        string GroupTableG(IEnumerable<IGrouping<string, RunGroup>> grps, int take = 0)
        {
            var rows = grps
                .Select(g => new
                {
                    Name = g.Key,
                    Count = g.Count(),
                    ExHr = g.Sum(r => r.DurationSec) > 0 ? g.Sum(r => r.PickupValue) / (g.Sum(r => r.DurationSec) / 3600.0) : 0,
                    Mon = g.Average(r => (double)r.Monsters),
                })
                .OrderByDescending(x => x.ExHr);
            var limited = take > 0 ? rows.Take(take) : rows.AsEnumerable();
            return string.Concat(limited
                .Select(x => "<tr>" +
                    $"<td>{Html(string.IsNullOrEmpty(x.Name) ? "—" : x.Name)}</td>" +
                    $"<td class=\"num\">{x.Count}</td>" +
                    $"<td class=\"num\">{V(x.ExHr)}</td>" +
                    $"<td class=\"num\">{x.Mon:0}</td></tr>\n"));
        }

        var bestMaps = GroupTableG(groups.GroupBy(g => g.MapName), opts.BestMapsCount);
        var bestLayouts = GroupTable(runs.GroupBy(r => r.Record.LayoutType ?? "unclassified"), opts.BestLayoutsCount);

        // ----- loot composition -----
        var allPickups = runs.SelectMany(r => r.Pickups).ToList();
        var byCat = allPickups.GroupBy(MapStatsWindow.Category)
            .Select(g => new { Cat = g.Key, Count = g.Count(), Val = g.Sum(p => p.ChaosValue ?? 0) })
            .OrderByDescending(x => x.Val).ToList();
        var catRows = string.Concat(byCat.Select(x =>
            $"<tr><td>{Html(x.Cat)}</td><td class=\"num\">{x.Count}</td><td class=\"num\">{V(x.Val)}</td></tr>\n"));

        var byRarity = allPickups.GroupBy(p => string.IsNullOrEmpty(p.Rarity) ? "—" : p.Rarity)
            .Select(g => $"{Html(g.Key)} {g.Count()}");
        var rarityLine = string.Join(" · ", byRarity);

        var topDrops = allPickups.GroupBy(p => string.IsNullOrEmpty(p.BaseName) ? p.ClassName : p.BaseName)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .OrderByDescending(g => g.Count()).Take(8)
            .Select(g => $"{Html(g.Key)} ×{g.Count()}");
        var dropsLine = string.Join(" · ", topDrops);

        // Most dropped ground items (loot.json = items that hit the floor, looted or not), gold piles excluded.
        var allGroundDrops = runs.SelectMany(r => r.Loot).Where(l => !IsGoldPile(l)).ToList();
        var topGroundDrops = allGroundDrops
            .GroupBy(l => string.IsNullOrEmpty(l.BaseName) ? l.ClassName : l.BaseName)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .OrderByDescending(g => g.Count()).Take(8)
            .Select(g => $"{Html(g.Key)} ×{g.Count()}");
        var groundDropsLine = string.Join(" · ", topGroundDrops);

        var goldAsEx = goldGained / GoldPerExalted;
        var incomeSplit = $"income {V(exGained)} · gold {N(goldGained)} (≈{V(goldAsEx)})";

        // ----- killers -----
        var killers = runs.SelectMany(r => r.Deaths).SelectMany(d => d.NearbyMonsters ?? new List<NearbyMonster>())
            .GroupBy(m => m.Name).OrderByDescending(g => g.Count()).Take(6)
            .Select(g => $"{Html(g.Key)} ×{g.Count()}");
        var killersLine = deaths > 0 ? string.Join(" · ", killers) : "no deaths — well played";

        // ----- chart data (oldest→newest for cumulative) — one point per run -----
        var chrono = groups.OrderBy(g => g.LoggedAt).ToList();
        var labels = string.Join(",", chrono.Select(g => $"'{Js(g.LoggedAt.ToString("HH:mm", Ci))}'"));
        var exPerRun = string.Join(",", chrono.Select(g => g.PickupValue.ToString("0.##", Ci)));
        var goldPerRun = string.Join(",", chrono.Select(g => ((double)g.GoldGained / GoldPerExalted).ToString("0.##", Ci)));
        double cum = 0;
        var cumEx = string.Join(",", chrono.Select(g => { cum += g.PickupValue; return cum.ToString("0.##", Ci); }));

        var mapAgg = groups.GroupBy(g => g.MapName)
            .Select(g => new { Name = g.Key, ExHr = g.Sum(r => r.DurationSec) > 0 ? g.Sum(r => r.PickupValue) / (g.Sum(r => r.DurationSec) / 3600.0) : 0 })
            .OrderByDescending(x => x.ExHr).Take(10).ToList();
        var mapLabels = string.Join(",", mapAgg.Select(x => $"'{Js(x.Name)}'"));
        var mapExHr = string.Join(",", mapAgg.Select(x => x.ExHr.ToString("0.##", Ci)));

        // ----- per-run log (one entry per grouped run) -----
        var runLog = opts.ShowRunLog ? string.Concat(groups.Select(BuildRunGroupDetails)) : "";

        // ----- Discord markdown summary (highest-value stats only) -----
        string ItemName(PickupItem p)
        {
            var n = string.IsNullOrEmpty(p.UniqueName) ? p.BaseName : p.UniqueName;
            if (string.IsNullOrEmpty(n)) n = p.ClassName;
            if (p.StackCount > 1) n += $" x{p.StackCount}";
            return n;
        }
        var mdLines = new List<string>
        {
            $"**ExileStats — last {hours:0}h** · {runCount} runs · {totalHours:0.0}h mapped",
            $"💰 Income: **{V(exGained)}** ({V(Hr(exGained))}/h)",
        };
        mdLines.Add($"🪙 Gold: **{Abbrev(goldGained)}** ({Abbrev(Hr(goldGained))}/h)");
        mdLines.Add($"⭐ XP: **{SignAbbrev(xpGained)}** ({SignAbbrev(Hr(xpGained))}/h)");
        mdLines.Add($"🦶 Units: {Abbrev(units)} ({Abbrev(Hr(units))}/h) · ☠️ Deaths: {deaths}");
        if (topItems.Count > 0)
            mdLines.Add("🏆 Top items: " + string.Join(", ",
                topItems.Take(3).Select(x => $"{ItemName(x.Item)} ({V(x.Item.ChaosValue ?? 0)})")));
        var bestRun = topRuns.FirstOrDefault();
        if (bestRun != null)
            mdLines.Add($"⛏️ Best run: {bestRun.MapName} — {V(bestRun.PickupValue)}");
        var mdJs = string.Join(",", mdLines.Select(l => $"'{Js(l)}'"));

        var generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Ci);

        var sb = new StringBuilder();
        sb.Append(TemplateHead);
        sb.Append("  <h1>ExileStats activity report</h1>\n");
        sb.Append($"  <p class=\"sub\">generated {generated} · last {hours:0} h · {runCount} run(s)</p>\n");
        sb.Append("  <button id=\"copyBtn\" class=\"copybtn\">Copy Discord summary</button>\n");

        sb.Append("  <div class=\"cards\">\n").Append(cards).Append("  </div>\n");
        if (opts.ShowNetWorth && latestNw != null)
            sb.Append($"  <p class=\"note\">Net worth as of {Html(latestNw.At.ToString("yyyy-MM-dd HH:mm", Ci))} · " +
                      $"{latestNw.TabCount} tab(s) · {latestNw.ItemCount} items" +
                      (latestNw.IncludesInventory ? " · incl. backpack" : "") +
                      " · only stash tabs opened in-game are counted</p>\n");

        if (opts.ShowNetWorth && hasNwChart)
        {
            sb.Append("  <h2>Net worth over time</h2>\n");
            sb.Append("  <div class=\"chartbox\" style=\"height:300px;\"><canvas id=\"nwChart\"></canvas></div>\n");
        }

        if (opts.ShowIncomeChart)
        {
            sb.Append("  <h2>Income over the period</h2>\n");
            sb.Append("  <div class=\"chartbox\" style=\"height:320px;\"><canvas id=\"incomeChart\"></canvas></div>\n");
        }
        if (opts.ShowMapChart)
        {
            sb.Append("  <h2>Exalted per hour by map</h2>\n");
            sb.Append("  <div class=\"chartbox\" style=\"height:360px;\"><canvas id=\"mapChart\"></canvas></div>\n");
        }

        if (opts.ShowTopItems || opts.ShowTopRuns)
        {
            if (opts.ShowTopItems && opts.ShowTopRuns) sb.Append("  <div class=\"grid2\">\n");
            if (opts.ShowTopItems)
                sb.Append("    <div><h2>Top items</h2><table><thead><tr><th>Item</th><th class=\"num\">Value</th><th>Map</th></tr></thead><tbody>\n")
                  .Append(topItemsRows).Append("    </tbody></table></div>\n");
            if (opts.ShowTopRuns)
                sb.Append("    <div><h2>Most profitable runs</h2><table><thead><tr><th>Map</th><th class=\"num\">Value</th><th class=\"num\">Gold</th><th class=\"num\">XP</th><th class=\"num\">Time</th></tr></thead><tbody>\n")
                  .Append(topRunsRows).Append("    </tbody></table></div>\n");
            if (opts.ShowTopItems && opts.ShowTopRuns) sb.Append("  </div>\n");
        }

        if (opts.ShowEfficiency)
            sb.Append("  <h2>Efficiency</h2>\n  <table class=\"kv\"><tbody>\n").Append(eff).Append("  </tbody></table>\n");

        if (opts.ShowBestMaps || opts.ShowBestLayouts)
        {
            if (opts.ShowBestMaps && opts.ShowBestLayouts) sb.Append("  <div class=\"grid2\">\n");
            if (opts.ShowBestMaps)
                sb.Append("    <div><h2>Best maps</h2><table><thead><tr><th>Map</th><th class=\"num\">Runs</th><th class=\"num\">Val/h</th><th class=\"num\">Mon</th></tr></thead><tbody>\n")
                  .Append(bestMaps).Append("    </tbody></table></div>\n");
            if (opts.ShowBestLayouts)
                sb.Append("    <div><h2>Best layout types</h2><table><thead><tr><th>Layout</th><th class=\"num\">Runs</th><th class=\"num\">Val/h</th><th class=\"num\">Mon</th></tr></thead><tbody>\n")
                  .Append(bestLayouts).Append("    </tbody></table></div>\n");
            if (opts.ShowBestMaps && opts.ShowBestLayouts) sb.Append("  </div>\n");
        }

        if (opts.ShowLootComposition)
        {
            sb.Append("  <h2>Loot composition</h2>\n");
            sb.Append("  <table><thead><tr><th>Category</th><th class=\"num\">Count</th><th class=\"num\">Value</th></tr></thead><tbody>\n")
              .Append(catRows).Append("  </tbody></table>\n");
            sb.Append($"  <p class=\"note\">Rarity: {rarityLine}</p>\n");
            sb.Append($"  <p class=\"note\">Most frequently looted: {dropsLine}</p>\n");
            sb.Append($"  <p class=\"note\">Most dropped (excl. gold): {groundDropsLine}</p>\n");
            sb.Append($"  <p class=\"note\">Income split: {incomeSplit}</p>\n");
            sb.Append($"  <p class=\"note\">Likely killers: {killersLine}</p>\n");
        }

        if (opts.ShowRunLog)
        {
            sb.Append("  <h2>Run log</h2>\n  <p class=\"note\">click a run to expand details + map</p>\n");
            sb.Append(runLog);
        }

        sb.Append("  <footer>ExileStats · value in exalted (NinjaPricer)").Append(divNote)
          .Append("; gold→exalted divisor is display-only (")
          .Append(N(GoldPerExalted)).Append(" g/ex). XP can be negative from PoE2 map death penalty.</footer>\n");
        sb.Append("</div>\n");

        // scripts
        sb.Append("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.js\"></script>\n");
        if (opts.ShowRunLog)
            sb.Append("<script src=\"https://cdn.jsdelivr.net/npm/svg-pan-zoom@3.6.1/dist/svg-pan-zoom.min.js\"></script>\n");
        sb.Append("<script>\n(function(){\n");
        sb.Append("  var labels=[").Append(labels).Append("];\n");
        sb.Append("  var exRun=[").Append(exPerRun).Append("]; var goldRun=[").Append(goldPerRun).Append("]; var cumEx=[").Append(cumEx).Append("];\n");
        sb.Append("  var mapLabels=[").Append(mapLabels).Append("]; var mapExHr=[").Append(mapExHr).Append("];\n");
        sb.Append("  var nwLabels=[").Append(nwLabels).Append("]; var nwEx=[").Append(nwExData).Append("]; var nwDiv=[").Append(nwDivData).Append("];\n");
        sb.Append("  var md=[").Append(mdJs).Append("].join('\\n');\n");
        sb.Append(ScriptBody);
        if (hasNwChart)
        {
            var nwColor = nwUseDiv ? "#E0A33A" : "#9D7BD8";
            var nwData = nwUseDiv ? "nwDiv" : "nwEx";
            var nwUnit = nwUseDiv ? "divine" : "exalted";
            var nwLab = nwUseDiv ? "net worth (div)" : "net worth (ex)";
            sb.Append("  var NW='").Append(nwColor).Append("';\n");
            sb.Append("  new Chart(document.getElementById('nwChart'),{type:'line',\n");
            sb.Append("    data:{labels:nwLabels,datasets:[{label:'").Append(nwLab).Append("',data:").Append(nwData).Append(",borderColor:NW,backgroundColor:NW,tension:.25,pointRadius:2,fill:false}");
            sb.Append("]},\n");
            sb.Append("    options:{responsive:true,maintainAspectRatio:false,plugins:{legend:{labels:{color:tick}}},\n");
            sb.Append("      scales:{x:{grid:{color:grid},ticks:{color:tick}},\n");
            sb.Append("        y:{grid:{color:grid},ticks:{color:tick},title:{display:true,text:'").Append(nwUnit).Append("',color:tick}}");
            sb.Append("}}});\n");
        }
        sb.Append(CopyScript);
        if (opts.ShowRunLog)
            sb.Append(PanZoomScript);
        sb.Append("})();\n</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    // One <details> per grouped run: a summed header wrapping the aggregated run body.
    private static string BuildRunGroupDetails(RunGroup g)
    {
        var when = g.LoggedAt.ToString("MM-dd HH:mm", Ci);
        var areaTag = g.Members.Count > 1 ? $" · {g.Members.Count} areas" : "";
        var summary =
            $"<b>{Html(g.MapName)}</b> <span class=\"dim\">{when}</span> · " +
            $"{V(g.PickupValue)} · {Abbrev(g.GoldGained)} g · XP {SignAbbrev(g.XpGained)} · " +
            $"{g.DurationSec:0}s · {g.Monsters} mon · {g.DeathCount} deaths{areaTag}";

        var sb = new StringBuilder();
        sb.Append("  <details class=\"run\">\n    <summary>").Append(summary).Append("</summary>\n");
        sb.Append("    <div class=\"run-body\">\n");
        sb.Append(BuildRunGroupInner(g, collapsibleLoot: true));
        sb.Append("    </div>\n  </details>\n");
        return sb.ToString();
    }

    // Shared run body: kv stats table, per-area breakdown (multi-area), pickups list, map(s). Used by both
    // the activity report (inside a <details>) and single-run export (immediately visible).
    private static string BuildRunGroupInner(RunGroup g, bool collapsibleLoot = false)
    {
        var head = g.Headline.Record;
        var sb = new StringBuilder();

        // detail kv — run totals (level/layout from the headline map; counts/values summed across areas)
        var white = g.Members.Sum(m => m.Record.White);
        var magic = g.Members.Sum(m => m.Record.Magic);
        var rare = g.Members.Sum(m => m.Record.Rare);
        var uniq = g.Members.Sum(m => m.Record.Unique);
        var groundVal = g.Members.Sum(m => m.Record.LootValue);
        sb.Append("      <table class=\"kv\"><tbody>\n");
        sb.Append(Row2("Area level", head.AreaLevel.ToString(Ci)));
        sb.Append(Row2("Monster level", head.MonsterLevel.ToString(Ci)));
        sb.Append(Row2("Character level", head.CharacterLevel.ToString(Ci)));
        sb.Append(Row2("Monsters (W/M/R/U)", $"{g.Monsters} ({white}/{magic}/{rare}/{uniq})"));
        sb.Append(Row2("Ground loot value", V(groundVal)));
        sb.Append(Row2("Looted value", V(g.PickupValue)));
        var tribute = g.Members.SelectMany(m => m.Content ?? new List<ContentSighting>())
            .Where(c => c.Type == "Ritual").Sum(c => c.TributeGained ?? 0);
        if (tribute > 0)
            sb.Append(Row2("Ritual tribute", tribute.ToString("N0", Ci)));

        // Ritual favours: one map-wide list replicated onto every ritual sighting — take the distinct list.
        var favours = g.Members.SelectMany(m => m.Content ?? new List<ContentSighting>())
            .FirstOrDefault(c => c.Type == "Ritual" && c.RitualFavours is { Count: > 0 })?.RitualFavours;
        if (favours != null)
        {
            var rr = g.Members.SelectMany(m => m.Content ?? new List<ContentSighting>())
                .Where(c => c.Type == "Ritual").Select(c => c.RitualRerolls ?? 0).DefaultIfEmpty(0).Max();
            var bought = favours.Count(f => f.Purchased);
            sb.Append(Row2("Ritual favours", $"{bought}/{favours.Count} bought" + (rr > 0 ? $", {rr} reroll(s)" : "")));
            var items = string.Join(", ", favours.OrderByDescending(f => f.ChaosValue ?? 0).Select(f =>
            {
                var name = string.IsNullOrEmpty(f.UniqueName) ? f.BaseName : f.UniqueName;
                if (f.StackSize > 1) name += $" x{f.StackSize}";
                var val = f.ChaosValue is { } v && v > 0 ? $" ({V(v)})" : "";
                return (f.Purchased ? "✔ " : "") + Html(name) + val;
            }));
            sb.Append(Row2(favours.Any(f => f.Purchased) ? "Favours (✔ bought)" : "Favours offered", items));
        }
        foreach (var exp in g.Members.SelectMany(m => m.Content ?? new List<ContentSighting>())
                     .Where(c => c.Type == "Expedition"))
        {
            var rewards = exp.OfferedRewards ?? exp.RewardPool;
            var head2 = $"Expedition ({exp.RuneCount ?? 0} runes)";
            if (rewards is { Count: > 0 })
            {
                var label = exp.OfferedRewards != null ? "offered" : "pool";
                var items = string.Join(", ", rewards.Take(5).Select(r =>
                    $"{Html(r.Name)} x{r.Count}" + (r.Value is { } v && v > 0 ? $" ({V(v)})" : "")));
                sb.Append(Row2(head2, $"{label}: {items}"));
            }
            else
            {
                sb.Append(Row2(head2, "—"));
            }
        }
        sb.Append(Row2("Units travelled", N(g.UnitsTravelled)));
        sb.Append(Row2("Layout", $"{Html(head.LayoutType ?? "—")} ({head.LayoutConfidence:0.00})"));
        if (head.MapObjectives is { Count: > 0 })
            sb.Append(Row2("Objectives", Html(string.Join(", ", head.MapObjectives))));
        sb.Append("      </tbody></table>\n");

        // per-area breakdown (only when the run spans more than the map itself)
        if (g.Members.Count > 1)
        {
            sb.Append("      <table><thead><tr><th>Area</th><th class=\"num\">Time</th><th class=\"num\">Mon</th><th class=\"num\">Value</th><th class=\"num\">Deaths</th></tr></thead><tbody>\n");
            foreach (var m in g.Members)
                sb.Append("      <tr>" +
                    $"<td>{Html(m.MapName)}</td>" +
                    $"<td class=\"num\">{m.DurationSec:0}s</td>" +
                    $"<td class=\"num\">{m.Monsters}</td>" +
                    $"<td class=\"num\">{V(m.PickupValue)}</td>" +
                    $"<td class=\"num\">{m.Deaths.Count}</td></tr>\n");
            sb.Append("      </tbody></table>\n");
        }

        // pickups table (every area in the run)
        var pickups = g.Pickups.OrderByDescending(p => p.ChaosValue ?? 0).ToList();
        if (pickups.Count > 0)
        {
            if (collapsibleLoot)
                sb.Append($"      <details><summary>Looted ({pickups.Count} items · {V(g.PickupValue)})</summary>\n");
            sb.Append("      <table><thead><tr><th>Looted</th><th class=\"num\">Value</th></tr></thead><tbody>\n");
            foreach (var p in pickups)
            {
                var name = string.IsNullOrEmpty(p.UniqueName) ? p.BaseName : p.UniqueName;
                if (string.IsNullOrEmpty(name)) name = p.ClassName;
                if (p.StackCount > 1) name += $" x{p.StackCount}";
                var val = (p.ChaosValue ?? 0) > 0 ? V(p.ChaosValue ?? 0) : "—";
                sb.Append($"      <tr><td class=\"r-{RarityClass(p.Rarity)}\">{Html(name)}</td><td class=\"num\">{val}</td></tr>\n");
            }
            sb.Append("      </tbody></table>\n");
            if (collapsibleLoot)
                sb.Append("      </details>\n");
        }

        // map(s): one per area in the run (each area has its own svg + coordinate space)
        foreach (var m in g.Members)
        {
            if (g.Members.Count > 1)
                sb.Append($"      <p class=\"note\">{Html(m.MapName)} · {m.DurationSec:0}s</p>\n");
            sb.Append(BuildMap(m));
        }

        return sb.ToString();
    }

    // Inline the run's map.svg (preferred, scales via its viewBox) or embed map.png as base64; either way
    // overlay the run's map-content icons (cropped from Icons.png, positioned in grid space) + a legend.
    private static string BuildMap(RunData r)
    {
        var folder = r.Folder;
        // SVG paints in document order: explored tint (bottom), then path, monster dots, content icons (top).
        var overlay =
            (_showExploredOnMap ? BuildExploredOverlaySvg(r) : "") +
            (_showPathOnMap ? BuildPathOverlaySvg(r) : "") +
            (_showMonstersOnMap ? BuildMonsterOverlaySvg(r) : "") +
            BuildContentOverlaySvg(r);
        var legend = BuildContentLegend(r);        // "<div class=content-legend>…" ("" if none)

        var svgPath = Path.Combine(folder, InstanceStore.SvgFile);
        if (File.Exists(svgPath))
        {
            string svg;
            try { svg = File.ReadAllText(svgPath); } catch { svg = null; }
            if (!string.IsNullOrWhiteSpace(svg))
            {
                var i = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
                if (i > 0) svg = svg.Substring(i);  // drop any <?xml?>/DOCTYPE prolog

                // The SVG viewBox is in grid units, so the content <image>s land at their world position
                // when injected just before the closing tag.
                if (overlay.Length > 0)
                {
                    var close = svg.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
                    if (close >= 0) svg = svg.Substring(0, close) + overlay + svg.Substring(close);
                }
                return "      <div class=\"run-svg\"><span class=\"pz-hint\">scroll = zoom &middot; drag = pan</span>" +
                       svg + "</div>\n" + legend;
            }
        }

        var pngPath = Path.Combine(folder, InstanceStore.ImageFile);
        if (File.Exists(pngPath))
        {
            try
            {
                var b64 = Convert.ToBase64String(File.ReadAllBytes(pngPath));
                var w = r.Record.AreaWidth;
                var h = r.Record.AreaHeight;

                // Wrap the raster in an SVG (viewBox = grid units) so icons can overlay; else a plain image.
                if (overlay.Length > 0 && w > 0 && h > 0)
                {
                    var svg = $"<svg viewBox=\"0 0 {w} {h}\" xmlns=\"http://www.w3.org/2000/svg\">" +
                              $"<image x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" " +
                              $"href=\"data:image/png;base64,{b64}\"/>" + overlay + "</svg>";
                    return "      <div class=\"run-svg\"><span class=\"pz-hint\">scroll = zoom &middot; drag = pan</span>" +
                       svg + "</div>\n" + legend;
                }
                return $"      <div class=\"run-svg\"><img src=\"data:image/png;base64,{b64}\" alt=\"map\"></div>\n";
            }
            catch { /* fall through */ }
        }

        return "      <p class=\"note\">no map captured (requires the forked Radar plugin)</p>\n";
    }

    // ---- Content icons (map overlay) ----

    private static string _iconsPng;                                  // resolved Icons.png path (or null)
    private static System.Drawing.Bitmap _iconsBmp;                   // loaded lazily, disposed per Generate
    private static readonly Dictionary<int, string> _iconUriCache = new();  // (int)MapIconsIndex -> data URI

    // Resolve the shared Icons.png and reset the per-report crop cache. Tries: a relative walk from the
    // plugin dir (…/Plugins/Source/ExileStats/../../../textures/Icons.png), the exilecore2Package env var,
    // then the default install path.
    private static void PrepareIcons(string pluginDirectory)
    {
        _iconUriCache.Clear();
        _iconsBmp?.Dispose();
        _iconsBmp = null;
        _iconsPng = null;

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(pluginDirectory))
            candidates.Add(Path.GetFullPath(Path.Combine(pluginDirectory, "..", "..", "..", "textures", "Icons.png")));
        var pkg = Environment.GetEnvironmentVariable("exilecore2Package");
        if (!string.IsNullOrEmpty(pkg))
            candidates.Add(Path.Combine(pkg, "textures", "Icons.png"));
        candidates.Add(@"C:\Exile\ExileCore2\textures\Icons.png");

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) { _iconsPng = c; break; } }
            catch { /* ignore bad path */ }
        }
    }

    private static void DisposeIcons()
    {
        _iconsBmp?.Dispose();
        _iconsBmp = null;
        _iconUriCache.Clear();
    }

    // base64 PNG data URI of a single icon cropped from Icons.png (by its normalized UV rect), cached per
    // index. Null when the sheet isn't available — the caller then falls back to a colored dot.
    private static string IconDataUri(int iconIndex)
    {
        if (_iconUriCache.TryGetValue(iconIndex, out var cached))
            return cached;

        string uri = null;
        try
        {
            if (_iconsBmp == null && _iconsPng != null && File.Exists(_iconsPng))
                _iconsBmp = new System.Drawing.Bitmap(_iconsPng);

            if (_iconsBmp != null)
            {
                var uv = SpriteHelper.GetUV((MapIconsIndex)iconIndex);
                int sw = _iconsBmp.Width, sh = _iconsBmp.Height;
                var rx = (int)Math.Round(uv.X * sw);
                var ry = (int)Math.Round(uv.Y * sh);
                var rw = (int)Math.Round(uv.Width * sw);
                var rh = (int)Math.Round(uv.Height * sh);
                rx = Math.Clamp(rx, 0, sw - 1);
                ry = Math.Clamp(ry, 0, sh - 1);
                rw = Math.Clamp(rw, 1, sw - rx);
                rh = Math.Clamp(rh, 1, sh - ry);

                using var crop = new System.Drawing.Bitmap(rw, rh);
                using (var g = System.Drawing.Graphics.FromImage(crop))
                    g.DrawImage(_iconsBmp, new System.Drawing.Rectangle(0, 0, rw, rh),
                        new System.Drawing.Rectangle(rx, ry, rw, rh), System.Drawing.GraphicsUnit.Pixel);
                using var ms = new MemoryStream();
                crop.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                uri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }
        catch { uri = null; }

        _iconUriCache[iconIndex] = uri;
        return uri;
    }

    // Content icons as an SVG fragment in grid space (the map viewBox unit). Falls back to a colored dot when
    // the icon sheet is unavailable. Completed content is drawn at reduced opacity.
    private static string BuildContentOverlaySvg(RunData r)
    {
        if (r.Content == null || r.Content.Count == 0)
            return "";

        var w = r.Record.AreaWidth;
        var s = w > 0 ? Math.Clamp(w * 0.015, 12.0, 44.0) : 24.0;  // icon size in grid units
        var sb = new StringBuilder();
        foreach (var c in r.Content)
        {
            var expedition = "";
            if (c.Type == "Expedition")
            {
                expedition = $" · {c.RuneCount ?? 0} runes";
                var rewards = c.OfferedRewards ?? c.RewardPool;
                if (rewards is { Count: > 0 })
                    expedition += $" · {(c.OfferedRewards != null ? "offered" : "best")} {Html(rewards[0].Name)} x{rewards[0].Count}";
            }
            var title = $"{Html(c.Type)} · {FmtClock(c.ElapsedSeconds)}" +
                        (c.Completed == true ? " · done" : c.Completed == false ? " · pending" : "") +
                        (c.TributeGained is { } trib ? $" · {trib:N0} tribute" : "") +
                        expedition;
            var uri = IconDataUri(c.Icon);
            if (uri != null)
            {
                var x = c.GridX - s / 2;
                var y = c.GridY - s / 2;
                sb.Append($"<image x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(s)}\" height=\"{F(s)}\" " +
                          $"href=\"{uri}\" opacity=\"{(c.Completed == true ? "0.5" : "1")}\"><title>{title}</title></image>");
            }
            else
            {
                sb.Append($"<circle cx=\"{F(c.GridX)}\" cy=\"{F(c.GridY)}\" r=\"{F(s / 2)}\" " +
                          $"fill=\"{ContentCatalog.HexFor(c.Type)}\" fill-opacity=\"{(c.Completed == true ? "0.5" : "0.9")}\">" +
                          $"<title>{title}</title></circle>");
            }
        }
        return sb.ToString();
    }

    // Explored-area tint as row-merged <rect>s in grid space (mirrors MapStatsWindow's tint): even-odd
    // walkable mask of the terrain loops ∩ reveal-disks along the path, at the configured reveal radius.
    private static string BuildExploredOverlaySvg(RunData r)
    {
        var rec = r.Record;
        if (_revealRadius <= 0f || rec.AreaWidth <= 0 || rec.AreaHeight <= 0)
            return "";

        var svgPath = Path.Combine(r.Folder, InstanceStore.SvgFile);
        if (!File.Exists(svgPath))
            return "";

        List<Vector2[]> loops;
        try { loops = LayoutClassifier.ParseTerrainLoops(File.ReadAllText(svgPath)); }
        catch { return ""; }
        if (loops.Count == 0)
            return "";

        var pathPts = WalkablePath(r, loops);
        if (pathPts.Count == 0)
            return "";

        if (MapCoverage.ComputeMask(rec.AreaWidth, rec.AreaHeight, loops, pathPts, _revealRadius) is not { } m)
            return "";

        var sb = new StringBuilder();
        var opa = Math.Clamp(_tintOpacity, 0.0, 1.0).ToString("0.###", Ci);
        var cell = m.Cell;
        // Row-merge consecutive explored cells into rectangles to keep the SVG small.
        for (var gy = 0; gy < m.Gh; gy++)
        {
            var gx = 0;
            while (gx < m.Gw)
            {
                if (!m.Explored[gy * m.Gw + gx]) { gx++; continue; }
                var start = gx;
                while (gx < m.Gw && m.Explored[gy * m.Gw + gx]) gx++;
                var x = start * cell;
                var y = gy * cell;
                var ww = (gx - start) * cell;
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(ww)}\" height=\"{F(cell)}\" " +
                          $"fill=\"{_tintHex}\" fill-opacity=\"{opa}\"/>");
            }
        }
        return sb.ToString();
    }

    // Player path as polyline segments in grid space (mirrors MapStatsWindow): dense path.json (fall back to
    // snapshots), filtered to walkable, split on checkpoint-teleport jumps; start/end dots + death markers.
    private static string BuildPathOverlaySvg(RunData r)
    {
        var rec = r.Record;
        var pts = r.Path.Count > 0
            ? r.Path.Select(p => new Vector2(p.X, p.Y)).ToList()
            : r.Snapshots.Select(s => new Vector2(s.GridX, s.GridY)).ToList();

        // Filter outliers to walkable terrain when loops are available (drops the bogus area-unload read).
        var svgPath = Path.Combine(r.Folder, InstanceStore.SvgFile);
        if (r.Path.Count > 0 && File.Exists(svgPath))
        {
            try
            {
                var loops = LayoutClassifier.ParseTerrainLoops(File.ReadAllText(svgPath));
                pts = WalkablePath(r, loops);
            }
            catch { /* keep raw */ }
        }
        if (pts.Count < 2)
            return "";

        var w = rec.AreaWidth;
        var h = rec.AreaHeight;
        var jump = w > 0 && h > 0 ? 0.2 * Math.Sqrt((double)w * w + (double)h * h) : double.MaxValue;
        var jumpSq = jump * jump;
        var sw = w > 0 ? Math.Clamp(w * 0.002, 0.5, 3.0) : 1.5;   // stroke width in grid units

        var sb = new StringBuilder();
        // Break into separate polylines wherever a teleport jump occurs.
        var seg = new StringBuilder();
        var segCount = 0;
        void Flush()
        {
            if (segCount >= 2)
                sb.Append($"<polyline points=\"{seg}\" fill=\"none\" stroke=\"#4DB3FF\" " +
                          $"stroke-opacity=\"0.8\" stroke-width=\"{F(sw)}\" stroke-linejoin=\"round\" " +
                          $"stroke-linecap=\"round\" vector-effect=\"non-scaling-stroke\"/>");
            seg.Clear();
            segCount = 0;
        }
        Vector2? prev = null;
        foreach (var p in pts)
        {
            if (prev is { } pg && Vector2.DistanceSquared(pg, p) > jumpSq)
                Flush();
            if (seg.Length > 0) seg.Append(' ');
            seg.Append($"{F(p.X)},{F(p.Y)}");
            segCount++;
            prev = p;
        }
        Flush();

        var dot = w > 0 ? Math.Clamp(w * 0.004, 2.0, 7.0) : 4.0;
        sb.Append($"<circle cx=\"{F(pts[0].X)}\" cy=\"{F(pts[0].Y)}\" r=\"{F(dot)}\" fill=\"#33FF4D\"><title>start</title></circle>");
        sb.Append($"<circle cx=\"{F(pts[^1].X)}\" cy=\"{F(pts[^1].Y)}\" r=\"{F(dot)}\" fill=\"#FF991A\"><title>end</title></circle>");

        foreach (var d in r.Deaths)
            sb.Append($"<circle cx=\"{F(d.GridX)}\" cy=\"{F(d.GridY)}\" r=\"{F(dot * 1.2)}\" " +
                      $"fill=\"#FF1A1A\" fill-opacity=\"0.9\" stroke=\"#FFFFFF\" stroke-width=\"{F(sw)}\">" +
                      $"<title>death</title></circle>");
        return sb.ToString();
    }

    // Path points filtered to walkable terrain (even-odd point-in-polygon across the loops). Mirrors
    // MapStatsWindow.FilterPathToWalkable: skip the filter if it would drop > 25% (unreliable parse).
    private static List<Vector2> WalkablePath(RunData r, List<Vector2[]> loops)
    {
        var pts = r.Path.Count > 0
            ? r.Path.Select(p => new Vector2(p.X, p.Y)).ToList()
            : r.Snapshots.Select(s => new Vector2(s.GridX, s.GridY)).ToList();
        if (loops == null || loops.Count == 0 || pts.Count == 0)
            return pts;
        var kept = pts.Where(p => InsideLoops(loops, p.X, p.Y)).ToList();
        if (kept.Count == pts.Count || kept.Count < pts.Count * 0.75)
            return pts;
        return kept;
    }

    private static bool InsideLoops(List<Vector2[]> loops, float px, float py)
    {
        var inside = false;
        foreach (var loop in loops)
        {
            var n = loop.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = loop[i];
                var b = loop[j];
                if ((a.Y > py) != (b.Y > py) &&
                    px < (b.X - a.X) * (py - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
        }
        return inside;
    }

    // Monster first-seen positions as small rarity-colored SVG circles in grid space. No icons (file size).
    private static string BuildMonsterOverlaySvg(RunData r)
    {
        if (r.MonsterPositions == null || r.MonsterPositions.Count == 0)
            return "";

        var w = r.Record.AreaWidth;
        var rad = w > 0 ? Math.Clamp(w * 0.004, 2.0, 8.0) : 4.0;   // dot radius in grid units
        var sb = new StringBuilder();
        foreach (var m in r.MonsterPositions)
        {
            var title = string.IsNullOrEmpty(m.Name) ? Html(m.Rarity) : $"{Html(m.Rarity)} · {Html(m.Name)}";
            sb.Append($"<circle cx=\"{F(m.GridX)}\" cy=\"{F(m.GridY)}\" r=\"{F(rad)}\" " +
                      $"fill=\"{RarityHex(m.Rarity)}\" fill-opacity=\"0.85\"><title>{title}</title></circle>");
        }
        return sb.ToString();
    }

    private static string RarityHex(string rarity) => rarity switch
    {
        "Magic" => "#7388ff",
        "Rare" => "#fff25a",
        "Unique" => "#ff8c26",
        _ => "#d9d9d9",
    };

    private static string BuildContentLegend(RunData r)
    {
        if (r.Content == null || r.Content.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.Append("      <div class=\"content-legend\">");
        foreach (var t in r.Content.Select(c => c.Type).Distinct())
        {
            var n = r.Content.Count(c => c.Type == t);
            sb.Append($"<span class=\"cl\"><i style=\"background:{ContentCatalog.HexFor(t)}\"></i>{Html(t)} ×{n}</span>");
        }
        sb.Append("</div>\n");
        return sb.ToString();
    }

    // Invariant short decimal for SVG coordinates.
    private static string F(double v) => v.ToString("0.#", Ci);

    private static string FmtClock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = (int)seconds;
        return $"{t / 60}:{t % 60:00}";
    }

    private static string Card(string label, string val) =>
        $"    <div class=\"card\"><div class=\"lab\">{Html(label)}</div><div class=\"val\">{Html(val)}</div></div>\n";

    private static string Row2(string k, string v) =>
        $"    <tr><td class=\"k\">{Html(k)}</td><td>{v}</td></tr>\n";

    private static string RarityClass(string rarity) => rarity switch
    {
        "Unique" => "unique",
        "Rare" => "rare",
        "Magic" => "magic",
        _ => "normal",
    };

    private static string BuildEmptyHtml(double hours) =>
        TemplateHead +
        "  <h1>ExileStats activity report</h1>\n" +
        $"  <p class=\"sub\">No map runs in the last {hours:0} h.</p>\n" +
        "  <p class=\"note\">Map some areas (and leave them, so they're logged), then generate again.</p>\n" +
        "</div>\n</body>\n</html>\n";

    // ---------- templates (CSS/theming reused from DashboardGenerator, with report-specific additions) ----------

    private const string TemplateHead =
@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>ExileStats activity report</title>
<style>
  :root{--bg:#faf9f5;--surface:#fff;--surface2:#f1efe8;--text:#2c2c2a;--muted:#5f5e5a;--border:rgba(0,0,0,.10);--grid:rgba(0,0,0,.08);}
  @media (prefers-color-scheme: dark){:root{--bg:#1f1e1c;--surface:#2a2926;--surface2:#333230;--text:#ece9e2;--muted:#b4b2a9;--border:rgba(255,255,255,.12);--grid:rgba(255,255,255,.10);}}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;line-height:1.6;padding:28px 20px 48px;}
  .wrap{max-width:980px;margin:0 auto;}
  h1{font-size:24px;font-weight:500;margin:0 0 4px;}
  .sub{color:var(--muted);font-size:14px;margin:0 0 24px;}
  h2{font-size:17px;font-weight:500;margin:32px 0 8px;}
  .note{color:var(--muted);font-size:13px;margin:6px 0;}
  .dim{color:var(--muted);font-weight:400;}
  .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:12px;margin:0 0 8px;}
  .card{background:var(--surface2);border-radius:8px;padding:12px 14px;}
  .card .lab{font-size:12px;color:var(--muted);} .card .val{font-size:22px;font-weight:500;margin-top:2px;}
  .copybtn{margin:0 0 8px;padding:8px 14px;font-size:13px;font-weight:500;color:#fff;background:#5865F2;border:0;border-radius:8px;cursor:pointer;}
  .copybtn:hover{background:#4752c4;}
  .chartbox{position:relative;width:100%;background:var(--surface);border:.5px solid var(--border);border-radius:12px;padding:14px;}
  .grid2{display:grid;grid-template-columns:1fr 1fr;gap:20px;}
  @media (max-width:720px){.grid2{grid-template-columns:1fr;}}
  table{width:100%;border-collapse:collapse;font-size:13px;}
  th,td{text-align:left;padding:5px 8px;border-bottom:.5px solid var(--border);}
  th{color:var(--muted);font-weight:500;}
  td.num,th.num{text-align:right;font-variant-numeric:tabular-nums;}
  table.kv td.k{color:var(--muted);width:50%;}
  .r-unique{color:#af6025;} .r-rare{color:#b0a000;} .r-magic{color:#6a6acf;} .r-normal{color:var(--text);}
  details.run{background:var(--surface);border:.5px solid var(--border);border-radius:10px;margin:8px 0;padding:4px 12px;}
  details.run summary{cursor:pointer;font-size:14px;padding:6px 0;}
  .run-body{padding:8px 0 12px;}
  .run-svg{background:#0b0b0d;border-radius:8px;padding:8px;margin-top:10px;position:relative;}
  /* fixed height so svg-pan-zoom has a measurable viewport; pan/zoom fills it */
  .run-svg svg{width:100%;height:520px;display:block;margin:0 auto;touch-action:none;cursor:grab;}
  .run-svg svg:active{cursor:grabbing;}
  .run-svg img{max-width:100%;height:auto;display:block;margin:0 auto;}
  .run-svg .pz-hint{position:absolute;top:10px;right:12px;font-size:11px;color:#9a978d;pointer-events:none;}
  /* terrain as a thin outline, not a filled blob (Radar SVG ships it filled) */
  .run-svg svg path{fill:none;stroke:#9a978d;stroke-width:1.2;vector-effect:non-scaling-stroke;}
  .content-legend{display:flex;flex-wrap:wrap;gap:6px 14px;margin:6px 2px 2px;font-size:12px;color:var(--muted);}
  .content-legend .cl{display:inline-flex;align-items:center;gap:5px;}
  .content-legend i{width:10px;height:10px;border-radius:2px;display:inline-block;}
  footer{color:var(--muted);font-size:12px;margin-top:28px;border-top:.5px solid var(--border);padding-top:12px;}
</style>
</head>
<body>
<div class=""wrap"">
";

    private const string ScriptBody =
@"  var mq=window.matchMedia('(prefers-color-scheme: dark)').matches;
  var grid=mq?'rgba(255,255,255,.10)':'rgba(0,0,0,.08)'; var tick=mq?'#D3D1C7':'#5F5E5A';
  var EX='#1D9E75', GOLD='#EF9F27', CUM='#378ADD';
  var _ic=document.getElementById('incomeChart');
  if(_ic) new Chart(_ic,{
    data:{labels:labels,datasets:[
      {type:'bar',label:'exalted / run',data:exRun,backgroundColor:EX,borderWidth:0},
      {type:'bar',label:'gold / run (as ex)',data:goldRun,backgroundColor:GOLD,borderWidth:0},
      {type:'line',label:'cumulative exalted',data:cumEx,borderColor:CUM,backgroundColor:CUM,tension:.25,pointRadius:2,yAxisID:'y1'}]},
    options:{responsive:true,maintainAspectRatio:false,plugins:{legend:{labels:{color:tick}}},
      scales:{x:{stacked:true,grid:{color:grid},ticks:{color:tick}},
        y:{stacked:true,grid:{color:grid},ticks:{color:tick},title:{display:true,text:'per run (ex)',color:tick}},
        y1:{position:'right',grid:{display:false},ticks:{color:tick},title:{display:true,text:'cumulative (ex)',color:tick}}}}});
  var _mc=document.getElementById('mapChart');
  if(_mc) new Chart(_mc,{type:'bar',
    data:{labels:mapLabels,datasets:[{data:mapExHr,backgroundColor:EX,borderWidth:0,barThickness:20}]},
    options:{indexAxis:'y',responsive:true,maintainAspectRatio:false,plugins:{legend:{display:false},tooltip:{callbacks:{label:function(c){return c.parsed.x.toFixed(1)+' ex/h';}}}},
      scales:{x:{grid:{color:grid},ticks:{color:tick},title:{display:true,text:'exalted per hour',color:tick}},y:{grid:{display:false},ticks:{color:tick,font:{size:12}}}}}});
";

    private const string CopyScript =
@"  function copyText(t){
    if(navigator.clipboard&&navigator.clipboard.writeText){return navigator.clipboard.writeText(t);}
    return new Promise(function(res,rej){try{var ta=document.createElement('textarea');ta.value=t;ta.style.position='fixed';ta.style.opacity='0';document.body.appendChild(ta);ta.focus();ta.select();document.execCommand('copy');document.body.removeChild(ta);res();}catch(e){rej(e);}});
  }
  var btn=document.getElementById('copyBtn');
  if(btn){btn.addEventListener('click',function(){copyText(md).then(function(){var o=btn.textContent;btn.textContent='Copied!';setTimeout(function(){btn.textContent=o;},1500);},function(){btn.textContent='Copy failed — select & copy manually';});});}
";

    // Lazy svg-pan-zoom init: maps live inside collapsed <details> (display:none -> zero size), so init each
    // svg the first time its run is expanded, when getBoundingClientRect is finally valid.
    private const string PanZoomScript =
@"  function initPZ(d){
    d.querySelectorAll('.run-svg svg').forEach(function(s){
      if(s.getAttribute('data-pz'))return;
      s.setAttribute('data-pz','1');
      try{var pz=svgPanZoom(s,{zoomEnabled:true,panEnabled:true,controlIconsEnabled:false,
        dblClickZoomEnabled:true,fit:true,center:true,minZoom:0.5,maxZoom:60,zoomScaleSensitivity:0.3});
        pz.resize();pz.fit();pz.center();}catch(e){}
    });
  }
  if(window.svgPanZoom){
    document.querySelectorAll('details.run').forEach(function(d){
      d.addEventListener('toggle',function(){if(d.open)initPZ(d);});
    });
  }
";

    // Immediate svg-pan-zoom init for single-run exports where maps are always visible (no <details>).
    private const string SingleRunPanZoomScript =
@"  if(window.svgPanZoom){
    document.querySelectorAll('.run-svg svg').forEach(function(s){
      try{var pz=svgPanZoom(s,{zoomEnabled:true,panEnabled:true,controlIconsEnabled:false,
        dblClickZoomEnabled:true,fit:true,center:true,minZoom:0.5,maxZoom:60,zoomScaleSensitivity:0.3});
        pz.resize();pz.fit();pz.center();}catch(e){}
    });
  }
";
}
