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
                rows[i] = new BoxEl
                {
                    Height = 34f, AlignItems = FlexAlign.Center, Padding = new Edges4(11, 0, 11, 0), Corners = Radii.ControlAll,
                    Role = AutomationRole.MenuItem,
                    Fill = i == sel ? Tok.FillSubtleSecondary : ColorF.Transparent,
                    HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    OnClick = () => Choose(idx),
                    Children = [new TextEl(Items[i]) { Size = 14f, Color = Tok.TextPrimary }],
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
                Direction = 0, Width = Width, MinHeight = 34f, AlignItems = FlexAlign.Center,
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
            Direction = 0, Width = Width, MinHeight = 34f, AlignItems = FlexAlign.Center, Padding = new Edges4(11, 5, 0, 6),
            Corners = Radii.ControlAll, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary,
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
