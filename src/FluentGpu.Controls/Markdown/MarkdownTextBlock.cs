using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.Controls;

/// <summary>
/// The visual style of a <see cref="MarkdownTextBlock"/> (the Labs-Windows <c>MarkdownThemes</c> equivalent,
/// <c>components\MarkdownTextBlock\src\MarkdownThemes.cs</c>), expressed through the engine's tokens so a theme or
/// definition swap re-resolves it: <see cref="Fluent"/> reads <c>Tok.*</c>, <c>Spacing.*</c> and <c>Radii.*</c> when
/// called, and the block calls it on every render (a theme change re-renders through <c>RethemeAll</c>). Build a
/// custom style with <c>MarkdownStyle.Fluent() with { … }</c>. Rationale per value: docs/design/subsystems/markdown.md §5.
/// </summary>
/// <summary>Which heading scale a <see cref="MarkdownStyle"/> uses (markdown.md §5.1).</summary>
public enum MarkdownHeadingScale : byte
{
    /// <summary>The DEFAULT: a document inside a page or panel. H1 = Subtitle 20/28 (what a WinUI Gallery page uses for
    /// its section headings; a document's H1 is a section of the app's page, never the page title), H2 = BodyLarge
    /// strong 18/24, H3 16/22, H4 = BodyStrong 14/20, H5 13/18, H6 = Caption strong 12/16. Fits a 460 px panel.</summary>
    Document,
    /// <summary>The page ramp: H1 = Title 28/36, H2 = Subtitle, H3 = BodyLarge strong, H4/H5 = BodyStrong, H6 = Caption
    /// strong — for a document that IS the page (a README view).</summary>
    Page,
}

public sealed record MarkdownStyle
{
    /// <summary>The Fluent style, resolved against the live theme. Call it where the block is built, never cache it.</summary>
    public static MarkdownStyle Fluent(MarkdownHeadingScale scale = MarkdownHeadingScale.Document)
    {
        var type = Tok.Definition.Type;
        bool page = scale == MarkdownHeadingScale.Page;
        return new MarkdownStyle
        {
            Body = type.Body,
            // Heading ramp = the WinUI type ramp rungs (TextBlock_themeresources.xaml:3-9): H1 Title 28/36, H2 Subtitle
            // 20/28, H3 BodyLarge 18/24 at the ramp's SemiBold, H4 BodyStrong 14/20, H5 BodyStrong, H6 Caption SemiBold.
            // WinUI has no h1-h6 precedent; Labs uses 22/20/18/16/14/12 SemiBold (MarkdownThemes.cs:25-35), which is
            // not on the ramp — markdown.md §5.1 explains the choice.
            H1 = page ? type.Title : type.Subtitle,
            H2 = page ? type.Subtitle : type.BodyLarge with { Weight = 600 },
            H3 = page ? type.BodyLarge with { Weight = 600 } : new TypeStyle(16f, 22f, 600),
            H4 = type.BodyStrong,
            H5 = page ? type.BodyStrong : new TypeStyle(13f, 18f, 600),
            H6 = type.Caption with { Weight = 600 },
            TextPrimary = Tok.TextPrimary,
            TextSecondary = Tok.TextSecondary,
            // HyperlinkForeground = AccentTextFillColorPrimaryBrush (Hyperlink_themeresources.xaml:5); WinUI 3 inline
            // hyperlinks draw NO underline (HyperlinkUnderlineVisible = False, :20).
            LinkColor = Tok.AccentTextPrimary,
            LinkUnderline = false,
            // Code: CardBackgroundFillColorSecondary + CardStrokeColorDefault, radius 4 (MarkdownThemes.cs:86-93).
            CodeFill = Tok.FillCardSecondary,
            CodeStroke = Tok.StrokeCardDefault,
            // Inline code pill: CardBackgroundFillColorSecondary (MarkdownThemes.cs:67), a line-band highlight behind the span.
            CodeSpanFill = Tok.FillCardSecondary,
            // ==mark==: no WinUI token for a text highlight; the caution (yellow) background tint reads as a marker in both themes.
            MarkFill = Tok.SystemFillCautionBackground,
            CodeFontFamily = "Cascadia Code",
            CodeFontSize = 13f,
            CodeLineHeight = 20f,
            // Quote bar: a visible neutral stroke (Labs' CardStroke bar reads as invisible on a dark page) — §5.4.
            QuoteBar = Tok.StrokeControlStrongDefault,
            QuoteBarWidth = 3f,
            TableStroke = Tok.StrokeCardDefault,
            TableHeaderFill = Tok.FillCardDefault,
            Divider = Tok.StrokeDividerDefault,
            CheckFill = Tok.AccentDefault,
            CheckMark = Tok.TextOnAccentPrimary,
            CheckBoxFill = Tok.FillControlAltSecondary,
            CheckBoxStroke = Tok.StrokeControlStrongDefault,
            ImageFill = Tok.FillCardSecondary,
            BlockGap = Spacing.S,            // paragraph margin 8 (MarkdownThemes.cs:104)
            HeadingGap = page ? Spacing.L : Spacing.M,   // H1-H4 top margin 16 (:56-59) on the page scale; 12 in a document
            HeadingGapSmall = Spacing.S,     // :60-61
            ListIndent = Spacing.XXL,        // 24 (Labs: 32, :107); on the 4px grid, one body line-height + 4
            ListItemGap = Spacing.XXS,       // tight-list item rhythm 2
            Inset = Spacing.M,               // quote / cell inner padding 12
            CellPadY = Spacing.S,
            CornerRadius = Radii.Control,    // CornerRadius 4 (:23)
            ImageWidth = 320f,
        };
    }

