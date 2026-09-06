using System.Collections.Generic;

namespace ExileStats;

/// <summary>Reads/writes the per-instance pickups.json (a JSON array of <see cref="PickupItem"/>) under the
/// instance folder. The caller (<see cref="PickupTracker"/>) only passes genuinely new pickup events.</summary>
public static class PickupLog
{
    private static string P(string d, string a, long h) => InstanceStore.FilePath(d, a, h, InstanceStore.PickupsFile);

    /// <summary>Appends newly-detected pickups to the instance's pickups.json.</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash, IReadOnlyList<PickupItem> items)
    {
        if (items == null || items.Count == 0) return;
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        JsonArrayLog<PickupItem>.AppendRange(P(pluginDirectory, areaId, instanceHash), items);
    }

    /// <summary>All logged pickups for an instance (empty if no file yet).</summary>
    public static List<PickupItem> Read(string pluginDirectory, string areaId, long instanceHash) =>
        JsonArrayLog<PickupItem>.Read(P(pluginDirectory, areaId, instanceHash));
}
