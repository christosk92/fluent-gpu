using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A minimal single-line text field — enough for an editable ComboBox and ColorPicker channel/hex entry, NOT a full
/// TextBox/IME subsystem. It owns a caret at the end of the string (v1 limitation), inserts printable characters
/// (<c>OnCharInput</c>), and handles Backspace / Enter (commit) / Escape (cancel) via <c>OnKeyDown</c>. The visible text
/// rides a <see cref="TextEl.TextBind"/> so a keystroke updates only the text node — no component re-render. Pass a
/// caller-owned <see cref="Text"/> signal for two-way binding; <see cref="Sanitize"/> clamps/validates each edit.
/// </summary>
public sealed class EditableText : Component
{
    public Signal<string>? Text;             // caller-owned (two-way); null → an internal signal seeded from Initial
    public string Initial = "";
    public Action<string>? OnCommit;         // Enter
    public Action? OnCancel;                 // Escape
    public Action<bool>? OnFocusChanged;     // focus gained(true)/lost(false): NumberBox validate-on-blur / open-Compact / select-all
    public Func<string, string>? Sanitize;   // applied after every edit (numeric/hex clamp); null = free text
    public float Width = 120f;
    public float Height = 32f;
    public float FontSize = 14f;
    public ColorF Foreground = Tok.TextPrimary;
    public ColorF DisabledForeground = Tok.TextDisabled;
    public ColorF CaretColor = Tok.TextPrimary;
    public string Placeholder = "";
    public bool IsEnabled = true;            // gates the WinUI Disabled state visuals
    public bool Mask = false;                // PasswordBox: render the value as dots (the model text is unchanged)
    /// <summary>Optional right-edge affix (PasswordBox reveal "eye", NumberBox spin column). Laid out at the right of the
    /// field, stretched to the inner height; the text area grows to fill the rest.</summary>
    public Element? RightAffix = null;

    public override Element Render()
    {
        var fallback = UseSignal(Initial);
        var text = Text ?? fallback;
        var (focused, setFocused) = UseState(false);
        void SetFocus(bool f) { if (f == focused) return; setFocused(f); OnFocusChanged?.Invoke(f); }

        void Commit(string s) { if (Sanitize is not null) s = Sanitize(s); text.Value = s; }

        void HandleChar(CharEventArgs e)
        {
            if (e.Codepoint < 0x20 || e.Codepoint == 0x7F) return;   // control chars are OnKeyDown's job
            Commit(text.Peek() + char.ConvertFromUtf32(e.Codepoint));
            e.Handled = true;
        }

        void HandleKey(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Back:
                    var s = text.Peek();
                    if (s.Length > 0) Commit(s[..^1]);
                    e.Handled = true;
                    break;
                case Keys.Enter:
                    OnCommit?.Invoke(text.Peek());
                    SetFocus(false);
                    e.Handled = true;
                    break;
                case Keys.Escape:
                    OnCancel?.Invoke();
                    SetFocus(false);
                    e.Handled = true;
                    break;
            }
        }

        // The editable text + caret live in a grow-1 lane so an optional right affix (reveal eye / spin column) can sit
        // flush against the right edge while the text fills the remaining width.
        var textLane = new List<Element>
        {
            new TextEl(Placeholder) { Size = FontSize, Color = IsEnabled ? Foreground : DisabledForeground, TextBind = () =>
                text.Value.Length == 0 ? Placeholder : (Mask ? new string('•', text.Value.Length) : text.Value) },
        };
        if (focused)
            textLane.Add(Embed.Comp(() => new EditableCaret { Color = CaretColor, Height = FontSize + 2f }));

        var rowChildren = new List<Element>
        {
            new BoxEl
            {
                // The TEXT lane carries the WinUI TextBox content padding (10,5,6,6); the affix is a FULL-HEIGHT sibling
                // (no vertical inset) so a NumberBox inline spin column spans the field height with both ▲/▼ visible.
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = 1f,
                Padding = new Edges4(10, 5, 6, 6),
                Children = textLane.ToArray(),
            },
        };
        if (RightAffix is not null)
            rowChildren.Add(RightAffix);

        var content = new BoxEl
        {
            Direction = 0,
            Width = Width,
            Height = Height,
            AlignItems = FlexAlign.Stretch,
            Children = rowChildren.ToArray(),
        };

        var children = new List<Element> { content };
        // WinUI focus affordance is BorderThickness 1,1,1,2 — the 1px stroke stays all around (below) and a 2px accent bar
        // is pinned to the BOTTOM edge only (not a full 2px ring). In a ZStack children stack at the origin, so the bar is
        // sized to the field width/height-2 and pushed down by Height-2.
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
            // A FOCUSED field must NOT lighten on hover (that dark→light flip read as a flicker). Hover only lightens the resting field.
            HoverFill = !IsEnabled ? Tok.FillControlDisabled : (focused ? Tok.FillControlInputActive : Tok.FillControlSecondary),
            // WinUI TextControlElevationBorderBrush is a 2-stop gradient (Tok.ControlElevationBorder); on focus it becomes
            // the accent variant (TextControlElevationBorderFocusedBrush → Tok.AccentControlElevationBorder); disabled flattens
            // to ControlStrokeColorDefault. A gradient border renders as a hollow SDF ring, so the ~6%-alpha control fill is
            // not painted over. The 1px stroke stays all around in every state; the focus accent emphasis is the separate
            // 2px bottom bar above (WinUI's 1,1,1,2 thickness), never a full 2px ring.
            // WinUI keeps the field border NEUTRAL (the subtle elevation gradient) in every state — focus emphasis is the
            // 2px accent bar on the BOTTOM edge only (above), NOT the whole border turning accent.
            BorderBrush = !IsEnabled ? GradientSpec.Solid(Tok.StrokeControlDefault) : Tok.ControlElevationBorder,
            BorderWidth = 1f,
            ClipToBounds = true,
            Focusable = IsEnabled,
            Role = AutomationRole.Text,
            OnPointerDown = IsEnabled ? (Action<Point2>)(_ => SetFocus(true)) : null,   // hit-testable → the dispatcher focuses it on pointer-up
            OnKeyDown = IsEnabled ? (Action<KeyEventArgs>)HandleKey : null,
            OnCharInput = IsEnabled ? (Action<CharEventArgs>)HandleChar : null,
            Children = children.ToArray(),
        };
    }
}

/// <summary>The blinking text caret: a thin bar that pulses its own opacity while mounted. Unmounting it (on blur) tears
/// the loop down, so the frame loop idles — no busy spin.</summary>
internal sealed class EditableCaret : Component
{
    public ColorF Color;
    public float Height = 16f;

    // A STEADY caret — no blink. A looping blink animation never lets the frame loop idle (renders forever ⇒ the
    // "permanent flicker"); a static caret is correct here and lets the engine settle to 0 fps when nothing changes.
    public override Element Render()
        => new BoxEl { Width = 1.5f, Height = Height, Fill = Color, Corners = CornerRadius4.All(1f) };
}
