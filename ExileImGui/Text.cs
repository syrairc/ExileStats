using System.Collections.Generic;
using System.Text;
using ImGuiNET;

namespace ExileImGui;

// shared text helpers for the foreground-drawlist widgets (overlays, richtext).
public static class Text
{
    // map common Unicode to ASCII (drop the rest) so text renders in ImGui's ASCII-only default font.
    public static string Ascii(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < 128) { sb.Append(c); continue; }
            switch (c)
            {
                case '–': case '—': case '−': sb.Append('-'); break;   // en/em dash, minus
                case '→': sb.Append("->"); break;
                case '←': sb.Append("<-"); break;
                case '•': case '·': case '●': sb.Append('*'); break;
                case '✓': case '✔': case '×': sb.Append('x'); break;
                case '“': case '”': sb.Append('"'); break;
                case '‘': case '’': sb.Append('\''); break;
                case '…': sb.Append("..."); break;
                default: sb.Append('?'); break;
            }
        }
        return sb.ToString();
    }

    // greedy word wrap. maxWidth is the pixel budget for the text at the given scale.
    public static List<string> Wrap(string text, float maxWidth, float scale)
    {
        var words = (text ?? "").Split(' ');
        var lines = new List<string>();
        var cur = "";
        foreach (var w in words)
        {
            var trial = cur.Length == 0 ? w : cur + " " + w;
            if (cur.Length > 0 && ImGui.CalcTextSize(trial).X * scale > maxWidth)
            {
                lines.Add(cur);
                cur = w;
            }
            else cur = trial;
        }
        if (cur.Length > 0) lines.Add(cur);
        if (lines.Count == 0) lines.Add(text ?? "");
        return lines;
    }
}
