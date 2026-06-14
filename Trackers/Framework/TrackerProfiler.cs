using System.Collections.Generic;
using System.Diagnostics;

namespace ExileStats;

/// <summary>
/// Optional per-tracker timing. The dispatcher brackets each tracker's run with <see cref="Begin"/> /
/// <see cref="End"/>; when <see cref="Enabled"/> is false both early-out (no Stopwatch, no allocation), so
/// the cost is zero unless the user turns it on. Stats are keyed by tracker name, so every future tracker
/// auto-appears as a row in the Map Statistics "Performance" tab.
/// </summary>
public sealed class TrackerProfiler
{
    public sealed class Stat
    {
        public long Calls;
        public double LastMs;
        public double TotalMs;                                   // sum of every sample since reset
        public double MaxMs;
        public double AvgMs => Calls > 0 ? TotalMs / Calls : 0;  // cumulative mean — stable over a long run
    }

    private readonly Dictionary<string, Stat> _stats = new();
    private readonly Stopwatch _sw = new();
    private string _active;

    public bool Enabled { get; set; }

    public IReadOnlyDictionary<string, Stat> Stats => _stats;

    public void Begin(string name)
    {
        if (!Enabled)
            return;
        _active = name;
        _sw.Restart();
    }

    public void End(string name)
    {
        if (!Enabled || _active != name)
            return;
        _sw.Stop();
        var ms = _sw.Elapsed.TotalMilliseconds;
        if (!_stats.TryGetValue(name, out var st))
            _stats[name] = st = new Stat();
        st.Calls++;
        st.LastMs = ms;
        st.TotalMs += ms;
        if (ms > st.MaxMs)
            st.MaxMs = ms;
        _active = null;
    }

    public void Reset() => _stats.Clear();
}
