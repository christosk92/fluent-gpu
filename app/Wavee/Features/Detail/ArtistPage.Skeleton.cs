using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The loading skeleton shown by StatefulRegion while the artist resource resolves.
sealed partial class ArtistPage : Component
{
    // ── loading skeleton ───────────────────────────────────────────────────────────────────────────────────
    static Element Skeleton() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            new BoxEl { Height = 420f, Fill = Tok.FillCardDefault },
            new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.XL,
                Padding = new Edges4(32f, 40f, 32f, WaveeSpace.XXL),
                Children =
                [
                    new BoxEl { Direction = 0, Gap = WaveeSpace.M, Children = [SkelBar(160f, 48f), SkelBar(44f, 44f), SkelBar(110f, 36f)] },
                    SkelRows(5),
                ],
            },
        ],
    };

    static Element SkelBar(float w, float h) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(WaveeRadius.Control), Fill = Tok.FillCardDefault };

    static Element SkelRows(int n)
    {
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
            rows[i] = new BoxEl
            {
                Direction = 0, Height = 56f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                Children = [SkelBar(20f, 14f), SkelBar(40f, 40f), new BoxEl { Grow = 1f, Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }, SkelBar(40f, 12f)],
            };
        return new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = rows };
    }
}
