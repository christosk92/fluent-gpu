using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Extent;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text;

namespace FluentGpu.Layout;

/// <summary>
/// Flexbox layout over the SoA scene columns. Two descents: Measure (bottom-up — content/basis base sizes, honoring
/// explicit size + min/max + text content) then Arrange (top-down — distribute free space by flex-grow/shrink,
/// position by justify-content, align the cross axis by align-items/align-self incl. stretch, applying margins).
/// Direction 0 = row (main = X), 1 = column (main = Y). Wrap / grid / absolute positioning are the remaining layout work.
/// </summary>
public sealed partial class FlexLayout
{
    private readonly SceneStore _scene;
    private readonly IFontSystem _fonts;

    // `--fg layout` (EngineSwitches.LayoutDiag): per-Run layout-cost diagnostic — Measure/Arrange node-visit counts + text-shape hit/miss. A
    // regression guard for the measure-call explosion this memo cures: a healthy pass keeps measure≈O(nodes); a runaway
    // measure≫arrange flags a redundant-measure blow-up. Gated to a single bool check (zero work/alloc) when off.
    private static bool s_layoutDiag => FluentGpu.Hosting.EngineSwitches.LayoutDiag;

    // `--fg layout-overflow`: report any node ARRANGED WIDER than its parent's content box. That is the silent failure
    // mode this engine has no other signal for — a measure OVERESTIMATE (a ZStack reporting a bounded layer's explicit
    // width as its own size, MeasureZStack) meeting the Yoga-style FlexShrink=0 default (Columns.cs), which together
    // turn one over-wide layer into a permanent arrange-time overflow that a ClipToBounds ancestor then hides. Nothing
    // throws, nothing logs, and the only symptom is content silently off-screen.
    // A scroll VIEWPORT's content is supposed to overflow, so those are skipped. ClipToBounds parents are deliberately
    // NOT skipped: an intentional clip is exactly what conceals this class of bug.
    private static bool s_layoutOverflow => FluentGpu.Hosting.EngineSwitches.LayoutOverflow;
    // These are per-FRAME accumulators now (not per-Run): the standalone Run() print below snapshots deltas so its
    // per-call semantics are unchanged, while the host resets them once per frame (ResetFrameDiagCounters) and reads
    // them into FrameStats — so a probe sees the whole frame's measure/arrange/text-reshape cost across the full layout
    // + scoped relayout + phase-7 reflow re-solves. The counters themselves are always-on (cheap int increments);
    // s_layoutDiag gates only the per-Run() Console.Error.WriteLine printout below.
    private int _dMeasure, _dTextHit, _dTextMiss, _dArrange, _dMeasureMemoHit;

    /// <summary>Per-frame layout-cost counters (valid only when --fg layout; else 0). Surfaced into FrameStats.</summary>
    public int DiagMeasure => _dMeasure;
    public int DiagArrange => _dArrange;
    public int DiagTextHit => _dTextHit;
    public int DiagTextMiss => _dTextMiss;
    /// <summary>Measure calls served by the within-pass memo (no recompute). <see cref="DiagMeasure"/> counts REAL
    /// measures only, so measure+memoHit is the raw call count and memoHit/(measure+memoHit) is the redundancy rate.</summary>
    public int DiagMeasureMemoHits => _dMeasureMemoHit;

    /// <summary>Host-driven per-frame reset: zero the diag counters at the top of a frame so DiagMeasure/Arrange/TextMiss
    /// read that frame's total after all layout (full + scoped + phase-7 reflow) has run. Cheap; no-op meaning when off.</summary>
    public void ResetFrameDiagCounters() { _dMeasure = _dTextHit = _dTextMiss = _dArrange = _dMeasureMemoHit = _dOverflow = _dUnmarkedLayoutWrites = 0; }

    // Within-pass Measure memo. Measure(node, availW) is a PURE function of the node's subtree content + availW within
    // ONE layout solve (it has no external mutable input; its only side effect is writing the node's Bounds W/H). But the
    // flex algorithm calls it redundantly — a row's fixed-size pre-pass AND the main loop each measure a non-grow child
    // at the SAME width, and that doubling COMPOUNDS with depth (~45x per node measured here on the Wavee tree: 10k calls
    // for 227 nodes). Memoizing per (node, availW) for the current pass collapses the explosion — the box-level twin of
    // the per-leaf text measure cache. A hit re-asserts the Bounds W/H so Arrange (which reads Bounds for base sizes) is
    // byte-identical to the unmemoized result. Reset by bumping the generation at each top-level solve (cross-pass tree
    // mutations are thereby never reused; within a pass the tree is immutable, so the function stays pure).
    // The same slot ALSO carries a PERSISTENT (cross-pass) record of the FIRST real measure of the last pass that
    // measured this node — the call whose result propagated UP into the parent's own size (later same-pass re-measures
    // happen inside an already-decided parent box). That record is what TryResolveSizeStable compares against.
    private struct MeasureMemo
    {
        public uint Gen; public float AvailW; public float W, H;         // within-pass memo
        public uint PassGen;                                             // _measureGen of the pass the record below is from (0 = never)
        public uint NodeGen;                                             // handle generation — ABA guard on a recycled node index
        public float PAvailW, PW, PH;                                    // that pass's offered width → measured border-box size
        public ulong PInputSig;                                          // LayoutSig at that moment (see LayoutSig)
        // P4 (Operation ultra-fast GPU engine): a CROSS-PASS 2-entry ring — typically one slot per (measure-width,
        // arrange-width) pair a stretched child is asked for every pass (layout.md §2.3). Valid ONLY while
        // SceneStore.IsLayoutClean(node) holds (no dirty node anywhere in the subtree THIS frame) AND
        // SceneStore.SubtreeVersion(node) still reads the value it read at StoreRing time (a MONOTONIC, cross-FRAME
        // signal — see SceneStore.Aux.cs — that nothing in this node's subtree has been marked layout-dirty since;
        // the frame-scoped clean bit alone is NOT enough, see the 2026-09-19 fix note below) AND
        // NodeGen matches (an ABA guard against a recycled node index) AND, defensively, LayoutSig matches (the same
        // conservative superset TryResolveSizeStable already trusts) — a hit returns the stored size WITHOUT
        // descending at all.
        public uint Ring0NodeGen, Ring1NodeGen;
        public float Ring0AvailW, Ring0W, Ring0H; public ulong Ring0Sig;
        public float Ring1AvailW, Ring1W, Ring1H; public ulong Ring1Sig;
        // P4 fix (2026-09-19, "the drawer-under-context-flyout overflow"): the subtree-content version read at
        // StoreRing time. Before this field, a ring hit was validated only by SceneStore.IsLayoutClean(node) — TRUE
        // means "nothing under here was marked dirty THIS FRAME", which says nothing about frames before it. A ring
        // slot recorded for this (node, availW) BEFORE an expanded row's drawer opened (its CLOSED size) is never
        // re-stored by the reflow ticks that follow (they only refresh the width(s) they actually visit); a LATER
        // frame's unrelated full-root layout (a "…" context flyout opening forces one) revisits the row at the same
        // availW with IsLayoutClean(row) true again (nothing touched it THIS frame) and that stale CLOSED-size slot
        // hits, so Measure returns without descending, ArrangeVirtualMeasured commits the closed RowH to the extent
        // table, and the open drawer overflows into the rows below. SubtreeVersion is bumped on every LayoutDirty
        // mark for the node AND every ancestor and NEVER resets, so comparing it (not just the frame-scoped clean
        // flag) at hit time is the only thing that proves "no one has touched this subtree in ANY frame since this
        // slot was written".
        public uint Ring0Ver, Ring1Ver;
    }
    private MeasureMemo[] _memo = System.Array.Empty<MeasureMemo>();
    private uint _measureGen;

    private void BeginMeasurePass()
    {
        // P8 (Operation ultra-fast GPU engine, scroll-root-cause-2026-09-23 §5): a layout pass used to declare itself
        // a bulk mutation here, unconditionally, because Bounds writes fan out across whole subtrees off a single
        // dirty ancestor with no per-node mark. They still do — but every one of them funnels through exactly three
        // choke points, each of which now ledgers precisely instead: SetArrangedBounds (the real per-node Bounds
        // commit, on every Arrange that isn't early-outed), the three ArrangeVirtual*'s direct content-Bounds writes,
        // and ArrangeViewport's own node (ScrollState ContentW/H/ViewportW/H + the anchor/realize bookkeeping its
        // ArrangeVirtual* callees write back onto the SAME node). WriteMeasuredBounds's hypothetical scribble and
        // RestoreArrangedDescendants' restore-to-the-last-real-rect are deliberately NOT marked: both are transient
        // or a no-op relative to what is already published (the whole point of the clean-subtree early-out is that
        // nothing under it changed). Scrolling is deliberately layout-free (layout.md §6), which is exactly why a
        // coast frame never calls this at all and stays incremental.
        _measureGen++;
        int cap = _scene.Capacity;
        // Resize (copy), never re-allocate blank: growth must not drop the persistent per-node measure records that
        // TryResolveSizeStable needs (nor the P4 cross-pass ring below). The within-pass half stays correct either
        // way (it is keyed by generation).
        if (_memo.Length < cap) System.Array.Resize(ref _memo, cap);
        if (_arranged.Length < cap) System.Array.Resize(ref _arranged, cap);
        if (_measuredPass.Length < cap) System.Array.Resize(ref _measuredPass, cap);
        _pass++;
    }

    /// <summary>P4 cross-pass measure ring lookup — see the <c>Ring0*</c>/<c>Ring1*</c> fields on <see cref="MeasureMemo"/>.
    /// Excluded for viewport/grid/z-stack roots (same conservative scope as <see cref="TryResolveSizeStable"/> and the
    /// within-pass memo — those measure paths keep no per-node record to validate against).</summary>
    private bool TryRingHit(NodeHandle node, float availW, out Size2 size)
    {
        size = default;
        if (Verifying) return false;   // --fg layout-verify: the oracle's re-solve descends for real, it never rides the ring
        uint i = node.Raw.Index;
        if (i >= (uint)_memo.Length) return false;
        // Two complementary validators, both required (P4 fix, 2026-09-19):
        //  - IsLayoutClean(node): SAME-FRAME dirtiness — something in this subtree is marked dirty and not yet
        //    processed/cleared, so any stored answer for it is suspect regardless of version (the parity gate's
        //    shape: a root ring slot stored by one pass, then a repeat edit to a still-dirty leaf before any clear —
        //    without this gate Measure(root) hit the ring, never descended, and Arrange read the leaf's stale
        //    measured Bounds).
        //  - SubtreeVersion(node): CROSS-FRAME staleness — see the field comment on Ring0Ver/Ring1Ver. The clean flag
        //    alone only proves "clean THIS frame", which a ring slot written frames earlier can satisfy on a later
        //    frame's full-root re-layout even though the subtree changed in between (the drawer-overflow bug).
        if (!_scene.IsLayoutClean(node)) return false;
        uint gen = node.Raw.Gen;
        uint curVer = _scene.SubtreeVersion(node);
        ref MeasureMemo m = ref _memo[i];
        ulong sig = 0; bool sigComputed = false;
        if (m.Ring0NodeGen == gen && m.Ring0AvailW == availW && m.Ring0Ver == curVer)
        {
            sig = LayoutSig(node); sigComputed = true;
            if (sig == m.Ring0Sig) { size = new Size2(m.Ring0W, m.Ring0H); return true; }
        }
        if (m.Ring1NodeGen == gen && m.Ring1AvailW == availW && m.Ring1Ver == curVer)
        {
            if (!sigComputed) sig = LayoutSig(node);
            if (sig == m.Ring1Sig) { size = new Size2(m.Ring1W, m.Ring1H); return true; }
        }
        return false;
    }

    /// <summary>P4 cross-pass measure ring store — updates the matching slot if this (node, availW) already has one,
    /// else pushes into slot 0 (slot 0 → slot 1, the simplest 2-entry recency ring; measure-width and arrange-width
    /// naturally settle into the two slots since they're the only two distinct widths a stretched child sees per pass).</summary>
    private void StoreRing(NodeHandle node, float availW, Size2 size)
    {
        uint i = node.Raw.Index;
        if (i >= (uint)_memo.Length) return;
        uint gen = node.Raw.Gen;
        uint ver = _scene.SubtreeVersion(node);   // P4 fix (2026-09-19): stamp the version TryRingHit will require unchanged
        ulong sig = LayoutSig(node);
        ref MeasureMemo m = ref _memo[i];
        if (m.Ring0NodeGen == gen && m.Ring0AvailW == availW) { m.Ring0W = size.Width; m.Ring0H = size.Height; m.Ring0Sig = sig; m.Ring0Ver = ver; return; }
        if (m.Ring1NodeGen == gen && m.Ring1AvailW == availW) { m.Ring1W = size.Width; m.Ring1H = size.Height; m.Ring1Sig = sig; m.Ring1Ver = ver; return; }
        m.Ring1NodeGen = m.Ring0NodeGen; m.Ring1AvailW = m.Ring0AvailW; m.Ring1W = m.Ring0W; m.Ring1H = m.Ring0H; m.Ring1Sig = m.Ring0Sig; m.Ring1Ver = m.Ring0Ver;
        m.Ring0NodeGen = gen; m.Ring0AvailW = availW; m.Ring0W = size.Width; m.Ring0H = size.Height; m.Ring0Sig = sig; m.Ring0Ver = ver;
    }

    private Size2 StoreMemo(NodeHandle node, float availW, Size2 size)
    {
        uint i = node.Raw.Index;
        if (i < (uint)_memo.Length)
        {
            ref MeasureMemo m = ref _memo[i];
            if (m.PassGen != _measureGen || m.NodeGen != node.Raw.Gen)
            {
                m.PassGen = _measureGen; m.NodeGen = node.Raw.Gen;
                m.PAvailW = availW; m.PW = size.Width; m.PH = size.Height;
                m.PInputSig = LayoutSig(node);
            }
            m.Gen = _measureGen; m.AvailW = availW; m.W = size.Width; m.H = size.Height;
        }
        return size;
    }

