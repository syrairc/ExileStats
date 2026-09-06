using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>Reads/writes the account-global stash data under <c>stash/</c>:
/// <see cref="InstanceStore.StashTabsFile"/> (latest snapshot per tab, keyed by tab name) and
/// <see cref="InstanceStore.NetWorthFile"/> (a net-worth time-series). Mirrors <see cref="PickupLog"/>:
/// static, locked file I/O, missing-file safe.</summary>
public static class StashLog
{
    private static readonly object _lock = new();

    /// <summary>Overwrites stash/tabs.json with the current per-tab snapshots (latest-wins map).</summary>
    public static void WriteTabs(string pluginDirectory, IReadOnlyDictionary<string, StashTabSnapshot> tabs)
    {
        if (tabs == null)
            return;
        var path = InstanceStore.StashFilePath(pluginDirectory, InstanceStore.StashTabsFile);
        lock (_lock)
            File.WriteAllText(path, JsonConvert.SerializeObject(tabs, Formatting.None));
    }

    /// <summary>Latest per-tab snapshots (empty if no file yet).</summary>
    public static Dictionary<string, StashTabSnapshot> ReadTabs(string pluginDirectory)
    {
        var path = InstanceStore.StashFilePath(pluginDirectory, InstanceStore.StashTabsFile);
        lock (_lock)
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonConvert.DeserializeObject<Dictionary<string, StashTabSnapshot>>(json)
                           ?? new Dictionary<string, StashTabSnapshot>();
            }
            return new Dictionary<string, StashTabSnapshot>();
        }
    }

    /// <summary>Appends one net-worth point to stash/networth.json.</summary>
    public static void AppendNetWorth(string pluginDirectory, NetWorthPoint point)
    {
        if (point == null)
            return;
        JsonArrayLog<NetWorthPoint>.Append(InstanceStore.StashFilePath(pluginDirectory, InstanceStore.NetWorthFile), point);
    }

    /// <summary>The whole net-worth time-series (empty if no file yet).</summary>
    public static List<NetWorthPoint> ReadNetWorth(string pluginDirectory) =>
        JsonArrayLog<NetWorthPoint>.Read(InstanceStore.StashFilePath(pluginDirectory, InstanceStore.NetWorthFile));
}
