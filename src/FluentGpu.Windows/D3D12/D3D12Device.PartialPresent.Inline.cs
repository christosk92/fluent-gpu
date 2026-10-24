using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;

using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

// PARTIAL PRESENT OF AN INLINE ITEM. An inline segment (D3D12Device.Inline.cs) holds no tiles: its ops are replayed into the
// back buffer by every composite that reaches it. Its pixels are still a pure function of (a) its ops and the scopes open
// around them, (b) the content of the images it draws and their cross-fade progress — so between two composites it changes
// exactly where an op entered, left or changed (TileDamage.Diff, the theorem a tile's sub-tile damage rests on) and where an
// image it draws changed. The recorder hands every inline row's op records (CompositeFrame.InlineOpsOf); this keeps the
// previous composite's list per slice and dirties only that, plus the host's repaint damage where it reaches the item (what
// only the UI can see). Before, an inline item repainted its WHOLE footprint whenever its bytes moved or any damage rect
// touched it — and the shell's static band is window-sized, so one seek-bar step or one equalizer-bar pixel recomposited and
// presented the whole window (audit PERF, 2026-10-24).
// Anything it cannot vouch for — no list, no list last composite, another grid / scale / identity, an op with an unbounded or
// spreading footprint in the diff — returns false, and the caller takes the old whole-footprint signature.
public sealed unsafe partial class D3D12Device
{
    // Two generations of per-slice snapshots (this composite's and the previous one's), swapped every PpComputeDirty.
    private TileOpRec[] _ppInlOps = new TileOpRec[1024], _ppInlOpsPrev = new TileOpRec[1024];
    private int _ppInlTop;
    private int[] _ppInlOff = new int[64], _ppInlN = new int[64], _ppInlOffPrev = new int[64], _ppInlNPrev = new int[64];
    private int[] _ppInlSeq = new int[64], _ppInlSeqPrev = new int[64];
    private ulong[] _ppInlKey = new ulong[64], _ppInlKeyPrev = new ulong[64];
    private ulong[] _ppInlImg = new ulong[64], _ppInlImgPrev = new ulong[64];
    // The row's SliceRow.ContentKey the snapshot was taken from: an unchanged key (and framing key) means the very same op
    // records, so the diff is known empty without comparing them op by op (audit PERF r2).
    private ulong[] _ppInlContent = new ulong[64], _ppInlContentPrev = new ulong[64];
    private int _ppInlFrameSeq = 1;
    private RepaintDamageRegion _ppInlDirty;   // window px, merged into the frame's dirty set after the entry pass

    private void PpInlineBeginFrame()
    {
        _ppInlTop = 0;
        _ppInlDirty = default;
        _ppInlFrameSeq++;
    }

    /// <summary>After the entry pass: this composite's snapshots become the baseline of the next.</summary>
    private void PpInlineEndFrame()
    {
        (_ppInlOps, _ppInlOpsPrev) = (_ppInlOpsPrev, _ppInlOps);
        (_ppInlOff, _ppInlOffPrev) = (_ppInlOffPrev, _ppInlOff);
        (_ppInlN, _ppInlNPrev) = (_ppInlNPrev, _ppInlN);
        (_ppInlSeq, _ppInlSeqPrev) = (_ppInlSeqPrev, _ppInlSeq);
        (_ppInlKey, _ppInlKeyPrev) = (_ppInlKeyPrev, _ppInlKey);
        (_ppInlImg, _ppInlImgPrev) = (_ppInlImgPrev, _ppInlImg);
        (_ppInlContent, _ppInlContentPrev) = (_ppInlContentPrev, _ppInlContent);
    }

