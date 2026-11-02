# FluentGpu: Subsystem Design Proposal: Markdown editor (`MarkdownEditor`)

> **PROPOSAL, 2026-11-02. Nothing here is built, and nothing has run on FluentGpu.** This is a design for a generic
> WYSIWYG Markdown editor in the shape of WinUI's `RichEditBox`. It would replace the "source + preview" answer in
> `markdown.md` §9 if the owner accepts decision 2 below. Engine facts were read at `de5e6b24f`. Sizes are estimates.

A working browser mock with every state is in [`../markdown-editor-mock.html`](../markdown-editor-mock.html). Download
and open it: type into the note and watch the file beside it. It is browser text with a loose parser of about 100
lines, so it shows the design and proves nothing about the engine.

![The mock in a 460 px panel, with the note on the left and the file on the right](../../media/markdown-editor/mock-wide-dark.png)

## 1. Recommendation

Build `MarkdownEditor` as a **projection over the source text**. The buffer holds the file's exact characters, the
renderer conceals the markers, and every edit is one replace on that text. No tree is ever written back, so nothing
the person did not touch can change.

Markers never appear when the caret arrives, because that reflows text on a caret move and breaks the no-layout-jumps
rule. Raw source is one key away (`Ctrl+/`) at the same offset. The first version covers text, lists, tasks, inline
marks, the selection flyout and undo. Tables, code and images start as islands that open their source in place.

**The main risk:** caret, selection and IME across many text flows have not been done on this engine. A throwaway
spike comes first, and §7 has a plan B if it fails.

## 2. Interaction model

| Area | Design |
|---|---|
| Markers | Hidden in the rendered view. The caret never reveals them. Raw source is the way to see them. |
| Typed Markdown | `**`, `*`, `` ` ``, `~~`, `==` and the line starts `# `, `- `, `1. `, `> `, `- [ ] `, `---`, ` ``` ` take effect when the construct is complete. An opener with no closer stays visible and dimmed while the caret is after it. `Ctrl+Z` removes only the closing delimiter. |
| Shortcuts | `Ctrl+B`, `I`, `E` (code), `Shift+X` (strike), `Shift+H` (highlight), `K` (link), `L` (task), `0` to `6` (block), `/` (source). `Ctrl+U` does nothing and says why: Markdown has no underline. |
| Selection flyout | WinUI's `CommandBarFlyout` shape, top edge left over the selection: Bold, Italic, Strikethrough, Code, Highlight, Link, More. Opens after a mouse, pen or touch selection, never a keyboard one. Takes no focus. Checked commands use the accent pill. |
| Insert menu | `/` at the start of an empty line opens a filtered list. `Enter` replaces `/query` with the block's syntax in one undo step. `Escape` leaves it as text. A footer Insert button opens the same menu. |
| Links | A click puts the caret in the text and opens a card with address, Open and Remove. `Ctrl+click` follows through `OnLinkClicked` or the existing warning dialog. A reference link keeps its definition line. |
| Consumer links | The engine knows only "a consumer inline syntax plus a completion source". A `[[wikilink]]` is two registrations in the consumer, and is one caret stop. An alias is never rewritten. |
| Tasks | The box changes one character at `MarkerOffset`, with no layout change. `Enter` continues with the sibling's bullet; on an empty item it leaves the list. |
| Tables | Phase 1: one selectable object whose source opens in place. Phase 3: cell editing that writes only the touched cell. |
| Code | The `MarkdownCodeCard` look, with a plain monospace body. ` ``` ` plus `Enter` also writes the closing fence, so the note never flips to code mid-typing. |
| Images | One object in the reserved 320 by 4:3 box, so loading moves nothing. Paste or drop goes through a consumer hook. |
| Undo | One history of source edits for the note, restoring exact caret and selection. Bursts coalesce; paste, block changes and closing delimiters seal their own step. |
| IME | A provisional span in the active flow. While it lasts: no parse, no conversion, no menus, no signal write. Commit is one undo step. |
| Selection | One anchor and one caret, both source offsets, across blocks. Delete removes the source range and re-emits a marker only when the cut orphans one. |
| 460 px panel | No toolbar. Flyout, shortcuts and a footer carry everything. |
| Wide window | A top command bar in the shape of WinUI Gallery's `RichEditBox` page, and a text column capped near 660 px. |

## 3. Visual design

Values are WinUI resources, exposed as `Tok.*`, read at render and never cached.

![Editor states, dark and light, on a dark page](../../media/markdown-editor/states-dark.png)

![Editor states, dark and light, on a light page](../../media/markdown-editor/states-light.png)

## 4. Architecture

```
keys, chars, IME, pointer  ->  one focus root  ->  command  ->  (start, removed, inserted)
                                                                         |
                                                                         v
SourceBuffer (new)     the file's characters, including newlines, BOM and final-newline state
        |
MarkdownParser         exists; gains an inline source map and front matter; re-parses the edited block range
        |
Projection (new)       per leaf block: display string, display-to-source map, TextSpans
        |              a pure function of the source, independent of the caret
