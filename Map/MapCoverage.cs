using System;
using System.Collections.Generic;
using Vector2 = System.Numerics.Vector2;

namespace ExileStats;

/// <summary>
/// Run-end batch computation of map exploration %. Given the walkable terrain loops (even-odd fill, in grid
/// units — from <see cref="LayoutClassifier"/>), the travelled path, and the network
/// bubble radius R, computes the fraction of walkable area lying within R of some path point. Pure (no
/// game/SVG deps) so it is unit-tested directly.
/// </summary>
public static class MapCoverage
{
    public const int CellSize = 5; // grid units per coverage cell

    /// <summary>Default map-**reveal** radius (grid units): how far terrain is visually uncovered around the
    /// player. Drives exploration % + the map-view tint. Far tighter than the stream bubble. Baked as the
    /// <c>MapRevealRadius</c> setting default.</summary>
    public const float DefaultRevealRadius = 100f;

    /// <summary>Default **monster-reveal** radius (grid units): the wider radius at which monsters become
    /// visible around the player (≈2× terrain reveal). The density denominator — monsters ÷ the walkable
    /// area within this radius of the path. Baked as the <c>MonsterRevealRadius</c> setting default.</summary>
    public const float DefaultMonsterRevealRadius = 230f;

    public readonly record struct Result(double Percent, int TotalWalkable, int ExploredWalkable);

    /// <summary>The explored-cell raster behind a coverage result: the per-cell explored flags plus grid
    /// shape, so the map view can tint exactly the cells the % counts. <see cref="Cell"/> = grid units per
    /// cell; cell (gx,gy) spans grid [gx*Cell,(gx+1)*Cell) × [gy*Cell,(gy+1)*Cell).</summary>
    public readonly record struct Mask(int Gw, int Gh, int Cell, bool[] Explored, int Total, int ExploredCount);

    /// <summary>Coverage result, or null when not computable (bad dims, no loops, empty path, R&lt;=0).</summary>
    public static Result? Compute(int width, int height, IReadOnlyList<Vector2[]> walkableLoops,
        IReadOnlyList<Vector2> path, float r)
    {
        if (ComputeMask(width, height, walkableLoops, path, r) is not { } m) return null;
        return new Result(100.0 * m.ExploredCount / m.Total, m.Total, m.ExploredCount);
    }

    /// <summary>The explored raster + counts, or null when not computable (bad dims, no loops, empty path,
    /// R&lt;=0, or no walkable cells). Single source for both the stored % and the map-view tint.</summary>
    public static Mask? ComputeMask(int width, int height, IReadOnlyList<Vector2[]> walkableLoops,
        IReadOnlyList<Vector2> path, float r)
    {
        if (width <= 0 || height <= 0 || walkableLoops == null || walkableLoops.Count == 0
            || path == null || path.Count == 0 || r <= 0f)
            return null;

        var gw = (width + CellSize - 1) / CellSize;
        var gh = (height + CellSize - 1) / CellSize;
        if (gw <= 0 || gh <= 0) return null;

        var walkable = RasterizeWalkable(gw, gh, walkableLoops);
        var total = 0;
        foreach (var w in walkable) if (w) total++;
        if (total == 0) return null;

        var explored = new bool[gw * gh];
        var rCells = (int)Math.Ceiling(r / CellSize);
        var r2 = r * r;
        foreach (var p in path)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) continue; // skip NaN/Infinity (corrupt path rows)
            var cx = (int)(p.X / CellSize);
            var cy = (int)(p.Y / CellSize);
            for (var oy = -rCells; oy <= rCells; oy++)
            {
                var gy = cy + oy;
                if (gy < 0 || gy >= gh) continue;
                for (var ox = -rCells; ox <= rCells; ox++)
                {
                    var gx = cx + ox;
                    if (gx < 0 || gx >= gw) continue;
                    var idx = gy * gw + gx;
                    if (explored[idx] || !walkable[idx]) continue;
                    var ccx = (gx + 0.5f) * CellSize; // cell centre, grid units
                    var ccy = (gy + 0.5f) * CellSize;
                    var dx = ccx - p.X;
                    var dy = ccy - p.Y;
                    if (dx * dx + dy * dy <= r2) explored[idx] = true;
                }
            }
        }

        var exploredCount = 0;
        foreach (var e in explored) if (e) exploredCount++;

        return new Mask(gw, gh, CellSize, explored, total, exploredCount);
    }

    // Even-odd scanline fill of every loop edge into a gw x gh walkable mask (row-major: gy*gw + gx).
    private static bool[] RasterizeWalkable(int gw, int gh, IReadOnlyList<Vector2[]> loops)
    {
        var mask = new bool[gw * gh];
        var xs = new List<float>();
        for (var gy = 0; gy < gh; gy++)
        {
            var sy = (gy + 0.5f) * CellSize; // scan at cell-row centre, grid units
            xs.Clear();
            foreach (var loop in loops)
            {
                if (loop == null || loop.Length < 2) continue;
                for (var i = 0; i < loop.Length; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Length];
                    var y0 = a.Y;
                    var y1 = b.Y;
                    if (y0 == y1) continue;
                    // half-open span so shared vertices aren't double-counted
                    if ((sy >= y0 && sy < y1) || (sy >= y1 && sy < y0))
                    {
                        var t = (sy - y0) / (y1 - y0);
                        xs.Add(a.X + t * (b.X - a.X));
                    }
                }
            }
            if (xs.Count < 2 || xs.Count % 2 != 0) continue; // need crossing pairs for even-odd fill
            xs.Sort();
            for (var k = 0; k + 1 < xs.Count; k += 2)
            {
                var cStart = (int)Math.Floor(xs[k] / CellSize);
                var cEnd = (int)Math.Ceiling(xs[k + 1] / CellSize) - 1;
                if (cStart < 0) cStart = 0;
                if (cEnd >= gw) cEnd = gw - 1;
                for (var gx = cStart; gx <= cEnd; gx++)
                    mask[gy * gw + gx] = true;
            }
        }
        return mask;
    }
}
