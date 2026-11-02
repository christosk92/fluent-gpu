using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Theming;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.Controls;

/// <summary>The editor's frame: <see cref="Surface"/> sits on the page or panel, <see cref="Field"/> has the RichEditBox look.</summary>
public enum MarkdownEditorChrome : byte { Surface, Field }

/// <summary>One row a consumer adds to the insert menu. <see cref="Syntax"/> uses <c>\n</c> for a line break; the caret lands at
/// <see cref="CaretOffset"/> in it, or after it when negative.</summary>
public sealed record MarkdownInsertCommand(string Label, string Syntax, int CaretOffset = -1)
{
    /// <summary>The Markdown the row writes, shown beside its label to teach the syntax. Null shows <see cref="Syntax"/>'s first line.</summary>
    public string? Hint { get; init; }
}

/// <summary>A row of a consumer completion list: <see cref="Insert"/> is written between the trigger and the closer.</summary>
public sealed record MarkdownCompletion(string Label, string Insert, string? Detail = null);

/// <summary>A consumer completion: typing <see cref="Trigger"/> opens a list from <see cref="Query"/>; accepting writes
/// trigger + insert + <see cref="Close"/>. A <c>[[wikilink]]</c> is this plus a <see cref="MarkdownInlineSyntax"/>, both in the consumer.</summary>
public sealed record MarkdownCompletionSource(string Trigger, string Close, Func<string, IReadOnlyList<MarkdownCompletion>> Query);

/// <summary>Options of <see cref="MarkdownEditor"/>. Read at mount.</summary>
public sealed record MarkdownEditorOptions
{
    public static readonly MarkdownEditorOptions Default = new();
    /// <summary>Consumer syntaxes, image resolver, link handler, style.</summary>
    public MarkdownOptions Markdown { get; init; } = MarkdownOptions.Default;
    /// <summary>Shown in an empty note.</summary>
    public Prop<string> Placeholder { get; init; } = "";
    public IReadOnlyList<MarkdownCompletionSource>? Completions { get; init; }
    public IReadOnlyList<MarkdownInsertCommand>? InsertCommands { get; init; }
    /// <summary>Every replace: (start, removed, inserted). Enough to save incrementally.</summary>
    public Action<int, string, string>? OnEdit { get; init; }
    /// <summary>The slot for front matter, which the editor never edits. Null shows a quiet one-line note of it.</summary>
    public Func<string, Element?>? FrontMatterSlot { get; init; }
    public MarkdownEditorChrome Chrome { get; init; } = MarkdownEditorChrome.Surface;
    /// <summary>Hands the consumer the document, for its own commands and checks.</summary>
    public Action<MarkdownEditModel>? OnModel { get; init; }
    /// <summary>Text column cap in a wide window.</summary>
    public float ReadingWidth { get; init; } = 660f;
    /// <summary>At or above this width the command bar shows; below it the footer carries Insert and Source.</summary>
    public float CommandBarMinWidth { get; init; } = 600f;
}

/// <summary>
/// A WYSIWYG Markdown editor in the shape of WinUI's RichEditBox: a projection over the source text
/// (docs/design/subsystems/markdown-editor.md). The buffer holds the file's exact characters, the renderer conceals the
/// markers, and every edit is one replace on that text, so nothing the person did not touch can change.
///
/// <para><b>Update path.</b> A caret move writes only the two flows' <see cref="TextEditState"/>: no render, no layout. A text
/// edit writes the source signal once and re-renders the note's element tree (keyed, so only the edited flow's node changes);
/// the visuals resync in the frame's after-animations hook, on the new layout. Nothing runs on a steady frame.</para>
///
/// <para><b>Screen readers.</b> The focus root is announced as one text field and no text pattern is exposed: a screen reader
/// does not get the note's content, caret or selection (markdown-editor.md decision 6, not funded).</para>
/// </summary>
public sealed class MarkdownEditor : Component
{
    /// <summary>An editor over <paramref name="source"/>. The signal holds the file; a write from outside becomes one minimal replace.</summary>
    public static Element Create(Signal<string> source, MarkdownEditorOptions? options = null)
        => Embed.Comp(() => new MarkdownEditor { Source = source, Options = options ?? MarkdownEditorOptions.Default });

    public required Signal<string> Source;
    public required MarkdownEditorOptions Options;

    private MarkdownEditModel _model = null!;
    private InputHooks _hooks = null!;
    private IOverlayService _overlay = null!;
    private Signal<int> _epoch = null!;       // re-renders the note's tree
    private Signal<int> _selEpoch = null!;    // re-renders only the command bar's checked states
    private Signal<string> _status = null!;
    private readonly ScrollHandle _scroll = new();
    private NodeHandle _contentNode, _rootNode, _scrollNode;
    private string _seen = "";
    private bool _focused;
    private bool _syncPending;
    private bool _follow;
    private float _goalX = float.NaN;
    private float _viewW, _viewH;
    private Signal<float> _viewHeight = null!;
    private Signal<bool> _wide = null!;
    private Signal<bool> _focusSig = null!;
    private float _anchorCaretY = float.NaN;
    private bool _sourceToggled;
    private readonly Dictionary<long, NodeHandle> _wrappers = new();
    private readonly Dictionary<long, Action<NodeHandle>> _captures = new();
    private readonly Dictionary<long, ScrollHandle> _codeScrolls = new();
    private readonly List<NodeHandle> _painted = new();
    private NodeHandle _caretNode;
    private int _lastAnchor = -1, _lastCaret = -1;
    private long _dimFlow = -1;
    private readonly ImeSession _ime;
    private MarkdownStyle _style = null!;

    public MarkdownEditor() => _ime = new ImeSession(this);

    // ── render ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public override Element Render()
    {
        _hooks = UseContext(InputHooks.Current);
        _overlay = UseContext(Overlay.Service);
        _epoch = UseSignal(0);
        _selEpoch = UseSignal(0);
        _status = UseSignal("");
        _viewHeight = UseSignal(0f);
        _wide = UseSignal(false);
        _focusSig = UseSignal(false);
        _ = _epoch.Value;
        string text = Source.Value ?? "";
        if (_model is null)
        {
            _model = new MarkdownEditModel(text, Options.Markdown) { OnEdit = Options.OnEdit };
            _seen = text;
            Options.OnModel?.Invoke(_model);
        }
        else if (!ReferenceEquals(text, _seen))
        {
            _seen = text;
            if (!string.Equals(text, _model.Text, StringComparison.Ordinal)) _model.SetExternalText(text);
        }
        UseEffect(() =>
        {
            var hooks = _hooks;
            hooks.SetAfterAnimations(this, OnAfterAnimations);
            return () =>
            {
                hooks.SetAfterAnimations(this, null);
                _scroll.AutoScroll(0d);
                CloseFlyouts();
            };
        }, DepKey.Empty);

        _style = Options.Markdown.Style ?? MarkdownStyle.Fluent();
        _syncPending = true;
        bool wide = _wide.Value;
        float minHeight = _viewHeight.Value;

        var p = _model.P;
        var column = new List<Element>(p.Views.Count);
        // An edit that made or merged a block lands at once: the block it splits would ease while the new one is already there, and the note below would bounce.
        _structural = p.Views.Count != _lastViewCount;
        _lastViewCount = p.Views.Count;
        long dimId = DimFlowId();
        long selObject = SelectedObjectId();
        _viewsById.Clear();
        for (int i = 0; i < p.Views.Count; i++)
        {
            var v = p.Views[i];
            _viewsById[v.Id] = v;
            // A block re-renders when its text, its neighbours' gap or its live state changes. The rest of a long note costs one compare each.
            var props = new BlockProps(v.Id, v.Signature, i > 0, FlowOfViewIs(v, dimId) ? dimId : -1, FlowOfViewIs(v, selObject) ? selObject : -1,
                _ime.Active && FlowOfViewIs(v, _ime.FlowId) ? _ime.Text + "|" + _ime.Caret.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            column.Add(Embed.Comp(props, () => new BlockHost { Owner = this }) with { Key = K("c", v.Id) });
        }
        PruneNodes(p);

        bool fieldChrome = Options.Chrome == MarkdownEditorChrome.Field;
        float padX = fieldChrome ? 10f : Spacing.L;
        float padY = fieldChrome ? 6f : Spacing.M;
        var body = new BoxEl
        {
            Key = _model.IsRaw ? "raw" : "note",
            Direction = 1,
            Gap = _model.IsRaw ? 0f : _style.BlockGap,
            Grow = 1f, Shrink = 1f, MinWidth = 0f,
            MaxWidth = wide ? Options.ReadingWidth : float.NaN,
            // The short crossfade of the source toggle. A note that just mounted is simply there.
            Enter = _sourceToggled ? new EnterExit(Opacity: 0f, Active: true) : default,
            Transition = _sourceToggled ? MotionTok.StandardEnter : default,
            Children = column.ToArray(),
        };
        var content = new BoxEl
        {
            Direction = 0,
            Justify = FlexJustify.Center,
            MinHeight = minHeight,
            Padding = new Edges4(padX, padY, padX, padY),
            // The editor's surface is not a button: no auto-tint on hover or press.
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            Focusable = true,
            Role = AutomationRole.Text,
            Cursor = CursorId.IBeam,
            OnRealized = h => _contentNode = h,
            OnFocusChanged = HandleFocus,
            OnKeyDown = HandleKey,
            OnCharInput = HandleChar,
            OnPointerPressed = HandlePressed,
            OnPointerReleased = HandleReleased,
            OnDrag = HandleDrag,
            OnPointerMoveWithin = HandleMoveWithin,
            OnContextRequested = HandleContext,
            Children = [body],
        };
        var scroller = new ScrollEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f,
            Handle = _scroll,
            OnRealized = h => _scrollNode = h,
            Content = content,
        };

        var rootKids = new List<Element>(3);
        if (wide) rootKids.Add(Embed.Comp(() => new Bar { Owner = this, Wide = true }));
        rootKids.Add(scroller);
        if (!wide) rootKids.Add(Embed.Comp(() => new Bar { Owner = this, Wide = false }));

        var fs = TextBox.DefaultFieldStyle;
        var fill = fieldChrome ? fs.Fill : ColorF.Transparent;
        var focusFill = fieldChrome ? fs.FocusedFill : ColorF.Transparent;
        var focusedSig = _focusSig;
        if (fieldChrome)
        {
            // WinUI's focused field: the accent bottom bar. Always laid out, so focus moves nothing; only its colour binds.
            var bar = fs.FocusBar;
            rootKids.Add(new BoxEl { Height = fs.FocusBarThickness, Shrink = 0f, HitTestVisible = false, Fill = Prop.Of(() => focusedSig.Value ? bar : ColorF.Transparent) });
        }
        return new BoxEl
        {
            Direction = 1,
            Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
            // The note's surface does not change under the pointer: a whole page of text lightening reads as a smudge.
            Fill = Prop.Of(() => focusedSig.Value ? focusFill : fill),
            BorderBrush = fieldChrome ? fs.BorderBrush : null,
            BorderWidth = fieldChrome ? fs.BorderWidth : 0f,
            Corners = fieldChrome ? CornerRadius4.All(fs.Radius) : default,
            ClipToBounds = fieldChrome,
            OnRealized = h => _rootNode = h,
            OnBoundsChanged = HandleRootBounds,
            Children = rootKids.ToArray(),
        };
    }

