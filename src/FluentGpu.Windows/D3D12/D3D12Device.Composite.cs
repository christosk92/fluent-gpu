using System.Runtime.CompilerServices;
using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Gen = FluentGpu.Interop.Generated;

using ColorF = FluentGpu.Foundation.ColorF;
using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

// The retained-tile COMPOSITE submit (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.4/§A.5/§A.8/§B/§D) — the
// primary window's only route. One command list, three phases, no back-buffer read and no copy of any render target:
//   1. TILE RASTER — every tile on the frame's raster list (visible first) replays its slice segment into its own surface
//      in one CLEAR→PRESERVE render pass (TileRasterizer); RasterDone reports exactly the tiles that landed faithfully.
//   2. OFFSCREEN — the work the composite pass samples: degraded (direct) segments rastered into a region scratch, group
//      surfaces (a layer slice with children, composited once — exact group semantics), self-blur (cached per slice by
//      content + σ), acrylic backdrops (a mini-composite of everything painted before the surface, dual-Kawase blurred).
//   3. COMPOSITE — ONE CLEAR→STORE pass on the back buffer: the clear colour, then every item in painter order —
//      whole-pixel tile placements (an exact texel Load × alpha × analytic feather × sdRoundRect clip), region / group /
//      blur surfaces, acrylic, DestOut video holes.
// The primary target is FLIP_SEQUENTIAL and presents PARTIALLY when it can (D3D12Device.PartialPresent.cs): the composite
// pass then PRESERVES the back buffer and recomposites only the repaint rects, and Present1 carries the dirty rects. The
// whole-frame route (CLEAR load, full present) stays for every frame whose history cannot be trusted.
public sealed unsafe partial class D3D12Device
{
    public bool SupportsComposite => true;

    private SurfacePool? _surfaces;
    private SliceCompositor? _compositor;
    private int _compositeTurn;
    private int _frameFeatherItems;
    /// <summary>Pixels (inside the item's scissor) composited through the analytic feather's evaluation this frame —
    /// only the strips outside each feather's unit interior (<see cref="FeatherQuadSplit"/>).</summary>
    private long _frameFeatherPx;
    private int _frameBlurCacheHits;
    // Placements this turn skipped because their slot held no texture (TouchTile false) — a valid tile that composited
    // nothing. Structurally 0 since the table owns tile texture lifetime (CompositeFrame.TrimSurfaces); always-on detector.
    private int _frameLostPlacements;
    private bool _lostPlacementsLogged;
    private int[] _rowOfSlice = NewRowMap(256);
    private int[] _rowPrepared = new int[256];
    private int[] _itemSurface = new int[64];
    private int[] _itemDown = new int[64];
    private ulong[] _itemKey = new ulong[64];   // a prepared group's content key (GroupCacheKey; 0 = not cacheable)
    private PixelRect[] _itemRegion = new PixelRect[64];
    private int[] _itemChunkStart = new int[64], _itemChunkCount = new int[64];
    private readonly record struct DirectChunk(int Surface, PixelRect Rect);
    private DirectChunk[] _chunks = new DirectChunk[32];
    private int _chunkCount;
    private D3D12_RESOURCE_BARRIER[] _tileBarriers = new D3D12_RESOURCE_BARRIER[64];
    private bool _renderPassesLogged;

    /// <summary>Retained-tile census of the last composite (render thread writes, diagnostics read).</summary>
    public int LastTilesRastered => _frameTilesRastered;
    public int LastRenderPasses => _frameRenderPasses;
    public int LastInlineGroups => _frameInlineGroups;
    public int LastDirectRegions => _frameDirectRegions;
    /// <summary>Self-blurs / acrylic backdrops the last composite re-drew from a retained result instead of recomputing.</summary>
    public int LastBlurCacheHits => _frameBlurCacheHits;
    public long TileSurfaceBytes => _surfaces?.TileBytes ?? 0L;

    // ── always-on per-kind offscreen split (plain fields; the scroll bench's diagnosis line) ──
    private int _offGroupN, _offGroupHits, _offBlurN, _offBlurHits, _offBackdropN, _offBackdropHits, _offDirectN, _offInlineN, _groupRenders;
    private long _offGroupPx, _offBlurPx, _offBackdropPx, _offDirectPx, _offInlinePx;

    /// <summary>Where the last composite's offscreen work went, per kind: surfaces leased + their pixel area for group
    /// surfaces, leaf self-blurs, acrylic backdrops, degraded direct segments and inline (folded) layers inside tiles, and
    /// the cache hits that avoided the work.</summary>
    public OffscreenSplit LastOffscreenSplit => new(_offGroupN, _offGroupPx, _offGroupHits, _offBlurN, _offBlurPx, _offBlurHits,
        _offBackdropN, _offBackdropPx, _offBackdropHits, _offDirectN, _offDirectPx, _offInlineN, _offInlinePx);

    /// <summary>Group surfaces rendered by the last composite (misses; <see cref="LastOffscreenSplit"/> has the hits).</summary>
    public int LastGroupSurfaces => _groupRenders;

    /// <inheritdoc/>
    public CompositeCacheStats LastCompositeCache => new(_groupRenders, _offGroupHits, _surfaces?.RetainedBytes ?? 0L);

    /// <inheritdoc/>
    public int LastLostPlacements => _frameLostPlacements;

    private void ResetOffscreenSplit()
    {
        _offGroupN = _offGroupHits = _offBlurN = _offBlurHits = _offBackdropN = _offBackdropHits = _offDirectN = _offInlineN = _groupRenders = 0;
        _groupRepairs = 0;
        _offGroupPx = _offBlurPx = _offBackdropPx = _offDirectPx = _offInlinePx = 0L;
    }

    private void EnsureCompositeResources(int tileCap)
    {
        if (_surfaces is not null && _surfaces.TileCap >= tileCap) return;
        if (_surfaces is not null) { WaitForGpu(); _surfaces.Dispose(); }
        _surfaces = new SurfacePool(_device, Math.Max(tileCap, 64));
        _compositeOwner = 0;   // a fresh pool has no owner yet: the next composite stamps it (CheckCompositeOwner)
        if (_compositor is null)
        {
            _compositor = new SliceCompositor();
            _compositor.Init(_device);
        }
    }

    // The SliceTable (CompositeFrame.OwnerToken) whose surface numbering the current tile pool holds; 0 = none since the
    // pool was (re)built. Tile slots carry no host key, so two hosts compositing into one pool silently overwrite each
    // other's retained tiles (F229) - this makes that visible instead of a stale-pixel mystery.
    private int _compositeOwner;

    // Checked BEFORE the submit opens (a Debug throw must not leave a command list open); the pool's first owner is stamped
    // after EnsureCompositeResources, so a pool rebuilt by THIS frame belongs to it.
    private void CheckCompositeOwner(int token)
    {
        if (token == 0 || _compositeOwner == 0 || _compositeOwner == token) return;   // unstamped frame / no owner yet / same owner
        Diag.Count("d3d12", "compositeOwnerMismatch");
#if DEBUG
        throw new InvalidOperationException("SubmitComposite: the tile pool belongs to slice table " + _compositeOwner
            + " but this frame was built from slice table " + token + " (two hosts share one device tile pool).");
#endif
    }

    public void SubmitComposite(in CompositeFrame frame, ISwapchain target)
    {
        var sc = _primarySwapchain ?? throw new InvalidOperationException("CreateSwapchain must be called before SubmitComposite.");
        if (!ReferenceEquals(target, sc))
            throw new InvalidOperationException("SubmitComposite composites into the PRIMARY swapchain only; a secondary target (detached pop-out / popup) must use SubmitDrawList.");
        if (sc.Disposed) throw new InvalidOperationException("The primary swapchain is disposed.");
        AssertSubmitThread();
        CheckCompositeOwner(frame.OwnerToken);
        TargetFrameState f = sc.Frame;
        // Tiles and region scratches can exceed a small window: the stencil DSV covers max(window, tile).
        _stencilFloorW = TileGrid.W; _stencilFloorH = TileGrid.H;
        int slot = OpenSubmit(sc, f);
        _frameScale = frame.Info.Scale <= 0f ? 1f : frame.Info.Scale;
        _imageClockMs = frame.Info.ImageClockMs;
        _glyphs!.BeginFrame(slot);
        int maxSurface = 0;
        for (int i = 0; i < frame.Rasters.Length; i++) maxSurface = Math.Max(maxSurface, frame.Rasters[i].Surface + 1);
        for (int i = 0; i < frame.Placements.Length; i++) maxSurface = Math.Max(maxSurface, frame.Placements[i].Surface + 1);
        EnsureCompositeResources(maxSurface);
        if (_compositeOwner == 0) _compositeOwner = frame.OwnerToken;
        MapRows(in frame);
        PrepareCompositeGlyphs(in frame);
        BeginRecording(sc, f, slot, isPrimaryTarget: true);
        ResetFrameCounters();
        _frameFeatherItems = 0; _frameFeatherPx = 0; _frameBlurCacheHits = 0; _frameTilesRastered = 0; _frameRenderPasses = 0; _frameInlineGroups = 0; _frameDirectRegions = 0;
        _frameLostPlacements = 0;
        ResetOffscreenSplit();
        BeginPipesFrame(slot);
        _compositeTurn++;
        _surfaces!.BeginFrame(slot, _compositeTurn, Gen.ID3D12FenceVtbl.GetCompletedValue(_fence), GpuProfile.IsWeak);
        // Tile textures trim ONLY where the table released the slot (no tile holds it) — never on the pool's own clock:
        // a tile consumed through a retained group / self-blur result is valid and placed while nothing samples it.
        _surfaces.TrimTiles(frame.TrimSurfaces);
        LogRenderPassesOnce();
        f.TextRepaintPending = false;
        EnsureItemScratch(frame.Items.Length);

        // 1. tiles (any scratch leased here is an inline folded layer inside a tile)
        PassBoundary(GpuPassKind.TileRaster, TileGrid.W, TileGrid.H);
        int inl0 = _surfaces.ScratchLeases; long inlPx0 = _surfaces.ScratchPx;
        RasterTiles(in frame);
        _offInlineN += _surfaces.ScratchLeases - inl0; _offInlinePx += _surfaces.ScratchPx - inlPx0;
        // 2. offscreen
        PassBoundary(GpuPassKind.Offscreen, (int)_w, (int)_h);
        PrepareTop(in frame);   // PrepareRange per top-level item + occlusion (D3D12Device.Occlusion.cs)
        // 3. the composite pass
        PassBoundary(GpuPassKind.Composite, (int)sc.W, (int)sc.H);
        ID3D12Resource* backBuffer = sc.BackBuffers[f.FrameIndex];
        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = sc.RtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += f.FrameIndex * _rtvSize;
        // Partial present (D3D12Device.PartialPresent.cs): what changed since this back buffer last received a frame.
        PpComputeDirty(in frame, sc);
        PpPlanRepaint(sc, f);
        if (_ppPartial)
        {
            // PRESERVE: the buffer already holds every pixel outside the repaint rects; each rect is cleared and recomposited.
            BeginPass(rtv, (int)_w, (int)_h, PassLoad.Preserve);
            BindCompositor((int)_w, (int)_h);
            PpCompositeRects(in frame);
        }
        else
        {
            // The pass's CLEAR load op writes the clear colour: like DISCARD it never reads the previous contents, and on
            // a tiler it is a per-bin fast clear instead of a full-window quad.
            BeginPass(rtv, (int)_w, (int)_h, PassLoad.Clear, frame.Info.Clear);
            BindCompositor((int)_w, (int)_h);
            if ((_frameKnockouts & GpuKnockouts.ClearOnly) == 0) DrawRange(in frame, 0, frame.Items.Length, 0, 0, (int)_w, (int)_h, -1);
        }
        EndPassIfOpen();
        PpEndFrame(sc, f);
        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);

