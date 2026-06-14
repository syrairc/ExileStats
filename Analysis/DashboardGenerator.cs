using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ExileStats
{
    /// <summary>
    /// Generates a self-contained map_dashboard.html from the per-run run.json files
    /// the plugin already writes, merged with layout classifications from layouts.json.
    ///
    /// Drop-in and dependency-free (System.Text.Json only). It re-reads the JSON files
    /// rather than depending on the plugin's in-memory types, so it can be called any time.
    ///
    /// Usage (e.g. after a map completes, or behind a hotkey/menu button):
    ///     DashboardGenerator.Generate(
    ///         mapsDir:      Path.Combine(PluginDir, "maps"),
    ///         layoutsPath:  Path.Combine(PluginDir, "layouts.json"),
    ///         outputPath:   Path.Combine(PluginDir, "map_dashboard.html"));
    ///
    /// All numeric stats (size, density, counts) are computed here. Layout TYPE comes
    /// from layouts.json; any MapId not listed there is treated as "unclassified".
    /// </summary>
    public static class DashboardGenerator
    {
        // ---- coverage floor + density rating bands (keep in sync with layout_database.json) ----
        // Density = monsters ÷ explored *walkable* megagrid (not the full bbox). A run feeds density only if
        // it has coverage data and cleared a meaningful slice (floor below); per-run densities are then
        // medianed. Explored-area normalization makes partial clears valid, so there's no monster-count gate.
        private const double MinClearSeconds = 30.0;
        private const double MinExploredPct  = 15.0;   // ignore barely-explored runs (tiny denom = noisy)

        // PROVISIONAL bands — the explored-walkable denom rescaled density ~3-4× vs the old bbox bands
        // (old: 60/80/95/135). RECALIBRATE these four numbers from the observed spread after the first
        // regen, and mirror them in: BandColor, the JS band() in ScriptBody, the legend in TemplateBodyMid.
        private const double BandVeryLow = 210, BandLow = 280, BandMedium = 330, BandHigh = 470;

        private static string Rating(double d) =>
            d < BandVeryLow ? "very_low" : d < BandLow ? "low" : d < BandMedium ? "medium"
            : d < BandHigh ? "high" : "very_high";

        private static string BandColor(double d) =>
            d >= BandHigh ? "#E24B4A" : d >= BandMedium ? "#EF9F27" : d >= BandLow ? "#1D9E75"
            : d >= BandVeryLow ? "#378ADD" : "#888780";

        private static readonly Dictionary<string, string> TypeColor = new()
        {
            ["grid_network"] = "#378ADD", ["open_obstacles"] = "#1D9E75", ["perimeter_loop"] = "#7F77DD",
            ["central_loop"] = "#E24B4A", ["linear"] = "#EF9F27", ["converging_branches"] = "#D85A30",
            ["boss_arena"] = "#888780", ["unclassified"] = "#B4B2A9"
        };

        private sealed class Run
        {
            public double DurationSec;
            public long Area;          // AreaWidth * AreaHeight, 0 if unknown
            public int Monsters;
            public double Loot;
            public string LayoutType;  // auto-classified per run (snake_case), null if not classified
            public double LayoutConf;  // classifier confidence 0..1
            public double? ExploredArea; // explored *walkable* area (grid²); null when no coverage was logged
            public double? ExploredPct;  // ExploredPercent (% of walkable); null when absent
        }

        private sealed class MapAgg
        {
            public string Name = "";
            public string Type = "unclassified";
            public int Runs;
            public int Clears;
            public long AreaMedian;        // grid^2
            public int ClearMonstersMedian;
            public bool HasDensity;
            public double Density;         // monsters per megagrid
        }

        public static void Generate(string mapsDir, string layoutsPath, string outputPath)
        {
            var layouts = LoadLayouts(layoutsPath);                 // MapId -> type
            var runsByMap = LoadRuns(mapsDir);                      // MapId -> (displayName, runs)

            var aggs = new List<MapAgg>();
            foreach (var (mapId, (name, runs)) in runsByMap)
            {
                var areas = runs.Where(r => r.Area > 0).Select(r => r.Area).ToList();
                // Runs that feed density: have explored-walkable area + cleared a meaningful slice. Explored-
                // area normalization makes partial clears valid, so no monster-count gate (unlike the old bbox
                // method). Old runs without coverage data are simply absent → density null until re-run.
                var covered = runs.Where(r => r.ExploredArea is > 0
                    && r.ExploredPct >= MinExploredPct && r.DurationSec >= MinClearSeconds).ToList();

                // Manual layouts.json wins; else the majority vote across this map's auto-classified runs.
                var manual = layouts.TryGetValue(mapId, out var t) ? t : null;
                var a = new MapAgg
                {
                    Name = name,
                    Type = manual ?? VoteType(runs) ?? "unclassified",
                    Runs = runs.Count,
                    Clears = covered.Count,
                    AreaMedian = areas.Count > 0 ? Median(areas) : 0
                };

                if (covered.Count > 0)
                {
                    a.ClearMonstersMedian = (int)Median(covered.Select(r => (long)r.Monsters).ToList());
                    // Median of per-run densities (monsters ÷ monster-reveal walkable megagrid). Per-run (not
                    // ratio-of-medians) because the covered area varies run-to-run now.
                    var perRun = covered.Select(r => r.Monsters / (r.ExploredArea.Value / 1_000_000.0)).ToList();
                    a.Density = Math.Round(MedianD(perRun), 1);
                    a.HasDensity = true;
                }
                aggs.Add(a);
            }

            // Effective type per MapId across the union of run-data maps + manual-only entries (manual
            // wins, else the vote, else unclassified) — so auto-classified maps absent from layouts.json
            // are still counted in the breakdown.
            var effByMapId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (mapId, (_, runs)) in runsByMap)
                effByMapId[mapId] = (layouts.TryGetValue(mapId, out var mt) ? mt : null) ?? VoteType(runs) ?? "unclassified";
            foreach (var (mapId, type) in layouts)                       // manual-only maps (no run data)
                if (!effByMapId.ContainsKey(mapId)) effByMapId[mapId] = type;

            var layoutCounts = effByMapId.Values
                .GroupBy(v => v)
                .Select(g => (Type: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count).ThenBy(x => x.Type)
                .ToList();

            var withDensity = aggs.Where(a => a.HasDensity)
                                  .OrderByDescending(a => a.Density).ToList();

            var html = BuildHtml(
                totalClassified: effByMapId.Count(kv => kv.Value != "unclassified"),
                withRunData: runsByMap.Count,
                withDensity: withDensity.Count,
                layoutTypeCount: layoutCounts.Count,
                density: withDensity,
                layoutCounts: layoutCounts);

            File.WriteAllText(outputPath, html, new UTF8Encoding(false));
        }

        // ---------- loading ----------

        private static Dictionary<string, string> LoadLayouts(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("maps", out var maps))
                foreach (var p in maps.EnumerateObject())
                    map[p.Name] = p.Value.TryGetProperty("type", out var ty) ? ty.GetString() ?? "unclassified" : "unclassified";
            return map;
        }

        private static Dictionary<string, (string name, List<Run> runs)> LoadRuns(string mapsDir)
        {
            var result = new Dictionary<string, (string, List<Run>)>(StringComparer.Ordinal);
            if (!Directory.Exists(mapsDir)) return result;

            foreach (var runFile in Directory.EnumerateFiles(mapsDir, "run.json", SearchOption.AllDirectories))
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(File.ReadAllText(runFile)); }
                catch { continue; } // tolerate a mid-write file
                using (doc)
                {
                    IEnumerable<JsonElement> entries = doc.RootElement.ValueKind == JsonValueKind.Array
                        ? doc.RootElement.EnumerateArray()
                        : SingleArray(doc.RootElement);

                    foreach (var e in entries)
                    {
                        var mapId = Str(e, "AreaId");
                        if (string.IsNullOrEmpty(mapId)) continue;

                        // Density area (grid²) = DensityWalkable cells (walkable within the monster-reveal
                        // radius of the path) × CellSize². Null when the run carries no coverage data (old
                        // runs / no forked Radar) → excluded from density.
                        var dw = NumN(e, "DensityWalkable");
                        var run = new Run
                        {
                            DurationSec = Duration(e),
                            Area = (long)Num(e, "AreaWidth") * (long)Num(e, "AreaHeight"),
                            Monsters = (int)Num(e, "MonstersTotal"),
                            Loot = NumD(e, "LootValue"),
                            LayoutType = Str(e, "LayoutType"),
                            LayoutConf = NumD(e, "LayoutConfidence"),
                            ExploredArea = dw.HasValue ? dw.Value * (double)(MapCoverage.CellSize * MapCoverage.CellSize) : (double?)null,
                            ExploredPct = NumN(e, "ExploredPercent")
                        };

                        var name = Str(e, "Name");
                        if (string.IsNullOrEmpty(name))
                            name = mapId.StartsWith("Map", StringComparison.Ordinal) ? mapId.Substring(3) : mapId;

                        if (!result.TryGetValue(mapId, out var slot))
                            result[mapId] = slot = (name, new List<Run>());
                        slot.Item2.Add(run);
                    }
                }
            }
            return result;
        }

        private static IEnumerable<JsonElement> SingleArray(JsonElement e) { yield return e; }

        // ---------- helpers ----------

        private static double Duration(JsonElement e)
        {
            // Prefer recomputing from timestamps (handles timezone offset); fall back to DurationSeconds.
            if (TryDate(e, "LoggedAt", out var logged) && TryDate(e, "EnteredAt", out var entered))
                return (logged - entered).TotalSeconds;
            return NumD(e, "DurationSeconds");
        }

        private static bool TryDate(JsonElement e, string key, out DateTimeOffset dt)
        {
            dt = default;
            return e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
                   DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt);
        }

        private static string Str(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static long Num(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

        private static double NumD(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : 0;

        // Nullable read: null when the property is absent (distinguishes "missing" from a real 0).
        private static double? NumN(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)
                ? n : (double?)null;

        private static long Median(List<long> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
        }

        private static double MedianD(List<double> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
        }

        // Majority vote of a map's per-run auto-classified LayoutType; ties broken by highest mean
        // confidence among the tied types. null if no run carries a (non-empty) classification.
        private static string VoteType(List<Run> runs)
        {
            var classified = runs.Where(r => !string.IsNullOrEmpty(r.LayoutType)).ToList();
            if (classified.Count == 0) return null;
            return classified
                .GroupBy(r => r.LayoutType)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Average(r => r.LayoutConf))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .First().Key;
        }

        // ---------- HTML ----------

        private static string Js(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private static string BuildHtml(int totalClassified, int withRunData, int withDensity,
            int layoutTypeCount, List<MapAgg> density, List<(string Type, int Count)> layoutCounts)
        {
            var ci = CultureInfo.InvariantCulture;

            var mapsLiteral = string.Join(",\n    ", density.Select(a =>
                $"['{Js(a.Name)}',{(a.AreaMedian / 1_000_000.0).ToString("0.00", ci)},{a.Density.ToString("0.0", ci)},'{a.Type}']"));

            var layoutLabels = string.Join(",", layoutCounts.Select(l => $"'{l.Type}'"));
            var layoutVals = string.Join(",", layoutCounts.Select(l => l.Count.ToString(ci)));

            string generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", ci);

            return TemplateHead
                + $"  <p class=\"sub\">ExileStats database · generated {generated}</p>\n"
                + CardsBlock(totalClassified, withRunData, withDensity, layoutTypeCount)
                + TemplateBodyMid
                + "<script src=\"https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.js\"></script>\n"
                + "<script>\n(function(){\n"
                + "  var maps=[\n    " + mapsLiteral + "\n  ];\n"
                + "  var lLabels=[" + layoutLabels + "]; var lVals=[" + layoutVals + "];\n"
                + ScriptBody
                + "})();\n</script>\n</body>\n</html>\n";
        }

        private static string CardsBlock(int a, int b, int c, int d) =>
            "  <div class=\"cards\">\n" +
            $"    <div class=\"card\"><div class=\"lab\">Maps classified</div><div class=\"val\">{a}</div></div>\n" +
            $"    <div class=\"card\"><div class=\"lab\">With run data</div><div class=\"val\">{b}</div></div>\n" +
            $"    <div class=\"card\"><div class=\"lab\">With density</div><div class=\"val\">{c}</div></div>\n" +
            $"    <div class=\"card\"><div class=\"lab\">Layout types</div><div class=\"val\">{d}</div></div>\n" +
            "  </div>\n";

        private const string TemplateHead =
@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Map Layout & Density Dashboard</title>
<style>
  :root{--bg:#faf9f5;--surface:#fff;--surface2:#f1efe8;--text:#2c2c2a;--muted:#5f5e5a;--border:rgba(0,0,0,.10);--grid:rgba(0,0,0,.08);}
  @media (prefers-color-scheme: dark){:root{--bg:#1f1e1c;--surface:#2a2926;--surface2:#333230;--text:#ece9e2;--muted:#b4b2a9;--border:rgba(255,255,255,.12);--grid:rgba(255,255,255,.10);}}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;line-height:1.6;padding:28px 20px 48px;}
  .wrap{max-width:880px;margin:0 auto;}
  h1{font-size:24px;font-weight:500;margin:0 0 4px;}
  .sub{color:var(--muted);font-size:14px;margin:0 0 24px;}
  h2{font-size:17px;font-weight:500;margin:32px 0 2px;}
  .note{color:var(--muted);font-size:13px;margin:0 0 14px;}
  .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:12px;margin:0 0 8px;}
  .card{background:var(--surface2);border-radius:8px;padding:14px 16px;}
  .card .lab{font-size:13px;color:var(--muted);} .card .val{font-size:24px;font-weight:500;margin-top:2px;}
  .legend{display:flex;flex-wrap:wrap;gap:14px;margin:0 0 10px;font-size:12px;color:var(--muted);}
  .legend span{display:flex;align-items:center;gap:5px;} .sw{width:10px;height:10px;border-radius:2px;display:inline-block;}
  .chartbox{position:relative;width:100%;background:var(--surface);border:.5px solid var(--border);border-radius:12px;padding:14px;}
  footer{color:var(--muted);font-size:12px;margin-top:28px;border-top:.5px solid var(--border);padding-top:12px;}
</style>
</head>
<body>
<div class=""wrap"">
  <h1>Map layout, size &amp; density</h1>
";

        private const string TemplateBodyMid =
@"  <h2>Monster density — explored area</h2>
  <p class=""note"">monsters per walkable megagrid within the monster-reveal radius of the path (per 1,000,000 grid²), colored by provisional rating</p>
  <div class=""legend"">
    <span><span class=""sw"" style=""background:#E24B4A""></span>very high (≥470)</span>
    <span><span class=""sw"" style=""background:#EF9F27""></span>high (330–469)</span>
    <span><span class=""sw"" style=""background:#1D9E75""></span>medium (280–329)</span>
    <span><span class=""sw"" style=""background:#378ADD""></span>low (210–279)</span>
    <span><span class=""sw"" style=""background:#888780""></span>very low (&lt;210)</span>
  </div>
  <div class=""chartbox"" style=""height:740px;""><canvas id=""densityChart"" role=""img"" aria-label=""Bar chart ranking maps by monster density.""></canvas></div>

  <h2>Size vs density</h2>
  <p class=""note"">map area (megagrid) against density; each point colored by layout type</p>
  <div class=""legend"" id=""scatterLegend""></div>
  <div class=""chartbox"" style=""height:420px;""><canvas id=""scatterChart"" role=""img"" aria-label=""Scatter of map area versus density, colored by layout type.""></canvas></div>

  <h2>Layout types</h2>
  <p class=""note"">how the classified maps break down by structural archetype</p>
  <div class=""chartbox"" style=""height:400px;""><canvas id=""layoutChart"" role=""img"" aria-label=""Bar chart of layout type counts.""></canvas></div>

  <footer>Layout judged from wall structure (SVG grey = walkable); colored objective lines ignored. Density = monsters ÷ walkable megagrid within the monster-reveal radius of the path (median of per-run; runs with ≥15% terrain explored, ≥30s); runs without coverage data excluded; bands provisional. Stats computed by the ExileStats plugin; layout types from layouts.json.</footer>
</div>
";

        private const string ScriptBody =
@"  var typeColor={grid_network:'#378ADD',open_obstacles:'#1D9E75',perimeter_loop:'#7F77DD',central_loop:'#E24B4A',linear:'#EF9F27',converging_branches:'#D85A30',boss_arena:'#888780',unclassified:'#B4B2A9'};
  function band(v){return v>=470?'#E24B4A':v>=330?'#EF9F27':v>=280?'#1D9E75':v>=210?'#378ADD':'#888780';}
  var mq=window.matchMedia('(prefers-color-scheme: dark)').matches;
  var grid=mq?'rgba(255,255,255,.10)':'rgba(0,0,0,.08)'; var tick=mq?'#D3D1C7':'#5F5E5A';
  new Chart(document.getElementById('densityChart'),{type:'bar',
    data:{labels:maps.map(function(m){return m[0];}),datasets:[{data:maps.map(function(m){return m[2];}),backgroundColor:maps.map(function(m){return band(m[2]);}),borderWidth:0,barThickness:24}]},
    options:{indexAxis:'y',responsive:true,maintainAspectRatio:false,plugins:{legend:{display:false},tooltip:{callbacks:{label:function(c){return c.parsed.x.toFixed(1)+' /Mgrid · '+maps[c.dataIndex][3];}}}},
      scales:{x:{grid:{color:grid},ticks:{color:tick},title:{display:true,text:'monsters per explored walkable megagrid',color:tick}},y:{grid:{display:false},ticks:{color:tick,font:{size:12}}}}}});
  var byType={}; maps.forEach(function(m){(byType[m[3]]=byType[m[3]]||[]).push({x:m[1],y:m[2],label:m[0]});});
  var sds=Object.keys(byType).map(function(t){return{label:t,data:byType[t],backgroundColor:typeColor[t]||'#888780',pointRadius:6,pointHoverRadius:8};});
  new Chart(document.getElementById('scatterChart'),{type:'scatter',data:{datasets:sds},
    options:{responsive:true,maintainAspectRatio:false,plugins:{legend:{display:false},tooltip:{callbacks:{label:function(c){return c.raw.label+': '+c.raw.x.toFixed(2)+' Mgrid, '+c.raw.y.toFixed(0)+' /Mgrid';}}}},
      scales:{x:{grid:{color:grid},ticks:{color:tick},title:{display:true,text:'map area (megagrid)',color:tick},min:0},y:{grid:{color:grid},ticks:{color:tick},title:{display:true,text:'density (/Mgrid)',color:tick},min:0}}}});
  var leg=document.getElementById('scatterLegend');
  Object.keys(byType).forEach(function(t){var s=document.createElement('span');s.innerHTML='<span class=\""sw\"" style=\""background:'+(typeColor[t]||'#888780')+'\""></span>'+t;leg.appendChild(s);});
  new Chart(document.getElementById('layoutChart'),{type:'bar',
    data:{labels:lLabels,datasets:[{data:lVals,backgroundColor:lLabels.map(function(t){return typeColor[t]||'#888780';}),borderWidth:0,barThickness:26}]},
    options:{indexAxis:'y',responsive:true,maintainAspectRatio:false,plugins:{legend:{display:false},tooltip:{callbacks:{label:function(c){return c.parsed.x+' maps';}}}},
      scales:{x:{grid:{color:grid},ticks:{color:tick,stepSize:1},title:{display:true,text:'number of maps',color:tick}},y:{grid:{display:false},ticks:{color:tick,font:{size:12}}}}}});
";
    }
}
