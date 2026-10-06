using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

// The rarely-set TextEl channels, split out so a label that sets none costs one null reference. Copy-on-write like BoxCold:
// TextEl's `with` copies share the instance until a setter that changes a value runs on the copy, which clones it first
// (Owner tells a clone made for that element from a shared one). Defaults mirror the former inline initializers exactly;
// Owner is excluded from equality. Owner keeps the element it was cloned for alive; that is bounded (one per with-chain link that mutated the block) and dies with the block.
internal sealed class TextCold : IEquatable<TextCold>
{
    public static readonly TextCold Default = new();

    public TextEl? Owner;

    public float CharSpacing;
    public LineStacking LineStacking = LineStacking.MaxHeight;
    public TextLineBounds LineBounds = TextLineBounds.Full;
    public ColorF HoverColor;
    public ColorF PressedColor;
    public ColorF DisabledColor;
    public ColorF FocusedColor;
    public bool Underline;
    public bool Strikethrough;
    public float BrushTransitionMs = float.NaN;
    public GlyphWipe? Wipe;
    public bool IsTextSelectionEnabled;
    public ColorF SelectionHighlightColor;
    public DynamicTextKind DynamicText;
    public float MinSize = float.NaN;

    public TextCold CloneFor(TextEl owner)
    {
        var c = (TextCold)MemberwiseClone();
        c.Owner = owner;
        return c;
    }

    public bool Equals(TextCold? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null) return false;
        return EqualityComparer<float>.Default.Equals(CharSpacing, o.CharSpacing)
            && EqualityComparer<LineStacking>.Default.Equals(LineStacking, o.LineStacking)
            && EqualityComparer<TextLineBounds>.Default.Equals(LineBounds, o.LineBounds)
            && EqualityComparer<ColorF>.Default.Equals(HoverColor, o.HoverColor)
            && EqualityComparer<ColorF>.Default.Equals(PressedColor, o.PressedColor)
            && EqualityComparer<ColorF>.Default.Equals(DisabledColor, o.DisabledColor)
            && EqualityComparer<ColorF>.Default.Equals(FocusedColor, o.FocusedColor)
            && EqualityComparer<bool>.Default.Equals(Underline, o.Underline)
            && EqualityComparer<bool>.Default.Equals(Strikethrough, o.Strikethrough)
            && EqualityComparer<float>.Default.Equals(BrushTransitionMs, o.BrushTransitionMs)
            && EqualityComparer<GlyphWipe?>.Default.Equals(Wipe, o.Wipe)
            && EqualityComparer<bool>.Default.Equals(IsTextSelectionEnabled, o.IsTextSelectionEnabled)
            && EqualityComparer<ColorF>.Default.Equals(SelectionHighlightColor, o.SelectionHighlightColor)
            && EqualityComparer<DynamicTextKind>.Default.Equals(DynamicText, o.DynamicText)
            && EqualityComparer<float>.Default.Equals(MinSize, o.MinSize);
    }

    public override bool Equals(object? obj) => Equals(obj as TextCold);
    public override int GetHashCode() => 0;
}
