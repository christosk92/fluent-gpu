using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;

namespace FluentGpu.Controls;

/// <summary>Why the field's text changed — the WinUI <c>AutoSuggestionBoxTextChangeReason</c> shape, surfaced through
/// <see cref="EditableText.LastChangeReason"/> so AutoSuggestBox/ComboBox can split user edits from programmatic writes.</summary>
public enum TextChangeReason : byte { UserInput, ProgrammaticChange, SuggestionChosen }

/// <summary>
/// The WinUI-parity text editor every text control composes (TextBox/PasswordBox/NumberBox/AutoSuggestBox/editable
/// ComboBox/ColorPicker), built on <see cref="TextEditCore"/> (document + caret/selection/undo) and the
/// <see cref="IFontSystem"/> editor queries (hit-test / caret metrics / range rects — the SAME layout pipeline the
/// renderer draws with).
///
/// <para><b>Document ↔ signal.</b> The caller-visible <see cref="Signal{T}"/> stays the single source of truth: a user
/// edit mutates the core document and then makes ONE <c>text.Value</c> write (TextBind updates just the text node — no
/// component re-render per keystroke); an external/programmatic signal write is folded back into the document by the
/// TextBind evaluation (<c>core.ResetText</c>, reason = <see cref="TextChangeReason.ProgrammaticChange"/>). An IME
/// composition is the one sanctioned divergence: the PROVISIONAL text lives in the document (so it renders, with clause
/// underlines) but the signal updates only on commit.</para>
///
/// <para><b>Rendering.</b> Caret/selection/IME decorations are retained scene state (<see cref="TextEditState"/> + the
/// pooled rect slab on the editor's TEXT node), replayed by the recorder — never composed elements. All geometry this
/// control writes (caret X, selection/underline rects) is in RAW text-node-local (document-space) coordinates; the
/// caret-follow <c>-ScrollX</c> is applied ONCE, as the text node's wrapper transform (a <c>TransformBind</c> reading a
/// FloatSignal — the compositor-bypass idiom), so the recorder's world transform shifts glyphs and decorations together.
/// <see cref="TextEditState.ScrollX"/> mirrors the applied offset for tests/IME math.</para>
///
/// <para><b>Focus.</b> Focus state is engine-driven (<see cref="BoxEl.OnFocusChanged"/> — WinUI GotFocus/LostFocus):
/// gained → arm the caret blinker + IME (sink + editable) + capture the Escape-revert snapshot; keyboard (Tab) focus
/// additionally selects all (pointer focus keeps the press-placed caret). Lost → disarm blinker/IME; the selection
/// STATE is kept but its visuals hide (SelectionActive off). Enter fires <see cref="OnCommit"/> and KEEPS focus
/// (WinUI); Escape reverts to the focus-time snapshot, fires <see cref="OnCancel"/>, and blurs (the established
/// engine contract that NumberBox/ComboBox/ColorPicker rely on).</para>
///
/// <para><b>Deviations (documented, not hidden):</b> <see cref="BeforeTextChanging"/> gates INSERTIONS (typing, paste,
/// IME commit, newline); deletions and undo/redo are not cancelable (WinUI fires it for every change). A
/// <see cref="Sanitize"/> that rewrites the value replaces the whole document as one undo step with the caret at the
/// end. Drag after a double-click extends by character, not by word. IME clause underlines sit at the line bottom
/// (face-metric underline placement arrives with the DirectWrite decoration work).</para>
///
/// <para><b>Focused border.</b> WinUI's Focused state swaps BorderBrush to <c>TextControlElevationBorderFocusedBrush</c>
/// (the accent-bottom-stop gradient) with BorderThickness 1,1,1,2 (TextBox_themeresources.xaml, the Focused
/// VisualState ~lines 295–304 + the brush definition ~48–66). Visually that is a neutral 1px ring whose BOTTOM edge
/// reads as a 2px accent bar. We keep the neutral <c>Tok.ControlElevationBorder</c> ring in every state and draw the
/// separate 2px accent bottom bar while focused — the same pixels without double-drawing an accent gradient under an
/// accent bar.</para>
/// </summary>
public sealed class EditableText : Component
{
    // ── public surface (source-compatible with every existing call site) ─────────────────────────────────────────────
    public Signal<string>? Text;             // caller-owned (two-way); null → an internal signal seeded from Initial
    public string Initial = "";
    public Action<string>? OnCommit;         // Enter (focus is KEPT — WinUI)
    public Action? OnCancel;                 // Escape (after the revert-to-snapshot; focus is dropped)
    public Action<bool>? OnFocusChanged;     // focus gained(true)/lost(false): NumberBox validate-on-blur / open-Compact
    public Func<string, string>? Sanitize;   // applied after every user edit (numeric/hex clamp); null = free text
    public float Width = 120f;
    public float Height = 32f;
    public float FontSize = 14f;
    public ColorF Foreground = Tok.TextPrimary;
    // WinUI TextControlForegroundDisabled = TemporaryTextFillColorDisabled #5DFEFEFE dark / #5C010101 light
    // (TextBox_themeresources.xaml:22/34 + :129/141) — distinct from the disabled PLACEHOLDER (TextFillColorDisabled).
    public ColorF DisabledForeground = Tok.TextControlForegroundDisabled;
    public ColorF CaretColor = Tok.TextPrimary;   // kept for source-compat; the caret bar is host-themed (TextEditStyle)
    public string Placeholder = "";
    public bool IsEnabled = true;            // gates the WinUI Disabled state visuals + the engine input gate
    public bool Mask = false;                // PasswordBox: display MaskChar per grapheme; copy/cut blocked, paste allowed
    /// <summary>Mask display character (WinUI <c>PasswordBox.PasswordChar</c>, default '●' U+25CF).</summary>
    public char MaskChar = '●';
    /// <summary>PasswordBox Peek/Visible: show the REAL text while keeping the <see cref="Mask"/> password semantics
    /// (copy/cut stay blocked — WinUI never allows copying out of a PasswordBox, revealed or not).</summary>
    public bool Revealed;
    /// <summary>Optional right-edge affix (PasswordBox reveal "eye", NumberBox spin column). Laid out at the right of the
    /// field, stretched to the inner height; the text area grows to fill the rest. When set, it REPLACES the WinUI
    /// DeleteButton lane (in WinUI the spin/reveal buttons take the affix slot instead of the delete button).
    /// Composers that mount/unmount the affix on a LIVE instance must go through <see cref="SetRightAffix"/> —
    /// a bare field write does not re-render the field.</summary>
    public Element? RightAffix = null;

