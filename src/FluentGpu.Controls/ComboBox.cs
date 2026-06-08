using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ComboBox: a closed box showing the selected item + chevron that opens a dropdown list (an anchored overlay).
/// In <see cref="Editable"/> mode the closed box is an <see cref="EditableText"/> field plus a dropdown chevron. Selection
/// is a caller <see cref="Signal{T}"/> so a page can read it; the dropdown width matches the box.
/// </summary>
public sealed class ComboBox : Component
{
    public IReadOnlyList<string> Items = [];
    public Signal<int> SelectedIndex = new(-1);
    public bool Editable;
    public Signal<string>? Text;
    public string Placeholder = "";
    public float Width = 220f;
    public Action<int>? OnSelectionChanged;

    public static Element Create(IReadOnlyList<string> items, Signal<int> selectedIndex, bool editable = false,
                                 Signal<string>? text = null, float width = 220f, string placeholder = "", Action<int>? onSelectionChanged = null)
        => Embed.Comp(() => new ComboBox
        {
            Items = items, SelectedIndex = selectedIndex, Editable = editable, Text = text,
            Width = width, Placeholder = placeholder, OnSelectionChanged = onSelectionChanged,
        });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var fallbackText = UseSignal("");
        var text = Text ?? fallbackText;
        var svc = UseContext(Overlay.Service);
        int sel = SelectedIndex.Value;

        void Choose(int i)
        {
            SelectedIndex.Value = i;
            if (Editable) text.Value = Items[i];
            OnSelectionChanged?.Invoke(i);
            handle.Value?.Close();
        }

        Element List()
        {
            var rows = new Element[Items.Count];
            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                bool selected = idx == sel;

                // WinUI ComboBoxItemPill: 3w x 16h left accent pill (Fill=AccentFillColorDefault, corner 1.5), vertically
                // centered, shown only on the selected row — identical construction to ListView / NavIndicator / SelectorBar.
                var label = new TextEl(Items[idx]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f };
                var content = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = 9f, Grow = 1f,
                    Children = selected
                        ? [new BoxEl { Width = 3f, Height = 16f, Corners = Radii.Circle(3f), Fill = Tok.AccentDefault, AlignSelf = FlexAlign.Center, Margin = new Edges4(1, 0, 0, 0) }, label]
                        : [label],
                };

                rows[i] = new BoxEl
                {
                    // WinUI ComboBoxItem: padding 11,5,11,7; margin 5,2,5,2; CornerRadius 3 (ComboBoxItemCornerRadius); auto height.
                    AlignItems = FlexAlign.Center, Padding = new Edges4(11, 5, 11, 7), Margin = new Edges4(5, 2, 5, 2),
                    Corners = CornerRadius4.All(3f),
                    Role = AutomationRole.MenuItem,
                    // Selected rows invert the hover/press ramp like ListView (WinUI SelectedPointerOver=Tertiary,
                    // SelectedPressed=Secondary); unselected use the standard Secondary→Tertiary ramp.
                    Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
                    HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
                    PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
                    OnClick = () => Choose(idx),
                    Children = [content],
                };
            }
            return new BoxEl { Direction = 1, Width = Width, Padding = new Edges4(4, 4, 4, 4), Children = rows };
        }

        void ToggleList()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, List, FlyoutPlacement.BottomStretch);
        }

        var chevron = new BoxEl
        {
            Width = 30f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Role = AutomationRole.Button,
            HoverFill = Tok.FillControlSecondary, OnClick = ToggleList,
            Children = [new TextEl(Icons.ChevronDown) { Size = 10f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont }],
        };

        if (Editable)
        {
            return new BoxEl
            {
                Direction = 0, Width = Width, MinHeight = 32f, AlignItems = FlexAlign.Center,   // WinUI ComboBoxMinHeight = 32
                Corners = Radii.ControlAll, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, Fill = Tok.FillControlDefault,
                Role = AutomationRole.ComboBox,
                OnRealized = h => anchor.Value = h,
                Children =
                [
                    Embed.Comp(() => new EditableText { Text = text, Width = Width - 30f, Height = 32f, Placeholder = Placeholder }),
                    chevron,
                ],
            };
        }

        string label = sel >= 0 && sel < Items.Count ? Items[sel] : Placeholder;
        return new BoxEl
        {
            // WinUI ComboBox: MinHeight=32, ComboBoxPadding=12,5,0,7, CornerRadius=ControlCornerRadius (4), BorderThickness=1.
            Direction = 0, Width = Width, MinHeight = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(12, 5, 0, 7),
            Corners = Radii.ControlAll, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
            // CommonStates: PointerOver=ControlFillColorSecondary, Pressed=ControlFillColorTertiary.
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            Role = AutomationRole.ComboBox,
            OnRealized = h => anchor.Value = h,
            OnClick = ToggleList,
            Children =
            [
                new TextEl(label) { Size = 14f, Color = sel >= 0 ? Tok.TextPrimary : Tok.TextSecondary, Grow = 1f },
                new TextEl(Icons.ChevronDown) { Size = 10f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont, Margin = new Edges4(0, 0, 11, 0) },
            ],
        };
    }
}
