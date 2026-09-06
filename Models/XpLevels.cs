namespace ExileStats;

// Cumulative experience required to reach each character level, straight from the PoE2 game data
// (ExperienceLevels.datc64). Index 0 = level 1 (0 xp), index 99 = level 100. ExileCore2 exposes no
// experience table, so it is baked in here.
public static class XpLevels
{
    public const int MaxLevel = 100;

    private static readonly long[] Cumulative =
    [
        0L, 525L, 1760L, 3781L, 7184L, 12186L, 19324L, 29377L,
        43181L, 61693L, 85990L, 117506L, 157384L, 207736L, 269997L, 346462L,
        439268L, 551295L, 685171L, 843709L, 1030734L, 1249629L, 1504995L, 1800847L,
        2142652L, 2535122L, 2984677L, 3496798L, 4080655L, 4742836L, 5490247L, 6334393L,
        7283446L, 8348398L, 9541110L, 10874351L, 12361842L, 14018289L, 15859432L, 17905634L,
        20171471L, 22679999L, 25456123L, 28517857L, 31897771L, 35621447L, 39721017L, 44225461L,
        49176560L, 54607467L, 60565335L, 67094245L, 74247659L, 82075627L, 90631041L, 99984974L,
        110197515L, 121340161L, 133497202L, 146749362L, 161191120L, 176922628L, 194049893L, 212684946L,
        232956711L, 255001620L, 278952403L, 304972236L, 333233648L, 363906163L, 397194041L, 433312945L,
        472476370L, 514937180L, 560961898L, 610815862L, 664824416L, 723298169L, 786612664L, 855129128L,
        929261318L, 1009443795L, 1096169525L, 1189918242L, 1291270350L, 1400795257L, 1519130326L, 1646943474L,
        1784977296L, 1934009687L, 2094900291L, 2268549086L, 2455921256L, 2658074992L, 2876116901L, 3111280300L,
        3364828162L, 3638186694L, 3932818530L, 4250334444L,
    ];

    // Total xp needed to have reached this level. Clamped to the table.
    public static long TotalFor(int level) => Cumulative[System.Math.Clamp(level, 1, MaxLevel) - 1];

    // Xp span of the level a character is currently on. 0 at max level.
    public static long SpanFor(int level) =>
        level >= MaxLevel ? 0 : TotalFor(level + 1) - TotalFor(level);

    // How far into the current level, as 0..1. 0 at max level (nothing left to fill).
    public static double Progress(int level, long xp)
    {
        var span = SpanFor(level);
        if (span <= 0) return 0;
        var into = xp - TotalFor(level);
        return System.Math.Clamp(into / (double)span, 0, 1);
    }

    // Xp still needed to hit the next level. 0 at max level.
    public static long Remaining(int level, long xp)
    {
        if (level >= MaxLevel) return 0;
        return System.Math.Max(0, TotalFor(level + 1) - xp);
    }
}
