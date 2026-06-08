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
    public Func<string, string>? Sanitize;   // applied after every edit (numeric/hex clamp); null = free text
    public float Width = 120f;
    public float Height = 32f;
    public float FontSize = 14f;
    public ColorF Foreground = Tok.TextPrimary;
    public ColorF CaretColor = Tok.TextPrimary;
    public string Placeholder = "";
    public bool Mask = false;                // PasswordBox: render the value as dots (the model text is unchanged)

    public override Element Render()
    {
        var fallback = UseSignal(Initial);
        var text = Text ?? fallback;
        var (focused, setFocused) = UseState(false);

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
                    setFocused(false);
                    e.Handled = true;
                    break;
                case Keys.Escape:
                    OnCancel?.Invoke();
                    setFocused(false);
                    e.Handled = true;
                    break;
            }
        }

        var row = new List<Element>
        {
            new TextEl(Placeholder) { Size = FontSize, Color = Foreground, TextBind = () =>
                text.Value.Length == 0 ? Placeholder : (Mask ? new string('•', text.Value.Length) : text.Value) },
        };
        if (focused)
            row.Add(Embed.Comp(() => new EditableCaret { Color = CaretColor, Height = FontSize + 2f }));

        return new BoxEl
        {
            Direction = 0,
            Width = Width,
            Height = Height,
            AlignItems = FlexAlign.Center,
            Gap = 1f,
            Padding = new Edges4(10, 0, 10, 0),
            Corners = Radii.ControlAll,
            Fill = Tok.FillControlDefault,
            HoverFill = Tok.FillControlSecondary,
            BorderColor = focused ? Tok.AccentDefault : Tok.StrokeControlDefault,
            BorderWidth = focused ? 2f : 1f,
            Focusable = true,
            Role = AutomationRole.Text,
            OnPointerDown = _ => setFocused(true),   // hit-testable → the dispatcher focuses it on pointer-up
            OnKeyDown = HandleKey,
            OnCharInput = HandleChar,
            Children = row.ToArray(),
        };
    }
}

/// <summary>The blinking text caret: a thin bar that pulses its own opacity while mounted. Unmounting it (on blur) tears
/// the loop down, so the frame loop idles — no busy spin.</summary>
internal sealed class EditableCaret : Component
{
    public ColorF Color;
    public float Height = 16f;

    static readonly Keyframe[] Blink =
    [
        new Keyframe(0f, 1f, Easing.Linear),
        new Keyframe(0.5f, 1f, Easing.Linear),
        new Keyframe(0.5f, 0f, Easing.Linear),
        new Keyframe(1f, 0f, Easing.Linear),
    ];

    public override Element Render()
    {
        UseKeyframes(AnimChannel.Opacity, Blink, 1060f, loop: true);
        return new BoxEl { Width = 1.5f, Height = Height, Fill = Color, Corners = CornerRadius4.All(1f) };
    }
}
