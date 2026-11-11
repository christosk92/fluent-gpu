using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Render;

// ── PARTIAL KEEP (audit PERF r2) ──────────────────────────────────────────────────────────────────────────────────────────
// A slice is kept whole only when its root's AGGREGATE record-dirty bits are 0, and a dirty node anywhere below sets the
// descendant bits of every ancestor. So one equalizer bar moving inside the playing track row (a repaint boundary inside the
// list's item band, inside the list's content, inside the page, inside the root) re-walked FIVE slices per turn — each
// visiting its dirty chain and every sibling on it to write the same bytes again — where one 300-byte slice changed.
//
// A slice whose OWN nodes are all clean, whose dirt lies only inside child slices, writes exactly the bytes it holds: its
// nodes record the same ops, and each child it cuts contributes only its MARKER, which is a function of that child root's own
// state and of the inputs the cut handed it. So it is kept whole and only its dirty child slices are walked, each entered
// directly with the inputs its cut handed its root walk the last time the parent was walked (CutEntry), and with the
// accumulated slice offset and window clip recomputed from the CURRENT poses — exactly what Place composites them at.
//
// The decision (TryPartialKeep) is the keep test with one clause relaxed: the root's inputs unchanged (the span input
// signature), span reuse on, the chain unblocked, the slot not denied — and, instead of "nothing below is dirty", a probe that
// follows only the dirty bits: every node of the slice's own walk on a dirty path is clean itself, every scroll viewport on
// such a path still has the scroll state its bytes were recorded under (that state changes without a dirty bit), and every
// dirty child slice root is clean ITSELF (else its marker may change), has a cut entry written under its current parent, is
// not an opacity group (its marker carries its content's drawn extent) and does not sit under a hover-elevate clip root.
// An overflow cue (a marquee's clip: EdgeFadeSpec.OverflowTail) breaks the premise the same way: its marker's bands, and
// whether it is a Layer cut at all, are resolved from its CONTENT's posed translate, which stamps no bit on the cue's node. So
// the cut entry remembers what the cue resolved to (CutEntry.Cue*). When it now resolves to other bands of the same edge-fade
// Layer marker (a feather ramping in or out: every turn of a ramp), that marker's fade edges and left/right bands are rewritten
// in place in the kept arena (QueueCuePatch / ApplyCuePatches: a new buffer generation for that arena, so its scan is redone);
// any other change of the cue — it appears or goes, or resolves to no fade on either side — and a cue node on a dirty path
// that holds no cut fall back. Anything else falls back to the full re-walk.
//
// `--fg rewalk-validate` takes the full re-walk wherever a partial keep would have been taken, and counts every kept arena
// whose re-walk wrote different bytes, every cut whose inputs differ from its saved entry, and every cut whose walk offset or
// clip differs from the recomputed one — the evidence the decision is exact. `--fg no-partial-keep` turns it off (A/B).
internal sealed partial class SceneRecordingContext
{
    /// <summary>Partial keep on (default). <c>--fg no-partial-keep</c> turns it off.</summary>
    internal static bool PartialKeepEnabled = true;
    /// <summary><c>--fg rewalk-validate</c>: never keep partially; re-walk and check that it would have been exact.</summary>
    internal static bool PartialKeepValidate;

    private static int s_validatedKeeps, s_keepMismatches, s_keepDrift, s_entryMismatches, s_entryDrift, s_offsetMismatches, s_cutsChecked, s_cuePatches;
    private static long s_lastValidateLog;

    internal const byte CutTranslation = 1, CutLayer = 2, CutBoundary = 3;

    /// <summary>What a slice's cut handed its root walk (written at the cut, read by a partial keep that re-enters the slice
    /// with its parent kept whole).</summary>
    internal struct CutEntry
    {
        public uint Frame;            // the recorder pass the cut ran in; 0 = none
        public int NodeIndex;
        public uint Gen;
        public int ParentSlot;        // the arena the cut wrote the marker into
        public byte Kind;             // CutTranslation / CutLayer / CutBoundary
        public bool OpacityGroup;     // its marker's layer carries its content's drawn extent: never re-entered
        public Affine2D ParentWorld;
        public float ParentOpacity;
        public int Depth;
        public RectF Clip;            // the clip its root walk ran under
        public float ParentScaleX, ParentScaleY;
        public bool ParentInMotion, ParentScrollInMotion;
        public InheritedState Inherited;
        public bool SelfHasLocal;
        public Affine2D SelfLocal;
        public bool SelfOmitLayer;
        public SliceRecorder.PoseKind SelfPose;
        public bool SelfAcrylic, SelfStickyClip, CarryAdditive;
        public int ParentAdditiveDepth;
        public RectF SliceClipPart;   // a translation cut: its marker clip (the parent's walk space); Infinite = none
        // An overflow cue (EdgeFadeSpec.OverflowTail) on the cut node: its marker's bands — and whether it is a Layer cut at all —
        // follow its CONTENT's posed translate, which moves with no bit on this node. What the cue resolved to when the marker
        // was written: the slice is re-entered only while it still resolves so.
        public bool OverflowCue;
        public EdgeMask CueEdges;
        public float CueLeft, CueRight;
        public float CueScaleX;       // an edge-fade Layer cut: its bands' DIP→device px scale; 0 = not an edge fade
        public int MarkerAt;          // a group Layer cut: where its marker op started in the parent's arena; -1 = none
    }

