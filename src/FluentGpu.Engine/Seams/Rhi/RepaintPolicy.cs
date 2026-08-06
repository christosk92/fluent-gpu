using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;

namespace FluentGpu.Rhi;

/// <summary>How a backend that owns a persistent canvas RT must paint this frame (gpu-renderer.md §13.1). Chosen by
/// <see cref="RepaintPolicy.Decide"/> from the frame's <see cref="RepaintDamageRegion"/> plus the four backend facts
/// only the device knows (stream safety, canvas validity, target-size agreement, layer kind).</summary>
public enum RepaintRoute : byte
{
    /// <summary>Clear + replay the WHOLE stream straight to the back buffer — today's path, byte for byte. The permanent
    /// safe harbor: it is also the CHEAPEST full frame (no canvas, no blit), so a high-coverage frame (scroll) takes it
    /// and pays exactly what it pays today. Leaves the canvas INVALID.</summary>
    FullDirect = 0,
    /// <summary>Clear + replay the WHOLE stream into the persistent canvas, then blit the canvas to the back buffer.
    /// Strictly more expensive than <see cref="FullDirect"/> (the blit), and taken ONLY to REBUILD a canvas that a
    /// following small-damage frame can then replay into partially.</summary>
    FullIntoCanvas = 1,
    /// <summary>Clear only the replay rects of the canvas, replay the stream culled+scissored to each, then blit the
    /// whole canvas. Zero replay rects = "nothing changed": blit the retained canvas and present.</summary>
    Partial = 2,
}

/// <summary>Backing storage for <see cref="ReplayRects"/> — one [InlineArray] so the whole set is a POD value.</summary>
[InlineArray(RepaintPolicy.MaxReplayRects)]
internal struct ReplayRectBuffer
{
    private RectF _e0;
}

/// <summary>The ≤ <see cref="RepaintPolicy.MaxReplayRects"/> rects a <see cref="RepaintRoute.Partial"/> frame clears and
/// replays, in world-space float DIPs (the DIP→device rounding-OUT happens at the RHI leaf). Pairwise DISJOINT, so
/// <see cref="SummedArea"/> is an exact area and no pixel is painted twice.</summary>
public struct ReplayRects
{
    private ReplayRectBuffer _rects;
    private byte _count;

    /// <summary>Live rect count (0 on a full route, and 0 on a Partial route means "blit the retained canvas").</summary>
    public readonly int Count => _count;

    public readonly RectF this[int index]
    {
        get
        {
            if ((uint)index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            return _rects[index];
        }
    }

    /// <summary>The live rects, in no particular order. The span aliases this instance's storage.</summary>
    [UnscopedRef]
    public ReadOnlySpan<RectF> AsSpan()
        => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<ReplayRectBuffer, RectF>(ref _rects), _count);

    /// <summary>Exact total area (members are pairwise disjoint).</summary>
    public readonly float SummedArea()
    {
        float a = 0f;
        for (int i = 0; i < _count; i++) a += _rects[i].W * _rects[i].H;
        return a;
    }

    internal void Append(in RectF r)
    {
        if (_count >= RepaintPolicy.MaxReplayRects) return;
        _rects[_count++] = r;
    }
}

/// <summary>
/// The PURE decision layer of damage-scissored repaint (gpu-renderer.md §13.1) — no backend types, no TerraFX, fully
/// gate-testable headlessly. The device supplies four facts it alone knows (which layer kinds the stream contains,
/// whether the stream can survive a clamped replay, whether its canvas still holds a coherent scene, whether the
/// published frame's target size still matches the swapchain) and this decides the route + the replay rects.
/// <para>
/// Every uncertain path resolves to a FULL redraw. That is the whole safety model: a missed damage source can only ever
/// cost performance, never correctness, as long as "I am not sure" means "repaint everything".
/// </para>
/// </summary>
public static class RepaintPolicy
{
    /// <summary>Canon §13.1's "&gt;~60% window coverage ⇒ full redraw". Past this the per-rect clear + N culled replays
    /// cost more than one clean full pass, and the full pass keeps the TBDR fast-clear. Checked TWICE: once on the raw
    /// accumulated rects and again on the MERGED replay rects, because coalescing 16 rects down to 4 can add enough dead
    /// area to cross the line.</summary>
    public const float CoverageCutoff = 0.60f;

