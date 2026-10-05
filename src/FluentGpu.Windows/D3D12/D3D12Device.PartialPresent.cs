using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.Windows;

using ColorF = FluentGpu.Foundation.ColorF;
using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

// PARTIAL PRESENT — the primary window composites and presents only what changed.
//
// The primary swapchain is FLIP_SEQUENTIAL: each back buffer keeps the pixels it held when it was last rendered. A frame
//   1. diffs its composite item list against the previous frame's (PpComputeDirty): one ENTRY per tile placement and per
//      offscreen surface, keyed by identity (kind, slice, occurrence, tile cell) and signed by everything that decides its
//      pixels — the item's composite parameters plus the surface's content key (a tile's raster serial rides beside it). The composite
//      is a pure function of those entries in painter order, so an entry whose key, signature and rect are unchanged paints
//      the same pixels: the DIRTY set is the old ∪ new rect of every entry that changed, appeared or vanished. It does not
//      trust the recorder's damage at all (a backdrop's blur reaches past the damage that caused it; the entry diff sees
//      the backdrop's own key change instead).
//   2. repaints, into this back buffer, the union of the dirty sets of every frame since the buffer was last rendered
//      (its AGE) — so what the buffer already holds outside that union is exactly the previous frame (PpPlanRepaint).
//   3. composites with a PRESERVE load: per repaint rect, a clear of the rect, then only the items that touch it, each
//      scissored to it (ItemScissor / DrawRange read _frameClip).
//   4. presents with Present1 dirty rects = this frame's dirty set (relative to the last PRESENTED frame).
// The entries are matched in PAINTER order (PpMatch: a monotone matching, any one of which is sound), so an item that
// appeared, vanished or moved in the order dirties only its own rects; a re-rastered tile dirties only what its raster
// wrote (a partial raster's damage — D3D12Device.TileDamage.cs). Anything that makes the history untrustworthy — the
// first frame, a resize / DPI / clear-colour / knockout change, a forced-full repaint reason, a non-composite submit, a
// device rebuild — takes
// the whole-frame route (CLEAR load + full present), exactly the route every frame took before. So does a repaint larger
// than PpFullCoverage of the window (the clear load is cheaper than preserving most of it). A stood-down present makes
// the next present whole (DWM never saw the stood-down frame's changes).
public sealed unsafe partial class D3D12Device
{
    /// <summary>At or above this share of the window, a repaint takes the whole-frame route.</summary>
    private const float PpFullCoverage = 0.45f;
    private const int PpHistory = (int)FRAME_COUNT + 1;

    private struct PpEntry
    {
        public ulong Key, Sig;
        public PixelRect Rect;
        // a tile placement: its surface slot, that slot's raster serial and the tile's origin in window px (the serial is
        // NOT in Sig: a re-raster repaints only what it wrote, SurfacePool.TileLastWrite); Slot -1 for every other entry
        public int Slot, Ox, Oy;
        public uint Serial;
        public int Item, SliceId;   // diagnostics (--fg damage-log): the composite item the entry belongs to
        public CompositeKind Kind;

        public readonly bool Same(in PpEntry o) => Key == o.Key && Sig == o.Sig && Rect.Equals(o.Rect);
    }

    /// <summary>How far the painter-order entry match looks ahead to resync after an entry that appeared or vanished.</summary>
    private const int PpLookahead = 8;

    private PpEntry[] _ppCur = new PpEntry[256], _ppPrev = new PpEntry[256];
    private int[] _ppOcc = new int[64];
    private int _ppCurN, _ppPrevN;
    private bool _ppPrevValid;
    private ulong _ppPrevStructure;   // the item (kind, slice) sequence — only the --fg no-precise-present arm acts on it
    private ColorF _ppPrevClear;
    private float _ppPrevScale;
    private uint _ppPrevW, _ppPrevH, _ppPrevEpoch;
    private GpuKnockouts _ppPrevKnockouts;

