using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Render;

/// <summary>The role a slice plays for the node it is cut at (the low bits of <see cref="SliceRow.Sub"/>).</summary>
public enum SliceRole : byte
{
    /// <summary>The node's own slice: the static root, a scroll content root, an effect root.</summary>
    Main = 0,
    /// <summary>A scroll viewport's overlay-scrollbar THUMB, recorded at offset 0 and posed along the track.</summary>
    Thumb = 1,
    /// <summary>A virtual list's recyclable ITEM BAND: the rows after the persistent prefix, clipped + feathered by a
    /// viewport-fixed rect while they ride the content's translation.</summary>
    Band = 2,
    /// <summary>The sticky-pinned children inside the item band's clip (no feather).</summary>
    PinnedBand = 3,
    /// <summary>The node's own group LAYER (edge fade, opacity group, self-blur, acrylic) when the node is itself a slice
    /// root — the scene root, a scroll content root, a sticky/parallax root: cut as an Effect slice beneath the node's Main
    /// slice, whose stream is then just this marker.</summary>
    Layer = 4,
    /// <summary>A sliced edge-fade scroller's CHROME — the edge-cue chevrons, the scrollbar rail and arrows — cut so it trails
    /// the fade's content and is placed over its feather (with <see cref="Thumb"/>: <see cref="SliceRecorder.IsScrollChrome"/>).</summary>
    Chrome = 5,
}

/// <summary>What one slice-recorder pass did (always-on census, zero-alloc).</summary>
/// <param name="Slices">Live slices after the pass.</param>
/// <param name="Walked">Slices whose arena was re-recorded (swapped + walked).</param>
/// <param name="Kept">Slices KEPT whole — their arena untouched, zero bytes written.</param>
/// <param name="EffectSlices">Effect slices registered this pass that spent the effect budget (<see cref="SliceRecorder.EffectSliceCap"/>).</param>
/// <param name="Folded">INLINE (folded) opacity / self-blur / edge-fade group layers in the placed partition — effects
/// recorded inline because the effect budget was spent or because they sat inside another inline layer (read off the
/// arenas' scans in <see cref="SliceRecorder.Place"/>, so a slice kept whole still counts the folds it carries).</param>
/// <param name="BytesRecorded">Bytes written into slice arenas this pass (0 on a composite-only / kept-whole turn).</param>
/// <param name="KeptAll">The root slice was kept whole: nothing was recorded at all — a composite-only turn.</param>
/// <param name="AcrylicSlices">Acrylic surfaces cut as their own slice (bounded separately by <see cref="SliceRecorder.AcrylicSliceCap"/>).</param>
/// <param name="AcrylicFallbacks">Acrylic surfaces that could not be cut and painted their FallbackColor (no frost). Expected 0.</param>
/// <param name="FreeFades">Distributable edge fades cut without spending the effect budget.</param>
/// <param name="SigMissWalks">Slices re-recorded although record-clean because an INPUT of their span changed (inherited
/// opacity, world, clip, chrome — <c>WalkWhy.SigMiss</c>; evidence-diagnostics §A.4).</param>
public readonly record struct SliceRecordStats(int Slices, int Walked, int Kept, int EffectSlices, int Folded, long BytesRecorded, bool KeptAll,
    int AcrylicSlices = 0, int AcrylicFallbacks = 0, int FreeFades = 0, int SigMissWalks = 0);

/// <summary>
/// The recorder partition of the retained tiled content layer (docs/plans/scroll-gpu-retained-tiles-implementation.md
/// §A.2/§A.3, phase P1). The scene walk (<see cref="SceneRecordingContext"/>) records into per-SLICE arenas instead of
/// one stream: a static root, one <see cref="SliceKind.Scroll"/> slice per scroll content root recorded in CONTENT space
/// (the content's posed translate is identity inside its walk), <see cref="SliceKind.Effect"/> slices for nodes with
/// compositor-time parameters (sticky/parallax translation, group opacity, self-blur, edge fade, acrylic; more than
/// <see cref="EffectSliceCap"/> fold back inline), the scrollbar thumb and a virtual list's item band. A parent stream
/// carries a <see cref="DrawOp.CompositeSlice"/> marker in each child slice's paint position.
/// <para><b>Poses are composite parameters.</b> A scroll offset, a sticky/parallax translation or a thumb position never
/// changes a recorded byte: the composite places the slice at its posed offset (<see cref="OwnDelta"/>). So a pure scroll
/// tick records nothing — the host runs a composite-only turn — and a slice whose root is clean is KEPT whole (its arena
/// untouched, zero bytes) instead of span-copied.</para>
/// <para><b>Span validity is per arena buffer.</b> Every arena swap mints a process-unique buffer generation; a span may
/// be copied only out of the arena's PRIOR generation (<see cref="SpanTable.TryGet"/>), so a slice kept for many frames
/// still copies its clean rows when it next re-records.</para>
/// <para><b>The composite plan.</b> <see cref="Place"/> lays the slices out at their current poses WITHOUT writing a
/// stream — each slot's segments, each child at its marker with its accumulated posed offset and composite clip, a group
/// around a non-leaf layer slice, a backdrop before an acrylic slice — and <see cref="BuildComposite"/> turns that plan into
/// the <see cref="CompositeFrame"/> every backend composites (<see cref="IGpuDevice.SubmitComposite"/>). Each slot's
/// arena is scanned once per buffer generation (markers, segment bounds, holes, cross-fades), so a composite-only turn
/// reads cached data only.</para>
/// <para>Owned by whoever owns the matching <see cref="SpanTable"/> (the host's inline pair, the render thread's pair);
/// single-threaded; zero managed allocation once warmed (arrays grow only when a scene first needs more).</para>
/// </summary>
public sealed partial class SliceRecorder
{
    /// <summary>At most this many effect slices are cut per pass; further candidates record inline in their containing
    /// slice (§A.2 — WebRender squashes past 8; our bound is the SliceTable's default cap).</summary>
    public const int EffectSliceCap = 16;

    /// <summary>At most this many ACRYLIC slices are cut per pass — a separate bound from <see cref="EffectSliceCap"/>,
    /// which governs only the foldable kinds (opacity, self-blur, non-distributable edge fade, translation). A frost exists
    /// only as a composite <c>Backdrop</c> item, so an acrylic is never folded inline: past this cap it paints its opaque
    /// FallbackColor (WinUI's no-backdrop answer) and is counted on <see cref="SliceRecordStats.AcrylicFallbacks"/>.</summary>
    public const int AcrylicSliceCap = 16;

    /// <summary>The <c>GpuKnockouts.GroupFades</c> identity control (probe-only; the host mirrors its knockouts here):
    /// every edge fade over child slices composites as a group instead of being distributed.</summary>
    public bool ForceGroupFades { get => Volatile.Read(ref _forceGroupFades); set => Volatile.Write(ref _forceGroupFades, value); }
    private bool _forceGroupFades;

    /// <summary>The <c>GpuKnockouts.StickyClipInPaint</c> identity control (probe-only; the host mirrors its knockouts
    /// here): every <c>.StickyClip</c> records its clip INLINE (the paint route: <see cref="NodePaint.ClipRect"/> as a
    /// PushClip in its slice's stream, re-recorded when it moves) instead of as a composite-time clip on its slice marker.
    /// The two routes are pixel-identical (<c>stickyclip-identity</c>).</summary>
    public bool ForceInlineStickyClip { get => Volatile.Read(ref _forceInlineStickyClip); set => Volatile.Write(ref _forceInlineStickyClip, value); }
    private bool _forceInlineStickyClip;
    // The knockout as the current pass reads it (latched at BeginPass), and as the last recorded pass did: a flip
    // re-walks every sticky-clip chain so each node is cut / baked afresh (a kept slice would keep the old route).
    private bool _passInlineSticky, _recordedInlineSticky;

    /// <summary><see cref="ForceInlineStickyClip"/> as latched for the pass being recorded.</summary>
    internal bool InlineStickyThisPass => _passInlineSticky;

    /// <summary>A scroll slice re-bases its content-space origin once the realized window has walked this far from it, so
    /// slice-space coordinates stay small floats (the poser's own WindowOrigin rule) — a SliceGeometry event.</summary>
    internal const double ScrollRebaseDip = 8192.0;

    /// <summary>Span-index depth below a slice root (pre-order entries for per-tile culling).</summary>
    internal const int SpanIndexDepth = 4;

    internal const int RootSlot = 0;

    /// <summary>Set when the last pass appended a top-band tail (orphans / scrim / ghost / overlays / chip / detached
    /// flies) behind the root's own span in the root arena — the root slice cannot be kept whole on the next pass either.
    /// Lives with the arenas (a render-thread recorder records successive publications through different snapshot
    /// recording contexts).</summary>
    internal bool RootHadTail;

    private static long s_bufGen;
    private static ulong NewGen() => (ulong)Interlocked.Increment(ref s_bufGen);

    /// <summary><c>Posed</c> = a BoxEl.CompositePose image layer: recorded pose-free, composited as one bilinear Image item at
    /// the node's current axis-aligned scale + translate (no tiles).</summary>
    internal enum PoseKind : byte { None, Content, Effect, Thumb, Posed }

    private struct Rec
    {
        public bool Live;
        public int NodeIndex;
        public uint Gen;
        public int Role;
        public SliceKind Kind;
        public PoseKind Pose;
        public int Parent;
        public int FirstChild, LastChild, NextSibling;
        public uint RegFrame;
        public bool Walked;        // re-recorded (swapped) this pass
        // pose model: world = BaseWorld ∘ T(o)·L·T(−o); own delta = posed − free (window DIP)
        public Affine2D BaseWorld;
        public float Ox, Oy;
        public Affine2D FreeLocal;
        public float OwnDx, OwnDy;
        // posed image layer (PoseKind.Posed): this turn's window-DIP affine (posed world ∘ free world⁻¹), the one the last
        // placement damaged for, and the cached eligibility of the stream (exactly one plain image)
        public Affine2D PosedM, LastPosedM;
        public ulong PosedScanGen;
        public byte PosedScan;      // 0 = not scanned for this buffer, 1 = a plain single image, 2 = anything else
        public DrawImageCmd PosedImage;
        public RectF PosedClip;     // the rectangular clips open at the image (free window DIP; Infinite = none)
        // scroll content
        public int VpNode;
        public uint VpGen;
        public bool Horizontal;
        public double Base;
        public ulong ChromeSig;
        public bool HasChrome;
        // thumb: track geometry at record (viewport-local, DIP)
        public float ThumbTravel, ThumbContent, ThumbViewport;
        // marker (containing space) + composite params
        public RectF MarkerClip;
        public int MarkerFlags;
        public PushLayerCmd Layer;
        public AcrylicRecipe Acrylic;
        public RectF Bounds;       // slice-space (pose-free window DIP) subtree bounds
        public bool PoseLocked;    // the stream holds a viewport-fixed op baked at the record-time delta
        // which cut budget the slice spends when registered (effect budget / acrylic budget / a free distributable fade)
        public BudgetClass Budget;
        // a repaint boundary's raster downscale (BoxEl.RasterScale): > 1 = the low-resolution route (no tiles)
        public byte LowRes;
        public bool Screen;   // BoxEl.LayerBlend.Screen: the item composites with CompositeItem.BlendCopy = BlendScreen
        public bool HasFeedback;          // BoxEl.Feedback: the item advances a trail surface (CompositeItem.Feedback*)
        public FeedbackState Feedback;
        // acrylic surfaces this slice's own walk recorded as their FallbackColor plate (carried when the slice is kept)
        public int AcrylicFallbacks;
        // an acrylic slice root: its frosted rect (the node box, containing-slice DIP), corner radii (DIP) and opacity
        public RectF AcrylicRect;
        public CornerRadius4 AcrylicRadii;
        public float AcrylicAlpha;
        // a slice root whose stream is its Layer marker alone: the span input signature (opacity, inherited state, focus /
        // text-edit / scroll colours…) its last walk recorded that marker and the Layer's content under
        public ulong LayerInputSig;
        // a COMPOSITE-TIME sticky clip (CompositeSliceFlags.StickyClip): the node's world in its own walk's space, with
        // which its current NodePaint.ClipRect (the ClipTop pose) becomes the slice's half-plane clip at placement
        public bool Sticky;
        public Affine2D StickyWorld;
        public bool HasLastSticky;
        public RectF LastStickyWc;
        // placement state
        public bool HasLast;
        public float LastAccDx, LastAccDy;
        public RectF LastFootprint;
        public float AccDx, AccDy; // this turn's accumulated delta
        public RectF EffClip;      // this turn's effective composite clip (window DIP)
        public bool Visited;       // reached by this turn's placement
    }

    private readonly struct Baked
    {
        public readonly int NodeIndex;
        public readonly uint Gen;
        public readonly Affine2D Local;
        public readonly int ByteStart;   // where the node's walk stood in the slot's arena: a span copy carries the entry along
        public Baked(int nodeIndex, uint gen, in Affine2D local, int byteStart) { NodeIndex = nodeIndex; Gen = gen; Local = local; ByteStart = byteStart; }
    }

    private Rec[] _recs = new Rec[32];
    private DrawList?[] _arenas = new DrawList?[32];
    private ulong[] _curGen = new ulong[32];
    private ulong[] _priorGen = new ulong[32];
    private SliceSpan[][] _index = new SliceSpan[32][];
    private int[] _indexCount = new int[32];
    // The previous walk's index per slot (double-buffered with the arena): a clean span copied out of the prior buffer
    // carries its prior sub-entries along, so the index — and every culling decision made off it — is a pure function of
    // the scene, never of which subtrees happened to be copied.
    private SliceSpan[][] _indexPrior = new SliceSpan[32][];
    private int[] _indexPriorCount = new int[32];
    // The baked poses a slot's stream holds, double-buffered like the index: a clean span copied out of the prior buffer
    // carries the baked bytes of every scaled / folded effect under it, so it carries their entries too (the effect node's
    // own walk, which adds them, never runs).
    private Baked[][] _baked = new Baked[32][];
    private int[] _bakedCount = new int[32];
    private Baked[][] _bakedPrior = new Baked[32][];
    private int[] _bakedPriorCount = new int[32];
    private int _slotCount;   // high-water of used slots

    private uint _frame;
    private int _walked, _kept, _effects, _folded, _acrylics, _acrylicFallbacks, _freeFades;
    private long _bytes;
    private bool _keptAll;

    // Translation-effect lookup (built per pass from the scene's scroll coverage): 1 = only TransX/TransY rows,
    // 2 = has a scale row (always recorded inline, its pose baked).
    private byte[] _transEffect = [];
    private readonly List<int> _transEffectMarked = new(16);
    // Sticky-clip lookup (the scene's ClipTop rows): true = the node's NodePaint.ClipRect is a scroll pose.
    private bool[] _clipEffect = [];
    private readonly List<int> _clipEffectMarked = new(8);

    // Poses a kept/recorded slice cannot honour this pass (a baked pose moved, a viewport's offset-dependent chrome
    // changed, a pose-locked slice moved, a slice root's posed LINEAR part changed): the recorder blocks their chains.
    private readonly List<NodeHandle> _mustRewalk = new(8);

    public SliceRecorder()
    {
        for (int i = 0; i < _index.Length; i++) { _index[i] = new SliceSpan[16]; _indexPrior[i] = new SliceSpan[16]; _baked[i] = new Baked[4]; _bakedPrior[i] = new Baked[4]; }
    }

    // ── census ────────────────────────────────────────────────────────────────────────────────────────────────────
    public SliceRecordStats LastStats { get; private set; }

    public int LiveSlices
    {
        get { int n = 0; for (int s = 0; s < _slotCount; s++) if (_recs[s].Live) n++; return n; }
    }

    /// <summary>The painter-ordered slice list of the last pass: (node index, gen, role, kind) per slice, depth first
    /// (a parent's segments interleave its children — see <see cref="BuildComposite"/>). Diagnostics / gates.</summary>
    public int CopySliceOrder(Span<(int NodeIndex, uint Gen, SliceRole Role, SliceKind Kind)> dst)
    {
        int n = 0;
        if (_slotCount == 0 || !_recs[RootSlot].Live) return 0;
        Order(RootSlot, dst, ref n);
        return n;
    }

    private void Order(int slot, Span<(int, uint, SliceRole, SliceKind)> dst, ref int n)
    {
        ref Rec r = ref _recs[slot];
        if (n < dst.Length) dst[n] = (r.NodeIndex, r.Gen, (SliceRole)r.Role, r.Kind);
        n++;
        for (int c = r.FirstChild; c >= 0; c = _recs[c].NextSibling) Order(c, dst, ref n);
    }

    /// <summary>The arena bytes of the slice cut at (<paramref name="nodeIndex"/>, <paramref name="gen"/>, role) —
    /// gates/diagnostics (empty when no such live slice).</summary>
    public ReadOnlySpan<byte> SliceBytes(int nodeIndex, uint gen, SliceRole role)
    {
        int s = Find(nodeIndex, gen, (int)role);
        return s < 0 || _arenas[s] is not { } dl ? default : dl.Bytes;
    }

    // ── slots ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private int Find(int nodeIndex, uint gen, int role)
    {
        for (int s = 0; s < _slotCount; s++)
        {
            ref Rec r = ref _recs[s];
            if (r.Live && r.NodeIndex == nodeIndex && r.Gen == gen && r.Role == role) return s;
        }
        return -1;
    }

    private int Allocate()
    {
        for (int s = 1; s < _slotCount; s++) if (!_recs[s].Live) return s;
        if (_slotCount == _recs.Length) Grow(_recs.Length * 2);
        return _slotCount++;
    }

    private void Grow(int n)
    {
        int old = _recs.Length;
        Array.Resize(ref _recs, n);
        Array.Resize(ref _arenas, n);
        Array.Resize(ref _curGen, n);
        Array.Resize(ref _priorGen, n);
        Array.Resize(ref _index, n);
        Array.Resize(ref _indexCount, n);
        Array.Resize(ref _indexPrior, n);
        Array.Resize(ref _indexPriorCount, n);
        Array.Resize(ref _baked, n);
        Array.Resize(ref _bakedCount, n);
        Array.Resize(ref _bakedPrior, n);
        Array.Resize(ref _bakedPriorCount, n);
        for (int i = old; i < n; i++) { _index[i] = new SliceSpan[16]; _indexPrior[i] = new SliceSpan[16]; _baked[i] = new Baked[4]; _bakedPrior[i] = new Baked[4]; }
    }

    /// <summary>The slot for (node, gen, role), created when absent. −1 when that slice was already registered THIS
    /// pass (a node reached twice — the caller records it inline instead).</summary>
    internal int FindOrCreate(int nodeIndex, uint gen, SliceRole role, SliceKind kind)
    {
        int s = Find(nodeIndex, gen, (int)role);
        if (s >= 0)
        {
            if (_recs[s].RegFrame == _frame) return -1;
            _recs[s].Kind = kind;
            _recs[s].LowRes = 0;   // the cut that registers it re-states its raster downscale (SetLowRes)
            _recs[s].Screen = false;   // …and its composite blend (SetScreen)
            _recs[s].HasFeedback = false;   // …and its feedback trail (SetFeedback)
            return s;
        }
        s = Allocate();
        _arenas[s] ??= new DrawList(4096);
        _arenas[s]!.Reset();
        _curGen[s] = NewGen();   // an empty current buffer: nothing can be kept or copied from a fresh slot
        _priorGen[s] = 0;
        _indexCount[s] = 0;
        _indexPriorCount[s] = 0;
        _bakedCount[s] = 0;
        _bakedPriorCount[s] = 0;
        _recs[s] = new Rec
        {
            Live = true, NodeIndex = nodeIndex, Gen = gen, Role = (int)role, Kind = kind, Parent = -1,
            FirstChild = -1, LastChild = -1, NextSibling = -1, RegFrame = 0, Base = double.NaN,
        };
        return s;
    }

    internal DrawList Arena(int slot) => _arenas[slot]!;
    internal ulong CurGen(int slot) => _curGen[slot];
    internal ulong PriorGen(int slot) => _priorGen[slot];
    internal bool IsRegisteredThisPass(int slot) => _recs[slot].RegFrame == _frame;
    internal SliceKind KindOf(int slot) => _recs[slot].Kind;

    /// <summary>Re-record the slice: swap its arena (the old buffer becomes the prior one span copies read) and reset its
    /// per-pass tables. Registers it.</summary>
    internal DrawList BeginWalk(int slot, FluentGpu.Render.Evidence.WalkWhy why = FluentGpu.Render.Evidence.WalkWhy.ParentWalked, uint detail = 0)
    {
        var dl = _arenas[slot]!;
        dl.SwapAndReset();
        _priorGen[slot] = _curGen[slot];
        _curGen[slot] = NewGen();
        ref Rec r = ref _recs[slot];
        r.FirstChild = r.LastChild = -1;
        r.PoseLocked = false;
        r.AcrylicFallbacks = 0;
        r.Walked = true;
        r.RegFrame = _frame;
        (_index[slot], _indexPrior[slot]) = (_indexPrior[slot], _index[slot]);
        _indexPriorCount[slot] = _indexCount[slot];
        _indexCount[slot] = 0;
        (_baked[slot], _bakedPrior[slot]) = (_bakedPrior[slot], _baked[slot]);
        _bakedPriorCount[slot] = _bakedCount[slot];
        _bakedCount[slot] = 0;
        _walked++;
        CountBudget(in r);
        EvNoteWalk(slot, why, detail);   // the walk ledger: which slice re-recorded, and why (evidence-diagnostics §A.4)
        return dl;
    }

    /// <summary>Close a re-recorded slice: its slice-space bounds and the bytes it wrote.</summary>
    internal void EndWalk(int slot, in RectF bounds)
    {
        _recs[slot].Bounds = bounds;
        _bytes += _arenas[slot]!.BytePosition;
        EvWalkBytes(slot, _arenas[slot]!.BytePosition);
    }

