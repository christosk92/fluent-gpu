using System;
using System.Collections.Generic;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

// ── The Markdown syntax tree (docs/design/subsystems/markdown.md §2) ───────────────────────────────────────────────
// Immutable records, produced by MarkdownParser (cold path) and consumed by MarkdownTextBlock's renderer. CommonMark
// block/inline vocabulary plus the GFM parts the viewer supports (tables, task-list items, strikethrough, autolinks).
// Nothing here knows about elements, tokens or any consumer: an app can walk the tree (outline, search, link graph)
// without rendering it.

/// <summary>A parsed Markdown document: the top-level block sequence.</summary>
public sealed record MarkdownDocument(MarkdownBlock[] Blocks)
{
    public static readonly MarkdownDocument Empty = new(Array.Empty<MarkdownBlock>());
    /// <summary>How many link reference definitions the parse collected (an incremental re-parse falls back to a full
    /// one when the count changes: a definition arriving late can re-type an earlier paragraph's <c>[x][ref]</c>).</summary>
    public int ReferenceCount { get; init; }
}

/// <summary>A block-level node. <see cref="SourceStart"/>/<see cref="SourceEnd"/> are the UTF-16 offsets of the
/// block in the ORIGINAL source text (every block, nested ones included; tabs count as written, not expanded): the
/// unit of incremental re-parse and block-index diffing while streaming (markdown.md §3.3).</summary>
public abstract record MarkdownBlock
{
    public int SourceStart { get; init; } = -1;
    public int SourceEnd { get; init; } = -1;
}

/// <summary>ATX (<c># Title</c>) or setext heading, level 1-6.</summary>
public sealed record MarkdownHeading(int Level, MarkdownInline[] Inlines) : MarkdownBlock;

public sealed record MarkdownParagraph(MarkdownInline[] Inlines) : MarkdownBlock;

/// <summary>A fenced or indented code block. <see cref="Language"/> is the first word of the fence info string
/// (empty for an indented block or a bare fence).</summary>
public sealed record MarkdownCodeBlock(string Language, string Code) : MarkdownBlock;

public sealed record MarkdownQuote(MarkdownBlock[] Children) : MarkdownBlock;

/// <summary>A bullet or ordered list. <see cref="Tight"/> follows CommonMark: a list is loose when any two items, or
/// two blocks inside one item, are separated by a blank line; a tight list renders its item paragraphs without
/// paragraph spacing.</summary>
public sealed record MarkdownList(bool Ordered, int Start, bool Tight, MarkdownListItem[] Items) : MarkdownBlock;

/// <summary>One list item. <see cref="Checked"/> is non-null for a GFM task-list item (<c>- [ ]</c> / <c>- [x]</c>);
/// <see cref="TaskIndex"/> is the item's ordinal among all task items of the document in document order (an item
/// before its nested items — the index handed to <see cref="MarkdownOptions.OnTaskToggled"/>), -1 for a plain item;
/// <see cref="MarkerOffset"/> is the UTF-16 offset in the ORIGINAL source of the character inside the marker's
/// brackets (the one <see cref="MarkdownTasks.Toggle"/> rewrites), -1 for a plain item.</summary>
public sealed record MarkdownListItem(MarkdownBlock[] Children, bool? Checked, int TaskIndex, int MarkerOffset = -1);

public sealed record MarkdownThematicBreak : MarkdownBlock
{
    public static readonly MarkdownThematicBreak Instance = new();
}

/// <summary>A GFM pipe table. <see cref="Alignments"/> has one entry per column (from the delimiter row); every
/// row in <see cref="Rows"/> has exactly <c>Header.Length</c> cells (short rows padded, long rows truncated — the
/// GFM rule).</summary>
public sealed record MarkdownTable(MarkdownInline[][] Header, MarkdownTableAlign[] Alignments, MarkdownInline[][][] Rows) : MarkdownBlock;

public enum MarkdownTableAlign : byte { None, Left, Center, Right }

/// <summary>A CommonMark HTML block. The viewer has no HTML renderer, so the raw source is shown verbatim as
/// monospace text (never interpreted) — see markdown.md §3.4.</summary>
public sealed record MarkdownHtmlBlock(string Html) : MarkdownBlock;

/// <summary>An inline node inside a paragraph, heading, table cell or list item.</summary>
public abstract record MarkdownInline;

public sealed record MarkdownText(string Text) : MarkdownInline;

/// <summary><c>*em*</c> / <c>**strong**</c>. The engine's shaper has no italic axis yet (text.md §4 face
/// resolution is family+weight), so the renderer maps emphasis to a weight step, not a slant — markdown.md §5.2.</summary>
public sealed record MarkdownEmphasis(MarkdownInline[] Children, bool Strong) : MarkdownInline;

public sealed record MarkdownStrikethrough(MarkdownInline[] Children) : MarkdownInline;

/// <summary><c>==marked==</c> (the markdown-it-mark / pulldown-cmark highlight extension): a line-band highlight.</summary>
public sealed record MarkdownMark(MarkdownInline[] Children) : MarkdownInline;

public sealed record MarkdownCodeSpan(string Code) : MarkdownInline;

/// <summary>A link. <see cref="Syntax"/> is <c>null</c> for a CommonMark link or autolink and the extension's
/// <see cref="MarkdownInlineSyntax.Name"/> for one produced by a consumer-registered inline syntax (a
/// <c>[[wikilink]]</c>), so the click handler can route by origin without re-parsing the URL.</summary>
public sealed record MarkdownLink(MarkdownInline[] Children, string Url, string Title, string? Syntax = null) : MarkdownInline;

