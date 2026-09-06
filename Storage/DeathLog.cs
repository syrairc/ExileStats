namespace ExileStats;

/// <summary>Appends <see cref="Death"/>s to the per-instance deaths.json (a JSON array) under the instance
/// folder. See <see cref="InstanceStore"/> for the layout.</summary>
public static class DeathLog
{
    public static void Append(string pluginDirectory, string areaId, long instanceHash, Death death)
    {
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        JsonArrayLog<Death>.Append(
            InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.DeathsFile), death);
    }
}