    /// <summary>Keep the slice whole (arena untouched) with every descendant slice it contains.</summary>
    internal void Keep(int slot)
    {
        ref Rec r = ref _recs[slot];
        r.RegFrame = _frame;
        r.Walked = false;
        _kept++;
        CountBudget(in r);
        _acrylicFallbacks += r.AcrylicFallbacks;
        for (int c = r.FirstChild; c >= 0; c = _recs[c].NextSibling) Keep(c);
    }

    internal RectF BoundsOf(int slot) => _recs[slot].Bounds;

    /// <summary>The span input signature the walk that just swapped <paramref name="slot"/>'s arena records its Layer marker
    /// (and so its Layer slice's content) under — what <see cref="HoldsOnlyMarker"/> keeps the slice whole on.</summary>
    internal void SetLayerInputSig(int slot, ulong inputSig) => _recs[slot].LayerInputSig = inputSig;

    /// <summary>Does <paramref name="slot"/>'s arena hold exactly ONE op — the marker <paramref name="cmd"/>, byte for byte —
    /// written by a walk under span input signature <paramref name="inputSig"/> (<see cref="SetLayerInputSig"/>)? (A slice
    /// root whose stream is its nested Layer slice's marker alone keeps whole on that.)</summary>
    internal bool HoldsOnlyMarker(int slot, in CompositeSliceCmd cmd, ulong inputSig)
    {
        if (!_recs[slot].Live || _arenas[slot] is not { } dl || _recs[slot].FirstChild < 0
            || _recs[slot].LayerInputSig != inputSig) return false;
        ReadOnlySpan<byte> bytes = dl.Bytes;
        int body = Unsafe.SizeOf<CompositeSliceCmd>();
        if (bytes.Length != sizeof(int) + body || (DrawOp)MemoryMarshal.Read<int>(bytes) != DrawOp.CompositeSlice) return false;
        CompositeSliceCmd held = MemoryMarshal.Read<CompositeSliceCmd>(bytes[sizeof(int)..]);
        return held == cmd;
    }

    internal void AddChild(int parent, int child)
    {
        ref Rec c = ref _recs[child];
        c.Parent = parent;
        c.NextSibling = -1;
        ref Rec p = ref _recs[parent];
        if (p.LastChild < 0) p.FirstChild = child;
        else _recs[p.LastChild].NextSibling = child;
        p.LastChild = child;
    }

    /// <summary>Which bound a registered effect slice counts against (<see cref="SetBudget"/>).</summary>
    internal enum BudgetClass : byte
    {
        /// <summary>A foldable effect (opacity group, self-blur, non-distributable edge fade, sticky/parallax translation):
        /// spends <see cref="EffectSliceCap"/>.</summary>
        Effect,
        /// <summary>An acrylic surface: spends <see cref="AcrylicSliceCap"/>, never the effect budget (a frost is not
        /// foldable — it exists only as a composite Backdrop item).</summary>
        Acrylic,
        /// <summary>An edge fade that passed the record-time distribution test (<see cref="CompositeSliceFlags.DistributeFade"/>):
        /// composited as an analytic per-item feather whenever placement allows, so it spends no budget.</summary>
        FreeFade,
    }

    private void CountBudget(in Rec r)
    {
        if (r.Kind != SliceKind.Effect || (r.Role != (int)SliceRole.Main && r.Role != (int)SliceRole.Layer)) return;
        switch (r.Budget)
        {
            case BudgetClass.Acrylic: _acrylics++; break;
            case BudgetClass.FreeFade: _freeFades++; break;
            default: _effects++; break;
        }
    }

    /// <summary>The cut budget <paramref name="slot"/> spends from now on (set at the cut, before its walk registers it).</summary>
    internal void SetBudget(int slot, BudgetClass budget) => _recs[slot].Budget = budget;
    /// <summary>The slice's raster downscale (0/1 = full resolution, tiled). Set by every cut that registers the slot.</summary>
    internal void SetLowRes(int slot, byte down) => _recs[slot].LowRes = down;
    /// <summary>BoxEl.LayerBlend.Screen on a repaint boundary: its composite item screens onto the back buffer.</summary>
    internal void SetScreen(int slot, bool screen) => _recs[slot].Screen = screen;
    /// <summary>BoxEl.Feedback on a repaint boundary: its composite item is a FEEDBACK item (the backend's trail surface).</summary>
    internal void SetFeedback(int slot, in FeedbackState state) { _recs[slot].HasFeedback = !state.Spec.IsNone; _recs[slot].Feedback = state; }

    /// <summary>The effect budget: may another FOLDABLE effect slice be CUT this pass?</summary>
    internal bool EffectBudgetLeft => _effects < EffectSliceCap;
    /// <summary>The acrylic budget (<see cref="AcrylicSliceCap"/>), separate from the effect budget.</summary>
    internal bool AcrylicBudgetLeft => _acrylics < AcrylicSliceCap;
    internal void NoteFolded() => _folded++;   // (the census Folded is the partition's inline layers — see Place)
    /// <summary>An acrylic surface that could not be cut painted its FallbackColor plate instead (no frost, never a hole).</summary>
    internal void NoteAcrylicFallback(int slot)
    {
        _acrylicFallbacks++;
        if ((uint)slot < (uint)_slotCount) _recs[slot].AcrylicFallbacks++;
    }

    /// <summary>Record the pose model + marker parameters of a slice being cut (the containing walk calls this at the
    /// cut, before the slice's own walk).</summary>
    internal void SetPose(int slot, PoseKind pose, in Affine2D baseWorld, float ox, float oy, in Affine2D freeLocal, float ownDx, float ownDy)
    {
        ref Rec r = ref _recs[slot];
        r.Pose = pose; r.BaseWorld = baseWorld; r.Ox = ox; r.Oy = oy; r.FreeLocal = freeLocal; r.OwnDx = ownDx; r.OwnDy = ownDy;
    }

    internal void SetMarker(int slot, in RectF clip, int flags, in PushLayerCmd layer, in AcrylicRecipe acrylic,
        in RectF acrylicRect = default, in CornerRadius4 acrylicRadii = default, float acrylicAlpha = 1f)
    {
        ref Rec r = ref _recs[slot];
        r.MarkerClip = clip; r.MarkerFlags = flags; r.Layer = layer; r.Acrylic = acrylic;
        r.AcrylicRect = acrylic.IsNone ? default : acrylicRect;
        r.AcrylicRadii = acrylicRadii;
        r.AcrylicAlpha = acrylicAlpha;
    }

    /// <summary>The slice cut at <paramref name="slot"/> carries (or not) its node's sticky clip as a composite-time clip
    /// (<see cref="CompositeSliceFlags.StickyClip"/>); <paramref name="world"/> = the node's world in the space its own
    /// walk records in (what its NodePaint.ClipRect is transformed by: the composition the paint route uses).</summary>
    internal void SetSticky(int slot, bool sticky, in Affine2D world)
    {
        ref Rec r = ref _recs[slot];
        r.Sticky = sticky;
        r.StickyWorld = sticky ? world : default;
        if (!sticky) r.HasLastSticky = false;
    }

    internal void SetScroll(int slot, int vpNode, uint vpGen, bool horizontal, double baseOffset, bool hasChrome, ulong chromeSig)
    {
        ref Rec r = ref _recs[slot];
        r.VpNode = vpNode; r.VpGen = vpGen; r.Horizontal = horizontal; r.Base = baseOffset; r.HasChrome = hasChrome; r.ChromeSig = chromeSig;
    }

    internal void SetThumb(int slot, int vpNode, uint vpGen, bool horizontal, float travel, float content, float viewport)
    {
        ref Rec r = ref _recs[slot];
        r.VpNode = vpNode; r.VpGen = vpGen; r.Horizontal = horizontal; r.ThumbTravel = travel; r.ThumbContent = content; r.ThumbViewport = viewport;
    }

    internal void MarkPoseLocked(int slot) => _recs[slot].PoseLocked = true;

    /// <summary>Pin every slice from <paramref name="fromSlot"/> up to (not including) <paramref name="toSlot"/>: a
    /// stream in <paramref name="toSlot"/> baked content at their record-time offsets (a hoisted hover-elevate card).</summary>
    internal void MarkPoseLockedChain(int fromSlot, int toSlot)
    {
        for (int s = fromSlot, guard = 0; s >= 0 && s != toSlot && guard < 64; s = _recs[s].Parent, guard++)
        {
            if (!_recs[s].Live) break;
            _recs[s].PoseLocked = true;
            if (s == RootSlot) break;
        }
    }

    /// <summary>True when the slot was re-recorded (swapped + walked) in this pass — false when kept whole.</summary>
    internal bool WalkedThisPass(int slot) => _recs[slot].RegFrame == _frame && _recs[slot].Walked;

    /// <summary>The scroll content base a slot recorded under (NaN when never recorded) — kept stable until the realized
    /// window has walked <see cref="ScrollRebaseDip"/> away (then it re-bases).</summary>
    internal double ScrollBase(int slot, double windowOrigin)
    {
        double b = _recs[slot].Base;
        if (double.IsNaN(b) || Math.Abs(windowOrigin - b) > ScrollRebaseDip) return windowOrigin;
        return b;
    }

    // ── per-slot tables filled by the walk ────────────────────────────────────────────────────────────────────────
    internal int BeginIndexEntry(int slot, int byteStart, int sortStart, int depth)
    {
        int n = _indexCount[slot];
        ref SliceSpan[] arr = ref _index[slot];
        if (n == arr.Length) Array.Resize(ref arr, arr.Length * 2);
        arr[n] = new SliceSpan(default, byteStart, 0, sortStart, 0, false, depth);
        _indexCount[slot] = n + 1;
        return n;
    }

    internal void EndIndexEntry(int slot, int entry, in RectF bounds, int byteEnd, int sortEnd, bool hasMarker)
    {
        ref SliceSpan e = ref _index[slot][entry];
        e = new SliceSpan(bounds, e.ByteStart, byteEnd - e.ByteStart, e.SortStart, sortEnd - e.SortStart, hasMarker, e.Depth);
    }

    /// <summary>A clean span of a node at <paramref name="depth"/> was copied out of the slot's prior buffer
    /// (<paramref name="priorByteStart"/>, <paramref name="byteLength"/>) to (<paramref name="byteStart"/>,
    /// <paramref name="sortStart"/>): carry its DESCENDANTS' prior index entries along, shifted — exactly the entries a
    /// fresh walk of that subtree would have produced.</summary>
    internal void CopyIndexFromPrior(int slot, int priorByteStart, int byteLength, int priorSortStart, int byteStart, int sortStart, int depth)
    {
        int count = _indexPriorCount[slot];
        if (count == 0 || byteLength <= 0) return;
        SliceSpan[] prior = _indexPrior[slot];
        int lo = 0, hi = count;   // first entry with ByteStart >= priorByteStart (entries are pre-order: sorted by start)
        while (lo < hi) { int mid = (lo + hi) >> 1; if (prior[mid].ByteStart < priorByteStart) lo = mid + 1; else hi = mid; }
        int end = priorByteStart + byteLength;
        int db = byteStart - priorByteStart, ds = sortStart - priorSortStart;
        for (int i = lo; i < count && prior[i].ByteStart < end; i++)
        {
            SliceSpan e = prior[i];
            if (e.Depth <= depth) continue;   // the copied node's own entry (or a sibling sharing its start) — not a descendant
            int n = _indexCount[slot];
            ref SliceSpan[] arr = ref _index[slot];
            if (n == arr.Length) Array.Resize(ref arr, arr.Length * 2);
            arr[n] = e with { ByteStart = e.ByteStart + db, SortStart = e.SortStart + ds };
            _indexCount[slot] = n + 1;
        }
    }

    internal void AddBaked(int slot, NodeHandle node, in Affine2D local)
        => AppendBaked(slot, new Baked((int)node.Raw.Index, node.Raw.Gen, local, _arenas[slot]!.BytePosition));

    private void AppendBaked(int slot, in Baked b)
    {
        int n = _bakedCount[slot];
        ref Baked[] arr = ref _baked[slot];
        if (n == arr.Length) Array.Resize(ref arr, arr.Length * 2);
        arr[n] = b;
        _bakedCount[slot] = n + 1;
    }

    /// <summary>A clean span was copied out of the slot's prior buffer (<paramref name="priorByteStart"/>,
    /// <paramref name="byteLength"/>) to <paramref name="byteStart"/>: carry along the baked poses recorded inside it,
    /// shifted, so a later move of one still re-records the slice (<see cref="CollectPoseMismatches"/>).</summary>
    internal void CopyBakedFromPrior(int slot, int priorByteStart, int byteLength, int byteStart)
    {
        int count = _bakedPriorCount[slot];
        if (count == 0 || byteLength <= 0) return;
        Baked[] prior = _bakedPrior[slot];
        int lo = 0, hi = count;   // first entry at or after priorByteStart (entries are in walk order: sorted by start)
        while (lo < hi) { int mid = (lo + hi) >> 1; if (prior[mid].ByteStart < priorByteStart) lo = mid + 1; else hi = mid; }
        int end = priorByteStart + byteLength, db = byteStart - priorByteStart;
        for (int i = lo; i < count && prior[i].ByteStart < end; i++)
        {
            ref readonly Baked b = ref prior[i];
            AppendBaked(slot, new Baked(b.NodeIndex, b.Gen, in b.Local, b.ByteStart + db));
        }
    }

    /// <summary>Where the slot's space was last PRESENTED (the last placement's accumulated offset; before any, its
    /// record-time one) — maps a removed/moved node's last-presented extent into the window. False when the slot is gone.</summary>
    internal bool TryPresentedDelta(int slot, out float dx, out float dy)
    {
        dx = dy = 0f;
        if ((uint)slot >= (uint)_slotCount || !_recs[slot].Live) return false;
        ref Rec r = ref _recs[slot];
        if (r.HasLast) { dx = r.LastAccDx; dy = r.LastAccDy; return true; }
        for (int s = slot, guard = 0; s >= 0 && guard < 64; s = _recs[s].Parent, guard++)
        {
            dx += _recs[s].OwnDx; dy += _recs[s].OwnDy;
            if (s == RootSlot) break;
        }
        return true;
    }

    // ── the pass ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open a record pass: stamp the frame, build the translation-effect lookup from the scene's scroll
    /// coverage, ensure the root slot, and collect every node whose recorded pose / offset-dependent chrome no longer
    /// matches the scene (the caller blocks those chains so they re-record — <see cref="MustRewalk"/>).</summary>
    internal void BeginPass(SceneRecordingSnapshot scene)
    {
        if (++_frame == 0) _frame = 1;
        _passInlineSticky = ForceInlineStickyClip;
        _walked = _kept = _effects = _folded = _acrylics = _acrylicFallbacks = _freeFades = 0;
        _sigMissWalks = 0;
        _bytes = 0;
        _keptAll = false;
        for (int i = 0; i < _transEffectMarked.Count; i++)
        {
            int idx = _transEffectMarked[i];
            if ((uint)idx < (uint)_transEffect.Length) _transEffect[idx] = 0;
        }
        _transEffectMarked.Clear();
        for (int i = 0; i < _clipEffectMarked.Count; i++)
        {
            int idx = _clipEffectMarked[i];
            if ((uint)idx < (uint)_clipEffect.Length) _clipEffect[idx] = false;
        }
        _clipEffectMarked.Clear();
        var effects = scene.ScrollCoverage.Effects;
        for (int i = 0; i < effects.Length; i++)
        {
            ScrollEffectRow row = effects[i];
            EffectChannel ch = row.Effect.Channel;
            if (ch == EffectChannel.ClipTop)
            {
                int ci = row.NodeIndex;
                if (ci < 0) continue;
                if (ci >= _clipEffect.Length) Array.Resize(ref _clipEffect, Math.Max(ci + 1, Math.Max(64, _clipEffect.Length * 2)));
                if (!_clipEffect[ci]) { _clipEffect[ci] = true; _clipEffectMarked.Add(ci); }
                continue;
            }
            if (!ScrollEffectEval.IsTransformChannel(ch)) continue;
            int idx = row.NodeIndex;
            if (idx < 0) continue;
            if (idx >= _transEffect.Length) Array.Resize(ref _transEffect, Math.Max(idx + 1, Math.Max(64, _transEffect.Length * 2)));
            byte v = ch == EffectChannel.ScaleXY ? (byte)2 : (byte)1;
            if (_transEffect[idx] == 0) _transEffectMarked.Add(idx);
            if (v > _transEffect[idx]) _transEffect[idx] = v;
        }

        var root = scene.Root;
        if (_slotCount == 0) { _slotCount = 1; _recs[RootSlot] = default; }
        ref Rec r = ref _recs[RootSlot];
        if (!r.Live || r.NodeIndex != (int)root.Raw.Index || r.Gen != root.Raw.Gen)
        {
            // a new scene root: every retained slice is foreign
            for (int s = 0; s < _slotCount; s++) _recs[s].Live = false;
            _arenas[RootSlot] ??= new DrawList(16384);
            _arenas[RootSlot]!.Reset();
            _curGen[RootSlot] = NewGen();
            _priorGen[RootSlot] = 0;
            _indexCount[RootSlot] = _indexPriorCount[RootSlot] = _bakedCount[RootSlot] = _bakedPriorCount[RootSlot] = 0;
            _recs[RootSlot] = new Rec
            {
                Live = true, NodeIndex = (int)root.Raw.Index, Gen = root.Raw.Gen, Role = 0, Kind = SliceKind.Static,
                Parent = -1, FirstChild = -1, LastChild = -1, NextSibling = -1, Base = double.NaN,
            };
        }

        CollectPoseMismatches(scene);
    }

    /// <summary>1 = a translation-only scroll-effect node (cut as an Effect slice); 2 = a transform-effect node with a
    /// scale row (recorded inline, pose baked); 0 = neither.</summary>
    internal byte TranslationEffectClass(int nodeIndex)
        => (uint)nodeIndex < (uint)_transEffect.Length ? _transEffect[nodeIndex] : (byte)0;

    /// <summary>Does <paramref name="nodeIndex"/> carry a <c>.StickyClip</c> (a <see cref="EffectChannel.ClipTop"/> row in
    /// the scene's scroll coverage)? Its <see cref="NodePaint.ClipRect"/> is then a POSE, written every scroll tick and
    /// never marking the node dirty, which the recorder honours as a composite-time clip on the node's slice marker
    /// (<see cref="CompositeSliceFlags.StickyClip"/>), or bakes into its stream and re-records when it moves
    /// (<see cref="BakeClip"/>).</summary>
    internal bool IsStickyClipNode(int nodeIndex)
        => (uint)nodeIndex < (uint)_clipEffect.Length && _clipEffect[nodeIndex];

    // Baked sticky clips: a .StickyClip recorded INLINE (not cut as a composite clip: inside an inline group layer, past
    // the effect budget, on a translation slice root, on a node whose own paint escapes its clip, or under the
    // StickyClipInPaint knockout). Its clip is a pose the sinks no longer mark dirty, so the recorder remembers the value
    // it baked and re-records the node's chain (and damages its subtree) when the posed value moves. Node-indexed and
    // persistent across passes: a clean ancestor span copied from the prior buffer carries the baked bytes along without
    // revisiting the node, so the entry must outlive the pass that wrote it.
    private uint[] _clipBakedGen = [];
    private RectF[] _clipBakedRect = [];
    private uint[] _clipChangedFrame = [];
    private readonly List<int> _clipBaked = new(8);

    /// <summary>The walk recorded <paramref name="node"/>'s sticky clip INLINE at <paramref name="clip"/> (its
    /// NodePaint.ClipRect this pass).</summary>
    internal void BakeClip(NodeHandle node, in RectF clip)
    {
        int idx = (int)node.Raw.Index;
        if (idx >= _clipBakedGen.Length)
        {
            int n = Math.Max(idx + 1, Math.Max(64, _clipBakedGen.Length * 2));
            Array.Resize(ref _clipBakedGen, n);
            Array.Resize(ref _clipBakedRect, n);
            Array.Resize(ref _clipChangedFrame, n);
        }
        if (_clipBakedGen[idx] == 0) _clipBaked.Add(idx);
        _clipBakedGen[idx] = BakedGen(node.Raw.Gen);
        _clipBakedRect[idx] = clip;
    }

    private static uint BakedGen(uint gen) => gen == 0 ? 1u : gen;   // 0 marks a free entry

    /// <summary><paramref name="nodeIndex"/>'s sticky clip is now a composite parameter of its own slice: nothing baked
    /// to watch.</summary>
    internal void UnbakeClip(int nodeIndex)
    {
        if ((uint)nodeIndex >= (uint)_clipBakedGen.Length || _clipBakedGen[nodeIndex] == 0) return;
        _clipBakedGen[nodeIndex] = 0;
        int at = _clipBaked.IndexOf(nodeIndex);
        if (at >= 0) { _clipBaked[at] = _clipBaked[^1]; _clipBaked.RemoveAt(_clipBaked.Count - 1); }
    }

    /// <summary>True when this pass found <paramref name="nodeIndex"/>'s posed sticky clip moved off the value its stream
    /// baked: the walk re-records it and damages its subtree (a paint change no dirty bit carries).</summary>
    internal bool ClipChangedThisPass(int nodeIndex)
        => (uint)nodeIndex < (uint)_clipChangedFrame.Length && _clipChangedFrame[nodeIndex] == _frame;

    /// <summary>Nodes whose chains must re-record this pass (see <see cref="BeginPass"/>).</summary>
    internal ReadOnlySpan<NodeHandle> MustRewalk => CollectionsMarshal.AsSpan(_mustRewalk);

