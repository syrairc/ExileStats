using System.Collections.Generic;
using System.Numerics;

namespace ExileStats;

// point-in-terrain tests shared by the window, the report and run-end coverage
public static class MapGeometry
{
    // even-odd point-in-polygon across all terrain loops
    public static bool InsideLoops(IReadOnlyList<Vector2[]> loops, float px, float py)
    {
        var inside = false;
        foreach (var loop in loops)
        {
            var n = loop.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = loop[i];
                var b = loop[j];
                if ((a.Y > py) != (b.Y > py) && px < (b.X - a.X) * (py - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
        }
        return inside;
    }

    // drop off-terrain points (area-unload reads). keeps the raw path when > 25% would go: bad parse
    public static List<Vector2> WalkablePath(List<Vector2> pts, IReadOnlyList<Vector2[]> loops)
    {
        if (loops == null || loops.Count == 0 || pts.Count == 0) return pts;
        var kept = new List<Vector2>(pts.Count);
        foreach (var p in pts) if (InsideLoops(loops, p.X, p.Y)) kept.Add(p);
        if (kept.Count == pts.Count || kept.Count < pts.Count * 0.75) return pts;
        return kept;
    }
}
