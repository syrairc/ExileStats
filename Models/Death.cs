using System;
using System.Collections.Generic;

namespace ExileStats;

/// <summary>One monster near the player at the moment of death — a likely killer. Sorted nearest-first
/// in <see cref="Death.NearbyMonsters"/>.</summary>
public class NearbyMonster
{
    public string Name { get; set; }
    public string Rarity { get; set; }
    public float Distance { get; set; }
}

/// <summary>
/// A player death recorded in real time while in a map (life edge from &gt;0 to &lt;=0). Stored in the
/// instance's deaths.json. <see cref="ZoneSwitchId"/> ties it to a specific visit; <see cref="NearbyMonsters"/>
/// lists hostiles within the configured range at the instant of death.
/// </summary>
public class Death
{
    public DateTime At { get; set; }
    public double ElapsedSeconds { get; set; }   // since the visit's EnteredAt
    public int ZoneSwitchId { get; set; }

    // Player position on the map grid at death.
    public float GridX { get; set; }
    public float GridY { get; set; }

    public long Xp { get; set; }

    public List<NearbyMonster> NearbyMonsters { get; set; }
}