    /// <summary>True when the retained slices can be composited at the scene's current poses without recording: no
    /// baked pose moved, no viewport's offset-dependent chrome changed, no pose-locked slice moved and no slice root's
    /// posed transform left the translation-only class. The render thread's composite-only turn requires it.</summary>
    public bool PosesCompatible(SceneRecordingSnapshot scene)
    {
        if (_slotCount == 0 || !_recs[RootSlot].Live) return false;
        CollectPoseMismatches(scene);
        return _mustRewalk.Count == 0;
    }

    private void CollectPoseMismatches(SceneRecordingSnapshot scene)
    {
        _mustRewalk.Clear();
        if (ForceInlineStickyClip != _recordedInlineSticky)
            for (int i = 0; i < _clipEffectMarked.Count; i++)
            {
                var h = scene.HandleAt(_clipEffectMarked[i]);
                if (!h.IsNull && scene.IsLive(h)) _mustRewalk.Add(h);
            }
        for (int i = _clipBaked.Count - 1; i >= 0; i--)
        {
            int idx = _clipBaked[i];
            var h = scene.HandleAt(idx);
            if (h.IsNull || BakedGen(h.Raw.Gen) != _clipBakedGen[idx] || !scene.IsLive(h))
            {
                _clipBakedGen[idx] = 0;
                _clipBaked[i] = _clipBaked[^1];
                _clipBaked.RemoveAt(_clipBaked.Count - 1);
                continue;
            }
            // Collapsed (itself or under a collapsed ancestor): the walk returns before it can re-bake the node, and the
            // node paints nothing, so a moved clip is no reason to re-record its chain. Kept, not purged: a span stored
            // before the collapse still carries the baked bytes, so the watch resumes the pass it shows again.
            if (!PaintReachable(scene, h)) continue;
            if (scene.Paint(h).ClipRect != _clipBakedRect[idx])
            {
                _clipChangedFrame[idx] = _frame;
                _mustRewalk.Add(h);
            }
        }
        for (int s = 0; s < _slotCount; s++)
        {
            ref Rec r = ref _recs[s];
            if (!r.Live) continue;
            for (int i = 0; i < _bakedCount[s]; i++)
            {
                ref readonly Baked b = ref _baked[s][i];
                var h = scene.HandleAt(b.NodeIndex);
                if (h.IsNull || h.Raw.Gen != b.Gen || !scene.IsLive(h)) continue;
                if (scene.Paint(h).LocalTransform != b.Local) _mustRewalk.Add(h);
            }
            if (r.Pose is PoseKind.Content or PoseKind.Effect)
            {
                var h = scene.HandleAt(r.NodeIndex);
                if (h.IsNull || h.Raw.Gen != r.Gen || !scene.IsLive(h)) continue;
                Affine2D l = scene.Paint(h).LocalTransform;
                if (!SameLinear(in l, in r.FreeLocal)) { _mustRewalk.Add(h); continue; }
                if (r.PoseLocked)
                {
                    OwnDelta(s, scene, out float dx, out float dy);
                    if (dx != r.OwnDx || dy != r.OwnDy) { _mustRewalk.Add(h); continue; }
                }
            }
            if (r.Pose == PoseKind.Posed)
            {
                // a pose the composite cannot honour: not axis-aligned any more, or the stream proved ineligible at placement
                // (more than one image, a rounded clip…) — the node re-records through the ordinary boundary route
                var h = scene.HandleAt(r.NodeIndex);
                if (h.IsNull || h.Raw.Gen != r.Gen || !scene.IsLive(h)) continue;
                Affine2D l = scene.Paint(h).LocalTransform;
                if (l.M12 != 0f || l.M21 != 0f || PoseFallback(r.NodeIndex, r.Gen)) { _mustRewalk.Add(h); continue; }
            }
            if (r.Pose == PoseKind.Content && r.HasChrome)
            {
                var vp = scene.HandleAt(r.VpNode);
                if (vp.IsNull || vp.Raw.Gen != r.VpGen || !scene.IsLive(vp) || !scene.HasScroll(vp)) continue;
                ref readonly ScrollState sc = ref scene.ScrollRef(vp);
                if (ChromeSig(in sc, ShownOffset(scene, vp, in sc)) != r.ChromeSig) _mustRewalk.Add(vp);
            }
        }
    }

    // The record walk's presence cut (SceneRecorder.Walk): a node is reached only when it and every ancestor are Visible.
    private static bool PaintReachable(SceneRecordingSnapshot scene, NodeHandle node)
    {
        for (var n = node; !n.IsNull; n = scene.Parent(n))
            if ((scene.Flags(n) & NodeFlags.Visible) == 0) return false;
        return true;
    }

    /// <summary>Close the pass: retire every slice not registered in it (its arena returns to the pool).</summary>
    internal void EndPass(bool keptAll)
    {
        _keptAll = keptAll;
        _recordedInlineSticky = _passInlineSticky;
        for (int s = 1; s < _slotCount; s++)
        {
            ref Rec r = ref _recs[s];
            if (r.Live && r.RegFrame != _frame) { r.Live = false; r.FirstChild = r.LastChild = r.NextSibling = -1; }
        }
        _recs[RootSlot].RegFrame = _frame;
        LastStats = new SliceRecordStats(LiveSlices, _walked, _kept, _effects, _folded, _bytes, keptAll,
            _acrylics, _acrylicFallbacks, _freeFades, _sigMissWalks);
    }

    /// <summary>A composite-only turn (no pass): the census reports it.</summary>
    /// <remarks>The slice PARTITION is the last pass's (nothing was cut or folded anew), so its effect / fold / acrylic
    /// counts carry forward — the census describes the slices being composited, not merely the work of this turn.</remarks>
    internal void NoteCompositeOnly()
    {
        SliceRecordStats last = LastStats;
        LastStats = new SliceRecordStats(LiveSlices, 0, 0, last.EffectSlices, last.Folded, 0, true,
            last.AcrylicSlices, last.AcrylicFallbacks, last.FreeFades);
    }

