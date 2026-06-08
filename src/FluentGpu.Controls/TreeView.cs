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
/// chevron; each level adds a fixed indent. Expansion state is keyed by the node's positional path so
/// distinct branches toggle independently.</summary>
public sealed class TreeView : Component
{
    public IReadOnlyList<TreeNode> Roots = [];

    public static Element Create(IReadOnlyList<TreeNode> roots)
        => Embed.Comp(() => new TreeView { Roots = roots });

    public override Element Render()
    {
        var (expanded, setExpanded) = UseState(ImmutableHashSet<string>.Empty);

        var rows = new List<Element>();

        void Walk(TreeNode n, int depth, string path)
        {
            bool hasKids = n.Children.Length > 0;
            bool open = expanded.Contains(path);
            rows.Add(Row(n.Label, depth, hasKids, open,
                () => setExpanded(open ? expanded.Remove(path) : expanded.Add(path))));
            if (hasKids && open)
                for (int i = 0; i < n.Children.Length; i++)
                    Walk(n.Children[i], depth + 1, path + "/" + i);
        }

        for (int i = 0; i < Roots.Count; i++)
            Walk(Roots[i], 0, i.ToString());

        return new BoxEl { Direction = 1, Children = rows.ToArray() };
    }

    static Element Row(string label, int depth, bool hasKids, bool open, System.Action onToggle)
        => new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 4f,
            MinHeight = 32f,
            Padding = new Edges4(8 + depth * 16, 0, 8, 0),
            Corners = Radii.ControlAll,
            Margin = new Edges4(4, 1, 4, 1),
            HoverFill = Tok.FillSubtleSecondary,
            OnClick = onToggle,
            Role = AutomationRole.Button,
            Children =
            [
                hasKids
                    ? new TextEl(open ? Icons.ChevronDown : "")
                        { Size = 10f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }
                    : new BoxEl { Width = 10f },
                new TextEl(label) { Size = 14f, Color = Tok.TextPrimary },
            ],
        };
}