    /// <summary>A marker a partial keep rewrites in place (an overflow cue whose bands moved), found by the probe and applied
    /// only once the whole probe succeeds.</summary>
    private struct CuePatch
    {
        public int ArenaSlot, ChildSlot, At;
        public EdgeMask Edges;
        public float Left, Right;
    }
    private CuePatch[] _cuePatches = new CuePatch[4];
    private int _cuePatchCount;

    /// <summary>What an overflow cue on <paramref name="node"/> resolves to under the current poses — the one part of a
    /// marker that reads state BELOW its node (<see cref="TryResolveEdgeFade"/>, whose explicit branch this mirrors). False
    /// when the node carries no overflow cue; <paramref name="edges"/> = None when the cue resolves to no fade.</summary>
    private static bool ResolveOverflowCue(SceneRecordingSnapshot scene, NodeHandle node, out EdgeMask edges, out float left, out float right)
    {
        edges = EdgeMask.None; left = right = 0f;
        if (!HasOverflowCue(scene, node, out EdgeFadeSpec raw)) return false;
        EdgeFadeSpec r = raw.ResolveOverflow(OverflowContentDx(scene, node));
        if (!r.IsNone) { edges = r.Edges; left = r.BandLeft; right = r.BandRight; }
        return true;
    }

    private static bool HasOverflowCue(SceneRecordingSnapshot scene, NodeHandle node, out EdgeFadeSpec raw)
    {
        raw = default;
        return (scene.Flags(node) & NodeFlags.SparsePaint) != 0 && scene.TryGetEdgeFade(node, out raw) && !raw.IsNone && raw.OverflowTail > 0f;
    }

    /// <summary>Remember what this cut hands its slice's root walk. Called at every cut, right before that walk, with the
    /// slice context already entered (<paramref name="saved"/> = the parent's).</summary>
    private void NoteCut(SceneRecordingSnapshot scene, SliceRecorder sl, int slot, NodeHandle node, byte kind, bool opacityGroup,
        in Affine2D parentWorld, float parentOpacity, int depth, in RectF walkClip, float parentScaleX, float parentScaleY,
        bool parentInMotion, bool parentScrollInMotion, in InheritedState inherited, bool carryAdditive, in SliceCtx saved,
        in RectF sliceClipPart, ref RecordAccumulator stats, int markerAt = -1, float cueScaleX = 0f)
    {
        var e = new CutEntry
        {
            Frame = sl.PassFrame, NodeIndex = (int)node.Raw.Index, Gen = node.Raw.Gen, ParentSlot = saved.CurSlot, Kind = kind,
            OpacityGroup = opacityGroup, ParentWorld = parentWorld, ParentOpacity = parentOpacity, Depth = depth, Clip = walkClip,
            ParentScaleX = parentScaleX, ParentScaleY = parentScaleY, ParentInMotion = parentInMotion,
            ParentScrollInMotion = parentScrollInMotion, Inherited = inherited, SelfHasLocal = stats.SelfHasLocal,
            SelfLocal = stats.SelfLocal, SelfOmitLayer = stats.SelfOmitLayer, SelfPose = stats.SelfPose,
            SelfAcrylic = stats.SelfAcrylic, SelfStickyClip = stats.SelfStickyClip, CarryAdditive = carryAdditive,
            ParentAdditiveDepth = saved.AdditiveDepth, SliceClipPart = sliceClipPart, CueScaleX = cueScaleX, MarkerAt = markerAt,
        };
        // every kind: a cue resolving to no fade leaves its node uncut or cut as a boundary, and one resolving to a fade cuts a Layer
        e.OverflowCue = ResolveOverflowCue(scene, node, out e.CueEdges, out e.CueLeft, out e.CueRight);
        ref CutEntry old = ref sl.CutOf(slot);
        if (PartialKeepValidate) ValidateCut(scene, sl, slot, in old, in e, ref stats);
        old = e;
    }

    /// <summary>The window clip a walk of <paramref name="slot"/> runs under (<see cref="RecordAccumulator.SliceClip"/>),
    /// recomputed from the current poses: every translation cut on its chain contributes its marker clip at its parent's
    /// current accumulated offset. False when an entry on the chain is missing.</summary>
    private static bool DeriveSliceClip(SceneRecordingSnapshot scene, SliceRecorder sl, int slot, out RectF clip)
    {
        clip = RectF.Infinite;
        for (int s = slot, guard = 0; s > SliceRecorder.RootSlot && guard < 64; s = sl.ParentOf(s), guard++)
        {
            ref CutEntry e = ref sl.CutOf(s);
            if (e.Frame == 0)
            {
                if (sl.PoseOf(s) is SliceRecorder.PoseKind.Content or SliceRecorder.PoseKind.Effect) return false;   // its clip is unknown
                continue;
            }
            if (e.SliceClipPart.IsInfinite) continue;
            sl.CurrentAcc(sl.ParentOf(s), scene, out float pdx, out float pdy);
            RectF w = SliceRecorder.Offset(in e.SliceClipPart, pdx, pdy);
            clip = clip.IsInfinite ? w : clip.Intersect(w);
        }
        return true;
    }

