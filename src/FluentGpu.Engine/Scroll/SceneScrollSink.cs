using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Scroll;

/// <summary>
/// The UI-side <see cref="IScrollSink"/> implementation (scroll-v3-plan §3.1/§10 WP-B item 3): the ONE place a kernel
/// write becomes scene state. <see cref="Apply"/> is called by <c>ScrollKernel.Tick</c>/<c>Reclamp</c> — never
/// anywhere else — exactly once per body that moved this pass, and does four things in order: (1) commit the result
/// columns through <see cref="ScrollState.ApplyMotion"/>, minting the one valid <see cref="ScrollWriteToken"/> for the
/// call; (2) write the content child's <c>LocalTransform</c> (<see cref="ScrollContentTransform"/>, ported verbatim
/// from the old <c>OverscrollPhysics</c>) and mark it dirty; (3) re-evaluate this viewport's generic scroll bindings
/// (<c>ScrollBindEval.ApplyContinuous</c>); (4) re-check the virtualization realize window and wake the host if it
/// needs more rows. Zero allocations.
/// </summary>
public sealed class SceneScrollSink : IScrollSink
{
    private readonly SceneStore _scene;
    private readonly Action _wake;

    public SceneScrollSink(SceneStore scene, Action wake)
    {
        _scene = scene;
        _wake = wake;
    }

    /// <summary>The frame counter the host bumps once per frame (scroll-v3-plan §3.1 "LastMovedFrame"). Stamped into
    /// every <see cref="ScrollWriteToken"/> minted by <see cref="Apply"/>, so <c>ScrollState.LastMovedFrame</c> —
    /// and, downstream, <c>ScrollBarChrome</c>'s "moved this frame" test — reads this sink's notion of "now" rather
    /// than a second independent clock.</summary>
    public uint FrameIndex { get; set; }

    /// <summary>Set once, after construction (scroll-v3-plan §3.1/§4/§10 WP-B item 4) — notified with the touched
    /// node index (and whether THIS call's write actually moved the visual — offset/band/zoom — versus a geometry-
    /// only re-touch) for every <see cref="Apply"/> call, so <see cref="ScrollBarChrome"/> can arm a viewport's fade/
    /// expand cycle purely from "this body really moved" — even when no PointerOver hover ever fired (a wheel notch
    /// with no prior pointer-move, or a touch pan, which never latches hover at all). Deliberately NOT keyed off
    /// <c>ScrollState.LastMovedFrame</c>/<c>ScrollWrite.Moved</c>: the kernel's write mask sets the OffsetX/Y|BandX/Y
    /// bits on EVERY <see cref="Apply"/> call regardless of whether the numeric value actually changed (e.g. an
    /// idempotent same-geometry <c>SetFrame</c> repost from a relayout — <c>FlexLayout.ArrangeViewport</c> posts one
    /// unconditionally, and <c>ScrollKernel</c>'s structural handler marks it touched unconditionally too), so that
    /// mask alone can't tell "really moved" from "merely touched". This sink computes the real answer itself, from
    /// values it already reads, before it becomes the ONLY signal chrome's auto-hide timer trusts — see
    /// <see cref="ScrollBarChrome.NotifyMoved"/>. Only identity + a bool cross this wire, never a raw motion value,
    /// so "motion never touches chrome" still holds at the VALUE level.</summary>
    public ScrollBarChrome? Chrome { get; set; }

    /// <summary>True once, this frame, iff at least one <see cref="Apply"/> call so far actually changed offset/band/
    /// zoom (the same <c>realMove</c> test <see cref="ScrollBarChrome.NotifyMoved"/> uses — see its remarks on why
    /// the kernel's own <c>ScrollFrameSummary.AnyMoved</c> can't be trusted for this: it is touched-count-based, true
    /// on an idempotent same-geometry <c>SetFrame</c> repost same as a real write). AppHost's FLIP-suppression
    /// latch (<c>MotionSuppressionSource.Scroll</c>) reads this instead of the kernel summary for the same reason
    /// chrome does. Cleared by <see cref="BeginFrame"/>, which the host calls once per frame alongside the
    /// <see cref="FrameIndex"/> bump — so this is the sink's own per-frame "did anything really move yet" latch, not
    /// a persistent flag that survives past the frame it was set in.</summary>
    public bool AnyRealMoveThisFrame { get; private set; }

