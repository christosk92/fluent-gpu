using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

/// <summary>
/// A virtualized collection viewport: ONE retained node + a window of keyed children. Each frame the visible range is
/// dirty, the reconciler asks the <see cref="Layout"/> for <c>[first,last)</c>, calls <see cref="RenderItem"/> for just
/// that window, keys each row, and feeds it to the existing keyed-LIS diff — so recycling IS CreateNode/FreeNode over
/// the slab free-list (no second pool). Scroll is layout-free (the <c>-ScrollOffset</c> is the content's transform).
///
/// <see cref="Layout"/> is a pluggable <see cref="IVirtualLayout"/> (stack / grid / custom — pure, allocation-free).
/// E11-L1: the viewport consumes BOTH seam kinds — an <see cref="IMeasuredVirtualLayout"/> gets the variable-extent
/// estimate-then-correct arrange (measured rows + scroll anchoring) through the SAME property; when the layout is
/// null, the legacy built-in Fenwick extent-table path runs (kept source/behavior-compatible).
/// The user-facing <c>Virtual.List/Grid/Custom/Measured/VariableList/…</c> factories live in FluentGpu.Controls; this
/// record is the engine primitive the reconciler diffs directly (ElementTypeId 6). See <c>design/subsystems/virtualization.md</c>.
/// </summary>
public sealed record VirtualListEl : Element
{
    public override ushort ElementTypeId => 6;

    public int ItemCount { get; init; }
    public Func<int, Element> RenderItem { get; init; } = static _ => new BoxEl();
    public Func<int, string>? KeyOf { get; init; }
    /// <summary>Signals-first BOUND row template (the recycler fast path): the template runs ONCE per visible slot
    /// with an index SIGNAL; scrolling rebinds a slot by writing its signal, so only the row's reactive binds
    /// (TextBind/FillBind/SourceBind/…) re-run — zero element rebuild, zero reconcile, zero keys. When set,
    /// <see cref="RenderItem"/>/<see cref="KeyOf"/> are ignored.</summary>
    public Func<IReadSignal<int>, Element>? RowBind { get; init; }
    public IVirtualLayout? Layout { get; init; }      // fixed OR measured (IMeasuredVirtualLayout) seam; null ⇒ legacy variable Fenwick
    public float EstimatedExtent { get; init; } = 48f;// legacy variable path: seed extent for unmeasured rows
    public int Overscan { get; init; } = 4;
    /// <summary>Research adjustment #16 — pre-realize CACHE EXTENT in PIXELS beyond the viewport (per edge). Overscan is
    /// row-based (<see cref="Overscan"/>); this is a pixel band converted to a row count against the average row extent
    /// at realize time and used as the effective overscan when set. <see cref="float.NaN"/> (default) ⇒ <see cref="Overscan"/>
    /// stays authoritative (byte-identical to the pre-knob path).</summary>
    public float CacheExtentPx { get; init; } = float.NaN;
    /// <summary>Research adjustment #16 — recycle-pool discriminator for the BOUND path (<see cref="RowBind"/>):
    /// <c>index → contentType</c>. A slot only rebinds to an index whose content-type matches the type it was built for;
    /// a cross-type reuse falls back to a full element rebuild (fresh slot). Null ⇒ one homogeneous pool (today's cheap
    /// rebind for every recycle). Ignored on the <see cref="RenderItem"/> path.</summary>
    public Func<int, int>? ContentType { get; init; }
    /// <summary>Research adjustment #5 — keep-alive predicate for the BOUND path (<see cref="RowBind"/>): <c>index → true</c>
    /// marks an item whose slot must NOT be index-rebound when it scrolls off-window. Its subtree parks HIDDEN (detached,
    /// no layout/paint, render-effects/animations quiesced — the <c>Flow.KeepAlive</c> parking mechanics) and is excluded
    /// from the recycle pool until the item re-enters the window or the bounded bucket evicts it (LRU). Null (default) ⇒
    /// no keep-alive bucket (recycled slots lose live state; byte-identical to the pre-#5 path). Ignored on RenderItem.</summary>
    public Func<int, bool>? KeepAlive { get; init; }
    /// <summary>Bounded keep-alive bucket cap (default 8): the most parked keep-alive slots retained at once; the LRU is
    /// evicted (subtree unmounted) beyond it. Only consulted when <see cref="KeepAlive"/> is set.</summary>
    public int KeepAliveCap { get; init; } = 8;
    public bool Horizontal { get; init; }
    /// <summary>Opt-in cold-mount stagger (bound lists only): when true, a freshly-mounted list realizes its large
    /// initial window a few rows PER FRAME instead of all at once — trading a couple of frames of staggered fill for
    /// removing the one-frame mount spike (the nav cold-mount stutter). Off by default: small/simple lists realize in a
    /// single frame (and the golden recycle/0-alloc gates assume that). A heavy detail/track list opts in.</summary>
    public bool StaggerColdRealize { get; init; }

