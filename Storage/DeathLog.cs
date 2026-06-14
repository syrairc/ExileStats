using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>Appends <see cref="Death"/>s to the per-instance deaths.json (a JSON array) under the instance
/// folder. See <see cref="InstanceStore"/> for the layout.</summary>
public static class DeathLog
{
    private static readonly object _lock = new();

    public static void Append(string pluginDirectory, string areaId, long instanceHash, Death death)
    {
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.DeathsFile);
        lock (_lock)
        {
            List<Death> list = null;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    list = JsonConvert.DeserializeObject<List<Death>>(json);
            }

            list ??= new List<Death>();
            list.Add(death);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }
}
