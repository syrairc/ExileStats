using System.Collections.Generic;

namespace ExileStats;

/// <summary>Reads/writes the per-instance content.json (a JSON array of <see cref="ContentSighting"/>) under
/// the instance folder. <see cref="Append"/> upserts by fingerprint - new sightings are added, existing ones
/// replaced (so a content piece's terminal state updates in place). Mirrors <see cref="LootLog"/>.</summary>
public static class ContentLog
{
    private static string P(string d, string a, long h) => InstanceStore.FilePath(d, a, h, InstanceStore.ContentFile);

    /// <summary>Already-logged sightings for an instance keyed by fingerprint (empty if no file yet). Seeds
    /// the tracker so re-entry / restart doesn't re-log and so state continues to upgrade.</summary>
    public static Dictionary<string, ContentSighting> LoadKnown(string pluginDirectory, string areaId, long instanceHash)
    {
        var map = new Dictionary<string, ContentSighting>();
        foreach (var s in Read(pluginDirectory, areaId, instanceHash))
            if (!string.IsNullOrEmpty(s.Fingerprint)) map[s.Fingerprint] = s;
        return map;
    }

    /// <summary>Upserts new/changed sightings into the instance's content.json (by fingerprint).</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash, IReadOnlyList<ContentSighting> sightings)
    {
        if (sightings == null || sightings.Count == 0) return;
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        JsonArrayLog<ContentSighting>.Upsert(P(pluginDirectory, areaId, instanceHash), sightings, s => s.Fingerprint);
    }

    /// <summary>All logged sightings for an instance (empty if no file yet).</summary>
    public static List<ContentSighting> Read(string pluginDirectory, string areaId, long instanceHash) =>
        JsonArrayLog<ContentSighting>.Read(P(pluginDirectory, areaId, instanceHash));
}