    /// <summary>How many rects a partial frame may clear + replay. Each rect costs one full walk of the DrawList
    /// (decode-time culled), so this trades CPU decode against wasted fill. 4 is canon's ≤16-accumulated set coalesced
    /// to the point where a simultaneous playhead + equalizer + caret + hover fade still stays separate.</summary>
    public const int MaxReplayRects = 4;

    /// <summary>Stream contains no layer ops at all — the streaming route, which may replay up to
    /// <see cref="MaxReplayRects"/> times.</summary>
    public const int LayerKindNone = 0;
    /// <summary>Stream contains opacity/blur/edge-fade groups — the layered route. Group RTs are POOL-LEASED (acquire →
    /// composite → release), so the stream cannot be replayed twice: a partial layered frame collapses to ONE union rect.</summary>
    public const int LayerKindGroups = 1;
    /// <summary>Stream contains an ACRYLIC layer. Always full-direct, and always invalidates the canvas: the acrylic
    /// backdrop snapshot physically copies back-buffer regions INTO the canvas as its scratch, so retained canvas
    /// content cannot survive the frame. Not policy — physics (AcrylicCompositor.SnapshotTargetRegion).</summary>
    public const int LayerKindAcrylic = 2;

    /// <summary>
    /// Pick the route for this frame and, on <see cref="RepaintRoute.Partial"/>, the rects to clear + replay.
    /// </summary>
    /// <param name="damage">This frame's repaint set (world-space DIPs), as published across the seam.</param>
    /// <param name="wDip">Target width in DIPs (physical / scale) — the coverage denominator.</param>
    /// <param name="hDip">Target height in DIPs.</param>
    /// <param name="layerKind">One of <see cref="LayerKindNone"/> / <see cref="LayerKindGroups"/> /
    /// <see cref="LayerKindAcrylic"/>. Anything else is treated as UNKNOWN ⇒ full-direct.</param>
    /// <param name="streamSafe"><see cref="RepaintStreamSafety.Scan"/> — the stream survives a clamped replay.</param>
    /// <param name="canvasValid">The persistent canvas currently holds a complete, coherent scene.</param>
    /// <param name="sizeMatches">The published frame's target size agrees with the live swapchain.</param>
    /// <param name="rects">The replay rects (empty unless the result is <see cref="RepaintRoute.Partial"/> with work).</param>
    public static RepaintRoute Decide(in RepaintDamageRegion damage, float wDip, float hDip, int layerKind,
        bool streamSafe, bool canvasValid, bool sizeMatches, out ReplayRects rects)
    {
        rects = default;

        // ── (1) Hard disqualifiers. Each one means "a canvas route cannot be made correct this frame", so it lands on
        // the cheapest full frame there is. An unknown layerKind is included deliberately: a NEW layer kind must default
        // to the safe harbor rather than silently inherit the groups route.
        if (layerKind != LayerKindNone && layerKind != LayerKindGroups) return RepaintRoute.FullDirect;
        if (!streamSafe || !sizeMatches) return RepaintRoute.FullDirect;
        if (wDip <= 0f || hDip <= 0f) return RepaintRoute.FullDirect;

        // ── (2) Nothing changed. With a live canvas that is a pure blit (an upload-forced or otherwise content-free
        // frame); without one the back buffer is FLIP_DISCARD-undefined and something must be drawn.
        if (damage.IsEmpty) return canvasValid ? RepaintRoute.Partial : RepaintRoute.FullDirect;

        // ── (3) A named full-repaint cause, or too much of the window changed: full-direct. This is where scroll lands
        // (~81 % coverage), and landing there is the POINT — it keeps today's cost with no blit tax.
        if (damage.IsFull) return RepaintRoute.FullDirect;
        if (damage.Coverage(wDip, hDip) >= CoverageCutoff) return RepaintRoute.FullDirect;

        // ── (4) Small damage. Coalesce to the route's rect budget (the layered route gets ONE union rect: its group RTs
        // are pool-leased, so the stream cannot be replayed twice), then RE-CHECK coverage — the merge adds dead area
        // and a post-merge union can cross the cutoff that the raw rects passed.
        int cap = layerKind == LayerKindGroups ? 1 : MaxReplayRects;
        Coalesce(in damage, wDip, hDip, cap, ref rects);
        if (rects.Count == 0) return canvasValid ? RepaintRoute.Partial : RepaintRoute.FullDirect;   // every rect fell outside the target
        if (rects.SummedArea() / (wDip * hDip) >= CoverageCutoff) { rects = default; return RepaintRoute.FullDirect; }

        // ── (5) The damage is small enough to be worth a partial — but the canvas must hold a coherent scene first.
        // Rebuild it with ONE full replay INTO the canvas so the NEXT small-damage frame can go partial. (A frame that
        // would have been full anyway never pays this: steps 1–3 already returned FullDirect.)
        if (!canvasValid) { rects = default; return RepaintRoute.FullIntoCanvas; }
        return RepaintRoute.Partial;
    }

