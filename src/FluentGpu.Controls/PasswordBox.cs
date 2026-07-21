using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>WinUI <c>PasswordRevealMode</c> (microsoft.ui.xaml.controls.controls.idl:163–167 — Peek, Hidden, Visible):
/// Peek (default) = press-and-hold the reveal "eye" shows the password, release re-masks; Hidden = always masked, no
/// reveal button; Visible = plain text always, no reveal button.</summary>
public enum PasswordRevealMode : byte { Peek = 0, Hidden = 1, Visible = 2 }

/// <summary>
/// WinUI <c>PasswordBox</c>: a single-line field built on <see cref="EditableText"/> with <c>Mask = true</c> — the
/// model text is kept verbatim, rendered as <see cref="PasswordChar"/> dots; copy/cut are blocked (paste allowed),
/// even while revealed. Inherits the EditableText field chrome unchanged (fill states, gradient ControlElevationBorder,
/// the 1px ring + 2px accent bottom-bar focus affordance = WinUI's TextControlBorderThemeThicknessFocused 1,1,1,2 —
/// PasswordBox_themeresources.xaml:156–157 + Common_themeresources.xaml:11 — no second divergent border here).
/// <para>The reveal button (<see cref="PasswordRevealMode.Peek"/>): glyph U+F78D @ PasswordBoxIconFontSize 12
/// (PasswordBox_themeresources.xaml:100 + :9), button Width 30, VerticalAlignment Stretch
/// (PasswordBox_themeresources.xaml:193), the TextControlButton state ramp (shared with the TextBox DeleteButton via
/// <see cref="EditableText.InnerButton"/>). Visibility is the dxaml ButtonStates rule (the template pair
/// PasswordBox_themeresources.xaml:165–178 is driven by CPasswordBox::UpdateVisualState, PasswordBox.cpp:532–565):
/// ButtonVisible iff CanInvokeRevealButton() = canShow ∧ hasSpace ∧ focused (PasswordBox.cpp:618–626), where canShow
/// (m_fCanShowRevealButton) arms ONLY when content changes from EMPTY — "only allow password reveal button if
/// transitioning from empty to non-empty state" (OnContentChanged, PasswordBox.cpp:366–377) — clears whenever the box
/// becomes empty again (PasswordBox.cpp:430–434) AND on every focus gain (OnGotFocus, PasswordBox.cpp:572–581:
/// "Password reveal button should not appear if focus got moved to the PasswordBox control"). Net effect: the eye
/// shows while the user is TYPING a fresh password, never from focusing/refocusing a populated box. hasSpace
/// (m_fHasSpaceForRevealButton) requires width &gt; 5 × FontSize (ArrangeOverride, PasswordBox.cpp:237–248).
/// Press-and-HOLD reveals — release anywhere re-masks (the watcher polls the button's engine Pressed flag, so
/// drag-off and release-outside re-mask too).</para>
/// <para>Composition note: the mounted <see cref="EditableText"/> component persists across renders (constructor props
/// freeze at mount), so the reveal flips and the affix visibility mutate the LIVE instance
/// (<see cref="EditableText.SetRevealed"/> / <see cref="EditableText.SetRightAffix"/>) from the focus/text callbacks —
/// SetRightAffix bumps EditableText's render-subscribed affix epoch, so the eye mounts/unmounts even when no focus
/// flip re-renders the field (the mid-typing arming case).</para>
/// </summary>
public sealed class PasswordBox : Component
{
    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those).
    /// <summary>The returned column root (pure layout: header / field / description / peek watcher). Owned: Children.</summary>
    public const string PartRoot = "Root";
    /// <summary>The header label (WinUI HeaderContentPresenter; a TextEl — use <c>Parts.Set&lt;TextEl&gt;</c>). Owned: none.</summary>
    public const string PartHeader = "Header";
    /// <summary>The description label (WinUI DescriptionPresenter; a TextEl). Owned: none.</summary>
    public const string PartDescription = "Description";
    /// <summary>The press-and-hold reveal "eye" (WinUI RevealButton). Owned: OnClick + OnPointerDown (the peek
    /// mechanics), TabStop (false — pointer focus must resolve to the field), OnRealized (the Pressed-flag watcher
    /// capture, chained). NOTE: built ONCE and cached (the SetRightAffix identity seam), so the modifier runs at the
    /// FIRST render only — keep it static (no signal-driven restyling here).</summary>
    public const string PartRevealButton = "RevealButton";
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract. The inner field chrome is the composed <see cref="EditableText"/>'s
    /// own (not forwarded — its part names would collide with this control's).</summary>
    public TemplateParts? Parts;

    public Prop<string> Placeholder = Loc.Bind(Strings.PasswordBox.Placeholder);   // culture-reactive default; a plain string overrides
    public float Width = 280f;
    public string? Header;
    public string? Description;
    public PasswordRevealMode RevealMode = PasswordRevealMode.Peek;   // WinUI default = Peek
    /// <summary>WinUI <c>PasswordBox.PasswordChar</c> (default '●' U+25CF).</summary>
    public char PasswordChar = '●';
    public int MaxLength;
    public bool IsEnabled = true;
    public Signal<string>? Password;          // caller-owned (two-way); null → internal
    public Action<string>? OnPasswordChanged; // WinUI PasswordChanged
    public Action<string>? OnCommit;          // Enter
    public Field<string>? Field;              // form-validation.md: invalid border + touched-on-blur + message row

    // WinUI TextControlThemeMinWidth = 64 (generic.xaml:97).
    private const float MinWidth = 64f;

    /// <summary>Source-compatible factory: the original (placeholder, width, header) shape plus the WinUI surface.</summary>
    public static Element Create(
        string? placeholder = null, float width = 280f, string? header = null,
        PasswordRevealMode revealMode = PasswordRevealMode.Peek,
        char passwordChar = '●',
        int maxLength = 0,
        bool isEnabled = true,
        string? description = null,
        Signal<string>? password = null,
        Action<string>? onChange = null,
        Action<string>? onCommit = null,
        TemplateParts? parts = null,
        Field<string>? field = null)
        => Embed.Comp(() => new PasswordBox
        {
            Placeholder = placeholder is null ? Loc.Bind(Strings.PasswordBox.Placeholder) : (Prop<string>)placeholder,
            Width = width, Header = header,
            RevealMode = revealMode, PasswordChar = passwordChar, MaxLength = maxLength, IsEnabled = isEnabled,
            Description = description, Password = password,
            OnPasswordChanged = onChange, OnCommit = onCommit, Parts = parts, Field = field,
        });

    private EditableText? _edit;
    private Element? _revealButton;   // built once; toggled in/out of the live EditableText's affix lane
    private NodeHandle _revealNode;
    private InputHooks? _hooks;
    private bool _focused;
    private bool _empty;
    /// <summary>CPasswordBox::m_fCanShowRevealButton (PasswordBox.cpp:32): armed only by an empty→non-empty content
    /// change (PasswordBox.cpp:366–377), cleared when the box empties (PasswordBox.cpp:430–434) and on focus gain
    /// (OnGotFocus, PasswordBox.cpp:572–581).</summary>
    private bool _canShowReveal;

    /// <summary>ButtonVisible iff Peek ∧ enabled ∧ CanInvokeRevealButton() = canShow ∧ hasSpace ∧ focused
    /// (PasswordBox.cpp:618–626 + UpdateVisualState:532–565; hasSpace = width &gt; 5 × FontSize 14,
    /// ArrangeOverride PasswordBox.cpp:237–248).</summary>
    private Element? AffixFor()
        => RevealMode == PasswordRevealMode.Peek && IsEnabled && _focused && _canShowReveal && !_empty
           && MathF.Max(Width, MinWidth) > 14f * 5f
            ? _revealButton : null;

    public override Element Render()
    {
        _hooks = UseContext(InputHooks.Current);
        var fallback = UseSignal("");
        var password = Password ?? fallback;
        float w = MathF.Max(Width, MinWidth);

        // Peek state: a value-equality-gated signal so the PEEK watcher mounts/unmounts on the flips only.
        var revealed = UseSignal(false);
        bool isRevealed = revealed.Value;

        void Remask()
        {
            revealed.Value = false;
            _edit?.SetRevealed(RevealMode == PasswordRevealMode.Visible);
            // The eye is IsTabStop=False (PasswordBox_themeresources.xaml:193) and the dispatcher resolves pointer
            // focus to the nearest focusable ancestor — the FIELD — so the click never moves focus off it (WinUI keeps
            // the field focused across RevealButton interactions). Belt-and-braces: re-assert the field anyway (a
            // no-op when it is already focused; same idiom as the EditableText DeleteButton).
            if (_edit is { } e && !e.RootNode.IsNull) _hooks?.RestoreFocus?.Invoke(e.RootNode);
        }

        // RevealButton: U+F78D @ 12 (PasswordBox_themeresources.xaml:100 + PasswordBoxIconFontSize :9), Width 30
        // stretch (:193), the shared TextControlButton chrome. Press-and-hold = reveal on pointer DOWN; the release
        // watcher below re-masks when the engine Pressed flag drops (release inside, outside, or drag-off).
        if (_revealButton is null)
        {
            Action<NodeHandle> revealCapture = h => _revealNode = h;
            Action<Point2> peekDown = _ => { revealed.Value = true; _edit?.SetRevealed(true); };
            var eye = EditableText.InnerButton(Icons.RevealPassword, 12f,
                onClick: Remask, onPointerDown: peekDown, onRealized: revealCapture);
            if (Parts is { } rp)
            {
                var m = rp.Apply(PartRevealButton, eye);
                eye = m with
                {
                    OnClick = Remask, OnPointerDown = peekDown, TabStop = false,
                    OnRealized = TemplateParts.Chain(revealCapture, m.OnRealized),
                };
            }
            _revealButton = eye;
        }

        var field = Embed.Comp(() =>
        {
            _empty = password.Peek().Length == 0;
            var e = new EditableText
            {
                Text = password,
                Placeholder = Placeholder,
                Width = w,
                Mask = true,                       // password semantics: copy/cut blocked even while revealed
                MaskChar = PasswordChar,
                Revealed = RevealMode == PasswordRevealMode.Visible,
                MaxLength = MaxLength,
                IsEnabled = IsEnabled,
                OnCommit = OnCommit,
                Field = Field?.Binding,
            };
            e.OnFocusChanged = f =>
            {
                _focused = f;
                if (f)
                {
                    // OnGotFocus (PasswordBox.cpp:572–581): "Password reveal button should not appear if focus got
                    // moved to the PasswordBox control" — refocusing a populated box never shows the eye.
                    _canShowReveal = false;
                }
                else
                {
                    revealed.Value = false;        // blur always ends a peek (WinUI: the peek lives within the press)
                    e.SetRevealed(RevealMode == PasswordRevealMode.Visible);
                }
                e.SetRightAffix(AffixFor());
            };
            e.OnTextChanged = s =>
            {
                bool wasEmpty = _empty;
                _empty = s.Length == 0;
                // OnContentChanged (PasswordBox.cpp:366–377): "only allow password reveal button if transitioning
                // from empty to non-empty state" — arm on a content change while the box WAS empty…
                if (!_canShowReveal && wasEmpty) _canShowReveal = true;
                // …and drop the flag whenever the box empties again (PasswordBox.cpp:430–434).
                if (_empty) _canShowReveal = false;
                e.SetRightAffix(AffixFor());       // mounts/unmounts the eye mid-typing (no focus flip to re-render)
                OnPasswordChanged?.Invoke(s);
            };
            _edit = e;
            return e;
        });

        var children = new List<Element>(4);
        if (Header is not null)
            // HeaderContentPresenter: TextControlHeaderForeground (BaseHigh, generic.xaml:886+207/4132); Disabled →
            // TextControlHeaderForegroundDisabled (PasswordBox_themeresources.xaml:111–112 + generic.xaml:887);
            // Margin = PasswordBoxTopHeaderMargin 0,0,0,8 (PasswordBox_themeresources.xaml:8); FontSize inherits 14.
            children.Add(Parts.Apply(PartHeader, new TextEl(Header)
            {
                Size = 14f,
                Color = IsEnabled ? Tok.TextControlHeaderForeground : Tok.TextControlHeaderForegroundDisabled,
                Margin = new Edges4(0, 0, 0, 8f),
            }));
        children.Add(field);
        if (Description is not null)
            // DescriptionPresenter (PasswordBox_themeresources.xaml:194): SystemControlDescriptionTextForegroundBrush
            // (BaseMedium, generic.xaml:327+209/4134).
            children.Add(Parts.Apply(PartDescription,
                new TextEl(Description) { Size = 14f, Color = Tok.TextControlDescriptionForeground }));
        // form-validation.md: the error message row (reveal-animated; zero space when valid).
        if (Field is { } vf) children.Add(FieldVisuals.MessageRow(vf.Error));

        // Peek release watcher: mounted only while revealed — re-masks the moment the reveal button stops being
        // pressed (engine NodeFlags.Pressed drops on PointerUp/cancel), covering release-outside and drag-off, which
        // never raise a click. The OnClick fast path above already handles release-inside.
        if (RevealMode == PasswordRevealMode.Peek && isRevealed)
            children.Add(Embed.Comp(() => new PeekReleaseWatcher
            {
                Button = () => _revealNode,
                Revealed = revealed,
                Remask = () => _edit?.SetRevealed(false),
            }));

        // The root shape stays a STABLE BoxEl across renders: mounting the watcher must not flip the root element
        // type (ComponentEl ↔ BoxEl), which would remount the field and drop focus mid-peek (Apply is type-preserving,
        // so a Parts modifier cannot flip it either).
        Element[] kids = children.ToArray();
        return Parts.Apply(PartRoot, new BoxEl { Direction = 1, Children = kids }) with { Children = kids };
    }
}

/// <summary>Per-frame poller (the DebounceTicker idiom): while the password is peek-revealed, watch the reveal
/// button's engine Pressed flag and re-mask the frame the press ends.</summary>
internal sealed class PeekReleaseWatcher : Component
{
    public required Func<NodeHandle> Button;
    public required Signal<bool> Revealed;
    public required Action Remask;

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted (only during a held peek)
        UseEffect(() =>
        {
            if (!Revealed.Peek()) return;
            var btn = Button();
            var scene = Context.Scene;
            if (scene is null || btn.IsNull || !scene.IsLive(btn) || (scene.Flags(btn) & NodeFlags.Pressed) == 0)
            {
                Revealed.Value = false;           // unmounts this watcher
                Remask();
            }
        }, tick);
        return new BoxEl { HitTestVisible = false };
    }
}
