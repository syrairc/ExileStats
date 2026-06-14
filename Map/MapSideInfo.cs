using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore2;
using ExileCore2.PoEMemory;

namespace ExileStats;

/// <summary>One map-content icon from the Map Content panel (e.g. Name "Checkpoint"). <see cref="Completed"/>
/// reflects whether the content's check mark (child <c>MapObjectiveCheck.dds</c>) is currently visible;
/// <see cref="Objective"/> is the readable tooltip line (e.g. "Activate all Checkpoints").</summary>
public class MapContentEntry
{
    public string Name { get; set; }
    public string Texture { get; set; }
    public bool Completed { get; set; }
    public string Objective { get; set; }
}

/// <summary>
/// Reads the per-map "Map Objectives" and "Map Content" panel
/// (<c>IngameState.IngameUi.MapSideUI</c>). Walks the element subtree once and classifies each node:
/// content icons by their <c>AtlasIconContent&lt;Name&gt;.dds</c> texture (only <see cref="Element.IsVisible"/>
/// ones — there's a hidden all-icons template node), objectives by their readable text. Index-independent
/// so it survives the panel's layout shifting between map types.
/// </summary>
public static class MapSideInfo
{
    private const string ContentTextureMarker = "AtlasIconContent";
    private const string ContentPrefix = "AtlasIconContent";
    private const string CheckMarker = "MapObjectiveCheck";

    // Objective bullet lines look like "<<bullet>> [RareMonsterMapDrop|Kill all Rare Monsters]".
    private static readonly Regex ObjectiveLabel = new(@"\[[^\[\]\|]*\|([^\[\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<<[^>]*>>", RegexOptions.Compiled);
    private static readonly Regex HasLatin = new("[A-Za-z]", RegexOptions.Compiled);

    private static readonly HashSet<string> Headers = new() { "Map Objectives", "Map Content" };

    public static (List<string> Objectives, List<MapContentEntry> Content) Read(GameController gc)
    {
        var objectives = new List<string>();
        var content = new List<MapContentEntry>();

        var root = gc?.IngameState?.IngameUi?.MapSideUI;
        if (root is { IsVisible: true })
            Walk(root, objectives, content);

        return (objectives.Distinct().ToList(), content);
    }

    private static void Walk(Element e, List<string> objectives, List<MapContentEntry> content)
    {
        // Prune invisible subtrees: skips the sibling QuestTracker (campaign quest text) and the hidden
        // all-icons content template. Chain visibility means a visible node can't sit under an invisible
        // one, so we never miss live content this way.
        if (e is not { IsVisible: true })
            return;

        var tex = e.TextureName;
        if (!string.IsNullOrEmpty(tex) && tex.Contains(ContentTextureMarker))
            content.Add(new MapContentEntry
            {
                Name = ContentName(tex),
                Texture = tex,
                Completed = HasVisibleCheck(e),
                Objective = CleanObjective(e.Tooltip?.TextNoTags),
            });

        var label = ObjectiveText(e.Text);
        if (label != null)
            objectives.Add(label);

        var children = e.Children;
        if (children != null)
            foreach (var c in children)
                Walk(c, objectives, content);
    }

    // "Art/.../AtlasIconContentCheckpoint.dds" -> "Checkpoint".
    private static string ContentName(string texture)
    {
        var file = texture[(texture.LastIndexOf('/') + 1)..];
        if (file.EndsWith(".dds")) file = file[..^4];
        if (file.StartsWith(ContentPrefix)) file = file[ContentPrefix.Length..];
        return file;
    }

    // True if any node in the icon's subtree is a *visible* check-mark (MapObjectiveCheck.dds), which the
    // game shows when that content is complete. Walks the full subtree (not visibility-pruned) so we can
    // read the check's own IsVisible directly.
    private static bool HasVisibleCheck(Element e)
    {
        if (e == null)
            return false;
        var tex = e.TextureName;
        if (e.IsVisible && !string.IsNullOrEmpty(tex) && tex.Contains(CheckMarker))
            return true;
        var children = e.Children;
        if (children != null)
            foreach (var c in children)
                if (HasVisibleCheck(c))
                    return true;
        return false;
    }

    // Strips bullet/markup tags and pulls the readable label out of a "[key|Label]" form; returns null for
    // empty input. Used for tooltip objective text on content icons.
    private static string CleanObjective(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = ObjectiveLabel.Match(text);
        var label = (m.Success ? m.Groups[1].Value : Tags.Replace(text, "")).Trim();
        return label.Length == 0 ? null : label;
    }

    // Returns the cleaned objective label, or null if the text isn't a real objective line
    // (header, empty, or garbage from an uninitialised text pointer).
    private static string ObjectiveText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !HasLatin.IsMatch(text))
            return null;

        var m = ObjectiveLabel.Match(text);
        var label = (m.Success ? m.Groups[1].Value : Tags.Replace(text, "")).Trim();
        if (label.Length == 0 || Headers.Contains(label))
            return null;
        return label;
    }
}
