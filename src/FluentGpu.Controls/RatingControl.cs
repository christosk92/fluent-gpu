using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>A WinUI RatingControl: a row of stars set by click or drag (press-and-sweep). The value is a caller
/// <see cref="FloatSignal"/> (so a page can read it); <see cref="ReadOnly"/> shows a fixed rating.</summary>
public sealed class RatingControl : Component
{
    // WinUI uses two glyphs: E734 (outline) for the unselected/background layer, E735 (filled) for the selected layer.
    const string OutlineStar = "";
    const string FilledStar = "";

    public FloatSignal Value = new(0f);
    public int Max = 5;
    public float StarSize = 24f;
    public float Gap = 8f;                  // RatingControlItemSpacing = 8 (was 4)
    public float MinHeight = 32f;           // WinUI MinHeight
    public bool ReadOnly;
    public Action<float>? OnChange;

    public static Element Create(FloatSignal value, int max = 5, bool readOnly = false, Action<float>? onChange = null)
        => Embed.Comp(() => new RatingControl { Value = value, Max = max, ReadOnly = readOnly, OnChange = onChange });

    public override Element Render()
    {
        float cur = Value.Value;            // subscribes -> the row re-renders as the rating changes
        float stride = StarSize + Gap;

        void Set(Point2 p)
        {
            if (ReadOnly) return;
            int v = Math.Clamp((int)MathF.Ceiling((p.X + 0.001f) / stride), 0, Max);
            if (v != (int)MathF.Round(Value.Peek())) { Value.Value = v; OnChange?.Invoke(v); }
        }

        var stars = new Element[Max];
        for (int i = 0; i < Max; i++)
        {
            bool filled = i < cur;
            // RatingControlSelectedForeground -> AccentDefault; RatingControlUnselectedForeground -> TextSecondary;
            // RatingControlDisabledSelectedForeground -> TextDisabled (ReadOnly renders a fixed, dimmed rating).
            ColorF color = ReadOnly
                ? (filled ? Tok.TextDisabled : Tok.TextSecondary)
                : (filled ? Tok.AccentDefault : Tok.TextSecondary);
            stars[i] = new TextEl(filled ? FilledStar : OutlineStar)
            {
                Size = StarSize,
                Color = color,
                FontFamily = Theme.IconFont,
            };
        }

        return new BoxEl
        {
            Direction = 0,
            Gap = Gap,
            MinHeight = MinHeight,
            AlignItems = FlexAlign.Center,
            Role = AutomationRole.Rating,
            OnPointerDown = ReadOnly ? null : Set,
            OnDrag = ReadOnly ? null : Set,
            Children = stars,
        };
    }
}
