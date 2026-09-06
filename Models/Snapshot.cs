using System;
using System.Collections.Generic;

namespace ExileStats;

/// <summary>Per-snapshot completion state of one map-content type (e.g. Checkpoint completed at this time).</summary>
public class SnapshotContent
{
    public string Name { get; set; }
    public bool Completed { get; set; }
}

/// <summary>One active buff on the player at a snapshot moment. <see cref="Timer"/>/<see cref="MaxTime"/>
/// are null for permanent buffs (the game reports them as infinity).</summary>
public class SnapshotBuff
{
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public int Charges { get; set; }
    public int Stacks { get; set; }
    public float? Timer { get; set; }
    public float? MaxTime { get; set; }
}

/// <summary>
/// A periodic point-in-time sample taken while in a map: player position, vitals, account totals, and a
/// monster count. Appended to the instance's snapshots.json (a time-series). <see cref="ZoneSwitchId"/>
/// ties a row to a specific visit when the same instance is entered more than once.
/// </summary>
public class Snapshot
{
    public DateTime At { get; set; }
    public double ElapsedSeconds { get; set; }   // since this visit started, not the instance's first EnteredAt
    public int ZoneSwitchId { get; set; }

    // Player position on the map grid
    public float GridX { get; set; }
    public float GridY { get; set; }

    // Account-wide totals
    public long Xp { get; set; }
    public long Gold { get; set; }

    // Vitals (current / max)
    public int Life { get; set; }
    public int MaxLife { get; set; }
    public int EnergyShield { get; set; }
    public int MaxEnergyShield { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }

    // Monster counts: cumulative distinct seen this run, and currently alive this frame
    public int MonstersSeen { get; set; }
    public int MonstersAlive { get; set; }

    // Map-content completion at this moment (best-effort; empty if the side panel wasn't readable).
    public List<SnapshotContent> Content { get; set; }

    // Full player stat sheet (GameStat name -> value); null when SnapshotStats is off.
    public Dictionary<string, int> Stats { get; set; }

    // Active player buffs at this moment; null when SnapshotBuffs is off.
    public List<SnapshotBuff> Buffs { get; set; }
}
