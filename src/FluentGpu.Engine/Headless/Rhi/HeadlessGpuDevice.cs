using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Hosting.Threading;

namespace FluentGpu.Rhi.Headless;

/// <summary>
/// CPU/null RHI backend (the structural test path from validation.md). Decodes the POD DrawList into reusable
/// command lists for assertions — no GPU, and no per-frame managed allocation once warmed (lists keep capacity).
/// </summary>
public sealed partial class HeadlessGpuDevice : IGpuDevice
{
    private readonly List<FillRoundRectCmd> _rects = new(64);
    private readonly List<int> _rectClipDepth = new(64);
    private readonly List<DrawGlyphRunCmd> _glyphs = new(64);
    private readonly List<DrawGlyphRunGradientCmd> _glyphGradients = new(16);
    private readonly List<ClipCmd> _clips = new(16);
    private readonly List<DrawImageCmd> _imageDraws = new(32);
    private readonly List<DrawRoundRectStrokeCmd> _strokes = new(16);
    private readonly List<DrawShadowCmd> _shadows = new(16);
    private readonly List<DrawArcCmd> _arcs = new(16);
    private readonly List<DrawPolylineStrokeCmd> _polylines = new(16);
    private readonly List<DrawGradientRectCmd> _gradients = new(16);
    private readonly List<DrawGradientStrokeCmd> _gradientStrokes = new(16);
    private readonly List<PushLayerCmd> _layers = new(8);
    private readonly List<DrawTabShapeCmd> _tabShapes = new(8);
    private readonly List<DrawIconMaskCmd> _iconMasks = new(16);
    private readonly List<DrawVideoCmd> _videos = new(4);
    private readonly List<EraseRoundRectCmd> _erases = new(4);
    private readonly List<FillPathCmd> _fillPaths = new(16);
    private readonly List<StrokePathCmd> _strokePaths = new(16);
    private readonly List<DrawSeriesCmd> _series = new(16);
    private readonly List<int> _blends = new(4);
    private readonly List<DrawSpritesCmd> _spriteChunks = new(8);
    private readonly List<int> _videoClipDepth = new(4);
    private readonly List<PushStencilClipCmd> _stencilClips = new(4);
    private readonly List<PopStencilClipCmd> _stencilPops = new(4);
    private readonly List<int> _imageStencilDepth = new(32);
    private readonly List<int> _rectStencilDepth = new(64);
    private readonly List<(int id, int w, int h)> _uploads = new(32);
    private readonly Dictionary<int, (int w, int h)> _resident = new(32);
    private readonly List<int> _evictions = new(16);
    private BakedBlurQueue? _bakedBlurs;