    /// <summary>Replace the right-affix element on the LIVE instance (the PasswordBox reveal eye mounts/unmounts per
    /// the WinUI ButtonStates rule mid-typing, when no focus flip re-renders this component). Props freeze at mount,
    /// so this bumps a render-subscribed epoch to re-render the field with the new affix lane.</summary>
    internal void SetRightAffix(Element? affix)
    {
        if (ReferenceEquals(RightAffix, affix)) return;
        RightAffix = affix;
        if (_affixEpoch is { } ep) ep.Value = ep.Peek() + 1;
    }

    // ── new WinUI-parity surface ──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Maximum length in UTF-16 code units; 0 = unlimited (WinUI <c>MaxLength</c>).</summary>
    public int MaxLength;
    /// <summary>Read-only field: caret/selection/copy work, mutations no-op (WinUI <c>IsReadOnly</c>).</summary>
    public bool IsReadOnly;
    /// <summary>Multi-line: Enter inserts '\r' (Ctrl+Enter commits), Up/Down/Page navigate lines, text wraps.</summary>
    public bool AcceptsReturn;
    /// <summary>Show the WinUI TextBox DeleteButton (✕, glyph E894) while focused ∧ non-empty (single-line, no Mask,
    /// not read-only, and only when no <see cref="RightAffix"/> occupies the lane). TextBox passes true.</summary>
    public bool ShowDeleteButton;
    /// <summary>Cancelable insert gate (WinUI <c>BeforeTextChanging</c>): receives the PROPOSED full text; return false
    /// to reject. Gates typing/paste/IME-commit/newline (deletions and undo are not cancelable here).</summary>
    public Func<string, bool>? BeforeTextChanging;
    /// <summary>Fired AFTER the signal write on every text change (user or programmatic); read
    /// <see cref="LastChangeReason"/> for the WinUI change reason.</summary>
    public Action<string>? OnTextChanged;
    /// <summary>The reason of the most recent text change (valid inside/after <see cref="OnTextChanged"/>).</summary>
    public TextChangeReason LastChangeReason { get; private set; } = TextChangeReason.ProgrammaticChange;
    /// <summary>True when the most recent USER edit removed text (Backspace/Delete/word-delete/cut/clear) — the
    /// editable ComboBox restricts its search to exact matches on deletions so auto-complete cannot fight backspacing
    /// (ComboBox_Partial.cpp:4098–4106 keys this off VK_BACK).</summary>
    public bool LastEditWasDeletion { get; private set; }
    /// <summary>Selection changed: (start, length) in UTF-16 document indices (WinUI <c>SelectionChanged</c>).</summary>
    public Action<int, int>? OnSelectionChanged;

    /// <summary>The editing state machine — the honest selection/caret/undo surface (<c>SelectionStart</c>,
    /// <c>CanUndo</c>, <c>UndoDepth</c>…). Reads are free; for programmatic mutations prefer the wrappers below so the
    /// scene visuals resync.</summary>
    public TextEditCore Core => _core;
    public int SelectionStart => _core.Selection.Start;
    public int SelectionLength => _core.Selection.Length;
    public string SelectedText => _core.SelectedText;
    public void Select(int start, int length) { _core.Select(start, length); SyncVisual(); }
    public void SelectAll() { _core.SelectAll(); SyncVisual(); }

    /// <summary>The realized field root (the focusable bordered box) — composers (PasswordBox reveal, ComboBox chevron)
    /// hand it to <c>InputHooks.RestoreFocus</c> so an inner-button click gives focus back to the field (WinUI keeps
    /// the field focused across DeleteButton/RevealButton clicks).</summary>
    internal NodeHandle RootNode => _rootNode;

    /// <summary>Flip the Peek reveal on the LIVE instance (component props freeze at mount — the composer mutates the
    /// persistent instance and this re-evaluates the display): bump the display epoch so TextBind re-renders the text
    /// node with/without the mask, and resync the caret/selection geometry (mask and plain text measure differently).</summary>
    internal void SetRevealed(bool revealed)
    {
        if (Revealed == revealed) return;
        Revealed = revealed;
        BumpDisplay();
        var tn = TextNode();
        if (!tn.IsNull) _hooks?.CaretReset?.Invoke(tn);
        SyncVisual();
    }

    /// <summary>Synchronous programmatic replace + select — the WinUI editable-ComboBox <c>UpdateEditableTextBox</c>
    /// (ComboBox_Partial.cpp:1512–1556: put_Text + Select): document, signal and selection update in ONE step (a bare
    /// signal write defers the doc fold to the next reactive flush, so a follow-up Select would clamp against the old
    /// document). Reason = ProgrammaticChange; the pending TextBind re-evaluation finds doc == signal and no-ops.</summary>
    internal void ReplaceText(string text, int selStart, int selLen)
    {
        if (!_core.Doc.AsSpan().SequenceEqual(text))
        {
            _core.ResetText(text);
            LastChangeReason = TextChangeReason.ProgrammaticChange;
            if (_text is { } t) t.Value = text;
            OnTextChanged?.Invoke(text);
            BumpDisplay();
        }
        _core.Select(Math.Clamp(selStart, 0, text.Length), Math.Clamp(selLen, 0, text.Length - Math.Clamp(selStart, 0, text.Length)));
        var tn = TextNode();
        if (!tn.IsNull) _hooks?.CaretReset?.Invoke(tn);
        SyncVisual();
    }

