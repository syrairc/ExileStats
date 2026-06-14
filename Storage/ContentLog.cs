using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>Reads/writes the per-instance content.json (a JSON array of <see cref="ContentSighting"/>) under
/// the instance folder. <see cref="Append"/> upserts by fingerprint — new sightings are added, existing ones
/// replaced (so a content piece's terminal state updates in place). Mirrors <see cref="LootLog"/>.</summary>
public static class ContentLog
{
    private static readonly object _lock = new();

    /// <summary>Already-logged sightings for an instance keyed by fingerprint (empty if no file yet). Seeds
    /// the tracker so re-entry / restart doesn't re-log and so state continues to upgrade.</summary>
    public static Dictionary<string, ContentSighting> LoadKnown(string pluginDirectory, string areaId, long instanceHash)
    {
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.ContentFile);
        lock (_lock)
        {
            var map = new Dictionary<string, ContentSighting>();
            foreach (var s in ReadList(path))
                if (!string.IsNullOrEmpty(s.Fingerprint))
                    map[s.Fingerprint] = s;
            return map;
        }
    }

    /// <summary>Upserts new/changed sightings into the instance's content.json (by fingerprint).</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash,
        IReadOnlyList<ContentSighting> sightings)
    {
        if (sightings == null || sightings.Count == 0)
            return;

        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.ContentFile);
        lock (_lock)
        {
            var list = ReadList(path);

            var byFp = new Dictionary<string, int>();
            for (var i = 0; i < list.Count; i++)
                if (!string.IsNullOrEmpty(list[i].Fingerprint))
                    byFp[list[i].Fingerprint] = i;

            foreach (var s in sightings)
            {
                if (!string.IsNullOrEmpty(s.Fingerprint) && byFp.TryGetValue(s.Fingerprint, out var idx))
                {
                    list[idx] = s; // update existing (terminal state)
                }
                else
                {
                    if (!string.IsNullOrEmpty(s.Fingerprint))
                        byFp[s.Fingerprint] = list.Count;
                    list.Add(s);
                }
            }

            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }

    /// <summary>All logged sightings for an instance (empty if no file yet).</summary>
    public static List<ContentSighting> Read(string pluginDirectory, string areaId, long instanceHash)
    {
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.ContentFile);
        lock (_lock)
            return ReadList(path);
    }

    private static List<ContentSighting> ReadList(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(json))
                return JsonConvert.DeserializeObject<List<ContentSighting>>(json) ?? new List<ContentSighting>();
        }
        return new List<ContentSighting>();
    }
}
