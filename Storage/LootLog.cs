using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>Reads/writes the per-instance loot.json (a JSON array of <see cref="LootItem"/>) under the
/// instance folder. Append is dedup-safe: the caller only passes items not already in the seen-set, which
/// is itself seeded from <see cref="LoadFingerprints"/>.</summary>
public static class LootLog
{
    private static readonly object _lock = new();

    /// <summary>Existing item fingerprints for an instance (empty if no file yet), used to seed the
    /// tracker so re-entry / restart doesn't re-log.</summary>
    public static IEnumerable<string> LoadFingerprints(string pluginDirectory, string areaId, long instanceHash)
    {
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.LootFile);
        lock (_lock)
        {
            var list = ReadList(path);
            return list.Select(i => i.Fingerprint).Where(f => !string.IsNullOrEmpty(f)).ToList();
        }
    }

    /// <summary>Appends newly-seen loot items to the instance's loot.json.</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash,
        IReadOnlyList<LootItem> newItems)
    {
        if (newItems == null || newItems.Count == 0)
            return;

        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.LootFile);
        lock (_lock)
        {
            var list = ReadList(path);
            list.AddRange(newItems);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }

    /// <summary>All logged loot items for an instance (empty if no file yet).</summary>
    public static List<LootItem> Read(string pluginDirectory, string areaId, long instanceHash)
    {
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.LootFile);
        lock (_lock)
            return ReadList(path);
    }

    private static List<LootItem> ReadList(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(json))
                return JsonConvert.DeserializeObject<List<LootItem>>(json) ?? new List<LootItem>();
        }
        return new List<LootItem>();
    }
}
