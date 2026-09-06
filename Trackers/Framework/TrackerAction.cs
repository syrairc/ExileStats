namespace ExileStats;

// One tick's work, passed the shared context by ref (no struct copy).
public delegate void TrackerAction(in TrackerContext ctx);
