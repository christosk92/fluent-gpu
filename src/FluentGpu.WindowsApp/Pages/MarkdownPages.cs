using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── MarkdownEditor: the WYSIWYG editor, a projection over the source text (docs/design/subsystems/markdown-editor.md) ──

[GalleryPage("MarkdownEditor", "MarkdownEditor", "Text", Icon = Icons.Edit, Level = GalleryLevel.RealWorld,
    Keywords = ["markdown", "editor", "wysiwyg", "rich text", "richeditbox", "notes"])]
sealed partial class MarkdownEditorPage : Component
{
    // Deliberately untidy: CRLF and LF, `*` bullets, __strong__, a setext heading, a reference link, trailing spaces. It stays so.
    const string Untidy =
        "---\r\ntitle: Atlas launch\r\n---\r\n\r\n" +
        "Atlas launch\r\n============\r\n\r\n" +
        "Draft for the __launch__ on *Monday*. See the [rollout guide][guide] and `dry-run` notes, with ==first draft== marked.   \r\n\r\n" +
        "* Freeze the copy\r\n* Book the room\r\n\r\n" +
        "- [x] Confirm the pilot group\r\n- [ ] Review risks\r\n\r\n" +
        "> Keep the first release small.\r\n\r\n" +
        "```sh\r\natlas release --dry-run\r\n```\r\n\r\n" +
        "| Item | Due |\r\n|---|---|\r\n| Copy | Mon |\r\n\r\n" +
        "[guide]: https://example.com/rollout \"Rollout\"\r\n";

    static readonly Signal<string> _panel = new(Untidy);
    static readonly Signal<string> _wide = new(Untidy);
    static readonly Signal<string> _links = new("Send the draft to [[Bulletin list|the list]] and type two brackets for more: \n");
    static readonly Signal<string> _empty = new("");

    static readonly string[] Notes = ["Bulletin list", "Bulletin schedule", "Atlas launch", "Risks", "Rollout"];

    public override Element Render() => GalleryPage.Shell("MarkdownEditor",
        "Write Markdown without seeing it. The file is the document: the markers are hidden, every edit is one replace on the text, and nothing " +
        "you did not touch changes. Ctrl+/ shows the source at the same place.",
        ExampleCard.Show(PanelSample),
        ExampleCard.Show(WideSample),
        ExampleCard.Show(ConsumerSample),
        ExampleCard.Show(EmptySample));

    static Element Sized(float width, float height, Element child) => new BoxEl { Direction = 1, Width = width, Height = height, Children = [child] };

    [Sample("In a 460 px panel", Description = "No toolbar: a selection flyout, shortcuts and a footer carry everything. The sample file is deliberately untidy and stays so.")]
    static Element Panel() => VStack(8,
        Sized(460f, 420f, MarkdownEditor.Create(_panel, new MarkdownEditorOptions { Chrome = MarkdownEditorChrome.Field })));

    [Sample("In a wide window", Description = "A command bar, and a text column capped for reading.")]
    static Element Wide() => new BoxEl { Direction = 1, Height = 420f, Children = [MarkdownEditor.Create(_wide, new MarkdownEditorOptions { Chrome = MarkdownEditorChrome.Field, CommandBarMinWidth = 560f })] };

    [Sample("Consumer syntax and completion", Description = "Two registrations in the app: an inline syntax and a completion source. The engine knows neither names nor notes. A link is one caret stop and its alias is never rewritten.")]
    static Element Consumer() => Sized(460f, 200f, MarkdownEditor.Create(_links, new MarkdownEditorOptions
    {
        Chrome = MarkdownEditorChrome.Field,
        Markdown = new MarkdownOptions
        {
            InlineSyntaxes =
            [
                new MarkdownInlineSyntax("wikilink", "[[", "]]", inner =>
                {
                    int bar = inner.IndexOf('|');
                    return new MarkdownLink([new MarkdownText(bar < 0 ? inner : inner[(bar + 1)..])], bar < 0 ? inner : inner[..bar], "", "wikilink");
                }),
            ],
        },
        Completions =
        [
            new MarkdownCompletionSource("[[", "]]", q =>
            {
                var rows = new List<MarkdownCompletion>();
                foreach (var n in Notes) if (n.Contains(q, StringComparison.OrdinalIgnoreCase)) rows.Add(new MarkdownCompletion(n, n));
                return rows;
            }),
        ],
    }));

    [Sample("An empty note", Description = "One line of placeholder, and / for the insert menu.")]
    static Element Empty() => Sized(460f, 160f, MarkdownEditor.Create(_empty, new MarkdownEditorOptions
    {
        Chrome = MarkdownEditorChrome.Field,
        Placeholder = "Write a note, or type / to insert",
    }));
}

