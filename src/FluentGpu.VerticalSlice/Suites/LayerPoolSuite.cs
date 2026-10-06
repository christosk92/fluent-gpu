using System;
using FluentGpu.Foundation;
using FluentGpu.Render;
using static FluentGpu.VerticalSlice.Harness.Gate;

// The surface pool's portable contract (gpu-renderer.md §13 — SurfacePool). The D3D12 leaf that owns the actual render
// targets is unreachable headlessly (TerraFX/COM, render-thread-confined) and there is no headless framebuffer, so what
// is pinned here is every DECISION the leaf computes through: the size bucket, the idle-trim verdict, the fence rule,
// the self-blur tap guard and the pooled-vs-in-use byte census.
//
// GpuProfile.IsWeak is ALWAYS false headlessly (only D3D12Device's UMA probe ever sets Tier), which is precisely why
// LayerTargetTrim.Classify takes `weak` as an argument instead of reading the global: both tiers are driven here.

static class LayerPoolSuite
{
    public static void Run(StringTable strings)
    {
        BucketChecks();
        TrimVerdictChecks();
        FenceRuleChecks();
        ReuseWithoutGrowthChecks();
        TapGuardChecks();
        CensusChecks();
        AllocChecks();
    }

    // The next-power-of-two ladder (floor 64) the linear ladder is measured against.
    static int Po2(int px)
    {
        int b = 64;
        while (b < px) b <<= 1;
        return b;
    }

    // ── the size ladder ─────────────────────────────────────────────────────────────────────────────────────────────

    static void BucketChecks()
    {
        bool monotone = true, covers = true, floor64 = true, linear = true, po2 = true;
        int prev = 0;
        for (int px = 1; px <= 9000; px++)
        {
            int d = LayerTargetBucket.Dim(px);
            if (d < prev) monotone = false;
            if (d < px) covers = false;
            if (d < LayerTargetBucket.MinDim) floor64 = false;
            if (px <= LayerTargetBucket.LinearCeiling && d % LayerTargetBucket.LinearStep != 0) linear = false;
            if (px > LayerTargetBucket.LinearCeiling && (d % LayerTargetBucket.HighStep != 0 || d - px >= LayerTargetBucket.HighStep)) po2 = false;
            prev = d;
        }
        Check("gate.layerpool.bucket-ladder is monotone, always covers the request, floors at 64, steps by 64 up to the linear ceiling and by 128 (never a power of two) above it",
            monotone && covers && floor64 && linear && po2,
            $"monotone={monotone} covers={covers} floor64={floor64} linear64={linear} po2={po2} ceiling={LayerTargetBucket.LinearCeiling}");

        // The reason this ladder exists: a guarded strip (a ~340x120 box + a sigma-4 tap guard) wastes far less area than
        // a power-of-two ladder would, WITHOUT ever under-covering.
        int gw = 340 + 13 * 2, gh = 120 + 13 * 2;
        long tight = (long)LayerTargetBucket.Dim(gw) * LayerTargetBucket.Dim(gh);
        long po2Area = (long)Po2(gw) * Po2(gh);
        long need = (long)gw * gh;
        Check("gate.layerpool.bucket-tighter-than-po2 the layer ladder covers a guarded blur strip with materially less waste than a power-of-two ladder",
            tight >= need && tight < po2Area,
            $"need={need}px tight={tight}px po2={po2Area}px waste={(double)tight / need:0.00}x vs {(double)po2Area / need:0.00}x");

        // The step that matters most: a full-window WIDTH. A po2 ladder jumps 1195 -> 2048 (1.7x waste on that axis
        // alone); the linear ceiling is set past a typical window dimension precisely to stop that.
        Check("gate.layerpool.bucket-window-width the ladder does not round a typical window width up to the next power of two",
            LayerTargetBucket.Dim(1195) == 1216 && Po2(1195) == 2048 && LayerTargetBucket.LinearCeiling >= 2048,
            $"layer(1195)={LayerTargetBucket.Dim(1195)} po2(1195)={Po2(1195)} ceiling={LayerTargetBucket.LinearCeiling}");

        // Bytes: the census/budget unit.
        Check("gate.layerpool.bucket-bytes B8G8R8A8 byte math matches w*h*4 and clamps negatives",
            LayerTargetBucket.Bytes(1195, 767) == 1195L * 767L * 4L && LayerTargetBucket.Bytes(-4, 10) == 0,
            $"window1195x767={LayerTargetBucket.Bytes(1195, 767)}B");
    }

    // ── the trim verdict ────────────────────────────────────────────────────────────────────────────────────────────

