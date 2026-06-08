using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ListView: a vertical list of selectable rows. Each row is a transparent pill that highlights on hover
/// (<see cref="Tok.FillSubtleSecondary"/>) and press (<see cref="Tok.FillSubtleTertiary"/>); the selected row gets a
/// dedicated selected fill plus a 3x16 accent pill at the leading edge. The pill is a MOUNTED ZStack overlay layer
/// (it does NOT sit in the content flow, so the label never shifts on select) and reveal-animates via a spring +
/// opacity transition like the NavigationView indicator. Single-selection, controllable via a caller <see cref="Signal{T}"/>.
/// </summary>
public sealed class ListView : Component
{
    public IReadOnlyList<string> Items = [];
    public Signal<int> SelectedIndex = new(-1);          // default unselected, like ComboBox
    public Action<int>? OnSelectionChanged;

    public static Element Create(IReadOnlyList<string> items,
                                 Signal<int>? selectedIndex = null,
                                 Action<int>? onSelectionChanged = null)
        => Embed.Comp(() => new ListView
        {
            Items = items,
            SelectedIndex = selectedIndex ?? new Signal<int>(-1),
            OnSelectionChanged = onSelectionChanged,
        });

    public override Element Render()
    {
        int sel = SelectedIndex.Value;

        void Choose(int i)
        {
            SelectedIndex.Value = i;
            OnSelectionChanged?.Invoke(i);
        }

        var rows = new Element[Items.Count];
        for (int i = 0; i < Items.Count; i++)
        {
            int idx = i;                  // capture for the click closure
            bool selected = idx == sel;

            var label = new TextEl(Items[idx])
            {
                Size = 14f,
                Color = Tok.TextPrimary,
                Grow = 1f,
            };

            // Content layer carries the row's padding so the pill overlay is positioned against the row's true leading
            // edge (independent of content padding). Always [label] — the pill never lives in the flow.
            var content = new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Grow = 1f,
                Padding = new Edges4(12, 0, 12, 0),
                Children = [label],
            };

            // Accent pill is a mounted ZStack overlay (3x16) — ArrangeZStack keeps its explicit size and overlays it at
            // the row origin without consuming flex track, so the label position is identical selected vs unselected.
            var pill = Embed.Comp(() => new ListViewPillIndicator { Selected = selected });

            rows[idx] = new BoxEl
            {
                ZStack = true,
                MinHeight = 40f,
                AlignItems = FlexAlign.Center,
                Margin = new Edges4(4, 2, 4, 2),
                Corners = Radii.ControlAll,
                // Selected gets a dedicated resting fill; hover/press use a distinct tier so selected != hovered.
                Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
                HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
                PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
                Role = AutomationRole.Button,
                OnClick = () => Choose(idx),
                Children = [pill, content],
            };
        }

        return new BoxEl
        {
            Direction = 1,
            Children = rows,
        };
    }
}

/// <summary>The 3x16 accent selection pill for a <see cref="ListView"/> row. Modeled on the NavigationView indicator:
/// it stays MOUNTED for every row and reveal-animates (ScaleY spring grows it from center, opacity fades in) instead of
/// popping in/out, so reselection feels continuous. File-local — used only by <see cref="ListView"/>.</summary>
internal sealed class ListViewPillIndicator : Component
{
    public bool Selected;

    public override Element Render()
    {
        // Mounted for every row (stable child slot); when unselected it collapses to nothing. Visibility is NOT gated
        // on an animation reaching its target (the earlier ScaleY/Opacity reveal left a select-on-mount pill at 0).
        if (!Selected) return new BoxEl { Width = 0f, Height = 0f };
        return new BoxEl
        {
            Width = 3f,
            Height = 16f,
            Margin = new Edges4(4f, 0f, 0f, 0f),   // ~4px leading-edge inset, matching NavIndicator
            Corners = CornerRadius4.All(1.5f),     // ListViewItemSelectionIndicatorCornerRadius = 1.5
            Fill = Tok.AccentDefault,
            AlignSelf = FlexAlign.Center,          // vertically center within the row (ArrangeZStack honors this now)
        };
    }
}
