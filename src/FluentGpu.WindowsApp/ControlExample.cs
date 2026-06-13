using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

/// <summary>
/// The WinUI Gallery "ControlExample" pattern as a stateless factory: a BodyStrong header (+ optional description), a
/// bordered example card holding the live control with an optional right-side options/output panel, and an optional
/// collapsible source-code panel (syntax-tinted, horizontally scrollable, copy-to-clipboard). A factory (not a
/// component) because the live <c>example</c> must refresh every render — component props are mount-only, which would
/// freeze the demo.
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

        // ONLY the example display area carries the solid base fill (WinUI ControlExampleDisplayBrush =
        // SolidBackgroundFillColorBase). The source expander below it keeps its own TRANSLUCENT card fills over the
        // Mica page — painting the whole card solid is what flattens the layered look.
        var exampleArea = new BoxEl
        {
            Direction = 1, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            Corners = code is null ? Radii.OverlayAll : new CornerRadius4(Radii.Overlay, Radii.Overlay, 0f, 0f),
            Children = [row],
        };

        var cardKids = new List<Element> { exampleArea };
        if (code is not null)
        {
            // The WinUI Gallery SampleCodePresenter shape: the REAL Expander control ("Source code", collapsed by
            // default) with its content panel restyled to 0 padding via the generic template-parts door — the same
            // way the WinUI gallery overrides ExpanderContentPadding. Same control as the Expander demo page.
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
            Direction = 1, Corners = Radii.OverlayAll, ClipToBounds = true,   // outer silhouette only — no fill/border (WinUI's root grid is pure layout)
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

    // The expander's content: a language toolbar ("C#" + copy, the WinUI tab-strip/copy-button row) over the code.
    // NO background of its own — the Expander content's translucent CardBackgroundFillColorSecondary shows through,
    // exactly like WinUI's SampleCodePresenter.
    static Element SourceContent(string code) => new BoxEl
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
            CodeText.Block(code),
        ],
    };
}

/// <summary>
/// A minimal C# syntax tinter for sample snippets (VS-dark palette). Cold path only — runs when a source panel is
/// expanded, never per frame. Whitespace renders as fixed-width spacers so indentation survives text measurement.
/// </summary>
static class CodeText
{
    static readonly ColorF Plain = ColorF.FromRgba(0xD4, 0xD4, 0xD4);
    static readonly ColorF Kw = ColorF.FromRgba(0x56, 0x9C, 0xD6);
    static readonly ColorF Str = ColorF.FromRgba(0xCE, 0x91, 0x78);
    static readonly ColorF Com = ColorF.FromRgba(0x6A, 0x99, 0x55);
    static readonly ColorF Num = ColorF.FromRgba(0xB5, 0xCE, 0xA8);
    static readonly ColorF Typ = ColorF.FromRgba(0x4E, 0xC9, 0xB0);
    static readonly ColorF Meth = ColorF.FromRgba(0xDC, 0xDC, 0xAA);

    const float FontSize = 12.5f;
    const float SpaceW = 7.5f;   // Cascadia Code advance ≈ 0.602 em at 12.5px

    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "using", "static", "var", "new", "true", "false", "null", "return", "void", "int", "float", "double",
        "string", "bool", "char", "sealed", "class", "record", "struct", "public", "private", "internal", "override",
        "readonly", "const", "out", "ref", "in", "is", "not", "and", "or", "if", "else", "switch", "case", "default",
        "for", "foreach", "while", "do", "break", "continue", "this", "base", "with", "get", "set", "init", "params",
        "async", "await", "nameof", "typeof",
    };

    const float LineH = 18f;   // ~12.5px Cascadia line box (generous, so descenders never clip)

    public static Element Block(string code)
    {
        // Horizontally scrollable so long lines are READABLE instead of clipped at the card edge. A horizontal ScrollEl
        // has no intrinsic height in a column, so we give it an explicit viewport height computed from the line count
        // (the row heights + gaps + padding); the inner column keeps its natural overflowing width for the pan.
        var lines = code.Replace("\r\n", "\n").Trim('\n').Split('\n');
        var rows = new Element[lines.Length];
        float h = 8f + 14f;                       // padding top + bottom
        for (int i = 0; i < lines.Length; i++)
        {
            rows[i] = Line(lines[i]);
            h += lines[i].Trim().Length == 0 ? 8f : LineH;
            if (i > 0) h += 3f;                   // inter-row gap
        }
        var content = new BoxEl { Direction = 1, Gap = 3f, Padding = new Edges4(16, 8, 16, 14), Children = rows };
        // Horizontal viewport with a fixed height (the column's cross-axis stretch fills the card width). Height is
        // explicit because a horizontal ScrollEl has no intrinsic height in a column.
        return new ScrollEl { Horizontal = true, Height = h, Content = content };
    }

    static Element Line(string line)
    {
        if (line.Trim().Length == 0) return new BoxEl { Height = 8f };

        var runs = new List<Element>();
        int i = 0, n = line.Length;
        while (i < n)
        {
            char c = line[i];
            if (c == ' ')
            {
                int s = i;
                while (i < n && line[i] == ' ') i++;
                runs.Add(new BoxEl { Width = (i - s) * SpaceW });
                continue;
            }
            if (c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                runs.Add(Run(line[i..], Com));
                break;
            }
            if (c == '"')
            {
                int s = i;
                i++;
                while (i < n && line[i] != '"') { if (line[i] == '\\') i++; i++; }
                if (i < n) i++;
                runs.Add(Run(line[s..i], Str));
                continue;
            }
            if (char.IsDigit(c))
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '.')) i++;
                runs.Add(Run(line[s..i], Num));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                string w = line[s..i];
                ColorF col = Keywords.Contains(w) ? Kw
                    : i < n && line[i] == '(' ? Meth
                    : char.IsUpper(w[0]) ? Typ
                    : Plain;
                runs.Add(Run(w, col));
                continue;
            }
            // punctuation: accumulate until the next token class (keeps run count low)
            int p = i;
            while (i < n && line[i] != ' ' && line[i] != '"' && !char.IsLetterOrDigit(line[i]) && line[i] != '_'
                   && !(line[i] == '/' && i + 1 < n && line[i + 1] == '/')) i++;
            runs.Add(Run(line[p..i], Plain));
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = runs.ToArray() };
    }

    static TextEl Run(string text, ColorF color) => new(text) { Size = FontSize, Color = color, FontFamily = "Cascadia Code" };
}
