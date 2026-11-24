using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.Controls;

// ── Controls/EditableLabel.cs ──────────────────────────────────────────────────────────────────────────────────────
// A label you can rename in place: `label.Editable(onCommit, options)`. The label stays exactly what its caller styled (any size, weight, wrapping,
// ellipsis; rich text through the `Editable(read, text, look, ...)` overload); editability is a modifier, and the one text input stays EditableText,
// mounted only while editing.
//
// GEOMETRY: NOTHING MOVES. The label's own box is the layout's in every state. What this adds draws in a bleed of 8 x 4 around it (a negative margin
// with the same padding back): on hover a plate (subtle fill, hairline border) and a pencil whose slot is always laid out; editing, EditableText in
// exactly the plate's rectangle, in the label's size and weight, its text at the label's x (lane padding = bleed - 1 border). The label stays laid out
// under the field (invisible), so the box never changes. In the touch band (Compact) the vertical bleed grows to a 40 DIP target and the pencil stays
// at rest (no hover on touch).
//
// MOTION (Fluent control timings): the plate's brushes and the pencil 83 ms; the field fades in 83 ms and its focus bar grows as EditableText's does.
//
// BEHAVIOUR. Click, Enter, Space or F2 edits, the whole text selected. Enter saves, Esc puts the old text back, a click elsewhere or Tab saves. An
// unchanged value, or an empty one where a value is required, saves nothing (it cancels). Read-only: the label alone.
//
// DRIVE. Self-driven (one label at a time app-wide; `EditableLabel.Begin(key)` starts the one keyed so, e.g. F2 on the card that holds it), or the
// caller's (`EditableOptions.Editing` + `OnBegin`, an editor with its own state machine: Save / Cancel buttons and a status around it are its own).

/// <summary>How an <see cref="EditableLabel"/> behaves (see the file header).</summary>
public sealed record EditableOptions
{
    /// <summary>Keys the editor and, self-driven, which label <see cref="EditableLabel.Begin"/> starts. Null: the label's text.</summary>
    public string? Key { get; init; }
    /// <summary>Shown in tertiary ink while the value is empty, and the field's placeholder.</summary>
    public string Placeholder { get; init; } = "";
    /// <summary>An empty value cannot be saved (a name): Enter and blur cancel instead.</summary>
    public bool Required { get; init; }
    /// <summary>The field keeps line breaks (EditableText.AcceptsReturn) and the plate aligns to the first line.</summary>
    public bool Multiline { get; init; }
    /// <summary>The touch band: a 40 DIP target, the pencil at rest.</summary>
    public bool Compact { get; init; }
    /// <summary>The label alone: no plate, no pencil, no I-beam.</summary>
    public bool IsReadOnly { get; init; }
    /// <summary>Driven by the caller: whether it is editing now (null: the control drives itself).</summary>
    public bool? Editing { get; init; }
    /// <summary>Driven by the caller: start editing.</summary>
    public Action? OnBegin { get; init; }
    /// <summary>Driven by the caller: its draft, two-way with the field (null: the field holds its own).</summary>
    public Signal<string>? Draft { get; init; }
    /// <summary>Esc, or an unchanged / invalid value on Enter or blur.</summary>
    public Action? OnCancel { get; init; }
}

/// <summary>A label you can rename in place (see the file header).</summary>
public static class EditableLabel
{
    /// <summary>The bleed the plate and the field draw in around the label, and the touch band's target height.</summary>
    public const float BleedX = 8f, BleedY = 4f, CompactTarget = 40f;
    const float PencilGap = 6f, PencilMin = 12f, PencilScale = 0.4f;

    static readonly Signal<string?> s_editing = new(null);

    /// <summary>Start editing the self-driven label keyed <paramref name="key"/>. UI thread.</summary>
    public static void Begin(string key) => s_editing.Value = key;

    /// <summary>Make <paramref name="label"/> renamable in place; <paramref name="onCommit"/> takes a changed, valid value.</summary>
    public static Element Editable(this TextEl label, Action<string> onCommit, EditableOptions? options = null)
        => Editable(label, label.Text.Current() ?? "", label, onCommit, options);