    static void TrimVerdictChecks()
    {
        // (a) an IN-USE slot is never retired, at any age, on any tier.
        bool inUseSafe = true;
        foreach (bool weak in new[] { false, true })
            foreach (int age in new[] { 0, 121, 601, 100000 })
                if (LayerTargetTrim.Classify(inUse: true, age, weak) != LayerTrimVerdict.Keep) inUseSafe = false;
        Check("gate.layerpool.trim-never-touches-in-use a leased slot is Keep for every tier/age combination",
            inUseSafe, "in-use x {tier} x {age}");

        // (b) trim AFTER idle, not before — both tiers, at the exact window boundary.
        int wWeak = LayerTargetTrim.IdleFramesWeak, wStrong = LayerTargetTrim.IdleFramesStrong;
        bool weakAt = LayerTargetTrim.Classify(false, wWeak, true) == LayerTrimVerdict.Keep;
        bool weakPast = LayerTargetTrim.Classify(false, wWeak + 1, true) == LayerTrimVerdict.Retire;
        bool strongAt = LayerTargetTrim.Classify(false, wStrong, false) == LayerTrimVerdict.Keep;
        bool strongPast = LayerTargetTrim.Classify(false, wStrong + 1, false) == LayerTrimVerdict.Retire;
        bool strongKeepsWeakWindow = LayerTargetTrim.Classify(false, wWeak + 1, false) == LayerTrimVerdict.Keep;
        Check("gate.layerpool.trim-after-idle a free scratch slot survives exactly its tier's window and is retired one frame past it (and a discrete GPU does NOT inherit the weak window)",
            weakAt && weakPast && strongAt && strongPast && strongKeepsWeakWindow,
            $"weak={wWeak} strong={wStrong} weakAt={weakAt} weakPast={weakPast} strongAt={strongAt} strongPast={strongPast} strongAtWeakWindow=Keep:{strongKeepsWeakWindow}");
    }

    // ── the fence rule ──────────────────────────────────────────────────────────────────────────────────────────────

    static void FenceRuleChecks()
    {
        Check("gate.layerpool.no-release-before-fence CanRelease is false until the frame fence has passed the entry's last recorded use, and true at equality",
            !LayerTargetTrim.CanRelease(lastUseFence: 10, completedFence: 9)
            && LayerTargetTrim.CanRelease(10, 10)
            && LayerTargetTrim.CanRelease(10, 11)
            && !LayerTargetTrim.CanRelease(ulong.MaxValue, 0),
            "10>9:false 10>=10:true 10<=11:true max>0:false");

        // A trim on a frame whose fence has NOT completed must retire (stop leasing) and yet release NOTHING: that is
        // the whole "no trim while fenced" invariant, and it is the composition of the two rules above. Modelled here
        // over the same two-stage shape the leaf implements (Classify → retire queue → CanRelease → Release).
        var pool = new PoolModel(slots: 8);
        int slot = pool.LeaseBucketed(1195, 767, frameFence: 100);
        pool.Release(slot);
        // Fence 100 is still in flight (completed 99) for the whole trim window and beyond.
        for (int f = 0; f <= LayerTargetTrim.IdleFramesWeak + 2; f++) pool.Tick(weak: true, completedFence: 99);
        bool retiredNotReleased = pool.LiveSlots == 0 && pool.RetiredCount == 1 && pool.ReleasedCount == 0;
        pool.Tick(weak: true, completedFence: 100);
        bool releasedOnceFenced = pool.RetiredCount == 0 && pool.ReleasedCount == 1;
        Check("gate.layerpool.no-trim-while-fenced an idle slot whose last-use fence is still in flight is retired but NEVER released, and is released on the first tick after the fence passes",
            retiredNotReleased && releasedOnceFenced,
            $"whileInFlight(live={pool.LiveSlots} retired=1 released=0)={retiredNotReleased} afterFence={releasedOnceFenced}");
    }

    // ── bucketed reuse without growth ───────────────────────────────────────────────────────────────────────────────