    // FNV-1a over every LayoutInput field a PARENT can read off this node (Margin/Flex*/AlignSelf/Min/Max/explicit
    // size) plus the container-side fields and the flag column — i.e. a conservative superset of "did anything about
    // this node's participation in its parent's solve change?". TextStyle is deliberately excluded: no ancestor reads
    // it, it can only move this node's own measured size, and that is compared exactly (not hashed).
    private static void MixU(ref ulong h, uint v) { h ^= v; h *= 1099511628211UL; }
    private static void MixF(ref ulong h, float v) => MixU(ref h, BitConverter.SingleToUInt32Bits(v));
    private static void MixE(ref ulong h, in Edges4 e) { MixF(ref h, e.Left); MixF(ref h, e.Top); MixF(ref h, e.Right); MixF(ref h, e.Bottom); }

    private ulong LayoutSig(NodeHandle node)
    {
        ref LayoutInput li = ref _scene.Layout(node);
        ulong h = 14695981039346656037UL;
        MixU(ref h, (uint)_scene.Flags(node));
        MixU(ref h, _scene.IsMirrorCollapsed(node) ? 1u : 0u);   // an anchor's flow removal leaves Flags untouched
        MixU(ref h, li.Direction);
        MixF(ref h, li.Gap);
        MixE(ref h, in li.Padding);
        MixE(ref h, in li.Margin);
        MixF(ref h, li.Width); MixF(ref h, li.Height); MixF(ref h, li.AspectRatio);
        MixF(ref h, li.MinW); MixF(ref h, li.MinH); MixF(ref h, li.MaxW); MixF(ref h, li.MaxH);
        MixF(ref h, li.FlexGrow); MixF(ref h, li.FlexShrink); MixF(ref h, li.FlexBasis);
        MixU(ref h, (uint)li.AlignSelf); MixU(ref h, (uint)li.Justify); MixU(ref h, (uint)li.AlignItems);
        MixU(ref h, li.Wrap ? 1u : 0u);
        return h;
    }

    /// <summary>
    /// Scoped-relayout early-out for a dirty node that found NO layout boundary above it (LayoutInvalidator's "escape to
    /// root"). Re-measures ONLY this subtree, at exactly the width its parent offered on the last pass; if the resulting
    /// border-box size AND every parent-facing LayoutInput field are byte-identical to that pass, no ancestor's Measure
    /// or Arrange can produce a different number this frame, so the whole-window solve is skipped and the subtree is
    /// re-arranged in place inside its existing (therefore still-correct) box. Returns false — caller must escalate to the
    /// full solve — whenever anything is unproven: no record, a recycled index, a viewport/grid/z-stack root (those
    /// measure paths keep no record), a changed field signature, or a changed measured size.
    /// <para>The invariant: in this solver a parent reads a child ONLY as (a) its measured size at the availW the parent
    /// supplies and (b) its own LayoutInput {Margin, FlexGrow/Shrink/Basis, AlignSelf, Min/Max, explicit Width/Height}.
    /// Both unchanged ⇒ every ancestor and sibling rect is unchanged ⇒ this node's arranged box is unchanged.</para>
    /// </summary>
    public bool TryResolveSizeStable(NodeHandle node)
    {
        if (node.IsNull) return false;
        uint i = node.Raw.Index;
        if (i >= (uint)_memo.Length) return false;
        // Viewport/grid/z-stack measure paths return without StoreMemo, so they carry no record to compare against.
        if (_scene.HasScroll(node) || _scene.HasGrid(node) || (_scene.Flags(node) & NodeFlags.ZStack) != 0) return false;
        ref MeasureMemo rec = ref _memo[i];
        if (rec.PassGen == 0 || rec.NodeGen != node.Raw.Gen) return false;
        // Copy out BEFORE BeginMeasurePass: the probe measure re-writes this slot (and may resize the array).
        float availW = rec.PAvailW, pw = rec.PW, ph = rec.PH;
        if (LayoutSig(node) != rec.PInputSig) return false;
        // Also copy the ARRANGED rect before measuring — Measure overwrites Bounds W/H with the hypothetical size.
        RectF arranged = _scene.Bounds(node);
        BeginMeasurePass();
        var m = Measure(node, availW);
        if (m.Width != pw || m.Height != ph) return false;   // outer size moved ⇒ ancestors must re-solve (Bounds scribble is repaired by that solve)
        Arrange(node, arranged.X, arranged.Y, arranged.W, arranged.H);
        return true;
    }

    public FlexLayout(SceneStore scene, IFontSystem fonts)
    {
        _scene = scene;
        _fonts = fonts;
    }

    public void Run(NodeHandle root)
    {
        if (root.IsNull) return;
        BeginMeasurePass();
        var size = Measure(root);
        Arrange(root, 0f, 0f, size.Width, size.Height);
        VerifyParity(root, float.PositiveInfinity, 0f, 0f, size.Width, size.Height, "Run");
    }

    /// <summary>Lay out the root to FILL the window (the conventional top-level behavior) — an auto-sized root takes
    /// the full client area; an explicitly-sized root keeps its size. The slice's direct <see cref="Run(NodeHandle)"/>
    /// keeps content-sizing for golden flexbox checks.</summary>
    public void Run(NodeHandle root, Size2 window)
    {
        if (root.IsNull) return;
        // Snapshot so the per-Run print stays per-call even though the counters are now frame accumulators (host-reset).
        int m0 = _dMeasure, th0 = _dTextHit, tm0 = _dTextMiss, a0 = _dArrange, mh0 = _dMeasureMemoHit;
        BeginMeasurePass();
        Measure(root, window.Width);
        ref LayoutInput li = ref _scene.Layout(root);
        float w = float.IsNaN(li.Width) ? window.Width : li.Width;
        float h = float.IsNaN(li.Height) ? window.Height : li.Height;
        Arrange(root, 0f, 0f, w, h);
        VerifyParity(root, window.Width, 0f, 0f, w, h, "Run(window)");
        if (s_layoutDiag) Console.Error.WriteLine($"[--fg layout] measure={_dMeasure - m0} memoHit={_dMeasureMemoHit - mh0} arrange={_dArrange - a0} textHit={_dTextHit - th0} textMiss={_dTextMiss - tm0}");
    }

    /// <summary>Re-solve ONLY the subtree rooted at <paramref name="node"/> against its current Bounds (or its
    /// LayoutInput size if set — a SizeMode.Relayout animation writes the interpolated width there each tick — except
    /// under a grid / virtual list, which place it in their own slot). The parent
    /// already placed this node, so this cannot propagate upward — a scoped, per-frame-affordable relayout for live reflow.</summary>
    public void RunSubtree(NodeHandle node)
    {
        if (node.IsNull) return;
        BeginMeasurePass();
        ref LayoutInput li = ref _scene.Layout(node);
        ref RectF b = ref _scene.Bounds(node);
        float w = float.IsNaN(li.Width) ? b.W : li.Width;
        float h = float.IsNaN(li.Height) ? b.H : li.Height;
        // A scoped relayout roots HERE (this node is a layout boundary — IsolateLayout / scroll viewport / fixed-size),
        // so no ANCESTOR pass re-sizes this node's OUTER box. On a PARENT-DETERMINED axis (no explicit size — the node
        // grows/stretches/fills) the stored Bounds can be STALE and too LARGE: a sibling that reserved space after this
        // node was last arranged (a docked player bar shrinking the content region) shrinks the parent, but the firewall
        // keeps re-solving this subtree against the stale box — so the node bleeds past its parent (content paints under
        // the translucent player bar). Re-clamp each parent-determined axis to the parent's current inner content box
        // (minus this node's margin): a parent-determined boundary can never legitimately exceed its parent. Explicit
        // li.Width/Height (incl. a SizeMode.Relayout animation writing the interpolated size) are honoured untouched.
        var parent = _scene.Parent(node);
        uint ni = node.Raw.Index;
        if (!parent.IsNull && PlacesChildInSlot(parent) && (_scene.Flags(node) & NodeFlags.Relayouting) == 0
            && ni < (uint)_arranged.Length && _scene.IsArrangedValid(node))
        {
            // A grid cell / virtual row: the parent arranges it at a SLOT it computes (ArrangeGrid's colW×rowH,
            // ArrangeVirtual's ItemRect) and ignores its explicit Width/Height, so the authored size is NOT the box a full
            // layout gives it. Re-solve at the box the parent last placed it in — else a change inside a fixed-size
            // ClipToBounds card narrows that one tile to its authored 160 in a 240 track until the next full layout.
            // A SizeMode.Relayout animation (Relayouting) still re-solves at the size it wrote into li.Width/Height.
            ref RectF slot = ref _arranged[ni];
            w = slot.W; h = slot.H;
        }
        else if (!parent.IsNull)
        {
            ref RectF pb = ref _scene.Bounds(parent);
            ref LayoutInput pli = ref _scene.Layout(parent);
            if (float.IsNaN(li.Width))  w = MathF.Min(w, MathF.Max(0f, pb.W - pli.Padding.Horizontal - li.Margin.Horizontal));
            if (float.IsNaN(li.Height)) h = MathF.Min(h, MathF.Max(0f, pb.H - pli.Padding.Vertical   - li.Margin.Vertical));
        }
        Measure(node, w);
        float ox = b.X, oy = b.Y;
        Arrange(node, ox, oy, w, h);
        VerifyParity(node, w, ox, oy, w, h, "RunSubtree");
    }

    /// <summary>A parent that arranges each child at a slot it computes itself and ignores the child's explicit
    /// Width/Height: a grid (column track × row height) or a virtual list's content node (ItemRect / extent slot).</summary>
    private bool PlacesChildInSlot(NodeHandle parent)
    {
        if (_scene.HasGrid(parent)) return true;
        var viewport = _scene.Parent(parent);
        if (viewport.IsNull || !_scene.HasScroll(viewport)) return false;
        ref readonly ScrollState sc = ref _scene.ScrollRow(viewport);
        return sc.ItemCount > 0 && sc.ContentNode == parent;
    }

    // P1 presence (layout.md §4.7): a collapsed node (Element.Visible resolved false) is out of layout flow. This
    // reads the DEDICATED SceneStore.AuxFlags.Collapsed bit, not NodeFlags.Visible: NodeFlags.Visible is also
    // toggled directly by callers that only want to cull PAINT/record reachability without leaving layout flow
    // (e.g. PagedShelf's permanently-mounted measurement probe layer — see PagedShelf.cs's "RECORD-cull" comment,
    // whose contract is "layout still runs, it ignores the flag"). Collapsing layout on every NodeFlags.Visible
    // clear broke that contract (gate.shelf.binding.measurement regressed to heights=68->68->68 — the probe never
    // measured because its cells were laid out at 0x0 while record-culled). SetCollapsed still mirrors the aux bit
    // onto NodeFlags.Visible for the recorder/hit-test/LayoutSig readers, so a presence flip is still
    // seen there for free — only the LAYOUT collapse decision itself must key off the dedicated bit.
    private bool Collapsed(NodeHandle h) => _scene.IsLayoutCollapsed(h);

    // FirstVisibleChild/NextVisibleSibling: the ONE substitution point that makes every Flex/Wrap/ZStack child loop
    // skip a collapsed child ENTIRELY (no box, no margin, no gap slot — true CSS display:none, not visibility:hidden)
    // by construction, with no per-loop idx/array bookkeeping. Grid deliberately does NOT use these (its row-assign
    // loop keeps raw NextSibling) — a collapsed grid cell keeps its track (gate.presence.grid-cell-keeps-track); it
    // relies instead on the Measure/Arrange top-level short-circuit below to size 0×0.
    private NodeHandle FirstVisibleChild(NodeHandle node)
    {
        var c = _scene.FirstChild(node);
        while (!c.IsNull && Collapsed(c)) c = _scene.NextSibling(c);
        return c;
    }
    private NodeHandle NextVisibleSibling(NodeHandle c)
    {
        c = _scene.NextSibling(c);
        while (!c.IsNull && Collapsed(c)) c = _scene.NextSibling(c);
        return c;
    }

    private static bool Row(in LayoutInput li) => li.Direction == 0;
    private static float Clamp(float v, float min, float max)
    {
        if (!float.IsNaN(min) && v < min) v = min;
        if (!float.IsNaN(max) && v > max) v = max;
        return v;
    }

    private static float DefiniteWidth(in LayoutInput li, float availW)
    {
        float w = !float.IsNaN(li.Width) ? li.Width : availW;
        if (!float.IsNaN(li.MaxW) && !float.IsInfinity(w)) w = MathF.Min(w, li.MaxW);
        else if (!float.IsNaN(li.MaxW) && float.IsInfinity(w)) w = li.MaxW;
        if (!float.IsNaN(li.MinW) && !float.IsInfinity(w)) w = MathF.Max(w, li.MinW);
        return w;
    }

    /// <summary>Per-frame count of nodes arranged wider than their parent's content box (valid only when
    /// --fg layout-overflow; else 0). Surfaced into FrameStats so a probe/test can assert ZERO rather than eyeball a log.</summary>
    public int DiagLayoutOverflows => _dOverflow;
    private int _dOverflow;

    // Split out of SetArrangedBounds so the hot path is one folded static-readonly branch and this never inlines.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ReportOverflow(NodeHandle node, in RectF next)
    {
        var p = _scene.Parent(node);
        if (p.IsNull) return;
        if (_scene.HasScroll(p)) return;   // a viewport's content is SUPPOSED to overflow — that is what scrolling is
        float padH = _scene.Layout(p).Padding.Horizontal;
        float parentW = _scene.Bounds(p).W;
        float inner = parentW - padH;
        if (next.W <= inner + 0.5f) return;
        _dOverflow++;
        Console.Error.WriteLine(
            $"[fg-layout-overflow] n#{node.Raw.Index} w={next.W:0.##} exceeds parent n#{p.Raw.Index} " +
            $"content={inner:0.##} (box={parentW:0.##}, pad={padH:0.##}) by {next.W - inner:0.##}");
    }

