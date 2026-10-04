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
/// gpu-renderer.md §7.3). <see cref="Alpha"/> = the erase strength (the punch's VideoReady x opacity), <see cref="RoundRectPx"/> +
/// <see cref="Radii"/> = the rounded rect the erase is clipped to (empty = the quad itself; F078).</summary>
public readonly record struct CompositeHoleErase(int ItemIndex, FluentGpu.Foundation.RectF RectPx, int ComposedOffset, int GroupDepth,
    float Alpha = 1f, FluentGpu.Foundation.CornerRadius4 Radii = default, FluentGpu.Foundation.RectF RoundRectPx = default);

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

    /// <summary>The slice-table owner (<see cref="CompositeFrame.OwnerToken"/>) of the most recent stamped composite; 0 before
    /// any. The headless model has no pool to corrupt, but exposing it lets a gate assert which host composited.</summary>
    public int LastCompositeOwner { get; private set; }

    /// <summary>The swapchain the most recent <see cref="SubmitComposite"/> was handed as its target; null before any. Lets a gate
    /// assert that a detached pop-out never reached the composite route and that the primary host did.</summary>
    public ISwapchain? LastCompositeTarget { get; private set; }

    /// <summary>Opt in to the D3D12 backend's rule that <see cref="SubmitComposite"/> accepts only the device's first-created
    /// (primary) swapchain, throwing for any other target. Off by default: many suites run several ordinary primary hosts on one
    /// device, and the headless model has no device-wide tile pool for a second host to corrupt. A routing gate turns it on so a
    /// detached pop-out that took the composite route fails loudly instead of being recorded.</summary>
    public bool RejectNonPrimaryComposite { get; init; }

    public void SubmitComposite(in CompositeFrame frame, ISwapchain target)
    {
        if (_renderConfined) ThreadGuard.AssertRenderOwner();
        // D3D12 draws the composite into the PRIMARY target only (its one device-wide tile pool); the headless model rejects a
        // secondary target the same way only when a routing gate opts in (RejectNonPrimaryComposite).
        if (RejectNonPrimaryComposite && !ReferenceEquals(target, _primarySwapchain))
            throw new InvalidOperationException("SubmitComposite composites into the PRIMARY swapchain only; a secondary target (detached pop-out / popup) must use SubmitDrawList.");
        if (frame.OwnerToken != 0) LastCompositeOwner = frame.OwnerToken;
        LastCompositeTarget = target;
        _compositeRecords.Clear();
        LastCompositeInfo = frame.Info;
        _groupTurn++;
        LastLostPlacements = 0;

        // The table's trims FIRST (the backend applies them at turn start): the only way a tile texture goes away.
        ApplyTrims(in frame);

        ReadOnlySpan<TileRaster> rasters = frame.Rasters;
        for (int i = 0; i < rasters.Length; i++)
        {
            TileRaster r = rasters[i];
            _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.BeginPass, CompositePassTarget.TileSurface, CompositePassLoad.Clear, r.Surface, r.Key));
            _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.RasterTile, CompositePassTarget.TileSurface, CompositePassLoad.Clear, r.Surface, r.Key, r.Reason));
            _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.EndPass, CompositePassTarget.TileSurface, CompositePassLoad.Clear, r.Surface, r.Key));
            _serials.Bump(r.Surface);
            TexRastered(r.Surface);
        }
        NotePlacedUnsampled(in frame);

        // The offscreen phase, as a backend prepares it: GROUP surfaces nested first and LEAF self-blurs, each keyed
        // (GroupCacheKey.Compute / GroupCacheKey.LeafBlur) and looked up in the modelled retained set — a hit re-draws the
        // retained surface and SAMPLES NO TILE, a miss renders it from its tiles and retains it.
        PrepareOffscreen(in frame);
        _groupRetained.RemoveAll(static g => g.Turn < 0);

        // The composite pass samples every top-level plain (unblurred) Tiles/Region item's placements.
        SampleRange(in frame, 0, frame.Items.Length);

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

    // ── the modelled group-surface / leaf self-blur cache (GroupCacheKey) ──
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
        /// <summary>A trimmed slot's texture is gone: its serial restarts (the backend's retired entry is reset whole).</summary>
        public readonly void Reset(int surface) { if (S is not null && (uint)surface < (uint)S.Length) S[surface] = 0u; }
        public readonly uint Serial(int surface) => S is not null && (uint)surface < (uint)S.Length ? S[surface] : 0u;
    }
    private Serials _serials;
    private ulong[] _groupKeys = new ulong[64];
    /// <summary>At most this many retained group / leaf-blur surfaces are modelled; past it a miss replaces the least
    /// recently used one — fixed storage, like the backend's pool bounded by <c>TileBudget.RetainedShare</c> (a scroll that
    /// misses every turn must not grow the model's list inside a measured frame).</summary>
    private const int GroupRetainedCap = 64;
    private readonly List<(ulong Key, int Turn)> _groupRetained = new(GroupRetainedCap);
    private int _groupTurn;
    private int _groupRenders, _groupHits;

    // ── the modelled tile-surface TEXTURES (what a backend holds per tile slot) ──
    // A slot is BACKED once a raster lands in it and stays backed until the table names it in CompositeFrame.TrimSurfaces —
    // exactly the D3D12 SurfacePool. "Last sampled" = the composite turn the slot was last rastered or drawn FROM (a plain
    // Tiles/Region item, a group or leaf self-blur rendered from its tiles); a retained group / blur re-drawn on a key hit
    // samples none of its tiles. That clock is what the pre-fix pool trimmed by, so it is kept as the gate's precondition.
    private bool[] _texBacked = new bool[64];
    private int[] _texLastSample = new int[64];
    private int[] _texRasterTurn = new int[64];

    /// <summary>Placements the most recent composite drew from a slot that held NO texture (a valid tile that composited
    /// nothing — the idle "content disappears" defect). Must be 0.</summary>
    public int LastLostPlacements { get; private set; }
    /// <summary><see cref="LastLostPlacements"/> summed over every composite of this device.</summary>
    public long LostPlacementsTotal { get; private set; }
    /// <summary>The longest any PLACED (valid, not re-rastered) tile went — in composite turns — without the composite
    /// sampling it: a tile consumed only through a retained group / leaf self-blur result. The pre-fix D3D12 pool retired a
    /// tile texture unsampled for more than <c>SliceTable.IdleEvictFrames + 120</c> turns, so a value past that is this
    /// run reaching the defect's trigger.</summary>
    public int MaxPlacedUnsampledTurns { get; private set; }
    /// <summary>Tile textures released because the table named the slot (<see cref="CompositeFrame.TrimSurfaces"/>).</summary>
    public long TrimmedTextures { get; private set; }
    /// <summary>Trims naming a slot that a placement of the SAME frame samples (must be 0: the table never trims a held slot).</summary>
    public long TrimmedWhilePlaced { get; private set; }
    /// <summary>Leaf self-blurs re-drawn from their retained result (no tile sampled) / rendered from their tiles.</summary>
    public long LeafBlurHits { get; private set; }
    public long LeafBlurRenders { get; private set; }
    /// <summary>Group surfaces re-drawn from their retained result, summed over every composite.</summary>
    public long GroupHitsTotal { get; private set; }

    private void TexEnsure(int slot)
    {
        if (slot < _texBacked.Length) return;
        int n = Math.Max(slot + 1, _texBacked.Length * 2);
        Array.Resize(ref _texBacked, n);
        Array.Resize(ref _texLastSample, n);
        Array.Resize(ref _texRasterTurn, n);
    }

    private void TexRastered(int slot)
    {
        if (slot < 0) return;
        TexEnsure(slot);
        _texBacked[slot] = true;
        _texLastSample[slot] = _groupTurn;
        _texRasterTurn[slot] = _groupTurn;
    }

    /// <summary>The composite samples placement slot <paramref name="slot"/>: a slot with no texture is a LOST placement
    /// (the backend skips it — nothing composites where the table believes current pixels are).</summary>
    private void Sample(int slot)
    {
        if (slot < 0) return;
        if (slot >= _texBacked.Length || !_texBacked[slot])
        {
            LastLostPlacements++;
            LostPlacementsTotal++;
            return;
        }
        _texLastSample[slot] = _groupTurn;
    }

    private void ApplyTrims(in CompositeFrame frame)
    {
        ReadOnlySpan<int> trims = frame.TrimSurfaces;
        ReadOnlySpan<TilePlacement> placed = frame.Placements;
        for (int i = 0; i < trims.Length; i++)
        {
            int s = trims[i];
            if (s < 0) continue;
            for (int p = 0; p < placed.Length; p++)
                if (placed[p].Surface == s) { TrimmedWhilePlaced++; break; }
            if (s < _texBacked.Length && _texBacked[s])
            {
                _texBacked[s] = false;
                _serials.Reset(s);
                TrimmedTextures++;
            }
        }
    }

    /// <summary>The defect's trigger, measured: how long each tile placed this turn (and not re-rastered in it) has gone
    /// without the composite sampling its texture.</summary>
    private void NotePlacedUnsampled(in CompositeFrame frame)
    {
        ReadOnlySpan<TilePlacement> placed = frame.Placements;
        for (int p = 0; p < placed.Length; p++)
        {
            int s = placed[p].Surface;
            if ((uint)s >= (uint)_texBacked.Length || !_texBacked[s] || _texRasterTurn[s] == _groupTurn) continue;
            int age = _groupTurn - _texLastSample[s];
            if (age > MaxPlacedUnsampledTurns) MaxPlacedUnsampledTurns = age;
        }
    }

    // The modelled retained leaf self-blurs (keyed by GroupCacheKey.LeafBlur), kept apart from the groups so neither
    // evicts the other's entries in the model.
    private readonly List<(ulong Key, int Turn)> _leafRetained = new(GroupRetainedCap);

    /// <summary>Retained-surface lookup in <paramref name="set"/>: true on a hit (refreshes its turn); on a miss the key is
    /// retained (LRU past the cap — fixed storage).</summary>
    private bool LookupOrRetain(List<(ulong Key, int Turn)> set, ulong key)
    {
        for (int g = 0; g < set.Count; g++)
            if (set[g].Key == key) { set[g] = (key, _groupTurn); return true; }
        if (set.Count < GroupRetainedCap) set.Add((key, _groupTurn));
        else
        {
            int lru = 0;
            for (int g = 1; g < set.Count; g++) if (set[g].Turn < set[lru].Turn) lru = g;
            set[lru] = (key, _groupTurn);
        }
        return false;
    }

    private void PrepareOffscreen(in CompositeFrame frame)
    {
        ReadOnlySpan<CompositeItem> items = frame.Items;
        if (_groupKeys.Length < items.Length) _groupKeys = new ulong[Math.Max(items.Length, _groupKeys.Length * 2)];
        Array.Clear(_groupKeys, 0, items.Length);
        _groupRenders = _groupHits = 0;
        PrepareRange(in frame, 0, items.Length);
        for (int g = 0; g < _groupRetained.Count; g++)
            if (_groupTurn - _groupRetained[g].Turn > GroupRetainTurns) _groupRetained[g] = (_groupRetained[g].Key, -1);
        for (int g = 0; g < _leafRetained.Count; g++)
            if (_groupTurn - _leafRetained[g].Turn > GroupRetainTurns) _leafRetained[g] = (_leafRetained[g].Key, -1);
        _leafRetained.RemoveAll(static g => g.Turn < 0);
        GroupHitsTotal += _groupHits;
        LastCompositeCache = new CompositeCacheStats(_groupRenders, _groupHits, 0L);
    }

    /// <summary>The backend's offscreen phase over items [<paramref name="a"/>, <paramref name="b"/>): a group's enclosed items
    /// first (nested groups, leaf self-blurs), then the group itself; a leaf self-blur on its own.</summary>
    private void PrepareRange(in CompositeFrame frame, int a, int b)
    {
        ReadOnlySpan<CompositeItem> items = frame.Items;
        for (int i = a; i < b && i < items.Length; i++)
        {
            ref readonly CompositeItem it = ref items[i];
            if (it.Kind == CompositeKind.Group)
            {
                int end = Math.Min(items.Length, i + 1 + it.GroupCount);
                PrepareRange(in frame, i + 1, end);
                PrepareGroup(in frame, i, end);
                i = end - 1;
            }
            else if (it.Kind is (CompositeKind.Tiles or CompositeKind.Region) && it.BlurSigma > 0f)
                PrepareLeafBlur(in frame, i);
        }
    }

    private void PrepareGroup(in CompositeFrame frame, int i, int end)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        int halo = it.BlurSigma > 0f ? SelfBlurRegion.TapRadius(it.BlurSigma) : 0;
        PixelRect region = GroupCacheKey.Region(in it, halo, (int)frame.Info.SizePx.Width, (int)frame.Info.SizePx.Height);
        if (region.IsEmpty) return;
        ulong key = GroupCacheKey.Compute(in frame, i, in region, ref _serials, _groupKeys, out bool cacheable);
        bool hit = false;
        if (cacheable)
        {
            _groupKeys[i] = key;
            hit = LookupOrRetain(_groupRetained, key);
        }
        if (hit) _groupHits++;
        else
        {
            _groupRenders++;
            SampleRange(in frame, i + 1, end);   // rendered: its enclosed items draw from their tiles / prepared surfaces
        }
        // evidence (the composite item record): which groups re-drew from a retained surface, which rendered
        if (i < frame.ItemFlags.Length) frame.ItemFlags[i] |= hit ? CompositeFrameFlags.ItemGroupHit : CompositeFrameFlags.ItemGroupRendered;
        _compositeRecords.Add(new CompositeRecord(CompositeRecordKind.PrepareGroup, ItemIndex: i, Item: it,
            Key: cacheable ? key : 0UL, Hit: hit));
    }

    /// <summary>A LEAF self-blur (a Tiles/Region item with σ &gt; 0): keyed like the backend's retained blur
    /// (<see cref="GroupCacheKey.LeafBlur"/>); a hit samples no tile, a miss assembles its source from every placed tile.</summary>
    private void PrepareLeafBlur(in CompositeFrame frame, int i)
    {
        ref readonly CompositeItem it = ref frame.Items[i];
        ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
        if (placed.IsEmpty) return;
        GroupCacheKey.BlurRegions(in it, (int)frame.Info.SizePx.Width, (int)frame.Info.SizePx.Height, out PixelRect src, out PixelRect region);
        if (src.IsEmpty || region.IsEmpty) return;
        ulong key = GroupCacheKey.LeafBlur(in it, placed, in src, in region, ref _serials);
        if (LookupOrRetain(_leafRetained, key)) { LeafBlurHits++; return; }
        LeafBlurRenders++;
        for (int p = 0; p < placed.Length; p++) Sample(placed[p].Surface);
    }

    /// <summary>What the backend's DrawRange samples over items [<paramref name="a"/>, <paramref name="b"/>): every plain
    /// (σ = 0) Tiles/Region item's placements. A group draws its prepared surface (its enclosed items were sampled — or
    /// not, on a hit — when it was prepared); a leaf self-blur draws its prepared blur; a degraded Direct item draws its
    /// transient chunks.</summary>
    private void SampleRange(in CompositeFrame frame, int a, int b)
    {
        ReadOnlySpan<CompositeItem> items = frame.Items;
        for (int i = a; i < b && i < items.Length; i++)
        {
            ref readonly CompositeItem it = ref items[i];
            if (it.Kind == CompositeKind.Group) { i = Math.Min(items.Length, i + 1 + it.GroupCount) - 1; continue; }
            if (it.Kind is not (CompositeKind.Tiles or CompositeKind.Region) || it.BlurSigma > 0f) continue;
            ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
            for (int p = 0; p < placed.Length; p++) Sample(placed[p].Surface);
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
                    _holeErases.Add(new CompositeHoleErase(i, it.Clip, _composed.Bytes.Length, _groupEnds.Count, it.Alpha, it.ClipRadii, it.RoundClip));
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