    public TypeStyle Body { get; init; }
    public TypeStyle H1 { get; init; }
    public TypeStyle H2 { get; init; }
    public TypeStyle H3 { get; init; }
    public TypeStyle H4 { get; init; }
    public TypeStyle H5 { get; init; }
    public TypeStyle H6 { get; init; }
    public ColorF TextPrimary { get; init; }
    public ColorF TextSecondary { get; init; }
    public ColorF LinkColor { get; init; }
    public bool LinkUnderline { get; init; }
    /// <summary>Strong weight: 700 Bold (Labs' BoldFontWeight is SemiBold, MarkdownThemes.cs:82; the Fluent body ramp is
    /// already SemiBold for BodyStrong, so strong inside body text takes the next step). Emphasis is the italic face.</summary>
    public ushort StrongWeight { get; init; } = 700;
    /// <summary>Line-band highlight behind inline code and behind <c>==mark==</c>.</summary>
    public ColorF CodeSpanFill { get; init; }
    public ColorF MarkFill { get; init; }
    public ColorF CodeFill { get; init; }
    public ColorF CodeStroke { get; init; }
    public string CodeFontFamily { get; init; } = "Cascadia Code";
    public float CodeFontSize { get; init; }
    public float CodeLineHeight { get; init; }
    public ColorF QuoteBar { get; init; }
    public float QuoteBarWidth { get; init; }
    public ColorF TableStroke { get; init; }
    public ColorF TableHeaderFill { get; init; }
    public ColorF Divider { get; init; }
    public ColorF CheckFill { get; init; }
    public ColorF CheckMark { get; init; }
    public ColorF CheckBoxFill { get; init; }
    public ColorF CheckBoxStroke { get; init; }
    public ColorF ImageFill { get; init; }
    public float BlockGap { get; init; }
    public float HeadingGap { get; init; }
    public float HeadingGapSmall { get; init; }
    public float ListIndent { get; init; }
    public float ListItemGap { get; init; }
    public float Inset { get; init; }
    public float CellPadY { get; init; }
    public float CornerRadius { get; init; }
    /// <summary>Width of an inline image box (the engine reports no natural size to layout yet — markdown.md §7).</summary>
    public float ImageWidth { get; init; }
    /// <summary>Reading-measure cap. NaN = fill the parent (the consumer decides; RichTextBlock caps at 560).</summary>
    public float MaxWidth { get; init; } = float.NaN;
}

/// <summary>
/// The WinUI-Gallery-style read-only Markdown viewer (the Labs-Windows <c>MarkdownTextBlock</c> role, on the engine's
/// <see cref="SpanTextEl"/> paragraph model): CommonMark + GFM tables, task lists, strikethrough and autolinks, Fluent
/// type ramp, tokens and corners, per-paragraph selection and copy, links routed to the consumer, images through a
/// resolver, consumer inline syntaxes. Design: docs/design/subsystems/markdown.md.
///
/// <para><b>Update path.</b> The text arrives as a re-pushed prop; the parse is memoised on it (<c>UseMemo</c>), so a
/// theme change re-renders without re-parsing and a text change re-parses once. Nothing here runs per frame: every
/// element is built on the cold render path, so steady frames allocate nothing (the owner rule).</para>
/// </summary>
public sealed class MarkdownTextBlock : Component
{
    /// <summary>The re-pushed props (markdown text + options).</summary>
    public sealed record Props(string Text, MarkdownOptions Options);

