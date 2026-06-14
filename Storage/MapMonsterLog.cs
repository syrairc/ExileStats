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

    // Run grouping: a run spans from leaving a town/hideout until returning to one. Every area visited in
    // between (the map plus any sub-areas — Abyssal Depths, boss arenas, …) shares one RunId, so the viewer
    // can present the map + its sub-areas as a single run. Value = the ZoneSwitchId of the run's first area
    // (the map). 0 = ungrouped (pre-RunId data, or logged outside a known run).
    public int RunId { get; set; }

    // WorldArea (the template / stable map identity). AreaId e.g. "MapReservoir".
    public string AreaId { get; set; }
    public int Index { get; set; }
    public int AreaLevel { get; set; }
    public long WorldAreaId { get; set; }
    public bool IsUnique { get; set; }

    // Area grid dimensions (GameController.IngameState.Data.AreaDimensions). Saved per instance so the
    // map view can map grid coords -> map.png pixels: pixel = grid * (pngDim / AreaDim).
    public int AreaWidth { get; set; }
    public int AreaHeight { get; set; }

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

    // Total NinjaPricer chaos value of items seen on the ground / picked up this visit (0 if NinjaPricer
    // wasn't pricing). Summed at log time from loot.json / pickups.json filtered to this ZoneSwitchId.
    public double LootValue { get; set; }
    public double PickupValue { get; set; }

    // Relative path of the Radar map PNG saved for this run (e.g. "maps/MapReservoir_123_...png"), if any.
    public string MapImageFile { get; set; }

    // Auto-classified walkable-geometry layout archetype (snake_case, matches layouts.json) + confidence
    // 0..1, computed from map.svg at log time by LayoutClassifier. Null/0 if no svg was captured this run.
    public string LayoutType { get; set; }
    public double LayoutConfidence { get; set; }

    // Map exploration: fraction of the walkable region (from map.svg terrain loops) within the map-REVEAL
    // radius of the travelled path (terrain you actually uncovered). Null when no svg was captured this run
    // (sub-delay exit / no forked Radar). RevealRadiusUsed = the reveal R applied (Settings.MapRevealRadius).
    public double? ExploredPercent { get; set; }
    // Raw coverage cell counts behind ExploredPercent (MapCoverage, CellSize=5) at the reveal radius. Null
    // on no-coverage runs.
    public int? ExploredWalkable { get; set; }
    public int? TotalWalkable { get; set; }
    public float RevealRadiusUsed { get; set; }
    // Density area: explored walkable cells within the MONSTER-reveal radius of the path (the wider corridor
    // in which monsters are actually seen). Density = MonstersTotal ÷ (DensityWalkable × CellSize² ÷ 1e6).
    // MonsterRadiusUsed = the monster-reveal R applied. Null = no coverage.
    public int? DensityWalkable { get; set; }
    public float MonsterRadiusUsed { get; set; }

    // Map side-panel: cleaned objective labels (e.g. "Kill all Rare Monsters") and the content icons
    // present this map (e.g. Checkpoint / Ritual / Breach). Read from MapSideUI while in the map.
    public List<string> MapObjectives { get; set; }
    public List<MapContentEntry> MapContent { get; set; }

    // True if this area is an atlas map (AreaId starts with "Map"). Not persisted.
    [JsonIgnore] public bool IsMapArea { get; set; }
}

/// <summary>Appends <see cref="MapRunRecord"/>s to the per-instance run.json (a JSON array; one record
/// per visit to the instance) under <c>maps/&lt;AreaId&gt;_&lt;InstanceHash&gt;/</c>. See
/// <see cref="InstanceStore"/> for the layout.</summary>
public static class MapMonsterLog
{
    private static readonly object _lock = new();

    /// <summary>Reads the instance's run.json (if any), appends this visit's record, writes it back.</summary>
    public static void Append(string pluginDirectory, string areaId, long instanceHash, MapRunRecord record)
    {
        InstanceStore.EnsureFolder(pluginDirectory, areaId, instanceHash);
        var path = InstanceStore.FilePath(pluginDirectory, areaId, instanceHash, InstanceStore.RunFile);
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
