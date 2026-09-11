using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluentGpu.Render;

/// <summary>
/// Size bucket for the OPACITY/BLUR layer pool's transient region targets (the sibling of
/// <see cref="AcrylicBackdropMath.BucketDim"/>, which stays power-of-two because the acrylic dual-Kawase pyramid
/// halves each level and wants halving-friendly dimensions).
///
/// A layer-group / blur-scratch target is sized to a DAMAGE box, not to a pyramid level, and those boxes cluster in
/// the few-hundred-pixel range (a lyric line's halo-inflated strip, a card's drawn extent). Next-power-of-two there
/// wastes up to 4x area — a 364x144 guarded strip becomes 512x256, 2.5x the texels it needs — and on a UMA adapter
/// every wasted texel is pinned host memory. So this bucket is LINEAR in 64-px steps up to
/// <see cref="LinearCeiling"/> and only then falls back to powers of two:
///
///   px  &lt;= 2048 : ceil to a multiple of 64  (64, 128, 192, ... 2048)
///   px  &gt;  2048 : next power of two          (4096, 8192, ...)        — few very large buckets
///
/// The ceiling is 2048 rather than something small because the po2 step that matters most is the one that straddles a
/// typical window WIDTH: at 1195 px the po2 ladder jumps to 2048 — a 1.7x waste on that axis alone, which was enough
/// to cancel the whole saving for a full-width band and push it back onto the canvas lease. Linear to 2048 makes that
/// same band 1216 px wide.
///
/// Reuse is preserved despite the finer granularity because every pool lease is BEST-FIT >= the bucket (the SMALLEST
/// free slot that fits; a larger one still serves a smaller request, and the shader clamps to the used sub-rect via
/// <see cref="AcrylicBackdropMath.SampleWindow"/>), so a finer ladder shrinks the surface a COLD lease creates
/// without fragmenting the WARM free list.
///
/// A bucketed dimension pair CAN coincide with the canvas size (a 1280x768 window buckets to itself). That aliasing is
/// harmless by design: the pool classifies a slot by its SIZE, not its provenance, so such a surface is a legitimate
/// canvas-sized group RT and a legitimate member of the canvas warm-reserve class either way.
/// </summary>
public static class LayerTargetBucket
{
    /// <summary>Smallest bucket (also the step below <see cref="LinearCeiling"/>).</summary>
    public const int MinDim = 64;

    /// <summary>The linear step used at or below <see cref="LinearCeiling"/>.</summary>
    public const int LinearStep = 64;

    /// <summary>Above this the ladder switches to powers of two. Set past a typical window dimension on purpose — see
    /// the class remarks on why a po2 step at 1195 -&gt; 2048 cancelled the saving for a full-width band.</summary>
    public const int LinearCeiling = 2048;

    /// <summary>Bucket one dimension. Monotone non-decreasing, always &gt;= <paramref name="px"/>, always &gt;=
    /// <see cref="MinDim"/>.</summary>
    public static int Dim(int px)
    {
        if (px <= MinDim) return MinDim;
        if (px <= LinearCeiling) return (px + (LinearStep - 1)) / LinearStep * LinearStep;
        int b = LinearCeiling;
        while (b < px) b <<= 1;
        return b;
    }

    /// <summary>Bytes a B8G8R8A8 target of these dimensions occupies (the census/budget unit).</summary>
    public static long Bytes(int w, int h) => (long)Math.Max(0, w) * Math.Max(0, h) * 4L;
}

/// <summary>What <see cref="LayerTargetTrim.Classify"/> decided for one pool slot on a fenced frame boundary.</summary>
public enum LayerTrimVerdict
{
    /// <summary>Leave the slot's resource live.</summary>
    Keep = 0,
    /// <summary>Retire it — move the resource to the fence-gated deferred-release queue.</summary>
    Retire = 1,
}

