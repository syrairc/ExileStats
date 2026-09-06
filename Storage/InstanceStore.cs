using System.IO;

namespace ExileStats;

/// <summary>Shared on-disk layout for per-map-instance data. Everything for one map instance lives in
/// <c>maps/&lt;AreaId&gt;_&lt;InstanceHash&gt;/</c>: <see cref="RunFile"/> (monster/run stats),
/// <see cref="LootFile"/> (ground loot), and <see cref="ImageFile"/> (Radar map PNG). A future in-game
/// viewer enumerates these folders to list completed maps.</summary>
public static class InstanceStore
{
    public const string RootFolder = "maps";
    public const string RunFile = "run.json";
    public const string LootFile = "loot.json";
    public const string PickupsFile = "pickups.json";
    public const string SnapshotFile = "snapshots.json";
    public const string PathFile = "path.json";
    public const string DeathsFile = "deaths.json";
    public const string ContentFile = "content.json";
    public const string WispFile = "wisps.json";
    public const string MonstersFile = "monsters.json";
    public const string ImageFile = "map.png";
    public const string SvgFile = "map.svg";

    /// <summary>Master index of every logged run (one lightweight row per visit), at <c>maps/index.json</c>.
    /// Lets date-range queries scan a single file instead of every instance folder.</summary>
    public const string IndexFile = "index.json";

    // Account-global stash / net-worth data lives outside maps/ (it isn't per-map-instance).
    public const string StashFolder = "stash";
    public const string StashTabsFile = "tabs.json";     // latest snapshot per stash tab (+ backpack)
    public const string NetWorthFile = "networth.json";  // net-worth time-series

    /// <summary>Full path to the master index file, creating the <c>maps/</c> root if needed.</summary>
    public static string IndexPath(string pluginDirectory)
    {
        var root = Path.Combine(pluginDirectory, RootFolder);
        Directory.CreateDirectory(root);
        return Path.Combine(root, IndexFile);
    }

    /// <summary>Folder name for an instance, e.g. "MapReservoir_123456789".</summary>
    public static string FolderName(string areaId, long instanceHash)
    {
        var safeArea = string.IsNullOrEmpty(areaId) ? "Unknown" : Sanitize(areaId);
        return $"{safeArea}_{instanceHash}";
    }

    /// <summary>Full path to the instance folder, creating it if needed.</summary>
    public static string EnsureFolder(string pluginDirectory, string areaId, long instanceHash)
    {
        var dir = Path.Combine(pluginDirectory, RootFolder, FolderName(areaId, instanceHash));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Full path to a file inside the instance folder (does not create the file).</summary>
    public static string FilePath(string pluginDirectory, string areaId, long instanceHash, string fileName)
        => Path.Combine(pluginDirectory, RootFolder, FolderName(areaId, instanceHash), fileName);

    /// <summary>Full path to the account-global stash folder (net worth lives here, NOT under maps/),
    /// creating it if needed.</summary>
    public static string StashDir(string pluginDirectory)
    {
        var dir = Path.Combine(pluginDirectory, StashFolder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Full path to a file inside the stash folder, creating the folder if needed.</summary>
    public static string StashFilePath(string pluginDirectory, string fileName)
        => Path.Combine(StashDir(pluginDirectory), fileName);

    // atlas map vs everything else. the one place the "Map" prefix rule lives
    public static bool IsMapAreaId(string areaId) =>
        !string.IsNullOrEmpty(areaId) && areaId.StartsWith("Map", System.StringComparison.Ordinal);

    public static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