    /// <summary>Reset the per-frame <see cref="AnyRealMoveThisFrame"/> latch — call exactly once per frame, before
    /// the kernel's <c>Tick</c>/<c>Reclamp</c> (same place <see cref="FrameIndex"/> is bumped).</summary>
    public void BeginFrame() => AnyRealMoveThisFrame = false;

    public void Apply(int node, in ScrollWrite w)
    {
        ref ScrollState sc = ref _scene.ScrollRefByIndex(node);
        float prevOffX = sc.OffsetX, prevOffY = sc.OffsetY, prevBandX = sc.BandX, prevBandY = sc.BandY, prevZoom = sc.ZoomFactor;
        var token = ScrollWriteToken.Mint(FrameIndex);
        sc.ApplyMotion(in token, in w);
        bool realMove = sc.OffsetX != prevOffX || sc.OffsetY != prevOffY
            || sc.BandX != prevBandX || sc.BandY != prevBandY || sc.ZoomFactor != prevZoom;
        if (realMove) AnyRealMoveThisFrame = true;
        Chrome?.NotifyMoved(node, realMove);

        bool horizontal = sc.Orientation == 1;
        float mainOffset = horizontal ? w.OffsetX : w.OffsetY;
        float mainBand = horizontal ? w.BandX : w.BandY;
        float zoom = w.Zoom > 0f ? w.Zoom : 1f;

        NodeHandle content = sc.ContentNode;
        if (!content.IsNull && _scene.IsLive(content))
        {
            ref NodePaint cp = ref _scene.Paint(content);
            // Edge-band-sign guard: TRANSFORM-LOCAL only (scroll-v3-plan §3.1) — unlike the pre-v3 dispatcher, this
            // sink can never write BandX/BandY itself (ApplyMotion is the only writer of result columns), so a
            // wrong-sign band at the clamp is corrected here for the paint, not persisted back onto sc. The kernel
            // owns getting the stored band right; this is strictly a 1-frame flash guard on the composed transform.
            float maxOff = horizontal ? MathF.Max(0f, sc.ContentW * zoom - sc.ViewportW)
                                       : MathF.Max(0f, sc.ContentH * zoom - sc.ViewportH);
            float guardedBand = ScrollContentTransform.GuardBandSign(mainBand, mainOffset, maxOff);
            ScrollContentTransform.WriteContentTransform(ref cp, in _scene.Bounds(content), horizontal, mainOffset, guardedBand,
                w.Zoom, _scene.DeviceScale);
            _scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);

            NodeHandle n = _scene.HandleAt(node);
            FluentGpu.Animation.ScrollBindEval.ApplyContinuous(_scene, n, ref sc);

            // Virtualization: keep transform-only scroll while the visible band remains inside the realized guard
            // band (ported from the pre-v3 InputDispatcher.ApplyScrollPosition — legacy snapshot :3915-3939). Zoomed
            // content maps the on-screen viewport band back to unscaled content-space units, matching the item
            // extents the layout/extent-table use.
            if (sc.ItemCount > 0)
            {
                float vp = (horizontal ? sc.ViewportW : sc.ViewportH) / zoom;
                float contentNext = mainOffset / zoom;
                int visibleFirst, visibleLast;
                if (sc.Layout is not null)   // fixed-geometry (stack/grid/custom)
                {
                    float cross = horizontal ? (sc.ContentH > 0f ? sc.ContentH : sc.ViewportH)
                                              : (sc.ContentW > 0f ? sc.ContentW : sc.ViewportW);
                    sc.Layout.Window(sc.ItemCount, cross, vp, contentNext, 0, out visibleFirst, out visibleLast);
                }
                else if (_scene.TryGetExtents(n, out var t) && t is not null)   // variable (extent table)
                {
                    visibleFirst = t.IndexAt(contentNext);
                    visibleLast = Math.Min(sc.ItemCount, t.IndexAt(contentNext + vp) + 1);
                }
                else { visibleFirst = visibleLast = 0; }

                if (VirtualWindowing.NeedsRealize(in sc, visibleFirst, visibleLast))
                {
                    _scene.Mark(n, NodeFlags.VirtualRangeDirty);
                    _wake();
                }
            }
        }

