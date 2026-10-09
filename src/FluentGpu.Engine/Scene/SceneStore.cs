using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>
/// The reconciler's only window onto the retained tree — handle-in / handle-out, POD-only.
/// (The slice implements this directly on <see cref="SceneStore"/>.)
/// </summary>
public interface ISceneBackend
{
    NodeHandle CreateNode(ushort elementTypeId);
    void FreeSubtree(NodeHandle node);
    void AppendChild(NodeHandle parent, NodeHandle child);

    ref LayoutInput Layout(NodeHandle node);
    ref NodePaint Paint(NodeHandle node);
    ref InteractionInfo Interaction(NodeHandle node);
    void SetClickHandler(NodeHandle node, Action? handler);
    Action? GetClickHandler(NodeHandle node);

    void Mark(NodeHandle node, NodeFlags flags);

    NodeHandle FirstChild(NodeHandle node);
    NodeHandle NextSibling(NodeHandle node);
    int ChildCount(NodeHandle node);
}

/// <summary>Struct-of-arrays retained RenderNode tree. One spine (gen + free-list) indexes all parallel columns.</summary>
[FluentGpu.CodeGen.EnableColdSlab] // GEN-17 (wired): generates the ColdSlab<T> the cold paint side-tables below use
public sealed partial class SceneStore : ISceneBackend
{
    internal FluentGpu.Render.SceneRecordingContext Recording { get; } = new();
    /// <summary>The slot high-water mark: the snapshot sizes its columns to it, but copies only reachable slots.</summary>
    internal int RecordingNodeCount => _high;
    // spine
    private uint[] _gen;
    // Free slots, as a binary MIN-heap of indices over [0, _freeCount): CreateNode always reuses the LOWEST free index, so
    // the live set packs toward the bottom of the slab and TrimExcessCapacity can give the all-free tail back once a big
    // page goes away. A LIFO list handed the most recently freed index straight back — after a 13k-node playlist those
    // are the HIGH ones — so the survivors stayed scattered up there and the slab kept its session high-water for good.
    private int[] _freeHeap;
    private int _freeCount;
    private int _high = 1;     // index 0 reserved = null

    // Exit-animation orphans: nodes removed from the logical tree but kept LIVE (drawing) until their exit animation
    // settles, then reclaimed. They are detached from topology (so reconcile + layout/input skip them) but indexed by
    // their former VISUAL parent so the recorder can replay them inside that parent's transform/clip/layer context.
    // Bounded by MaxOrphans (overflow instant-frees the oldest).
    // MaxAgeMs: this orphan's OWN reclaim deadline, measured on the ANIMATION clock (0 = it has none and only the
    // host's global wall-clock settle-timeout applies). EnqueuedAnimMs is its stamp on that clock. POD, stored inline
    // with the wall stamp — no per-orphan object, so the frame stays alloc-free.
    private struct OrphanEntry
    {
        public NodeHandle Node, VisualParent; public float Px, Py;
        public long EnqueuedTicks; public double EnqueuedAnimMs; public float MaxAgeMs;
    }
    private readonly List<OrphanEntry> _orphans = new();
    private readonly Dictionary<NodeHandle, List<NodeHandle>> _orphansByParent = new();
    private const int MaxOrphans = 64;

    /// <summary>The ANIMATION timebase in ms (monotonic sum of the per-frame deltas the host feeds the animator),
    /// published here by the host each frame so orphan bookkeeping can measure a deadline on the SAME clock the exit
    /// tracks integrate on. It is deliberately not the wall clock: the frame delta is clamped (≤40ms), so on a hitchy
    /// run wall time outruns animation time and a wall-measured deadline would force-reclaim HEALTHY exits mid-fade.
    /// Never reset (monotonic); 0 in a store no host drives, which simply means every orphan's own deadline is inert
    /// and the host's global wall backstop is the only guard.</summary>
    public double AnimClockMs;

    // Connected-animation overlays: standalone flying shared-element (Hero) nodes that are NOT in the logical tree but
    // draw in an UNCLIPPED top band ABOVE the drag ghost (so a card art flying into a clipped rail escapes every
    // ancestor scissor). Each also carries NodeFlags.ConnectedOverlay (excluded from the main + orphan passes).
    // Set/cleared by FluentGpu.Animation.ConnectedAnimation; bounded so a nav storm cannot unbound the band.
    private readonly List<NodeHandle> _overlays = new();
    private const int MaxOverlays = 8;

    // Clip rect for the connected-animation overlay band (window DIP). RectF.Infinite ⇒ the band escapes every scissor
    // (the historical default); set to a content-region rect by FluentGpu.Animation.ConnectedAnimation so a flying cover
    // is bounded to the page area (never sails over the sidebar / window chrome) while still clearing the inner rail
    // scissor. Reset to Infinite when no fly is in flight. Read once per frame by the SceneRecorder overlay pass.
    public RectF OverlayClip = RectF.Infinite;

    // Scope for the drop-spotlight SCRIM band (window DIP). Null = the whole scene root, i.e. the full window. An app
    // whose chrome must stay lit (Wavee excludes its title bar + player bar) sets it to its CONTENT region; the scrim
    // rect and every cutout are intersected with it. Persistent scalar CONFIG, not per-frame state: it survives scene
    // resets and is re-asserted by its owner on layout, so a drag never has to know about it. Read once per frame by
    // the SceneRecorder scrim band, and only while a drag actually has spotlight destinations.
    public RectF? SpotlightScrimClip;

    // Effective device-pixel scale (DIP→px), set by AppHost each frame from the window scale (1 in headless / on DPI
    // change re-read). Scroll transform rounding and recorder-side self-blur support geometry share it so both land on
    // the same physical grid while their logical offsets/bounds remain float.
    public float DeviceScale = 1f;

    // topology (int indices; 0 = none)
    private int[] _parent, _firstChild, _lastChild, _prevSib, _nextSib, _childCount;

    // identity + columns
    private ushort[] _elementTypeId;
    private LayoutInput[] _layout;
    private RectF[] _bounds;          // LOCAL
    private NodePaint[] _paint;
    private DynamicTextKind[] _dynamicText;
    private int _dynamicTextCount;
    private InteractionInfo[] _interaction;
    private NodeFlags[] _flags;
    private byte[] _recordDirty;
    private byte[] _recordDirtySelf;
    private byte[] _recordDirtyDescendant;
    private int[] _recordDirtyWrote;
    private int _recordDirtyWroteCount;
    // Each contribution has its OWN lifetime: a child's fresh content cannot retain a navigation's old ancestor
    // self-content, and a fresh transform cannot retain old content on the same node. Marks after publication K
    // belong to K+1; skipped publications keep exactly the contributions that have not been adopted yet.
    private struct RecordDirtyStamps
    {
        public ulong SelfTransform, SelfContent, DescendantTransform, DescendantContent;
    }
    private RecordDirtyStamps[] _recordDirtyStamp;
    private ulong _publishSeq;
    private Action?[] _click;         // managed edge payload (GC ref at the edge only)
    private Action<RectF>?[] _boundsChanged;   // post-layout arranged-bounds callback — the ELEMENT AUTHOR's Element.OnBoundsChanged
    private Action<RectF>?[] _boundsChangedHook;   // post-layout arranged-bounds callback — HOOK-owned (UseMeasuredBounds/Width). SEPARATE
                                                   // slot because the reconciler re-writes _boundsChanged on every re-render (WriteColumns),
                                                   // which would clobber a composed hook handler; FlexLayout dispatches to BOTH slots.
    private RectF[] _boundsDelivered;   // last arranged rect actually delivered to _boundsChanged: the edge baseline for
                                        // OnBoundsChanged. NOT the live Bounds (which Measure pre-writes to the hypothetical
                                        // size each pass), so the callback fires on a real ARRANGED-rect change even for an
                                        // unconstrained node whose arranged size equals its measured size (the marquee bug).
    private Action<KeyEventArgs>?[] _keyHandler;
    private Action<CharEventArgs>?[] _charHandler;   // text (character) input handler
    private Action<Point2>?[] _pointerDown;   // position-aware (local coords) press / drag handlers
    private Action<Point2>?[] _drag;
    private Action<Point2>?[] _hoverMove;     // position-aware bare-hover move (no press) — RatingControl preview, etc.
    private Action<Point2>?[] _pointerMoveWithin; // routed mouse/pen move for a node or any descendant
    private Action?[] _pointerExit;           // fired when the pointer leaves the node (hover lost) — reset hover preview
    private Action<PointerEventArgs>?[] _pointerPressed;   // press w/ click-count + modifiers (double/triple-click, drag-select)
    private Action<PointerEventArgs>?[] _pointerReleased;  // clean release-over-press target (tap commit)
    private Action<WheelEventArgs>?[] _pointerWheel;        // element-level wheel hook (pre-viewport-scroll; NumberBox)
    private Action<ContextRequestEventArgs>?[] _contextRequested;  // right-click / Menu-key / long-press context request (local coords + trigger)
    private Action<bool>?[] _focusChanged;                 // dispatcher focus moved onto (true) / off (false) this node (GotFocus/LostFocus)
    // Drag-reorder lifecycle (E5): fired by Input.DragController once a CanDrag press crosses the drag threshold.
    private Action<DragEventArgs>?[] _dragStarted;         // threshold crossed → the gesture is a drag (WinUI DragStarting)
    private Action<DragEventArgs>?[] _dragDelta;           // every pointer move while the drag is active (coords + velocity)
    private Action<DragEventArgs>?[] _dragCompleted;       // released after an active drag (the click is suppressed)
    private Action?[] _dragCanceled;                       // Escape / capture loss / window blur aborted the drag

    // Sparse side-table for scroll/virtual viewports (O(viewports), not one-per-node). Keyed by node index.
    private readonly ColdSlab<ScrollState> _scroll = new();   // GEN-17 (wired)
    // Scene-wide census for the recorder/input inactive fast path. Usually zero; supports concurrent ItemsViews.
    private int _activeVirtualDisclosureCount;
    // Per-variable-list extent tables (Fenwick); persist across frames. Keyed by viewport node index.
    private readonly Dictionary<int, ExtentTable> _extents = new();
    // Grid specs for grid-container nodes (O(grids)). Keyed by node index.
    private readonly ColdSlab<GridSpec> _grids = new();   // GEN-17 (wired)
    // Optional rich-paint side-tables (O(decorated nodes), keyed by node index): eased interaction, shadow, gradient, acrylic.
    private readonly ColdSlab<InteractionAnim> _interact = new();   // GEN-17 (wired)
    private readonly ColdSlab<ShadowSpec> _shadows = new();   // GEN-17 (wired): dense slab, not Dictionary
    private readonly ColdSlab<ArcSpec> _arcs = new();
    private readonly ColdSlab<PolylineStrokeSpec> _polylines = new();   // GEN-17 (wired)
    private readonly ColdSlab<PathSpec> _paths = new();   // GEN-17 (wired) — VisualKind.Path's geometry/fill/stroke
    private readonly ColdSlab<SeriesSpec> _series = new();   // VisualKind.Series' static half; the samples ride SceneStore.Series.cs
    private readonly ColdSlab<ClipPathSpec> _clipPaths = new();   // tier-3 stencil path clip (gpu-renderer.md §6) — implies ClipsToBounds
    private readonly ColdSlab<GradientSpec> _gradients = new();   // GEN-17 (wired)
    private readonly ColdSlab<Point2> _radialGradientCenters = new(); // bindable normalized override for radial fills
    private readonly ColdSlab<GradientSpec> _gradientTos = new();   // BoxEl.GradientTo: the blend target of the fill
    private readonly ColdSlab<float> _gradientMixes = new();        // BoxEl.GradientMix: 0..1 toward _gradientTos (absent = 0)
    private readonly ColdSlab<byte> _blends = new();              // BoxEl.Blend (low nibble) | BoxEl.LayerBlend (high nibble); absent = both SrcOver
    private readonly ColdSlab<FeedbackState> _feedback = new();   // BoxEl.Feedback + its bound warp/decay (visualizer F6)
    private readonly ColdSlab<GradientSpec> _borderBrushes = new();   // GEN-17 (wired) — gradient border stroke (elevation edge)
    // Stateful gradient variants (P4b): the recorder per-frame interpolates resting→state stops by the eased hover/press
    // progress. Sparse (O(state-gradient nodes)). Stop arrays are mount-allocated + stable — never rebuilt per frame.
    private readonly ColdSlab<GradientSpec> _hoverGradients = new();   // GEN-17 (wired)
    private readonly ColdSlab<GradientSpec> _pressedGradients = new();   // GEN-17 (wired)
    private readonly ColdSlab<GradientSpec> _hoverBorderBrushes = new();   // GEN-17 (wired)
    private readonly ColdSlab<GradientSpec> _pressedBorderBrushes = new();   // GEN-17 (wired)
    private readonly ColdSlab<AcrylicSpec> _acrylics = new();   // GEN-17 (wired)
    private readonly ColdSlab<byte> _repaintBoundaries = new();   // BoxEl.RepaintBoundary (sparse; presence = on)
    // Per-element edge fade (sparse): feather the subtree's alpha (+ optional blur) near chosen edges; read at record
    // time → PushLayer{EdgeFade}. Freed on FreeSubtree.
    private readonly ColdSlab<EdgeFadeSpec> _edgeFades = new();   // GEN-17 (wired)
    private readonly ColdSlab<ImageVisualEffects> _imageEffects = new();
    // Per-text-node measure cache (pure-function: (text,style,availW) → size); self-invalidating, freed on FreeSubtree.
    private readonly ColdSlab<TextMeasureCache> _measureCache = new();   // GEN-17 (wired) — hot: per-layout text measure
    // Implicit brush transitions (WinUI BrushTransition): sparse, O(transitioning nodes), advanced at phase 7.
    private readonly ColdSlab<BrushAnim> _brushAnims = new();   // GEN-17 (wired)
    private readonly List<int> _brushScratch = new();
    // Text-edit decoration state (sparse, O(editors)): caret/IME/focus PODs + per-node POOLED decoration-rect slots
    // (grow-only RectF[] reused across frames — a selection drag updates at pointer rate with ZERO steady alloc).
    private readonly Dictionary<int, TextEditState> _textEdits = new();
    private readonly Dictionary<int, (RectF[]? Arr, int Count)> _textEditSelRects = new();
    private readonly Dictionary<int, (RectF[]? Arr, int Count)> _textEditUnderlineRects = new();
    // Span-text side-tables (sparse, O(span paragraphs) — rtb-01/rtb-02/api-04):
    // the element's TextSpan array (hyperlink actions; the POD shaping overlay lives in SpanRunTable.Shared keyed by
    // TextStyle.SpanRunId); the dispatcher-owned read-only selection range; the per-control selection highlight color.
    // (Arr, Count) grow-only pooled slot — same discipline as _textEditSelRects above: SetSpanText COPIES the
    // caller's spans (element-static array OR a SpanBuffer.Current view over a REUSED backing array, P2) into a
    // scene-owned array so a template that refills its SpanBuffer on the next recycle can never retroactively
    // mutate text the scene already committed this frame (rtb-01/P2 "scene owns the copy").
    private readonly Dictionary<int, (TextSpan[]? Arr, int Count)> _spanText = new();
    // Sparse index-resolved hyperlink handler (P2 "bound spans with index-resolved clicks", SpanTextEl.OnSpanClick):
    // mount-static, written unconditionally each WriteColumns pass (a plain reference set on an existing dictionary
    // key allocates nothing) — most nodes never populate this table.
    private readonly Dictionary<int, Action<int>> _spanClickHandlers = new();
    private readonly Dictionary<int, (int Start, int End)> _textSelection = new();
    private readonly ColdSlab<ColorF> _selectionHighlight = new();   // GEN-17 (wired)
    private readonly ColdSlab<GlyphWipe> _glyphWipes = new();        // sparse per-node glyph wipe (general text-reveal; lyrics karaoke)
    // E5-L2 drag-drop side-tables (sparse, O(sources)/O(targets), keyed by node index): the reconciler writes them
    // from BoxEl.Draggable / BoxEl.DropTarget; Input.DragDropContext reads them at promotion / per pointer move.
    private readonly Dictionary<int, DragSource> _dragSources = new();
    private readonly Dictionary<int, DropTargetSpec> _dropTargets = new();
    // Compatible spotlight destinations for the LIVE drag, in registry order. A List (not a HashSet): the scrim band
    // ITERATES it once per frame to cut one rounded window per root, and the set is tiny (a handful of opt-in targets),
    // so the linear Contains the membership tests use is cheaper than a hash lookup and the order is stable.
    private readonly List<int> _dropSpotlightRoots = new(8);
    private int _dropTargetsVersion;
    private bool _dropSpotlightActive;
    private NodeHandle _dropSpotlightOver;

    // UseGesture (input-a11y.md §13) declarations: sparse (only nodes that declared a gesture hook have an entry), the
    // _textEdits/_brushAnims side-table pattern — no per-node array, no resize cost. FluentGpu.Hooks WRITES the
    // subscription on mount; FluentGpu.Input READS it when the gesture arena resolves a winner on the node (both
    // reference Scene; neither references the other — this column is the seam). The handler delegates are the only GC
    // edge (a freshly-captured user closure at mount, like every HandlerTable column — foundations: GC at the edge OK).
    private readonly Dictionary<int, GestureSubscription> _gestureSubs = new();

    public NodeHandle Root { get; set; }

    /// <summary>The node currently lifted by an active item-drag (E5 ghost) — set/cleared by
    /// <c>Input.DragController</c> at promotion/restore (the node also carries <see cref="NodeFlags.DragGhost"/>).
    /// The recorder EXCLUDES it from the clipped main pass and re-walks its subtree in an UNCLIPPED top band emitted
    /// last, so the lifted visual escapes every ancestor scissor and paints above overlays. Null = no drag.</summary>
    public NodeHandle DragGhost { get; set; }

    /// <summary>Optional OPAQUE plate filled beneath the <see cref="DragGhost"/> subtree (inside its opacity group,
    /// with the ghost node's own corner radii) — the promotion-time <c>DragVisualStyle.Backplate</c>. Set/cleared by
    /// <c>Input.DragController</c> exactly like <see cref="DragGhost"/>; null = no plate.</summary>
    public ColorF? DragGhostBackplate { get; set; }

    /// <summary>
    /// A destination's override of the <see cref="DragLift.Stationary"/> SOURCE-row dim, for the one frame shape where
    /// two owners disagree about the same node's opacity: a same-list insertion performs VIRTUAL REMOVAL (the dragged
    /// rows go to 0 — they are "in the chip"), while <c>DragController.ReassertPresented</c> re-writes the style's
    /// 0.4 dim after every mid-drag reconcile. The re-assert runs AFTER the animation compose in the frame, so without
    /// this the press-source row flickers back to 0.4 on every reconcile frame while its siblings stay hidden.
    /// <para>Set by the insertion while it hides sources, cleared when it stops (and at session end). Null = the
    /// source's own <c>DragVisualStyle.Opacity</c> stands, which is the unchanged default for every other drag.</para>
    /// </summary>
    public float? DragSourceOpacityOverride { get; set; }

    /// <summary>The drag OVERLAY root (a mounted <c>DragPreviewLayer</c> registers its container here): excluded from
    /// the clipped main pass and re-walked UNCLIPPED in the topmost band — above the main pass, above the
    /// <see cref="DragGhost"/> band and above the connected-animation overlays — so a drag chip can never be clipped by
    /// an ancestor scissor nor overdrawn. Null = no layer mounted.</summary>
    public NodeHandle DragOverlay { get; set; }

    /// <summary>Optional interner for text-id lifetime accounting: when set, freeing a node (or rewriting its dynamic
    /// text) releases its <c>paint.Text</c> / <c>TextStyle.Family</c> refs so streamed virtual-list text is reclaimed
    /// instead of accumulating for the process lifetime. Wired by the reconciler at composition.</summary>
    public StringTable? Strings { get; set; }