    // Clamp the accumulated rects to the target, fold every overlap/abutment, then merge the least-wasteful pair until
    // at most `cap` remain. Merging can create fresh overlaps, so every merge is followed by a re-normalize; the result
    // is therefore still PAIRWISE DISJOINT, which is what makes the post-merge coverage test exact and guarantees no
    // pixel is cleared+replayed twice. stackalloc only — this runs on the submit path.
    private static void Coalesce(in RepaintDamageRegion damage, float wDip, float hDip, int cap, ref ReplayRects rects)
    {
        Span<RectF> buf = stackalloc RectF[RepaintDamageRegion.MaxRects];
        int n = 0;
        var target = new RectF(0f, 0f, wDip, hDip);
        // Indexer + Count (both `readonly`) rather than AsSpan() — AsSpan is [UnscopedRef] and non-readonly, so reading
        // it off an `in` parameter would force a defensive copy of the whole 16-rect region.
        int count = damage.Count;
        for (int i = 0; i < count; i++)
        {
            RectF c = damage[i].Intersect(in target);
            if (c.W <= 0f || c.H <= 0f) continue;
            buf[n++] = c;
        }
        Normalize(buf, ref n);
        while (n > cap)
        {
            int bestA = 0, bestB = 1;
            float bestWaste = float.MaxValue;
            for (int i = 1; i < n; i++)
                for (int j = 0; j < i; j++)
                {
                    RectF u = Union(in buf[i], in buf[j]);
                    float waste = u.W * u.H - buf[i].W * buf[i].H - buf[j].W * buf[j].H;
                    if (waste < bestWaste) { bestWaste = waste; bestA = j; bestB = i; }
                }
            buf[bestA] = Union(in buf[bestA], in buf[bestB]);
            buf[bestB] = buf[n - 1];
            n--;
            Normalize(buf, ref n);
        }
        for (int i = 0; i < n; i++) rects.Append(in buf[i]);
    }

    // Overlap-OR-abut (closed intervals), same adjacency rule RepaintDamageRegion.Add uses — two rects that merely share
    // an edge become one, so "disjoint" means "separated by real space".
    private static bool Adjacent(in RectF a, in RectF b)
        => a.X <= b.Right && b.X <= a.Right && a.Y <= b.Bottom && b.Y <= a.Bottom;

    private static RectF Union(in RectF a, in RectF b)
    {
        float x0 = MathF.Min(a.X, b.X), y0 = MathF.Min(a.Y, b.Y);
        float x1 = MathF.Max(a.Right, b.Right), y1 = MathF.Max(a.Bottom, b.Bottom);
        return new RectF(x0, y0, x1 - x0, y1 - y0);
    }

    private static void Normalize(Span<RectF> buf, ref int n)
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < n && !merged; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (!Adjacent(in buf[i], in buf[j])) continue;
                    buf[i] = Union(in buf[i], in buf[j]);
                    buf[j] = buf[n - 1];
                    n--;
                    merged = true;
                    break;
                }
        }
    }
}