    /// <summary>Make any element that shows <paramref name="text"/> renamable in place (rich text: its links stay at rest); the field takes
    /// <paramref name="look"/>'s size and weight.</summary>
    public static Element Editable(this Element read, string text, TextEl look, Action<string> onCommit, EditableOptions? options = null)
    {
        var o = options ?? new EditableOptions();
        return Embed.Comp(new Props(read, text, look, onCommit, o), static () => new Host()) with { Key = "editable:" + (o.Key ?? text) };
    }

    sealed record Props(Element Read, string Text, TextEl Look, Action<string> Commit, EditableOptions Options);

    sealed class Host : Component
    {
        Props? _p;
        EditableText? _editor;
        Action? _begin, _cancel;
        Action<string>? _commit;
        Action<KeyEventArgs>? _keys;
        Action<RectF>? _measured;
        float _plateH;   // the plate's height as laid out (the label + 2 x the vertical bleed): the field takes exactly it

        public override Element Render()
        {
            var p = UseProps<Props>();
            _p = p;
            var o = p.Options;
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            string key = o.Key ?? p.Text;
            bool editing = !o.IsReadOnly && (o.Editing ?? s_editing.Value == key);
            _begin ??= () =>
            {
                if (_p is not { } q) return;
                if (q.Options.OnBegin is { } b) b(); else Begin(q.Options.Key ?? q.Text);
            };
            _cancel ??= () =>
            {
                if (_p is not { } q) return;
                End(q.Options.Key ?? q.Text);
                q.Options.OnCancel?.Invoke();
            };
            _commit ??= v =>
            {
                if (_p is not { } q) return;
                string t = q.Options.Multiline ? v : v.Trim();
                if (t == q.Text || (q.Options.Required && t.Trim().Length == 0)) { _cancel!(); return; }   // nothing to save
                End(q.Options.Key ?? q.Text);
                q.Commit(t);
            };
            _keys ??= e =>
            {
                if (e.Handled || e.KeyCode is not (Keys.Enter or Keys.Space or Keys.F2)) return;
                _begin!();
                e.Handled = true;
            };

            bool blank = string.IsNullOrWhiteSpace(p.Text);
            Element run = blank && o.Placeholder.Length > 0 ? p.Look with { Text = o.Placeholder, Color = Tok.TextTertiary } : p.Read;
            if (o.IsReadOnly) return run;

            float lineH = p.Look.LineHeight > 0f ? p.Look.LineHeight : p.Look.Size * 1.34f;
            float bleedY = o.Compact ? MathF.Max(BleedY, (CompactTarget - lineH) / 2f) : BleedY;
            float glyph = MathF.Max(PencilMin, p.Look.Size * PencilScale);
            _measured ??= r => { if (r.H > 0f) _plateH = r.H; };
            // The label in a plate that bleeds past it, the pencil after it. Laid out in every state: while the field shows it is invisible and
            // holds the box.
            var read = new BoxEl
            {
                Key = "editable-read", Direction = 0, Gap = PencilGap, MinWidth = 0f, Shrink = 1f, Grow = o.Multiline ? 1f : 0f, OnBoundsChanged = _measured,
                AlignItems = o.Multiline ? FlexAlign.Start : FlexAlign.Center,
                Margin = new Edges4(-BleedX, -bleedY, -BleedX, -bleedY), Padding = new Edges4(BleedX, bleedY, BleedX, bleedY),
                Corners = Radii.ControlAll, Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                BorderWidth = 1f, BorderColor = ColorF.Transparent, HoverBorderColor = Tok.StrokeControlDefault, BrushTransitionMs = 83f,
                Role = AutomationRole.Button, Cursor = editing ? null : CursorId.IBeam, Focusable = !editing,
                OnClick = editing ? null : _begin, OnKeyDown = editing ? null : _keys,
                Opacity = editing ? 0f : 1f, HitTestVisible = !editing,
                Children =
                [
                    // A line hugs its words (the plate follows them); a paragraph keeps its column (its plate spans it).
                    new BoxEl { MinWidth = 0f, Shrink = 1f, Grow = o.Multiline ? 1f : 0f, Children = [o.Multiline ? run : Hug(run)] },
                    // The pencil rides the plate's hover (a non-interactive wrapper); its slot is always laid out.
                    new BoxEl
                    {
                        Key = "editable-pencil", Shrink = 0f, HitTestVisible = false, Height = MathF.Min(lineH, glyph + 6f), AlignItems = FlexAlign.Center,
                        Opacity = o.Compact ? 0.6f : 0f, HoverOpacity = editing ? 0f : 1f, Transition = MotionTok.ControlFast,
                        Children = [Icon(Icons.Edit, glyph, Tok.TextSecondary)],
                    },
                ],
            };
            // A line's plate hugs the words (a row that fills the column holds it), so the hover and the pencil follow the text; the field spans the column.
            Element row = new BoxEl { Key = "editable-row", Direction = 0, MinWidth = 0f, Children = [read] };
            // The control spans its column (whatever its parent aligns), so the field has the column's width to type in.
            if (!editing) return new BoxEl { Key = "editable", ZStack = true, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [row] };
            float fieldH = _plateH > 0f ? _plateH : lineH + 2f * bleedY;
            return new BoxEl { Key = "editable", ZStack = true, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [row, Field(p, bleedY, fieldH, o.Multiline ? 0f : PencilGap + glyph, hooks, post)] };
        }

