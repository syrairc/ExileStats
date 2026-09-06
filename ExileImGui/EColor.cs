using System;
using System.Numerics;
using ImGuiNET;
using SColor = System.Drawing.Color;

namespace ExileImGui;

// ported from the PoE1 ExileImGui toolkit; SharpDX.Color -> System.Drawing.Color for ExileCore2.
public static class EColor
{
    public static Vector4 ToVector4(SColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    public static SColor FromVector4(Vector4 v) => SColor.FromArgb(
        (byte)Math.Clamp(v.W * 255f, 0, 255),
        (byte)Math.Clamp(v.X * 255f, 0, 255),
        (byte)Math.Clamp(v.Y * 255f, 0, 255),
        (byte)Math.Clamp(v.Z * 255f, 0, 255));

    public static uint U32(SColor c) => ImGui.GetColorU32(ToVector4(c));

    public static SColor Fade(SColor c, float a) =>
        SColor.FromArgb((byte)Math.Clamp(a * 255f, 0, 255), c.R, c.G, c.B);

    // multiply rgb, keep alpha. >1 lightens, <1 darkens - for hover/press shades of a given fill.
    public static SColor Scale(SColor c, float f) => SColor.FromArgb(
        c.A,
        (byte)Math.Clamp(c.R * f, 0, 255),
        (byte)Math.Clamp(c.G * f, 0, 255),
        (byte)Math.Clamp(c.B * f, 0, 255));

    // drop all saturation, keep the brightness and alpha. same luma as Contrast uses.
    public static SColor Desaturate(SColor c)
    {
        byte l = (byte)Math.Clamp(c.R * 0.299f + c.G * 0.587f + c.B * 0.114f, 0, 255);
        return SColor.FromArgb(c.A, l, l, l);
    }

    // readable text color for a fill. perceptual luma, threshold picked so mid greens go black.
    public static SColor Contrast(SColor bg) =>
        bg.R * 0.299f + bg.G * 0.587f + bg.B * 0.114f > 150f ? SColor.Black : SColor.White;

    // stable across processes. string.GetHashCode is per-process randomized, don't use it here.
    public static SColor CategoryColor(string s)
    {
        uint h = 2166136261u;
        foreach (char ch in s ?? "")
        {
            h ^= ch;
            h *= 16777619u;
        }
        ImGui.ColorConvertHSVtoRGB((h % 360u) / 360f, 0.55f, 0.95f, out float r, out float g, out float b);
        return SColor.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    // push N style colors, pop them on dispose. use with `using`.
    public readonly struct StyleColorScope : IDisposable
    {
        readonly int _n;
        public StyleColorScope(params (ImGuiCol col, uint rgba)[] colors)
        {
            _n = colors.Length;
            foreach (var (col, rgba) in colors) ImGui.PushStyleColor(col, rgba);
        }
        public void Dispose() => ImGui.PopStyleColor(_n);
    }
}
