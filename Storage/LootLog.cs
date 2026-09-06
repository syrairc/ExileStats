using System.Collections.Generic;

namespace ExileStats;

/// <summary>Reads/writes the per-instance loot.json (a JSON array of <see cref="LootItem"/>) under the
/// instance folder. Append is dedup-safe: the caller only passes items not already in the seen-set, which
/// is itself seeded from <see cref="LoadFingerprints"/>.</summary>
public static class LootLog
{
    private static string P(string d, string a, long h) => InstanceStore.FilePath(d, a, h, InstanceStore.LootFile);

    /// <summary>Appends newly-seen loot items to the instance's loot.json.</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash, IReadOnlyList<LootItem> newItems)
    {
        if (newItems == null || newItems.Count == 0) return;
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        JsonArrayLog<LootItem>.AppendRange(P(pluginDirectory, areaId, instanceHash), newItems);
    }

    /// <summary>All logged loot items for an instance (empty if no file yet).</summary>
    public static List<LootItem> Read(string pluginDirectory, string areaId, long instanceHash) =>
        JsonArrayLog<LootItem>.Read(P(pluginDirectory, areaId, instanceHash));

    /// <summary>Existing item fingerprints for an instance (empty if no file yet), used to seed the
    /// tracker so re-entry / restart doesn't re-log.</summary>
    public static IEnumerable<string> LoadFingerprints(string pluginDirectory, string areaId, long instanceHash)
    {
        var fps = new List<string>();
        foreach (var i in Read(pluginDirectory, areaId, instanceHash))
            if (!string.IsNullOrEmpty(i.Fingerprint)) fps.Add(i.Fingerprint);
        return fps;
    }
}
