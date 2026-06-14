using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>Appends <see cref="Snapshot"/>s to the per-instance snapshots.json (a JSON array time-series)
/// under the instance folder. See <see cref="InstanceStore"/> for the layout.</summary>
public static class SnapshotLog
{
    private static readonly object _lock = new();

    public static void Append(string pluginDirectory, string areaId, long instanceHash, Snapshot snapshot)
    {
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.SnapshotFile);
        lock (_lock)
        {
            List<Snapshot> list = null;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    list = JsonConvert.DeserializeObject<List<Snapshot>>(json);
            }

            list ??= new List<Snapshot>();
            list.Add(snapshot);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }
}