    // P4: the LAST rect an Arrange call actually placed each node at — written ONLY here, unlike scene.Bounds()
    // (which Measure also scribbles hypothetical W/H into for a not-yet-arranged pass). AuxFlags.ArrangedValid says
    // whether the slot holds a real value yet (false for a fresh/recycled node index — cleared on CreateNode).
    private RectF[] _arranged = System.Array.Empty<RectF>();
    // Per-pass stamp grown with `_arranged`: every Measure Bounds W/H scribble records `_pass` so an Arrange early-out
    // can restore descendants this pass actually touched. A ring-hit parent never visits children — their stamp stays
    // stale and their Bounds still hold the last arranged rect (do not "restore" those).
    private int[] _measuredPass = System.Array.Empty<int>();
    private int _pass;

    private void SetArrangedBounds(NodeHandle node, in RectF next)
    {
        ref RectF b = ref _scene.Bounds(node);
        b = next;
        // --fg layout-verify's from-scratch re-solve writes Bounds (that IS what it compares) but must leave every other
        // trace of a layout pass alone: the arranged-rect column, the ArrangedValid bit, the overflow report and the
        // OnBoundsChanged edge are all real per-frame effects that a second solve would duplicate.
        if (Verifying) return;
        // P8: this IS the per-node Bounds commit (every non-early-outed Arrange call lands here exactly once) — the
        // ledger mark that replaces BeginMeasurePass's old blanket NoteBulkMutation. Unconditional: Arrange placed
        // this node whether or not the rect numerically moved, and re-copying an unchanged row costs one memcpy.
        _scene.NoteCaptureChanged((int)node.Raw.Index);
        if (s_layoutOverflow) ReportOverflow(node, in next);
        uint ai = node.Raw.Index;
        if (ai < (uint)_arranged.Length) _arranged[ai] = next;
        _scene.SetArrangedValid(node);
        NoteVerifySig(node);   // DEBUG tripwire baseline: the layout inputs this placement was computed from
        var handler = _scene.GetBoundsChangedHandler(node);   // element author's Element.OnBoundsChanged
        var hook = _scene.GetBoundsChangedHook(node);          // hook-owned observers (UseMeasuredBounds/Width) — separate slot
        if (handler is null && hook is null) return;
        // Edge-trigger against the LAST DELIVERED arranged rect — NOT the live Bounds. Measure pre-writes Bounds to each
        // node's hypothetical size earlier in this pass, so for an unconstrained node (arranged == measured, e.g. the
        // marquee's Shrink=0 text box) a Bounds-vs-next compare is always false and the handler would fire only once via
        // the mount one-shot, never again on a real content-driven size change. Comparing against the delivered baseline
        // also stops a constrained node (arranged != measured) from firing spuriously every pass. Fire on a real change,
        // OR once when a freshly-installed handler is still pending its initial delivery; then advance the baseline.
        ref RectF delivered = ref _scene.BoundsDeliveredRef(node);
        bool pending = (_scene.Flags(node) & NodeFlags.BoundsChangedPending) != 0;
        bool changed = delivered.X != next.X || delivered.Y != next.Y || delivered.W != next.W || delivered.H != next.H;
        if (changed || pending)
        {
            if (pending) _scene.Unmark(node, NodeFlags.BoundsChangedPending);
            delivered = next;
            handler?.Invoke(next);
            hook?.Invoke(next);
        }
    }

    /// <summary>P4 Arrange early-out companion: the skip path never calls <see cref="SetArrangedBounds"/> (geometry
    /// provably didn't change, so there is nothing to deliver on a REAL change), but a handler/hook installed since
    /// the last real arrange still owes its one-shot initial delivery — <see cref="NodeFlags.BoundsChangedPending"/>
    /// is exactly that signal, independent of whether the rect moved.</summary>
    private void DeliverPendingBoundsChangedIfAny(NodeHandle node, in RectF current)
    {
        if ((_scene.Flags(node) & NodeFlags.BoundsChangedPending) == 0) return;
        var handler = _scene.GetBoundsChangedHandler(node);
        var hook = _scene.GetBoundsChangedHook(node);
        _scene.Unmark(node, NodeFlags.BoundsChangedPending);
        ref RectF delivered = ref _scene.BoundsDeliveredRef(node);
        delivered = current;
        handler?.Invoke(current);
        hook?.Invoke(current);
    }

    /// <summary>Measure's live-column scribble: hypothetical W/H into <c>scene.Bounds</c>, stamped so a later Arrange
    /// early-out can restore every descendant this pass touched (a skip must not strand Grow=1 Width=NaN leaves at the
    /// hypothetical size — typically W=0 inside a clean ZStack).</summary>
    private void WriteMeasuredBounds(NodeHandle node, float w, float h)
    {
        uint i = node.Raw.Index;
        ref RectF b = ref _scene.Bounds(node);
        b = new RectF(b.X, b.Y, w, h);
        if (i < (uint)_measuredPass.Length) _measuredPass[i] = _pass;
    }

    /// <summary>Walk children Measure scribbled this pass and put their last arranged rect back. The skip already
    /// re-asserted <paramref name="n"/> itself; without this walk a clean ZStack (ring-excluded) leaves Grow=1
    /// Width=NaN descendants at the hypothetical W=0 Measure wrote.</summary>
    private void RestoreArrangedDescendants(NodeHandle n)
    {
        for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
        {
            uint ci = c.Raw.Index;
            if (ci >= (uint)_measuredPass.Length || _measuredPass[ci] != _pass) continue; // Measure never descended here (ring hit) → Bounds still hold the arranged rect
            if (_scene.IsArrangedValid(c)) { ref RectF cb = ref _scene.Bounds(c); cb = _arranged[ci]; }
            RestoreArrangedDescendants(c);
        }
    }

    // ── Measure: fill Bounds.W/H with each node's base (hypothetical) border-box size ──
    private Size2 Measure(NodeHandle node, float availW = float.PositiveInfinity)
    {
        // P1 presence: a collapsed node (incl. a collapsed scroll/grid/zstack root — "a collapsed virtual slot root
        // measures 0 at its rect") measures 0×0 unconditionally, before the viewport/grid/zstack dispatch and before
        // the memo lookup (no memo entry recorded — cheap enough to redo, and TryResolveSizeStable's LayoutSig
        // already re-escalates on the Visible flip so this never desyncs a scoped relayout).
        if (Collapsed(node))
        {
            WriteMeasuredBounds(node, 0f, 0f);
            return default;
        }
        // Within-pass memo: same (node, availW) already solved this pass ⇒ reuse it, re-asserting the Bounds W/H so the
        // Arrange pass (which reads Bounds for base main/cross sizes) sees exactly what an unmemoized recompute would.
        uint mi = node.Raw.Index;
        if (mi < (uint)_memo.Length)
        {
            ref MeasureMemo hit = ref _memo[mi];
            if (hit.Gen == _measureGen && hit.AvailW == availW)
            {
                _dMeasureMemoHit++;
                WriteMeasuredBounds(node, hit.W, hit.H);
                return new Size2(hit.W, hit.H);
            }
        }
        // A scroll/virtual viewport is a layout boundary: its size is its own box (explicit/flex), independent of
        // content — content overflow is what scrolls. (layout.md §4.3/§6.)
        // NOTE: the viewport/grid/zstack measure paths are NOT memoized (only the pure general flex path below is) — they
        // recompute every call. Their SUBTREES still benefit (the general-path nodes inside them memoize). Conservative:
        // the flex-row pre-pass/main-loop redundancy that compounds is entirely in the general path.
        bool special = _scene.HasScroll(node) || _scene.HasGrid(node) || (_scene.Flags(node) & NodeFlags.ZStack) != 0;

        // P4 cross-pass ring: a hit means this exact (node, availW) was measured before, NOTHING in the subtree has
        // moved since (SceneStore.IsLayoutClean), the node index hasn't been recycled, and this node's own
        // participation fields still hash the same — so the stored size is provably still correct and Measure can
        // return WITHOUT DESCENDING at all (no children visited, no text re-shaped). Re-assert Bounds W/H exactly
        // like the within-pass hit above so Arrange (which reads Bounds for base sizes) sees the identical value.
        if (!special && TryRingHit(node, availW, out var ringSize))
        {
            _dMeasureMemoHit++;
            WriteMeasuredBounds(node, ringSize.Width, ringSize.Height);
            return StoreMemo(node, availW, ringSize);
        }

        // Counted AFTER the memo checks: _dMeasure is REAL measure work (a memo/ring hit is a few loads, not a
        // solve), so a measure≫arrange reading now means genuine recompute redundancy rather than call-site churn
        // the memo absorbs.
        _dMeasure++;
        ref LayoutInput li = ref _scene.Layout(node);
        ref NodePaint paint = ref _scene.Paint(node);

        if (_scene.HasScroll(node)) return MeasureViewport(node, in li, availW);
        if (_scene.HasGrid(node)) return MeasureGrid(node, in li, availW);
        if ((_scene.Flags(node) & NodeFlags.ZStack) != 0) return MeasureZStack(node, in li, availW);

        float w, h;
        if (paint.VisualKind == VisualKind.Text)
        {
            float measureW = DefiniteWidth(in li, availW);
            // MaxLines/Trim need a finite wrap width even when Wrap=NoWrap — otherwise a long title measures at its
            // natural width, the grid cell's cross-size inflates, and glyphs bleed into the next column.
            bool widthConstrained = !float.IsInfinity(measureW);
            bool needsColumnBudget = li.TextStyle.Wrap != Foundation.TextWrap.NoWrap
                || li.TextStyle.Trim != Foundation.TextTrim.None;
            float maxW = widthConstrained && needsColumnBudget
                ? MathF.Max(0f, measureW)
                : float.PositiveInfinity;
            // Measure cache: skip re-shaping when (text, style, availWidth) are unchanged (the §2.3 down-rule win on a
            // scoped relayout). Pure-function key ⇒ self-invalidating; helps the real shaping path, neutral headless.
            // P4: a 2-ENTRY RING — a stretched leaf is measured at the parent's available width AND again at the final
            // arranged width, which thrashed a single slot into a miss on every single pass (Scene/Columns.cs).
            ref TextMeasureCache mc = ref _scene.MeasureCacheRef(node);
            if (mc.TryGet(paint.Text, in li.TextStyle, maxW, out var cached))
            {
                _dTextHit++;
                w = cached.Size.Width; h = cached.Size.Height;
            }
            else
            {
                _dTextMiss++;
                // Auto-fit (TextEl.MinSize / TextStyle.MinSizeDip): shrink the font so the run fits MaxLines at maxW.
                // Opt-in (MinSizeDip>0), so normal text skips this entirely. The chosen size feeds BOTH the measured box
                // and the recorder (stored as FitSize); 0 ⇒ no shrink (the recorder shapes at the authored SizeDip).
                float fit = 0f;
                TextStyle eff = li.TextStyle;
                if (li.TextStyle.MinSizeDip > 0f && li.TextStyle.MinSizeDip < li.TextStyle.SizeDip
                    && li.TextStyle.MaxLines > 0 && li.TextStyle.Wrap != Foundation.TextWrap.NoWrap && !float.IsInfinity(maxW))
                {
                    float chosen = FitTextSize(paint.Text, li.TextStyle, maxW);
                    if (chosen < li.TextStyle.SizeDip) { fit = chosen; eff = li.TextStyle with { SizeDip = chosen }; }
                }
                var m = _fonts.Measure(paint.Text, eff, maxW);
                w = m.Size.Width; h = m.Size.Height;
                // Retain the face's decoration metrics alongside the size: the recorder places underline/strikethrough
                // bars (NodePaint.TextDecorations) from this row at record time without re-touching the font seam.
                mc.Store(new TextMeasureEntry
                {
                    Valid = true, Text = paint.Text, Style = li.TextStyle, MaxW = maxW, Size = new Size2(w, h),
                    FitSize = fit,
                    UnderlineY = m.UnderlineY, UnderlineThickness = m.UnderlineThickness, StrikeY = m.StrikeY,
                });
            }
        }
        else
        {
            bool row = Row(li);
            // The width children may occupy (content box). A stretched child in a column gets the full content width
            // (so wrapped text knows where to break); a row's children share it (an upper bound is fine for wrapping).
            float measureW = DefiniteWidth(in li, availW);
            float childAvail = float.IsInfinity(measureW) ? measureW : MathF.Max(0f, measureW - li.Padding.Horizontal);
            if (li.Wrap && TryWrapMainLimit(in li, row, availW, out float wrapMainLimit))
            {
                (w, h) = MeasureWrap(node, in li, row, wrapMainLimit);   // multi-line: main is fixed, cross grows with line count
            }
            else
            {
                // In a ROW with a definite width, a flex-grow child's real width is (childAvail − the fixed siblings),
                // not the whole row. Measure its (possibly wrapping) content against THAT — otherwise a fixed pane +
                // grow content wraps to the entire window and overflows. (A column already stretches children to its
                // full width, so this only matters for the row's main axis.) NoWrap content ignores the bound, so this
                // is a no-op except where it's needed.
                float growAvail = childAvail;
                float totalGrow = 0f;
                if (row && !float.IsInfinity(childAvail))
                {
                    float fixedMain = 0f; int cc = 0;
                    for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
                    {
                        ref LayoutInput cli2 = ref _scene.Layout(c);
                        cc++;
                        if (cli2.FlexGrow > 0f) { totalGrow += cli2.FlexGrow; continue; }
                        float cm = !float.IsNaN(cli2.FlexBasis) ? cli2.FlexBasis : Measure(c, childAvail).Width;
                        fixedMain += cm + MarginMain(cli2, row);
                    }
                    if (totalGrow > 0f) growAvail = MathF.Max(0f, childAvail - fixedMain - (cc > 1 ? li.Gap * (cc - 1) : 0f));
                }

                float main = 0f, cross = 0f;
                float baseUsed = 0f, totalShrinkScaled = 0f;   // Arrange's first pass, mirrored for the shrink re-measure below
                int n = 0;
                for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
                {
                    ref LayoutInput cli = ref _scene.Layout(c);
                    // A grow child measures at its SHARE of the leftover, not the whole of it: with several grow
                    // children (three flex:1 1 0 cards), measuring each against the full remainder wraps its text at
                    // ~N× the width it will actually get — fewer measured lines, a shorter row cross, and the arrange
                    // pass (which re-measures at the true flexed width) then gets Stretch-clipped to this under-count:
                    // every card cut mid-line. Weight-dividing makes measure equal arrange for the standard
                    // equal-grow/basis-0 row; a grow child with a real basis still errs high (an upper bound), never low.
                    float cAvail = row && cli.FlexGrow > 0f && totalGrow > 0f && !float.IsInfinity(growAvail)
                        ? growAvail * (cli.FlexGrow / totalGrow) + (float.IsNaN(cli.FlexBasis) ? 0f : cli.FlexBasis)
                        : (row && cli.FlexGrow > 0f ? growAvail : childAvail);
                    var cs = Measure(c, cAvail);
                    float cMain = row ? cs.Width : cs.Height;
                    float cCross = row ? cs.Height : cs.Width;
                    // In an INDEFINITE column, a growable Basis=0 child must still contribute its content height while
                    // the parent determines its own height; otherwise the following sibling is stacked over that content.
                    // A row with a finite width is different: Basis=0 is the standard "flex: 1 1 0" contract and MUST
                    // suppress intrinsic width. Controls such as AutoSuggestBox depend on that to shrink before fixed
                    // toolbar siblings. Applying the column min-size rule to finite rows makes their stale/intrinsic
                    // content width become the flex base and lets it paint across later siblings.
                    if (!float.IsNaN(cli.FlexBasis))
                    {
                        bool indefiniteMain = row
                            ? float.IsInfinity(measureW)
                            : float.IsNaN(li.Height);
                        cMain = cli.FlexGrow > 0f && indefiniteMain
                            ? MathF.Max(cli.FlexBasis, cMain)
                            : cli.FlexBasis;
                    }
                    float baseMain = ClampMain(cli, row, cMain);
                    baseUsed += baseMain + MarginMain(cli, row);
                    totalShrinkScaled += cli.FlexShrink * baseMain;
                    cMain += MarginMain(cli, row);
                    cCross += MarginCross(cli, row);
                    main += cMain;
                    cross = MathF.Max(cross, cCross);
                    n++;
                }
                // A row that overflows its definite width hands the deficit to its Shrink children in Arrange, which
                // re-measures each at its narrower main size — where wrapped text gains lines. Take the cross from THAT
                // measure: otherwise the row reserves the one-line height its parent already stacked against, and the
                // extra line paints over the next sibling (or is clipped by a Stretch row). Same distribution as
                // Arrange's; the child's base W/H is put back after, since Arrange reads its base main from Bounds.
                if (row && !float.IsInfinity(childAvail) && totalShrinkScaled > 0f)
                {
                    float free = childAvail - baseUsed - (n > 1 ? li.Gap * (n - 1) : 0f);
                    if (free < 0f)
                    {
                        for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
                        {
                            ref LayoutInput cli = ref _scene.Layout(c);
                            if (cli.FlexShrink <= 0f) continue;
                            ref RectF cb = ref _scene.Bounds(c);
                            float baseMain = ClampMain(cli, row, !float.IsNaN(cli.FlexBasis) ? cli.FlexBasis : cb.W);
                            float fm = MathF.Max(0f, ClampMain(cli, row, baseMain + free * (cli.FlexShrink * baseMain / totalShrinkScaled)));
                            if (fm <= 0f || fm >= cb.W) continue;   // not narrower than its measure ⇒ same height
                            float bw = cb.W, bh = cb.H;
                            cross = MathF.Max(cross, Measure(c, fm).Height + MarginCross(cli, row));
                            WriteMeasuredBounds(c, bw, bh);
                        }
                    }
                }
                // A SizeMode.Reflow exit orphan is detached (FirstVisibleChild skips it) but still owns the closing
                // height: without this add, a measured virtual row snaps to the without-child extent on the remove
                // frame while the orphan paints at its last full Bounds. ONLY such an orphan is folded in — see
                // AddOrphanMain. Read the live LayoutInput — no recursive Measure (content is frozen; the Size track
                // writes the animating extent).
                AddOrphanMain(node, row, ref main, ref cross, ref n);
                if (n > 1) main += li.Gap * (n - 1);
                main += row ? li.Padding.Horizontal : li.Padding.Vertical;
                cross += row ? li.Padding.Vertical : li.Padding.Horizontal;
                w = row ? main : cross;
                h = row ? cross : main;
            }
        }

        if (!float.IsNaN(li.Width)) w = li.Width;
        if (!float.IsNaN(li.Height)) h = li.Height;

        ApplyAspect(in li, availW, ref w, ref h);

        w = Clamp(w, li.MinW, li.MaxW);
        h = Clamp(h, li.MinH, li.MaxH);

        WriteMeasuredBounds(node, w, h);
        var result = new Size2(w, h);
        StoreRing(node, availW, result);   // P4: always refresh the cross-pass ring — its later read validity is the clean-subtree gate above, not the write.
        return StoreMemo(node, availW, result);
    }

