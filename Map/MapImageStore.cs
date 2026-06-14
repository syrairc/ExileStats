using System.IO;

namespace ExileStats;

/// <summary>Saves the Radar map into the instance folder: the vector <c>map.svg</c> (from
/// <c>Radar.GetMapSvg</c>, infinitely scalable, preferred) or the raster <c>map.png</c> (from
/// <c>Radar.GetMapImage</c>). Latest capture overwrites. See <see cref="InstanceStore"/> for the layout.</summary>
public static class MapImageStore
{
    /// <summary>Writes <paramref name="png"/> and returns the path relative to the plugin dir
    /// (e.g. "maps/MapReservoir_123/map.png").</summary>
    public static string Save(string pluginDirectory, string areaId, long instanceHash, byte[] png)
    {
        var dir = InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        File.WriteAllBytes(Path.Combine(dir, InstanceStore.ImageFile), png);
        return $"{InstanceStore.RootFolder}/{InstanceStore.FolderName(areaId, instanceHash)}/{InstanceStore.ImageFile}";
    }

    /// <summary>Writes the <paramref name="svg"/> text and returns the path relative to the plugin dir
    /// (e.g. "maps/MapReservoir_123/map.svg").</summary>
    public static string SaveSvg(string pluginDirectory, string areaId, long instanceHash, string svg)
    {
        var dir = InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        File.WriteAllText(Path.Combine(dir, InstanceStore.SvgFile), svg);
        return $"{InstanceStore.RootFolder}/{InstanceStore.FolderName(areaId, instanceHash)}/{InstanceStore.SvgFile}";
    }
}