    private void PpInlineEnsure(int sliceId)
    {
        if (sliceId < _ppInlOff.Length) return;
        int n = System.Math.Max(sliceId + 1, _ppInlOff.Length * 2);
        System.Array.Resize(ref _ppInlOff, n); System.Array.Resize(ref _ppInlOffPrev, n);
        System.Array.Resize(ref _ppInlN, n); System.Array.Resize(ref _ppInlNPrev, n);
        System.Array.Resize(ref _ppInlSeq, n); System.Array.Resize(ref _ppInlSeqPrev, n);
        System.Array.Resize(ref _ppInlKey, n); System.Array.Resize(ref _ppInlKeyPrev, n);
        System.Array.Resize(ref _ppInlImg, n); System.Array.Resize(ref _ppInlImgPrev, n);
        System.Array.Resize(ref _ppInlContent, n); System.Array.Resize(ref _ppInlContentPrev, n);
    }

    /// <summary>Inline item <paramref name="it"/>: snapshot its op list, and when the previous composite left a trusted one,
    /// add what changed (window px, cut to <paramref name="sci"/>) to <see cref="_ppInlDirty"/> and return true.</summary>
    private bool PpInlineDiff(in CompositeFrame frame, in CompositeItem it, in PixelRect foot, in PixelRect sci)
    {
        int sliceId = it.SliceId;
        int row = RowOf(sliceId);
        if (sliceId < 0 || row < 0 || !frame.InlineOpsOf(row, out ReadOnlySpan<TileOpRec> ops)) return false;
        ref readonly SliceRow sr = ref frame.Slices[row];
        PpInlineEnsure(sliceId);

        // What the records are relative to, and which segment they describe: a different grid, scale or identity makes the
        // previous list incomparable.
        ulong key = 0x1A11_0000_0000_0001UL;
        Mix(ref key, (ulong)(uint)sr.Frame.OriginX << 32 | (uint)sr.Frame.OriginY);
        Mix(ref key, (ulong)BitConverter.SingleToUInt32Bits(sr.Frame.Scale) << 32 | BitConverter.SingleToUInt32Bits(_frameScale));
        Mix(ref key, sr.Identity);
        Mix(ref key, (ulong)(uint)sr.NodeIndex << 32 | sr.Gen);
        Mix(ref key, PathRealizationCache.Shared.Generation);   // a path-slab compaction re-points the path ops' bytes
        ulong img = PpInlineImageFold(in frame, in sr, in it, out PixelRect imageFoot);

        // this composite's snapshot (a slice composited twice in one frame keeps its first list: the second item falls back)
        bool dup = _ppInlSeq[sliceId] == _ppInlFrameSeq;
        if (!dup)
        {
            if (_ppInlTop + ops.Length > _ppInlOps.Length)
            {
                var grown = new TileOpRec[System.Math.Max(_ppInlTop + ops.Length, _ppInlOps.Length * 2)];
                _ppInlOps.AsSpan(0, _ppInlTop).CopyTo(grown);
                _ppInlOps = grown;
            }
            ops.CopyTo(_ppInlOps.AsSpan(_ppInlTop));
            _ppInlOff[sliceId] = _ppInlTop; _ppInlN[sliceId] = ops.Length;
            _ppInlTop += ops.Length;
            _ppInlSeq[sliceId] = _ppInlFrameSeq;
            _ppInlKey[sliceId] = key;
            _ppInlImg[sliceId] = img;
            _ppInlContent[sliceId] = sr.ContentKey;
        }
        if (dup || _ppInlSeqPrev[sliceId] != _ppInlFrameSeq - 1 || _ppInlKeyPrev[sliceId] != key) return false;

        PixelRect d = default;
        if (sr.ContentKey == 0 || _ppInlContentPrev[sliceId] != sr.ContentKey)
        {
            ReadOnlySpan<TileOpRec> prev = _ppInlOpsPrev.AsSpan(_ppInlOffPrev[sliceId], _ppInlNPrev[sliceId]);
            if (!TileDamage.Diff(prev, ops, SliceRecorder.InlineOpsExtent, SliceRecorder.InlineOpsExtent, out d)) return false;
        }
        else
        {
            // the same records as last composite: no op changed — but a list Diff cannot vouch for (a spreading op) stays refused
            for (int i = 0; i < ops.Length; i++) if ((ops[i].Flags & TileOpRec.FlagSpread) != 0) return false;
        }
        if (!d.IsEmpty)
        {
            // records → window px: + the frame's origin + the item's placement (the grid's sub-pixel residual and the AA slack
            // are inside the 2 px grow), snapped out to the even quad grid as a tile's damage is
            int bx = (int)SliceRecorder.InlineOpsOrigin + (int)it.Transform.Dx, by = (int)SliceRecorder.InlineOpsOrigin + (int)it.Transform.Dy;
            var w = new PixelRect(((d.Left + bx - 2) & ~1), ((d.Top + by - 2) & ~1), ((d.Right + bx + 3) & ~1), ((d.Bottom + by + 3) & ~1));
            PpInlineDirty(PpIntersect(in w, in sci), "inline-ops");
        }
        if (img != _ppInlImgPrev[sliceId]) PpInlineDirty(PpIntersect(in imageFoot, in sci), "inline-image");
        // what only the host can see (an image's content under unchanged bytes, a live cross-fade): its own rects, where they
        // reach the item — never the whole footprint
        var damage = frame.Info.RepaintDamage;
        if (damage.IsFull) PpInlineDirty(in foot, "inline-full-damage");
        else
        {
            ReadOnlySpan<RectF> rects = damage.AsSpan();
            for (int k = 0; k < rects.Length; k++)
            {
                RectF r = rects[k];
                var px = new PixelRect((int)MathF.Floor(r.X * _frameScale) - 1, (int)MathF.Floor(r.Y * _frameScale) - 1,
                    (int)MathF.Ceiling(r.Right * _frameScale) + 1, (int)MathF.Ceiling(r.Bottom * _frameScale) + 1);
                PpInlineDirty(PpIntersect(in px, in foot), "inline-damage");
            }
        }
        return true;
    }

