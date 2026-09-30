namespace FluentGpu.Scroll.Effects;

/// <summary>
/// The static geometry a <see cref="ScrollEffect"/> needs to evaluate, all in CONTENT coordinates (double precision —
/// the same frame the extent tables and scroll offset live in; only the returned paint value narrows to float).
/// Captured once per frame per bound node/track; <see cref="ScrollEffectEval.Evaluate"/> takes it by <c>in</c> so
/// evaluating many effects against one geometry costs no copies.
/// </summary>
/// <param name="NodeY">The bound node's top, in content coordinates (0 = start of the scroller's content).</param>
/// <param name="NodeH">The bound node's height, in content coordinates.</param>
/// <param name="ScopeEnd">The end (bottom) of the sticky containing block, in content coordinates — the point past
/// which a <see cref="EffectKind.Sticky"/>/<see cref="EffectKind.StickyClip"/> effect releases.</param>
/// <param name="Extent">The scroller's total content extent (design doc's <c>IExtentSource.Total</c>).</param>
/// <param name="Viewport">The scroller's viewport extent along the scroll axis.</param>
/// <param name="Track">The scrollbar track length, DIP (paint space — float is exact at UI scale).</param>
/// <param name="ThumbLen">The scrollbar thumb length, DIP.</param>
public readonly record struct EffectGeometry(
    double NodeY,
    double NodeH,
    double ScopeEnd,
    double Extent,
    double Viewport,
    float Track,
    float ThumbLen);