    // ── runtime state (the component instance persists across renders) ──────────────────────────────────────────────
    private readonly TextEditCore _core = new();
    private InputHooks? _hooks;
    private Signal<string>? _text;
    private Signal<int>? _epoch;             // display epoch: bumped on doc-only changes (IME provisional, sanitize)
    private Signal<int>? _affixEpoch;        // affix epoch: SetRightAffix re-renders the field (affix mount/unmount)
    private Signal<bool>? _empty;            // doc-empty flag; flips re-render (delete-button mount/unmount) only
    private FluentGpu.Signals.FloatSignal? _scroll;   // caret-follow offset; TransformBind shifts the text wrapper by -value
    private Action<bool> _setFocused = static _ => { };
    private bool _focusedNow;
    private string? _snapshot;               // text at focus-gain (the Escape revert target)
    private bool _synced;                    // first signal→doc sync done (suppress the mount-time OnTextChanged)
    private NodeHandle _rootNode, _laneNode, _scrollerNode;
    private ImeSession? _ime;
    private int _lastSelStart = -1, _lastSelLen = -1;

    // display cache (doc → display string; identity for plain text, '●'×graphemes + index map for Mask)
    private string _disp = "";
    private int _dispVersion = -1;
    private bool _dispMask;
    private int[] _dispToDoc = [];
    private int _graphemes;

