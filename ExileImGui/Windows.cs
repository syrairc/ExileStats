using System;
using System.Numerics;
using ImGuiNET;

namespace ExileImGui;

public static class Windows
{
    // Begin/End contract: only call when you intend to show the window. if Begin returns false
    // (collapsed) skip the body but STILL call End(). this is the imgui gotcha.
    public static bool Begin(string title, string id, ref bool open, ImGuiWindowFlags flags = 0, Vector2? firstSize = null)
    {
        if (firstSize.HasValue) ImGui.SetNextWindowSize(firstSize.Value, ImGuiCond.FirstUseEver);
        return ImGui.Begin(title + "##" + id, ref open, flags);
    }

    public static void End() => ImGui.End();

    // tab bar that OR-folds each tab body's changed flag into the return (dirty rollup)
    public static bool TabBar(string id, params (string label, Func<bool> body)[] tabs)
    {
        bool changed = false;
        if (ImGui.BeginTabBar("##" + id))
        {
            foreach (var (label, body) in tabs)
            {
                if (ImGui.BeginTabItem(Text.Ascii(label)))
                {
                    changed |= body();
                    ImGui.EndTabItem();
                }
            }
            ImGui.EndTabBar();
        }
        return changed;
    }

    public static void MasterDetail(string id, float leftWidth, Action left, Action right)
    {
        ImGui.PushID(id);
        ImGui.BeginChild("##l", new Vector2(leftWidth, 0), ImGuiChildFlags.Border);
        left();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##r", new Vector2(0, 0), ImGuiChildFlags.Border);
        right();
        ImGui.EndChild();
        ImGui.PopID();
    }

    // two panels side by side, stacked once the pane narrows past minWidth - 2 columns of 130px is worse
    // than one of 260. the left column measures its own content (WidthFixed with no width), the right one
    // takes the rest. careful: anything in the left column that sizes to "available width" comes out a few
    // pixels wide, give those an explicit width.
    public static bool TwoColumn(string id, float minWidth, Func<bool> left, Func<bool> right)
    {
        if (ImGui.GetContentRegionAvail().X < minWidth)
        {
            bool stacked = left();
            ImGui.Separator();
            return right() || stacked;
        }

        if (!ImGui.BeginTable("##" + id, 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
            return false;
        ImGui.TableSetupColumn("l", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("r", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        bool d = left();
        ImGui.TableNextColumn();
        d = right() || d;
        ImGui.EndTable();
        return d;
    }

    // open with ImGui.OpenPopup(id) first. returns true the frame a button was pressed; confirmed tells which.
    public static bool ConfirmModal(string id, string message, out bool confirmed)
    {
        confirmed = false;
        bool acted = false;
        // imgui.net 1.90 has no BeginPopupModal(id, flags) overload, so we pass a throwaway open flag.
        bool open = true;
        if (ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            using (new EColor.StyleColorScope((ImGuiCol.Text, 0xFF0000FFu))) // red = 0xAABBGGRR
                ImGui.TextUnformatted(Text.Ascii(message));
            if (ImGui.Button("Confirm")) { confirmed = true; acted = true; ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { acted = true; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        return acted;
    }
}
