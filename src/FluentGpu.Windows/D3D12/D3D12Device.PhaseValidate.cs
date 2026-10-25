using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Gen = FluentGpu.Interop.Generated;

using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

// `--fg phase-validate` (audit PERF r3 RASTER): the equivalence check of the whole-pixel snap of a FRACTIONAL placement
// (SliceRecorder.FractionalPx). A slice under a composited transition layer whose placement is a whole pixel up to float
// noise rasters at phase 0; this validator takes the tiles the composite places this turn whose raw placement was snapped
// (SliceRow.RefPhaseX/Y ≠ Frame.PhaseX/Y), re-rasters each at the RAW phase (relative to the same whole-pixel part, so on
// the same tile grid) into a shadow scratch, copies the composited tile and the shadow to readbacks and compares them byte
// for byte once the GPU passed the frame: pixels that differ and the largest channel delta. It checks what is ON SCREEN — a
// tile kept across turns because its phase held (the point of the snap) as well as one rastered this turn. A bounded number
// per turn, round-robin over the placements so a still page is covered over a few turns. Diagnostics only.
// `--fg footprint-validate` rides the same machinery: the shadow is the tile's own phase with the footprint skip OFF
// (D3D12Device.OpFootprints.cs — every op decoded), so a mismatch is a primitive the skip dropped that the decoder draws.
public sealed unsafe partial class D3D12Device
{
    private struct PhaseCheck
    {
        public ID3D12Resource* A, B;     // readback: the composited tile, the raw-phase shadow raster
        public ulong Bytes, Fence;
        public uint Pitch;
        public int W, H, Turn;
        public TileKey Key;
        public float DPhaseX, DPhaseY;   // raw − grid phase (px)
        public bool Busy;
        public byte Kind;                // 0 = phase, 1 = footprint
    }
    private readonly PhaseCheck[] _phaseChecks = new PhaseCheck[8];
    private const int PhaseChecksPerTurn = 2;
    private int _phaseCursor;
    private long _phChecked, _phIdentical, _phDiffering, _phDiffPx, _phSkipped, _phExact;
    private int _phMaxDelta;
    private long _fvChecked, _fvIdentical, _fvDiffering, _fvDiffPx, _fvSkipped;
    private int _fvMaxDelta, _fvCursor;

    /// <summary>RasterTiles' tail (every tile surface is in PIXEL_SHADER_RESOURCE): poll the finished checks, then queue up to
    /// <see cref="PhaseChecksPerTurn"/> new ones over this turn's placements.</summary>
    private void QueuePhaseChecks(in CompositeFrame frame)
    {
        PollPhaseChecks();
        ReadOnlySpan<TilePlacement> placements = frame.Placements;
        int n = placements.Length;
        if (n == 0) return;
        if (TileDamage.PhaseValidate)
        {
            int queued = 0;
            for (int k = 0; k < n && queued < PhaseChecksPerTurn; k++)
            {
                int i = (_phaseCursor + k) % n;
                ref readonly TilePlacement p = ref placements[i];
                int r = RowOf(p.Key.SliceId);
                if (r < 0) continue;
                ref readonly SliceRow row = ref frame.Slices[r];
                if (row.RefPhaseX == row.Frame.PhaseX && row.RefPhaseY == row.Frame.PhaseY) { _phExact++; continue; }
                if (_surfaces!.TileResource(p.Surface) == null) continue;
                if (QueuePhaseCheck(in frame, in row, in p, kind: 0)) queued++;
                _phaseCursor = (i + 1) % n;
            }
        }
        if (TileDamage.FootprintValidate)
        {
            int queued = 0;
            for (int k = 0; k < n && queued < PhaseChecksPerTurn; k++)
            {
                int i = (_fvCursor + k) % n;
                ref readonly TilePlacement p = ref placements[i];
                int r = RowOf(p.Key.SliceId);
                if (r < 0 || _surfaces!.TileResource(p.Surface) == null) continue;
                if (QueuePhaseCheck(in frame, in frame.Slices[r], in p, kind: 1)) queued++;
                _fvCursor = (i + 1) % n;
            }
        }
    }

