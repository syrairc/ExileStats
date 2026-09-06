using System.Drawing;
using System.Numerics;

namespace ExileStats;

// the four item / monster rarity colours, in every form the ui needs. change a colour here only
public static class RarityPalette
{
    public static readonly Color White = Color.FromArgb(217, 217, 217);
    public static readonly Color Magic = Color.FromArgb(136, 136, 255);
    public static readonly Color Rare = Color.FromArgb(255, 255, 119);
    public static readonly Color Unique = Color.FromArgb(175, 96, 37);

    public static Color For(string rarity) => rarity switch
    {
        "Unique" => Unique,
        "Rare" => Rare,
        "Magic" => Magic,
        _ => White,
    };

    public static Vector4 Vec(string rarity, float alpha = 1f)
    {
        var c = For(rarity);
        return new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
    }

    public static string Hex(string rarity) => ContentCatalog.ToHex(For(rarity));
}
