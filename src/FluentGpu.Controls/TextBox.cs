using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>TextBox</c>: a text field built on <see cref="EditableText"/>, optionally with a header label stacked above
/// and a description row below (the WinUI template's HeaderContentPresenter / DescriptionPresenter,
/// TextBox_themeresources.xaml:335/340). Header-less it is just the bare field. EditableText already supplies the
/// bordered surface (fill, gradient ControlElevationBorder, focus affordance, 4px corners) — TextBox wraps no second
/// border. Single-line shows the DeleteButton (✕ E894 while focused ∧ non-empty); multi-line (<c>acceptsReturn</c>)
/// hides it (WinUI: the DeleteButton is suppressed when TextWrapping/AcceptsReturn make the box multi-line).
/// </summary>
public static class TextBox
{
    // WinUI TextControlThemeMinWidth = 64 (generic.xaml:97); the field never narrows below this.
    private const float MinWidth = 64f;
    // WinUI TextBoxTopHeaderMargin = 0,0,0,8 (TextBox_themeresources.xaml:175).
    private const float HeaderMargin = 8f;

    /// <summary>The long-tail TextBox surface (everything past the controlled value) as one record — the WinUI grown
    /// props: placeholder/width, header/description labels, MaxLength, IsReadOnly, AcceptsReturn + multi-line height,
    /// IsEnabled, and the EditableText validation/selection/commit callbacks + a form <see cref="Field{T}"/>.</summary>
    public sealed record TextBoxOptions
    {
        public string Placeholder { get; init; } = "";
        public float Width { get; init; } = 280f;
        public string? Header { get; init; }
        public string? Description { get; init; }
        public int MaxLength { get; init; }
        public bool IsReadOnly { get; init; }
        public bool AcceptsReturn { get; init; }
        public float Height { get; init; } = 32f;   // multi-line: pass the taller box height (WinUI MinHeight stays 32)
        public bool IsEnabled { get; init; } = true;
        public Func<string, bool>? BeforeTextChanging { get; init; }
        public Action<int, int>? OnSelectionChanged { get; init; }
        public Action<string>? OnCommit { get; init; }
        public Action? OnCancel { get; init; }
        /// <summary>OPT-IN: also fire <see cref="OnCommit"/> when focus leaves the field with an uncommitted edit — the
        /// inline-rename contract. Off by default, so an existing TextBox is byte-identical. An Escape that reverts and
        /// blurs does NOT commit, and an Enter that already committed is not re-published on the following blur; see
        /// <see cref="EditableText.CommitOnLostFocus"/> for the two double-commit cases and how they are settled.</summary>
        public bool CommitOnLostFocus { get; init; }
        public Field<string>? Field { get; init; }
    }

    private static readonly TextBoxOptions DefaultOptions = new();

    /// <summary>The text field's surface now: the active theme's (<see cref="ControlStyles.TextBoxFieldStyle"/>), else
    /// <see cref="TextFieldStyle.Fluent"/>. Every <see cref="EditableText"/>-based field (TextBox, PasswordBox, NumberBox, the
    /// ColorPicker hex box) draws its surface from it unless its composer hands it another (AutoSuggestBox, ComboBox).</summary>
    public static TextFieldStyle DefaultFieldStyle => ControlStyles.Resolve(ControlStyles.TextBoxFieldStyle, TextFieldStyle.Fluent);

