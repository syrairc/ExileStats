using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>Reads/writes the per-instance monsters.json (a JSON array of <see cref="MonsterSighting"/>)
/// under the instance folder. Append is dedup-safe: the caller only passes monsters not already in the
/// seen-set, which is itself seeded from <see cref="LoadFingerprints"/>. Mirrors <see cref="LootLog"/>.</summary>
public static class MonsterLog
{
    private static readonly object _lock = new();

    /// <summary>Existing monster fingerprints for an instance (empty if no file yet), used to seed the
    /// collector so re-entry / restart doesn't re-log.</summary>
    public static IEnumerable<string> LoadFingerprints(string pluginDirectory, string areaId, long instanceHash)
    {
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.MonstersFile);
        lock (_lock)
        {
            var list = ReadList(path);
            return list.Select(m => m.Fingerprint).Where(f => !string.IsNullOrEmpty(f)).ToList();
        }
    }

    /// <summary>Appends newly-seen monster sightings to the instance's monsters.json.</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash,
        IReadOnlyList<MonsterSighting> newMonsters)
    {
        if (newMonsters == null || newMonsters.Count == 0)
            return;

        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.MonstersFile);
        lock (_lock)
        {
            var list = ReadList(path);
            list.AddRange(newMonsters);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }

    private static List<MonsterSighting> ReadList(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(json))
                return JsonConvert.DeserializeObject<List<MonsterSighting>>(json) ?? new List<MonsterSighting>();
        }
        return new List<MonsterSighting>();
    }
}
