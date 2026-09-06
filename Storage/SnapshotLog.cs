namespace ExileStats;

/// <summary>Appends <see cref="Snapshot"/>s to the per-instance snapshots.json (a JSON array time-series)
/// under the instance folder. See <see cref="InstanceStore"/> for the layout.</summary>
public static class SnapshotLog
{
    public static void Append(string pluginDirectory, string areaId, long instanceHash, Snapshot snapshot)
    {
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        JsonArrayLog<Snapshot>.Append(
            InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.SnapshotFile), snapshot);
    }
}
