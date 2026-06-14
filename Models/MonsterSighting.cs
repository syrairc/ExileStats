using System;
using Newtonsoft.Json;

namespace ExileStats;

/// <summary>
/// One distinct monster's first-seen position in a map instance. Logged once per monster, deduped by
/// <see cref="Fingerprint"/> (monster path + rounded grid position) so re-entering the same instance does
/// not re-log it. Appended to the instance's monsters.json. The rich fields are written only when the
/// "detailed" toggle is on (otherwise null + omitted), keeping slim rows small.
/// </summary>
public class MonsterSighting
{
    // Slim (always written)
    public string Rarity { get; set; }
    public float GridX { get; set; }
    public float GridY { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public double ElapsedSeconds { get; set; }
    public int ZoneSwitchId { get; set; }
    public string Fingerprint { get; set; }

    // Rich (only when detailed; omitted from JSON when null)
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string TypeKey { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string Name { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string Path { get; set; }

    // Fingerprint: monster path + rounded grid position (position-stable across re-entry).
    public static string MakeFingerprint(string path, float x, float y)
        => $"{path}@{(int)Math.Round(x)}:{(int)Math.Round(y)}";
}
