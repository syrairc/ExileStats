using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

// one json array per file, read-modify-write under a per-type lock. every *Log facade delegates here
internal static class JsonArrayLog<T>
{
    private static readonly object Lock = new();

    public static List<T> Read(string path)
    {
        lock (Lock) return ReadRaw(path);
    }

    public static void Append(string path, T item) => Modify(path, l => l.Add(item));

    public static void AppendRange(string path, IReadOnlyList<T> items)
    {
        if (items == null || items.Count == 0) return;
        Modify(path, l => l.AddRange(items));
    }

    // replace rows whose key matches, append the rest. key order is preserved
    public static void Upsert(string path, IReadOnlyList<T> items, Func<T, string> key)
    {
        if (items == null || items.Count == 0) return;
        Modify(path, list =>
        {
            var at = new Dictionary<string, int>();
            for (var i = 0; i < list.Count; i++)
            {
                var k = key(list[i]);
                if (!string.IsNullOrEmpty(k)) at[k] = i;
            }
            foreach (var it in items)
            {
                var k = key(it);
                if (!string.IsNullOrEmpty(k) && at.TryGetValue(k, out var idx)) list[idx] = it;
                else
                {
                    if (!string.IsNullOrEmpty(k)) at[k] = list.Count;
                    list.Add(it);
                }
            }
        });
    }

    // write path: a present, non-empty, corrupt file must throw so the append aborts and disk stays untouched
    public static void Modify(string path, Action<List<T>> transform)
    {
        lock (Lock)
        {
            var list = ReadStrict(path);
            transform(list);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.None));
        }
    }

    // read path: never throws, a corrupt file just reads as empty so the ui/report can't crash on one bad file
    private static List<T> ReadRaw(string path)
    {
        try { return ReadStrict(path); }
        catch { return new List<T>(); }
    }

    // missing/empty file means "no rows yet", only a present-but-unparseable file throws.
    // internal (not private) so JsonLinesLog can use it for the strict legacy-array conversion read
    internal static List<T> ReadStrict(string path)
    {
        if (!File.Exists(path)) return new List<T>();
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();
        return JsonConvert.DeserializeObject<List<T>>(json) ?? new List<T>();
    }
}