    public string BackendName => "Headless";
    public bool SupportsSecondarySwapchains => true;
    public int FrameCount { get; private set; }
    public ColorF LastClear { get; private set; }
    /// <summary>The full submit context of the most recent <see cref="SubmitDrawList(ReadOnlySpan{byte},ReadOnlySpan{ulong},in FrameInfo)"/>
    /// — including <see cref="FrameInfo.RepaintDamage"/> and <see cref="FrameInfo.PublishSequence"/>, which no backend
    /// consumes yet. Captured so the headless gates can assert on the payload that crosses the seam.</summary>
    public FrameInfo LastFrameInfo { get; private set; }
    public IReadOnlyList<FillRoundRectCmd> LastRects => _rects;
    /// <summary>Clip-stack depth at each <see cref="LastRects"/> command (parallel list).</summary>
    public IReadOnlyList<int> LastRectClipDepths => _rectClipDepth;
    public IReadOnlyList<DrawGlyphRunCmd> LastGlyphs => _glyphs;
    /// <summary>Karaoke-wipe glyph runs drawn this frame (the soft-wipe op, A1).</summary>
    public IReadOnlyList<DrawGlyphRunGradientCmd> LastGlyphGradients => _glyphGradients;
    /// <summary>Every PushClip pushed this frame (for clip assertions; the recorder pre-intersects each one).</summary>
    public IReadOnlyList<ClipCmd> LastClips => _clips;
    /// <summary>Image quads drawn this frame (Ready==0 ⇒ placeholder shown while decode is in flight).</summary>
    public IReadOnlyList<DrawImageCmd> LastImages => _imageDraws;
    /// <summary>SDF outlines (focus rings / stroked borders) drawn this frame.</summary>
    public IReadOnlyList<DrawRoundRectStrokeCmd> LastStrokes => _strokes;
    /// <summary>The clip-stack depth at the moment each stroke was decoded (parallel to <see cref="LastStrokes"/>) —
    /// asserts a focus ring records OUTSIDE its ClipsToBounds node's own clip (depth of the parent context).</summary>
    public IReadOnlyList<int> LastStrokeClipDepths => _strokeClipDepth;
    private readonly List<int> _strokeClipDepth = new(16);
    /// <summary>Soft drop shadows drawn this frame.</summary>
    public IReadOnlyList<DrawShadowCmd> LastShadows => _shadows;
    /// <summary>Circular-arc strokes (ProgressRing) drawn this frame.</summary>
    public IReadOnlyList<DrawArcCmd> LastArcs => _arcs;
    /// <summary>Stroked polylines drawn this frame.</summary>
    public IReadOnlyList<DrawPolylineStrokeCmd> LastPolylines => _polylines;
    /// <summary>Gradient-filled rects drawn this frame.</summary>
    public IReadOnlyList<DrawGradientRectCmd> LastGradients => _gradients;
    /// <summary>Gradient-tinted border strokes (WinUI elevation borders) drawn this frame.</summary>
    public IReadOnlyList<DrawGradientStrokeCmd> LastGradientStrokes => _gradientStrokes;
    /// <summary>Layers pushed this frame — acrylic (Kind 0, blur/tint recipe fields), flat opacity groups (Kind 1,
    /// GroupAlpha), AND per-node self-blur groups (Kind 2, GroupAlpha + <see cref="PushLayerCmd.BlurSigma"/> — the
    /// Expressive Motion Kit) all ride the same opcode; assert on <see cref="PushLayerCmd.Kind"/>/<c>GroupAlpha</c>/
    /// <c>BlurSigma</c>.</summary>
    public IReadOnlyList<PushLayerCmd> LastLayers => _layers;
    /// <summary>WinUI selected-tab shapes drawn this frame (DrawTabShape — rounded-top + inverted bottom flares).</summary>
    public IReadOnlyList<DrawTabShapeCmd> LastTabShapes => _tabShapes;
    /// <summary>ThemedIcon vector-layer masks drawn this frame (DrawIconMask — PathId + per-instance tint).</summary>
    public IReadOnlyList<DrawIconMaskCmd> LastIconMasks => _iconMasks;
    /// <summary>Video hole punches recorded this frame (DrawVideo — SurfaceId + erase strength VideoReady). The real
    /// backend ERASES these rects toward premultiplied zero so the DComp video visual below the swapchain shows through;
    /// headless just captures the payload.</summary>
    public IReadOnlyList<DrawVideoCmd> LastVideos => _videos;
    /// <summary>General rounded-rect ERASES recorded this frame (DrawOp.EraseRoundRect) — the drop-spotlight scrim's
    /// cutouts. The real backend runs them through the DestOut PSO against whatever surface is bound (an opacity-group
    /// RT for the scrim); headless just captures the payload in emission order.</summary>
    public IReadOnlyList<EraseRoundRectCmd> LastErases => _erases;
    /// <summary>Tessellated path fills recorded this frame (DrawOp.FillPath — gpu-renderer.md §5). VtxStart/VtxCount/
    /// IdxStart/IdxCount index <see cref="FluentGpu.Render.PathRealizationCache.Shared"/>'s retained slab; headless just
    /// captures the payload (no GPU pipeline — that is §1.5).</summary>
    public IReadOnlyList<FillPathCmd> LastFillPaths => _fillPaths;
    /// <summary>Tessellated path strokes recorded this frame (DrawOp.StrokePath). TrimStart/TrimEnd/DashOn/DashOff/
    /// TrimMode are payload-only (never part of the realization key) — see <see cref="StrokePathCmd"/>.</summary>
    public IReadOnlyList<StrokePathCmd> LastStrokePaths => _strokePaths;
    /// <summary>Series chunks (DrawOp.DrawSeries) recorded this frame, in emission order.</summary>
    public IReadOnlyList<DrawSeriesCmd> LastSeries => _series;
    /// <summary>Paint-blend switches (DrawOp.SetBlend) recorded this frame, in order (0 = SrcOver, 1 = Additive).</summary>
    public IReadOnlyList<int> LastBlends => _blends;
    /// <summary>Sprite chunks (DrawOp.DrawSprites) recorded this frame, in emission order.</summary>
    public IReadOnlyList<DrawSpritesCmd> LastSprites => _spriteChunks;
    /// <summary>Sum of <see cref="FillPathCmd.VtxCount"/>/<see cref="StrokePathCmd.VtxCount"/> across this frame's path
    /// draws — a cheap "did anything actually tessellate/draw" probe for gates, without re-decoding the stream.</summary>
    public int LastPathVertexCount
    {
        get
        {
            int n = 0;
            foreach (var f in _fillPaths) n += f.VtxCount;
            foreach (var s in _strokePaths) n += s.VtxCount;
            return n;
        }
    }
    /// <summary>Sum of <see cref="FillPathCmd.IdxCount"/>/<see cref="StrokePathCmd.IdxCount"/> across this frame's path
    /// draws.</summary>
    public int LastPathIndexCount
    {
        get
        {
            int n = 0;
            foreach (var f in _fillPaths) n += f.IdxCount;
            foreach (var s in _strokePaths) n += s.IdxCount;
            return n;
        }
    }
    /// <summary>Clip-stack depth at each <see cref="LastVideos"/> command (parallel list) — a PiP hole records INSIDE
    /// its rounded container's clip, which is where its corner rounding actually comes from.</summary>
    public IReadOnlyList<int> LastVideoClipDepths => _videoClipDepth;
    /// <summary>Tier-3 STENCIL path clips pushed this frame (DrawOp.PushStencilClip — gpu-renderer.md §6). VtxStart/
    /// VtxCount/IdxStart/IdxCount index <see cref="FluentGpu.Render.PathRealizationCache.Shared"/>'s retained slab, the
    /// same refs the mask pre-pass would draw; <see cref="PushStencilClipCmd.DeviceRect"/> doubles as the scope's
    /// tier-1 scissor. Headless models the STRUCTURE (nesting, balance, per-command depth) — geometry semantics are
    /// gated by <c>PathHitTest</c> against the recorded transform, not by a second rasterizer.</summary>
    public IReadOnlyList<PushStencilClipCmd> LastStencilClips => _stencilClips;
    /// <summary>The matching pops (DrawOp.PopStencilClip), in stream order — each re-carries its push's geometry.</summary>
    public IReadOnlyList<PopStencilClipCmd> LastStencilPops => _stencilPops;
    /// <summary>Stencil-scope nesting depth at each <see cref="LastImages"/> command (parallel list): 0 = outside every
    /// stencil scope, N = inside N nested ones.</summary>
    public IReadOnlyList<int> LastImageStencilDepths => _imageStencilDepth;
    /// <summary>Stencil-scope nesting depth at each <see cref="LastRects"/> command (parallel list).</summary>
    public IReadOnlyList<int> LastRectStencilDepths => _rectStencilDepth;
    /// <summary>Push/pop balance check — must be 0 at end of a well-formed frame. Rounded (tier-2) clips are visible
    /// on <see cref="LastClips"/> entries via <see cref="ClipCmd.CornerRadius"/>/<c>RoundedRect</c>.</summary>
    public int ClipBalance { get; private set; }
    /// <summary>PushLayer/PopLayer balance check — must be 0 at end of a well-formed frame.</summary>
    public int LayerBalance { get; private set; }
    /// <summary>PushStencilClip/PopStencilClip balance check — must be 0 at end of a well-formed frame. A stencil clip
    /// IS also a clip level, so it moves <see cref="ClipBalance"/> too (the scope's DeviceRect is the scissor).</summary>
    public int StencilClipBalance { get; private set; }