/// <summary>
/// "Can this DrawList survive being replayed with the root scissor clamped to a damage rect?" — a PURE scan over the
/// opcode stream (the engine owns the encoding, so this is gate-testable headlessly and shared by every backend).
/// <para>
/// UNSAFE in v1, each for a concrete reason:
/// <list type="bullet">
/// <item><b>Acrylic</b> — the backdrop snapshot copies target regions INTO the canvas as scratch, clobbering the
/// retained scene (AcrylicCompositor.SnapshotTargetRegion).</item>
/// <item><b>Blur</b> (self-blur groups) — a Gaussian's taps read OUTSIDE the clamp, so a clamped replay samples pixels
/// the clamp never redrew; the region-local and pin-cache paths also lease their own shifted-viewport surfaces.</item>
/// <item><b>EdgeFade</b> — both classes. The blurred one for the same reason as Blur; the PLAIN (σ = 0) strip-fade
/// because its snapshot/restore reads and writes the top-level target OUTSIDE the clamped scissor and its precondition
/// on that target is unverified under a partial frame (marked follow-up — see StripTargetResource).</item>
/// <item><b>Any unrecognized op / any truncated payload</b> — an op this scanner cannot size could be anything.</item>
/// </list>
/// A plain <see cref="LayerKind.Opacity"/> group IS safe: it leases a canvas-sized RT, clears it (fully, or over the
/// recorder-patched extent), draws the subtree under the clamped scissor, and composites back under
/// <c>CurrentScissorRect()</c> — every read is inside a box that was cleared this frame and inside the clamp.
/// </para>
/// </summary>
public static class RepaintStreamSafety
{
    /// <summary>True when <paramref name="cmds"/> may be replayed under a damage-clamped root scissor. Frames the walk
    /// cannot fully account for return false, which the policy turns into a full redraw.</summary>
    public static bool Scan(ReadOnlySpan<byte> cmds)
    {
        int pos = 0;
        // Mirrors the decoders' framing exactly (a trailing < 4-byte remainder is not an op), so this can never disagree
        // with what SubmitStreaming/SubmitWithLayers will actually walk.
        while (pos + sizeof(int) <= cmds.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
            pos += sizeof(int);
            int body;
            switch (op)
            {
                case DrawOp.FillRoundRect: body = Unsafe.SizeOf<FillRoundRectCmd>(); break;
                case DrawOp.DrawGlyphRun: body = Unsafe.SizeOf<DrawGlyphRunCmd>(); break;
                case DrawOp.DrawGlyphRunGradient: body = Unsafe.SizeOf<DrawGlyphRunGradientCmd>(); break;
                case DrawOp.PushClip: body = Unsafe.SizeOf<ClipCmd>(); break;
                case DrawOp.PopClip: body = 0; break;
                case DrawOp.DrawImage: body = Unsafe.SizeOf<DrawImageCmd>(); break;
                case DrawOp.DrawRoundRectStroke: body = Unsafe.SizeOf<DrawRoundRectStrokeCmd>(); break;
                case DrawOp.DrawShadow: body = Unsafe.SizeOf<DrawShadowCmd>(); break;
                case DrawOp.DrawArc: body = Unsafe.SizeOf<DrawArcCmd>(); break;
                case DrawOp.DrawPolylineStroke: body = Unsafe.SizeOf<DrawPolylineStrokeCmd>(); break;
                case DrawOp.DrawGradientRect: body = Unsafe.SizeOf<DrawGradientRectCmd>(); break;
                case DrawOp.DrawGradientStroke: body = Unsafe.SizeOf<DrawGradientStrokeCmd>(); break;
                case DrawOp.DrawTabShape: body = Unsafe.SizeOf<DrawTabShapeCmd>(); break;
                case DrawOp.DrawIconMask: body = Unsafe.SizeOf<DrawIconMaskCmd>(); break;
                case DrawOp.DrawVideo: body = Unsafe.SizeOf<DrawVideoCmd>(); break;
                case DrawOp.EraseRoundRect: body = Unsafe.SizeOf<EraseRoundRectCmd>(); break;
                case DrawOp.PopLayer: body = Unsafe.SizeOf<PopLayerCmd>(); break;
                case DrawOp.PushLayer:
                    body = Unsafe.SizeOf<PushLayerCmd>();
                    if (pos + body > cmds.Length) return false;
                    if (MemoryMarshal.Read<PushLayerCmd>(cmds.Slice(pos)).Kind != (int)LayerKind.Opacity) return false;
                    break;
                default: return false;   // an op this scanner cannot size — never guess
            }
            if (pos + body > cmds.Length) return false;   // truncated payload: a malformed run
            pos += body;
        }
        return true;
    }
}