    /// <summary>A static document.</summary>
    public static Element Create(string text, MarkdownOptions? options = null)
        => Embed.Comp(new Props(text ?? "", options ?? MarkdownOptions.Default), () => new MarkdownTextBlock());

    /// <summary>A live document: the block re-parses and re-renders when <paramref name="text"/> changes (the whole
    /// block, one granular re-render — a document is one unit of content).</summary>
    public static Element Create(Signal<string> text, MarkdownOptions? options = null)
        => Embed.Comp(() => new LiveHost { Text = text, Options = options ?? MarkdownOptions.Default });

    private sealed class LiveHost : Component
    {
        public required Signal<string> Text;
        public required MarkdownOptions Options;
        public override Element Render() => Embed.Comp(new Props(Text.Value ?? "", Options), () => new MarkdownTextBlock());
    }

    /// <summary>The previous parse (streaming re-parses only the tail — MarkdownParser.Parse(previous)).</summary>
    private sealed class ParseHolder { public string? Text; public MarkdownDocument? Doc; }

    public override Element Render()
    {
        var p = UseProps<Props>();
        var holder = UseRef(new ParseHolder());
        var doc = UseMemo(() =>
        {
            var h = holder.Value;
            var d = MarkdownParser.Parse(p.Text, p.Options, h.Doc, h.Text);
            h.Text = p.Text; h.Doc = d;
            return d;
        }, DepKey.Combine(p.Text, DepKey.FromRef(p.Options)));
        var hooks = UseContext(InputHooks.Current);
        var overlay = UseContext(Overlay.Service);   // the default link flow's dialog host (the null service = nothing opens)
        var style = p.Options.Style ?? MarkdownStyle.Fluent();
        var r = new MarkdownRenderer(p.Options, style, hooks, overlay);
        var root = r.Blocks(doc.Blocks, tight: false);
        if (!float.IsNaN(style.MaxWidth)) root = root with { MaxWidth = style.MaxWidth };
        if (!p.Options.IsStreaming) return root;
        // Streaming: the column's growth springs under the flow-reveal (the Expander's disclosure motion, MotionTok.Reveal —
        // reduced motion snaps by the token's policy); layout lands once per re-parse, the presented height follows. A new
        // block fades in (Enter) instead of popping. Nothing here runs per frame: the spring is the engine's.
        var revealHost = new BoxEl
        {
            Direction = 1, ClipToBounds = true,   // the ExpanderContentClip shape: the clip host is what springs
            Layout = new LayoutTransition(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(), Size: SizeMode.FlowReveal, Anchor: SizeAnchor.Parallax),
            Children = [root],
        };
        return new BoxEl { Direction = 1, Children = [revealHost] };
    }
}

/// <summary>Syntax tree → elements (markdown.md §4). One instance per render; stateless apart from the options.</summary>
internal sealed class MarkdownRenderer(MarkdownOptions options, MarkdownStyle style, InputHooks hooks, IOverlayService overlay)
{
    private readonly MarkdownOptions _o = options;
    private readonly MarkdownStyle _s = style;
    private readonly InputHooks _hooks = hooks;
    private readonly IOverlayService _overlay = overlay;

    // ── blocks ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public BoxEl Blocks(MarkdownBlock[] blocks, bool tight, int depth = 0)
    {
        var kids = new List<Element>(blocks.Length);
        for (int i = 0; i < blocks.Length; i++)
        {
            var el = Block(blocks[i], depth);
            if (i > 0 && blocks[i] is MarkdownHeading h)
            {
                // Extra air above a heading (Labs H1-H4 top margin 16, H5/H6 8): the block gap already gives BlockGap.
                float top = (h.Level <= 4 ? _s.HeadingGap : _s.HeadingGapSmall) - (tight ? 0f : _s.BlockGap);
                if (top > 0) el = WithTopMargin(el, top);
            }
            if (depth == 0 && _o.IsStreaming)
                el = new BoxEl { Direction = 1, Enter = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.StandardEnter, Children = [el] };
            kids.Add(el);
        }
        return new BoxEl { Direction = 1, Gap = tight ? 0f : _s.BlockGap, Children = kids.ToArray() };
    }

