using FluentGpu.Hosting.Threading;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;

namespace FluentGpu.Rhi.Headless;

/// <summary>One recorded composite operation (the headless model of <see cref="IGpuDevice.SubmitComposite"/>).</summary>
public enum CompositeRecordKind : byte
{
    /// <summary>A render pass opened on <see cref="CompositeRecord.Target"/> with <see cref="CompositeRecord.Load"/> → STORE.</summary>
    BeginPass,
    /// <summary>One tile replayed into <see cref="CompositeRecord.Surface"/> (inside a tile-surface pass).</summary>
    RasterTile,
    /// <summary>The open render pass closed.</summary>
    EndPass,
    /// <summary>One composite item drawn into the back buffer (inside the back-buffer pass).</summary>
    DrawItem,
    /// <summary>The Present1 parameters staged for the primary target's next Present.</summary>
    StagePresent,
    /// <summary>A <see cref="CompositeKind.Group"/> item's surface prepared (offscreen phase): <see cref="CompositeRecord.Key"/>
    /// = its content key (<c>GroupCacheKey</c>; 0 = not cacheable), <see cref="CompositeRecord.Hit"/> = re-drawn from the
    /// retained surface instead of rendered (<see cref="CompositeRecord.ItemIndex"/> names the group item).</summary>
    PrepareGroup,
}

/// <summary>One <see cref="CompositeKind.EraseVideoHole"/> item of the most recent composite, as the headless model draws it
/// (it keeps no pixels, so the erase is recorded where it acts): <see cref="ItemIndex"/> = the item; <see cref="RectPx"/> =
/// the rect it clears to transparent (its clip, device px); <see cref="ComposedOffset"/> = its painter position in
/// <see cref="HeadlessGpuDevice.LastComposedStream"/> — every composed op before that byte offset lies UNDER the erase
/// (cleared inside the rect), every op from it on paints OVER the hole; <see cref="GroupDepth"/> = the
/// <see cref="CompositeKind.Group"/> items enclosing it (inside one it clears that group's surface, never the back buffer —
/// gpu-renderer.md §7.3).</summary>
public readonly record struct CompositeHoleErase(int ItemIndex, FluentGpu.Foundation.RectF RectPx, int ComposedOffset, int GroupDepth);

/// <summary>What a render pass targets.</summary>
public enum CompositePassTarget : byte { TileSurface, BackBuffer }

/// <summary>A render pass's beginning access (the ending access is always STORE in the composite model).</summary>
public enum CompositePassLoad : byte { Clear, Discard, Preserve }

/// <summary>One entry of <see cref="HeadlessGpuDevice.LastCompositeRecords"/>. Fields not meaningful for
/// <see cref="Kind"/> keep their defaults (<see cref="Surface"/>/<see cref="ItemIndex"/> −1).</summary>
public readonly record struct CompositeRecord(
    CompositeRecordKind Kind,
    CompositePassTarget Target = CompositePassTarget.BackBuffer,
    CompositePassLoad Load = CompositePassLoad.Discard,
    int Surface = -1,
    TileKey Tile = default,
    InvalidationReason Reason = InvalidationReason.None,
    int ItemIndex = -1,
    CompositeItem Item = default,
    int DirtyRectCount = 0,
    bool HasScroll = false,
    PixelRect ScrollRect = default,
    int ScrollDx = 0,
    int ScrollDy = 0,
    ulong Key = 0,
    bool Hit = false);

// The headless composite model: records the op sequence a real backend performs, in order, so the TileSuite gates can
// assert on it (render-pass discipline, raster counts, painter order, present params), and models the frame's primitives
// from the composite items (ModelComposite) so every primitive-level gate reads the composited result.
public sealed partial class HeadlessGpuDevice
{
    private readonly List<CompositeRecord> _compositeRecords = new(64);

    public bool SupportsComposite => true;

    /// <summary>Completed <see cref="SubmitComposite"/> calls.</summary>
    public int CompositeFrameCount { get; private set; }