    static void ReuseWithoutGrowthChecks()
    {
        // A scrolling blurred row: the guarded box jitters by a pixel or two every frame (sub-pixel floor/ceil), which is
        // exactly the pattern an EXACT-size pool would churn on. Bucketing plus a best-fit lease must stop creating
        // surfaces after warmup and must not grow the pool's bytes.
        var pool = new PoolModel(slots: 16);
        int createsAfterWarmup = 0;
        long bytesAfterWarmup = 0;
        for (int f = 0; f < 400; f++)
        {
            int w = 340 + f % 7, h = 120 + (f * 3) % 5;      // the per-frame jitter
            int a = pool.LeaseBucketed(w, h, frameFence: (ulong)f);
            int b = pool.LeaseBucketed(w, h, frameFence: (ulong)f);   // the blur's ping-pong pair
            pool.Release(a); pool.Release(b);
            pool.Tick(weak: true, completedFence: (ulong)f);
            if (f == 60) { pool.ResetCreateCount(); bytesAfterWarmup = pool.LiveBytes; }
            if (f > 60) createsAfterWarmup = pool.CreateCount;
        }
        Check("gate.layerpool.bucketed-reuse-without-growth 340 frames of a per-frame-jittering guarded box create ZERO new surfaces after warmup and the pool's live bytes never grow",
            createsAfterWarmup == 0 && pool.LiveBytes <= bytesAfterWarmup,
            $"createsAfterWarmup={createsAfterWarmup} bytes={pool.LiveBytes}B (warm={bytesAfterWarmup}B) liveSlots={pool.LiveSlots}");
    }

    // ── the self-blur tap guard ─────────────────────────────────────────────────────────────────────────────────────

    static void TapGuardChecks()
    {
        // TapRadius + 1 must cover the kernel's full tap reach: it sizes the halo a self-blur's source is rastered over
        // (the tile rasterizer's inline blur groups, the composite's self-blur surface), so every texel a pass reads is
        // either real source or a cleared transparent one.
        bool guardCovers = true;
        Span<float> kOff = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        Span<float> kWgt = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        for (float sigma = 0.25f; sigma <= 4f; sigma += 0.25f)
        {
            int n = AcrylicBackdropMath.BuildKernel(sigma, kOff, kWgt);
            float maxTap = 0f;
            for (int k = 0; k < n; k++) maxTap = MathF.Max(maxTap, kOff[k]);
            if (maxTap > SelfBlurRegion.TapRadius(sigma) + 1) guardCovers = false;
        }
        Check("gate.layerpool.guard-covers-taps the self-blur guard band (TapRadius + 1) is at least the largest bilinear tap offset the kernel emits for every down==1 sigma",
            guardCovers, "sigma 0.25..4 step 0.25, maxTapOffset <= TapRadius+1");
    }

    // ── the pooled/in-use/retained byte census ──────────────────────────────────────────────────────────────────────

    static void CensusChecks()
    {
        long big = LayerTargetBucket.Bytes(1195, 767);
        long small = LayerTargetBucket.Bytes(384, 192);
        LayerTargetCensus c = default;
        c = c.WithSlot(big, inUse: true, retained: false);       // one surface compositing
        c = c.WithSlot(big, inUse: false, retained: false);      // one free scratch slot
        c = c.WithSlot(big, inUse: false, retained: false);      // …and the second
        c = c.WithSlot(small, inUse: false, retained: true);     // a retained tile / derived surface
        c = c.WithSlot(small, inUse: false, retained: true);
        c = c.WithRetired(big);                                  // one awaiting its fence

        bool split = c.InUseBytes == big && c.InUseCount == 1
                  && c.FreeBytes == big * 2 && c.FreeCount == 2
                  && c.RetainedBytes == small * 2 && c.RetainedCount == 2
                  && c.RetiredBytes == big && c.RetiredCount == 1;
        bool total = c.TotalBytes == big * 4 + small * 2 && c.LiveCount == 5;
        Check("gate.layerpool.census-bytes the pooled/in-use/retained/retired split adds up and TotalBytes accounts for every surface the pool holds (a retained surface is never counted as free, and a retired entry is not a live slot)",
            split && total,
            $"inuse={c.InUseBytes} free={c.FreeBytes} retained={c.RetainedBytes} retired={c.RetiredBytes} total={c.TotalBytes} live={c.LiveCount}");

        // The device reports the surface pool + baked-blur as one line, so the sum must be a plain field-wise add.
        var sum = c + c;
        Check("gate.layerpool.census-sums two pools' censuses add field-wise, so the device can report the surface pool + baked-blur as one census line",
            sum.TotalBytes == c.TotalBytes * 2 && sum.LiveCount == c.LiveCount * 2 && sum.RetainedCount == 4,
            $"sumTotal={sum.TotalBytes} sumLive={sum.LiveCount}");

        LayerTargetCensus empty = default;
        Check("gate.layerpool.census-empty a pool that has never leased reports zero in every field",
            empty.TotalBytes == 0 && empty.LiveCount == 0 && empty.ToDetail().Length > 0,
            $"detail=\"{empty.ToDetail()}\"");
    }

    // ── zero allocation ─────────────────────────────────────────────────────────────────────────────────────────────