    /// <summary>Every <see cref="UploadImage"/> this run (one entry per decode completion) — for upload assertions.</summary>
    public IReadOnlyList<(int id, int w, int h)> Uploads => _uploads;
    /// <summary>Currently-resident image ids → their uploaded dims (last upload wins; for residency assertions).</summary>
    public IReadOnlyDictionary<int, (int w, int h)> ResidentImages => _resident;

    /// <summary>Create every WINDOWED-POPUP swapchain (the desktop-acrylic targets a flyout/menu leases) with
    /// <see cref="HeadlessSwapchain.PresentStandDown"/> set — the recorder's stand-in for a backend that cannot present
    /// into that target yet (the real one stands down while the popup HWND is still hidden). The popup's own swapchain
    /// is created inside the host frame that leases it, so the switch has to live here to be in place for its first
    /// present. The main window's target is unaffected.</summary>
    public bool StandDownPopupPresents { get; set; }

    // Render-ownership tripwire (INCIDENT 2026-09, detached-window-render-isolation-implementation.md §2/§7.2): inert
    // (false) for every existing headless host — only a test that opts in with MarkRenderConfined() arms it, mirroring
    // D3D12Device's AssertDeviceOwner/_renderConfined gate so the headless RenderOwnershipTests exercise the same
    // contract without a real GPU.
    private bool _renderConfined;
    public void MarkRenderConfined() => _renderConfined = true;

