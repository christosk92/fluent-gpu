using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.GalleryKit;

/// <summary>
/// The WinUI-Gallery "ControlExample" card, graduated into GalleryKit (was <c>ControlExample</c> in the app). A
/// BodyStrong header (+ optional description), a bordered example area holding the live control with an optional
/// right-side options/output panel, and an optional collapsible source-code panel (the theme-aware
/// <see cref="CodeBlock"/>, horizontally scrollable, copy-to-clipboard).
///
/// <para>Two ways to use it: <see cref="Build"/> — the classic literal form (a live element + a hand-written
/// <c>code</c> string; kept for the ~100 pages migrating incrementally in G8b), and <see cref="Show"/> — the
/// never-drift form driven by a generated <see cref="Sample"/> (the code shown IS the compiled sample body, and the
/// example mounts with a live <see cref="Knobs"/> panel).</para>
/// </summary>
public static class ExampleCard
{
    /// <summary>The classic literal card: a live <paramref name="example"/> element + an optional hand-written
    /// <paramref name="code"/> string. A factory (not a component) because the live example must refresh every render —
    /// component props are mount-only, which would freeze the demo.</summary>
    public static Element Build(string title, Element example, string? description = null,
        Element? options = null, Element? output = null, string? code = null, FlexAlign exampleAlign = FlexAlign.Start)
        => Card(title, description, example, options, output, code, exampleAlign);

    /// <summary>The never-drift card: mount a generated <see cref="Sample"/> — the live example (with an interactive
    /// <see cref="Knobs"/> panel) over its verbatim source, wired to a copy affordance.</summary>
    public static Element Show(Sample sample) => Embed.Comp(() => new SampleCard { Sample = sample });

    // Shared card assembly (used by both Build and SampleCard).
    internal static Element Card(string title, string? description, Element example,
        Element? options, Element? output, string? code, FlexAlign exampleAlign)
    {
        var display = new BoxEl { Grow = 1, Padding = Spacing.CardPad, Direction = 0, AlignItems = exampleAlign, Children = [example] };

        Element row = (options is null && output is null)
            ? display
            : new BoxEl { Direction = 0, AlignItems = FlexAlign.Stretch, Children = [display, Divider(vertical: true), OptionsPanel(options, output)] };

        var exampleArea = new BoxEl
        {
            Direction = 1, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            Corners = code is null ? Radii.OverlayAll : new CornerRadius4(Radii.Overlay, Radii.Overlay, 0f, 0f),
            Children = [row],
        };

        var cardKids = new List<Element> { exampleArea };
        if (code is not null)
        {
            var c = code;
            cardKids.Add(Embed.Comp(() => new Expander
            {
                Header = "Source code",
                Content = SourceContent(c),
                Parts = new() { [Expander.PartContent] = p => p with { Padding = Edges4.All(0) } },
            }));
        }

        var card = new BoxEl
        {
            Direction = 1, Corners = Radii.OverlayAll, ClipToBounds = true,
            Children = cardKids.ToArray(),
        };

        var outer = new List<Element> { BodyStrong(title) };
        if (description is not null) outer.Add(Body(description).Secondary());
        outer.Add(card);
        return new BoxEl { Direction = 1, Gap = 8, Margin = new Edges4(0, 0, 0, 12), Children = outer.ToArray() };
    }

    // The right-side column: an "Output" readout (when present) over the framework-control knob rows under an "Options"
    // header (the WinUI-Gallery property panel). Rendered only when there is something to show (the caller passes null →
    // the card collapses to just the example area), so it is inherently collapsible-when-empty.
    private static Element OptionsPanel(Element? options, Element? output)
    {
        var kids = new List<Element>();
        if (output is not null) { kids.Add(Caption("Output").Tertiary()); kids.Add(output); }
        if (options is not null)
        {
            if (output is not null) kids.Add(new BoxEl { Height = 4f });
            kids.Add(Caption("Options").Tertiary());
            kids.Add(options);
        }
        return new BoxEl { Direction = 1, Gap = Spacing.StackM, Padding = Spacing.CardPad, MinWidth = 240, Children = kids.ToArray() };
    }

    // The source expander body: a "C#" + copy toolbar over the theme-aware CodeBlock (copyable:false — the toolbar owns copy).
    private static Element SourceContent(string code) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8, Padding = new Edges4(16, 8, 8, 0),
                Children =
                [
                    Caption("C#").Tertiary(),
                    new BoxEl { Grow = 1 },
                    Embed.Comp(() => new CopyButton { Text = code, Label = "Copy" }),
                ],
            },
            CodeBlock.Of(code),
        ],
    };
}

/// <summary>The component behind <see cref="ExampleCard.Show"/>: owns one <see cref="Knobs"/> for the example's
/// lifetime, mounts the live example (which registers its knobs while building), and renders the knobs panel beside it
/// over the verbatim source.</summary>
internal sealed class SampleCard : Component
{
    public Sample Sample = null!;

    public override Element Render()
    {
        var knobsRef = UseRef<Knobs?>(null);
        knobsRef.Value ??= new Knobs();
        var knobs = knobsRef.Value!;

        // Building the example registers the knobs (cached by label — stable across re-renders), so BuildPanel below
        // sees them. options is null when the sample declares no knobs (a plain live example, no side panel).
        var example = Sample.Factory(knobs);
        Element? options = knobs.HasAny ? knobs.BuildPanel() : null;

        return ExampleCard.Card(Sample.Title, Sample.Description, example, options, output: null, code: Sample.Code, exampleAlign: FlexAlign.Start);
    }
}
