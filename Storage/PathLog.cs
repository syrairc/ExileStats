using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>Reads/writes the per-instance path.json (a JSON array of <see cref="PathPoint"/>) under the
/// instance folder. <see cref="PathTracker"/> buffers points in memory and flushes them here in batches, so
/// the read-append-write happens occasionally rather than every sample.</summary>
public static class PathLog
{
    private static readonly object _lock = new();

    /// <summary>Appends a batch of buffered path points to the instance's path.json.</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash,
        IReadOnlyList<PathPoint> points)
    {
        if (points == null || points.Count == 0)
            return;

        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.PathFile);
        lock (_lock)
        {
            var list = ReadList(path);
            list.AddRange(points);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }

    /// <summary>All logged path points for an instance (empty if no file yet).</summary>
    public static List<PathPoint> Read(string pluginDirectory, string areaId, long instanceHash)
    {
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.PathFile);
        lock (_lock)
            return ReadList(path);
    }

    private static List<PathPoint> ReadList(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(json))
                return JsonConvert.DeserializeObject<List<PathPoint>>(json) ?? new List<PathPoint>();
        }
        return new List<PathPoint>();
    }
}
