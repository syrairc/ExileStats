using System;
using ExileCore2;

namespace ExileStats;

/// <summary>
/// Drives the registered per-tick trackers. Builds one shared <see cref="EntityBuckets"/> per tick, then for
/// each tracker checks the gate (tracked-area + enabled + per-tracker interval), times it via the
/// <see cref="Profiler"/>, and runs it inside its own try/catch so one failing tracker can't stop the rest.
/// Replaces the old flat wall of <c>if (Settings.X &amp;&amp; IsTracked) { try { ... } }</c> blocks in Tick.
/// </summary>
public sealed class TrackerDispatcher
{
    private readonly DelegateTracker[] _trackers;
    private readonly long[] _nextDue;       // per tracker, earliest next run (TickCount64); 0 = not armed

    public TrackerProfiler Profiler { get; } = new();

    public TrackerDispatcher(params DelegateTracker[] trackers)
    {
        _trackers = trackers;
        _nextDue = new long[trackers.Length];
    }

    /// <summary>Arm the per-tracker interval gates on area entry: an interval-throttled tracker first runs one
    /// interval after entry (skips the load-instant), matching the old snapshot timer behavior.</summary>
    public void OnAreaChange(ExileStatsSettings s)
    {
        var now = Environment.TickCount64;
        for (var i = 0; i < _trackers.Length; i++)
        {
            var iv = _trackers[i].IntervalMs(s);
            _nextDue[i] = iv > 0 ? now + iv : 0;
        }
    }

    public void Tick(ExileStats plugin, GameController gc, ExileStatsSettings settings,
        MapRunRecord area, double elapsedSeconds, bool areaTracked)
    {
        Profiler.Enabled = settings.EnableProfiler.Value;

        var buckets = new EntityBuckets(gc);
        var ctx = new TrackerContext(buckets, area, elapsedSeconds);
        var now = Environment.TickCount64;

        for (var i = 0; i < _trackers.Length; i++)
        {
            var t = _trackers[i];
            try
            {
                if (t.RequiresTrackedArea && !areaTracked)
                    continue;
                if (!t.Enabled(settings))
                    continue;

                var iv = t.IntervalMs(settings);
                if (iv > 0)
                {
                    if (now < _nextDue[i])
                        continue;
                    _nextDue[i] = now + iv;
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
