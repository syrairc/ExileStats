using System;
using System.Collections.Generic;

namespace ExileStats;

/// <summary>
/// Buffers player-position samples for an accurate map path. Sampling is <b>distance-stepped</b>: a point is
/// kept each time the player has moved at least <c>stepUnits</c> grid units since the last kept point, so the
/// path is uniformly dense in space regardless of player speed. (A pure time gate under-sampled fast-traversed
/// corridors — points were spaced <c>speed × interval</c> apart, dense in slow rooms but sparse in corridors,
/// drawing a jagged line that skipped bends.) <c>intervalMs</c> is only a small minimum-interval floor between
/// kept points (anti-spam); the distance step, re-evaluated cheaply each Tick, sets the spacing.
/// <see cref="ExileStats"/> drains the buffer to <see cref="PathLog"/> in batches.
///
/// Outlier rejection: the game occasionally reports a bogus position the instant an area loads (typically
/// near the origin, or outside the area bounds) — those produce a path line shooting off to the map corner.
/// <see cref="Sample"/> rejects non-positive coordinates and, when area dimensions are known, anything
/// outside them.
/// </summary>
public class PathTracker
{
    private readonly List<PathPoint> _buffer = new();
    private string _areaId;
    private long _instanceHash;
    private int _zoneSwitchId;
    private DateTime _nextSampleAt;
    private float _lastX, _lastY;
    private bool _hasLast;

    public string AreaId => _areaId;
    public long InstanceHash => _instanceHash;
    public int BufferedCount => _buffer.Count;

    /// <summary>Reset for a freshly-entered area; remembers where to flush this visit's points.</summary>
    public void SetArea(string areaId, long instanceHash, int zoneSwitchId)
    {
        _buffer.Clear();
        _areaId = areaId;
        _instanceHash = instanceHash;
        _zoneSwitchId = zoneSwitchId;
        _nextSampleAt = DateTime.MinValue;
        _hasLast = false;
    }

    /// <summary>Record the player position if it has moved a full step since the last kept point.
    /// <paramref name="intervalMs"/> is a minimum-interval floor between kept points (anti-spam);
    /// <paramref name="stepUnits"/> is the grid-unit distance step that sets the spacing (floored at 1).
    /// <paramref name="areaW"/>/<paramref name="areaH"/> are the area grid dimensions for bounds-checking
    /// (pass 0 if unknown — then only the origin check applies).</summary>
    public void Sample(float x, float y, double elapsed, int intervalMs, float stepUnits, float areaW, float areaH)
    {
        var now = DateTime.Now;
        if (now < _nextSampleAt)
            return;

        // Outlier: bogus origin/negative read, or outside the area bounds.
        if (x <= 0 || y <= 0)
            return;
        if (areaW > 0 && areaH > 0 && (x >= areaW || y >= areaH))
            return;

        // Distance step: keep only once the player has moved a full step (so spacing is uniform in space,
        // not in time — corridors run fast but stay densely sampled). Re-evaluated each Tick until reached.
        if (_hasLast)
        {
            var step = stepUnits < 1f ? 1f : stepUnits;
            var dx = x - _lastX;
            var dy = y - _lastY;
            if (dx * dx + dy * dy < step * step)
                return;
        }

        // Floor advances only on a kept point, so slow accumulation toward a step isn't throttled out.
        _nextSampleAt = now.AddMilliseconds(intervalMs);
        _lastX = x;
        _lastY = y;
        _hasLast = true;
        _buffer.Add(new PathPoint { T = elapsed, X = x, Y = y, Z = _zoneSwitchId });
    }

    /// <summary>Returns and clears the buffered points (null if empty).</summary>
    public List<PathPoint> Drain()
    {
        if (_buffer.Count == 0)
            return null;
        var pts = new List<PathPoint>(_buffer);
        _buffer.Clear();
        return pts;
    }
}
