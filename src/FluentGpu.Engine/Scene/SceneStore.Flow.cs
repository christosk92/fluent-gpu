using FluentGpu.Foundation;

namespace FluentGpu.Scene;

public sealed partial class SceneStore
{
    /// <summary>The rect <paramref name="h"/> is PRESENTED at (window DIP): <see cref="AbsoluteRect"/> plus every
    /// SizeMode.FlowReveal shift the recorder applies up its chain, at its presented height. Walks each shifting
    /// ancestor's children up to the chain node (O(depth × siblings)) — gates, diagnostics and cold geometry queries only.
    /// At rest it equals AbsoluteRect.</summary>
    public RectF PresentedAbsoluteRect(NodeHandle h)
    {
        RectF a = AbsoluteRect(h);
        float dy = 0f;
        for (NodeHandle n = h, parent = Parent(h); !parent.IsNull; n = parent, parent = Parent(parent))
        {
            var cursor = FlowCursor.For(in _paint[parent.Raw.Index]);
            if (TryGetRevealBands(parent, out var bands, out byte mask, out int prefix, out int firstRealized)) cursor.SetBands(in bands, mask, prefix, firstRealized);
            if (!cursor.Active) continue;
            int ordinal = 0;
            for (var c = FirstChild(parent); !c.IsNull; c = NextSibling(c), ordinal++)
            {
                float s = cursor.Step(ordinal, _bounds[c.Raw.Index].Y, _paint[c.Raw.Index].FlowDelta, out _, out _);
                if (c == n) { dy += s; break; }
            }
        }
        ref readonly NodePaint p = ref _paint[h.Raw.Index];
        // Both presented extents (a Reveal's width as well as its height): what the recorder draws and hit-testing reads (InputDispatcher's hitW).
        return new RectF(a.X, a.Y + dy, float.IsNaN(p.PresentedW) ? a.W : p.PresentedW, float.IsNaN(p.PresentedH) ? a.H : p.PresentedH);
    }
}