// ── MarkdownTextBlock: the read-only CommonMark + GFM viewer (docs/design/subsystems/markdown.md) ─────────────────

[GalleryPage("MarkdownTextBlock", "MarkdownTextBlock", "Text", Icon = Icons.Document, Level = GalleryLevel.RealWorld,
    Keywords = ["markdown", "commonmark", "gfm", "rich text", "wikilink", "streaming"])]
sealed partial class MarkdownTextBlockPage : Component
{
    static readonly Signal<string> _lastLink = new("—");
    static readonly Signal<string> _lastTask = new("—");

    public override Element Render() => GalleryPage.Shell("MarkdownTextBlock",
        "Render CommonMark and GitHub-flavoured Markdown with the Fluent type ramp, tokens and corners. A link asks before " +
        "it opens (http, https and mailto only, the real destination shown; a consumer handler replaces that flow), images never " +
        "fetch without a resolver, and custom inline syntax is the consumer's.",
        ExampleCard.Show(DocumentSample),
        ExampleCard.Show(ExtensionsSample),
        ExampleCard.Show(SourcePreviewSample),
        ExampleCard.Show(StreamingSample));

    [Sample("A demanding document", Description = "Headings on the document scale, italics, inline-code pills, ==mark==, nested and loose lists, task items, a table, fenced code, quotes, links, an unresolved image, entities and escapes.")]
    static Element Document() => MarkdownTextBlock.Create(SampleText, new MarkdownOptions
    {
        OnLinkClicked = link => _lastLink.Value = link.Url,
    });

    // The task source is the one truth: a toggle rewrites it (MarkdownTasks.Toggle flips the N-th marker through the
    // parser's positions) and the re-parse shows the new state. The fenced and indented `- [ ]` are decoys: not tasks,
    // so they never shift the index. CRLF line endings on purpose — the rewrite must keep them.
    static readonly Signal<string> _tasks = new(
        "Fan out [[Atlas launch]] into [[Bulletin]]; see the [guide](docs/guide.md) or <https://commonmark.org>.\r\n\r\n" +
        "- [x] Parsed\r\n- [ ] Rendered in both themes\r\n  - [ ] Dark\r\n  - [ ] Light\r\n\r\n```md\r\n- [ ] not a task: inside a fence\r\n```\r\n\r\n" +
        "    - [ ] not a task: indented code\r\n\r\n- [ ] Reviewed by the owner");

    [Sample("Consumer syntax, link routing and task toggles", Description = "A [[wikilink]] syntax registered by the app, every click reported back, checkboxes made interactive by OnTaskToggled — the toggle rewrites the source with MarkdownTasks.Toggle.")]
    static Element Extensions() => VStack(12,
        MarkdownTextBlock.Create(_tasks,
            new MarkdownOptions
            {
                InlineSyntaxes =
                [
                    new MarkdownInlineSyntax("wikilink", "[[", "]]", inner =>
                    {
                        int bar = inner.IndexOf('|');
                        string target = bar < 0 ? inner : inner[..bar];
                        string label = bar < 0 ? inner : inner[(bar + 1)..];
                        return new MarkdownLink([new MarkdownText(label)], target, "", "wikilink");
                    }),
                ],
                OnLinkClicked = link => _lastLink.Value = (link.Syntax ?? "link") + ": " + link.Url,
                OnTaskToggled = (i, on) =>
                {
                    _tasks.Value = MarkdownTasks.Toggle(_tasks.Peek(), i, on);
                    _lastTask.Value = $"task {i} → {(on ? "done" : "open")}";
                },
            }),
        GalleryPage.LiveText(() => "Last link: " + _lastLink.Value),
        GalleryPage.LiveText(() => "Last task: " + _lastTask.Value));

    static readonly Signal<string> _source = new(
        "# Source and preview\n\nEdit the *Markdown* on the left; the viewer re-parses once per change — the **source + preview** " +
        "editing model (markdown.md §9, option B).\n\n- [ ] a task\n- `code` and ==mark==\n\n" +
        "No handler here, so a link asks first: [read the guide](https://example.com/guide), a misleading " +
        "[example.com](https://docs.example.net/elsewhere) label, [email the author](mailto:author@example.com), " +
        "a [local file](file:///C:/notes/todo.md), a [script](javascript:alert(1)), an [app link](myapp://open?id=1) and a [relative one](docs/guide.md).\n\n" +
        "| A table wider than this column | Second column with a long header | Third | Fourth column header text |\n" +
        "|---|---|---|---|\n| cell content that is fairly long | more cell content here | x | and the last cell |\n");

