using FluentGpu.Dsl;
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

    /// <summary>Create a TextBox. The original 3-parameter shape is source-compatible; everything after
    /// <paramref name="header"/> is the grown WinUI surface (Description, two-way Text signal, MaxLength, IsReadOnly,
    /// AcceptsReturn + multi-line height, IsEnabled, and the EditableText change/selection/commit callbacks).</summary>
    public static Element Create(
        string placeholder = "", float width = 280f, string? header = null,
        string? description = null,
        Signal<string>? text = null,
        int maxLength = 0,
        bool isReadOnly = false,
        bool acceptsReturn = false,
        float height = 32f,                      // multi-line: pass the taller box height (WinUI MinHeight stays 32)
        bool isEnabled = true,
        Action<string>? onTextChanged = null,
        Func<string, bool>? beforeTextChanging = null,
        Action<int, int>? onSelectionChanged = null,
        Action<string>? onCommit = null,
        Action? onCancel = null)
    {
        float w = Math.Max(width, MinWidth);
        float h = Math.Max(height, 32f);         // TextControlThemeMinHeight = 32 (generic.xaml:96)

        Element field = Embed.Comp(() => new EditableText
        {
            Placeholder = placeholder, Width = w, Height = h,
            Text = text, MaxLength = maxLength, IsReadOnly = isReadOnly, AcceptsReturn = acceptsReturn,
            IsEnabled = isEnabled,
            // ShowDeleteButton: the WinUI TextBox DeleteButton lane — TextBox is the one composer that turns it on
            // (PasswordBox/NumberBox put their reveal/spin affixes in that lane instead). EditableText itself
            // suppresses it for multi-line/mask/read-only.
            ShowDeleteButton = true,
            OnTextChanged = onTextChanged,
            BeforeTextChanging = beforeTextChanging,
            OnSelectionChanged = onSelectionChanged,
            OnCommit = onCommit,
            OnCancel = onCancel,
        });

        if (header is null && description is null) return field;

        var stack = new List<Element>(3);
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
        stack.Add(field);
        if (description is not null)
            // DescriptionPresenter (TextBox_themeresources.xaml:340): Foreground =
            // SystemControlDescriptionTextForegroundBrush (BaseMedium #99FFFFFF dark / #99000000 light,
            // generic.xaml:327+209/4134); no extra margin in the template (the row sits flush below the field);
            // FontSize inherits ControlContentThemeFontSize = 14.
            stack.Add(new TextEl(description) { Size = 14f, Color = Tok.TextControlDescriptionForeground });

        return new BoxEl { Direction = 1, Children = stack.ToArray() };
    }
}