/// <summary>
/// The layer pool's IDLE-TRIM policy, extracted from the D3D12 leaf so it is one source of truth for
/// <c>OpacityLayerCompositor</c>, <c>AcrylicCompositor</c> and the headless VerticalSlice gates
/// (<c>gate.layerpool.*</c>) — the same split <see cref="AcrylicBackdropMath"/>/<see cref="SelfBlurRegion"/> use.
///
/// <para><b>The problem it solves.</b> Pool slots are created LAZILY on lease and were previously released only when
/// the pool was FULL (LRU eviction) or after a long idle window. On a UMA/iGPU adapter every GPU allocation is pinned
/// into resident write-combine segments, so a canvas-sized group target (window w*h*4 — 3.5 MiB at 1195x767) that
/// nothing has leased for ten seconds is ten seconds of working set for nothing. Measured on an Adreno X1-85:
/// canvas-class group targets were the largest single class in the engine's tracked GPU bytes at launch while
/// <c>blurGroups=1 blurHeld=0</c> on most frames — i.e. almost all of it idle.</para>
///
/// <para><b>The policy.</b> Three windows, all counted in SUBMITTED frames (a slot's <c>IdleFrames</c> is bumped once
/// per frame it was not leased):</para>
/// <list type="bullet">
/// <item><b><see cref="IdleFramesWeak"/> / <see cref="IdleFramesStrong"/></b> — the ordinary trim window. A free
/// bucketed scratch slot idle this long is retired. A CANVAS-sized slot is retired too, EXCEPT the
/// <see cref="WarmCanvasReserve"/> most-recently-used ones: the common frame opens 1-2 groups, and a cold canvas
/// lease is a multi-MiB <c>CreateCommittedResource</c> INSIDE the submit, which is exactly the mid-frame hitch the
/// pool exists to avoid. The reserve is the "warm" half of the trade.</item>
/// <item><b><see cref="ColdIdleFrames"/></b> — the cold window. Past this, even the warm reserve goes: a quarter of a
/// minute with no layer at all means the surface that used them is closed (a dismissed flyout, a navigated-away page),
/// so holding two canvas targets against a possible return is no longer a trade, it is a leak with a nice name.</item>
/// <item><b><see cref="PinIdleFrames"/></b> — retained region pins (a blur-cache entry) reclaim on the short window
/// regardless of tier: a live pin is hit by <c>FindPin</c> on every submit, so only an ORPHAN (a rect/sigma a row has
/// left behind) ever accumulates idle frames at all.</item>
/// </list>
///
/// <para><b>Weak-tier hard cap.</b> <see cref="WeakCanvasHardCap"/> bounds how many canvas-sized slots may be LIVE at
/// once on a weak adapter even before any idle window elapses (adreno-hang-fixes.md M5) — idle ones beyond the cap are
/// retired immediately so idle multi-MiB scratch cannot pile onto a tiny LOCAL segment. In-use slots are never
/// touched, so the frame's actual nesting depth is always honored.</para>
///
/// <para><b>The fence rule is not part of this policy and must not be re-litigated here.</b> "Retire" means
/// <i>move the resource to the deferred-release queue</i>, never <i>Release() it now</i>. The actual release is gated
/// on <see cref="CanRelease"/> (the frame fence has passed the slot's last recorded use), which is the
/// threading-render-seam deferred-reclaim convention the whole renderer uses. Trimming therefore cannot free a
/// resource a submit in flight still references, no matter what <see cref="Classify"/> returns.</para>
/// </summary>
public static class LayerTargetTrim
{
    /// <summary>Ordinary trim window on a WEAK (UMA / iGPU / WARP) adapter: 120 submitted frames — ~1 s at 120 Hz,
    /// ~2 s at 60 Hz. Short because every byte here is resident host memory on such an adapter.</summary>
    public const int IdleFramesWeak = 120;

    /// <summary>Ordinary trim window on a discrete adapter: 600 submitted frames (~10 s). Dedicated VRAM makes an idle
    /// pooled target far cheaper, and re-creating one costs a real driver allocation, so the window stays generous.</summary>
    public const int IdleFramesStrong = 600;

    /// <summary>The COLD window, both tiers: past 900 idle submitted frames (~15 s at 60 Hz) the warm reserve is
    /// released too. Deliberately STRICTLY GREATER than both ordinary windows above — if it equalled
    /// <see cref="IdleFramesStrong"/> there would be no reserve stage at all on a discrete adapter, because the cold
    /// clause would fire on the same frame the ordinary one does.</summary>
    public const int ColdIdleFrames = 900;

    /// <summary>Retained region pins reclaim on the short window on every tier — see the class remarks.</summary>
    public const int PinIdleFrames = 120;

    /// <summary>How many canvas-sized slots survive the ordinary trim window so the common 1-2 groups per frame never
    /// pay a mid-frame allocation. Released by the cold window.</summary>
    public const int WarmCanvasReserve = 2;

    /// <summary>Max LIVE canvas-sized slots on a weak adapter; idle ones beyond this are retired immediately (M5).
    /// In-use slots are exempt, so a deeper nesting still renders correctly (it just re-creates next frame).</summary>
    public const int WeakCanvasHardCap = 4;

