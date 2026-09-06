namespace ExileStats;

/// <summary>
/// The one place the display currency rule lives (see "Display conventions" in CLAUDE.md): a value is shown
/// in the base currency until it is worth more than <see cref="DefaultDivThreshold"/> divines, then it flips
/// to divines. Everything priced by NinjaPricer is in the base currency, so that's the input unit.
/// </summary>
public static class Currency
{
    /// <summary>Base currency NinjaPricer prices in - exalted for PoE2.</summary>
    public const string BaseUnit = "ex";

    public const string DivineUnit = "div";

    /// <summary>Flip to divines once a value exceeds this many divines.</summary>
    public const double DefaultDivThreshold = 5;

    // fixed culture so grouping/decimal chars don't shift with the machine locale
    private static readonly System.Globalization.CultureInfo Ci = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>True once <paramref name="value"/> converted at <paramref name="divRate"/> exceeds
    /// <paramref name="divThreshold"/> divines. Always false when <paramref name="divRate"/> is unknown.</summary>
    public static bool UseDivine(double value, double divRate, double divThreshold = DefaultDivThreshold) =>
        divRate > 0 && value / divRate > divThreshold;

    /// <summary>Value + unit, e.g. "420 ex" or "12.5 div". A non-positive <paramref name="divRate"/> (no
    /// NinjaPricer) keeps everything in the base currency.</summary>
    public static string Format(double value, double divRate, double divThreshold = DefaultDivThreshold) =>
        UseDivine(value, divRate, divThreshold) ? Num(value / divRate) + " " + DivineUnit : Num(value) + " " + BaseUnit;

    /// <summary>Base currency with the divine equivalent appended, e.g. "1,240 ex (6.2d)". The suffix is
    /// dropped when <paramref name="divRate"/> is unknown. For readouts that want both units at once rather
    /// than the either/or of <see cref="Format"/>.</summary>
    public static string FormatWithDivine(double value, double divRate)
    {
        var b = Num(value) + " " + BaseUnit;
        return divRate > 0 ? b + " (" + (value / divRate).ToString("0.0", Ci) + "d)" : b;
    }

    /// <summary>Whole numbers above 100, one decimal below - keeps a rate line from jittering on noise.</summary>
    public static string Num(double v) => v >= 100 || v <= -100 ? v.ToString("N0", Ci) : v.ToString("0.#", Ci);
}
