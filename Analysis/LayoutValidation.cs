using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExileStats;

/// <summary>
/// Runs <see cref="LayoutClassifier"/> over every captured map.svg under maps/ and compares the
/// prediction to the hand-labelled layouts.json (the ground truth the plugin reads). Drives the in-game
/// "Layout classifier validation" settings panel. Dependency-free (System.Text.Json) — no ImGui here so
/// the same logic can be exercised headless; the panel only renders the returned rows.
/// </summary>
public static class LayoutValidation
{
    public readonly record struct Row(
        string MapId, string Predicted, double PredConf, string Label, string LabelConf, bool Agree,
        string Features);

    private static readonly Regex TrailingHash = new(@"_\d+$", RegexOptions.Compiled);

    public static List<Row> Run(string pluginDir)
    {
        var rows = new List<Row>();
        var mapsRoot = Path.Combine(pluginDir, "maps");
        if (!Directory.Exists(mapsRoot)) return rows;

        var labels = LoadLabels(Path.Combine(pluginDir, "layouts.json"));

        foreach (var dir in Directory.EnumerateDirectories(mapsRoot))
        {
            var svgPath = Path.Combine(dir, "map.svg");
            if (!File.Exists(svgPath)) continue;

            var mapId = TrailingHash.Replace(Path.GetFileName(dir), "");

            int w = 0, h = 0, monsters = -1;
            string areaId = mapId;
            var objectives = new List<string>();
            var runPath = Path.Combine(dir, "run.json");
            if (File.Exists(runPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(runPath));
                    var e = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                        ? doc.RootElement[0] : doc.RootElement;
                    w = GetInt(e, "AreaWidth"); h = GetInt(e, "AreaHeight");
                    monsters = GetInt(e, "MonstersTotal", -1);
                    areaId = GetStr(e, "AreaId") ?? mapId;
                    if (e.TryGetProperty("MapObjectives", out var mo) && mo.ValueKind == JsonValueKind.Array)
                        objectives = mo.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString()).ToList();
                }
                catch { /* tolerate a mid-write run.json */ }
            }

            string svg;
            try { svg = File.ReadAllText(svgPath); } catch { continue; }

            var loops = LayoutClassifier.ParseTerrainLoops(svg);
            if (w <= 0 || h <= 0) (w, h) = LayoutClassifier.ParseSize(svg);
            var feats = LayoutClassifier.ExtractFeatures(loops, w, h);
            var (type, conf) = LayoutClassifier.Classify(loops, w, h, new RunContext(monsters, objectives, areaId));
            var pred = LayoutTypeNames.ToSnake(type);

            labels.TryGetValue(mapId, out var lbl);
            bool agree = lbl.type != null && lbl.type == pred;
            rows.Add(new Row(mapId, pred, conf, lbl.type ?? "(none)", lbl.conf ?? "", agree, feats.Debug()));
        }

        return rows.OrderBy(r => r.MapId, StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, (string type, string conf)> LoadLabels(string path)
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (!File.Exists(path)) return d;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("maps", out var maps))
                foreach (var p in maps.EnumerateObject())
                {
                    string t = p.Value.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                    string c = p.Value.TryGetProperty("confidence", out var cf) ? cf.GetString() : null;
                    d[p.Name] = (t, c);
                }
        }
        catch { /* missing/mid-write layouts.json -> no labels */ }
        return d;
    }

    private static int GetInt(JsonElement e, string k, int def = 0) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : def;

    private static string GetStr(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
