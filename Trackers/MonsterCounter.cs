using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace ExileStats;

/// <summary>
/// Counts hostile monsters in the current area. "Seen" accumulates distinct <see cref="Entity.Id"/>
/// values across the area's lifetime (entities stream in as you explore), so it answers "how many
/// monsters are in this map"; reset on area change since Ids are per-area. "Alive" is the current-frame
/// count. Player summons (non-hostile) and temporary daemons (<see cref="DiesAfterTime"/>) are excluded.
/// Also tracks a per-type breakdown and the names of unique monsters seen.
/// </summary>
public class MonsterCounter
{
    public static readonly MonsterRarity[] Rarities =
        [MonsterRarity.White, MonsterRarity.Magic, MonsterRarity.Rare, MonsterRarity.Unique];

    private readonly HashSet<long> _seenIds = new();          // global dedup by Entity.Id
    private readonly Dictionary<MonsterRarity, int> _seenByRarity = new();
    private readonly Dictionary<MonsterRarity, int> _alive = new();
    private readonly Dictionary<string, int> _seenByType = new();   // metadata key -> distinct count
    private readonly HashSet<string> _uniqueNames = new();
    private int _aliveTotal;   // cached sum of _alive (off the per-frame draw path)

    // Position logging (piggybacks the first-sight pass; no extra entity scan).
    private readonly HashSet<string> _positionSeen = new();           // fingerprint dedup across visits
    private readonly List<MonsterSighting> _newSightings = new();      // buffer drained to disk in batches
    private bool _logPositions;
    private int _minRarityIndex;        // index into Rarities (0=White .. 3=Unique)
    private bool _detailed;
    private int _zoneSwitchId;          // current visit; stamped onto new sightings

    public MonsterCounter()
    {
        foreach (var r in Rarities)
        {
            _seenByRarity[r] = 0;
            _alive[r] = 0;
        }
    }

    public void Reset()
    {
        _seenIds.Clear();
        _seenByType.Clear();
        _uniqueNames.Clear();
        _positionSeen.Clear();
        _newSightings.Clear();
        _aliveTotal = 0;
        foreach (var r in Rarities)
        {
            _seenByRarity[r] = 0;
            _alive[r] = 0;
        }
    }

    /// <summary>Configure position logging for the instance being entered and seed the fingerprint
    /// dedup-set from already-logged monsters so re-entry / restart doesn't re-log. Mirrors
    /// <see cref="LootTracker.SetArea"/>.</summary>
    public void SetArea(IEnumerable<string> knownFingerprints, int zoneSwitchId, bool logPositions,
        int minRarityIndex, bool detailed)
    {
        _positionSeen.Clear();
        _newSightings.Clear();
        _zoneSwitchId = zoneSwitchId;
        _logPositions = logPositions;
        _minRarityIndex = minRarityIndex;
        _detailed = detailed;
        if (knownFingerprints != null)
            foreach (var fp in knownFingerprints)
                _positionSeen.Add(fp);
    }

    public int BufferedCount => _newSightings.Count;

    /// <summary>Returns the buffered new sightings and clears the buffer (drained to disk by the caller).</summary>
    public List<MonsterSighting> DrainSightings()
    {
        var drained = new List<MonsterSighting>(_newSightings);
        _newSightings.Clear();
        return drained;
    }

    public void Update(IEnumerable<Entity> entities, double elapsedSeconds)
    {
        foreach (var r in Rarities)
            _alive[r] = 0;

        if (entities == null)
            return;

        foreach (var e in entities)
        {
            if (e is not { Type: EntityType.Monster, IsHostile: true })
                continue;
            if (e.HasComponent<DiesAfterTime>())
                continue;
            if (!_seenByRarity.ContainsKey(e.Rarity))
                continue; // only the four real rarities

            if (_seenIds.Add(e.Id)) // first time we've seen this entity this area
            {
                _seenByRarity[e.Rarity]++;

                var key = TypeKey(e.Path);
                if (key.Length > 0)
                    _seenByType[key] = _seenByType.GetValueOrDefault(key) + 1;

                if (e.Rarity == MonsterRarity.Unique)
                {
                    var name = e.RenderName;
                    if (!string.IsNullOrEmpty(name))
                        _uniqueNames.Add(name.Split(',')[0]);
                }

                // Log this monster's first-seen position (deduped by fingerprint across visits).
                if (_logPositions && Array.IndexOf(Rarities, e.Rarity) >= _minRarityIndex)
                {
                    var p = e.GridPos;
                    var fp = MonsterSighting.MakeFingerprint(e.Path, p.X, p.Y);
                    if (_positionSeen.Add(fp))
                    {
                        var s = new MonsterSighting
                        {
                            Rarity = e.Rarity.ToString(),
                            GridX = p.X,
                            GridY = p.Y,
                            FirstSeenAt = DateTime.Now,
                            ElapsedSeconds = Math.Round(elapsedSeconds, 1),
                            ZoneSwitchId = _zoneSwitchId,
                            Fingerprint = fp,
                        };
                        if (_detailed)
                        {
                            s.TypeKey = key.Length > 0 ? key : null;
                            s.Name = string.IsNullOrEmpty(e.RenderName) ? null : e.RenderName.Split(',')[0];
                            s.Path = e.Path;
                        }
                        _newSightings.Add(s);
                    }
                }
            }

            if (e.IsAlive)
                _alive[e.Rarity]++;
        }

        _aliveTotal = _alive[MonsterRarity.White] + _alive[MonsterRarity.Magic]
                      + _alive[MonsterRarity.Rare] + _alive[MonsterRarity.Unique];
    }

    // Compacts a metadata path into a type key: drops the "Metadata/Monsters/" prefix and the
    // "@<level>" variant suffix so the same monster at different levels merges.
    private static string TypeKey(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        const string prefix = "Metadata/Monsters/";
        if (path.StartsWith(prefix, StringComparison.Ordinal))
            path = path[prefix.Length..];
        var at = path.IndexOf('@');
        return at >= 0 ? path[..at] : path;
    }

    public int Seen(MonsterRarity r) => _seenByRarity.GetValueOrDefault(r);
    public int Alive(MonsterRarity r) => _alive.GetValueOrDefault(r);
    public int SeenTotal => _seenIds.Count;
    public int AliveTotal => _aliveTotal;

    public Dictionary<string, int> SeenByTypeSnapshot() => new(_seenByType);
    public List<string> UniqueNamesSnapshot() => _uniqueNames.ToList();
}