/// <summary>
/// Decode-time primitive culling for a damage-scissored replay (R2 — mandatory, not an optimization). The pipes' instance
/// banks are PER FRAME, not per replay: N naive full-list replays multiply consumption ×N and silently DROP primitives
/// inside the damage when a bank overflows (Gradient's is only 512). Skipping a primitive whose conservative device AABB
/// misses the active replay rect keeps consumption at ≈ one frame's worth plus boundary straddlers.
/// <para>
/// The halos below are the per-kind slack between an op's declared rect and the pixels its VERTEX SHADER actually
/// rasterizes, so a primitive whose geometry lies outside the rect but whose FOOTPRINT reaches into it is kept. They are
/// deliberately ≥ the shader's own quad inflation. A primitive exactly ON the rect edge is KEPT (the tests are inclusive).
/// </para>
/// </summary>
public static class RepaintCull
{
    /// <summary>The SDF pipelines inflate their quad by 2 local units for the AA feather (RoundRectPipeline /
    /// GradientPipeline VS: <c>margin = stroke/2 + 2</c>). Also the floor for every other kind.</summary>
    public const float AaHaloDip = 2f;

    /// <summary>Glyph halo floor: a run's declared <c>Bounds</c> is the node box, and ascenders/descenders/italic
    /// overhang/wipe lift can reach outside it. Canon R2: <c>max(4px, em/2)</c>.</summary>
    public const float GlyphHaloMinDip = 4f;

    /// <summary>An outline band straddles the edge by <c>width/2</c>, plus the AA feather.</summary>
    public static float StrokeHalo(float strokeWidth)
        => (strokeWidth > 0f ? strokeWidth * 0.5f : 0f) + AaHaloDip;

    /// <summary>A drop shadow's quad half-extent past its (already offset) box: ShadowPipeline's VS uses
    /// <c>spread + 3·max(blur/2, 0.5)</c>; this returns <c>spread + 3·max(blur, 0.5) + AA</c>, a deliberate superset so a
    /// future shader tweak cannot silently under-cull. The OFFSET is applied by the caller (it shifts the box, it does
    /// not grow it symmetrically).</summary>
    public static float ShadowHalo(float spread, float blur)
        => MathF.Max(spread, 0f) + 3f * MathF.Max(blur, 0.5f) + AaHaloDip;

    /// <summary>Glyph-run halo: <c>max(GlyphHaloMinDip, fontSize/2)</c>, plus any per-glyph vertical lift.</summary>
    public static float GlyphHalo(float fontSize, float lift = 0f)
        => MathF.Max(GlyphHaloMinDip, MathF.Abs(fontSize) * 0.5f) + MathF.Abs(lift);

    /// <summary>Device-space AABB of a local rect under a 2×3 affine — all four corners, so rotation/skew are handled
    /// (canon §13.1 says the damage AABBs come from all four transformed corners; the cull test must agree).</summary>
    public static void Aabb(float x, float y, float w, float h,
        float m11, float m12, float m21, float m22, float dx, float dy,
        out float left, out float top, out float right, out float bottom)
    {
        float x0 = x, y0 = y, x1 = x + w, y1 = y + h;
        float ax = x0 * m11 + y0 * m21 + dx, ay = x0 * m12 + y0 * m22 + dy;
        float bx = x1 * m11 + y0 * m21 + dx, by = x1 * m12 + y0 * m22 + dy;
        float cx = x0 * m11 + y1 * m21 + dx, cy = x0 * m12 + y1 * m22 + dy;
        float ex = x1 * m11 + y1 * m21 + dx, ey = x1 * m12 + y1 * m22 + dy;
        left = MathF.Min(MathF.Min(ax, bx), MathF.Min(cx, ex));
        right = MathF.Max(MathF.Max(ax, bx), MathF.Max(cx, ex));
        top = MathF.Min(MathF.Min(ay, by), MathF.Min(cy, ey));
        bottom = MathF.Max(MathF.Max(ay, by), MathF.Max(cy, ey));
    }

    /// <summary>Keep the primitive whose device AABB (inflated by <paramref name="halo"/>) touches
    /// <paramref name="rect"/>. INCLUSIVE on every side: a primitive exactly on the rect edge is KEPT.</summary>
    public static bool Keep(float left, float top, float right, float bottom, float halo, in RectF rect)
        => left - halo <= rect.Right && right + halo >= rect.X
        && top - halo <= rect.Bottom && bottom + halo >= rect.Y;
}