    private static Element WithTopMargin(Element el, float top)
    {
        if (el.Is(out SpanTextEl s)) return s with { Margin = new Edges4(s.Margin.Left, s.Margin.Top + top, s.Margin.Right, s.Margin.Bottom) };
        if (el.Is(out BoxEl b)) return b with { Margin = new Edges4(b.Margin.Left, b.Margin.Top + top, b.Margin.Right, b.Margin.Bottom) };
        return new BoxEl { Direction = 1, Margin = new Edges4(0, top, 0, 0), Children = [el] };
    }

    /// <summary>One block as the viewer draws it: the editor's islands.</summary>
    internal Element BlockElement(MarkdownBlock block) => Block(block, 0);

    private Element Block(MarkdownBlock block, int depth) => block switch
    {
        MarkdownHeading h => Paragraph(h.Inlines, HeadingStyle(h.Level)),
        MarkdownParagraph p => Paragraph(p.Inlines, _s.Body),
        MarkdownCodeBlock c => CodeCard(c.Code, c.Language),
        MarkdownHtmlBlock html => CodeCard(html.Html, "html"),
        MarkdownQuote q => Quote(q, depth),
        MarkdownList l => List(l, depth),
        MarkdownThematicBreak => new BoxEl { Height = 1f, Fill = _s.Divider, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(0, _s.BlockGap / 2f, 0, _s.BlockGap / 2f) },
        MarkdownTable t => Table(t),
        _ => new BoxEl(),
    };

    private TypeStyle HeadingStyle(int level) => level switch
    {
        1 => _s.H1, 2 => _s.H2, 3 => _s.H3, 4 => _s.H4, 5 => _s.H5, _ => _s.H6,
    };

    /// <summary>A paragraph: one <see cref="SpanTextEl"/> flow. An image splits the flow (a span cannot host a
    /// bitmap): text before, the image block, text after — in a column.</summary>
    private Element Paragraph(MarkdownInline[] inlines, in TypeStyle type, TextAlign align = TextAlign.Start)
    {
        var spans = new List<TextSpan>();
        List<Element>? parts = null;
        var baseState = new InlineState(0, default, false, false, null, null, null);
        for (int i = 0; i < inlines.Length; i++)
        {
            if (inlines[i] is MarkdownImage img)
            {
                parts ??= new List<Element>();
                if (spans.Count > 0) { parts.Add(Flow(spans.ToArray(), type, align)); spans.Clear(); }
                parts.Add(Image(img) with { Margin = new Edges4(0, _s.BlockGap, 0, _s.BlockGap) });
                continue;
            }
            if (inlines[i] is MarkdownLineBreak { Hard: true })
            {
                // A hard break ends the flow: the text seam has no in-paragraph newline, so the next line is its own
                // flow stacked with no gap (selection stays per flow — rtb-02 scope).
                parts ??= new List<Element>();
                parts.Add(Flow(spans.Count > 0 ? spans.ToArray() : [new TextSpan(" ")], type, align));
                spans.Clear();
                continue;
            }
            Spans(inlines[i], baseState, spans);
        }
        if (parts is null) return Flow(spans.ToArray(), type, align);
        if (spans.Count > 0) parts.Add(Flow(spans.ToArray(), type, align));
        return new BoxEl { Direction = 1, Children = parts.ToArray() };
    }

    private SpanTextEl Flow(TextSpan[] spans, in TypeStyle type, TextAlign align) => new(spans)
    {
        Size = type.Size,
        LineHeight = type.LineHeight,
        Weight = type.Weight,
        Color = _s.TextPrimary,
        Wrap = TextWrap.Wrap,
        TextAlign = align,
        IsTextSelectionEnabled = _o.IsTextSelectionEnabled,
        Shrink = 1f,
        MinWidth = 0f,
    };

    // ── inlines → spans ─────────────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct InlineState(ushort Weight, ColorF Color, bool Underline, bool Strike, string? Family, float? Size, Action? OnClick, bool Italic = false, ColorF Background = default);