    /// <summary>Test seam: the composited-video presenter this device answers for EVERY swapchain (the headless seam has no
    /// DirectComposition). <see langword="null"/> (the default) keeps every host's video drain a no-op, as before.</summary>
    public FluentGpu.Pal.IVideoPresenter? VideoPresenterForTest { get; set; }
    public FluentGpu.Pal.IVideoPresenter? VideoPresenter => VideoPresenterForTest;

    /// <summary>Device-level composition commits (<see cref="IGpuDevice.CommitVideoComposition"/>) this run, and a hook run on
    /// each — a test's door to WHEN the host commits relative to its presents.</summary>
    public int VideoCompositionCommits { get; private set; }
    public System.Action? OnCommitVideoComposition { get; set; }
    public void CommitVideoComposition() { VideoCompositionCommits++; OnCommitVideoComposition?.Invoke(); }

    // The FIRST swapchain created is the "primary" for the handful of device-level diagnostic getters that predate
    // per-target state (detached-window-render-isolation-implementation.md §3.3) and have not been migrated to a
    // per-call target parameter — today only HintSettlePresentCount below.
    private HeadlessSwapchain? _primarySwapchain;

    /// <summary>The first swapchain created (the window's) — the gates' door to its occlusion / stand-down seams.</summary>
    public HeadlessSwapchain? PrimarySwapchain => _primarySwapchain;

    private readonly List<HeadlessSwapchain> _createdSwapchains = new(2);

    /// <summary>Every swapchain this device created, in creation order (index 0 is the primary): a gate's door to the target
    /// a given host presented into.</summary>
    public IReadOnlyList<HeadlessSwapchain> CreatedSwapchains => _createdSwapchains;

    public ISwapchain CreateSwapchain(in SwapchainDesc desc)
    {
        if (_renderConfined) ThreadGuard.AssertRenderOwner();
        var sc = new HeadlessSwapchain(desc.SizePx, _renderConfined) { PresentStandDown = desc.DesktopAcrylic && StandDownPopupPresents };
        _primarySwapchain ??= sc;
        _createdSwapchains.Add(sc);
        return sc;
    }