/// <summary>An image reference. The viewer never fetches <see cref="Url"/> itself: it is handed to
/// <see cref="MarkdownOptions.ImageResolver"/>, and without one the alt text shows instead (markdown.md §6.3).</summary>
public sealed record MarkdownImage(string Alt, string Url, string Title) : MarkdownInline;

/// <summary>A line break: hard (two trailing spaces or a backslash) or soft (a newline inside a paragraph, which
/// renders as a space per CommonMark §6.8).</summary>
public sealed record MarkdownLineBreak(bool Hard) : MarkdownInline;

// ── Extension points (markdown.md §6) ───────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A consumer-registered inline syntax: <see cref="Open"/> … <see cref="Close"/> delimiters whose inner text
/// <see cref="Build"/> turns into an inline node (a <see cref="MarkdownLink"/> with <c>Syntax = Name</c> for
/// a <c>[[wikilink]]</c>, a <see cref="MarkdownEmphasis"/> for <c>==highlight==</c>, <c>null</c> to decline). The
/// parser tries every registered syntax at each position BEFORE the CommonMark inline rules, in registration order,
/// so an extension can claim <c>[[</c> ahead of the <c>[</c> link rule. The inner text is passed raw (no nested
/// inline parsing); <see cref="Build"/> runs on the cold parse path, so it may allocate.
/// </summary>
public sealed record MarkdownInlineSyntax(string Name, string Open, string Close, Func<string, MarkdownInline?> Build);

/// <summary>What a link click hands the consumer (markdown.md §6.1). <see cref="Syntax"/> names the extension that
/// produced the link, <c>null</c> for a CommonMark link or autolink.</summary>
public readonly record struct MarkdownLinkClick(string Url, string Title, string? Syntax);

/// <summary>
/// The per-block options (the WinUI <c>MarkdownTextBlock</c> DP equivalents, Labs-Windows
/// <c>components\MarkdownTextBlock\src\MarkdownConfig.cs</c>: <c>BaseUrl</c>, <c>ImageProvider</c>,
/// <c>Themes</c>, and the <c>OnLinkClicked</c> event). Safe by default for untrusted content: with no
/// <see cref="OnLinkClicked"/> a link click does nothing, and with no <see cref="ImageResolver"/> an image renders
/// as its alt text — the block never launches a URI and never fetches anything on its own.
/// </summary>
public sealed record MarkdownOptions
{
    public static readonly MarkdownOptions Default = new();

    /// <summary>Fires on a link click with the link's URL, title and origin syntax. <c>null</c> = clicks do nothing
    /// (the link still looks like a link). A consumer that wants the WinUI <c>NavigateUri</c> behaviour calls
    /// <c>InputHooks.Current.Default.OpenUri</c> from here itself.</summary>
    public Action<MarkdownLinkClick>? OnLinkClicked { get; init; }

    /// <summary>Maps an image URL from the source to an engine image source (a file path or URL the
    /// <c>ImageCache</c> may load); return <c>null</c> to refuse. <c>null</c> resolver = every image shows as
    /// alt text. The viewer never loads a source the resolver did not return.</summary>
    public Func<string, string?>? ImageResolver { get; init; }

    /// <summary>Consumer inline syntaxes (markdown.md §6.2), tried in order before the CommonMark rules.</summary>
    public IReadOnlyList<MarkdownInlineSyntax>? InlineSyntaxes { get; init; }

    /// <summary>When set, task-list checkboxes are interactive and a click reports <c>(taskIndex, isChecked)</c>; the
    /// consumer rewrites its source. <c>null</c> = checkboxes render read-only (the GFM viewer behaviour).</summary>
    public Action<int, bool>? OnTaskToggled { get; init; }

    /// <summary>WinUI <c>RichTextBlock.IsTextSelectionEnabled</c>, default TRUE (RichTextBlock.cpp:1730). Selection is
    /// per paragraph today (rtb-02 scope).</summary>
    public bool IsTextSelectionEnabled { get; init; } = true;

    /// <summary>Which GFM extensions the parser enables (all on by default, the Labs-Windows defaults:
    /// <c>UsePipeTables</c>/<c>UseTaskLists</c>/<c>UseAutoLinks</c>/<c>UseEmphasisExtras</c>,
    /// MarkdownTextBlock.xaml.cs:66-70).</summary>
    public MarkdownExtensions Extensions { get; init; } = MarkdownExtensions.All;

    /// <summary>The visual style. <c>null</c> = <see cref="MarkdownStyle.Fluent"/> resolved at render (live tokens).</summary>
    public MarkdownStyle? Style { get; init; }

    /// <summary>The text is arriving token by token (an AI reply, a log). The parser closes unterminated inline
    /// delimiters in the growing tail so partial <c>**bold</c> renders as bold instead of flickering through literal
    /// asterisks (the Streamdown/remend idea), re-parses only the last top-level block, and the block reveals its growth
    /// with the engine's flow-reveal spring instead of snapping (no layout jump; reduced motion snaps by policy).
    /// markdown.md §11.1.</summary>
    public bool IsStreaming { get; init; }
}

[Flags]
public enum MarkdownExtensions : byte
{
    None = 0,
    Tables = 1,
    TaskLists = 2,
    Strikethrough = 4,
    Autolinks = 8,
    /// <summary><c>==mark==</c> highlight (not GFM; markdown-it-mark, pulldown-cmark <c>ENABLE_MARK</c>).</summary>
    Mark = 16,
    All = Tables | TaskLists | Strikethrough | Autolinks | Mark,
}
