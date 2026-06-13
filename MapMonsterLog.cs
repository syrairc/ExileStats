using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>
/// One logged map run: full area/template metadata, session/server context, gold/XP deltas, and the
/// monster counts accumulated while in the area. Appended to map_monster_log.json when leaving a map
/// back to the hideout. Group by <see cref="AreaId"/> (e.g. "MapReservoir") to collect data across
/// multiple instances of the same map.
/// </summary>
public class MapRunRecord
{
    // Timing
    public DateTime LoggedAt { get; set; }
    public DateTime EnteredAt { get; set; }
    public double DurationSeconds { get; set; }

    // AreaInstance (the rolled instance)
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public int Act { get; set; }
    public int RealLevel { get; set; }
    public bool IsTown { get; set; }
    public bool IsHideout { get; set; }
    public bool IsPeaceful { get; set; }
    public bool HasWaypoint { get; set; }
    public long InstanceHash { get; set; }
    public int ZoneSwitchId { get; set; }

    // WorldArea (the template / stable map identity). AreaId e.g. "MapReservoir".
    public string AreaId { get; set; }
    public int Index { get; set; }
    public int AreaLevel { get; set; }
    public long WorldAreaId { get; set; }
    public bool IsUnique { get; set; }

    // Session / server context
    public string League { get; set; }
    public int MonsterLevel { get; set; }
    public int CharacterLevel { get; set; }
    public long ServerInstanceId { get; set; }

    // Gold (account-wide; delta over the run)
    public long GoldStart { get; set; }
    public long GoldEnd { get; set; }
    public long GoldGained { get; set; }

    // Experience (cumulative Player.XP; delta over the run)
    public long XpStart { get; set; }
    public long XpEnd { get; set; }
    public long XpGained { get; set; }

    // Monster counts (distinct hostile entities seen this run)
    public int MonstersTotal { get; set; }
    public int White { get; set; }
    public int Magic { get; set; }
    public int Rare { get; set; }
    public int Unique { get; set; }

    // Breakdown by monster type (metadata key -> distinct count) and unique monster names seen.
    public Dictionary<string, int> MonstersByType { get; set; }
    public List<string> UniqueMonsters { get; set; }

    // True if this area is an atlas map (AreaId starts with "Map"). Not persisted.
    [JsonIgnore] public bool IsMapArea { get; set; }
}

/// <summary>Appends <see cref="MapRunRecord"/>s to a JSON array file in the plugin directory.</summary>
public static class MapMonsterLog
{
    public const string FileName = "map_monster_log.json";
    private static readonly object _lock = new();

    /// <summary>Reads the existing array (if any), appends the record, writes it back.</summary>
    public static void Append(string pluginDirectory, MapRunRecord record)
    {
        var path = Path.Combine(pluginDirectory, FileName);
        lock (_lock)
        {
            List<MapRunRecord> list = null;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    list = JsonConvert.DeserializeObject<List<MapRunRecord>>(json);
            }

            list ??= new List<MapRunRecord>();
            list.Add(record);
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
    }
}
