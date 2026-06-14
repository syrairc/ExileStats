using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>Reads/writes the per-instance wisps.json (a JSON array of <see cref="WispEncounter"/>) under the
/// instance folder. <see cref="Append"/> upserts by fingerprint (victim position) — new encounters are added,
/// an existing one at the same victim location is replaced. Mirrors <see cref="ContentLog"/>.</summary>
public static class WispLog
{
    private static readonly object _lock = new();

    /// <summary>Already-logged encounters for an instance keyed by fingerprint (empty if no file yet). Seeds
    /// the tracker so re-entry / restart doesn't re-log a resolved encounter.</summary>
    public static Dictionary<string, WispEncounter> LoadKnown(string pluginDirectory, string areaId, long instanceHash)
    {
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.WispFile);
        lock (_lock)
        {
            var map = new Dictionary<string, WispEncounter>();
            foreach (var s in ReadList(path))
                if (!string.IsNullOrEmpty(s.Fingerprint))
                    map[s.Fingerprint] = s;
            return map;
        }
    }

    /// <summary>Upserts new encounters into the instance's wisps.json (by fingerprint).</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash,
        IReadOnlyList<WispEncounter> encounters)
    {
        if (encounters == null || encounters.Count == 0)
            return;

        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.WispFile);
        lock (_lock)
        {
            var list = ReadList(path);

            var byFp = new Dictionary<string, int>();
            for (var i = 0; i < list.Count; i++)
                if (!string.IsNullOrEmpty(list[i].Fingerprint))
                    byFp[list[i].Fingerprint] = i;

            foreach (var s in encounters)
            {
                if (!string.IsNullOrEmpty(s.Fingerprint) && byFp.TryGetValue(s.Fingerprint, out var idx))
                {
                    list[idx] = s; // update existing
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

    /// <summary>All logged encounters for an instance (empty if no file yet).</summary>
    public static List<WispEncounter> Read(string pluginDirectory, string areaId, long instanceHash)
    {
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.WispFile);
        lock (_lock)
            return ReadList(path);
    }

    private static List<WispEncounter> ReadList(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(json))
                return JsonConvert.DeserializeObject<List<WispEncounter>>(json) ?? new List<WispEncounter>();
        }
        return new List<WispEncounter>();
    }
}
