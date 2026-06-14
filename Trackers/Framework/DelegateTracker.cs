using System;

namespace ExileStats;

/// <summary>
/// A registry entry that adapts an existing tracker into an <see cref="ITracker"/> via delegates, so the
/// whole tracker set reads as one declarative table in <c>ExileStats.BuildDispatcher</c> (add a monitor =
/// add one line). The run delegate is a plugin method that holds the actual logic.
/// </summary>
public sealed class DelegateTracker : ITracker
{
    private readonly Func<ExileStatsSettings, bool> _enabled;
    private readonly Func<ExileStatsSettings, int> _interval;
    private readonly TrackerAction _run;

    public string Name { get; }
    public EntityNeed Needs { get; }
    public bool RequiresTrackedArea { get; }

    public DelegateTracker(string name, EntityNeed needs, bool requiresTrackedArea,
        Func<ExileStatsSettings, bool> enabled, Func<ExileStatsSettings, int> interval, TrackerAction run)
    {
        Name = name;
        Needs = needs;
        RequiresTrackedArea = requiresTrackedArea;
        _enabled = enabled;
        _interval = interval;
        _run = run;
    }

    public bool Enabled(ExileStatsSettings s) => _enabled(s);
    public int IntervalMs(ExileStatsSettings s) => _interval(s);
    public void Run(in TrackerContext ctx) => _run(in ctx);
}
