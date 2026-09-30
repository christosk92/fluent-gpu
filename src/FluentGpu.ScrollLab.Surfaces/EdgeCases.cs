using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace FluentGpu.ScrollLab.Surfaces;

/// <summary>The three degenerate extents an input path must survive without motion artefacts.</summary>
public enum EdgeCaseKind : byte
{
    /// <summary>Three rows — content far shorter than the viewport (no scroll range at all).</summary>
    ThreeRows = 0,
    /// <summary>Content exactly the viewport's height (a zero scroll range at the boundary: max offset = 0).</summary>
    ExactlyViewport = 1,
    /// <summary>No content.</summary>
    Empty = 2,
}

/// <summary>
/// The edge-case surface (scroll-lab plan §4): a picker over <see cref="EdgeCaseKind"/>, the body remounted per kind
/// (keyed) so the shared <see cref="Handle"/> re-binds to each viewport in turn. A wheel/touchpad/touch gesture on any
/// of them must produce no motion, no overpan beyond policy and no jitter — exactly what the probe records.
/// </summary>
public sealed class EdgeCases : Component
{
    public ScrollHandle Handle = null!;

    static readonly string[] Labels = { "3 rows", "Exactly viewport", "Empty" };

    public static Element Create(ScrollHandle handle) => Embed.Comp(() => new EdgeCases { Handle = handle });

    public override Element Render()
    {
        var kind = UseSignal(0);
        int k = kind.Value;
        var handle = Handle;
        Element body = (EdgeCaseKind)k switch
        {
            EdgeCaseKind.ThreeRows => Rows(3, handle),
            EdgeCaseKind.ExactlyViewport => Embed.Comp(() => new ExactViewport { Handle = handle }),
            _ => Rows(0, handle),
        };
        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            MinHeight = 0f,
            Gap = 8f,
            Children =
            [
                SelectorBar.Create(Labels, kind),
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, Children = [body with { Key = "edge-" + k }] },
            ],
        };
    }

    static Element Rows(int count, ScrollHandle handle)
    {
        int bits = PositionBarcode.BitsFor(Math.Max(count, 2));
        return ItemsView.CreateBound(count, scope => SurfaceRows.TrackRow(scope.Index, bits, FixedList100k.RowHeight),
            RepeatLayout.Stack(FixedList100k.RowHeight),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None,
                Scroll = new ScrollOptions { Handle = handle },
            });
    }

    /// <summary>A scroller whose content is exactly its own measured viewport height (six rows sharing it evenly).</summary>
    sealed class ExactViewport : Component
    {
        public ScrollHandle Handle = null!;
        const int RowCount = 6;

        public override Element Render()
        {
            var bounds = UseMeasuredBounds();
            int bits = PositionBarcode.BitsFor(RowCount);
            var rows = new Element[RowCount];
            for (int i = 0; i < RowCount; i++)
            {
                var index = new FluentGpu.Signals.Signal<int>(i);
                rows[i] = new BoxEl
                {
                    Direction = 0,
                    Grow = 1f,
                    Basis = 0f,
                    MinHeight = 0f,
                    Key = "r" + i,
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, MinWidth = 0f, Padding = new Edges4(12f, 0f, 12f, 0f), AlignItems = FlexAlign.Center,
                            Fill = (i & 1) == 0 ? Tok.FillSubtleSecondary : ColorF.Transparent,
                            Children = [new TextEl(SurfaceRows.Title(i)) { Size = 14f }],
                        },
                        PositionBarcode.Column(index, bits, _ => MathF.Max(1f, bounds.Value.H / RowCount)),
                    ],
                };
            }
            return new ScrollEl
            {
                Grow = 1f,
                MinHeight = 0f,
                Handle = Handle,
                Content = new BoxEl
                {
                    Direction = 1,
                    Height = Prop.Of(() => MathF.Max(0f, bounds.Value.H)),
                    Children = rows,
                },
            };
        }
    }
}