    static void AllocChecks()
    {
        // Both run on the render thread's per-frame composite path (the trim tick once per frame, Dim/Bytes per lease),
        // so they are inside the phases 6-13 zero-alloc contract.
        LayerTargetTrim.Classify(false, 5, true);                 // warm
        LayerTargetBucket.Dim(365);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int keeps = 0; long bytes = 0;
        for (int i = 0; i < 10000; i++)
        {
            if (LayerTargetTrim.Classify(i % 3 == 0, i % 900, i % 7 == 0) == LayerTrimVerdict.Keep) keeps++;
            bytes += LayerTargetBucket.Bytes(LayerTargetBucket.Dim(64 + i % 1400), LayerTargetBucket.Dim(64 + i % 900));
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.layerpool.alloc-zero 10000 iterations of Classify + Dim/Bytes allocate 0 managed bytes (they run per frame / per lease on the render thread)",
            delta == 0 && keeps > 0 && bytes > 0,
            $"delta={delta}B/10000 keeps={keeps}");
    }

    /// <summary>
    /// A pure model of the surface pool's scratch: the SAME two-stage shape the D3D12 leaf implements — best-fit
    /// bucketed lease, LRU eviction, a per-frame <see cref="LayerTargetTrim.Classify"/> tick that RETIRES into a
    /// fence-gated queue, and a drain that releases only what <see cref="LayerTargetTrim.CanRelease"/> allows. It
    /// exists so "trim after idle", "no trim while fenced" and "reuse without growth" are assertions about a POOL and
    /// not just about a predicate; the leaf's own COM/descriptor bookkeeping is what stays --screenshot territory.
    /// </summary>
    sealed class PoolModel
    {
        struct Slot { public bool Live, InUse; public int W, H; public int Idle; public ulong Fence; }
        struct RetiredEntry { public ulong Fence; public long Bytes; }

        readonly Slot[] _slots;
        readonly System.Collections.Generic.List<RetiredEntry> _retired = new();
        public int CreateCount { get; private set; }
        public int ReleasedCount { get; private set; }

        public PoolModel(int slots) => _slots = new Slot[slots];

        public int RetiredCount => _retired.Count;
        public void ResetCreateCount() => CreateCount = 0;

        public int LiveSlots { get { int n = 0; for (int i = 0; i < _slots.Length; i++) if (_slots[i].Live) n++; return n; } }

        public long LiveBytes
        {
            get
            {
                long b = 0;
                for (int i = 0; i < _slots.Length; i++) if (_slots[i].Live) b += LayerTargetBucket.Bytes(_slots[i].W, _slots[i].H);
                return b;
            }
        }

        public int LeaseBucketed(int w, int h, ulong frameFence)
        {
            w = LayerTargetBucket.Dim(w); h = LayerTargetBucket.Dim(h);
            // 1) BEST-FIT among free slots that can hold the request (the leaf's reuse rule).
            int best = -1;
            for (int i = 0; i < _slots.Length; i++)
            {
                ref var s = ref _slots[i];
                if (!s.Live || s.InUse || s.W < w || s.H < h) continue;
                if (best < 0 || (long)s.W * s.H < (long)_slots[best].W * _slots[best].H) best = i;
            }
            if (best < 0)
            {
                for (int i = 0; i < _slots.Length; i++) if (!_slots[i].Live) { best = i; break; }
                if (best < 0)                                     // 2) LRU evict a free slot
                {
                    for (int i = 0; i < _slots.Length; i++)
                    {
                        if (_slots[i].InUse) continue;
                        if (best < 0 || _slots[i].Fence < _slots[best].Fence) best = i;
                    }
                    if (best < 0) throw new InvalidOperationException("model pool exhausted");
                    RetireSlot(ref _slots[best]);
                }
                _slots[best] = new Slot { Live = true, W = w, H = h };
                CreateCount++;
            }
            ref var e = ref _slots[best];
            e.InUse = true; e.Idle = 0; e.Fence = frameFence;
            return best;
        }

        public void Release(int slot) => _slots[slot].InUse = false;

        public void Tick(bool weak, ulong completedFence)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                ref var s = ref _slots[i];
                if (!s.Live) continue;
                if (!s.InUse) s.Idle++;
                if (LayerTargetTrim.Classify(s.InUse, s.Idle, weak) == LayerTrimVerdict.Retire) RetireSlot(ref s);
            }
            Drain(completedFence);
        }

        void RetireSlot(ref Slot s)
        {
            _retired.Add(new RetiredEntry { Fence = s.Fence, Bytes = LayerTargetBucket.Bytes(s.W, s.H) });
            s = default;
        }

        void Drain(ulong completedFence)
        {
            for (int i = _retired.Count - 1; i >= 0; i--)
            {
                if (!LayerTargetTrim.CanRelease(_retired[i].Fence, completedFence)) continue;
                _retired.RemoveAt(i);
                ReleasedCount++;
            }
        }
    }
}
