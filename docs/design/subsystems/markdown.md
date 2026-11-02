# FluentGpu — Subsystem Design: Markdown (`MarkdownTextBlock`)

> **OWNER, AS-BUILT 2026-11-02 (third pass).** This doc owns the generic Markdown viewer in `FluentGpu.Controls`: the
> syntax tree (`MarkdownDocument` and its block/inline records), the in-house parser (`MarkdownParser`), the options
> and extension seams (`MarkdownOptions`, `MarkdownInlineSyntax`, `MarkdownLinkClick`), the style record
> (`MarkdownStyle`) and the block → element mapping (`MarkdownTextBlock`, `MarkdownRenderer`, `MarkdownCodeCard`).
> It adds **no SceneStore column, no DrawList opcode, no PAL seam, no RHI method and no engine hook**: it is pure
> composition over `SpanTextEl` (text.md §19 / rtb-01), `BoxEl`, `GridEl`, `ScrollEl` and `ImageEl`. Where it says
> "gap", the engine primitive it would need is named and the doc that owns that primitive is cited.

**Where it lives.** `src/FluentGpu.Controls/Markdown/{MarkdownSyntax,MarkdownParser,MarkdownTextBlock}.cs`. Gallery:
`src/FluentGpu.WindowsApp/Pages/MarkdownPages.cs` (page key `MarkdownTextBlock`, category Text).

**Status.** Viewer plus the engine side of source + preview editing (§9, decided: option B) and streaming (§11.1).
Everything marked *built* is in the tree and was driven with real input through the engine's own queue (§12); *later* and
*speculative* items are not.

---

## 0. Thesis and scope

A Markdown document is **content, not UI**: the control turns a string into the engine's ordinary elements, through
the ordinary tokens, with every side effect (open a link, fetch an image, toggle a task) handed to the consumer. The
first consumer is a note panel over an Obsidian vault; the engine knows nothing about Obsidian — a `[[wikilink]]` is a
consumer-registered inline syntax (§6.2), exactly as react-markdown leaves custom syntax to remark plugins.

In scope (built): CommonMark blocks and inlines; GFM tables, task lists, strikethrough, autolinks; code blocks with a
copy affordance; images through a resolver; per-paragraph selection and copy; links routed to the consumer; consumer
inline syntaxes; Fluent type ramp, tokens, corners; dark/light through the token table.
Out of scope for this pass (documented, §3.5/§12): HTML rendering, footnotes, definition lists, math, syntax
highlighting of fenced code beyond the house `CodeBlock` tinter, cross-paragraph selection, block virtualization,
editing.

## 1. Consumers and the shape of the API

```csharp
MarkdownTextBlock.Create(string text, MarkdownOptions? options = null)   // static
MarkdownTextBlock.Create(Signal<string> text, MarkdownOptions? options)  // live: re-parse per change
```

`MarkdownOptions` is the WinUI/Labs `MarkdownTextBlock` DP set folded into one record
(CommunityToolkit/Labs-Windows `components/MarkdownTextBlock/src/MarkdownConfig.cs`: `BaseUrl`, `ImageProvider`, `Themes`; the
`OnLinkClicked` event with `LinkClickedEventArgs.Handled`, `MarkdownTextBlock.xaml.cs:24-34`):

| Member | Labs / WinUI precedent | FluentGpu |
|---|---|---|
| `OnLinkClicked` | `OnLinkClicked` event; unhandled → `Launcher` | `Action<MarkdownLinkClick>`; a handler replaces the whole default flow. **null = the warning dialog** (§6.1): the real destination, Open / Cancel, http(s) only, nothing without an `OverlayHost`. |
| `ImageResolver` | `IImageProvider.GetImage(url)` (ImageProvider.cs:7-11) | `Func<string,string?>`; **null = alt text**, never a fetch. |
| `InlineSyntaxes` | none (Markdig extensions on the pipeline) | `MarkdownInlineSyntax(Name, Open, Close, Build)` list, tried before CommonMark rules. |
| `OnTaskToggled` | none (Labs task boxes are read-only `MyTaskListCheckBox`) | `Action<int,bool>`; null = read-only boxes. |
| `IsTextSelectionEnabled` | `RichTextBlock.IsTextSelectionEnabled` default TRUE (RichTextBlock.cpp:1730) | same default. |
| `Extensions` | `UsePipeTables/UseTaskLists/UseAutoLinks/UseEmphasisExtras` (MarkdownTextBlock.xaml.cs:66-70) | `MarkdownExtensions` flags, all on. |
| `Style` | `MarkdownThemes` | `MarkdownStyle`, resolved from tokens at render (§5). |

Nothing in the options names a vault, a note, a status or a person; the glossary terms of the first consumer (the
public app `christosk92/thoughts`, its `GLOSSARY.md`) stay in the consumer.

## 2. The syntax tree (owned here)

`MarkdownSyntax.cs`. Immutable records, no element or token knowledge, walkable by a consumer for an outline, a search
index or a link graph without rendering:

- Blocks: `MarkdownHeading(Level, Inlines)`, `MarkdownParagraph`, `MarkdownCodeBlock(Language, Code)`,
  `MarkdownQuote(Children)`, `MarkdownList(Ordered, Start, Tight, Items)` / `MarkdownListItem(Children, Checked,
  TaskIndex)`, `MarkdownThematicBreak`, `MarkdownTable(Header, Alignments, Rows)`, `MarkdownHtmlBlock(Html)`.
- Inlines: `MarkdownText`, `MarkdownEmphasis(Children, Strong)`, `MarkdownStrikethrough`, `MarkdownCodeSpan`,
  `MarkdownLink(Children, Url, Title, Syntax)`, `MarkdownImage(Alt, Url, Title)`, `MarkdownLineBreak(Hard)`.

Design choices: a tree, not a pull stream (pulldown-cmark's event model, §10.2) — the renderer needs random access
(table cells, list tightness, the task ordinal) and the parse is a cold path, so the tree's allocations are
irrelevant; records, so a consumer can pattern-match and so `with` builds derived trees. Every block carries `SourceStart`/`SourceEnd`
(UTF-16 offsets into the ORIGINAL source, nested blocks included; the top-level ones are the unit of incremental
re-parse and block-index diffing, §3.3), and a task item carries `MarkerOffset` (the character inside its brackets).
Inline positions (Markdig's trivia, pulldown-cmark's `OffsetIter`) are *later* — an editor's need (§11.4). Task items carry `TaskIndex` in DOCUMENT order (a parent counts before its nested
items) — the order `MarkdownTasks` walks the source in (§6.4).

