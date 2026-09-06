using System;
using System.Collections.Generic;
using System.Numerics;
using ExileImGui;
using ImGuiNET;
using SColor = System.Drawing.Color;

namespace ExileStats;

/// <summary>What the overlay needs to draw, read once per frame by the plugin so this class stays free of
/// game-memory calls.</summary>
internal readonly struct OverlayData
{
    public readonly int Level;
    public readonly double Progress;      // 0..1 through the current level
    public readonly long Remaining;       // xp still needed for the next level
    public readonly RunRates.Result? Rates;
    public readonly bool HasOmen;
    public readonly double DivineRate;    // base currency per divine; 0 = unknown (no NinjaPricer)

    public OverlayData(int level, double progress, long remaining, RunRates.Result? rates, bool hasOmen,
        double divineRate)
    {
        Level = level;
        Progress = progress;
        Remaining = remaining;
        Rates = rates;
        HasOmen = hasOmen;
        DivineRate = divineRate;
    }
}

/// <summary>
/// The on-screen statistics panel: an xp bar plus per-hour / per-map rates, each line individually
/// toggleable. Drawn on the ImGui foreground draw list (not inside a window) and moved/resized with the
/// ExileImGui <see cref="Overlay"/> helper, which writes straight back into the position/width settings.
/// </summary>
internal sealed class StatsOverlay
{
    private const string Id = "exilestats_overlay";
    private readonly Overlay _overlay = new();
    // Label + value are kept apart so the values can share one measured column (see Draw). A null Value is a
    // label-only row that spans, like the warning.
    private readonly List<(string Label, string Value, SColor Color)> _lines = new();

    public void Draw(ExileStatsSettings s, in OverlayData d)
    {
        var scale = Math.Max(0.5f, s.OverlayScale.Value);
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * scale;
        var lineH = fontSize + 3f;
        const float Pad = 6f;

        BuildLines(s, d, out bool showBar);

        float barH = showBar ? fontSize + 6f : 0f;
        float height = Pad * 2 + barH + (showBar && _lines.Count > 0 ? 4f : 0f) + _lines.Count * lineH;
        if (barH <= 0f && _lines.Count == 0)
        {
            _overlay.Release(Id);   // nothing enabled: don't strand a drag latch on this panel
            return;
        }

        var origin = new Vector2(s.OverlayPositionX.Value, s.OverlayPositionY.Value);
        var size = new Vector2(s.OverlayWidth.Value, height);

        _overlay.Locked = s.OverlayLocked.Value;
        var (hovered, edge, active) = _overlay.Handle(Id, ref origin, size,
            s.OverlayPositionX, s.OverlayPositionY, s.OverlayWidth);
        // Swallow the drag click before the game sees it, but only while actually grabbing the panel.
        if (hovered || active)
            Overlay.ClickBlocker(Id, origin, size);

        var dl = ImGui.GetForegroundDrawList();
        var min = origin;
        var max = origin + size;
        Overlay.Chrome(dl, min, max, s.OverlayBgColor.Value, SColor.FromArgb(120, 90, 90, 90), 1f, 4f);

        float y = min.Y + Pad;
        if (showBar)
        {
            DrawXpBar(dl, font, fontSize,
                new Vector2(min.X + Pad, y), new Vector2(max.X - Pad, y + barH), s, d);
            y += barH + (_lines.Count > 0 ? 4f : 0f);
        }

        // Values start at one shared column, measured off the widest label - hand-padded labels only line up
        // in a monospace font, and only if every label is padded to the same length (they weren't).
        float labelW = 0f;
        foreach (var (label, value, _) in _lines)
            if (value != null)
                labelW = Math.Max(labelW, font.CalcTextSizeA(fontSize, float.MaxValue, 0f, label).X);
        float valueX = min.X + Pad + labelW + fontSize * 0.6f;

        foreach (var (label, value, color) in _lines)
        {
            var col = EColor.U32(color);
            dl.AddText(font, fontSize, new Vector2(min.X + Pad, y), col, label);
            if (value != null)
                dl.AddText(font, fontSize, new Vector2(valueX, y), col, value);
            y += lineH;
        }

        if (_overlay.Movable)
            Overlay.DragHint(min, max, active, hovered, edge);
    }