Scene                  one SpanTextEl flow per leaf, bound through Prop<TextSpans>; TextEditState paints caret
```

### Round-trip rules

1. **R1** Nothing is serialized. A save writes the buffer.
2. **R2** A command touches its own range only. Bold writes two markers. A task toggle writes one character.
3. **R3** New syntax copies its neighbours: the sibling's bullet and number style, the note's emphasis marker.
4. **R4** Inserted newlines use the note's dominant ending. Mixed endings stay mixed. BOM and final newline never change.
5. **R5** Front matter is a protected range: no caret stops, excluded from `Ctrl+A`, reached by no edit.
6. **R6** Syntax the parser does not model stays literal text or an island.
7. **R7** A marker is written only if the result still parses as that mark.

Verification follows the workspace rule of no tests: Debug assertions that bytes outside each replace are unchanged,
and a corpus of untidy notes through a throwaway driver that is not committed.

### No layout jumps

| Event | What moves | How |
|---|---|---|
| Caret moves | Nothing | No reveal. This is the reason for the decision. |
| `**` closes | The line shortens by the marker width | `SizeMode.FlowReveal` if the line count changes |
| `# ` converts | Text size at once, height eases, blocks below glide | `LayoutTransition` with `SizeMode.Reflow` |
| Task toggled | Fill only | Fixed box size |
| Island opens | Height eases | `SizeMode.FlowReveal` |
| Image loads | Nothing | Reserved box |
| Source toggle | Everything but the caret line | Anchored on the caret line, short crossfade |

### Proposed API

```csharp
// Proposed, not built. Generic: nothing here names a vault or a note.
MarkdownEditor.Create(Signal<string> source, MarkdownEditorOptions? options = null)

public sealed record MarkdownEditorOptions {
    public MarkdownOptions Markdown { get; init; }
    public Prop<string> Placeholder { get; init; }
    public IReadOnlyList<MarkdownCompletionSource>? Completions { get; init; }
    public IReadOnlyList<MarkdownInsertCommand>? InsertCommands { get; init; }
    public Func<PastedContent, string?>? OnPasteContent { get; init; }
    public Action<int, string, string>? OnEdit { get; init; }          // start, removed, inserted
    public Func<MarkdownFrontMatter, Element?>? FrontMatterSlot { get; init; }
    public MarkdownEditorChrome Chrome { get; init; }                  // Surface (panel) or Field (RichEditBox look)
    public MarkerReveal Reveal { get; init; } = MarkerReveal.Never;
}
```

## 5. Findings from reading the code

These were read, not executed.

- The low level is closer than `markdown.md` §9 suggests: read-only selection already drives `HitTestText`,
  `GetRangeRects` and `SetTextEditRects` on span flows. A caret on a span flow has not been tried.
- `ITextDocument` from `text.md` §16 does not exist in code. The real thing is `EditDocument` plus `TextEditCore`.
- `EditDocument` stores `\r` only and forbids `\n`, so it cannot hold a file as written. Today's `TextBox` path appears
  to write that `\r` text back through the signal on an edit, so a source + preview consumer should check its newlines.
- Leading front matter parses as a thematic break plus a setext heading.
- Inline positions are harder than block ones: tabs are expanded and container prefixes stripped before parsing.
- Incremental parse is append-only today, so mid-note edits need a new window.

## 6. Risks

| Risk | Level | Note |
|---|---|---|
| Caret and selection across many `SpanTextEl` flows | High | Untried. The spike answers it. |
| IME over hidden markers | High | Needs real IMEs by hand; the harness cannot supply them. |
| Accessibility | High | `Uia/Placeholder.cs` is a scaffold with no text pattern, so a screen reader would see almost nothing. |
| Inline source map | Medium | Tabs, containers, lazy continuation, entities. |
| Round trip with no tests | Medium | Rests on the design, Debug assertions and the throwaway driver. |

## 7. Phases

| Phase | Gets you | New lines (estimate) | Can it drop? |
|---|---|---|---|
| Spike | Throwaway gallery page: one span flow with a caret and hidden markers, caret across two flows, a real IME by hand. Go or no-go. | 300 to 500, not kept | No |
| 1. Writing | Text, headings, lists, tasks, quotes, marks, typed Markdown, shortcuts, flyout, undo, cross-block selection, source toggle, front matter. Islands for the rest. | 4,000 to 6,000 | No |
| 2. Inserting | Insert menu, completion, link card, code editing, image card, paste rules. | 1,500 to 2,000 | Image card and paste hook can wait |
| 3. Tables | Cell editing, `Tab`, add row and column. | 1,000 to 1,500 | Yes |
| 4. Long notes, access | Block virtualization, mid-note parse, UIA text pattern. | 2,000 to 4,000 | Virtualization yes |
| 5. Extras | Reveal mode, HTML paste, block handles, footnotes, callouts. | Small to medium each | All of it |

**Rejected:** a tree plus serializer (fidelity dies in the serializer, as Milkdown's patches and Tiptap's changelog
show), a tree with trivia, reveal at the caret as the default, one giant text flow, and a web view.

**Plan B if the spike fails:** the block under the caret swaps to a plain `EditableText` showing its source. It keeps
the round-trip rules, but jumps on every block entry and has no cross-block selection, so it is a fallback only.

## 8. Decisions for the owner

1. Markers: never revealed (recommended), or revealed at the caret?
2. Reverse the 2026-11-02 source + preview choice? Recommended yes, keeping raw source as a toggle.
3. Front matter in the raw view: read-only by default (recommended), or editable?
4. Does a single newline in a paragraph render as a space (CommonMark) or a break? A break needs an in-paragraph newline in the engine.
5. Accept phase 1 with tables, code and images as islands? Recommended yes.
6. Fund the UIA text pattern, and when? Recommended deciding before phase 1 ships.
7. Link click: caret and card, with `Ctrl+click` to follow (recommended), or open at once?
8. Ship in `FluentGpu.Controls` or as a separate package? No recommendation.

## 9. Not verified

- Nothing ran on FluentGpu. In the mock, block changes snap where the engine would ease.
- Typora, Obsidian and Notion were not opened. That evidence is web pages and memory, from small samples.
- Reference repositories were read in part and never built. No code was copied.
- Accent ramp values are from memory. Sizes and the 4 ms per key target are estimates.
