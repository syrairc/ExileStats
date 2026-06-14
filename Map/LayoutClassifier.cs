using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace ExileStats;

/// <summary>
/// Structural archetype of a map, judged purely from walkable wall/room geometry. Names map 1:1 to the
/// snake_case strings in layouts.json (see <see cref="LayoutTypeNames"/>).
/// </summary>
public enum LayoutType
{
    Unclassified = 0,
    Linear,
    OpenObstacles,
    ConvergingBranches,
    CentralLoop,
    PerimeterLoop,
    GridNetwork,
    Corridors,
    BossArena,
}

public static class LayoutTypeNames
{
    private static readonly Dictionary<LayoutType, string> ToName = new()
    {
        [LayoutType.Unclassified] = "unclassified",
        [LayoutType.Linear] = "linear",
        [LayoutType.OpenObstacles] = "open_obstacles",
        [LayoutType.ConvergingBranches] = "converging_branches",
        [LayoutType.CentralLoop] = "central_loop",
        [LayoutType.PerimeterLoop] = "perimeter_loop",
        [LayoutType.GridNetwork] = "grid_network",
        [LayoutType.Corridors] = "corridors",
        [LayoutType.BossArena] = "boss_arena",
    };

    private static readonly Dictionary<string, LayoutType> FromName =
        BuildReverse();

    private static Dictionary<string, LayoutType> BuildReverse()
    {
        var d = new Dictionary<string, LayoutType>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ToName) d[kv.Value] = kv.Key;
        return d;
    }

    public static string ToSnake(LayoutType t) => ToName.TryGetValue(t, out var s) ? s : "unclassified";

    public static LayoutType FromSnake(string s) =>
        !string.IsNullOrEmpty(s) && FromName.TryGetValue(s, out var t) ? t : LayoutType.Unclassified;
}

/// <summary>Run-side context that helps classify (boss detection); geometry alone works without it.</summary>
public readonly record struct RunContext(int MonstersTotal, IReadOnlyList<string> MapObjectives, string AreaId)
{
    public static RunContext Empty => new(-1, Array.Empty<string>(), null);
}

/// <summary>Geometric features extracted from the rasterized walkable region (exposed for debugging/UI).</summary>
public readonly record struct LayoutFeatures
{
    public int GridW { get; init; }
    public int GridH { get; init; }
    public int WalkableCells { get; init; }

    public double AreaFraction { get; init; }   // walkable / (GridW*GridH)
    public double BBoxFill { get; init; }        // walkable / walkable-bbox area
    public double Elongation { get; init; }      // PCA sqrt(l1/l2), >=1; high => linear
    public double BBoxAspect { get; init; }      // long/short side of walkable bbox

    public double MeanWallDist { get; init; }    // distance-transform stats (grid cells)
    public double MedianWallDist { get; init; }
    public double MaxWallDist { get; init; }
    public double PassageHalfWidth { get; init; } // MedianWallDist rescaled to game grid units (corridor thinness)
    public double OpenFraction { get; init; }    // frac walkable cells with DT > OpenThresh

    public int InteriorHoleCount { get; init; }
    public double LargestHoleFraction { get; init; }      // largest interior hole / walkable-bbox area
    public double InteriorHoleAreaFraction { get; init; } // all interior holes / walkable-bbox area
    public bool CentreWalkable { get; init; }             // central bbox region mostly walkable

    public int SkelEndpoints { get; init; }
    public int SkelJunctions { get; init; }
    public int SkelIndependentLoops { get; init; }        // cycle rank E - V + C
    public int SkelComponents { get; init; }
    public int MaxJunctionDegree { get; init; }
    public double Tortuosity { get; init; }               // skeleton length / bbox diagonal
    public int FunnelScore { get; init; }                 // endpoints feeding one sink (tree)

    public string Debug() =>
        $"area={AreaFraction:0.00} fill={BBoxFill:0.00} elong={Elongation:0.0} asp={BBoxAspect:0.0} " +
        $"open={OpenFraction:0.00} wd={MeanWallDist:0.0} phw={PassageHalfWidth:0.0} holes={InteriorHoleCount} bigHole={LargestHoleFraction:0.00} " +
        $"ctr={(CentreWalkable ? 1 : 0)} loops={SkelIndependentLoops} jct={SkelJunctions} end={SkelEndpoints} funnel={FunnelScore}";
}

