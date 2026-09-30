using FluentGpu.Foundation;

namespace FluentGpu.Scroll.Effects;

/// <summary>
/// One node's transform-class scroll effects folded for a tick (every row on <see cref="EffectChannel.TransX"/>,
/// <see cref="EffectChannel.TransY"/> or <see cref="EffectChannel.ScaleXY"/> — the rows share the node's single
/// <c>LocalTransform</c>, so they COMPOSE instead of the last writer winning: a parallax and an overscroll stretch on the
/// same hero photo both land). Translations add, scales multiply.
/// </summary>
/// <param name="Scale">Product of the plain <see cref="EffectKind.Map"/> scale rows — applied about the node's authored
/// transform origin (the recorder conjugates <c>LocalTransform</c> about it).</param>
/// <param name="Tx">Sum of the horizontal translation rows, DIP.</param>
/// <param name="Ty">Sum of the vertical translation rows, DIP.</param>
/// <param name="Stretch">Product of the <see cref="EffectKind.Stretch"/> rows (1 = none): scales about the node's
/// TOP-CENTRE whatever its authored origin, and carries its own pull-cancelling translation.</param>
public readonly record struct EffectTransform(float Scale, float Tx, float Ty, float Stretch)
{
    /// <summary>No effect: identity.</summary>
    public static readonly EffectTransform Identity = new(1f, 0f, 0f, 1f);

    /// <summary>Folds one evaluated row into the accumulator.</summary>
    public EffectTransform Add(EffectChannel channel, EffectKind kind, float value) => channel switch
    {
        EffectChannel.TransX => this with { Tx = Tx + value },
        EffectChannel.TransY => this with { Ty = Ty + value },
        EffectChannel.ScaleXY when kind == EffectKind.Stretch => this with { Stretch = Stretch * value },
        EffectChannel.ScaleXY => this with { Scale = Scale * value },
        _ => this,
    };

    /// <summary>The node-local matrix the recorder conjugates about the node's transform origin
    /// (<c>T(o)·L·T(−o)</c>, <paramref name="originX"/>/<paramref name="originY"/> in DIP within the node box of
    /// <paramref name="width"/>×<paramref name="height"/>). The stretch pivot is re-expressed against that origin:
    /// drawn <c>y' = s·y + ty</c> about the top edge with <c>ty = −(s−1)·H</c> (the pull), and <c>x' = s·(x − W/2) + W/2</c>
    /// ⇒ <c>Dx += (s−1)(ox − W/2)</c>, <c>Dy += (s−1)(oy − H)</c>.</summary>
    public Affine2D ToLocal(float width, float height, float originX, float originY)
    {
        float s = Scale * Stretch;
        float dx = Tx, dy = Ty;
        if (Stretch != 1f)
        {
            float k = Stretch - 1f;
            dx += k * (originX - width * 0.5f);
            dy += k * (originY - height);
        }
        return new Affine2D(s, 0f, 0f, s, dx, dy);
    }
}