    private bool QueuePhaseCheck(in CompositeFrame frame, in SliceRow row, in TilePlacement p, byte kind)
    {
        ref long skipped = ref (kind == 0 ? ref _phSkipped : ref _fvSkipped);
        int slot = -1;
        for (int i = 0; i < _phaseChecks.Length; i++) if (!_phaseChecks[i].Busy) { slot = i; break; }
        if (slot < 0) { skipped++; return false; }
        if (_surfaces!.TileState(p.Surface) != D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE) { skipped++; return false; }
        var tr = new TileRaster(p.Key, p.Surface, InvalidationReason.None, 0, p.W, p.H);
        // An image whose pixels changed under its id since the tile drew it, or one still in flight, makes the pair
        // incomparable (the same rule as damage-validate).
        if (!ImageDamage(in frame, in row, in tr, default).IsEmpty) { skipped++; return false; }
        ulong fence = _fenceValue + 1;
        int scratch = _surfaces.AcquireScratch(p.W, p.H, fence);
        if (scratch < 0) { skipped++; return false; }
        int dropped = DroppedInstanceCount(), glyphDropped = _glyphs!.DroppedInstances, inFlight = _frameImagesInFlight;
        int tileX = p.Key.Tx * TileGrid.W + row.Frame.OriginX, tileY = p.Key.Ty * TileGrid.H + row.Frame.OriginY;
        SliceRow raw = row;
        if (kind == 0) raw.Frame = row.Frame with { PhaseX = row.RefPhaseX, PhaseY = row.RefPhaseY };
        _replayFoot = null; _replayFootN = 0;   // the shadow decodes every op (the footprint check's reference)
        EndPassIfOpen();
        ScratchBarrier(scratch, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var srtv = _surfaces.ScratchRtv(scratch);
        BeginPass(srtv, p.W, p.H, PassLoad.Clear);   // exactly a whole raster's pass (RasterTiles)
        int imgSlot = _imgRecSlot;
        _imgRecSlot = -1;                            // the shadow records no image draws for the tile
        ReplaySegment(in frame, in raw, -tileX, -tileY, p.W, p.H, srtv,
            new RectF(p.Key.Tx * (float)TileGrid.W, p.Key.Ty * (float)TileGrid.H, p.W, p.H));
        _imgRecSlot = imgSlot;
        EndPassIfOpen();
        if (DroppedInstanceCount() != dropped || _glyphs.DroppedInstances != glyphDropped || _frameImagesInFlight != inFlight)
        {
            _surfaces.ReleaseScratch(scratch);
            skipped++;
            InvalidateCmdState();
            return false;
        }

        ref PhaseCheck c = ref _phaseChecks[slot];
        D3D12_RESOURCE_DESC desc = default;
        desc.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = (ulong)p.W; desc.Height = (uint)p.H; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp; uint rows; ulong rowBytes; ulong total;
        _device->GetCopyableFootprints(&desc, 0, 1, 0, &fp, &rows, &rowBytes, &total);
        if (c.A == null || c.Bytes < total)
        {
            ReleasePhaseCheck(ref c);
            c.A = CreateReadback(total, "PhaseValidate.Readback");
            c.B = CreateReadback(total, "PhaseValidate.Readback");
            c.Bytes = total;
        }
        ID3D12Resource* tile = _surfaces.TileResource(p.Surface);
        Barrier(tile, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        ScratchBarrier(scratch, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        CopyToReadback(tile, c.A, fp, p.W, p.H);
        CopyToReadback(_surfaces.ScratchResource(scratch), c.B, fp, p.W, p.H);
        Barrier(tile, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        _surfaces.ReleaseScratch(scratch);
        c.Pitch = fp.Footprint.RowPitch;
        c.W = p.W; c.H = p.H; c.Turn = _compositeTurn; c.Key = p.Key;
        c.DPhaseX = row.RefPhaseX - row.Frame.PhaseX; c.DPhaseY = row.RefPhaseY - row.Frame.PhaseY;
        c.Fence = fence;
        c.Busy = true;
        c.Kind = kind;
        InvalidateCmdState();
        return true;
    }

    /// <summary>Compare every queued check the GPU has finished.</summary>
    private void PollPhaseChecks()
    {
        ulong done = Gen.ID3D12FenceVtbl.GetCompletedValue(_fence);
        for (int i = 0; i < _phaseChecks.Length; i++)
        {
            ref PhaseCheck c = ref _phaseChecks[i];
            if (!c.Busy || done < c.Fence) continue;
            c.Busy = false;
            void* pa, pb;
            D3D12_RANGE rr = new() { Begin = 0, End = (nuint)c.Bytes };
            if (c.A->Map(0, &rr, &pa) < 0) continue;
            if (c.B->Map(0, &rr, &pb) < 0) { D3D12_RANGE z0 = default; c.A->Unmap(0, &z0); continue; }
            long bad = 0;
            int maxDelta = 0;
            for (int y = 0; y < c.H; y++)
            {
                uint* ra = (uint*)((byte*)pa + (ulong)y * c.Pitch), rb = (uint*)((byte*)pb + (ulong)y * c.Pitch);
                for (int x = 0; x < c.W; x++)
                {
                    uint va = ra[x], vb = rb[x];
                    if (va == vb) continue;
                    bad++;
                    for (int k = 0; k < 32; k += 8) maxDelta = Math.Max(maxDelta, Math.Abs((int)((va >> k) & 0xFF) - (int)((vb >> k) & 0xFF)));
                }
            }
            D3D12_RANGE z = default;
            c.A->Unmap(0, &z); c.B->Unmap(0, &z);
            var ci = CultureInfo.InvariantCulture;
            if (c.Kind == 1)
            {
                _fvChecked++;
                if (bad == 0) _fvIdentical++;
                else
                {
                    _fvDiffering++;
                    _fvDiffPx += bad;
                    if (maxDelta > _fvMaxDelta) _fvMaxDelta = maxDelta;
                    Console.Error.WriteLine(string.Create(ci,
                        $"[footprint-validate] MISMATCH turn={c.Turn} slice={c.Key.SliceId} tile=({c.Key.Tx},{c.Key.Ty}) {c.W}x{c.H} px={bad} maxDelta={maxDelta}"));
                }
                if (_fvChecked % 50 == 1) Console.Error.WriteLine(FootprintValidateLine());
                continue;
            }
            _phChecked++;
            if (bad == 0) _phIdentical++;
            else
            {
                _phDiffering++;
                _phDiffPx += bad;
                if (maxDelta > _phMaxDelta) _phMaxDelta = maxDelta;
                if (_phDiffering <= 20 || maxDelta > 1)
                    Console.Error.WriteLine(string.Create(ci,
                        $"[phase-validate] DIFF turn={c.Turn} slice={c.Key.SliceId} tile=({c.Key.Tx},{c.Key.Ty}) {c.W}x{c.H} dPhase=({c.DPhaseX:R},{c.DPhaseY:R}) px={bad} ({100.0 * bad / Math.Max(1, c.W * c.H):0.###}%) maxDelta={maxDelta}"));
            }
            if (_phChecked % 50 == 1) Console.Error.WriteLine(PhaseValidateLine());
        }
    }

    /// <summary>The validator's running totals (also printed at teardown).</summary>
    internal string PhaseValidateLine() => string.Create(CultureInfo.InvariantCulture,
        $"[phase-validate] checked={_phChecked} identical={_phIdentical} differing={_phDiffering} diffPx={_phDiffPx} maxDelta={_phMaxDelta} exactPlacements={_phExact} skipped={_phSkipped}");

    /// <summary>The footprint validator's running totals.</summary>
    internal string FootprintValidateLine() => string.Create(CultureInfo.InvariantCulture,
        $"[footprint-validate] checked={_fvChecked} identical={_fvIdentical} mismatches={_fvDiffering} diffPx={_fvDiffPx} maxDelta={_fvMaxDelta} skipped={_fvSkipped} opsSkipped={_rcFootSkippedTally}");

    private static void ReleasePhaseCheck(ref PhaseCheck c)
    {
        if (c.A != null) { D3D12MemoryDiagnostics.Release(c.A, "PhaseValidate.Readback"); c.A->Release(); }
        if (c.B != null) { D3D12MemoryDiagnostics.Release(c.B, "PhaseValidate.Readback"); c.B->Release(); }
        c = default;
    }

    /// <summary>Device teardown / recovery (the GPU is drained): release the readbacks.</summary>
    private void ReleasePhaseChecks()
    {
        if (_phChecked > 0) Console.Error.WriteLine(PhaseValidateLine());
        if (_fvChecked > 0) Console.Error.WriteLine(FootprintValidateLine());
        for (int i = 0; i < _phaseChecks.Length; i++) ReleasePhaseCheck(ref _phaseChecks[i]);
    }
}