    /// <summary>Aspect-ratio (CSS aspect-ratio): derive the missing extent for a fluid box. Explicit Width+Height both
    /// set wins (aspect ignored). When both are fluid, take the offered width constraint as the box width and derive the
    /// height — the parent's cross-stretch then arranges that same width, and this measured height rides along as the
    /// main size (re-measured against the final cross size in Arrange, so measure↔arrange stay square). Shared by the
    /// general path and <see cref="MeasureZStack"/>: a ZStack used to return its tallest layer and drop the ratio.</summary>
    private static void ApplyAspect(in LayoutInput li, float availW, ref float w, ref float h)
    {
        float ar = li.AspectRatio;
        if (float.IsNaN(ar) || ar <= 0f) return;
        // The ratio-determining axis is clamped by its OWN min/max BEFORE the other extent is derived (CSS transfers
        // min/max through the ratio): a stretched Ui.AspectRatio(1, cover) { MaxWidth = 300 } in a 1000-wide column
        // derived h from the unclamped 1000 and was arranged as a 300x1000 strip. The derived axis keeps its own
        // clamp in the caller.
        bool defW = !float.IsNaN(li.Width), defH = !float.IsNaN(li.Height);
        if (defW && !defH) { w = Clamp(w, li.MinW, li.MaxW); h = w / ar; }
        else if (defH && !defW) { h = Clamp(h, li.MinH, li.MaxH); w = h * ar; }
        else if (!defW && !defH && !float.IsInfinity(availW))
        {
            w = Clamp(MathF.Max(0f, availW - li.Padding.Horizontal), li.MinW, li.MaxW);
            h = w / ar;
        }
    }

    /// <summary>Fold the main/cross size of a parent's REFLOWING exit orphans into its Measure. Allocation-free: walks
    /// the scene's existing per-parent orphan list (empty in the steady state — OrphanCount short-circuits). Does not
    /// recurse into the orphan; the Size track already wrote LayoutInput.
    /// <para>ONLY an orphan whose MAIN axis is reflow-driven contributes. A SizeMode.Reflow exit is the one exit that
    /// still occupies layout space (its track eases the main extent to 0 and the parent must ease closed with it);
    /// every other exit — the opacity cross-dissolve a Skel region orphans under its real content, a fly-out — paints
    /// OVER live content that is already measured, so adding its extent double-counts the same band. Inside a measured
    /// virtual row that doubling is not merely a wrong height: it is committed into the ExtentTable through
    /// SetMeasured and ballooned every row below for the exit duration.</para>
    /// <para>The discriminator available to layout is the reflow track's own layout-visible product: it writes the
    /// eased extent straight into the orphan's LayoutInput main axis every tick (AnimScheduler compose), so a definite
    /// main size on a detached node means a live Size track owns it. A frozen last-arranged Bounds is NOT that signal
    /// and is deliberately no longer read here — it is exactly what an opacity-only orphan still carries.</para></summary>
    private void AddOrphanMain(NodeHandle node, bool row, ref float main, ref float cross, ref int n)
    {
        if (_scene.OrphanCount == 0) return;
        var orphans = _scene.OrphanChildrenOf(node);
        if (orphans is null) return;
        for (int i = 0; i < orphans.Count; i++)
        {
            var o = orphans[i];
            if (!_scene.IsLive(o)) continue;
            ref LayoutInput oli = ref _scene.Layout(o);
            float oMain = row ? oli.Width : oli.Height;
            if (float.IsNaN(oMain)) continue;   // no Size track on this axis ⇒ the orphan holds no layout space
            float oCross = row ? oli.Height : oli.Width;
            if (float.IsNaN(oCross)) oCross = row ? _scene.Bounds(o).H : _scene.Bounds(o).W;
            main += oMain + MarginMain(oli, row);
            oCross += MarginCross(oli, row);
            if (oCross > cross) cross = oCross;
            n++;
        }
    }