    private readonly Dictionary<long, MdView> _viewsById = new();

    private sealed record BlockProps(long Id, string Signature, bool HasBlockAbove, long DimFlow, long SelectedObject, string Ime);

    /// <summary>One top-level block, so an edit re-renders the block it touched and not the note.</summary>
    private sealed class BlockHost : Component
    {
        public required MarkdownEditor Owner;
        public override Element Render()
        {
            var p = UseProps<BlockProps>();
            return Owner._viewsById.TryGetValue(p.Id, out var v) ? Owner.TopLevel(v, p.HasBlockAbove ? 1 : 0) : new BoxEl();
        }
    }

    private long DimFlowId()
    {
        var f = _model.CurrentFlow;
        if (f is not null) foreach (var r in f.Runs) if ((r.Style & MdRunStyle.Dim) != 0) return f.Id;
        return -1;
    }

    /// <summary>Flow ids carry their block's id in the high bits.</summary>
    private static bool FlowOfViewIs(MdView v, long flowId) => flowId >= 0 && flowId >> 20 == v.Id;

    private MdFlow? FlowById(long id)
    {
        foreach (var f in _model.P.Flows) if (f.Id == id) return f;
        return null;
    }

    /// <summary>The task box's character now: a memoised block's offsets go stale when text above it changes, its item's place in the block does not.</summary>
    private int TaskCharOffset(long viewId, int ordinal)
    {
        foreach (var v in _model.P.Views)
        {
            if (v.Id != viewId) continue;
            int n = 0;
            return Walk(v, ordinal, ref n);
        }
        return -1;
        static int Walk(MdView v, int want, ref int n)
        {
            switch (v)
            {
                case MdQuoteView q:
                    foreach (var k in q.Children) { int r = Walk(k, want, ref n); if (r >= 0) return r; }
                    break;
                case MdListView l:
                    foreach (var it in l.Items)
                    {
                        if (it.Item.Checked is not null && n++ == want) return it.Item.TaskCharOffset;
                        foreach (var k in it.Children) { int r = Walk(k, want, ref n); if (r >= 0) return r; }
                    }
                    break;
            }
            return -1;
        }
    }

    private void HandleRootBounds(RectF r)
    {
        _viewW = r.W;
        bool wide = r.W >= Options.CommandBarMinWidth;
        if (_wide.Peek() != wide) _wide.Value = wide;
        // The bars have fixed heights, so the scroller's view is the root less them: the content fills it, and a click below the text lands in it.
        float bars = (wide ? BarHeight : FooterHeight) + (Options.Chrome == MarkdownEditorChrome.Field ? TextBox.DefaultFieldStyle.FocusBarThickness : 0f);
        float h = MathF.Max(0f, r.H - bars);
        if (MathF.Abs(h - _viewH) >= 0.5f)
        {
            _viewH = h;
            _viewHeight.Value = h;
        }
    }

    private const float BarHeight = 40f, FooterHeight = 36f;

    private void PruneNodes(MdProjection p)
    {
        if (_wrappers.Count <= p.Flows.Count * 2 + 64) return;
        var keep = new HashSet<long>();
        foreach (var f in p.Flows) keep.Add(f.Id);
        var drop = new List<long>();
        foreach (var k in _wrappers.Keys) if (!keep.Contains(k)) drop.Add(k);
        foreach (var k in drop) { _wrappers.Remove(k); _captures.Remove(k); }
    }

    private static string K(string prefix, long id) => prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private Action<NodeHandle> Capture(long id)
    {
        if (_captures.TryGetValue(id, out var a)) return a;
        a = h => _wrappers[id] = h;
        _captures[id] = a;
        return a;
    }

    private Element TopLevel(MdView v, int index)
    {
        _renderingView = v.Id;
        _renderingTask = 0;
        var el = ViewElement(v, 0);
        float top = 0f;
        if (index > 0 && v is MdLeafView { Flows: [{ Kind: MdFlowKind.Heading } h, ..] })
            top = MathF.Max(0f, (h.HeadingLevel <= 4 ? _style.HeadingGap : _style.HeadingGapSmall) - _style.BlockGap);
        // The block is what springs: layout lands once, the presented height eases, the blocks below ride it (the no-jump table).
        return new BoxEl
        {
            Key = K("b", v.Id),
            Direction = 1,
            Margin = top > 0f ? new Edges4(0, top, 0, 0) : default,
            ClipToBounds = v is not MdRawBlockView { Kind: MdObjectKind.None },
            Layout = _structural || v is MdRawBlockView { Kind: MdObjectKind.None } ? default : new LayoutTransition(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(), Size: SizeMode.FlowReveal, Anchor: SizeAnchor.Parallax),
            Children = [el],
        };
    }

    private bool _structural;
    private int _lastViewCount = -1;
    private long _renderingView;
    private int _renderingTask;

    private Element ViewElement(MdView v, int depth) => v switch
    {
        MdLeafView l => Leaf(l),
        MdCodeView c => CodeCard(c),
        MdRawBlockView r => RawBlock(r),
        MdObjectView o => ObjectView(o.Object),
        MdQuoteView q => Quote(q, depth),
        MdListView l => ListView(l, depth),
        MdFrontMatterView fm => FrontMatter(fm),
        _ => new BoxEl(),
    };

    private Element Leaf(MdLeafView l)
    {
        if (l.Flows.Length == 1) return FlowElement(l.Flows[0]);
        var kids = new Element[l.Flows.Length];
        for (int i = 0; i < kids.Length; i++) kids[i] = FlowElement(l.Flows[i]);
        return new BoxEl { Direction = 1, Children = kids };
    }

    private TypeStyle TypeOf(MdFlow f) => f.Kind == MdFlowKind.Heading
        ? f.HeadingLevel switch { 1 => _style.H1, 2 => _style.H2, 3 => _style.H3, 4 => _style.H4, 5 => _style.H5, _ => _style.H6 }
        : _style.Body;

    private Element FlowElement(MdFlow f)
    {
        var type = TypeOf(f);
        bool mono = f.Kind is MdFlowKind.CodeLine or MdFlowKind.RawLine;
        return new BoxEl
        {
            Key = K("f", f.Id),
            Direction = 1,
            OnRealized = Capture(f.Id),
            Children =
            [
                new SpanTextEl(BuildSpans(f))
                {
                    Size = mono ? _style.CodeFontSize : type.Size,
                    LineHeight = mono ? _style.CodeLineHeight : type.LineHeight,
                    Weight = mono ? (ushort)0 : type.Weight,
                    // Source shows as written: Cascadia Code's ligatures would draw `|---|` and `==` as other glyphs.
                    FontFamily = mono ? (_style.CodeFontFamily == "Cascadia Code" ? "Cascadia Mono" : _style.CodeFontFamily) : null,
                    Color = f.ReadOnly ? _style.TextSecondary : _style.TextPrimary,
                    Wrap = f.Kind == MdFlowKind.CodeLine ? TextWrap.NoWrap : TextWrap.Wrap,
                    IsTextSelectionEnabled = false,
                    Shrink = f.Kind == MdFlowKind.CodeLine ? 0f : 1f,
                    MinWidth = 0f,
                },
            ],
        };
    }

    private string Placeholder => Options.Placeholder.Current();

    /// <summary>The note's only text is nothing: the placeholder shows in its one empty flow, and is never hit or measured as text.</summary>
    private bool ShowsPlaceholder(MdFlow f) => f.Length == 0 && _model.P.Flows.Count == 1 && _model.Text.Length == _model.P.BodyStart && Placeholder.Length > 0;

    private TextSpan[] BuildSpans(MdFlow f)
    {
        if (f.Length == 0 && !_ime.ActiveOn(f))
            return ShowsPlaceholder(f) ? [new TextSpan(Placeholder, Color: _style.TextSecondary)] : [new TextSpan(" ")];
        bool dimAllowed = DimFlowId() == f.Id;
        var spans = new List<TextSpan>(f.Runs.Length + 1);
        string d = f.Display;
        int imeAt = _ime.ActiveOn(f) ? _ime.DisplayAt : -1;
        void AddRun(int from, int to, MdRunStyle st)
        {
            if (to <= from) return;
            string t = d.Substring(from, to - from);
            spans.Add(Span(t, st, dimAllowed));
        }
        bool imeDone = imeAt < 0;
        foreach (var r in f.Runs)
        {
            if (!imeDone && imeAt >= r.Start && imeAt <= r.End)
            {
                AddRun(r.Start, imeAt, r.Style);
                spans.Add(Span(_ime.Text, r.Style, false));
                AddRun(imeAt, r.End, r.Style);
                imeDone = true;
                continue;
            }
            AddRun(r.Start, r.End, r.Style);
        }
        if (!imeDone) spans.Add(new TextSpan(_ime.Text));
        return spans.ToArray();
    }

    private TextSpan Span(string text, MdRunStyle st, bool dimAllowed)
    {
        ushort weight = (st & MdRunStyle.Strong) != 0 ? _style.StrongWeight : (ushort)0;
        ColorF color = default;
        if ((st & MdRunStyle.Link) != 0) color = _style.LinkColor;
        if (dimAllowed && (st & MdRunStyle.Dim) != 0) color = _style.TextSecondary;
        bool code = (st & MdRunStyle.Code) != 0;
        ColorF bg = code ? _style.CodeSpanFill : (st & MdRunStyle.Mark) != 0 ? _style.MarkFill : default;
        return new TextSpan(text, weight, color,
            Underline: (st & MdRunStyle.Link) != 0 && _style.LinkUnderline,
            Strikethrough: (st & MdRunStyle.Strike) != 0,
            Size: code ? _style.CodeFontSize : null,
            FontFamily: code ? _style.CodeFontFamily : null,
            Italic: (st & MdRunStyle.Em) != 0,
            Background: bg);
    }

