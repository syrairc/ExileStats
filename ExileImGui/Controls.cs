using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using ExileCore2.Shared.Nodes;
using SColor = System.Drawing.Color;

namespace ExileImGui;

// ported subset of the PoE1 ExileImGui Controls. The sprite-sheet / icon-picker / button-group section is
// left out - it needs SpriteHelper + GridAtlas and nothing here draws icons.
public static class Controls
{
    // escape % so printf-style imgui text funcs don't eat mod text. null safe.
    public static string EscPct(string s) => s?.Replace("%", "%%") ?? "";

    // hover tooltip for the item just drawn. no-op on empty text.
    public static void Tip(string tip)
    {
        if (!string.IsNullOrEmpty(tip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(Text.Ascii(tip));
    }

    // toggle: ref / node / get-set
    public static bool Toggle(string label, string id, ref bool v) =>
        ImGui.Checkbox(Text.Ascii(label) + "##" + id, ref v);

    public static bool Toggle(string label, string id, ToggleNode n)
    {
        bool v = n.Value;
        if (Toggle(label, id, ref v)) { n.Value = v; return true; }
        return false;
    }

    public static bool Toggle(string label, string id, Func<bool> get, Action<bool> set)
    {
        bool v = get();
        if (Toggle(label, id, ref v)) { set(v); return true; }
        return false;
    }

    // slider int
    public static bool SliderInt(string label, string id, ref int v, int min, int max) =>
        ImGui.SliderInt(Text.Ascii(label) + "##" + id, ref v, min, max);

    public static bool SliderInt(string label, string id, RangeNode<int> n)
    {
        int v = n.Value;
        if (SliderInt(label, id, ref v, n.Min, n.Max)) { n.Value = v; return true; }
        return false;
    }

    public static bool SliderInt(string label, string id, Func<int> get, Action<int> set, int min, int max)
    {
        int v = get();
        if (SliderInt(label, id, ref v, min, max)) { set(v); return true; }
        return false;
    }

    // slider float
    public static bool SliderFloat(string label, string id, ref float v, float min, float max, string fmt = "%.1f") =>
        ImGui.SliderFloat(Text.Ascii(label) + "##" + id, ref v, min, max, fmt);

    public static bool SliderFloat(string label, string id, RangeNode<float> n, string fmt = "%.1f")
    {
        float v = n.Value;
        if (SliderFloat(label, id, ref v, n.Min, n.Max, fmt)) { n.Value = v; return true; }
        return false;
    }

    public static bool SliderFloat(string label, string id, Func<float> get, Action<float> set,
        float min, float max, string fmt = "%.1f")
    {
        float v = get();
        if (SliderFloat(label, id, ref v, min, max, fmt)) { set(v); return true; }
        return false;
    }

    // compact color: swatch + optional alpha bar, no raw rgba spinners
    public static bool Color(string label, string id, ref SColor c, bool alpha = true)
    {
        Vector4 v = EColor.ToVector4(c);
        var flags = ImGuiColorEditFlags.NoInputs |
                    (alpha ? ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf
                           : ImGuiColorEditFlags.NoAlpha);
        if (ImGui.ColorEdit4(Text.Ascii(label) + "##" + id, ref v, flags)) { c = EColor.FromVector4(v); return true; }
        return false;
    }

    public static bool Color(string label, string id, ColorNode n, bool alpha = true)
    {
        SColor c = n.Value;
        if (Color(label, id, ref c, alpha)) { n.Value = c; return true; }
        return false;
    }

    public static bool Color(string label, string id, Func<SColor> get, Action<SColor> set, bool alpha = true)
    {
        SColor c = get();
        if (Color(label, id, ref c, alpha)) { set(c); return true; }
        return false;
    }

    public static bool EnumCombo<T>(string id, ref T value, float width = 120f) where T : struct, Enum
    {
        string[] names = Enum.GetNames(typeof(T));
        var vals = (T[])Enum.GetValues(typeof(T));
        int cur = Array.IndexOf(vals, value);
        if (cur < 0) cur = 0;
        ImGui.SetNextItemWidth(width);
        if (ImGui.Combo("##" + id, ref cur, names, names.Length)) { value = vals[cur]; return true; }
        return false;
    }

    public static bool Category(string label, string id) =>
        ImGui.CollapsingHeader(Text.Ascii(label) + "##" + id, ImGuiTreeNodeFlags.DefaultOpen);

    // generic n-button segmented picker. active tinted with theme accent so it tracks the user theme.
    public static bool Segmented<T>(string id, ref T value, params (string label, T val)[] opts) where T : struct, Enum
    {
        bool changed = false;
        ImGui.PushID(id);
        uint accent = ImGui.GetColorU32(ImGuiCol.Header);
        // accent and the plain button fill sit close together in some themes, so the losers get dimmed too
        uint dim = ImGui.GetColorU32(ImGuiCol.Button, 0.35f);
        uint dimText = ImGui.GetColorU32(ImGuiCol.Text, 0.55f);
        for (int i = 0; i < opts.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 1);
            bool active = EqualityComparer<T>.Default.Equals(value, opts[i].val);
            ImGui.PushStyleColor(ImGuiCol.Button, active ? accent : dim);
            if (!active) ImGui.PushStyleColor(ImGuiCol.Text, dimText);
            if (ImGui.Button(Text.Ascii(opts[i].label))) { value = opts[i].val; changed = true; }
            ImGui.PopStyleColor(active ? 1 : 2);
        }
        ImGui.PopID();
        return changed;
    }

    // [checkbox][control greyed when off] label. folds the control's change with the toggle.
    public static bool OverrideRow(string id, string label, ref bool ovr, Func<bool> control)
    {
        ImGui.PushID(id);
        bool changed = ImGui.Checkbox("##o", ref ovr);
        ImGui.SameLine();
        if (!ovr) ImGui.BeginDisabled();
        changed |= control();
        if (!ovr) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted(Text.Ascii(label));
        ImGui.PopID();
        return changed;
    }

    // ---- grids ----
    // one cell of a ToggleGrid. Get/Set instead of a ref because the values usually live on an object.
    public struct ToggleItem
    {
        public string Label;
        public string Tip;
        public Func<bool> Get;
        public Action<bool> Set;
        public ToggleItem(string label, Func<bool> get, Action<bool> set, string tip = null)
        { Label = label; Get = get; Set = set; Tip = tip; }

        public ToggleItem(string label, ToggleNode n, string tip = null)
        { Label = label; Get = () => n.Value; Set = v => n.Value = v; Tip = tip; }
    }

    // same shape for a color cell.
    public struct ColorItem
    {
        public string Label;
        public Func<SColor> Get;
        public Action<SColor> Set;
        public ColorItem(string label, Func<SColor> get, Action<SColor> set) { Label = label; Get = get; Set = set; }
    }

    // column width for a set of labels: the widest one plus a framed widget and its gaps. a checkbox and a
    // compact color swatch are both one frame-height square, so this measures either.
    public static float LabelStride(params string[] labels)
    {
        float w = 0f;
        foreach (var l in labels) w = Math.Max(w, ImGui.CalcTextSize(Text.Ascii(l)).X);
        return w + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X * 2;
    }

    // how many `stride`-wide columns fit in `avail`. pure so the reflow math is testable. always at least 1.
    public static int FitColumns(float avail, float stride, int max = 0)
    {
        if (stride <= 0f) return 1;
        int n = (int)Math.Floor(avail / stride);
        if (n < 1) n = 1;
        if (max > 0 && n > max) n = max;
        return n;
    }

    // generic n-up grid: you draw the cell, this places it. columns < 1 measures the space available and
    // reflows as the window resizes. NOT a table: a table with no explicit size fills whatever width its
    // parent offers, which deadlocks inside a column that is itself measuring its content.
    public static bool Grid(string id, int count, float stride, Func<int, bool> cell, int columns = 0)
    {
        if (count < 1 || cell == null) return false;
        if (columns < 1) columns = FitColumns(ImGui.GetContentRegionAvail().X, stride);

        bool d = false;
        float x0 = ImGui.GetCursorPosX();   // cell-local origin: inside a table this isn't 0
        ImGui.PushID(id);
        for (int i = 0; i < count; i++)
        {
            int col = i % columns;
            if (col > 0) ImGui.SameLine(x0 + col * stride);
            ImGui.PushID(i);
            d |= cell(i);
            ImGui.PopID();
        }
        ImGui.PopID();
        return d;
    }

    // labels off an item array, for the Grid stride
    static float ItemStride<T>(T[] items, Func<T, string> label)
    {
        var labels = new string[items.Length];
        for (int i = 0; i < items.Length; i++) labels[i] = label(items[i]);
        return LabelStride(labels);
    }

    // n-up checkboxes on a stride measured off the widest label. columns < 1 fits what the width allows.
    public static bool ToggleGrid(string id, ToggleItem[] items, int columns = 2)
    {
        if (items == null || items.Length == 0) return false;
        return Grid(id, items.Length, ItemStride(items, it => it.Label), i =>
        {
            bool v = items[i].Get();
            bool changed = Toggle(items[i].Label, "t", ref v);
            if (changed) items[i].Set(v);
            Tip(items[i].Tip);
            return changed;
        }, columns);
    }

    // the same grid of compact color swatches. defaults to auto-fit.
    public static bool ColorGrid(string id, ColorItem[] items, int columns = 0, bool alpha = true)
    {
        if (items == null || items.Length == 0) return false;
        return Grid(id, items.Length, ItemStride(items, it => it.Label), i =>
        {
            SColor c = items[i].Get();
            if (Color(items[i].Label, "c", ref c, alpha)) { items[i].Set(c); return true; }
            return false;
        }, columns);
    }

    // ---- trailing group: imgui has no right-align ----
    // measure the group once, place it the next frame. width is cached per id, so a group only jumps on the
    // first frame it exists. not nestable - one open group at a time.
    static readonly Dictionary<string, float> _trailW = new();
    static string _trailId;

    public static void BeginTrailing(string id, float pad = 0f)
    {
        float w = _trailW.TryGetValue(id, out var x) ? x : 0f;
        // cursor + avail is the right edge of whatever we're in, so this also lands correctly inside a
        // table cell (GetContentRegionMax would give the whole window and overshoot).
        float left = ImGui.GetCursorPosX();
        float right = left + ImGui.GetContentRegionAvail().X;
        ImGui.SameLine(0, 0);
        ImGui.SetCursorPosX(Math.Max(left, right - w - pad));
        ImGui.BeginGroup();
        _trailId = id;
    }

    public static void EndTrailing()
    {
        ImGui.EndGroup();
        if (_trailId != null) _trailW[_trailId] = ImGui.GetItemRectSize().X;   // remembered for next frame
        _trailId = null;
    }

    // nudge the next item down so a short widget sits centred against the framed controls on the same row.
    public static void AlignMid(float itemHeight) =>
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0f, (ImGui.GetFrameHeight() - itemHeight) * 0.5f));

    // ---- icon toggle: a square latch button instead of a labelled checkbox ----
    public static bool IconToggle(string glyph, string id, ref bool v, string tooltip = null, float width = 22f)
    {
        bool changed = false;
        uint fill = ImGui.GetColorU32(v ? ImGuiCol.Header : ImGuiCol.FrameBg);
        using (new EColor.StyleColorScope((ImGuiCol.Button, fill)))
            if (ImGui.Button(Text.Ascii(glyph) + "##" + id, new Vector2(width, 0)))
            {
                v = !v;
                changed = true;
            }
        Tip(tooltip);
        return changed;
    }

    public static bool IconToggle(string glyph, string id, ToggleNode n, string tooltip = null, float width = 22f)
    {
        bool v = n.Value;
        if (IconToggle(glyph, id, ref v, tooltip, width)) { n.Value = v; return true; }
        return false;
    }

    // style-vars only, no colors, so the user theme colors survive. use with `using`.
    public readonly struct PanelStyleScope : IDisposable
    {
        public PanelStyleScope()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 3));
        }
        public void Dispose() => ImGui.PopStyleVar(5);
    }
}