    private void Spans(MarkdownInline node, in InlineState st, List<TextSpan> into)
    {
        switch (node)
        {
            case MarkdownText t:
                into.Add(new TextSpan(t.Text, st.Weight, st.Color, st.Underline, st.Strike, st.Size, st.Family, st.OnClick, Italic: st.Italic, Background: st.Background));
                break;
            case MarkdownLineBreak br:
                into.Add(new TextSpan(" ", st.Weight, st.Color, false, false, st.Size, st.Family, st.OnClick, Italic: st.Italic));
                break;
            case MarkdownCodeSpan c:
                // The pill: CodeSpanFill as a line-band behind the monospace run (SpanStyle.HighlightBit).
                into.Add(new TextSpan(c.Code, st.Weight, st.Color, st.Underline, st.Strike, _s.CodeFontSize, _s.CodeFontFamily, st.OnClick, Background: _s.CodeSpanFill));
                break;
            case MarkdownMark m:
            {
                var inner = st with { Background = _s.MarkFill };
                foreach (var k in m.Children) Spans(k, inner, into);
                break;
            }
            case MarkdownEmphasis e:
            {
                // *em* = the italic face (the weight word's style axis); **strong** = StrongWeight.
                var inner = e.Strong ? st with { Weight = Math.Max(st.Weight, _s.StrongWeight) } : st with { Italic = true };
                foreach (var k in e.Children) Spans(k, inner, into);
                break;
            }
            case MarkdownStrikethrough s:
            {
                var inner = st with { Strike = true };
                foreach (var k in s.Children) Spans(k, inner, into);
                break;
            }
            case MarkdownLink l:
            {
                Action? click;
                if (_o.OnLinkClicked is { } handler)
                {
                    var args = new MarkdownLinkClick(l.Url, l.Title, l.Syntax);
                    click = () => handler(args);   // the consumer's handler replaces the whole default flow
                }
                else
                {
                    // Default flow (markdown.md §6.1): a warning dialog that shows the REAL destination, Open / Cancel;
                    // only http(s) may open; without an OverlayHost nothing opens at all.
                    string url = l.Url; var overlay = _overlay;
                    click = () => MarkdownLinkPolicy.ConfirmAndOpen(overlay, url);
                }
                var inner = st with { Color = _s.LinkColor, Underline = st.Underline || _s.LinkUnderline, OnClick = click };
                foreach (var k in l.Children) Spans(k, inner, into);
                break;
            }
            case MarkdownImage img:
                // An image nested inside emphasis/a link cannot split the flow: its alt text stands in.
                into.Add(new TextSpan(img.Alt.Length > 0 ? img.Alt : img.Url, st.Weight, st.Color, st.Underline, st.Strike, st.Size, st.Family, st.OnClick, Italic: st.Italic, Background: st.Background));
                break;
        }
    }

    // ── code ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private Element CodeCard(string code, string language)
        => Embed.Comp(new MarkdownCodeCard.Props(code, language, _s, _o.IsTextSelectionEnabled), () => new MarkdownCodeCard());

    // ── quote ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private Element Quote(MarkdownQuote q, int depth) => new BoxEl
    {
        Direction = 0,
        Gap = _s.Inset,
        Children =
        [
            new BoxEl { Width = _s.QuoteBarWidth, Fill = _s.QuoteBar, Corners = CornerRadius4.All(_s.QuoteBarWidth / 2f), AlignSelf = FlexAlign.Stretch },
            Blocks(q.Children, tight: false, depth + 1) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
        ],
    };