    /// <summary>Create a TextBox. The controlled value is a two-way <paramref name="text"/> signal (null ⇒ the field
    /// materializes its own — auto-materialize); user edits write it then fire <paramref name="onChange"/>. All other
    /// props live in <paramref name="options"/> (<see cref="TextBoxOptions"/>).</summary>
    public static Element Create(
        Signal<string>? text = null,
        Action<string>? onChange = null,
        TextBoxOptions? options = null)
    {
        var o = options ?? DefaultOptions;
        string placeholder = o.Placeholder;
        string? header = o.Header;
        string? description = o.Description;
        int maxLength = o.MaxLength;
        bool isReadOnly = o.IsReadOnly;
        bool acceptsReturn = o.AcceptsReturn;
        bool isEnabled = o.IsEnabled;
        var beforeTextChanging = o.BeforeTextChanging;
        var onSelectionChanged = o.OnSelectionChanged;
        var onCommit = o.OnCommit;
        var onCancel = o.OnCancel;
        bool commitOnLostFocus = o.CommitOnLostFocus;
        var field = o.Field;
        float w = Math.Max(o.Width, MinWidth);
        float h = Math.Max(o.Height, 32f);       // TextControlThemeMinHeight = 32 (generic.xaml:96)

        // The width is RE-PUSHED (a propless embed freezes its fields at mount): a field whose Width option changes follows it.
        Element editor = Embed.Comp(new EditableText.LiveWidth(w), () => new EditableText
        {
            Placeholder = placeholder, Width = w, Height = h,
            Text = text, MaxLength = maxLength, IsReadOnly = isReadOnly, AcceptsReturn = acceptsReturn,
            IsEnabled = isEnabled,
            // ShowDeleteButton: the WinUI TextBox DeleteButton lane — TextBox is the one composer that turns it on
            // (PasswordBox/NumberBox put their reveal/spin affixes in that lane instead). EditableText itself
            // suppresses it for multi-line/mask/read-only.
            ShowDeleteButton = true,
            OnTextChanged = onChange,
            BeforeTextChanging = beforeTextChanging,
            OnSelectionChanged = onSelectionChanged,
            OnCommit = onCommit,
            OnCancel = onCancel,
            CommitOnLostFocus = commitOnLostFocus,
            // form-validation.md: the editor owns the invalid-border + touched-on-blur; TextBox owns the message row.
            Field = field?.Binding,
        });

        if (header is null && description is null && field is null) return editor;

        var stack = new List<Element>(4);
        if (header is not null)
            // HeaderContentPresenter: Foreground = TextControlHeaderForeground (BaseHigh #FFFFFFFF dark / #FF000000
            // light, generic.xaml:886+207/4132); Disabled → TextControlHeaderForegroundDisabled (BaseMediumLow
            // #66FFFFFF / #66000000, TextBox_themeresources.xaml:258–259 + generic.xaml:887+211/4136); FontSize
            // inherits ControlContentThemeFontSize = 14; Margin = TextBoxTopHeaderMargin 0,0,0,8.
            stack.Add(new TextEl(header)
            {
                Size = 14f,
                Color = isEnabled ? Tok.TextControlHeaderForeground : Tok.TextControlHeaderForegroundDisabled,
                Margin = new Edges4(0, 0, 0, HeaderMargin),
            });
        stack.Add(editor);
        if (description is not null)
            // DescriptionPresenter (TextBox_themeresources.xaml:340): Foreground =
            // SystemControlDescriptionTextForegroundBrush (BaseMedium #99FFFFFF dark / #99000000 light,
            // generic.xaml:327+209/4134); no extra margin in the template (the row sits flush below the field);
            // FontSize inherits ControlContentThemeFontSize = 14.
            stack.Add(new TextEl(description) { Size = 14f, Color = Tok.TextControlDescriptionForeground });

        // form-validation.md: the error message row (shared visual; reveal-animates in, zero space when valid).
        if (field is { } vf)
            stack.Add(FieldVisuals.MessageRow(vf.Error));

        return new BoxEl { Direction = 1, Children = stack.ToArray() };
    }
}

