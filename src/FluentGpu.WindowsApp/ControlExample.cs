using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

/// <summary>
/// The WinUI Gallery "ControlExample" pattern as a stateless factory: a BodyStrong header (+ optional description), a
/// bordered example card holding the live control with an optional right-side options/output panel, and an optional
/// source-code panel. A factory (not a component) because the live <c>example</c> must refresh every render — component
/// props are mount-only, which would freeze the demo.
/// </summary>
static class ControlExample
{
    public static Element Build(string title, Element example, string? description = null,
        Element? options = null, Element? output = null, string? code = null, FlexAlign exampleAlign = FlexAlign.Start)
    {
        var display = new BoxEl { Grow = 1, Padding = Spacing.CardPad, Direction = 0, AlignItems = exampleAlign, Children = [example] };

        Element row = (options is null && output is null)
            ? display
            : new BoxEl { Direction = 0, AlignItems = FlexAlign.Stretch, Children = [display, Divider(vertical: true), OptionsPanel(options, output)] };

        var cardKids = new List<Element> { row };
        if (code is not null) cardKids.Add(SourcePanel(code));

        var card = new BoxEl
        {
            Direction = 1, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            Fill = Tok.FillSolidBase, ClipToBounds = true,   // content stays within the card chrome (overlays/flyouts use the top-level layer, unaffected)
            Children = cardKids.ToArray(),   // flat card (WinUI ControlExample has no drop shadow)
        };

        var outer = new List<Element> { BodyStrong(title) };
        if (description is not null) outer.Add(Body(description).Secondary());
        outer.Add(card);
        return new BoxEl { Direction = 1, Gap = 8, Margin = new Edges4(0, 0, 0, 12), Children = outer.ToArray() };
    }

    static Element OptionsPanel(Element? options, Element? output)
    {
        var kids = new List<Element>();
        if (output is not null) { kids.Add(Caption("Output").Tertiary()); kids.Add(output); }
        if (options is not null) kids.Add(options);
        return new BoxEl { Direction = 1, Gap = Spacing.StackM, Padding = Spacing.CardPad, MinWidth = 220, Children = kids.ToArray() };
    }

    static Element SourcePanel(string code) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            Divider(),
            new BoxEl
            {
                Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Padding = new Edges4(16, 10, 16, 10), Fill = Tok.FillCardSecondary,
                Children = [Icon(Icons.Code, 13f).Foreground(Tok.TextSecondary), Body("Source code").Secondary()],
            },
            new BoxEl
            {
                Padding = new Edges4(16, 4, 16, 16),
                Children = [new TextEl(code) { Size = 13f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" }],
            },
        ],
    };
}