    /// <summary>Optional slot-free notification (node INDEX): invoked by <see cref="FreeSubtree"/> as a node's slot is
    /// reclaimed, so subsystems that key per-node state by INDEX (rather than gen-checked handle) — the AnimEngine
    /// layout-transition side-table, the ScrollIntegrator conscious-bar timers — can drop the dormant row symmetrically.
    /// Without it a freed slot's stale spec/state would be inherited by the NEXT node reusing that index. Wired by the
    /// host; null on backends that don't use the index-keyed side-tables.</summary>
    public Action<int>? OnFreeIndex { get; set; }

    /// <summary>Host hook: a viewport node's scroll row was just created (<see cref="ScrollRef"/>'s first touch). The
    /// host binds a <c>ScrollHandle</c> to it (scroll rework §9). Null until wired (headless suites that never scroll).</summary>
    public Action<int>? OnScrollNodeAdded { get; set; }

    /// <summary>Host hook: a viewport node's scroll row is being freed (<see cref="FreeSubtree"/>). The host unbinds and
    /// forgets its <c>ScrollHandle</c>.</summary>
    public Action<int>? OnScrollNodeRemoved { get; set; }

    /// <summary>The window's plan table (scroll rework §2), installed by the host so layout can shift a viewport's
    /// plan frame in the same call it commits a measured-extent correction (<c>Virtualizer.ApplyMeasured</c>). Null
    /// until wired (headless suites that never scroll).</summary>
    public FluentGpu.Scroll.Runtime.PlanSlots? PlanSlots { get; set; }

    /// <summary>Host hook: the <c>ScrollHandle</c> bound to a live viewport node (controls reach their viewport's handle
    /// through the scene they already hold). Null when unwired or when the node is not a live scroller.</summary>
    public Func<NodeHandle, FluentGpu.Scroll.Runtime.ScrollHandle?>? ResolveScrollHandle { get; set; }

    /// <summary>The scroll handle bound to <paramref name="viewport"/>, or null.</summary>
    public FluentGpu.Scroll.Runtime.ScrollHandle? ScrollHandleFor(NodeHandle viewport) => ResolveScrollHandle?.Invoke(viewport);

    /// <summary>Publisher hook (scroll rework §5): fills a publication's <see cref="FluentGpu.Scroll.Runtime.ScrollCoverageTable"/>
    /// at capture from the host's UI-side coverage. Null ⇒ the table stays empty (no viewport is posed render-side).</summary>
    public Action<FluentGpu.Scroll.Runtime.ScrollCoverageTable>? CaptureScrollCoverage { get; set; }

    /// <summary>Per-viewport scrollbar "conscious" chrome side-table — FadeT/ExpandT/PointerOver/PointerOverScrollbar/
    /// IdleMs, kept out of <see cref="ScrollState"/> so motion and chrome never share a writer. Always present.</summary>
    public FluentGpu.Scroll.Runtime.ScrollBarChromeTable ScrollChrome { get; }

    // ── app-authored scroll handles (Element.Handle) — the reconciler records the authored handle per viewport node;
    //    the host binds it (or mints an internal one) when OnScrollNodeAdded fires / at its frame step. UI-thread only.
    private readonly Dictionary<int, FluentGpu.Scroll.Runtime.ScrollHandle> _authoredHandles = new();
    public void SetAuthoredScrollHandle(NodeHandle node, FluentGpu.Scroll.Runtime.ScrollHandle? handle)
    {
        int idx = (int)node.Raw.Index;
        if (handle is null) _authoredHandles.Remove(idx); else _authoredHandles[idx] = handle;
    }
    public bool TryGetAuthoredScrollHandle(int nodeIndex, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FluentGpu.Scroll.Runtime.ScrollHandle? handle)
        => _authoredHandles.TryGetValue(nodeIndex, out handle);

    public SceneStore(int initialCapacity = 64)
    {
        ScrollChrome = new FluentGpu.Scroll.Runtime.ScrollBarChromeTable();
        int capacity = initialCapacity;
        if (capacity < 4) capacity = 4;
        _gen = new uint[capacity];
        _freeHeap = new int[capacity];
        _parent = new int[capacity];
        _firstChild = new int[capacity];
        _lastChild = new int[capacity];
        _prevSib = new int[capacity];
        _nextSib = new int[capacity];
        _childCount = new int[capacity];
        _elementTypeId = new ushort[capacity];
        _layout = new LayoutInput[capacity];
        _bounds = new RectF[capacity];
        _paint = new NodePaint[capacity];
        _dynamicText = new DynamicTextKind[capacity];
        _interaction = new InteractionInfo[capacity];
        _flags = new NodeFlags[capacity];
        _aux = new byte[capacity];
        _subtreeVersion = new uint[capacity];
        _recordDirty = new byte[capacity];
        _recordDirtySelf = new byte[capacity];
        _recordDirtyDescendant = new byte[capacity];
        _recordDirtyWrote = new int[capacity];
        _recordDirtyStamp = new RecordDirtyStamps[capacity];
        _captureStamp = new ulong[capacity];
        _createdStamp = new ulong[capacity];
        _captureWrote = new int[capacity];
        _inCaptureList = new bool[capacity];
        _click = new Action?[capacity];
        _boundsChanged = new Action<RectF>?[capacity];
        _boundsChangedHook = new Action<RectF>?[capacity];
        _boundsDelivered = new RectF[capacity];
        _keyHandler = new Action<KeyEventArgs>?[capacity];
        _charHandler = new Action<CharEventArgs>?[capacity];
        _pointerDown = new Action<Point2>?[capacity];
        _drag = new Action<Point2>?[capacity];
        _hoverMove = new Action<Point2>?[capacity];
        _pointerMoveWithin = new Action<Point2>?[capacity];
        _pointerExit = new Action?[capacity];
        _pointerPressed = new Action<PointerEventArgs>?[capacity];
        _pointerReleased = new Action<PointerEventArgs>?[capacity];
        _pointerWheel = new Action<WheelEventArgs>?[capacity];
        _contextRequested = new Action<ContextRequestEventArgs>?[capacity];
        _focusChanged = new Action<bool>?[capacity];
        _dragStarted = new Action<DragEventArgs>?[capacity];
        _dragDelta = new Action<DragEventArgs>?[capacity];
        _dragCompleted = new Action<DragEventArgs>?[capacity];
        _dragCanceled = new Action?[capacity];
    }

    public int LiveCount { get; private set; }

    /// <summary>SoA column length (the high-water spine allocation) — O(1) census of the slab size, not the live count.</summary>
    public int Capacity => _gen.Length;
    /// <summary>A floor <see cref="TrimExcessCapacity"/> (and any other future column-shrinking path) will never cut
    /// below, on top of its own built-in <c>FloorCap</c> (256): a host that knows its steady-state scene/snapshot size
    /// (Wavee passes ~8192) sets this once so the idle trim stops re-growing the slab back onto the LOH every cold
    /// navigation. 0 (the default) keeps today's trim behaviour exactly — the built-in floor alone decides.</summary>
    public int CapacityFloor { get; set; }
    /// <summary>UI-owned allocation/free revision for one-shot cold tail reclamation; paint/motion do not change it.</summary>
    internal ulong CapacityRevision { get; private set; }
    /// <summary>Live scroll/virtual-viewport rows — O(1) census of the <c>_scroll</c> side-table.</summary>
    public int ScrollStateCount => _scroll.Count;
    /// <summary>In-flight implicit brush transitions — O(1) census of the <c>_brushAnims</c> side-table.</summary>
    public int BrushAnimCount => _brushAnims.Count;

    public bool IsLive(NodeHandle h)
        => h.Raw.Index > 0 && h.Raw.Index < (uint)_high && _gen[h.Raw.Index] == h.Raw.Gen;

    public NodeHandle CreateNode(ushort elementTypeId)
    {
        int idx;
        if (_freeCount != 0) idx = PopLowestFree();
        else { if (_high >= _gen.Length) Grow(); idx = _high++; }

        if (_gen[idx] == 0) _gen[idx] = 1;
        // reset columns
        _parent[idx] = _firstChild[idx] = _lastChild[idx] = _prevSib[idx] = _nextSib[idx] = _childCount[idx] = 0;
        _elementTypeId[idx] = elementTypeId;
        _layout[idx] = LayoutInput.Default;
        _bounds[idx] = default;
        _paint[idx] = NodePaint.Default;
        ClearDynamicText(idx);
        _interaction[idx] = default;
        _flags[idx] = NodeFlags.Visible | NodeFlags.HitTestVisible | NodeFlags.NewThisFrame;
        NoteCaptureCreated(idx);   // P8: a (re)allocated slot must be copied wholesale, never merged onto its predecessor
        _aux[idx] = 0;
        _recordDirty[idx] = 0;
        _recordDirtySelf[idx] = 0;
        _recordDirtyDescendant[idx] = 0;
        MarkRecordDirty(idx);
        _click[idx] = null;
        _boundsChanged[idx] = null;
        _boundsChangedHook[idx] = null;
        _boundsDelivered[idx] = default;
        _keyHandler[idx] = null;
        _charHandler[idx] = null;
        _pointerDown[idx] = null;
        _drag[idx] = null;
        _hoverMove[idx] = null;
        _pointerMoveWithin[idx] = null;
        _pointerExit[idx] = null;
        _pointerPressed[idx] = null;
        _pointerReleased[idx] = null;
        _pointerWheel[idx] = null;
        _contextRequested[idx] = null;
        _focusChanged[idx] = null;
        _dragStarted[idx] = null;
        _dragDelta[idx] = null;
        _dragCompleted[idx] = null;
        _dragCanceled[idx] = null;
        LiveCount++;
        unchecked { CapacityRevision++; }
        return new NodeHandle(new Handle((uint)idx, _gen[idx]));
    }

    public void FreeSubtree(NodeHandle node)
    {
        if (!IsLive(node)) return;
        // Repaint damage (§13.1): the vacated band must be recorded BEFORE the subtree is torn down — after the free
        // there is no handle left to ask. Only the subtree ROOT is captured; its extent (and the span table's stored
        // SubtreeBounds, which the recorder prefers) already covers the descendants going with it.
        CaptureRemovalExtent(node);
        FreeSubtreeCore(node);
    }

    private void FreeSubtreeCore(NodeHandle node)
    {
        if (!IsLive(node)) return;
        // Logically-detached exiting children are absent from the topology walk below. Retire them with a hard-freed
        // visual parent instead of letting them escape into a root-level render band.
        ReclaimOrphanChildren(node);
        int idx = (int)node.Raw.Index;
        // free children first
        int c = _firstChild[idx];
        while (c != 0)
        {
            int next = _nextSib[c];
            FreeSubtreeCore(new NodeHandle(new Handle((uint)c, _gen[c])));
            c = next;
        }
        DetachFromParent(idx);
        if (Strings is { } st)
        {
            st.Release(_paint[idx].Text);
            st.Release(_layout[idx].TextStyle.FontFamily);
        }
        ReleaseSpanRun(_layout[idx].TextStyle.SpanRunId);   // span-run + per-span family lifetime (rtb-01)
        _click[idx] = null;
        _boundsChanged[idx] = null;
        _boundsChangedHook[idx] = null;
        _boundsDelivered[idx] = default;
        _keyHandler[idx] = null;
        _charHandler[idx] = null;
        _pointerDown[idx] = null;
        _drag[idx] = null;
        _hoverMove[idx] = null;
        _pointerMoveWithin[idx] = null;
        _pointerExit[idx] = null;
        _pointerPressed[idx] = null;
        _pointerReleased[idx] = null;
        _pointerWheel[idx] = null;
        _contextRequested[idx] = null;
        _focusChanged[idx] = null;
        _dragStarted[idx] = null;
        _dragDelta[idx] = null;
        _dragCompleted[idx] = null;
        _dragCanceled[idx] = null;
        ClearDynamicText(idx);
        NodeFlags flags = _flags[idx];
        if ((flags & NodeFlags.Scrollable) != 0)
        {
            if (_scroll.TryGet(idx, out var scroll) && float.IsFinite(scroll.DisclosureT))
            {
                Debug.Assert(_activeVirtualDisclosureCount > 0);
                if (_activeVirtualDisclosureCount > 0) _activeVirtualDisclosureCount--;
            }
            // Tell the host first (it unbinds the viewport's ScrollHandle while the row still exists), then drop the
            // index-keyed side-tables: this node's ScrollState row, its chrome row and its authored handle.
            OnScrollNodeRemoved?.Invoke(idx);
            ScrollChrome.Clear(idx);
            _scroll.Remove(idx);
            _authoredHandles.Remove(idx);
        }
        _grids.Remove(idx);
        // A viewport's measured row extents are keyed by its index like every side-table here: the next node to reuse the
        // slot must start from its own estimates, not the freed list's row heights (and the table must not outlive it).
        if (_extents.Count != 0) _extents.Remove(idx);
        if (_hitPassThrough.Count != 0) _hitPassThrough.Remove(idx);
        if (_wheelTargets.Count != 0) _wheelTargets.Remove(idx);
        if (_wheelOccludes.Count != 0) _wheelOccludes.Remove(idx);   // only a BoxEl rewrites it: a reused slot must not stay opaque
        // Scroll-linked effect rows are index-keyed: a freed slot must not hand its effects (or its engaged signals) to
        // the next node that reuses the index.
        if (_scrollEffects.Count != 0) _scrollEffects.Remove(idx);
        if (_scrollEffectEngaged.Count != 0) _scrollEffectEngaged.Remove(idx);
        if (_scrollScopes.Count != 0) _scrollScopes.Remove(idx);
        if (_debugKeys is { Count: > 0 } dk) dk.Remove(idx);   // a reused slot must not inherit a freed node's key (evidence names)
        if ((flags & NodeFlags.InteractionAnim) != 0) _interact.Remove(idx);
        if ((flags & NodeFlags.SparsePaint) != 0)
        {
            _shadows.Remove(idx);
            _arcs.Remove(idx);
            _polylines.Remove(idx);
            _paths.Remove(idx);
            _series.Remove(idx);
            _clipPaths.Remove(idx);
            _gradients.Remove(idx);
            _radialGradientCenters.Remove(idx);
            _gradientTos.Remove(idx);
            _gradientMixes.Remove(idx);
            _blends.Remove(idx);
            _feedback.Remove(idx);
            _borderBrushes.Remove(idx);
            _hoverGradients.Remove(idx);
            _pressedGradients.Remove(idx);
            _hoverBorderBrushes.Remove(idx);
            _pressedBorderBrushes.Remove(idx);
            _acrylics.Remove(idx);
            _repaintBoundaries.Remove(idx);
            _edgeFades.Remove(idx);
            _imageEffects.Remove(idx);
            _brushAnims.Remove(idx);
        }
        if (_paint[idx].VisualKind == VisualKind.Text)
        {
            _measureCache.Remove(idx);
            if (_textEdits.Count != 0) _textEdits.Remove(idx);
            if (_textEditSelRects.Count != 0) _textEditSelRects.Remove(idx);
            if (_textEditUnderlineRects.Count != 0) _textEditUnderlineRects.Remove(idx);
            if (_spanText.Count != 0) _spanText.Remove(idx);
            if (_spanClickHandlers.Count != 0) _spanClickHandlers.Remove(idx);
            if (_textSelection.Count != 0) _textSelection.Remove(idx);
            _selectionHighlight.Remove(idx);
            _glyphWipes.Remove(idx);
        }
        if (_paint[idx].VisualKind == VisualKind.ListRow && (_rowCells.Count != 0 || _rowCellClickHandlers.Count != 0))
            ReleaseRowCells(idx);
        if (_paint[idx].VisualKind == VisualKind.Series && _seriesSamples.Count != 0) _seriesSamples.Remove(idx);
        if (_paint[idx].VisualKind == VisualKind.Sprites) ReleaseSprites(idx);
        if (_dragSources.Count != 0) _dragSources.Remove(idx);
        if (_dropTargets.Count != 0 && _dropTargets.Remove(idx)) _dropTargetsVersion++;
        if (_dropSpotlightRoots.Count != 0) _dropSpotlightRoots.Remove(idx);
        if (_dropSpotlightOver == node) _dropSpotlightOver = NodeHandle.Null;
        if (_gestureSubs.Count != 0) _gestureSubs.Remove(idx);   // drop the node's UseGesture declaration with it (handler closures released)
        if (DragGhost == node) { DragGhost = NodeHandle.Null; DragGhostBackplate = null; }   // a freed ghost must not linger in the recorder's top band
        if (DragOverlay == node) DragOverlay = NodeHandle.Null;   // …nor a freed preview-layer root in the overlay band
        if ((flags & NodeFlags.ConnectedOverlay) != 0) RemoveOverlay(node);   // a freed overlay must not linger in the band
        OnFreeIndex?.Invoke(idx);   // symmetric teardown of INDEX-keyed external side-tables (AnimEngine transitions / scroll chrome rows)
        _recordDirty[idx] = 0;
        _recordDirtySelf[idx] = 0;
        _recordDirtyDescendant[idx] = 0;
        _gen[idx]++;
        if (_gen[idx] == 0) _gen[idx] = 1;
        PushFree(idx);
        LiveCount--;
        unchecked { CapacityRevision++; }
    }