    // ── lists ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private Element List(MarkdownList l, int depth)
    {
        var rows = new Element[l.Items.Length];
        for (int i = 0; i < l.Items.Length; i++)
        {
            var item = l.Items[i];
            Element marker = item.Checked is { } chk
                ? TaskBox(chk, item.TaskIndex)
                : new TextEl(l.Ordered ? (l.Start + i).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." : Bullet(depth))
                {
                    Size = _s.Body.Size, LineHeight = _s.Body.LineHeight, Color = _s.TextPrimary,
                    TextAlign = l.Ordered ? TextAlign.End : TextAlign.Center,
                    Width = _s.ListIndent - Spacing.XS,
                };
            rows[i] = new BoxEl
            {
                Direction = 0,
                Gap = Spacing.XS,
                AlignItems = FlexAlign.Start,
                Children =
                [
                    new BoxEl { Width = _s.ListIndent - Spacing.XS, Shrink = 0f, Direction = 1, AlignItems = item.Checked is null ? FlexAlign.Stretch : FlexAlign.Center, Children = [marker] },
                    Blocks(item.Children, l.Tight, depth + 1) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
                ],
            };
        }
        return new BoxEl { Direction = 1, Gap = l.Tight ? _s.ListItemGap : _s.BlockGap, Children = rows };
    }

    private static string Bullet(int depth) => (depth % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };

    /// <summary>The WinUI CheckBox look at glyph size (CheckBox_themeresources: 20px box, ControlCornerRadius, the
    /// accent fill + on-accent check when checked, ControlAltFill + strong stroke when not). Read-only unless the
    /// consumer set <see cref="MarkdownOptions.OnTaskToggled"/>.</summary>
    private Element TaskBox(bool isChecked, int taskIndex)
    {
        float size = _s.Body.LineHeight - 2f;
        bool interactive = _o.OnTaskToggled is not null;
        Action? onClick = null;
        if (_o.OnTaskToggled is { } toggle) onClick = () => toggle(taskIndex, !isChecked);
        var fill = isChecked ? _s.CheckFill : _s.CheckBoxFill;
        return new BoxEl
        {
            Width = size, Height = size,
            Margin = new Edges4(0, 1f, 0, 1f),
            Corners = CornerRadius4.All(_s.CornerRadius),
            Fill = fill, HoverFill = fill, PressedFill = fill,
            BorderColor = isChecked ? _s.CheckFill : _s.CheckBoxStroke, BorderWidth = 1f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.CheckBox,
            Focusable = interactive,
            Cursor = interactive ? CursorId.Hand : null,
            OnClick = onClick,
            Children = isChecked ? [Icon(Icons.Accept, size - 6f).Foreground(_s.CheckMark)] : [],
        };
    }

    // ── tables ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private Element Table(MarkdownTable t)
    {
        int cols = t.Header.Length;
        var tracks = new TrackSize[cols];
        for (int c = 0; c < cols; c++) tracks[c] = TrackSize.Auto;
        var cells = new List<Element>(cols * (t.Rows.Length + 1));
        for (int c = 0; c < cols; c++) cells.Add(Cell(t.Header[c], t.Alignments[c], header: true, divider: true));
        for (int r = 0; r < t.Rows.Length; r++)
            for (int c = 0; c < cols; c++)
                cells.Add(Cell(t.Rows[r][c], t.Alignments[c], header: false, divider: r < t.Rows.Length - 1));
        // The frame HUGS the table and PANS it when it cannot fit (markdown.md §5.7, layout.md §7.2): the grid opts out of
        // the stretch so it measures to its Auto tracks (max-content when they fit, the CSS auto-table split between each
        // column's longest word and its max-content otherwise, never narrower than a word); the FitContentWidth viewport
        // measures it against the column width, hugs it up to that width and scrolls horizontally only when the
        // longest-word floor is wider than the column; the bordered card hugs the viewport.
        var grid = new GridEl { Columns = tracks, ColGap = 0f, RowGap = 0f, Children = cells.ToArray(), AlignSelf = FlexAlign.Start };
        var pan = new ScrollEl { Horizontal = true, FitContentWidth = true, Content = grid };
        return new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Start,
            BorderColor = _s.TableStroke, BorderWidth = 1f,
            Corners = CornerRadius4.All(_s.CornerRadius),
            ClipToBounds = true,
            Children = [pan],
        };
    }

    private Element Cell(MarkdownInline[] inlines, MarkdownTableAlign align, bool header, bool divider)
    {
        var ta = align switch { MarkdownTableAlign.Center => TextAlign.Center, MarkdownTableAlign.Right => TextAlign.End, _ => TextAlign.Start };
        var body = Paragraph(inlines, header ? _s.Body with { Weight = 600 } : _s.Body, ta);
        // A column, so the flow stretches to the cell. It grows, so a row's rule stays on the row's edge in a shorter cell.
        var kids = new List<Element>(2) { new BoxEl { Direction = 1, Grow = 1f, Padding = new Edges4(_s.Inset, _s.CellPadY, _s.Inset, _s.CellPadY), Children = [body] } };
        if (divider) kids.Add(new BoxEl { Height = 1f, Fill = _s.TableStroke, AlignSelf = FlexAlign.Stretch });
        return new BoxEl { Direction = 1, Fill = header ? _s.TableHeaderFill : default, Children = kids.ToArray() };
    }