    // the dirty set of each recent composite turn (window px), for buffer-age repaint
    private readonly int[] _ppHistTurn = new int[PpHistory];
    private readonly bool[] _ppHistFull = new bool[PpHistory];
    private readonly RepaintDamageRegion[] _ppHist = new RepaintDamageRegion[PpHistory];
    // which turn each back buffer last received, under which swapchain epoch (0 = unknown ⇒ full)
    private readonly int[] _ppBufTurn = new int[(int)FRAME_COUNT];
    private readonly uint[] _ppBufEpoch = new uint[(int)FRAME_COUNT];

    // this frame
    private RepaintDamageRegion _ppDirty;      // window px
    private bool _ppDirtyFull;
    private readonly PixelRect[] _ppRepaint = new PixelRect[RepaintDamageRegion.MaxRects];
    private int _ppRepaintN;
    private bool _ppPartial;                  // this frame composites through the PRESERVE route
    private PixelRect[] _itemFoot = new PixelRect[64];

    // the composite pass's frame clip (a repaint rect) — ItemScissor intersects every item scissor with it
    private bool _frameClipOn;
    private PixelRect _frameClip;

    /// <summary>Partial-present census of the last composite (render thread writes, diagnostics read): the repaint
    /// coverage of the window (1 = whole frame) and the number of repaint rects.</summary>
    public float LastPartialCoverage { get; private set; } = 1f;
    public int LastPartialRects { get; private set; }
    /// <summary>Composites that took the PRESERVE route since the device was created (the identity probe asserts it ran).</summary>
    public long PartialFrameCount { get; private set; }

    /// <summary>Forget every back buffer's history: the next frame of the primary target composites and presents whole.</summary>
    private void PpInvalidate()
    {
        System.Array.Clear(_ppBufTurn);
        _ppPrevValid = false;
    }

    private static PixelRect PpIntersect(in PixelRect a, in PixelRect b)
    {
        int l = System.Math.Max(a.Left, b.Left), t = System.Math.Max(a.Top, b.Top);
        int r = System.Math.Min(a.Right, b.Right), btm = System.Math.Min(a.Bottom, b.Bottom);
        return r > l && btm > t ? new PixelRect(l, t, r, btm) : default;
    }

    private static PixelRect PpUnion(in PixelRect a, in PixelRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new PixelRect(System.Math.Min(a.Left, b.Left), System.Math.Min(a.Top, b.Top),
            System.Math.Max(a.Right, b.Right), System.Math.Max(a.Bottom, b.Bottom));
    }

    private PixelRect PpPx(in RectF r)
    {
        var w = new PixelRect(0, 0, (int)_w, (int)_h);
        var p = new PixelRect((int)MathF.Floor(r.X), (int)MathF.Floor(r.Y), (int)MathF.Ceiling(r.X + r.W), (int)MathF.Ceiling(r.Y + r.H));
        return PpIntersect(in p, in w);
    }

    /// <summary>The item's scissor in window px (the window when unbounded) — what ItemScissor gives it.</summary>
    private PixelRect PpScissor(in CompositeItem it)
    {
        var w = new PixelRect(0, 0, (int)_w, (int)_h);
        if (IsUnbounded(it.Clip)) return w;
        var c = new PixelRect((int)MathF.Floor(it.Clip.X), (int)MathF.Floor(it.Clip.Y), (int)MathF.Ceiling(it.Clip.Right), (int)MathF.Ceiling(it.Clip.Bottom));
        return PpIntersect(in c, in w);
    }