## 3. The parser: in-house, cold, CommonMark-shaped

### 3.1 In-house, not a dependency

`FluentGpu.Controls` references `Engine` only and carries no NuGet; `FluentGpu.Engine` has no NuGet at all; the whole
graph is NativeAOT-clean (CLAUDE.md "The `src/` layout"). Markdig (BSD-2, github.com/xoofx/markdig) is
the obvious .NET candidate: 600+ spec tests, 20+ extensions, trivia roundtrip, source positions. Rejected for the
control kit because (a) it would be the first and only third-party dependency of the portable kit and would ship in
the single `FluentGpu` package as a transitive dependency; (b) its extension model is a pipeline of block/inline
parsers plus HTML renderers — the HTML half is dead weight here; (c) the subset a viewer needs is ~900 lines
(`MarkdownParser.cs`) and the owner's rule is "find the house one before writing a primitive", of which there was
none. Markdig stays the reference for **behaviour** (its `PipeTable`, `TaskList`, `AutoLink` and `EmphasisExtra`
specs are the test corpora this parser was shaped against; no code was copied). Revisit if the editor (§9) needs
roundtrip-safe rewriting: Markdig's `trackTrivia` (`src/Markdig/Roundtrip.md`) is exactly that, and an app (not the
kit) may reference it.

### 3.2 Structure

Two passes, the CommonMark reference shape (spec appendix A; pulldown-cmark's "first pass: block tree, second pass:
inlines"): block parsing on lines with container prefixes stripped per level (quotes and list items recurse with the
inner lines), then inline parsing per leaf. Inline parsing is a tokenizer plus the spec's delimiter-run "process
emphasis" algorithm (§6.2 of the spec, with the flanking and multiple-of-3 rules), which is what markdown-it and
Markdig also implement; strikethrough rides the same delimiter stack with `~~` only.

Supported, per the spec sections: ATX/setext headings (§4.2/4.3), thematic breaks (§4.1), indented and fenced code
(§4.4/4.5) with the fence-indent strip, HTML blocks as raw text (§4.6, kept literal — §3.4), link reference
definitions (§4.7, collected in a pre-pass, lines consumed), paragraphs and interruption rules (§4.8; an ordered item
interrupts only when it starts at 1), block quotes with lazy continuation (§5.1), lists with content-column indentation,
nested lists, tight/loose (§5.2–5.4), backslash escapes (§6.1), a named-entity subset plus numeric references (§6.2),
code spans (§6.3), emphasis (§6.4), inline/reference links and images with nested brackets and titles (§6.5/6.6),
angle autolinks (§6.7), hard and soft breaks (§6.8/6.9). GFM: pipe tables with escaped pipes and short/long row
padding (GFM §4.10), task-list items (§5.3), strikethrough (§6.5), bare autolinks for `http(s)://` and `www.` with
trailing-punctuation trimming (§6.9).

### 3.3 Cost model, incremental parse and the tail-closer

`MarkdownParser.Parse` runs once per document text, memoised by the block on the text (`UseMemo` keyed on the text
hash + the options reference), **never per frame**; it allocates freely (lists, builders, records) because it is on the
cold path — the same stance as `CodeBlock`'s lexer. Steady frames: zero allocation, because nothing in the control runs
per frame — measured (§12): `FrameStats.HotPhaseAllocBytes` = 0 over 60 idle frames on the gallery page, and 0 over 60
frames after a stream ended.

**Incremental (`IsStreaming`).** `Parse(text, options, previous, previousText)`: when the new text extends the previous
one and the reference-definition count is unchanged, every top-level block of the previous document but the LAST is
reused as is (a block that was followed by another block cannot be re-opened by appended text) and only the tail from
the last block's `SourceStart` is re-parsed; task ordinals continue from the reused prefix. A late reference definition
forces a full parse (a `[x][ref]` earlier in the text may have become a link — Streamdown's `parse-blocks.tsx:12-16`
problem, solved by the count check). Streaming frames do allocate (the tail parse, measure-cache fills, the re-render):
23–38 of 90 frames, max 31–72 KB (§12) — content-change frames, not steady ones.

**Tail-closer.** Before the tail is parsed, `CloseTail` appends the closers an unterminated inline construct still needs —
code-span ticks (and nothing else, since what follows is code), `)` for an open `](`, then `==`, `~~`, `*`, `**`, `_`,
`__` — so `**bo` renders bold at once instead of flickering through literal asterisks (remend's idea,
`streamdown/packages/remend`). A tail inside an open fence is left alone (it is code). List bullets and quote markers
at line start are not delimiters. Verified: `*Atlas l` rendered italic before its closing `*` arrived (§12).

### 3.4 HTML and untrusted content

