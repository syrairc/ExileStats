namespace ExileStats;

/// <summary>
/// One Expedition runestone reward line — either a computed-possible reward (from the runestone's runes +
/// area level, <see cref="ContentSighting.RewardPool"/>) or an actual offered option the game presented after
/// the runestone activated (<see cref="ContentSighting.OfferedRewards"/>). Mirrors the data
/// <c>Expedition2Good</c> reads off an <c>Expedition2Recipe</c>.
/// </summary>
public class ExpeditionReward
{
    public string Name { get; set; }    // Recipe.Description ?? Reward.BaseName
    public int Count { get; set; }      // Recipe.RewardCount
    public double? Value { get; set; }  // whole-stack exalted (perUnit * Count); null if NinjaPricer absent/unpriced
}
