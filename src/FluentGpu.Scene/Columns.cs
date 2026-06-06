using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Scene;

public enum VisualKind : byte { None = 0, Box = 1, Text = 2 }

/// <summary>Layout-input column (slice flex subset: stack direction + gap + padding + explicit size + text style).</summary>
public struct LayoutInput
{
    public byte Direction;        // 0 = row (main = X), 1 = column (main = Y)
    public float Gap;             // between-children spacing on the main axis
    public Edges4 Padding;
    public float Width;           // NaN = auto (content)
    public float Height;          // NaN = auto (content)
    public TextStyle TextStyle;   // for VisualKind.Text leaves

    public static LayoutInput Default => new()
    {
        Direction = 1,            // default container stacks vertically
        Gap = 0,
        Padding = default,
        Width = float.NaN,
        Height = float.NaN,
    };
}

/// <summary>Paint column — one cache line of per-node visual state read by the record phase.</summary>
public struct NodePaint
{
    public Affine2D LocalTransform;
    public float Opacity;
    public ColorF Fill;
    public ColorF BorderColor;
    public float BorderWidth;
    public CornerRadius4 Corners;
    public ColorF TextColor;
    public StringId Text;
    public VisualKind VisualKind;

    public static NodePaint Default => new()
    {
        LocalTransform = Affine2D.Identity,
        Opacity = 1f,
        Fill = ColorF.Transparent,
        VisualKind = VisualKind.None,
    };
}

/// <summary>Hit-test / input column.</summary>
public struct InteractionInfo
{
    public ushort HandlerMask;    // bit0 = click/pointer
    public CursorId Cursor;
    public const ushort ClickBit = 1;
}