        /// <summary>EditableText in the plate's rectangle, the label's font, its text at the label's x; focused with everything selected.</summary>
        /// <param name="pencil">The pencil's slot after a line's label: the field keeps it free, so the text wraps where the label's did.</param>
        Element Field(Props p, float bleedY, float height, float pencil, InputHooks? hooks, Action<Action> post)
        {
            var o = p.Options;
            var parts = new TemplateParts();
            parts[EditableText.PartRoot] = b => b with
            {
                Corners = Radii.ControlAll,
                OnRealized = h => post(() => { hooks?.FocusNode?.Invoke(hooks.FirstFocusableIn?.Invoke(h) ?? h, false); _editor?.SelectAll(); }),
            };
            // A label that wraps edits wrapped (Enter still saves); its lines start where the label's did.
            bool wraps = o.Multiline || p.Look.Wrap != TextWrap.NoWrap;
            parts[EditableText.PartLane] = b => b with
            {
                // A line (wrapped or not) sits centred in its box as the label does; a paragraph starts at the top.
                AlignItems = o.Multiline ? FlexAlign.Start : FlexAlign.Center,
                Padding = wraps ? new Edges4(BleedX - 1f, bleedY - 1f, BleedX + 1f + pencil, bleedY - 1f) : new Edges4(BleedX - 1f, 0f, BleedX + 1f + pencil, 0f),
            };
            // The field types in the label's own face (family, weight, italic, tracking, line height, features): the same text wraps the same way.
            var look = p.Look;
            parts.Set<TextEl>(EditableText.PartText, t => t with
            {
                FontFamily = look.FontFamily ?? t.FontFamily, Weight = look.Weight, Bold = look.Bold, Italic = look.Italic, CharSpacing = look.CharSpacing,
                LineHeight = look.LineHeight > 0f ? look.LineHeight : t.LineHeight, LineStacking = look.LineStacking, LineBounds = look.LineBounds,
                FontFeatures = look.FontFeatures,
            });
            Signal<string> draft = o.Draft ?? new Signal<string>(p.Text);
            float size = p.Look.Size;
            bool multi = o.Multiline;
            string placeholder = o.Placeholder;
            var commit = _commit;
            var cancel = _cancel;
            return new BoxEl
            {
                Key = "editable-field", Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, Height = height, Margin = new Edges4(-BleedX, -bleedY, -BleedX, 0f),
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Children =
                [
                    Embed.Comp(() => _editor = new EditableText
                    {
                        Text = draft, Width = float.NaN, Height = height, FontSize = size, Placeholder = placeholder, AcceptsReturn = multi, TextWrapping = wraps,
                        CommitOnLostFocus = true, OnCommit = commit, OnCancel = cancel, Parts = parts,
                    }) with { Key = "editable-editor:" + (int)size },
                ],
            };
        }

        /// <summary>The label at its own width: a label that fills its column (Grow, a zero Basis) would measure nothing inside the hugging plate.</summary>
        static Element Hug(Element e) => e switch
        {
            TextEl t => t with { Grow = 0f, Basis = float.NaN },
            BoxEl b => b with { Grow = 0f, Basis = float.NaN },
            _ => e,
        };

        static void End(string key)
        {
            if (s_editing.Peek() == key) s_editing.Value = null;
        }
    }
}