    public void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h)
    {
        _uploads.Add((imageId, w, h));   // never cleared: uploads are one-shot per decode, so the log is the history
        _resident[imageId] = (w, h);
    }

    /// <summary>Image ids the residency manager evicted (GPU texture freed) — for eviction assertions.</summary>
    public IReadOnlyList<int> Evictions => _evictions;
    public void EvictImage(int imageId) { _resident.Remove(imageId); _evictions.Add(imageId); }
    public void SetBakedBlurQueue(BakedBlurQueue queue) => _bakedBlurs = queue;
    public bool HasPendingUploads => false;

    /// <summary>E5 (design-engine-images.md): census of fence-only-maintenance calls — how many times AppHost reclaimed
    /// on an elided/skipped frame instead of forcing a submit. `gate.repaint.elided-frame-reclaims` reads this.</summary>
    public int ReclaimCalls { get; private set; }
    public void ReclaimCompletedUploads() => ReclaimCalls++;

    /// <summary>Phase 1: <c>HintSettlePresent</c> moved to <see cref="ISwapchain"/> (per-target seam), so the counter
    /// itself now lives on <see cref="HeadlessSwapchain"/>. Reads the PRIMARY target's counter — every existing
    /// headless gate (LayoutShellSuite.cs RZ-SETTLE) creates exactly one swapchain, so this keeps them passing
    /// unchanged.</summary>
    public int HintSettlePresentCount => _primarySwapchain?.HintSettlePresentCount ?? 0;

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)
    {
        BeginModel(in ctx);
        int balance = 0, layerBalance = 0, stencilBalance = 0;
        DecodeInto(drawList, 0f, 0f, ref balance, ref layerBalance, ref stencilBalance);
        EndModel(balance, layerBalance, stencilBalance);
    }

    /// <summary>Open one modelled frame: clear every per-frame list (capacity kept) and take one baked-blur job.</summary>
    private void BeginModel(in FrameInfo ctx)
    {
        _rects.Clear();   // retains capacity → no alloc after warmup
        if (_bakedBlurs is { Paused: false } bakes)
            if (bakes.TryDequeueJob(out var job))
            {
                if (_resident.ContainsKey(job.SourceId))
                {
                    _resident[job.Id] = (job.OutputW, job.OutputH);
                    bakes.Post(new BakedBlurQueue.Result(job.Id, job.Generation, true, job.OutputW, job.OutputH,
                        job.Quality, job.IsUpgrade));
                }
                else bakes.Post(new BakedBlurQueue.Result(job.Id, job.Generation, false, 0, 0,
                    job.Quality, job.IsUpgrade));
            }
        _rectClipDepth.Clear();
        _glyphs.Clear();
        _glyphGradients.Clear();
        _clips.Clear();
        _imageDraws.Clear();
        _strokes.Clear();
        _strokeClipDepth.Clear();
        _shadows.Clear();
        _arcs.Clear();
        _polylines.Clear();
        _gradients.Clear();
        _gradientStrokes.Clear();
        _layers.Clear();
        _tabShapes.Clear();
        _iconMasks.Clear();
        _videos.Clear();
        _erases.Clear();
        _fillPaths.Clear();
        _strokePaths.Clear();
        _series.Clear();
        _blends.Clear();
        _spriteChunks.Clear();
        _videoClipDepth.Clear();
        _stencilClips.Clear();
        _stencilPops.Clear();
        _imageStencilDepth.Clear();
        _rectStencilDepth.Clear();
        LastClear = ctx.Clear;
        LastFrameInfo = ctx;
        FrameCount++;
    }

    /// <summary>Decode one command stream INTO the current modelled frame, every op translated by (<paramref name="dx"/>,
    /// <paramref name="dy"/>) (a composite item's posed offset), continuing the clip / layer / stencil balances.</summary>
    private void DecodeInto(ReadOnlySpan<byte> stream, float dx, float dy, ref int balance, ref int layerBalance, ref int stencilBalance)
        => DecodeInto(stream, dx, dy, ref balance, ref layerBalance, ref stencilBalance, default, default, 0f, 0f, compose: false);

    /// <summary>The composite form: <paramref name="spans"/> (segment-relative, slice px) culls every clean subtree whose
    /// placed bounds (+ <paramref name="placeX"/>/<paramref name="placeY"/> device px) miss <paramref name="clipPx"/> (empty =
    /// unbounded) — what the per-tile replay culls — and <paramref name="compose"/> appends each decoded op to
    /// <see cref="LastComposedStream"/>.</summary>
    private void DecodeInto(ReadOnlySpan<byte> stream, float dx, float dy, ref int balance, ref int layerBalance, ref int stencilBalance,
        ReadOnlySpan<Render.Tiles.SliceSpan> spans, RectF clipPx, float placeX, float placeY, bool compose)
    {
        Span<byte> moved = _moved;
        bool translate = dx != 0f || dy != 0f;
        bool cull = !spans.IsEmpty && !(clipPx.W <= 0f && clipPx.H <= 0f && clipPx.X == 0f && clipPx.Y == 0f);
        int at = 0, e = 0;
        while (at + sizeof(int) <= stream.Length)
        {
            if (cull)
            {
                while (e < spans.Length && spans[e].ByteStart < at) e++;
                bool skipped = false;
                while (e < spans.Length && spans[e].ByteStart == at)
                {
                    var en = spans[e];
                    if (!en.HasMarker && en.ByteLength > 0)
                    {
                        var wb = new RectF(en.Bounds.X + placeX, en.Bounds.Y + placeY, en.Bounds.W, en.Bounds.H);
                        if (!wb.Overlaps(clipPx)) { at = en.ByteStart + en.ByteLength; skipped = true; break; }
                    }
                    e++;
                }
                if (skipped) continue;
            }
            int op = MemoryMarshal.Read<int>(stream.Slice(at));
            if (!RepaintStreamSafety.TryBodySize((DrawOp)op, out int body) || at + sizeof(int) + body > stream.Length) return;
            ReadOnlySpan<byte> drawList = stream.Slice(at + sizeof(int), body);
            if (translate && body <= moved.Length)
            {
                drawList.CopyTo(moved);
                DrawOpTranslate.Apply((DrawOp)op, moved[..body], dx, dy);
                drawList = moved[..body];
            }
            at += sizeof(int) + body;
            if (compose)
            {
                drawList.CopyTo(_composed.AppendOp((DrawOp)op, body, 0));
                if ((DrawOp)op == DrawOp.PushClip) _modelClipTop.Add(MemoryMarshal.Read<ClipCmd>(drawList).DeviceRect);
                else if ((DrawOp)op == DrawOp.PopClip && _modelClipTop.Count > 0) _modelClipTop.RemoveAt(_modelClipTop.Count - 1);
            }
            int pos = 0;
            switch ((DrawOp)op)
            {
                case DrawOp.FillRoundRect:
                    _rects.Add(MemoryMarshal.Read<FillRoundRectCmd>(drawList.Slice(pos)));
                    _rectClipDepth.Add(balance);
                    _rectStencilDepth.Add(stencilBalance);
                    pos += Unsafe.SizeOf<FillRoundRectCmd>();
                    break;
                case DrawOp.DrawGlyphRun:
                    _glyphs.Add(MemoryMarshal.Read<DrawGlyphRunCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawGlyphRunCmd>();
                    break;
                case DrawOp.DrawGlyphRunGradient:
                    _glyphGradients.Add(MemoryMarshal.Read<DrawGlyphRunGradientCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawGlyphRunGradientCmd>();
                    break;
                case DrawOp.PushClip:
                    _clips.Add(MemoryMarshal.Read<ClipCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<ClipCmd>();
                    balance++;
                    break;
                case DrawOp.PopClip:
                    balance--;
                    break;
                case DrawOp.DrawImage:
                    _imageDraws.Add(MemoryMarshal.Read<DrawImageCmd>(drawList.Slice(pos)));
                    _imageStencilDepth.Add(stencilBalance);
                    pos += Unsafe.SizeOf<DrawImageCmd>();
                    break;
                case DrawOp.DrawRoundRectStroke:
                    _strokes.Add(MemoryMarshal.Read<DrawRoundRectStrokeCmd>(drawList.Slice(pos)));
                    _strokeClipDepth.Add(balance);
                    pos += Unsafe.SizeOf<DrawRoundRectStrokeCmd>();
                    break;
                case DrawOp.DrawShadow:
                    _shadows.Add(MemoryMarshal.Read<DrawShadowCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawShadowCmd>();
                    break;
                case DrawOp.DrawArc:
                    _arcs.Add(MemoryMarshal.Read<DrawArcCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawArcCmd>();
                    break;
                case DrawOp.DrawPolylineStroke:
                    _polylines.Add(MemoryMarshal.Read<DrawPolylineStrokeCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawPolylineStrokeCmd>();
                    break;
                case DrawOp.DrawGradientRect:
                    _gradients.Add(MemoryMarshal.Read<DrawGradientRectCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawGradientRectCmd>();
                    break;
                case DrawOp.DrawGradientStroke:
                    _gradientStrokes.Add(MemoryMarshal.Read<DrawGradientStrokeCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawGradientStrokeCmd>();
                    break;
                case DrawOp.PushLayer:
                {
                    var layer = MemoryMarshal.Read<PushLayerCmd>(drawList.Slice(pos));
                    _layers.Add(layer);
                    // An ACRYLIC PushLayer decoded out of a composite item's segment: the invariant gates pair it with the
                    // frame's Backdrop items (a frost exists only as a composite item placed before its slice).
                    if (compose && _modelItem >= 0 && layer.Kind == (int)LayerKind.Acrylic)
                        _acrylicLayerItems.Add((_modelItem, layer.DeviceRect));
                    pos += Unsafe.SizeOf<PushLayerCmd>();
                    layerBalance++;
                    break;
                }
                case DrawOp.PopLayer:
                    pos += Unsafe.SizeOf<PopLayerCmd>();
                    layerBalance--;
                    break;
                case DrawOp.DrawTabShape:
                    _tabShapes.Add(MemoryMarshal.Read<DrawTabShapeCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawTabShapeCmd>();
                    break;
                case DrawOp.DrawIconMask:
                    _iconMasks.Add(MemoryMarshal.Read<DrawIconMaskCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawIconMaskCmd>();
                    break;
                case DrawOp.DrawVideo:
                    _videos.Add(MemoryMarshal.Read<DrawVideoCmd>(drawList.Slice(pos)));
                    _videoClipDepth.Add(balance);
                    pos += Unsafe.SizeOf<DrawVideoCmd>();
                    break;
                case DrawOp.EraseRoundRect:
                    _erases.Add(MemoryMarshal.Read<EraseRoundRectCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<EraseRoundRectCmd>();
                    break;
                case DrawOp.FillPath:
                    _fillPaths.Add(MemoryMarshal.Read<FillPathCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<FillPathCmd>();
                    break;
                case DrawOp.StrokePath:
                    _strokePaths.Add(MemoryMarshal.Read<StrokePathCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<StrokePathCmd>();
                    break;
                case DrawOp.DrawSeries:
                    _series.Add(MemoryMarshal.Read<DrawSeriesCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawSeriesCmd>();
                    break;
                case DrawOp.DrawSprites:
                    _spriteChunks.Add(MemoryMarshal.Read<DrawSpritesCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawSpritesCmd>();
                    break;
                case DrawOp.SetBlend:
                    _blends.Add(MemoryMarshal.Read<SetBlendCmd>(drawList.Slice(pos)).Mode);
                    pos += Unsafe.SizeOf<SetBlendCmd>();
                    break;
                // A stencil clip IS a clip level (its DeviceRect is the scope's scissor), so it moves `balance` too —
                // that keeps ClipBalance honest for every existing gate that asserts on it.
                case DrawOp.PushStencilClip:
                    _stencilClips.Add(MemoryMarshal.Read<PushStencilClipCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<PushStencilClipCmd>();
                    balance++;
                    stencilBalance++;
                    break;
                case DrawOp.PopStencilClip:
                    _stencilPops.Add(MemoryMarshal.Read<PopStencilClipCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<PopStencilClipCmd>();
                    balance--;
                    stencilBalance--;
                    break;
                case DrawOp.CompositeSlice:
                    break;   // a slice marker is recorder-internal: its child composites as its own item
                default:
                    return; // unknown opcode — stop (corrupt stream guard)
            }
        }
    }

    private const int MaxTranslatedPayload = 1024;
    private readonly byte[] _moved = new byte[MaxTranslatedPayload];
    private readonly List<RectF> _modelClipTop = new(16);
    // The composite item whose segment the model is decoding (−1 outside a composite) and the acrylic layers found in them.
    private int _modelItem = -1;
    private readonly List<(int Item, RectF RectDip)> _acrylicLayerItems = new(4);

    /// <summary>Every ACRYLIC PushLayer the most recent composite decoded out of an item's segment: (the composite item
    /// index, the layer's frosted rect in window DIP at the item's posed offset). A frost exists only as a
    /// <see cref="CompositeKind.Backdrop"/> item placed BEFORE that item; an acrylic layer without one is a hole.</summary>
    public IReadOnlyList<(int Item, RectF RectDip)> LastAcrylicLayerItems => _acrylicLayerItems;

    private void EndModel(int balance, int layerBalance, int stencilBalance)
    {
        ClipBalance = balance;
        LayerBalance = layerBalance;
        StencilClipBalance = stencilBalance;
    }


    // Test seam (CANDIDATE FIX, INCIDENT 2026-09 gate): arm a ONE-SHOT throw against a SPECIFIC target's next submit —
    // keyed by target so a gate can fault a detached CHILD's swapchain without touching the parent's own submits (the
    // real incident: a child-only D3D12 failure that must not take the shared render thread down with it). Lets
    // `gate.detached.render-failure-survives` exercise AppHost.SubmitPresentOnRenderThread's catch (RenderFailed
    // latch, OnRenderFailed, DrainChildRenderSources skipping a failed child) against a real headless AppHost/render
    // thread, with no D3D12 device involved.
    private readonly Dictionary<ISwapchain, Exception> _throwOnNextSubmit = new();
    public void ThrowOnNextSubmit(ISwapchain target, Exception ex) => _throwOnNextSubmit[target] = ex;

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx, ISwapchain target)
    {
        if (_throwOnNextSubmit.Remove(target, out var armedEx)) throw armedEx;
        LastDirectTarget = target;
        DirectSubmitCount++;
        SubmitDrawList(drawList, sortKeys, in ctx);
    }

    /// <summary>The swapchain of the most recent targeted <c>SubmitDrawList</c> (the DIRECT route a detached pop-out and a
    /// windowed popup present through); null before any.</summary>
    public ISwapchain? LastDirectTarget { get; private set; }

    /// <summary>Completed targeted <c>SubmitDrawList</c> calls (the direct route; composite turns are
    /// <see cref="CompositeFrameCount"/>).</summary>
    public int DirectSubmitCount { get; private set; }

    // Per-swapchain present-queue depth (IGpuDevice.SetPresentQueueDepth(ISwapchain, int)): a headless device has no queue to
    // resize, so this only records what each target was last set to (1..2 like the D3D12 policy bound) for the routing gates —
    // MaxFrameLatency stays 1 so the deterministic PresentQpc = FrameQpc + 2·refresh contract holds.
    private readonly Dictionary<ISwapchain, int> _presentQueueDepths = new();

    public int SetPresentQueueDepth(ISwapchain target, int depth)
    {
        int want = Math.Clamp(depth, 1, 2);
        _presentQueueDepths[target] = want;
        return want;
    }

    /// <summary>The present-queue depth <paramref name="target"/> was last set to (1 before any set): a test seam for "a child's
    /// depth policy retargets the child's swapchain and never the primary's".</summary>
    public int PresentQueueDepthOf(ISwapchain target) => _presentQueueDepths.TryGetValue(target, out int d) ? d : 1;

    public void Dispose() { }
}

public sealed class HeadlessSwapchain : ISwapchain
{
    private readonly bool _renderConfined;

    public HeadlessSwapchain(Size2 size, bool renderConfined = false) { SizePx = size; _renderConfined = renderConfined; }
    public Size2 SizePx { get; private set; }
    public int PresentCount { get; private set; }
    public void Resize(Size2 px) { if (_renderConfined) ThreadGuard.AssertRenderOwner(); SizePx = px; }

    /// <summary>Model the real backend's PRESENT STAND-DOWN: <c>D3D12Device.Present</c> paints nothing when its target
    /// is covered / cloaked / hidden, so a requested present is not a painted one. While this is set
    /// <see cref="Present"/> is a no-op and the target keeps NO presented content — which is what lets a headless check
    /// exercise the popup reveal handshake (a popup window must stay hidden until its swapchain has actually
    /// presented, or it appears as its frosted composition chrome with nothing inside it).</summary>
    public bool PresentStandDown { get; set; }

    public void Present()
    {
        if (PresentStandDown) return;
        PresentCount++;
    }

    /// <summary>Test seam: model a full present queue - while set, <see cref="PresentNoWait"/> is REFUSED (nothing presented)
    /// and counted in <see cref="RefusedPresents"/>, as the real backend does on DXGI_ERROR_WAS_STILL_DRAWING.</summary>
    public bool RefusePresentNoWait { get; set; }

    /// <summary>Non-blocking presents refused while <see cref="RefusePresentNoWait"/> was set.</summary>
    public int RefusedPresents { get; private set; }

    /// <inheritdoc/>
    public bool PresentNoWait()
    {
        if (RefusePresentNoWait) { RefusedPresents++; return false; }
        Present();
        return true;
    }

    /// <inheritdoc/>
    public bool HasPresentedContent => PresentCount > 0;
    public void Dispose() { if (_renderConfined) ThreadGuard.AssertRenderOwner(); }

    /// <summary>THIS target's last-present stand-down (Phase 1: per-target, not device-global). Mirrors
    /// <see cref="PresentStandDown"/> — when the configured stand-down suppresses a present, it stood down.</summary>
    public bool LastPresentStoodDown => PresentStandDown;

    /// <summary>Test seam: a modelled DXGI occlusion latch.</summary>
    public bool Occluded { get; set; }

    /// <inheritdoc/>
    public bool IsOccluded => Occluded || PresentStandDown;

    /// <summary>Phase 1 (detached-window-render-isolation-implementation.md §3.3): moved off IGpuDevice onto the
    /// target it actually describes. `HeadlessGpuDevice.HintSettlePresentCount` delegates to the primary swapchain's
    /// count so the existing gate keeps reading it off the device.</summary>
    public int HintSettlePresentCount { get; private set; }
    public void HintSettlePresent() => HintSettlePresentCount++;

    /// <summary>Test seam (F101): how many times the host completed the settle sync, and the last blocking flag it passed
    /// (true only for an inline UI-thread present; a render-thread present never blocks).</summary>
    public int CompleteSettlePresentCount { get; private set; }
    public bool LastSettleBlocked { get; private set; }
    public void CompleteSettlePresent(bool blockUntilComposed)
    {
        CompleteSettlePresentCount++;
        LastSettleBlocked = blockUntilComposed;
    }

    // Windowed desktop-acrylic popup chrome (the real D3D12 backend drives Windows.UI.Composition; headless captures the
    // parameters so the cross-seam wiring — content rect, open direction, closedRatio, corner — is verifiable).
    public PopupChromeMetrics? LastPopupChrome { get; private set; }
    public bool PopupOpenPlayed { get; private set; }
    public void ConfigurePopupChrome(in PopupChromeMetrics m) => LastPopupChrome = m;
    public void AnimatePopupOpen() => PopupOpenPlayed = true;
}
