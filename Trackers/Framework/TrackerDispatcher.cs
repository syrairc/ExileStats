using System;
using System.Collections.Generic;
using ExileCore2;

namespace ExileStats;

/// <summary>
/// Drives the registered per-tick trackers. Builds one shared <see cref="EntityBuckets"/> per tick, then for
/// each tracker checks the gate (tracked-area + enabled + per-tracker interval), times it via the
/// <see cref="Profiler"/>, and runs it inside its own try/catch so one failing tracker can't stop the rest.
/// Replaces the old flat wall of <c>if (Settings.X &amp;&amp; IsTracked) { try { … } }</c> blocks in Tick.
/// </summary>
public sealed class TrackerDispatcher
{
    private readonly ITracker[] _trackers;
    private readonly Dictionary<string, long> _nextDueMs = new();   // tracker name -> earliest next run (TickCount64)

    public TrackerProfiler Profiler { get; } = new();

    public TrackerDispatcher(params ITracker[] trackers) => _trackers = trackers;

    /// <summary>Arm the per-tracker interval gates on area entry: an interval-throttled tracker first runs one
    /// interval after entry (skips the load-instant), matching the old snapshot timer behavior.</summary>
    public void OnAreaChange(ExileStatsSettings s)
    {
        var now = Environment.TickCount64;
        foreach (var t in _trackers)
        {
            var iv = t.IntervalMs(s);
            if (iv > 0)
                _nextDueMs[t.Name] = now + iv;
            else
                _nextDueMs.Remove(t.Name);
        }
    }

    public void Tick(ExileStats plugin, GameController gc, ExileStatsSettings settings,
        MapRunRecord area, double elapsedSeconds, bool areaTracked)
    {
        Profiler.Enabled = settings.EnableProfiler.Value;

        var buckets = new EntityBuckets(gc);
        var ctx = new TrackerContext(plugin, gc, buckets, area, elapsedSeconds);
        var now = Environment.TickCount64;

        foreach (var t in _trackers)
        {
            try
            {
                if (t.RequiresTrackedArea && !areaTracked)
                    continue;
                if (!t.Enabled(settings))
                    continue;

                var iv = t.IntervalMs(settings);
                if (iv > 0)
                {
                    if (_nextDueMs.TryGetValue(t.Name, out var due) && now < due)
                        continue;
                    _nextDueMs[t.Name] = now + iv;
                }

                Profiler.Begin(t.Name);
                t.Run(in ctx);
                Profiler.End(t.Name);
            }
            catch (Exception ex)
            {
                plugin.Err($"ExileStats -> tracker {t.Name} failed: {ex}");
            }
        }
    }
}