    /// <summary>The ordinary trim window for the running adapter tier.</summary>
    public static int IdleFrames(bool weak) => weak ? IdleFramesWeak : IdleFramesStrong;

    /// <summary>May a retired resource be Released now? The frame fence must have passed its last recorded use — the
    /// deferred-reclaim convention (threading-render-seam.md). This is the ONLY gate on the actual release; the
    /// trim decision above never releases anything by itself.</summary>
    public static bool CanRelease(ulong lastUseFence, ulong completedFence) => lastUseFence <= completedFence;

    /// <summary>
    /// Trim verdict for ONE pool slot, evaluated on a fenced frame boundary.
    /// </summary>
    /// <param name="inUse">Leased by a group/pass still open this frame — always <see cref="LayerTrimVerdict.Keep"/>.</param>
    /// <param name="isPin">A RETAINED region pin (blur cache / acrylic backdrop cache) rather than transient scratch.</param>
    /// <param name="isCanvasSized">The slot's surface is the full canvas (the expensive class the reserve protects).</param>
    /// <param name="idleFrames">Consecutive submitted frames without a lease.</param>
    /// <param name="weak"><see cref="FluentGpu.Foundation.GpuProfile.IsWeak"/>.</param>
    /// <param name="idleCanvasRank">Recency rank of this slot among the IDLE canvas-sized slots — 0 = the most recently
    /// used, 1 = the next, and so on. Ignored unless <paramref name="isCanvasSized"/>. Callers derive it without
    /// allocating by counting idle canvas slots with a strictly greater last-use fence.</param>
    public static LayerTrimVerdict Classify(
        bool inUse, bool isPin, bool isCanvasSized, int idleFrames, bool weak, int idleCanvasRank)
    {
        if (inUse) return LayerTrimVerdict.Keep;
        if (isPin)
            return idleFrames > PinIdleFrames ? LayerTrimVerdict.Retire : LayerTrimVerdict.Keep;
        if (!isCanvasSized)
            return idleFrames > IdleFrames(weak) ? LayerTrimVerdict.Retire : LayerTrimVerdict.Keep;

        // Canvas-sized transient scratch — the class the reserve and the weak hard cap both talk about.
        if (weak && idleCanvasRank >= WeakCanvasHardCap) return LayerTrimVerdict.Retire;   // M5: immediate
        if (idleFrames > ColdIdleFrames) return LayerTrimVerdict.Retire;                    // cold: the reserve goes too
        if (idleFrames > IdleFrames(weak) && idleCanvasRank >= WarmCanvasReserve) return LayerTrimVerdict.Retire;
        return LayerTrimVerdict.Keep;
    }
}

