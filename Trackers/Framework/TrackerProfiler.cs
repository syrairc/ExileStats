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
        public double AvgMs => Calls > 0 ? TotalMs / Calls : 0;  // cumulative mean - stable over a long run
    }

    private readonly Dictionary<string, Stat> _stats = new();
    private readonly List<(string Name, long Ticks)> _stack = new();   // open frames, so Begin/End can nest

    public bool Enabled { get; set; }

    public IReadOnlyDictionary<string, Stat> Stats => _stats;

    public void Begin(string name)
    {
        if (!Enabled)
            return;
        _stack.Add((name, Stopwatch.GetTimestamp()));
    }

    public void End(string name)
    {
        if (!Enabled)
            return;
        // pop the innermost frame with this name; drops any inner frame left open by a throw
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Name != name)
                continue;
            var ms = (Stopwatch.GetTimestamp() - _stack[i].Ticks) * 1000.0 / Stopwatch.Frequency;
            _stack.RemoveRange(i, _stack.Count - i);
            Record(name, ms);
            return;
        }
    }

    /// <summary>Record a plain number (not a duration) as its own row - entity counts, call counts.</summary>
    public void Count(string name, double n)
    {
        if (!Enabled)
            return;
        Record(name, n);
    }

    private void Record(string name, double v)
    {
        if (!_stats.TryGetValue(name, out var st))
            _stats[name] = st = new Stat();
        st.Calls++;
        st.LastMs = v;
        st.TotalMs += v;
        if (v > st.MaxMs)
            st.MaxMs = v;
    }

    public void Reset()
    {
        _stats.Clear();
        _stack.Clear();
    }
}
