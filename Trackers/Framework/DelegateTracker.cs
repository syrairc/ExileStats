using System;

namespace ExileStats;

/// <summary>
/// One registry entry: a per-tick monitor built from delegates, so the whole tracker set reads as one
/// declarative table in <c>ExileStats.BuildDispatcher</c> (add a monitor = add one line). The dispatcher
/// gates it (area + enabled + interval), times it, and runs it. The run delegate holds the actual logic.
/// </summary>
public sealed class DelegateTracker
{
    private readonly Func<ExileStatsSettings, bool> _enabled;
    private readonly Func<ExileStatsSettings, int> _interval;
    private readonly TrackerAction _run;

    public string Name { get; }
    public bool RequiresTrackedArea { get; }    // false for NetWorth (the stash opens in town/hideout)

    public DelegateTracker(string name, bool requiresTrackedArea,
        Func<ExileStatsSettings, bool> enabled, Func<ExileStatsSettings, int> interval, TrackerAction run)
    {
        Name = name;
        RequiresTrackedArea = requiresTrackedArea;
        _enabled = enabled;
        _interval = interval;
        _run = run;
    }

    public bool Enabled(ExileStatsSettings s) => _enabled(s);
    public int IntervalMs(ExileStatsSettings s) => _interval(s);
    public void Run(in TrackerContext ctx) => _run(in ctx);
}