/// <summary>
/// POOLED-vs-IN-USE byte accounting for one compositor's layer pool — the honest half of the <c>gpu bytes</c> census.
/// <c>gpu bytes</c> is a single tracked-resource total, so a pool holding four idle canvas targets is indistinguishable
/// from one actively compositing four groups; on a UMA adapter, where all of it is resident host memory, that is the
/// difference between a working-set bug and a working-set cost. Every field is a plain sum, so building this is a
/// fixed-bucket walk with no allocation (the string form is only rendered by the census sampler's cadence).
/// </summary>
public readonly record struct LayerTargetCensus(
    long InUseBytes, int InUseCount,
    long FreeBytes, int FreeCount,
    long PinBytes, int PinCount,
    long RetiredBytes, int RetiredCount)
{
    /// <summary>Everything this pool currently holds, whether leased, idle, retained or awaiting its fence.</summary>
    public long TotalBytes => InUseBytes + FreeBytes + PinBytes + RetiredBytes;

    /// <summary>Slots holding a resource (retired entries are no longer slots, so they are not counted here).</summary>
    public int LiveCount => InUseCount + FreeCount + PinCount;

    /// <summary>Sum two pools' censuses (the device reports opacity + acrylic + baked-blur as one line).</summary>
    public static LayerTargetCensus operator +(LayerTargetCensus a, LayerTargetCensus b) => new(
        a.InUseBytes + b.InUseBytes, a.InUseCount + b.InUseCount,
        a.FreeBytes + b.FreeBytes, a.FreeCount + b.FreeCount,
        a.PinBytes + b.PinBytes, a.PinCount + b.PinCount,
        a.RetiredBytes + b.RetiredBytes, a.RetiredCount + b.RetiredCount);

    /// <summary>Accumulate one slot. <paramref name="bytes"/> is the surface size, not the used sub-rect: the driver
    /// pins the whole surface, so the census must report the whole surface.</summary>
    public LayerTargetCensus WithSlot(long bytes, bool inUse, bool isPin) => isPin
        ? this with { PinBytes = PinBytes + bytes, PinCount = PinCount + 1 }
        : inUse
            ? this with { InUseBytes = InUseBytes + bytes, InUseCount = InUseCount + 1 }
            : this with { FreeBytes = FreeBytes + bytes, FreeCount = FreeCount + 1 };

    /// <summary>Accumulate one entry still on the fence-gated release queue.</summary>
    public LayerTargetCensus WithRetired(long bytes)
        => this with { RetiredBytes = RetiredBytes + bytes, RetiredCount = RetiredCount + 1 };

    /// <summary>One compact census token set — <c>inuse=3.5/1 free=7.0/2 pin=0.4/6 retire=0.0/0</c> in MiB — for the
    /// <c>gpu</c> census line. Allocates a string, so it belongs on the census sampler's cadence, never per frame.</summary>
    public string ToDetail() =>
        $"inuse={Mib(InUseBytes)}/{InUseCount} free={Mib(FreeBytes)}/{FreeCount}" +
        $" pin={Mib(PinBytes)}/{PinCount} retire={Mib(RetiredBytes)}/{RetiredCount}";

    private static string Mib(long bytes)
        => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Coordinate mapping for a BOUNDED (non-canvas-sized) layer target — the one piece of the region-local path that must
/// be identical in the D3D12 leaf and in the headless gate, because getting it wrong does not fail loudly: it draws
/// the right pixels in the wrong place, or stretches them.
///
/// <para>A bounded target is a bucketed surface (<see cref="LayerTargetBucket"/>) whose texel (0,0) holds the canvas
/// pixel at <c>Origin</c>. The subtree records with UNCHANGED absolute coordinates and is entered under a viewport
/// shifted by <c>-Origin</c> (still <c>canvasW x canvasH</c> wide, so recorded clip-space positions need no rewrite —
/// <c>D3D12Device.SetLocalBlurViewport</c>), which lands absolute pixel <c>p</c> at <c>p - Origin</c> in the surface.
/// The composite then draws the output box at its screen position with the viewport set to that box, so the
/// fullscreen triangle's uv [0,1] must map onto exactly the matching sub-rect of the surface — that is
/// <see cref="For"/>. Because the surface is best-fit and therefore usually LARGER than the used extent, the mapping
/// is against the SURFACE dimensions, never the used ones.</para>
/// </summary>
public readonly record struct LayerTargetUv(float U0, float V0, float DU, float DV);

/// <summary>Companion mapper for <see cref="LayerTargetUv"/> (see its remarks for the contract).</summary>
public static class LayerTargetMap
{
    /// <summary>The uv sub-rect of a bounded target's surface that corresponds to screen box
    /// <paramref name="output"/>, for a surface of <paramref name="texW"/>x<paramref name="texH"/> whose texel (0,0)
    /// is canvas pixel (<paramref name="originX"/>, <paramref name="originY"/>). Degenerate inputs yield the identity
    /// (full-surface) rect so a caller can never divide by zero.</summary>
    public static LayerTargetUv For(int originX, int originY, int texW, int texH, in SelfBlurPixelBox output)
    {
        if (texW <= 0 || texH <= 0 || output.IsEmpty) return new LayerTargetUv(0f, 0f, 1f, 1f);
        return new LayerTargetUv(
            (output.MinX - originX) / (float)texW,
            (output.MinY - originY) / (float)texH,
            output.Width / (float)texW,
            output.Height / (float)texH);
    }

    /// <summary>The SOURCE uv offset+scale a pass needs when it sweeps a target viewport of
    /// <paramref name="dstW"/>x<paramref name="dstH"/> over the region starting at
    /// (<paramref name="srcOriginX"/>, <paramref name="srcOriginY"/>) of a source surface of
    /// <paramref name="srcTexW"/>x<paramref name="srcTexH"/>. This is the generalization that lets a blur pass read a
    /// CANVAS-sized group RT while writing a small bounded scratch (and vice versa) instead of requiring both
    /// surfaces to be the same size.</summary>
    public static LayerTargetUv Sweep(int srcOriginX, int srcOriginY, int srcTexW, int srcTexH, int dstW, int dstH)
    {
        if (srcTexW <= 0 || srcTexH <= 0) return new LayerTargetUv(0f, 0f, 1f, 1f);
        return new LayerTargetUv(
            srcOriginX / (float)srcTexW,
            srcOriginY / (float)srcTexH,
            dstW / (float)srcTexW,
            dstH / (float)srcTexH);
    }
}

/// <summary>
/// Admission probe for a BOUNDED layer target: may this layer's subtree be rendered into a smaller-than-canvas
/// surface entered under a SHIFTED viewport?
///
/// <para>Two things break in that space, and neither fails loudly:</para>
/// <list type="bullet">
/// <item>a NESTED <c>PushLayer</c> binds its own canvas-sized RT (or, for an acrylic, snapshots the enclosing target
/// and composites back into it in canvas coordinates) — none of which is in the shifted space;</item>
/// <item>a TIER-3 STENCIL clip (<c>PushStencilClip</c>) masks against the swapchain-sized stencil DSV, which describes
/// canvas pixels; under a shifted viewport there is no valid mapping, so the scope silently degrades to its plain
/// scissor and a path-clipped element stops being masked.</item>
/// </list>
///
/// <para>The region-local SELF-BLUR path accepts the stencil degradation (it is documented and counted on
/// <c>stencilFallback</c>) because a self blur is rare and already carries other restrictions. A plain OPACITY group
/// is not rare — every fade-in is one — so the bounded-opacity path refuses a stencil-bearing subtree outright rather
/// than broadening a known-lossy case across the common path. That is the ONE difference between this probe and
/// <see cref="BlurPinKey.TryCompute"/>'s walk, which accepts stencil ops because it only needs a content key.</para>
///
/// <para><c>DrawVideo</c> is refused too, on its own grounds: its DestOut erase is layer-local by construction
/// (gpu-renderer.md §7.3), so a video hole inside ANY offscreen group is already a documented limitation — keeping it
/// on the canvas-sized lease at least keeps that limitation the one the doc describes.</para>
///
/// <para>Allocation-free, and it reads nothing but op codes and payload sizes: an UNRECOGNIZED op returns false, so a
/// newly added opcode inside a group can never desync the walk into admitting something it has not seen.</para>
/// </summary>
public static class LayerSubtreeProbe
{
    /// <summary>Walk the subtree starting at <paramref name="start"/> (the first op AFTER the layer's
    /// <c>PushLayerCmd</c>) to its matching <see cref="DrawOp.PopLayer"/>. True iff every op in it is a known LEAF
    /// draw or a plain scissor clip — no nested layer, no stencil scope, nothing unrecognized.
    /// <paramref name="afterPop"/> is the byte offset just past that PopLayer.</summary>
    public static bool IsBoundable(ReadOnlySpan<byte> cmds, int start, out int afterPop)
    {
        afterPop = start;
        int pos = start;
        while (pos + sizeof(int) <= cmds.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
            int bodyOff = pos + sizeof(int);
            switch (op)
            {
                case DrawOp.PopLayer: afterPop = bodyOff + Unsafe.SizeOf<PopLayerCmd>(); return true;
                case DrawOp.FillRoundRect: pos = bodyOff + Unsafe.SizeOf<FillRoundRectCmd>(); break;
                case DrawOp.DrawGlyphRun: pos = bodyOff + Unsafe.SizeOf<DrawGlyphRunCmd>(); break;
                case DrawOp.DrawGlyphRunGradient: pos = bodyOff + Unsafe.SizeOf<DrawGlyphRunGradientCmd>(); break;
                case DrawOp.DrawImage: pos = bodyOff + Unsafe.SizeOf<DrawImageCmd>(); break;
                case DrawOp.DrawRoundRectStroke: pos = bodyOff + Unsafe.SizeOf<DrawRoundRectStrokeCmd>(); break;
                case DrawOp.DrawShadow: pos = bodyOff + Unsafe.SizeOf<DrawShadowCmd>(); break;
                case DrawOp.DrawGradientRect: pos = bodyOff + Unsafe.SizeOf<DrawGradientRectCmd>(); break;
                case DrawOp.DrawGradientStroke: pos = bodyOff + Unsafe.SizeOf<DrawGradientStrokeCmd>(); break;
                case DrawOp.DrawArc: pos = bodyOff + Unsafe.SizeOf<DrawArcCmd>(); break;
                case DrawOp.DrawPolylineStroke: pos = bodyOff + Unsafe.SizeOf<DrawPolylineStrokeCmd>(); break;
                case DrawOp.DrawTabShape: pos = bodyOff + Unsafe.SizeOf<DrawTabShapeCmd>(); break;
                // A ThemedIcon mask is a glyph-shaped leaf draw through the R8 atlas — clip-space geometry like every
                // other leaf, so it maps under a shifted viewport. Admitted even though BlurPinKey's walk does not
                // list it (that walk needs a content KEY and simply has no fold for it); icons are common enough
                // inside a fade group that refusing them would cost most of the bounded path's reach.
                case DrawOp.DrawIconMask: pos = bodyOff + Unsafe.SizeOf<DrawIconMaskCmd>(); break;
                case DrawOp.EraseRoundRect: pos = bodyOff + Unsafe.SizeOf<EraseRoundRectCmd>(); break;
                case DrawOp.FillPath: pos = bodyOff + Unsafe.SizeOf<FillPathCmd>(); break;
                case DrawOp.StrokePath: pos = bodyOff + Unsafe.SizeOf<StrokePathCmd>(); break;
                case DrawOp.PushClip: pos = bodyOff + Unsafe.SizeOf<ClipCmd>(); break;
                case DrawOp.PopClip: pos = bodyOff; break;   // no payload
                default: return false;                        // nested PushLayer, a stencil scope, or an unknown op
            }
        }
        return false;
    }
}

/// <summary>
/// The physical-px extent a BOUNDED plain-opacity group target needs: the recorder's accumulated subtree DRAW bounds
/// (<c>PushLayerCmd.CompositeClip</c>, patched by <c>DrawList.PatchOpacityLayerExtent</c>) scaled to device pixels
/// with the SAME floor/ceil clamp the clear (<see cref="EdgeFadeLayerClear"/>) and the bounded composite
/// (<c>OpacityLayerCompositor.CompositeOpacity</c>) already use — so the leased surface, the cleared region and the
/// composited region are one box by construction and no uncleared pooled texel can ever be sampled.
///
/// <para>Nothing is lost by bounding the TARGET to this box: the composite has been scissored to it since the
/// patched-extent change, so subtree pixels outside it were already discarded. A group the recorder left UNPATCHED
/// (empty <c>CompositeClip</c>) means "extent unknown" and stays on the full-canvas lease.</para>
/// </summary>
public static class BoundedGroupRegion
{
    /// <summary>Minimum area ratio (canvas / box) before bounding is worth a bucketed lease. Below this the bucket
    /// rounds back up to roughly the canvas anyway and the shifted-viewport path only adds restrictions, so the
    /// full-canvas lease is the better trade.</summary>
    public const int MinAreaSavingRatio = 2;

    /// <summary>Compute the bounded extent, or an empty box when the group must stay full-canvas (unpatched extent,
    /// degenerate rect, or too little saving to be worth the bucketed lease).</summary>
    public static SelfBlurPixelBox Compute(in FluentGpu.Foundation.RectF compositeClip, float scale, int canvasW, int canvasH)
    {
        if (canvasW <= 0 || canvasH <= 0 || !(scale > 0f)) return default;
        if (compositeClip.W <= 0f || compositeClip.H <= 0f) return default;   // unpatched ⇒ extent unknown
        // An INFINITE rect is the recorder's "unbounded" sentinel, not a real extent — and float.Infinity through the
        // floor/ceil below would cast to an undefined int, so it is refused before the arithmetic, not clamped after.
        if (compositeClip.IsInfinite) return default;
        int left = Math.Clamp((int)MathF.Floor(compositeClip.X * scale), 0, canvasW);
        int top = Math.Clamp((int)MathF.Floor(compositeClip.Y * scale), 0, canvasH);
        int right = Math.Clamp((int)MathF.Ceiling((compositeClip.X + compositeClip.W) * scale), left, canvasW);
        int bottom = Math.Clamp((int)MathF.Ceiling((compositeClip.Y + compositeClip.H) * scale), top, canvasH);
        if (right <= left || bottom <= top) return default;

        // Worth it only if the BUCKETED surface is materially smaller than the canvas.
        long bucketArea = (long)LayerTargetBucket.Dim(right - left) * LayerTargetBucket.Dim(bottom - top);
        long canvasArea = (long)canvasW * canvasH;
        if (bucketArea * MinAreaSavingRatio > canvasArea) return default;
        return new SelfBlurPixelBox(left, top, right, bottom);
    }
}
