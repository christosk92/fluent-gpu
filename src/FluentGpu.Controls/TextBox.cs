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
        public Field<string>? Field { get; init; }
    }

    private static readonly TextBoxOptions DefaultOptions = new();

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
        var field = o.Field;
        float w = Math.Max(o.Width, MinWidth);
        float h = Math.Max(o.Height, 32f);       // TextControlThemeMinHeight = 32 (generic.xaml:96)

        Element editor = Embed.Comp(() => new EditableText
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
