using System.Collections.Generic;

namespace ExileStats;

/// <summary>Reads/writes the per-instance path.json (JSON Lines, one <see cref="PathPoint"/> per line;
/// legacy array files still read and are converted on first append) under the instance folder.
/// <see cref="PathTracker"/> buffers points in memory and flushes them here in batches, so appending is a
/// plain file append, not a read-modify-write.</summary>
public static class PathLog
{
    private static string P(string d, string a, long h) => InstanceStore.FilePath(d, a, h, InstanceStore.PathFile);

    /// <summary>Appends a batch of buffered path points to the instance's path.json.</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash, IReadOnlyList<PathPoint> points)
    {
        if (points == null || points.Count == 0) return;
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        JsonLinesLog<PathPoint>.Append(P(pluginDirectory, areaId, instanceHash), points);
    }

    /// <summary>All logged path points for an instance (empty if no file yet).</summary>
    public static List<PathPoint> Read(string pluginDirectory, string areaId, long instanceHash) =>
        JsonLinesLog<PathPoint>.Read(P(pluginDirectory, areaId, instanceHash));

    /// <summary>For readers that already hold the instance folder path (stats window, report).</summary>
    public static List<PathPoint> ReadFolder(string folder) =>
        JsonLinesLog<PathPoint>.Read(System.IO.Path.Combine(folder, InstanceStore.PathFile));
}