        // scroll-v3-plan §2.3: WP-F is retargeting this call's signature to (int node, byte activity, byte writer,
        // float off) concurrently — this call is already written in THAT shape (activity/writer as byte, not the
        // pre-v3 Phase/ScrollWriter types) so it compiles once WP-F lands; until then this is a build error in
        // ScrollTrace.cs's package, not here.
        ScrollTrace.OffsetWrite(node, (byte)w.Activity, (byte)w.Writer, mainOffset);

        // scroll-v3-plan §7.2 (WP-R1): edge-driven ScrollController republish — one dictionary-lookup call, allocation-free.
        _scene.ScrollControllers.NotifyMoved(node);
    }

    /// <summary>The ONE token type that unlocks <see cref="ScrollState.ApplyMotion"/> (scroll-v3-plan §3.1 "Token").
    /// Minted only by <see cref="Apply"/>, for the duration of that call. A ref struct cannot be stored/boxed/escape
    /// a stack frame, so it cannot outlive the <see cref="Apply"/> call that minted it — that alone is the release
    /// guarantee (the struct collapses to empty, <see cref="IsValid"/> is a compile-time <c>true</c> constant, and
    /// <see cref="ScrollState.ApplyMotion"/> is <c>AggressiveInlining</c>, so the whole check erases). In
    /// DEBUG/FLUENTGPU_DIAG an extra nonce is compared against a <see cref="ThreadStatic"/> "currently minting" value
    /// so a token constructed OUTSIDE an active <see cref="Apply"/> call (impossible today — the ctor is private —
    /// but the belt-and-suspenders the plan asks for) reads as invalid rather than silently valid.</summary>
    public readonly ref struct ScrollWriteToken
    {
        /// <summary>The frame this write happened on — see <see cref="SceneScrollSink.FrameIndex"/>. Carried on the
        /// token (not a second ApplyMotion parameter) so the pinned <c>ApplyMotion(in ScrollWriteToken, in ScrollWrite)</c>
        /// signature never has to grow: the token IS the sink's identity for this call, and the frame stamp is part
        /// of that identity.</summary>
        public readonly uint FrameIndex;

#if DEBUG || FLUENTGPU_DIAG
        [ThreadStatic] private static int s_liveNonce;
        private static int s_nextNonce;
        private readonly int _nonce;

        private ScrollWriteToken(uint frameIndex, int nonce)
        {
            FrameIndex = frameIndex;
            _nonce = nonce;
        }

        internal static ScrollWriteToken Mint(uint frameIndex)
        {
            int nonce = ++s_nextNonce;
            if (nonce == 0) nonce = ++s_nextNonce;   // never mint the sentinel 0 (== "no live token")
            s_liveNonce = nonce;
            return new ScrollWriteToken(frameIndex, nonce);
        }

        internal bool IsValid => _nonce != 0 && _nonce == s_liveNonce;
#else
        private ScrollWriteToken(uint frameIndex) => FrameIndex = frameIndex;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static ScrollWriteToken Mint(uint frameIndex) => new(frameIndex);

        internal bool IsValid
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => true;
        }
#endif
    }
}