    // ── the free heap (see _freeHeap) ──
    private void PushFree(int idx)
    {
        int i = _freeCount++;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (_freeHeap[parent] <= idx) break;
            _freeHeap[i] = _freeHeap[parent];
            i = parent;
        }
        _freeHeap[i] = idx;
    }

    private int PopLowestFree()
    {
        int lowest = _freeHeap[0];
        int last = _freeHeap[--_freeCount];
        int i = 0;
        while (true)
        {
            int child = 2 * i + 1;
            if (child >= _freeCount) break;
            if (child + 1 < _freeCount && _freeHeap[child + 1] < _freeHeap[child]) child++;
            if (_freeHeap[child] >= last) break;
            _freeHeap[i] = _freeHeap[child];
            i = child;
        }
        if (_freeCount > 0) _freeHeap[i] = last;
        return lowest;
    }

    // ── Repaint-damage removal ledger (gpu-renderer.md §13.1) ───────────────────────────────────────────────────────
    // An unmounted node stops covering the band it presented at last frame, and — unlike a MOVE — nothing re-touches
    // that band, so a region-aware repaint would freeze last frame's pixels there. The free path is the only place the
    // extent still exists, so it is snapshotted here and drained by SceneRecorder.Record (its single consumer).
    // Fixed capacity, no growth: a teardown larger than the ledger sets the overflow flag and the recorder forces a
    // full repaint instead of silently under-damaging.
    private const int RemovalLedgerCap = 64;

    /// <summary>One unmounted node's identity + its model extent at free time (see <see cref="PendingRemovalExtents"/>).
    /// The recorder prefers the span table's stored SubtreeBounds for this (index, gen) — which folds in every halo —
    /// and falls back to <paramref name="ModelRect"/> when the node presented under no stored span. <paramref name="Hidden"/>:
    /// at free time the node sat in a collapsed (<see cref="InCollapsedSubtree"/>) or KeepAlive-parked subtree, which the
    /// recorder never reaches, so whatever it once presented was vacated when the subtree hid.</summary>
    public readonly record struct RemovedNodeExtent(int NodeIndex, uint Gen, RectF ModelRect, bool Hidden);

    private readonly RemovedNodeExtent[] _removedExtents = new RemovedNodeExtent[RemovalLedgerCap];
    private readonly ulong[] _removedStamp = new ulong[RemovalLedgerCap];
    private int _removedCount;
    private bool _removedOverflow;
    private ulong _removedOverflowStamp;

    /// <summary>Nodes unmounted since the last record, with the extent they last occupied.</summary>
    public ReadOnlySpan<RemovedNodeExtent> PendingRemovalExtents => _removedExtents.AsSpan(0, _removedCount);

    /// <summary>More removals happened than the ledger holds ⇒ the repaint set cannot be trusted; force a full repaint.</summary>
    public bool PendingRemovalOverflow => _removedOverflow;

    /// <summary>Drop the ledger — called by <c>SceneRecorder.Record</c> once it has folded the extents into the frame's
    /// repaint region.</summary>
    public void ClearPendingRemovals()
    {
        _removedCount = 0;
        _removedOverflow = false;
    }

    /// <summary>Drop only the removals a consumed publication carried (stamp ≤ <paramref name="consumedSeq"/>);
    /// the rest stay for the next capture. The overflow flag is stamped like an entry.</summary>
    public void ClearPendingRemovals(ulong consumedSeq)
    {
        int kept = 0;
        for (int i = 0; i < _removedCount; i++)
        {
            if (_removedStamp[i] <= consumedSeq) continue;
            _removedExtents[kept] = _removedExtents[i];
            _removedStamp[kept++] = _removedStamp[i];
        }
        _removedCount = kept;
        if (_removedOverflow && _removedOverflowStamp <= consumedSeq) _removedOverflow = false;
    }

    private void CaptureRemovalExtent(NodeHandle node)
    {
        _mutationStamp = _publishSeq + 1;   // a removal is a captured change (the snapshot copies the ledger)
        if (_removedCount >= RemovalLedgerCap) { _removedOverflow = true; _removedOverflowStamp = _publishSeq + 1; return; }
        RectF abs = AbsoluteRect(node);
        ref NodePaint p = ref _paint[(int)node.Raw.Index];
        float pw = float.IsNaN(p.PresentedW) ? abs.W : p.PresentedW;   // presented (Reveal) extent may exceed the model box
        float ph = float.IsNaN(p.PresentedH) ? abs.H : p.PresentedH;
        // A degenerate rect is recorded too (not skipped): the recorder needs to see the entry so it can tell "this node
        // never presented anything" from "it presented and we lost the extent" (⇒ MissingRemovalExtent). A node mounted
        // under a collapsed or parked ancestor is never laid out or walked: no span, a 0x0 box, and nothing to vacate.
        bool hidden = (_flags[(int)node.Raw.Index] & NodeFlags.Parked) != 0 || InCollapsedSubtree(node);
        _removedStamp[_removedCount] = _publishSeq + 1; _removedExtents[_removedCount++] = new RemovedNodeExtent((int)node.Raw.Index, node.Raw.Gen, new RectF(abs.X, abs.Y, pw, ph), hidden);
    }

    public void AppendChild(NodeHandle parent, NodeHandle child)
    {
        Debug.Assert(IsLive(parent) && IsLive(child));
        int p = (int)parent.Raw.Index, c = (int)child.Raw.Index;
        _parent[c] = p;
        _prevSib[c] = _lastChild[p];
        _nextSib[c] = 0;
        if (_lastChild[p] != 0) NoteCaptureChanged(_lastChild[p]);   // P8: its captured NextSibling changes below
        if (_lastChild[p] != 0) _nextSib[_lastChild[p]] = c;
        else _firstChild[p] = c;
        _lastChild[p] = c;
        _childCount[p]++;
        MarkRecordDirty(c, RecordDirtyLayout);
    }

    /// <summary>Attach <paramref name="child"/> as the first child without freeing/recreating it. Used by reverse
    /// virtual-window rotation so logical-item overlap keeps its retained subtree and only entering rows rebind.</summary>
    internal void PrependChild(NodeHandle parent, NodeHandle child)
    {
        Debug.Assert(IsLive(parent) && IsLive(child));
        int p = (int)parent.Raw.Index, c = (int)child.Raw.Index;
        _parent[c] = p;
        _prevSib[c] = 0;
        _nextSib[c] = _firstChild[p];
        if (_firstChild[p] != 0) _prevSib[_firstChild[p]] = c;
        else _lastChild[p] = c;
        _firstChild[p] = c;
        _childCount[p]++;
        MarkRecordDirty(c, RecordDirtyLayout);
    }

    /// <summary>Unlink a child from its parent without freeing it (used by keyed reconcile to reorder).</summary>
    public void Detach(NodeHandle child)
    {
        if (IsLive(child)) DetachFromParent((int)child.Raw.Index);
    }

    private void DetachFromParent(int c)
    {
        int p = _parent[c];
        if (p == 0) return;
        MarkRecordDirty(c, RecordDirtyLayout);
        if (_prevSib[c] != 0) NoteCaptureChanged(_prevSib[c]);   // P8: its captured NextSibling changes below
        if (_prevSib[c] != 0) _nextSib[_prevSib[c]] = _nextSib[c]; else _firstChild[p] = _nextSib[c];
        if (_nextSib[c] != 0) _prevSib[_nextSib[c]] = _prevSib[c]; else _lastChild[p] = _prevSib[c];
        _childCount[p]--;
        _parent[c] = _prevSib[c] = _nextSib[c] = 0;
    }

    // ── exit-animation orphans ────────────────────────────────────────────────────────────────────
    /// <summary>Remove a node from the logical tree but keep it LIVE and drawing: detach from its parent (so reconcile +
    /// layout no longer walk it as a child), retain its former visual parent, and flag it Exiting. The recorder replays it
    /// inside that parent's active transform/clip/layer/popup context; <see cref="ReclaimOrphan"/> frees it on settle. The
    /// frozen origin remains only as a defensive fallback for an already-rootless orphan.
    /// <para>A SizeMode.Reflow exit still contributes its animating main-axis size to the visual parent's Measure so a
    /// measured virtual row eases closed instead of snapping, and the reflow compose writes the orphan's own Bounds so
    /// ClipsToBounds follows the Size track.</para>
    /// <paramref name="maxAgeMs"/> is this orphan's OWN hard deadline on the <see cref="AnimClockMs"/> timebase (see
    /// <see cref="OrphanMaxAgeMs"/>): the host force-reclaims it past that age even while tracks remain. 0 ⇒ only the
    /// host's global settle-timeout applies.</summary>
    public void Orphan(NodeHandle node, float maxAgeMs = 0f)
    {
        if (!IsLive(node)) return;
        if (_orphans.Count >= MaxOrphans) DropOrphanAt(0);   // budget: instant-free oldest
        RectF abs = AbsoluteRect(node);
        int idx = (int)node.Raw.Index;
        NodeHandle visualParent = Parent(node);
        float px = abs.X - _bounds[idx].X, py = abs.Y - _bounds[idx].Y;   // frozen parent-world origin
        DetachFromParent(idx);
        _flags[idx] |= NodeFlags.Exiting;
        _orphans.Add(new OrphanEntry
        {
            Node = node, VisualParent = visualParent, Px = px, Py = py,
            EnqueuedTicks = Stopwatch.GetTimestamp(), EnqueuedAnimMs = AnimClockMs,
            MaxAgeMs = maxAgeMs > 0f ? maxAgeMs : 0f,
        });
        if (!visualParent.IsNull)
        {
            if (!_orphansByParent.TryGetValue(visualParent, out var children))
                _orphansByParent.Add(visualParent, children = new List<NodeHandle>(2));
            children.Add(node);
        }
    }

    /// <summary>Free a settled exit orphan (the deferred <see cref="FreeSubtree"/> — gen bump → handle dead).
    /// Marks the former visual parent LayoutDirty so a measured virtual row re-solves WITHOUT the orphan and writes
    /// the closed height back through SetMeasured — otherwise the last orphan-inclusive extent can stick in the
    /// ExtentTable after the Size track has already been torn down.</summary>
    public void ReclaimOrphan(NodeHandle node)
    {
        NodeHandle visualParent = default;
        for (int i = _orphans.Count - 1; i >= 0; i--)
            if (_orphans[i].Node == node)
            {
                var entry = _orphans[i];
                visualParent = entry.VisualParent;
                UnindexOrphan(in entry);
                _orphans.RemoveAt(i);
                break;
            }
        FreeSubtree(node);
        if (!visualParent.IsNull && IsLive(visualParent))
            Mark(visualParent, NodeFlags.LayoutDirty);
    }

    /// <summary>Exiting children formerly owned by <paramref name="visualParent"/>, in removal order. Internal recorder
    /// seam: the list is stable during a record pass because reconcile/reclaim run outside phase 8.</summary>
    internal List<NodeHandle>? OrphanChildrenOf(NodeHandle visualParent)
        => _orphansByParent.TryGetValue(visualParent, out var children) ? children : null;

    private void UnindexOrphan(in OrphanEntry entry)
    {
        if (entry.VisualParent.IsNull || !_orphansByParent.TryGetValue(entry.VisualParent, out var children)) return;
        children.Remove(entry.Node);
        if (children.Count == 0) _orphansByParent.Remove(entry.VisualParent);
    }

    private void DropOrphanAt(int index)
    {
        var entry = _orphans[index];
        UnindexOrphan(in entry);
        _orphans.RemoveAt(index);
        FreeSubtree(entry.Node);
        if (!entry.VisualParent.IsNull && IsLive(entry.VisualParent))
            Mark(entry.VisualParent, NodeFlags.LayoutDirty);
    }

    private void ReclaimOrphanChildren(NodeHandle visualParent)
    {
        if (!_orphansByParent.Remove(visualParent, out var children)) return;
        // Remove the global lifecycle rows before freeing. FreeSubtree(child) recursively handles exits whose visual
        // parent is itself this exiting child.
        for (int i = children.Count - 1; i >= 0; i--)
        {
            NodeHandle child = children[i];
            for (int j = _orphans.Count - 1; j >= 0; j--)
                if (_orphans[j].Node == child) { _orphans.RemoveAt(j); break; }
            FreeSubtree(child);
        }
    }

    public bool IsOrphan(NodeHandle node)
    {
        for (int i = 0; i < _orphans.Count; i++) if (_orphans[i].Node == node) return true;
        return false;
    }

    /// <summary>The former visual parent an exit orphan still paints under, or false when <paramref name="node"/> is
    /// not an orphan. Used by the reflow compose to dirty that parent (the topological <c>Parent</c> is null after
    /// <see cref="Orphan"/>) so a measured virtual row re-solves against the orphan's animating height.</summary>
    public bool TryGetOrphanVisualParent(NodeHandle node, out NodeHandle parent)
    {
        for (int i = 0; i < _orphans.Count; i++)
        {
            if (_orphans[i].Node == node) { parent = _orphans[i].VisualParent; return true; }
        }
        parent = default;
        return false;
    }

    /// <summary>Count of exit orphans currently animating out (the host keeps painting while &gt; 0).</summary>
    public int OrphanCount => _orphans.Count;

    /// <summary>The i-th orphan node + its frozen parent-world origin (used only by the rootless fallback draw pass).</summary>
    public NodeHandle OrphanAt(int i, out float px, out float py)
    {
        var e = _orphans[i]; px = e.Px; py = e.Py; return e.Node;
    }

    /// <summary>The former visual parent that owns the orphan's render context. Null is the defensive rootless fallback.</summary>
    internal NodeHandle OrphanVisualParentAt(int i) => _orphans[i].VisualParent;

    /// <summary><see cref="Stopwatch.GetTimestamp"/> when the i-th orphan was enqueued — the host's settle-timeout
    /// backstop force-reclaims an orphan whose exit track wedged (a never-settling animation) so it can't pin the wake
    /// loop forever.</summary>
    public long OrphanEnqueuedTicks(int i) => _orphans[i].EnqueuedTicks;

    /// <summary>The i-th orphan's own hard reclaim deadline in ms (what <see cref="Orphan"/> was given), or 0 when it has
    /// none and only the host's global settle-timeout applies. A caller that knows its exit's nominal duration (the
    /// reconciler, from the <c>LayoutTransition</c>) sets this so a WEDGED exit is dropped in ~one exit duration instead
    /// of painting a half-faded layer over live content until the global backstop expires.</summary>
    public float OrphanMaxAgeMs(int i) => _orphans[i].MaxAgeMs;

    /// <summary>How much ANIMATION time (<see cref="AnimClockMs"/>) has passed since the i-th orphan was enqueued — the
    /// age its <see cref="OrphanMaxAgeMs"/> deadline is measured against. Same timebase as the exit tracks, so a healthy
    /// exit always settles inside its own deadline no matter how badly the wall clock hitches.</summary>
    public double OrphanAnimAgeMs(int i) => AnimClockMs - _orphans[i].EnqueuedAnimMs;

    // ── connected-animation overlays (flying shared-element heroes) ───────────────────────────────
    /// <summary>Register a node as a connected-animation overlay: it draws in an UNCLIPPED top band ABOVE the drag
    /// ghost (escaping every ancestor scissor) and is excluded from the main + orphan passes. The node's
    /// <see cref="NodePaint.LocalTransform"/> carries the animated fly position/scale. Bounded by <c>MaxOverlays</c>
    /// (overflow instant-frees the oldest). Cleared by <see cref="RemoveOverlay"/> or when the node is freed.</summary>
    public void AddOverlay(NodeHandle node)
    {
        if (!IsLive(node) || _overlays.Contains(node)) return;
        if (_overlays.Count >= MaxOverlays) { var old = _overlays[0]; _overlays.RemoveAt(0); FreeSubtree(old); }
        _flags[node.Raw.Index] |= NodeFlags.ConnectedOverlay;
        _overlays.Add(node);
        NoteCaptureChanged((int)node.Raw.Index);   // the overlay band and the node's flags are captured
    }

    /// <summary>Drop a node from the overlay band (does NOT free it — the caller owns its lifetime).</summary>
    public void RemoveOverlay(NodeHandle node)
    {
        for (int i = _overlays.Count - 1; i >= 0; i--)
            if (_overlays[i] == node) { _overlays.RemoveAt(i); break; }
        if (IsLive(node)) _flags[node.Raw.Index] &= ~NodeFlags.ConnectedOverlay;
        _mutationStamp = _publishSeq + 1;   // the overlay band is captured (a dead node has no row to ledger)
    }

    /// <summary>Count of connected-animation overlays currently flying (the host keeps painting while &gt; 0).</summary>
    public int OverlayCount => _overlays.Count;

    /// <summary>The i-th connected-animation overlay node (for the recorder's top-band draw pass).</summary>
    public NodeHandle OverlayAt(int i) => _overlays[i];

    public bool IsOverlay(NodeHandle node)
    {
        for (int i = 0; i < _overlays.Count; i++) if (_overlays[i] == node) return true;
        return false;
    }

    // ── column accessors (re-fetch after any CreateNode that may grow) ─────────────
    public ref LayoutInput Layout(NodeHandle h) => ref _layout[h.Raw.Index];
    public ref RectF Bounds(NodeHandle h) => ref _bounds[h.Raw.Index];
    public ref NodePaint Paint(NodeHandle h) => ref _paint[h.Raw.Index];
    public ref InteractionInfo Interaction(NodeHandle h) => ref _interaction[h.Raw.Index];
    /// <summary>Read a node's flag word. By VALUE, deliberately: a mutable <c>ref</c> here would let any of ~300 call
    /// sites write a captured column invisibly, which the P8 capture ledger cannot survive. Writers use
    /// <see cref="SetFlagBits"/>/<see cref="ClearFlagBits"/>/<see cref="Mark"/>/<see cref="Unmark"/>.</summary>
    public NodeFlags Flags(NodeHandle h) => _flags[h.Raw.Index];
    public ushort ElementTypeId(NodeHandle h) => _elementTypeId[h.Raw.Index];

    private int LiveIndex(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        if (raw > 0 && raw < (uint)_high)
        {
            int idx = (int)raw;
            if (_gen[idx] == h.Raw.Gen) return idx;
        }
        throw new InvalidOperationException($"Node handle {h} is not live in this SceneStore.");
    }

    private int LiveIndexOrZero(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        if (raw > 0 && raw < (uint)_high)
        {
            int idx = (int)raw;
            if (_gen[idx] == h.Raw.Gen) return idx;
        }
        return 0;
    }

    public void SetClickHandler(NodeHandle h, Action? handler) => _click[LiveIndex(h)] = handler;
    public Action? GetClickHandler(NodeHandle h) => _click[LiveIndexOrZero(h)];
    public void SetBoundsChangedHandler(NodeHandle h, Action<RectF>? handler)
    {
        int idx = LiveIndex(h);
        // First install of a handler on a node that had none ⇒ arm a one-shot initial delivery: an unconstrained node
        // whose final arranged rect equals its silently-Measured rect would otherwise never see its first value (the
        // edge-triggered SetArrangedBounds only fires on a delta). Re-installing on steady re-renders (handler already
        // present) does NOT re-arm, so the callback fires once at mount then only on real bounds changes.
        if (handler is not null && _boundsChanged[idx] is null) _flags[idx] |= NodeFlags.BoundsChangedPending;
        else if (handler is null) _flags[idx] &= ~NodeFlags.BoundsChangedPending;
        _boundsChanged[idx] = handler;
    }
    public Action<RectF>? GetBoundsChangedHandler(NodeHandle h) => _boundsChanged[LiveIndexOrZero(h)];
    /// <summary>Add a HOOK-owned arranged-bounds observer (UseMeasuredBounds/Width) — composed via <see cref="Delegate.Combine"/>
    /// into a SLOT SEPARATE from the element author's <see cref="SetBoundsChangedHandler"/> (which the reconciler clobbers on every
    /// re-render). FlexLayout dispatches to both. Does NOT arm <see cref="NodeFlags.BoundsChangedPending"/>: the hook seeds its
    /// own initial value from the live bounds and aligns the delivered baseline at install time, so no first-arrange one-shot is
    /// needed (and none is wanted — it would double-deliver on top of the seed).</summary>
    public void AddBoundsChangedHook(NodeHandle h, Action<RectF> handler)
    {
        int idx = LiveIndex(h);
        _boundsChangedHook[idx] = (Action<RectF>?)Delegate.Combine(_boundsChangedHook[idx], handler);
    }
    /// <summary>Remove a handler added by <see cref="AddBoundsChangedHook"/> (hook cleanup / unmount). Safe on an
    /// already-freed node (its slot was cleared on free).</summary>
    public void RemoveBoundsChangedHook(NodeHandle h, Action<RectF> handler)
    {
        int idx = LiveIndexOrZero(h);
        if (idx == 0) return;
        _boundsChangedHook[idx] = (Action<RectF>?)Delegate.Remove(_boundsChangedHook[idx], handler);
    }
    public Action<RectF>? GetBoundsChangedHook(NodeHandle h) => _boundsChangedHook[LiveIndexOrZero(h)];
    /// <summary>The last arranged rect delivered to this node's OnBoundsChanged (the edge baseline). FlexLayout fires the
    /// handler when the freshly-arranged rect differs from this — NOT from the live <see cref="Bounds"/>, which Measure
    /// pre-writes to the hypothetical size each pass (so an unconstrained node would otherwise never re-notify).</summary>
    public ref RectF BoundsDeliveredRef(NodeHandle h) => ref _boundsDelivered[LiveIndex(h)];
    public void SetKeyHandler(NodeHandle h, Action<KeyEventArgs>? handler) => _keyHandler[LiveIndex(h)] = handler;
    public Action<KeyEventArgs>? GetKeyHandler(NodeHandle h) => _keyHandler[LiveIndexOrZero(h)];
    public void SetCharHandler(NodeHandle h, Action<CharEventArgs>? handler) => _charHandler[LiveIndex(h)] = handler;
    public Action<CharEventArgs>? GetCharHandler(NodeHandle h) => _charHandler[LiveIndexOrZero(h)];
    public void SetPointerDown(NodeHandle h, Action<Point2>? handler) => _pointerDown[LiveIndex(h)] = handler;
    public Action<Point2>? GetPointerDown(NodeHandle h) => _pointerDown[LiveIndexOrZero(h)];
    public void SetDrag(NodeHandle h, Action<Point2>? handler) => _drag[LiveIndex(h)] = handler;
    public Action<Point2>? GetDrag(NodeHandle h) => _drag[LiveIndexOrZero(h)];
    public void SetHoverMove(NodeHandle h, Action<Point2>? handler) => _hoverMove[LiveIndex(h)] = handler;
    public Action<Point2>? GetHoverMove(NodeHandle h) => _hoverMove[LiveIndexOrZero(h)];
    public void SetPointerMoveWithin(NodeHandle h, Action<Point2>? handler) => _pointerMoveWithin[LiveIndex(h)] = handler;
    public Action<Point2>? GetPointerMoveWithin(NodeHandle h) => _pointerMoveWithin[LiveIndexOrZero(h)];
    public void SetPointerExit(NodeHandle h, Action? handler) => _pointerExit[LiveIndex(h)] = handler;
    public Action? GetPointerExit(NodeHandle h) => _pointerExit[LiveIndexOrZero(h)];
    public void SetPointerPressed(NodeHandle h, Action<PointerEventArgs>? handler) => _pointerPressed[LiveIndex(h)] = handler;
    public Action<PointerEventArgs>? GetPointerPressed(NodeHandle h) => _pointerPressed[LiveIndexOrZero(h)];
    public void SetPointerReleased(NodeHandle h, Action<PointerEventArgs>? handler) => _pointerReleased[LiveIndex(h)] = handler;
    public Action<PointerEventArgs>? GetPointerReleased(NodeHandle h) => _pointerReleased[LiveIndexOrZero(h)];
    public void SetPointerWheel(NodeHandle h, Action<WheelEventArgs>? handler) => _pointerWheel[LiveIndex(h)] = handler;
    public Action<WheelEventArgs>? GetPointerWheel(NodeHandle h) => _pointerWheel[LiveIndexOrZero(h)];
    public void SetContextRequested(NodeHandle h, Action<ContextRequestEventArgs>? handler) => _contextRequested[LiveIndex(h)] = handler;
    public Action<ContextRequestEventArgs>? GetContextRequested(NodeHandle h) => _contextRequested[LiveIndexOrZero(h)];
    public void SetFocusChanged(NodeHandle h, Action<bool>? handler) => _focusChanged[LiveIndex(h)] = handler;
    public Action<bool>? GetFocusChanged(NodeHandle h) => _focusChanged[LiveIndexOrZero(h)];
    // Drag-reorder lifecycle columns (E5) — set by the reconciler from BoxEl.CanDrag, read by Input.DragController.
    public void SetDragStarted(NodeHandle h, Action<DragEventArgs>? handler) => _dragStarted[LiveIndex(h)] = handler;
    public Action<DragEventArgs>? GetDragStarted(NodeHandle h) => _dragStarted[LiveIndexOrZero(h)];
    public void SetDragDelta(NodeHandle h, Action<DragEventArgs>? handler) => _dragDelta[LiveIndex(h)] = handler;
    public Action<DragEventArgs>? GetDragDelta(NodeHandle h) => _dragDelta[LiveIndexOrZero(h)];
    public void SetDragCompleted(NodeHandle h, Action<DragEventArgs>? handler) => _dragCompleted[LiveIndex(h)] = handler;
    public Action<DragEventArgs>? GetDragCompleted(NodeHandle h) => _dragCompleted[LiveIndexOrZero(h)];
    public void SetDragCanceled(NodeHandle h, Action? handler) => _dragCanceled[LiveIndex(h)] = handler;
    public Action? GetDragCanceled(NodeHandle h) => _dragCanceled[LiveIndexOrZero(h)];

    // ── implicit brush transitions (WinUI BrushTransition; phase-7 advanced) ──────────────────────
    public bool HasBrushAnims => _brushAnims.Count > 0;
    public void SetBrushAnim(NodeHandle h, in BrushAnim ba)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _brushAnims.GetOrAdd(idx) = ba;
        MarkRecordDirty(idx);
    }
    public bool TryGetBrushAnim(NodeHandle h, out BrushAnim ba) => _brushAnims.TryGet((int)h.Raw.Index, out ba);

    /// <summary>Force a full re-record: mark every occupied node <see cref="NodeFlags.PaintDirty"/>. Device-lost recovery
    /// (threading-render-seam.md §9) — after <c>RecoverDevice</c> the backend's textures + glyph atlas are freshly
    /// recreated and empty, so the next frame must regenerate the WHOLE DrawList to repopulate them via on-demand glyph
    /// re-rasterization + image re-upload. Paired with the host's <c>_needFullLayout</c>. Cold path (once per loss).</summary>
    public void MarkAllPaintDirty()
    {
        for (int i = 1; i < _high; i++)
            if (_gen[i] != 0)
            {
                _flags[i] |= NodeFlags.PaintDirty;
                MarkRecordDirty(i);
            }
    }

    /// <summary>Set the brush cross-fade progress, driven by the unified engine's <c>AnimChannel.BrushFade</c> track
    /// (the separate per-frame AdvanceBrushAnims ticker is deleted). Marks PaintDirty; drops the row at T≥1 so the
    /// recorder snaps to the live color. The engine's BrushFade track keeps the loop awake while a fade runs.</summary>
    public void SetBrushAnimT(int idx, float t)
    {
        if (!_brushAnims.TryGet(idx, out var ba)) return;
        if (_gen[idx] == 0) { _brushAnims.Remove(idx); return; }
        ba.T = t < 0f ? 0f : (t > 1f ? 1f : t);
        _flags[idx] |= NodeFlags.PaintDirty;
        MarkRecordDirty(idx);
        if (ba.T >= 1f) _brushAnims.Remove(idx);
        else _brushAnims.GetOrAdd(idx) = ba;
    }

    // ── text-edit decoration side-table (sparse; only editor TEXT nodes have an entry) ───────────────
    /// <summary>Get-or-create the text-edit row for an editor's text node (caret/IME/focus PODs).</summary>
    public ref TextEditState TextEditRef(NodeHandle h)
    {
        NoteCaptureChanged((int)h.Raw.Index);   // P8: write-intent accessor for a captured side table
        return ref CollectionsMarshal.GetValueRefOrAddDefault(_textEdits, (int)h.Raw.Index, out _);
    }

    public bool HasTextEdit(NodeHandle h) => _textEdits.ContainsKey((int)h.Raw.Index);

    /// <summary>Read the text-edit row by value (false + default if the node is not an editor).</summary>
    public bool TryGetTextEdit(NodeHandle h, out TextEditState s) => _textEdits.TryGetValue((int)h.Raw.Index, out s);

    /// <summary>Drop the node's text-edit row AND its pooled decoration-rect slots (editor unmounted / no longer editable).</summary>
    public void ClearTextEdit(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        _textEdits.Remove(idx);
        _textEditSelRects.Remove(idx);
        _textEditUnderlineRects.Remove(idx);
        MarkRecordDirty(idx);
    }

    /// <summary>Any editor currently focused with a blink-visible caret (cheap host gate; O(editors), usually 0–1).</summary>
    public bool AnyTextEditCaretVisible
    {
        get
        {
            const byte on = TextEditState.CaretVisible | TextEditState.Focused;
            foreach (var kv in _textEdits)
                if ((kv.Value.Flags & on) == on) return true;
            return false;
        }
    }

    /// <summary>
    /// Publish this frame's selection-highlight + IME-clause-underline rects for an editor's text node (TEXT-NODE-LOCAL
    /// coords, computed by the control at edit/drag time). Backing arrays are per-node pooled and grow-only — reused
    /// across frames so a selection drag at pointer rate is 0-alloc once grown. Empty spans clear (count → 0, array kept).
    /// </summary>
    public void SetTextEditRects(NodeHandle node, ReadOnlySpan<RectF> selection, ReadOnlySpan<RectF> compUnderlines)
    {
        int idx = (int)node.Raw.Index;
        StoreRects(_textEditSelRects, idx, selection);
        StoreRects(_textEditUnderlineRects, idx, compUnderlines);
        MarkRecordDirty(idx);
    }

    /// <summary>The node's published selection-highlight rects (empty span when no selection).</summary>
    public ReadOnlySpan<RectF> GetTextEditSelectionRects(NodeHandle h)
        => _textEditSelRects.TryGetValue((int)h.Raw.Index, out var s) && s.Arr is not null
            ? s.Arr.AsSpan(0, s.Count) : default;

    /// <summary>The node's published IME composition-underline rects (empty span when no composition).</summary>
    public ReadOnlySpan<RectF> GetTextEditUnderlineRects(NodeHandle h)
        => _textEditUnderlineRects.TryGetValue((int)h.Raw.Index, out var s) && s.Arr is not null
            ? s.Arr.AsSpan(0, s.Count) : default;

    // ── span-text side-tables (rtb-01 inline runs / rtb-02 read-only selection / api-04 highlight color) ────────────

    /// <summary>Attach a span paragraph's element spans (hyperlink OnClick lookup; written by the reconciler from
    /// <c>SpanTextEl.Spans</c> — the POD shaping overlay rides <c>TextStyle.SpanRunId</c> instead). COPIES into a
    /// scene-owned, grow-only-capacity array (never shrinks; alias-safe against a reused <see cref="SpanBuffer"/> the
    /// caller refills next recycle) rather than retaining the caller's array by reference. Empty span clears.</summary>
    public void SetSpanText(NodeHandle node, ReadOnlySpan<TextSpan> spans)
    {
        int idx = (int)node.Raw.Index;
        if (spans.Length == 0) { _spanText.Remove(idx); return; }
        _spanText.TryGetValue(idx, out var slot);
        if (slot.Arr is null || slot.Arr.Length < spans.Length) slot.Arr = new TextSpan[spans.Length];
        spans.CopyTo(slot.Arr);
        slot.Count = spans.Length;
        _spanText[idx] = slot;
    }

    /// <summary>The node's scene-owned span-text copy (the live [0, Count) prefix — never the backing array's full
    /// length, which may hold stale high-water capacity past the live count).</summary>
    public bool TryGetSpanText(NodeHandle h, out ReadOnlySpan<TextSpan> spans)
    {
        if (_spanText.TryGetValue((int)h.Raw.Index, out var slot) && slot.Arr is not null)
        {
            spans = slot.Arr.AsSpan(0, slot.Count);
            return true;
        }
        spans = default;
        return false;
    }

    /// <summary>Index-resolved hyperlink handler for a <c>SpanTextEl</c> node (P2): the dispatcher tries the clicked
    /// span's own <c>TextSpan.OnClick</c> first, else this. Mount-static — the reconciler writes it unconditionally
    /// each <c>WriteColumns</c> pass (a plain reference set, no bind-effect machinery). Null clears.</summary>
    public void SetSpanClickHandler(NodeHandle node, Action<int>? handler)
    {
        int idx = (int)node.Raw.Index;
        if (handler is null) _spanClickHandlers.Remove(idx);
        else _spanClickHandlers[idx] = handler;
    }

    public bool TryGetSpanClickHandler(NodeHandle h, out Action<int> handler)
        => _spanClickHandlers.TryGetValue((int)h.Raw.Index, out handler!);

    /// <summary>Attach (or clear, when null) a node's <see cref="GlyphWipe"/> — the sparse carrier for a glyph-run wipe
    /// (the lyrics karaoke). Read by the recorder's Text case → emits <c>DrawGlyphRunGradient</c>. Only wiped nodes pay.</summary>
    public void SetGlyphWipe(NodeHandle h, GlyphWipe? w)
    {
        int idx = (int)h.Raw.Index;
        if (w is null) _glyphWipes.Remove(idx);
        else _glyphWipes.GetOrAdd(idx) = w.Value;
        MarkRecordDirty(idx);
    }
    public bool TryGetGlyphWipe(NodeHandle h, out GlyphWipe w) => _glyphWipes.TryGet((int)h.Raw.Index, out w);

    /// <summary>Move the split of a node's existing <see cref="GlyphWipe"/> (the <c>AnimChannel.GlyphWipeSplit</c> side table
    /// on the UI-owned path). A node without a wipe has nothing to move.</summary>
    public void SetGlyphWipeSplit(NodeHandle h, float split)
    {
        int idx = (int)h.Raw.Index;
        if (!_glyphWipes.TryGet(idx, out var w)) return;
        _glyphWipes.GetOrAdd(idx) = w with { Split = split < 0f ? 0f : (split > 1f ? 1f : split) };
        _flags[idx] |= NodeFlags.PaintDirty;
        MarkRecordDirty(idx);
    }

    /// <summary>Swap a text node's span-run id with ownership accounting (the scene row owns one table ref plus one
    /// StringTable ref per span family — mirroring the <c>paint.Text</c> discipline). Reconciler rewrite path; the
    /// free path releases via <see cref="ReleaseSpanRun"/>.</summary>
    public void ReleaseSpanRun(int id)
    {
        if (id == 0) return;
        if (Strings is { } st && SpanRunTable.Shared.Resolve(id) is { } run)
            for (int i = 0; i < run.Spans.Length; i++) st.Release(run.Spans[i].FontFamily);
        SpanRunTable.Shared.Release(id);
    }

    /// <summary>The dispatcher-owned read-only selection range on a selectable text node (UTF-16 [start, end) of the
    /// node's paint text). Mirrors what the published selection rects show; consumers (Ctrl+C copy) read it back.</summary>
    public void SetTextSelection(NodeHandle node, int start, int end)
    {
        int idx = (int)node.Raw.Index;
        _textSelection[idx] = (start, end);
        MarkRecordDirty(idx);
    }

    public bool TryGetTextSelection(NodeHandle h, out int start, out int end)
    {
        if (_textSelection.TryGetValue((int)h.Raw.Index, out var r)) { start = r.Start; end = r.End; return true; }
        start = end = 0;
        return false;
    }

    public void ClearTextSelection(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        _textSelection.Remove(idx);
        MarkRecordDirty(idx);
    }

    /// <summary>Per-node selection-highlight override (api-04, WinUI TextBlock.SelectionHighlightColor —
    /// TextBlock.cpp:266/330). A==0 clears back to the host theme brush (TextEditStyle.SelectionFill — the system
    /// accent, TextSelectionManager.cpp:52-56).</summary>
    public void SetSelectionHighlight(NodeHandle node, ColorF color)
    {
        int idx = (int)node.Raw.Index;
        if (color.A <= 0f) _selectionHighlight.Remove(idx);
        else _selectionHighlight.GetOrAdd(idx) = color;
        MarkRecordDirty(idx);
    }

    public bool TryGetSelectionHighlight(NodeHandle h, out ColorF color)
        => _selectionHighlight.TryGet((int)h.Raw.Index, out color);

    private static void StoreRects(Dictionary<int, (RectF[]? Arr, int Count)> table, int idx, ReadOnlySpan<RectF> rects)
    {
        if (rects.IsEmpty)
        {
            // Clear without dropping the pooled array; never create an entry just to say "empty".
            ref var existing = ref CollectionsMarshal.GetValueRefOrNullRef(table, idx);
            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref existing)) existing.Count = 0;
            return;
        }
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(table, idx, out _);
        RectF[]? arr = slot.Arr;
        if (arr is null || arr.Length < rects.Length)
        {
            int cap = arr is { Length: > 0 } ? arr.Length : 4;
            while (cap < rects.Length) cap *= 2;
            arr = new RectF[cap];   // grow-only; steady-state (selection drags) reuses with zero alloc
            slot.Arr = arr;
        }
        rects.CopyTo(arr);
        slot.Count = rects.Length;
    }

    /// <summary>First live, enabled, visible, attached node whose keyboard-accelerator chord matches — cold keydown path, O(high).
    /// A node under a presence-collapsed ancestor (<see cref="InCollapsedSubtree"/>) does not count as visible. A non-empty
    /// <paramref name="within"/> (an open modal and the overlays stacked above it) also requires the owner under one of those roots.</summary>
    public NodeHandle FindAccelerator(int key, KeyModifiers mods, ReadOnlySpan<NodeHandle> within = default)
    {
        for (int i = 1; i < _high; i++)
        {
            if (_gen[i] == 0 || _interaction[i].AccelKey != key || _interaction[i].AccelMods != mods) continue;
            var h = new NodeHandle(new Handle((uint)i, _gen[i]));
            if (!IsLive(h)) continue;
            if ((_flags[i] & (NodeFlags.Visible | NodeFlags.Disabled)) != NodeFlags.Visible) continue;
            if (!IsAttachedToRoot(i)) continue;
            if (InCollapsedSubtree(h)) continue;   // a collapsed ancestor clears only its own Visible bit
            if (!InAnyScope(i, within)) continue;   // a modal dialog is open: the page behind it gets no chords
            return h;
        }
        return NodeHandle.Null;
    }

    /// <summary>First live, enabled, visible, attached node whose access-key mnemonic matches (Alt+letter) — cold path, O(high).
    /// A node under a presence-collapsed ancestor (<see cref="InCollapsedSubtree"/>) does not count as visible. A non-empty
    /// <paramref name="within"/> (an open modal and the overlays stacked above it) also requires the owner under one of those roots.</summary>
    public NodeHandle FindAccessKey(char key, ReadOnlySpan<NodeHandle> within = default)
    {
        for (int i = 1; i < _high; i++)
        {
            if (_gen[i] == 0 || _interaction[i].AccessKey != key) continue;
            var h = new NodeHandle(new Handle((uint)i, _gen[i]));
            if (!IsLive(h)) continue;
            if ((_flags[i] & (NodeFlags.Visible | NodeFlags.Disabled)) != NodeFlags.Visible) continue;
            if (!IsAttachedToRoot(i)) continue;
            if (InCollapsedSubtree(h)) continue;   // a collapsed ancestor clears only its own Visible bit
            if (!InAnyScope(i, within)) continue;   // a modal dialog is open: the page behind it gets no chords
            return h;
        }
        return NodeHandle.Null;
    }

    /// <summary>Is this chord owner linked under <see cref="Root"/>? A KeepAlive-parked page, a parked virtual-list slot and
    /// an exit orphan stay live with their handlers but are detached, so without this a hidden page's Ctrl+R (lower slot
    /// index, first match) shadows the shown page's same chord.</summary>
    private bool IsAttachedToRoot(int idx) => IsAttachedUnder(idx, (int)Root.Raw.Index);

    private bool IsAttachedUnder(int idx, int ancestor)
    {
        if (ancestor == 0) return false;
        for (int n = idx; n != 0; n = _parent[n])
            if (n == ancestor) return true;
        return false;
    }

    /// <summary>Empty <paramref name="within"/> = no restriction; else the node sits under one of its LIVE roots (a dead
    /// root's slot may already belong to another node).</summary>
    private bool InAnyScope(int idx, ReadOnlySpan<NodeHandle> within)
    {
        if (within.IsEmpty) return true;
        foreach (var r in within)
            if (IsLive(r) && IsAttachedUnder(idx, (int)r.Raw.Index)) return true;
        return false;
    }

    public bool HasDynamicText => _dynamicTextCount > 0;

    /// <summary>Bumped on every dynamic-text registration CHANGE (mount, unmount, kind swap) — a freshly-mounted node
    /// has no resolved id yet, so the host must force one <see cref="UpdateDynamicText"/> pass even when no displayed
    /// value moved this frame (the intern-on-change fast path would otherwise skip it).</summary>
    public int DynamicTextEpoch { get; private set; }

    public void SetDynamicText(NodeHandle h, DynamicTextKind kind)
    {
        int idx = (int)h.Raw.Index;
        var old = _dynamicText[idx];
        if (old == kind) return;
        if (old == DynamicTextKind.None && kind != DynamicTextKind.None) _dynamicTextCount++;
        else if (old != DynamicTextKind.None && kind == DynamicTextKind.None) _dynamicTextCount--;
        _dynamicText[idx] = kind;
        DynamicTextEpoch++;
        MarkRecordDirty(idx);
    }

    public void UpdateDynamicText(Func<DynamicTextKind, StringId> resolve)
    {
        if (_dynamicTextCount == 0) return;
        for (int i = 1; i < _high; i++)
        {
            var kind = _dynamicText[i];
            if (kind == DynamicTextKind.None) continue;
            var next = resolve(kind);
            if (next == _paint[i].Text) continue;
            if (Strings is { } st) { st.AddRef(next); st.Release(_paint[i].Text); }   // per-frame ids (FPS/ms) reclaim instead of accreting
            _paint[i].Text = next;
            MarkRecordDirty(i);
        }
    }

    private void ClearDynamicText(int idx)
    {
        if (_dynamicText[idx] == DynamicTextKind.None) return;
        _dynamicText[idx] = DynamicTextKind.None;
        _dynamicTextCount--;
    }

    // Arena-backed dirty worklist (layout.md §4.4): the nodes marked LayoutDirty this frame, so scoped relayout is
    // O(dirty) — the host walks each up to its layout boundary and re-solves just that subtree.
    // Record-dirty is the recorder clean-span invalidation bit. It up-propagates because a parent span covers the
    // parent's whole emitted command range, including descendants.
    public const byte RecordDirtyTransform = 1;
    /// <summary>The node's own paint changed (PaintDirty, creation, text / selection / glyph-wipe writes): its whole
    /// subtree's pixels may differ — the recorder damages the subtree.</summary>
    public const byte RecordDirtyContent = 2;
    /// <summary>The node's GEOMETRY or structure may have changed (LayoutDirty, child attach / detach) but not its paint:
    /// it re-records, and the recorder damages only what actually moved or resized — the node's own old ∪ new extent
    /// when its box or placement changed, each child the same way (a re-window that re-appends unchanged rows or grows
    /// the realized range damages nothing but the rows that entered). Shares the content bit's publication stamp.</summary>
    public const byte RecordDirtyLayout = 4;

    public bool AnyRecordDirty => _recordDirtyWroteCount > 0;

    public bool IsRecordDirty(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        return raw > 0 && raw < (uint)_high && _gen[raw] == h.Raw.Gen && _recordDirty[raw] != 0;
    }

    public byte RecordDirtyBits(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        return raw > 0 && raw < (uint)_high && _gen[raw] == h.Raw.Gen ? _recordDirty[raw] : (byte)0;
    }

    public byte RecordDirtySelfBits(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        return raw > 0 && raw < (uint)_high && _gen[raw] == h.Raw.Gen ? _recordDirtySelf[raw] : (byte)0;
    }

    public byte RecordDirtyDescendantBits(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        return raw > 0 && raw < (uint)_high && _gen[raw] == h.Raw.Gen ? _recordDirtyDescendant[raw] : (byte)0;
    }

    public void ClearRecordDirty()
    {
        for (int i = 0; i < _recordDirtyWroteCount; i++)
        {
            int idx = _recordDirtyWrote[i];
            if ((uint)idx < (uint)_recordDirty.Length)
            {
                _recordDirty[idx] = 0;
                _recordDirtySelf[idx] = 0;
                _recordDirtyDescendant[idx] = 0;
                NoteCaptureChanged(idx);   // P8: zeroing the bits is itself a captured-column change
            }
            _recordDirtyWrote[i] = 0;
        }
        _recordDirtyWroteCount = 0;
    }

    /// <summary>The publication the host just captured this scene into. Every later mark belongs to the NEXT one.</summary>
    public ulong PublishSeq => _publishSeq;
    public void NotePublished(ulong seq) => _publishSeq = seq;

    /// <summary>True while any record-dirty bit is still set — retained until a publication carrying it is consumed
    /// (<see cref="ClearRecordDirty(ulong)"/>). The host keeps publishing while bits remain, so the renderer's snapshot sheds
    /// them as it did before the no-op publication skip existed (a stale bit would re-damage its band on every render-side
    /// motion turn).</summary>
    internal bool HasRecordDirtyLedger => _recordDirtyWroteCount > 0;

    /// <summary>Retire each self/descendant transform/content contribution the render thread has adopted
    /// (stamp ≤ <paramref name="consumedSeq"/>), keeping newer contributions. A snapshot carries the union of deltas
    /// since the last CONSUMED publication without a fresh child retaining old ancestor self damage.
    /// O(entries), compacts in place.</summary>
    public void ClearRecordDirty(ulong consumedSeq)
    {
        int kept = 0;
        for (int i = 0; i < _recordDirtyWroteCount; i++)
        {
            int idx = _recordDirtyWrote[i];
            if ((uint)idx >= (uint)_recordDirty.Length) continue;
            ref var stamps = ref _recordDirtyStamp[idx];
            byte self = UnconsumedRecordBits(_recordDirtySelf[idx], stamps.SelfTransform, stamps.SelfContent, consumedSeq);
            byte descendant = UnconsumedRecordBits(_recordDirtyDescendant[idx], stamps.DescendantTransform, stamps.DescendantContent, consumedSeq);
            if (self != _recordDirtySelf[idx] || descendant != _recordDirtyDescendant[idx])
            {
                _recordDirtySelf[idx] = self;
                _recordDirtyDescendant[idx] = descendant;
                _recordDirty[idx] = (byte)(self | descendant);
                NoteCaptureChanged(idx);   // P8: partial retirement changes captured columns too
            }
            if ((self | descendant) != 0) _recordDirtyWrote[kept++] = idx;
        }
        for (int i = kept; i < _recordDirtyWroteCount; i++) _recordDirtyWrote[i] = 0;
        _recordDirtyWroteCount = kept;
    }

    private void MarkRecordDirty(int idx) => MarkRecordDirty(idx, RecordDirtyContent);

    private static byte UnconsumedRecordBits(byte bits, ulong transform, ulong content, ulong consumedSeq)
        => (byte)(bits & ((transform > consumedSeq ? RecordDirtyTransform : 0)
                       | (content > consumedSeq ? RecordDirtyContent | RecordDirtyLayout : 0)));

    private void MarkRecordDirty(int idx, byte bits)
    {
        if ((uint)idx >= (uint)_high || _gen[idx] == 0 || bits == 0) return;
        for (int n = idx; n != 0; n = _parent[n])
        {
            byte oldAggregate = _recordDirty[n];
            byte oldSelf = _recordDirtySelf[n];
            byte oldDescendant = _recordDirtyDescendant[n];
            byte nextAggregate = (byte)(oldAggregate | bits);
            byte nextSelf = n == idx ? (byte)(oldSelf | bits) : oldSelf;
            byte nextDescendant = n == idx ? oldDescendant : (byte)(oldDescendant | bits);
            // Restamp only this contribution, including when its bit was already present. Ancestors' descendant
            // bits must outlive this publication, but their independent self bits can retire as soon as consumed.
            ref var stamps = ref _recordDirtyStamp[n];
            ulong stamp = _publishSeq + 1;
            if (n == idx)
            {
                if ((bits & RecordDirtyTransform) != 0) stamps.SelfTransform = stamp;
                if ((bits & (RecordDirtyContent | RecordDirtyLayout)) != 0) stamps.SelfContent = stamp;
            }
            else
            {
                if ((bits & RecordDirtyTransform) != 0) stamps.DescendantTransform = stamp;
                if ((bits & (RecordDirtyContent | RecordDirtyLayout)) != 0) stamps.DescendantContent = stamp;
            }
            // P8: the MARKED node's own captured columns changed — that is why it is being marked (a paint ref write, a
            // glyph wipe, a text/layout change) — so its capture row must be re-copied even when its dirty bits were
            // already set. A node written on consecutive frames keeps its bits set (each mark re-stamps them one
            // publication ahead of the render thread's consumption), and noting the ledger only on a bit CHANGE froze
            // its snapshot row at the first write until a full capture: the lyrics karaoke wipe advanced at the
            // full-capture cadence (~5 Hz) while every frame wrote a new split. Ancestors are unchanged except for
            // their dirty bytes, so they stay gated on a bit change below.
            if (n == idx) NoteCaptureChanged(n);
            if (nextAggregate == oldAggregate && nextSelf == oldSelf && nextDescendant == oldDescendant)
                continue;
            if (n != idx) NoteCaptureChanged(n);   // P8: the record-dirty BYTES are captured columns - a changed bit is a changed row

            _recordDirty[n] = nextAggregate;
            _recordDirtySelf[n] = nextSelf;
            _recordDirtyDescendant[n] = nextDescendant;
            if (oldAggregate == 0 && oldSelf == 0 && oldDescendant == 0)
            {
                if (_recordDirtyWroteCount == _recordDirtyWrote.Length)
                {
                    int next = Math.Max(_recordDirtyWrote.Length * 2, _gen.Length);
                    Array.Resize(ref _recordDirtyWrote, next);
                }
                _recordDirtyWrote[_recordDirtyWroteCount++] = n;
            }
        }
    }

    private readonly List<NodeHandle> _layoutDirty = new();
    /// <summary>Set once any node is marked <see cref="NodeFlags.LayoutDirty"/> this frame (cheap host gate for scoped relayout).</summary>
    public bool AnyLayoutDirty => _layoutDirty.Count > 0;
    /// <summary>The nodes marked LayoutDirty this frame (the scoped-relayout worklist).</summary>
    public IReadOnlyList<NodeHandle> LayoutDirtyNodes => _layoutDirty;
    /// <summary>Cleared by the host after it runs (scoped) layout — clears the worklist and the per-node LayoutDirty bits.</summary>
    public void ClearLayoutDirty()
    {
        for (int i = 0; i < _layoutDirty.Count; i++)
        {
            var h = _layoutDirty[i];
            if (!IsLive(h)) continue;
            _flags[h.Raw.Index] &= ~NodeFlags.LayoutDirty;
            NoteCaptureChanged((int)h.Raw.Index);   // P8: _flags is a captured column
        }
        _layoutDirty.Clear();
        // P4: clear every AuxFlags.SubtreeLayoutDirty bit Mark() set this frame, from the set-list rather than by walking
        // up from the worklist entries. A freed or detached dirty node has no path back to its former ancestors - see
        // SceneStore.Aux.cs.
        ClearSubtreeLayoutDirtyBits();
    }

    // Frame-scoped transform-motion worklist (mirrors _layoutDirty): the nodes whose transform was written THIS frame
    // (scroll/fling/drag/FLIP/sticky — every motion writer marks TransformDirty). The recorder reads the bit to gate
    // glyph baseline snapping (moving text rides sub-pixel with its plate); the host clears the bits right after record,
    // so a node at rest re-snaps on the very next recorded frame.
    private readonly List<NodeHandle> _transformWrote = new();
    /// <summary>True when any node's transform was written this frame (host gate for the one-frame settle repaint).</summary>
    public bool AnyTransformWrote => _transformWrote.Count > 0;
    /// <summary>Cleared by the host right after record — clears the per-node TransformDirty bits marked this frame.</summary>
    public void ClearTransformDirty()
    {
        for (int i = 0; i < _transformWrote.Count; i++)
        {
            var h = _transformWrote[i];
            if (!IsLive(h)) continue;
            _flags[h.Raw.Index] &= ~NodeFlags.TransformDirty;
            NoteCaptureChanged((int)h.Raw.Index);   // P8: _flags is a captured column
        }
        _transformWrote.Clear();
    }

    // Persistent registry of nodes carrying BoundsAnimated. Entries are appended on the 0→1 transition and compacted
    // by the host's FLIP capture pass, so the empty/common case avoids a recursive full-tree search.
    private readonly List<NodeHandle> _boundsAnimated = new();
    internal List<NodeHandle> BoundsAnimatedNodes => _boundsAnimated;

    // Scene-owned VirtualRangeDirty worklist (E6): mirrors _layoutDirty — appended on the 0→1 edge so the reconciler's
    // ReRealizeVirtuals iterates ONLY the dirty viewports instead of scanning the whole _virtuals dictionary every frame.
    // NOT cleared per-frame like _layoutDirty: the reconciler swap-removes an entry once its window fully realizes (the
    // flag clears); a budget-deferred / mid-warm entry stays queued (still flagged) until it catches up. Duplicate-free by
    // construction — the flag stays set while queued, so no fresh 0→1 edge re-adds it.
    private readonly List<NodeHandle> _virtualRangeDirty = new();
    /// <summary>The queued VirtualRangeDirty viewports (the reconciler's realize worklist). Mutable: the reconciler
    /// swap-removes consumed entries in place (same-assembly, mirrors how the host consumes _layoutDirty).</summary>
    internal List<NodeHandle> VirtualRangeDirtyNodes => _virtualRangeDirty;

    public void Mark(NodeHandle h, NodeFlags flags)
    {
        int idx = (int)h.Raw.Index;
        NodeFlags old = _flags[idx];
        if ((flags & NodeFlags.LayoutDirty) != 0)
        {
            // P4 fix (2026-09-19): the subtree-content version bumps on EVERY LayoutDirty mark, edge or not. A second
            // mark on an already-dirty node (a second edit before this frame's ClearLayoutDirty; a realize/rebind
            // between the D1 loop's two RunDirty passes) changes content a ring slot stored in between may already
            // answer for, and the 0→1 edge below never fires for it — see SceneStore.Aux.cs and FlexLayout.TryRingHit.
            BumpSubtreeVersionChain(idx);
            if ((old & NodeFlags.LayoutDirty) == 0)
            {
                _layoutDirty.Add(h);
                // P4 (Operation ultra-fast GPU engine): propagate a subtree-dirty bit up to the layout boundary so
                // Measure/Arrange can skip a whole clean subtree without walking it — see SceneStore.Aux.cs.
                MarkSubtreeLayoutDirtyChain(idx);
            }
        }
        if ((flags & NodeFlags.TransformDirty) != 0 && (old & NodeFlags.TransformDirty) == 0) _transformWrote.Add(h);
        if ((flags & NodeFlags.BoundsAnimated) != 0 && (old & NodeFlags.BoundsAnimated) == 0) _boundsAnimated.Add(h);
        if ((flags & NodeFlags.VirtualRangeDirty) != 0 && (old & NodeFlags.VirtualRangeDirty) == 0) _virtualRangeDirty.Add(h);
        byte recordBits = 0;
        if ((flags & NodeFlags.TransformDirty) != 0) recordBits |= RecordDirtyTransform;
        if ((flags & NodeFlags.LayoutDirty) != 0) recordBits |= RecordDirtyLayout;
        if ((flags & NodeFlags.PaintDirty) != 0) recordBits |= RecordDirtyContent;
        if (recordBits != 0) MarkRecordDirty(idx, recordBits);
        // _flags is a captured column. MarkRecordDirty ledgers the node for any dirty mark; a mark of other flags only
        // (VirtualRangeDirty, BoundsAnimated, Parked, StickyPinned…) is ledgered here, exactly as SetFlagBits does.
        else if ((old | flags) != old) NoteCaptureChanged(idx);
        _flags[idx] = old | flags;
    }
    public void Unmark(NodeHandle h, NodeFlags flags)
    {
        _flags[h.Raw.Index] &= ~flags;
        NoteCaptureChanged((int)h.Raw.Index);   // P8: _flags is a captured column and Unmark marks nothing else
    }

    /// <summary>Set flag bits on a node - the WRITE half of the <see cref="Flags(NodeHandle)"/> pair (which is a
    /// by-value read). Writers must come through here (or <see cref="ClearFlagBits"/>) so the P8 capture ledger sees
    /// the change: hover/press/focus flips a captured column without any record-dirty mark of its own.</summary>
    public void SetFlagBits(NodeHandle h, NodeFlags flags)
    {
        int idx = (int)h.Raw.Index;
        NodeFlags next = _flags[idx] | flags;
        if (next == _flags[idx]) return;
        _flags[idx] = next;
        NoteCaptureChanged(idx);
    }

    /// <summary>Clear flag bits on a node - see <see cref="SetFlagBits"/>. (<see cref="Unmark"/> is the same operation
    /// spelled for the dirty-bit vocabulary; both ledger the write.)</summary>
    public void ClearFlagBits(NodeHandle h, NodeFlags flags)
    {
        int idx = (int)h.Raw.Index;
        NodeFlags next = _flags[idx] & ~flags;
        if (next == _flags[idx]) return;
        _flags[idx] = next;
        NoteCaptureChanged(idx);
    }

    /// <summary>Replace a node's whole flag word (rare - a caller composing several bits at once).</summary>
    public void SetFlagsRaw(NodeHandle h, NodeFlags flags)
    {
        int idx = (int)h.Raw.Index;
        if (_flags[idx] == flags) return;
        _flags[idx] = flags;
        NoteCaptureChanged(idx);
    }

    // ── scroll/virtual side-table (sparse; only viewport nodes have an entry) ──
    /// <summary>Get-or-create the scroll row for a viewport node; marks it <see cref="NodeFlags.Scrollable"/>.</summary>
    public ref ScrollState ScrollRef(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        NoteCaptureChanged(idx);   // P8: ScrollState is a captured column and every writer comes through this ref
        ref ScrollState s = ref _scroll.GetOrAdd(idx, out bool existed);
        if (!existed)
        {
            s = ScrollState.Default;
            _flags[idx] |= NodeFlags.Scrollable;
            MarkRecordDirty(idx);
            // P4: a viewport's ArrangeViewport has continuous per-frame obligations that are NOT LayoutDirty-gated
            // (scrolling is layout-free) — mark every ancestor so the Arrange early-out never strands it unreached.
            MarkScrollDescendantChain(idx);
            OnScrollNodeAdded?.Invoke(idx);
        }
        return ref s;
    }
    public bool HasScroll(NodeHandle h) => _scroll.Contains((int)h.Raw.Index);

    /// <summary>READ-ONLY view of an existing viewport's scroll row, without the write-intent ledger mark
    /// <see cref="ScrollRef"/> makes (every write must still go through <see cref="ScrollRef"/>). For callers that read the
    /// row every frame. The row must exist (<see cref="HasScroll"/>, asserted); a missing one reads as
    /// <see cref="ScrollState.Default"/> and is never created.</summary>
    internal ref readonly ScrollState ScrollRow(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        Debug.Assert(_scroll.Contains(idx), "ScrollRow: the node has no scroll row (guard with HasScroll)");
        if (!_scroll.Contains(idx)) return ref s_missingScrollRow;
        return ref _scroll.GetOrAdd(idx);   // an existing row: a lookup, never an add
    }
    private static readonly ScrollState s_missingScrollRow = ScrollState.Default;
    /// <summary>Read the scroll row by value (default if the node is not a viewport).</summary>
    public bool TryGetScroll(NodeHandle h, out ScrollState s) => _scroll.TryGet((int)h.Raw.Index, out s);

    /// <summary>Index-based scroll row lookup for callers that only carry a raw node INDEX (the scroll coverage / pose
    /// sinks, the scrollbar chrome), so this re-derives liveness from the slot's own generation + the
    /// <see cref="NodeFlags.Scrollable"/> bit (set only by <see cref="ScrollRef"/>'s first-create, cleared by the row
    /// removal in <see cref="FreeSubtree"/>) instead of trusting a caller-supplied handle. A late/stray Apply for an
    /// index that was freed (or freed-and-reused by an unrelated node) since the command was posted lands harmlessly
    /// on the scratch fallback row below — Unbind is posted before the row is removed, so this is a defensive guard
    /// for a same-frame free+realloc race, not the steady-state path.</summary>
    private ScrollState _scrollRefByIndexFallback;
    public ref ScrollState ScrollRefByIndex(int node)
    {
        if ((uint)node < (uint)_high && _gen[node] != 0 && (_flags[node] & NodeFlags.Scrollable) != 0)
        {
            NoteCaptureChanged(node);   // P8: see ScrollRef
            return ref _scroll.GetOrAdd(node, out _);
        }   // guaranteed to already exist (Scrollable is only ever set alongside the row)
        _scrollRefByIndexFallback = default;
        return ref _scrollRefByIndexFallback;
    }
    /// <summary>True while any viewport owns an active expand/collapse presentation.</summary>
    public bool HasActiveVirtualDisclosures => _activeVirtualDisclosureCount != 0;
    /// <summary>Resolve the shared recyclable-item clip owned by a virtual viewport from its direct content node.
    /// The returned prefix count maps directly to the content node's leading child ordinals.</summary>
    public bool TryGetVirtualItemBand(NodeHandle content, out int persistentPrefixCount, out float topInset)
        => TryGetVirtualItemBand(content, out persistentPrefixCount, out topInset, out _);

    /// <summary>Resolve the shared recyclable-item clip and optional top alpha-feather owned by a virtual viewport.</summary>
    public bool TryGetVirtualItemBand(NodeHandle content, out int persistentPrefixCount, out float topInset, out float topFadeBand)
    {
        persistentPrefixCount = 0;
        topInset = float.NaN;
        topFadeBand = 0f;
        if (content.IsNull || !IsLive(content)) return false;
        NodeHandle viewport = Parent(content);
        if (viewport.IsNull || !IsLive(viewport) || !_scroll.TryGet((int)viewport.Raw.Index, out var sc)
            || sc.ContentNode != content || sc.Orientation != 0 || !float.IsFinite(sc.ItemClipTopInset))
            return false;
        persistentPrefixCount = Math.Clamp(sc.PersistentPrefixCount, 0, sc.ItemCount);
        topInset = MathF.Max(0f, sc.ItemClipTopInset);
        topFadeBand = MathF.Max(0f, sc.ItemClipTopFadeBand);
        return true;
    }

    /// <summary>Resolve the active contiguous disclosure band owned by a vertical virtual viewport from its direct
    /// content node. Geometry is in content-local DIP; <paramref name="progress"/> is clamped to 0..1.</summary>
    public bool TryGetVirtualDisclosure(NodeHandle content, out int firstIndex, out int count,
                                        out float top, out float extent, out float progress,
                                        out int persistentPrefixCount, out int firstRealized)
    {
        firstIndex = -1;
        count = 0;
        top = extent = progress = 0f;
        persistentPrefixCount = firstRealized = 0;
        if (_activeVirtualDisclosureCount == 0) return false;
        if (content.IsNull || !IsLive(content)) return false;
        NodeHandle viewport = Parent(content);
        if (viewport.IsNull || !IsLive(viewport) || !_scroll.TryGet((int)viewport.Raw.Index, out var sc)
            || sc.ContentNode != content || sc.Orientation != 0 || !float.IsFinite(sc.DisclosureT)
            || sc.DisclosureFirst < 0 || sc.DisclosureCount <= 0 || sc.DisclosureExtent <= 0f)
            return false;
        firstIndex = sc.DisclosureFirst;
        count = sc.DisclosureCount;
        top = sc.DisclosureTop;
        extent = sc.DisclosureExtent;
        progress = Math.Clamp(sc.DisclosureT, 0f, 1f);
        persistentPrefixCount = Math.Clamp(sc.PersistentPrefixCount, 0, sc.ItemCount);
        firstRealized = Math.Max(persistentPrefixCount, sc.FirstRealized);
        return true;
    }

    /// <summary>Arm or retarget one viewport disclosure and maintain the scene-wide active census.</summary>
    public bool BeginVirtualDisclosure(NodeHandle viewport, int firstIndex, int count,
                                       float top, float extent, float progress)
    {
        if (viewport.IsNull || !IsLive(viewport) || firstIndex < 0 || count <= 0
            || !float.IsFinite(top) || !float.IsFinite(extent) || extent <= 0f
            || !float.IsFinite(progress) || !_scroll.TryGet((int)viewport.Raw.Index, out var snapshot)
            || snapshot.Orientation != 0 || snapshot.ContentNode.IsNull || !IsLive(snapshot.ContentNode))
            return false;

        ref ScrollState sc = ref ScrollRef(viewport);
        bool wasActive = float.IsFinite(sc.DisclosureT);
        sc.DisclosureFirst = firstIndex;
        sc.DisclosureCount = count;
        sc.DisclosureTop = top;
        sc.DisclosureExtent = extent;
        sc.DisclosureT = Math.Clamp(progress, 0f, 1f);
        if (!wasActive) _activeVirtualDisclosureCount++;
        Mark(sc.ContentNode, NodeFlags.PaintDirty);
        return true;
    }

    /// <summary>Animation-side write for a viewport disclosure progress channel.</summary>
    public void SetVirtualDisclosureProgress(NodeHandle viewport, float progress)
    {
        if (viewport.IsNull || !IsLive(viewport) || !float.IsFinite(progress)
            || !_scroll.TryGet((int)viewport.Raw.Index, out var snapshot) || !float.IsFinite(snapshot.DisclosureT))
            return;
        ref ScrollState sc = ref ScrollRef(viewport);
        float next = Math.Clamp(progress, 0f, 1f);
        if (sc.DisclosureT == next) return;
        sc.DisclosureT = next;
        if (!sc.ContentNode.IsNull && IsLive(sc.ContentNode)) Mark(sc.ContentNode, NodeFlags.PaintDirty);
    }

    /// <summary>Current disclosure progress for animation retargeting; zero when inactive.</summary>
    public float VirtualDisclosureProgress(NodeHandle viewport)
        => TryGetScroll(viewport, out var sc) && float.IsFinite(sc.DisclosureT) ? sc.DisclosureT : 0f;

    /// <summary>Release one viewport disclosure. Repeated clears are harmless and never underflow the census.</summary>
    public void ClearVirtualDisclosure(NodeHandle viewport)
    {
        if (viewport.IsNull || !IsLive(viewport) || !_scroll.TryGet((int)viewport.Raw.Index, out var snapshot)) return;
        bool wasActive = float.IsFinite(snapshot.DisclosureT);
        bool hadState = wasActive || snapshot.DisclosureFirst >= 0 || snapshot.DisclosureCount != 0
            || snapshot.DisclosureTop != 0f || snapshot.DisclosureExtent != 0f;
        if (!hadState) return;

        ref ScrollState sc = ref ScrollRef(viewport);
        sc.DisclosureFirst = -1;
        sc.DisclosureCount = 0;
        sc.DisclosureTop = 0f;
        sc.DisclosureExtent = 0f;
        sc.DisclosureT = float.NaN;
        if (wasActive)
        {
            Debug.Assert(_activeVirtualDisclosureCount > 0);
            if (_activeVirtualDisclosureCount > 0) _activeVirtualDisclosureCount--;
        }
        if (!sc.ContentNode.IsNull && IsLive(sc.ContentNode)) Mark(sc.ContentNode, NodeFlags.PaintDirty);
    }

    // ── hit-test pass-through (WinUI FlyoutBase.OverlayInputPassThroughElement) ──────────────────
    // A light-dismiss scrim registers ONE target subtree whose rendered bounds it yields to: pointer input there
    // bypasses the scrim and reaches the content beneath (the MenuBar hover-switches titles with a menu open,
    // FlyoutBase_Partial.cpp:3922-3938). Sparse — O(open scrims), cleared on free.
    private readonly ColdSlab<NodeHandle> _hitPassThrough = new();   // GEN-17 (wired)

    public void SetHitTestPassThrough(NodeHandle node, NodeHandle target)
    {
        if (!IsLive(node)) return;
        if (target.IsNull) _hitPassThrough.Remove((int)node.Raw.Index);
        else _hitPassThrough.GetOrAdd((int)node.Raw.Index) = target;
    }

    public bool TryGetHitTestPassThrough(NodeHandle node, out NodeHandle target)
        => _hitPassThrough.TryGet((int)node.Raw.Index, out target);

    // ── background-scroll occlusion (Element.BlocksBackgroundScroll) ──────────────────────────────
    // A covering modal/popup surface's geometric bounds sit over background content it is not an ancestor of; this
    // marks it opaque for InputDispatcher.ContainingScrollerForAxis's paint-order-blind geometric fallback. Sparse —
    // O(open blocking overlays), same shape as _hitPassThrough.
    private readonly ColdSlab<bool> _wheelOccludes = new();   // GEN-17-shaped (see _hitPassThrough)

    public void SetBlocksBackgroundScroll(NodeHandle node, bool value)
    {
        if (!IsLive(node)) return;
        if (!value) _wheelOccludes.Remove((int)node.Raw.Index);
        else _wheelOccludes.GetOrAdd((int)node.Raw.Index) = true;
    }

    public bool GetBlocksBackgroundScroll(NodeHandle node)
        => _wheelOccludes.TryGet((int)node.Raw.Index, out bool v) && v;

    // ── wheel routing target (Element.WheelTarget) ─────────────────────────────────────────────────
    // A list header laid out ABOVE its list names the list's scroller as the target for wheel input over the header
    // (InputDispatcher.RouteWheelTarget), so the notch glides the LIST instead of the header's own ancestor scroller.
    // Sparse — O(headers), same shape as _hitPassThrough; the slot wraps the reference because ColdSlab is struct-only.
    private struct WheelTargetSlot { public FluentGpu.Scroll.Runtime.ScrollHandle? Target; }
    private readonly ColdSlab<WheelTargetSlot> _wheelTargets = new();

    /// <summary>Live wheel-target rows — the dispatcher's O(1) early-out before it walks a hit chain.</summary>
    public int WheelTargetCount => _wheelTargets.Count;

    public void SetWheelTarget(NodeHandle node, FluentGpu.Scroll.Runtime.ScrollHandle? target)
    {
        if (!IsLive(node)) return;
        int idx = (int)node.Raw.Index;
        if (target is null) { if (_wheelTargets.Count != 0) _wheelTargets.Remove(idx); }
        else _wheelTargets.GetOrAdd(idx).Target = target;
    }

    public bool TryGetWheelTarget(NodeHandle node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FluentGpu.Scroll.Runtime.ScrollHandle? target)
    {
        if (_wheelTargets.Count != 0 && _wheelTargets.TryGet((int)node.Raw.Index, out var slot) && slot.Target is not null)
        {
            target = slot.Target;
            return true;
        }
        target = null;
        return false;
    }

    // ── scroll-linked effects (scroll rework §8) — node index → its baked effect rows (scope resolved to a node
    //    index at bake). The host evaluates them after layout (UI-side paint + coverage-table rows for the poser).
    private readonly Dictionary<int, FluentGpu.Scroll.Effects.ScrollEffect[]> _scrollEffects = new();
    private readonly Dictionary<int, string> _scrollScopes = new();
    public Dictionary<int, FluentGpu.Scroll.Effects.ScrollEffect[]> ScrollEffects => _scrollEffects;
    public int ScrollEffectCount => _scrollEffects.Count;
    public void SetScrollEffects(NodeHandle h, FluentGpu.Scroll.Effects.ScrollEffect[]? effects)
    {
        int idx = (int)h.Raw.Index;
        if (effects is null || effects.Length == 0) _scrollEffects.Remove(idx); else _scrollEffects[idx] = effects;
    }
    public bool TryGetScrollEffects(int nodeIndex, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FluentGpu.Scroll.Effects.ScrollEffect[]? effects)
        => _scrollEffects.TryGetValue(nodeIndex, out effects);
    // The authored engaged-edge signals (ScrollEffectSpec.Engaged), index-aligned with the node's effect rows; present only
    // when at least one row declares one. UI-thread only (the host writes them before publish; never captured).
    private readonly Dictionary<int, FluentGpu.Signals.Signal<bool>?[]> _scrollEffectEngaged = new();
    public void SetScrollEffectEngaged(NodeHandle h, FluentGpu.Signals.Signal<bool>?[]? engaged)
    {
        int idx = (int)h.Raw.Index;
        if (engaged is null) _scrollEffectEngaged.Remove(idx); else _scrollEffectEngaged[idx] = engaged;
    }
    public bool TryGetScrollEffectEngaged(int nodeIndex, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FluentGpu.Signals.Signal<bool>?[]? engaged)
    {
        if (_scrollEffectEngaged.Count == 0) { engaged = null; return false; }
        return _scrollEffectEngaged.TryGetValue(nodeIndex, out engaged);
    }
    /// <summary>Names <paramref name="h"/> as a sticky scope (<c>Element.ScrollScope</c>); null clears.</summary>
    public void SetScrollScope(NodeHandle h, string? name)
    {
        int idx = (int)h.Raw.Index;
        if (name is null) _scrollScopes.Remove(idx); else _scrollScopes[idx] = name;
    }
    public bool TryGetScrollScope(int nodeIndex, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
        => _scrollScopes.TryGetValue(nodeIndex, out name);
    /// <summary>The nearest ancestor of <paramref name="node"/> (inclusive of its parent chain) named <paramref name="scope"/>,
    /// or Null.</summary>
    public NodeHandle FindScrollScope(NodeHandle node, string scope)
    {
        if (_scrollScopes.Count == 0) return NodeHandle.Null;
        for (var p = Parent(node); !p.IsNull; p = Parent(p))
            if (_scrollScopes.TryGetValue((int)p.Raw.Index, out var n) && n == scope) return p;
        return NodeHandle.Null;
    }

    /// <summary>Get-or-create the variable-height extent table for a viewport, (re)building it on item-count change.</summary>
    public ExtentTable ExtentTableFor(NodeHandle h, int itemCount, float estimate)
    {
        int idx = (int)h.Raw.Index;
        if (!_extents.TryGetValue(idx, out var t)) { t = new ExtentTable(itemCount, estimate); _extents[idx] = t; }
        // RESIZE, never Reset: re-seeding every row to one estimate on a count change discards every measured extent
        // (ExtentTable.Resize carries the reasoning — it is the sidebar disclosure flicker).
        else if (t.Count != itemCount) t.Resize(itemCount, estimate);
        return t;
    }
    public bool TryGetExtents(NodeHandle h, out ExtentTable? t) => _extents.TryGetValue((int)h.Raw.Index, out t);

    public void SetGrid(NodeHandle h, in GridSpec spec) => _grids.GetOrAdd((int)h.Raw.Index) = spec;
    public bool HasGrid(NodeHandle h) => _grids.Contains((int)h.Raw.Index);
    public bool TryGetGrid(NodeHandle h, out GridSpec spec) => _grids.TryGet((int)h.Raw.Index, out spec);

    // ── rich-paint side-tables ──
    /// <summary>Get-or-create the eased-interaction row for a node (hover/press progress).</summary>
    public ref InteractionAnim InteractRef(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        NoteCaptureChanged(idx);   // P8: write-intent accessor for a captured side table
        _flags[idx] |= NodeFlags.InteractionAnim;
        ref InteractionAnim s = ref _interact.GetOrAdd(idx, out bool existed);
        if (!existed) { s = InteractionAnim.Default; MarkRecordDirty(idx); }
        return ref s;
    }
    public bool TryGetInteract(NodeHandle h, out InteractionAnim s) => _interact.TryGet((int)h.Raw.Index, out s);

    /// <summary>Write the eased hover (or press) progress for a node — driven by the engine's HoverFade/PressFade track
    /// (the deleted InteractionAnimator.Tick's job). The row exists (the fade was seeded through InteractRef); marks
    /// PaintDirty so the recorder re-composites the hover/press cross-fade + scale.</summary>
    public void SetInteractT(NodeHandle node, bool press, float t)
    {
        if (!IsLive(node)) return;
        ref InteractionAnim ia = ref InteractRef(node);
        if (press) ia.PressT = t; else ia.HoverT = t;
        Mark(node, NodeFlags.PaintDirty);
    }

    public void SetShadow(NodeHandle h, in ShadowSpec s)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _shadows.GetOrAdd(idx) = s;
        MarkRecordDirty(idx);
    }
    public bool TryGetShadow(NodeHandle h, out ShadowSpec s) => _shadows.TryGet((int)h.Raw.Index, out s);
    public void ClearShadow(NodeHandle h) { int idx = (int)h.Raw.Index; _shadows.Remove(idx); MarkRecordDirty(idx); }

    public void SetArc(NodeHandle h, in ArcSpec a)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _arcs.GetOrAdd(idx) = a;
        MarkRecordDirty(idx);
    }
    public bool TryGetArc(NodeHandle h, out ArcSpec a) => _arcs.TryGet((int)h.Raw.Index, out a);
    public void ClearArc(NodeHandle h) { int idx = (int)h.Raw.Index; _arcs.Remove(idx); MarkRecordDirty(idx); }

    public void SetPolylineStroke(NodeHandle h, in PolylineStrokeSpec p)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _polylines.GetOrAdd(idx) = p;
        MarkRecordDirty(idx);
    }
    public bool TryGetPolylineStroke(NodeHandle h, out PolylineStrokeSpec p) => _polylines.TryGet((int)h.Raw.Index, out p);
    public void ClearPolylineStroke(NodeHandle h) { int idx = (int)h.Raw.Index; _polylines.Remove(idx); MarkRecordDirty(idx); }

    public void SetPath(NodeHandle h, in PathSpec ps)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _paths.GetOrAdd(idx) = ps;
        MarkRecordDirty(idx);
    }
    public bool TryGetPath(NodeHandle h, out PathSpec ps) => _paths.TryGet((int)h.Raw.Index, out ps);
    public void ClearPath(NodeHandle h) { int idx = (int)h.Raw.Index; _paths.Remove(idx); MarkRecordDirty(idx); }

    public void SetSeries(NodeHandle h, in SeriesSpec spec)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _series.GetOrAdd(idx) = spec;
        MarkRecordDirty(idx);
    }
    public bool TryGetSeries(NodeHandle h, out SeriesSpec spec) => _series.TryGet((int)h.Raw.Index, out spec);
    public void ClearSeries(NodeHandle h) { int idx = (int)h.Raw.Index; _series.Remove(idx); MarkRecordDirty(idx); }

    /// <summary>The tier-3 STENCIL path clip for this node (gpu-renderer.md §6). Setting it marks SparsePaint and
    /// dirties the record exactly like <see cref="SetPath"/> does for the FillPath lane, so a changed silhouette
    /// invalidates any clean span that copied the old push/pop pair. The caller (the reconciler) also marks
    /// <see cref="NodeFlags.ClipsToBounds"/> — the recorder's gate is
    /// <c>ClipsToBounds &amp;&amp; SparsePaint &amp;&amp; TryGetClipPath</c>, which costs nothing on non-clipping nodes.</summary>
    public void SetClipPath(NodeHandle h, in ClipPathSpec cs)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _clipPaths.GetOrAdd(idx) = cs;
        MarkRecordDirty(idx);
    }
    public bool TryGetClipPath(NodeHandle h, out ClipPathSpec cs) => _clipPaths.TryGet((int)h.Raw.Index, out cs);
    public void ClearClipPath(NodeHandle h) { int idx = (int)h.Raw.Index; _clipPaths.Remove(idx); MarkRecordDirty(idx); }

    public void SetGradient(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _gradients.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetGradient(NodeHandle h, out GradientSpec g) => _gradients.TryGet((int)h.Raw.Index, out g);
    public void ClearGradient(NodeHandle h) { int idx = (int)h.Raw.Index; _gradients.Remove(idx); MarkRecordDirty(idx); }

    public void SetRadialGradientCenter(NodeHandle h, Point2 center)
    {
        int idx = (int)h.Raw.Index;
        if (_radialGradientCenters.TryGet(idx, out Point2 current) && current == center) return;
        _flags[idx] |= NodeFlags.SparsePaint;
        _radialGradientCenters.GetOrAdd(idx) = center;
        _flags[idx] |= NodeFlags.PaintDirty;
        MarkRecordDirty(idx);
    }
    public bool TryGetRadialGradientCenter(NodeHandle h, out Point2 center)
        => _radialGradientCenters.TryGet((int)h.Raw.Index, out center);
    public void ClearRadialGradientCenter(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        if (!_radialGradientCenters.TryGet(idx, out _)) return;
        _radialGradientCenters.Remove(idx);
        _flags[idx] |= NodeFlags.PaintDirty;
        MarkRecordDirty(idx);
    }

    public void SetGradientTo(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        if (_gradientTos.TryGet(idx, out var cur) && cur.Equals(g)) return;   // equal writes are no-ops (a re-render re-applies)
        _flags[idx] |= NodeFlags.SparsePaint;
        _gradientTos.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetGradientTo(NodeHandle h, out GradientSpec g) => _gradientTos.TryGet((int)h.Raw.Index, out g);
    public void ClearGradientTo(NodeHandle h) { int idx = (int)h.Raw.Index; _gradientTos.Remove(idx); MarkRecordDirty(idx); }

    /// <summary>The fill's blend toward <see cref="SetGradientTo"/>. Paint-only: equal writes are no-ops; 0 is stored as
    /// absent (the recorder's default).</summary>
    public void SetGradientMix(NodeHandle h, float mix)
    {
        int idx = (int)h.Raw.Index;
        mix = float.IsFinite(mix) ? Math.Clamp(mix, 0f, 1f) : 0f;
        bool had = _gradientMixes.TryGet(idx, out float current);
        if (mix <= 0f)
        {
            if (!had) return;
            _gradientMixes.Remove(idx);
        }
        else
        {
            if (had && current == mix) return;
            _flags[idx] |= NodeFlags.SparsePaint;
            _gradientMixes.GetOrAdd(idx) = mix;
        }
        _flags[idx] |= NodeFlags.PaintDirty;
        MarkRecordDirty(idx);
    }
    public bool TryGetGradientMix(NodeHandle h, out float mix) => _gradientMixes.TryGet((int)h.Raw.Index, out mix);

    /// <summary>BoxEl.Feedback (null clears). Equality-gated; marks the record dirty so the slice re-records and its composite
    /// item carries the new warp/decay.</summary>
    public void SetFeedback(NodeHandle h, FeedbackState? state)
    {
        int idx = (int)h.Raw.Index;
        bool had = _feedback.TryGet(idx, out var cur);
        if (state is not { } s) { if (!had) return; _feedback.Remove(idx); MarkRecordDirty(idx); return; }
        if (had && cur == s) return;
        _flags[idx] |= NodeFlags.SparsePaint;
        _feedback.GetOrAdd(idx) = s;
        MarkRecordDirty(idx);
    }
    public bool TryGetFeedback(NodeHandle h, out FeedbackState state) => _feedback.TryGet((int)h.Raw.Index, out state);

    /// <summary>BoxEl.Blend / BoxEl.LayerBlend. Equality-gated; both SrcOver is stored as absent.</summary>
    public void SetBlend(NodeHandle h, PaintBlend paint, LayerBlend layer)
    {
        int idx = (int)h.Raw.Index;
        byte v = (byte)((byte)paint | ((byte)layer << 4));
        bool had = _blends.TryGet(idx, out byte cur);
        if (v == 0) { if (!had) return; _blends.Remove(idx); }
        else { if (had && cur == v) return; _flags[idx] |= NodeFlags.SparsePaint; _blends.GetOrAdd(idx) = v; }
        MarkRecordDirty(idx);
    }
    public bool TryGetBlend(NodeHandle h, out byte packed) => _blends.TryGet((int)h.Raw.Index, out packed);

    public void SetBorderBrush(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _borderBrushes.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetBorderBrush(NodeHandle h, out GradientSpec g) => _borderBrushes.TryGet((int)h.Raw.Index, out g);
    public void ClearBorderBrush(NodeHandle h) { int idx = (int)h.Raw.Index; _borderBrushes.Remove(idx); MarkRecordDirty(idx); }

    public void SetHoverGradient(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _hoverGradients.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetHoverGradient(NodeHandle h, out GradientSpec g) => _hoverGradients.TryGet((int)h.Raw.Index, out g);
    public void ClearHoverGradient(NodeHandle h) { int idx = (int)h.Raw.Index; _hoverGradients.Remove(idx); MarkRecordDirty(idx); }

    public void SetPressedGradient(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _pressedGradients.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetPressedGradient(NodeHandle h, out GradientSpec g) => _pressedGradients.TryGet((int)h.Raw.Index, out g);
    public void ClearPressedGradient(NodeHandle h) { int idx = (int)h.Raw.Index; _pressedGradients.Remove(idx); MarkRecordDirty(idx); }

    public void SetHoverBorderBrush(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _hoverBorderBrushes.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetHoverBorderBrush(NodeHandle h, out GradientSpec g) => _hoverBorderBrushes.TryGet((int)h.Raw.Index, out g);
    public void ClearHoverBorderBrush(NodeHandle h) { int idx = (int)h.Raw.Index; _hoverBorderBrushes.Remove(idx); MarkRecordDirty(idx); }

    public void SetPressedBorderBrush(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _pressedBorderBrushes.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetPressedBorderBrush(NodeHandle h, out GradientSpec g) => _pressedBorderBrushes.TryGet((int)h.Raw.Index, out g);
    public void ClearPressedBorderBrush(NodeHandle h) { int idx = (int)h.Raw.Index; _pressedBorderBrushes.Remove(idx); MarkRecordDirty(idx); }

    public void SetAcrylic(NodeHandle h, in AcrylicSpec a)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _acrylics.GetOrAdd(idx) = a;
        MarkRecordDirty(idx);
    }
    public bool TryGetAcrylic(NodeHandle h, out AcrylicSpec a) => _acrylics.TryGet((int)h.Raw.Index, out a);
    public void ClearAcrylic(NodeHandle h) { int idx = (int)h.Raw.Index; _acrylics.Remove(idx); MarkRecordDirty(idx); }

    /// <summary>BoxEl.RepaintBoundary: the subtree records into its own retained slice (SceneRecorder's isolation cut).
    /// Equality-gated so an identical re-render marks nothing.</summary>
    /// <param name="down">The boundary's raster DOWNSCALE (1 = full resolution, 2/4/8 = BoxEl.RasterScale 1/2, 1/4, 1/8).</param>
    public void SetRepaintBoundary(NodeHandle h, bool on, byte down = 1, bool compositePose = false)
    {
        int idx = (int)h.Raw.Index;
        bool had = _repaintBoundaries.TryGet(idx, out byte cur);
        if (down < 1) down = 1;
        if (compositePose) down = (byte)(1 | CompositePoseBit);   // a posed image layer has no low-resolution route
        if (had == on && (!on || cur == down)) return;
        if (on) { _flags[idx] |= NodeFlags.SparsePaint; _repaintBoundaries.GetOrAdd(idx) = down; }
        else _repaintBoundaries.Remove(idx);
        MarkRecordDirty(idx);
    }
    public bool IsRepaintBoundary(NodeHandle h) => _repaintBoundaries.TryGet((int)h.Raw.Index, out _);
    /// <summary>The boundary's raster downscale (1 = full); 0 when the node is not a repaint boundary.</summary>
    public byte RepaintBoundaryDown(NodeHandle h) => _repaintBoundaries.TryGet((int)h.Raw.Index, out byte d) ? (byte)(d & DownMask) : (byte)0;
    /// <summary>The boundary column's raw byte (downscale | <see cref="CompositePoseBit"/>) — what the recording snapshot copies.</summary>
    public byte RepaintBoundaryBits(NodeHandle h) => _repaintBoundaries.TryGet((int)h.Raw.Index, out byte d) ? d : (byte)0;
    /// <summary>BoxEl.CompositePose: the boundary is a posed image layer (its pose applies at composite time).</summary>
    public bool IsCompositePose(NodeHandle h) => _repaintBoundaries.TryGet((int)h.Raw.Index, out byte d) && (d & CompositePoseBit) != 0;
    /// <summary>The boundary column's flag bit for BoxEl.CompositePose (the low nibble is the raster downscale 1/2/4/8).</summary>
    public const byte CompositePoseBit = 0x10, DownMask = 0x0F;

    /// <summary>BoxEl.RasterScale → the downscale factor the low-resolution slice route uses (snapped: 1, 2, 4, 8).</summary>
    public static byte RasterDown(float rasterScale)
        => rasterScale >= 0.75f || !float.IsFinite(rasterScale) ? (byte)1 : rasterScale >= 0.375f ? (byte)2 : rasterScale >= 0.1875f ? (byte)4 : (byte)8;

    public void SetEdgeFade(NodeHandle h, in EdgeFadeSpec e)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _edgeFades.GetOrAdd(idx) = e;
        MarkRecordDirty(idx);
    }
    public bool TryGetEdgeFade(NodeHandle h, out EdgeFadeSpec e) => _edgeFades.TryGet((int)h.Raw.Index, out e);
    public void ClearEdgeFade(NodeHandle h) { int idx = (int)h.Raw.Index; _edgeFades.Remove(idx); MarkRecordDirty(idx); }

    public void SetImageEffects(NodeHandle h, in ImageVisualEffects effects)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _imageEffects.GetOrAdd(idx) = effects;
        MarkRecordDirty(idx);
    }
    public bool TryGetImageEffects(NodeHandle h, out ImageVisualEffects effects)
        => _imageEffects.TryGet((int)h.Raw.Index, out effects);
    public void ClearImageEffects(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        _imageEffects.Remove(idx);
        MarkRecordDirty(idx);
    }

    // ── E5-L2 drag-drop columns (BoxEl.Draggable / BoxEl.DropTarget → Input.DragDropContext) ──────
    /// <summary>Set (or clear, null) the node's typed drag-source spec — the reconciler writes it from
    /// <c>BoxEl.Draggable</c>; the L2 context resolves the nearest one up the chain at L1 promotion.</summary>
    public void SetDragSource(NodeHandle h, DragSource? s)
    {
        int idx = (int)h.Raw.Index;
        if (s is null) _dragSources.Remove(idx);
        else _dragSources[idx] = s;
    }

    public bool TryGetDragSource(NodeHandle h, out DragSource? s)
    {
        bool found = _dragSources.TryGetValue((int)h.Raw.Index, out var v);
        s = v;
        return found;
    }

    /// <summary>Set (or clear, null) the node's drop-target spec — the reconciler writes it from
    /// <c>BoxEl.DropTarget</c>; the L2 context walks the hit chain per move for the nearest ACCEPTING one.</summary>
    public void SetDropTarget(NodeHandle h, DropTargetSpec? t)
    {
        int idx = (int)h.Raw.Index;
        if (t is null)
        {
            if (_dropTargets.Remove(idx)) _dropTargetsVersion++;
            _dropSpotlightRoots.Remove(idx);
            return;
        }
        if (_dropTargets.TryGetValue(idx, out var prior) && ReferenceEquals(prior, t)) return;
        _dropTargets[idx] = t;
        _dropTargetsVersion++;
    }

    public bool TryGetDropTarget(NodeHandle h, out DropTargetSpec? t)
    {
        bool found = _dropTargets.TryGetValue((int)h.Raw.Index, out var v);
        t = v;
        return found;
    }

    /// <summary>Cheap per-move gate: any drop target in the scene at all (skips the chain walk for plain reorders).</summary>
    public bool HasDropTargets => _dropTargets.Count > 0;

    /// <summary>Generation of the sparse target registry — bumped when a spec is added, replaced or removed. It is a
    /// CHEAP HINT for the per-move refresh, not the authority on compatibility: the signals-first bound realize path
    /// recycles a row by writing its bind signal, which re-points a live node at a different logical item WITHOUT
    /// touching this column. A live drag therefore also re-collects once per frame through
    /// <c>DragDropContext.SyncSpotlightBeforeRecord</c> (phase 7.8) — never during record.</summary>
    public int DropTargetsVersion => _dropTargetsVersion;

    public bool DropSpotlightActive => _dropSpotlightActive && _dropSpotlightRoots.Count != 0;

    public bool IsDropSpotlightRoot(NodeHandle h)
        => DropSpotlightActive && !h.IsNull && _dropSpotlightRoots.Contains((int)h.Raw.Index);

    /// <summary>Is <paramref name="h"/> inside a compatible spotlight destination? A HOST-facing query with no in-tree
    /// consumer: the scrim needs only the root set itself (<see cref="DropSpotlightRootAt"/>), and the presentation-only
    /// exemption walk that used to call this is deleted along with the per-node dim. Kept because an app that wants to
    /// restyle its own content by spotlight membership has no other way to ask, and it is O(depth) with no state.</summary>
    public bool IsUnderDropSpotlightRoot(NodeHandle h)
    {
        if (!DropSpotlightActive) return false;
        for (var n = h; !n.IsNull; n = Parent(n))
            if (_dropSpotlightRoots.Contains((int)n.Raw.Index)) return true;
        return false;
    }

    /// <summary>How many compatible spotlight destinations the live drag has — the scrim band's cutout count.</summary>
    public int DropSpotlightRootCount => DropSpotlightActive ? _dropSpotlightRoots.Count : 0;

    /// <summary>The i-th compatible spotlight destination (0 &lt;= i &lt; <see cref="DropSpotlightRootCount"/>). The
    /// recorder cuts one rounded window per root out of the scrim band.
    /// <para>The set stores raw INDICES and rebuilds the handle from the current generation, so this can never hand back
    /// a stale handle — which is exactly why <c>FreeSubtree</c> must keep pruning the set as nodes die. Do not "simplify"
    /// that prune away on the theory that the recorder's <c>IsLive</c> check will catch a freed root: it cannot, and the
    /// cutout would then follow whichever unrelated node recycled the index.</para></summary>
    public NodeHandle DropSpotlightRootAt(int i)
    {
        int idx = _dropSpotlightRoots[i];
        return new NodeHandle(new Handle((uint)idx, _gen[idx]));
    }

    public NodeHandle DropSpotlightOver => _dropSpotlightActive ? _dropSpotlightOver : NodeHandle.Null;

    /// <summary>Capability-test every opt-in target and publish only compatible live roots. Called off the record path —
    /// at drag begin, on a <see cref="DropTargetsVersion"/> change, and once per frame at phase 7.8
    /// (<c>DragDropContext.SyncSpotlightBeforeRecord</c>) — so the <c>CanAccept</c>/<c>SpotlightWhen</c> delegates it
    /// invokes run PER FRAME for the life of a drag, not on a cold edge. They must therefore be cheap and
    /// allocation-free (frame phases 6-13 are the 0-alloc region).</summary>
    public void RefreshDropSpotlight(DragSession session)
    {
        _dropSpotlightRoots.Clear();
        foreach (var pair in _dropTargets)
        {
            int idx = pair.Key;
            var spec = pair.Value;
            if (spec.VisualPolicy != DropTargetVisualPolicy.Spotlight || !spec.Accepts(session.Kind)) continue;
            var node = new NodeHandle(new Handle((uint)idx, _gen[idx]));
            if (!IsLive(node) || (_flags[idx] & NodeFlags.Disabled) != 0) continue;
            // Reachability: the hit-test PRUNES any subtree whose root has HitTestVisible cleared (InputDispatcher), so
            // a target beneath one can never become the destination — advertising it punches a scrim cutout at geometry
            // the pointer provably cannot reach. That is the always-mounted-but-hidden layer shape: the sidebar keeps
            // its expanded pane mounted at Opacity 0 / HitTestVisible false while collapsed, and its virtualized rows
            // still write live specs, so the veil used to erase 56-DIP holes over the RAIL's dividers and gaps — bright
            // empty plates on rows that render nothing (B3). O(depth), allocation-free.
            if (!IsHitReachable(node))
            {
                // Fork-closing trace (compiled out of Release, runtime-gated by --fg diag). A target that vanishes from
                // the spotlight is otherwise indistinguishable from one that was never registered, which is exactly how
                // a whole PAGE of dead targets reads as "drag is broken" rather than as "these are unreachable".
                // The Enabled gate is hoisted so the int→object box in Set() cannot land on a drag frame's alloc budget
                // when diagnostics are compiled in but switched off (the default Debug shape the alloc tripwire runs in).
                Diag.Count("input.dragdrop", "spotlight.unreachable");
                if (Diag.CompiledIn && Diag.Enabled) Diag.Set("input.dragdrop", "spotlight.unreachable.last", idx);
                continue;
            }
            if (spec.CanAccept is { } canAccept && !canAccept(session)) continue;
            // A target that is TRANSPARENT for this session accepts nothing and refuses nothing, so it must not
            // advertise itself as a destination either (DropTargetSpec.Transparent).
            if (spec.Transparent is { } transparent && transparent(session)) continue;
            // Per-SESSION policy (DropTargetSpec.SpotlightWhen): a target may opt into the dim in general and still
            // refuse it for THIS gesture — a same-list reorder must not scrim the app it is reordering inside (A14).
            // Evaluated here, on the cold refresh edge, so record stays free of policy delegates.
            if (spec.SpotlightWhen is { } spotlightWhen && !spotlightWhen(session)) continue;
            if (!_dropSpotlightRoots.Contains(idx)) _dropSpotlightRoots.Add(idx);
        }
        _dropSpotlightActive = _dropSpotlightRoots.Count != 0;
        if (!_dropSpotlightActive || !_dropSpotlightRoots.Contains((int)_dropSpotlightOver.Raw.Index))
            _dropSpotlightOver = NodeHandle.Null;
    }

    /// <summary>Can the hit-test reach <paramref name="h"/> at all? Mirrors the dispatcher's subtree prune: one cleared
    /// <see cref="NodeFlags.Visible"/> or <see cref="NodeFlags.HitTestVisible"/> anywhere on the ancestor chain (or on the node itself) makes every node
    /// below it unhittable, and therefore an impossible drop destination.
    /// <para>Reachability is proved by TERMINATION AT <see cref="Root"/>, not by running out of ancestors. The hit test
    /// descends from <c>Root</c> and nowhere else (<c>InputDispatcher.HitTest</c>), so a subtree that is live but no
    /// longer linked to it is unhittable no matter how healthy its flags look. Two shapes reach here and both used to
    /// pass by terminating on a Null parent: a KeepAlive-PARKED page — an inactive tab, which <c>Reconciler</c> parks
    /// with <c>SetSubtreeParked</c> + <c>Detach</c> while deliberately RETAINING <c>HitTestVisible</c>, and whose drop
    /// targets stay registered (the registry is only cleared on node free) — and an exit ORPHAN. Both would otherwise
    /// advertise destinations, punching scrim cutouts at stale last-arranged rects over geometry the pointer provably
    /// cannot reach. Re-attaching (tab reactivation) restores reachability by itself: the filter is recomputed per
    /// refresh and keeps no per-node exclusion state.</para>
    /// Allocation-free; the chain is short.</summary>
    private bool IsHitReachable(NodeHandle h)
    {
        var last = NodeHandle.Null;
        for (var n = h; !n.IsNull; n = Parent(n))
        {
            // Liveness FIRST. LiveIndex THROWS on a dead handle (it never returns a negative — the `idx < 0` guard this
            // replaces was unreachable code), and a throw here escapes RefreshDropSpotlight into DragDropContext.Move,
            // killing the whole gesture instead of filtering one target. Dead ⇒ unreachable, which is what we return.
            if (!IsLive(n)) return false;
            // Both bits, exactly the dispatcher's prune: a presence collapse clears only Visible (SetCollapsed).
            if ((_flags[n.Raw.Index] & (NodeFlags.Visible | NodeFlags.HitTestVisible)) != (NodeFlags.Visible | NodeFlags.HitTestVisible)) return false;
            last = n;
        }
        return last == Root;
    }

    public void SetDropSpotlightOver(NodeHandle node)
        => _dropSpotlightOver = _dropSpotlightActive && IsDropSpotlightRoot(node) ? node : NodeHandle.Null;

    public void ClearDropSpotlight()
    {
        _dropSpotlightRoots.Clear();
        _dropSpotlightOver = NodeHandle.Null;
        _dropSpotlightActive = false;
    }

    // ── UseGesture subscriptions (input-a11y.md §13; the Hooks⇄Input seam) ──────────────────────────────────────
    /// <summary>Cheap census: any node in the scene declared a <c>UseGesture</c> hook (lets the dispatcher skip the
    /// gesture-routing probe entirely when no component subscribes — the common case).</summary>
    public bool HasGestureSubs => _gestureSubs.Count > 0;

    /// <summary>Install / merge one <c>UseGesture</c> handler on a node (input-a11y.md §13). Idempotent per (node, kind):
    /// the latest handler for a kind replaces the prior one (a re-mount re-asserts the same closure); other kinds on the
    /// node are preserved (a component may declare Tap AND Pan). Called by <c>FluentGpu.Hooks.UseGesture</c> on mount.</summary>
    public void SetGestureHandler(NodeHandle h, GestureType kind, Action<GestureEventArgs>? handler)
    {
        int idx = LiveIndex(h);
        ref GestureSubscription s = ref CollectionsMarshal.GetValueRefOrAddDefault(_gestureSubs, idx, out _);
        s.Set(kind, handler);
        // Maintain GestureBit so the node hit-tests (a UseGesture-only node is otherwise non-interactive): set while any
        // handler is installed, cleared when the last one goes (Input opens a gesture arena only over a hit node).
        if (s.HasAny) _interaction[idx].HandlerMask |= InteractionInfo.GestureBit;
        else { _interaction[idx].HandlerMask &= ~(uint)InteractionInfo.GestureBit; _gestureSubs.Remove(idx); }
    }

    /// <summary>True iff the node declared a <c>UseGesture</c> for <paramref name="kind"/> (the dispatcher enrolls a
    /// matching arena member only for declared kinds). Stale/dead handles read false.</summary>
    public bool WantsGesture(NodeHandle h, GestureType kind)
        => _gestureSubs.TryGetValue(LiveIndexOrZero(h), out var s) && s.Handler(kind) is not null;

    /// <summary>The handler for (node, kind), or null. The dispatcher invokes it when the gesture arena resolves the
    /// node's matching member as the winner (§7A.2). Stale/dead handles read null.</summary>
    public Action<GestureEventArgs>? GetGestureHandler(NodeHandle h, GestureType kind)
        => _gestureSubs.TryGetValue(LiveIndexOrZero(h), out var s) ? s.Handler(kind) : null;

    /// <summary>Get-or-create the per-node text measure cache row (layout.md §2.3).</summary>
    public ref TextMeasureCache MeasureCacheRef(NodeHandle h)
    {
        NoteCaptureChanged((int)h.Raw.Index);   // P8: write-intent accessor (the measure pass fills it)
        return ref _measureCache.GetOrAdd((int)h.Raw.Index);
    }

    internal bool TryGetMeasureCache(NodeHandle h, out TextMeasureCache cache)
        => _measureCache.TryGet((int)h.Raw.Index, out cache);

    public NodeHandle FirstChild(NodeHandle h) => Wrap(_firstChild[h.Raw.Index]);
    public NodeHandle NextSibling(NodeHandle h) => Wrap(_nextSib[h.Raw.Index]);
    public NodeHandle Parent(NodeHandle h) => Wrap(_parent[h.Raw.Index]);
    public NodeHandle LastChild(NodeHandle h) => Wrap(_lastChild[h.Raw.Index]);
    public int ChildCount(NodeHandle h) => _childCount[h.Raw.Index];

    /// <summary>Absolute (window-space) rect = local W/H at the summed origin up the parent chain. (Slice uses translation-only transforms.)</summary>
    public RectF AbsoluteRect(NodeHandle h)
    {
        float x = 0f, y = 0f;
        for (var n = h; !n.IsNull; n = Parent(n))
        {
            x += _bounds[n.Raw.Index].X + _paint[n.Raw.Index].LocalTransform.Dx;   // include scroll / composited translation
            y += _bounds[n.Raw.Index].Y + _paint[n.Raw.Index].LocalTransform.Dy;
            var parent = Parent(n);
            if (!parent.IsNull)
            {
                x += _paint[parent.Raw.Index].ChildShiftX;
                y += _paint[parent.Raw.Index].ChildShiftY;
            }
        }
        return new RectF(x, y, _bounds[h.Raw.Index].W, _bounds[h.Raw.Index].H);
    }

    /// <summary>Does the tree say <paramref name="h"/> is on screen: live, linked to <see cref="Root"/> (a KeepAlive-parked
    /// tab or an exit orphan is not), <see cref="NodeFlags.Visible"/> on every node of its chain, no zero
    /// <see cref="NodePaint.Opacity"/> on the chain, and a non-empty box. Clip and scroll culling are NOT considered: a
    /// caller intersects the rect with the window or its viewport itself. Capture tooling (the Store screenshot export)
    /// uses it to drop parked pages and collapsed panes from a keyed-rect dump, whose stale last-arranged rects would
    /// otherwise point at pixels nothing paints. Allocation-free; O(depth).</summary>
    public bool IsShown(NodeHandle h)
    {
        if (h.IsNull) return false;
        ref RectF own = ref _bounds[h.Raw.Index];
        if (own.W <= 0f || own.H <= 0f) return false;
        var last = NodeHandle.Null;
        for (var n = h; !n.IsNull; n = Parent(n))
        {
            if (!IsLive(n)) return false;
            if ((_flags[n.Raw.Index] & NodeFlags.Visible) == 0) return false;
            if (_paint[n.Raw.Index].Opacity <= 0f) return false;
            last = n;
        }
        return last == Root;
    }

    /// <summary>Same walk as <see cref="AbsoluteRect"/>, but refuses (returns <c>false</c>) the moment any node on the
    /// parent chain carries a <see cref="NodePaint.LocalTransform"/> whose scale/skew is not identity. This is the E1
    /// image-repaint path's rect source (<c>Reconciler.AddImageNodeRepaint</c>, damage-scoped-repaint-design.md "Step
    /// 3"): a landing/crossfade band can only describe a plain translated box, so a node under a scaled/rotated
    /// ancestor (or its own scaled transform) must fall back to the caller's named <c>ForceFull(DetachedContent)</c>
    /// instead of emitting a rect that under-covers the actual painted pixels. Zero-alloc, same walk shape as
    /// <see cref="AbsoluteRect"/>.</summary>
    public bool TryAbsoluteRectTranslationOnly(NodeHandle h, out RectF r)
    {
        float x = 0f, y = 0f;
        for (var n = h; !n.IsNull; n = Parent(n))
        {
            var xform = _paint[n.Raw.Index].LocalTransform;
            if (xform.M11 != 1f || xform.M12 != 0f || xform.M21 != 0f || xform.M22 != 1f) { r = default; return false; }
            x += _bounds[n.Raw.Index].X + xform.Dx;
            y += _bounds[n.Raw.Index].Y + xform.Dy;
            var parent = Parent(n);
            if (!parent.IsNull)
            {
                x += _paint[parent.Raw.Index].ChildShiftX;
                y += _paint[parent.Raw.Index].ChildShiftY;
            }
        }
        r = new RectF(x, y, _bounds[h.Raw.Index].W, _bounds[h.Raw.Index].H);
        return true;
    }

    /// <summary>The window-space rect a node actually PAINTS at: its local W/H run through the FULL affine walk the
    /// recorder composes (parent chain, each node's <c>ChildShift</c>, then <c>LocalTransform</c> about the node's
    /// <c>OriginX/OriginY</c>) and boxed. <see cref="AbsoluteRect"/> folds only the translation part, so under a scaled
    /// ancestor (a scale-in entrance, a connected/page transition, a zoom FLIP) it describes a rect that is not where the
    /// node is drawn. Anything that must LINE UP with painted pixels, such as a composited video visual placed against
    /// the punched hole, needs this one. A rotation/skew yields the axis-aligned bounding box (a DirectComposition
    /// placement can express an axis-aligned scale only). A translation-only chain returns exactly what
    /// <see cref="AbsoluteRect"/> does (the same sums, in the same order), so no existing placement moves a bit.
    /// Zero-alloc.</summary>
    public RectF AbsoluteTransformedRect(NodeHandle h)
        => AbsoluteTransformedRect(h, _bounds[h.Raw.Index].W, _bounds[h.Raw.Index].H);

    /// <summary><see cref="AbsoluteTransformedRect(NodeHandle)"/> for an explicit LOCAL size instead of the node's laid-out
    /// W/H: a <see cref="SizeMode.Reveal"/> ancestor presents <c>PresentedW</c>/<c>PresentedH</c> while its bounds stay
    /// final, and a clip walk wants the presented extent mapped through the same transform.</summary>
    public RectF AbsoluteTransformedRect(NodeHandle h, float width, float height)
    {
        if (TryAbsoluteRectTranslationOnly(h, out RectF plain))
            return new RectF(plain.X, plain.Y, width, height);
        return WorldAffine(h).TransformBounds(new RectF(0f, 0f, width, height));
    }

    // node-local -> window affine, composed exactly like the recorder / ConnectedAnimation.WorldTransform: parent world,
    // the parent's ChildShift, the node's origin, then its LocalTransform about (OriginX*W, OriginY*H). Depth-bounded
    // recursion over a handful of ancestors; no allocation.
    private Affine2D WorldAffine(NodeHandle n)
    {
        int i = (int)n.Raw.Index;
        NodeHandle parent = Parent(n);
        Affine2D world = Affine2D.Identity;
        if (!parent.IsNull)
        {
            world = WorldAffine(parent);
            world = world.Translate(_paint[parent.Raw.Index].ChildShiftX, _paint[parent.Raw.Index].ChildShiftY);
        }
        world = world.Translate(_bounds[i].X, _bounds[i].Y);
        Affine2D local = _paint[i].LocalTransform;
        if (!local.IsIdentity)
        {
            float ox = _bounds[i].W * _paint[i].OriginX, oy = _bounds[i].H * _paint[i].OriginY;
            world = world.Translate(ox, oy).Multiply(local).Translate(-ox, -oy);
        }
        return world;
    }

    /// <summary>Same origin walk as <see cref="AbsoluteRect"/> (window-space = summed origin up the parent chain) but
    /// LAYOUT bounds only — no compositor <c>LocalTransform</c>/<c>ChildShiftX/Y</c> folded in. A content-space caller
    /// computing a SCROLL target (offsets live in content space == layout space) needs this: <see cref="AbsoluteRect"/>
    /// is exactly what got PAINTED, and an ancestor mid-FLIP (an Expander host's Reflow, an inline drawer's own
    /// <c>SizeMode.Reflow</c>) skews its LocalTransform/ChildShift on precisely the frame a bring-into-view effect
    /// samples it — so a target derived from painted pixels lands off by whatever delta is in flight that frame. Use
    /// <see cref="AbsoluteRect"/> for anything that must reflect what's actually on screen (hit-testing, a connector
    /// drawn against a sibling); use this for anything feeding a scroll offset. Zero-alloc, same walk shape.</summary>
    public RectF AbsoluteLayoutRect(NodeHandle h)
    {
        float x = 0f, y = 0f;
        for (var n = h; !n.IsNull; n = Parent(n))
        {
            x += _bounds[n.Raw.Index].X;
            y += _bounds[n.Raw.Index].Y;
        }
        return new RectF(x, y, _bounds[h.Raw.Index].W, _bounds[h.Raw.Index].H);
    }

    private NodeHandle Wrap(int idx) => idx == 0 ? NodeHandle.Null : new NodeHandle(new Handle((uint)idx, _gen[idx]));

    /// <summary>Public index → handle wrap: the scroll pose sinks and <c>ScrollBarChrome</c> only ever carry a raw node
    /// index (the scroll runtime is Scene-agnostic), so this is how
    /// they recover a <see cref="NodeHandle"/> to call the ordinary handle-based scene API (<see cref="Mark"/>,
    /// <see cref="Paint"/>, <see cref="Bounds"/>, …) for the one node they DO need a handle for. Stamps the SLOT'S
    /// CURRENT generation, so freeing bumps it out from under a stale index — callers still check
    /// <see cref="IsLive"/> (or, equivalently here, <see cref="NodeFlags.Scrollable"/>) before trusting the result.</summary>
    public NodeHandle HandleAt(int idx) => Wrap(idx);

    // ── evidence names (docs/plans/evidence-diagnostics-implementation.md §A.5) ─────────────────────────────────────
    // The Element.Key of every KEYED node, recorded at mount (the reconciler's Mount — mounts already allocate) and
    // dropped when the node is freed. UI thread only: the render thread stores (index, gen) and the UI resolves names at
    // export / log time (NodeDescriber). Created on the first keyed mount — a snapshot store never pays for it.
    private Dictionary<int, string>? _debugKeys;

    /// <summary>Record <paramref name="node"/>'s <c>Element.Key</c> (UI thread, at mount).</summary>
    public void NoteDebugKey(NodeHandle node, string key)
    {
        if (node.IsNull || key.Length == 0) return;
        (_debugKeys ??= new Dictionary<int, string>(256))[(int)node.Raw.Index] = key;
    }

    /// <summary>The <c>Element.Key</c> <paramref name="node"/> was mounted with, or null when it is unkeyed (UI thread).</summary>
    public string? DebugKeyOf(NodeHandle node)
        => _debugKeys is not null && IsLive(node) && _debugKeys.TryGetValue((int)node.Raw.Index, out var k) ? k : null;

    /// <summary>Every live keyed node and its key (UI thread; the evidence bundle's keyed.tsv — allocates the rows).</summary>
    public void CopyDebugKeys(List<(NodeHandle Node, string Key)> dst)
    {
        if (_debugKeys is null) return;
        foreach (var kv in _debugKeys)
        {
            var h = HandleAt(kv.Key);
            if (!h.IsNull && IsLive(h)) dst.Add((h, kv.Value));
        }
    }

    private void Grow() => ResizeColumns(_gen.Length * 2);

    /// <summary>Resize every parallel SoA column to <paramref name="n"/> slots (grow OR shrink). The single place the
    /// column list lives — <see cref="Grow"/> (×2) and <see cref="TrimExcessCapacity"/> (tail-trim) both route here so
    /// the set can never drift between them.</summary>
    private void ResizeColumns(int n)
    {
        Array.Resize(ref _gen, n); Array.Resize(ref _freeHeap, n);
        Array.Resize(ref _parent, n); Array.Resize(ref _firstChild, n); Array.Resize(ref _lastChild, n);
        Array.Resize(ref _prevSib, n); Array.Resize(ref _nextSib, n); Array.Resize(ref _childCount, n);
        Array.Resize(ref _elementTypeId, n); Array.Resize(ref _layout, n); Array.Resize(ref _bounds, n);
        Array.Resize(ref _paint, n); Array.Resize(ref _dynamicText, n); Array.Resize(ref _interaction, n); Array.Resize(ref _flags, n);
        Array.Resize(ref _aux, n);
        Array.Resize(ref _subtreeVersion, n);   // P4 fix: per-node subtree-content version, parallel to _aux (SceneStore.Aux.cs)
        Array.Resize(ref _recordDirty, n); Array.Resize(ref _recordDirtySelf, n); Array.Resize(ref _recordDirtyDescendant, n);
        Array.Resize(ref _recordDirtyWrote, n); Array.Resize(ref _recordDirtyStamp, n);
        if (_recordDirtyWroteCount > n) _recordDirtyWroteCount = n;
        // P8 capture ledger: sized WITH the columns (like _recordDirtyWrote/_recordDirtyStamp) so a steady frame never
        // grows it - the ledger can hold at most one entry per node, and a lazy doubling mid-frame is a managed
        // allocation in a phase that is required to make none.
        Array.Resize(ref _captureStamp, n); Array.Resize(ref _createdStamp, n);
        Array.Resize(ref _captureWrote, n); Array.Resize(ref _inCaptureList, n);
        if (_captureWroteCount > n) _captureWroteCount = n;
        Array.Resize(ref _click, n); Array.Resize(ref _boundsChanged, n); Array.Resize(ref _boundsChangedHook, n); Array.Resize(ref _boundsDelivered, n); Array.Resize(ref _keyHandler, n); Array.Resize(ref _charHandler, n);
        Array.Resize(ref _pointerDown, n); Array.Resize(ref _drag, n); Array.Resize(ref _hoverMove, n); Array.Resize(ref _pointerMoveWithin, n); Array.Resize(ref _pointerExit, n);
        Array.Resize(ref _pointerPressed, n); Array.Resize(ref _pointerReleased, n); Array.Resize(ref _pointerWheel, n); Array.Resize(ref _contextRequested, n);
        Array.Resize(ref _focusChanged, n);
        Array.Resize(ref _dragStarted, n); Array.Resize(ref _dragDelta, n);
        Array.Resize(ref _dragCompleted, n); Array.Resize(ref _dragCanceled, n);
        NoteCaptureColumnsResized(n);   // P8: a column realloc is not a delta the ledger can describe
    }

    /// <summary>Conservative slab tail-trim (mem-02): the SoA columns only ever GROW (Gen0 churn at the reconcile edge
    /// ratchets <see cref="Capacity"/> to the session high-water and never gives it back). Index-stability is sacred —
    /// live handles MUST keep their indices — so the ONLY legal shrink is cutting the all-free TAIL above the highest
    /// LIVE index. Find that index H; when the slab is mostly empty tail (capacity &gt; 2·(H+1) and past a floor), shrink
    /// every column to the next pow2 ≥ H+1 and drop the freelist entries that fall in the trimmed tail. Returns the slot
    /// count reclaimed (0 = no-op — nothing trimmable, or below the floor). The host calls it on a slow idle cadence;
    /// allocation at trim time (the transient free-set) is acceptable since it runs only when fully idle.</summary>
    public int TrimExcessCapacity()
    {
        int cap = _gen.Length;
        const int BuiltInFloorCap = 256;   // never shrink below this — keeps a sane reusable working set, matches the guard
        // The caller-set floor (0 = none) only ever RAISES the effective floor — it can widen the reusable working
        // set past BuiltInFloorCap, never shrink below it.
        int FloorCap = CapacityFloor > BuiltInFloorCap ? CapacityFloor : BuiltInFloorCap;
        if (cap <= FloorCap) return 0;

        // Free indices below _high are exactly the freelist members; build the set (transient — idle-time only).
        var free = new HashSet<int>(_freeCount);
        for (int i = 0; i < _freeCount; i++) free.Add(_freeHeap[i]);

        // Highest LIVE index: scan down from the high-water, skipping freed slots. (Every index in [1,_high) was
        // allocated at least once, so below-high ⇒ live XOR free; H=0 ⇒ no live nodes at all.)
        int h = 0;
        for (int i = _high - 1; i >= 1; i--)
            if (!free.Contains(i)) { h = i; break; }

        int target = h + 1;                                   // keep slots [0, h] live-addressable
        // Mostly-empty-tail gate: only worth a realloc when the slab is more than double the live span and past the floor.
        if (cap <= 2 * target || cap <= FloorCap) return 0;

        int newCap = 1 << (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(1, target - 1)));
        if (newCap < FloorCap) newCap = FloorCap;
        if (newCap >= cap) return 0;                          // pow2 rounding swallowed the slack — nothing to give back

        ResizeColumns(newCap);
        _high = target;                                       // the tail above H is gone; fresh capacity [target,newCap) is reachable via _high++

        // Rebuild the free heap keeping only entries that survive the trim (index < the new _high). Built from the
        // pre-captured `free` set (NOT from the just-resized _freeHeap — it was cut to newCap positions).
        // Freed slots in [target, newCap) become plain fresh capacity (reachable via _high++); slots ≥ newCap are gone.
        _freeCount = 0;
        foreach (int f in free)
            if (f < target) PushFree(f);

        unchecked { CapacityRevision++; }
        return cap - newCap;
    }

    // ISceneBackend explicit ref returns already satisfied above.
    ref LayoutInput ISceneBackend.Layout(NodeHandle node) => ref _layout[node.Raw.Index];
    ref NodePaint ISceneBackend.Paint(NodeHandle node) => ref _paint[node.Raw.Index];
    ref InteractionInfo ISceneBackend.Interaction(NodeHandle node) => ref _interaction[node.Raw.Index];
}
