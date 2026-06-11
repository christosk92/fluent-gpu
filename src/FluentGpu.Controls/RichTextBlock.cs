using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>RichTextBlock</c> equivalent: read-only, multi-paragraph rich text laid out as a vertical column of
/// paragraphs. Each paragraph is a <see cref="SpanTextEl"/> — an array of typed inline runs (the WinUI
/// Run/Bold/Hyperlink inline model, RichTextBlock.g.h:63 get_Blocks) that shapes as ONE wrapped flow (rtb-01).
///
/// <para><b>Selection (rtb-02):</b> ON by default — IsTextSelectionEnabled=TRUE by default on CRichTextBlock
/// (RichTextBlock.cpp:1730); mouse drag selects (word on double-click, all on triple), Ctrl+C copies through the
/// clipboard seam (TextSelectionManager.cpp:30-41), the highlight reuses the editor's selection brushes
/// (TextControlSelectionHighlightColor ≡ the system accent, TextSelectionManager.cpp:52-56) and the I-beam shows
/// while selectable. <see cref="Options.SelectionHighlightColor"/> overrides the brush per control (api-04 —
/// TextBlock.cpp:266/330 SelectionHighlightColor). Honest scope: selection is PER PARAGRAPH (the drag clamps to the
/// paragraph it anchored in); WinUI's cross-paragraph selection arrives with the block-collection pass.</para>
///
/// <para><b>Hyperlinks:</b> <see cref="Hyperlink"/> spans get the WinUI inline-Hyperlink treatment — accent
/// foreground (HyperlinkForeground = AccentTextFillColorPrimary, generic.xaml:1120-1122), underline, the Hand cursor
/// over the span's laid rects and OnClick on release (SetCursor(MouseCursorHand), RichTextBlock.cpp:2995).</para>
/// </summary>
public static class RichTextBlock
{
    /// <summary>Per-block options (WinUI DP equivalents). Defaults match the WinUI control: selection enabled
    /// (RichTextBlock.cpp:1730), theme selection brush, the established 560px reading measure + 10px paragraph gap.</summary>
    public readonly record struct Options
    {
        public Options() { }
        /// <summary>WinUI <c>RichTextBlock.IsTextSelectionEnabled</c> — default TRUE (RichTextBlock.cpp:1730).</summary>
        public bool IsTextSelectionEnabled { get; init; } = true;
        /// <summary>WinUI <c>RichTextBlock.SelectionHighlightColor</c> (api-04). A==0 = the theme accent brush.</summary>
        public ColorF SelectionHighlightColor { get; init; }
        public float MaxWidth { get; init; } = 560f;
        public float ParagraphGap { get; init; } = 10f;
    }

    // ── inline-run factories (the WinUI Documents vocabulary) ───────────────────────────────────────────────────────

    /// <summary>A plain run inheriting the paragraph style (WinUI <c>Run</c>).</summary>
    public static TextSpan Run(string text) => new(text);

    /// <summary>A SemiBold run — the WinUI <c>Bold</c> inline maps onto the type ramp's strong weight
    /// (BaseTextBlockStyle FontWeight=SemiBold, TextBlock_themeresources.xaml:13).</summary>
    public static TextSpan Bold(string text) => new(text, Weight: 600);

    /// <summary>An inline hyperlink (WinUI <c>Hyperlink</c>): accent foreground (HyperlinkForeground =
    /// AccentTextFillColorPrimary, generic.xaml:1120-1122) + underline + the engine Hand cursor over the span's laid
    /// rects, firing <paramref name="onClick"/> on click (RichTextBlock.cpp:2995-3001). No italic variant: the
    /// shaper has no style axis yet (faces resolve by family+weight).</summary>
    public static TextSpan Hyperlink(string text, Action onClick)
        => new(text, Color: Tok.AccentTextPrimary, Underline: true, OnClick: onClick);

    /// <summary>One rich paragraph from inline runs — body type (14px primary, wrapping), selection per
    /// <paramref name="isTextSelectionEnabled"/> (the control default is on; see <see cref="Options"/>).</summary>
    public static SpanTextEl Paragraph(TextSpan[] spans, bool isTextSelectionEnabled = true, ColorF selectionHighlightColor = default)
        => new(spans)
        {
            Size = 14f,
            Color = Tok.TextPrimary,
            Wrap = TextWrap.Wrap,
            IsTextSelectionEnabled = isTextSelectionEnabled,
            SelectionHighlightColor = selectionHighlightColor,
        };

    // ── block factories ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A column of body paragraphs (14px, primary text), 10px between paragraphs, capped at 560px wide.
    /// Source-compatible with the pre-inline-model API; each paragraph is now a selectable single-run
    /// <see cref="SpanTextEl"/> (RichTextBlock selection default-on, RichTextBlock.cpp:1730).</summary>
    public static BoxEl Create(IReadOnlyList<string> paragraphs) => Create(paragraphs, new Options());

    public static BoxEl Create(IReadOnlyList<string> paragraphs, in Options options)
    {
        var children = new List<Element>(paragraphs.Count);
        for (int i = 0; i < paragraphs.Count; i++)
            children.Add(Paragraph([Run(paragraphs[i])], options.IsTextSelectionEnabled, options.SelectionHighlightColor));
        return Column(children, in options);
    }

    /// <summary>A column of RICH paragraphs — each an array of inline runs (mixed weights/colors/hyperlinks) that
    /// wraps as one flow (rtb-01).</summary>
    public static BoxEl Create(IReadOnlyList<TextSpan[]> paragraphs) => Create(paragraphs, new Options());

    public static BoxEl Create(IReadOnlyList<TextSpan[]> paragraphs, in Options options)
    {
        var children = new List<Element>(paragraphs.Count);
        for (int i = 0; i < paragraphs.Count; i++)
            children.Add(Paragraph(paragraphs[i], options.IsTextSelectionEnabled, options.SelectionHighlightColor));
        return Column(children, in options);
    }

    /// <summary>A titled rich-text block: a bold 20px heading followed by the body paragraphs (source-compatible).</summary>
    public static BoxEl Article(string heading, IReadOnlyList<string> paragraphs) => Article(heading, paragraphs, new Options());

    public static BoxEl Article(string heading, IReadOnlyList<string> paragraphs, in Options options)
    {
        var children = new List<Element>(paragraphs.Count + 1)
        {
            new SpanTextEl([new TextSpan(heading, Weight: 700)])
            {
                Size = 20f,
                Color = Tok.TextPrimary,
                IsTextSelectionEnabled = options.IsTextSelectionEnabled,
                SelectionHighlightColor = options.SelectionHighlightColor,
            },
        };
        for (int i = 0; i < paragraphs.Count; i++)
            children.Add(Paragraph([Run(paragraphs[i])], options.IsTextSelectionEnabled, options.SelectionHighlightColor));
        return Column(children, in options);
    }

    private static BoxEl Column(List<Element> children, in Options options) => new()
    {
        Direction = 1,
        Gap = options.ParagraphGap,
        MaxWidth = options.MaxWidth,
        Children = children.ToArray(),
    };
}