CommonMark says raw HTML passes through to the HTML output. This control has no HTML renderer and will not get one:
an HTML block renders as a code card labelled `html`, an inline tag stays literal text. That is the react-markdown
posture ("safe by default — no `dangerouslySetInnerHTML`", readme.md:18-19, §10.4): untrusted Markdown cannot inject
anything. A link never opens silently: without `OnLinkClicked` it goes through the warning dialog of §6.1 (the real
destination shown, http and https only); images never load without `ImageResolver`; a consumer that wants a
different whitelist (react-markdown's `defaultUrlTransform`, `lib/index.js:124`: `https?|ircs?|mailto|xmpp`) sets
`OnLinkClicked` and applies it there.

### 3.5 Not implemented (and why)

Footnotes, definition lists, math, abbreviations, custom containers, alerts (Markdig extras): none needed by the
first consumer; footnotes and GitHub alerts (`> [!NOTE]`) are the two most likely *later* additions (Markdig and
pulldown-cmark both ship them). HTML inlines: §3.4. Nested links are rejected (spec rule), an image inside emphasis or
a link falls back to its alt text (a span cannot host a bitmap). Smart punctuation ("SmartyPants") is off: the
engine's `FontFeatures` and the owner's typography rules decide quotes, not the parser.

## 4. Rendering: block → element

`MarkdownRenderer` is a stateless mapping built once per render:

| Block | Element | Notes |
|---|---|---|
| Paragraph / heading | one `SpanTextEl` flow (rtb-01) | per-paragraph selection; hard breaks split into stacked flows (§7.1). |
| Code block / HTML block | `MarkdownCodeCard` component | card + header strip (language, Copy) + monospace lines in a horizontal `ScrollEl` (no wrap, pans). |
| Quote | row: bar `BoxEl` + child column | nested quotes recurse with depth. |
| List | column of rows: marker cell (`ListIndent − XS` wide) + child column | bullet per depth `•` `◦` `▪`, `n.` for ordered; task items replace the marker with a check box. |
| Thematic break | 1 px `BoxEl` in `StrokeDividerDefault` | `Ui.Divider` look, half a block gap above and below. |
| Table | `GridEl` with `Auto` tracks inside a bordered, clipped card | header cells `BodyStrong` on `FillCardDefault`; a 1 px rule under every row but the last; per-column `TextAlign` from the delimiter row. |
| Image | `ImageEl` in a corner-clipped card, or an alt-text chip | §5.8. |

Spacing: the block column has `Gap = BlockGap` (8); a heading that is not the first block gets extra top margin so
its distance from the previous block is `HeadingGap` (16, H1–H4) or `HeadingGapSmall` (8, H5–H6) — Labs' heading
margins (`MarkdownThemes.cs:56-61`). Tight lists use `Gap = 0` between an item's blocks and `ListItemGap` (2) between
items; loose lists use the block gap.

**Update paths.** Text is a re-pushed prop (`Embed.Comp(props, factory)` + `UseProps`), so a changed document reaches
the block without a remount; a theme switch re-runs `Render` through `RethemeAll`, re-resolving `MarkdownStyle.Fluent()`
from the live tokens without re-parsing (the memo holds). The code card's `Props` are re-pushed too, so a re-parsed
document updates its cards in place and keeps their "Copied" state.

## 5. Visual rules: WinUI first, decided where WinUI is silent

Every colour, size, radius and gap is read from `Tok.*`, `Tok.Definition.Type`, `Spacing.*` and `Radii.*` **inside
`MarkdownStyle.Fluent()`**, called at render — never cached (skill rule 11). Light and dark therefore follow the token
table with no control-local branches; the renders in §12 are the proof.

### 5.1 Type ramp and headings (no WinUI precedent — decided: a DOCUMENT scale by default)
WinUI has no `h1…h6`; its ramp is Caption 12 / Body 14 / BodyLarge 18 / Subtitle 20 / Title 28 / TitleLarge 40 /
Display 68, SemiBold from BodyStrong up (`controls\dev\CommonStyles\TextBlock_themeresources.xaml:3-9, :13`). Labs picks
22/20/18/16/14/12 SemiBold (`MarkdownThemes.cs:25-35`). Two scales exist (`MarkdownHeadingScale`):

| | H1 | H2 | H3 | H4 | H5 | H6 | heading gap |
|---|---|---|---|---|---|---|---|
| **Document** (default) | Subtitle 20/28 | BodyLarge 18/24 at 600 | 16/22 at 600 | BodyStrong 14/20 | 13/18 at 600 | Caption 12/16 at 600 | 12 |
| Page | Title 28/36 | Subtitle 20/28 | BodyLarge 18/24 at 600 | BodyStrong | BodyStrong | Caption at 600 | 16 |

**Why Document is the default.** A Markdown document inside an app is a section of a page, never the page itself: a
WinUI Gallery page titles itself with Title (28) and heads its sections with Subtitle (20), so a document's H1 at
Subtitle sits one rung under the app's own title instead of competing with it. In a 460 px panel (the first consumer)
Title 28/36 wraps a six-word heading onto two lines and dominates the body; Subtitle holds one line. The six rungs stay
distinct (20/18/16/14/13/12) and every one is at or above the type floor; 16/22 and 13/18 are the two off-ramp values,
chosen so H3 and H5 do not collapse into their neighbours (Labs has the same two off-ramp rungs). `Page` is for a
document that IS the page (a README view): `MarkdownStyle.Fluent(MarkdownHeadingScale.Page)`.

### 5.2 Emphasis = the italic face (built; the engine gap is closed)
The shaper used to resolve faces by family + weight only. The cause is fixed in the text engine (text.md §4.3b, the
**font weight word**): the style axis rides bit 10 of every weight the pipeline already carries, the registry keys one
face per style, and the DWrite leaves decode it — no new column, seam or cache key. `TextEl.Italic`, `SpanTextEl.Italic`
and `TextSpan.Italic` are the authoring surface. The viewer renders `*em*` with the italic face, `**strong**` at
`StrongWeight` (700; Labs' `BoldFontWeight` is SemiBold, `MarkdownThemes.cs:82`, but the Fluent body ramp is already
SemiBold for BodyStrong, so strong inside body text takes the next step), `***both***` bold italic. The weight-based
stand-in is deleted. A family without an italic face (nearest match) renders upright; a file font gets a synthetic
oblique.

### 5.3 Links
`HyperlinkForeground = AccentTextFillColorPrimaryBrush` in Default and Light (`Hyperlink_themeresources.xaml:5, :15`);
`HyperlinkUnderlineVisible = False` (`:20`), so WinUI 3 inline hyperlinks are **not underlined**. The viewer matches
(`LinkColor = Tok.AccentTextPrimary`, `LinkUnderline = false`). The house `RichTextBlock.Hyperlink` underlines; the
viewer follows the WinUI 3 resource rather than that factory, and the knob exists for consumers who want underlines.
The engine supplies the hand cursor over a clickable span (`SpanTextEl` doc, RichTextBlock.cpp:2995); a span with no
handler gets no `OnClick` and therefore no hand — a link that does nothing must not promise to.

### 5.4 Code (Labs + WinUI Gallery)
Card = `CardBackgroundFillColorSecondary` fill, `CardStrokeColorDefault` 1 px, radius 4 (`MarkdownThemes.cs:86-93`
`CodeBlock*`; `MyCodeBlock.cs:26-32`). Header strip with the language and a Copy button is the WinUI Gallery
`SampleCodePresenter` pattern (microsoft/WinUI-Gallery `WinUIGallery/Controls/SampleCodePresenter.xaml.cs`) and Zed's
`copy_button_visibility` (zed-industries/zed `crates/markdown/src/markdown.rs:538`). Font: `Cascadia Code` 13/20, the same
face the house `CodeBlock` uses (Labs: Consolas, :91). Long lines pan horizontally (`ScrollEl Horizontal`) as in
`CodeBlock`; they never wrap, so code keeps its shape. The horizontal viewport's height is computed from the line
count (a horizontal `ScrollEl` has no intrinsic height in a column — `CodeBlock.Block`'s note).
**Inline code** (built): Labs draws a pill (`InlineCodeBackground` CardSecondary, radius 2, :67-78). The engine now
has a per-span highlight band — `TextSpan.Background` → `SpanStyle.Background` + `HighlightBit`; both text seams publish
one line-fragment `SpanRect` per wrapped line (the link-band geometry), and the recorder fills it (radius 2) before the
selection fill and the glyphs, so a selection paints over it (text.md §19; registered in README §2.7). The pill is
`CodeSpanFill` = CardBackgroundFillColorSecondary; on the dark page that token is subtle (Labs adds a 1 px stroke a
span band cannot draw — a stroke is *later*). `==mark==` (the markdown-it-mark / pulldown-cmark extension,
`MarkdownExtensions.Mark`) rides the same band in `MarkFill` = SystemFillColorCautionBackground: WinUI has no
text-highlight token; the caution tint reads as a marker in both themes.

### 5.5 Quotes (Labs differs — decided)
Labs: a 4 px bar in `CardStrokeColorDefault` (`MarkdownThemes.cs:112-113`, `MyQuote.cs:35-40`). On the dark page
that stroke is a 0.06-alpha line: invisible. **Decision:** 3 px bar in `ControlStrokeColorStrongDefault` (the stroke
WinUI uses for an unchecked CheckBox border, visible in both themes), rounded to a capsule, `Inset` (12) gap to the
text. Text keeps the primary colour (Labs `QuoteForeground`, :114).

### 5.6 Lists and task boxes
Marker column `ListIndent − XS` = 20 px wide, 4 px gap (Labs `ListGutterWidth` 32, :102; GitHub 2 em). The marker is
a `TextEl` at the body size and line height so its baseline sits on the first line. Task boxes follow the WinUI
`CheckBox` at glyph scale: `AccentFillColorDefault` fill + on-accent check when checked, `ControlAltFillColorSecondary`
+ `ControlStrokeColorStrongDefault` when not, `ControlCornerRadius`; 18 px (line height − 2) so the row keeps the body
line height — no layout jump between a task and a plain item. Read-only unless `OnTaskToggled` is set (then the box is
focusable, hand cursor, `AutomationRole.CheckBox`).

### 5.7 Tables
Labs: `TableBorderBrush` CardStroke 1 px, `TableHeadingBackground` CardBackgroundFillColorDefault, cell padding 4
(`MarkdownThemes.cs:125-127`). The viewer uses 12/8 cell padding (`Inset`, `CellPadY`) — Labs' 4 px is the one Labs
value the owner's 4 px-grid rules would call cramped at 14 px body — and the Fluent card corner (4). Columns are `Auto`
tracks; the sizing rule is the engine's (layout.md §7.2, the CSS auto-table algorithm every studied renderer and
WinUI's own `Grid` Auto columns approximate): when the columns' max-content widths fit, each takes its max-content
width and the frame hugs the table; when they do not, each column gets its longest word plus a share of the slack in
proportion to (max − min), rows follow their tallest wrapped cell, and the frame hugs the column width; when even the
longest words do not fit, every column is its longest word and the table **scrolls horizontally inside its frame**
(`ScrollEl.FitContentWidth`), the frame still inside the content width — never a page overflow, never a word broken
letter by letter, never a row overlapping the next. What was wrong before: the engine measured a grid to the
available width and scaled Auto tracks proportionally when they did not fit (a narrow column shrank to a glyph and
its header broke `T / hi / rd`), the frame measured its grid unconstrained (so it never wrapped and the frame framed
empty space), and an unconstrained grid measure reported 0 height (cells clipped and overlapped). Fixed in the
engine: the min-content probe (`WrapWholeWords` + `TextMetrics.MaxLineWidth`), the auto-table split, the hugging
inline-sized grid, `ScrollEl.FitContentWidth` at measure and arrange, and a probe that leaves no memo trace. The
sample page's own text was checked at 1240, 1100, 960, 820, 780, 620 and 560 px windows (content widths from 832 down
to ~170), dark and light, with real wheel input panning the overflowing table and springing back; see §12.

### 5.8 Images (no WinUI precedent)
Resolved: an `ImageEl` at `ImageWidth` (320) × 4:3, `Fit = Contain`, in a corner-clipped card on `CardSecondary`,
because `ImageEl` needs a box and layout does not yet see a decoded bitmap's natural size (media-pipeline.md owns the
decode; a "size from the decode" layout channel is *later*). Unresolved: a chip with the Picture glyph and the alt
text on `CardSecondary`, which is also the state of every image in untrusted content.

