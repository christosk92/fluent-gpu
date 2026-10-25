using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Text;

namespace FluentGpu.Reconciler;

// ── Shape prefetch at the reconciler's text writes (PERF r3 MODEL, on UI's C4 seam) ─────────────────────────────────────────
// The layout pass hands its new text to the shaping workers just before it measures (FlexLayout.PrefetchTexts). The text is
// known earlier: the reconciler writes a node's text and style while the flush mounts or re-renders it, and the flush then still
// renders and reconciles the rest of the page before layout starts. Handing the strings over at the write gives the workers that
// time: the measure finds more of them shaped. The same batch rule as the layout pass: nothing is handed over until a pass has 4
// or more new strings (one or two, a ticking label or a keystroke, shape inline faster than a hand-off); once a pass reaches 4,
// every further write of the pass goes at once. A pass ends with each reactive flush. Output: identical (the workers make the
// call the measure would; the store's in-flight key makes a second hand-off of the same string a no-op).
public sealed partial class TreeReconciler
{
    /// <summary>The font system whose workers shape ahead (the host's; null: no prefetch).</summary>
    public IFontSystem? Fonts { get; set; }

    private const int TextPrefetchBatchMin = 4;   // FlexLayout.PrefetchBatchMin
    private readonly List<NodeHandle> _textPrefetch = new();
    private bool _textPrefetchOpen;

    /// <summary>Strings handed to the shaping workers from text writes (cumulative; census).</summary>
    public long TextWritesPrefetched { get; private set; }

    /// <summary>The reconciler wrote <paramref name="node"/>'s text or text style (a mount, a changed text or style, a bound text).</summary>
    private void NoteTextWrite(NodeHandle node)
    {
        var fonts = Fonts;
        if (fonts is null || !fonts.CanPrefetchShapes) return;
        _scene.ShapeHanded(node) = false;   // a new text or style: not handed (yet), so the layout walk would prefetch it
        if (_scene.Paint(node).Text.IsEmpty) return;
        if (_textPrefetchOpen) { PrefetchTextNode(fonts, node); return; }
        _textPrefetch.Add(node);
        if (_textPrefetch.Count < TextPrefetchBatchMin) return;
        foreach (var n in _textPrefetch) PrefetchTextNode(fonts, n);
        _textPrefetch.Clear();
        _textPrefetchOpen = true;
    }

    private void PrefetchTextNode(IFontSystem fonts, NodeHandle n)
    {
        if (!_scene.IsLive(n)) return;
        ref NodePaint paint = ref _scene.Paint(n);
        if (paint.Text.IsEmpty) return;
        fonts.PrefetchShape(paint.Text, in _scene.Layout(n).TextStyle);
        _scene.ShapeHanded(n) = true;   // the layout pass's walk skips it: one hand-off per string, not two
        TextWritesPrefetched++;
    }

    /// <summary>End of a prefetch pass (the host calls it after each reactive flush): fewer than the batch minimum are dropped.</summary>
    public void EndTextPrefetchPass()
    {
        _textPrefetch.Clear();
        _textPrefetchOpen = false;
    }
}
