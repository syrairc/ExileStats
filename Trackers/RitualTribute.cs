using System.Linq;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.MemoryObjects;

namespace ExileStats;

/// <summary>
/// Reads the live in-map tribute total from the Ritual kill-points HUD widget (bottom-right during a
/// ritual fight). Tribute is <b>not</b> a player/altar stat, and the <c>IngameUi.RitualWindow</c> copy is
/// stale during combat — only this HUD readout updates per kill. The widget is a small container with three
/// children: a <c>Ritual/KillPointsBase.dds</c> background, the numeric text (e.g. "1,127"), and a
/// <c>Ritual/KillPointsIconOverlay.dds</c> overlay. We locate it by the stable <c>Ritual/KillPoints</c>
/// texture-name anchor (element addresses recycle and deep child indices shift across patches/resolutions),
/// then parse the digits of the numeric sibling. Returns null when the widget isn't present/visible (no
/// active ritual) or on any read failure — never throws.
/// </summary>
public static class RitualTribute
{
    private const string TextureAnchor = "Ritual/KillPoints";

    public static int? Read(IngameUIElements ui)
    {
        try
        {
            var root = ui?.Root;
            if (root == null)
                return null;

            // Find the kill-points widget by its texture, then read the numeric sibling text node.
            var anchor = root.FindChildRecursive(e => Has(e?.TextureName, TextureAnchor));
            var parent = anchor?.Parent;
            if (parent?.Children == null)
                return null;

            foreach (var c in parent.Children)
            {
                var txt = c?.Text;
                if (string.IsNullOrEmpty(txt))
                    continue;
                var digits = new string(txt.Where(char.IsDigit).ToArray());   // strip thousands separators
                if (digits.Length > 0 && int.TryParse(digits, out var v))
                    return v;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool Has(string s, string sub) =>
        !string.IsNullOrEmpty(s) && s.Contains(sub);
}
