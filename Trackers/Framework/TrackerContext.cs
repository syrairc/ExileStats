using ExileCore2;

namespace ExileStats;

/// <summary>
/// Everything a tracker needs for one tick, bundled so the dispatcher signature never grows as trackers are
/// added. Passed by <c>in</c> to avoid copying.
/// </summary>
public readonly struct TrackerContext
{
    public readonly ExileStats Plugin;
    public readonly GameController Gc;
    public readonly EntityBuckets Buckets;
    public readonly MapRunRecord Area;          // current area record (may be null when not in a tracked area)
    public readonly double ElapsedSeconds;      // since area entry (unrounded)

    public TrackerContext(ExileStats plugin, GameController gc, EntityBuckets buckets,
        MapRunRecord area, double elapsedSeconds)
    {
        Plugin = plugin;
        Gc = gc;
        Buckets = buckets;
        Area = area;
        ElapsedSeconds = elapsedSeconds;
    }
}