    /// <summary>The op log of the most recent <see cref="SubmitComposite"/>: per raster a tile-surface pass
    /// (BeginPass CLEAR, RasterTile, EndPass), then ONE back-buffer pass (BeginPass CLEAR — the clear colour written by the
    /// load op, never a read of the previous frame — DrawItem × items, EndPass),
    /// then StagePresent. Cleared (capacity kept) per submit — zero allocation once warmed.</summary>
    public IReadOnlyList<CompositeRecord> LastCompositeRecords => _compositeRecords;

    private readonly List<SliceRow> _compositeSlices = new(16);

    /// <summary>The slice rows of the most recent <see cref="SubmitComposite"/> (their ids name the rastered tiles'
    /// slices; kind / frame say what each is). Rebuilt per submit, capacity kept.</summary>
    public IReadOnlyList<SliceRow> LastCompositeSlices => _compositeSlices;

    /// <summary>The frame context of the most recent <see cref="SubmitComposite"/>.</summary>
    public FrameInfo LastCompositeInfo { get; private set; }

    public void SubmitComposite(in CompositeFrame frame)
    {
        if (_renderConfined) ThreadGuard.AssertRenderOwner();
        _compositeRecords.Clear();
        LastCompositeInfo = frame.Info;

        ReadOnlySpan<TileRaster> rasters = frame.Rasters;
        for (int i = 0; i < rasters.Length; i++)
        {
            TileRaster r = rasters[i];
            _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.BeginPass, CompositePassTarget.TileSurface, CompositePassLoad.Clear, r.Surface, r.Key));
            _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.RasterTile, CompositePassTarget.TileSurface, CompositePassLoad.Clear, r.Surface, r.Key, r.Reason));
            _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.EndPass, CompositePassTarget.TileSurface, CompositePassLoad.Clear, r.Surface, r.Key));
            _serials.Bump(r.Surface);
        }

        // The offscreen phase's GROUP surfaces, nested first (as a backend prepares them): each keyed by GroupCacheKey and
        // looked up in the modelled retained set — a hit re-draws the retained surface, a miss renders and retains it.
        _groupTurn++;
        PrepareGroups(in frame, 0, frame.Items.Length);
        _groupRetained.RemoveAll(static g => g.Turn < 0);

        ReadOnlySpan<CompositeItem> items = frame.Items;
        _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.BeginPass, CompositePassTarget.BackBuffer, CompositePassLoad.Clear));
        for (int i = 0; i < items.Length; i++)
            _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.DrawItem, ItemIndex: i, Item: items[i]));
        _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.EndPass, CompositePassTarget.BackBuffer, CompositePassLoad.Clear));
        _compositeSlices.Clear();
        for (int i = 0; i < frame.Slices.Length; i++) _compositeSlices.Add(frame.Slices[i]);

        PresentParams p = frame.Present;
        _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.StagePresent, DirtyRectCount: p.DirtyRects.Length, HasScroll: p.HasScroll,
            ScrollRect: p.ScrollRect, ScrollDx: p.ScrollDx, ScrollDy: p.ScrollDy));
        CompositeFrameCount++;
        LastCompositeItemCount = items.Length;
        LastCompositeRasterCount = rasters.Length;
        LastCompositeSliceCount = frame.Slices.Length;

        // Every scheduled raster lands (the headless model never drops an instance nor waits on an upload).
        for (int i = 0; i < frame.RasterDone.Length; i++) frame.RasterDone[i] = 1;

        // The PIXEL model of the composited frame, decoded from the composite ops themselves: each placed segment's stream
        // at its item's posed offset (window DIP), in painter order — a segment continues the scopes its slice's earlier
        // segments opened, exactly as the slice's own stream nests its children — and a layer item's (or group's) marker
        // layer around what it composites. Backdrops and video holes add no primitive (the acrylic slice's own PushLayer
        // and the DrawVideo op are in the streams); each EraseVideoHole item is recorded at its painter position in the
        // composed stream instead (LastHoleErases).
        ModelComposite(in frame);
    }

    private readonly List<(int End, int Layers, bool Clipped)> _groupEnds = new(8);
    private readonly DrawList _composed = new(4096);
    private readonly List<CompositeHoleErase> _holeErases = new(4);

    /// <summary>The <see cref="CompositeKind.EraseVideoHole"/> items of the most recent composite, in painter order, each at
    /// its position in <see cref="LastComposedStream"/> (see <see cref="CompositeHoleErase"/>). Rebuilt per composite,
    /// capacity kept.</summary>
    public IReadOnlyList<CompositeHoleErase> LastHoleErases => _holeErases;

    // ── the modelled group-surface cache (GroupCacheKey) ──
    /// <summary>A retained group surface unused for this many composites is dropped (the backend's RetainTurns).</summary>
    private const int GroupRetainTurns = 30;
    private struct Serials : ITileSerials
    {
        public uint[]? S;
        public void Bump(int surface)
        {
            if (surface < 0) return;
            if (S is null || surface >= S.Length) Array.Resize(ref S, Math.Max(64, Math.Max(surface + 1, (S?.Length ?? 0) * 2)));
            S[surface]++;
        }
        public readonly uint Serial(int surface) => S is not null && (uint)surface < (uint)S.Length ? S[surface] : 0u;
    }
    private Serials _serials;
    private ulong[] _groupKeys = new ulong[64];
    /// <summary>At most this many retained group surfaces are modelled; past it a miss replaces the least recently used
    /// one — fixed storage, like the backend's pool bounded by <c>TileBudget.RetainedShare</c> (a scroll that misses every
    /// turn must not grow the model's list inside a measured frame).</summary>
    private const int GroupRetainedCap = 64;
    private readonly List<(ulong Key, int Turn)> _groupRetained = new(GroupRetainedCap);
    private int _groupTurn;
    private int _groupRenders, _groupHits;

    private void PrepareGroups(in CompositeFrame frame, int a, int b)
    {
        ReadOnlySpan<CompositeItem> items = frame.Items;
        if (a == 0)
        {
            if (_groupKeys.Length < items.Length) _groupKeys = new ulong[Math.Max(items.Length, _groupKeys.Length * 2)];
            Array.Clear(_groupKeys, 0, items.Length);
            _groupRenders = _groupHits = 0;
        }
        for (int i = a; i < b && i < items.Length; i++)
        {
            ref readonly CompositeItem it = ref items[i];
            if (it.Kind != CompositeKind.Group) continue;
            int end = Math.Min(items.Length, i + 1 + it.GroupCount);
            PrepareGroups(in frame, i + 1, end);
            int halo = it.BlurSigma > 0f ? SelfBlurRegion.TapRadius(it.BlurSigma) : 0;
            PixelRect region = GroupCacheKey.Region(in it, halo, (int)frame.Info.SizePx.Width, (int)frame.Info.SizePx.Height);
            if (!region.IsEmpty)
            {
                ulong key = GroupCacheKey.Compute(in frame, i, in region, ref _serials, _groupKeys, out bool cacheable);
                bool hit = false;
                if (cacheable)
                {
                    _groupKeys[i] = key;
                    for (int g = 0; g < _groupRetained.Count; g++)
                        if (_groupRetained[g].Key == key) { _groupRetained[g] = (key, _groupTurn); hit = true; break; }
                    if (!hit && _groupRetained.Count < GroupRetainedCap) _groupRetained.Add((key, _groupTurn));
                    else if (!hit)
                    {
                        int lru = 0;
                        for (int g = 1; g < _groupRetained.Count; g++) if (_groupRetained[g].Turn < _groupRetained[lru].Turn) lru = g;
                        _groupRetained[lru] = (key, _groupTurn);
                    }
                }
                if (hit) _groupHits++; else _groupRenders++;
                // evidence (the composite item record): which groups re-drew from a retained surface, which rendered
                if (i < frame.ItemFlags.Length) frame.ItemFlags[i] |= hit ? CompositeFrameFlags.ItemGroupHit : CompositeFrameFlags.ItemGroupRendered;
                _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.PrepareGroup, ItemIndex: i, Item: it,
                    Key: cacheable ? key : 0UL, Hit: hit));
            }
            i = end - 1;
        }
        if (a == 0)
        {
            for (int g = 0; g < _groupRetained.Count; g++)
                if (_groupTurn - _groupRetained[g].Turn > GroupRetainTurns) _groupRetained[g] = (_groupRetained[g].Key, -1);
            LastCompositeCache = new CompositeCacheStats(_groupRenders, _groupHits, 0L);
        }
    }

    /// <summary>The most recent composite's modelled frame as ONE painter-ordered command stream (every placed segment
    /// at its posed offset, culled to its item clip by the slice span index, each narrower composite clip and marker
    /// layer re-emitted around what it bounds) — for gates that decode a byte stream. Rebuilt per composite.</summary>
    public DrawList LastComposedStream => _composed;

    private void ModelComposite(in CompositeFrame frame)
    {
        BeginModel(in frame.Info);
        _composed.Reset();
        _modelClipTop.Clear();
        _acrylicLayerItems.Clear();
        _holeErases.Clear();
        int balance = 0, layerBalance = 0, stencilBalance = 0;
        float scale = frame.Info.Scale > 0f ? frame.Info.Scale : 1f;
        ReadOnlySpan<CompositeItem> items = frame.Items;
        _groupEnds.Clear();
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly CompositeItem it = ref items[i];
            switch (it.Kind)
            {
                case CompositeKind.Group:
                {
                    // the group surface is DRAWN under its item clip (a backend scissors the group's composite with it — a
                    // sticky group's band line lives there), then the distributed ancestor fades it carries (outer first),
                    // then its own layer
                    bool gclipped = PushItemClip(in it, scale, ref balance);
                    int pushed = EmitInherited(in frame, i, in it, ref layerBalance);
                    if (it.HasLayer != 0 && i < frame.ItemLayers.Length) { EmitLayer(frame.ItemLayers[i], ref layerBalance); pushed++; }
                    _groupEnds.Add((i + it.GroupCount, pushed, gclipped));
                    break;
                }
                case CompositeKind.Tiles:
                case CompositeKind.Region:
                case CompositeKind.Direct:
                {
                    int r = frame.RowIndexOf(it.SliceId);
                    if (r < 0) break;
                    ref readonly SliceRow row = ref frame.Slices[r];
                    float dx = (it.Transform.Dx - row.Frame.OriginX) / scale, dy = (it.Transform.Dy - row.Frame.OriginY) / scale;
                    // The item's composite clip becomes a clip scope when it is narrower than the stream's open clip (a
                    // band clip, a marker's outer clip) — the structure the slice's own stream nests its children in.
                    bool clipped = PushItemClip(in it, scale, ref balance);
                    int inheritedPushed = EmitInherited(in frame, i, in it, ref layerBalance);
                    bool layer = it.HasLayer != 0 && i < frame.ItemLayers.Length;
                    if (layer) EmitLayer(frame.ItemLayers[i], ref layerBalance);
                    ReadOnlySpan<SliceSpan> spans = row.SpanIndexCount > 0 && row.SpanIndexStart + row.SpanIndexCount <= frame.SliceSpans.Length
                        ? frame.SliceSpans.Slice(row.SpanIndexStart, row.SpanIndexCount) : default;
                    _modelItem = i;
                    DecodeInto(frame.StreamOf(in row), dx, dy, ref balance, ref layerBalance, ref stencilBalance, spans, it.Clip,
                        it.Transform.Dx, it.Transform.Dy, compose: true);
                    _modelItem = -1;
                    if (layer) EmitPopLayer(ref layerBalance);
                    for (int k = 0; k < inheritedPushed; k++) EmitPopLayer(ref layerBalance);
                    if (clipped) EmitPopClip(ref balance);
                    break;
                }
                case CompositeKind.EraseVideoHole:
                    // The backend clears the item's rect to transparent (a DestOut quad) in whatever target is bound. The
                    // model keeps no pixels, so it records the erase where it acts: at this point of the composed stream
                    // (everything composed so far lies under it, everything after paints over the hole) and inside how
                    // many groups (an enclosed erase clears the group surface, not the back buffer).
                    _holeErases.Add(new CompositeHoleErase(i, it.Clip, _composed.Bytes.Length, _groupEnds.Count));
                    break;
            }
            // close every group whose last enclosed item was this one
            for (int g = _groupEnds.Count - 1; g >= 0 && _groupEnds[g].End <= i; g--)
            {
                int pops = _groupEnds[g].Layers;
                bool gclip = _groupEnds[g].Clipped;
                _groupEnds.RemoveAt(g);
                for (int k = 0; k < pops; k++) EmitPopLayer(ref layerBalance);
                if (gclip) EmitPopClip(ref balance);
            }
        }
        EndModel(balance, layerBalance, stencilBalance);
    }

    private bool PushItemClip(in CompositeItem it, float scale, ref int balance)
    {
        if (it.Clip.W <= 0f && it.Clip.H <= 0f && it.Clip.X == 0f && it.Clip.Y == 0f) return false;   // unbounded
        var dip = new FluentGpu.Foundation.RectF(it.Clip.X / scale, it.Clip.Y / scale, it.Clip.W / scale, it.Clip.H / scale);
        if (_modelClipTop.Count > 0)
        {
            var top = _modelClipTop[^1];
            const float eps = 0.51f;
            // not narrower than the clip already open: nothing to add
            if (dip.X <= top.X + eps && dip.Y <= top.Y + eps && dip.Right >= top.Right - eps && dip.Bottom >= top.Bottom - eps) return false;
            dip = dip.Intersect(top);
        }
        var round = it.RoundClip.W > 0f
            ? new FluentGpu.Foundation.RectF(it.RoundClip.X / scale, it.RoundClip.Y / scale, it.RoundClip.W / scale, it.RoundClip.H / scale)
            : default;
        var c = new ClipCmd(dip, round, it.RoundClip.W > 0f ? it.ClipRadii.TopLeft / scale : 0f);
        _clips.Add(c);
        _modelClipTop.Add(dip);
        balance++;
        System.Runtime.InteropServices.MemoryMarshal.Write(_composed.AppendOp(DrawOp.PushClip, System.Runtime.CompilerServices.Unsafe.SizeOf<ClipCmd>(), 0), in c);
        return true;
    }

    private void EmitPopClip(ref int balance)
    {
        balance--;
        if (_modelClipTop.Count > 0) _modelClipTop.RemoveAt(_modelClipTop.Count - 1);
        _composed.AppendOp(DrawOp.PopClip, 0, 0);
    }

    /// <summary>Re-emit the edge fades DISTRIBUTED onto item <paramref name="i"/> (outer first) as layers around it — the
    /// model composes exactly what the group route would have wrapped it in. Returns how many were pushed.</summary>
    private int EmitInherited(in CompositeFrame frame, int i, in CompositeItem it, ref int layerBalance)
    {
        int n = Math.Min((int)it.Inherited, 2);
        for (int k = 0; k < n; k++) EmitLayer(frame.InheritedLayer(i, k), ref layerBalance);
        return n;
    }

    private void EmitLayer(in PushLayerCmd l, ref int layerBalance)
    {
        _layers.Add(l);
        layerBalance++;
        System.Runtime.InteropServices.MemoryMarshal.Write(_composed.AppendOp(DrawOp.PushLayer, System.Runtime.CompilerServices.Unsafe.SizeOf<PushLayerCmd>(), 0), in l);
    }

    private void EmitPopLayer(ref int layerBalance)
    {
        layerBalance--;
        System.Runtime.InteropServices.MemoryMarshal.Write(_composed.AppendOp(DrawOp.PopLayer, System.Runtime.CompilerServices.Unsafe.SizeOf<PopLayerCmd>(), 0),
            new PopLayerCmd(default));
    }

    /// <summary>The modelled group-surface cache of the most recent <see cref="SubmitComposite"/> (see
    /// <see cref="CompositeRecordKind.PrepareGroup"/>).</summary>
    public CompositeCacheStats LastCompositeCache { get; private set; }

    /// <summary>Back-buffer captures asked of the model (<see cref="IGpuDevice.TryCaptureBackBuffer"/>): the headless model
    /// has no pixels to read, so it counts the request and answers false — the host still completes the capture with the
    /// turn's ledger frame (<c>gate.tiles.capture-seq-aligned</c>).</summary>
    public int CaptureRequests { get; private set; }

    public bool TryCaptureBackBuffer(out byte[]? bgra, out int width, out int height)
    {
        CaptureRequests++;
        bgra = null;
        width = height = 0;
        return false;
    }

    /// <summary>Items / rasters / slice rows of the most recent <see cref="SubmitComposite"/>.</summary>
    public int LastCompositeItemCount { get; private set; }
    public int LastCompositeRasterCount { get; private set; }
    public int LastCompositeSliceCount { get; private set; }
}