    private static ulong PpItemSig(in CompositeItem it)
    {
        ulong h = 0x51A7_0000_0000_0001UL;
        Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.Transform.Dx) << 32 | BitConverter.SingleToUInt32Bits(it.Transform.Dy));
        Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.Alpha) << 32 | BitConverter.SingleToUInt32Bits(it.BlurSigma));
        PpMixRect(ref h, it.Clip); PpMixRect(ref h, it.RoundClip); PpMixRect(ref h, it.SourceClip);
        Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.ClipRadii.TopLeft) << 32 | (uint)it.BlendCopy << 16 | (uint)it.HasLayer << 8 | it.LowResDown);
        PpMixRect(ref h, it.Feather.Rect); PpMixRect(ref h, it.Feather2.Rect);
        Mix(ref h, (ulong)(uint)it.Feather.GetHashCode() << 32 | (uint)it.Feather2.GetHashCode());
        if (it.Kind == CompositeKind.Backdrop) Mix(ref h, (ulong)(uint)it.Acrylic.GetHashCode());
        return h;
    }

    private static void PpMixRect(ref ulong h, in RectF r)
    {
        Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(r.X) << 32 | BitConverter.SingleToUInt32Bits(r.Y));
        Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(r.W) << 32 | BitConverter.SingleToUInt32Bits(r.H));
    }

    private void PpAdd(ulong key, ulong sig, in PixelRect rect, int slot = -1, int ox = 0, int oy = 0, uint serial = 0)
    {
        if (_ppCurN == _ppCur.Length) System.Array.Resize(ref _ppCur, _ppCur.Length * 2);
        ref PpEntry e = ref _ppCur[_ppCurN++];
        e.Key = key; e.Sig = sig; e.Rect = rect;
        e.Slot = slot; e.Ox = ox; e.Oy = oy; e.Serial = serial;
        e.Item = _ppAddItem; e.SliceId = _ppAddSlice; e.Kind = _ppAddKind;
    }

    private int _ppAddItem, _ppAddSlice;
    private CompositeKind _ppAddKind;

    /// <summary><c>--fg damage-log</c>: why an entry dirtied the frame.</summary>
    private void PpLog(in PpEntry e, string why, in PixelRect r)
    {
        if (!TileDamage.Log || r.IsEmpty) return;
        Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[damage.pp] turn={_compositeTurn} item={e.Item} {e.Kind} slice={e.SliceId} {why} [{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}]"));
    }

    /// <summary>The occurrence of (<paramref name="kind"/>, <paramref name="sliceId"/>) among this frame's items so far: an
    /// item's identity that does not depend on its index, so an item inserted or removed elsewhere leaves the rest matched.</summary>
    private int PpOccurrence(CompositeKind kind, int sliceId)
    {
        int at = (sliceId + 1) * 8 + (int)kind;
        if (at < 0) return 0;
        if (at >= _ppOcc.Length) System.Array.Resize(ref _ppOcc, System.Math.Max(at + 1, _ppOcc.Length * 2));
        return _ppOcc[at]++;
    }

    /// <summary>A matched entry pair: a tile re-rastered since the previous frame dirties what that raster wrote (its whole
    /// placement unless the slot's last raster was partial AND is the only one since); anything else matched is clean.</summary>
    private void PpMatched(in PpEntry cur, in PpEntry prev)
    {
        if (cur.Slot < 0 || cur.Serial == prev.Serial) return;
        if (TileDamage.PrecisePresent && cur.Serial == prev.Serial + 1 && _surfaces!.TileLastWrite(cur.Slot, out PixelRect d))
        {
            var w = new PixelRect(cur.Ox + d.Left, cur.Oy + d.Top, cur.Ox + d.Right, cur.Oy + d.Bottom);
            PixelRect dr = PpIntersect(in w, in cur.Rect);
            PpLog(in cur, "tile-partial", in dr);
            PpDirtyAdd(in dr);
        }
        else { PpLog(in cur, "tile-whole", in cur.Rect); PpDirtyAdd(in cur.Rect); }
    }

    /// <summary>The dirty set between the previous frame's entries and this frame's, both in PAINTER order: a monotone
    /// matching (common prefix, common suffix, then a greedy resync with a short lookahead) pairs entries with the same
    /// identity, signature and rect; every unmatched entry dirties its rect. Sound for any monotone matching (the composite
    /// is the painter-ordered fold of its entries, and outside the dirty set every pixel is covered by the same matched
    /// entries in the same order: the theorem TileDamage.Diff applies to a tile's ops), so an item that appeared, vanished
    /// or changed its place in the order repaints only where it was and is, never the whole frame.</summary>
    private void PpMatch()
    {
        int na = _ppPrevN, nb = _ppCurN;
        int p = 0;
        while (p < na && p < nb && _ppPrev[p].Same(in _ppCur[p])) { PpMatched(in _ppCur[p], in _ppPrev[p]); p++; }
        int s = 0;
        while (s < na - p && s < nb - p && _ppPrev[na - 1 - s].Same(in _ppCur[nb - 1 - s])) { PpMatched(in _ppCur[nb - 1 - s], in _ppPrev[na - 1 - s]); s++; }
        int ia = p, ib = p, ea = na - s, eb = nb - s;
        while (ia < ea && ib < eb)
        {
            if (_ppPrev[ia].Same(in _ppCur[ib])) { PpMatched(in _ppCur[ib], in _ppPrev[ia]); ia++; ib++; continue; }
            bool synced = false;
            for (int k = 1; k <= PpLookahead && !synced; k++)
            {
                if (ia + k < ea && _ppPrev[ia + k].Same(in _ppCur[ib]))
                {
                    for (int q = 0; q < k; q++) { PpLog(in _ppPrev[ia + q], "gone", in _ppPrev[ia + q].Rect); PpDirtyAdd(in _ppPrev[ia + q].Rect); }
                    ia += k; synced = true;
                }
                else if (ib + k < eb && _ppPrev[ia].Same(in _ppCur[ib + k]))
                {
                    for (int q = 0; q < k; q++) { PpLog(in _ppCur[ib + q], "new", in _ppCur[ib + q].Rect); PpDirtyAdd(in _ppCur[ib + q].Rect); }
                    ib += k; synced = true;
                }
            }
            if (synced) continue;
            PpLog(in _ppPrev[ia], "was", in _ppPrev[ia].Rect); PpDirtyAdd(in _ppPrev[ia].Rect);
            PpLog(in _ppCur[ib], (_ppPrev[ia].Key == _ppCur[ib].Key ? (_ppPrev[ia].Sig != _ppCur[ib].Sig ? "sig" : "rect") : "now"), in _ppCur[ib].Rect);
            PpDirtyAdd(in _ppCur[ib].Rect);
            ia++; ib++;
        }
        for (; ia < ea; ia++) { PpLog(in _ppPrev[ia], "gone", in _ppPrev[ia].Rect); PpDirtyAdd(in _ppPrev[ia].Rect); }
        for (; ib < eb; ib++) { PpLog(in _ppCur[ib], "new", in _ppCur[ib].Rect); PpDirtyAdd(in _ppCur[ib].Rect); }
    }

    /// <summary>Step 1: this frame's entries, their per-item footprints (<c>_itemFoot</c>, for the per-rect cull) and the
    /// DIRTY set against the previous frame's entries. Runs after the offscreen phase, so every surface key is known.</summary>
    private void PpComputeDirty(in CompositeFrame frame, D3D12Swapchain sc)
    {
        ReadOnlySpan<CompositeItem> items = frame.Items;
        if (_itemFoot.Length < items.Length) _itemFoot = new PixelRect[System.Math.Max(items.Length, _itemFoot.Length * 2)];
        _ppCurN = 0;
        System.Array.Clear(_ppOcc);
        ulong structure = 0x5747_0000_0000_0001UL;
        ulong fresh = 0xF2E5_0000_0000_0000UL ^ (ulong)(uint)_compositeTurn * 0x9E3779B97F4A7C15UL;   // differs every turn
        int hiddenEnd = -1;   // the members of a hidden group
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly CompositeItem it = ref items[i];
            int occ = PpOccurrence(it.Kind, it.SliceId);
            _ppAddItem = i; _ppAddSlice = it.SliceId; _ppAddKind = it.Kind;
            Mix(ref structure, (ulong)(uint)it.Kind << 32 | (uint)it.SliceId);   // counted for hidden items too: their peers keep their identity
            if (i < hiddenEnd || Hidden(i, items.Length))
            {
                // hidden under a later opaque item: it paints no pixel, so it never repaints one (D3D12Device.Occlusion.cs)
                if (it.Kind == CompositeKind.Group) hiddenEnd = Math.Max(hiddenEnd, i + 1 + it.GroupCount);
                _itemFoot[i] = default;
                continue;
            }
            ulong id = 0xE7A1_0000_0000_0001UL;
            Mix(ref id, (ulong)(uint)occ << 40 | (ulong)(uint)it.Kind << 32 | (uint)it.SliceId);
            ulong sig = PpItemSig(in it);
            PixelRect sci = PpScissor(in it);
            PixelRect foot = default;
            switch (it.Kind)
            {
                case CompositeKind.Tiles:
                case CompositeKind.Region:
                    if (it.BlurSigma <= 0f && (_frameKnockouts & GpuKnockouts.ForceFullDirect) == 0)
                    {
                        ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
                        for (int p = 0; p < placed.Length; p++)
                        {
                            PixelRect r = PpIntersect(PpPx(PlacementRect(in it, in placed[p])), in sci);
                            ulong k = id; Mix(ref k, (ulong)(ushort)placed[p].Key.Tx << 16 | (ushort)placed[p].Key.Ty);
                            ulong s = sig; Mix(ref s, (ulong)(uint)placed[p].Surface);
                            Mix(ref s, (ulong)(uint)placed[p].W << 32 | (uint)placed[p].H);
                            PpAdd(k, s, in r, placed[p].Surface, (int)it.Transform.Dx + placed[p].Key.Tx * TileGrid.W,
                                (int)it.Transform.Dy + placed[p].Key.Ty * TileGrid.H, _surfaces!.TileSerial(placed[p].Surface));
                            foot = PpUnion(in foot, in r);
                        }
                        break;
                    }
                    goto default;   // a blurred leaf or the all-direct knockout composites a prepared surface
                case CompositeKind.Backdrop:
                {
                    foot = PpIntersect(PpPx(it.RoundClip), in sci);
                    ulong s = sig; Mix(ref s, _itemKey[i] != 0 ? _itemKey[i] : fresh);
                    PpAdd(id, s, in foot);
                    break;
                }
                case CompositeKind.EraseVideoHole:
                    foot = PpIntersect(PpPx(it.Clip), in sci);
                    PpAdd(id, sig, in foot);
                    break;
                default:
                {
                    // a prepared surface (group, self-blur, degraded / low-resolution segment): its region or its chunks
                    if (_itemSurface[i] >= 0) foot = PpIntersect(in _itemRegion[i], in sci);
                    int end = _itemChunkStart[i] + _itemChunkCount[i];
                    for (int c = _itemChunkStart[i]; c < end; c++) foot = PpUnion(in foot, PpIntersect(_chunks[c].Rect, in sci));
                    ulong s = sig;
                    if (it.Kind == CompositeKind.Group && it.BlurSigma <= 0f && TileDamage.PrecisePresent)
                    {
                        // An unblurred group composites each pixel from its members' pixels at that pixel (alpha, feather,
                        // rounded clip): what changed inside it is exactly what its members' own entries dirty, so its
                        // signature is its composite parameters and region alone — a member's sub-tile re-raster repaints
                        // that much of the group, not the whole group.
                    }
                    else if (_itemKey[i] != 0 && (it.Kind != CompositeKind.Direct || it.LowResDown > 1)) Mix(ref s, _itemKey[i]);
                    else if (it.Kind == CompositeKind.Direct && it.BlurSigma <= 0f && !PpDamaged(in frame, in foot) && RowOf(it.SliceId) is int row and >= 0)
                    {
                        // A DEGRADED segment re-rasters every turn, but from its bytes alone: the same stream (+ prefix) at the
                        // same placement paints the same pixels — unless repaint damage reaches it (an image's content can
                        // change under byte-identical commands), which takes the fresh signature below.
                        MixBytes(ref s, frame.PrefixOf(in frame.Slices[row]));
                        MixBytes(ref s, frame.StreamOf(in frame.Slices[row]));
                    }
                    else Mix(ref s, fresh);
                    PpAdd(id, s, in foot);
                    break;
                }
            }
            _itemFoot[i] = foot;
        }

        _ppDirty = default;
        // A different item STRUCTURE no longer forces the whole frame: the painter-order match (PpMatch) dirties exactly
        // where the items that appeared, vanished or moved in the order were and are.
        _ppDirtyFull = !_ppPrevValid || (!TileDamage.PrecisePresent && structure != _ppPrevStructure) || _w != _ppPrevW || _h != _ppPrevH
            || _frameScale != _ppPrevScale || !frame.Info.Clear.Equals(_ppPrevClear) || _frameKnockouts != _ppPrevKnockouts
            || sc.PpEpoch != _ppPrevEpoch || frame.Info.RepaintDamage.FullReason == RepaintFullReason.TargetInvalidated;
        if (!_ppDirtyFull) PpMatch();
        // this frame becomes the baseline of the next one
        (_ppPrev, _ppCur) = (_ppCur, _ppPrev);
        _ppPrevN = _ppCurN;
        if (_ppCur.Length < _ppPrev.Length) _ppCur = new PpEntry[_ppPrev.Length];
        _ppPrevValid = true;
        _ppPrevStructure = structure;
        _ppPrevW = _w; _ppPrevH = _h; _ppPrevScale = _frameScale; _ppPrevClear = frame.Info.Clear; _ppPrevKnockouts = _frameKnockouts;
        _ppPrevEpoch = sc.PpEpoch;
    }

    /// <summary>Does this frame's repaint damage (window DIP) reach <paramref name="rect"/> (window px)? Full damage reaches all.</summary>
    private bool PpDamaged(in CompositeFrame frame, in PixelRect rect)
    {
        var damage = frame.Info.RepaintDamage;
        return !rect.IsEmpty && DamageReaches(in damage, in rect);
    }

    private void PpDirtyAdd(in PixelRect r)
    {
        if (r.IsEmpty) return;
        _ppDirty.Add(new RectF(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
    }

    /// <summary>Step 2: the repaint set for the back buffer this frame renders into — this frame's dirty set ∪ the dirty
    /// sets of every turn since that buffer last received a frame — or the whole-frame route.</summary>
    private void PpPlanRepaint(D3D12Swapchain sc, TargetFrameState f)
    {
        _ppPartial = false;
        _ppRepaintN = 0;
        if (!sc.SequentialFlip || _ppDirtyFull || (_frameKnockouts & GpuKnockouts.FullPresent) != 0) return;
        int idx = (int)f.FrameIndex;
        if ((uint)idx >= (uint)_ppBufTurn.Length) return;
        int bufTurn = _ppBufTurn[idx];
        if (bufTurn == 0 || _ppBufEpoch[idx] != sc.PpEpoch || _compositeTurn - bufTurn > PpHistory) return;
        RepaintDamageRegion repaint = _ppDirty;
        for (int t = bufTurn + 1; t < _compositeTurn; t++)
        {
            int h = t % PpHistory;
            if (_ppHistTurn[h] != t || _ppHistFull[h]) return;   // a turn we cannot account for: whole frame
            repaint.Union(in _ppHist[h]);
        }
        float window = (float)_w * _h;
        if (window <= 0f || repaint.SummedArea() >= PpFullCoverage * window) return;
        ReadOnlySpan<RectF> rects = repaint.AsSpan();
        for (int k = 0; k < rects.Length; k++)
        {
            PixelRect p = PpPx(rects[k]);
            if (!p.IsEmpty) _ppRepaint[_ppRepaintN++] = p;
        }
        _ppPartial = true;
    }

    /// <summary>Step 4 bookkeeping, after the composite pass: remember this turn's dirty set, which turn the back buffer
    /// now holds, and stage the Present1 dirty rects on the target.</summary>
    private void PpEndFrame(D3D12Swapchain sc, TargetFrameState f)
    {
        int h = _compositeTurn % PpHistory;
        _ppHistTurn[h] = _compositeTurn;
        _ppHistFull[h] = _ppDirtyFull;
        _ppHist[h] = _ppDirtyFull ? default : _ppDirty;
        int idx = (int)f.FrameIndex;
        if ((uint)idx < (uint)_ppBufTurn.Length) { _ppBufTurn[idx] = _compositeTurn; _ppBufEpoch[idx] = sc.PpEpoch; }

        sc.PpPresentCount = 0;
        bool partialPresent = sc.SequentialFlip && !_ppDirtyFull && (_frameKnockouts & GpuKnockouts.FullPresent) == 0 && _ppDirty.Count > 0;
        if (partialPresent)
        {
            ReadOnlySpan<RectF> rects = _ppDirty.AsSpan();
            for (int k = 0; k < rects.Length; k++)
            {
                PixelRect p = PpPx(rects[k]);
                if (p.IsEmpty) continue;
                sc.PpPresentRects[sc.PpPresentCount++] = new RECT { left = p.Left, top = p.Top, right = p.Right, bottom = p.Bottom };
            }
        }
        // Nothing changed since the last presented frame (the diff is trusted and empty): the back buffer holds exactly the
        // pixels on screen, so a one-pixel dirty rect is a true description — DWM then recomposes one pixel instead of the
        // whole window, as a Present without rects asks it to.
        if (sc.PpPresentCount == 0 && TileDamage.PrecisePresent && sc.SequentialFlip && !_ppDirtyFull
            && (_frameKnockouts & GpuKnockouts.FullPresent) == 0 && _w > 0 && _h > 0)
        {
            sc.PpPresentRects[0] = new RECT { left = 0, top = 0, right = 1, bottom = 1 };
            sc.PpPresentCount = 1;
            partialPresent = true;
        }
        float window = (float)_w * _h;
        long presentPx = 0, dirtyPx = 0, repaintPx = 0;
        for (int k = 0; k < sc.PpPresentCount; k++)
            presentPx += (long)(sc.PpPresentRects[k].right - sc.PpPresentRects[k].left) * (sc.PpPresentRects[k].bottom - sc.PpPresentRects[k].top);
        if (!partialPresent) presentPx = (long)window;
        if (_ppDirtyFull) dirtyPx = (long)window;
        else
        {
            ReadOnlySpan<RectF> dr = _ppDirty.AsSpan();
            for (int k = 0; k < dr.Length; k++) { PixelRect p = PpPx(dr[k]); dirtyPx += (long)(p.Right - p.Left) * (p.Bottom - p.Top); }
        }
        if (_ppPartial) for (int k = 0; k < _ppRepaintN; k++) repaintPx += (long)(_ppRepaint[k].Right - _ppRepaint[k].Left) * (_ppRepaint[k].Bottom - _ppRepaint[k].Top);
        else repaintPx = (long)window;
        NoteCompositeCensus(repaintPx, presentPx, dirtyPx, !_ppPartial);
        LastPartialCoverage = !_ppPartial || window <= 0f ? 1f : RepaintCoverage(window);
        LastPartialRects = _ppPartial ? _ppRepaintN : 0;
        if (_ppPartial) PartialFrameCount++;
        Diag.Set("d3d12", "partialRects", LastPartialRects);
    }

    private float RepaintCoverage(float window)
    {
        long a = 0;
        for (int k = 0; k < _ppRepaintN; k++) a += (long)(_ppRepaint[k].Right - _ppRepaint[k].Left) * (_ppRepaint[k].Bottom - _ppRepaint[k].Top);
        return System.Math.Clamp(a / window, 0f, 1f);
    }

    /// <summary>The PRESERVE composite: per repaint rect, clear it to the frame's clear colour (a copy, like the CLEAR load
    /// op writes) and draw every item that touches it, scissored to it.</summary>
    private void PpCompositeRects(in CompositeFrame frame)
    {
        for (int k = 0; k < _ppRepaintN; k++)
        {
            PixelRect r = _ppRepaint[k];
            _frameClip = r;
            _frameClipOn = true;
            _compositor!.Scissor(_cmdList, r.Left, r.Top, r.Right, r.Bottom);
            _compositor.Begin(r.Left, r.Top, r.Right, r.Bottom);
            _compositor.Color(frame.Info.Clear);
            _compositor.Draw(_cmdList, SliceCompositor.Pso.FillCopy, default);
            if ((_frameKnockouts & GpuKnockouts.ClearOnly) == 0) DrawRange(in frame, 0, frame.Items.Length, 0, 0, (int)_w, (int)_h, -1);
        }
        _frameClipOn = false;
    }
}