    // ── E11-L2 item lifecycle (the WinUI ItemsRepeater ElementPrepared/ElementClearing/ElementIndexChanged trio +
    //    the UseVisibleRange prefetch hook). Fired by the reconciler at realize time (cold realize edge, never on a
    //    steady transform-only scroll frame):
    //    • Prepared  — index entered the realized window (fresh mount OR a recycled node rebound to it).
    //    • Clearing  — index left the realized window (its node was recycled to another index or freed).
    //      Recycling therefore fires Clearing(old) + Prepared(new) — exactly WinUI's recycle event order.
    //    • IndexChanged — a BOUND slot (RowBind path) was rebound from one index to another (the slot's element
    //      identity persists; only its index signal moved): (oldIndex, newIndex).
    /// <summary>WinUI <c>ItemsRepeater.ElementPrepared</c> — fired with the item index when it is realized.</summary>
    public Action<int>? OnItemPrepared { get; init; }
    /// <summary>WinUI <c>ItemsRepeater.ElementClearing</c> — fired with the item index when it leaves the window.</summary>
    public Action<int>? OnItemClearing { get; init; }
    /// <summary>WinUI <c>ItemsRepeater.ElementIndexChanged</c> — a persistent bound slot rebound (oldIndex, newIndex).</summary>
    public Action<int, int>? OnItemIndexChanged { get; init; }
    /// <summary>Prefetch hook: the realized window changed → (first, last) exclusive. Use to warm images/data just
    /// outside the viewport (the plan's <c>UseVisibleRange</c> surface). Fired only when the range actually moved.</summary>
    public Action<int, int>? OnVisibleRange { get; init; }
    /// <summary>Called once when this viewport is realized into the scene, with its node handle — the escape hatch a
    /// composing control (ItemsView) uses to drive <c>ScrollState</c> (StartBringItemIntoView, sticky pinning).</summary>
    public Action<NodeHandle>? OnRealized { get; init; }
    public (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? OnScrollGeometryChanged { get; init; }

    // The viewport participates in its parent's layout like a box (size + flex + margin + a backing fill).
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    public ColorF Fill { get; init; }

    /// <summary>Scroll-edge cues (controls.md §8.3): a surface-colour gradient fade at an overflowing edge so a clipped
    /// list signals there is more below the fold. <see cref="ScrollEdgeCues.Auto"/> (default) resolves to
    /// <see cref="ScrollEdgeCuesDefaults.Default"/> (ON, fade-only); <see cref="ScrollEdgeCues.None"/> opts out.</summary>
    public ScrollEdgeCues EdgeCues { get; init; } = ScrollEdgeCues.Auto;
    /// <summary>Explicit edge fade on the virtualized viewport (premium alpha-mask cue; one offscreen RT). Null = none.</summary>
    public EdgeFadeSpec? EdgeFade { get; init; }
    /// <summary>Auto edge fade: feather only the overflowing edges, ramped with the offset. Ignored when EdgeFade is set.</summary>
    public bool AutoEdgeFade { get; init; }
    /// <summary>Never draw the conscious scrollbar for this viewport (a paged shelf navigates by its pager, not a
    /// draggable bar; the offset is still programmatically scrolled). Edge-fade cues are unaffected.</summary>
    public bool SuppressScrollBar { get; init; }

    /// <summary>Scroll-position restoration key — a STABLE per-content identity (see <see cref="ScrollEl.ScrollKey"/>).
    /// For a huge virtualized list this is what makes a revisit land at the saved row on the FIRST realized window (the
    /// seed is applied before <c>RealizeWindow</c>), with no top-then-jump. Null ⇒ no restoration.</summary>
    public string? ScrollKey { get; init; }
}