    /// <summary>Re-register every child slice whose marker lies in the (already copied) byte range of
    /// <paramref name="dl"/>, in stream order, under <paramref name="parent"/> — a clean span copy carries its child
    /// slices' paint positions, so the slices themselves are kept whole.</summary>
    internal void KeepMarkersIn(DrawList dl, int byteStart, int byteLength, int parent)
    {
        ReadOnlySpan<byte> bytes = dl.Bytes.Slice(byteStart, byteLength);
        int pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes[pos..]);
            pos += sizeof(int);
            if (!RepaintStreamSafety.TryBodySize(op, out int body)) return;
            if (op == DrawOp.CompositeSlice)
            {
                var m = MemoryMarshal.Read<CompositeSliceCmd>(bytes[pos..]);
                int s = Find(m.NodeIndex, m.Gen, m.Sub);
                if (s >= 0 && _recs[s].RegFrame != _frame)
                {
                    AddChild(parent, s);
                    Keep(s);
                }
            }
            pos += body;
        }
    }

    /// <summary>Can every child slice marked in the PRIOR buffer range be kept (all still live, none registered yet this
    /// pass)? A span copy that cannot re-register its children must not be taken.</summary>
    internal bool CanKeepMarkersInPrior(ReadOnlySpan<byte> prior)
    {
        int pos = 0;
        while (pos + sizeof(int) <= prior.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(prior[pos..]);
            pos += sizeof(int);
            if (!RepaintStreamSafety.TryBodySize(op, out int body)) return false;
            if (op == DrawOp.CompositeSlice)
            {
                var m = MemoryMarshal.Read<CompositeSliceCmd>(prior[pos..]);
                int s = Find(m.NodeIndex, m.Gen, m.Sub);
                if (s < 0 || _recs[s].RegFrame == _frame) return false;
            }
            pos += body;
        }
        return true;
    }

    // ── poses ─────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static bool SameLinear(in Affine2D a, in Affine2D b)
        => a.M11 == b.M11 && a.M12 == b.M12 && a.M21 == b.M21 && a.M22 == b.M22;

    /// <summary><c>base ∘ T(o)·L·T(−o)</c> — the recorder's node-world composition.</summary>
    internal static Affine2D Conjugate(in Affine2D baseWorld, in Affine2D local, float ox, float oy)
        => local.IsIdentity ? baseWorld : baseWorld.Translate(ox, oy).Multiply(local).Translate(-ox, -oy);

    /// <summary>The offset a viewport's chrome draws against: the render poser's shown position when this tick posed
    /// it, else the UI frame's offset.</summary>
    internal static float ShownOffset(SceneRecordingSnapshot scene, NodeHandle vp, in ScrollState sc)
        => scene.TryGetPosedOffset(vp, out double posed) ? (float)posed : (float)sc.Offset;

    /// <summary>A hash of every OFFSET-DEPENDENT value a viewport's own recorded chrome carries: the auto edge fade's
    /// per-edge bands and the edge cues' alphas (both ramp over the last 24 DIP at either end and are constant in between).
    /// The thumb position is a composite parameter (the thumb slice), so it is deliberately absent — mid-list, a scroll
    /// never changes this value.</summary>
    internal static ulong ChromeSig(in ScrollState sc, float shown)
    {
        ulong h = 14695981039346656037UL;
        const float runway = 24f;   // SceneRecorder's auto-edge-fade runway and EdgeCueRunwayPx
        if (sc.AutoEdgeFade && sc.AutoEdgeFadeBand > 0.5f)
        {
            float band = sc.AutoEdgeFadeBand;
            float before = shown > 0.5f ? band * Math.Clamp(shown / runway, 0f, 1f) : 0f;
            float pastEnd = sc.Orientation == 1 ? sc.ContentW - (shown + sc.ViewportW) : sc.ContentH - (shown + sc.ViewportH);
            float after = pastEnd > 0.5f ? band * Math.Clamp(pastEnd / runway, 0f, 1f) : 0f;
            h = Mix(Mix(h, before), after);
        }
        if (sc.EdgeCueConfig != 0)
        {
            bool horizontal = sc.Orientation == 1;
            float content = horizontal ? sc.ContentW : sc.ContentH;
            float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
            float aBefore = Math.Clamp(shown / runway, 0f, 1f);
            float aAfter = Math.Clamp((content - (shown + viewport)) / runway, 0f, 1f);
            h = Mix(Mix(h, aBefore), aAfter);
        }
        return h;

        static ulong Mix(ulong h, float v)
        {
            h ^= BitConverter.SingleToUInt32Bits(v);
            return h * 1099511628211UL;
        }
    }

    /// <summary>A slice's OWN posed offset (window DIP) relative to how it was recorded — the composite parameter.</summary>
    internal bool OwnDelta(int slot, SceneRecordingSnapshot scene, out float dx, out float dy)
    {
        ref Rec r = ref _recs[slot];
        dx = dy = 0f;
        switch (r.Pose)
        {
            case PoseKind.Content:
            case PoseKind.Effect:
            {
                var h = scene.HandleAt(r.NodeIndex);
                if (h.IsNull || h.Raw.Gen != r.Gen || !scene.IsLive(h)) { dx = r.OwnDx; dy = r.OwnDy; return false; }
                Affine2D l = scene.Paint(h).LocalTransform;
                if (!SameLinear(in l, in r.FreeLocal)) { dx = r.OwnDx; dy = r.OwnDy; return false; }
                Affine2D posed = Conjugate(in r.BaseWorld, in l, r.Ox, r.Oy);
                Affine2D free = Conjugate(in r.BaseWorld, in r.FreeLocal, r.Ox, r.Oy);
                dx = posed.Dx - free.Dx;
                dy = posed.Dy - free.Dy;
                return true;
            }
            case PoseKind.Thumb:
            {
                var vp = scene.HandleAt(r.VpNode);
                if (vp.IsNull || vp.Raw.Gen != r.VpGen || !scene.IsLive(vp) || !scene.HasScroll(vp)) { dx = r.OwnDx; dy = r.OwnDy; return false; }
                ref readonly ScrollState sc = ref scene.ScrollRef(vp);
                float shown = ShownOffset(scene, vp, in sc);
                float d = Math.Clamp(shown / MathF.Max(r.ThumbContent - r.ThumbViewport, 1f), 0f, 1f) * r.ThumbTravel;
                float lx = r.Horizontal ? d : 0f, ly = r.Horizontal ? 0f : d;
                dx = r.BaseWorld.M11 * lx + r.BaseWorld.M21 * ly;
                dy = r.BaseWorld.M12 * lx + r.BaseWorld.M22 * ly;
                return true;
            }
            default:
                return true;
        }
    }

    // ── posed image layers (BoxEl.CompositePose) ──────────────────────────────────────────────────────────────────────

    // Nodes whose posed stream proved ineligible at placement (node-indexed, gen-stamped like the baked-clip table): the next
    // pass cuts them through the ordinary boundary route (pose baked, tiles) — identical pixels, counted.
    private uint[] _poseFallbackGen = [];
    private int _poseFallbacks;

    /// <summary>Posed slices that fell back to the ordinary tiled boundary route since this recorder was created (a gate).</summary>
    public int PoseFallbacks => _poseFallbacks;
    /// <summary>The Image composite items of the last <see cref="BuildComposite"/> (posed layers drawn without tiles).</summary>
    public int LastPosedImages { get; private set; }

    /// <summary>Did this node's posed stream prove ineligible (so the recorder cuts it as a plain boundary)?</summary>
    internal bool PoseFallback(int nodeIndex, uint gen)
        => (uint)nodeIndex < (uint)_poseFallbackGen.Length && _poseFallbackGen[nodeIndex] == BakedGen(gen);

    private void NotePoseFallback(int nodeIndex, uint gen)
    {
        if (nodeIndex >= _poseFallbackGen.Length) Array.Resize(ref _poseFallbackGen, Math.Max(nodeIndex + 1, Math.Max(64, _poseFallbackGen.Length * 2)));
        if (_poseFallbackGen[nodeIndex] == BakedGen(gen)) return;
        _poseFallbackGen[nodeIndex] = BakedGen(gen);
        _poseFallbacks++;
    }

    /// <summary>The window-DIP affine a posed slice composites with this turn: <c>posed world ∘ free world⁻¹</c> (the stream is
    /// recorded at the free world). False when the pose or the base world is not axis-aligned (the slice must re-record
    /// through the ordinary route); <paramref name="m"/> is then identity.</summary>
    private bool PosedAffine(int slot, SceneRecordingSnapshot scene, out Affine2D m)
    {
        m = Affine2D.Identity;
        ref Rec r = ref _recs[slot];
        var h = scene.HandleAt(r.NodeIndex);
        if (h.IsNull || h.Raw.Gen != r.Gen || !scene.IsLive(h)) { m = r.PosedM; return false; }
        Affine2D l = scene.Paint(h).LocalTransform;
        Affine2D free = r.BaseWorld;
        if (l.M12 != 0f || l.M21 != 0f || free.M12 != 0f || free.M21 != 0f || free.M11 == 0f || free.M22 == 0f) return false;
        Affine2D posed = Conjugate(in r.BaseWorld, in l, r.Ox, r.Oy);
        // free⁻¹ for an axis-aligned matrix
        float i11 = 1f / free.M11, i22 = 1f / free.M22;
        var inv = new Affine2D(i11, 0f, 0f, i22, -free.Dx * i11, -free.Dy * i22);
        m = posed.Multiply(in inv);
        return float.IsFinite(m.M11) && float.IsFinite(m.M22) && float.IsFinite(m.Dx) && float.IsFinite(m.Dy);
    }

    /// <summary>Is the posed slice's stream exactly one plain image — optionally under rectangular clips — that the composite
    /// can draw as ONE bilinear quad? No corner radii, no overlay / mask / saturation, no layers, no second paint op, no child
    /// slice. Cached per arena buffer.</summary>
    private bool AnalyzePosedImage(int slot, out DrawImageCmd image, out RectF clip)
    {
        ref Rec r = ref _recs[slot];
        if (r.PosedScanGen != _curGen[slot] || r.PosedScan == 0)
        {
            r.PosedScanGen = _curGen[slot];
            r.PosedScan = ScanPosedImage(slot, out r.PosedImage, out r.PosedClip) ? (byte)1 : (byte)2;
        }
        image = r.PosedImage;
        clip = r.PosedClip;
        return r.PosedScan == 1;
    }

    private bool ScanPosedImage(int slot, out DrawImageCmd image, out RectF clipAtImage)
    {
        image = default;
        clipAtImage = RectF.Infinite;
        if (_arenas[slot] is not { } dl) return false;
        ReadOnlySpan<byte> bytes = dl.Bytes;
        int pos = 0, images = 0;
        RectF top = RectF.Infinite;
        Span<RectF> stack = stackalloc RectF[8];
        int depth = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes[pos..]);
            int payload = pos + sizeof(int);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || payload + body > bytes.Length) return false;
            ReadOnlySpan<byte> p = bytes.Slice(payload, body);
            switch (op)
            {
                case DrawOp.PushClip:
                {
                    var c = MemoryMarshal.Read<ClipCmd>(p);
                    if (c.CornerRadius > 0f || depth == stack.Length) return false;
                    top = top.IsInfinite ? c.DeviceRect : c.DeviceRect.Intersect(top);
                    stack[depth++] = top;
                    break;
                }
                case DrawOp.PopClip:
                    if (depth == 0) return false;
                    depth--;
                    top = depth > 0 ? stack[depth - 1] : RectF.Infinite;
                    break;
                case DrawOp.DrawImage:
                {
                    var im = MemoryMarshal.Read<DrawImageCmd>(p);
                    if (++images > 1) return false;
                    if (im.Radii.TopLeft > 0f || im.Radii.TopRight > 0f || im.Radii.BottomRight > 0f || im.Radii.BottomLeft > 0f
                        || im.Overlay.A > 0f || im.MaskEdges != 0 || im.Saturation != 1f
                        || im.Transform.M12 != 0f || im.Transform.M21 != 0f) return false;
                    image = im;
                    clipAtImage = top;
                    break;
                }
                default:
                    return false;   // any other paint, a layer, a stencil clip, a child slice marker, a blend change…
            }
            pos = payload + body;
        }
        return images == 1 && depth == 0;
    }

    // ── the per-slot scan (rebuilt only when the slot's arena buffer changes) ─────────────────────────────────────────

    /// <summary>A child slice marker in a slot's stream: its byte range, payload, and the innermost ROUNDED clip open in
    /// the containing stream at that point (slot space; radius 0 = none).</summary>
    private struct ScanMark { public int ByteStart, ByteEnd; public CompositeSliceCmd Cmd; public RectF RoundRect; public float RoundR; }
    /// <summary>A painter-ordered SEGMENT of a slot's stream (the bytes between two markers): its byte range, the
    /// commands in it and the union of their painted footprints (slot space DIP, clipped by the in-stream clips).</summary>
    /// <summary>A scanned segment. <see cref="Opaque"/> (slot space DIP) is the largest rect its stream paints FULLY OPAQUE
    /// (a solid fill of alpha 1, no corner radius, axis-aligned, outside every layer and non-rectangular clip, cut by the
    /// rectangular clips): what it hides of the items composited before it. Empty = none known.</summary>
    private struct ScanSeg { public int ByteStart, ByteEnd, Commands; public RectF Bounds, Opaque; }
    /// <summary>A video hole a segment punched: its rect (slot space DIP, cut by the in-stream clips) and where its composite
    /// erase goes relative to that segment (<see cref="VideoHoleErase.Order"/> — on the tile, before it; inside an inline
    /// group layer, after it). The composite erase must be the SAME shape and strength as the in-tile punch (F078), so the scan
    /// also keeps the hole's own uncut rect (<see cref="Hole"/>), its per-corner radii in window DIP (<see cref="Radii"/>, scaled
    /// by the placing transform), its erase strength (<see cref="Strength"/> = VideoReady x Opacity, the factor the in-tile
    /// DestOut multiplies) and the innermost in-stream rounded clip open at the op (<see cref="RoundRect"/>/<see cref="RoundR"/>).</summary>
    private struct ScanVideo
    {
        public int Seg; public RectF Rect; public VideoEraseOrder Order;
        public RectF Hole; public CornerRadius4 Radii; public float Strength; public RectF RoundRect; public float RoundR;
        public int SurfaceId;   // the registry slot token the DrawVideo carries (F070: keys the posed hole the video placement follows)
        public RectF After;     // F087: the union of the painted bounds (slot space DIP) of every op recorded AFTER this DrawVideo in its segment
    }
    private struct ScanFade { public int Seg; public RectF Rect; public float Start, End; }

    private ulong[] _scanGen = new ulong[32];
    private ulong[] _scanHash = new ulong[32];
    private ScanMark[][] _scanMarks = new ScanMark[32][];
    private int[] _scanMarkCount = new int[32];
    private ScanSeg[][] _scanSegs = new ScanSeg[32][];
    private ScanVideo[][] _scanVideos = new ScanVideo[32][];
    private int[] _scanVideoCount = new int[32];
    private ScanFade[][] _scanFades = new ScanFade[32][];
    private int[] _scanFadeCount = new int[32];
    private int[] _scanInline = new int[32];   // inline (folded) opacity / blur / edge-fade PushLayers in the slot's arena
    private readonly List<(RectF Rect, RectF Round, float R)> _scanClip = new(32);

    private void EnsureScanStorage(int n)
    {
        if (_scanGen.Length >= n) return;
        Array.Resize(ref _scanGen, n);
        Array.Resize(ref _scanHash, n);
        Array.Resize(ref _scanMarks, n);
        Array.Resize(ref _scanMarkCount, n);
        Array.Resize(ref _scanSegs, n);
        Array.Resize(ref _scanVideos, n);
        Array.Resize(ref _scanVideoCount, n);
        Array.Resize(ref _scanFades, n);
        Array.Resize(ref _scanFadeCount, n);
        Array.Resize(ref _scanInline, n);
    }

    /// <summary>Scan one slot's arena ONCE per buffer generation: its markers (+ the rounded clip open at each), its
    /// segments' painted bounds and command counts, its video holes, its image cross-fade windows — and, in the same pass,
    /// the arena's content hash (the fold of every op's <see cref="FluentGpu.Render.Evidence.TileContentHash.OpHash"/>) and the
    /// per-op content table the tiles' wants fold (content-derived validity, <c>SliceRecorder.Content.cs</c>). A kept slice keeps
    /// its scan — a composite-only turn reads only cached data.</summary>
    private void ScanSlot(int s)
    {
        EnsureScanStorage(_recs.Length);
        if (_scanGen[s] == _curGen[s] && _scanSegs[s] is not null) return;
        _scanGen[s] = _curGen[s];
        _scanMarks[s] ??= new ScanMark[8];
        _scanSegs[s] ??= new ScanSeg[9];
        _scanVideos[s] ??= new ScanVideo[2];
        _scanFades[s] ??= new ScanFade[4];
        int marks = 0, videos = 0, fades = 0, inline = 0;
        var layers = default(InlineLayerNesting);   // the inline group layers open at each op (a video hole's erase order)
        _scanClip.Clear();
        ReadOnlySpan<byte> bytes = _arenas[s]!.Bytes;
        ulong whole = FluentGpu.Render.Evidence.TileContentHash.Empty;
        ContentScanBegin(s);
        int pos = 0, segStart = 0, cmds = 0;
        RectF segBounds = default, segOpaque = default;
        bool segVideo = false;
        int stencils = 0;   // stencil (path) clips open: nothing inside them is a known opaque rect
        RectF top = RectF.Infinite, round = default;
        float roundR = 0f;
        // The span-index entries open at each op (pre-order, properly nested, at most SpanIndexDepth deep): every footprint
        // below grows them, so the per-tile replay's entry cull keeps whatever a tile's want counted (GrowIndex).
        Span<int> openEntries = stackalloc int[SpanIndexDepth];
        int openCount = 0, nextEntry = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes[pos..]);
            int payload = pos + sizeof(int);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || payload + body > bytes.Length) break;
            ReadOnlySpan<byte> p = bytes.Slice(payload, body);
            ulong oh = FluentGpu.Render.Evidence.TileContentHash.OpHash(bytes.Slice(pos, sizeof(int) + body));
            whole = FluentGpu.Render.Evidence.TileContentHash.Fold(whole, oh);
            bool layerPush = false, layerSpread = false;
            float layerReach = 0f;
            switch (op)
            {
                case DrawOp.PushClip:
                {
                    var c = MemoryMarshal.Read<ClipCmd>(p);
                    RectF r = top.IsInfinite ? c.DeviceRect : c.DeviceRect.Intersect(top);
                    if (c.CornerRadius > 0f) { round = c.RoundedRect; roundR = c.CornerRadius; }
                    _scanClip.Add((r, round, roundR));
                    top = r;
                    ContentScanOp(s, pos, ClipSlack(in r), oh, scope: true, clip: true);
                    break;
                }
                case DrawOp.PushLayer:
                {
                    var lc = MemoryMarshal.Read<PushLayerCmd>(p);
                    int kind = lc.Kind;
                    if (kind != (int)LayerKind.Acrylic) inline++;
                    layers.Push(kind);
                    layerPush = true;
                    // a blurred group composites each pixel from its content AROUND it: no sub-tile repaint bounds it
                    layerSpread = (kind == (int)LayerKind.Blur || kind == (int)LayerKind.EdgeFade) && lc.BlurSigma > 0f;
                    if (layerSpread) layerReach = SelfBlurRegion.TapRadius(lc.BlurSigma);
                    goto default;
                }
                case DrawOp.PushStencilClip:
                {
                    var c = MemoryMarshal.Read<PushStencilClipCmd>(p);
                    RectF r = top.IsInfinite ? c.DeviceRect : c.DeviceRect.Intersect(top);
                    stencils++;
                    _scanClip.Add((r, round, roundR));
                    top = r;
                    ContentScanOp(s, pos, ClipSlack(in r), oh, scope: true, clip: true);
                    break;
                }
                case DrawOp.PopClip:
                case DrawOp.PopStencilClip:
                    if (op == DrawOp.PopStencilClip && stencils > 0) stencils--;
                    if (_scanClip.Count > 0) _scanClip.RemoveAt(_scanClip.Count - 1);
                    if (_scanClip.Count > 0) { var e = _scanClip[^1]; top = e.Rect; round = e.Round; roundR = e.R; }
                    else { top = RectF.Infinite; round = default; roundR = 0f; }
                    ContentScanPop();
                    break;
                case DrawOp.PopLayer:
                    layers.Pop();
                    ContentScanPop();
                    break;
                case DrawOp.SetBlend:
                    // the blend every following op is drawn with: part of their content (ContentScanOp folds it in)
                    ContentScanBlend(MemoryMarshal.Read<SetBlendCmd>(p).Mode == (int)PaintBlend.Additive);
                    break;
                case DrawOp.CompositeSlice:
                {
                    AddSeg(s, marks, segStart, pos, cmds, segBounds, segVideo ? default : segOpaque);
                    if (marks == _scanMarks[s].Length) Array.Resize(ref _scanMarks[s], marks * 2);
                    _scanMarks[s][marks++] = new ScanMark
                    {
                        ByteStart = pos, ByteEnd = payload + body, Cmd = MemoryMarshal.Read<CompositeSliceCmd>(p),
                        RoundRect = round, RoundR = roundR,
                    };
                    ContentScanSegment(s, marks);   // the scopes still open where segment `marks` starts
                    segStart = payload + body;
                    segBounds = default; segOpaque = default; segVideo = false;
                    cmds = 0;
                    pos = payload + body;
                    continue;
                }
                default:
                    if (SliceOpBounds.TryGet(op, p, out RectF b))
                    {
                        // cut by the clip as the backend's scissor draws it: rounded OUT to whole device px (ClipSlack)
                        if (!top.IsInfinite) b = b.Intersect(ClipSlack(in top));
                        // An INVISIBLE fill (opacity 0, or a solid colour of alpha 0 — a dimming plate parked at rest, a
                        // transparent hit plate) paints nothing: it must not stretch the segment's painted bounds, or a
                        // full-window plate makes its segment hold a window of empty tiles. Its bytes stay in the stream and
                        // its content scan below is unchanged; the day it becomes visible its bytes change and it is scanned in.
                        if (!b.IsEmpty && !InvisibleFill(op, p))
                        {
                            segBounds = Union(segBounds, b);
                            GrowIndex(s, pos, in b, openEntries, ref openCount, ref nextEntry);
                        }
                        // An ADDITIVE fill (SetBlend, colour ONE/ONE, alpha ZERO/ONE) leaves the tile's alpha as it found it:
                        // over the transparent clear it composites as page + glow, so it hides nothing beneath it.
                        if (layers.Depth == 0 && stencils == 0 && roundR <= 0f && !_cBlendAdditive && OpaqueFill(op, p, in top, out RectF o)
                            && o.W * o.H > segOpaque.W * segOpaque.H)
                            segOpaque = o;
                        ContentScanOp(s, pos, in b, oh, layerPush, spread: layerSpread, reach: layerReach);
                        // F087: this op paints after every hole already seen in the current segment, so it may cover them (the hole's
                        // own DrawVideo is added below, after this, and never counts against itself).
                        if (!b.IsEmpty)
                            for (int vi = videos - 1; vi >= 0 && _scanVideos[s][vi].Seg == marks; vi--)
                                _scanVideos[s][vi].After = Union(_scanVideos[s][vi].After, b);
                        if (op == DrawOp.DrawVideo)
                        {
                            var v = MemoryMarshal.Read<DrawVideoCmd>(p);
                            RectF hole = v.Transform.TransformBounds(v.Dst);
                            RectF wr = top.IsInfinite ? hole : hole.Intersect(top);
                            if (v.VideoReady > 0f && v.Opacity > 0f && !wr.IsEmpty)
                            {
                                if (videos == _scanVideos[s].Length) Array.Resize(ref _scanVideos[s], videos * 2);
                                // The radii ride the node-local rect, so the placing transform's scale carries them to window DIP.
                                float rs = MathF.Sqrt(MathF.Abs(v.Transform.M11 * v.Transform.M22 - v.Transform.M12 * v.Transform.M21));
                                _scanVideos[s][videos++] = new ScanVideo
                                {
                                    Seg = marks, Rect = wr, Order = VideoHoleErase.Order(layers.InGroup),
                                    Hole = hole,
                                    Radii = new CornerRadius4(v.Radii.TopLeft * rs, v.Radii.TopRight * rs, v.Radii.BottomRight * rs, v.Radii.BottomLeft * rs),
                                    Strength = Math.Clamp(v.VideoReady, 0f, 1f) * Math.Clamp(v.Opacity, 0f, 1f),
                                    RoundRect = round, RoundR = roundR,
                                    SurfaceId = v.SurfaceId,
                                };
                                segVideo = true;   // a video hole erases its segment's pixels: no opaque claim
                                WarnFadedVideo(v.Opacity);
                            }
                        }
                        else if (op == DrawOp.DrawImage)
                        {
                            var im = MemoryMarshal.Read<DrawImageCmd>(p);
                            if (im.FadeDurationMs > 0f && !float.IsNaN(im.FadeStartMs) && !b.IsEmpty)
                            {
                                if (fades == _scanFades[s].Length) Array.Resize(ref _scanFades[s], fades * 2);
                                _scanFades[s][fades++] = new ScanFade { Seg = marks, Rect = b, Start = im.FadeStartMs, End = im.FadeStartMs + im.FadeDurationMs };
                            }
                        }
                    }
                    else if (layerPush) ContentScanOp(s, pos, in top, oh, scope: true, spread: layerSpread, reach: layerReach);   // an extent-unknown layer: all it encloses
                    break;
            }
            cmds++;
            pos = payload + body;
        }
        if (pos < bytes.Length)
            whole = FluentGpu.Render.Evidence.TileContentHash.Fold(whole, FluentGpu.Render.Evidence.TileContentHash.OpHash(bytes[pos..]));
        _scanHash[s] = FluentGpu.Render.Evidence.TileContentHash.Fold(whole, (ulong)bytes.Length);
        AddSeg(s, marks, segStart, bytes.Length, cmds, segBounds, segVideo ? default : segOpaque);
        _scanMarkCount[s] = marks;
        _scanVideoCount[s] = videos;
        _scanFadeCount[s] = fades;
        _scanInline[s] = inline;
    }

    /// <summary>Grow every span-index entry of slot <paramref name="s"/> holding the op at <paramref name="pos"/> by its
    /// footprint <paramref name="b"/>. An entry is recorded with its subtree's BOXES, but a tile's want counts each op by
    /// its <see cref="SliceOpBounds"/> footprint — a glyph run's reaches a whole halo past its node box — and the per-tile
    /// replay skips an entry that misses the tile: an entry short of a footprint drops a run the tile counted and freezes
    /// the cut-off ink into a tile marked valid. Pre-order entries nest, so the ones open at <paramref name="pos"/> are a
    /// stack (<paramref name="open"/>) the ascending scan pops and pushes.</summary>
    private void GrowIndex(int s, int pos, in RectF b, Span<int> open, ref int openCount, ref int next)
    {
        SliceSpan[] index = _index[s];
        int count = _indexCount[s];
        while (openCount > 0 && index[open[openCount - 1]].ByteStart + index[open[openCount - 1]].ByteLength <= pos) openCount--;
        for (; next < count && index[next].ByteStart <= pos; next++)
            if (index[next].ByteStart + index[next].ByteLength > pos && openCount < open.Length) open[openCount++] = next;
        for (int k = 0; k < openCount; k++)
        {
            ref SliceSpan en = ref index[open[k]];
            en = en with { Bounds = Union(en.Bounds, b) };
        }
    }

    /// <summary>How far past a clip's DIP rect the backend's scissor can reach: it rounds the clip OUT to whole device px
    /// (ToScissor), so a primitive just outside the rect can still paint the scissor's partial edge pixel. One DIP covers
    /// that at any scale ≥ 1 (the decode-time cull's CullSafetyDip makes the same assumption).</summary>
    private const float ClipSlackDip = 1f;

    /// <summary><paramref name="clip"/> grown by <see cref="ClipSlackDip"/>: what an op's footprint is cut by, so the per-op
    /// content table (wants, sub-tile damage) never drops a pixel the rounded-out scissor lets an op paint.</summary>
    private static RectF ClipSlack(in RectF clip)
        => clip.IsInfinite || clip.IsEmpty ? clip
            : new RectF(clip.X - ClipSlackDip, clip.Y - ClipSlackDip, clip.W + 2f * ClipSlackDip, clip.H + 2f * ClipSlackDip);

    private static int s_fadedVideoWarned;

    /// <summary>Debug guard (F078): a DrawVideo recorded with opacity below 1. The composite erase now follows the opacity, but
    /// the DirectComposition video visual has no opacity of its own and stays at full strength, so the video would show at 100%
    /// through a partly faded card. One loud line per process; Wavee avoids opacity ancestors of a video hole by convention.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void WarnFadedVideo(float opacity)
    {
        if (opacity >= 0.999f || Interlocked.Exchange(ref s_fadedVideoWarned, 1) != 0) return;
        FluentGpu.Foundation.Diag.Line($"[video] DrawVideo recorded with opacity {opacity:0.##} < 1: the video visual cannot fade, so the video stays at full strength under a faded hole (gpu-renderer.md 7.3)");
    }

    private void AddSeg(int s, int k, int start, int end, int cmds, in RectF bounds, in RectF opaque)
    {
        if (k >= _scanSegs[s].Length) Array.Resize(ref _scanSegs[s], Math.Max(k + 1, _scanSegs[s].Length * 2));
        _scanSegs[s][k] = new ScanSeg { ByteStart = start, ByteEnd = end, Commands = cmds, Bounds = bounds, Opaque = opaque };
    }

    /// <summary>Recorded commands across every live slice arena (the frame's command census).</summary>
    public int TotalCommandCount
    {
        get
        {
            int n = 0;
            for (int s = 0; s < _slotCount; s++) if (_recs[s].Live && _arenas[s] is { } dl) n += dl.CommandCount;
            return n;
        }
    }

    // ── placement (the composite plan) ────────────────────────────────────────────────────────────────────────────

    private enum PlanKind : byte { Seg, GroupOpen, GroupClose, Backdrop, Video }

    /// <summary>One painter-ordered composite-plan entry: a slot's SEGMENT at its accumulated posed offset under the
    /// composite clip its ancestors impose (+ the leaf layer it carries), a GROUP open/close around a non-leaf layer
    /// slice, an ACRYLIC backdrop, or a VIDEO hole.</summary>
    private struct Plan
    {
        public PlanKind Kind;
        public int Slot, Segment;
        public float AccDx, AccDy;
        public RectF Clip;
        public RectF RoundRect;
        public float RoundR;
        public bool HasLayer;
        public PushLayerCmd Layer;   // window DIP, at the posed offset
        public RectF InnerClip;      // blur source (window DIP)
        public RectF Rect;           // backdrop / video rect (window DIP; a video's is the part cut to the composite clip)
        public RectF Hole;           // video: the hole's own rect, uncut (window DIP) - what its radii round
        public CornerRadius4 Radii;  // backdrop / video-hole radii (DIP)
        public AcrylicRecipe Acrylic;
        public float Alpha;
        public Inherit Dist;         // the distributed ancestor edge fades this item carries (segments and groups)
        // A composite-time STICKY clip on this entry (a sticky slice's own segments, or its group): the half-plane's top
        // in the slice's walk space (DIP) and the slice's accumulated delta. Its device-px top is taken with the tile
        // raster's own arithmetic, floor(StickyY·s) + round(StickyDy·s): exactly the scissor the in-stream PushClip gave
        // the same content in its tiles (the paint route). NaN = none.
        public float StickyY, StickyDy;
    }

    /// <summary>Up to two ancestor edge fades DISTRIBUTED onto every item below them (gpu-renderer.md §13.1e): each item
    /// multiplies their analytic feathers (window DIP layers at the posed offset — A the outer one) instead of the fades
    /// compositing through a group surface.</summary>
    private struct Inherit
    {
        public byte Count;
        public PushLayerCmd A, B;

        public readonly Inherit With(in PushLayerCmd l)
        {
            Inherit r = this;
            if (r.Count == 0) r.A = l; else r.B = l;
            r.Count++;
            return r;
        }
    }

    private Plan[] _plan = new Plan[64];
    private int _planCount;
    private readonly RectF[] _videoRects = new RectF[8];
    private int _videoRectCount;
    // F070: each hole's UNCLIPPED rect at the slot's posed offset, keyed by the registry token its DrawVideo carries - what the render
    // thread's VideoPlacementApplier follows, so a composite-only scroll or animation turn moves the video exactly as far as the hole.
    private readonly FluentGpu.Media.VideoPosedHole[] _posedHoles = new FluentGpu.Media.VideoPosedHole[FluentGpu.Media.VideoSurfaceRegistry.MaxSurfaces];
    private int _posedHoleCount;
    // F087: what the occlusion verdict of each posed hole needs, parallel to _posedHoles: its erase's plan entry (-1 = the clip emptied the
    // erase), its segment, the posed bounds of the ops that paint after it in that segment, and whether it sits in an inline group layer.
    private struct PosedMeta { public int PlanIdx, Slot, Seg; public RectF After; public bool Inline; }
    private readonly PosedMeta[] _posedMeta = new PosedMeta[FluentGpu.Media.VideoSurfaceRegistry.MaxSurfaces];
    private ulong _compositeHash;

    /// <summary>Video hole rects (window DIP) the last <see cref="Place"/> placed — the published answer for
    /// <c>RectOverVideoHole</c> and the video presenter's feedback.</summary>
    internal ReadOnlySpan<RectF> VideoRects => _videoRects.AsSpan(0, _videoRectCount);

    /// <summary>The holes the last <see cref="Place"/> placed as the video placement must follow them (F070): the unclipped posed rect and
    /// the composite clip that cut it, per registry token. A hole the composite clipped away entirely is still listed (its clip is what
    /// hides the video behind it).</summary>
    internal ReadOnlySpan<FluentGpu.Media.VideoPosedHole> PosedHoles => _posedHoles.AsSpan(0, _posedHoleCount);

    /// <summary>A signature of everything the last <see cref="Place"/> composites: every placed segment's arena CONTENT
    /// (a byte hash — a slice re-walked into identical bytes signs the same), its posed offset, clip and layer, every
    /// backdrop and hole. Two turns with the same signature and no
    /// repaint damage present the same pixels (the render thread's skip-submit key).</summary>
    public ulong CompositeHash => _compositeHash;

    /// <summary>
    /// Lay out this turn's COMPOSITE PLAN from the slice arenas at the scene's CURRENT poses (no stream is written): walk
    /// the slice tree from the root in paint order — each slot's segments, each child slice at its marker with its
    /// accumulated posed offset and composite clip, a group around a non-leaf layer slice, a backdrop before an acrylic
    /// slice, each video hole's erase BEFORE the segment that punched it (after it when the hole sits inside an inline group
    /// layer — <see cref="VideoHoleErase.Order"/>). Unions every slice whose placement moved since the last
    /// turn into <paramref name="repaint"/> (old ∪ new footprint, window DIP). Zero allocation once warmed.
    /// </summary>
    public void Place(SceneRecordingSnapshot scene, ref RepaintDamageRegion repaint)
    {
        _planCount = 0;
        _videoRectCount = 0;
        _posedHoleCount = 0;
        _compositeHash = 14695981039346656037UL;
        if (_slotCount == 0 || !_recs[RootSlot].Live) return;
        for (int s = 0; s < _slotCount; s++) _recs[s].Visited = false;
        ref Rec root = ref _recs[RootSlot];
        root.AccDx = root.AccDy = 0f;
        root.EffClip = RectF.Infinite;
        root.Visited = true;
        PlaceSlot(RootSlot, scene, 0f, 0f, 0f, 0f, RectF.Infinite, RectF.Infinite, float.NaN, 0f, RectF.Infinite, default, 0f, default, default, default, false, ref repaint);
        if (_posedHoleCount > 0) ClassifyVideoOcclusion();
        // The census's Folded: the inline (folded) group layers the placed partition composites — read off the arenas'
        // scans, so a slice kept whole or a span copied from the prior buffer still counts the folds it carries.
        int folded = 0;
        for (int s = 0; s < _slotCount; s++) if (_recs[s].Live && _recs[s].Visited && (uint)s < (uint)_scanInline.Length) folded += _scanInline[s];
        LastStats = LastStats with { Folded = folded };
    }

    /// <param name="effClip">The composite clip everything below this slot inherits (window DIP; a sticky clip's
    /// half-plane included exactly).</param>
    /// <param name="segClip">The clip of this slot's OWN segments: <paramref name="effClip"/>, except that a sticky slot's
    /// own half-plane enters it grown by <see cref="StickyGrowDip"/> and is applied exactly in device px through
    /// <paramref name="stickyY"/> / <paramref name="stickyDy"/> (see <see cref="Plan.StickyY"/>).</param>
    /// <param name="cull">Window DIP outside which a child slice shows nothing (Infinite = none): inside a sticky GROUP,
    /// whose band line clips only where the group is drawn, the children the line has wholly cut away are not placed.</param>
    /// <param name="thumbDist">What this slot's own scrollbar THUMB slices inherit instead of <paramref name="dist"/>: a
    /// scroller's edge feather dissolves its content, never its overlay scrollbar (the thumb stays crisp over the fade,
    /// as it sat over the painted edge cue) — so a distributed fade's own thumb carries only the fades around it.</param>
    /// <param name="deferThumbs">This slot is a GROUP's content and its thumbs trail everything it paints: they are
    /// placed after the group closes (<see cref="PlaceTrailingThumbs"/>), not inside its surface.</param>
    private void PlaceSlot(int slot, SceneRecordingSnapshot scene, float accDx, float accDy, float parentAccDx, float parentAccDy,
        in RectF effClip, in RectF segClip, float stickyY, float stickyDy, in RectF cull, in RectF round, float roundR, in Plan leafLayer,
        in Inherit dist, in Inherit thumbDist, bool deferThumbs, ref RepaintDamageRegion repaint)
    {
        ScanSlot(slot);
        int marks = _scanMarkCount[slot];
        for (int k = 0; k <= marks; k++)
        {
            // The holes this segment punched on its tile erase what EARLIER items composited under them BEFORE the segment
            // composites (gpu-renderer.md §7.3 "Emit order"): the segment's tile already holds the hole with every later op
            // of the segment — transport, captions, a strip, a ✕ — painted back over it, so an erase after it would wipe
            // that chrome every frame.
            PlaceVideoErases(slot, k, accDx, accDy, in effClip, VideoEraseOrder.BeforeSegment);

            ref Plan e = ref NewPlan(PlanKind.Seg);
            e.Slot = slot; e.Segment = k; e.AccDx = accDx; e.AccDy = accDy; e.Clip = segClip; e.RoundRect = round; e.RoundR = roundR;
            e.StickyY = stickyY; e.StickyDy = stickyDy;
            if (marks == 0 && leafLayer.HasLayer) { e.HasLayer = true; e.Layer = leafLayer.Layer; e.InnerClip = leafLayer.InnerClip; }
            e.Dist = dist;
            Mix(ref _compositeHash, _scanHash[slot]); Mix(ref _compositeHash, (ulong)k);
            Mix(ref _compositeHash, accDx); Mix(ref _compositeHash, accDy); MixRect(ref _compositeHash, segClip);
            if (!float.IsNaN(stickyY)) { Mix(ref _compositeHash, stickyY); Mix(ref _compositeHash, stickyDy); }
            if (e.HasLayer) { Mix(ref _compositeHash, e.Layer.GroupAlpha); Mix(ref _compositeHash, e.Layer.BlurSigma); MixRect(ref _compositeHash, e.Layer.DeviceRect); }
            MixDist(ref _compositeHash, in dist);

            // A hole punched inside an inline group layer erased only that layer's scratch: the tile still holds this
            // segment's earlier content under it, so its erase follows the segment (`e` is not touched past this point —
            // NewPlan may grow the plan array).
            PlaceVideoErases(slot, k, accDx, accDy, in effClip, VideoEraseOrder.AfterSegment);

            if (k < marks)
            {
                bool thumb = IsScrollChrome(_scanMarks[slot][k].Cmd.Sub);
                if (thumb && deferThumbs) continue;
                PlaceChild(slot, in _scanMarks[slot][k], scene, accDx, accDy, parentAccDx, parentAccDy, in effClip, in cull, in round, roundR,
                    thumb ? thumbDist : dist, ref repaint);
            }
        }
    }

    /// <summary>The composite ERASE of every video hole segment <paramref name="k"/> of <paramref name="slot"/> punched whose
    /// <see cref="VideoEraseOrder"/> is <paramref name="order"/>: its rect at the slot's posed offset (window DIP), cut to the
    /// slot's composite clip, published as a hole (<see cref="VideoRects"/>) and signed into the composite hash.</summary>
    private void PlaceVideoErases(int slot, int k, float accDx, float accDy, in RectF effClip, VideoEraseOrder order)
    {
        for (int v = 0; v < _scanVideoCount[slot]; v++)
        {
            ref ScanVideo sv = ref _scanVideos[slot][v];
            if (sv.Seg != k || sv.Order != order) continue;
            RectF wr = Offset(sv.Rect, accDx, accDy);
            // F070: the posed hole as the video placement follows it - the unclipped rect and the clip that cuts it, recorded BEFORE the
            // clip empties the erase, so a hole scrolled fully under a header still tells the applier to hide the video there.
            int posedAt = -1;
            if (sv.SurfaceId > 0 && _posedHoleCount < _posedHoles.Length)
            {
                posedAt = _posedHoleCount++;
                _posedHoles[posedAt] = new FluentGpu.Media.VideoPosedHole { Token = sv.SurfaceId, Hole = Offset(sv.Hole, accDx, accDy), EffClip = effClip };
                _posedMeta[posedAt] = new PosedMeta
                {
                    PlanIdx = -1, Slot = slot, Seg = k, After = Offset(sv.After, accDx, accDy), Inline = order == VideoEraseOrder.AfterSegment,
                };
            }
            if (!effClip.IsInfinite) wr = wr.Intersect(effClip);
            if (wr.IsEmpty) continue;
            ref Plan ve = ref NewPlan(PlanKind.Video);
            if (posedAt >= 0) _posedMeta[posedAt].PlanIdx = _planCount - 1;
            ve.Slot = slot; ve.Rect = wr;
            // F078: the erase carries the hole's own shape and strength, like the in-tile punch it pairs with.
            ve.Hole = Offset(sv.Hole, accDx, accDy);
            ve.Radii = sv.Radii;
            ve.Alpha = sv.Strength;
            if (sv.RoundR > 0f) { ve.RoundRect = Offset(sv.RoundRect, accDx, accDy); ve.RoundR = sv.RoundR; }
            if (_videoRectCount < _videoRects.Length) _videoRects[_videoRectCount++] = wr;
            MixRect(ref _compositeHash, wr);
            Mix(ref _compositeHash, sv.Strength);
            MixRect(ref _compositeHash, ve.Hole);
            Mix(ref _compositeHash, sv.Radii.TopLeft); Mix(ref _compositeHash, sv.Radii.TopRight);
            Mix(ref _compositeHash, sv.Radii.BottomRight); Mix(ref _compositeHash, sv.Radii.BottomLeft);
            if (sv.RoundR > 0f) { MixRect(ref _compositeHash, ve.RoundRect); Mix(ref _compositeHash, sv.RoundR); }
        }
    }

    /// <summary>F087: the occlusion verdict of every posed hole of the plan just laid out - does anything paint over the visible part of
    /// the hole AFTER the video's own <c>DrawVideo</c>? Painter order is the plan's order: the later ops of the hole's own segment
    /// (<see cref="ScanVideo.After"/>), then every later segment (its painted bounds at its posed offset, cut by its composite clip), backdrop
    /// and hole. Bounds are per whole segment, so a segment that merely has paint somewhere over the hole's rect counts as covering it: the
    /// verdict can only err towards "covered" (underlay), never towards a video hiding UI. A hole inside an inline group layer, or one the
    /// clip emptied, is never clear. Neither is a hole whose erase is PARTIAL (<see cref="Plan.Alpha"/> below 1: the underlay leaves part of
    /// the UI over the video, a promoted visual has no opacity and would show at full strength over it), one an in-stream rounded clip cuts
    /// (<see cref="Plan.RoundR"/> above 0: the presenter rounds only by the element's own corner radius, a promoted video would show square
    /// corners over the UI outside the ancestor's rounded clip), or one that sits under a layer or group surface (an ancestor's opacity /
    /// fade / blur composites the UI over the underlay the same way). Detached fly snapshots are recorded into the root slice's tail, so they
    /// are plan segments like any other and count as covering. Allocation-free; runs only when the plan holds a hole.</summary>
    private void ClassifyVideoOcclusion()
    {
        for (int h = 0; h < _posedHoleCount; h++)
        {
            ref PosedMeta m = ref _posedMeta[h];
            bool clear = false;
            if (m.PlanIdx >= 0 && !m.Inline && _plan[m.PlanIdx].Alpha >= 0.999f && _plan[m.PlanIdx].RoundR <= 0f && !InsideGroup(m.PlanIdx))
            {
                RectF hole = _plan[m.PlanIdx].Rect;   // the part of the hole the composite clip leaves: what the video shows
                clear = !hole.IsEmpty && (m.After.IsEmpty || !m.After.Overlaps(in hole));
                for (int j = m.PlanIdx + 1; clear && j < _planCount; j++)
                {
                    ref Plan e = ref _plan[j];
                    RectF r;
                    switch (e.Kind)
                    {
                        case PlanKind.Seg:
                        {
                            if (e.Slot == m.Slot && e.Segment == m.Seg)
                            {
                                // the hole's own segment: its later ops are m.After; a leaf layer / distributed fade on it dims the UI over the video
                                if (e.HasLayer || e.Dist.Count > 0) clear = false;
                                continue;
                            }
                            ScanSeg[]? segs = e.Slot >= 0 && e.Slot < _scanSegs.Length ? _scanSegs[e.Slot] : null;
                            if (segs is null || (uint)e.Segment >= (uint)segs.Length) { clear = false; continue; }
                            RectF b = segs[e.Segment].Bounds;
                            r = b.IsEmpty ? default : Offset(in b, e.AccDx, e.AccDy);
                            if (!e.Clip.IsInfinite) r = r.Intersect(e.Clip);
                            break;
                        }
                        case PlanKind.Backdrop:
                        case PlanKind.Video:
                            r = e.Rect;
                            break;
                        default:
                            continue;   // a group's open / close: what it encloses is Seg entries of their own
                    }
                    if (!r.IsEmpty && r.Overlaps(in hole)) clear = false;
                }
            }
            _posedHoles[h].Unoccluded = clear;
        }
    }

    /// <summary>Is plan entry <paramref name="idx"/> inside an open GROUP (a layer slice with children composited through one surface)?</summary>
    private bool InsideGroup(int idx)
    {
        int depth = 0;
        for (int j = idx - 1; j >= 0; j--)
        {
            PlanKind k = _plan[j].Kind;
            if (k == PlanKind.GroupClose) depth++;
            else if (k == PlanKind.GroupOpen && depth-- == 0) return true;
        }
        return false;
    }

    /// <summary>A scroller's CHROME slice (its scrollbar thumb, or its rail + edge-cue chevrons): drawn over the scroller's
    /// edge feather, never under it (SceneRecorder's chromeOverFade — the paint route closes the feather before it).</summary>
    internal static bool IsScrollChrome(int sub) => (sub & 7) is (int)SliceRole.Thumb or (int)SliceRole.Chrome;

    /// <summary>Is <paramref name="layer"/> a feather the chrome under it goes OVER — an edge fade at full group alpha
    /// (under a partial alpha the chrome stays inside the group, as in the paint route)?</summary>
    private static bool ChromeOverLayer(in PushLayerCmd layer) => layer.Kind == (int)LayerKind.EdgeFade && layer.GroupAlpha >= 0.999f;

    /// <summary>Can <paramref name="slot"/>'s chrome slices be lifted out of its group: every marker from the first chrome
    /// marker on is chrome and nothing after it paints (so placing them after the group changes no painter order)?</summary>
    private bool ThumbsTrail(int slot)
    {
        ScanSlot(slot);
        int marks = _scanMarkCount[slot], first = -1;
        for (int k = 0; k < marks; k++)
        {
            bool thumb = IsScrollChrome(_scanMarks[slot][k].Cmd.Sub);
            if (thumb && first < 0) first = k;
            else if (!thumb && first >= 0) return false;
        }
        if (first < 0) return false;
        for (int k = first + 1; k <= marks; k++) if (!_scanSegs[slot][k].Bounds.IsEmpty) return false;
        return true;
    }

    /// <summary>A group's trailing scrollbar thumbs, placed after its surface — crisp over the group's fade.</summary>
    private void PlaceTrailingThumbs(int slot, SceneRecordingSnapshot scene, float accDx, float accDy, float parentAccDx, float parentAccDy,
        in RectF effClip, in RectF cull, in RectF round, float roundR, in Inherit dist, ref RepaintDamageRegion repaint)
    {
        int marks = _scanMarkCount[slot];
        for (int k = 0; k < marks; k++)
            if (IsScrollChrome(_scanMarks[slot][k].Cmd.Sub))
                PlaceChild(slot, in _scanMarks[slot][k], scene, accDx, accDy, parentAccDx, parentAccDy, in effClip, in cull, in round, roundR,
                    in dist, ref repaint);
    }

    private static void MixDist(ref ulong h, in Inherit d)
    {
        if (d.Count == 0) return;
        Mix(ref h, 0xD157UL + d.Count);
        MixRect(ref h, d.A.DeviceRect); Mix(ref h, d.A.FadeBandL); Mix(ref h, d.A.FadeBandT); Mix(ref h, d.A.FadeBandR); Mix(ref h, d.A.FadeBandB);
        if (d.Count > 1) { MixRect(ref h, d.B.DeviceRect); Mix(ref h, d.B.FadeBandL); Mix(ref h, d.B.FadeBandT); Mix(ref h, d.B.FadeBandR); Mix(ref h, d.B.FadeBandB); }
    }

    private ref Plan NewPlan(PlanKind kind)
    {
        if (_planCount == _plan.Length) Array.Resize(ref _plan, _plan.Length * 2);
        ref Plan e = ref _plan[_planCount++];
        e = default;
        e.Kind = kind;
        e.Slot = -1;
        e.StickyY = float.NaN;
        return ref e;
    }

    /// <summary>Where a child slice places this turn: its accumulated posed offset (<paramref name="cdx"/>,
    /// <paramref name="cdy"/>), the offset its marker's own params ride (<paramref name="pdx"/>, <paramref name="pdy"/> —
    /// the containing slot's, or one level up for <see cref="CompositeSliceFlags.ParamsUp"/>) and its composite clip. Pure:
    /// the placement and the distribution test share it.</summary>
    private void ChildGeometry(int child, in CompositeSliceCmd m, SceneRecordingSnapshot scene, float accDx, float accDy,
        float parentAccDx, float parentAccDy, in RectF parentClip, out float cdx, out float cdy, out float pdx, out float pdy, out RectF clip)
    {
        bool up = m.Has(CompositeSliceFlags.ParamsUp);
        pdx = up ? parentAccDx : accDx; pdy = up ? parentAccDy : accDy;
        OwnDelta(child, scene, out float odx, out float ody);
        cdx = accDx + odx; cdy = accDy + ody;
        // The composite clip: the ancestors' clip ∩ the marker's recorded clip (+ its outer clip).
        clip = parentClip;
        if (!m.Clip.IsEmpty && !m.Clip.IsInfinite) clip = clip.IsInfinite ? Offset(m.Clip, pdx, pdy) : clip.Intersect(Offset(m.Clip, pdx, pdy));
        if (m.Has(CompositeSliceFlags.OuterClip))
        {
            RectF r = Offset(m.OuterClip.DeviceRect, pdx, pdy);
            clip = clip.IsInfinite ? r : clip.Intersect(r);
        }
    }

    /// <summary>The marker's group layer translated to its posed offset (window DIP). A sticky slice's edge fade first
    /// takes the half-plane <paramref name="stickyWc"/> (its walk space, the marker's space) the way the paint route
    /// recorded it: the fade rect and its composite clip cut at the band line, which IS the edge the content dissolves at.</summary>
    private static PushLayerCmd PosedLayer(in CompositeSliceCmd m, float pdx, float pdy, bool sticky = false, in RectF stickyWc = default)
    {
        PushLayerCmd l = m.Layer;
        if (sticky && (LayerKind)l.Kind == LayerKind.EdgeFade)
            l = l with
            {
                DeviceRect = l.DeviceRect.Intersect(stickyWc),
                CompositeClip = l.CompositeClip.IsEmpty ? l.CompositeClip : l.CompositeClip.Intersect(stickyWc),
            };
        return l with
        {
            DeviceRect = Offset(l.DeviceRect, pdx, pdy),
            CompositeClip = l.CompositeClip.IsEmpty ? l.CompositeClip : Offset(l.CompositeClip, pdx, pdy),
        };
    }

    /// <summary>How far a sticky slot's own-segment clip (DIP) reaches above its exact band line: a superset margin so the
    /// DIP clip never cuts a device row that the exact px top (<see cref="Plan.StickyY"/>) keeps, at any scale ≥ 0.5.</summary>
    internal const float StickyGrowDip = 2f;

    /// <summary>The composite-time sticky clip of <paramref name="child"/> this turn: its current NodePaint.ClipRect (the
    /// ClipTop pose) transformed by the world its walk recorded under, in that walk's space. False when the slice carries
    /// none or it is released (an Infinite ClipRect).</summary>
    private bool StickyWc(int child, SceneRecordingSnapshot scene, out RectF wc)
    {
        wc = default;
        ref Rec c = ref _recs[child];
        if (!c.Sticky) return false;
        var h = scene.HandleAt(c.NodeIndex);
        if (h.IsNull || h.Raw.Gen != c.Gen || !scene.IsLive(h)) return false;
        RectF cr = scene.Paint(h).ClipRect;
        if (cr.IsInfinite) return false;
        wc = c.StickyWorld.TransformBounds(cr);
        return true;
    }

    private static RectF ClipAnd(in RectF clip, in RectF r) => clip.IsInfinite ? r : clip.Intersect(r);

    /// <summary>The video holes of a slice the band line cut away whole, and of every slice below it, posed under the clip that cut
    /// them (window DIP) with no erase: nothing of the subtree composites, but the video placement follows <see cref="PosedHoles"/>,
    /// and a token missing there leaves the video at the UI's published rect, whose viewport knows only ClipsToBounds ancestors.</summary>
    private void PoseCutHoles(int slot, SceneRecordingSnapshot scene, float accDx, float accDy, float parentAccDx, float parentAccDy, in RectF clip)
    {
        ScanSlot(slot);
        for (int v = 0; v < _scanVideoCount[slot]; v++)
        {
            ref ScanVideo sv = ref _scanVideos[slot][v];
            if (sv.SurfaceId <= 0 || _posedHoleCount >= _posedHoles.Length) continue;
            int at = _posedHoleCount++;
            _posedHoles[at] = new FluentGpu.Media.VideoPosedHole { Token = sv.SurfaceId, Hole = Offset(sv.Hole, accDx, accDy), EffClip = clip };
            _posedMeta[at] = new PosedMeta { PlanIdx = -1, Slot = slot, Seg = sv.Seg, After = Offset(sv.After, accDx, accDy), Inline = sv.Order == VideoEraseOrder.AfterSegment };
        }
        int marks = _scanMarkCount[slot];
        for (int k = 0; k < marks; k++)
        {
            CompositeSliceCmd m = _scanMarks[slot][k].Cmd;
            int child = Find(m.NodeIndex, m.Gen, m.Sub);
            if (child < 0 || _recs[child].Visited) continue;
            ChildGeometry(child, in m, scene, accDx, accDy, parentAccDx, parentAccDy, in clip, out float cdx, out float cdy, out _, out _, out RectF cc);
            PoseCutHoles(child, scene, cdx, cdy, accDx, accDy, in cc);
        }
    }

    private static RectF GrowTop(in RectF r, float by) => new(r.X, r.Y - by, r.W, r.H + by);

    private void PlaceChild(int parentSlot, in ScanMark mark, SceneRecordingSnapshot scene, float accDx, float accDy,
        float parentAccDx, float parentAccDy, in RectF parentClip, in RectF cull, in RectF parentRound, float parentRoundR,
        in Inherit dist, ref RepaintDamageRegion repaint)
    {
        CompositeSliceCmd m = mark.Cmd;
        int child = Find(m.NodeIndex, m.Gen, m.Sub);
        if (child < 0 || _recs[child].Visited) return;   // a retired/duplicated child: nothing to place (never on a consistent pass)
        ref Rec c = ref _recs[child];
        c.Visited = true;
        ChildGeometry(child, in m, scene, accDx, accDy, parentAccDx, parentAccDy, in parentClip,
            out float cdx, out float cdy, out float pdx, out float pdy, out RectF clip);

        // The innermost rounded clip in effect at the marker (the marker's own rounded outer clip, else the containing
        // stream's).
        RectF round = parentRound;
        float roundR = parentRoundR;
        if (mark.RoundR > 0f) { round = Offset(mark.RoundRect, accDx, accDy); roundR = mark.RoundR; }
        if (m.Has(CompositeSliceFlags.OuterClip) && m.OuterClip.CornerRadius > 0f)
        {
            round = Offset(m.OuterClip.RoundedRect, pdx, pdy); roundR = m.OuterClip.CornerRadius;
        }
        c.AccDx = cdx; c.AccDy = cdy;

        // A composite-time STICKY clip (CompositeSliceFlags.StickyClip): the node's ClipTop pose, a half-plane that is
        // viewport-fixed while the slice rides the page. Everything below the slice inherits it exactly (childClip — the
        // arithmetic the marker clips of the paint route had); the slice's own segments and its group draw take it in
        // device px with the tile scissor's arithmetic (stickyY/stickyDy) under a DIP clip grown to a superset (ownClip).
        bool sticky = StickyWc(child, scene, out RectF stickyWc);
        RectF childClip = clip, ownClip = clip;
        float stickyY = float.NaN;
        if (sticky)
        {
            RectF win = Offset(stickyWc, cdx, cdy);
            childClip = ClipAnd(clip, win);
            ownClip = ClipAnd(clip, GrowTop(win, StickyGrowDip));
            stickyY = stickyWc.Y;
            Mix(ref _compositeHash, 0x571CUL); MixRect(ref _compositeHash, win);
        }
        c.EffClip = childClip;

        // A posed image layer composites through its own affine (scale + translate about its transform origin): its footprint
        // is the free bounds mapped through it, and a change of that pose is the composite damage.
        Affine2D pm = Affine2D.Identity;
        bool poseMoved = false;
        if (c.Pose == PoseKind.Posed)
        {
            PosedAffine(child, scene, out pm);
            poseMoved = c.HasLast && c.LastPosedM != pm;
            c.PosedM = pm;
            c.LastPosedM = pm;
            Mix(ref _compositeHash, pm.M11); Mix(ref _compositeHash, pm.M22); Mix(ref _compositeHash, pm.Dx); Mix(ref _compositeHash, pm.Dy);
        }

        // Composite damage: the slice's footprint moved, or its sticky line did.
        RectF placed = Offset(c.Pose == PoseKind.Posed ? pm.TransformBounds(c.Bounds) : c.Bounds, cdx, cdy);
        RectF footprint = clip.IsInfinite ? placed : placed.Intersect(clip);
        bool stickyMoved = sticky != c.HasLastSticky || (sticky && c.LastStickyWc != stickyWc);
        if (c.HasLast && (c.LastAccDx != cdx || c.LastAccDy != cdy || stickyMoved || poseMoved))
        {
            repaint.Add(Pad(c.LastFootprint));
            repaint.Add(Pad(footprint));
        }
        c.HasLast = true;
        c.LastAccDx = cdx; c.LastAccDy = cdy;
        c.LastFootprint = footprint;
        c.HasLastSticky = sticky;
        c.LastStickyWc = stickyWc;
        // Fully cut away (the band line has passed the slice's far edge, its own or an enclosing sticky group's): nothing of
        // it composites this turn, exactly as the paint route culled the subtree an empty clip left nothing of.
        // Its video holes are still posed under the clip that cut them (PosedHoles): the video behind them must hide at the line too.
        if (sticky && (childClip.IsEmpty || placed.Intersect(childClip).IsEmpty)) { PoseCutHoles(child, scene, cdx, cdy, accDx, accDy, in childClip); return; }
        if (!cull.IsInfinite && placed.Intersect(cull).IsEmpty) { PoseCutHoles(child, scene, cdx, cdy, accDx, accDy, ClipAnd(childClip, cull)); return; }

        // The marker's group layer, translated to its posed offset. A WhileStuck fade (CompositeSliceFlags.FadeWhileStuck)
        // exists only on a turn whose sticky clip is engaged: released, the slice places with no layer at all.
        Plan layer = default;
        if (m.Has(CompositeSliceFlags.Layer) && (sticky || !m.Has(CompositeSliceFlags.FadeWhileStuck)))
        {
            layer.HasLayer = true;
            layer.Layer = PosedLayer(in m, pdx, pdy, sticky, in stickyWc);
            layer.InnerClip = m.Has(CompositeSliceFlags.InnerClip) ? Offset(m.InnerClip.DeviceRect, pdx, pdy) : default;
        }

        // An acrylic slice: its frosted backdrop composites first (from everything painted before it), then its content.
        if (!c.Acrylic.IsNone && !c.AcrylicRect.IsEmpty)
        {
            ref Plan b = ref NewPlan(PlanKind.Backdrop);
            b.Slot = child;
            b.Rect = Offset(c.AcrylicRect, cdx, cdy);
            b.Radii = c.AcrylicRadii;
            b.Acrylic = c.Acrylic;
            b.Alpha = c.AcrylicAlpha;
            b.Clip = clip;
            MixRect(ref _compositeHash, b.Rect); Mix(ref _compositeHash, b.Alpha);
        }

        ScanSlot(child);
        bool leaf = _scanMarkCount[child] == 0;
        if (layer.HasLayer && !leaf)
        {
            if (Distributable(child, in m, in layer.Layer, scene, cdx, cdy, accDx, accDy, in childClip, dist.Count,
                    sticky ? Offset(stickyWc, cdx, cdy) : RectF.Infinite))
            {
                // A DISTRIBUTED edge fade (gpu-renderer.md §13.1e): no group surface — every item below carries the
                // fade's analytic feather (exact: inside the band no two items overlap, and the fade has no own paint).
                Mix(ref _compositeHash, 0xFEA7UL);
                PlaceSlot(child, scene, cdx, cdy, accDx, accDy, childClip, ownClip, stickyY, cdy, cull, round, roundR, default,
                    dist.With(in layer.Layer), in dist, false, ref repaint);
                return;
            }
            // A layer slice with children: one group surface holds the slice AND its descendants (exact group semantics).
            // The group item carries the distributed ancestor fades (it is one item of theirs); its enclosed items none.
            // A sticky group's own clip applies where its surface is DRAWN (a scissor commutes with the group's pointwise
            // alpha / feather), so what it encloses renders unclipped by it — the content key stays put while the page
            // moves the group under its band line.
            ref Plan g = ref NewPlan(PlanKind.GroupOpen);
            g.Slot = child; g.HasLayer = true; g.Layer = layer.Layer; g.InnerClip = layer.InnerClip;
            g.Clip = ownClip; g.RoundRect = round; g.RoundR = roundR;
            g.StickyY = stickyY; g.StickyDy = cdy;
            g.AccDx = cdx; g.AccDy = cdy;
            g.Rect = c.Bounds.IsEmpty ? default : Offset(c.Bounds, cdx, cdy);   // the placed footprint (the group-cache region)
            g.Dist = dist;
            Mix(ref _compositeHash, layer.Layer.GroupAlpha); Mix(ref _compositeHash, layer.Layer.BlurSigma); MixRect(ref _compositeHash, ownClip);
            MixRect(ref _compositeHash, layer.Layer.DeviceRect);
            Mix(ref _compositeHash, cdx); Mix(ref _compositeHash, cdy); MixDist(ref _compositeHash, in dist);
            bool liftThumbs = ChromeOverLayer(in layer.Layer) && ThumbsTrail(child);
            int posedFrom = _posedHoleCount;
            PlaceSlot(child, scene, cdx, cdy, accDx, accDy, clip, clip, float.NaN, 0f,
                sticky ? ClipAnd(cull, Offset(stickyWc, cdx, cdy)) : cull, round, roundR, default, default, default, liftThumbs, ref repaint);
            // The group's band line cuts its surface where it is drawn, so every hole it encloses shows only below it: the video
            // behind each takes the line too (its erase stays unclipped by it, inside the surface).
            if (sticky)
                for (int h = posedFrom; h < _posedHoleCount; h++) _posedHoles[h].EffClip = ClipAnd(_posedHoles[h].EffClip, childClip);
            ref Plan gc = ref NewPlan(PlanKind.GroupClose);
            gc.Slot = child;
            if (liftThumbs) PlaceTrailingThumbs(child, scene, cdx, cdy, accDx, accDy, in childClip, in cull, in round, roundR, in dist, ref repaint);
        }
        else PlaceSlot(child, scene, cdx, cdy, accDx, accDy, childClip, ownClip, stickyY, cdy, cull, round, roundR, layer, in dist, in dist, false, ref repaint);
    }

    // ── the distribution test (gpu-renderer.md §13.1e) ─────────────────────────────────────────────────────────────

    /// <summary>At most this many item footprints are tested per distributed fade (past it the fade stays a group).</summary>
    private const int MaxFootprints = 128;
    private readonly RectF[] _fp = new RectF[MaxFootprints];

    /// <summary>
    /// May the edge fade on <paramref name="child"/>'s marker be DISTRIBUTED — composited as an analytic feather on every
    /// item below it instead of through a group surface? For premultiplied source-over, feather(A over B) equals
    /// feather(A) over feather(B) at a pixel iff f·αA·αB·(1−f) = 0, so it is exact when: the layer is a pure fade
    /// (<see cref="CompositeSliceFlags.DistributeFade"/>: fade mode, no blur, alpha ≈ 1); the slice paints nothing of its
    /// own this turn (every own segment's painted bounds empty — a fill / border / expanded scrollbar track makes it a
    /// group); no acrylic backdrop or video hole lies below it; every item below carries at most two feathers (a third
    /// makes the fade a group); and no two item footprints overlap inside the band strips where 0 &lt; f &lt; 1. The test runs
    /// per placement: a turn that fails it composites the fade as a group (≤ 1/255 apart — the group surface's 8-bit
    /// quantization), which the group cache then keeps.
    /// </summary>
    private bool Distributable(int child, in CompositeSliceCmd m, in PushLayerCmd l, SceneRecordingSnapshot scene,
        float cdx, float cdy, float accDx, float accDy, in RectF clip, int inherited, in RectF ownSticky)
    {
        if (ForceGroupFades || !m.Has(CompositeSliceFlags.DistributeFade)) return false;
        if ((LayerKind)l.Kind != LayerKind.EdgeFade || l.BlurSigma > 0f || l.GroupAlpha < 0.999f) return false;
        if (inherited + 1 > 2) return false;
        int n = 0;
        bool ok = true;
        CollectFootprints(child, scene, cdx, cdy, accDx, accDy, in clip, inherited + 1, 0f, true, in ownSticky, ref n, ref ok);
        return ok && Disjoint(0, n, in l);
    }

    /// <summary>The footprints (window DIP, placed) of every composite item <paramref name="slot"/> would contribute,
    /// appended to <see cref="_fp"/> from <paramref name="n"/>; <paramref name="ok"/> = false when the subtree holds an
    /// acrylic backdrop, a video hole, an item that would carry a third feather, or more items than the scratch holds.
    /// <paramref name="feathers"/> = the distributed feathers every item here already carries; <paramref name="halo"/> =
    /// a self-blur's reach (DIP) the items' pixels spread by. <paramref name="root"/> = the fade's own slot (its own
    /// segments must paint nothing).</summary>
    private void CollectFootprints(int slot, SceneRecordingSnapshot scene, float accDx, float accDy, float parentAccDx, float parentAccDy,
        in RectF clip, int feathers, float halo, bool root, in RectF ownSticky, ref int n, ref bool ok)
    {
        ScanSlot(slot);
        if (_scanVideoCount[slot] > 0) { ok = false; return; }
        int marks = _scanMarkCount[slot];
        for (int k = 0; k <= marks && ok; k++)
        {
            RectF sb = _scanSegs[slot][k].Bounds;
            // a sticky slot's own paint counts only where its band line lets it show (the paint route's scan saw it
            // through the in-stream clip; the composite clips it at placement — the same pixels either way)
            if (!sb.IsEmpty && !ownSticky.IsInfinite && Offset(sb, accDx, accDy).Intersect(ownSticky).IsEmpty) sb = default;
            if (!sb.IsEmpty)
            {
                if (root) { ok = false; return; }   // the fade's own paint overlaps its content inside the band
                RectF fp = Offset(sb, accDx, accDy);
                if (halo > 0f) fp = new RectF(fp.X - halo, fp.Y - halo, fp.W + 2f * halo, fp.H + 2f * halo);
                if (!clip.IsInfinite) fp = fp.Intersect(clip);
                if (!fp.IsEmpty)
                {
                    if (n == MaxFootprints) { ok = false; return; }
                    _fp[n++] = fp;
                }
            }
            if (k == marks) break;
            CompositeSliceCmd cm = _scanMarks[slot][k].Cmd;
            if (root && IsScrollChrome(cm.Sub)) continue;   // the fade's own chrome: never under its feather
            int c = Find(cm.NodeIndex, cm.Gen, cm.Sub);
            if (c < 0) continue;
            ref Rec rc = ref _recs[c];
            if (!rc.Acrylic.IsNone && !rc.AcrylicRect.IsEmpty) { ok = false; return; }
            ChildGeometry(c, in cm, scene, accDx, accDy, parentAccDx, parentAccDy, in clip,
                out float ccdx, out float ccdy, out float pdx, out float pdy, out RectF cclip);
            bool csticky = StickyWc(c, scene, out RectF cwc);
            RectF cwin = csticky ? Offset(cwc, ccdx, ccdy) : RectF.Infinite;
            RectF groupClip = cclip;   // a group's footprint: its sticky clip applies where it is drawn, not to its extent
            if (csticky) cclip = ClipAnd(cclip, cwin);
            ScanSlot(c);
            bool cleaf = _scanMarkCount[c] == 0;
            bool hasLayer = cm.Has(CompositeSliceFlags.Layer) && (csticky || !cm.Has(CompositeSliceFlags.FadeWhileStuck));
            int own = hasLayer && (LayerKind)cm.Layer.Kind == LayerKind.EdgeFade ? 1 : 0;
            float chalo = halo;
            if (hasLayer && (LayerKind)cm.Layer.Kind == LayerKind.Blur && cm.Layer.BlurSigma > 0f)
                chalo += SelfBlurRegion.TapRadius(cm.Layer.BlurSigma);
            if (hasLayer && !cleaf)
            {
                PushLayerCmd cl = PosedLayer(in cm, pdx, pdy, csticky, in cwc);
                int start = n;
                if (!ForceGroupFades && own == 1 && cm.Has(CompositeSliceFlags.DistributeFade) && cl.BlurSigma <= 0f
                    && cl.GroupAlpha >= 0.999f && feathers + 1 <= 2)
                {
                    // a nested fade that itself distributes: its items join ours (carrying one more feather)
                    bool nok = true;
                    CollectFootprints(c, scene, ccdx, ccdy, accDx, accDy, in cclip, feathers + 1, chalo, true, in cwin, ref n, ref nok);
                    if (nok && Disjoint(start, n, in cl)) continue;
                    if (n == MaxFootprints && !nok) { ok = false; return; }
                    n = start;   // it composites as a group after all: one item
                }
                if (feathers + own > 2) { ok = false; return; }
                if (rc.Bounds.IsEmpty) continue;
                RectF gfp = Offset(rc.Bounds, ccdx, ccdy);
                if (chalo > 0f) gfp = new RectF(gfp.X - chalo, gfp.Y - chalo, gfp.W + 2f * chalo, gfp.H + 2f * chalo);
                if (!groupClip.IsInfinite) gfp = gfp.Intersect(groupClip);
                if (gfp.IsEmpty) continue;
                if (n == MaxFootprints) { ok = false; return; }
                _fp[n++] = gfp;
                // a group enclosing an acrylic backdrop / a video hole: its backdrop mini-composite reads what lies beneath
                // the group, which distribution changes — keep the fade a group
                if (!SubtreeComposites(c)) { ok = false; return; }
                continue;
            }
            if (feathers + own > 2) { ok = false; return; }
            CollectFootprints(c, scene, ccdx, ccdy, accDx, accDy, in cclip, feathers, chalo, false, in cwin, ref n, ref ok);
        }
    }

    /// <summary>True when nothing below <paramref name="slot"/> is an acrylic backdrop or a video hole.</summary>
    private bool SubtreeComposites(int slot)
    {
        ScanSlot(slot);
        if (_scanVideoCount[slot] > 0) return false;
        for (int k = 0; k < _scanMarkCount[slot]; k++)
        {
            CompositeSliceCmd cm = _scanMarks[slot][k].Cmd;
            int c = Find(cm.NodeIndex, cm.Gen, cm.Sub);
            if (c < 0) continue;
            if (!_recs[c].Acrylic.IsNone && !_recs[c].AcrylicRect.IsEmpty) return false;
            if (!SubtreeComposites(c)) return false;
        }
        return true;
    }

    /// <summary>No two footprints in [<paramref name="a"/>, <paramref name="b"/>) overlap inside the region where the fade
    /// <paramref name="l"/>'s feather is strictly between 0 and 1 (its enabled band strips and active corner squares; the
    /// whole plane when its intensity is below 1).</summary>
    private bool Disjoint(int a, int b, in PushLayerCmd l)
    {
        Span<RectF> bands = stackalloc RectF[8];
        int nb = BandStrips(in l, bands, out bool everywhere);
        for (int i = a; i < b; i++)
            for (int j = i + 1; j < b; j++)
            {
                RectF o = _fp[i].Intersect(_fp[j]);
                if (o.IsEmpty) continue;
                if (everywhere) return false;
                for (int k = 0; k < nb; k++) if (!o.Intersect(bands[k]).IsEmpty) return false;
            }
        return true;
    }

    /// <summary>The window-DIP regions where an edge-fade layer's feather is strictly between 0 and 1: each enabled band
    /// strip along its rect's edge and each active rounded corner's square. <paramref name="everywhere"/> = its intensity
    /// is below 1 (the feather is then fractional outside the rect as well).</summary>
    private static int BandStrips(in PushLayerCmd l, Span<RectF> dst, out bool everywhere)
    {
        everywhere = l.FadeIntensity < 0.999f;
        RectF r = l.DeviceRect;
        int n = 0;
        float bl = MathF.Max(0f, l.FadeBandL), bt = MathF.Max(0f, l.FadeBandT), br = MathF.Max(0f, l.FadeBandR), bb = MathF.Max(0f, l.FadeBandB);
        if (bl > 0f) dst[n++] = new RectF(r.X, r.Y, bl, r.H);
        if (bt > 0f) dst[n++] = new RectF(r.X, r.Y, r.W, bt);
        if (br > 0f) dst[n++] = new RectF(r.Right - br, r.Y, br, r.H);
        if (bb > 0f) dst[n++] = new RectF(r.X, r.Bottom - bb, r.W, bb);
        CornerRadius4 c = l.Radii;
        if (bl > 0f && bt > 0f && c.TopLeft > 0f) dst[n++] = new RectF(r.X, r.Y, c.TopLeft, c.TopLeft);
        if (br > 0f && bt > 0f && c.TopRight > 0f) dst[n++] = new RectF(r.Right - c.TopRight, r.Y, c.TopRight, c.TopRight);
        if (br > 0f && bb > 0f && c.BottomRight > 0f) dst[n++] = new RectF(r.Right - c.BottomRight, r.Bottom - c.BottomRight, c.BottomRight, c.BottomRight);
        if (bl > 0f && bb > 0f && c.BottomLeft > 0f) dst[n++] = new RectF(r.X, r.Bottom - c.BottomLeft, c.BottomLeft, c.BottomLeft);
        return n;
    }

    private static RectF Pad(in RectF r)
    {
        if (r.IsEmpty) return default;
        const float pad = SceneRecordingContext.RepaintAaPadDip;
        return new RectF(r.X - pad, r.Y - pad, r.W + 2f * pad, r.H + 2f * pad);
    }

    private static RectF Union(in RectF a, in RectF b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        float x0 = MathF.Min(a.X, b.X), y0 = MathF.Min(a.Y, b.Y);
        float x1 = MathF.Max(a.Right, b.Right), y1 = MathF.Max(a.Bottom, b.Bottom);
        return new RectF(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Offset a window-space rect. An empty rect stays empty, and an UNBOUNDED axis stays unbounded (an item
    /// band's clip is infinite across the scroll axis and finite along it — only the finite axis moves).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static RectF Offset(in RectF r, float dx, float dy)
    {
        if ((dx == 0f && dy == 0f) || r.IsEmpty) return r;
        const float unbounded = -5e8f;
        return new RectF(r.X <= unbounded ? r.X : r.X + dx, r.Y <= unbounded ? r.Y : r.Y + dy, r.W, r.H);
    }

    private static void Mix(ref ulong h, ulong v) { h ^= v; h *= 1099511628211UL; }
    private static void Mix(ref ulong h, float v) => Mix(ref h, BitConverter.SingleToUInt32Bits(v));
    private static void MixRect(ref ulong h, in RectF r) { Mix(ref h, r.X); Mix(ref h, r.Y); Mix(ref h, r.W); Mix(ref h, r.H); }

    // ── the composite frame (§A.5 / §A.8) ─────────────────────────────────────────────────────────────────────────

    private SliceRow[] _rows = new SliceRow[64];
    private CompositeItem[] _items = new CompositeItem[64];
    private PushLayerCmd[] _itemLayers = new PushLayerCmd[64];
    private PushLayerCmd[] _itemInherited = new PushLayerCmd[128];
    private TileRaster[] _rasters = new TileRaster[256];
    private byte[] _rasterDone = new byte[256];
    private TilePlacement[] _placements = new TilePlacement[256];
    private SliceSpan[] _frameSpans = new SliceSpan[256];
    private byte[] _frameStreams = new byte[16384];
    private readonly TileKey[] _needed = new TileKey[512];
    private readonly byte[] _neededOrder = new byte[512];
    private readonly PixelRect[] _dirty = new PixelRect[RepaintDamageRegion.MaxRects];
    private int[] _groupOpenAt = new int[16];
    private int _rowCount, _itemCount, _rasterCount, _placementCount, _frameSpanCount, _streamLen, _dirtyCount;
    private int _turn;
    private int _lastThemeEpoch = int.MinValue;
    private ColorF _lastClear;
    private bool _hasLastClear;
    private float _lastImageClock = float.NaN;
    private int _exposedMissing;
    private int _coverageClamps;

    /// <summary>The composite items the last <see cref="BuildComposite"/> produced (painter order).</summary>
    public ReadOnlySpan<CompositeItem> LastItems => _items.AsSpan(0, _itemCount);
    public ReadOnlySpan<SliceRow> LastRows => _rows.AsSpan(0, _rowCount);
    public ReadOnlySpan<TileRaster> LastRasters => _rasters.AsSpan(0, _rasterCount);
    /// <summary>VISIBLE tiles of non-degraded slices that composited nothing in the last turn (must be 0).</summary>
    public int LastExposedTileMissing => _exposedMissing;
    /// <summary>Scroll segments whose visible content reached past their realized coverage in the last composite (a
    /// band no tile could hold). Must be 0.</summary>
    public int LastCoverageClamps => _coverageClamps;
    /// <summary>The tile budget (bytes) the last turn resolved against.</summary>
    public long LastBudgetBytes { get; private set; }
    /// <summary>Tiles the backend actually rastered in the last turn (<see cref="EndComposite"/>).</summary>
    public int LastRasteredTiles { get; private set; }
    /// <summary>Surface bytes of the tiles <see cref="LastRasteredTiles"/> counts.</summary>
    public long LastRasteredBytes { get; private set; }

    /// <summary>
    /// Build this turn's <see cref="CompositeFrame"/> from the last <see cref="Place"/>'s plan and run the
    /// <see cref="SliceTable"/> per-turn flow: open every segment as a slice (static / scroll / effect; a stable device
    /// origin — the window grid for static and scroll slices, the floored content origin for an effect slice's region),
    /// apply the image cross-fades advancing inside them (Content) and a theme / background change or a forced-full repaint
    /// (BackgroundOrTheme, whole slices), request each segment's needed tiles — each resident tile checked against the
    /// content its segment's bytes now describe (Content / PrimCount, <see cref="SliceTable.Request{TContent}"/>)
    /// (<see cref="TileGrid.Needed(in RectF, double, in MotionFeel, double, double, bool, Span{TileKey}, Span{byte}, out int, int)"/>
    /// over its composite viewport ∩ its painted bounds, and the realized coverage for a scroll slice), resolve surfaces
    /// within the budget (visible first across ALL slices), and lay out the painter-ordered composite items with their
    /// posed transforms, clips, group alpha, feather, blur and acrylic. The slice streams are concatenated into the frame
    /// only when <paramref name="withStreams"/> (a backend that consumes them). Call <see cref="EndComposite"/> after the
    /// submit.
    /// </summary>
    public CompositeFrame BuildComposite(SliceTable table, SceneRecordingSnapshot scene, in FrameInfo info, int themeEpoch,
        in RepaintDamageRegion repaint, bool withStreams)
    {
        float scale = info.Scale > 0f ? info.Scale : 1f;
        EvBeginFrame(in info, scale);
        table.BeginFrame(++_turn);
        _rowCount = _itemCount = _rasterCount = _placementCount = _frameSpanCount = _streamLen = _dirtyCount = 0;
        int posedImages = 0;
        if (withStreams) ConcatStreams();
        _coverageClamps = 0;
        // A theme / window-background change — or a repaint the host could not describe (first frame, resize, device
        // recovery, an undescribed image landing) — repaints every retained tile WHOLE, before any per-node damage.
        bool themeChanged = _lastThemeEpoch != themeEpoch || !_hasLastClear || _lastClear != info.Clear;
        if ((themeChanged && _lastThemeEpoch != int.MinValue) || repaint.IsFull) table.InvalidateAll(InvalidationReason.BackgroundOrTheme);
        _lastThemeEpoch = themeEpoch;
        _lastClear = info.Clear;
        _hasLastClear = true;
        // Image cross-fades advance with the image clock under byte-identical commands — the one per-node pixel change no
        // content want can see: every fade window the clock passed through since the last turn invalidates the tiles
        // under its image's rect in its own segment (below).
        float clock = info.ImageClockMs, lastClock = _lastImageClock;
        _lastImageClock = clock;

        MotionFeel feel = FluentGpu.Scroll.Diag.ScrollTunables.Current;
        int groupDepth = 0;
        float winW = info.SizePx.Width / scale, winH = info.SizePx.Height / scale;
        for (int i = 0; i < _planCount; i++)
        {
            ref Plan e = ref _plan[i];
            switch (e.Kind)
            {
                case PlanKind.GroupOpen:
                {
                    if (groupDepth == _groupOpenAt.Length) Array.Resize(ref _groupOpenAt, _groupOpenAt.Length * 2);
                    _groupOpenAt[groupDepth++] = _itemCount;
                    LayerParams(in e.Layer, e.InnerClip, scale, out float gAlpha, out float gSigma, out EdgeFeather gFeather, out RectF gSrc);
                    ApplyDist(in e.Dist, scale, ref gAlpha, ref gFeather, out EdgeFeather gFeather2);
                    // The group's transform is its slice's posed offset (whole device px) and its footprint the placed
                    // extent of what it encloses (+ its blur reach): a group moved rigidly by an ancestor scroll keeps its
                    // surface origin relative to its content — the group-cache key (GroupCacheKey) then matches.
                    var gT = Affine2D.Translation(MathF.Round(e.AccDx * scale), MathF.Round(e.AccDy * scale));
                    RectF gFoot = ScalePx(e.Rect, scale);
                    if (!gFoot.IsEmpty && gSigma > 0f)
                    {
                        float hr = SelfBlurRegion.TapRadius(gSigma);
                        gFoot = new RectF(gFoot.X - hr, gFoot.Y - hr, gFoot.W + 2f * hr, gFoot.H + 2f * hr);
                    }
                    AddItem(new CompositeItem(-1, CompositeKind.Group, gT, gAlpha, StickyClipPx(ClipPx(e.Clip, scale), in e, scale, winW, winH), RadiiPx(e.RoundR, scale),
                        gFeather, gSigma, default, 0, RoundPx(e.RoundRect, e.RoundR, scale), 0, 1, gSrc, gFeather2, gFoot, e.Dist.Count), in e.Layer, in e.Dist, in e);
                    continue;
                }
                case PlanKind.GroupClose:
                {
                    if (groupDepth == 0) continue;
                    int at = _groupOpenAt[--groupDepth];
                    _items[at] = _items[at] with { GroupCount = _itemCount - at - 1 };
                    continue;
                }
                case PlanKind.Backdrop:
                    AddItem(new CompositeItem(-1, CompositeKind.Backdrop, Affine2D.Identity, e.Alpha, ClipPx(e.Clip, scale),
                        new CornerRadius4(e.Radii.TopLeft * scale, e.Radii.TopRight * scale, e.Radii.BottomRight * scale, e.Radii.BottomLeft * scale),
                        default, 0f, e.Acrylic, 0, ScalePx(e.Rect, scale)), default, default, in e);
                    continue;
                case PlanKind.Video:
                {
                    // F078 + pixel rule R (F073): the erase quad is the hole's rect cut to the composite clip, each edge rounded
                    // to the nearest device pixel (the same rule the DirectComposition video rect uses, so the erase and the
                    // video share one edge); its strength is the punch's (VideoReady x opacity); and its shape is the hole's own
                    // rounded rect (per-corner radii) - or, when the hole is square, the in-stream rounded clip it sits under.
                    RectF erasePx = WholePx(ScalePx(e.Rect, scale));
                    if (erasePx.IsEmpty) continue;
                    RectF roundPx = default;
                    CornerRadius4 radiiPx = default;
                    if (e.Radii.TopLeft > 0f || e.Radii.TopRight > 0f || e.Radii.BottomRight > 0f || e.Radii.BottomLeft > 0f)
                    {
                        roundPx = WholePx(ScalePx(e.Hole, scale));
                        radiiPx = new CornerRadius4(e.Radii.TopLeft * scale, e.Radii.TopRight * scale, e.Radii.BottomRight * scale, e.Radii.BottomLeft * scale);
                    }
                    else if (e.RoundR > 0f)
                    {
                        roundPx = RoundPx(e.RoundRect, e.RoundR, scale);
                        radiiPx = RadiiPx(e.RoundR, scale);
                    }
                    AddItem(new CompositeItem(-1, CompositeKind.EraseVideoHole, Affine2D.Identity, e.Alpha, erasePx,
                        radiiPx, default, 0f, default, 0, roundPx), default, default, in e);
                    continue;
                }
            }

            // ── a segment ──
            ref Rec r = ref _recs[e.Slot];
            if (!r.Live) continue;
            // A posed image layer (BoxEl.CompositePose) whose stream is exactly one plain image composites as ONE bilinear
            // Image item at its current pose and holds no tiles; anything else falls back to the tiled route (and re-records).
            bool posedImage = false;
            DrawImageCmd posedCmd = default;
            RectF posedStreamClip = RectF.Infinite;
            if (r.Pose == PoseKind.Posed && e.Segment == 0 && _scanMarkCount[e.Slot] == 0)
            {
                posedImage = AnalyzePosedImage(e.Slot, out posedCmd, out posedStreamClip);
                if (!posedImage) NotePoseFallback(r.NodeIndex, r.Gen);
            }
            else if (r.Pose == PoseKind.Posed) NotePoseFallback(r.NodeIndex, r.Gen);
            ref ScanSeg sg = ref _scanSegs[e.Slot][e.Segment];
            RectF bDip = sg.Bounds;
            bool effect = r.Kind == SliceKind.Effect;
            if (effect && !ForceUncutExtents) bDip = ExtentWithinMarkerClip(in r, in bDip, scale);
            int covSlot = CoverageSlot(e.Slot);
            // Only a VIRTUAL list's content grows along its main axis as rows realize (and parks at both ends): its
            // segments keep the main-axis origin at content 0 and charge whole cells there. Any other scroll segment is
            // cut at its painted bounds on both axes, like a static one (a 40-px heading between two shelves charges a
            // ~40-row surface, not a 512-row cell per column — gate.tiles.segment-extent).
            bool grows = covSlot >= 0 && GrowsOnMainAxis(covSlot, scene);
            SegmentOrigin(r.Kind, grows ? (_recs[covSlot].Horizontal ? 1 : 0) : -1, bDip, scale,
                out float ox, out float oy, out float residualX, out float residualY);
            var frame = new SliceFrame((int)ox, (int)oy, residualX, residualY, scale);
            RectF contentPx = bDip.IsEmpty ? default : new RectF(bDip.X * scale - ox, bDip.Y * scale - oy, bDip.W * scale, bDip.H * scale);
            int sub = e.Segment * 8 + r.Role;
            ulong identity = SegmentIdentity(e.Slot, e.Segment);
            int id = table.OpenSlice(r.NodeIndex, r.Gen, r.Kind, in frame, in contentPx, r.Pose == PoseKind.Content ? r.VpNode : -1, sub, identity, grows);
            if (id < 0) continue;

            ref SliceRow row = ref table.Row(id);
            int arenaBase = withStreams ? StreamOffsetOf(e.Slot) : 0;
            row.StreamBase = arenaBase;
            row.DrawListStart = arenaBase + sg.ByteStart;
            row.DrawListLength = sg.ByteEnd - sg.ByteStart;
            // the image cross-fades of this segment the clock passed through (slice space DIP → slice-space device px)
            if ((uint)e.Slot < (uint)_scanFades.Length && _scanFades[e.Slot] is { } fadesOf)
                for (int f = 0; f < _scanFadeCount[e.Slot]; f++)
                {
                    ref ScanFade sf = ref fadesOf[f];
                    if (sf.Seg != e.Segment) continue;
                    bool live = clock >= sf.Start && (clock <= sf.End || (!float.IsNaN(lastClock) && lastClock < sf.End));
                    if (!live) continue;
                    RectF px = new(sf.Rect.X * scale - ox, sf.Rect.Y * scale - oy, sf.Rect.W * scale, sf.Rect.H * scale);
                    table.InvalidateRect(id, in px, InvalidationReason.Content);
                }
            // span index entries inside this segment
            row.SpanIndexStart = _frameSpanCount;
            for (int x = 0; x < _indexCount[e.Slot]; x++)
            {
                SliceSpan en = _index[e.Slot][x];
                if (en.ByteStart < sg.ByteStart || en.ByteStart >= sg.ByteEnd) continue;
                if (_frameSpanCount == _frameSpans.Length) Array.Resize(ref _frameSpans, _frameSpans.Length * 2);
                RectF bpx = new(en.Bounds.X * scale - ox, en.Bounds.Y * scale - oy, en.Bounds.W * scale, en.Bounds.H * scale);
                _frameSpans[_frameSpanCount++] = en with { Bounds = bpx, ByteStart = en.ByteStart - sg.ByteStart };
            }
            row.SpanIndexCount = _frameSpanCount - row.SpanIndexStart;

            // needed tiles: the composite viewport ∩ the segment's painted bounds, and a scroll slice's realized coverage.
            // A low-resolution boundary holds none (the backend replays it into one downscaled surface instead).
            byte lowRes = r.LowRes > 1 && r.Kind == SliceKind.Effect ? r.LowRes : (byte)0;
            if (r.HasFeedback && lowRes == 0) lowRes = 1;   // a feedback trail always takes the low-res surface route (scale 1 allowed): no tiles
            if (!contentPx.IsEmpty && lowRes == 0 && !posedImage)
            {
                RectF vp = e.Clip.IsInfinite ? new RectF(0f, 0f, winW, winH) : e.Clip;
                // A self-blurred leaf samples its whole blur SOURCE (the visible output grown by the kernel's reach),
                // which can extend past its composite clip: its tiles must cover that — but only as far as the blur
                // pipeline reaches from the clip (GroupCacheKey.BlurRegions cuts the source there): a blurred row
                // scrolled out of its viewport requests no tiles at all.
                if (e.HasLayer && e.Layer.Kind == (int)LayerKind.Blur && !e.InnerClip.IsEmpty)
                    vp = SelfBlurRegion.SourceRequest(e.InnerClip, e.Clip, e.Layer.BlurSigma, scale);
                RectF vpDip = Offset(vp, -e.AccDx, -e.AccDy);
                RectF vpPx = new(vpDip.X * scale - ox, vpDip.Y * scale - oy, vpDip.W * scale, vpDip.H * scale);
                bool horizontal = false;
                double velocity = 0.0, c0, c1;
                if (covSlot >= 0 && TryCoverage(covSlot, scene, scale, ox, oy, out horizontal, out velocity, out c0, out c1,
                        out double e0, out double e1))
                {
                    // Coverage clamp: the visible part of the content extent reaching past the realized rows — a band
                    // no tile can hold (the realize window trailed the viewport). Counted, must stay 0.
                    double v0 = Math.Max(horizontal ? vpPx.X : vpPx.Y, e0), v1 = Math.Min(horizontal ? vpPx.Right : vpPx.Bottom, e1);
                    if (v1 > v0 && (v0 < c0 - 0.5 || v1 > c1 + 0.5)) _coverageClamps++;
                    // clamp the cross axis to the content, the main axis to coverage ∩ content
                    if (horizontal)
                    {
                        float y0 = MathF.Max(vpPx.Y, contentPx.Y), y1 = MathF.Min(vpPx.Bottom, contentPx.Bottom);
                        vpPx = y1 > y0 ? new RectF(vpPx.X, y0, vpPx.W, y1 - y0) : default;
                        c0 = Math.Max(c0, contentPx.X); c1 = Math.Min(c1, contentPx.Right);
                    }
                    else
                    {
                        float x0 = MathF.Max(vpPx.X, contentPx.X), x1 = MathF.Min(vpPx.Right, contentPx.Right);
                        vpPx = x1 > x0 ? new RectF(x0, vpPx.Y, x1 - x0, vpPx.H) : default;
                        c0 = Math.Max(c0, contentPx.Y); c1 = Math.Min(c1, contentPx.Bottom);
                    }
                }
                else
                {
                    vpPx = vpPx.Intersect(contentPx);
                    c0 = contentPx.Y; c1 = contentPx.Bottom;
                }
                if (!vpPx.IsEmpty)
                {
                    TileGrid.Needed(vpPx, velocity, in feel, c0, c1, horizontal, _needed, _neededOrder, out int nNeeded, id);
                    // Content-derived validity: a resident tile whose want moved since its raster re-rasters, whatever
                    // the damage above said (a re-walk whose bytes changed with no damage of its own — an inherited alpha,
                    // a thumb fill, an op's halo past a node's damage rect).
                    var content = new SegmentContent(this, e.Slot, e.Segment, ox, oy, scale);
                    table.Request(id, vpPx, c0, c1, horizontal, _needed.AsSpan(0, nNeeded), _neededOrder.AsSpan(0, nNeeded), ref content);
                }
            }

            // the composite item
            if (_rowCount == _rows.Length) Array.Resize(ref _rows, _rows.Length * 2);
            _rows[_rowCount++] = row;
            NoteRow(_rowCount - 1, e.Slot, e.Segment, ox, oy);
            float alpha = 1f, sigma = 0f;
            EdgeFeather feather = default;
            RectF srcPx = default;
            if (e.HasLayer) LayerParams(in e.Layer, e.InnerClip, scale, out alpha, out sigma, out feather, out srcPx);
            ApplyDist(in e.Dist, scale, ref alpha, ref feather, out EdgeFeather feather2);
            var transform = Affine2D.Translation(ox + MathF.Round(e.AccDx * scale), oy + MathF.Round(e.AccDy * scale));
            RectF clipPx = StickyClipPx(ClipPx(e.Clip, scale), in e, scale, winW, winH);
            if (r.Pose == PoseKind.Posed)
            {
                Affine2D pmx = r.PosedM;
                if (posedImage)
                {
                    // window DIP (the stream's space, at the free pose) → device px through the pose and the accumulated offset
                    transform = new Affine2D(pmx.M11 * scale, 0f, 0f, pmx.M22 * scale, (pmx.Dx + e.AccDx) * scale, (pmx.Dy + e.AccDy) * scale);
                    if (!posedStreamClip.IsInfinite)
                    {
                        RectF sc = Offset(pmx.TransformBounds(posedStreamClip), e.AccDx, e.AccDy);
                        RectF cut = ClipAnd(e.Clip, sc);
                        clipPx = StickyClipPx(ClipPx(cut, scale), in e, scale, winW, winH);
                    }
                    posedImages++;
                }
                else transform = Affine2D.Translation(ox + MathF.Round((e.AccDx + pmx.Dx) * scale), oy + MathF.Round((e.AccDy + pmx.Dy) * scale));
            }
            // A Screen-blended or feedback item never hides what is under it: its result depends on the destination.
            bool claimsCover = !r.Screen && !r.HasFeedback;
            AddItem(new CompositeItem(id, posedImage ? CompositeKind.Image : lowRes > 0 ? CompositeKind.Direct : effect ? CompositeKind.Region : CompositeKind.Tiles, transform, alpha,
                clipPx,
                RadiiPx(e.RoundR, scale), feather, sigma, default, r.Screen ? CompositeItem.BlendScreen : (byte)0, RoundPx(e.RoundRect, e.RoundR, scale), 0, e.HasLayer ? (byte)1 : (byte)0, srcPx,
                feather2, default, e.Dist.Count, lowRes,
                claimsCover && !posedImage ? OpaquePx(in sg.Opaque, in e, scale, alpha, sigma, in feather, in feather2, in clipPx) : default,
                r.HasFeedback ? r.Feedback.Spec : default, r.HasFeedback ? r.Feedback.Warp : default, r.HasFeedback ? r.Feedback.EffectiveDecay : 0f),
                in e.Layer, in e.Dist, in e);
        }
        while (groupDepth > 0) { int at = _groupOpenAt[--groupDepth]; _items[at] = _items[at] with { GroupCount = _itemCount - at - 1 }; }
        LastPosedImages = posedImages;

        LastBudgetBytes = TileBudget.Current((int)info.SizePx.Width, (int)info.SizePx.Height);
        table.Resolve(LastBudgetBytes, _rasters, out _rasterCount);
        if (_rasterDone.Length < _rasters.Length) _rasterDone = new byte[_rasters.Length];
        Array.Clear(_rasterDone, 0, _rasterCount);
        for (int i = 0; i < _itemCount; i++)
        {
            ref CompositeItem it = ref _items[i];
            if ((it.Kind == CompositeKind.Tiles || it.Kind == CompositeKind.Region) && table.IsDegraded(it.SliceId))
                it = it with { Kind = CompositeKind.Direct };
        }
        for (int i = 0; i < _rowCount; i++)
        {
            if (_placementCount + table.TileCap > _placements.Length) Array.Resize(ref _placements, Math.Max(_placements.Length * 2, _placementCount + table.TileCap));
            int p0 = _placementCount;
            _placementCount += table.CollectPlacements(_rows[i].Id, _placements.AsSpan(_placementCount));
            ComputeWants(table, i, p0, _placementCount, scale);   // the want of each surface acquired this turn (content validity)
        }
        _exposedMissing = table.CountExposedMissing();
        PlanRasterDamage(table, scale);   // sub-tile damage: a content re-raster of a trusted surface repaints only what changed

        // Present1 parameters: the repaint set (window DIP) ∪ the visible re-rastered tiles' destinations → device px.
        var dirty = repaint;
        if (!dirty.IsFull)
        {
            for (int i = 0; i < _rasterCount && !dirty.IsFull; i++)
            {
                ref readonly TileRaster tr = ref _rasters[i];
                if (tr.Order != TileGrid.OrderVisible) continue;
                if (!TryItemOf(tr.Key.SliceId, out CompositeItem it)) continue;
                PixelRect wr = tr.Written;
                if (wr.IsEmpty) continue;
                RectF dst = new((it.Transform.Dx + tr.Key.Tx * (float)TileGrid.W + wr.Left) / scale, (it.Transform.Dy + tr.Key.Ty * (float)TileGrid.H + wr.Top) / scale,
                    (wr.Right - wr.Left) / scale, (wr.Bottom - wr.Top) / scale);
                if (!it.Clip.IsEmpty) dst = dst.Intersect(new RectF(it.Clip.X / scale, it.Clip.Y / scale, it.Clip.W / scale, it.Clip.H / scale));
                if (!dst.IsEmpty) dirty.Add(in dst);
            }
        }
        if (!dirty.IsFull)
        {
            var rects = dirty.AsSpan();
            for (int i = 0; i < rects.Length && _dirtyCount < _dirty.Length; i++)
                _dirty[_dirtyCount++] = RepaintPolicy.ToPixel(rects[i], scale, (int)info.SizePx.Width, (int)info.SizePx.Height);
        }
        // An EMPTY dirty set on a submitted frame still presents: name the whole target so DWM never keeps a stale texel.
        var present = dirty.IsFull || _dirtyCount == 0 ? PresentParams.Full : new PresentParams(_dirty.AsSpan(0, _dirtyCount));
        return new CompositeFrame(in info, _rows.AsSpan(0, _rowCount), withStreams ? _frameStreams.AsSpan(0, _streamLen) : default,
            _rasters.AsSpan(0, _rasterCount), _placements.AsSpan(0, _placementCount), _items.AsSpan(0, _itemCount), present,
            _frameSpans.AsSpan(0, _frameSpanCount), _itemLayers.AsSpan(0, _itemCount),
            _rasterDone.AsSpan(0, _rasterCount), _itemInherited.AsSpan(0, 2 * _itemCount),
            EvRasterFlags(_rasterCount), EvItemFlags(_itemCount), table.TrimmedSurfaces,   // the textures the table released (§13.1g)
            table.OwnerId);
    }

    private void AddItem(in CompositeItem item, in PushLayerCmd layer, in Inherit dist, in Plan e)
    {
        if (_itemCount == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
            Array.Resize(ref _itemLayers, _items.Length);
            Array.Resize(ref _itemInherited, _items.Length * 2);
        }
        _itemLayers[_itemCount] = item.HasLayer != 0 ? layer : default;
        _itemInherited[2 * _itemCount] = dist.Count > 0 ? dist.A : default;
        _itemInherited[2 * _itemCount + 1] = dist.Count > 1 ? dist.B : default;
        EvNoteItem(_itemCount, in e);
        _items[_itemCount++] = item;
    }

    /// <summary>Fold the distributed ancestor fades into an item's parameters: their group alpha multiplies (≈ 1 by the
    /// distribution test), and their feathers fill the item's feather slots — <paramref name="feather"/> when the item has
    /// none of its own, then <paramref name="feather2"/> (the composite multiplies both: the exact product).</summary>
    private static void ApplyDist(in Inherit dist, float scale, ref float alpha, ref EdgeFeather feather, out EdgeFeather feather2)
    {
        feather2 = default;
        for (int k = 0; k < dist.Count; k++)
        {
            LayerParams(k == 0 ? dist.A : dist.B, default, scale, out float a, out _, out EdgeFeather f, out _);
            alpha *= a;
            if (f.IsNone) continue;
            if (feather.IsNone) feather = f;
            else feather2 = f;
        }
    }

    private bool TryItemOf(int sliceId, out CompositeItem item)
    {
        for (int i = 0; i < _itemCount; i++)
            if (_items[i].SliceId == sliceId && _items[i].Kind is CompositeKind.Tiles or CompositeKind.Region) { item = _items[i]; return true; }
        item = default;
        return false;
    }

    /// <summary>The composite parameters a marker layer resolves to: group alpha, self-blur σ (device px) + its crisp
    /// source clip, and the analytic edge feather (device px) — an EdgeFade's bands and rect are window DIP.</summary>
    private static void LayerParams(in PushLayerCmd l, in RectF innerClip, float scale, out float alpha, out float sigma,
        out EdgeFeather feather, out RectF sourcePx)
    {
        alpha = l.GroupAlpha;
        sigma = 0f;
        feather = default;
        sourcePx = default;
        switch ((LayerKind)l.Kind)
        {
            case LayerKind.Blur:
                sigma = l.BlurSigma;
                if (!innerClip.IsEmpty) sourcePx = ScalePx(innerClip, scale);
                break;
            case LayerKind.EdgeFade:
            {
                RectF fr = l.DeviceRect;
                feather = new EdgeFeather(new RectF(fr.X * scale, fr.Y * scale, fr.W * scale, fr.H * scale),
                    l.FadeBandL * scale, l.FadeBandT * scale, l.FadeBandR * scale, l.FadeBandB * scale,
                    new CornerRadius4(l.Radii.TopLeft * scale, l.Radii.TopRight * scale, l.Radii.BottomRight * scale, l.Radii.BottomLeft * scale),
                    (FadeFalloff)l.FadeFalloff, l.FadeIntensity);
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RectF ScalePx(in RectF r, float scale) => r.IsEmpty ? default : new RectF(r.X * scale, r.Y * scale, r.W * scale, r.H * scale);

    /// <summary>Pixel rule R: a device-px rect with X, Y, Right and Bottom each rounded to the nearest whole pixel
    /// independently (<see cref="MidpointRounding.AwayFromZero"/>) - NOT origin + size, which would let a fractional origin
    /// move the far edge by a pixel. The rule the DirectComposition video rect applies too, so the composite erase and the
    /// video visual below it cover the same pixels. A rect thinner than half a pixel rounds to empty.</summary>
    internal static RectF WholePx(in RectF px)
    {
        if (px.IsEmpty) return default;
        float x0 = MathF.Round(px.X, MidpointRounding.AwayFromZero), y0 = MathF.Round(px.Y, MidpointRounding.AwayFromZero);
        float x1 = MathF.Round(px.Right, MidpointRounding.AwayFromZero), y1 = MathF.Round(px.Bottom, MidpointRounding.AwayFromZero);
        return x1 > x0 && y1 > y0 ? new RectF(x0, y0, x1 - x0, y1 - y0) : default;
    }

    /// <summary>A composite clip in device px, snapped OUT to whole pixels (the scissor a direct replay would set);
    /// unbounded → empty (no clip).</summary>
    /// <summary>A <see cref="DrawOp.FillRoundRect"/> that paints nothing: zero opacity, or a solid fill of alpha 0.</summary>
    private static bool InvisibleFill(DrawOp op, ReadOnlySpan<byte> payload)
    {
        if (op != DrawOp.FillRoundRect) return false;
        var f = MemoryMarshal.Read<FillRoundRectCmd>(payload);
        return f.Opacity <= 0f || (f.FillKind == 0 && f.Fill.A <= 0f);
    }

    /// <summary>The window-px rect a placed segment paints fully opaque (<see cref="CompositeItem.Opaque"/>), or empty: its
    /// scanned opaque rect (slot DIP) in window px, cut by the item's clip. The raster may place it at x·scale + AccDx·scale
    /// or at the whole-pixel placement round(AccDx·scale) (+ a residual): the rect is the INTERSECTION of both. The backend
    /// turns it into the whole pixels the composited item writes opaque. Only an item that composites its pixels unchanged
    /// can hide what lies beneath it: alpha 1, no blur, no feather, no rounded clip, no distributed fade.</summary>
    private static RectF OpaquePx(in RectF opaque, in Plan e, float scale, float alpha, float sigma,
        in EdgeFeather feather, in EdgeFeather feather2, in RectF clipPx)
    {
        if (opaque.IsEmpty || alpha < 1f || sigma > 0f || e.RoundR > 0f || e.Dist.Count > 0) return default;
        if (feather.Rect.W > 0f || feather.Rect.H > 0f || feather2.Rect.W > 0f || feather2.Rect.H > 0f) return default;
        float ex = e.AccDx * scale, ey = e.AccDy * scale, rx = MathF.Round(ex), ry = MathF.Round(ey);
        float x0 = opaque.X * scale + MathF.Max(ex, rx), y0 = opaque.Y * scale + MathF.Max(ey, ry);
        float x1 = opaque.Right * scale + MathF.Min(ex, rx), y1 = opaque.Bottom * scale + MathF.Min(ey, ry);
        bool unbounded = clipPx.W <= 0f && clipPx.H <= 0f && clipPx.X == 0f && clipPx.Y == 0f;
        if (!unbounded)
        {
            x0 = MathF.Max(x0, clipPx.X); y0 = MathF.Max(y0, clipPx.Y);
            x1 = MathF.Min(x1, clipPx.Right); y1 = MathF.Min(y1, clipPx.Bottom);
        }
        return x1 > x0 && y1 > y0 ? new RectF(x0, y0, x1 - x0, y1 - y0) : default;
    }

    /// <summary>A <see cref="DrawOp.FillRoundRect"/> that paints its whole rect FULLY OPAQUE: a solid colour of alpha 1 at
    /// opacity 1, square corners, under a translation-only transform. <paramref name="rect"/> = that rect (slot space DIP)
    /// cut by the open rectangular <paramref name="clip"/>. Its edges may be anti-aliased: the composite shrinks the rect
    /// to whole pixels before trusting it. The caller also skips it under the Additive paint blend.</summary>
    private static bool OpaqueFill(DrawOp op, ReadOnlySpan<byte> payload, in RectF clip, out RectF rect)
    {
        rect = default;
        if (op != DrawOp.FillRoundRect) return false;
        var f = MemoryMarshal.Read<FillRoundRectCmd>(payload);
        if (f.FillKind != 0 || f.Fill.A < 1f || f.Opacity < 1f) return false;
        if (f.Radii.TopLeft > 0f || f.Radii.TopRight > 0f || f.Radii.BottomRight > 0f || f.Radii.BottomLeft > 0f) return false;
        var t = f.Transform;
        if (t.M11 != 1f || t.M12 != 0f || t.M21 != 0f || t.M22 != 1f) return false;
        rect = new RectF(f.Rect.X + t.Dx, f.Rect.Y + t.Dy, f.Rect.W, f.Rect.H);
        if (!clip.IsInfinite) rect = rect.Intersect(clip);
        return !rect.IsEmpty;
    }

    private static RectF ClipPx(in RectF clip, float scale)
    {
        if (clip.IsInfinite) return default;
        if (clip.IsEmpty) return new RectF(0f, 0f, 0f, 0f);
        float x0 = MathF.Floor(clip.X * scale), y0 = MathF.Floor(clip.Y * scale);
        float x1 = MathF.Ceiling(clip.Right * scale), y1 = MathF.Ceiling(clip.Bottom * scale);
        return new RectF(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>An item's device-px clip with its sticky band line applied EXACTLY: top = floor(StickyY·s) +
    /// round(StickyDy·s) — the row the in-stream PushClip of the paint route scissored the same content at in its tiles
    /// (the tile rasterizer floors the slice-space clip; the item is placed at round(AccDy·s) plus its grid origin).
    /// <paramref name="clipPx"/> is already a superset there (the grown DIP clip). Never yields the unbounded encoding.</summary>
    private static RectF StickyClipPx(RectF clipPx, in Plan e, float scale, float winW, float winH)
    {
        if (float.IsNaN(e.StickyY)) return clipPx;
        if (clipPx.W <= 0f && clipPx.H <= 0f && clipPx.X == 0f && clipPx.Y == 0f)
            clipPx = new RectF(0f, 0f, MathF.Ceiling(winW * scale), MathF.Ceiling(winH * scale));
        float top = MathF.Floor(e.StickyY * scale) + MathF.Round(e.StickyDy * scale);
        float t = MathF.Max(clipPx.Y, top), b = MathF.Max(clipPx.Bottom, t);
        var r = new RectF(clipPx.X, t, clipPx.W, b - t);
        // an all-zero rect is the "unbounded" encoding: an empty clip there must stay empty, not become the whole target
        return r.W <= 0f && r.H <= 0f && r.X == 0f && r.Y == 0f ? new RectF(-1f, -1f, 0f, 0f) : r;
    }

    private static RectF RoundPx(in RectF round, float r, float scale) => r > 0f ? ScalePx(round, scale) : default;
    private static CornerRadius4 RadiiPx(float r, float scale) => r > 0f ? CornerRadius4.All(r * scale) : default;

    /// <summary>An effect segment's painted bounds cut to its MARKER clip (slot-space DIP, one device px of slack so the
    /// snapped-out composite clip's edge pixels stay inside): what the slice can ever show. A slice placed with its
    /// containing slice (its own role, no pose, no sticky clip, its params in that slice's space) composites under that
    /// clip, so a tile never needs content past it: a marquee's line, a clipped node's overflow, is rastered only where
    /// it can be seen (a 144-DIP player-bar title held a 960x128 surface for its 616-DIP line), and content sliding
    /// under the clip no longer moves the slice's origin (a geometry invalidation per frame). Pixel-identical: the
    /// origin stays on the grid, so every op rasters at the same device position. Not for a slice that samples outside
    /// its clip (a self-blur, a blurred fade, acrylic) or that has no tiles (a low-resolution or feedback boundary).</summary>
    /// <summary>A probe-only IDENTITY control (<c>extent-cut-identity</c>, like <see cref="ForceGroupFades"/>): every effect
    /// slice keeps its whole painted bounds instead of the extent cut to its marker clip. Process-wide; the probe flips it
    /// between two captures of the same scene. Never set by the engine.</summary>
    public static bool ForceUncutExtents { get => Volatile.Read(ref s_forceUncutExtents); set => Volatile.Write(ref s_forceUncutExtents, value); }
    private static bool s_forceUncutExtents;

    private static RectF ExtentWithinMarkerClip(in Rec r, in RectF bounds, float scale)
        => ExtentWithinMarkerClip(in bounds, scale, new ExtentFacts(r.MarkerClip, r.Pose, r.Sticky, r.Role, r.LowRes,
            r.HasFeedback, !r.Acrylic.IsNone, r.MarkerFlags, r.Layer));

    /// <summary>What <see cref="ExtentWithinMarkerClip(in RectF, float, in ExtentFacts)"/> reads of a slice (pure, so the
    /// eligibility rules are testable without a recorder).</summary>
    internal readonly record struct ExtentFacts(RectF MarkerClip, PoseKind Pose, bool Sticky, int Role, byte LowRes,
        bool Feedback, bool Acrylic, int MarkerFlags, PushLayerCmd Layer);

    internal static RectF ExtentWithinMarkerClip(in RectF bounds, float scale, in ExtentFacts f)
    {
        if (bounds.IsEmpty || f.MarkerClip.IsInfinite || f.MarkerClip.IsEmpty || f.Pose != PoseKind.None || f.Sticky
            || f.Role is not ((int)SliceRole.Main or (int)SliceRole.Layer)
            || f.LowRes > 1 || f.Feedback || f.Acrylic
            || (f.MarkerFlags & (int)(CompositeSliceFlags.InnerClip | CompositeSliceFlags.ParamsUp)) != 0)
            return bounds;
        if ((f.MarkerFlags & (int)CompositeSliceFlags.Layer) != 0
            && (f.Layer.Kind is (int)LayerKind.Blur or (int)LayerKind.Acrylic || f.Layer.BlurSigma > 0f))
            return bounds;
        float slack = 1f / MathF.Max(scale, 0.01f);
        RectF clip = new(f.MarkerClip.X - slack, f.MarkerClip.Y - slack, f.MarkerClip.W + 2f * slack, f.MarkerClip.H + 2f * slack);
        RectF cut = bounds.Intersect(clip);
        return cut.IsEmpty ? default : cut;
    }

    /// <summary>The slot whose realized coverage drives a segment's needed tiles: a scroll content slice itself, or the
    /// content slice an item band / pinned band rides; −1 for everything else.</summary>
    /// <summary>A segment's device-px tile origin, always on <see cref="TileGrid.OriginGrid"/>: an EFFECT or STATIC segment's
    /// at its content floored to the grid on both axes (an effect keeps the residual to its content as a geometry key);
    /// a VIRTUAL list's scroll content (<paramref name="scrollAxis"/> 0 vertical / 1 horizontal) only on the cross axis —
    /// the main axis stays at content space 0, since rows realize and park at both ends; any other scroll segment
    /// (<paramref name="scrollAxis"/> −1) like a static one. The tiles then cover the painted bounds, not
    /// the window grid (a 1380-px pane costs 1024+384, not 2×1024).</summary>
    internal static void SegmentOrigin(SliceKind kind, int scrollAxis, in RectF boundsDip, float scale,
        out float ox, out float oy, out float residualX, out float residualY)
    {
        ox = oy = residualX = residualY = 0f;
        if (boundsDip.IsEmpty) return;
        float bx = boundsDip.X * scale, by = boundsDip.Y * scale;
        const float g = TileGrid.OriginGrid;
        if (kind == SliceKind.Effect)
        {
            ox = MathF.Floor(bx / g) * g; oy = MathF.Floor(by / g) * g;
            residualX = bx - ox; residualY = by - oy;
            return;
        }
        if (scrollAxis != 1) ox = MathF.Floor(bx / g) * g;   // not the horizontal main axis
        if (scrollAxis != 0) oy = MathF.Floor(by / g) * g;   // not the vertical main axis
    }

    /// <summary>Does the content slot <paramref name="covSlot"/> belong to a VIRTUAL list (its realized rows move along
    /// the main axis as it scrolls)?</summary>
    private bool GrowsOnMainAxis(int covSlot, SceneRecordingSnapshot scene)
    {
        ref Rec r = ref _recs[covSlot];
        var vp = scene.HandleAt(r.VpNode);
        if (vp.IsNull || vp.Raw.Gen != r.VpGen || !scene.IsLive(vp) || !scene.HasScroll(vp)) return false;
        return scene.ScrollRef(vp).ItemCount > 0;
    }

    private int CoverageSlot(int slot)
    {
        for (int s = slot, guard = 0; s >= 0 && guard < 8; s = _recs[s].Parent, guard++)
        {
            ref Rec r = ref _recs[s];
            if (!r.Live) return -1;
            if (r.Pose == PoseKind.Content) return s;
            if (r.Kind != SliceKind.Scroll) return -1;   // only scroll-kind slices (bands) inherit their content's coverage
        }
        return -1;
    }

    /// <summary>A segment's identity: its node / role / index, the markers bracketing it, and the scroll base its slot
    /// records under. A change means the segment's bytes now describe different content.</summary>
    private ulong SegmentIdentity(int slot, int k)
    {
        ulong h = 14695981039346656037UL;
        ref Rec r = ref _recs[slot];
        Mix(ref h, (ulong)(uint)r.NodeIndex); Mix(ref h, r.Gen); Mix(ref h, (ulong)(uint)r.Role); Mix(ref h, (ulong)(uint)k);
        int marks = _scanMarkCount[slot];
        if (k > 0) { ref ScanMark a = ref _scanMarks[slot][k - 1]; Mix(ref h, (ulong)(uint)a.Cmd.NodeIndex); Mix(ref h, a.Cmd.Gen); Mix(ref h, (ulong)(uint)a.Cmd.Sub); }
        else Mix(ref h, 0x5EEDUL);
        if (k < marks) { ref ScanMark b = ref _scanMarks[slot][k]; Mix(ref h, (ulong)(uint)b.Cmd.NodeIndex); Mix(ref h, b.Cmd.Gen); Mix(ref h, (ulong)(uint)b.Cmd.Sub); }
        else Mix(ref h, 0xE0DUL);
        if (!double.IsNaN(r.Base)) Mix(ref h, (ulong)BitConverter.DoubleToInt64Bits(r.Base));
        return h;
    }

    /// <summary>After the submit: exactly the tiles the backend reported rastered (<paramref name="rasterDone"/>, parallel
    /// to the raster list) become valid — a tile it could not raster faithfully stays invalid and is scheduled again —
    /// and the slices not opened this turn retire.</summary>
    public void EndComposite(SliceTable table, in CompositeFrame frame)
        => EndComposite(table, frame.RasterDone, frame.RasterFlags, frame.ItemFlags);

    /// <summary><see cref="EndComposite(SliceTable, in CompositeFrame)"/> over the raw spans (the flag spans are evidence
    /// only: scratch-refused rasters and the group-cache outcome, recorded by the ledgers).</summary>
    public void EndComposite(SliceTable table, ReadOnlySpan<byte> rasterDone, ReadOnlySpan<byte> rasterFlags = default,
        ReadOnlySpan<byte> itemFlags = default)
    {
        int done = 0;
        long bytes = 0;
        for (int i = 0; i < _rasterCount; i++)
        {
            if (i < rasterDone.Length && rasterDone[i] != 0 && table.MarkRastered(_rasters[i].Key))
            {
                done++;
                bytes += LayerTargetBucket.Bytes(_rasters[i].W, _rasters[i].H);
            }
            else table.MarkRasterFailed(_rasters[i].Key,   // its pixels may now differ from the surface's snapshot
                beyondPlan: i < rasterFlags.Length && (rasterFlags[i] & CompositeFrameFlags.RasterBeyondPlan) != 0);
        }
        LastRasteredTiles = done;
        LastRasteredBytes = bytes;
        EvLedgerRasters(table, rasterDone, rasterFlags);   // every scheduled raster: faithful?, scratch refused?, the content it drew
        EvCaptureComposite(table, itemFlags);              // the turn's items + placements → the published composite record
        table.EndFrame();
    }

    private bool TryCoverage(int slot, SceneRecordingSnapshot scene, float scale, float ox, float oy,
        out bool horizontal, out double velocityPx, out double c0, out double c1, out double e0, out double e1)
    {
        ref Rec r = ref _recs[slot];
        horizontal = r.Horizontal;
        velocityPx = 0.0; c0 = c1 = e0 = e1 = 0.0;
        var vp = scene.HandleAt(r.VpNode);
        if (vp.IsNull || vp.Raw.Gen != r.VpGen || !scene.IsLive(vp) || !scene.HasScroll(vp)) return false;
        ref readonly ScrollState sc = ref scene.ScrollRef(vp);
        // slice-space DIP of absolute content offset A = the content box origin + (A − Base): the free local carries
        // (WindowOrigin − Base) and the rows sit at (A − WindowOrigin) inside the content box.
        double origin = horizontal ? r.BaseWorld.Dx : r.BaseWorld.Dy;
        double baseOff = double.IsNaN(r.Base) ? sc.WindowOrigin : r.Base;
        double coverEnd = sc.ItemCount > 0 ? sc.CoverEnd : (horizontal ? sc.ContentW : sc.ContentH);
        double start = origin + sc.CoverStart - baseOff;
        double end = origin + coverEnd - baseOff;
        double o = horizontal ? ox : oy;
        c0 = start * scale - o;
        c1 = end * scale - o;
        // the whole content extent [0, Content) in the same space
        e0 = (origin - baseOff) * scale - o;
        e1 = (origin + (horizontal ? sc.ContentW : sc.ContentH) - baseOff) * scale - o;
        velocityPx = sc.Velocity * scale;
        return true;
    }

    private int[] _streamOffset = new int[32];

    private void ConcatStreams()
    {
        if (_streamOffset.Length < _recs.Length) Array.Resize(ref _streamOffset, _recs.Length);
        int len = 0;
        for (int s = 0; s < _slotCount; s++) if (_recs[s].Live) len += _arenas[s]!.BytePosition;
        if (_frameStreams.Length < len) _frameStreams = new byte[Math.Max(len, _frameStreams.Length * 2)];
        int at = 0;
        for (int s = 0; s < _slotCount; s++)
        {
            _streamOffset[s] = at;
            if (!_recs[s].Live) continue;
            ReadOnlySpan<byte> b = _arenas[s]!.Bytes;
            b.CopyTo(_frameStreams.AsSpan(at));
            at += b.Length;
        }
        _streamLen = at;
    }

    private int StreamOffsetOf(int slot) => slot < _streamOffset.Length ? _streamOffset[slot] : 0;
}
