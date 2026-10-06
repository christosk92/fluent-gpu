using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Gen = FluentGpu.Interop.Generated;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Cumulative damage census of the primary composite (render thread writes, diagnostics read; totals since the
/// device was created — a reader diffs two snapshots). <see cref="RasterPx"/> = tile pixels the rasters wrote (a partial
/// raster counts its damage), <see cref="RasterWholePx"/> = what whole-tile rasters of the same tiles would have written,
/// <see cref="CompositePx"/> = back-buffer pixels the composite pass repainted (the window on a whole-frame turn),
/// <see cref="PresentPx"/> = pixels the Present1 dirty rects named (the window on a full present), <see cref="DirtyPx"/> =
/// this turn's own dirty set (what changed since the previous frame; the composite repaints more when the back buffer is
/// older — buffer age). <see cref="FullFrames"/> = composites that took the whole-frame route, <see cref="Validated"/> /
/// <see cref="Mismatches"/> = <c>--fg damage-validate</c> checks done / failed.</summary>
public readonly record struct DamageCensus(long Frames, long PartialRasters, long WholeRasters, long RasterPx, long RasterWholePx,
    long CompositePx, long PresentPx, long DirtyPx, long FullFrames, long Validated, long Mismatches);

// SUB-TILE DAMAGE, the backend's half (gpu-renderer.md §13.1l): a TileRaster the table planned PARTIAL keeps its surface
// (PRESERVE) and re-rasters only its damage — the rect is cleared to transparent (what a whole raster's CLEAR load
// writes), then the segment replays with every scissor cut to it (the decoder's chokepoint and an inline group's composite)
// and the decode-time cull narrowed to it. A surface whose texture was created this turn holds nothing to keep: it
// rasters whole. `--fg damage-validate` re-rasters each partial tile whole into a shadow scratch in the same submission,
// copies both to readback buffers and compares them byte for byte once the GPU passed the frame.
public sealed unsafe partial class D3D12Device
{
    // ── the replay clamp (replay px; ReplaySegment sets it for a partial raster) ──
    private bool _replayClampOn;
    private RECT _replayClamp;

    /// <summary><paramref name="sc"/> (replay px) cut to the partial raster's damage; unchanged outside a partial replay.</summary>
    private RECT ClampToReplay(RECT sc)
    {
        if (!_replayClampOn) return sc;
        sc.left = Math.Max(sc.left, _replayClamp.left); sc.top = Math.Max(sc.top, _replayClamp.top);
        sc.right = Math.Min(sc.right, _replayClamp.right); sc.bottom = Math.Min(sc.bottom, _replayClamp.bottom);
        if (sc.right < sc.left) sc.right = sc.left;
        if (sc.bottom < sc.top) sc.bottom = sc.top;
        return sc;
    }

    // ── image content under unchanged bytes ──
    // A DrawImage samples whatever pixels its id holds NOW (an LQIP preview, then the full-res art; a re-baked derivative):
    // the bytes, and so the table's diff, do not change when they do. Per tile surface the backend records the content
    // serial (ImageTextureStore.ContentSerial) of every image its rasters drew; a partial raster grows its damage over each
    // image of the tile whose serial moved since, so the image is re-drawn whole, exactly as a whole raster would.
    private (int Id, uint Serial)[][] _tileImg = [];
    private int[] _tileImgN = [];
    private int _imgRecSlot = -1;   // the tile surface whose raster records its image draws (-1 = none)

    private void BeginImageRecord(int slot, bool whole)
    {
        if (_tileImg.Length <= slot)
        {
            int n = Math.Max(slot + 1, _tileImg.Length * 2);
            int old = _tileImg.Length;
            Array.Resize(ref _tileImg, n); Array.Resize(ref _tileImgN, n);
            for (int i = old; i < n; i++) _tileImg[i] = new (int, uint)[8];
        }
        if (whole) _tileImgN[slot] = 0;
        _imgRecSlot = slot;
    }

    private void EndImageRecord() => _imgRecSlot = -1;

    /// <summary>The decoder drew image <paramref name="id"/> into the recording tile: remember the pixels it drew.</summary>
    private void NoteRasterImage(int id)
    {
        int slot = _imgRecSlot;
        uint serial = _imageTextures?.ContentSerial(id) ?? 0u;
        ref (int Id, uint Serial)[] list = ref _tileImg[slot];
        int n = _tileImgN[slot];
        for (int i = 0; i < n; i++) if (list[i].Id == id) { list[i].Serial = serial; return; }
        if (n == list.Length) Array.Resize(ref list, n * 2);
        list[n] = (id, serial);
        _tileImgN[slot] = n + 1;
    }

    /// <summary>The tile px of every image the segment draws into tile <paramref name="tr"/> whose pixels changed since the
    /// surface's rasters drew it (or that no raster of it recorded), unioned with <paramref name="damage"/>.</summary>
    private PixelRect ImageDamage(in CompositeFrame frame, in SliceRow row, in TileRaster tr, PixelRect damage)
    {
        if (_imageTextures is null) return damage;
        ReadOnlySpan<byte> stream = frame.StreamOf(in row);
        float s = _frameScale <= 0f ? 1f : _frameScale;
        float ox = tr.Key.Tx * (float)TileGrid.W + row.Frame.OriginX, oy = tr.Key.Ty * (float)TileGrid.H + row.Frame.OriginY;
        int slot = tr.Surface;
        int n = slot < _tileImgN.Length ? _tileImgN[slot] : 0;
        int pos = 0;
        while (pos + sizeof(int) <= stream.Length)
        {
            var op = (DrawOp)System.Runtime.InteropServices.MemoryMarshal.Read<int>(stream[pos..]);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > stream.Length) break;
            if (op == DrawOp.DrawImage)
            {
                ReadOnlySpan<byte> payload = stream.Slice(pos + sizeof(int), body);
                if (SliceOpBounds.TryGet(op, payload, out RectF b))
                {
                    var r = TileDamage.Round(new RectF(b.X * s - ox, b.Y * s - oy, b.W * s, b.H * s), tr.W, tr.H);
                    if (!r.IsEmpty)
                    {
                        int id = System.Runtime.InteropServices.MemoryMarshal.Read<DrawImageCmd>(payload).ImageId;
                        uint now = _imageTextures.ContentSerial(id);
                        bool same = false;
                        for (int i = 0; i < n; i++) if (_tileImg[slot][i].Id == id) { same = _tileImg[slot][i].Serial == now; break; }
                        if (!same) damage = TileDamage.Union(in damage, in r);
                    }
                }
            }
            pos += sizeof(int) + body;
        }
        return damage;
    }

    // ── census ──
    private long _dcFrames, _dcPartial, _dcWhole, _dcRasterPx, _dcRasterWholePx, _dcCompositePx, _dcPresentPx, _dcDirtyPx, _dcFull;
    private long _dcValidated, _dcMismatches;

    /// <summary>The cumulative damage census (see <see cref="DamageCensus"/>).</summary>
    public DamageCensus LastDamageCensus => new(Volatile.Read(ref _dcFrames), Volatile.Read(ref _dcPartial), Volatile.Read(ref _dcWhole),
        Volatile.Read(ref _dcRasterPx), Volatile.Read(ref _dcRasterWholePx), Volatile.Read(ref _dcCompositePx),
        Volatile.Read(ref _dcPresentPx), Volatile.Read(ref _dcDirtyPx), Volatile.Read(ref _dcFull),
        Volatile.Read(ref _dcValidated), Volatile.Read(ref _dcMismatches));

    private static long AreaOf(in PixelRect r) => r.IsEmpty ? 0L : (long)(r.Right - r.Left) * (r.Bottom - r.Top);

    private void NoteRasterCensus(bool partial, in PixelRect damage, int w, int h)
    {
        Volatile.Write(ref _dcRasterWholePx, _dcRasterWholePx + (long)w * h);
        if (partial) { Volatile.Write(ref _dcPartial, _dcPartial + 1); Volatile.Write(ref _dcRasterPx, _dcRasterPx + AreaOf(in damage)); }
        else { Volatile.Write(ref _dcWhole, _dcWhole + 1); Volatile.Write(ref _dcRasterPx, _dcRasterPx + (long)w * h); }
    }

    /// <summary>PpEndFrame's half: what this composite repainted and presented.</summary>
    private void NoteCompositeCensus(long compositePx, long presentPx, long dirtyPx, bool full)
    {
        Volatile.Write(ref _dcCompositePx, _dcCompositePx + compositePx);
        Volatile.Write(ref _dcPresentPx, _dcPresentPx + presentPx);
        Volatile.Write(ref _dcDirtyPx, _dcDirtyPx + dirtyPx);
        if (full) Volatile.Write(ref _dcFull, _dcFull + 1);
        Volatile.Write(ref _dcFrames, _dcFrames + 1);
    }

    /// <summary><c>--fg damage-log</c>: one line per scheduled raster.</summary>
    private void LogRaster(in TileRaster tr, in SliceRow row, bool partial, bool fresh, in PixelRect d)
    {
        var ci = CultureInfo.InvariantCulture;
        string grown = d.Equals(tr.Damage) ? "" : " (+images)";
        string dmg = partial
            ? $"partial [{d.Left},{d.Top} {d.Right - d.Left}x{d.Bottom - d.Top}] {100.0 * AreaOf(d) / Math.Max(1, tr.W * tr.H):0.0}%{grown}"
            : (tr.Partial && fresh ? "whole (fresh texture)" : tr.Partial ? "whole (images)" : "whole");
        Console.Error.WriteLine(string.Create(ci,
            $"[damage] turn={_compositeTurn} slice={tr.Key.SliceId} {row.Kind} node={row.NodeIndex} tile=({tr.Key.Tx},{tr.Key.Ty}) {tr.W}x{tr.H} reason={tr.Reason} order={tr.Order} origin=({row.Frame.OriginX},{row.Frame.OriginY}) res=({row.Frame.ResidualX:0.###},{row.Frame.ResidualY:0.###}) {dmg}"));
    }

    // ── --fg damage-validate ──
    private struct DamageCheck
    {
        public ID3D12Resource* A, B;     // readback: the partially rastered tile, the whole shadow raster
        public ulong Bytes, Fence;
        public uint Pitch;
        public int W, H, Turn;
        public TileKey Key;
        public PixelRect Damage;
        public bool Busy;
        public (DrawOp Op, PixelRect R, float A, float B)[] Ops;   // forensics: the segment's ops reaching the tile
        public int OpN;
    }
    private readonly DamageCheck[] _dmgChecks = new DamageCheck[16];
    private long _dcSkipped;

    /// <summary>Re-raster <paramref name="tr"/> (just partially rastered into its surface, which is still RENDER_TARGET) whole
    /// into a shadow scratch and queue a byte compare of the two. No pass is left open.</summary>
    private void QueueDamageCheck(in CompositeFrame frame, in SliceRow row, in TileRaster tr, PixelRect damage, int tileX, int tileY)
    {
        int slot = FreeCheckSlot();
        if (slot < 0) { PollDamageChecks(); slot = FreeCheckSlot(); }   // compare what the GPU has finished, then retry
        if (slot < 0) { _dcSkipped++; return; }
        ulong fence = _fenceValue + 1;
        int scratch = _surfaces!.AcquireScratch(tr.W, tr.H, fence);
        if (scratch < 0) { _dcSkipped++; return; }
        // An image whose side-queue pixels land mid-frame changes what its draws sample between the partial raster and the
        // shadow (ImageTextureStore polls its fences live): such a pair is not comparable, the check is skipped.
        if (!ImageDamage(in frame, in row, in tr, default).IsEmpty) { _surfaces.ReleaseScratch(scratch); _dcSkipped++; return; }
        int dropped = DroppedInstanceCount(), glyphDropped = _glyphs!.DroppedInstances;
        EndPassIfOpen();
        ScratchBarrier(scratch, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var srtv = _surfaces.ScratchRtv(scratch);
        // an explicit clear, then a PRESERVE pass: the same pattern as the present validator's shadow (a CLEAR-load pass
        // followed by a copy lost draws on the Adreno driver now and then)
        float* zero = stackalloc float[4];
        _cmdList->ClearRenderTargetView(srtv, zero, 0, null);
        BeginPass(srtv, tr.W, tr.H, PassLoad.Preserve);
        ReplaySegment(in frame, in row, -tileX, -tileY, tr.W, tr.H, srtv,
            new RectF(tr.Key.Tx * (float)TileGrid.W, tr.Key.Ty * (float)TileGrid.H, tr.W, tr.H));
        EndPassIfOpen();
        if (DroppedInstanceCount() != dropped || _glyphs.DroppedInstances != glyphDropped
            || !ImageDamage(in frame, in row, in tr, default).IsEmpty) { _surfaces.ReleaseScratch(scratch); _dcSkipped++; return; }

        ref DamageCheck c = ref _dmgChecks[slot];
        D3D12_RESOURCE_DESC desc = default;
        desc.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = (ulong)tr.W; desc.Height = (uint)tr.H; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp; uint rows; ulong rowBytes; ulong total;
        _device->GetCopyableFootprints(&desc, 0, 1, 0, &fp, &rows, &rowBytes, &total);
        if (c.A == null || c.Bytes < total)
        {
            ReleaseCheck(ref c);
            c.A = CreateReadback(total);
            c.B = CreateReadback(total);
            c.Bytes = total;
        }
        ID3D12Resource* tile = _surfaces.TileResource(tr.Surface);
        Barrier(tile, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        ScratchBarrier(scratch, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        CopyToReadback(tile, c.A, fp, tr.W, tr.H);
        CopyToReadback(_surfaces.ScratchResource(scratch), c.B, fp, tr.W, tr.H);
        Barrier(tile, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        _surfaces.ReleaseScratch(scratch);
        c.Pitch = fp.Footprint.RowPitch;
        c.W = tr.W; c.H = tr.H; c.Turn = _compositeTurn; c.Key = tr.Key; c.Damage = damage;
        c.Fence = fence;
        c.Busy = true;
        DvCaptureOps(ref c, in frame, in row, in tr);
        InvalidateCmdState();
    }

    /// <summary>Forensics: the ops of the segment whose footprint reaches the tile (tile px), kept with the check.</summary>
    private void DvCaptureOps(ref DamageCheck c, in CompositeFrame frame, in SliceRow row, in TileRaster tr)
    {
        c.Ops ??= new (DrawOp, PixelRect, float, float)[256];
        c.OpN = 0;
        ReadOnlySpan<byte> stream = frame.StreamOf(in row);
        float sc = _frameScale <= 0f ? 1f : _frameScale;
        float ox = tr.Key.Tx * (float)TileGrid.W + row.Frame.OriginX, oy = tr.Key.Ty * (float)TileGrid.H + row.Frame.OriginY;
        int pos = 0;
        while (pos + sizeof(int) <= stream.Length)
        {
            var op = (DrawOp)System.Runtime.InteropServices.MemoryMarshal.Read<int>(stream[pos..]);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > stream.Length) break;
            ReadOnlySpan<byte> payload = stream.Slice(pos + sizeof(int), body);
            if (SliceOpBounds.TryGet(op, payload, out RectF b))
            {
                var r = new PixelRect((int)MathF.Floor(b.X * sc - ox), (int)MathF.Floor(b.Y * sc - oy), (int)MathF.Ceiling(b.Right * sc - ox), (int)MathF.Ceiling(b.Bottom * sc - oy));
                if (r.Right > 0 && r.Bottom > 0 && r.Left < tr.W && r.Top < tr.H)
                {
                    float a = 0f, bb = 0f;
                    if (op == DrawOp.DrawGlyphRunGradient) { var g = System.Runtime.InteropServices.MemoryMarshal.Read<DrawGlyphRunGradientCmd>(payload); a = g.Split; bb = g.Lift; }
                    else if (op == DrawOp.DrawGlyphRun) { var g = System.Runtime.InteropServices.MemoryMarshal.Read<DrawGlyphRunCmd>(payload); a = g.FontSize; }
                    if (c.OpN == c.Ops.Length) Array.Resize(ref c.Ops, c.OpN * 2);
                    c.Ops[c.OpN++] = (op, r, a, bb);
                }
            }
            pos += sizeof(int) + body;
        }
    }

    private int FreeCheckSlot()
    {
        for (int i = 0; i < _dmgChecks.Length; i++) if (!_dmgChecks[i].Busy) return i;
        return -1;
    }

    /// <summary>The validate arm's replay-honesty asserts for a partial raster: the glyph cull halo held (no glyph quad
    /// outside it — DEBUG / FLUENTGPU_DIAG measure it) and no stencil scope degraded to its scissor during the replay
    /// (either would make a partial raster's cull or clamp disagree with the whole raster's). A breach is a mismatch.</summary>
    private void NoteReplayHonesty(in TileRaster tr, long halo0, int stencilFb0)
    {
        if (_glyphHaloBreaches == halo0 && _frameStencilFallback == stencilFb0) return;
        Volatile.Write(ref _dcMismatches, _dcMismatches + 1);
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[damage-validate] HONESTY turn={_compositeTurn} slice={tr.Key.SliceId} tile=({tr.Key.Tx},{tr.Key.Ty}) glyphHaloBreaches+={_glyphHaloBreaches - halo0} stencilFallback+={_frameStencilFallback - stencilFb0}"));
    }

    private ID3D12Resource* CreateReadback(ulong bytes, string name = "DamageValidate.Readback")
    {
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC bd = default;
        bd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = bytes; bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
        bd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* rb;
        Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &bd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&rb), name);
        D3D12MemoryDiagnostics.Track(rb, name, bytes);
        return rb;
    }

    private void CopyToReadback(ID3D12Resource* src, ID3D12Resource* dst, in D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp, int w, int h)
    {
        D3D12_TEXTURE_COPY_LOCATION d = default;
        d.pResource = dst;
        d.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        d.Anonymous.PlacedFootprint = fp;
        D3D12_TEXTURE_COPY_LOCATION s = default;
        s.pResource = src;
        s.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        s.Anonymous.SubresourceIndex = 0;
        D3D12_BOX box = new() { left = 0, top = 0, front = 0, right = (uint)w, bottom = (uint)h, back = 1 };
        _cmdList->CopyTextureRegion(&d, 0, 0, 0, &s, &box);
        Rec(RecordedOp.CopyTexture, (uint)(nint)src, (uint)(w * h));
    }

    private static void ReleaseCheck(ref DamageCheck c)
    {
        if (c.A != null) { D3D12MemoryDiagnostics.Release(c.A, "DamageValidate.Readback"); c.A->Release(); }
        if (c.B != null) { D3D12MemoryDiagnostics.Release(c.B, "DamageValidate.Readback"); c.B->Release(); }
        c = default;
    }

    /// <summary>Compare every queued check the GPU has finished (start of a composite; render thread).</summary>
    private void PollDamageChecks()
    {
        ulong done = Gen.ID3D12FenceVtbl.GetCompletedValue(_fence);
        for (int i = 0; i < _dmgChecks.Length; i++)
        {
            ref DamageCheck c = ref _dmgChecks[i];
            if (!c.Busy || done < c.Fence) continue;
            c.Busy = false;
            void* pa, pb;
            D3D12_RANGE rr = new() { Begin = 0, End = (nuint)c.Bytes };
            if (c.A->Map(0, &rr, &pa) < 0) continue;
            if (c.B->Map(0, &rr, &pb) < 0) { D3D12_RANGE z0 = default; c.A->Unmap(0, &z0); continue; }
            long bad = 0, outside = 0;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, maxDelta = 0;
            for (int y = 0; y < c.H; y++)
            {
                uint* ra = (uint*)((byte*)pa + (ulong)y * c.Pitch), rb = (uint*)((byte*)pb + (ulong)y * c.Pitch);
                for (int x = 0; x < c.W; x++)
                {
                    uint va = ra[x], vb = rb[x];
                    if (va == vb) continue;
                    bad++;
                    if (x < c.Damage.Left || x >= c.Damage.Right || y < c.Damage.Top || y >= c.Damage.Bottom) outside++;
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    for (int k = 0; k < 32; k += 8) maxDelta = Math.Max(maxDelta, Math.Abs((int)((va >> k) & 0xFF) - (int)((vb >> k) & 0xFF)));
                }
            }
            D3D12_RANGE z = default;
            c.A->Unmap(0, &z); c.B->Unmap(0, &z);
            Volatile.Write(ref _dcValidated, _dcValidated + 1);
            var ci = CultureInfo.InvariantCulture;
            if (bad > 0)
            {
                Volatile.Write(ref _dcMismatches, _dcMismatches + 1);
                Console.Error.WriteLine(string.Create(ci,
                    $"[damage-validate] MISMATCH turn={c.Turn} slice={c.Key.SliceId} tile=({c.Key.Tx},{c.Key.Ty}) {c.W}x{c.H} damage=[{c.Damage.Left},{c.Damage.Top} → {c.Damage.Right},{c.Damage.Bottom}) px={bad} outsideDamage={outside} bbox=[{x0},{y0} → {x1 + 1},{y1 + 1}) maxDelta={maxDelta}"));
                if (_dcMismatches <= 6)
                    for (int k = 0; k < c.OpN; k++)
                    {
                        var (op, r, fa, fb) = c.Ops[k];
                        if (r.Right < x0 - 24 || r.Left > x1 + 24 || r.Bottom < y0 - 24 || r.Top > y1 + 24) continue;
                        Console.Error.WriteLine(string.Create(ci, $"[damage-validate]   op#{k} {op} [{r.Left},{r.Top} → {r.Right},{r.Bottom}) a={fa:0.###} b={fb:0.###}"));
                    }
            }
            else if (_dcValidated % 100 == 1)
                Console.Error.WriteLine(string.Create(ci,
                    $"[damage-validate] ok validated={_dcValidated} mismatches={_dcMismatches} skipped={_dcSkipped}"));
        }
    }

    /// <summary>Device teardown (the GPU is drained): release the validation readbacks.</summary>
    private void ReleaseDamageChecks()
    {
        for (int i = 0; i < _dmgChecks.Length; i++) ReleaseCheck(ref _dmgChecks[i]);
    }
}
