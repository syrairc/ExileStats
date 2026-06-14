using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>One row in the master run index — just enough to pick runs by date/map without opening any
/// per-instance file. <see cref="Folder"/> + <see cref="ZoneSwitchId"/> locate the full record on disk.</summary>
public class RunIndexEntry
{
    public DateTime LoggedAt { get; set; }
    public DateTime EnteredAt { get; set; }
    public double DurationSeconds { get; set; }

    public string MapName { get; set; }     // DisplayName (falls back to Name)
    public string MapId { get; set; }        // AreaId, e.g. "MapReservoir"
    public long InstanceId { get; set; }     // AreaInstance.Hash
    public int ZoneSwitchId { get; set; }    // which visit, within the instance

    public int RunId { get; set; }           // groups a map + its sub-areas into one run (see MapRunRecord)
    public bool IsMapArea { get; set; }      // true = atlas map (the run's headline area); false = sub-area/zone

    public string Folder { get; set; }       // instance folder name under maps/
    public bool Archived { get; set; }       // hidden from picker unless "Show archived" is on
}

/// <summary>Appends a <see cref="RunIndexEntry"/> to the master <c>maps/index.json</c> on every logged run,
/// so date-range statistics scan one file instead of every instance folder.</summary>
public static class RunIndex
{
    private static readonly object _lock = new();

    /// <summary>Reads the full index without modifying it.</summary>
    public static List<RunIndexEntry> ReadAll(string pluginDirectory)
    {
        var path = InstanceStore.IndexPath(pluginDirectory);
        lock (_lock)
            return ReadAllInternal(path);
    }

    /// <summary>Locked read → transform → write-back. Use for archive/purge operations.</summary>
    public static void Modify(string pluginDirectory, Action<List<RunIndexEntry>> transform)
    {
        var path = InstanceStore.IndexPath(pluginDirectory);
        lock (_lock)
        {
            var list = ReadAllInternal(path);
            transform(list);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }

    private static List<RunIndexEntry> ReadAllInternal(string path)
    {
        if (!File.Exists(path)) return new List<RunIndexEntry>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<List<RunIndexEntry>>(json) ?? new List<RunIndexEntry>();
        }
        catch { return new List<RunIndexEntry>(); }
    }

    public static void Append(string pluginDirectory, MapRunRecord record)
    {
        var entry = new RunIndexEntry
        {
            LoggedAt = record.LoggedAt,
            EnteredAt = record.EnteredAt,
            DurationSeconds = record.DurationSeconds,
            MapName = string.IsNullOrEmpty(record.DisplayName) ? record.Name : record.DisplayName,
            MapId = record.AreaId,
            InstanceId = record.InstanceHash,
            ZoneSwitchId = record.ZoneSwitchId,
            RunId = record.RunId,
            IsMapArea = record.IsMapArea,
            Folder = InstanceStore.FolderName(record.AreaId, record.InstanceHash),
        };

        var path = InstanceStore.IndexPath(pluginDirectory);
        lock (_lock)
        {
            List<RunIndexEntry> list = null;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    list = JsonConvert.DeserializeObject<List<RunIndexEntry>>(json);
            }

            list ??= new List<RunIndexEntry>();
            list.Add(entry);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }
}