    /// <summary>The xp bar: a framed track filled to the level progress, with "Lv NN  pp.p%" centred on it.</summary>
    private static void DrawXpBar(ImDrawListPtr dl, ImFontPtr font, float fontSize,
        Vector2 min, Vector2 max, ExileStatsSettings s, in OverlayData d)
    {
        var track = SColor.FromArgb(160, 25, 25, 28);
        dl.AddRectFilled(min, max, EColor.U32(track), 3f);

        var fillW = (float)(d.Progress * (max.X - min.X));
        if (fillW > 1f)
            dl.AddRectFilled(min, new Vector2(min.X + fillW, max.Y), EColor.U32(s.OverlayBarColor.Value), 3f);
        dl.AddRect(min, max, EColor.U32(SColor.FromArgb(160, 90, 90, 90)), 3f);

        var label = d.Level >= XpLevels.MaxLevel
            ? $"Lv {d.Level}  max"
            : $"Lv {d.Level}  {d.Progress * 100:0.00}%";
        var ts = font.CalcTextSizeA(fontSize, float.MaxValue, 0f, label);
        var at = new Vector2(min.X + (max.X - min.X - ts.X) * 0.5f, min.Y + (max.Y - min.Y - ts.Y) * 0.5f);
        RichText.Outlined(dl, font, fontSize, at, label, s.OverlayTextColor.Value);
    }

    /// <summary>Rebuilds the enabled text rows. Each is gated by its own setting; a rate with no history
    /// yet prints "--" rather than dropping the row (a row that comes and goes reflows the panel).</summary>
    private void BuildLines(ExileStatsSettings s, in OverlayData d, out bool showBar)
    {
        showBar = s.OverlayShowXpBar.Value;
        _lines.Clear();

        var text = s.OverlayTextColor.Value;
        var r = d.Rates;

        if (s.OverlayShowXpHour)
            _lines.Add(("XP/h", r is { } a ? Big(a.XpPerHour) : "--", text));

        if (s.OverlayShowXpMap)
            _lines.Add(("XP/map", r is { } b ? Big(b.XpPerMap) : "--", text));

        if (s.OverlayShowMapsHour)
            _lines.Add(("Maps/h", r is { } c ? $"{c.MapsPerHour:0.0}" : "--", text));

        // Looted worth (MapRunRecord.PickupValue), in the base currency with the divine equivalent after it.
        // Needs NinjaPricer; unpriced runs just total 0 (and the divine suffix drops off).
        if (s.OverlayShowValueHour)
            _lines.Add(("Value/h",
                r is { } g ? Currency.FormatWithDivine(g.ValuePerHour, d.DivineRate) : "--", text));

        if (s.OverlayShowValueMap)
            _lines.Add(("Value/map",
                r is { } h ? Currency.FormatWithDivine(h.ValuePerMap, d.DivineRate) : "--", text));

        if (s.OverlayShowTimeToLevel)
            _lines.Add(("Next lv", d.Level >= XpLevels.MaxLevel ? "max"
                : r is { XpPerHour: > 0 } e ? Hms(d.Remaining / e.XpPerHour) : "--", text));

        if (s.OverlayShowMapsToLevel)
            _lines.Add(("Maps to lv", d.Level >= XpLevels.MaxLevel ? "max"
                : r is { XpPerMap: > 0 } f ? $"{d.Remaining / f.XpPerMap:0.0}" : "--", text));

        // Omen of Amelioration cuts the map death xp penalty, so it only matters once there's xp worth losing.
        if (s.OverlayShowOmenWarning && !d.HasOmen &&
            d.Level < XpLevels.MaxLevel && d.Progress * 100 >= s.OverlayOmenThreshold.Value)
            _lines.Add(("NO OMEN", null, s.OverlayWarnColor.Value));
    }

    /// <summary>Compact magnitude for xp figures - 1.23B / 45.6M / 789k.</summary>
    public static string Big(double v)
    {
        var abs = Math.Abs(v);
        if (abs >= 1e9) return $"{v / 1e9:0.00}B";
        if (abs >= 1e6) return $"{v / 1e6:0.0}M";
        if (abs >= 1e3) return $"{v / 1e3:0.0}k";
        return $"{v:0}";
    }

    /// <summary>Hours as "3h 12m" / "47m" / "40s". Anything past 99h is unbounded noise, so it caps.</summary>
    public static string Hms(double hours)
    {
        if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0) return "--";
        if (hours > 99) return ">99h";
        int total = (int)Math.Round(hours * 3600);
        int h = total / 3600, m = total % 3600 / 60, sec = total % 60;
        if (h > 0) return $"{h}h {m:00}m";
        if (m > 0) return $"{m}m {sec:00}s";
        return $"{sec}s";
    }
}