## 6. Extension points (generic, no consumer code in the engine)

### 6.1 Links — the default warning dialog, or the consumer's handler
`OnLinkClicked(MarkdownLinkClick{Url, Title, Syntax})`. `Syntax` is `null` for CommonMark links and autolinks and the
registered name for a consumer syntax, so a vault app can route `wikilink` to a note and everything else to
`OpenUri` without re-parsing the URL. A handler REPLACES the default flow entirely (no `Handled` flag: there is no
default left to suppress).

**Default (no handler) — `MarkdownLinkPolicy.ConfirmAndOpen`, decided 2026-11-02.** A modal `ContentDialog`
(`ContentDialog.Show` on the `Overlay.Service` context; Windows 11 dialog style) in the shape of Outlook's link
warning: title "Open this link?", one line of explanation, then the FULL real destination verbatim in the code face,
selectable, wrapping — a misleading label (`[example.com](https://docs.example.net/elsewhere)`) can never hide where
the click goes. Buttons: **Open** (→ `IPlatformApp.OpenUri`, exactly once) and **Cancel**. The DEFAULT button is
Cancel: Enter on a destination the user has not read must not open it (the safe-default stance of a security prompt;
WinUI's "primary is default" is for ordinary confirmations). Schemes: `http`, `https` and `mailto` may open — a web
address is what a browser launch means, and a mail address goes through the same dialog with mail wording (title
"Write an email?", button **Write**) so the user sees the real recipient before the mail app opens (owner decision
2026-11-02; the first pass refused it). Anything else (`file:` can open local programs through shell handlers,
`javascript:` and custom protocols hand control to whatever registered them, a relative path has no handler) gets
the refusal dialog: title "This link can't be opened", the scheme named (or "no
web address"), the destination still shown, **Close** only. No `OverlayHost` ancestor: the context is the null
service, the policy returns false and nothing opens — never a silent launch. The strings are kit loc keys
(`markdown.*`). Images are untouched by this policy: they still never fetch without a resolver. Verified with real
input (§12).

### 6.2 Inline syntaxes
`MarkdownInlineSyntax(Name, Open, Close, Build)`: delimiter-bounded, tried at every inline position **before** the
CommonMark rules in registration order, so `[[` is claimed ahead of `[`. `Build(inner)` returns any inline node
(`MarkdownLink` for `[[Note|label]]`, `MarkdownEmphasis` for `==mark==`, `null` to decline and fall through). The inner
text is raw (no nested inline parse) — enough for wikilinks, tags, highlights and `%%comments%%`; a syntax that needs
nested inlines or block-level containers (callouts) is *later* (§11). This is the markdown-it "rule" idea at its
smallest: an app registers behaviour, the kit owns the grammar.

### 6.3 Images
`ImageResolver(url) → string?`: a path or URL the `ImageCache` may load (media-pipeline.md). A vault app maps a
relative `assets/x.png` to its folder and returns `null` for anything remote it does not trust.

### 6.4 Tasks — the source is the one truth
The viewer owns NO task state: a box shows what the source says. A click reports `OnTaskToggled(taskIndex, isChecked)`
(`taskIndex` = the item's ordinal among task items in document order, parent before nested children; markers inside
fenced code are not tasks), the consumer rewrites its source, the re-parse shows the new state — one truth, no shadow
state, which is what an Obsidian vault requires. `MarkdownTasks.Toggle(source, taskIndex, isChecked)` does the rewrite
(it flips the one character inside the n-th marker's brackets, walking lines with the parser's marker shape and
skipping fences; `MarkerOffset` exposes the position); a consumer that keeps the string in a `Signal<string>` and binds
the viewer to it (`MarkdownTextBlock.Create(signal)`) gets immediate feedback with no layout jump — the box is the same
size in both states, so only its fill changes. The first-pass bug (a box that stayed checked after the toggle fired)
was exactly a missing write-back: the sample never rewrote its source. The helper no longer scans text: it parses the
source and walks the tree to the N-th task item, whose `MarkerOffset` the parser recorded (item-level source
positions, §2), then flips that one character — so a `- [ ]` in fenced code, indented code, a code span or any other
non-task text can never shift the index, and CRLF sources keep their line endings (verified, §12). With tabs before a
marker the parser's column lands on the right line and the helper re-finds the `[` on that source line.

## 7. Selection, copy, accessibility

### 7.1 Selection (built, scoped)
`SpanTextEl` selection is **per paragraph** (rtb-02: "the drag clamps to the paragraph it anchored in"); the viewer
turns it on for every flow and code line when `IsTextSelectionEnabled` (default true). Verified with real input (§12):
drag, double-click (word), triple-click (the whole flow) each select and Ctrl+C copies the exact text; a plain click
collapses the selection (Ctrl+C then copies nothing); Shift+Arrow does NOT extend a read-only selection (not
implemented in the dispatcher — *later*). Ctrl+C over a read-only selection copied NOTHING before this pass:
`InputDispatcher.Clipboard` was never wired by `AppHost` (only the hooks' clipboard was); fixed at the cause in
`AppHost` — an engine bug every `RichTextBlock` had. Cross-block selection and
"copy as Markdown" (Zed's `copy_as_markdown` rebalances the selected source, `markdown.rs:1139`) need a block-spanning
selection model — text.md §14/§17 own `SelectionState`/`ITextReadSide`; *later*. A hard line break splits a paragraph
into stacked flows (the text seam has no in-paragraph newline for a span flow), which also splits selection there.

### 7.2 Copy
Code cards copy their source through `InputHooks.Clipboard` (the `CodeBlock` seam). Paragraph copy is the engine's
Ctrl+C on a selection.

### 7.3 Accessibility (as-built, with gaps)
Links are hyperlink spans (the engine's hand cursor + click resolution; UIA exposure of inline hyperlinks is whatever
`SpanTextEl` provides today — input-a11y.md owns it). Task boxes: `AutomationRole.CheckBox`. Copy buttons:
`AutomationRole.Button`, focusable. **Gap:** `AutomationRole` has no `Heading`/`Table`/`List` members and
`SpanTextEl` carries no role, so headings and tables are plain text to UIA. Adding `Heading(level)`, `Table`, `Row`,
`Cell`, `List`, `ListItem` roles is input-a11y.md's call — *later*, and the one a11y item that should not slip past
the second pass.

## 8. Large documents and virtualization

Cold parse is linear in the text; a 1 MB note parses in milliseconds on the UI thread. The cost that scales is
**elements**: every block becomes retained nodes at mount. This pass renders eagerly, which is right for a note panel
(hundreds of blocks) and wrong for a 10 000-block log. The virtualization unit is the **top-level block**: the tree is
already a block array, so the *later* step is `Virtual.List` / `ItemsView.CreateBound` over `MarkdownDocument.Blocks`
with per-block height estimates from line counts (virtualization.md owns the substrate; skill rule 12). Zed does the
equivalent with `root_block_starts` (`parser.rs:28`); Streamdown memoises per top-level block
(`parseMarkdownIntoBlocks`, `index.tsx:42`). Until then, a consumer wraps the block in a `ScrollView` and pays the
mount once.

## 9. Editing: the comparison the owner asked for (decision open)

> `MarkdownEditor` now exists as a projection over the source text: [`markdown-editor.md`](markdown-editor.md). The options below are the reasoning that led there; the owner has not yet formally reversed the source + preview choice.

The accepted baseline is a plain `TextBox` (`AcceptsReturn`) plus "Open in Obsidian". Three ways past it:

| Option | What it is | Precedent | Cost on this engine | Risk |
|---|---|---|---|---|
| **A. Read-only + external edit** (baseline) | this viewer; edits happen in Obsidian or a plain `TextBox` | WinUI Gallery, Zed's `markdown_preview` | none beyond this pass | the panel never feels like a note tool |
| **B. Source + preview** | the `TextBox` beside (or stacked with) this viewer, live via `Create(Signal<string>)`; optional sync-scroll | VS Code, Markdig-powered Markdown Editor v2, Zed preview | small: a two-pane layout in the app; sync-scroll needs source positions in the tree (§2, *later*) | two representations of one thing on a narrow panel |
| **C. Live preview (Obsidian / Typora)** | one surface: rendered text, the block under the caret shows its source | Milkdown (`plugin-block`, `plugin-streaming`), Tiptap/ProseMirror, Obsidian Live Preview (CodeMirror 6 + `@codemirror/lang-markdown` decorations) | large: a rich `ITextDocument` editor over `SpanTextEl` flows, caret and IME across blocks, roundtrip-safe rewriting (Markdig `trackTrivia` territory), undo | the biggest text work the engine has not done; `EditableText` is single-flow |

**Decided (owner, 2026-11-02): B.** The engine side is built and verified — a multi-line `TextBox` over a
`Signal<string>` beside `MarkdownTextBlock.Create(signal)` (gallery "Source + preview"): typing, Enter, arrows, undo,
paste and a long paste all re-render live (§12). The two-pane layout belongs to the app. The rest of this section is
kept as the record of the comparison. **Original recommendation:** B for the next pass, C only if the owner wants the
panel to replace Obsidian for writing. B is honest about what the engine has (a solid `TextBox`, a viewer that re-renders per change),
costs one app layout, and keeps the Markdown source canonical — which an Obsidian vault requires anyway (the vault is
the store, GLOSSARY "Vault"). C is the loved experience (Typora, Obsidian, Milkdown's "WYSIWYG that stays Markdown")
but every project that ships it is a full editor framework (ProseMirror, CodeMirror 6) and the engine's editing seam
is a single-flow `EditableText`. If C is the destination, the pre-work is engine-level: a multi-flow document editor
(text.md §16 `ITextDocument` already promises "single AND multi-line"), source positions in the tree, and an
in-paragraph newline for span flows. **Not built in this pass; the owner decides (§13).**

## 10. What the community loves, what it complains about, what this control takes

Each project was read from a shallow clone of its public repository on 2026-11-01 (licences from each repo's
LICENSE file; no code copied — ideas only, which no licence restricts). Paths below are relative to that repository. Complaints are drawn from each project's own
README/CHANGELOG/issue templates in the clone and from well-known public issue threads named below; where a claim is
from memory of a thread rather than a file in the clone, it says so.

### 10.1 Markdig — BSD-2 (xoofx/markdig, `readme.md`)
Loved: speed with low GC pressure, 600+ spec tests, 20+ extensions, **AST with source positions and trivia roundtrip**
(`src/Markdig/Roundtrip.md`), pluggable core ("disable HTML parsing", readme:14). Complaints: the HTML renderer is the
first-class output and UI renderers must walk the AST themselves (Labs' `WinUIRenderer` is ~1 500 lines of that); the
extension pipeline order is a known foot-gun (which extension runs first changes the parse). **Take:** the two-pass,
spec-shaped parser; the pipeline-free, flag-based `MarkdownExtensions`; the rule that a consumer syntax runs before
core rules (Markdig inline parsers register by opening character). **Reject (for now):** source positions/trivia —
until an editor needs them (§9). **Keep in view:** a consumer app may reference Markdig for roundtrip rewriting.

### 10.2 pulldown-cmark — MIT (pulldown-cmark/pulldown-cmark, `README.md`)
Loved: "a bare minimum of allocation and copying", pull/event model, 100 % spec, source maps (`OffsetIter`), the
GFM/footnote/`==mark==` options; Zed builds on it. Complaints: a pull stream is awkward for layouts that need
look-ahead (tables, tight lists) — Zed buffers the events into a `Vec` first (`parser.rs:28`), which is the tree in
disguise. **Take:** the option flags idea; the standard of "fast because it does little". **Reject:** the pull model —
the renderer wants a tree and the parse is cold.

### 10.3 markdown-it — MIT (markdown-it/markdown-it, `README.md`)
Loved: "safe by default", rules you can add or replace, linkify + typographer, the plugin ecosystem. Complaints: the
rule chain and `env` state are implicit (plugins step on each other's `state`), and the typographer silently changes
content. **Take:** the registered-rule idea at its smallest (`MarkdownInlineSyntax`); bare autolinks. **Reject:**
typographer — the engine's font features decide quotes.

### 10.4 react-markdown + remark — MIT (remarkjs/react-markdown, `readme.md`)
Loved: safe by default (no `dangerouslySetInnerHTML`, readme:18-19), **a component per node type** that the app
overrides, `urlTransform` with a protocol whitelist (`lib/index.js:124, :433`), plugins. Complaints: bundle size and
the unified/remark plugin stack's version churn; rendering cost on long documents because the whole tree re-renders.
**Take:** inert-by-default links and images; the shape of `MarkdownLinkClick`. **Later:** per-node-type component
overrides — the natural next extension point once consumers ask for custom block renderers (a callout, a diagram).

### 10.5 Streamdown + remend — Apache-2.0 (vercel/streamdown, `packages/{streamdown,remend}`)
Loved: Markdown **as it streams** — remend closes unterminated `**`, `` ` ``, `[`, `~~` so partial text renders
(`packages/remend/README.md`), per-block memoisation (`parseMarkdownIntoBlocks`), a shared wall-clock stagger so
newly arrived words fade in without overlapping (`lib/animate.ts:5-40`, which cites its own issue #482 on overlap),
pinned-to-bottom scrolling (`lib/use-pinned-scroll.ts`), "static" vs "streaming" modes (`index.tsx:267`). Complaints
(from its changelog in the clone): animation backlog under fast streams (hence `MAX_ANIMATION_BACKLOG_MS`), flicker when
a block's type changes as tokens arrive (a paragraph becoming a list), link definitions arriving after their use
(`parse-blocks.tsx:12-16`). **Take:** nothing yet in code; the whole design is §11.1's starting point and the engine's
retained scene is a better home for it than the DOM (no diffing by key, animate the node). **Reject:** per-word fade as a
default — the owner's motion rule is deliberate motion, not decoration.

### 10.6 Markdown.Avalonia — MIT (whistyun/Markdown.Avalonia, `README.md`)
Loved: a native-control renderer for a .NET UI framework, Markdown → `StyledElement`s, themes via styles. Complaints:
tightly coupled to Avalonia versions (its README is mostly a version-compatibility table) and a regex-based (MdXaml)
parser, not CommonMark. **Take:** the confirmation that a XAML-style renderer over native text runs is the right
level — not an HTML view. **Reject:** regex parsing.

### 10.7 flutter_markdown — BSD-3 (in `flutter/packages`, discontinued 2025)
The package had already been removed from `flutter/packages` before archival, so nothing was read from a clone. From its public API (memory): a `MarkdownStyleSheet` of ~40 `TextStyle`/padding
fields and a `builders` map per block type; its deprecation notice points users at community forks. **Take:** the
style record (`MarkdownStyle` is its analogue, resolved from tokens); the warning that a style record with 40 knobs is
where maintenance went to die — ours has 35 and should not grow without a theme-level reason.

### 10.8 egui_commonmark — Apache-2.0/MIT (lampsitter/egui_commonmark, `README.md`)
Loved: an immediate-mode viewer over pulldown-cmark with a `CommonMarkCache` for images/links, tables, task lists,
footnotes, compile-time Markdown macros. Complaints: immediate-mode re-parses or re-walks per frame unless cached; no
selection. **Take:** the cache boundary (parse once, keep it) — ours is `UseMemo`. **Reject:** per-frame rendering,
which the retained scene makes unnecessary.

### 10.9 CodeMirror 6 `@codemirror/lang-markdown` — MIT (codemirror/lang-markdown)
Loved: an **incremental Lezer parser** with decorations for live preview (what Obsidian builds on), nested languages
in fences. Complaints: writing live-preview decorations is hard (the Obsidian plugin community's most asked question).
**Take:** the proof that live preview is "an editor with a Markdown parser that keeps positions", i.e. §9 option C is
an editor project, not a viewer feature.

### 10.10 Milkdown — MIT (Milkdown/milkdown, `packages/plugins`)
Loved: a plugin-driven WYSIWYG that stays Markdown (remark ↔ ProseMirror), `plugin-streaming` for AI output,
`plugin-block` handles. Complaints (issue tracker, memory): ProseMirror schema conflicts between plugins; theme
changes break between majors. **Take:** the vocabulary for §9 C; `plugin-streaming` as a second precedent for §11.1.

### 10.11 Tiptap — MIT (ueberdosis/tiptap, `README.md`)
Loved: headless, framework-agnostic, 100+ extensions, collaboration. Complaints: Markdown is not its native model
(it is HTML/ProseMirror; Markdown import/export is lossy), Pro extensions behind a subscription. **Take:** "headless"
as the test for this control — no chrome of its own beyond the code card header. **Reject:** HTML as the document model.

### 10.12 Zed — GPL-3.0 (zed-industries/zed, `crates/markdown`)
Loved: a GPU-rendered Markdown element in a retained UI framework (gpui), **cross-block selection with copy-as-Markdown
that rebalances the selected source** (`markdown.rs:1139`, `selection.rs`), copy buttons on code blocks, parsing on a
background task (`markdown.rs:1195-1240` `background_spawn`), `root_block_starts` for block-level work, path ranges in
links (`path_range.rs`), mermaid. Complaints (issues, memory): large Markdown in the agent panel was slow until the
block-level work landed; heading anchors. **Take:** the target feature list for §11.2–11.3 (selection + copy-as-source,
background parse for huge documents). **Licence note:** GPL — ideas only, no code, as with the rest.

## 11. Ideas that stand out on FluentGpu's strengths

Each is marked **this pass / later / speculative**, and bold ideas do not delay the viewer: everything "this pass"
above is built and rendered; nothing below is.

1. **Streaming with no layout jumps** (*built and verified*; `MarkdownOptions.IsStreaming`). The tail-closer, the
   block-index incremental parse (§3.3), an enter fade per new top-level block (`Enter` opacity 0 →
   `MotionTok.StandardEnter`) and a `SizeMode.FlowReveal` clip host around the column (the Expander's disclosure spec,
   `MotionTok.Reveal`; reduced motion snaps by the token's policy). **The snap and its cause (fixed in the engine):**
   the first measurement showed the column's growth landing in one frame. The FLIP/FlowReveal "before" capture
   (`AppHost.CaptureProjections`) ran only on frames the host could PREDICT would reconcile, and
   `HostTimerQueue.AnyDueStateChange` deliberately excludes an interval tick (capturing on every tick had cost the
   seek rail a capture and a projection walk per playing frame). A stream is driven by `UseInterval`: every tick
   re-rendered the block with no capture, so the resize seed had nothing to spring from and every FlowReveal under
   it snapped. Fix (backdrop-effects-animation.md, FLIP capture): after the frame-clock drain queued the tick's work,
   the host opens the capture if none is open AND a COMPONENT-RENDER row is queued
   (`ReactiveRuntime.HasPendingRowOfKind(Rows, RowKindComp)`) AND the tree has bounds-animated nodes — a tick that
   only writes bound values still captures nothing, so the audited cost does not return. Proof (§12): a revealed
   line's clip edge advances 1331→1334→1336→1337→1338→1341→1342 over consecutive frames; the Expander's own reveal is
   unchanged (input-driven path, never touched; re-measured). The original idea: Streamdown's problem is the DOM: a
   block that changes type re-mounts, words appear at once, the page scrolls under the reader. On this engine the block
   column is a retained tree: parse the stream with a remend-style tail-closer (close unterminated `**`/`` ` ``/`[`),
   diff **by block index** (the syntax tree is an array), and let the reconciler keep every settled block's nodes; a
   growing last block reveals with `SizeMode.FlowReveal` (skill rule 9) so the column never snaps, and a pinned-bottom
   scroll (`use-pinned-scroll` idea) rides the scroll engine's pose, not a DOM measurement. Zero allocation on steady
   frames holds because the per-token work is a cold parse of the tail block, not per-frame work. This is the one idea
   no reference does well and the engine's rules make natural; it is also what an AI-assisted note panel would need.
2. **Cross-block selection and copy-as-Markdown** (*later*; needs text.md §14/§17 work). Zed's rebalanced selection
   source is the model. With source positions in the tree (§2) the engine can copy the exact source slice, which no
   HTML-based viewer can do losslessly.
3. **Background parse for huge documents** (*later*). Zed parses off the UI thread; the engine's `UseAsync`/task seams
   can hand a `MarkdownDocument` back and the memo swaps it — a skeleton from `Skel.Region` meanwhile (rule 12).
4. **Source ⇄ rendered transitions** (*speculative*; depends on §9). With source positions, a block can morph between
   its rendered flow and its source text with the engine's `Layout`/FLIP transitions (animation rework §11): the
   Obsidian "caret enters a block, it shows source" moment as deliberate motion instead of a snap. Only worth it if the
   owner picks option C.
5. **Per-span highlight rect** (*later*; engine primitive). One painted rect behind a span gives inline-code pills
   (§5.4), `==mark==` highlights and search hits; text.md §15's selection rect is the shape to generalise.
6. **Heading outline and anchors from the tree** (*this pass, consumer-side*). Because the tree is public, a consumer
   can build an outline, "scroll to heading" (the scroll engine's `ScrollIntoView`), and link graphs today with no
   engine work — Zed's `heading_slugs` is the precedent.
7. **GPU-resident code cards** (*speculative*). A code card is static text in a pan: a `RepaintBoundary` tile makes
   scrolling a long technical note a composite, not a re-record (gpu-renderer.md §13.1). Measure first.

## 12. Verification and remaining work

**Renders** (`--screenshot`, the engine's own D3D12 output): the gallery page in dark and light (`--light`) at 1240
and 1440 wide, page-content and full-shell shots, after each pass; judged by eye against the WinUI resources cited in §5.

**Real input, through the engine's own queue.** A throwaway driver (not committed) mounted the real gallery page on an
`OffscreenSurface` (real D3D12 + DirectWrite, a `HeadlessPlatformApp` clipboard) and fed `HeadlessWindow.QueueInput`
from step files — the same input path the Wavee inspector uses — capturing a PNG after each step. What actually happened:

| Behaviour | Result |
|---|---|
| Drag-select in a paragraph, Ctrl+C | selected and copied `"A Thought is a single thing someone w"` (exact drag extent) |
| Double-click | selected one word, copied `"down,"` |
| Triple-click | selected the whole flow (lines 1–3 of the paragraph, up to the hard break), copied it |
| Plain click after a selection, Ctrl+C | selection collapsed; nothing copied (sentinel untouched) |
| Shift+Right after a click | no selection; **not implemented** for read-only text (dispatcher) |
| Ctrl+C before the fix | copied nothing — `InputDispatcher.Clipboard` unwired in `AppHost`; fixed |
| Hover link / Copy button | Copy button hover fill drawn; a span's hand cursor is not visible in a capture (not verified) |
| Copy button click | clipboard = the exact fenced source; label flips to "Copied" |
| Link clicks with a handler (wikilink, inline, autolink) | handler received `wikilink: Atlas launch`, `link: docs/guide.md`, `link: https://commonmark.org`; `OpenedUris` stayed empty |
| Default link flow, no handler (§6.1) | https link → dialog with the real URL; **Cancel**: closed, `OpenedUris` empty; **Enter** (default = Cancel): closed, empty; **Open**: `OpenedUris` = `https://example.com/guide` exactly once; misleading label `[example.com](https://docs.example.net/elsewhere)` → dialog shows `https://docs.example.net/elsewhere`; `mailto:author@example.com` → mail dialog, **Cancel** opens nothing, **Write** reaches `OpenedUris` once; `file:///C:/notes/todo.md`, `javascript:alert(1)` and `myapp://open?id=1` → refusal naming the scheme, Close only, nothing opened; relative `docs/guide.md` → refusal "no address scheme" |
| Read-only task box (no `OnTaskToggled`) | click changed nothing |
| Task toggle off/on, nested (Dark = 2, Light = 3), after a fenced `- [ ]` decoy (Reviewed = 4) | source rewritten, boxes re-rendered, status line matched; **first run toggled the parent instead of the nested child** — the parser numbered children before their parent; fixed in `ParseList` |
| Editor: type, Enter, arrows, undo, paste, long paste (3 blocks + fence) | all landed in the source and re-rendered live; two Ctrl+Z steps undid three typing bursts (editor grouping); the TextBox scrolled to its caret on the long paste |
| Streaming start / pause | buttons work; `*Atlas l` italic before its closer arrived (tail-closer); `1. Wr` faded in |
| Select a settled block mid-stream, keep streaming | selection held through 12 more ticks; Ctrl+C copied `"Streaming reply"` |
| Scroll mid-stream | page scrolled; stream continued |
| Table sizing at 1240 / 960 / 820 / 780 / 620 / 560 px windows, dark and light | fits by the auto-table split when the maxima do not fit (e.g. 139/151/57/128 in a 476 px pane, rows at their tallest cell), floors at the longest words when the slack is gone (328 px pane), and at 620 (pane ≈ 170 px) overflows inside its frame: the frame stays at the pane edge, a real wheel over the table pans it (`OffsetX` 0 → end, back to 0), both themes; a wheel over a table that fits rubber-bands ≤ 41 px and springs back to 0.5 → 0 after the lift (the scroll runtime's overscroll, not an overflow — content = viewport) |
| Growth spring | **springs** after the engine fix (§11.1): lowest revealed pixel 1331→1334→1336→1337→1338→1341→1342 then 1352→1355→1357→1360→1361 over consecutive frames (36-frame capture); before the fix one 22 px step |
| Expander motion after the fix | controlled Expander toggled by a click: its status label moved 674→676→679→681→683→684→685→686→687 over consecutive frames (still a spring) |
| Task toggles on a CRLF source with a fenced AND an indented `- [ ]` decoy | Parsed (0) off/on, Dark (2), Light (3), Reviewed (4): each flipped its own marker, decoys untouched, CRLF kept |
| `FrameStats.HotPhaseAllocBytes` | idle page 0/60 frames; after a stream ended 0/60; during streaming 23–38 of 90 frames allocate (max 31–72 KB — tail parse + measure fills + re-render, content-change frames); editor idle 792 B once in 30 frames (the editor's, outside this control) |

Not driven: hover cursor shape (not in a capture), reduced motion (not switchable from the driver), touch/pen, IME,
the light theme under input (dark only), cross-paragraph drag (by design per paragraph).

**Remaining** (none blocks the viewer): the streaming growth spring (§11.1); UIA roles for headings/tables/lists
(§7.3 — not selected by the owner, still listed); Shift+Arrow read-only selection; cross-block selection + copy as
Markdown (§7.1); block virtualization (§8); image natural size (§5.8); a stroke on the
inline-code pill (§5.4); footnotes and alerts (§3.5); nested-inline consumer syntaxes (§6.2); inline source positions
(§2); live preview (§9 C) if ever wanted; a wheel over a table that already fits still rubber-bands (the scroll
runtime's wheel overscroll policy, not this control's).

## 13. Owner decisions (most important first)

Taken on 2026-11-02: editing = B (source + preview); streaming is in the charter and its spring is fixed in the
engine; real italics; the highlight band; the Document heading scale; `==mark==` stays caution yellow; links open by
default behind the warning dialog; `MarkdownTasks` on parser positions. Still the owner's:

Taken 2026-11-02 as well: Cancel stays the default button; `mailto:` opens through the dialog; every other scheme stays
refused.

1. **Custom app protocols** — refused (built); an app that wants its own scheme sets `OnLinkClicked`.
2. **Dialog copy** — the three strings are kit loc keys (`markdown.linkOpen*`, `markdown.linkRefused*`); wording is
   the owner's to tune.

## 14. Ownership registration

Registered in `subsystems/README.md` §1 (doc row) and §2.7 (the Markdown syntax tree + extension seams), and in
`SPEC-INDEX.md` §2 ("Markdown viewer contract"). Cross-references: text.md (`SpanTextEl`, selection, italics),
controls.md (the kit), virtualization.md (§8), media-pipeline.md (`ImageCache`), input-a11y.md (§7.3),
theming.md (tokens read at render).