    private Element Quote(MdQuoteView q, int depth)
    {
        var kids = new Element[q.Children.Length];
        for (int i = 0; i < kids.Length; i++) kids[i] = ViewElement(q.Children[i], depth + 1);
        return new BoxEl
        {
            Direction = 0,
            Gap = _style.Inset,
            Children =
            [
                new BoxEl { Width = _style.QuoteBarWidth, Fill = _style.QuoteBar, Corners = CornerRadius4.All(_style.QuoteBarWidth / 2f), AlignSelf = FlexAlign.Stretch },
                new BoxEl { Direction = 1, Gap = _style.BlockGap, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = kids },
            ],
        };
    }

    private Element ListView(MdListView l, int depth)
    {
        var rows = new Element[l.Items.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var it = l.Items[i];
            Element marker = it.Item.Checked is { } chk
                ? TaskBox(chk, _renderingView, _renderingTask++)
                : new TextEl(l.Ordered ? (l.Start + i).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." : Bullet(depth))
                {
                    Size = _style.Body.Size, LineHeight = _style.Body.LineHeight, Color = _style.TextPrimary,
                    TextAlign = l.Ordered ? TextAlign.End : TextAlign.Center,
                    Width = _style.ListIndent - Spacing.XS,
                };
            var kids = new Element[it.Children.Length];
            for (int k = 0; k < kids.Length; k++) kids[k] = ViewElement(it.Children[k], depth + 1);
            rows[i] = new BoxEl
            {
                Direction = 0,
                Gap = Spacing.XS,
                AlignItems = FlexAlign.Start,
                Children =
                [
                    new BoxEl { Width = _style.ListIndent - Spacing.XS, Shrink = 0f, Direction = 1, AlignItems = it.Item.Checked is null ? FlexAlign.Stretch : FlexAlign.Center, Children = [marker] },
                    new BoxEl { Direction = 1, Gap = l.Tight ? 0f : _style.BlockGap, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = kids },
                ],
            };
        }
        return new BoxEl { Direction = 1, Gap = l.Tight ? _style.ListItemGap : _style.BlockGap, Children = rows };
    }

    private static string Bullet(int depth) => (depth % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };

    /// <summary>The box: fixed size in both states, so a toggle changes a fill and one character, and moves nothing.</summary>
    private Element TaskBox(bool isChecked, long viewId, int ordinal)
    {
        float size = _style.Body.LineHeight - 2f;
        var fill = isChecked ? _style.CheckFill : _style.CheckBoxFill;
        return new BoxEl
        {
            Width = size, Height = size,
            Margin = new Edges4(0, 1f, 0, 1f),
            Corners = CornerRadius4.All(_style.CornerRadius),
            Fill = fill, HoverFill = isChecked ? fill : Tok.FillControlAltTertiary, PressedFill = fill,
            BorderColor = isChecked ? _style.CheckFill : _style.CheckBoxStroke, BorderWidth = 1f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.CheckBox,
            Cursor = CursorId.Hand,
            TabStop = false,
            OnClick = () => ToggleTask(TaskCharOffset(viewId, ordinal)),
            Children = isChecked ? [Icon(Icons.Accept, size - 6f).Foreground(_style.CheckMark)] : [],
        };
    }

    private void ToggleTask(int charOffset)
    {
        _model.ToggleTaskAt(charOffset);
        AfterModel(follow: false);
        RefocusRoot();
    }

    private Element CodeCard(MdCodeView c)
    {
        var rows = new Element[c.Lines.Length];
        for (int i = 0; i < rows.Length; i++) rows[i] = FlowElement(c.Lines[i]);
        if (!_codeScrolls.TryGetValue(c.Id, out var handle)) _codeScrolls[c.Id] = handle = new ScrollHandle();
        var type = Tok.Definition.Type.Caption;
        return new BoxEl
        {
            Direction = 1,
            Fill = _style.CodeFill, BorderColor = _style.CodeStroke, BorderWidth = 1f,
            Corners = CornerRadius4.All(_style.CornerRadius), ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinHeight = 24f,
                    Padding = new Edges4(_style.Inset, Spacing.XXS, Spacing.XS, Spacing.XXS),
                    Children = [new TextEl(c.Language.Length > 0 ? c.Language : "code") { Size = type.Size, LineHeight = type.LineHeight, Color = _style.TextSecondary }],
                },
                new BoxEl { Height = 1f, Fill = _style.CodeStroke, AlignSelf = FlexAlign.Stretch },
                new ScrollEl
                {
                    Horizontal = true, Handle = handle, SuppressScrollBar = false,
                    Height = c.Lines.Length * _style.CodeLineHeight + _style.CellPadY * 2f,
                    Content = new BoxEl { Direction = 1, Padding = new Edges4(_style.Inset, _style.CellPadY, _style.Inset, _style.CellPadY), Children = rows },
                },
            ],
        };
    }

    private Element RawBlock(MdRawBlockView r)
    {
        var rows = new Element[r.Lines.Length];
        for (int i = 0; i < rows.Length; i++) rows[i] = FlowElement(r.Lines[i]);
        if (r.Kind == MdObjectKind.None) return new BoxEl { Direction = 1, Children = rows };
        // An island's source, open in place: the code card's look, so the block keeps its place and its weight.
        return new BoxEl
        {
            Direction = 1,
            Fill = _style.CodeFill, BorderColor = Tok.AccentDefault, BorderWidth = 1f,
            Corners = CornerRadius4.All(_style.CornerRadius),
            Padding = new Edges4(_style.Inset, _style.CellPadY, _style.Inset, _style.CellPadY),
            Children = rows,
        };
    }

    private Element ObjectView(MdFlow o)
    {
        long objectId = o.Id;
        Element inner = o.Object switch
        {
            MdObjectKind.Rule => new BoxEl { Height = 1f, Fill = _style.Divider, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(0, _style.BlockGap, 0, _style.BlockGap) },
            MdObjectKind.Image => ImageBlock(o),
            _ => new MarkdownRenderer(IslandOptions, _style, _hooks, NullOverlayService.Instance).BlockElement(o.Block!),
        };
        bool selected = _model.SelectionStart == o.Start && _model.SelectionEnd == o.End;
        var layers = new List<Element>(3) { new BoxEl { Direction = 1, Children = [inner] } };
        // Selected: a 2 px accent outline, drawn over the object so nothing moves.
        layers.Add(new BoxEl
        {
            HitTestVisible = false, Visible = selected,
            BorderColor = Tok.AccentDefault, BorderWidth = 2f,
            Corners = CornerRadius4.All(_style.CornerRadius),
        });
        if (selected && o.Object is MdObjectKind.Table or MdObjectKind.Html or MdObjectKind.Code)
            layers.Add(Button.Standard(Loc.Get(Strings.Markdown.Editor.EditSource), () => { if (FlowById(objectId) is { } now) OpenIslandFrom(now); }) with
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = new Edges4(0, Spacing.XS, Spacing.XS, 0),
            });
        return new BoxEl
        {
            Key = K("f", o.Id),
            ZStack = true,
            OnRealized = Capture(o.Id),
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            OnPointerPressed = e => { if (FlowById(objectId) is { } now) PressObject(now, e); },
            Children = layers.ToArray(),
        };
    }

    private MarkdownOptions IslandOptions => Options.Markdown with { IsTextSelectionEnabled = false, OnLinkClicked = static _ => { }, OnTaskToggled = null };

    private Element ImageBlock(MdFlow o)
    {
        string? source = o.Image is { } img ? Options.Markdown.ImageResolver?.Invoke(img.Url) : null;
        if (source is not null)
            return new BoxEl
            {
                AlignSelf = FlexAlign.Start, MaxWidth = _style.ImageWidth,
                Corners = CornerRadius4.All(_style.CornerRadius), ClipToBounds = true, Fill = _style.ImageFill,
                // The reserved box: loading moves nothing.
                Children = [new ImageEl { Source = source, Width = _style.ImageWidth, AspectRatio = 4f / 3f, Fit = ImageFit.Contain }],
            };
        var caption = Tok.Definition.Type.Caption;
        string label = o.Image is { } m ? (m.Alt.Length > 0 ? m.Alt : m.Url) : "";
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Start,
            Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
            Fill = _style.ImageFill, Corners = CornerRadius4.All(_style.CornerRadius),
            Children =
            [
                Icon(Icons.Picture, 14f).Foreground(_style.TextSecondary),
                new TextEl(label) { Size = caption.Size, LineHeight = caption.LineHeight, Color = _style.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f },
            ],
        };
    }

    private Element FrontMatter(MdFrontMatterView fm)
    {
        if (Options.FrontMatterSlot?.Invoke(fm.Text) is { } slot) return slot;
        var caption = Tok.Definition.Type.Caption;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            HitTestVisible = false,
            Children =
            [
                Icon(Icons.Document, 12f).Foreground(_style.TextSecondary),
                new TextEl(Loc.Get(Strings.Markdown.Editor.FrontMatter)) { Size = caption.Size, LineHeight = caption.LineHeight, Color = _style.TextSecondary },
            ],
        };
    }

    // ── focus ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private void HandleFocus(bool gained)
    {
        _focused = gained;
        _focusSig.Value = gained;
        var hooks = _hooks;
        if (gained)
        {
            if (hooks.TextInput is { } ti) { ti.SetSink(_ime); ti.SetEditable(true); }
            if (hooks.LastPointerWasTouch?.Invoke() == true) hooks.ShowTouchKeyboard?.Invoke();
        }
        else
        {
            if (hooks.TextInput is { } ti) { ti.SetEditable(false); ti.SetSink(null); }
            hooks.HideTouchKeyboard?.Invoke();
            if (!_caretNode.IsNull) hooks.CaretBlur?.Invoke(_caretNode);
            _caretNode = NodeHandle.Null;
            if (!_cardOpen) CloseFlyouts();
        }
        Sync();
    }

    private void RefocusRoot()
    {
        if (!_contentNode.IsNull) _hooks.RestoreFocus?.Invoke(_contentNode);
    }

    // ── keyboard ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void HandleKey(KeyEventArgs e)
    {
        if (_status.Peek().Length > 0) _status.Value = "";
        if (_ime.Active) return;
        if (MenuKey(e)) { e.Handled = true; return; }
        bool ctrl = (e.Mods & KeyModifiers.Ctrl) != 0, shift = (e.Mods & KeyModifiers.Shift) != 0, alt = (e.Mods & (KeyModifiers.Alt | KeyModifiers.Win)) != 0;
        if (ctrl && !alt && Chord(e.KeyCode, shift)) { e.Handled = true; return; }
        if (e.KeyCode == Keys.Tab && !ctrl && !alt && TabInCode(shift)) { e.Handled = true; return; }

        var bind = TextEditKeymap.Map(e.KeyCode, e.Mods, multiLine: true);
        if (bind.Command == TextEditCommand.None) return;
        CloseSelectionFlyout();
        switch (bind.Command)
        {
            case TextEditCommand.Left: _model.MoveHorizontal(-1, bind.Extend); Moved(); break;
            case TextEditCommand.Right: _model.MoveHorizontal(1, bind.Extend); Moved(); break;
            case TextEditCommand.WordLeft: _model.MoveHorizontal(-1, bind.Extend, word: true); Moved(); break;
            case TextEditCommand.WordRight: _model.MoveHorizontal(1, bind.Extend, word: true); Moved(); break;
            case TextEditCommand.Up: MoveVertical(-1, 1, bind.Extend); break;
            case TextEditCommand.Down: MoveVertical(1, 1, bind.Extend); break;
            case TextEditCommand.PageUp: MoveVertical(-1, PageLines(), bind.Extend); break;
            case TextEditCommand.PageDown: MoveVertical(1, PageLines(), bind.Extend); break;
            case TextEditCommand.Home: LineEdge(-1, bind.Extend); break;
            case TextEditCommand.End: LineEdge(1, bind.Extend); break;
            case TextEditCommand.DocHome: _model.MoveToDocEdge(-1, bind.Extend); Moved(); break;
            case TextEditCommand.DocEnd: _model.MoveToDocEdge(1, bind.Extend); Moved(); break;
            case TextEditCommand.SelectAll: _model.SelectAll(); Moved(false); break;
            case TextEditCommand.Copy: Copy(); break;
            case TextEditCommand.Cut: Cut(); break;
            case TextEditCommand.Paste: Paste(); break;
            case TextEditCommand.Undo: _model.Undo(); AfterModel(); break;
            case TextEditCommand.Redo: _model.Redo(); AfterModel(); break;
            case TextEditCommand.Backspace: _model.Backspace(); AfterModel(); break;
            case TextEditCommand.WordBackspace: _model.Backspace(word: true); AfterModel(); break;
            case TextEditCommand.Delete: _model.DeleteForward(); AfterModel(); break;
            case TextEditCommand.WordDelete: _model.DeleteForward(word: true); AfterModel(); break;
            case TextEditCommand.InsertNewline: _model.Enter(); AfterModel(); break;
            case TextEditCommand.Commit:
                _model.Enter();
                AfterModel();
                break;
            case TextEditCommand.Cancel:
                if (_model.HasSelection) { _model.Select(_model.Caret, _model.Caret); Moved(false); break; }
                return;
            default: return;
        }
        e.Handled = true;
    }

    private bool Chord(int key, bool shift)
    {
        if (key >= Keys.D0 && key <= Keys.D6 && !shift) { _model.SetBlockLevel(key - Keys.D0); AfterModel(); return true; }
        switch (key)
        {
            case Keys.B when !shift: Mark(MarkdownMarkKind.Bold); return true;
            case Keys.I when !shift: Mark(MarkdownMarkKind.Italic); return true;
            case Keys.E when !shift: Mark(MarkdownMarkKind.Code); return true;
            case Keys.X when shift: Mark(MarkdownMarkKind.Strikethrough); return true;
            case Keys.H when shift: Mark(MarkdownMarkKind.Highlight); return true;
            case Keys.K when !shift: OpenLinkCommand(); return true;
            case Keys.L when !shift: _model.ToggleTaskLine(); AfterModel(); return true;
            case Keys.U when !shift: _status.Value = Loc.Get(Strings.Markdown.Editor.NoUnderline); return true;
            case 0xBF when !shift: ToggleSource(); return true;   // the / key on a US layout
        }
        return false;
    }

    private bool TabInCode(bool shift)
    {
        var f = _model.CurrentFlow;
        if (f is null || f.Kind is not (MdFlowKind.CodeLine or MdFlowKind.RawLine) || shift) return false;
        string indent = _model.Text.Contains("\n\t", StringComparison.Ordinal) ? "\t" : "    ";
        _model.InsertTyped(indent);
        AfterModel();
        return true;
    }

    private void HandleChar(CharEventArgs e)
    {
        if (e.Codepoint < 0x20 || e.Codepoint == 0x7F) return;
        e.Handled = true;
        if (_ime.Active) return;
        CloseSelectionFlyout();
        _model.InsertTyped(char.ConvertFromUtf32(e.Codepoint));
        AfterModel();
        UpdateMenus();
    }

    private void Mark(MarkdownMarkKind kind)
    {
        _model.ToggleMark(kind);
        AfterModel();
    }

    private void Moved(bool follow = true)
    {
        _goalX = float.NaN;
        _follow = follow;
        AfterMaybeInvalidated();
        UpdateMenus();
    }

    /// <summary>Selection moved. If that closed an island, the tree changes; otherwise only decorations do.</summary>
    private int _renderedVersion = -1;
    private void AfterMaybeInvalidated()
    {
        if (_model.Version != _renderedVersion || SelectionTouchesObject() || DimStateChanged()) { RequestRender(); return; }
        Sync();
    }

    private bool SelectionTouchesObject()
    {
        bool now = SelectedObjectId() >= 0;
        if (now || _hadObjectSelected) { _hadObjectSelected = now; return true; }
        return false;
    }
    private bool _hadObjectSelected;

    private long SelectedObjectId()
    {
        if (!_model.HasSelection) return -1;
        var f = _model.CurrentFlow;
        return f is { IsObject: true } && _model.SelectionStart == f.Start && _model.SelectionEnd == f.End ? f.Id : -1;
    }

    /// <summary>An opener waiting for its closer dims only in the caret's flow: leaving or entering that flow repaints it, colour only.</summary>
    private bool DimStateChanged()
    {
        var f = _model.CurrentFlow;
        long now = -1;
        if (f is not null) foreach (var r in f.Runs) if ((r.Style & MdRunStyle.Dim) != 0) { now = f.Id; break; }
        if (now == _dimFlow) return false;
        _dimFlow = now;
        return true;
    }

    private void RequestRender() => _epoch.Value = _epoch.Peek() + 1;

    private void AfterModel(bool follow = true)
    {
        _goalX = float.NaN;
        _follow = follow;
        string t = _model.Text;
        if (!ReferenceEquals(t, Source.Peek()))
        {
            _seen = t;
            Source.Value = t;
        }
        DimStateChanged();
        _hadObjectSelected = SelectedObjectId() >= 0;
        RequestRender();
        UpdateMenus();
    }

    private void Copy()
    {
        if (_model.HasSelection) _hooks.Clipboard?.SetText(_model.CopySelection());
    }

    private void Cut()
    {
        if (!_model.HasSelection) return;
        _hooks.Clipboard?.SetText(_model.CopySelection());
        _model.Backspace();
        AfterModel();
    }

    private void Paste()
    {
        if (_hooks.Clipboard is { } cb && cb.TryGetText(out string t) && t.Length > 0)
        {
            _model.Paste(t);
            AfterModel();
        }
    }

    // ── source toggle ──────────────────────────────────────────────────────────────────────────────────────────────

    internal void ToggleSource()
    {
        CloseFlyouts();
        // Keep the caret's line where it is on screen.
        _anchorCaretY = CaretViewY();
        _sourceToggled = true;
        _model.ToggleRaw();
        _follow = false;
        RequestRender();
        Refresh();
    }

    private float CaretViewY()
    {
        var scene = Context.Scene;
        if (scene is null || _scrollNode.IsNull || !scene.IsLive(_scrollNode)) return float.NaN;
        var f = _model.CurrentFlow;
        if (f is null) return float.NaN;
        var tn = TextNodeOf(f);
        if (tn.IsNull) return float.NaN;
        float top = 0f;
        if (TryCaretGeometry(f, tn, out _, out float t, out _)) top = t;
        return scene.AbsoluteRect(tn).Y + top - scene.AbsoluteRect(_scrollNode).Y;
    }

    private void Refresh() => _selEpoch.Value = _selEpoch.Peek() + 1;

    // ── geometry ───────────────────────────────────────────────────────────────────────────────────────────────────

    private NodeHandle TextNodeOf(MdFlow f)
    {
        var scene = Context.Scene;
        if (scene is null || !_wrappers.TryGetValue(f.Id, out var w) || w.IsNull || !scene.IsLive(w)) return NodeHandle.Null;
        if (f.IsObject) return w;
        return scene.FirstChild(w);
    }

    private string QueryText(MdFlow f)
    {
        if (_ime.ActiveOn(f)) return f.Display.Insert(_ime.DisplayAt, _ime.Text);
        if (f.Length > 0) return f.Display;
        return ShowsPlaceholder(f) ? Placeholder : " ";
    }

    private bool TryCaretGeometry(MdFlow f, NodeHandle tn, out float x, out float top, out float lh)
    {
        x = top = 0f; lh = 0f;
        var scene = Context.Scene;
        if (scene is null || _hooks.Fonts is not { } fonts || f.IsObject) return false;
        int d = f.Length == 0 && !_ime.ActiveOn(f) ? 0 : f.ToDisplay(_model.Caret);
        if (_ime.ActiveOn(f)) d = _ime.DisplayAt + _ime.Caret;
        float w = QueryWidth(scene, f, tn);
        fonts.GetCaret(QueryText(f), scene.Layout(tn).TextStyle, w, d, out x, out top, out lh, out _, scene.Bounds(tn).W);
        return true;
    }

    private static float QueryWidth(SceneStore scene, MdFlow f, NodeHandle tn)
        => f.Kind == MdFlowKind.CodeLine ? float.PositiveInfinity : MathF.Max(1f, scene.Bounds(tn).W);

    /// <summary>The flow under a content-local point, by rects: above and below the text land on the nearest flow.</summary>
    private int FlowIndexAt(Point2 contentLocal)
    {
        var scene = Context.Scene;
        var p = _model.P;
        if (scene is null || _contentNode.IsNull || p.Flows.Count == 0) return -1;
        float y = scene.AbsoluteRect(_contentNode).Y + contentLocal.Y;
        int lo = 0, hi = p.Flows.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            var tn = TextNodeOf(p.Flows[mid]);
            if (tn.IsNull) { lo = mid + 1; continue; }
            var r = scene.AbsoluteRect(tn);
            if (y <= r.Y + r.H) hi = mid; else lo = mid + 1;
        }
        // Between two flows: the nearer one.
        if (lo > 0)
        {
            var a = TextNodeOf(p.Flows[lo - 1]);
            var b = TextNodeOf(p.Flows[lo]);
            if (!a.IsNull && !b.IsNull)
            {
                var ra = scene.AbsoluteRect(a);
                var rb = scene.AbsoluteRect(b);
                if (y < rb.Y && y - (ra.Y + ra.H) < rb.Y - y) return lo - 1;
            }
        }
        return lo;
    }

    private int HitOffset(Point2 contentLocal, out MdFlow? flow, out int display)
    {
        flow = null; display = 0;
        var scene = Context.Scene;
        var p = _model.P;
        int i = FlowIndexAt(contentLocal);
        if (scene is null || i < 0 || _hooks.Fonts is not { } fonts) return _model.Caret;
        var f = p.Flows[i];
        flow = f;
        if (f.IsObject) return f.Start;
        if (f.Length == 0) return f.Start;
        var tn = TextNodeOf(f);
        if (tn.IsNull) return f.Start;
        var ca = scene.AbsoluteRect(_contentNode);
        var ta = scene.AbsoluteRect(tn);
        var local = new Point2(contentLocal.X - (ta.X - ca.X), contentLocal.Y - (ta.Y - ca.Y));
        int d = fonts.HitTestText(f.Display, scene.Layout(tn).TextStyle, QueryWidth(scene, f, tn), local, out _, scene.Bounds(tn).W);
        d = f.Snap(d);
        display = d;
        return f.LeftOf(d);
    }

    // ── pointer ────────────────────────────────────────────────────────────────────────────────────────────────────

    private bool _pressing, _dragged;
    private byte _pressClicks;
    private KeyModifiers _pressMods;
    private PointerKind _pressKind;

    private void HandlePressed(PointerEventArgs e)
    {
        CloseFlyouts();
        if (e.Button != 0) { e.Handled = true; return; }
        if (!_focused) RefocusRoot();
        _pressing = true;
        _dragged = false;
        _pressClicks = e.ClickCount;
        _pressMods = e.Mods;
        _pressKind = e.Kind;
        int at = HitOffset(e.Local, out var f, out int d);
        if (f is { IsObject: true }) { PressObject(f, e); return; }
        switch (e.ClickCount)
        {
            case 1:
                _model.SetCaret(at, (e.Mods & KeyModifiers.Shift) != 0);
                break;
            case 2 when f is not null && f.Length > 0:
            {
                int end = f.WordRight(d);
                int start = f.WordLeft(end);
                if (start > d) start = d;
                _model.Select(f.RightOf(start), f.LeftOf(end));
                break;
            }
            default:
                if (f is not null) _model.SelectFlow(f);
                break;
        }
        _goalX = float.NaN;
        _follow = false;
        AfterMaybeInvalidated();
        if (_hooks.CaretReset is { } reset && !_caretNode.IsNull) reset(_caretNode);
        e.Handled = true;
    }

    private void PressObject(MdFlow o, PointerEventArgs e)
    {
        if (!_focused) RefocusRoot();
        _pressing = false;
        CloseFlyouts();
        // Double click opens an island's source.
        if (e.ClickCount >= 2 && o.Object is MdObjectKind.Table or MdObjectKind.Html or MdObjectKind.Code)
        {
            OpenIslandFrom(o);
            e.Handled = true;
            return;
        }
        _model.Select(o.Start, o.End);
        Moved(false);
        e.Handled = true;
    }

    private void OpenIslandFrom(MdFlow o)
    {
        // The flow held by a live view may be a parse old: resolve it by where it starts.
        _model.Select(o.Start, o.End);
        var now = _model.CurrentFlow;
        if (now is { IsObject: true }) _model.OpenIsland(now);
        _follow = true;
        RequestRender();
        RefocusRoot();
    }

    private void HandleDrag(Point2 local)
    {
        if (!_pressing) return;
        _dragged = true;
        int at = HitOffset(local, out _, out _);
        if (at != _model.Caret)
        {
            _model.Select(_model.Anchor, at);
            Sync();
        }
        // Past the view's edge: scroll, faster the further out.
        var scene = Context.Scene;
        if (scene is null || _scrollNode.IsNull || _contentNode.IsNull || !scene.IsLive(_scrollNode)) return;
        var view = scene.AbsoluteRect(_scrollNode);
        float y = scene.AbsoluteRect(_contentNode).Y + local.Y;
        float over = y < view.Y ? y - view.Y : y > view.Y + view.H ? y - (view.Y + view.H) : 0f;
        _scroll.AutoScroll(over * 8d);
    }

    private void HandleReleased(PointerEventArgs e) => EndGesture();

    private void EndGesture()
    {
        if (!_pressing) return;
        _pressing = false;
        _scroll.AutoScroll(0d);
        if (_model.HasSelection)
        {
            // Mouse, pen and touch get the flyout; a keyboard selection never does.
            OpenSelectionFlyout(expanded: false);
            return;
        }
        if (_dragged || _pressClicks != 1 || _model.IsRaw) return;
        // A plain click on a link: the caret is in the text, and the card offers the rest. Ctrl+click follows it.
        if (LinkAtCaret(out var f, out var link))
        {
            if ((_pressMods & KeyModifiers.Ctrl) != 0) FollowLink(link);
            else OpenLinkCard(f, link);
        }
    }

    private bool LinkAtCaret(out MdFlow flow, out MdLink link)
    {
        flow = null!; link = null!;
        var f = _model.CurrentFlow;
        if (f is null || f.IsObject) return false;
        int d = f.ToDisplay(_model.Caret);
        foreach (var l in f.Links)
            if (d >= l.DisplayStart && d <= l.DisplayEnd && _model.Caret >= l.SourceStart && _model.Caret <= l.SourceEnd)
            {
                flow = f; link = l;
                return true;
            }
        return false;
    }

    private void FollowLink(MdLink l)
    {
        if (Options.Markdown.OnLinkClicked is { } handler) handler(new MarkdownLinkClick(l.Url, l.Title, l.Syntax));
        else MarkdownLinkPolicy.ConfirmAndOpen(_overlay, l.Url);
    }

    private void HandleContext(ContextRequestEventArgs e)
    {
        OpenSelectionFlyout(expanded: true);
    }

    private void HandleMoveWithin(Point2 local)
    {
        // With a mouse the flyout leaves when the pointer does.
        if (_flyout is not { IsOpen: true } || _flyoutExpanded) return;
        var scene = Context.Scene;
        if (scene is null || _contentNode.IsNull) return;
        var ca = scene.AbsoluteRect(_contentNode);
        var r = SelectionRect();
        float px = ca.X + local.X, py = ca.Y + local.Y;
        const float slack = 120f;
        if (px < r.X - slack || px > r.X + r.W + slack || py < r.Y - slack || py > r.Y + r.H + slack) CloseSelectionFlyout();
    }

    // ── vertical movement ──────────────────────────────────────────────────────────────────────────────────────────

    private int PageLines()
    {
        float lh = MathF.Max(1f, _style?.Body.LineHeight ?? 20f);
        return Math.Max(1, (int)((_viewH - lh) / lh));
    }

    private void MoveVertical(int dir, int lines, bool extend)
    {
        var scene = Context.Scene;
        if (scene is null || _hooks.Fonts is not { } fonts) return;
        var p = _model.P;
        for (int step = 0; step < lines; step++)
        {
            int i = p.IndexAt(_model.Caret);
            if (i < 0) return;
            var f = p.Flows[i];
            var tn = TextNodeOf(f);
            if (tn.IsNull) return;
            var abs = scene.AbsoluteRect(tn);
            float x = 0f, top = 0f, lh = abs.H;
            if (!f.IsObject) TryCaretGeometry(f, tn, out x, out top, out lh);
            if (float.IsNaN(_goalX)) _goalX = abs.X + x;
            float nextY = top + lh * 0.5f + dir * lh;
            if (!f.IsObject && nextY >= 0f && nextY < abs.H)
            {
                int d = f.Snap(fonts.HitTestText(f.Display, scene.Layout(tn).TextStyle, QueryWidth(scene, f, tn), new Point2(_goalX - abs.X, nextY), out _, abs.W));
                _model.SetCaret(f.LeftOf(d), extend);
                continue;
            }
            int ni = i + dir;
            if (ni < 0 || ni >= p.Flows.Count)
            {
                _model.SetCaret(dir < 0 ? f.Start : f.End, extend);
                break;
            }
            var g = p.Flows[ni];
            if (g.IsObject)
            {
                if (extend) _model.SetCaret(dir > 0 ? g.End : g.Start, true);
                else _model.Select(dir > 0 ? g.Start : g.End, dir > 0 ? g.End : g.Start);
                continue;
            }
            var gn = TextNodeOf(g);
            if (gn.IsNull) { _model.SetCaret(g.Start, extend); continue; }
            var ga = scene.AbsoluteRect(gn);
            if (g.Length == 0) { _model.SetCaret(g.Start, extend); continue; }
            float y = dir > 0 ? 2f : ga.H - 2f;
            int gd = g.Snap(fonts.HitTestText(g.Display, scene.Layout(gn).TextStyle, QueryWidth(scene, g, gn), new Point2(_goalX - ga.X, y), out _, ga.W));
            _model.SetCaret(g.LeftOf(gd), extend);
        }
        _follow = true;
        AfterMaybeInvalidated();
        UpdateMenus();
    }

    private void LineEdge(int dir, bool extend)
    {
        var scene = Context.Scene;
        var f = _model.CurrentFlow;
        if (scene is null || f is null || f.IsObject || f.Length == 0 || _hooks.Fonts is not { } fonts)
        {
            Moved();
            return;
        }
        var tn = TextNodeOf(f);
        if (tn.IsNull || !TryCaretGeometry(f, tn, out _, out float top, out float lh)) return;
        var b = scene.Bounds(tn);
        int d = f.Snap(fonts.HitTestText(f.Display, scene.Layout(tn).TextStyle, QueryWidth(scene, f, tn), new Point2(dir < 0 ? -1e6f : 1e6f, top + lh * 0.5f), out _, b.W));
        _model.SetCaret(dir < 0 ? f.LeftOf(d) : d >= f.Length ? f.End : f.LeftOf(d), extend);
        Moved();
    }

    // ── decorations ────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnAfterAnimations()
    {
        if (!_syncPending) return;
        _syncPending = false;
        _renderedVersion = _model.Version;
        Sync();
        if (!float.IsNaN(_anchorCaretY))
        {
            float now = CaretViewY();
            if (!float.IsNaN(now)) _scroll.ScrollBy(now - _anchorCaretY, ScrollMove.Immediate);
            _anchorCaretY = float.NaN;
        }
    }

    /// <summary>The one writer of caret and selection into the scene. Writes only the flows the selection touches.</summary>
    private void Sync()
    {
        var scene = Context.Scene;
        var hooks = _hooks;
        if (scene is null || hooks?.Fonts is not { } fonts || _model is null) return;
        var p = _model.P;
        if (p.Flows.Count == 0) return;

        // Selection: this flow's part, plus a short tail where a selected paragraph break follows.
        foreach (var n in _painted)
        {
            if (!scene.IsLive(n)) continue;
            ref TextEditState old = ref scene.TextEditRef(n);
            old.Flags &= unchecked((byte)~TextEditState.SelectionActive);
            scene.SetTextEditRects(n, default, default);
            scene.Mark(n, NodeFlags.PaintDirty);
        }
        _painted.Clear();
        int a = _model.SelectionStart, b = _model.SelectionEnd;
        if (a != b && _focused)
        {
            int ia = p.IndexAt(a), ib = p.IndexAt(b);
            Span<RectF> rects = stackalloc RectF[64];
            for (int i = ia; i <= ib && i < p.Flows.Count; i++)
            {
                var f = p.Flows[i];
                if (f.IsObject) continue;
                var tn = TextNodeOf(f);
                if (tn.IsNull) continue;
                int ds = f.ToDisplay(Math.Max(a, f.Start)), de = f.ToDisplay(Math.Min(b, f.End));
                float w = QueryWidth(scene, f, tn);
                var style = scene.Layout(tn).TextStyle;
                int n = 0;
                if (de > ds) n = fonts.GetRangeRects(QueryText(f), style, w, ds, de, rects[..^1], scene.Bounds(tn).W);
                if (b > f.End && i < p.Flows.Count - 1)
                {
                    fonts.GetCaret(QueryText(f), style, w, f.Length == 0 ? 0 : f.Length, out float ex, out float et, out float eh, out _, scene.Bounds(tn).W);
                    rects[n++] = new RectF(ex, et, 6f, eh);
                }
                ref TextEditState tes = ref scene.TextEditRef(tn);
                if (n > 0) tes.Flags |= TextEditState.SelectionActive;
                scene.SetTextEditRects(tn, rects[..n], default);
                scene.Mark(tn, NodeFlags.PaintDirty);
                _painted.Add(tn);
            }
        }

        // Caret: on one flow at a time.
        var cf = _model.CurrentFlow;
        bool wantCaret = _focused && cf is { IsObject: false } && a == b;
        var caretNode = wantCaret ? TextNodeOf(cf!) : NodeHandle.Null;
        if (caretNode != _caretNode)
        {
            if (!_caretNode.IsNull && scene.IsLive(_caretNode)) hooks.CaretBlur?.Invoke(_caretNode);
            _caretNode = caretNode;
            if (!caretNode.IsNull) hooks.CaretFocus?.Invoke(caretNode, hooks.TextInput?.CaretBlinkMs ?? 500);
        }
        if (!caretNode.IsNull && TryCaretGeometry(cf!, caretNode, out float cx, out float ctop, out float clh))
        {
            ref TextEditState t = ref scene.TextEditRef(caretNode);
            t.CaretX = cx; t.CaretTop = ctop; t.CaretH = clh;
            scene.Mark(caretNode, NodeFlags.PaintDirty);
            hooks.CaretReset?.Invoke(caretNode);

            // IME clause bars sit on the caret flow's rects.
            if (_ime.ActiveOn(cf!) && _ime.Text.Length > 0)
            {
                Span<RectF> ul = stackalloc RectF[8];
                int nUl = fonts.GetRangeRects(QueryText(cf!), scene.Layout(caretNode).TextStyle, QueryWidth(scene, cf!, caretNode), _ime.DisplayAt, _ime.DisplayAt + _ime.Text.Length, ul, scene.Bounds(caretNode).W);
                for (int i = 0; i < nUl; i++) ul[i] = new RectF(ul[i].X, ul[i].Y + ul[i].H - 2f, ul[i].W, 1f);
                scene.SetTextEditRects(caretNode, default, ul[..nUl]);
                _painted.Add(caretNode);
            }

            var abs = scene.AbsoluteRect(caretNode);
            if (hooks.ImeSetCaretRect is { } setRect) setRect(new RectF(abs.X + cx, abs.Y + ctop, 1f, clh));
            if (_follow && !_contentNode.IsNull && scene.IsLive(_contentNode))
            {
                float y = abs.Y + ctop - scene.AbsoluteRect(_contentNode).Y;
                _scroll.BringIntoView(y, clh, float.NaN, ScrollMove.Immediate, 8d);
                // A code line pans inside its own frame.
                if (cf!.Kind == MdFlowKind.CodeLine && CodeHandleOf(cf) is { } ch) ch.BringIntoView(cx, 2f, float.NaN, ScrollMove.Immediate, 24d);
            }
        }
        else if (_follow && cf is { IsObject: true } && !_contentNode.IsNull)
        {
            var o = TextNodeOf(cf);
            if (!o.IsNull)
            {
                var r = scene.AbsoluteRect(o);
                _scroll.BringIntoView(r.Y - scene.AbsoluteRect(_contentNode).Y, r.H, float.NaN, ScrollMove.Immediate, 8d);
            }
        }
        _follow = false;

        if (_model.Anchor != _lastAnchor || _model.Caret != _lastCaret)
        {
            _lastAnchor = _model.Anchor;
            _lastCaret = _model.Caret;
            Refresh();
        }
    }

    private ScrollHandle? CodeHandleOf(MdFlow f)
    {
        foreach (var v in _model.P.Views)
            if (v is MdCodeView c && c.SourceStart == f.BlockStart)
                return _codeScrolls.TryGetValue(c.Id, out var h) ? h : null;
        return null;
    }

    private RectF SelectionRect()
    {
        var scene = Context.Scene;
        if (scene is null || _hooks.Fonts is not { } fonts) return default;
        var p = _model.P;
        int i = p.IndexAt(_model.SelectionStart);
        if (i < 0) return default;
        var f = p.Flows[i];
        var tn = TextNodeOf(f);
        if (tn.IsNull) return default;
        var abs = scene.AbsoluteRect(tn);
        if (f.IsObject) return abs;
        fonts.GetCaret(QueryText(f), scene.Layout(tn).TextStyle, QueryWidth(scene, f, tn), f.ToDisplay(_model.SelectionStart), out float x, out float top, out float lh, out _, abs.W);
        return new RectF(abs.X + x, abs.Y + top, 1f, lh);
    }

    private RectF CaretRect()
    {
        var scene = Context.Scene;
        var f = _model.CurrentFlow;
        if (scene is null || f is null) return default;
        var tn = TextNodeOf(f);
        if (tn.IsNull) return default;
        var abs = scene.AbsoluteRect(tn);
        if (!TryCaretGeometry(f, tn, out float x, out float top, out float lh)) return abs;
        return new RectF(abs.X + x, abs.Y + top, 1f, lh);
    }

    // ── the selection flyout ───────────────────────────────────────────────────────────────────────────────────────

    private OverlayHandle? _flyout, _menu, _card;
    private bool _flyoutExpanded, _cardOpen;
    private Signal<bool>? _flyoutOverflow;
    private Signal<int>? _flyoutEpoch;

    private void CloseSelectionFlyout()
    {
        if (_flyout is { IsOpen: true } f) f.Close();
        _flyout = null;
    }

    private void CloseFlyouts()
    {
        CloseSelectionFlyout();
        CloseMenu();
        if (_card is { IsOpen: true } c) c.Close();
        _card = null;
        _cardOpen = false;
    }

    private void OpenSelectionFlyout(bool expanded)
    {
        CloseSelectionFlyout();
        _flyoutExpanded = expanded;
        _flyoutOverflow = new Signal<bool>(expanded);
        _flyoutEpoch = new Signal<int>(0);
        _flyout = _overlay.OpenAt(
            SelectionRect,
            () => Embed.Comp(() => new SelectionFlyoutBody { Owner = this, Overflow = _flyoutOverflow!, Epoch = _flyoutEpoch!, Expanded = expanded }),
            FlyoutPlacement.TopLeft,
            new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.CommandBar) { PreserveFocusOnOpen = true },
            () => _contentNode);
    }

    private sealed class SelectionFlyoutBody : Component
    {
        public required MarkdownEditor Owner;
        public required Signal<bool> Overflow;
        public required Signal<int> Epoch;
        public required bool Expanded;

        public override Element Render()
        {
            int epoch = Epoch.Value;
            var o = Owner;
            AppBarCommand Toggle(string glyph, string label, MarkdownMarkKind k) => new(glyph, label, () =>
            {
                o.Mark(k);
                o.RefocusRoot();
                Epoch.Value = Epoch.Peek() + 1;
            }, AppBarCommandKind.ToggleButton, o._model.IsMarkActive(k));
            IReadOnlyList<AppBarCommand> primary =
            [
                Toggle(Icons.Bold, Loc.Get(Strings.Markdown.Editor.Bold), MarkdownMarkKind.Bold),
                Toggle(Icons.Italic, Loc.Get(Strings.Markdown.Editor.Italic), MarkdownMarkKind.Italic),
                Toggle(Icons.Strikethrough, Loc.Get(Strings.Markdown.Editor.Strikethrough), MarkdownMarkKind.Strikethrough),
                Toggle(Icons.Code, Loc.Get(Strings.Markdown.Editor.Code), MarkdownMarkKind.Code),
                Toggle(Icons.Highlight, Loc.Get(Strings.Markdown.Editor.Highlight), MarkdownMarkKind.Highlight),
                new(Icons.Link, Loc.Get(Strings.Markdown.Editor.Link), () => { o.CloseSelectionFlyout(); o.OpenLinkCommand(); }),
            ];
            IReadOnlyList<AppBarCommand> secondary =
            [
                new(Icons.Cut, Loc.Get(Strings.Markdown.Editor.Cut), () => { o.Cut(); o.CloseSelectionFlyout(); o.RefocusRoot(); }),
                new(Icons.Copy, Loc.Get(Strings.Markdown.Editor.Copy), () => { o.Copy(); o.CloseSelectionFlyout(); o.RefocusRoot(); }),
                new(Icons.Paste, Loc.Get(Strings.Markdown.Editor.Paste), () => { o.Paste(); o.CloseSelectionFlyout(); o.RefocusRoot(); }),
                AppBarCommand.Separator,
                new(Icons.Font, Loc.Get(Strings.Markdown.Editor.Paragraph), () => o.BlockLevel(0)),
                new(Icons.Font, Loc.Get(Strings.Markdown.Editor.Heading1), () => o.BlockLevel(1)) { AcceleratorText = "Ctrl+1" },
                new(Icons.Font, Loc.Get(Strings.Markdown.Editor.Heading2), () => o.BlockLevel(2)) { AcceleratorText = "Ctrl+2" },
                new(Icons.Font, Loc.Get(Strings.Markdown.Editor.Heading3), () => o.BlockLevel(3)) { AcceleratorText = "Ctrl+3" },
            ];
            // The body's command arrays are read at mount: a new epoch mounts a new body, so checked states follow the selection.
            return new BoxEl
            {
                Direction = 1,
                Children =
                [
                    Embed.Comp(() => new CommandBarFlyoutBody
                    {
                        Primary = primary, Secondary = secondary, Expanded = Overflow, AlwaysExpanded = Expanded,
                        Close = () => o.CloseSelectionFlyout(),
                    }) with { Key = "body" + epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ],
            };
        }
    }

    private void BlockLevel(int level)
    {
        _model.SetBlockLevel(level);
        CloseSelectionFlyout();
        AfterModel();
        RefocusRoot();
    }

    // ── the bars ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The command bar (wide) or footer (panel). Reads only the selection epoch: a caret move re-renders this, not the note.</summary>
    private sealed class Bar : Component
    {
        public required MarkdownEditor Owner;
        public required bool Wide;

        public override Element Render()
        {
            var o = Owner;
            _ = o._selEpoch.Value;
            _ = o._epoch.Value;
            string status = o._status.Value;
            var model = o._model;
            var caption = Tok.Definition.Type.Caption;
            var kids = new List<Element>(12);
            if (Wide)
            {
                kids.Add(LabelButton(BlockLabel(model), o.OpenStyleMenu) with { Width = 104f });   // fixed: a caret move changes the label and moves nothing
                kids.Add(Sep());
                kids.Add(Tool(Icons.Bold, model.IsMarkActive(MarkdownMarkKind.Bold), () => o.BarMark(MarkdownMarkKind.Bold)));
                kids.Add(Tool(Icons.Italic, model.IsMarkActive(MarkdownMarkKind.Italic), () => o.BarMark(MarkdownMarkKind.Italic)));
                kids.Add(Tool(Icons.Strikethrough, model.IsMarkActive(MarkdownMarkKind.Strikethrough), () => o.BarMark(MarkdownMarkKind.Strikethrough)));
                kids.Add(Tool(Icons.Code, model.IsMarkActive(MarkdownMarkKind.Code), () => o.BarMark(MarkdownMarkKind.Code)));
                kids.Add(Tool(Icons.Highlight, model.IsMarkActive(MarkdownMarkKind.Highlight), () => o.BarMark(MarkdownMarkKind.Highlight)));
                kids.Add(Tool(Icons.Link, false, o.OpenLinkCommand));
                kids.Add(Sep());
            }
            kids.Add(LabelButton(Loc.Get(Strings.Markdown.Editor.Insert), o.OpenInsertFromBar));
            kids.Add(new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Padding = new Edges4(Spacing.S, 0, Spacing.S, 0), Children = [new TextEl(status) { Size = caption.Size, LineHeight = caption.LineHeight, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f }] });
            kids.Add(Tool(Icons.Code, model.IsRaw, o.BarSource, label: Loc.Get(Strings.Markdown.Editor.Source)));
            return new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Gap = Spacing.XXS,
                Height = Wide ? BarHeight : FooterHeight,
                Shrink = 0f,
                Padding = new Edges4(Spacing.XS, 0, Spacing.XS, 0),
                Children = kids.ToArray(),
            };
        }

        private static string BlockLabel(MarkdownEditModel m) => m.CurrentHeadingLevel switch
        {
            1 => Loc.Get(Strings.Markdown.Editor.Heading1), 2 => Loc.Get(Strings.Markdown.Editor.Heading2),
            3 => Loc.Get(Strings.Markdown.Editor.Heading3), 4 => Loc.Get(Strings.Markdown.Editor.Heading4),
            5 => Loc.Get(Strings.Markdown.Editor.Heading5), 6 => Loc.Get(Strings.Markdown.Editor.Heading6),
            _ => Loc.Get(Strings.Markdown.Editor.Paragraph),
        };

        private static Element Sep() => new BoxEl { Width = 1f, Height = 20f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(Spacing.XS, 0, Spacing.XS, 0) };

        private static BoxEl LabelButton(string label, Action onClick)
        {
            var body = Tok.Definition.Type.Body;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = 32f,
                Padding = new Edges4(Spacing.S, 0, Spacing.S, 0), Corners = Radii.ControlAll,
                Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, TabStop = false, AllowFocusOnInteraction = false,
                OnClick = onClick,
                Children = [new TextEl(label) { Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextPrimary }],
            };
        }

        /// <summary>The WinUI command button: subtle ramp at rest, the accent pill when checked. Fill only: a check moves nothing.</summary>
        private static Element Tool(string glyph, bool on, Action onClick, string? label = null)
        {
            var fg = on ? Tok.TextOnAccentPrimary : Tok.TextPrimary;
            var body = Tok.Definition.Type.Body;
            var kids = new List<Element>(2) { Icon(glyph, 16f).Foreground(fg) };
            if (label is not null) kids.Add(new TextEl(label) { Size = body.Size, LineHeight = body.LineHeight, Color = fg });
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.XS,
                Height = 32f, MinWidth = 32f,
                Padding = label is null ? default : new Edges4(Spacing.S, 0, Spacing.S, 0),
                Corners = Radii.ControlAll,
                Fill = on ? Tok.AccentDefault : Tok.FillSubtleTransparent,
                HoverFill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary,
                PressedFill = on ? Tok.AccentDefault : Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, TabStop = false, AllowFocusOnInteraction = false,
                OnClick = onClick,
                Children = kids.ToArray(),
            };
        }
    }

    private void BarMark(MarkdownMarkKind k)
    {
        Mark(k);
        RefocusRoot();
        Refresh();
    }

    private void BarSource()
    {
        ToggleSource();
        RefocusRoot();
    }

    // ── menus: insert (/) and completion ───────────────────────────────────────────────────────────────────────────

    private sealed record MenuRow(string Label, string Hint, Action Accept);
    private const int MaxMenuRows = 12;

    private readonly Signal<int> _menuEpoch = new(0);
    private List<MenuRow> _menuRows = new();
    private int _menuIndex;
    private long _menuDismissedFlow = -1;
    private int _menuAnchorStart = -1;

    private void CloseMenu()
    {
        if (_menu is { IsOpen: true } m) m.Close();
        _menu = null;
        _menuAnchorStart = -1;
    }

    private bool MenuKey(KeyEventArgs e)
    {
        if (_menu is not { IsOpen: true }) return false;
        switch (e.KeyCode)
        {
            case Keys.Up: _menuIndex = (_menuIndex + _menuRows.Count - 1) % Math.Max(1, _menuRows.Count); _menuEpoch.Value = _menuEpoch.Peek() + 1; return true;
            case Keys.Down: _menuIndex = (_menuIndex + 1) % Math.Max(1, _menuRows.Count); _menuEpoch.Value = _menuEpoch.Peek() + 1; return true;
            case Keys.Enter:
            case Keys.Tab:
                if (_menuIndex < _menuRows.Count) _menuRows[_menuIndex].Accept();
                return true;
            case Keys.Escape:
                _menuDismissedFlow = _model.CurrentFlow?.Id ?? -1;
                CloseMenu();
                return true;
        }
        return false;
    }

    /// <summary>After an edit or move: does the text before the caret ask for a menu?</summary>
    private void UpdateMenus()
    {
        if (_ime.Active) return;
        var f = _model.CurrentFlow;
        if (f is null || f.IsObject || _model.HasSelection || _model.IsRaw || f.Kind == MdFlowKind.CodeLine) { CloseMenu(); return; }
        if (f.Id != _menuDismissedFlow) _menuDismissedFlow = -1;
        int d = f.ToDisplay(_model.Caret);
        string before = f.Display.Substring(0, Math.Min(d, f.Length));

        // A consumer completion: its trigger on this flow with no closer after it.
        if (Options.Completions is { Count: > 0 } sources && _menuDismissedFlow < 0)
            foreach (var src in sources)
            {
                int t = LastTrigger(before, src);
                if (t < 0) continue;
                string q = before.Substring(t + src.Trigger.Length);
                var results = src.Query(q);
                var rows = new List<MenuRow>(results.Count);
                int start = f.S[t];
                int end = _model.Caret;
                foreach (var r in results)
                    rows.Add(new MenuRow(r.Label, r.Detail ?? "", () =>
                    {
                        _model.ReplaceRangePlain(start, end, src.Trigger + r.Insert + src.Close);
                        CloseMenu();
                        AfterModel();
                    }));
                ShowMenu(rows, start);
                return;
            }

        // `/` at the start of a line: the insert menu, filtered by what follows. A space ends it, so a path stays text.
        if (_menuDismissedFlow < 0 && f.Kind == MdFlowKind.Paragraph && before.Length > 0 && before[0] == '/' && f.Display == before
            && before.IndexOf(' ') < 0 && before.IndexOf('/', 1) < 0 && f.Item is null && !f.InQuote)
        {
            string q = before.Substring(1);
            int start = f.Start, end = _model.Caret;
            ShowMenu(InsertRows(q, start, end), start);
            return;
        }
        CloseMenu();
    }

    private static int LastTrigger(string before, MarkdownCompletionSource src)
    {
        int t = before.LastIndexOf(src.Trigger, StringComparison.Ordinal);
        if (t < 0) return -1;
        return before.IndexOf(src.Close, t + src.Trigger.Length, StringComparison.Ordinal) >= 0 ? -1 : t;
    }

    private List<MenuRow> InsertRows(string query, int start, int end)
    {
        var rows = new List<MenuRow>();
        void Add(string label, string hint, string syntax, int caret = -1)
        {
            if (query.Length > 0 && label.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) return;
            rows.Add(new MenuRow(label, hint, () =>
            {
                _model.ReplaceWithSyntax(start, end, syntax, caret);
                CloseMenu();
                AfterModel();
                RefocusRoot();
            }));
        }
        Add(Loc.Get(Strings.Markdown.Editor.Heading1), "#", "# ");
        Add(Loc.Get(Strings.Markdown.Editor.Heading2), "##", "## ");
        Add(Loc.Get(Strings.Markdown.Editor.Heading3), "###", "### ");
        Add(Loc.Get(Strings.Markdown.Editor.BulletList), "-", "- ");
        Add(Loc.Get(Strings.Markdown.Editor.NumberedList), "1.", "1. ");
        Add(Loc.Get(Strings.Markdown.Editor.Task), "- [ ]", "- [ ] ");
        Add(Loc.Get(Strings.Markdown.Editor.Quote), ">", "> ");
        Add(Loc.Get(Strings.Markdown.Editor.CodeBlock), "```", "```\n\n```", 4);
        Add(Loc.Get(Strings.Markdown.Editor.Table), "| |", "| Column | Column |\n| --- | --- |\n|  |  |\n");
        Add(Loc.Get(Strings.Markdown.Editor.Divider), "---", "---\n");
        if (Options.InsertCommands is { } extra)
            foreach (var c in extra) Add(c.Label, c.Hint ?? FirstLine(c.Syntax), c.Syntax, c.CaretOffset);
        return rows;
    }

    private static string FirstLine(string s)
    {
        int n = s.IndexOf('\n');
        return n < 0 ? s : s.Substring(0, n);
    }

    /// <summary>The footer's Insert: the same menu. In an empty block it writes there; otherwise a new block after this one.</summary>
    private void OpenInsertFromBar()
    {
        var f = _model.CurrentFlow;
        if (f is null) return;
        int start = _model.Caret, end = _model.Caret;
        if (f.Length == 0 && f.Item is null && !f.InQuote) { start = f.Start; end = f.Start; }
        else
        {
            _model.Select(f.End, f.End);
            _model.Enter();
            AfterModel();
            start = end = _model.Caret;
        }
        _menuIndex = 0;
        ShowMenu(InsertRows("", start, end), start);
        RefocusRoot();
    }

    private void OpenStyleMenu()
    {
        var rows = new List<MenuRow>
        {
            new(Loc.Get(Strings.Markdown.Editor.Paragraph), "Ctrl+0", () => StyleRow(0)),
            new(Loc.Get(Strings.Markdown.Editor.Heading1), "Ctrl+1", () => StyleRow(1)),
            new(Loc.Get(Strings.Markdown.Editor.Heading2), "Ctrl+2", () => StyleRow(2)),
            new(Loc.Get(Strings.Markdown.Editor.Heading3), "Ctrl+3", () => StyleRow(3)),
            new(Loc.Get(Strings.Markdown.Editor.Heading4), "Ctrl+4", () => StyleRow(4)),
            new(Loc.Get(Strings.Markdown.Editor.Heading5), "Ctrl+5", () => StyleRow(5)),
            new(Loc.Get(Strings.Markdown.Editor.Heading6), "Ctrl+6", () => StyleRow(6)),
        };
        _menuIndex = _model.CurrentHeadingLevel;
        ShowMenu(rows, _model.Caret);
        RefocusRoot();
    }

    private void StyleRow(int level)
    {
        CloseMenu();
        _model.SetBlockLevel(level);
        AfterModel();
        RefocusRoot();
    }

    private void ShowMenu(List<MenuRow> rows, int anchorStart)
    {
        if (rows.Count == 0) { CloseMenu(); return; }
        if (rows.Count > MaxMenuRows) rows.RemoveRange(MaxMenuRows, rows.Count - MaxMenuRows);   // the query narrows it
        _menuRows = rows;
        _menuIndex = Math.Clamp(_menuIndex, 0, rows.Count - 1);
        _menuEpoch.Value = _menuEpoch.Peek() + 1;
        if (_menu is { IsOpen: true }) return;
        _menuAnchorStart = anchorStart;
        _menu = _overlay.OpenAt(
            CaretRect,
            () => Embed.Comp(() => new MenuBody { Owner = this }),
            FlyoutPlacement.BottomLeft,
            // The editor keeps focus: typing goes on filtering, and the list follows the keys it hands over.
            new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Flyout) { PreserveFocusOnOpen = true },
            () => _contentNode);
    }

    private sealed class MenuBody : Component
    {
        public required MarkdownEditor Owner;
        public override Element Render()
        {
            _ = Owner._menuEpoch.Value;
            var rows = Owner._menuRows;
            var kids = new Element[rows.Count];
            var body = Tok.Definition.Type.Body;
            var caption = Tok.Definition.Type.Caption;
            for (int i = 0; i < kids.Length; i++)
            {
                var r = rows[i];
                bool current = i == Owner._menuIndex;
                var fill = current ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent;
                // WinUI MenuFlyoutItem metrics: min height 32, padding 11,8,11,9, the hint in Caption secondary.
                kids[i] = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.L, MinHeight = 32f,
                    Padding = new Edges4(11f, 6f, 11f, 6f), Margin = new Edges4(Spacing.XS, 0, Spacing.XS, 0),
                    Corners = Radii.ControlAll,
                    Fill = fill, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    Role = AutomationRole.MenuItem, TabStop = false, AllowFocusOnInteraction = false,
                    OnClick = r.Accept,
                    Children =
                    [
                        new TextEl(r.Label) { Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(r.Hint) { Size = caption.Size, LineHeight = caption.LineHeight, Color = Tok.TextSecondary, Shrink = 0f },
                    ],
                };
            }
            return new BoxEl { Direction = 1, Width = 260f, Padding = new Edges4(0, 2f, 0, 2f), Children = kids };
        }
    }

    // ── the link card ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ctrl+K and the Link command: the link under the caret opens its card; a selection or word gets a new link's address asked for.</summary>
    private void OpenLinkCommand()
    {
        if (_model.IsRaw) return;
        if (LinkAtCaret(out var f, out var link)) { OpenLinkCard(f, link); return; }
        var cf = _model.CurrentFlow;
        if (cf is null || cf.IsObject) return;
        int a = _model.SelectionStart, b = _model.SelectionEnd;
        if (a == b)
        {
            int d = cf.ToDisplay(a);
            int ws = d, we = d;
            while (ws > 0 && char.IsLetterOrDigit(cf.Display[ws - 1])) ws--;
            while (we < cf.Length && char.IsLetterOrDigit(cf.Display[we])) we++;
            if (ws == we) return;
            a = cf.S[ws]; b = cf.E[we - 1];
        }
        if (_model.P.IndexAt(a) != _model.P.IndexAt(b)) return;
        OpenAddressCard(cf, null, a, b);
    }

    private void OpenLinkCard(MdFlow f, MdLink link) => OpenAddressCard(f, link, link.SourceStart, link.SourceEnd);

    private void OpenAddressCard(MdFlow f, MdLink? link, int a, int b)
    {
        if (_card is { IsOpen: true } c) c.Close();
        _cardOpen = true;
        var address = new Signal<string>(link?.Url ?? "");
        bool reference = link is { UrlStart: < 0 };
        void Apply()
        {
            string url = address.Peek().Trim();
            if (url.Length == 0) { CloseCard(); return; }
            if (link is null) _model.WrapAsLink(a, b, url);
            else if (!reference && url != link.Url) _model.SetLinkAddress(link, url);
            CloseCard();
            AfterModel();
        }
        void Remove()
        {
            if (link is not null) _model.RemoveLink(f, link);
            CloseCard();
            AfterModel();
        }
        void Open()
        {
            if (link is not null) FollowLink(link);
            CloseCard();
        }
        _card = _overlay.OpenAt(
            CaretRect,
            () => Embed.Comp(() => new LinkCard
            {
                Address = address, Link = link, IsReference = reference,
                OnApply = Apply, OnOpen = Open, OnRemove = Remove, OnCancel = CloseCard,
            }),
            FlyoutPlacement.BottomLeft,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Flyout),
            () => _contentNode);
        _card.ClosedAction = () => { _cardOpen = false; RefocusRoot(); };
    }

    private void CloseCard()
    {
        if (_card is { IsOpen: true } c) c.Close();
        _card = null;
    }

    private sealed class LinkCard : Component
    {
        public required Signal<string> Address;
        public required MdLink? Link;
        public required bool IsReference;
        public required Action OnApply, OnOpen, OnRemove, OnCancel;

        public override Element Render()
        {
            var caption = Tok.Definition.Type.Caption;
            var rows = new List<Element>(4)
            {
                new TextEl(Loc.Get(Strings.Markdown.Editor.Address)) { Size = caption.Size, LineHeight = caption.LineHeight, Color = Tok.TextSecondary },
            };
            if (IsReference)
            {
                // The address lives in the reference's definition line, and stays there.
                rows.Add(new TextEl(Link!.Url) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f });
                rows.Add(new TextEl(Loc.Get(Strings.Markdown.Editor.ReferenceAddress)) { Size = caption.Size, LineHeight = caption.LineHeight, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f });
            }
            else
            {
                rows.Add(TextBox.Create(Address, options: new TextBox.TextBoxOptions { Width = 268f, OnCommit = _ => OnApply() }));
            }
            var buttons = new List<Element>(3);
            if (!IsReference) buttons.Add(Button.Accent(Loc.Get(Strings.Markdown.Editor.Apply), OnApply));
            if (Link is not null)
            {
                buttons.Add(Button.Standard(Loc.Get(Strings.Markdown.Open), OnOpen));
                if (Link.Syntax is null) buttons.Add(Button.Standard(Loc.Get(Strings.Markdown.Editor.RemoveLink), OnRemove));
            }
            rows.Add(new BoxEl { Direction = 0, Gap = Spacing.S, Children = buttons.ToArray() });
            return new BoxEl { Direction = 1, Gap = Spacing.S, Width = 292f, Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M), Children = rows.ToArray() };
        }
    }

    // ── IME ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Composition is a provisional span shown in the active flow. While it lasts: no parse, no conversion, no menus, no
    /// signal write. Commit is one undo step. Not driven by a real IME: the harness cannot supply one (markdown-editor.md §9).</summary>
    private sealed class ImeSession(MarkdownEditor owner) : ITextInputSink
    {
        public bool Active;
        public string Text = "";
        public int Caret;
        public long FlowId = -1;
        public int DisplayAt;

        public bool ActiveOn(MdFlow f) => Active && f.Id == FlowId;

        public void OnCompositionStart()
        {
            var m = owner._model;
            if (m.HasSelection)
            {
                m.Backspace();
                owner.AfterModel();
            }
            var f = m.CurrentFlow;
            Active = f is { IsObject: false };
            if (!Active) return;
            FlowId = f!.Id;
            DisplayAt = f.ToDisplay(m.Caret);
            Text = "";
            Caret = 0;
            owner.CloseFlyouts();
        }

        public void OnCompositionUpdate(ReadOnlySpan<char> text, int caret, ReadOnlySpan<ImeClause> clauses)
        {
            if (!Active) return;
            Text = text.ToString();
            Caret = Math.Clamp(caret, 0, Text.Length);
            owner.RequestRender();
        }

        public void OnCompositionCommit(ReadOnlySpan<char> text)
        {
            if (!Active) return;
            End();
            string final = text.ToString();
            if (final.Length > 0)
            {
                owner._model.Paste(final);
                owner.AfterModel();
            }
            else owner.RequestRender();
        }

        public void OnCompositionEnd()
        {
            if (!Active) return;
            End();
            owner.RequestRender();
        }

        private void End()
        {
            Active = false;
            Text = "";
            FlowId = -1;
        }
    }
}