        _surfaces.EndFrame();
        EndFeedbackFrame(in frame);   // F6: publish HasLiveFeedback, forget trails whose boundary left
        UnmapRows(in frame);
        NoteLostPlacementsEdge(_frameLostPlacements);
        NoteFaithfulness(f);
        PublishDecodeDiagnostics();
        Diag.Set("d3d12", "tilesRastered", _frameTilesRastered);
        Diag.Set("d3d12", "renderPasses", _frameRenderPasses);
        Diag.Set("d3d12", "inlineGroups", _frameInlineGroups);
        Diag.Set("d3d12", "directRegions", _frameDirectRegions);
        Diag.Set("d3d12", "blurCacheHits", _frameBlurCacheHits);
        EndRecording(sc, f, slot, RepaintRoute.Composite, _frameBackBufferTransitions);
    }

    /// <summary>Edge-gated, always-on: a composite that skipped a placement because its slot held no texture (a valid tile
    /// that drew nothing — the "content disappears" class). One line per onset, re-armed by a clean turn.</summary>
    private void NoteLostPlacementsEdge(int lost)
    {
        if (lost <= 0) { _lostPlacementsLogged = false; return; }
        if (_lostPlacementsLogged) return;
        _lostPlacementsLogged = true;
        Diag.Line("[d3d12.tiles] lostPlacements=" + lost.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " turn=" + _compositeTurn.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " (a placed tile's surface slot held no texture: that tile composited nothing this turn)");
    }

    private void LogRenderPassesOnce()
    {
        if (_renderPassesLogged) return;
        _renderPassesLogged = true;
        D3D12SideQueues.QueryRenderPassesTier(_device, out D3D12_RENDER_PASS_TIER tier);
        Diag.Line($"[d3d12.present] renderPassesTier={(int)tier} composite=retained-tiles tile={TileGrid.W}x{TileGrid.H} canonicalViewport={CanonicalViewport} present=partial(FLIP_SEQUENTIAL, Present1 dirty rects)");
    }

    // ── rows / items bookkeeping ──────────────────────────────────────────────────────────────────────────────────

    private void MapRows(in CompositeFrame frame)
    {
        int max = 0;
        for (int i = 0; i < frame.Slices.Length; i++) max = Math.Max(max, frame.Slices[i].Id + 1);
        if (_rowOfSlice.Length < max) { _rowOfSlice = NewRowMap(max * 2); _rowPrepared = new int[max * 2]; }
        for (int i = 0; i < frame.Slices.Length; i++) _rowOfSlice[frame.Slices[i].Id] = i;
    }

    private void UnmapRows(in CompositeFrame frame)
    {
        for (int i = 0; i < frame.Slices.Length; i++) _rowOfSlice[frame.Slices[i].Id] = -1;
    }

    private static int[] NewRowMap(int n)
    {
        var a = new int[n];
        Array.Fill(a, -1);
        return a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RowOf(int sliceId) => (uint)sliceId < (uint)_rowOfSlice.Length ? _rowOfSlice[sliceId] : -1;

    private void EnsureItemScratch(int n)
    {
        if (_itemSurface.Length < n)
        {
            int c = Math.Max(n, _itemSurface.Length * 2);
            _itemSurface = new int[c]; _itemDown = new int[c]; _itemRegion = new PixelRect[c];
            _itemChunkStart = new int[c]; _itemChunkCount = new int[c]; _itemKey = new ulong[c];
        }
        Array.Fill(_itemSurface, -1, 0, n);
        Array.Clear(_itemKey, 0, n);
        Array.Clear(_itemChunkCount, 0, n);
        _chunkCount = 0;
    }

    /// <summary>Every stream this frame DECODES (a rastered tile's segment, a degraded segment) is glyph-prepared before
    /// the list opens — the atlas band then uploads once, outside every render pass.</summary>
    private void PrepareCompositeGlyphs(in CompositeFrame frame)
    {
        int stamp = _compositeTurn + 1;
        bool allDirect = ((GpuKnockouts)_knockouts & GpuKnockouts.ForceFullDirect) != 0;
        for (int i = 0; i < frame.Rasters.Length; i++)
        {
            int r = RowOf(frame.Rasters[i].Key.SliceId);
            if (r < 0 || _rowPrepared[r] == stamp) continue;
            _rowPrepared[r] = stamp;
            SliceRow row = frame.Slices[r];
            PrepareGlyphs(frame.StreamOf(in row));
        }
        for (int i = 0; i < frame.Items.Length; i++)
        {
            ref readonly CompositeItem it = ref frame.Items[i];
            bool direct = it.Kind == CompositeKind.Direct || (allDirect && it.Kind is CompositeKind.Tiles or CompositeKind.Region);
            if (!direct) continue;
            int r = RowOf(it.SliceId);
            if (r < 0 || _rowPrepared[r] == stamp) continue;
            _rowPrepared[r] = stamp;
            SliceRow row = frame.Slices[r];
            PrepareGlyphs(frame.StreamOf(in row));
        }
    }

    // ── 1. tile raster ────────────────────────────────────────────────────────────────────────────────────────────

    private void RasterTiles(in CompositeFrame frame)
    {
        ReadOnlySpan<TileRaster> rasters = frame.Rasters;
        if (rasters.Length == 0) return;
        // ForceFullDirect (knockout): nothing is retained — every segment is drawn direct, every tile stays invalid.
        if ((_frameKnockouts & GpuKnockouts.ForceFullDirect) != 0) return;
        ulong fence = _fenceValue + 1;
        if (_tileBarriers.Length < rasters.Length) _tileBarriers = new D3D12_RESOURCE_BARRIER[rasters.Length * 2];
        int nb = 0;
        for (int i = 0; i < rasters.Length; i++)
        {
            ref readonly TileRaster tr = ref rasters[i];
            if (RowOf(tr.Key.SliceId) < 0) continue;
            ID3D12Resource* res = _surfaces!.EnsureTile(tr.Surface, tr.W, tr.H, fence);
            if (_surfaces.TileState(tr.Surface) == D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET) continue;
            _tileBarriers[nb++] = Transition(res, _surfaces.TileState(tr.Surface), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
            _surfaces.SetTileState(tr.Surface, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        }
        FlushBarriers(nb);

        for (int i = 0; i < rasters.Length; i++)
        {
            ref readonly TileRaster tr = ref rasters[i];
            int r = RowOf(tr.Key.SliceId);
            if (r < 0) continue;
            ref readonly SliceRow row = ref frame.Slices[r];
            int tileX = tr.Key.Tx * TileGrid.W + row.Frame.OriginX, tileY = tr.Key.Ty * TileGrid.H + row.Frame.OriginY;
            int dropped = DroppedInstanceCount();
            int glyphDropped = _glyphs!.DroppedInstances;
            int inFlight = _frameImagesInFlight;
            int refused0 = _surfaces!.ScratchRefused;
            var rtv = _surfaces!.TileRtv(tr.Surface);
            BeginPass(rtv, tr.W, tr.H, PassLoad.Clear);
            ReplaySegment(in frame, in row, -tileX, -tileY, tr.W, tr.H, rtv,
                new RectF(tr.Key.Tx * (float)TileGrid.W, tr.Key.Ty * (float)TileGrid.H, tr.W, tr.H));
            EndPassIfOpen();
            _frameTilesRastered++;
            // Faithful only when nothing was dropped and every image it drew was resident: an overflowed bank grows at
            // the next BeginFrame, an image lands when its side-queue fence passes — either way the tile, left invalid,
            // rasters again (the host keeps turning while uploads are in flight).
            if (i < frame.RasterDone.Length)
                frame.RasterDone[i] = DroppedInstanceCount() == dropped && _glyphs.DroppedInstances == glyphDropped
                    && _frameImagesInFlight == inFlight ? (byte)1 : (byte)0;
            // evidence (§A.3): an inline group inside this tile asked for a scratch and was refused — it drew nothing
            if (i < frame.RasterFlags.Length && _surfaces.ScratchRefused != refused0)
                frame.RasterFlags[i] |= CompositeFrameFlags.RasterScratchRefused;
        }

        nb = 0;
        for (int i = 0; i < rasters.Length; i++)
        {
            ref readonly TileRaster tr = ref rasters[i];
            if (RowOf(tr.Key.SliceId) < 0) continue;
            if (_surfaces!.TileState(tr.Surface) != D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET) continue;
            _tileBarriers[nb++] = Transition(_surfaces.TileResource(tr.Surface), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            _surfaces.SetTileState(tr.Surface, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        }
        FlushBarriers(nb);
        InvalidateCmdState();
    }

    private static D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* res, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER b = default;
        b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Anonymous.Transition.pResource = res;
        b.Anonymous.Transition.StateBefore = before;
        b.Anonymous.Transition.StateAfter = after;
        b.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        return b;
    }

    private void FlushBarriers(int n)
    {
        if (n == 0) return;
        EndPassIfOpen();
        fixed (D3D12_RESOURCE_BARRIER* p = _tileBarriers) _cmdList->ResourceBarrier((uint)n, p);
        Rec(RecordedOp.Barrier, (uint)n, 0, aux: 2);
    }

    // ── 2. offscreen ──────────────────────────────────────────────────────────────────────────────────────────────

    private void PrepareRange(in CompositeFrame frame, int a, int b)
    {
        ReadOnlySpan<CompositeItem> items = frame.Items;
        bool allDirect = (_frameKnockouts & GpuKnockouts.ForceFullDirect) != 0;
        SurfacePool pool = _surfaces!;
        for (int i = a; i < b && i < items.Length; i++)
        {
            ref readonly CompositeItem it = ref items[i];
            int n0 = pool.ScratchLeases; long px0 = pool.ScratchPx;
            switch (it.Kind)
            {
                case CompositeKind.Group:
                {
                    int end = Math.Min(items.Length, i + 1 + it.GroupCount);
                    PrepareRange(in frame, i + 1, end);
                    n0 = pool.ScratchLeases; px0 = pool.ScratchPx;   // the enclosed items counted themselves
                    int hits0 = _offGroupHits, renders0 = _groupRenders;
                    PrepareGroup(in frame, i);
                    if (i < frame.ItemFlags.Length)   // evidence: the group-cache outcome, per item (§A.2)
                        frame.ItemFlags[i] |= _offGroupHits != hits0 ? CompositeFrameFlags.ItemGroupHit
                                             : _groupRenders != renders0 ? CompositeFrameFlags.ItemGroupRendered : (byte)0;
                    _offGroupN += pool.ScratchLeases - n0; _offGroupPx += pool.ScratchPx - px0;
                    i = end - 1;
                    break;
                }
                case CompositeKind.Backdrop:
                    PrepareBackdrop(in frame, i);
                    _offBackdropN += pool.ScratchLeases - n0; _offBackdropPx += pool.ScratchPx - px0;
                    break;
                case CompositeKind.Direct:
                    PrepareDirect(in frame, i);
                    _offDirectN += pool.ScratchLeases - n0; _offDirectPx += pool.ScratchPx - px0;
                    break;
                case CompositeKind.Tiles:
                case CompositeKind.Region:
                    if (allDirect)
                    {
                        PrepareDirect(in frame, i);
                        _offDirectN += pool.ScratchLeases - n0; _offDirectPx += pool.ScratchPx - px0;
                    }
                    else if (it.BlurSigma > 0f)
                    {
                        PrepareLeafBlur(in frame, i);
                        _offBlurN += pool.ScratchLeases - n0; _offBlurPx += pool.ScratchPx - px0;
                    }
                    break;
            }
        }
    }

    /// <summary>A LOW-RESOLUTION repaint boundary (BoxEl.RasterScale, <see cref="CompositeItem.LowResDown"/> = d): its
    /// visible painted region replayed ONCE into one scratch at 1/d of the window scale, composited by
    /// <see cref="DrawSurfaceItem"/>'s bilinear Sample route (down = d). It holds no tiles. The result is RETAINED under a
    /// content key (the segment's bytes, its placement, the region, d) and re-used while no repaint damage reaches the
    /// region — so a still field costs one upsample quad, and the acrylic backdrops above it stay cacheable
    /// (<see cref="BackdropKey"/> mixes <c>_itemKey</c>). The region's origin is snapped so the low-res pixel grid lands on
    /// whole window pixels: window px = (u + X0)·d − OriginX + Dx.</summary>
    private void PrepareLowRes(in CompositeFrame frame, int i, in SliceRow row)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        int d = it.LowResDown;
        PixelRect area = ItemRegion(in it, 0);
        if (!row.ContentBounds.IsEmpty)
        {
            RectF c = row.ContentBounds;
            area = new PixelRect(Math.Max(area.Left, (int)MathF.Floor(it.Transform.Dx + c.X) - 1), Math.Max(area.Top, (int)MathF.Floor(it.Transform.Dy + c.Y) - 1),
                Math.Min(area.Right, (int)MathF.Ceiling(it.Transform.Dx + c.Right) + 1), Math.Min(area.Bottom, (int)MathF.Ceiling(it.Transform.Dy + c.Bottom) + 1));
        }
        if (area.IsEmpty) return;
        int tx = (int)it.Transform.Dx, ty = (int)it.Transform.Dy;
        int x0 = FloorDiv(area.Left - tx + row.Frame.OriginX, d), y0 = FloorDiv(area.Top - ty + row.Frame.OriginY, d);
        int l = x0 * d - row.Frame.OriginX + tx, t = y0 * d - row.Frame.OriginY + ty;
        int w = (area.Right - l + d - 1) / d, h = (area.Bottom - t + d - 1) / d;
        if (w <= 0 || h <= 0) return;
        var region = new PixelRect(l, t, l + w * d, t + h * d);
        _frameDirectRegions++;

        ulong key = 0xD0E5_0000_0000_0001UL;
        Mix(ref key, (ulong)(uint)l << 32 | (uint)t); Mix(ref key, (ulong)(uint)w << 32 | (uint)h);
        Mix(ref key, (ulong)(uint)d << 32 | BitConverter.SingleToUInt32Bits(_frameScale));
        Mix(ref key, (ulong)BitConverter.SingleToUInt32Bits(row.Frame.ResidualX) << 32 | BitConverter.SingleToUInt32Bits(row.Frame.ResidualY));
        MixBytes(ref key, frame.PrefixOf(in row));
        MixBytes(ref key, frame.StreamOf(in row));
        ulong fence = _fenceValue + 1;
        var damage = frame.Info.RepaintDamage;
        bool damaged = DamageReaches(in damage, in region);
        if (!damaged)
        {
            int hit = _surfaces!.FindRetained(key, fence, out _);
            if (hit >= 0)
            {
                _frameBlurCacheHits++;
                _itemSurface[i] = hit; _itemDown[i] = d; _itemRegion[i] = region; _itemKey[i] = key;
                return;
            }
        }
        int s = _surfaces!.AcquireScratch(w, h, fence);
        if (s < 0) return;
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = _surfaces.ScratchRtv(s);
        BeginPass(rtv, w, h, PassLoad.Clear);
        float full = _frameScale;
        _frameScale = full / d;   // the replay maps stream DIPs at the reduced scale (restored below)
        try
        {
            ReplaySegment(in frame, in row, -x0, -y0, w, h, rtv, new RectF(-1e7f, -1e7f, 2e7f, 2e7f));
        }
        finally { _frameScale = full; }
        EndPassIfOpen();
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        InvalidateCmdState();
        // A damage-forced re-render may have new pixels under byte-identical commands (an image's content swap): the key
        // the backdrops above see must change with it, or they would re-use a blur of the old pixels.
        _itemSurface[i] = s; _itemDown[i] = d; _itemRegion[i] = region; _itemKey[i] = damaged ? key ^ (fence * 0x9E3779B97F4A7C15UL) | 1UL : key;
        _surfaces.Retain(s, key, d, RetainedCap());
    }

    /// <summary>A self-blurred LOW-RESOLUTION repaint boundary (BoxEl.RasterScale + Blur — the visualizer's cloud layer, a
    /// blurred backdrop field): its blur source replayed ONCE at 1/d into one scratch and blurred there at σ/d, instead of
    /// rastered at full resolution into tile-sized chunks, assembled into a full-resolution scratch and box-downsampled
    /// back down. The blur's own schedule is a 2× box chain to 1/<c>DownsampleFactor(σ)</c> before its Gaussian, so for
    /// d ≤ that factor both routes blur on the SAME texel grid (the region's top-left sits on the slice grid, a multiple of
    /// every d) with the same kernel (σ/d at 1/(d·down′) is σ at 1/down); they differ only in how a texel's first d×d
    /// block is formed — rasterized at the block's centre instead of averaged from d×d full-resolution samples — which is
    /// exactly what RasterScale asks for ("only for SOFT content whose look survives the upsample"). Measured on the
    /// visualizer's clouds (σ 40, d 4), readback against the full-resolution route: ≤ 1/255 per blurred texel; a texel
    /// straddling the source's cut edge (beyond the clip's reach, or the layer's bound) can differ more, as it holds that
    /// d×d block's content whole. A d past the blur's own factor (or one whose grids would not meet) takes the
    /// full-resolution route. Retained under a content key like <see cref="PrepareLowRes"/>, so a still blurred field costs
    /// one upsample quad.</summary>
    private void PrepareLowResBlur(in CompositeFrame frame, int i, in SliceRow row)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        int d = it.LowResDown;
        if (!SelfBlurRegion.LowResBlurOnSameGrid(it.BlurSigma, d))
        {
            PrepareDirectBlur(in frame, i, in row);
            return;
        }
        BlurRegions(in it, out PixelRect src, out PixelRect region);
        if (src.IsEmpty || region.IsEmpty) return;
        int tx = (int)it.Transform.Dx, ty = (int)it.Transform.Dy;
        int x0 = FloorDiv(region.Left - tx + row.Frame.OriginX, d), y0 = FloorDiv(region.Top - ty + row.Frame.OriginY, d);
        int l = x0 * d - row.Frame.OriginX + tx, t = y0 * d - row.Frame.OriginY + ty;
        // the low-res texel grid must be the blur's own: the region's top-left on it (always, for a slice origin and a
        // region on the 64-px slice grid — else the full-resolution route keeps the blur's phase)
        if (l != region.Left || t != region.Top) { PrepareDirectBlur(in frame, i, in row); return; }
        int w = (region.Right - l + d - 1) / d, h = (region.Bottom - t + d - 1) / d;
        if (w <= 0 || h <= 0) return;
        _frameDirectRegions++;

        ulong key = 0xB10D_0000_0000_0001UL;
        Mix(ref key, (ulong)(uint)l << 32 | (uint)t); Mix(ref key, (ulong)(uint)w << 32 | (uint)h);
        Mix(ref key, (ulong)(uint)(src.Left - l) << 32 | (uint)(src.Top - t)); Mix(ref key, (ulong)(uint)(src.Right - l) << 32 | (uint)(src.Bottom - t));
        Mix(ref key, (ulong)(uint)d << 32 | BitConverter.SingleToUInt32Bits(_frameScale));
        Mix(ref key, (ulong)BitConverter.SingleToUInt32Bits(it.BlurSigma));
        Mix(ref key, (ulong)BitConverter.SingleToUInt32Bits(row.Frame.ResidualX) << 32 | BitConverter.SingleToUInt32Bits(row.Frame.ResidualY));
        MixBytes(ref key, frame.PrefixOf(in row));
        MixBytes(ref key, frame.StreamOf(in row));
        ulong fence = _fenceValue + 1;
        var damage = frame.Info.RepaintDamage;
        bool damaged = DamageReaches(in damage, in region);
        if (!damaged)
        {
            int hit = _surfaces!.FindRetained(key, fence, out int hitDown);
            if (hit >= 0)
            {
                _frameBlurCacheHits++;
                _itemSurface[i] = hit; _itemDown[i] = hitDown; _itemRegion[i] = region; _itemKey[i] = key;
                return;
            }
        }
        int s = _surfaces!.AcquireScratch(w, h, fence);
        if (s < 0) return;
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = _surfaces.ScratchRtv(s);
        BeginPass(rtv, w, h, PassLoad.Clear);
        float full = _frameScale;
        _frameScale = full / d;   // the replay maps stream DIPs at the reduced scale (restored below)
        try
        {
            ReplaySegment(in frame, in row, -x0, -y0, w, h, rtv, new RectF(-1e7f, -1e7f, 2e7f, 2e7f));
        }
        finally { _frameScale = full; }
        // The full-resolution route clears everything outside the blur's source; texels wholly outside it are cleared here
        // (a texel straddling its edge keeps its content — the edge is the clip's reach or the layer's own bound).
        int sl = (src.Left - l) / d, st = (src.Top - t) / d;
        int sr = Math.Min(w, (src.Right - l + d - 1) / d), sb = Math.Min(h, (src.Bottom - t + d - 1) / d);
        BindCompositor(_surfaces.ScratchW(s), _surfaces.ScratchH(s));
        ClearLowRes(0, 0, w, st);
        ClearLowRes(0, sb, w, h);
        ClearLowRes(0, st, sl, sb);
        ClearLowRes(sr, st, w, sb);
        EndPassIfOpen();
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        InvalidateCmdState();
        int result = BlurSurface(s, w, h, it.BlurSigma / d, out int down);
        if (result != s) _surfaces.ReleaseScratch(s);
        // a damage-forced re-render may have new pixels under byte-identical commands: the key the backdrops above see must
        // change with it (PrepareLowRes)
        _itemSurface[i] = result; _itemDown[i] = d * down; _itemRegion[i] = region;
        _itemKey[i] = damaged ? key ^ (fence * 0x9E3779B97F4A7C15UL) | 1UL : key;
        if (result >= 0) _surfaces.Retain(result, key, d * down, RetainedCap());

        void ClearLowRes(int cl, int ct, int cr, int cb)
        {
            if (cr <= cl || cb <= ct) return;
            _compositor!.Scissor(_cmdList, cl, ct, cr, cb);
            _compositor.Begin(cl, ct, cr, cb);
            _compositor.Color(new ColorF(0f, 0f, 0f, 0f));
            _compositor.Draw(_cmdList, SliceCompositor.Pso.FillCopy, default);
        }
    }

    // ── F6 feedback trails ─────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>One feedback boundary's trail between frames: the retained-surface key of its last advance, the content key
    /// that advance consumed, how many settle advances remain, and when it last advanced (a resume after a long gap clears
    /// it — a stale trail jumping is worse than a restart). Render-thread owned; dropped with the device's surfaces.</summary>
    private struct FeedbackTrail { public ulong SurfaceKey, ContentKey; public int SettleLeft; public long LastTicks, Advances, Turn; public int W, H, D; }
    private readonly Dictionary<long, FeedbackTrail> _feedbackTrails = new();
    private int _feedbackLive;   // Volatile: any trail still settling (IGpuDevice.HasLiveFeedback)

    bool IGpuDevice.HasLiveFeedback => Volatile.Read(ref _feedbackLive) != 0;

    /// <summary>A FEEDBACK boundary (BoxEl.Feedback): advance its trail — draw the previous surface through the inverse warp,
    /// decayed, into a fresh scratch, replay this frame's content on top at the trail scale — when the content changed or
    /// while it settles; otherwise re-composite the last surface unchanged (no double decay on a composite-only turn). The
    /// result is retained under a per-advance key and composited by <see cref="DrawSurfaceItem"/>'s bilinear route.</summary>
    private void PrepareFeedback(in CompositeFrame frame, int i, in SliceRow row)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        int d = Math.Max(1, (int)it.LowResDown);
        // The trail covers the item's REGION (its composite clip ∩ the window), not the content's painted bounds: the warp
        // carries old pixels past the fresh content, and a surface anchored on bounds that move with the content would
        // re-anchor (or restart) the trail every advance. Clip the feedback box (ClipToBounds) to bound it.
        PixelRect area = ItemRegion(in it, 0);
        if (area.IsEmpty) return;
        int tx = (int)it.Transform.Dx, ty = (int)it.Transform.Dy;
        int x0 = FloorDiv(area.Left - tx + row.Frame.OriginX, d), y0 = FloorDiv(area.Top - ty + row.Frame.OriginY, d);
        int l = x0 * d - row.Frame.OriginX + tx, t = y0 * d - row.Frame.OriginY + ty;
        int w = (area.Right - l + d - 1) / d, h = (area.Bottom - t + d - 1) / d;
        if (w <= 0 || h <= 0) return;
        var region = new PixelRect(l, t, l + w * d, t + h * d);
        _frameDirectRegions++;

        long id = (long)row.NodeIndex << 32 | row.Gen;
        _feedbackTrails.TryGetValue(id, out var trail);
        float decay = float.IsFinite(it.FeedbackDecay) ? it.FeedbackDecay : it.Feedback.Decay;
        ulong content = 0xFEED_0000_0000_0001UL;
        MixBytes(ref content, frame.PrefixOf(in row));
        MixBytes(ref content, frame.StreamOf(in row));
        Mix(ref content, (ulong)BitConverter.SingleToUInt32Bits(decay) << 32 | (uint)d);
        Mix(ref content, (ulong)BitConverter.SingleToUInt32Bits(it.FeedbackWarp.M11) << 32 | BitConverter.SingleToUInt32Bits(it.FeedbackWarp.M12));
        Mix(ref content, (ulong)BitConverter.SingleToUInt32Bits(it.FeedbackWarp.Dx) << 32 | BitConverter.SingleToUInt32Bits(it.FeedbackWarp.Dy));
        ulong fence = _fenceValue + 1;
        long now = Stopwatch.GetTimestamp();
        bool resized = trail.W != w || trail.H != h || trail.D != d;
        bool stale = trail.LastTicks != 0 && (now - trail.LastTicks) > 2L * Stopwatch.Frequency;
        bool changed = content != trail.ContentKey;
        int ping = resized || stale || trail.SurfaceKey == 0 ? -1 : _surfaces!.FindRetained(trail.SurfaceKey, fence, out _);
        int s = !changed && trail.SettleLeft <= 0 && ping >= 0 ? -1 : _surfaces!.AcquireScratch(w, h, fence);
        if (s < 0)
        {
            // Parked (nothing new to draw and the trail is below one 8-bit step), or no scratch this frame: re-composite the
            // last surface as-is rather than flash empty.
            if (ping < 0) return;
            trail.Turn = _compositeTurn;
            _feedbackTrails[id] = trail;
            _itemSurface[i] = ping; _itemDown[i] = d; _itemRegion[i] = region; _itemKey[i] = trail.SurfaceKey;
            return;
        }
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = _surfaces!.ScratchRtv(s);
        BeginPass(rtv, w, h, PassLoad.Clear);
        if (ping >= 0)
        {
            // Pass 1: the previous trail through the INVERSE warp about the surface centre (the warp is DIP, the surface px
            // are DIP · scale / d), times the keep fraction toward FadeTo, minus the ordered dither.
            float k = _frameScale / d;
            Affine2D fw = it.FeedbackWarp;
            if (fw.M11 == 0f && fw.M12 == 0f && fw.M21 == 0f && fw.M22 == 0f) fw = Affine2D.Identity;
            float cx = w * 0.5f, cy = h * 0.5f;
            // forward (surface px): p' = A·(p − c) + c + t·k ; inverse: p = A⁻¹·(p' − c − t·k) + c
            float det = fw.M11 * fw.M22 - fw.M12 * fw.M21;
            if (MathF.Abs(det) < 1e-6f) det = 1e-6f;
            // (x', y') = (M11 x + M21 y, M12 x + M22 y) ⇒ x = (M22 x' − M21 y')/det, y = (−M12 x' + M11 y')/det
            float r0x = fw.M22 / det, r0y = -fw.M21 / det, r1x = -fw.M12 / det, r1y = fw.M11 / det;
            float ox = cx + fw.Dx * k, oy = cy + fw.Dy * k;
            float keep = Math.Clamp(1f - decay, 0f, 1f);
            ColorF fade = it.Feedback.FadeTo;
            BindCompositor(w, h);   // the pass just opened: nothing has bound the compositor's root signature / viewport yet
            _compositor!.Begin(0f, 0f, w, h);
            var K = _compositor.K;
            // K[11] / K[15]: the trail's extent in px (the valid part of the ping; a pooled scratch may be larger)
            K[8] = r0x; K[9] = r0y; K[10] = cx - (r0x * ox + r0y * oy); K[11] = w;
            K[12] = r1x; K[13] = r1y; K[14] = cy - (r1x * ox + r1y * oy); K[15] = h;
            K[16] = 1f / _surfaces.ScratchW(ping); K[17] = 1f / _surfaces.ScratchH(ping); K[18] = keep; K[19] = 1f / 255f;
            K[20] = fade.R * fade.A; K[21] = fade.G * fade.A; K[22] = fade.B * fade.A; K[23] = fade.A;
            _compositor!.Draw(_cmdList, SliceCompositor.Pso.Feedback, _surfaces.ScratchSrv(ping));
        }
        // Pass 2: this frame's content on top, at the trail scale. FeedbackSpec.Fresh = Additive floors the replay's paint
        // blend at additive (fresh light adds onto the trail); SrcOver leaves the stream's own SetBlend brackets in charge.
        float full = _frameScale;
        _frameScale = full / d;
        _blendBase = it.Feedback.Fresh == PaintBlend.Additive;
        try { ReplaySegment(in frame, in row, -x0, -y0, w, h, rtv, new RectF(-1e7f, -1e7f, 2e7f, 2e7f)); }
        finally { _frameScale = full; _blendBase = false; _blendAdditive = false; }
        EndPassIfOpen();
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        InvalidateCmdState();
        trail.Advances++;
        ulong key = 0xFEED_5000_0000_0001UL ^ (ulong)id;
        Mix(ref key, (ulong)trail.Advances);
        trail.SettleLeft = changed ? FeedbackSpec.SettleTurnsFor(decay) : trail.SettleLeft - 1;
        trail.SurfaceKey = key; trail.ContentKey = content; trail.LastTicks = now; trail.Turn = _compositeTurn; trail.W = w; trail.H = h; trail.D = d;
        _feedbackTrails[id] = trail;
        _surfaces.Retain(s, key, d, RetainedCap());
        _itemSurface[i] = s; _itemDown[i] = d; _itemRegion[i] = region; _itemKey[i] = key;
    }

    /// <summary>After a composite: publish whether any trail PREPARED THIS COMPOSITE still settles, and forget trails whose
    /// boundary left the frame (not prepared for 2 s). A trail missing from this composite cannot settle, so it stops
    /// counting as live at once — else an unmounted boundary held the FeedbackSettle wake forever.</summary>
    private void EndFeedbackFrame(in CompositeFrame frame)
    {
        if (_feedbackTrails.Count == 0) { Volatile.Write(ref _feedbackLive, 0); return; }
        bool live = false;
        long now = Stopwatch.GetTimestamp();
        _feedbackGone.Clear();
        foreach (var (id, trail) in _feedbackTrails)
        {
            bool here = trail.Turn == _compositeTurn;
            if (here && trail.SettleLeft > 0) live = true;
            if (!here && now - trail.LastTicks > 2L * Stopwatch.Frequency) _feedbackGone.Add(id);
        }
        foreach (long id in _feedbackGone) _feedbackTrails.Remove(id);
        Volatile.Write(ref _feedbackLive, live ? 1 : 0);
    }
    private readonly List<long> _feedbackGone = new();

    /// <summary>Does this frame's repaint set reach <paramref name="region"/> (window px)? A forced-full set reaches all.</summary>
    private bool DamageReaches(in RepaintDamageRegion damage, in PixelRect region)
    {
        if (damage.IsFull) return true;
        ReadOnlySpan<RectF> rects = damage.AsSpan();
        for (int k = 0; k < rects.Length; k++)
        {
            RectF px = new(rects[k].X * _frameScale, rects[k].Y * _frameScale, rects[k].W * _frameScale, rects[k].H * _frameScale);
            if (Overlaps(px, in region)) return true;
        }
        return false;
    }

    private static void MixBytes(ref ulong h, ReadOnlySpan<byte> bytes)
    {
        Mix(ref h, (ulong)bytes.Length);
        int n8 = bytes.Length / 8;
        ReadOnlySpan<ulong> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes[..(n8 * 8)]);
        for (int k = 0; k < words.Length; k++) Mix(ref h, words[k]);
        for (int k = n8 * 8; k < bytes.Length; k++) Mix(ref h, bytes[k]);
    }

    /// <summary>The window-px region an item's offscreen surface must cover: its clip ∩ the window (the window when
    /// unbounded), grown by <paramref name="halo"/> and clamped.</summary>
    private PixelRect ItemRegion(in CompositeItem it, int halo)
    {
        int l = 0, t = 0, r = (int)_w, b = (int)_h;
        if (!IsUnbounded(it.Clip))
        {
            l = Math.Max(l, (int)MathF.Floor(it.Clip.X) - halo); t = Math.Max(t, (int)MathF.Floor(it.Clip.Y) - halo);
            r = Math.Min(r, (int)MathF.Ceiling(it.Clip.Right) + halo); b = Math.Min(b, (int)MathF.Ceiling(it.Clip.Bottom) + halo);
        }
        return r > l && b > t ? new PixelRect(l, t, r, b) : default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnbounded(RectF clip) => clip.W <= 0f && clip.H <= 0f && clip.X == 0f && clip.Y == 0f;

    /// <summary>A DEGRADED segment (its visible tiles did not fit the budget this frame, or the ForceFullDirect knockout):
    /// replayed straight into TRANSIENT tile-grid chunks covering its visible region — today's cost, never blank — and
    /// composited like its tiles. Each chunk is the segment's tile cell at that position (origin on the tile grid, cut at
    /// the region), so every pixel is rastered at the same target position a retained tile would raster it at: the GPU's
    /// pixel quads, attribute interpolation and blur phase all match, which is what makes a degraded frame bit-identical
    /// to a composited one. A self-blurred segment assembles its chunks into the blur source exactly as the leaf path
    /// assembles its tiles.</summary>
    private void PrepareDirect(in CompositeFrame frame, int i)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        int r = RowOf(it.SliceId);
        if (r < 0) return;
        ref readonly SliceRow row = ref frame.Slices[r];
        bool blur = it.BlurSigma > 0f;
        if (it.IsFeedback && !blur && (_frameKnockouts & GpuKnockouts.ForceFullDirect) == 0) { PrepareFeedback(in frame, i, in row); return; }
        if (it.LowResDown > 1 && (_frameKnockouts & GpuKnockouts.ForceFullDirect) == 0)
        {
            if (blur) PrepareLowResBlur(in frame, i, in row);
            else PrepareLowRes(in frame, i, in row);
            return;
        }
        PrepareDirectBlur(in frame, i, in row);
    }

    /// <summary>The degraded route proper (see <see cref="PrepareDirect"/>): the segment replayed into transient tile-grid
    /// chunks at full resolution, assembled and blurred when it self-blurs.</summary>
    private void PrepareDirectBlur(in CompositeFrame frame, int i, in SliceRow row)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        bool blur = it.BlurSigma > 0f;
        PixelRect src = default, region = default;
        if (blur) BlurRegions(in it, out src, out region);
        PixelRect area = blur ? src : ItemRegion(in it, 0);
        // Bound the raster to the segment's PAINTED extent (SliceRow.ContentBounds, slice space ⇒ window px through the
        // placement offset). The tile route anchors and cuts its surfaces at exactly those bounds (SegmentOrigin /
        // SurfaceExtent), so nothing outside them is ever drawn; without this an unclipped degraded segment (#root)
        // re-rastered the WHOLE window into transient chunks every frame whatever little it actually painted.
        if (!blur && !row.ContentBounds.IsEmpty)
        {
            RectF c = row.ContentBounds;
            int cl = (int)MathF.Floor(it.Transform.Dx + c.X) - 1, ct = (int)MathF.Floor(it.Transform.Dy + c.Y) - 1;
            int cr = (int)MathF.Ceiling(it.Transform.Dx + c.X + c.W) + 1, cb = (int)MathF.Ceiling(it.Transform.Dy + c.Y + c.H) + 1;
            area = new PixelRect(Math.Max(area.Left, cl), Math.Max(area.Top, ct), Math.Min(area.Right, cr), Math.Min(area.Bottom, cb));
        }
        if (area.IsEmpty) return;
        _frameDirectRegions++;
        int first = _chunkCount;
        int tx = (int)it.Transform.Dx, ty = (int)it.Transform.Dy;
        int c0 = FloorDiv(area.Left - tx, TileGrid.W), c1 = FloorDiv(area.Right - 1 - tx, TileGrid.W);
        int r0 = FloorDiv(area.Top - ty, TileGrid.H), r1 = FloorDiv(area.Bottom - 1 - ty, TileGrid.H);
        ulong fence = _fenceValue + 1;
        for (int cy = r0; cy <= r1; cy++)
        for (int cx = c0; cx <= c1; cx++)
        {
            // the cell at its tile origin, cut at the area's far edges (the origin itself never moves)
            int l = tx + cx * TileGrid.W, t = ty + cy * TileGrid.H;
            int w = Math.Min(l + TileGrid.W, area.Right) - l, h = Math.Min(t + TileGrid.H, area.Bottom) - t;
            if (w <= 0 || h <= 0) continue;
            int s = _surfaces!.AcquireScratch(w, h, fence);
            if (s < 0) continue;
            ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
            var rtv = _surfaces.ScratchRtv(s);
            BeginPass(rtv, w, h, PassLoad.Clear);
            int cellX = cx * TileGrid.W + row.Frame.OriginX, cellY = cy * TileGrid.H + row.Frame.OriginY;
            ReplaySegment(in frame, in row, -cellX, -cellY, w, h, rtv,
                new RectF(cx * (float)TileGrid.W, cy * (float)TileGrid.H, w, h));
            EndPassIfOpen();
            ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            InvalidateCmdState();
            if (_chunkCount == _chunks.Length) Array.Resize(ref _chunks, _chunks.Length * 2);
            _chunks[_chunkCount++] = new DirectChunk(s, new PixelRect(l, t, l + w, t + h));
        }
        if (!blur)
        {
            _itemChunkStart[i] = first; _itemChunkCount[i] = _chunkCount - first;
            return;
        }
        // blur: the chunks are the crisp source — assembled, blurred, released
        int bs = AssembleBlurSource(in it, in src, in region, first, _chunkCount - first, default, 0, 0);
        for (int k = first; k < _chunkCount; k++) _surfaces!.ReleaseScratch(_chunks[k].Surface);
        _chunkCount = first;
        FinishBlur(i, bs, in region, it.BlurSigma);
    }

    /// <summary>A LEAF self-blur: its placed tiles assembled into the blur source (scissored to the source rect),
    /// Gaussian-blurred, drawn by the composite pass. Same regions and assembly as the degraded path's.</summary>
    private void PrepareLeafBlur(in CompositeFrame frame, int i)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
        if (placed.IsEmpty) return;
        BlurRegions(in it, out PixelRect src, out PixelRect region);
        if (src.IsEmpty || region.IsEmpty) return;
        // The blurred result is retained keyed by what it is made of (σ, the regions, and every placed tile's surface
        // + raster serial): a frame that changed none of it re-draws the cached blur instead of re-blurring.
        ulong key = LeafBlurKey(in it, placed, in src, in region);
        int hit = _surfaces!.FindRetained(key, _fenceValue + 1, out int hitDown);
        if (hit >= 0)
        {
            _frameBlurCacheHits++; _offBlurHits++;
            _itemSurface[i] = hit; _itemDown[i] = hitDown; _itemRegion[i] = region; _itemKey[i] = key;
            return;
        }
        _itemKey[i] = key;   // the content key the partial-present diff signs this surface with
        int bs = AssembleBlurSource(in it, in src, in region, 0, 0, placed, (int)it.Transform.Dx, (int)it.Transform.Dy);
        FinishBlur(i, bs, in region, it.BlurSigma);
        if (_itemSurface[i] >= 0) _surfaces.Retain(_itemSurface[i], key, _itemDown[i], RetainedCap());
    }

    /// <summary>The leaf self-blur's content key — engine-side (<see cref="GroupCacheKey.LeafBlur"/>) so the headless model
    /// keys its modelled cache with the same number.</summary>
    private ulong LeafBlurKey(in CompositeItem it, ReadOnlySpan<TilePlacement> placed, in PixelRect src, in PixelRect region)
    {
        var serials = new PoolSerials(_surfaces!);
        return GroupCacheKey.LeafBlur(in it, placed, in src, in region, ref serials);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Mix(ref ulong h, ulong v)
    {
        h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        h *= 0xFF51AFD7ED558CCDUL;
    }

    /// <summary>Draw a blur's crisp source into a scratch covering <paramref name="region"/>, scissored to
    /// <paramref name="src"/>: the degraded chunks [<paramref name="chunkFirst"/>, +<paramref name="chunkCount"/>) or the
    /// retained <paramref name="placed"/> tiles (slice origin at window px (<paramref name="tx"/>, <paramref name="ty"/>)).
    /// Returns the scratch (−1 when none was free), left in PIXEL_SHADER_RESOURCE.</summary>
    private int AssembleBlurSource(in CompositeItem it, in PixelRect src, in PixelRect region, int chunkFirst, int chunkCount,
        ReadOnlySpan<TilePlacement> placed, int tx, int ty)
    {
        int w = region.Right - region.Left, h = region.Bottom - region.Top;
        ulong fence = _fenceValue + 1;
        int s = _surfaces!.AcquireScratch(w, h, fence);
        if (s < 0) return -1;
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        BeginPass(_surfaces.ScratchRtv(s), w, h, PassLoad.Clear);
        BindCompositor(_surfaces.ScratchW(s), _surfaces.ScratchH(s));
        _compositor!.Scissor(_cmdList, src.Left - region.Left, src.Top - region.Top, src.Right - region.Left, src.Bottom - region.Top);
        for (int k = chunkFirst; k < chunkFirst + chunkCount; k++)
        {
            PixelRect c = _chunks[k].Rect;
            _compositor.Begin(c.Left - region.Left, c.Top - region.Top, c.Right - region.Left, c.Bottom - region.Top);
            _compositor.Draw(_cmdList, SliceCompositor.Pso.Load, _surfaces.ScratchSrv(_chunks[k].Surface));
        }
        for (int p = 0; p < placed.Length; p++)
        {
            if (!_surfaces.TouchTile(placed[p].Surface, fence)) { _frameLostPlacements++; continue; }
            float x0 = tx + placed[p].Key.Tx * TileGrid.W - region.Left, y0 = ty + placed[p].Key.Ty * TileGrid.H - region.Top;
            _compositor.Begin(x0, y0, x0 + placed[p].W, y0 + placed[p].H);
            _compositor.Draw(_cmdList, SliceCompositor.Pso.Load, _surfaces.TileSrv(placed[p].Surface));
        }
        EndPassIfOpen();
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        return s;
    }

    private void FinishBlur(int i, int source, in PixelRect region, float sigma)
    {
        if (source < 0) return;
        int w = region.Right - region.Left, h = region.Bottom - region.Top;
        int result = BlurSurface(source, w, h, sigma, out int down);
        if (result != source) _surfaces!.ReleaseScratch(source);
        _itemSurface[i] = result; _itemDown[i] = down; _itemRegion[i] = region;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>A self-blur's two window-px rects, shared by the tile and the degraded path so both blur the same pixels:
    /// <paramref name="src"/> = the content the blur reads (its recorded source clip — the visible output grown by the
    /// kernel's reach — else its clip ∩ the window), <paramref name="region"/> = the scratch it blurs in (the source, or
    /// the clip grown by the reach), its top-left floored onto the slice's origin grid.</summary>
    private void BlurRegions(in CompositeItem it, out PixelRect src, out PixelRect region)
        => GroupCacheKey.BlurRegions(in it, (int)_w, (int)_h, out src, out region);

    /// <summary>The retained-surface byte cap this turn: <c>TileBudget.RetainedShare</c> of the window's tile budget.</summary>
    private long RetainedCap() => TileBudget.RetainedBytesCap(TileBudget.Current((int)_w, (int)_h));

    /// <summary>The surface pool's tile serials, for <see cref="GroupCacheKey"/>.</summary>
    private readonly struct PoolSerials : ITileSerials
    {
        private readonly SurfacePool _pool;
        public PoolSerials(SurfacePool pool) => _pool = pool;
        public uint Serial(int surface) => _pool.TileSerial(surface);
    }

    /// <summary>A GROUP (a layer slice with child slices): its enclosed items composited into one surface covering its
    /// placed footprint (its clip ∩ the window when the footprint is larger than a tile — <see cref="GroupCacheKey.Region"/>),
    /// then blurred when the layer self-blurs. The result is RETAINED under its content key (<see cref="GroupCacheKey"/>): a
    /// turn whose enclosed items, tiles and relative placement are unchanged — a page scroll moving the group rigidly —
    /// re-draws it instead of re-rendering it (gpu-renderer.md §13.1e).</summary>
    private void PrepareGroup(in CompositeFrame frame, int i)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        int halo = it.BlurSigma > 0f ? SelfBlurRegion.TapRadius(it.BlurSigma) : 0;
        PixelRect region = GroupCacheKey.Region(in it, halo, (int)_w, (int)_h);
        if (region.Right <= region.Left || region.Bottom <= region.Top) return;
        int w = region.Right - region.Left, h = region.Bottom - region.Top;
        ulong fence = _fenceValue + 1;
        var serials = new PoolSerials(_surfaces!);
        bool cacheable = (_frameKnockouts & GpuKnockouts.ForceFullDirect) == 0;
        ulong key = cacheable ? GroupCacheKey.Compute(in frame, i, in region, ref serials, _itemKey, out cacheable) : 0UL;
        if (cacheable)
        {
            _itemKey[i] = key;
            int hit = _surfaces!.FindRetained(key, fence, out int hitDown);
            if (hit >= 0)
            {
                _offGroupHits++;
                _itemSurface[i] = hit; _itemDown[i] = hitDown; _itemRegion[i] = region;
                return;
            }
        }
        // An unblurred group whose key missed only because something inside it changed is REPAIRED in place over the
        // pixels that changed (GroupDelta) — bit-identical to the full render below, at the cost of the changed area.
        GroupMemo? memo = null;
        ulong shape = 0;
        int entryCount = 0;
        bool repairable = cacheable && it.BlurSigma <= 0f;
        if (repairable)
        {
            ulong fresh = 0xF2E5_0000_0000_0000UL ^ (ulong)(uint)_compositeTurn * 0x9E3779B97F4A7C15UL;
            shape = GroupDelta.Build(in frame, i, in region, ref serials, _itemKey, _itemRegion, _itemSurface, fresh, ref _groupEntries, out entryCount);
            memo = FindGroupMemo(shape);
            if (memo is not null && RepairGroup(in frame, i, in region, key, memo, entryCount)) return;
        }
        int s = _surfaces!.AcquireScratch(w, h, fence);
        if (s < 0) return;
        _groupRenders++;
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        BeginPass(_surfaces.ScratchRtv(s), w, h, PassLoad.Clear);
        BindCompositor(_surfaces.ScratchW(s), _surfaces.ScratchH(s));
        int end = Math.Min(frame.Items.Length, i + 1 + it.GroupCount);
        DrawRange(in frame, i + 1, end, region.Left, region.Top, w, h, -1);
        EndPassIfOpen();
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        int result = s, down = 1;
        if (it.BlurSigma > 0f)
        {
            result = BlurSurface(s, w, h, it.BlurSigma, out down);
            if (result != s) _surfaces.ReleaseScratch(s);
        }
        _itemSurface[i] = result; _itemDown[i] = down; _itemRegion[i] = region;
        if (cacheable && result >= 0) _surfaces.Retain(result, key, down, RetainedCap());
        if (repairable && result >= 0) RememberGroup(memo ?? NewGroupMemo(shape), key, entryCount);
    }

    // ── the group repair (GroupDelta) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a retained unblurred group surface was last rendered from: its shape, the content key it is retained
    /// under, and its entries.</summary>
    private sealed class GroupMemo
    {
        public ulong Shape, Key;
        public GroupEntry[] Entries = new GroupEntry[16];
        public int Count;
        public int Turn;
    }

    private readonly List<GroupMemo> _groupMemos = new(MaxGroupMemos);
    private GroupEntry[] _groupEntries = new GroupEntry[16];
    private readonly PixelRect[] _groupDirty = new PixelRect[GroupDelta.MaxDirtyRects];
    private PixelRect _drawClip;
    private bool _drawClipOn;
    private int _groupRepairs;

    /// <summary>Unblurred group surfaces the last composite repaired in place (counted in the renders too).</summary>
    public int LastGroupRepairs => _groupRepairs;

    private GroupMemo? FindGroupMemo(ulong shape)
    {
        for (int k = 0; k < _groupMemos.Count; k++) if (_groupMemos[k].Shape == shape) return _groupMemos[k];
        return null;
    }

    /// <summary>At most this many group memos are kept; a new shape recycles the least recently used one (a hand-off whose
    /// lines move mints a new shape every turn — recycling keeps that allocation-free).</summary>
    private const int MaxGroupMemos = 8;

    /// <summary>A memo for a shape seen for the first time: a fresh one while fewer than <see cref="MaxGroupMemos"/> exist,
    /// else the least recently used one, recycled.</summary>
    private GroupMemo NewGroupMemo(ulong shape)
    {
        GroupMemo? memo = null;
        if (_groupMemos.Count >= MaxGroupMemos)
        {
            memo = _groupMemos[0];
            for (int k = 1; k < _groupMemos.Count; k++) if (_groupMemos[k].Turn < memo.Turn) memo = _groupMemos[k];
        }
        else _groupMemos.Add(memo = new GroupMemo());
        memo.Shape = shape;
        memo.Count = 0;
        return memo;
    }

    /// <summary><paramref name="memo"/> now describes the surface retained under <paramref name="key"/>: this turn's
    /// <paramref name="count"/> entries (in <see cref="_groupEntries"/>; the buffers swap, so neither allocates).</summary>
    private void RememberGroup(GroupMemo memo, ulong key, int count)
    {
        memo.Key = key;
        memo.Turn = _compositeTurn;
        (memo.Entries, _groupEntries) = (_groupEntries, memo.Entries);
        memo.Count = count;
    }

    /// <summary>Repair the surface <paramref name="memo"/> describes into this turn's content (<paramref name="key"/>): clear
    /// each rect whose entries changed and redraw every enclosed item scissored to it. False (nothing touched) when the
    /// entries cannot be compared, too much changed, or the surface is gone or already drawn this turn.</summary>
    private bool RepairGroup(in CompositeFrame frame, int i, in PixelRect region, ulong key, GroupMemo memo, int count)
    {
        int w = region.Right - region.Left, h = region.Bottom - region.Top;
        int n = GroupDelta.Diff(memo.Entries.AsSpan(0, memo.Count), _groupEntries.AsSpan(0, count), (long)w * h, _groupDirty);
        if (n < 0) return false;
        int s = _surfaces!.FindRetainedForRepair(memo.Key, _fenceValue + 1);
        if (s < 0) return false;
        _groupRenders++;
        _groupRepairs++;
        if (n > 0)
        {
            ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
            BeginPass(_surfaces.ScratchRtv(s), w, h, PassLoad.Preserve);
            BindCompositor(_surfaces.ScratchW(s), _surfaces.ScratchH(s));
            int end = Math.Min(frame.Items.Length, i + 1 + frame.Items[i].GroupCount);
            for (int k = 0; k < n; k++)
            {
                PixelRect d = _groupDirty[k];
                // what the full render's CLEAR load op writes, inside this rect only
                _compositor!.Scissor(_cmdList, d.Left, d.Top, d.Right, d.Bottom);
                _compositor.Begin(d.Left, d.Top, d.Right, d.Bottom);
                _compositor.Color(new ColorF(0f, 0f, 0f, 0f));
                _compositor.Draw(_cmdList, SliceCompositor.Pso.FillCopy, default);
                _drawClip = new PixelRect(d.Left + region.Left, d.Top + region.Top, d.Right + region.Left, d.Bottom + region.Top);
                _drawClipOn = true;
                DrawRange(in frame, i + 1, end, region.Left, region.Top, w, h, -1);
                _drawClipOn = false;
            }
            EndPassIfOpen();
            ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        }
        _itemSurface[i] = s; _itemDown[i] = 1; _itemRegion[i] = region;
        _surfaces.Retain(s, key, 1, RetainedCap());
        RememberGroup(memo, key, count);
        return true;
    }

    /// <summary>An in-app ACRYLIC backdrop (§A.5): a mini-composite of everything painted before the surface — the clear
    /// colour and every earlier item — under its rect grown by the chain's reach, then the dual-Kawase down/up chain
    /// (<see cref="AcrylicKawaseMath"/>). No back-buffer read: the source is the tiles themselves.</summary>
    private void PrepareBackdrop(in CompositeFrame frame, int i)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        if (it.RoundClip.W <= 0f || it.RoundClip.H <= 0f) return;
        AcrylicKawaseMath.SelectChain(it.Acrylic.BlurSigma, _frameScale, out int iters, out float offset);
        int pad = AcrylicKawaseMath.PadPx(iters, offset);
        int l = Math.Max(0, (int)MathF.Floor(it.RoundClip.X) - pad), t = Math.Max(0, (int)MathF.Floor(it.RoundClip.Y) - pad);
        int r = Math.Min((int)_w, (int)MathF.Ceiling(it.RoundClip.Right) + pad), b = Math.Min((int)_h, (int)MathF.Ceiling(it.RoundClip.Bottom) + pad);
        if (r <= l || b <= t) return;
        int w = r - l, h = b - t;
        ulong fence = _fenceValue + 1;
        // The frosted backdrop is retained keyed by everything beneath it (§A.5 — composite-only turns keep it): the
        // items before it (kind, placement, alpha, clip, feather, every tile surface + raster serial), the clear colour,
        // the region and the chain. A turn that moved or re-rastered nothing beneath re-uses it.
        // An opaque item below covering the whole region: the mini-composite starts at it, with no clear (D3D12Device.Occlusion.cs)
        int cover = CoverFrom(i, new PixelRect(l, t, r, b));
        int from = Math.Max(cover, 0);
        ulong key = BackdropKey(in frame, i, from, l, t, r, b, iters, offset, out bool cacheable);
        _itemKey[i] = cacheable ? key : 0UL;   // 0 = re-blurred from scratch: the partial-present diff treats it as changed
        if (cacheable)
        {
            int hit = _surfaces!.FindRetained(key, fence, out _);
            if (hit >= 0)
            {
                _frameBlurCacheHits++; _offBackdropHits++;
                _itemSurface[i] = hit; _itemDown[i] = 1; _itemRegion[i] = new PixelRect(l, t, r, b);
                return;
            }
        }
        Span<int> lv = stackalloc int[AcrylicKawaseMath.MaxIterations + 1];
        lv[0] = _surfaces!.AcquireScratch(w, h, fence);
        if (lv[0] < 0) return;
        ScratchBarrier(lv[0], D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        BeginPass(_surfaces.ScratchRtv(lv[0]), w, h, cover >= 0 ? PassLoad.Discard : PassLoad.Clear);
        BindCompositor(_surfaces.ScratchW(lv[0]), _surfaces.ScratchH(lv[0]));
        _compositor!.Scissor(_cmdList, 0, 0, w, h);
        _compositor.Begin(0f, 0f, w, h);
        _compositor.Color(frame.Info.Clear);
        if (cover < 0) _compositor.Draw(_cmdList, SliceCompositor.Pso.FillCopy, default);   // else the occluder writes every pixel
        DrawRange(in frame, from, i, l, t, w, h, i);
        EndPassIfOpen();
        ScratchBarrier(lv[0], D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

        int levels = 0;
        for (int k = 1; k <= iters; k++)
        {
            lv[k] = _surfaces.AcquireScratch(AcrylicKawaseMath.LevelDim(w, k), AcrylicKawaseMath.LevelDim(h, k), fence);
            if (lv[k] < 0) break;
            levels = k;
        }
        for (int k = 1; k <= levels; k++) KawasePass(lv[k - 1], lv[k], w, h, k - 1, k, offset, down: true);
        for (int k = levels; k >= 1; k--) KawasePass(lv[k], lv[k - 1], w, h, k, k - 1, offset, down: false);
        for (int k = 1; k <= levels; k++) _surfaces.ReleaseScratch(lv[k]);
        _itemSurface[i] = lv[0]; _itemDown[i] = 1; _itemRegion[i] = new PixelRect(l, t, r, b);
        if (cacheable) _surfaces.Retain(lv[0], key, 1, RetainedCap());
    }

    /// <summary>The content key of backdrop item <paramref name="i"/>: everything the mini-composite of items [0, i) would
    /// draw into (<paramref name="l"/>, <paramref name="t"/>)-(<paramref name="r"/>, <paramref name="b"/>). Not cacheable
    /// when something beneath is redrawn from scratch every turn (a degraded segment, an unkeyed offscreen surface).
    /// <para>SCOPED to the region: the mini-composite is scissored to it (<see cref="DrawRange"/> into a w×h scratch), so
    /// an item whose clip, tile placements or prepared surface lie wholly outside it paints none of its pixels and is
    /// left out of the key — a visualizer animating across the window no longer re-blurs a panel it never touches, and a
    /// degraded segment elsewhere no longer makes every backdrop uncacheable. Two shapes can carry pixels from OUTSIDE the
    /// region into it and are keyed unscoped: an earlier backdrop overlapping the region (its blur sampled its own region),
    /// and a group overlapping it (its surface may be blurred over its whole footprint).</para></summary>
    private ulong BackdropKey(in CompositeFrame frame, int i, int from, int l, int t, int r, int b, int iters, float offset, out bool cacheable)
    {
        cacheable = true;
        if ((_frameKnockouts & GpuKnockouts.ForceFullDirect) != 0) { cacheable = false; return 0; }
        ulong h = 0xBACD_0000_0000_0001UL;
        Mix(ref h, (ulong)(uint)l << 32 | (uint)t); Mix(ref h, (ulong)(uint)r << 32 | (uint)b);
        Mix(ref h, (ulong)(uint)iters << 32 | BitConverter.SingleToUInt32Bits(offset));
        Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(frame.Info.Clear.R) << 32 | BitConverter.SingleToUInt32Bits(frame.Info.Clear.G));
        Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(frame.Info.Clear.B) << 32 | BitConverter.SingleToUInt32Bits(frame.Info.Clear.A));
        Mix(ref h, 0xF20E_0000_0000_0000UL | (uint)from);   // the items before an opaque cover of the region draw nothing there
        ReadOnlySpan<CompositeItem> items = frame.Items;
        var region = new PixelRect(l, t, r, b);
        bool scoped = true;
        for (int k = from; k < i && k < items.Length; k++)
            if (items[k].Kind == CompositeKind.Backdrop && Overlaps(in _itemRegion[k], in region)) { scoped = false; break; }
        int unscopedEnd = -1;   // items [.., unscopedEnd) sit inside a group that overlaps the region: keyed whole
        for (int k = from; k < i && k < items.Length; k++)
        {
            ref readonly CompositeItem it = ref items[k];
            if (Hidden(k, i))
            {
                // hidden under an opaque item below the backdrop: the mini-composite never draws it
                if (it.Kind == CompositeKind.Group) k = Math.Min(items.Length, k + 1 + it.GroupCount) - 1;
                continue;
            }
            bool scope = scoped && k >= unscopedEnd;
            if (scope && !ItemMayPaint(in frame, k, in region))
            {
                if (it.Kind == CompositeKind.Group) k = Math.Min(items.Length, k + 1 + it.GroupCount) - 1;
                continue;
            }
            if (it.Kind == CompositeKind.Direct)
            {
                // A low-resolution boundary's surface is content-keyed (PrepareLowRes): the backdrop keys on that. Any
                // other direct segment is re-drawn from scratch every turn — nothing beneath it can be cached.
                if (it.LowResDown <= 1 || _itemKey[k] == 0) { cacheable = false; return 0; }
                Mix(ref h, _itemKey[k]);
            }
            if (it.Kind == CompositeKind.Group) unscopedEnd = Math.Max(unscopedEnd, k + 1 + it.GroupCount);
            Mix(ref h, (ulong)(uint)it.Kind << 32 | (uint)it.SliceId);
            Mix(ref h, (ulong)(uint)(int)it.Transform.Dx << 32 | (uint)(int)it.Transform.Dy);
            Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.Alpha) << 32 | BitConverter.SingleToUInt32Bits(it.BlurSigma));
            Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.Clip.X) << 32 | BitConverter.SingleToUInt32Bits(it.Clip.Y));
            Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.Clip.W) << 32 | BitConverter.SingleToUInt32Bits(it.Clip.H));
            Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.RoundClip.X) << 32 | BitConverter.SingleToUInt32Bits(it.RoundClip.Y));
            Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.RoundClip.W) << 32 | BitConverter.SingleToUInt32Bits(it.RoundClip.H));
            Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.Feather.Rect.X) << 32 | BitConverter.SingleToUInt32Bits(it.Feather.Rect.Y));
            Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.Feather.Rect.W) << 32 | BitConverter.SingleToUInt32Bits(it.Feather.Rect.H));
            Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.Feather2.Rect.X) << 32 | BitConverter.SingleToUInt32Bits(it.Feather2.Rect.Y));
            Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.Feather2.Rect.W) << 32 | BitConverter.SingleToUInt32Bits(it.Feather2.Rect.H));
            if (it.Kind is CompositeKind.Tiles or CompositeKind.Region)
            {
                ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
                bool cull = scope && it.BlurSigma <= 0f;   // a blurred leaf samples its tiles past their own rects
                for (int p = 0; p < placed.Length; p++)
                {
                    if (cull && !Overlaps(PlacementRect(in it, in placed[p]), in region)) continue;
                    Mix(ref h, (ulong)(ushort)placed[p].Key.Tx << 48 | (ulong)(ushort)placed[p].Key.Ty << 32 | (uint)placed[p].Surface);
                    Mix(ref h, _surfaces!.TileSerial(placed[p].Surface));
                }
            }
        }
        return h;
    }

    /// <summary>Can item <paramref name="k"/> put any pixel inside <paramref name="region"/> when <see cref="DrawRange"/>
    /// composites it? Conservative: true unless its scissor, every tile placement, or every prepared surface/chunk of it
    /// lies wholly outside. Items before the backdrop are already prepared (PrepareRange runs in painter order).</summary>
    private bool ItemMayPaint(in CompositeFrame frame, int k, in PixelRect region)
    {
        ref readonly CompositeItem it = ref frame.Items[k];
        if (!IsUnbounded(it.Clip) && !Overlaps(it.Clip, in region)) return false;
        switch (it.Kind)
        {
            case CompositeKind.Direct:
            {
                if (_itemSurface[k] >= 0) return Overlaps(in _itemRegion[k], in region);
                int end = _itemChunkStart[k] + _itemChunkCount[k];
                for (int c = _itemChunkStart[k]; c < end; c++)
                    if (Overlaps(_chunks[c].Rect, in region)) return true;
                return false;   // prepared nothing (or nothing here): it draws nothing into the region
            }
            case CompositeKind.Tiles:
            case CompositeKind.Region:
            {
                if (it.BlurSigma > 0f) return _itemSurface[k] < 0 || Overlaps(in _itemRegion[k], in region);
                ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
                for (int p = 0; p < placed.Length; p++)
                    if (Overlaps(PlacementRect(in it, in placed[p]), in region)) return true;
                return false;
            }
            case CompositeKind.Group:
                return it.Footprint.W <= 0f || it.Footprint.H <= 0f || Overlaps(it.Footprint, in region);
            case CompositeKind.Backdrop:
                return _itemSurface[k] < 0 || Overlaps(in _itemRegion[k], in region);
            default:
                return true;
        }
    }

    /// <summary>A tile placement's window-px rect, exactly as <see cref="DrawRange"/> places its quad: the part of the tile
    /// its ops paint (<see cref="TilePlacement.Px0"/>..).</summary>
    private static RectF PlacementRect(in CompositeItem it, in TilePlacement p)
        => new(it.Transform.Dx + p.Key.Tx * TileGrid.W + p.Px0, it.Transform.Dy + p.Key.Ty * TileGrid.H + p.Py0,
               Math.Max(0, p.Px1 - p.Px0), Math.Max(0, p.Py1 - p.Py0));

    private static bool Overlaps(in PixelRect a, in PixelRect b)
        => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>Float rect vs pixel rect, rounded OUT (a sub-pixel sliver still counts).</summary>
    private static bool Overlaps(in RectF a, in PixelRect b)
        => MathF.Floor(a.X) < b.Right && b.Left < MathF.Ceiling(a.X + a.W) && MathF.Floor(a.Y) < b.Bottom && b.Top < MathF.Ceiling(a.Y + a.H);

    private void KawasePass(int src, int dst, int w, int h, int srcLevel, int dstLevel, float offset, bool down)
    {
        int suw = AcrylicKawaseMath.LevelDim(w, srcLevel), suh = AcrylicKawaseMath.LevelDim(h, srcLevel);
        int duw = AcrylicKawaseMath.LevelDim(w, dstLevel), duh = AcrylicKawaseMath.LevelDim(h, dstLevel);
        ScratchBarrier(dst, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        BeginPass(_surfaces!.ScratchRtv(dst), _surfaces.ScratchW(dst), _surfaces.ScratchH(dst), PassLoad.Discard);
        BindCompositor(_surfaces.ScratchW(dst), _surfaces.ScratchH(dst));
        _compositor!.Scissor(_cmdList, 0, 0, duw, duh);
        _compositor.Begin(0f, 0f, duw, duh);
        var k = _compositor.K;
        float stx = 1f / _surfaces.ScratchW(src), sty = 1f / _surfaces.ScratchH(src);
        k[8] = stx; k[9] = sty; k[10] = offset; k[11] = 0f;
        k[12] = stx * suw / duw; k[13] = sty * suh / duh;
        k[14] = (suw - 0.5f) * stx; k[15] = (suh - 0.5f) * sty;
        _compositor.Draw(_cmdList, down ? SliceCompositor.Pso.KawaseDown : SliceCompositor.Pso.KawaseUp, _surfaces.ScratchSrv(src));
        EndPassIfOpen();
        ScratchBarrier(dst, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
    }

    // ── 3. drawing items ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Draw items [<paramref name="a"/>, <paramref name="b"/>) into the target bound by the open pass, whose pixel
    /// 0 is window px (<paramref name="ox"/>, <paramref name="oy"/>) and whose extent is <paramref name="tw"/>×
    /// <paramref name="th"/>. The compositor is bound for it. <paramref name="stopBackdrop"/> ≥ 0 = a backdrop's
    /// mini-composite: a group enclosing that backdrop is not drawn (it is not prepared yet).</summary>
    private void DrawRange(in CompositeFrame frame, int a, int b, int ox, int oy, int tw, int th, int stopBackdrop)
    {
        ReadOnlySpan<CompositeItem> items = frame.Items;
        bool feathers = (_frameKnockouts & GpuKnockouts.EdgeFadesOff) == 0;
        bool allDirect = (_frameKnockouts & GpuKnockouts.ForceFullDirect) != 0;
        ulong fence = _fenceValue + 1;
        for (int i = a; i < b && i < items.Length; i++)
        {
            ref readonly CompositeItem it = ref items[i];
            if (_frameClipOn && (uint)i < (uint)_itemFoot.Length && !Overlaps(in _itemFoot[i], in _frameClip))
            {
                // a partial composite: this item paints nothing inside the current repaint rect
                if (it.Kind == CompositeKind.Group) i = Math.Min(items.Length, i + 1 + it.GroupCount) - 1;
                continue;
            }
            if (Hidden(i, b))
            {
                // an opaque item this composite draws later covers all its pixels (D3D12Device.Occlusion.cs)
                if (it.Kind == CompositeKind.Group) i = Math.Min(items.Length, i + 1 + it.GroupCount) - 1;
                continue;
            }
            switch (it.Kind)
            {
                case CompositeKind.Group:
                {
                    int end = Math.Min(items.Length, i + 1 + it.GroupCount);
                    if (stopBackdrop >= 0 && stopBackdrop > i && stopBackdrop < end) break;   // enclosing: skip (see summary)
                    DrawSurfaceItem(in it, i, ox, oy, feathers);
                    i = end - 1;
                    break;
                }
                case CompositeKind.Backdrop:
                    DrawBackdrop(in it, i, ox, oy);
                    break;
                case CompositeKind.EraseVideoHole:
                {
                    ItemScissor(in it, ox, oy);
                    _compositor!.Begin(it.Clip.X - ox, it.Clip.Y - oy, it.Clip.Right - ox, it.Clip.Bottom - oy);
                    _compositor.Color(new ColorF(0f, 0f, 0f, 1f));
                    _compositor.Alpha(it.Alpha);
                    if (it.RoundClip.W > 0f)
                    {
                        // F078: the hole's own rounded rect. Uniform radii take the shared rounded-clip path; a hole with
                        // different corners (a PiP rounded on one side) takes the per-corner one.
                        RectF holeRound = Offset(it.RoundClip, -ox, -oy);
                        if (it.ClipRadii.IsUniform) _compositor.RoundClip(holeRound, it.ClipRadii.TopLeft);
                        else _compositor.CornerClip(holeRound, it.ClipRadii);
                    }
                    _compositor.Draw(_cmdList, SliceCompositor.Pso.Erase, default);
                    break;
                }
                case CompositeKind.Direct:
                    DrawSurfaceItem(in it, i, ox, oy, feathers);
                    break;
                case CompositeKind.Tiles:
                case CompositeKind.Region:
                {
                    if (allDirect || it.BlurSigma > 0f) { DrawSurfaceItem(in it, i, ox, oy, feathers); break; }
                    ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
                    if (placed.IsEmpty) break;
                    ItemScissor(in it, ox, oy);
                    if (feathers && (!it.Feather.IsNone || !it.Feather2.IsNone)) _frameFeatherItems++;
                    for (int p = 0; p < placed.Length; p++)
                    {
                        if (!_surfaces!.TouchTile(placed[p].Surface, fence)) { _frameLostPlacements++; continue; }
                        // only the part of the tile its ops paint (TilePlacement.Paint*): the rest of the surface is transparent
                        ref readonly TilePlacement tp = ref placed[p];
                        int px0 = tp.Px0, py0 = tp.Py0, px1 = tp.Px1, py1 = tp.Py1;
                        if (px1 <= px0 || py1 <= py0) continue;
                        float x0 = it.Transform.Dx + tp.Key.Tx * TileGrid.W - ox + px0, y0 = it.Transform.Dy + tp.Key.Ty * TileGrid.H - oy + py0;
                        float x1 = x0 + (px1 - px0), y1 = y0 + (py1 - py0);
                        if (x0 >= tw || y0 >= th || x1 <= 0f || y1 <= 0f) continue;
                        DrawItemQuad(in it, x0, y0, x1, y1, ox, oy, feathers,
                            LoadPso(in it), _surfaces.TileSrv(tp.Surface),
                            srcOx: px0, srcOy: py0);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>The texel-load PSO an item composites with: copy, SCREEN (BoxEl.LayerBlend.Screen) or source-over.</summary>
    private static SliceCompositor.Pso LoadPso(in CompositeItem it) => it.BlendCopy switch
    {
        CompositeItem.BlendCopyWrite => SliceCompositor.Pso.LoadCopy,
        CompositeItem.BlendScreen => SliceCompositor.Pso.LoadScreen,
        _ => SliceCompositor.Pso.Load,
    };

    /// <summary>An item whose pixels were prepared offscreen (a degraded segment, a group, a self-blur): its surface at its
    /// region — a whole-pixel texel Load, or a bilinear sample of a downsampled blur.</summary>
    private void DrawSurfaceItem(in CompositeItem it, int i, int ox, int oy, bool feathers)
    {
        // a feathered item counts whichever way it composites (tiles, a group / blur surface, degraded chunks)
        if (feathers && (!it.Feather.IsNone || !it.Feather2.IsNone) && (_itemChunkCount[i] > 0 || _itemSurface[i] >= 0)) _frameFeatherItems++;
        if (_itemChunkCount[i] > 0)
        {
            // a degraded segment's transient chunks: whole-pixel texel loads, exactly like its tiles
            ItemScissor(in it, ox, oy);
            int end = _itemChunkStart[i] + _itemChunkCount[i];
            for (int k = _itemChunkStart[i]; k < end; k++)
            {
                PixelRect c = _chunks[k].Rect;
                DrawItemQuad(in it, c.Left - ox, c.Top - oy, c.Right - ox, c.Bottom - oy, ox, oy, feathers,
                    LoadPso(in it), _surfaces!.ScratchSrv(_chunks[k].Surface));
            }
            return;
        }
        int s = _itemSurface[i];
        if (s < 0) return;
        PixelRect region = _itemRegion[i];
        ItemScissor(in it, ox, oy);
        int down = _itemDown[i];
        if (down <= 1)
            DrawItemQuad(in it, region.Left - ox, region.Top - oy, region.Right - ox, region.Bottom - oy, ox, oy, feathers,
                it.BlendCopy == CompositeItem.BlendScreen ? SliceCompositor.Pso.LoadScreen : SliceCompositor.Pso.Load, _surfaces!.ScratchSrv(s));
        else
        {
            // A low-resolution boundary's surface is pooled (often larger than the region): its far edges clamp to the last
            // WRITTEN texel, as the sampler's clamp already does at uv 0 — the upsampled layer ends where its region ends on
            // all four sides, instead of fading into the cleared texels past its right and bottom edges.
            float clampU = 0f, clampV = 0f;
            if (it.LowResDown > 1)
            {
                clampU = ((region.Right - region.Left) / down - 0.5f) / _surfaces!.ScratchW(s);
                clampV = ((region.Bottom - region.Top) / down - 0.5f) / _surfaces.ScratchH(s);
            }
            DrawItemQuad(in it, region.Left - ox, region.Top - oy, region.Right - ox, region.Bottom - oy, ox, oy, feathers,
                it.BlendCopy == CompositeItem.BlendScreen ? SliceCompositor.Pso.SampleScreen : SliceCompositor.Pso.Sample, _surfaces!.ScratchSrv(s), sample: true,
                1f / (down * _surfaces.ScratchW(s)), 1f / (down * _surfaces.ScratchH(s)), clampU, clampV);
        }
    }

    /// <summary>One item quad (target px): a texel Load whose source origin is the quad's top-left, or — with
    /// <paramref name="sample"/> — a bilinear sample mapped from that top-left at (<paramref name="sampleSx"/>,
    /// <paramref name="sampleSy"/>) per px. A feathered item's quad is split by its feathers' unit interior
    /// (<see cref="FeatherQuadSplit"/>): the interior piece draws WITHOUT the feather (it is exactly 1 there — the same
    /// pixels), only the strips around it evaluate it.</summary>
    private void DrawItemQuad(in CompositeItem it, float x0, float y0, float x1, float y1, int ox, int oy, bool feathers,
        SliceCompositor.Pso pso, D3D12_GPU_DESCRIPTOR_HANDLE srv, bool sample = false, float sampleSx = 0f, float sampleSy = 0f,
        float clampU = 0f, float clampV = 0f, int srcOx = 0, int srcOy = 0)
    {
        bool f1 = feathers && !it.Feather.IsNone, f2 = feathers && !it.Feather2.IsNone;
        Span<FeatherPiece> pieces = stackalloc FeatherPiece[FeatherQuadSplit.MaxPieces];
        int n;
        if (f1 || f2)
        {
            float il = -EdgeFeatherMask.Unbounded, it0 = -EdgeFeatherMask.Unbounded, ir = EdgeFeatherMask.Unbounded, ib = EdgeFeatherMask.Unbounded;
            if (f1)
            {
                EdgeFeatherMask.UnitInterior(it.Feather, out float l, out float t, out float r, out float b);
                il = MathF.Max(il, l - ox); it0 = MathF.Max(it0, t - oy); ir = MathF.Min(ir, r - ox); ib = MathF.Min(ib, b - oy);
            }
            if (f2)
            {
                EdgeFeatherMask.UnitInterior(it.Feather2, out float l, out float t, out float r, out float b);
                il = MathF.Max(il, l - ox); it0 = MathF.Max(it0, t - oy); ir = MathF.Min(ir, r - ox); ib = MathF.Min(ib, b - oy);
            }
            n = FeatherQuadSplit.Split(x0, y0, x1, y1, il, it0, ir, ib, pieces);
        }
        else
        {
            pieces[0] = new FeatherPiece(x0, y0, x1, y1, false);
            n = 1;
        }
        for (int k = 0; k < n; k++)
        {
            ref readonly FeatherPiece p = ref pieces[k];
            _compositor!.Begin(p.X0, p.Y0, p.X1, p.Y1);
            if (sample) _compositor.SampleMap(x0, y0, sampleSx, sampleSy);
            if (clampU > 0f) _compositor.SampleClamp(clampU, clampV);
            else _compositor.SourceOrigin(p.X0 - x0 + srcOx, p.Y0 - y0 + srcOy);
            ItemParams(in it, ox, oy, feathers && p.Feathered);
            _compositor.Draw(_cmdList, pso, srv);
            if (p.Feathered)
            {
                float w = MathF.Min(p.X1, _itemScissor.Right) - MathF.Max(p.X0, _itemScissor.Left);
                float h = MathF.Min(p.Y1, _itemScissor.Bottom) - MathF.Max(p.Y0, _itemScissor.Top);
                if (w > 0f && h > 0f) _frameFeatherPx += (long)(w * h);
            }
        }
    }

    private void DrawBackdrop(in CompositeItem it, int i, int ox, int oy)
    {
        int s = _itemSurface[i];
        if (s < 0) return;
        PixelRect region = _itemRegion[i];
        ItemScissor(in it, ox, oy);
        RectF rect = Offset(it.RoundClip, -ox, -oy);
        _compositor!.Begin(rect.X, rect.Y, rect.Right, rect.Bottom);
        var k = _compositor.K;
        float tw = _surfaces!.ScratchW(s), th = _surfaces.ScratchH(s);
        k[8] = region.Left - ox; k[9] = region.Top - oy; k[10] = 1f / tw; k[11] = 1f / th;
        k[12] = it.Alpha; k[13] = it.Acrylic.FeatherFrac; k[14] = it.ClipRadii.TopLeft; k[15] = 0f;
        k[16] = rect.X; k[17] = rect.Y; k[18] = rect.Right; k[19] = rect.Bottom;
        k[20] = it.Acrylic.Tint.R; k[21] = it.Acrylic.Tint.G; k[22] = it.Acrylic.Tint.B; k[23] = it.Acrylic.Tint.A;
        k[24] = it.Acrylic.Fallback.R; k[25] = it.Acrylic.Fallback.G; k[26] = it.Acrylic.Fallback.B; k[27] = it.Acrylic.Fallback.A;
        k[28] = it.Acrylic.TintOpacity; k[29] = it.Acrylic.LuminosityOpacity; k[30] = it.Acrylic.NoiseOpacity; k[31] = 0f;
        int w = region.Right - region.Left, h = region.Bottom - region.Top;
        k[32] = (w - 0.5f) / tw; k[33] = (h - 0.5f) / th; k[34] = 0.5f / tw; k[35] = 0.5f / th;
        _compositor.Draw(_cmdList, SliceCompositor.Pso.Acrylic, _surfaces.ScratchSrv(s));
    }

    /// <summary>The item's composite parameters onto the current quad: alpha, the analytic feather(s) — its own and a
    /// distributed ancestor fade's, multiplied — and the rounded clip, moved into the target's space.</summary>
    private void ItemParams(in CompositeItem it, int ox, int oy, bool feathers)
    {
        _compositor!.Alpha(it.Alpha);
        if (feathers && !it.Feather.IsNone)
        {
            EdgeFeather moved = it.Feather with { Rect = Offset(it.Feather.Rect, -ox, -oy) };
            _compositor.Feather(in moved);
        }
        if (feathers && !it.Feather2.IsNone)
        {
            EdgeFeather moved2 = it.Feather2 with { Rect = Offset(it.Feather2.Rect, -ox, -oy) };
            _compositor.Feather2(in moved2);
        }
        if (it.RoundClip.W > 0f) _compositor.RoundClip(Offset(it.RoundClip, -ox, -oy), it.ClipRadii.TopLeft);
    }

    private void ItemScissor(in CompositeItem it, int ox, int oy)
    {
        int tw = _compositor!.TargetW, th = _compositor.TargetH;
        int l, t, r, b;
        if (IsUnbounded(it.Clip)) { l = 0; t = 0; r = tw; b = th; }
        else
        {
            l = (int)MathF.Floor(it.Clip.X) - ox; t = (int)MathF.Floor(it.Clip.Y) - oy;
            r = (int)MathF.Ceiling(it.Clip.Right) - ox; b = (int)MathF.Ceiling(it.Clip.Bottom) - oy;
        }
        if (_frameClipOn)
        {
            // a partial composite: nothing outside the current repaint rect may be touched (that pixel is the previous frame)
            l = Math.Max(l, _frameClip.Left - ox); t = Math.Max(t, _frameClip.Top - oy);
            r = Math.Min(r, _frameClip.Right - ox); b = Math.Min(b, _frameClip.Bottom - oy);
            if (r <= l || b <= t) { l = t = r = b = 0; }
        }
        if (_drawClipOn)
        {
            // a group repair: nothing outside the rect being repaired may be touched (that pixel is already current)
            l = Math.Max(l, _drawClip.Left - ox); t = Math.Max(t, _drawClip.Top - oy);
            r = Math.Min(r, _drawClip.Right - ox); b = Math.Min(b, _drawClip.Bottom - oy);
            if (r <= l || b <= t) { l = t = r = b = 0; }
        }
        _compositor.Scissor(_cmdList, l, t, r, b);
        _itemScissor = new PixelRect(Math.Max(0, l), Math.Max(0, t), Math.Min(tw, r), Math.Min(th, b));
    }

    /// <summary>The scissor the current item draws under (target px, clamped to the target) — what a quad really shades.</summary>
    private PixelRect _itemScissor;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RectF Offset(in RectF r, float dx, float dy) => new(r.X + dx, r.Y + dy, r.W, r.H);
}