/// <summary>
/// The surface of a text-entry field (WinUI TextBox / AutoSuggestBox / ComboBox field): corner radius, the state fills, the
/// ring, an optional resting underline and the focused bottom bar. Read at RENDER through the control style table
/// (<see cref="ControlStyles.TextBoxFieldStyle"/>, <see cref="ControlStyles.AutoSuggestBoxFieldStyle"/>, and
/// <see cref="ComboBox.Style.Field"/>). <see cref="Fluent"/> is the WinUI field exactly: ControlCornerRadius, the
/// ControlFillColor* ramp, the ControlElevationBorder gradient ring and the 2-DIP accent bar on focus, no underline.
/// <para>An underline field (Zune) is <c>TextFieldStyle.Fluent() with { Radius = 0, Fill = transparent, …, BorderBrush = null,
/// BorderWidth = 0, UnderlineThickness = 1, Underline = … }</c>: the ring goes and a line sits on the bottom edge, recoloured
/// and thickened to the focus bar while focused.</para>
/// </summary>
public sealed record TextFieldStyle
{
    /// <summary>The field's corner radius (ControlCornerRadius).</summary>
    public float Radius { get; init; } = Radii.Control;
    /// <summary>The resting fill (ControlFillColorDefault).</summary>
    public ColorF Fill { get; init; } = Tok.FillControlDefault;
    /// <summary>The pointer-over fill (ControlFillColorSecondary).</summary>
    public ColorF HoverFill { get; init; } = Tok.FillControlSecondary;
    /// <summary>The pressed fill (ControlFillColorTertiary; a ComboBox field, an AutoSuggestBox field).</summary>
    public ColorF PressedFill { get; init; } = Tok.FillControlTertiary;
    /// <summary>The focused (input-active) fill (ControlFillColorInputActive).</summary>
    public ColorF FocusedFill { get; init; } = Tok.FillControlInputActive;
    /// <summary>The disabled fill (ControlFillColorDisabled).</summary>
    public ColorF DisabledFill { get; init; } = Tok.FillControlDisabled;
    /// <summary>The ring as a gradient (ControlElevationBorder); null = no gradient ring (<see cref="BorderColor"/> alone).</summary>
    public GradientSpec? BorderBrush { get; init; } = Tok.ControlElevationBorder;
    /// <summary>The ring as a flat colour, used when <see cref="BorderBrush"/> is null (transparent by default).</summary>
    public ColorF BorderColor { get; init; } = ColorF.Transparent;
    /// <summary>The disabled ring (ControlStrokeColorDefault, flat).</summary>
    public GradientSpec? DisabledBorderBrush { get; init; } = GradientSpec.Solid(Tok.StrokeControlDefault);
    /// <summary>The ring's width (1; 0 = no ring).</summary>
    public float BorderWidth { get; init; } = 1f;
    /// <summary>A resting line along the bottom edge (0 = none, the WinUI field).</summary>
    public float UnderlineThickness { get; init; }
    /// <summary>The resting underline's colour.</summary>
    public ColorF Underline { get; init; } = ColorF.Transparent;
    /// <summary>The focused bottom bar (TextControlElevationBorderFocused's accent stop): its colour and thickness (2).</summary>
    public ColorF FocusBar { get; init; } = Tok.AccentDefault;
    public float FocusBarThickness { get; init; } = 2f;

    /// <summary>The built-in WinUI field. Reads the live <c>Tok</c> on every call.</summary>
    public static TextFieldStyle Fluent() => new();

    /// <summary>Wrap a field whose root is a ROW (an AutoSuggestBox field, a closed ComboBox) so <paramref name="style"/>'s underline
    /// can sit on its bottom edge: the field and the line share an overlay stack that takes over the field's outer sizing. A
    /// style with no underline returns <paramref name="field"/> itself, so the WinUI field keeps its exact node structure.</summary>
    internal static Element WithUnderline(BoxEl field, TextFieldStyle style, bool focused, bool enabled)
    {
        if (style.UnderlineThickness <= 0f) return field;
        bool bar = focused && enabled && style.FocusBarThickness > 0f;
        var line = new BoxEl
        {
            Height = bar ? MathF.Max(style.UnderlineThickness, style.FocusBarThickness) : style.UnderlineThickness,
            AlignSelf = FlexAlign.End,
            Fill = bar ? style.FocusBar : style.Underline,
            HitTestVisible = false,
        };
        return new BoxEl
        {
            ZStack = true,
            Width = field.Width, Height = field.Height,
            MinWidth = field.MinWidth, MaxWidth = field.MaxWidth, MinHeight = field.MinHeight, MaxHeight = field.MaxHeight,
            Grow = field.Grow, Shrink = field.Shrink, Basis = field.Basis, AlignSelf = field.AlignSelf, Margin = field.Margin,
            Children = [field with { Margin = default }, line],
        };
    }
}

