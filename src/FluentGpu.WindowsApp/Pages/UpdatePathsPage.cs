using System.Collections.Generic;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── "The three update paths" (G8c2 flagship Fundamentals page) ────────────────────────────────────────────────────
// ONE slider drives three consumers, each a self-contained component with a LIVE render counter (UseRef bumped in
// Render). The counters tell the engine's differentiator out loud: the SAME value reaches pixels three ways, and only
// the reactive-read path re-runs a component.
//   (a) Bound channel  — the signal is bound to the Transform channel; a mount-time effect writes just that node's
//       column (compositor-only). The component renders ONCE; the counter never moves as you drag.
//   (b) Reactive read  — the component reads the signal in Render(), so it re-renders on every change (the counter climbs).
//   (c) Flow.For list  — the signal is read inside the list's items thunk; only the keyed list rebinds. The host
//       component renders ONCE (counter frozen) while the row set changes.
[GalleryPage("update-paths", "The three update paths", "Fundamentals", Icon = Icons.Refresh, Level = GalleryLevel.Advanced)]
sealed class UpdatePathsPage : Component
{
    public override Element Render()
    {
        var t = UseFloatSignal(0.35f);

        return GalleryPage.ShellKeyed("update-paths", "The three update paths",
            "A change reaches pixels through one mechanism — a signal — but a signal can drive three very different update " +
            "paths. Drag the slider and watch each card's render counter: only the reactive-read card re-runs a component. " +
            "This is what \"granular, signals-first\" buys you.",
            new BoxEl
            {
                Direction = 1, Gap = 16f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = 8f, Padding = Edges4.All(16), Fill = Tok.FillCardDefault,
                        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center,
                                Children =
                                [
                                    new TextEl("Drive") { Size = 13f, Bold = true, Color = Tok.TextSecondary },
                                    Slider.Create(t, length: 320f),
                                    new TextEl("") { Text = Prop.Of(() => t.Value.ToString("0.00")), Size = 13f, Bold = true, Color = Tok.AccentDefault, FontFamily = "Cascadia Code" },
                                ],
                            },
                            Caption("One FloatSignal, three consumers below.").Secondary(),
                        ],
                    },
                    Embed.Comp(() => new BoundPathCard { T = t }),
                    Embed.Comp(() => new ReactivePathCard { T = t }),
                    Embed.Comp(() => new ListPathCard { T = t }),
                ],
            });
    }
}

file sealed class PathCardChrome
{
    public static Element Card(string title, string badge, ColorF badgeColor, string blurb, int renders, Element demo) => new BoxEl
    {
        Direction = 1, Gap = 10f, Padding = Edges4.All(18), Fill = Tok.FillCardDefault,
        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
                Children =
                [
                    new BoxEl { Grow = 1f, Children = [BodyStrong(title)] },
                    new BoxEl
                    {
                        Padding = new Edges4(8, 2, 8, 2), Corners = Radii.PillAll, Fill = badgeColor,
                        Children = [new TextEl(badge) { Size = 11f, Bold = true, Color = Tok.TextOnAccentPrimary }],
                    },
                    new TextEl($"renders: {renders}") { Size = 12f, Bold = true, Color = Tok.TextPrimary, FontFamily = "Cascadia Code" },
                ],
            },
            Caption(blurb).Secondary(),
            new BoxEl { Direction = 0, Padding = new Edges4(0, 8, 0, 4), Children = [demo] },
        ],
    };
}

// (a) Bound Transform — renders once; the box glides via a compositor-only channel write.
file sealed class BoundPathCard : Component
{
    public FloatSignal T = null!;
    public override Element Render()
    {
        var renders = UseRef(0);
        renders.Value++;
        var demo = new BoxEl
        {
            Width = 380f, Height = 44f, Fill = Tok.FillSubtleSecondary, Corners = Radii.ControlAll, Justify = FlexJustify.Center,
            Children =
            [
                new BoxEl
                {
                    Width = 44f, Height = 44f, Corners = Radii.ControlAll, Fill = Tok.AccentDefault,
                    // Bound to the Transform channel: the thunk's read subscribes a mount-time effect, NOT this render.
                    Transform = Prop.Of(() => Affine2D.Translation(T.Value * 320f, 0f)),
                },
            ],
        };
        return PathCardChrome.Card("(a) Bound channel", "0 re-renders", Tok.SystemFillSuccess,
            "Transform = Prop.Of(() => Affine2D.Translation(t.Value * 320, 0)) — a compositor-only write. The component renders once at mount.",
            renders.Value, demo);
    }
}

// (b) Reactive read — the component reads the signal in Render(), so it re-renders each change.
file sealed class ReactivePathCard : Component
{
    public FloatSignal T = null!;
    public override Element Render()
    {
        var renders = UseRef(0);
        renders.Value++;
        float v = T.Value;   // reading in render() subscribes THIS component
        var demo = new BoxEl
        {
            Direction = 1, Gap = 4f,
            Children =
            [
                new TextEl($"value = {v:0.00}   ({(int)(v * 100)}%)") { Size = 20f, Bold = true, Color = Tok.TextPrimary },
                new TextEl(v < 0.33f ? "low" : v < 0.66f ? "mid" : "high") { Size = 13f, Color = Tok.AccentDefault },
            ],
        };
        return PathCardChrome.Card("(b) Reactive read", "re-renders", Tok.AccentDefault,
            "float v = t.Value in Render() subscribes the whole component — it re-runs on every change (watch the counter climb).",
            renders.Value, demo);
    }
}

// (c) Flow.For list — the signal is read in the items thunk; only the keyed list rebinds, not this component.
file sealed class ListPathCard : Component
{
    public FloatSignal T = null!;
    public override Element Render()
    {
        var renders = UseRef(0);
        renders.Value++;
        var list = Flow.For(
            () => { int n = (int)(T.Value * 24f) + 1; var xs = new List<int>(n); for (int i = 0; i < n; i++) xs.Add(i); return xs; },
            i => i.ToString(),
            (i, _) => new BoxEl { Width = 12f, Height = 36f, Corners = Radii.ControlAll, Fill = Tok.AccentSubtle });
        var demo = new BoxEl { Direction = 0, Gap = 4f, Wrap = true, Children = [list] };
        return PathCardChrome.Card("(c) Flow.For list", "list rebind", Tok.SystemFillCaution,
            "Flow.For reads t.Value in its items thunk — the keyed list adds/removes bars while this component renders once.",
            renders.Value, demo);
    }
}
