using System.Collections.Generic;

namespace ExileStats;

/// <summary>Reads/writes the per-instance wisps.json (a JSON array of <see cref="WispEncounter"/>) under the
/// instance folder. <see cref="Append"/> upserts by fingerprint (victim position) - new encounters are added,
/// an existing one at the same victim location is replaced. Mirrors <see cref="ContentLog"/>.</summary>
public static class WispLog
{
    private static string P(string d, string a, long h) => InstanceStore.FilePath(d, a, h, InstanceStore.WispFile);

    /// <summary>Already-logged encounters for an instance keyed by fingerprint (empty if no file yet). Seeds
    /// the tracker so re-entry / restart doesn't re-log a resolved encounter.</summary>
    public static Dictionary<string, WispEncounter> LoadKnown(string pluginDirectory, string areaId, long instanceHash)
    {
        var map = new Dictionary<string, WispEncounter>();
        foreach (var s in Read(pluginDirectory, areaId, instanceHash))
            if (!string.IsNullOrEmpty(s.Fingerprint)) map[s.Fingerprint] = s;
        return map;
    }

    /// <summary>Upserts new encounters into the instance's wisps.json (by fingerprint).</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash, IReadOnlyList<WispEncounter> encounters)
    {
        if (encounters == null || encounters.Count == 0) return;
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        JsonArrayLog<WispEncounter>.Upsert(P(pluginDirectory, areaId, instanceHash), encounters, s => s.Fingerprint);
    }

    // no external caller needs the raw list, only LoadKnown's fingerprint map
    private static List<WispEncounter> Read(string pluginDirectory, string areaId, long instanceHash) =>
        JsonArrayLog<WispEncounter>.Read(P(pluginDirectory, areaId, instanceHash));
}