/// <summary>
/// Classifies a map's layout archetype from its walkable terrain loops (parsed from the Radar SVG).
/// Pure geometry — no ImGui / ExileCore2 dependency, so it compiles into the plugin and runs standalone.
///
/// Pipeline: parse loops -> even-odd rasterize to a coarse bool grid -> extract features (coverage,
/// elongation, distance-transform openness, interior holes, skeleton graph) -> decision cascade.
/// </summary>
public static class LayoutClassifier
{
    private const int LongSide = 128;     // coarse grid long edge
    private const int OpenThresh = 4;     // DT (cells) above which a cell counts as "open" (room, not corridor)
    private const int MinHoleCells = 3;   // ignore sub-grid obstacle specks

    // ---- SVG terrain-loop parser (mirrors MapSvg.ParsePath; no color, no ImGui) ----

    private static readonly Regex PathRe =
        new(@"<path\b[^>]*\bd=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex CmdRe =
        new(@"([MLZ])\s*(-?\d+)?\s*(-?\d+)?", RegexOptions.Compiled);
    private static readonly Regex SizeRe =
        new(@"<svg[^>]*\bwidth=""(\d+)""[^>]*\bheight=""(\d+)""", RegexOptions.Compiled);

    /// <summary>Parse the terrain &lt;path&gt; into closed loops of integer grid points. Degenerate
    /// loops (bbox &lt; 2 units on a side — the border-frame sliver / thinning artifacts) are dropped.</summary>
    public static List<Vector2[]> ParseTerrainLoops(string svgText)
    {
        var loops = new List<Vector2[]>();
        if (string.IsNullOrEmpty(svgText)) return loops;

        var m = PathRe.Match(svgText);
        if (!m.Success) return loops;

        List<Vector2> cur = null;
        void Flush()
        {
            if (cur is { Count: >= 3 } && !Degenerate(cur)) loops.Add(cur.ToArray());
            cur = null;
        }

        foreach (Match c in CmdRe.Matches(m.Groups[1].Value))
        {
            switch (c.Groups[1].Value)
            {
                case "M":
                    Flush();
                    cur = new List<Vector2>();
                    if (c.Groups[2].Success && c.Groups[3].Success)
                        cur.Add(new Vector2(int.Parse(c.Groups[2].Value), int.Parse(c.Groups[3].Value)));
                    break;
                case "L":
                    if (cur != null && c.Groups[2].Success && c.Groups[3].Success)
                        cur.Add(new Vector2(int.Parse(c.Groups[2].Value), int.Parse(c.Groups[3].Value)));
                    break;
                case "Z":
                    Flush();
                    break;
            }
        }
        Flush();
        return loops;
    }

    /// <summary>viewBox/size from the SVG header, for callers without explicit area dims.</summary>
    public static (int w, int h) ParseSize(string svgText)
    {
        var m = SizeRe.Match(svgText ?? "");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
    }

    private static bool Degenerate(List<Vector2> pts)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        return (maxX - minX) < 2 || (maxY - minY) < 2;
    }

    // ---- entry points ----

    public static (LayoutType Type, double Confidence) ClassifyFromSvg(
        string svgText, int areaWidth, int areaHeight, RunContext ctx = default)
    {
        var loops = ParseTerrainLoops(svgText);
        if (areaWidth <= 0 || areaHeight <= 0)
            (areaWidth, areaHeight) = ParseSize(svgText);
        return Classify(loops, areaWidth, areaHeight, ctx);
    }

    public static (LayoutType Type, double Confidence) Classify(
        IReadOnlyList<Vector2[]> walkableLoops, int areaWidth, int areaHeight, RunContext ctx = default)
    {
        var f = ExtractFeatures(walkableLoops, areaWidth, areaHeight);
        return Decide(f, ctx);
    }

    // ---- feature extraction ----

