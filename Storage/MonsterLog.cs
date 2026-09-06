using System.Collections.Generic;

namespace ExileStats;

/// <summary>Reads/writes the per-instance monsters.json (JSON Lines, one <see cref="MonsterSighting"/> per
/// line; legacy array files still read and are converted on first append) under the instance folder.
/// Append is dedup-safe: the caller only passes monsters not already in the seen-set, which is itself
/// seeded from <see cref="LoadFingerprints"/>. Mirrors <see cref="LootLog"/>.</summary>
public static class MonsterLog
{
    private static string P(string d, string a, long h) => InstanceStore.FilePath(d, a, h, InstanceStore.MonstersFile);

    /// <summary>Appends newly-seen monster sightings to the instance's monsters.json.</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash, IReadOnlyList<MonsterSighting> newMonsters)
    {
        if (newMonsters == null || newMonsters.Count == 0) return;
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        JsonLinesLog<MonsterSighting>.Append(P(pluginDirectory, areaId, instanceHash), newMonsters);
    }

    /// <summary>All logged monster sightings for an instance (empty if no file yet).</summary>
    public static List<MonsterSighting> Read(string pluginDirectory, string areaId, long instanceHash) =>
        JsonLinesLog<MonsterSighting>.Read(P(pluginDirectory, areaId, instanceHash));

    /// <summary>For readers that already hold the instance folder path (stats window, report).</summary>
    public static List<MonsterSighting> ReadFolder(string folder) =>
        JsonLinesLog<MonsterSighting>.Read(System.IO.Path.Combine(folder, InstanceStore.MonstersFile));

    /// <summary>Existing monster fingerprints for an instance (empty if no file yet), used to seed the
    /// collector so re-entry / restart doesn't re-log.</summary>
    public static IEnumerable<string> LoadFingerprints(string pluginDirectory, string areaId, long instanceHash)
    {
        var fps = new List<string>();
        foreach (var m in Read(pluginDirectory, areaId, instanceHash))
            if (!string.IsNullOrEmpty(m.Fingerprint)) fps.Add(m.Fingerprint);
        return fps;
    }
}
