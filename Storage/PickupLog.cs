using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>Reads/writes the per-instance pickups.json (a JSON array of <see cref="PickupItem"/>) under the
/// instance folder. The caller (<see cref="PickupTracker"/>) only passes genuinely new pickup events.</summary>
public static class PickupLog
{
    private static readonly object _lock = new();

    /// <summary>Appends newly-detected pickups to the instance's pickups.json.</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash,
        IReadOnlyList<PickupItem> newItems)
    {
        if (newItems == null || newItems.Count == 0)
            return;

        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.PickupsFile);
        lock (_lock)
        {
            var list = ReadList(path);
            list.AddRange(newItems);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }

    /// <summary>All logged pickups for an instance (empty if no file yet).</summary>
    public static List<PickupItem> Read(string pluginDirectory, string areaId, long instanceHash)
    {
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.PickupsFile);
        lock (_lock)
            return ReadList(path);
    }

    private static List<PickupItem> ReadList(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(json))
                return JsonConvert.DeserializeObject<List<PickupItem>>(json) ?? new List<PickupItem>();
        }
        return new List<PickupItem>();
    }
}
