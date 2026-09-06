using System;
using System.Collections.Generic;

namespace ExileStats;

// the one run-grouping rule: RunId groups, RunId 0 is its own solo run, members oldest first
public static class RunGrouping
{
    public static string Key(int runId, string folder, int zoneSwitchId) =>
        runId != 0 ? "run:" + runId : "solo:" + folder + ":" + zoneSwitchId;

    public static List<List<T>> Group<T>(IEnumerable<T> items, Func<T, int> runId, Func<T, string> folder,
        Func<T, int> zone, Func<T, DateTime> loggedAt)
    {
        var byKey = new Dictionary<string, List<T>>();
        var order = new List<List<T>>();
        foreach (var it in items)
        {
            var k = Key(runId(it), folder(it), zone(it));
            if (!byKey.TryGetValue(k, out var g))
            {
                g = new List<T>();
                byKey[k] = g;
                order.Add(g);
            }
            g.Add(it);
        }
        foreach (var g in order)
            g.Sort((a, b) => loggedAt(a).CompareTo(loggedAt(b)));
        return order;
    }

    public static T Headline<T>(List<T> members, Func<T, bool> isMap)
    {
        foreach (var m in members) if (isMap(m)) return m;
        return members[0];
    }
}