    public static LayoutFeatures ExtractFeatures(IReadOnlyList<Vector2[]> loops, int areaWidth, int areaHeight)
    {
        if (loops == null || loops.Count == 0 || areaWidth <= 0 || areaHeight <= 0)
            return default;

        int longSide = Math.Max(areaWidth, areaHeight);
        double s = (double)LongSide / longSide;
        int gw = Math.Max(1, (int)Math.Ceiling(areaWidth * s));
        int gh = Math.Max(1, (int)Math.Ceiling(areaHeight * s));

        bool[,] walk = Rasterize(loops, s, gw, gh);

        // walkable count + bbox + PCA accumulators
        int count = 0;
        int minX = gw, minY = gh, maxX = -1, maxY = -1;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
                if (walk[y, x])
                {
                    count++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    sx += x; sy += y; sxx += (double)x * x; syy += (double)y * y; sxy += (double)x * y;
                }

        if (count == 0) return new LayoutFeatures { GridW = gw, GridH = gh };

        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        double bboxArea = (double)bw * bh;

        // PCA elongation
        double mx = sx / count, my = sy / count;
        double cxx = sxx / count - mx * mx, cyy = syy / count - my * my, cxy = sxy / count - mx * my;
        double tr = cxx + cyy, det = cxx * cyy - cxy * cxy;
        double disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
        double l1 = tr / 2 + disc, l2 = tr / 2 - disc;
        double elongation = l2 > 1e-6 ? Math.Sqrt(l1 / l2) : (l1 > 1e-6 ? 999 : 1);
        double bboxAspect = (double)Math.Max(bw, bh) / Math.Max(1, Math.Min(bw, bh));

        // distance transform (chamfer 3-4) over walkable cells, walls = 0 distance neighbours
        var dt = DistanceTransform(walk, gw, gh);
        double sumD = 0, maxD = 0; int open = 0;
        var dvals = new List<double>(count);
        for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
                if (walk[y, x])
                {
                    double d = dt[y, x] / 3.0; // chamfer 3 ~= 1 cell
                    sumD += d; if (d > maxD) maxD = d; if (d > OpenThresh) open++;
                    dvals.Add(d);
                }
        dvals.Sort();
        double meanD = sumD / count;
        double medD = dvals[dvals.Count / 2];
        double openFrac = (double)open / count;

        // interior holes (CCL on non-walkable cells not touching the border)
        var (holeCount, largestHole, totalHole) = InteriorHoles(walk, gw, gh);

        // centre-walkable test (central 30% of the walkable bbox)
        bool centreWalkable = CentreWalkable(walk, minX, minY, bw, bh);

        // skeleton graph
        var skel = ZhangSuen(walk, gw, gh);
        var (endpoints, junctions, loopsCount, components, maxDeg, skelLen) = SkeletonGraph(skel, gw, gh);
        double diag = Math.Sqrt((double)bw * bw + (double)bh * bh);
        double tort = diag > 0 ? skelLen / diag : 0;
        int funnel = (loopsCount == 0 && maxDeg >= 3) ? endpoints : 0;

        return new LayoutFeatures
        {
            GridW = gw,
            GridH = gh,
            WalkableCells = count,
            AreaFraction = (double)count / (gw * gh),
            BBoxFill = count / bboxArea,
            Elongation = elongation,
            BBoxAspect = bboxAspect,
            MeanWallDist = meanD,
            MedianWallDist = medD,
            MaxWallDist = maxD,
            // medD is in coarse cells; 1/s = longSide/LongSide game units per cell, so this is the
            // passage half-width in game grid units (scale-invariant — thin corridors read the same on any map).
            PassageHalfWidth = medD / s,
            OpenFraction = openFrac,
            InteriorHoleCount = holeCount,
            LargestHoleFraction = largestHole / bboxArea,
            InteriorHoleAreaFraction = totalHole / bboxArea,
            CentreWalkable = centreWalkable,
            SkelEndpoints = endpoints,
            SkelJunctions = junctions,
            SkelIndependentLoops = loopsCount,
            SkelComponents = components,
            MaxJunctionDegree = maxDeg,
            Tortuosity = tort,
            FunnelScore = funnel,
        };
    }

    // ---- rasterizer: even-odd scanline fill of all loops at once ----

    private static bool[,] Rasterize(IReadOnlyList<Vector2[]> loops, double s, int gw, int gh)
    {
        var walk = new bool[gh, gw];
        var xs = new List<double>(32);
        for (int y = 0; y < gh; y++)
        {
            double yc = y + 0.5;
            xs.Clear();
            foreach (var loop in loops)
            {
                int n = loop.Length;
                for (int i = 0; i < n; i++)
                {
                    var a = loop[i]; var b = loop[(i + 1) % n];
                    double ay = a.Y * s, by = b.Y * s;
                    // half-open straddle avoids double-counting shared vertices
                    if ((ay <= yc) == (by <= yc)) continue;
                    double t = (yc - ay) / (by - ay);
                    xs.Add(a.X * s + t * (b.X * s - a.X * s));
                }
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                int x0 = (int)Math.Ceiling(xs[i] - 0.5);
                int x1 = (int)Math.Floor(xs[i + 1] - 0.5);
                if (x0 < 0) x0 = 0;
                if (x1 > gw - 1) x1 = gw - 1;
                for (int x = x0; x <= x1; x++) walk[y, x] = true;
            }
        }
        return walk;
    }

