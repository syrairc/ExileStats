using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace ExileImGui;

// what a Card.Draw did this frame. fold Changed into your dirty flag; act on Closed to drop the card.
public readonly struct CardResult
{
    public readonly bool Changed;
    public readonly bool Closed;
    public CardResult(bool changed, bool closed) { Changed = changed; Closed = closed; }
}

// bordered, rounded, filled settings "card": a title header over a caller-drawn body, optionally
// collapsible and/or closeable. (Card.List from the PoE1 toolkit is left out - it needs ListEditor.)
public static class Card
{
    // one card. body runs when expanded and returns its own changed flag. Closed is true the frame the
    // x is clicked; the caller decides what to drop.
    public static CardResult Draw(string id, string title, Func<bool> body,
        bool collapsible = false, bool closeable = false, bool defaultOpen = true)
    {
        bool changed = false, closed = false;
        ImGui.PushID(id);

        // inset + rounded, with a subtle raised fill so it reads as a card. no hardcoded colors: the fill
        // borrows FrameBg and the border comes from the theme via the Border child flag.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.FrameBg));
        // AutoResizeY -> height hugs the content; x=0 still fills the available width.
        ImGui.BeginChild("##c", Vector2.Zero, ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY);

        if (DrawHeader(title, collapsible, closeable, defaultOpen, ref closed))
            changed = body();

        ImGui.EndChild();
        ImGui.PopStyleColor(1);
        ImGui.PopStyleVar(2);
        ImGui.PopID();
        return new CardResult(changed, closed);
    }

    // open state per BeginCard, so EndCard knows whether it has body padding to undo. a stack because
    // cards nest in principle; in practice it's one deep.
    static readonly Stack<bool> _open = new();

    // a card whose FIRST ROW is the disclosure: arrow + title + a right-aligned trailing slot, with the
    // body inside the same bordered box, so header and body read as one object.
    //
    // Begin/End contract, same as Windows.Begin: ALWAYS call EndCard, including when this returns false.
    public static bool BeginCard(string id, string title, Action trailing = null, Action menu = null,
        bool? setOpen = null, bool defaultOpen = false)
    {
        ImGui.PushID(id);
        // zero window padding so the framed header fills the card edge to edge; the body gets its own indent.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild("##card", Vector2.Zero, ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY);

        // Framed makes the header a filled bar; SpanFullWidth widens the hit area to the whole card;
        // AllowOverlap keeps the trailing widgets clickable; NoTreePushOnOpen means there's no TreePop to pair.
        var flags = ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.SpanFullWidth
                  | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (defaultOpen) flags |= ImGuiTreeNodeFlags.DefaultOpen;
        if (setOpen.HasValue) ImGui.SetNextItemOpen(setOpen.Value);

        // ###h keys the node on "h" so renaming the card doesn't reset its open state mid-type.
        bool open = ImGui.TreeNodeEx(Text.Ascii(title) + "###h", flags);
        if (menu != null && ImGui.BeginPopupContextItem("##ctx")) { menu(); ImGui.EndPopup(); }
        if (trailing != null)
        {
            Controls.BeginTrailing(id + "_trail", 6f);
            trailing();
            Controls.EndTrailing();
        }

        _open.Push(open);
        if (open) { ImGui.Indent(8f); ImGui.Dummy(new Vector2(0, 2f)); }
        return open;
    }

    public static void EndCard()
    {
        if (_open.Count > 0 && _open.Pop()) { ImGui.Dummy(new Vector2(0, 4f)); ImGui.Unindent(8f); }
        ImGui.EndChild();
        ImGui.PopStyleVar(1);
        ImGui.PopID();
    }

    // draws the header and returns whether the body should draw. sets closed if the x fired this frame.
    static bool DrawHeader(string title, bool collapsible, bool closeable, bool defaultOpen, ref bool closed)
    {
        var t = Text.Ascii(title);
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

        if (collapsible)
        {
            // ###h keys the header on "h" so editing the title doesn't reset the collapse state mid-frame.
            if (closeable)
            {
                bool keep = true;
                bool open = ImGui.CollapsingHeader(t + "###h", ref keep, flags);
                if (!keep) closed = true;
                return open;
            }
            return ImGui.CollapsingHeader(t + "###h", flags);
        }

        // static title: right-align the x, then a rule under it to split header from body.
        ImGui.TextUnformatted(t);
        if (closeable)
        {
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
            if (ImGui.SmallButton("x")) closed = true;
        }
        ImGui.Separator();
        return true;
    }
}