    /// <summary>The keep test with "nothing below is dirty" relaxed to "nothing of this slice's own walk is dirty" — see the
    /// file header. On success the slice's arena is kept, its clean child slices kept, its dirty ones walked directly, and
    /// <paramref name="result"/> is what the full walk would have returned (a superset: the kept extent ∪ the re-walked ones).</summary>
    private bool TryPartialKeep(SceneRecordingSnapshot scene, ImageRecordingSnapshot? images, NodeHandle node, int selfSlot, bool inMotion,
        bool rootPosed, in DrawSpan kept, ulong spanInputSig, in FocusVisualStyle focus, in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack,
        ReadOnlySpan<NodeHandle> skipRoots, SpanTable spans, uint spanFrame, bool spanReuseDisabled, bool spanStoreEnabled,
        ref RecordAccumulator stats, out SpanRecordResult result)
    {
        result = default;
        if (!PartialKeepEnabled || stats.InlineLayerDepth != 0) return false;
        var sl = stats.Slices!;
        // its walk folded a cut for want of budget: this pass may cut it, which changes this slice's own bytes
        if (sl.RefusedCut(selfSlot)) return false;
        int probe = sl.BeginProbe(selfSlot, scene.Capacity);
        int dirty = 0;
        _cuePatchCount = 0;
        if (!ProbeOwnWalk(scene, node, sl, spans, spanFrame, inMotion, rootPosed, ref dirty) || dirty == 0) return false;
        // the markers whose overflow cue moved: rewritten in place in the kept bytes (under validation too — the patched bytes
        // become the prior buffer the full re-walk is checked against, so the check covers the patch)
        ApplyCuePatches(sl);
        if (PartialKeepValidate)
        {
            // take the full re-walk; its end checks it wrote exactly the kept bytes (EndWalk → ValidateKept)
            sl.ExpectSameBytes(selfSlot);
            return false;
        }
        sl.KeepSelf(selfSlot);
        sl.NotePartialKeep();
        var r = new SpanRecordResult();
        r.Include(kept.SubtreeBounds);
        KeepOrReenterUnit(scene, images, selfSlot, probe, in focus, in textEdit, scrollThumb, scrollTrack, skipRoots, spans, spanFrame,
            spanReuseDisabled, spanStoreEnabled, ref stats, ref r, 0);
        var stored = r.HasBounds ? kept with { SubtreeBounds = r.SubtreeBounds } : kept;
        spans.Store((int)node.Raw.Index, node.Raw.Gen, spanFrame, spanInputSig, in stored, sl.CurGen(selfSlot), selfSlot);
        stats.SpansReused++;
        result = r;
        return true;
    }

    private const int PartialKeepProbeLimit = 4096;
    private int[] _probeStack = new int[64];