    // ---- chamfer 3-4 distance transform (walkable cell -> distance to nearest wall/edge) ----

    private static int[,] DistanceTransform(bool[,] walk, int gw, int gh)
    {
        const int INF = 1 << 28;
        var d = new int[gh, gw];
        for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
                d[y, x] = walk[y, x] ? INF : 0;

        for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
            {
                if (d[y, x] == 0) continue;
                int v = d[y, x];
                if (x > 0) v = Math.Min(v, d[y, x - 1] + 3);
                if (y > 0) v = Math.Min(v, d[y - 1, x] + 3);
                if (x > 0 && y > 0) v = Math.Min(v, d[y - 1, x - 1] + 4);
                if (x < gw - 1 && y > 0) v = Math.Min(v, d[y - 1, x + 1] + 4);
                d[y, x] = v;
            }
        for (int y = gh - 1; y >= 0; y--)
            for (int x = gw - 1; x >= 0; x--)
            {
                if (d[y, x] == 0) continue;
                int v = d[y, x];
                if (x < gw - 1) v = Math.Min(v, d[y, x + 1] + 3);
                if (y < gh - 1) v = Math.Min(v, d[y + 1, x] + 3);
                if (x < gw - 1 && y < gh - 1) v = Math.Min(v, d[y + 1, x + 1] + 4);
                if (x > 0 && y < gh - 1) v = Math.Min(v, d[y + 1, x - 1] + 4);
                d[y, x] = v;
            }
        return d;
    }

    // ---- interior obstacle holes: non-walkable components not touching the grid border ----

