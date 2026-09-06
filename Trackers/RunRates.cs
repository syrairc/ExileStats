using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>
/// Rolling per-run xp / duration samples, grouped by <c>RunId</c> (a map plus its sub-areas is one run),
/// used by the statistics overlay for the "last N maps" averages. Runs are appended as they finish and the
/// recent history is seeded from disk once so the averages are meaningful right after a plugin reload.
/// </summary>
public sealed class RunRates
{
    /// <summary>One finished run. Times are UTC and both derived from the same clock: <see cref="End"/> is
    /// the log time, <see cref="Start"/> is End minus the logged duration (so a local/UTC mix can't skew it).</summary>
    public sealed class Sample
    {
        public int RunId;
        public DateTime Start;
        public DateTime End;
        public long Xp;
        public double Value;      // looted worth in the base currency (MapRunRecord.PickupValue)
        public bool IsMap;
    }

    public readonly record struct Result(int MapCount, long Xp, double Value, double SpanHours,
        double XpPerHour, double XpPerMap, double MapsPerHour,
        double ValuePerHour, double ValuePerMap);

    private const int Cap = 60;          // plenty for any "last N" the slider allows
    private readonly List<Sample> _runs = new();
    private bool _seeded;
    private int _version;
    private int _cachedN = -1;
    private int _cachedVersion = -1;
    private Result? _cached;

    public int Count => _runs.Count;

    // one finished area segment. same RunId as the newest sample = same run, another area of it
    private void Fold(int runId, DateTime start, DateTime end, long xp, double value, bool isMap)
    {
        var last = _runs.Count > 0 ? _runs[^1] : null;
        if (last != null && runId != 0 && last.RunId == runId)
        {
            last.Xp += xp;
            last.Value += value;
            last.IsMap |= isMap;
            if (end > last.End) last.End = end;
            if (start < last.Start) last.Start = start;
            _version++;
            return;
        }
        _runs.Add(new Sample { RunId = runId, Start = start, End = end, Xp = xp, Value = value, IsMap = isMap });
        Trim();
        _version++;
    }

    /// <summary>Folds a finished area into the rolling history, merging into the run it belongs to.</summary>
    public void Add(MapRunRecord rec)
    {
        if (rec == null) return;
        var end = rec.LoggedAt.ToUniversalTime();
        Fold(rec.RunId, end.AddSeconds(-Math.Max(0, rec.DurationSeconds)), end, rec.XpGained, rec.PickupValue,
            rec.IsMapArea);
    }

    /// <summary>Loads the last <paramref name="hours"/> of runs from <c>maps/index.json</c> (+ each folder's
    /// run.json for the xp figures). Runs once; later calls are no-ops. Never throws.</summary>
    public void Seed(string pluginDirectory, int hours)
    {
        if (_seeded) return;
        _seeded = true;

        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, hours));
            var index = RunIndex.ReadAll(pluginDirectory)
                .Where(e => e.LoggedAt.ToUniversalTime() >= cutoff)
                .OrderBy(e => e.LoggedAt)
                .ToList();
            if (index.Count == 0) return;

            // One read per instance folder, then match each index row to its visit by LoggedAt (ZoneSwitchId fallback).
            var byFolder = new Dictionary<string, List<MapRunRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in index)
            {
                if (string.IsNullOrEmpty(e.Folder)) continue;
                if (!byFolder.TryGetValue(e.Folder, out var records))
                {
                    records = ReadRunFile(pluginDirectory, e.Folder);
                    byFolder[e.Folder] = records;
                }

                // ZoneSwitchId repeats when an instance is re-entered, so pin the visit by log time first
                var rec = records.FirstOrDefault(r => r.LoggedAt == e.LoggedAt)
                          ?? records.FirstOrDefault(r => r.ZoneSwitchId == e.ZoneSwitchId);
                var end = e.LoggedAt.ToUniversalTime();
                // recompute from the id: rows logged before a prefix was recognised have a stale flag
                Fold(e.RunId, end.AddSeconds(-Math.Max(0, e.DurationSeconds)), end,
                    rec?.XpGained ?? 0, rec?.PickupValue ?? 0, InstanceStore.IsMapAreaId(e.MapId));
            }
        }
        catch { /* history is a nicety; an unreadable index just means the overlay warms up as you play */ }
    }

    /// <summary>Averages over the most recent <paramref name="lastN"/> map runs. Null until at least one map
    /// run with a positive wall span is known.</summary>
    public Result? Compute(int lastN)
    {
        if (lastN < 1) lastN = 1;
        if (_cachedN == lastN && _cachedVersion == _version) return _cached;

        var maps = new List<Sample>(lastN);
        for (int i = _runs.Count - 1; i >= 0 && maps.Count < lastN; i--)
            if (_runs[i].IsMap) maps.Add(_runs[i]);
        if (maps.Count == 0)
        {
            _cachedN = lastN; _cachedVersion = _version; _cached = null;
            return _cached;
        }

        // maps is newest-first, so [^1] is the oldest of the window.
        var span = (maps[0].End - maps[^1].Start).TotalHours;
        if (span <= 0)
        {
            _cachedN = lastN; _cachedVersion = _version; _cached = null;
            return _cached;
        }

        long xp = 0;
        double value = 0;
        foreach (var m in maps) { xp += m.Xp; value += m.Value; }

        _cachedN = lastN; _cachedVersion = _version;
        _cached = new Result(maps.Count, xp, value, span,
            xp / span, xp / (double)maps.Count, maps.Count / span,
            value / span, value / maps.Count);
        return _cached;
    }

    private void Trim()
    {
        if (_runs.Count > Cap) _runs.RemoveRange(0, _runs.Count - Cap);
    }

    private static List<MapRunRecord> ReadRunFile(string pluginDirectory, string folder)
    {
        try
        {
            var path = Path.Combine(pluginDirectory, InstanceStore.RootFolder, folder, InstanceStore.RunFile);
            if (!File.Exists(path)) return new List<MapRunRecord>();
            return JsonConvert.DeserializeObject<List<MapRunRecord>>(File.ReadAllText(path))
                   ?? new List<MapRunRecord>();
        }
        catch { return new List<MapRunRecord>(); }
    }
}