    public override Element Render()
    {
        _hooks = UseContext(InputHooks.Current);
        var fallback = UseSignal(Initial);
        var text = Text ?? fallback;
        _text = text;
        var epoch = UseSignal(0);
        _epoch = epoch;
        var affixEpoch = UseSignal(0);
        _affixEpoch = affixEpoch;
        _ = affixEpoch.Value;   // subscribe: SetRightAffix on the live instance re-renders the affix lane
        var scroll = UseFloatSignal(0f);
        _scroll = scroll;
        var (focused, setFocused) = UseState(false);
        _setFocused = setFocused;
        // Emptiness as a dedicated bool SIGNAL (maintained by the edit paths), NOT a memo over the text signal — a memo
        // marks its subscribers stale on every upstream write, which would re-render this component per keystroke; the
        // bool signal's value-equality gate re-renders only on the empty↔non-empty FLIP (delete-button mount/unmount).
        var emptySig = UseSignal(text.Peek().Length == 0);
        _empty = emptySig;
        _ime ??= new ImeSession(this);

        // Core configuration follows the props (cheap; props are fixed per mounted instance anyway).
        _core.MaxLength = MaxLength;
        _core.IsReadOnly = IsReadOnly;
        _core.AcceptsReturn = AcceptsReturn;

        // The visible text node. TextBind/ColorBind read the SIGNAL + the display epoch, so a keystroke (signal write)
        // or a doc-only change (epoch bump) updates exactly this node — no component re-render. The bind evaluation is
        // also where an EXTERNAL signal write folds back into the document (SyncFromSignal).
        var textEl = new TextEl("")
        {
            Size = FontSize,
            Color = IsEnabled ? Foreground : DisabledForeground,
            DisabledColor = DisabledForeground,
            Wrap = AcceptsReturn ? TextWrap.Wrap : TextWrap.NoWrap,
            Width = AcceptsReturn ? MathF.Max(8f, Width - 16f) : float.NaN,   // wrap width = content box (padding 10+6)
            TextBind = BindDisplay,
            ColorBind = BindColor,
        };

        // lane (padded, clipping viewport) > scroller (carries the -ScrollX caret-follow transform) > text leaf.
        var lane = new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center,
            // WinUI TextBox content padding (10,5,6,6); the affix is a FULL-HEIGHT sibling outside this padding.
            Padding = new Edges4(10, 5, 6, 6),
            ClipToBounds = true,
            OnRealized = h => _laneNode = h,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    TransformBind = () => Affine2D.Translation(-scroll.Value, 0f),
                    OnRealized = h => _scrollerNode = h,
                    Children = [textEl],
                },
            ],
        };

        var rowChildren = new List<Element> { lane };
        if (RightAffix is not null)
        {
            rowChildren.Add(RightAffix);
        }
        else if (ShowDeleteButton && !AcceptsReturn && !Mask && IsEnabled && !IsReadOnly && focused && !emptySig.Value)
        {
            rowChildren.Add(DeleteButton());
        }

        var content = new BoxEl
        {
            Direction = 0,
            Width = Width,
            Height = Height,
            AlignItems = FlexAlign.Stretch,
            Children = rowChildren.ToArray(),
        };

        var children = new List<Element> { content };
        // WinUI focus affordance is BorderThickness 1,1,1,2 — the 1px stroke stays all around (below) and a 2px accent
        // bar is pinned to the BOTTOM edge only (not a full 2px ring). See the class doc for the
        // TextControlElevationBorderFocusedBrush equivalence (TextBox_themeresources.xaml ~48–66 / ~295–304).
        if (focused && IsEnabled)
            children.Add(new BoxEl
            {
                Width = Width, Height = 2f, OffsetY = Height - 2f,
                Fill = Tok.AccentDefault, Corners = CornerRadius4.All(0f),
            });

        return new BoxEl
        {
            ZStack = true,
            Width = Width,
            Height = Height,
            Corners = Radii.ControlAll,   // WinUI ControlCornerRadius = 4px
            // WinUI TextBox state fills: rest=ControlFillColorDefault, PointerOver=ControlFillColorSecondary,
            // focused=ControlFillColorInputActive, disabled=ControlFillColorDisabled.
            Fill = !IsEnabled ? Tok.FillControlDisabled : (focused ? Tok.FillControlInputActive : Tok.FillControlDefault),
            // A FOCUSED field must NOT lighten on hover (that dark→light flip read as a flicker).
            HoverFill = !IsEnabled ? Tok.FillControlDisabled : (focused ? Tok.FillControlInputActive : Tok.FillControlSecondary),
            // WinUI keeps the field border NEUTRAL (the subtle elevation gradient) in every state — focus emphasis is
            // the 2px accent bar on the BOTTOM edge (above), per the focused-border note in the class doc.
            BorderBrush = !IsEnabled ? GradientSpec.Solid(Tok.StrokeControlDefault) : Tok.ControlElevationBorder,
            BorderWidth = 1f,
            ClipToBounds = true,
            IsEnabled = IsEnabled,
            Focusable = IsEnabled,
            Role = AutomationRole.Text,
            Cursor = CursorId.IBeam,
            OnRealized = h => _rootNode = h,
            OnFocusChanged = HandleFocus,
            OnKeyDown = HandleKey,
            OnCharInput = HandleChar,
            OnPointerPressed = HandlePressed,
            OnDrag = HandleDrag,
            Children = children.ToArray(),
        };
    }

    // ── the WinUI TextControlButton inner-button family (TextBox DeleteButton / PasswordBox RevealButton) ──────────
    /// <summary>The shared inner-button chrome: button Width=30 + VerticalAlignment=Stretch (TextBox_themeresources.xaml:339;
    /// PasswordBox_themeresources.xaml:193), ButtonLayoutGrid Margin = TextBoxInnerButtonMargin 0,4,4,4
    /// (TextBox_themeresources.xaml:176/209); rest fill TextControlButtonBackground = SystemControlTransparentBrush
    /// (generic.xaml:889), PointerOver = SubtleFillColorSecondary #0FFFFFFF dark / #09000000 light, Pressed =
    /// SubtleFillColorTertiary #0AFFFFFF / #06000000 (TextBox_themeresources.xaml:40–41/147–148); glyph foreground
    /// TextControlButtonForeground = TextFillColorSecondary #C5FFFFFF / #9E000000 rest+hover → TextFillColorTertiary
    /// #87FFFFFF / #72000000 pressed (TextBox_themeresources.xaml:45–47/152–154); border transparent.</summary>
    internal static BoxEl InnerButton(string glyph, float glyphSize, Action? onClick,
        Action<Point2>? onPointerDown = null, Action<NodeHandle>? onRealized = null) => new()
    {
        Width = 30f,
        Margin = new Edges4(0, 4, 4, 4),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button,
        Cursor = CursorId.Arrow,
        // WinUI: both inner buttons are IsTabStop=False (DeleteButton TextBox_themeresources.xaml:339 + perf2026:293;
        // RevealButton PasswordBox_themeresources.xaml:193 + perf2026:135) — they can NEVER take focus, by Tab or by
        // click. The dispatcher resolves a pointer activation to the nearest focusable ancestor, so clicking the
        // affix keeps the FIELD focused (no LostFocus→GotFocus storm; the PasswordBox eye survives its own click —
        // CPasswordBox::OnGotFocus only clears m_fCanShowRevealButton when focus actually moves, PasswordBox.cpp:572–581).
        TabStop = false,
        OnClick = onClick,
        OnPointerDown = onPointerDown,
        OnRealized = onRealized,
        Children =
        [
            new TextEl(glyph)
            {
                Size = glyphSize, FontFamily = Theme.IconFont,
                Color = Tok.TextSecondary, PressedColor = Tok.TextTertiary,
            },
        ],
    };

    // The WinUI TextBox DeleteButton (TextBox_themeresources.xaml:205–251): GlyphElement E894 @ TextBoxIconFontSize=12.
    private BoxEl DeleteButton() => InnerButton(Icons.ClearText, 12f, ClearAllKeepFocus);

    private void ClearAllKeepFocus()
    {
        if (IsReadOnly) return;
        if (!AcceptProposed(default, replaceAll: true)) return;
        _core.SelectAll();
        LastEditWasDeletion = true;
        if (_core.InsertText(default)) AfterUserEdit();
        // The DeleteButton is IsTabStop=False (TextBox_themeresources.xaml:339): the click's pointer focus resolves to
        // the field root, so focus never left it. Belt-and-braces: re-assert the field (no-op when already focused).
        if (!_rootNode.IsNull) _hooks?.RestoreFocus?.Invoke(_rootNode);
    }

    // ── focus (engine OnFocusChanged — WinUI GotFocus/LostFocus) ────────────────────────────────────────────────────
    private void HandleFocus(bool gained)
    {
        var hooks = _hooks;
        var tn = TextNode();
        if (gained)
        {
            _focusedNow = true;
            _snapshot = _text?.Peek() ?? "";
            if (hooks is not null && !tn.IsNull)
                hooks.CaretFocus?.Invoke(tn, hooks.TextInput?.CaretBlinkMs ?? 500);
            if (hooks?.TextInput is { } ti && !IsReadOnly)
            {
                ti.SetSink(_ime);
                ti.SetEditable(true);
            }
            // Keyboard (Tab) focus → select all (WinUI); pointer focus keeps the caret the press just placed.
            if (!_rootNode.IsNull && Context.Scene is { } sc && sc.IsLive(_rootNode)
                && (sc.Flags(_rootNode) & NodeFlags.FocusVisual) != 0)
                _core.SelectAll();
            SyncVisual();
        }
        else
        {
            _focusedNow = false;
            if (hooks?.TextInput is { } ti)
            {
                ti.SetEditable(false);   // cancels any in-flight composition through the sink
                ti.SetSink(null);
            }
            if (hooks is not null && !tn.IsNull) hooks.CaretBlur?.Invoke(tn);
            _snapshot = null;
            SyncVisual();                // selection STATE kept; SelectionActive off → highlight hides (simplified WinUI)
        }
        _setFocused(gained);
        OnFocusChanged?.Invoke(gained);
    }

    // ── keyboard ────────────────────────────────────────────────────────────────────────────────────────────────────
    private void HandleKey(KeyEventArgs e)
    {
        var bind = TextEditKeymap.Map(e.KeyCode, e.Mods, AcceptsReturn);
        if (bind.Command == TextEditCommand.None) return;   // not ours — MUST bubble (AutoSuggestBox Up/Down nav)

        switch (bind.Command)
        {
            case TextEditCommand.Copy: DoCopy(); break;
            case TextEditCommand.Cut: DoCut(); break;
            case TextEditCommand.Paste: DoPaste(); break;

            case TextEditCommand.Up: MoveVertical(-1, page: false, bind.Extend); break;
            case TextEditCommand.Down: MoveVertical(+1, page: false, bind.Extend); break;
            case TextEditCommand.PageUp: MoveVertical(-1, page: true, bind.Extend); break;
            case TextEditCommand.PageDown: MoveVertical(+1, page: true, bind.Extend); break;

            case TextEditCommand.Commit:
                OnCommit?.Invoke(_text?.Peek() ?? "");   // WinUI: Enter commits and KEEPS focus
                break;

            case TextEditCommand.Cancel:
                DoCancel();
                break;

            case TextEditCommand.InsertNewline:
                if (!IsReadOnly && AcceptProposed("\r"))
                {
                    int v0 = _core.Doc.Version;
                    LastEditWasDeletion = false;
                    if (_core.Apply(TextEditCommand.InsertNewline) && _core.Doc.Version != v0) AfterUserEdit();
                }
                break;

            case TextEditCommand.Undo:
            case TextEditCommand.Redo:
            {
                int v0 = _core.Doc.Version;
                if (_core.Apply(bind.Command))
                {
                    // Undo restores a previously-sanitized state — pushing it through Sanitize again could fight the
                    // history, so the signal gets the restored document verbatim.
                    if (_core.Doc.Version != v0) PushDocToSignal(TextChangeReason.UserInput);
                    else SyncVisual();
                }
                break;
            }

            default:
            {
                int v0 = _core.Doc.Version;
                bool changed = _core.Apply(bind.Command, bind.Extend);
                // The only doc-changing default-case commands are the deletion family (Backspace/Delete/word-delete).
                if (_core.Doc.Version != v0) { LastEditWasDeletion = true; AfterUserEdit(); }
                else if (changed) SyncVisual();
                break;
            }
        }
        e.Handled = true;   // every mapped editor chord is consumed (WinUI TextBox does not let caret keys bubble)
    }

    private void HandleChar(CharEventArgs e)
    {
        if (e.Codepoint < 0x20 || e.Codepoint == 0x7F) return;   // control chars are OnKeyDown's job
        string ch = char.ConvertFromUtf32(e.Codepoint);
        if (!IsReadOnly && AcceptProposed(ch))
        {
            int v0 = _core.Doc.Version;
            LastEditWasDeletion = false;
            if (_core.InsertText(ch) && _core.Doc.Version != v0) AfterUserEdit();
        }
        e.Handled = true;
    }

    // ── pointer (caret placement / word / select-all / drag-select) ─────────────────────────────────────────────────
    private void HandlePressed(PointerEventArgs e)
    {
        // WinUI focuses a text field on pointer DOWN (the dispatcher's default focus lands on pointer UP) — focus now
        // so the caret blinker/IME arm before the caret is placed and a drag-selection paints while the button is held.
        if (!_focusedNow && !_rootNode.IsNull) _hooks?.RestoreFocus?.Invoke(_rootNode);
        var p = ToTextLocal(e.Local);
        switch (e.ClickCount)
        {
            case 1:
                _core.SetCaret(HitDoc(p), extend: (e.Mods & KeyModifiers.Shift) != 0);
                break;
            case 2:
            {
                // Double-click: the word at the hit + its trailing whitespace (RichEdit/WinUI double-click shape).
                int di = HitDoc(p);
                int end = _core.Doc.NextWord(di);
                int start = _core.Doc.PrevWord(end);
                if (start > di) start = di;
                _core.Select(start, end - start);
                break;
            }
            default:
                _core.SelectAll();   // triple-click
                break;
        }
        var tn = TextNode();
        if (!tn.IsNull) _hooks?.CaretReset?.Invoke(tn);
        SyncVisual();
        e.Handled = true;
    }

    private void HandleDrag(Point2 local)
    {
        // Pointer-rate: display-string cache + pooled rect slab keep the steady drag 0-alloc.
        _core.SetCaret(HitDoc(ToTextLocal(local)), extend: true);
        SyncVisual();
    }

    // ── clipboard ───────────────────────────────────────────────────────────────────────────────────────────────────
    private void DoCopy()
    {
        if (Mask) return;   // WinUI PasswordBox: copy blocked (paste stays allowed)
        if (_core.CopySelection() is { } t) _hooks?.Clipboard?.SetText(t);
    }

    private void DoCut()
    {
        if (Mask) return;
        if (IsReadOnly) { DoCopy(); return; }   // read-only cut degrades to copy (WinUI)
        if (_core.CutSelection() is { } t)
        {
            _hooks?.Clipboard?.SetText(t);
            LastEditWasDeletion = true;
            AfterUserEdit();
        }
    }

    private void DoPaste()
    {
        if (IsReadOnly) return;
        if (_hooks?.Clipboard is { } cb && cb.TryGetText(out string t) && t.Length > 0 && AcceptProposed(t))
        {
            LastEditWasDeletion = false;
            if (_core.Paste(t)) AfterUserEdit();
        }
    }

    // ── Escape: revert to the focus-time snapshot, notify, blur ─────────────────────────────────────────────────────
    private void DoCancel()
    {
        string snap = _snapshot ?? _text?.Peek() ?? "";
        if (!_core.Doc.AsSpan().SequenceEqual(snap))
        {
            _core.ResetText(snap);
            PushDocToSignal(TextChangeReason.ProgrammaticChange);   // a revert is not a user edit
        }
        OnCancel?.Invoke();
        _hooks?.RestoreFocus?.Invoke(NodeHandle.Null);   // blur — the dispatcher fires HandleFocus(false)
    }

    // ── vertical navigation (multi-line; StickyX = the goal column the CONTROL owns) ────────────────────────────────
    private void MoveVertical(int direction, bool page, bool extend)
    {
        if (_hooks?.Fonts is not { } fonts) return;
        var scene = Context.Scene;
        var tn = TextNode();
        if (scene is null || tn.IsNull || !scene.IsLive(tn)) return;

        string disp = DisplayText();
        var style = scene.Layout(tn).TextStyle;
        float maxW = QueryMaxWidth(scene, tn);
        fonts.GetCaret(disp, style, maxW, DocToDisplay(_core.Active), out float cx, out float top, out float lh, out _);

        float sticky = float.IsNaN(_core.StickyX) ? cx : _core.StickyX;   // remember the goal X on the first vertical move
        int lines = page ? Math.Max(1, (int)((Height - 11f) / MathF.Max(1f, lh))) : 1;
        float targetY = top + lh * 0.5f + direction * lines * lh;        // hit-test mid-line (clamps at doc edges)
        int dispIdx = fonts.HitTestText(disp, style, maxW, new Point2(sticky, targetY), out _);
        _core.SetCaret(DisplayToDoc(dispIdx), extend);
        _core.StickyX = sticky;   // SetCaret reset it — the control re-establishes the goal column after the move
        SyncVisual();
    }

    // ── document mutation plumbing ──────────────────────────────────────────────────────────────────────────────────
    /// <summary>BeforeTextChanging gate for INSERTIONS: builds the proposed full text (selection replaced by
    /// <paramref name="insert"/>; <paramref name="replaceAll"/> = the delete-button clear).</summary>
    private bool AcceptProposed(ReadOnlySpan<char> insert, bool replaceAll = false)
    {
        if (BeforeTextChanging is null) return true;
        string doc = _core.Doc.GetText();
        var (s, len) = replaceAll ? (0, doc.Length) : _core.Selection;
        return BeforeTextChanging(string.Concat(doc.AsSpan(0, s), insert, doc.AsSpan(s + len)));
    }

    /// <summary>A user edit landed in the document: apply <see cref="Sanitize"/> (a rewriting sanitize replaces the
    /// whole value as ONE undo step, caret at end — the legacy caret-at-end shape), then the single signal write.</summary>
    private void AfterUserEdit()
    {
        if (Sanitize is { } san)
        {
            string raw = _core.Doc.GetText();
            string fin = san(raw);
            if (!string.Equals(fin, raw, StringComparison.Ordinal))
            {
                _core.Select(0, _core.Doc.Length);
                _core.InsertText(fin);
            }
        }
        PushDocToSignal(TextChangeReason.UserInput);
    }

    /// <summary>The ONE signal write per change: doc → signal, change events, blink reset, visual resync.</summary>
    private void PushDocToSignal(TextChangeReason reason)
    {
        string v = _core.Doc.GetText();
        LastChangeReason = reason;
        if (_text is { } t) t.Value = v;          // TextBind updates only the text node (no component re-render)
        OnTextChanged?.Invoke(v);
        BumpDisplay();                            // covers the value-unchanged case (sanitize collapse) too
        var tn = TextNode();
        if (!tn.IsNull) _hooks?.CaretReset?.Invoke(tn);
        SyncVisual();
    }

    /// <summary>External/programmatic signal write → fold into the document (runs inside the TextBind evaluation).
    /// The IME provisional span is the sanctioned divergence and must not be clobbered.</summary>
    private void SyncFromSignal(string v)
    {
        if (_ime is { Active: true }) return;
        string nv = EditDocument.NormalizeNewlines(v);
        if (_core.Doc.AsSpan().SequenceEqual(nv)) return;
        bool first = !_synced;
        _core.ResetText(nv);
        LastChangeReason = TextChangeReason.ProgrammaticChange;
        if (!first) OnTextChanged?.Invoke(v);
        var tn = TextNode();
        if (!tn.IsNull) _hooks?.CaretReset?.Invoke(tn);
        SyncVisual();
    }

    /// <summary>Doc-only display change (IME provisional / sanitize collapse): re-evaluate TextBind + request a frame.</summary>
    private void BumpDisplay()
    {
        if (_epoch is { } ep) ep.Value = ep.Peek() + 1;
    }

    // ── TextBind / ColorBind thunks (fine-grained node bindings; see class doc) ─────────────────────────────────────
    private string BindDisplay()
    {
        string v = _text!.Value;     // subscribe: external writes re-evaluate this binding
        _ = _epoch!.Value;           // subscribe: doc-only changes (IME provisional) re-evaluate too
        _synced = true;
        SyncFromSignal(v);
        if (_empty is { } em) em.Value = _core.Doc.Length == 0;   // re-renders the component on the FLIP only
        return _core.Doc.Length == 0 ? Placeholder : DisplayText();
    }

    private ColorF BindColor()
    {
        _ = _text!.Value;
        _ = _epoch!.Value;
        bool placeholder = _core.Doc.Length == 0 && Placeholder.Length > 0;
        // Disabled: placeholder = TextControlPlaceholderForegroundDisabled = TextFillColorDisabled (#5DFFFFFF dark /
        // #5C000000 light, TextBox_themeresources.xaml:38/145); text = TextControlForegroundDisabled =
        // TemporaryTextFillColorDisabled (#5DFEFEFE / #5C010101, TextBox_themeresources.xaml:22+34 / :129+141).
        if (!IsEnabled) return placeholder ? Tok.TextDisabled : DisabledForeground;
        // Placeholder = TextControlPlaceholderForeground(/PointerOver/Focused) — ALL TextFillColorSecondary
        // (TextBox_themeresources.xaml:35–37), so one static color covers rest/hover/focused exactly.
        return placeholder ? Tok.TextSecondary : Foreground;
    }

    // ── display text + Mask index mapping (docIndex ↔ displayIndex; rebuilt per doc version — user-rate alloc) ──────
    /// <summary>The mask is APPLIED to the display only while not <see cref="Revealed"/> (PasswordBox Peek hold /
    /// PasswordRevealMode.Visible show the real text; the copy-block password semantics key off <see cref="Mask"/>).</summary>
    private bool MaskApplied => Mask && !Revealed;

    private string DisplayText()
    {
        var doc = _core.Doc;
        bool mask = MaskApplied;
        if (_dispVersion == doc.Version && _dispMask == mask) return _disp;
        _dispVersion = doc.Version;
        _dispMask = mask;
        if (!mask)
        {
            _disp = doc.GetText();
            return _disp;
        }
        var s = doc.AsSpan();
        if (_dispToDoc.Length < s.Length + 2) _dispToDoc = new int[s.Length + 2];
        int count = 0, i = 0;
        _dispToDoc[0] = 0;
        while (i < s.Length)
        {
            int step = StringInfo.GetNextTextElementLength(s.Slice(i));
            if (step <= 0) step = 1;
            i += step;
            _dispToDoc[++count] = i;
        }
        _graphemes = count;
        _disp = new string(MaskChar, count);   // WinUI default PasswordChar '●' (U+25CF), one per grapheme
        return _disp;
    }

    private int DocToDisplay(int docIdx)
    {
        if (!MaskApplied) return docIdx;
        DisplayText();
        int lo = 0, hi = _graphemes;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_dispToDoc[mid] <= docIdx) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private int DisplayToDoc(int dispIdx)
    {
        if (!MaskApplied) return dispIdx;
        DisplayText();
        return _dispToDoc[Math.Clamp(dispIdx, 0, _graphemes)];
    }

    // ── geometry helpers ────────────────────────────────────────────────────────────────────────────────────────────
    private NodeHandle TextNode()
    {
        var sc = Context.Scene;
        if (sc is null || _scrollerNode.IsNull || !sc.IsLive(_scrollerNode)) return NodeHandle.Null;
        return sc.FirstChild(_scrollerNode);
    }

    /// <summary>Root-box-local pointer position → text-node-local (display/document space). Uses the PRESENTED
    /// absolute rects, so the currently-applied caret-follow transform is accounted for.</summary>
    private Point2 ToTextLocal(Point2 rootLocal)
    {
        var sc = Context.Scene;
        var tn = TextNode();
        if (sc is null || tn.IsNull || _rootNode.IsNull || !sc.IsLive(_rootNode)) return rootLocal;
        var ra = sc.AbsoluteRect(_rootNode);
        var ta = sc.AbsoluteRect(tn);
        return new Point2(rootLocal.X - (ta.X - ra.X), rootLocal.Y - (ta.Y - ra.Y));
    }

    private int HitDoc(Point2 textLocal)
    {
        if (_hooks?.Fonts is not { } fonts) return _core.Active;
        var scene = Context.Scene;
        var tn = TextNode();
        if (scene is null || tn.IsNull || !scene.IsLive(tn)) return _core.Active;
        string disp = DisplayText();   // hit-testing runs on the EDIT text (mask-aware), never the placeholder
        if (disp.Length == 0) return 0;
        int dispIdx = fonts.HitTestText(disp, scene.Layout(tn).TextStyle, QueryMaxWidth(scene, tn), textLocal, out _);
        return DisplayToDoc(dispIdx);
    }

    private float QueryMaxWidth(SceneStore scene, NodeHandle tn)
        => AcceptsReturn ? MathF.Max(1f, scene.Bounds(tn).W) : float.PositiveInfinity;

    // ── SyncVisual: the single scene-state writer (caret POD + selection/underline rect slab + caret-follow) ───────
    // Geometry is RAW text-local (document-space); the wrapper transform applies -ScrollX once for glyphs + decorations.
    private void SyncVisual()
    {
        var scene = Context.Scene;
        var hooks = _hooks;
        var tn = TextNode();
        if (scene is null || hooks?.Fonts is not { } fonts || tn.IsNull || !scene.IsLive(tn)) return;

        string disp = DisplayText();
        var style = scene.Layout(tn).TextStyle;
        float maxW = QueryMaxWidth(scene, tn);

        fonts.GetCaret(disp, style, maxW, DocToDisplay(_core.Active), out float cx, out float top, out float lh, out _);

        // Caret-follow (single-line): keep the caret inside [pad, viewport - pad] of the padded lane.
        float scrollX = _scroll?.Peek() ?? 0f;
        if (!AcceptsReturn && !_laneNode.IsNull && scene.IsLive(_laneNode))
        {
            ref LayoutInput ll = ref scene.Layout(_laneNode);
            float vw = scene.Bounds(_laneNode).W - ll.Padding.Left - ll.Padding.Right;
            if (vw > 1f)
            {
                const float pad = 8f;
                fonts.GetCaret(disp, style, maxW, disp.Length, out float endX, out _, out _, out _);
                float maxScroll = MathF.Max(0f, endX + 2f - vw);
                if (cx - scrollX > vw - pad) scrollX = cx - (vw - pad);
                if (cx - scrollX < pad) scrollX = cx - pad;
                scrollX = Math.Clamp(scrollX, 0f, maxScroll);
            }
            else scrollX = 0f;
        }
        else if (AcceptsReturn) scrollX = 0f;
        if (_scroll is { } ss) ss.Value = scrollX;   // TransformBind applies -ScrollX at the next flush

        ref TextEditState tes = ref scene.TextEditRef(tn);
        tes.CaretX = cx;
        tes.CaretTop = top;
        tes.CaretH = lh;
        tes.ScrollX = scrollX;
        tes.CompStart = _ime is { Active: true } s2 ? s2.Start : 0;
        tes.CompLen = _ime is { Active: true } s3 ? s3.Len : 0;
        bool selActive = _core.HasSelection && _focusedNow;
        if (selActive) tes.Flags |= TextEditState.SelectionActive;
        else tes.Flags &= unchecked((byte)~TextEditState.SelectionActive);
        // Focused/CaretVisible are CaretBlinker-owned — never touched here.

        // Selection highlight rects (display space == raw text-local).
        Span<RectF> sel = stackalloc RectF[AcceptsReturn ? 32 : 8];
        int nSel = 0;
        if (selActive)
        {
            var (selStart, selLen) = _core.Selection;
            nSel = fonts.GetRangeRects(disp, style, maxW, DocToDisplay(selStart), DocToDisplay(selStart + selLen), sel);
        }

        // IME clause underline bars: thin bars at the bottom of each covered line fragment (thick for target clauses).
        Span<RectF> ul = stackalloc RectF[8];
        int nUl = 0;
        if (_ime is { Active: true, Len: > 0 } ime)
        {
            Span<RectF> frag = stackalloc RectF[4];
            int clauses = ime.ClauseCount;
            if (clauses == 0)
            {
                // No clause segmentation → one Input-style underline across the whole composition.
                int n = fonts.GetRangeRects(disp, style, maxW, DocToDisplay(ime.Start), DocToDisplay(ime.Start + ime.Len), frag);
                for (int i = 0; i < n && nUl < ul.Length; i++)
                    ul[nUl++] = new RectF(frag[i].X, frag[i].Y + frag[i].H - 2f, frag[i].W, 1f);
            }
            else
            {
                for (int c = 0; c < clauses && nUl < ul.Length; c++)
                {
                    var cl = ime.Clauses[c];
                    bool target = cl.Kind is ImeClauseKind.TargetConverted or ImeClauseKind.TargetNotConverted;
                    int n = fonts.GetRangeRects(disp, style, maxW,
                        DocToDisplay(ime.Start + cl.Start), DocToDisplay(ime.Start + cl.Start + cl.Length), frag);
                    for (int i = 0; i < n && nUl < ul.Length; i++)
                        ul[nUl++] = new RectF(frag[i].X, frag[i].Y + frag[i].H - 2f, frag[i].W, target ? 2f : 1f);
                }
            }
        }

        scene.SetTextEditRects(tn, sel[..nSel], ul[..nUl]);
        scene.Mark(tn, NodeFlags.PaintDirty);

        // IME candidate-window placement (DIP; the host converts to physical px). AbsoluteRect carries the APPLIED
        // transform — compensate to the just-computed scrollX so the rect is fresh even before the binding flush.
        if (_focusedNow && hooks.ImeSetCaretRect is { } setRect && !_scrollerNode.IsNull && scene.IsLive(_scrollerNode))
        {
            var abs = scene.AbsoluteRect(tn);
            float appliedDx = scene.Paint(_scrollerNode).LocalTransform.Dx;
            setRect(new RectF(abs.X - appliedDx - scrollX + cx, abs.Y + top, 1f, lh));
        }

        // SelectionChanged (start, length) — fired on actual transitions only.
        var (curStart, curLen) = _core.Selection;
        if (curStart != _lastSelStart || curLen != _lastSelLen)
        {
            _lastSelStart = curStart;
            _lastSelLen = curLen;
            OnSelectionChanged?.Invoke(curStart, curLen);
        }
    }

    // ── IME session (ITextInputSink): provisional composition lives in the DOC, the signal updates on commit only ────
    private sealed class ImeSession(EditableText owner) : ITextInputSink
    {
        public bool Active;
        public int Start;          // document index of the provisional span
        public int Len;            // provisional span length (UTF-16)
        public ImeClause[] Clauses = [];
        public int ClauseCount;

        public void OnCompositionStart()
        {
            if (owner.IsReadOnly) return;
            if (owner._core.HasSelection) owner._core.InsertText(default);   // composition replaces the selection (one undo op)
            Active = true;
            Start = owner._core.Selection.Start;
            Len = 0;
            ClauseCount = 0;
        }

        public void OnCompositionUpdate(ReadOnlySpan<char> text, int caret, ReadOnlySpan<ImeClause> clauses)
        {
            if (!Active) return;
            var doc = owner._core.Doc;
            // Raw doc ops on purpose: the provisional span must not pollute the undo history (commit is the one undo op).
            if (Len > 0) doc.Remove(Start, Len);
            if (!text.IsEmpty) doc.Insert(Start, text);
            Len = text.Length;
            owner._core.SetCaret(Start + Math.Clamp(caret, 0, Len));
            if (Clauses.Length < clauses.Length) Clauses = new ImeClause[Math.Max(4, clauses.Length)];
            clauses.CopyTo(Clauses);
            ClauseCount = clauses.Length;
            owner.BumpDisplay();
            var tn = owner.TextNode();
            if (!tn.IsNull) owner._hooks?.CaretReset?.Invoke(tn);
            owner.SyncVisual();
        }

        public void OnCompositionCommit(ReadOnlySpan<char> text)
        {
            if (!Active) return;
            RemoveProvisional();
            string final = text.ToString();
            if (final.Length > 0 && !owner.IsReadOnly && owner.AcceptProposed(final) && owner._core.Paste(final))
            {
                owner.LastEditWasDeletion = false;
                owner.AfterUserEdit();   // commit = ONE undo step (Paste seals both sides) + the one signal write
            }
            else
            {
                owner.BumpDisplay();
                owner.SyncVisual();
            }
        }

        public void OnCompositionEnd()
        {
            if (!Active) return;
            RemoveProvisional();   // cancel path (a commit already cleared the span)
            owner.BumpDisplay();
            owner.SyncVisual();
        }

        private void RemoveProvisional()
        {
            if (Len > 0) owner._core.Doc.Remove(Start, Len);
            owner._core.SetCaret(Start);
            Active = false;
            Len = 0;
            ClauseCount = 0;
        }
    }
}
