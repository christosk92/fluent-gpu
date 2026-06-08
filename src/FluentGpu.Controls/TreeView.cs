using System.Collections.Generic;
using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>One node in a <see cref="TreeView"/>: a label plus zero or more child nodes.</summary>
public sealed record TreeNode(string Label, params TreeNode[] Children);

/// <summary>A WinUI TreeView: a hierarchical, expandable list. Nodes with children get an expand/collapse
/// chevron (always visible: right when collapsed, down when expanded); each level adds a fixed indent.
/// Expansion state is keyed by the node's positional path so distinct branches toggle independently. The
/// selected row gets a NEUTRAL <see cref="Tok.FillSubtleSecondary"/> fill plus a 3px accent left indicator
/// bar as the only accent cue.</summary>
public sealed class TreeView : Component
{
    public IReadOnlyList<TreeNode> Roots = [];

    public static Element Create(IReadOnlyList<TreeNode> roots)
        => Embed.Comp(() => new TreeView { Roots = roots });

    public override Element Render()
    {
        var (expanded, setExpanded) = UseState(ImmutableHashSet<string>.Empty);
        var (selected, setSelected) = UseState<string?>(null);

        var rows = new List<Element>();

        void Walk(TreeNode n, int depth, string path)
        {
            bool hasKids = n.Children.Length > 0;
            bool open = expanded.Contains(path);
            string p = path;
            rows.Add(Row(n.Label, depth, hasKids, open, selected == p, () =>
            {
                setSelected(p);
                if (hasKids) setExpanded(open ? expanded.Remove(p) : expanded.Add(p));
            }));
            if (hasKids && open)
                for (int i = 0; i < n.Children.Length; i++)
                    Walk(n.Children[i], depth + 1, path + "/" + i);
        }

        for (int i = 0; i < Roots.Count; i++)
            Walk(Roots[i], 0, i.ToString());

        return new BoxEl { Direction = 1, Children = rows.ToArray() };
    }

    static Element Row(string label, int depth, bool hasKids, bool open, bool isSelected, System.Action onClick)
    {
        // 3x16 accent left indicator bar — only on the selected row, vertically centered. WinUI TreeViewItem
        // SelectionIndicator is a Rectangle with RadiusX/Y = 2 (a gentle round, NOT a full pill).
        var accentBar = new BoxEl
        {
            Width = 3f, Height = 16f,
            Corners = CornerRadius4.All(2f),
            Fill = Tok.AccentDefault,
            AlignSelf = FlexAlign.Center,
        };

        var inner = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 4f,
            Grow = 1f,
            Padding = new Edges4(8 + depth * 16, 0, 8, 0),
            Children =
            [
                // Chevron always shows for parent nodes (collapsed = right, expanded = down); leaves reserve the slot.
                hasKids
                    ? new TextEl(open ? Icons.ChevronDown : Icons.ChevronRight)
                        { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextPrimary }   // TreeViewItemForeground = TextPrimary (Pressed → TextSecondary)
                    : new BoxEl { Width = 12f },
                new TextEl(label) { Size = 14f, Color = Tok.TextPrimary },
            ],
        };

        return new BoxEl
        {
            ZStack = true,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 28f,
            Corners = Radii.ControlAll,
            Margin = new Edges4(4, 2, 4, 2),                 // WinUI TreeViewItemPresenterMargin = 4,2
            Fill = isSelected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,            // WinUI Pressed / SelectedPressed → SubtleFillColorTertiary
            OnClick = onClick,
            Role = AutomationRole.Button,
            Children = isSelected ? [accentBar, inner] : [inner],
        };
    }
}
