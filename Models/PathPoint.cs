namespace ExileStats;

/// <summary>
/// One high-rate player-position sample for drawing an accurate map path. Deliberately compact (short
/// field names) because these are logged many times per second: <see cref="T"/> = elapsed seconds since the
/// visit's EnteredAt, <see cref="X"/>/<see cref="Y"/> = player <c>GridPos</c>, <see cref="Z"/> = ZoneSwitchId
/// (which visit). Appended to the instance's path.json, separate from the richer (but sparse) snapshots.
/// </summary>
public class PathPoint
{
    public double T { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public int Z { get; set; }
}
