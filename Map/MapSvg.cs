using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace ExileStats;

/// <summary>
/// Parsed Radar map SVG (from <c>Radar.GetMapSvg</c>). The SVG is a tiny, regular grammar — one even-odd
/// terrain <c>&lt;path&gt;</c> of <c>M/L/Z</c> integer subpaths, route <c>&lt;polyline&gt;</c>s, and target
/// <c>&lt;circle&gt;</c>s — all in grid units (viewBox = AreaDimensions). We keep the geometry in grid
/// coords and stroke it via the ImGui draw list, so it stays crisp at any zoom (no rasterization).
/// </summary>
public class MapSvg
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    public uint TerrainColor { get; private set; }
    public List<Vector2[]> TerrainLoops { get; } = new();        // closed loops (stroked)
    public List<(Vector2[] Pts, uint Color)> Routes { get; } = new();
    public List<(Vector2 Center, float Radius, uint Color)> Circles { get; } = new();

    private static readonly Regex SizeRe =
        new(@"<svg[^>]*\bwidth=""(\d+)""[^>]*\bheight=""(\d+)""", RegexOptions.Compiled);
    private static readonly Regex PathRe =
        new(@"<path\b[^>]*\bfill=""#([0-9a-fA-F]{6})""[^>]*?(?:fill-opacity=""([0-9.]+)"")?[^>]*\bd=""([^""]*)""",
            RegexOptions.Compiled);
    private static readonly Regex PolylineRe =
        new(@"<polyline\b[^>]*\bstroke=""#([0-9a-fA-F]{6})""[^>]*\bpoints=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex CircleRe =
        new(@"<circle\b[^>]*\bcx=""([0-9.\-]+)""[^>]*\bcy=""([0-9.\-]+)""[^>]*\br=""([0-9.\-]+)""[^>]*\bfill=""#([0-9a-fA-F]{6})""",
            RegexOptions.Compiled);
    // One M/L command + its two integer coords.
    private static readonly Regex CmdRe = new(@"([MLZ])\s*(-?\d+)?\s*(-?\d+)?", RegexOptions.Compiled);

    public static MapSvg Load(string filePath)
    {
        try { return Parse(File.ReadAllText(filePath)); }
        catch { return null; }
    }

    public static MapSvg Parse(string svg)
    {
        if (string.IsNullOrEmpty(svg))
            return null;

        var result = new MapSvg();

        var size = SizeRe.Match(svg);
        if (size.Success)
        {
            result.Width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
            result.Height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        var pathM = PathRe.Match(svg);
        if (pathM.Success)
        {
            var opacity = pathM.Groups[2].Success
                ? float.Parse(pathM.Groups[2].Value, CultureInfo.InvariantCulture)
                : 1f;
            // Stroke the contour: keep it visible even when the source fill is faint.
            result.TerrainColor = HexColor(pathM.Groups[1].Value, Math.Max(opacity, 0.55f));
            ParsePath(pathM.Groups[3].Value, result.TerrainLoops);
        }

        foreach (Match m in PolylineRe.Matches(svg))
        {
            var col = HexColor(m.Groups[1].Value, 1f);
            var pts = ParsePoints(m.Groups[2].Value);
            if (pts.Length >= 2)
                result.Routes.Add((pts, col));
        }

        foreach (Match m in CircleRe.Matches(svg))
        {
            var cx = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var cy = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var r = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            result.Circles.Add((new Vector2(cx, cy), r, HexColor(m.Groups[4].Value, 1f)));
        }

        return result;
    }

    // Split a "M x y L x y ... Z M ..." path into closed loops of grid points. Degenerate loops (bbox < 2
    // units on a side) are dropped: the Radar terrain path opens with a full-height 1px-wide border-frame
    // rectangle at x≈viewBox width, which otherwise strokes as a stray vertical line down the map's right
    // edge. (LayoutClassifier.ParseTerrainLoops drops the same sliver for coverage.)
    private static void ParsePath(string d, List<Vector2[]> loops)
    {
        List<Vector2> cur = null;
        void Flush()
        {
            if (cur is { Count: >= 3 } && !Degenerate(cur)) loops.Add(cur.ToArray());
            cur = null;
        }
        foreach (Match m in CmdRe.Matches(d))
        {
            var cmd = m.Groups[1].Value;
            switch (cmd)
            {
                case "M":
                    Flush();
                    cur = new List<Vector2>();
                    if (m.Groups[2].Success && m.Groups[3].Success)
                        cur.Add(new Vector2(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
                    break;
                case "L":
                    if (cur != null && m.Groups[2].Success && m.Groups[3].Success)
                        cur.Add(new Vector2(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
                    break;
                case "Z":
                    Flush();
                    break;
            }
        }
        Flush();
    }

    // A loop whose bounding box is <2 units on either side — the Radar border-frame sliver, or a
    // thinned-to-nothing artifact. Not a real terrain region; dropped so it isn't stroked.
    private static bool Degenerate(List<Vector2> pts)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return (maxX - minX) < 2 || (maxY - minY) < 2;
    }

    private static Vector2[] ParsePoints(string points)
    {
        var tokens = points.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<Vector2>(tokens.Length);
        foreach (var t in tokens)
        {
            var comma = t.IndexOf(',');
            if (comma <= 0) continue;
            if (float.TryParse(t[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(t[(comma + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                list.Add(new Vector2(x, y));
        }
        return list.ToArray();
    }

    private static uint HexColor(string rrggbb, float alpha)
    {
        var r = Convert.ToInt32(rrggbb.Substring(0, 2), 16) / 255f;
        var g = Convert.ToInt32(rrggbb.Substring(2, 2), 16) / 255f;
        var b = Convert.ToInt32(rrggbb.Substring(4, 2), 16) / 255f;
        return ImGui.GetColorU32(new Vector4(r, g, b, alpha));
    }
}
