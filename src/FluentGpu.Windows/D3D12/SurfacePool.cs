using System.Threading;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

using ColorF = FluentGpu.Foundation.ColorF;
using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// The retained-tile surfaces (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.2/§A.6): ONE BGRA8 texture per
/// logical tile surface slot the <see cref="SliceTable"/> hands out (a full 1024×512 tile, or an effect slice's bucketed
/// region), plus a pool of SCRATCH surfaces for the composite's offscreen work (group surfaces, degraded direct raster,
/// blur sources/levels, acrylic backdrops, inline folded groups). Render-thread-owned, like every <c>ComPtr</c> here.
/// <list type="bullet">
/// <item><b>States:</b> every surface rests in <c>PIXEL_SHADER_RESOURCE</c>; a raster pass transitions it to
/// <c>RENDER_TARGET</c> and back. No surface is ever a copy source or destination.</item>
/// <item><b>Descriptors:</b> RTVs live in a CPU-only heap (consumed at record time); SRVs are written into THIS frame's
/// bank of a shader-visible heap (<see cref="D3D12Device.FrameBankDepth"/> banks), so a recreated surface never rewrites
/// a descriptor an in-flight frame still samples.</item>
/// <item><b>Lifetime:</b> a replaced or trimmed texture retires behind the frame fence (never released while a submit
/// in flight references it). A tile slot's texture is trimmed ONLY when the <see cref="SliceTable"/> names the slot
/// (<see cref="TrimTiles"/> ← <c>CompositeFrame.TrimSurfaces</c>: no tile has held it for
/// <see cref="SliceTable.SurfaceTrimTurns"/> turns) — never on a clock of the pool's own. The pool cannot see every use of
/// a tile: a group / self-blur / acrylic backdrop re-drawn from its RETAINED result samples none of the tiles it was made
/// from, so a "last sampled" clock aged out tiles the table still placed as valid and the composite drew nothing there
/// (content vanished at idle, back only where a hover re-rastered a tile). Idle scratch trims on
/// <see cref="LayerTargetTrim"/>'s windows.</item>
/// <item><b>Retained derived surfaces:</b> a scratch holding a content-keyed result — a self-blur of tiles that did not
/// change, an acrylic backdrop over a frame beneath that did not change — is kept across turns under its key
/// (<see cref="Retain"/> / <see cref="FindRetained"/>) instead of being recomputed, and returns to the scratch pool once
/// unused for <see cref="RetainTurns"/> turns.</item>
/// </list>
/// </summary>
internal sealed unsafe class SurfacePool : IDisposable
{
    public const int ScratchCap = 128;

    /// <summary>A retained derived surface unused for this many composite turns returns to the scratch pool.</summary>
    public const int RetainTurns = 30;

    private struct Entry
    {
        public ID3D12Resource* Res;
        public int W, H;
        public D3D12_RESOURCE_STATES State;
        public int LastTurn;
        public long LastUseMs;       // scratch: wall clock of the latest lease (the idle path's clock: no turns advance when idle)
        public ulong LastUseFence;
        public uint Serial;          // bumped on every raster into the slot (content identity for derived caches)
        public bool InUse;           // scratch: leased this frame
        public int SrvStamp;         // the turn whose bank holds this entry's SRV
        public bool Retained;        // scratch: holds a content-keyed result across turns
        public ulong RetainKey;
        public int RetainAux;        // what the result needs to be drawn again (e.g. its downsample factor)
    }

    private struct Retired { public ID3D12Resource* Res; public ulong Fence; public long Bytes; }

    private readonly ID3D12Device* _device;
    private readonly int _tileCap;
    private readonly Entry[] _tiles;
    private readonly Entry[] _scratch = new Entry[ScratchCap];
    private readonly List<Retired> _retired = new(16);
    // Running totals of _retired, written by the render thread wherever the queue changes and read by the UI thread's
    // census sampler — which must never enumerate the render thread's live List (it threw "Collection was modified"
    // mid-navigation when a trim ran under the sampler).
    private long _retiredBytes;
    private int _retiredCount;
    private ID3D12DescriptorHeap* _rtvHeap;
    private ID3D12DescriptorHeap* _srvHeap;
    private uint _rtvInc, _srvInc;
    private int _bank;
    private int _turn;