    [Sample("Source + preview", Description = "The engine side of the editing model: a multi-line TextBox over a Signal<string>, the viewer bound to the same signal. No OnLinkClicked: the default link dialog shows the real destination (http/https open, anything else is refused).")]
    static Element SourcePreview() => HStack(16,
        TextBox.Create(_source, options: new TextBox.TextBoxOptions { AcceptsReturn = true, Width = 340f, Height = 220f }),
        new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [MarkdownTextBlock.Create(_source)] });

    [Sample("Streaming", Description = "AI output arriving token by token: unterminated **bold** and `code` are closed in the tail, only the last block re-parses, the column grows under the flow-reveal spring and new blocks fade in — no layout jump; reduced motion snaps.")]
    static Element Streaming() => Embed.Comp(() => new StreamingDemo());

    sealed class StreamingDemo : Component
    {
        readonly Signal<string> _text = new("");
        readonly Signal<bool> _running = new(false);
        int _cursor;

        public override Element Render()
        {
            bool running = _running.Value;
            UseInterval(() =>
            {
                if (_cursor >= StreamText.Length) { _running.Value = false; return; }
                // 2-6 characters per tick, the shape of a token stream.
                int step = 2 + (_cursor * 7) % 5;
                int end = System.Math.Min(StreamText.Length, _cursor + step);
                _cursor = end;
                _text.Value = StreamText[.._cursor];
            }, 30f, enabled: running);
            return VStack(12,
                HStack(8,
                    Button.Create(running ? "Pause" : _cursor >= StreamText.Length ? "Done" : "Stream", () => { if (_cursor < StreamText.Length) _running.Value = !_running.Peek(); }),
                    Button.Create("Reset", () => { _running.Value = false; _cursor = 0; _text.Value = ""; }),
                    GalleryPage.LiveText(() => $"{_text.Value.Length}/{StreamText.Length} chars")),
                new BoxEl
                {
                    MinHeight = 120f, AlignSelf = FlexAlign.Stretch, Padding = Edges4.All(Spacing.M), Corners = Radii.OverlayAll,
                    Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                    Children = [MarkdownTextBlock.Create(_text, new MarkdownOptions { IsStreaming = true })],
                }) with { Grow = 1f, Shrink = 1f, MinWidth = 0f };   // the example card lays its sample in a row
        }
    }

    const string StreamText = """
        ## Streaming reply

        Here is a **plan** for the *Atlas* launch, with `three` steps and a ==highlight==:

        1. Write the announcement — short, in the author's own words.
        2. Fan it out into [[Bulletin]] and [[Atlas]].
        3. Review on Friday; park what does not fit.

        ```csharp
        var doc = MarkdownParser.Parse(text, options, previous, previousText);
        ```

        > Only the last block re-parses; everything above is reused as is.

        | Step | Owner |
        |------|-------|
        | 1    | author |
        | 2    | AI     |

        - [x] Streamed
        - [ ] Reviewed

        That is the whole reply.
        """;

    const string SampleText = """
        # Notes, in Markdown

        A *note* is a single thing someone wrote down, in their own words. This paragraph has **strong**, *emphasis*,
        ***both***, `inline code`, ~~struck~~ text, ==marked text==, an [inline link](https://example.com "Example"),
        a [reference link][spec], an autolink <https://commonmark.org> and a bare one: www.example.com/docs.
        Entities: &amp; &copy; &rarr; &#8212; escapes: \*not emphasis\* and \# not a heading.
        A hard break (backslash form) follows this line\
        and the next line is a soft break
        rendered as a space.

        ## Lists

        1. Ordered, starting at one
        2. With a nested bullet list
           - Tight item
           - Another, with `code`
             - Third level
        3. Back to the ordered list

        - [x] A finished task
        - [ ] An open task with **strong** text
        - [ ] A task that wraps onto a second line because it keeps going well past the column width of the panel

        Loose list:

        - First paragraph of a loose item.

          Second paragraph of the same item.

        - Second item

        ### Code

        ```csharp
        public static Element Create(string text, MarkdownOptions? options = null)
            => Embed.Comp(new Props(text, options ?? MarkdownOptions.Default), () => new MarkdownTextBlock());
        ```

            indented code block
            keeps its spaces

        #### Quotes and rules

        > Keep the source as the one truth.
        >
        > — *the README*, with a lazy
        continuation line and a nested quote:
        > > Inner quote

        ---

        ##### Table

        | Status  | Meaning                          | Owner  |
        |:--------|:---------------------------------|-------:|
        | Inbox   | New, not yet reviewed            | author |
        | Parked  | Kept but not for now             | author |
        | Doing   | In progress, `#doing`            | author |

        ###### Images and HTML

        ![Board screenshot](board.png "The board")

        <div class="note">raw HTML stays literal</div>

        Setext heading
        --------------

        The end.

        [spec]: https://spec.commonmark.org/ "CommonMark spec"
        """;
}