    // ── Arrange: position + size children within the node's final box ──
    private void Arrange(NodeHandle node, float x, float y, float finalW, float finalH)
    {
        // P1 presence: snap to 0×0 at the offered position and stop — no recursion into a collapsed subtree (its
        // children are never visited during layout; the recorder/hit-test walks independently early-return on the
        // node's own cleared NodeFlags.Visible, so their staleness doesn't matter while collapsed).
        if (Collapsed(node)) { SetArrangedBounds(node, new RectF(x, y, 0f, 0f)); return; }

        // P4 early-out (layout.md §4.2/§4.6): nothing in this subtree (self or any descendant) was marked
        // layout-dirty this frame, a previous Arrange already recorded a real rect for this node, and the box the
        // caller is placing it into is BYTE-IDENTICAL to that rect. Every input this algorithm reads — this node's
        // own LayoutInput and every descendant's — is therefore provably unchanged (that IS what "clean" means, see
        // SceneStore.IsLayoutClean), so re-running it can only reproduce the same placement it already has.
        // EXCLUDES any node that IS or CONTAINS a scroll viewport (HasScrollInSubtree): ArrangeViewport has
        // continuous per-frame obligations — posting SetFrame, checking VirtualWindowing.NeedsRealize, re-realizing
        // rows — that are NOT gated by LayoutDirty at all (scrolling is deliberately layout-free/transform-only,
        // layout.md §6), so a CLEAN, geometrically-unchanged ANCESTOR must still be walked into whenever a viewport
        // lives anywhere below it, or that viewport silently stops being serviced every frame layout runs at all
        // (found via gate.semantic-zoom.reduced-motion: a KeepAlive overview's ItemsView viewport, several levels
        // below an unchanging full-bleed wrapper, stopped receiving Arrange calls entirely once the wrapper settled).
        uint ei = node.Raw.Index;
        // --fg layout-verify: the oracle's re-solve is a FROM-SCRATCH solve by definition — every incremental
        // short-circuit is off inside it, or it would just reproduce the incremental answer it exists to check.
        if (!Verifying
            && ei < (uint)_arranged.Length && _scene.IsArrangedValid(node) && !_scene.HasScrollInSubtree(node) && _scene.IsLayoutClean(node))
        {
            ref RectF prev = ref _arranged[ei];
            if (prev.X == x && prev.Y == y && prev.W == finalW && prev.H == finalH)
            {
                // DEBUG tripwire: this early-out is a claim about the WHOLE subtree ("nothing under here changed").
                // Re-hash it against the last real arrange and count any node that moved without a dirty mark.
                VerifyEarlyOutSubtree(node);
                ref RectF pb = ref _scene.Bounds(node);
                pb = prev;   // defensive re-assert — Measure only ever touches W/H, never X/Y, but this keeps the invariant local and cheap
                // Measure scribbled hypothetical W/H into every descendant it visited (a clean ZStack is a ring miss,
                // so the skip must not strand those children at the measure size — typically W=0 for Grow=1 Width=NaN).
                if (ei < (uint)_measuredPass.Length && _measuredPass[ei] == _pass) RestoreArrangedDescendants(node);
                DeliverPendingBoundsChangedIfAny(node, in prev);
                return;
            }
        }

        _dArrange++;
        SetArrangedBounds(node, new RectF(x, y, finalW, finalH));

        ref LayoutInput li = ref _scene.Layout(node);
        if (_scene.HasScroll(node)) { ArrangeViewport(node, finalW, finalH, in li); return; }
        if (_scene.HasGrid(node)) { ArrangeGrid(node, finalW, finalH, in li); return; }
        if ((_scene.Flags(node) & NodeFlags.ZStack) != 0) { ArrangeZStack(node, finalW, finalH, in li); return; }
        if (_scene.FirstChild(node).IsNull) return;
        bool row = Row(li);

        if (li.Wrap) { ArrangeWrap(node, finalW, finalH, in li, row); return; }

        float availMain = (row ? finalW : finalH) - (row ? li.Padding.Horizontal : li.Padding.Vertical);
        float availCross = (row ? finalH : finalW) - (row ? li.Padding.Vertical : li.Padding.Horizontal);
        float padMainStart = row ? li.Padding.Left : li.Padding.Top;
        float padCrossStart = row ? li.Padding.Top : li.Padding.Left;

        // A column's final cross-size is often only known during arrange (for example, a NavigationView content
        // frame after its fixed pane has consumed 320px). Re-measure stretch children against that final width before
        // computing main sizes, otherwise wrapped text can keep its single-line measured height/width and drag the
        // page wider than the actual frame.
        if (!row && !float.IsInfinity(availCross))
        {
            for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
            {
                ref LayoutInput cli = ref _scene.Layout(c);
                FlexAlign align = cli.AlignSelf == FlexAlign.Auto ? li.AlignItems : cli.AlignSelf;
                float crossMargin = MarginCross(cli, row);
                bool hasExplicitCross = !float.IsNaN(cli.Width);
                float childW = (align == FlexAlign.Stretch && !hasExplicitCross)
                    ? MathF.Max(0f, availCross - crossMargin)
                    : (!float.IsNaN(cli.Width) ? cli.Width : MathF.Max(0f, availCross - crossMargin));
                Measure(c, childW);
            }
        }

        // First pass: base main sizes + counts.
        int n = 0; float usedMain = 0f, totalGrow = 0f, totalShrinkScaled = 0f;
        for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
        {
            ref LayoutInput cli = ref _scene.Layout(c);
            ref RectF cb = ref _scene.Bounds(c);
            float baseMain = !float.IsNaN(cli.FlexBasis) ? cli.FlexBasis : (row ? cb.W : cb.H);
            baseMain = ClampMain(cli, row, baseMain);
            usedMain += baseMain + MarginMain(cli, row);
            totalGrow += cli.FlexGrow;
            totalShrinkScaled += cli.FlexShrink * baseMain;
            n++;
        }
        if (n > 1) usedMain += li.Gap * (n - 1);
        float free = availMain - usedMain;

        // Distribute free space → each child's final main size.
        Span<float> finalMain = n <= 64 ? stackalloc float[n] : new float[n];
        {
            int i = 0;
            for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
            {
                ref LayoutInput cli = ref _scene.Layout(c);
                ref RectF cb = ref _scene.Bounds(c);
                float baseMain = !float.IsNaN(cli.FlexBasis) ? cli.FlexBasis : (row ? cb.W : cb.H);
                baseMain = ClampMain(cli, row, baseMain);
                float fm = baseMain;
                if (free > 0f && totalGrow > 0f) fm = baseMain + free * (cli.FlexGrow / totalGrow);
                else if (free < 0f && totalShrinkScaled > 0f) fm = baseMain + free * (cli.FlexShrink * baseMain / totalShrinkScaled);
                finalMain[i] = MathF.Max(0f, ClampMain(cli, row, fm));
                i++;
            }
        }

        // Leftover after sizing → justify-content spacing.
        float consumed = 0f; for (int i = 0; i < n; i++) consumed += finalMain[i];
        { for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c)) consumed += MarginMain(_scene.Layout(c), row); }
        if (n > 1) consumed += li.Gap * (n - 1);
        float leftover = MathF.Max(0f, availMain - consumed);
        (float lead, float between) = Distribute(li.Justify, leftover, n);

        // Place children. idx increments ONLY for a visited (non-collapsed) child — keeps finalMain[idx] in lockstep
        // with the n/finalMain built above (both walk the same FirstVisibleChild/NextVisibleSibling sequence).
        float cursor = padMainStart + lead;
        int idx = 0;
        for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
        {
            ref LayoutInput cli = ref _scene.Layout(c);
            ref RectF cb = ref _scene.Bounds(c);

            float fMain = finalMain[idx];
            if (row && fMain > 0f && !float.IsInfinity(fMain))
                Measure(c, fMain);

            FlexAlign align = cli.AlignSelf == FlexAlign.Auto ? li.AlignItems : cli.AlignSelf;
            float crossMargin = MarginCross(cli, row);
            float baseCross = row ? cb.H : cb.W;
            bool hasExplicitCross = !float.IsNaN(row ? cli.Height : cli.Width);
            float fCross = (align == FlexAlign.Stretch && !hasExplicitCross)
                ? ClampCross(cli, row, availCross - crossMargin)
                : baseCross;

            float crossFree = availCross - (fCross + crossMargin);
            float crossOff = align switch
            {
                FlexAlign.Center => crossFree / 2f,
                FlexAlign.End => crossFree,
                _ => 0f,   // Start / Stretch
            };

            float mainStart = cursor + MarginMainStart(cli, row);
            float crossStart = padCrossStart + crossOff + MarginCrossStart(cli, row);

            float cx = row ? mainStart : crossStart;
            float cy = row ? crossStart : mainStart;
            float cw = row ? fMain : fCross;
            float ch = row ? fCross : fMain;

            Arrange(c, cx, cy, cw, ch);
            cursor += MarginMain(cli, row) + fMain + li.Gap + between;
            idx++;
        }
    }

    // ── Scroll / virtual viewport (layout.md §6: layout-free scroll; content arranged at content-box origin) ──

    private Size2 MeasureViewport(NodeHandle node, in LayoutInput li, float availW)
    {
        // Default ScrollView is a hard viewport boundary: it should take the size assigned by parent flex/layout and
        // publish overflow to the scroll system, not make the page/nav/sidebar grow to its full content height.
        // Popup list presenters opt into ContentSized: auto-size to rows, then clamp by MaxHeight and scroll overflow.
        _scene.TryGetScroll(node, out var sc);
        var content = sc.ContentNode;
        bool horizontal = sc.Orientation == 1;
        float w = float.IsNaN(li.Width) ? float.NaN : li.Width;
        float h = float.IsNaN(li.Height) ? float.NaN : li.Height;

        if (!sc.ContentSized)
        {
            // A vertical viewport with a FINITE offered width ADOPTS it (CSS overflow-y: the content is width-
            // constrained and only the scroll axis overflows). Without this a wide child (e.g. a horizontal card
            // strip / Home shelf) hugged the content's natural width PAST the viewport, so the page could never shrink
            // below it on resize (it was measured at +Inf in the cross-hug below). Horizontal viewports and genuinely
            // unconstrained contexts (availW = +Inf) still take the natural-width hug below. (layout.md §6.)
            if (!horizontal && float.IsNaN(w) && !float.IsInfinity(availW))
                w = MathF.Max(0f, availW);

            // D1 — natural-size fallback for NON-FLEXING virtual viewports the parent does not size. WinUI's
            // ItemsView template is a ScrollView over an ItemsRepeater (ItemsView.xaml:19-37, VerticalAlignment=Top):
            // measured unconstrained it reports the repeater's natural extent — it does not collapse to 0 (the
            // gallery ListView/ItemsView empty-panel regression). Cross axis: an auto-width vertical list fills the
            // available width (block-level, the MeasureGrid rule); main axis: the layout's ContentExtent. Gated on
            // FlexGrow == 0 so a Grow viewport (every Virtual.* factory, app fill-lists) keeps its 0 base — a
            // 10k-row list must never inject a ~440000px flex basis; grow/stretch size it at arrange and
            // realize-after-layout (ArrangeViewport tail) re-windows against the published viewport.
            if (sc.ItemCount > 0 && li.FlexGrow == 0f)
            {
                if (!horizontal && float.IsNaN(w) && !float.IsInfinity(availW))
                    w = MathF.Max(0f, availW);
                if (horizontal ? float.IsNaN(w) : float.IsNaN(h))
                {
                    float cross = horizontal
                        ? (float.IsNaN(h) ? 0f : MathF.Max(0f, h - li.Padding.Vertical))
                        : (float.IsNaN(w) ? 0f : MathF.Max(0f, w - li.Padding.Horizontal));
                    // Viewport-aware layout: seed a best-known main estimate (the offered width for a horizontal shelf)
                    // so ContentExtent is reasonable this frame; arrange + realize-after-layout correct it.
                    if (sc.Layout is IViewportVirtualLayout dvl)
                        dvl.SetViewport(horizontal ? (float.IsInfinity(availW) ? 0f : MathF.Max(0f, availW)) : 0f, cross);
                    // Natural measured stacks publish their extent to an OUTER scroller. Refresh realized row extents
                    // before reading ContentExtent so a reflowing drawer pushes trailing page content in this same
                    // layout pass; waiting for ArrangeVirtualMeasured leaves the parent one frame behind and produces
                    // the visible album-only clip/snap. Filling list viewports never enter this branch.
                    if (sc.Layout is MeasuredStackVirtualLayout measured
                        && !content.IsNull && _scene.IsLive(content))
                        RefreshNaturalMeasuredStack(in sc, measured, content, cross, horizontal);
                    float main = sc.Layout is not null ? sc.Layout.ContentExtent(sc.ItemCount, cross)
                               : sc.Extent is { } extents ? (float)extents.Total
                               : 0f;
                    if (horizontal) w = main + li.Padding.Horizontal;
                    else h = main + li.Padding.Vertical;
                }
            }
            // Cross-axis hug (non-virtual content) — a viewport clips/scrolls ONLY its scroll axis; its cross axis has
            // no overflow, so size it to the content when the parent leaves it indefinite. Else a horizontal strip in an
            // auto-height column (the cross axis is the column's MAIN axis, which neither cross-stretch nor Grow fills)
            // collapses to 0. The scroll axis is never touched here — it stays parent-assigned (never self-grows).
            if (sc.ItemCount == 0 && !content.IsNull && _scene.IsLive(content)
                && (horizontal ? float.IsNaN(h) : float.IsNaN(w)))
            {
                float crossAvailW = horizontal ? float.PositiveInfinity
                                  : (!float.IsNaN(w) ? MathF.Max(0f, w - li.Padding.Horizontal) : float.PositiveInfinity);
                var cs = Measure(content, crossAvailW);
                if (horizontal) h = cs.Height + li.Padding.Vertical;
                else w = cs.Width + li.Padding.Horizontal;
            }
            if (float.IsNaN(w)) w = 0f;
            if (float.IsNaN(h)) h = 0f;
            w = Clamp(w, li.MinW, li.MaxW);
            h = Clamp(h, li.MinH, li.MaxH);
            WriteMeasuredBounds(node, w, h);
            return new Size2(w, h);
        }

        if (content.IsNull || !_scene.IsLive(content))
        {
            if (float.IsNaN(w)) w = 0f;
            if (float.IsNaN(h)) h = 0f;
        }
        else if (float.IsNaN(w) || float.IsNaN(h))
        {
            float outerW = DefiniteWidth(in li, availW);
            float contentAvailW = horizontal
                ? float.PositiveInfinity
                : (!float.IsNaN(w) ? MathF.Max(0f, w - li.Padding.Horizontal)
                   : !float.IsInfinity(outerW) ? MathF.Max(0f, outerW - li.Padding.Horizontal)
                   : float.PositiveInfinity);
            var cs = Measure(content, contentAvailW);
            if (float.IsNaN(w)) w = cs.Width + li.Padding.Horizontal;
            if (float.IsNaN(h)) h = cs.Height + li.Padding.Vertical;
        }
        w = Clamp(w, li.MinW, li.MaxW);
        h = Clamp(h, li.MinH, li.MaxH);
        WriteMeasuredBounds(node, w, h);
        return new Size2(w, h);
    }

    /// <summary>Updates an auto-main measured stack's intrinsic extent before its viewport is measured. Allocation-free;
    /// arrange repeats the measure to preserve the normal measured-virtual anchor/deferred-correction contract.
    /// <para>INVARIANT: this commits into the layout's SHARED ExtentTable, so it runs only against a REAL cross offer.
    /// The caller's cross is 0 whenever the viewport's cross axis is still indefinite (an auto-width list measured at
    /// +Inf leaves w = NaN ⇒ cross = 0), and every row measured at a 0/degenerate width answers with a hypothetical
    /// extent — text wrapped to nothing, images at their intrinsic fallback. Writing THAT through SetMeasured is not a
    /// transient bad frame: it is a durable corruption of the table every later pass, anchor re-pin and scrollbar reads
    /// as measured truth. Arrange re-measures against the real cross, so skipping here costs nothing.</para></summary>
    private void RefreshNaturalMeasuredStack(in ScrollState sc, MeasuredStackVirtualLayout layout,
                                             NodeHandle content, float cross, bool horizontal)
    {
        if (!(cross > 0f) || float.IsInfinity(cross)) return;   // NaN-safe: no real cross offer ⇒ nothing may enter the table
        _ = layout.ContentExtent(sc.ItemCount, cross);       // ensure/reset the backing extent table before SetMeasured
        int ord = 0;
        for (var row = _scene.FirstChild(content); !row.IsNull; row = _scene.NextSibling(row), ord++)
        {
            int index = VirtualIndex(in sc, ord);
            if ((uint)index >= (uint)sc.ItemCount) continue;
            ref LayoutInput rli = ref _scene.Layout(row);
            float mL = rli.Margin.Left, mT = rli.Margin.Top, mR = rli.Margin.Right, mB = rli.Margin.Bottom;
            // Commit EXACTLY what ArrangeVirtual pass 1 commits (the margin box, measured at the same availW): the two
            // write one shared table, and a margin-less write here shrank the natural viewport by every row's margin
            // while arrange grew the content back — clipped bottom rows and an inner scroll that never healed.
            float measureW = horizontal ? float.PositiveInfinity
                           : MathF.Max(0f, layout.ItemRect(index, cross).W - mL - mR);
            if (measureW <= 0f) continue;                    // same rule per row: a margin-eaten slot is not a measurement
            var measured = Measure(row, measureW);
            layout.SetMeasured(index, horizontal ? measured.Width + mL + mR : measured.Height + mT + mB, cross);
        }
    }

    private void ArrangeViewport(NodeHandle node, float finalW, float finalH, in LayoutInput li)
    {
        // P8: ScrollState is a captured sparse column, and this call (directly below, and via the ArrangeVirtual*
        // callees it dispatches to — all keyed on this SAME node) is the only place a viewport's ContentW/H,
        // ViewportW/H, WindowOrigin/Cover* and the realize-worklist NodeFlags bit get written. One mark up front covers
        // every one of those writes for this node (NoteCaptureChanged is idempotent per publication).
        if (!Verifying) _scene.NoteCaptureChanged((int)node.Raw.Index);
        // Snapshot by value: arranging content may add nested-viewport rows to the scroll table and relocate refs.
        _scene.TryGetScroll(node, out var sc0);
        var content = sc0.ContentNode;
        bool horizontal = sc0.Orientation == 1;
        float innerW = finalW - li.Padding.Horizontal;
        float innerH = finalH - li.Padding.Vertical;
        float padL = li.Padding.Left, padT = li.Padding.Top;

        (float contentW, float contentH) = sc0.ItemCount > 0
            ? ArrangeVirtual(node, in sc0, content, innerW, innerH, padL, padT, horizontal)
            : ArrangePlainScroll(content, innerW, innerH, padL, padT, horizontal);

        // Publish ContentSize + viewport extent (Layout-owned fields) via a fresh ref (post-recursion).
        ref ScrollState sc = ref _scene.ScrollRef(node);
        bool viewportPaintChanged = sc.ContentW != contentW || sc.ContentH != contentH
            || sc.ViewportW != innerW || sc.ViewportH != innerH;
        sc.ContentW = contentW; sc.ContentH = contentH;
        sc.ViewportW = innerW; sc.ViewportH = innerH;
        if (sc.ItemCount == 0)
        {
            // A plain scroller's coverage is its whole content, arranged at origin 0.
            sc.WindowOrigin = 0.0;
            sc.CoverStart = 0.0;
            sc.CoverEnd = horizontal ? contentW : contentH;
        }
        // Scrollbar geometry and auto edge masks are emitted by the viewport span itself. A scoped layout can update
        // these ScrollState fields after normal reconciliation, so explicitly invalidate the viewport/ancestor span
        // instead of relying on an unrelated child dirty bit to defeat retained-subtree reuse.
        if (viewportPaintChanged && !Verifying) _scene.Mark(node, NodeFlags.PaintDirty);

        // The content translate for THIS frame's shown offset (scroll rework §2/§6): UI-side for hit-testing and the
        // published frame; the render poser re-poses it at present time. A shown offset past the new clamp lands on
        // the clamp (the plan is re-clamped by the host's SetExtent in the same frame).
        if (!content.IsNull && _scene.IsLive(content) && !Verifying)
        {
            double max = Math.Max(0.0, (double)(horizontal ? contentW : contentH) * (sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f) - (horizontal ? innerW : innerH));
            if (sc.Offset > max) sc.Offset = max;
            if (sc.Offset < 0.0) sc.Offset = 0.0;
            float trans = ScrollContentPose.Translate(sc.WindowOrigin, sc.Offset, _scene.DeviceScale);
            ref NodePaint cp = ref _scene.Paint(content);
            Affine2D before = cp.LocalTransform;
            ScrollContentPose.WriteContentTransform(ref cp, in _scene.Bounds(content), horizontal, trans, sc.ZoomFactor);
            // The pose is a TRANSFORM of the content, never a content change: the retained tiles hold the content in its
            // own space and the composite re-poses them, so a re-window must not dirty the content's paint (that
            // re-rastered every visible tile of the list each frame). Unchanged ⇒ no mark at all.
            if (!before.Equals(cp.LocalTransform)) _scene.Mark(content, NodeFlags.TransformDirty);
        }

        // D1 realize-after-layout: the realize window was computed BEFORE this arrange published the real viewport
        // size (a mount realizes against the Height hint; a relayout can also grow the host). If the realized window
        // no longer covers the present-time window, flag the node — the host (AppHost.Paint) re-realizes + re-runs
        // scoped layout inside the SAME frame (bounded), so the first presented frame shows the real rows.
        if (sc.ItemCount > 0 && !Verifying && sc.Extent is { } ext)
        {
            float vpExtent = horizontal ? sc.ViewportW : sc.ViewportH;
            var feel = FluentGpu.Scroll.Diag.ScrollTunables.Current;
            var rw = Virtualizer.Plan(ext, sc.Offset, sc.Velocity, vpExtent, in feel, sc.AnchorIndex);
            if (ScrollContentPose.NeedsRealize(in sc, in rw))
                _scene.Mark(node, NodeFlags.VirtualRangeDirty);
        }
    }

    private (float w, float h) ArrangePlainScroll(NodeHandle content, float innerW, float innerH, float padL, float padT, bool horizontal)
    {
        if (content.IsNull) return (0f, 0f);
        // Fill the cross axis to the viewport during ARRANGE; do not write LayoutInput.Width/Height here. LayoutInput is
        // the reconciled model, not mutable layout scratch. Mutating it poisoned content-sized popup lists: the first
        // arrange wrote the 96px menu minimum into the column, so the next measure clipped long menu labels to 96px.
        var cs = Measure(content, horizontal ? float.PositiveInfinity : innerW);   // vertical scroll: wrap text to the viewport width
        float contentW = horizontal ? cs.Width : innerW;
        float contentH = horizontal ? innerH : cs.Height;
        Arrange(content, padL, padT, contentW, contentH);   // content-box origin; the content translate adds WindowOrigin − Offset
        return (contentW, contentH);
    }

    /// <summary>
    /// Virtual arrangement (scroll rework §6): every realized row is measured, its measured extent is committed to the
    /// viewport's <see cref="IExtentSource"/> through <see cref="Virtualizer.ApplyMeasured"/> — a correction ABOVE the
    /// anchor shifts the plan's coordinate frame in the same call, so the anchor row's screen position is unchanged
    /// before this frame ever presents — and the rows are arranged RELATIVE to <see cref="ScrollState.WindowOrigin"/>
    /// (<c>OffsetOf(WindowOriginIndex)</c>) as small floats. Fixed-geometry layouts (grids, shelves) place cells by
    /// <see cref="IVirtualLayout.ItemRect"/> on the cross axis and by the extent source on the scroll axis.
    /// </summary>
    private (float w, float h) ArrangeVirtual(NodeHandle node, in ScrollState sc, NodeHandle content,
                                              float innerW, float innerH, float padL, float padT, bool horizontal)
    {
        if (content.IsNull || sc.Extent is not { } ext) return (0f, 0f);
        var layout = sc.Layout;
        float cross = horizontal ? innerH : innerW;
        if (ext.Count != sc.ItemCount) ext.Resize(sc.ItemCount);
        int anchorIndex = Math.Clamp(sc.AnchorIndex, 0, Math.Max(0, sc.ItemCount - 1));
        // A cross-size change reflows a layout that places items off it (a responsive grid's column count, a lined flow's
        // re-wrap, an aspect grid's row height): the anchor item moves to another row with no plan shift, so the same offset
        // shows other items. Take the anchor's offset at the cross the last arrange used, BEFORE the reflow below; pass 0
        // shifts the frame by how far it moved. A list resting at the top keeps no anchor, and a viewport not arranged yet
        // (the mount realizes at a hint cross) has no position on screen to keep.
        float arrangedCross = horizontal ? sc.ContentH : sc.ContentW;
        double reflowFrom = double.NaN;
        if (!Verifying && ext is VirtualLayoutExtent { Cross: > 0f } reflowing && reflowing.Cross == arrangedCross
            && cross > 0f && cross != arrangedCross && sc.Offset > 0.0)
            reflowFrom = ext.OffsetOf(anchorIndex);
        if (layout is IViewportVirtualLayout vl) vl.SetViewport(horizontal ? innerW : innerH, cross);
        if (ext is VirtualLayoutExtent vle && cross > 0f) vle.Cross = cross;
        _ = ext.Total;   // a lazily-tabled measured layout must own its table BEFORE the first SetMeasured below

        GridVirtualLayout? measuredGrid = layout is GridVirtualLayout { IsMeasured: true } grid ? grid : null;
        measuredGrid?.ResetMeasurePass(sc.ItemCount, cross);
        bool measured = ext is MeasuredExtent || layout is IMeasuredVirtualLayout;
        var vpId = new ScrollViewportId((int)node.Raw.Index, node.Raw.Gen);
        var slots = _scene.PlanSlots;

        // Pass 0 — an out-of-band extent rewrite since the last pass (a wholesale reseed, IAnchoredReseedLayout) or the
        // cross-size reflow above moved the rows above the anchor with no plan shift: anchor it here, in the same call as this
        // pass's measured corrections, so the rewrite and the re-measure that corrects it net to the anchor row staying
        // where the user sees it.
        double shift = 0.0;
        double rebase = double.IsNaN(reflowFrom) ? 0.0 : ext.OffsetOf(anchorIndex) - reflowFrom;
        if (!Verifying && layout is IAnchoredReseedLayout reseeded
            && reseeded.TakeReseedShift(anchorIndex, out double reseedDelta))
            rebase += reseedDelta;
        if (rebase != 0.0)
        {
            bool anchored = slots is not null && slots.Shift(vpId, rebase);
            shift += rebase;
            if (FluentGpu.Scroll.Diag.ScrollProbe.Level != FluentGpu.Scroll.Diag.ProbeLevel.Off)
                FluentGpu.Scroll.Diag.ScrollProbe.Extent((int)node.Raw.Index, 0, anchorIndex, rebase, anchored,
                    FluentGpu.Scroll.Diag.ProbeExtentCause.Structural);
        }

        // Pass 1 — measure every realized row and commit its extent (the ONE extent write path; corrections above the
        // anchor shift the plan's frame in the same call, and this frame's displayed offset shifts with it).
        int ord = 0;
        for (var rc = _scene.FirstChild(content); !rc.IsNull; rc = _scene.NextSibling(rc), ord++)
        {
            int index = VirtualIndex(in sc, ord);
            if ((uint)index >= (uint)sc.ItemCount) continue;
            ref LayoutInput rli = ref _scene.Layout(rc);
            float mL = rli.Margin.Left, mT = rli.Margin.Top, mR = rli.Margin.Right, mB = rli.Margin.Bottom;
            float slotCross;
            if (layout is not null)
            {
                var rect = layout.ItemRect(index, cross);
                slotCross = horizontal ? rect.H : rect.W;
            }
            else slotCross = cross;
            float measureW = horizontal ? float.PositiveInfinity : MathF.Max(0f, slotCross - mL - mR);
            var cs = Measure(rc, measureW);
            if (!measured) continue;
            float main = horizontal ? cs.Width + mL + mR : cs.Height + mT + mB;
            if (!Verifying)
            {
                double delta = slots is not null
                    ? Virtualizer.ApplyMeasured(ext, index, main, anchorIndex, slots, vpId)
                    : ext.SetMeasured(index, main, anchorIndex);
                shift += delta;
            }
        }

        // Pass 2 — arrange relative to the window's arrange origin (Virtualizer.ArrangeOriginIndex, chosen at realize) at
        // the corrected slots (row-synced for grids). A retained row's box is unchanged by a window shift.
        double origin = ext.OffsetOf(Math.Clamp(sc.WindowOriginIndex, 0, sc.ItemCount));
        ord = 0;
        for (var rc = _scene.FirstChild(content); !rc.IsNull; rc = _scene.NextSibling(rc), ord++)
        {
            int index = VirtualIndex(in sc, ord);
            if ((uint)index >= (uint)sc.ItemCount) continue;
            ref LayoutInput rli = ref _scene.Layout(rc);
            float mL = rli.Margin.Left, mT = rli.Margin.Top, mR = rli.Margin.Right, mB = rli.Margin.Bottom;
            float mainPos = (float)(ext.OffsetOf(index) - origin);
            float mainExtent, crossPos, crossExtent;
            if (layout is not null)
            {
                var rect = layout.ItemRect(index, cross);
                mainExtent = horizontal ? rect.W : rect.H;
                crossPos = horizontal ? rect.Y : rect.X;
                crossExtent = horizontal ? rect.H : rect.W;
            }
            else
            {
                mainExtent = (float)ext.ExtentOf(index);
                crossPos = 0f;
                crossExtent = cross;
            }
            float x = horizontal ? mainPos + mL : crossPos + mL;
            float y = horizontal ? crossPos + mT : mainPos + mT;
            float w = horizontal ? mainExtent - mL - mR : crossExtent - mL - mR;
            float h = horizontal ? crossExtent - mT - mB : mainExtent - mT - mB;
            Arrange(rc, x, y, MathF.Max(0f, w), MathF.Max(0f, h));
        }

        float mainContent = (float)ext.Total;
        float contentW = horizontal ? mainContent : innerW;
        float contentH = horizontal ? innerH : mainContent;
        _scene.Bounds(content) = new RectF(padL, padT, contentW, contentH);
        // P8: bypasses SetArrangedBounds (this is the virtualization content node's synthetic box, not an
        // authored child) — mark it directly so its captured Bounds row isn't left stale.
        if (!Verifying) _scene.NoteCaptureChanged((int)content.Raw.Index);

        ref ScrollState scw = ref _scene.ScrollRef(node);
        scw.WindowOrigin = origin;
        ScrollContentPose.CoverageOf(ext, sc.PersistentPrefixCount, sc.FirstRealized, sc.LastRealized, out scw.CoverStart, out scw.CoverEnd);
        if (shift != 0.0)
        {
            scw.Offset += shift;   // this frame's displayed offset rides the same frame shift the plan just took
            _scene.ScrollHandleFor(node)?.NoteFrameShift(shift);   // the live contact origin / last shown move with the frame
            if (FluentGpu.Scroll.Diag.ScrollProbe.Level != FluentGpu.Scroll.Diag.ProbeLevel.Off)
                FluentGpu.Scroll.Diag.ScrollProbe.Extent((int)node.Raw.Index, 0, anchorIndex, shift, true,
                    FluentGpu.Scroll.Diag.ProbeExtentCause.FrameShift);
        }
        return (contentW, contentH);
    }

    private static int VirtualIndex(in ScrollState sc, int childOrdinal)
    {
        int prefix = Math.Clamp(sc.PersistentPrefixCount, 0, sc.ItemCount);
        return childOrdinal < prefix ? childOrdinal : sc.FirstRealized + childOrdinal - prefix;
    }

    // ── Z-stack: children overlay at the origin (each filling the box unless explicitly sized), painted in order ──

    private Size2 MeasureZStack(NodeHandle node, in LayoutInput li, float availW)
    {
        float childAvail = DefiniteWidth(in li, availW);
        if (!float.IsInfinity(childAvail)) childAvail = MathF.Max(0f, childAvail - li.Padding.Horizontal);
        float maxW = 0f, maxH = 0f;
        for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
        {
            // A3: a MeasureUnboundedWidth child (e.g. a rail tooltip) opts out of the stack's own constrained
            // width and reports its natural content width instead of being squeezed to childAvail.
            bool unbounded = _scene.Layout(c).MeasureUnboundedWidth;
            var cs = Measure(c, unbounded ? float.PositiveInfinity : childAvail);
            maxW = MathF.Max(maxW, cs.Width); maxH = MathF.Max(maxH, cs.Height);
        }
        float w = float.IsNaN(li.Width) ? maxW + li.Padding.Horizontal : li.Width;
        float h = float.IsNaN(li.Height) ? maxH + li.Padding.Vertical : li.Height;
        // The ratio owns the box like any other: without it a square ZStack tile measured to its tallest layer.
        ApplyAspect(in li, availW, ref w, ref h);
        w = Clamp(w, li.MinW, li.MaxW); h = Clamp(h, li.MinH, li.MaxH);
        WriteMeasuredBounds(node, w, h);
        return new Size2(w, h);
    }

    /// <summary>A ZStack's container-level horizontal default: <see cref="FlexJustify"/> is a main-axis DISTRIBUTION,
    /// but an overlay stack has one child per layer, so only its alignment sense carries over. Space* ⇒ Start.</summary>
    private static FlexAlign StackJustify(FlexJustify j) => j switch
    {
        FlexJustify.Center => FlexAlign.Center,
        FlexJustify.End => FlexAlign.End,
        _ => FlexAlign.Start,
    };

    private void ArrangeZStack(NodeHandle node, float finalW, float finalH, in LayoutInput li)
    {
        float innerW = finalW - li.Padding.Horizontal, innerH = finalH - li.Padding.Vertical;
        float padL = li.Padding.Left, padT = li.Padding.Top;
        for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
        {
            // Snapshot the child's layout inputs BEFORE any re-measure below: a ref into the SoA column must not be
            // held across a call that can touch the store.
            float mL, mT, mR, mB, declW, declH, minW, maxW, minH, maxH;
            FlexAlign align, justify;
            bool unboundedW;
            {
                ref LayoutInput cli = ref _scene.Layout(c);
                mL = cli.Margin.Left; mT = cli.Margin.Top; mR = cli.Margin.Right; mB = cli.Margin.Bottom;
                declW = cli.Width; declH = cli.Height;
                minW = cli.MinW; maxW = cli.MaxW; minH = cli.MinH; maxH = cli.MaxH;
                unboundedW = cli.MeasureUnboundedWidth;
                // A ZStack has no main axis, so BOTH axes are alignment (the WinUI overlay-Grid model):
                //   vertical   = AlignSelf, falling back to the stack's AlignItems
                //   horizontal = JustifySelf, falling back to the stack's Justify (a distribution mapped to its
                //                alignment sense — Space* has no meaning with one child per layer, so it reads Start)
                // plus the child's leading Margin as the offset. Start/Stretch/Auto keep the legacy top-left origin, so
                // an existing ZStack that never authored Justify/JustifySelf arranges exactly as before.
                align = cli.AlignSelf == FlexAlign.Auto ? li.AlignItems : cli.AlignSelf;
                justify = cli.JustifySelf == FlexAlign.Auto ? StackJustify(li.Justify) : cli.JustifySelf;
            }

            float slotW = MathF.Max(0f, innerW - mL - mR);   // the child's slot: the stack minus its own margin
            float slotH = MathF.Max(0f, innerH - mT - mB);
            // Explicit child size, else fill the slot, then the layer's own Min/Max, as a flex parent applies them
            // (ClampMain/ClampCross). Without the clamp a capped fill layer (a MaxWidth caption pill or dialog card)
            // spans the whole stack, and a MinWidth layer is squeezed below its floor when the stack is narrower.
            float cw = Clamp(float.IsNaN(declW) ? slotW : declW, minW, maxW);
            float ch = Clamp(float.IsNaN(declH) ? slotH : declH, minH, maxH);

            // An AUTO-sized child that is CENTERED or END-aligned takes its DESIRED extent on that axis — the CSS /
            // XAML rule that only a stretched child fills. Without it, alignment is silently inert on an auto-sized
            // layer: the child fills the slot, so there is no free space left to align it within (this is why a
            // content-sized badge could never be parked in a corner). Start/Stretch keep filling, which is both the
            // legacy behaviour and what a full-bleed backdrop/scrim layer wants.
            unboundedW &= float.IsNaN(declW);   // an explicit Width always wins, flag or not
            bool desiredW = !unboundedW && float.IsNaN(declW) && (justify == FlexAlign.Center || justify == FlexAlign.End);
            bool desiredH = float.IsNaN(declH) && (align == FlexAlign.Center || align == FlexAlign.End);
            if (unboundedW)
            {
                // A3: measure at the child's own natural width regardless of the stack's slot — Min-against-slotW
                // below would just clamp it straight back down to the very constraint it opted out of.
                var desired = Measure(c, float.PositiveInfinity);
                cw = desired.Width;
                if (desiredH) ch = MathF.Min(ch, desired.Height);
            }
            else if (desiredW || desiredH)
            {
                var desired = Measure(c, slotW);
                if (desiredW) cw = MathF.Min(cw, desired.Width);
                if (desiredH) ch = MathF.Min(ch, desired.Height);
            }

            float freeV = MathF.Max(0f, innerH - ch - mT - mB);
            // Width overflow may go NEGATIVE — but ONLY for the two layers that asked to escape the slot, because a
            // negative freeH is what walks a child off the stack's LEADING edge:
            //   • justify == End — "End" means flush against the trailing edge, and that promise is what an oversized
            //     layer is authored for (a 200-wide tip right-anchored on a 44-wide rail hangs off the left).
            //   • unboundedW (A3) — the child explicitly opted out of the stack's width; re-clamping here would put
            //     back exactly the constraint the flag removed in the measure branch above.
            // Center stays CLAMPED at 0 (legacy): centering an overflow would shift EVERY oversized centered layer
            // half its overflow past the stack's origin, app-wide, where it has always pinned at x=0 — and a centered
            // layer carries no anchoring promise that overflowing could keep. The vertical axis never overflows
            // (freeV above stays clamped) — there is no height-side opt-out flag to honour.
            float freeH = innerW - cw - mL - mR;
            if (!(unboundedW || justify == FlexAlign.End)) freeH = MathF.Max(0f, freeH);
            float oy = align == FlexAlign.Center ? freeV * 0.5f : align == FlexAlign.End ? freeV : 0f;
            float ox = justify == FlexAlign.Center ? freeH * 0.5f : justify == FlexAlign.End ? freeH : 0f;
            Arrange(c, padL + mL + ox, padT + mT + oy, cw, ch);   // overlay at the aligned origin (recorder paints in order)
        }
    }

    // ── CSS Grid — distinct true-tracks (Pixel/Star/Auto) + row-major auto-flow (layout.md §7) ──

    // Largest font size in [MinSizeDip, SizeDip] whose run wraps to ≤ MaxLines at maxW (TextEl auto-fit). "Fits" is
    // monotonic in size (a bigger size never wraps fewer lines), so a short binary search converges. Each probe does
    // two cheap Measure calls (a single-line height reference + the wrapped box); this runs ONLY on a cache miss of an
    // opt-in (MinSizeDip>0) text node — never normal text, never a steady frame — so the extra measures are immaterial.
    private float FitTextSize(StringId text, TextStyle style, float maxW)
    {
        int maxLines = style.MaxLines;
        float maxS = style.SizeDip, minS = style.MinSizeDip;

        bool Fits(float s)
        {
            var probe = style with { SizeDip = s };
            float oneLine = _fonts.Measure(text, probe with { Wrap = Foundation.TextWrap.NoWrap, MaxLines = 0, Trim = Foundation.TextTrim.None }, float.PositiveInfinity).Size.Height;
            if (oneLine <= 0f) return true;
            float boxH = _fonts.Measure(text, probe with { MaxLines = 0, Trim = Foundation.TextTrim.None }, maxW).Size.Height;
            int lines = Math.Max(1, (int)MathF.Round(boxH / oneLine));
            return lines <= maxLines;
        }

        if (Fits(maxS)) return maxS;
        if (!Fits(minS)) return minS;
        float lo = minS, hi = maxS;
        for (int i = 0; i < 6; i++) { float mid = (lo + hi) * 0.5f; if (Fits(mid)) lo = mid; else hi = mid; }
        return lo;
    }

    // A grid cell's slot holds its MARGIN box (CSS grid; the ArrangeVirtual slot rule): an Auto track and an auto row
    // count the margin, and the cell is measured and placed inset by it. A collapsed cell keeps its track but sizes 0×0,
    // so it contributes no margin either.
    private Edges4 GridCellMargin(NodeHandle c) => Collapsed(c) ? default : _scene.Layout(c).Margin;

    private Size2 MeasureGrid(NodeHandle node, in LayoutInput li, float availW)
    {
        _scene.TryGetGrid(node, out var g);
        float padH = li.Padding.Horizontal, padV = li.Padding.Vertical;
        // Border-box width: explicit, else the width the parent will stretch us to (availW). A CSS grid is block-level —
        // it fills the available inline size, and star tracks NEED that concrete width to divide. Without availW a
        // stretch-width grid measured to height 0, so the parent column stacked the next sibling over its overflow.
        // Clamped by MinWidth/MaxWidth BEFORE the tracks resolve: the parent arranges us at the clamped width
        // (ClampCross/ClampMain) and ArrangeGrid counts columns there. Resolving at the raw width and clamping after
        // measured a different row count: a MaxWidth grid painted its last row over the next sibling, a MinWidth one
        // left an empty band (the TryWrapMainLimit rule, for grids).
        float w = Clamp(!float.IsNaN(li.Width) ? li.Width
                : float.IsInfinity(availW) ? 0f
                : MathF.Max(0f, availW), li.MinW, li.MaxW);
        int count = GridColCount(in g, w - padH);   // auto-fill resolves the count from the (now known) width
        float h;
        if (w > 0f && count > 0)
        {
            Span<float> colW = count <= 64 ? stackalloc float[count] : new float[count];
            ResolveColumns(node, in g, count, w - padH, colW);
            h = GridContentHeight(node, in g, count, colW) + padV;
        }
        else h = float.IsNaN(li.Height) ? 0f : li.Height;
        h = Clamp(h, li.MinH, li.MaxH);
        WriteMeasuredBounds(node, w, h);
        return new Size2(w, h);
    }

    private void ArrangeGrid(NodeHandle node, float finalW, float finalH, in LayoutInput li)
    {
        _scene.TryGetGrid(node, out var g);
        float padL = li.Padding.Left, padT = li.Padding.Top;
        float innerW = finalW - li.Padding.Horizontal;
        int count = GridColCount(in g, innerW);   // same width Measure saw → same count, so rows/height stay consistent
        if (count == 0 || _scene.FirstChild(node).IsNull) return;

        Span<float> colW = count <= 64 ? stackalloc float[count] : new float[count];
        Span<float> colX = count <= 64 ? stackalloc float[count] : new float[count];
        Span<NodeHandle> rowKids = count <= 64 ? stackalloc NodeHandle[count] : new NodeHandle[count];
        ResolveColumns(node, in g, count, innerW, colW);
        float cx = padL;
        for (int j = 0; j < count; j++) { colX[j] = cx; cx += colW[j] + g.ColGap; }

        bool autoRow = float.IsNaN(g.RowHeight);
        float rowTop = padT;
        var child = _scene.FirstChild(node);
        while (!child.IsNull)
        {
            int n = 0;
            var c = child;
            for (; n < count && !c.IsNull; n++, c = _scene.NextSibling(c)) rowKids[n] = c;

            float rowH = autoRow ? 0f : g.RowHeight;
            if (autoRow)
                for (int j = 0; j < n; j++)
                {
                    var m = GridCellMargin(rowKids[j]);
                    var cs = Measure(rowKids[j], MathF.Max(0f, colW[j] - m.Horizontal));
                    rowH = MathF.Max(rowH, cs.Height + m.Vertical);
                }
            for (int j = 0; j < n; j++)
            {
                var m = GridCellMargin(rowKids[j]);
                float cw = MathF.Max(0f, colW[j] - m.Horizontal);
                if (!autoRow) Measure(rowKids[j], cw);   // base sizes for the cell's own flex, at the cell's width so text wraps to the track
                Arrange(rowKids[j], colX[j] + m.Left, rowTop + m.Top, cw, MathF.Max(0f, rowH - m.Vertical));
            }
            rowTop += rowH + g.RowGap;
            child = c;
        }
    }

    // Effective column count. Fixed grids use their declared track list; an auto-fill grid (MinColWidth > 0) packs as
    // many equal 1fr columns as fit at >= MinColWidth, so the tracks always fill the width and the count reflows with it
    // (CSS repeat(auto-fill, minmax(MinColWidth, 1fr))), capped at MaxColumns when > 0. Width unknown (0 / ∞) ⇒ assume
    // a single column. The formula is GridEl.AutoFillColumnCount — ONE definition shared with app-side form rules, so
    // a caller predicting the count (cells = 2·cols − 1) can never disagree with the tracks actually laid out.
    private static int GridColCount(in GridSpec g, float innerW)
    {
        if (g.MinColWidth > 0f)
            return Dsl.GridEl.AutoFillColumnCount(innerW, g.MinColWidth, g.ColGap, g.MaxColumns);
        return g.Columns?.Length ?? 0;
    }

    private void ResolveColumns(NodeHandle node, in GridSpec g, int count, float availW, Span<float> colW)
    {
        if (g.MinColWidth > 0f)   // auto-fill: 'count' equal (1fr) tracks share the width evenly → flush fill, no ragged edge
        {
            float starGaps = count > 1 ? (count - 1) * g.ColGap : 0f;
            float each = count > 0 ? MathF.Max(0f, (availW - starGaps) / count) : 0f;
            for (int j = 0; j < count; j++) colW[j] = each;
            return;
        }

        Span<float> autoW = count <= 64 ? stackalloc float[count] : new float[count];
        bool anyAuto = false;
        for (int j = 0; j < count; j++) if (g.Columns[j].Kind == TrackKind.Auto) { anyAuto = true; break; }
        if (anyAuto)
        {
            int k = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), k++)
            {
                int col = k % count;
                if (g.Columns[col].Kind == TrackKind.Auto) { var cs = Measure(c); autoW[col] = MathF.Max(autoW[col], cs.Width + GridCellMargin(c).Horizontal); }
            }
        }

        float fixedW = 0f, starTotal = 0f;
        for (int j = 0; j < count; j++)
        {
            var t = g.Columns[j];
            if (t.Kind == TrackKind.Pixel) fixedW += t.Value;
            else if (t.Kind == TrackKind.Auto) fixedW += autoW[j];
            else starTotal += MathF.Max(0f, t.Value);
        }
        float gaps = count > 1 ? (count - 1) * g.ColGap : 0f;
        // Overflow guard (layout.md §7): when the FIXED (Px/Auto) tracks + gaps cannot fit a FINITE width, scale the
        // fixed tracks down proportionally so the row fits EXACTLY instead of spilling past the edge with overlapping
        // cells (Star tracks already resolve to 0). Grids don't scroll, so an over-wide fixed grid is a layout error we
        // degrade gracefully. No-op when it already fits (scale 1) or the width is unconstrained (the measure pass).
        float fixedScale = 1f;
        if (!float.IsInfinity(availW) && fixedW > 0f && fixedW + gaps > availW)
            fixedScale = Math.Clamp((availW - gaps) / fixedW, 0f, 1f);
        float remaining = MathF.Max(0f, availW - fixedW * fixedScale - gaps);
        for (int j = 0; j < count; j++)
        {
            var t = g.Columns[j];
            colW[j] = t.Kind switch
            {
                TrackKind.Pixel => t.Value * fixedScale,
                TrackKind.Auto => autoW[j] * fixedScale,
                _ => starTotal > 0f ? remaining * MathF.Max(0f, t.Value) / starTotal : 0f,
            };
        }
    }

    private float GridContentHeight(NodeHandle node, in GridSpec g, int count, ReadOnlySpan<float> colW)
    {
        int childCount = _scene.ChildCount(node);
        if (childCount == 0) return 0f;
        int rows = (childCount + count - 1) / count;
        if (!float.IsNaN(g.RowHeight)) return rows * g.RowHeight + (rows - 1) * g.RowGap;

        float sumRowH = 0f, rowH = 0f; int k = 0;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), k++)
        {
            var m = GridCellMargin(c);
            var cs = Measure(c, MathF.Max(0f, colW[k % count] - m.Horizontal));   // at the track width less the margin, so wrapping text reports its wrapped height
            rowH = MathF.Max(rowH, cs.Height + m.Vertical);
            if (k % count == count - 1) { sumRowH += rowH; rowH = 0f; }
        }
        if (k % count != 0) sumRowH += rowH;   // trailing partial row
        return sumRowH + (rows - 1) * g.RowGap;
    }

    // Wrap: main axis is finite (explicit size or parent-provided row width); children flow onto multiple lines.
    // The limit is clamped by the main-axis Min/Max, the same clamp Measure's tail applies to the box and the parent's
    // arrange (ClampCross/ClampMain) gives it. Breaking lines at the raw offered width while ArrangeWrap breaks at the
    // clamped one counted a different number of lines: a MaxWidth chip row measured one line at 1000 and painted three at
    // 400 over the next sibling (MinWidth the other way: a measured line too many, an empty band below).
    private static bool TryWrapMainLimit(in LayoutInput li, bool row, float availW, out float mainLimit)
    {
        float explicitMain = row ? li.Width : li.Height;
        float limit = !float.IsNaN(explicitMain) ? explicitMain : (row ? availW : float.PositiveInfinity);
        if (float.IsInfinity(limit))
        {
            mainLimit = 0f;
            return false;
        }

        mainLimit = MathF.Max(0f, ClampMain(in li, row, limit));
        return true;
    }

    private (float w, float h) MeasureWrap(NodeHandle node, in LayoutInput li, bool row, float mainLimit)
    {
        float availMain = MathF.Max(0f, mainLimit - (row ? li.Padding.Horizontal : li.Padding.Vertical));
        // Text (MaxLines=1 + Trim) must measure against the LINE width so a lone over-long item ellipsizes
        // instead of overflowing. Never the leftover: leftover would crush a shrinkable run instead of wrapping.
        float childAvailW = row ? availMain : float.PositiveInfinity;
        float cursor = 0f, lineCross = 0f, totalCross = 0f;
        bool first = true, any = false;
        for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
        {
            var cs = Measure(c, childAvailW);
            ref LayoutInput cli = ref _scene.Layout(c);
            float oMain = (row ? cs.Width : cs.Height) + MarginMain(cli, row);
            float oCross = (row ? cs.Height : cs.Width) + MarginCross(cli, row);
            if (!first && cursor + li.Gap + oMain > availMain + 0.01f)
            {
                totalCross += lineCross + li.Gap;   // close the line + a cross-axis gap
                cursor = oMain; lineCross = oCross;
            }
            else
            {
                cursor += first ? oMain : li.Gap + oMain;
                lineCross = MathF.Max(lineCross, oCross);
            }
            first = false; any = true;
        }
        if (any) totalCross += lineCross;
        float crossSize = totalCross + (row ? li.Padding.Vertical : li.Padding.Horizontal);
        float mainSize = mainLimit;
        return row ? (mainSize, crossSize) : (crossSize, mainSize);
    }

    // flex-wrap arrange. Children flow into lines (CSS flex line-breaking); then — the part the original single pass
    // skipped — each line distributes ITS leftover main to that line's flex-grow children (CSS grow applies per line,
    // including the last), so a wrapped row fills edge-to-edge instead of leaving a ragged gap. A line with no grow child
    // (pill/chip rows, FlexGrow 0) yields growUnit 0 and is placed at base size byte-for-byte as before. Allocation-free:
    // two linked-list walks per line, no per-line buffer.
    // Within each line, items are then placed exactly like the single-line path: the line's leftover main goes through
    // Distribute(li.Justify) (zero when a grow child took it), and each item's cross placement follows AlignSelf ?? AlignItems
    // against the LINE's cross size (its tallest item + margins): Stretch fills it unless the item has an explicit cross
    // size, Center/End offset it. Line breaking and the measured height are untouched (alignment only moves items inside a
    // line whose cross size is already fixed), so measure and arrange still agree on every line. No AlignContent: a line is
    // as tall as its content.
    // BASE SIZES COME FROM Measure, NEVER FROM scene.Bounds. A wrap container that hits the cross-pass measure ring (or the
    // within-pass memo) returns its size WITHOUT visiting its children (Measure, P4), so each child's Bounds still hold its
    // LAST ARRANGED rect — a Grow child stretched to fill the previous, wider line. Breaking lines on those stretched widths
    // overflows the line (730 → 700 with nothing dirty: the third Grow tile dropped to an extra line the cached cross size
    // never counted and painted over the next sibling). Measure(c, lineWidth) is exactly the call MeasureWrap counts lines
    // with — a memo/ring hit when the child is clean, a real solve only when it is not — so measure and arrange can never
    // disagree about where a line ends, whatever the caches hold. (FlexBasis plays no part in wrap line breaking — neither
    // here nor in MeasureWrap; both read the measured size, and Grow only distributes each line's leftover.)
    private void ArrangeWrap(NodeHandle node, float finalW, float finalH, in LayoutInput li, bool row)
    {
        float padMainStart = row ? li.Padding.Left : li.Padding.Top;
        float padCrossStart = row ? li.Padding.Top : li.Padding.Left;
        float availMain = (row ? finalW : finalH) - (row ? li.Padding.Horizontal : li.Padding.Vertical);
        // MeasureWrap's childAvailW: the line width on a row (text MaxLines=1 + Trim ellipsizes against it), unbounded on a column.
        float childAvailW = row ? MathF.Max(0f, availMain) : float.PositiveInfinity;
        float lineTop = padCrossStart;

        for (var lineStart = FirstVisibleChild(node); !lineStart.IsNull;)
        {
            // Pass 1 — gather one line: the children that fit, their base-main extent (bases + margins + gaps), total grow.
            // The break condition mirrors MeasureWrap exactly, so arrange's line count matches the measured cross height.
            float usedMain = 0f, totalGrow = 0f, lineCross = 0f;
            int count = 0;
            for (var c = lineStart; !c.IsNull; c = NextVisibleSibling(c))
            {
                var cs = Measure(c, childAvailW);
                ref LayoutInput cli = ref _scene.Layout(c);
                float oMain = (row ? cs.Width : cs.Height) + MarginMain(cli, row);
                float next = usedMain + (count > 0 ? li.Gap : 0f) + oMain;
                if (count > 0 && next > availMain + 0.01f) break;   // CSS: always ≥1 item per line
                usedMain = next; totalGrow += cli.FlexGrow; count++;
                lineCross = MathF.Max(lineCross, (row ? cs.Height : cs.Width) + MarginCross(cli, row));   // the line's cross size, known before placement
            }

            // Pass 2 — share this line's free main across its grow children (0 grow ⇒ growUnit 0 ⇒ base size), then justify
            // what is left (nothing when a grow child took it) and align each item on the line's cross size, like Arrange.
            float freeMain = MathF.Max(0f, availMain - usedMain);
            float growUnit = totalGrow > 0f ? freeMain / totalGrow : 0f;
            (float lead, float between) = Distribute(li.Justify, totalGrow > 0f ? 0f : freeMain, count);
            float cursor = padMainStart + lead;
            var cc = lineStart;
            for (int i = 0; i < count; i++, cc = NextVisibleSibling(cc))
            {
                var cs = Measure(cc, childAvailW);   // a memo hit after pass 1 — the same base size that decided this line
                ref LayoutInput cli = ref _scene.Layout(cc);
                float baseMain = row ? cs.Width : cs.Height, baseCross = row ? cs.Height : cs.Width;
                float mainSize = baseMain + cli.FlexGrow * growUnit;
                if (i > 0) cursor += li.Gap + between;

                FlexAlign align = cli.AlignSelf == FlexAlign.Auto ? li.AlignItems : cli.AlignSelf;
                float crossMargin = MarginCross(cli, row);
                bool hasExplicitCross = !float.IsNaN(row ? cli.Height : cli.Width);
                float crossSize = (align == FlexAlign.Stretch && !hasExplicitCross)
                    ? ClampCross(cli, row, lineCross - crossMargin)
                    : baseCross;
                float crossFree = lineCross - (crossSize + crossMargin);
                float crossOff = align switch
                {
                    FlexAlign.Center => crossFree / 2f,
                    FlexAlign.End => crossFree,
                    _ => 0f,   // Start / Stretch
                };

                float childMainPos = cursor + MarginMainStart(cli, row);
                float childCrossPos = lineTop + crossOff + MarginCrossStart(cli, row);
                float cx = row ? childMainPos : childCrossPos;
                float cy = row ? childCrossPos : childMainPos;
                Arrange(cc, cx, cy, row ? mainSize : crossSize, row ? crossSize : mainSize);
                cursor += MarginMain(cli, row) + mainSize;
            }
            lineTop += lineCross + li.Gap;
            lineStart = cc;   // cc walked exactly `count` siblings ⇒ first child of the next line (or Null)
        }
    }

    private static (float lead, float between) Distribute(FlexJustify j, float leftover, int n) => j switch
    {
        FlexJustify.Center => (leftover / 2f, 0f),
        FlexJustify.End => (leftover, 0f),
        FlexJustify.SpaceBetween => (0f, n > 1 ? leftover / (n - 1) : 0f),
        FlexJustify.SpaceAround => (n > 0 ? leftover / n / 2f : 0f, n > 0 ? leftover / n : 0f),
        FlexJustify.SpaceEvenly => (leftover / (n + 1), leftover / (n + 1)),
        _ => (0f, 0f),   // Start
    };

    private static float MarginMain(in LayoutInput li, bool row) => row ? li.Margin.Horizontal : li.Margin.Vertical;
    private static float MarginCross(in LayoutInput li, bool row) => row ? li.Margin.Vertical : li.Margin.Horizontal;
    private static float MarginMainStart(in LayoutInput li, bool row) => row ? li.Margin.Left : li.Margin.Top;
    private static float MarginCrossStart(in LayoutInput li, bool row) => row ? li.Margin.Top : li.Margin.Left;
    private static float ClampMain(in LayoutInput li, bool row, float v) => row ? Clamp(v, li.MinW, li.MaxW) : Clamp(v, li.MinH, li.MaxH);
    private static float ClampCross(in LayoutInput li, bool row, float v) => row ? Clamp(v, li.MinH, li.MaxH) : Clamp(v, li.MinW, li.MaxW);
}
