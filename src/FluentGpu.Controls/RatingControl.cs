using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>A WinUI RatingControl: a row of stars set by click or drag (press-and-sweep). The value is a caller
/// <see cref="FloatSignal"/> (so a page can read it); <see cref="ReadOnly"/> shows a fixed rating.</summary>
public sealed class RatingControl : Component
{
    public FloatSignal Value = new(0f);
    public int Max = 5;
    public float StarSize = 24f;
    public float Gap = 4f;
    public bool ReadOnly;
    public Action<float>? OnChange;

    public static Element Create(FloatSignal value, int max = 5, bool readOnly = false, Action<float>? onChange = null)
        => Embed.Comp(() => new RatingControl { Value = value, Max = max, ReadOnly = readOnly, OnChange = onChange });

    public override Element Render()
    {
        float cur = Value.Value;            // subscribes → the row re-renders as the rating changes
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
            stars[i] = new TextEl(Icons.Star)
            {
                Size = StarSize,
                Color = filled ? Tok.AccentDefault : Tok.FillControlStrong,
                FontFamily = Theme.IconFont,
            };
        }

        return new BoxEl
        {
            Direction = 0,
            Gap = Gap,
            AlignItems = FlexAlign.Center,
            Role = AutomationRole.Rating,
            OnPointerDown = ReadOnly ? null : Set,
            OnDrag = ReadOnly ? null : Set,
            Children = stars,
        };
    }
}
