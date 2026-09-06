using ExileCore2;
using ExileCore2.PoEMemory;

namespace ExileStats;

/// <summary>
/// Reads a UI element's texture path straight out of client memory, because
/// <c>ExileCore2.PoEMemory.Element.TextureName</c> is <b>broken against PoE2 0.5.5</b> - it returns "" or
/// the raw pointer rendered as UTF-16 mojibake, for every element. Anything that located an element by
/// texture (the ritual tribute HUD anchor, the map side panel's content icons) silently matched nothing.
///
/// Chain: <c>Element +0x240</c> -> texture holder, <c>+0x8</c> -> wide string. The string is the art path
/// with the atlas sub-rect and a flag appended, pipe separated, so it is cut at the first '|':
/// <c>Art/Textures/.../KillPointsBase.dds|0,0,208,8c|0</c>. A texture-less element reads 0 at +0x240.
///
/// Offset is the client's, not the framework's, so this keeps working if TextureName is ever fixed
/// upstream. Verified in ExileCore2/RE/element-texture-name.md (0.5.5, confirmed live) and already used
/// this way by the ExileMaps plugin. Never throws.
/// </summary>
public static class ElementTexture
{
    private const int TextureRef = 0x240;

    public static string Of(Element e, GameController gc)
    {
        try
        {
            var addr = e?.Address ?? 0;
            if (addr == 0 || gc == null)
                return null;

            var mem = gc.Memory;
            var slot = mem.Read<long>(addr + TextureRef);
            if (slot == 0)
                return null;                      // element has no texture

            var str = mem.Read<long>(slot + 8);
            if (str == 0)
                return null;

            var raw = mem.ReadStringU(str, 512);
            if (string.IsNullOrEmpty(raw))
                return null;

            var bar = raw.IndexOf('|');
            return bar > 0 ? raw[..bar] : raw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the element's texture path contains <paramref name="sub"/>.</summary>
    public static bool Has(Element e, GameController gc, string sub)
    {
        var t = Of(e, gc);
        return !string.IsNullOrEmpty(t) && t.Contains(sub);
    }
}
