using System.Numerics;
using ImGuiNET;
using SColor = System.Drawing.Color;

namespace ExileImGui;

// text drawn onto a draw list with a background pill or a black outline, plus a bare highlight box.
// these draw to an explicit ImDrawListPtr so they work on the foreground list (overlays) or a window list.
public static class RichText
{
    // 4-corner black outline the game overlays use, so light text stays readable over anything.
    static readonly Vector2[] Outline = { new(-1, -1), new(1, -1), new(-1, 1), new(1, 1) };

    // text with a 1px black outline. font/size let callers match the game font scale.
    public static void Outlined(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, string text, SColor color)
    {
        uint black = ImGui.GetColorU32(new Vector4(0, 0, 0, 1f));
        foreach (var o in Outline) dl.AddText(font, size, pos + o, black, text);
        dl.AddText(font, size, pos, EColor.U32(color), text);
    }

    // rounded background box behind text. returns the outer pill size so callers can advance/layout.
    public static Vector2 Pill(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, string text,
        SColor textColor, SColor bg, float padX = 4f, float padY = 2f, float rounding = 2f)
    {
        var ts = font.CalcTextSizeA(size, float.MaxValue, 0f, text);
        var min = new Vector2(pos.X - padX, pos.Y - padY);
        var max = new Vector2(pos.X + ts.X + padX, pos.Y + ts.Y + padY);
        if (bg.A > 0) dl.AddRectFilled(min, max, EColor.U32(bg), rounding);
        dl.AddText(font, size, pos, EColor.U32(textColor), text);
        return max - min;
    }

    // outline box around a screen rect, e.g. to ring an item slot. no fill.
    public static void Highlight(ImDrawListPtr dl, Vector2 min, Vector2 max, SColor color,
        float thickness = 3f, float rounding = 0f, float expand = 2f)
    {
        var e = new Vector2(expand, expand);
        dl.AddRect(min - e, max + e, EColor.U32(color), rounding, ImDrawFlags.None, thickness);
    }
}