    /// <summary>Follow the dirty bits below <paramref name="root"/> through the nodes the slice's own walk records (stopping at
    /// the root of every child slice it cuts): true when none of them is dirty itself or holds a scroll state its bytes were not
    /// recorded under, and every dirty child slice can be re-entered (each is marked; <paramref name="dirty"/> counts them).</summary>
    /// <param name="inMotion">The slice root's own motion gate: what every node of its walk hands a child slice's root (each of
    /// them is clean, so none adds a transform write of its own) — a child slice is re-entered only under the gate it was cut under.</param>
    /// <param name="rootPosed">The slice root walks under a pose-free local: its own transform-only self bit is its pose, no byte.</param>
    private bool ProbeOwnWalk(SceneRecordingSnapshot scene, NodeHandle root, SliceRecorder sl, SpanTable spans, uint spanFrame, bool inMotion,
        bool rootPosed, ref int dirty)
    {
        if ((scene.RecordDirtySelfBits(root) & (rootPosed ? ~SceneStore.RecordDirtyTransform : 0xFF)) != 0) return false;
        // the slice's OWN Layer slice (a slice root that needs a group layer cuts it at its own node): its stream is that marker
        int own = sl.ProbeBoundary((int)root.Raw.Index);
        if (own >= 0) return MarkIfReenterable(scene, sl, own, root, spans, spanFrame, inMotion, ref dirty);
        int top = 0, visited = 0;
        _probeStack[top++] = (int)root.Raw.Index;
        while (top > 0)
        {
            var n = scene.HandleAt(_probeStack[--top]);
            if (n.IsNull) return false;
            NodeFlags nf = scene.Flags(n);
            if ((nf & NodeFlags.Scrollable) != 0 && scene.HasScroll(n)
                && !spans.ViewportSigMatches((int)n.Raw.Index, n.Raw.Gen, ViewportStateSig(scene, n, nf))) return false;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c))
            {
                if (scene.RecordDirtyBits(c) == 0 || !scene.IsLive(c)) continue;
                int slot = sl.ProbeBoundary((int)c.Raw.Index);
                if (slot >= 0 && sl.GenOf(slot) == c.Raw.Gen)
                {
                    if (!MarkIfReenterable(scene, sl, slot, c, spans, spanFrame, inMotion, ref dirty)) return false;
                    continue;
                }
                if (scene.RecordDirtySelfBits(c) != 0) return false;
                if ((scene.Flags(c) & NodeFlags.Visible) == 0) continue;   // not recorded, and was not (its visibility did not change)
                if (scene.RecordDirtyDescendantBits(c) == 0) continue;
                // an overflow cue with no cut of its own (it resolved to no fade, or was folded into an inline layer): its fade —
                // or the Layer cut it now needs — follows the content moving below it, so these bytes may not be what it records
                if (HasOverflowCue(scene, c, out _)) return false;
                if (++visited > PartialKeepProbeLimit) return false;
                if (top == _probeStack.Length) Array.Resize(ref _probeStack, top * 2);
                _probeStack[top++] = (int)c.Raw.Index;
            }
        }
        return true;
    }

    private bool MarkIfReenterable(SceneRecordingSnapshot scene, SliceRecorder sl, int slot, NodeHandle c, SpanTable spans, uint spanFrame,
        bool inMotion, ref int dirty)
    {
        if (scene.RecordDirtyBits(c) == 0) return true;   // clean: kept whole with its subtree
        ref CutEntry e = ref sl.CutOf(slot);
        // its own state changed: its marker may differ — unless the change is only its transform and that is its pose (a posed
        // slice's marker is pose-free: the composite places it)
        byte own = scene.RecordDirtySelfBits(c);
        if (e.Frame != 0 && e.SelfHasLocal && sl.PoseOf(slot) is SliceRecorder.PoseKind.Content or SliceRecorder.PoseKind.Effect)
            own &= unchecked((byte)~SceneStore.RecordDirtyTransform);
        if (own != 0) return false;
        if (e.Frame == 0 || e.NodeIndex != (int)c.Raw.Index || e.Gen != c.Raw.Gen || e.ParentSlot != sl.ParentOf(slot)) return false;
        if (e.OpacityGroup || e.Inherited.UnderElevateRoot != 0) return false;
        // an overflow cue's marker follows its content's pose: the parent keeps it only while the cue resolves as it did then
        bool cue = ResolveOverflowCue(scene, c, out EdgeMask cueEdges, out float cueLeft, out float cueRight);
        if (cue != e.OverflowCue) return false;
        if (cue && (cueEdges != e.CueEdges || cueLeft != e.CueLeft || cueRight != e.CueRight)
            && !QueueCuePatch(sl, slot, in e, cueEdges, cueLeft, cueRight)) return false;
        // the motion gate is the one cut input that changes with no dirty bit (a transform write ends without one)
        if (e.ParentInMotion != inMotion) return false;
        if (spans.IsBlocked((int)c.Raw.Index, spanFrame)) return false;
        if (!DeriveSliceClip(scene, sl, slot, out _)) return false;
        sl.MarkReenter(slot);
        dirty++;
        return true;
    }

    /// <summary>The overflow cue of the Layer cut at <paramref name="slot"/> resolves to other bands than its marker holds. When
    /// that is ALL that changed — it was and still is an edge-fade Layer marker (a fade before and after: the same cut, kind and
    /// flags; every other input of the marker is the cut's own, unchanged state) and the marker is found in the parent's kept
    /// arena — queue a rewrite of its fade edges and left/right bands, exactly as the cut computes them. False = take the walk.</summary>
    private bool QueueCuePatch(SliceRecorder sl, int slot, in CutEntry e, EdgeMask edges, float left, float right)
    {
        if (e.Kind != CutLayer || e.CueScaleX <= 0f || e.MarkerAt < 0 || e.CueEdges == EdgeMask.None || edges == EdgeMask.None) return false;
        DrawList arena = sl.Arena(e.ParentSlot);
        int at = FindMarker(arena, e.MarkerAt, e.NodeIndex, e.Gen, (int)sl.RoleOf(slot));
        if (at < 0) return false;
        CompositeSliceCmd m = arena.ReadCompositeSlice(at);
        if (!m.Has(CompositeSliceFlags.Layer) || m.Layer.Kind != (int)LayerKind.EdgeFade) return false;
        if (_cuePatchCount == _cuePatches.Length) Array.Resize(ref _cuePatches, _cuePatchCount * 2);
        _cuePatches[_cuePatchCount++] = new CuePatch { ArenaSlot = e.ParentSlot, ChildSlot = slot, At = at, Edges = edges, Left = left, Right = right };
        return true;
    }

    /// <summary>The op start of the marker of (<paramref name="nodeIndex"/>, <paramref name="gen"/>, <paramref name="sub"/>) in
    /// <paramref name="arena"/>'s current bytes: at <paramref name="hint"/> (where the cut wrote it) when it is still there, else
    /// by stepping the ops (a span copy may have moved it). −1 = not there.</summary>
    private static int FindMarker(DrawList arena, int hint, int nodeIndex, uint gen, int sub)
    {
        ReadOnlySpan<byte> b = arena.Bytes;
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<CompositeSliceCmd>();
        if (IsMarkerAt(b, hint, size, nodeIndex, gen, sub)) return hint;
        int pos = 0;
        while (pos + sizeof(int) <= b.Length)
        {
            var op = (DrawOp)System.Runtime.InteropServices.MemoryMarshal.Read<int>(b[pos..]);
            if (!FluentGpu.Rhi.RepaintStreamSafety.TryBodySize(op, out int body)) return -1;
            if (op == DrawOp.CompositeSlice && IsMarkerAt(b, pos, size, nodeIndex, gen, sub)) return pos;
            pos += sizeof(int) + body;
        }
        return -1;
    }

    private static bool IsMarkerAt(ReadOnlySpan<byte> b, int at, int size, int nodeIndex, uint gen, int sub)
    {
        if (at < 0 || at + sizeof(int) + size > b.Length
            || (DrawOp)System.Runtime.InteropServices.MemoryMarshal.Read<int>(b[at..]) != DrawOp.CompositeSlice) return false;
        var m = System.Runtime.InteropServices.MemoryMarshal.Read<CompositeSliceCmd>(b[(at + sizeof(int))..]);
        return m.NodeIndex == nodeIndex && m.Gen == gen && m.Sub == sub && m.Kind == (int)Tiles.SliceKind.Effect;
    }

    /// <summary>Rewrite every queued marker in its kept arena: the fade edges and left/right bands the cut would write now (the
    /// same arithmetic as <c>DrawList.EdgeFadeLayerCmd</c> over the cut's scale), and the cut entry follows.</summary>
    private void ApplyCuePatches(SliceRecorder sl)
    {
        for (int i = 0; i < _cuePatchCount; i++)
        {
            ref CuePatch p = ref _cuePatches[i];
            ref CutEntry e = ref sl.CutOf(p.ChildSlot);
            DrawList arena = sl.Arena(p.ArenaSlot);
            CompositeSliceCmd m = arena.ReadCompositeSlice(p.At);
            float sx = e.CueScaleX;
            PushLayerCmd layer = m.Layer with
            {
                FadeEdges = (int)p.Edges,
                FadeBandL = MathF.Max(0f, (p.Edges & EdgeMask.Left) != 0 ? p.Left * sx : 0f),
                FadeBandR = MathF.Max(0f, (p.Edges & EdgeMask.Right) != 0 ? p.Right * sx : 0f),
            };
            arena.PatchCompositeSlice(p.At, m with { Layer = layer });
            s_cuePatches++;
            sl.NoteMarkerPatched(p.ArenaSlot, p.ChildSlot, in layer);
            e.CueEdges = p.Edges; e.CueLeft = p.Left; e.CueRight = p.Right; e.MarkerAt = p.At;
        }
        _cuePatchCount = 0;
    }

    /// <summary>Register a partially kept slice's unit: its sub-slices kept with it (their children handled the same way), its
    /// clean child slices kept whole, its marked dirty ones walked directly — in the order a full walk registers them.</summary>
    private void KeepOrReenterUnit(SceneRecordingSnapshot scene, ImageRecordingSnapshot? images, int slot, int probe,
        in FocusVisualStyle focus, in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack, ReadOnlySpan<NodeHandle> skipRoots,
        SpanTable spans, uint spanFrame, bool spanReuseDisabled, bool spanStoreEnabled, ref RecordAccumulator stats, ref SpanRecordResult r,
        int depth)
    {
        var sl = stats.Slices!;
        for (int c = sl.FirstChildOf(slot); c >= 0; c = sl.NextSiblingOf(c))
        {
            if (!sl.IsLiveSlot(c)) continue;
            if (SliceRecorder.IsSubSlice(sl.RoleOf(c)))
            {
                sl.KeepSelf(c);
                if (depth < 64)
                    KeepOrReenterUnit(scene, images, c, probe, in focus, in textEdit, scrollThumb, scrollTrack, skipRoots, spans, spanFrame,
                        spanReuseDisabled, spanStoreEnabled, ref stats, ref r, depth + 1);
                continue;
            }
            if (!sl.IsMarkedReenter(c, probe)) { sl.Keep(c); continue; }
            r.Include(Reenter(scene, images, c, in focus, in textEdit, scrollThumb, scrollTrack, skipRoots, spans, spanFrame,
                spanReuseDisabled, spanStoreEnabled, ref stats));
            sl.NoteReentered();
        }
    }

    /// <summary>Walk child slice <paramref name="slot"/> as its cut would, its parent kept: the cut's saved inputs, the offset and
    /// window clip of its chain at the current poses. Returns its painted extent in the parent's walk space.</summary>
    private SpanRecordResult Reenter(SceneRecordingSnapshot scene, ImageRecordingSnapshot? images, int slot, in FocusVisualStyle focus,
        in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack, ReadOnlySpan<NodeHandle> skipRoots, SpanTable spans,
        uint spanFrame, bool spanReuseDisabled, bool spanStoreEnabled, ref RecordAccumulator stats)
    {
        var sl = stats.Slices!;
        CutEntry e = sl.CutOf(slot);
        var node = scene.HandleAt(e.NodeIndex);
        var saved = SaveSlice(ref stats);
        sl.CurrentAcc(sl.ParentOf(slot), scene, out float pdx, out float pdy);
        float odx = 0f, ody = 0f;
        bool posedCut = sl.PoseOf(slot) is SliceRecorder.PoseKind.Content or SliceRecorder.PoseKind.Effect;
        if (posedCut) sl.OwnDelta(slot, scene, out odx, out ody);
        DeriveSliceClip(scene, sl, slot, out RectF sliceClip);
        stats.CurDx = pdx; stats.CurDy = pdy;
        stats.SliceClip = sliceClip;
        stats.AdditiveDepth = e.ParentAdditiveDepth;
        stats.SelfNode = node;
        stats.SelfSlot = slot;
        stats.SelfHasLocal = e.SelfHasLocal;
        stats.SelfLocal = e.SelfLocal;
        stats.SelfOmitLayer = e.SelfOmitLayer;
        stats.SelfPose = e.SelfPose;
        stats.SelfAcrylic = e.SelfAcrylic;
        stats.SelfStickyClip = e.SelfStickyClip;
        EnterSlice(ref stats, sl, slot, odx, ody, carryAdditive: e.CarryAdditive);
        var parentReloc = stats.Reloc;
        stats.Reloc = default;   // a slice root's lineage is its own arena's (SpanTable.Relocation never crosses slots)
        bool pushed = PushWalkPath(node);
        SpanRecordResult res = default;
        try
        {
            if (pushed)
                res = WalkCore(scene, sl.Arena(slot), images, node, e.ParentWorld, e.ParentOpacity, e.Depth, e.Clip, in focus, in textEdit,
                    scrollThumb, scrollTrack, e.ParentScaleX, e.ParentScaleY, e.ParentInMotion, e.ParentScrollInMotion, e.Inherited,
                    skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
        }
        finally
        {
            if (pushed) PopWalkPath();
            stats.SelfNode = NodeHandle.Null;
            stats.Reloc = parentReloc;
            RestoreSlice(ref stats, in saved);
        }
        if (!res.HasBounds || !posedCut) return res;
        // a posed slice paints at its own pose, bounded by its marker clip (what its cut hands its parent)
        var painted = new SpanRecordResult();
        RectF p = SliceRecorder.Offset(res.SubtreeBounds, odx, ody);
        painted.Include(e.SliceClipPart.IsInfinite ? p : p.Intersect(e.SliceClipPart));
        return painted;
    }

    /// <summary>The scroll state a viewport's own bytes depend on that moves without a dirty bit (see
    /// <see cref="MixScrollViewport"/>), as one value: a partial keep may keep a viewport's bytes only under the state they
    /// were recorded under.</summary>
    private ulong ViewportStateSig(SceneRecordingSnapshot scene, NodeHandle node, NodeFlags flags)
    {
        ulong h = 0x5C011_5EED_0001UL;
        MixScrollViewport(scene, node, flags, ref h);
        MixVirtualItemBand(scene, node, ref h);
        return h;
    }

    // ── --fg rewalk-validate ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A slice the partial keep would have kept was re-walked instead: did the walk write exactly the bytes it holds?</summary>
    internal static void ValidateKept(SliceRecorder sl, int slot, DrawList arena)
    {
        s_validatedKeeps++;
        if (!arena.BytesEqualPrior())
        {
            // float drift of a recomputed world or clip (< 0.001): the same pixels — counted apart
            if (arena.DiffersOnlyByFloatDrift(1e-3f)) { s_keepDrift++; MaybeLogValidation(); return; }
            s_keepMismatches++;
            arena.FirstOpDifferingFromPrior(out int at, out int opAt, out int curOp, out int priorOp);
            Console.Error.WriteLine($"[rewalk-validate] MISMATCH kept arena: slot={slot} node={sl.NodeIndexOf(slot)}:{sl.GenOf(slot)} role={sl.RoleOf(slot)} bytes={arena.BytePosition} firstDiff@{at} (op@{opAt}) op {(DrawOp)priorOp} -> {(DrawOp)curOp}");
            // what the kept bytes named there (a child slice's marker), what the re-walk wrote in its place, and the cut budget it saw.
            // Both buffers share every byte before the first difference, so the op starts at the same offset in each.
            int body = System.Runtime.CompilerServices.Unsafe.SizeOf<CompositeSliceCmd>();
            ReadOnlySpan<byte> prior = arena.PriorBytes(0, arena.PriorByteLength), cur = arena.Bytes;
            if ((DrawOp)priorOp == DrawOp.CompositeSlice && opAt >= 0 && opAt + sizeof(int) + body <= prior.Length)
            {
                var m = System.Runtime.InteropServices.MemoryMarshal.Read<CompositeSliceCmd>(prior[(opAt + sizeof(int))..]);
                Console.Error.WriteLine($"[rewalk-validate]   kept marker:    child={m.NodeIndex}:{m.Gen} sub={m.Sub} kind={(Tiles.SliceKind)m.Kind} flags={m.Flags} | {sl.BudgetDiag(m.NodeIndex, m.Gen, m.Sub)}");
                if ((DrawOp)curOp == DrawOp.CompositeSlice && opAt + sizeof(int) + body <= cur.Length)
                {
                    var n = System.Runtime.InteropServices.MemoryMarshal.Read<CompositeSliceCmd>(cur[(opAt + sizeof(int))..]);
                    Console.Error.WriteLine($"[rewalk-validate]   rewalk marker:  child={n.NodeIndex}:{n.Gen} sub={n.Sub} kind={(Tiles.SliceKind)n.Kind} flags={n.Flags}");
                    Console.Error.WriteLine($"[rewalk-validate]   differs: {MarkerDiff(in m, in n)}");
                }
            }
        }
        MaybeLogValidation();
    }

    /// <summary>Validation diagnostics: every field two <see cref="CompositeSliceCmd"/> markers differ in, as kept -> re-walked.</summary>
    private static string MarkerDiff(in CompositeSliceCmd a, in CompositeSliceCmd b)
    {
        var sb = new System.Text.StringBuilder();
        void D(bool same, string name, object x, object y) { if (!same) sb.Append(name).Append('(').Append(x).Append(" -> ").Append(y).Append(") "); }
        D(a.NodeIndex == b.NodeIndex && a.Gen == b.Gen && a.Sub == b.Sub, "child", $"{a.NodeIndex}:{a.Gen}/{a.Sub}", $"{b.NodeIndex}:{b.Gen}/{b.Sub}");
        D(a.Kind == b.Kind, "kind", a.Kind, b.Kind);
        D(a.Flags == b.Flags, "flags", a.Flags, b.Flags);
        D(a.Clip == b.Clip, "clip", a.Clip, b.Clip);
        D(a.OuterClip == b.OuterClip, "outerClip", a.OuterClip, b.OuterClip);
        if (a.Layer != b.Layer)
        {
            PushLayerCmd x = a.Layer, y = b.Layer;
            int before = sb.Length;
            D(x.Kind == y.Kind, "layer.kind", x.Kind, y.Kind);
            D(x.DeviceRect == y.DeviceRect, "layer.rect", x.DeviceRect, y.DeviceRect);
            D(x.CompositeClip == y.CompositeClip, "layer.compositeClip", x.CompositeClip, y.CompositeClip);
            D(x.GroupAlpha == y.GroupAlpha, "layer.alpha", x.GroupAlpha, y.GroupAlpha);
            D(x.FadeEdges == y.FadeEdges, "layer.fadeEdges", x.FadeEdges, y.FadeEdges);
            D(x.FadeBandL == y.FadeBandL && x.FadeBandT == y.FadeBandT && x.FadeBandR == y.FadeBandR && x.FadeBandB == y.FadeBandB,
                "layer.fadeBands(LTRB)", $"{x.FadeBandL},{x.FadeBandT},{x.FadeBandR},{x.FadeBandB}", $"{y.FadeBandL},{y.FadeBandT},{y.FadeBandR},{y.FadeBandB}");
            D(x.BlurSigma == y.BlurSigma, "layer.blur", x.BlurSigma, y.BlurSigma);
            D(x.Radii == y.Radii && x.FadeFalloff == y.FadeFalloff && x.FadeIntensity == y.FadeIntensity, "layer.shape", x.Radii, y.Radii);
            if (sb.Length == before) D(false, "layer", x, y);   // an acrylic / feather field: print both whole
        }
        D(a.InnerClip == b.InnerClip, "innerClip", a.InnerClip, b.InnerClip);
        D(a.PopRect == b.PopRect, "popRect", a.PopRect, b.PopRect);
        D(a.SortKey == b.SortKey && a.LayerSortKey == b.LayerSortKey, "sortKeys", a.SortKey, b.SortKey);
        return sb.Length == 0 ? "(no field: padding bytes)" : sb.ToString();
    }

    private static void ValidateCut(SceneRecordingSnapshot scene, SliceRecorder sl, int slot, in CutEntry old, in CutEntry cur,
        ref RecordAccumulator stats)
    {
        s_cutsChecked++;
        // the recomputed offset and clip must be what the walk carries (every cut: they are what a re-entry would use)
        sl.CurrentAcc(slot, scene, out float dx, out float dy);
        bool clipKnown = DeriveSliceClipWith(scene, sl, slot, in cur, out RectF clip);
        if (MathF.Abs(dx - stats.CurDx) > 1e-3f || MathF.Abs(dy - stats.CurDy) > 1e-3f || (clipKnown && !SameClip(in clip, in stats.SliceClip)))
        {
            s_offsetMismatches++;
            Console.Error.WriteLine($"[rewalk-validate] MISMATCH offset: slot={slot} node={cur.NodeIndex} walk=({stats.CurDx},{stats.CurDy}) clip={stats.SliceClip} derived=({dx},{dy}) clip={clip}");
        }
        // a cut under a parent the partial keep would have kept must hand its root walk the inputs it saved
        if (old.Frame != 0 && sl.ExpectingSameBytes(cur.ParentSlot) && old.NodeIndex == cur.NodeIndex && old.Gen == cur.Gen
            && old.ParentInMotion == cur.ParentInMotion && !SameEntry(in old, in cur))
        {
            if (SameEntry(in old, in cur, drift: 1e-3f)) { s_entryDrift++; return; }   // float drift of a world / clip (< 0.001 DIP): same pixels
            s_entryMismatches++;
            Console.Error.WriteLine($"[rewalk-validate] MISMATCH cut inputs: slot={slot} node={cur.NodeIndex}:{cur.Gen} kind={cur.Kind} differs={EntryDiff(in old, in cur)}");
        }
        MaybeLogValidation();
    }

    private static bool DeriveSliceClipWith(SceneRecordingSnapshot scene, SliceRecorder sl, int slot, in CutEntry cur, out RectF clip)
    {
        // the slot's own entry is being written now: derive with the new one in place of the stored one
        ref CutEntry stored = ref sl.CutOf(slot);
        CutEntry keep = stored;
        stored = cur;
        bool ok = DeriveSliceClip(scene, sl, slot, out clip);
        stored = keep;
        return ok;
    }

    private static bool SameClip(in RectF a, in RectF b)
        => a.IsInfinite ? b.IsInfinite
            : !b.IsInfinite && MathF.Abs(a.X - b.X) <= 1e-3f && MathF.Abs(a.Y - b.Y) <= 1e-3f && MathF.Abs(a.W - b.W) <= 1e-3f && MathF.Abs(a.H - b.H) <= 1e-3f;

    private static string EntryDiff(in CutEntry a, in CutEntry b)
    {
        var sb = new System.Text.StringBuilder();
        void D(bool same, string name, object x, object y) { if (!same) sb.Append(name).Append('(').Append(x).Append("->").Append(y).Append(") "); }
        D(a.ParentSlot == b.ParentSlot, "parentSlot", a.ParentSlot, b.ParentSlot);
        D(a.ParentWorld == b.ParentWorld, "world", a.ParentWorld, b.ParentWorld);
        D(a.ParentOpacity == b.ParentOpacity, "opacity", a.ParentOpacity, b.ParentOpacity);
        D(a.Depth == b.Depth, "depth", a.Depth, b.Depth);
        D(a.Clip == b.Clip, "clip", a.Clip, b.Clip);
        D(a.ParentScaleX == b.ParentScaleX && a.ParentScaleY == b.ParentScaleY, "scale", a.ParentScaleX, b.ParentScaleX);
        D(a.ParentInMotion == b.ParentInMotion, "inMotion", a.ParentInMotion, b.ParentInMotion);
        D(a.Inherited.HoverT == b.Inherited.HoverT && a.Inherited.PressT == b.Inherited.PressT, "hoverPress", a.Inherited.HoverT, b.Inherited.HoverT);
        D(a.Inherited.InteractiveFlags == b.Inherited.InteractiveFlags, "iflags", a.Inherited.InteractiveFlags, b.Inherited.InteractiveFlags);
        D(a.Inherited.HasProgress == b.Inherited.HasProgress && a.Inherited.Disabled == b.Inherited.Disabled, "progress", a.Inherited.HasProgress, b.Inherited.HasProgress);
        D(a.SelfHasLocal == b.SelfHasLocal && a.SelfLocal == b.SelfLocal, "selfLocal", a.SelfLocal, b.SelfLocal);
        D(a.SelfPose == b.SelfPose, "pose", a.SelfPose, b.SelfPose);
        D(a.SelfOmitLayer == b.SelfOmitLayer && a.SelfAcrylic == b.SelfAcrylic && a.SelfStickyClip == b.SelfStickyClip, "selfFlags", a.SelfOmitLayer, b.SelfOmitLayer);
        D(a.CarryAdditive == b.CarryAdditive && a.ParentAdditiveDepth == b.ParentAdditiveDepth, "additive", a.ParentAdditiveDepth, b.ParentAdditiveDepth);
        D(a.SliceClipPart == b.SliceClipPart, "sliceClip", a.SliceClipPart, b.SliceClipPart);
        D(a.Kind == b.Kind && a.OpacityGroup == b.OpacityGroup, "kind", a.Kind, b.Kind);
        return sb.ToString();
    }

    private static bool Near(in Affine2D a, in Affine2D b, float d)
        => MathF.Abs(a.M11 - b.M11) <= d && MathF.Abs(a.M12 - b.M12) <= d && MathF.Abs(a.M21 - b.M21) <= d && MathF.Abs(a.M22 - b.M22) <= d
           && MathF.Abs(a.Dx - b.Dx) <= d && MathF.Abs(a.Dy - b.Dy) <= d;

    private static bool SameEntry(in CutEntry a, in CutEntry b, float drift = 0f)
        => a.ParentSlot == b.ParentSlot && a.Kind == b.Kind && a.OpacityGroup == b.OpacityGroup
           && (drift > 0f ? Near(in a.ParentWorld, in b.ParentWorld, drift) : a.ParentWorld == b.ParentWorld)
           && a.ParentOpacity == b.ParentOpacity && a.Depth == b.Depth && (drift > 0f ? SameClip(in a.Clip, in b.Clip) : a.Clip == b.Clip)
           && a.ParentScaleX == b.ParentScaleX
           && a.ParentScaleY == b.ParentScaleY && a.ParentInMotion == b.ParentInMotion && a.Inherited.HoverT == b.Inherited.HoverT
           && a.Inherited.PressT == b.Inherited.PressT && a.Inherited.InteractiveFlags == b.Inherited.InteractiveFlags
           && a.Inherited.HasProgress == b.Inherited.HasProgress && a.Inherited.Disabled == b.Inherited.Disabled
           && a.Inherited.UnderElevateRoot == b.Inherited.UnderElevateRoot && a.SelfHasLocal == b.SelfHasLocal && a.SelfLocal == b.SelfLocal
           && a.SelfOmitLayer == b.SelfOmitLayer && a.SelfPose == b.SelfPose && a.SelfAcrylic == b.SelfAcrylic
           && a.SelfStickyClip == b.SelfStickyClip && a.CarryAdditive == b.CarryAdditive && a.ParentAdditiveDepth == b.ParentAdditiveDepth
           && (drift > 0f ? SameClip(in a.SliceClipPart, in b.SliceClipPart) : a.SliceClipPart == b.SliceClipPart);

    private static void MaybeLogValidation()
    {
        long now = Environment.TickCount64;
        if (now - s_lastValidateLog < 2000) return;
        s_lastValidateLog = now;
        Console.Error.WriteLine($"[rewalk-validate] {(s_keepMismatches + s_entryMismatches + s_offsetMismatches == 0 ? "ok" : "FAIL")} keepsChecked={s_validatedKeeps} keepMismatches={s_keepMismatches} keepDrift={s_keepDrift} cutsChecked={s_cutsChecked} entryMismatches={s_entryMismatches} entryDrift={s_entryDrift} offsetMismatches={s_offsetMismatches} cuePatches={s_cuePatches}");
    }
}