    private static (int count, double largest, double total) InteriorHoles(bool[,] walk, int gw, int gh)
    {
        var seen = new bool[gh, gw];
        var stack = new Stack<(int, int)>();
        int count = 0; double largest = 0, total = 0;

        for (int y0 = 0; y0 < gh; y0++)
            for (int x0 = 0; x0 < gw; x0++)
            {
                if (walk[y0, x0] || seen[y0, x0]) continue;
                // flood this non-walkable component
                stack.Clear(); stack.Push((x0, y0)); seen[y0, x0] = true;
                int size = 0; bool touchesBorder = false;
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop();
                    size++;
                    if (x == 0 || y == 0 || x == gw - 1 || y == gh - 1) touchesBorder = true;
                    if (x > 0 && !walk[y, x - 1] && !seen[y, x - 1]) { seen[y, x - 1] = true; stack.Push((x - 1, y)); }
                    if (x < gw - 1 && !walk[y, x + 1] && !seen[y, x + 1]) { seen[y, x + 1] = true; stack.Push((x + 1, y)); }
                    if (y > 0 && !walk[y - 1, x] && !seen[y - 1, x]) { seen[y - 1, x] = true; stack.Push((x, y - 1)); }
                    if (y < gh - 1 && !walk[y + 1, x] && !seen[y + 1, x]) { seen[y + 1, x] = true; stack.Push((x, y + 1)); }
                }
                if (touchesBorder || size < MinHoleCells) continue;
                count++; total += size; if (size > largest) largest = size;
            }
        return (count, largest, total);
    }

    private static bool CentreWalkable(bool[,] walk, int minX, int minY, int bw, int bh)
    {
        int cx0 = minX + (int)(bw * 0.35), cx1 = minX + (int)(bw * 0.65);
        int cy0 = minY + (int)(bh * 0.35), cy1 = minY + (int)(bh * 0.65);
        int total = 0, on = 0;
        for (int y = cy0; y <= cy1; y++)
            for (int x = cx0; x <= cx1; x++)
            {
                total++;
                if (walk[y, x]) on++;
            }
        return total > 0 && (double)on / total > 0.5;
    }

    // ---- Zhang-Suen thinning ----

    private static bool[,] ZhangSuen(bool[,] src, int gw, int gh)
    {
        var img = (bool[,])src.Clone();
        var toClear = new List<(int, int)>();
        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 200)
        {
            changed = false;
            for (int step = 0; step < 2; step++)
            {
                toClear.Clear();
                for (int y = 1; y < gh - 1; y++)
                    for (int x = 1; x < gw - 1; x++)
                    {
                        if (!img[y, x]) continue;
                        bool p2 = img[y - 1, x], p3 = img[y - 1, x + 1], p4 = img[y, x + 1],
                             p5 = img[y + 1, x + 1], p6 = img[y + 1, x], p7 = img[y + 1, x - 1],
                             p8 = img[y, x - 1], p9 = img[y - 1, x - 1];
                        int b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) +
                                (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                        if (b < 2 || b > 6) continue;
                        int a = Trans(p2, p3) + Trans(p3, p4) + Trans(p4, p5) + Trans(p5, p6) +
                                Trans(p6, p7) + Trans(p7, p8) + Trans(p8, p9) + Trans(p9, p2);
                        if (a != 1) continue;
                        if (step == 0)
                        {
                            if (p2 && p4 && p6) continue;
                            if (p4 && p6 && p8) continue;
                        }
                        else
                        {
                            if (p2 && p4 && p8) continue;
                            if (p2 && p6 && p8) continue;
                        }
                        toClear.Add((x, y));
                    }
                if (toClear.Count > 0)
                {
                    changed = true;
                    foreach (var (x, y) in toClear) img[y, x] = false;
                }
            }
        }
        return img;
    }

    private static int Trans(bool a, bool b) => (!a && b) ? 1 : 0;

    // ---- skeleton graph: endpoints, junctions, cycle rank, after spur pruning ----

    private static (int endpoints, int junctions, int loops, int components, int maxDeg, double length)
        SkeletonGraph(bool[,] skel, int gw, int gh)
    {
        // prune short spurs (< 3 px) to reduce thinning noise
        PruneSpurs(skel, gw, gh, 3);

        int Deg(int x, int y)
        {
            int d = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < gw && ny < gh && skel[ny, nx]) d++;
                }
            return d;
        }

        int endpoints = 0, junctions = 0, maxDeg = 0, length = 0;
        int V = 0, edgeEnds = 0;
        for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
            {
                if (!skel[y, x]) continue;
                length++;
                int deg = Deg(x, y);
                if (deg != 2) { V++; edgeEnds += deg; }
                if (deg == 1) endpoints++;
                if (deg >= 3) { junctions++; if (deg > maxDeg) maxDeg = deg; }
            }

        // count skeleton pixel components (8-connected)
        int components = SkelComponents(skel, gw, gh, out _);

        // edges between nodes ~= edgeEnds/2 (deg-2 chains collapse to edges); cycle rank = E - V + C
        int E = edgeEnds / 2;
        int loops = Math.Max(0, E - V + components);
        return (endpoints, junctions, loops, components, maxDeg, length);
    }

    private static void PruneSpurs(bool[,] skel, int gw, int gh, int maxLen)
    {
        for (int iter = 0; iter < maxLen; iter++)
        {
            var rm = new List<(int, int)>();
            for (int y = 1; y < gh - 1; y++)
                for (int x = 1; x < gw - 1; x++)
                {
                    if (!skel[y, x]) continue;
                    int d = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            if (skel[y + dy, x + dx]) d++;
                        }
                    if (d == 1) rm.Add((x, y));
                }
            if (rm.Count == 0) break;
            foreach (var (x, y) in rm) skel[y, x] = false;
        }
    }

    private static int SkelComponents(bool[,] skel, int gw, int gh, out int pixels)
    {
        var seen = new bool[gh, gw];
        var stack = new Stack<(int, int)>();
        int comp = 0; pixels = 0;
        for (int y0 = 0; y0 < gh; y0++)
            for (int x0 = 0; x0 < gw; x0++)
            {
                if (!skel[y0, x0] || seen[y0, x0]) continue;
                comp++; stack.Clear(); stack.Push((x0, y0)); seen[y0, x0] = true;
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop(); pixels++;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < gw && ny < gh && skel[ny, nx] && !seen[ny, nx])
                            { seen[ny, nx] = true; stack.Push((nx, ny)); }
                        }
                }
            }
        return comp;
    }

    // ---- decision cascade ----

    // NOTE on the skeleton graph: at a 128px grid these maps' corridors are ~1 cell wide, so Zhang-Suen
    // thinning fragments into hundreds of spurious loops/junctions — those counts are noise and are NOT
    // gated on here (kept in the feature struct for diagnostics only). Decisions use robust region-level
    // features: largest enclosed hole, walkable-centre test, PCA elongation, coverage, and run context.
    private static (LayoutType, double) Decide(LayoutFeatures f, RunContext ctx)
    {
        if (f.WalkableCells < 25)
            return (LayoutType.Unclassified, 0.5);

        double Clamp(double c) => Math.Min(0.95, Math.Max(0.35, c));

        // 1. Boss arena — dedicated boss fight. AreaId is decisive; geometry/monster count as backup.
        bool isBossId = ctx.AreaId != null &&
            (ctx.AreaId.StartsWith("MapUberBoss_", StringComparison.Ordinal) ||
             ctx.AreaId.Contains("UniqueReactor", StringComparison.Ordinal));
        if (isBossId)
            return (LayoutType.BossArena, 0.95);
        if (ctx.MonstersTotal is >= 0 and <= 2 && f.AreaFraction < 0.05)
            return (LayoutType.BossArena, Clamp(0.6 + (ctx.MonstersTotal <= 1 ? 0.15 : 0)));

        // 2. Linear — a long thin snake: very high PCA elongation over a tiny, sparse footprint.
        if (f.Elongation > 4.0 && f.AreaFraction < 0.10 && f.BBoxFill < 0.15)
            return (LayoutType.Linear, Clamp(0.55 + 0.05 * (f.Elongation - 4.0)));

        // 3. Perimeter loop — walkable ring around a large NON-walkable centre (centre not walkable).
        if (f.LargestHoleFraction > 0.20 && !f.CentreWalkable)
            return (LayoutType.PerimeterLoop, Clamp(0.55 + 1.6 * (f.LargestHoleFraction - 0.20)));

        // 4. Central loop — ring of rooms enclosing a walkable central room: a moderate central hole
        //    with a walkable centre, compact (low aspect) and enclosed (low openness).
        if (f.LargestHoleFraction is >= 0.08 and < 0.20 && f.CentreWalkable &&
            f.BBoxAspect < 1.6 && f.OpenFraction < 0.12)
            return (LayoutType.CentralLoop, Clamp(0.55 + 1.5 * (f.LargestHoleFraction - 0.08)));

        // 5. Open obstacles — a broad walkable basin (genuinely open cells, not corridors) peppered with
        //    scattered small obstacles: a real openness signal over a low-density footprint with many small
        //    holes and no dominant hole. NOTE: the high-coverage "obstacle field" variant of this archetype
        //    is geometrically indistinguishable from grid_network at this resolution (e.g. Pit≈Bastille,
        //    Savanna≈Epitaph), so it is intentionally NOT forced here — those land in grid_network and are
        //    reported as known ambiguities rather than guessed.
        if (f.OpenFraction > 0.15 && f.BBoxFill < 0.40 && f.InteriorHoleCount >= 12 &&
            f.AreaFraction > 0.16 && f.LargestHoleFraction < 0.08 && f.BBoxAspect < 1.8)
            return (LayoutType.OpenObstacles, Clamp(0.5 + 0.6 * (f.OpenFraction - 0.15)));

        // 6. Corridors — a sparse skeleton of thin passages linking discrete rooms across large empty gaps,
        //    the opposite of grid_network's dense web of thick adjoining rooms. Three robust region-level
        //    signals (no noisy skeleton counts):
        //      - low BBoxFill: walkable cells fill little of their own bbox (the "large empty gaps").
        //      - thin passages via MeanWallDist (cells): corridors have no thick rooms, so the mean
        //        distance-to-wall stays ~1 cell; any room-grid pulls the mean up to >=1.4. (The MEDIAN
        //        half-width — PassageHalfWidth — saturates at ~1 cell at this 128px grid and only tracks
        //        map size, so the mean is the usable thinness proxy here.)
        //      - low Elongation: a compact footprint of linked rooms, NOT a stringy/winding web — this is
        //        what rejects the low-fill organic grids (Flotsam, elong 3.3) and crescents (WaywardIsle).
        //    Thresholds are fixture-tuned starting points (see Analysis/layouts.json), not hard constants.
        if (f.BBoxFill <= 0.21 && f.MeanWallDist <= 1.25 && f.Elongation <= 2.5)
            return (LayoutType.Corridors, Clamp(0.55 + 1.2 * (0.21 - f.BBoxFill)));

        // 7. Grid network — the default/residual bucket for a structured, multi-route interior (the most
        //    common type). Kept at modest confidence on purpose: it absorbs the grid/open ambiguity above,
        //    so a wrong guess here never outranks a structurally-decided type.
        double gridConf = 0.5 + (f.InteriorHoleCount >= 10 ? 0.05 : 0);
        return (LayoutType.GridNetwork, Clamp(gridConf));
    }
}