    public SurfacePool(ID3D12Device* device, int tileCap)
    {
        _device = device;
        _tileCap = tileCap;
        _tiles = new Entry[tileCap];

        D3D12_DESCRIPTOR_HEAP_DESC rh = default;
        rh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        rh.NumDescriptors = (uint)(tileCap + ScratchCap);
        ID3D12DescriptorHeap* rhp;
        Check(_device->CreateDescriptorHeap(&rh, __uuidof<ID3D12DescriptorHeap>(), (void**)&rhp), "SurfacePool.RtvHeap");
        _rtvHeap = rhp;
        _rtvInc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        D3D12MemoryDiagnostics.Track(_rtvHeap, "Tiles.RtvHeap", (ulong)rh.NumDescriptors * _rtvInc);

        D3D12_DESCRIPTOR_HEAP_DESC sh = default;
        sh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        sh.NumDescriptors = (uint)((tileCap + ScratchCap) * D3D12Device.FrameBankDepth);
        sh.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ID3D12DescriptorHeap* shp;
        Check(_device->CreateDescriptorHeap(&sh, __uuidof<ID3D12DescriptorHeap>(), (void**)&shp), "SurfacePool.SrvHeap");
        _srvHeap = shp;
        _srvInc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        D3D12MemoryDiagnostics.Track(_srvHeap, "Tiles.SrvHeap", (ulong)sh.NumDescriptors * _srvInc);
        for (int i = 0; i < _tiles.Length; i++) _tiles[i].SrvStamp = int.MinValue;
        for (int i = 0; i < _scratch.Length; i++) _scratch[i].SrvStamp = int.MinValue;
    }

    public ID3D12DescriptorHeap* SrvHeap => _srvHeap;
    public int TileCap => _tileCap;

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    // ── frame ─────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Open composite turn <paramref name="turn"/> on SRV bank <paramref name="bank"/>: release every retired
    /// texture whose last use the GPU has passed, and trim idle SCRATCH (retire behind the fence). Tile textures trim only
    /// through <see cref="TrimTiles"/>.</summary>
    public void BeginFrame(int bank, int turn, ulong completedFence, bool weak)
    {
        _bank = bank;
        _turn = turn;
        ScratchLeases = 0; ScratchPx = 0L; ScratchRefused = 0;
        DrainRetired(completedFence);
        for (int i = 0; i < _scratch.Length; i++)
        {
            ref Entry e = ref _scratch[i];
            if (e.Retained && turn - e.LastTurn > RetainTurns) e.Retained = false;   // stale result: back to the pool
            if (e.Res == null || e.InUse || e.Retained) continue;
            if (LayerTargetTrim.Classify(false, turn - e.LastTurn, weak) == LayerTrimVerdict.Retire) Retire(ref e);
        }
        EnforceFreeCap(turn, weak);
    }

