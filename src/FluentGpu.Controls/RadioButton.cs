using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI RadioButton: a ring + (when selected) a filled accent dot, plus a label. Mutual exclusion comes from a
/// shared selected index — use <see cref="Group"/> to render a set of options over one selected-index callback, or
/// <see cref="Create"/> for a single button you wire up yourself. Controlled.
/// </summary>
public static partial class RadioButton
{
    public sealed record Style
    {
        public float RingSize { get; init; } = 20f;
        public float DotSize { get; init; } = 10f;
        public float FontSize { get; init; } = 14f;
        public float MinHeight { get; init; } = 32f;
        public ColorF OffFill { get; init; }
        public ColorF OffHover { get; init; }
        public ColorF OffBorder { get; init; }
        public ColorF OnRing { get; init; }
        public ColorF Dot { get; init; }
        public ColorF Foreground { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OffFill = Tok.FillControlDefault, OffHover = Tok.FillControlSecondary, OffBorder = Tok.StrokeControlSecondary,
        OnRing = Tok.AccentDefault, Dot = Tok.TextOnAccentPrimary, Foreground = Tok.TextPrimary,
    };

    public static BoxEl Create(string label, bool selected, Action onSelect, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        var ring = new BoxEl
        {
            Width = s.RingSize, Height = s.RingSize,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(s.RingSize),
            BorderWidth = selected ? 0f : 1f,
            BorderColor = selected ? ColorF.Transparent : s.OffBorder,
            Fill = selected ? s.OnRing : s.OffFill,
            HoverFill = selected ? s.OnRing : s.OffHover,
            Children = selected ? [new BoxEl { Width = s.DotSize, Height = s.DotSize, Corners = Radii.Circle(s.DotSize), Fill = s.Dot }] : [],
        };

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 10f,
            MinHeight = s.MinHeight,
            Role = AutomationRole.RadioButton,
            OnClick = onSelect,
            Children = [ring, new TextEl(label) { Size = s.FontSize, Color = s.Foreground }],
        };
    }

    /// <summary>A mutually-exclusive group: renders one radio per option; clicking option i invokes <paramref name="onSelect"/>(i).</summary>
    public static BoxEl Group(IReadOnlyList<string> options, int selected, Action<int> onSelect, bool horizontal = false, Style? style = null)
    {
        var children = new Element[options.Count];
        for (int i = 0; i < options.Count; i++)
        {
            int idx = i;
            children[i] = Create(options[i], i == selected, () => onSelect(idx), style);
        }
        return new BoxEl { Direction = horizontal ? (byte)0 : (byte)1, Gap = horizontal ? 16f : 4f, Children = children };
    }
}
