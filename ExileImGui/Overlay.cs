using System;
using System.Numerics;
using ImGuiNET;
using ExileCore2.Shared.Nodes;
using SColor = System.Drawing.Color;

namespace ExileImGui;

// drag/resize for foreground-drawlist panels (overlays drawn outside an ImGui window). one Overlay per plugin
// tracks which panel is dragged/resized so several panels coexist. set Locked (and optionally MoveHeld/InteractHeld
// from your own held keys) each frame; while Movable, Handle moves the panel + resizes the enabled edges and writes
// back to your Pos/MaxWidth/MaxHeight nodes.
public sealed class Overlay
{
    public bool Locked;
    public bool MoveHeld;       // caller sets each frame: a held move-key (e.g. Alt) makes a locked overlay grabbable
    public bool InteractHeld;   // caller sets each frame: a held interact-key (e.g. Ctrl) arms row clicks
    public bool Movable => !Locked || MoveHeld;

    string _dragId, _resizeId;
    ResizeEdge _resizeEdge;     // which edge the active resize latched onto
    Vector2 _dragOffset;
    const float Edge = 6f;   // right-edge grab band for resize

    [Flags]
    public enum ResizeEdge { None = 0, Right = 1, Bottom = 2, Corner = Right | Bottom }

    // pure hit-test for resize grips. returns the edge band under the mouse, masked to `enabled`.
    // corner wins when the mouse is near both right and bottom AND both are enabled; else the single edge.
    public static ResizeEdge HitEdge(Vector2 mouse, Vector2 origin, Vector2 size, ResizeEdge enabled, float edge = Edge)
    {
        bool inside = mouse.X >= origin.X && mouse.X <= origin.X + size.X
                   && mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        if (!inside) return ResizeEdge.None;
        bool nearR = mouse.X >= origin.X + size.X - edge;
        bool nearB = mouse.Y >= origin.Y + size.Y - edge;
        bool canR = (enabled & ResizeEdge.Right) != 0;
        bool canB = (enabled & ResizeEdge.Bottom) != 0;
        if (nearR && nearB && canR && canB) return ResizeEdge.Corner;
        if (nearR && canR) return ResizeEdge.Right;
        if (nearB && canB) return ResizeEdge.Bottom;
        return ResizeEdge.None;
    }

    // pure hit-test: is the mouse over the rect, and is it in the right resize band.
    public static (bool hovered, bool onEdge) HitTest(Vector2 mouse, Vector2 origin, Vector2 size, float edge = Edge)
    {
        bool hovered = mouse.X >= origin.X && mouse.X <= origin.X + size.X
                    && mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        bool onEdge = hovered && mouse.X >= origin.X + size.X - edge;
        return (hovered, onEdge);
    }

    // top-left anchored. left-drag body to move (writes posX/posY). drag the enabled edges to resize:
    // right -> maxWidth, bottom -> maxHeight, corner -> both. bottom/corner need a non-null maxHeight (a panel
    // with no fixed-height node auto-sizes, so vertical resize is masked off). mutates origin to follow a move.
    public (bool hovered, ResizeEdge edge, bool active) Handle(string id, ref Vector2 origin, Vector2 size,
        RangeNode<int> posX, RangeNode<int> posY, RangeNode<int> maxWidth,
        RangeNode<int> maxHeight = null, ResizeEdge resizable = ResizeEdge.Right)
    {
        if (!Movable && _dragId != id && _resizeId != id) return (false, ResizeEdge.None, false);

        var enabled = resizable;
        if (maxHeight == null) enabled &= ~ResizeEdge.Bottom;   // no height node -> no vertical resize

        var mouse = ImGui.GetMousePos();
        var (hovered, _) = HitTest(mouse, origin, size);
        var edge = HitEdge(mouse, origin, size, enabled);
        var shown = _resizeId == id ? _resizeEdge : edge;
        if (shown == ResizeEdge.Corner) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNWSE);
        else if (shown == ResizeEdge.Right) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        else if (shown == ResizeEdge.Bottom) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);

        if (_dragId == null && _resizeId == null && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (edge != ResizeEdge.None) { _resizeId = id; _resizeEdge = edge; }
            else { _dragId = id; _dragOffset = mouse - origin; }
        }
        if (_dragId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                origin = mouse - _dragOffset;
                posX.Value = Math.Clamp((int)Math.Round(origin.X), posX.Min, posX.Max);
                posY.Value = Math.Clamp((int)Math.Round(origin.Y), posY.Min, posY.Max);
            }
            else _dragId = null;
        }
        if (_resizeId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if ((_resizeEdge & ResizeEdge.Right) != 0)
                    maxWidth.Value = Math.Clamp((int)Math.Round(mouse.X - origin.X), maxWidth.Min, maxWidth.Max);
                if ((_resizeEdge & ResizeEdge.Bottom) != 0 && maxHeight != null)
                    maxHeight.Value = Math.Clamp((int)Math.Round(mouse.Y - origin.Y), maxHeight.Min, maxHeight.Max);
            }
            else _resizeId = null;
        }
        return (hovered, shown, _dragId == id || _resizeId == id);
    }

    public void Release(string id)
    {
        if (_dragId == id) _dragId = null;
        if (_resizeId == id) _resizeId = null;
    }

    // panel chrome: filled background + outline. draw before your content. radius rounds the outer rect only.
    public static void Chrome(ImDrawListPtr dl, Vector2 min, Vector2 max, SColor background, SColor border,
        float thickness = 1f, float radius = 0f)
    {
        if (background.A > 0) dl.AddRectFilled(min, max, EColor.U32(background), radius);
        if (thickness > 0) dl.AddRect(min, max, EColor.U32(border), radius, ImDrawFlags.None, thickness);
    }

    // invisible ImGui window over the panel. only job: be hovered so ImGui sets WantCaptureMouse (ExileCore
    // honours it), swallowing drag clicks before the game sees them. draw before your panel content, while
    // hovered or active.
    public static void ClickBlocker(string id, Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoScrollWithMouse;
        ImGui.Begin($"##eidrag_{id}", flags);
        ImGui.End();
    }

    // outline + edge grip(s) drawn around a movable panel. draw after your content. `edge` is Handle's returned edge.
    public static void DragHint(Vector2 min, Vector2 max, bool active, bool hovered, ResizeEdge edge)
    {
        var hint = (active || hovered)
            ? SColor.FromArgb(220, 120, 220, 255)
            : SColor.FromArgb(120, 120, 220, 255);
        var dl = ImGui.GetForegroundDrawList();
        dl.AddRect(min, max, EColor.U32(hint), 0f, ImDrawFlags.None, 1.5f);
        var grip = EColor.U32(SColor.FromArgb(255, 120, 220, 255));
        if ((edge & ResizeEdge.Right) != 0)
            dl.AddLine(new Vector2(max.X, min.Y), new Vector2(max.X, max.Y), grip, 3f);
        if ((edge & ResizeEdge.Bottom) != 0)
            dl.AddLine(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y), grip, 3f);
    }
}