    /// <summary>Release every retired texture whose last use the GPU has passed. Also the idle path's drain (a retire made
    /// between turns would otherwise wait for the next composite, which an idle app never runs).</summary>
    public void DrainRetired(ulong completedFence)
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (!LayerTargetTrim.CanRelease(_retired[i].Fence, completedFence)) continue;
            D3D12MemoryDiagnostics.Release(_retired[i].Res, "Tiles.Surface");
            _retired[i].Res->Release();
            Interlocked.Add(ref _retiredBytes, -_retired[i].Bytes);
            Interlocked.Decrement(ref _retiredCount);
            _retired.RemoveAt(i);
        }
    }

    /// <summary>Retired textures still waiting on the fence (the idle path asks whether to look again soon).</summary>
    public int RetiredCount => Volatile.Read(ref _retiredCount);

    // The FREE scratch (unleased, unretained) a pool keeps warm is byte-capped (LayerTargetTrim.FreeScratchCapBytes): the
    // oldest-used go first, and only slots unused for 2+ turns count, so a surface a repeating animation re-leases every
    // turn is never a victim and the cap cannot thrash a live effect.
    private void EnforceFreeCap(int turn, bool weak)
    {
        long cap = LayerTargetTrim.FreeScratchCapBytes(weak);
        while (true)
        {
            long free = 0; int victim = -1, oldest = int.MaxValue;
            for (int i = 0; i < _scratch.Length; i++)
            {
                ref Entry e = ref _scratch[i];
                if (e.Res == null || e.InUse || e.Retained || turn - e.LastTurn < 2) continue;
                free += LayerTargetBucket.Bytes(e.W, e.H);
                if (e.LastTurn < oldest) { oldest = e.LastTurn; victim = i; }
            }
            if (free <= cap || victim < 0) return;
            Retire(ref _scratch[victim]);
        }
    }

    /// <summary>Idle-path housekeeping on the wall clock (render thread, between turns): drain what the fence has passed and
    /// retire FREE scratch nothing has leased for <see cref="LayerTargetTrim.IdleMs"/>: an idle app runs no composite turn to
    /// age them. A retained derived result is left alone (re-drawing it is the point of keeping it). Returns the ms until it next
    /// has something to do (-1 = nothing pending).</summary>
    public int TrimIdle(long nowMs, ulong completedFence, bool weak)
    {
        DrainRetired(completedFence);
        long next = long.MaxValue;
        for (int i = 0; i < _scratch.Length; i++)
        {
            ref Entry e = ref _scratch[i];
            if (e.Res == null || e.InUse || e.Retained) continue;
            if (LayerTargetTrim.IsIdleFor(nowMs, e.LastUseMs, weak)) Retire(ref e);
            else next = Math.Min(next, LayerTargetTrim.IdleMs(weak) - (nowMs - e.LastUseMs));
        }
        if (RetiredCount > 0) next = Math.Min(next, 500);   // waiting on the fence
        return next == long.MaxValue ? -1 : (int)Math.Max(1, next);
    }

    /// <summary>Retire (behind its last-use fence) the texture of every tile slot the <see cref="SliceTable"/> released this
    /// turn (<c>CompositeFrame.TrimSurfaces</c>: slots no tile holds). The slot's next raster re-creates its texture
    /// (<see cref="EnsureTile"/>). The ONLY way a tile texture is trimmed.</summary>
    public void TrimTiles(ReadOnlySpan<int> slots)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            int slot = slots[i];
            if ((uint)slot >= (uint)_tiles.Length) continue;
            ref Entry e = ref _tiles[slot];
            if (e.Res != null) Retire(ref e);
        }
    }

    /// <summary>Close the turn: every scratch lease returns to the free list.</summary>
    public void EndFrame()
    {
        for (int i = 0; i < _scratch.Length; i++) _scratch[i].InUse = false;
    }

    private void Retire(ref Entry e)
    {
        if (e.Res == null) { e = default; e.SrvStamp = int.MinValue; return; }
        long retiredBytes = LayerTargetBucket.Bytes(e.W, e.H);
        _retired.Add(new Retired { Res = e.Res, Fence = e.LastUseFence, Bytes = retiredBytes });
        Interlocked.Add(ref _retiredBytes, retiredBytes);
        Interlocked.Increment(ref _retiredCount);
        e = default;
        e.SrvStamp = int.MinValue;
    }

    /// <summary>Retire every texture (device recovery / teardown — the caller has drained the GPU).</summary>
    public void ReleaseAll()
    {
        for (int i = 0; i < _tiles.Length; i++) if (_tiles[i].Res != null) { D3D12MemoryDiagnostics.Release(_tiles[i].Res, "Tiles.Surface"); _tiles[i].Res->Release(); _tiles[i] = default; _tiles[i].SrvStamp = int.MinValue; }
        for (int i = 0; i < _scratch.Length; i++) if (_scratch[i].Res != null) { D3D12MemoryDiagnostics.Release(_scratch[i].Res, "Tiles.Surface"); _scratch[i].Res->Release(); _scratch[i] = default; _scratch[i].SrvStamp = int.MinValue; }
        foreach (var r in _retired) { D3D12MemoryDiagnostics.Release(r.Res, "Tiles.Surface"); r.Res->Release(); }
        _retired.Clear();
        Interlocked.Exchange(ref _retiredBytes, 0);
        Interlocked.Exchange(ref _retiredCount, 0);
    }

    // ── tiles ─────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The texture behind tile surface slot <paramref name="slot"/> at exactly <paramref name="w"/>×<paramref name="h"/>
    /// (created on first use or when the extent changed — the old texture retires behind the fence). Marks the slot used
    /// this turn and bumps its content serial (the caller is about to raster into it).</summary>
    public ID3D12Resource* EnsureTile(int slot, int w, int h, ulong frameFence)
    {
        ref Entry e = ref _tiles[slot];
        if (e.Res == null || e.W != w || e.H != h)
        {
            Retire(ref e);
            e.Res = CreateTarget(w, h, "Tiles.Surface");
            e.W = w; e.H = h;
            e.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            _device->CreateRenderTargetView(e.Res, null, Rtv(slot));
        }
        e.LastTurn = _turn;
        e.LastUseFence = frameFence;
        e.Serial++;
        return e.Res;
    }

    /// <summary>A placed tile slot the composite SAMPLES this turn: stamps its last-use fence (a later trim retires the
    /// texture only once the GPU has passed it). False when the slot holds no texture — a placement the backend cannot
    /// honour; the caller skips it and counts it (<c>IGpuDevice.LastLostPlacements</c>, must be 0).</summary>
    public bool TouchTile(int slot, ulong frameFence)
    {
        if ((uint)slot >= (uint)_tiles.Length) return false;
        ref Entry e = ref _tiles[slot];
        if (e.Res == null) return false;
        e.LastTurn = _turn;
        e.LastUseFence = frameFence;
        return true;
    }

    public uint TileSerial(int slot) => (uint)slot < (uint)_tiles.Length ? _tiles[slot].Serial : 0u;
    public ID3D12Resource* TileResource(int slot) => _tiles[slot].Res;
    public D3D12_CPU_DESCRIPTOR_HANDLE TileRtv(int slot) => Rtv(slot);
    public D3D12_GPU_DESCRIPTOR_HANDLE TileSrv(int slot) => Srv(ref _tiles[slot], slot);
    public D3D12_RESOURCE_STATES TileState(int slot) => _tiles[slot].State;
    public void SetTileState(int slot, D3D12_RESOURCE_STATES s) => _tiles[slot].State = s;

    // ── scratch ───────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Lease a scratch surface of at least <paramref name="w"/>×<paramref name="h"/> (bucketed with
    /// <see cref="LayerTargetBucket.Dim"/>) for the rest of this turn. −1 when every scratch slot is leased.</summary>
    public int AcquireScratch(int w, int h, ulong frameFence)
    {
        w = LayerTargetBucket.Dim(Math.Max(1, w));
        h = LayerTargetBucket.Dim(Math.Max(1, h));
        int best = -1;
        long bestArea = long.MaxValue;
        for (int i = 0; i < _scratch.Length; i++)
        {
            ref Entry e = ref _scratch[i];
            if (e.InUse || e.Retained || e.Res == null || e.W < w || e.H < h) continue;
            long area = (long)e.W * e.H;
            if (area > 4L * w * h) continue;   // far too large: create a right-sized one instead
            if (area < bestArea) { bestArea = area; best = i; }
        }
        if (best < 0)
        {
            for (int i = 0; i < _scratch.Length && best < 0; i++) if (_scratch[i].Res == null) best = i;
            if (best < 0)
            {
                // Every slot holds a texture: replace the least recently used idle one.
                int lru = int.MaxValue;
                for (int i = 0; i < _scratch.Length; i++)
                    if (!_scratch[i].InUse && _scratch[i].LastTurn < lru) { lru = _scratch[i].LastTurn; best = i; }
                if (best >= 0) _scratch[best].Retained = false;
                if (best < 0) { ScratchRefused++; return -1; }
            }
            ref Entry n = ref _scratch[best];
            Retire(ref n);
            n.Res = CreateTarget(w, h, "Tiles.Scratch");
            n.W = w; n.H = h;
            n.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            _device->CreateRenderTargetView(n.Res, null, Rtv(_tileCap + best));
        }
        ref Entry s = ref _scratch[best];
        ScratchLeases++; ScratchPx += (long)w * h;
        s.Retained = false;   // an evicted retained result (every slot held a texture): its key no longer resolves
        s.InUse = true;
        s.LastTurn = _turn;
        s.LastUseMs = Environment.TickCount64;
        s.LastUseFence = frameFence;
        return best;
    }

    /// <summary>Scratch leases this turn and their bucketed pixel area — the OFFSCREEN work the composite needed
    /// (degraded segments, groups, self-blurs, acrylic backdrops, inline layers inside tiles).</summary>
    public int ScratchLeases { get; private set; }
    /// <summary>Scratch leases REFUSED this turn (every slot was leased) — whatever asked drew nothing. Expected 0; the
    /// composite ORs it into the raster's evidence flags and the device counters (evidence-diagnostics §A.3).</summary>
    public int ScratchRefused { get; private set; }
    public long ScratchPx { get; private set; }

    /// <summary>Retire every scratch texture, retained results included (each behind its fence), so the next leases create
    /// textures of exactly their bucket size. A probe's control between two captures: a sampled route (an upsample, a blur
    /// chain) maps its coordinates through the leased texture's size, and a larger pooled texture can move a coordinate
    /// across the filter's fixed-point weight step (≤ 1/255 on a gradient). Between frames only (render thread parked).</summary>
    public void DropScratch()
    {
        for (int i = 0; i < _scratch.Length; i++) Retire(ref _scratch[i]);
    }

    public void ReleaseScratch(int i) { if ((uint)i < (uint)_scratch.Length && !_scratch[i].Retained) _scratch[i].InUse = false; }

    /// <summary>The scratch retained under <paramref name="key"/> by an earlier turn, leased for this one (−1 = none);
    /// <paramref name="aux"/> = what <see cref="Retain"/> stored with it.</summary>
    public int FindRetained(ulong key, ulong frameFence, out int aux)
    {
        for (int i = 0; i < _scratch.Length; i++)
        {
            ref Entry e = ref _scratch[i];
            if (!e.Retained || e.RetainKey != key || e.Res == null) continue;
            e.InUse = true;
            e.LastTurn = _turn;
            e.LastUseMs = Environment.TickCount64;
            e.LastUseFence = frameFence;
            aux = e.RetainAux;
            return i;
        }
        aux = 0;
        return -1;
    }

    /// <summary>Keep scratch <paramref name="i"/> (leased this turn) across turns under <paramref name="key"/>, replacing
    /// whatever was retained under it before — within <paramref name="capBytes"/> of retained surfaces in all
    /// (<c>TileBudget.RetainedBytesCap</c>): past it the least recently used retained results NOT used this turn return to
    /// the pool first; a result that still does not fit is simply not retained (recomputed next time).</summary>
    public void Retain(int i, ulong key, int aux, long capBytes = long.MaxValue)
    {
        if ((uint)i >= (uint)_scratch.Length) return;
        for (int k = 0; k < _scratch.Length; k++)
            if (k != i && _scratch[k].Retained && _scratch[k].RetainKey == key) _scratch[k].Retained = false;
        ref Entry e = ref _scratch[i];
        long mine = LayerTargetBucket.Bytes(e.W, e.H);
        long held = RetainedBytes - (e.Retained ? mine : 0L);
        while (held + mine > capBytes)
        {
            int lru = -1, oldest = int.MaxValue;
            for (int k = 0; k < _scratch.Length; k++)
                if (k != i && _scratch[k].Retained && _scratch[k].LastTurn < _turn && _scratch[k].LastTurn < oldest) { oldest = _scratch[k].LastTurn; lru = k; }
            if (lru < 0) { e.Retained = false; return; }
            _scratch[lru].Retained = false;
            held -= LayerTargetBucket.Bytes(_scratch[lru].W, _scratch[lru].H);
        }
        e.Retained = true;
        e.RetainKey = key;
        e.RetainAux = aux;
    }

    /// <summary>Scratch surfaces currently holding a retained result.</summary>
    public int RetainedCount
    {
        get { int n = 0; for (int i = 0; i < _scratch.Length; i++) if (_scratch[i].Retained) n++; return n; }
    }
    public ID3D12Resource* ScratchResource(int i) => _scratch[i].Res;
    public int ScratchW(int i) => _scratch[i].W;
    public int ScratchH(int i) => _scratch[i].H;
    public D3D12_CPU_DESCRIPTOR_HANDLE ScratchRtv(int i) => Rtv(_tileCap + i);
    public D3D12_GPU_DESCRIPTOR_HANDLE ScratchSrv(int i) => Srv(ref _scratch[i], _tileCap + i);
    public D3D12_RESOURCE_STATES ScratchState(int i) => _scratch[i].State;
    public void SetScratchState(int i, D3D12_RESOURCE_STATES s) => _scratch[i].State = s;

    // ── census ────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Bytes held by scratch surfaces currently retaining a content-keyed result (groups, self-blurs, backdrops).</summary>
    public long RetainedBytes
    {
        get
        {
            long b = 0;
            for (int i = 0; i < _scratch.Length; i++)
                if (_scratch[i].Retained && _scratch[i].Res != null) b += LayerTargetBucket.Bytes(_scratch[i].W, _scratch[i].H);
            return b;
        }
    }

    /// <summary>Pooled-vs-in-use bytes (tile surfaces count as retained pins; scratch as in-use / free).</summary>
    public LayerTargetCensus TargetCensus
    {
        get
        {
            LayerTargetCensus c = default;
            for (int i = 0; i < _tiles.Length; i++)
                if (_tiles[i].Res != null) c = c.WithSlot(LayerTargetBucket.Bytes(_tiles[i].W, _tiles[i].H), inUse: false, retained: true);
            for (int i = 0; i < _scratch.Length; i++)
                if (_scratch[i].Res != null)
                    c = c.WithSlot(LayerTargetBucket.Bytes(_scratch[i].W, _scratch[i].H), _scratch[i].InUse && !_scratch[i].Retained, retained: _scratch[i].Retained);
            c = c.WithRetiredTotal(Interlocked.Read(ref _retiredBytes), Volatile.Read(ref _retiredCount));
            return c;
        }
    }

    /// <summary>Bytes held by tile surface textures.</summary>
    public long TileBytes
    {
        get { long b = 0; for (int i = 0; i < _tiles.Length; i++) if (_tiles[i].Res != null) b += LayerTargetBucket.Bytes(_tiles[i].W, _tiles[i].H); return b; }
    }

    // ── internals ─────────────────────────────────────────────────────────────────────────────────────────────────
    private D3D12_CPU_DESCRIPTOR_HANDLE Rtv(int index)
    {
        var h = _rtvHeap->GetCPUDescriptorHandleForHeapStart();
        h.ptr += (nuint)index * _rtvInc;
        return h;
    }

    private D3D12_GPU_DESCRIPTOR_HANDLE Srv(ref Entry e, int index)
    {
        int slot = _bank * (_tileCap + ScratchCap) + index;
        if (e.SrvStamp != _turn)
        {
            var cpu = _srvHeap->GetCPUDescriptorHandleForHeapStart();
            cpu.ptr += (nuint)slot * _srvInc;
            D3D12_SHADER_RESOURCE_VIEW_DESC sd = default;
            sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            sd.ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D;
            sd.Shader4ComponentMapping = 0x1688;   // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
            sd.Anonymous.Texture2D.MipLevels = 1;
            _device->CreateShaderResourceView(e.Res, &sd, cpu);
            e.SrvStamp = _turn;
        }
        var gpu = _srvHeap->GetGPUDescriptorHandleForHeapStart();
        gpu.ptr += (ulong)slot * _srvInc;
        return gpu;
    }

    private ID3D12Resource* CreateTarget(int w, int h, string name)
    {
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width = (ulong)w; rd.Height = (uint)h; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        ulong bytes = D3D12MemoryDiagnostics.AllocationBytes(_device, &rd);
        // Optimized clear value = transparent black: every tile / scratch pass begins with a CLEAR to exactly that.
        D3D12_CLEAR_VALUE cv = default;
        cv.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        ID3D12Resource* res;
        Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, &cv, __uuidof<ID3D12Resource>(), (void**)&res), name);
        D3D12MemoryDiagnostics.Track(res, "Tiles.Surface", bytes != 0 ? bytes : (ulong)w * (ulong)h * 4UL);
        return res;
    }

    public void Dispose()
    {
        ReleaseAll();
        if (_rtvHeap != null) { D3D12MemoryDiagnostics.Release(_rtvHeap, "Tiles.RtvHeap"); _rtvHeap->Release(); _rtvHeap = null; }
        if (_srvHeap != null) { D3D12MemoryDiagnostics.Release(_srvHeap, "Tiles.SrvHeap"); _srvHeap->Release(); _srvHeap = null; }
    }
}
