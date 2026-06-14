using System.Collections.Generic;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace ExileStats;

/// <summary>
/// Pre-filtered per-tick entity views, built once by the dispatcher and shared by every tracker so adding a
/// tracker never adds another full-entity pass. Single-type buckets (<see cref="Monsters"/>,
/// <see cref="WorldItems"/>) come straight from <c>EntityListWrapper.ValidEntitiesByType</c> — a valid-only
/// per-frame index the core already maintains, so they're free dictionary lookups, not scans.
/// <see cref="AllValid"/> (every valid entity, for multi-type trackers like content) is materialized lazily
/// and cached, so it's built at most once per tick and only when something actually needs it.
/// </summary>
public sealed class EntityBuckets
{
    private static readonly List<Entity> Empty = new();
    private readonly GameController _gc;
    private IReadOnlyList<Entity> _allValid;

    public EntityBuckets(GameController gc) => _gc = gc;

    private IReadOnlyList<Entity> ByType(EntityType t)
    {
        var map = _gc?.EntityListWrapper?.ValidEntitiesByType;
        return map != null && map.TryGetValue(t, out var list) ? list : Empty;
    }

    public IReadOnlyList<Entity> Monsters => ByType(EntityType.Monster);
    public IReadOnlyList<Entity> WorldItems => ByType(EntityType.WorldItem);

    public IReadOnlyList<Entity> AllValid
    {
        get
        {
            if (_allValid != null)
                return _allValid;
            var valid = _gc?.EntityListWrapper?.OnlyValidEntities;
            return _allValid = valid == null
                ? Empty
                : valid as IReadOnlyList<Entity> ?? new List<Entity>(valid);
        }
    }
}
