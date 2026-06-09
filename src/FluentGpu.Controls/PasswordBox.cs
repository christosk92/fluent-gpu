using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
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
/// <see cref="EditableText.InnerButton"/>). It appears while the field is focused ∧ non-empty (the template's
/// ButtonStates ButtonVisible/ButtonCollapsed pair, PasswordBox_themeresources.xaml:165–178), and press-and-HOLD
/// reveals — release anywhere re-masks (the watcher polls the button's engine Pressed flag, so drag-off and
/// release-outside re-mask too).</para>
/// <para>Composition note: the mounted <see cref="EditableText"/> component persists across renders (constructor props
/// freeze at mount), so the reveal flips and the affix visibility mutate the LIVE instance
/// (<see cref="EditableText.SetRevealed"/> / <see cref="EditableText.RightAffix"/>) from the focus/text callbacks —
/// EditableText re-renders itself on exactly those flips and reads the updated fields.</para>
/// </summary>
public sealed class PasswordBox : Component
{
    public string Placeholder = "Password";
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

    // WinUI TextControlThemeMinWidth = 64 (generic.xaml:97).
    private const float MinWidth = 64f;

    /// <summary>Source-compatible factory: the original (placeholder, width, header) shape plus the WinUI surface.</summary>
    public static Element Create(
        string placeholder = "Password", float width = 280f, string? header = null,
        PasswordRevealMode revealMode = PasswordRevealMode.Peek,
        char passwordChar = '●',
        int maxLength = 0,
        bool isEnabled = true,
        string? description = null,
        Signal<string>? password = null,
        Action<string>? onPasswordChanged = null,
        Action<string>? onCommit = null)
        => Embed.Comp(() => new PasswordBox
        {
            Placeholder = placeholder, Width = width, Header = header,
            RevealMode = revealMode, PasswordChar = passwordChar, MaxLength = maxLength, IsEnabled = isEnabled,
            Description = description, Password = password,
            OnPasswordChanged = onPasswordChanged, OnCommit = onCommit,
        });

    private EditableText? _edit;
    private Element? _revealButton;   // built once; toggled in/out of the live EditableText's affix lane
    private NodeHandle _revealNode;
    private InputHooks? _hooks;
    private bool _focused;
    private bool _empty;

    /// <summary>ButtonVisible iff Peek ∧ enabled ∧ focused ∧ non-empty (the WinUI ButtonStates pair,
    /// PasswordBox_themeresources.xaml:165–178).</summary>
    private Element? AffixFor()
        => RevealMode == PasswordRevealMode.Peek && IsEnabled && _focused && !_empty ? _revealButton : null;

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
            // The click moved dispatcher focus onto the button — give it back (WinUI keeps the field focused across
            // RevealButton interactions; same idiom as the EditableText DeleteButton).
            if (_edit is { } e && !e.RootNode.IsNull) _hooks?.RestoreFocus?.Invoke(e.RootNode);
        }

        // RevealButton: U+F78D @ 12 (PasswordBox_themeresources.xaml:100 + PasswordBoxIconFontSize :9), Width 30
        // stretch (:193), the shared TextControlButton chrome. Press-and-hold = reveal on pointer DOWN; the release
        // watcher below re-masks when the engine Pressed flag drops (release inside, outside, or drag-off).
        _revealButton ??= EditableText.InnerButton(Icons.RevealPassword, 12f,
            onClick: Remask,
            onPointerDown: _ => { revealed.Value = true; _edit?.SetRevealed(true); },
            onRealized: h => _revealNode = h);

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
            };
            e.OnFocusChanged = f =>
            {
                _focused = f;
                if (!f)
                {
                    revealed.Value = false;        // blur always ends a peek (WinUI: the peek lives within the press)
                    e.SetRevealed(RevealMode == PasswordRevealMode.Visible);
                }
                e.RightAffix = AffixFor();         // the focus flip re-renders EditableText, which reads the new affix
            };
            e.OnTextChanged = s =>
            {
                _empty = s.Length == 0;
                e.RightAffix = AffixFor();         // an empty↔non-empty flip re-renders EditableText (button mount/unmount)
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
            children.Add(new TextEl(Header)
            {
                Size = 14f,
                Color = IsEnabled ? Tok.TextControlHeaderForeground : Tok.TextControlHeaderForegroundDisabled,
                Margin = new Edges4(0, 0, 0, 8f),
            });
        children.Add(field);
        if (Description is not null)
            // DescriptionPresenter (PasswordBox_themeresources.xaml:194): SystemControlDescriptionTextForegroundBrush
            // (BaseMedium, generic.xaml:327+209/4134).
            children.Add(new TextEl(Description) { Size = 14f, Color = Tok.TextControlDescriptionForeground });

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
        // type (ComponentEl ↔ BoxEl), which would remount the field and drop focus mid-peek.
        return new BoxEl { Direction = 1, Children = children.ToArray() };
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