    private void PpInlineDirty(in PixelRect r, string why)
    {
        if (r.IsEmpty) return;
        if (TileDamage.Log)
            Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[damage.pp] turn={_compositeTurn} {why} [{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}]"));
        _ppInlDirty.Add(new RectF(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
    }

    /// <summary>The fold of what the inline item's images paint beyond their bytes — each drawn image's content serial and
    /// its cross-fade progress at this frame's image clock — and the window px all of them cover.</summary>
    private ulong PpInlineImageFold(in CompositeFrame frame, in SliceRow row, in CompositeItem it, out PixelRect foot)
    {
        foot = default;
        ulong h = 0x1A6E_0000_0000_0001UL;
        if (_imageTextures is null) return h;
        ReadOnlySpan<byte> stream = frame.StreamOf(in row);
        float s = _frameScale <= 0f ? 1f : _frameScale;
        float ox = it.Transform.Dx - row.Frame.OriginX, oy = it.Transform.Dy - row.Frame.OriginY;
        int pos = 0;
        while (pos + sizeof(int) <= stream.Length)
        {
            var op = (DrawOp)System.Runtime.InteropServices.MemoryMarshal.Read<int>(stream[pos..]);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > stream.Length) break;
            if (op == DrawOp.DrawImage)
            {
                ReadOnlySpan<byte> payload = stream.Slice(pos + sizeof(int), body);
                var c = System.Runtime.InteropServices.MemoryMarshal.Read<DrawImageCmd>(payload);
                Mix(ref h, (ulong)(uint)c.ImageId << 32 | _imageTextures.ContentSerial(c.ImageId));
                Mix(ref h, BitConverter.SingleToUInt32Bits(FluentGpu.Scene.ImageCache.ResolveFade(frame.Info.ImageClockMs, c.FadeStartMs, c.FadeDurationMs, c.FadeEasing)));
                if (SliceOpBounds.TryGet(op, payload, out RectF b))
                {
                    var px = new PixelRect((int)MathF.Floor(b.X * s + ox) - 2, (int)MathF.Floor(b.Y * s + oy) - 2,
                        (int)MathF.Ceiling(b.Right * s + ox) + 2, (int)MathF.Ceiling(b.Bottom * s + oy) + 2);
                    foot = PpUnion(in foot, in px);
                }
                else foot = new PixelRect(0, 0, (int)_w, (int)_h);
            }
            pos += sizeof(int) + body;
        }
        return h;
    }
}
