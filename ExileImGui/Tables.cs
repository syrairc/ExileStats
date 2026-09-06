using System;
using ImGuiNET;

namespace ExileImGui;

// thin helpers for the compact settings-table pattern: fixed/stretch columns, a header row, and a per-row
// scope that handles the two things everyone forgets - PushID per row and the themed selected-row background.
public static class Tables
{
    // width > 0 => fixed px column, width <= 0 => stretch. draws the header row unless showHeader is false.
    // returns false when the table is clipped; skip the body and DON'T call End in that case.
    public static bool Begin(string id, (string name, float width)[] cols, bool showHeader = true,
        ImGuiTableFlags flags = ImGuiTableFlags.RowBg)
    {
        if (!ImGui.BeginTable(id, cols.Length, flags)) return false;
        foreach (var (name, width) in cols)
            ImGui.TableSetupColumn(name,
                width > 0 ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch,
                width > 0 ? width : 0f);
        if (showHeader) ImGui.TableHeadersRow();
        return true;
    }

    public static void End() => ImGui.EndTable();

    // PushID + TableNextRow + themed selection bg. dispose pops the id. use with `using`, then TableNextColumn per cell.
    public static RowScope Row(int id, bool selected = false) => new(id, selected);

    public readonly struct RowScope : IDisposable
    {
        public RowScope(int id, bool selected)
        {
            ImGui.PushID(id);
            ImGui.TableNextRow();
            if (selected) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.Header));
        }
        public void Dispose() => ImGui.PopID();
    }

    // dim a row's contents while keeping them interactive (unlike BeginDisabled). use with `using`.
    public static DimScope Dim(bool on) => new(on);

    public readonly struct DimScope : IDisposable
    {
        readonly bool _on;
        public DimScope(bool on)
        {
            _on = on;
            if (on) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.55f);
        }
        public void Dispose() { if (_on) ImGui.PopStyleVar(); }
    }
}