    // ── images ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private BoxEl Image(MarkdownImage img)
    {
        string? source = _o.ImageResolver?.Invoke(img.Url);
        if (source is null)
        {
            // Unresolved (or no resolver): the alt text as a caption chip — never a fetch.
            string label = img.Alt.Length > 0 ? img.Alt : img.Url;
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Start,
                Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
                Fill = _s.ImageFill, Corners = CornerRadius4.All(_s.CornerRadius),
                Children =
                [
                    Icon(Icons.Picture, 14f).Foreground(_s.TextSecondary),
                    new TextEl(label) { Size = Tok.Definition.Type.Caption.Size, LineHeight = Tok.Definition.Type.Caption.LineHeight, Color = _s.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f },
                ],
            };
        }
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            Corners = CornerRadius4.All(_s.CornerRadius), ClipToBounds = true, Fill = _s.ImageFill,
            Children = [new ImageEl { Source = source, Width = _s.ImageWidth, AspectRatio = 4f / 3f, Fit = ImageFit.Contain, Corners = CornerRadius4.All(_s.CornerRadius) }],
        };
    }
}

/// <summary>
/// A fenced/indented code block: a card (CardBackgroundFillColorSecondary + CardStroke, radius 4 — the Labs
/// <c>MyCodeBlock</c> border, MyCodeBlock.cs:26-32) with the WinUI-Gallery <c>SampleCodePresenter</c> header strip
/// (language label + Copy) and monospace lines that pan horizontally instead of wrapping. Its own component so the
/// "Copied" state is per block; the props are re-pushed so a re-parsed document updates it in place.
/// </summary>
internal sealed class MarkdownCodeCard : Component
{
    public sealed record Props(string Code, string Language, MarkdownStyle Style, bool Selectable);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var hooks = UseContext(InputHooks.Current);
        var (copied, setCopied) = UseState(false);
        var s = p.Style;
        var lines = p.Code.Split('\n');
        var rows = new Element[lines.Length];
        for (int i = 0; i < lines.Length; i++)
            rows[i] = new TextEl(lines[i].Length == 0 ? " " : lines[i])
            {
                Size = s.CodeFontSize, LineHeight = s.CodeLineHeight, FontFamily = s.CodeFontFamily, Color = s.TextPrimary,
                Wrap = TextWrap.NoWrap, IsTextSelectionEnabled = p.Selectable,
            };
        float padX = s.Inset, padY = s.CellPadY;
        float h = lines.Length * s.CodeLineHeight + padY * 2f;
        var content = new BoxEl { Direction = 1, Padding = new Edges4(padX, padY, padX, padY), Children = rows };
        var pan = new ScrollEl { Horizontal = true, Height = h, Content = content };

        var copyFg = copied ? Tok.AccentDefault : s.TextSecondary;
        var copyBtn = new BoxEl
        {
            Direction = 0, Gap = Spacing.XS + 2f, AlignItems = FlexAlign.Center, MinHeight = 24f,
            Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Focusable = true, Role = AutomationRole.Button,
            OnClick = () => { hooks.Clipboard?.SetText(p.Code); setCopied(true); },
            Children =
            [
                Icon(copied ? Icons.Accept : Icons.Copy, 12f).Foreground(copyFg),
                new TextEl(Loc.Get(copied ? Strings.Markdown.Copied : Strings.Markdown.Copy)) { Size = Tok.Definition.Type.Caption.Size, Color = copyFg },
            ],
        };
        var header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(padX, Spacing.XS, Spacing.XS, Spacing.XS),
            Children =
            [
                new TextEl(p.Language.Length > 0 ? p.Language : "code") { Size = Tok.Definition.Type.Caption.Size, Color = s.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f },
                copyBtn,
            ],
        };
        return new BoxEl
        {
            Direction = 1,
            Fill = s.CodeFill, BorderColor = s.CodeStroke, BorderWidth = 1f,
            Corners = CornerRadius4.All(s.CornerRadius), ClipToBounds = true,
            Children = [header, new BoxEl { Height = 1f, Fill = s.CodeStroke, AlignSelf = FlexAlign.Stretch }, pan],
        };
    }
}
