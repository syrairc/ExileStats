namespace ExileStats;

// Which pre-filtered entity bucket a tracker consumes each tick (see EntityBuckets). Informational —
// trackers pull the bucket they need from the context; lets the registry/profiler describe a tracker.
public enum EntityNeed { None, Monsters, WorldItems, AllValid }

// One tick's work, passed the shared context by ref (no struct copy).
public delegate void TrackerAction(in TrackerContext ctx);

// A per-tick monitor. The dispatcher gates it (area + enabled + interval), times it (profiler), and runs it,
// so adding a monitor is "write a tracker + register it" — no Tick edits and no extra full-entity pass.
public interface ITracker
{
    string Name { get; }
    bool Enabled(ExileStatsSettings s);
    bool RequiresTrackedArea { get; }       // false for NetWorth (the stash opens in town/hideout)
    EntityNeed Needs { get; }
    int IntervalMs(ExileStatsSettings s);   // minimum ms between runs; 0 = every tick
    void Run(in TrackerContext ctx);
}
