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
        foreach (var r in Rarities)
        {
            _seenByRarity[r] = 0;
            _alive[r] = 0;
        }
    }

    public void Update(IEnumerable<Entity> entities)
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
            }

            if (e.IsAlive)
                _alive[e.Rarity]++;
        }
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
    public int AliveTotal => Rarities.Sum(Alive);

    public Dictionary<string, int> SeenByTypeSnapshot() => new(_seenByType);
    public List<string> UniqueNamesSnapshot() => _uniqueNames.ToList();
}
