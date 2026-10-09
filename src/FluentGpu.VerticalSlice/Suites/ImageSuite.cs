using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;


    sealed class TestCodec : IImageCodec
    {
        readonly Action? _onDecode;
        public TestCodec(Action? onDecode = null) => _onDecode = onDecode;
        public bool DecodeConstrained(ReadOnlySpan<byte> encoded, int tw, int th, Span<byte> dst, out int w, out int h)
        {
            _onDecode?.Invoke();
            w = tw; h = th;
            dst.Slice(0, tw * th * 4).Fill(0xFF);
            return true;
        }
    }

    sealed class TestFetcher : IImageFetcher
    {
        readonly Func<string, FetchResult>? _map;
        public TestFetcher(Func<string, FetchResult>? map = null) => _map = map;
        public Task<FetchResult> FetchAsync(string source, System.Threading.CancellationToken ct)
        {
            if (_map != null) return Task.FromResult(_map(source));
            return Task.FromResult(FetchResult.Pooled(ArrayPool<byte>.Shared.Rent(16), 16));
        }
    }

    sealed class DropPrefetchDecoder : IImageDecoder
    {
        readonly Queue<(int id, int w, int h)> _pending = new();
        byte[] _scratch = Array.Empty<byte>();

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            if (priority != ImagePriority.Visible) return false;
            _pending.Enqueue((id, targetW <= 0 ? 1 : targetW, targetH <= 0 ? 1 : targetH));
            return true;
        }

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            while (_pending.Count > 0)
            {
                var (id, w, h) = _pending.Dequeue();
                int bytes = w * h * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                _scratch.AsSpan(0, bytes).Fill(0xFF);
                onPixels(id, _scratch.AsSpan(0, bytes), w, h);
                onComplete(id, true, w, h, ImageFailureKind.None, 1);
            }
        }
    }

    sealed class TimeoutThenOkDecoder : IImageDecoder
    {
        readonly Dictionary<int, int> _attempts = new();
        readonly Queue<(int id, int w, int h)> _pending = new();
        byte[] _scratch = Array.Empty<byte>();

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            _pending.Enqueue((id, targetW <= 0 ? 1 : targetW, targetH <= 0 ? 1 : targetH));
            return true;
        }

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            while (_pending.Count > 0)
            {
                var (id, w, h) = _pending.Dequeue();
                int n = _attempts.TryGetValue(id, out var a) ? a + 1 : 1;
                _attempts[id] = n;
                if (n == 1) { onComplete(id, false, 0, 0, ImageFailureKind.Timeout, 1); continue; }
                int bytes = w * h * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                _scratch.AsSpan(0, bytes).Fill(0xFF);
                onPixels(id, _scratch.AsSpan(0, bytes), w, h);
                onComplete(id, true, w, h, ImageFailureKind.None, n);
            }
        }
    }

    sealed class CancelAwareDecoder : IImageDecoder
    {
        readonly Queue<(int id, int w, int h)> _pending = new();
        readonly HashSet<int> _canceled = new();
        byte[] _scratch = Array.Empty<byte>();

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            _canceled.Remove(id);
            _pending.Enqueue((id, targetW <= 0 ? 1 : targetW, targetH <= 0 ? 1 : targetH));
            return true;
        }

        public void Cancel(int id) => _canceled.Add(id);

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            while (_pending.Count > 0)
            {
                var (id, w, h) = _pending.Dequeue();
                if (_canceled.Remove(id)) { onComplete(id, false, 0, 0, ImageFailureKind.Canceled, 1); continue; }
                int bytes = w * h * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                _scratch.AsSpan(0, bytes).Fill(0xFF);
                onPixels(id, _scratch.AsSpan(0, bytes), w, h);
                onComplete(id, true, w, h, ImageFailureKind.None, 1);
            }
        }
    }

    sealed class GatedDecoder : IImageDecoder
    {
        readonly Queue<(int id, int w, int h)> _pending = new();
        byte[] _scratch = Array.Empty<byte>();
        bool _armed;

        public void Arm() => _armed = true;

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            _pending.Enqueue((id, targetW <= 0 ? 1 : targetW, targetH <= 0 ? 1 : targetH));
            return true;
        }

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            if (!_armed) return;
            while (_pending.Count > 0)
            {
                var (id, w, h) = _pending.Dequeue();
                int bytes = w * h * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                _scratch.AsSpan(0, bytes).Fill(0xFF);
                onPixels(id, _scratch.AsSpan(0, bytes), w, h);
                onComplete(id, true, w, h, ImageFailureKind.None, 1);
            }
        }
    }

    sealed class GatedCancelAwareDecoder : IImageDecoder
    {
        readonly Queue<(int id, int w, int h)> _pending = new();
        readonly HashSet<int> _canceled = new();
        readonly Dictionary<int, int> _cancelCounts = new();
        byte[] _scratch = Array.Empty<byte>();
        bool _armed;

        public void Arm() => _armed = true;
        public int CancelCount(int id) => _cancelCounts.TryGetValue(id, out int n) ? n : 0;

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            _canceled.Remove(id);
            _pending.Enqueue((id, targetW <= 0 ? 1 : targetW, targetH <= 0 ? 1 : targetH));
            return true;
        }

        public void Cancel(int id)
        {
            _canceled.Add(id);
            _cancelCounts[id] = CancelCount(id) + 1;
        }

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            if (!_armed) return;
            while (_pending.Count > 0)
            {
                var (id, w, h) = _pending.Dequeue();
                if (_canceled.Remove(id)) { onComplete(id, false, 0, 0, ImageFailureKind.Canceled, 1); continue; }
                int bytes = w * h * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                _scratch.AsSpan(0, bytes).Fill(0xFF);
                onPixels(id, _scratch.AsSpan(0, bytes), w, h);
                onComplete(id, true, w, h, ImageFailureKind.None, 1);
            }
        }
    }

    sealed class SelectiveGatedDecoder : IImageDecoder
    {
        readonly List<(int Id, string Source, int W, int H)> _pending = new();
        readonly HashSet<string> _released = new(StringComparer.Ordinal);
        byte[] _scratch = Array.Empty<byte>();

        public void Release(string source) => _released.Add(source);

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            _pending.Add((id, source, Math.Max(1, targetW), Math.Max(1, targetH)));
            return true;
        }

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if (!_released.Contains(p.Source)) continue;
                _pending.RemoveAt(i);
                int bytes = p.W * p.H * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                _scratch.AsSpan(0, bytes).Fill(0xFF);
                onPixels(p.Id, _scratch.AsSpan(0, bytes), p.W, p.H);
                onComplete(p.Id, true, p.W, p.H, ImageFailureKind.None, 1);
            }
        }
    }

    // Hold-last-good (media-pipeline.md §hold-last-good) test decoder: completion is gated PER-ID (Release(id)) rather
    // than per-source, so a test can complete an OLD decode while a NEW one (same source, different key — or a
    // different source under the bound path) sits Pending under a separate id, exactly the window the reconciler's
    // SwapImageId hold must cover. Pump() only resolves ids explicitly released (still the +1-frame contract: Begin
    // never completes synchronously).
    sealed class SelectiveIdDecoder : IImageDecoder
    {
        readonly Dictionary<int, (int W, int H)> _pending = new();
        readonly HashSet<int> _release = new();
        readonly HashSet<int> _fail = new();
        readonly HashSet<int> _canceled = new();
        byte[] _scratch = Array.Empty<byte>();

        public int LastBeginId { get; private set; }   // the most recently Begin'd id — lets a test discover a NEW
                                                          // (held) pending id it can never see in a recorded draw.
        public bool IsPending(int id) => _pending.ContainsKey(id);
        public void Release(int id) => _release.Add(id);
        public void Fail(int id) { _release.Add(id); _fail.Add(id); }
        public bool WasCanceled(int id) => _canceled.Contains(id);

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            LastBeginId = id;
            _pending[id] = (Math.Max(1, targetW), Math.Max(1, targetH));
            return true;
        }

        public void Cancel(int id) => _canceled.Add(id);

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            if (_release.Count == 0) return;
            foreach (int id in _release.ToArray())
            {
                _release.Remove(id);
                bool fail = _fail.Remove(id);
                if (!_pending.Remove(id, out var wh)) continue;
                if (fail) { onComplete(id, false, 0, 0, ImageFailureKind.Decode, 1); continue; }
                int bytes = wh.W * wh.H * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                _scratch.AsSpan(0, bytes).Fill(0xFF);
                onPixels(id, _scratch.AsSpan(0, bytes), wh.W, wh.H);
                onComplete(id, true, wh.W, wh.H, ImageFailureKind.None, 1);
            }
        }
    }

    // W2-E3 (scroll-feel plan, wave 2): records the lane every request was queued at (Begin) and applies every raise-only
    // Prioritize the cache forwards, so a test can read the lane an image CURRENTLY sits in. Never completes anything:
    // every entry stays Pending, which keeps lane moves observable (a settled entry ignores Promote by design).
    sealed class LaneRecordingDecoder : IImageDecoder
    {
        readonly Dictionary<int, string> _source = new();
        readonly Dictionary<int, ImagePriority> _begin = new();
        readonly Dictionary<int, ImagePriority> _lane = new();

        public int BeginCount => _begin.Count;

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            _source[id] = source; _begin[id] = priority; _lane[id] = priority;
            return true;
        }

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) { }

        public void Prioritize(int id, ImagePriority priority)
        {
            if (_lane.TryGetValue(id, out var cur) && priority < cur) _lane[id] = priority;
        }

        int IdOf(string source)
        {
            foreach (var (id, s) in _source) if (string.Equals(s, source, StringComparison.Ordinal)) return id;
            return 0;
        }
        /// <summary>The lane the LATEST request for <paramref name="source"/> was queued at; null if never requested.</summary>
        public ImagePriority? BeginLaneOf(string source)
        {
            int id = IdOf(source);
            if (id == 0) return null;
            return _begin[id];
        }
        /// <summary>The lane <paramref name="source"/> currently sits in (Begin lowered by every Prioritize); null if never requested.</summary>
        public ImagePriority? LaneOf(string source)
        {
            int id = IdOf(source);
            if (id == 0) return null;
            return _lane[id];
        }
    }

    // A 200 px viewport of 40 px rows, one cover per row (keyed RenderItem rows, or persistent bound slots when Bound):
    // 5 rows unambiguously visible, the engine's visible edge at row 5, the +1 guard at row 6, the Overscan=4 halo beyond.
    sealed class OverscanLaneProbe : Component
    {
        public bool Bound;

        public override Element Render()
        {
            var list = new VirtualListEl
            {
                ItemCount = 200, EstimatedExtent = 40f, Width = 200f, Height = 200f,
                RenderItem = static i => Row("static/" + i),
                RowBind = Bound ? (Func<IReadSignal<int>, Element>)(sig => Row(Prop.Of(() => "bound/" + sig.Value))) : null,
            };
            return new BoxEl { Width = 200f, Height = 200f, Children = [list] };
        }

        static Element Row(Prop<string> source) => new BoxEl
        {
            Width = 200f, Height = 40f,
            Children = [new ImageEl { Source = source, Width = 24f, Height = 24f }],
        };
    }

    // E7 gate fixture: wraps FakeImageDecoder (+1-frame latency, always-succeeds) while counting Begin calls, so a test
    // can prove a pinned-exhausted entry's retry sweep (ImageCache.RetryPinnedExhausted) actually issued a NEW decode
    // request rather than just changing state — the thing 45b's admission-rejection sink alone can't distinguish.
    sealed class CountingFakeDecoder : IImageDecoder
    {
        readonly FakeImageDecoder _inner = new();
        public int BeginCount { get; private set; }

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            BeginCount++;
            return _inner.Begin(id, source, targetW, targetH, priority);
        }

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) => _inner.Pump(onComplete, onPixels);
    }

    // T10 gate fixture: models the two DecodeScheduler behaviours a pinned canceled leftover comes from — Cancel of a
    // queued/in-flight decode completing as Canceled on the next pump, and Begin REFUSING off-screen lanes (never
    // Visible) while the queue is full — plus a permanent 404 for one source. Counts Begin calls per source so the gate
    // can prove the leftover sweep issued a NEW decode with no Request/Pin from the node, and that a real failure didn't.
    sealed class BackpressureCancelDecoder : IImageDecoder
    {
        readonly Queue<(int id, int w, int h, bool notFound)> _pending = new();
        readonly HashSet<int> _canceled = new();
        readonly Dictionary<string, int> _begins = new();
        byte[] _scratch = Array.Empty<byte>();
        public bool OffscreenFull;          // DecodeScheduler's QueueCapacity backpressure: non-Visible Begins refused
        public string? NotFoundSource;      // completes as a permanent NotFound
        public ImagePriority LastAccepted;

        public int BeginsOf(string source) => _begins.TryGetValue(source, out int n) ? n : 0;

        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
        {
            _begins[source] = BeginsOf(source) + 1;
            _canceled.Remove(id);
            if (OffscreenFull && priority != ImagePriority.Visible) return false;
            LastAccepted = priority;
            _pending.Enqueue((id, targetW <= 0 ? 1 : targetW, targetH <= 0 ? 1 : targetH, source == NotFoundSource));
            return true;
        }

        public void Cancel(int id) => _canceled.Add(id);

        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            // Only work queued BEFORE this pump completes in it (the +1-frame contract), even if a completion restarts.
            for (int left = _pending.Count; left > 0; left--)
            {
                var (id, w, h, notFound) = _pending.Dequeue();
                if (_canceled.Remove(id)) { onComplete(id, false, 0, 0, ImageFailureKind.Canceled, 1); continue; }
                if (notFound) { onComplete(id, false, 0, 0, ImageFailureKind.NotFound, 1); continue; }
                int bytes = w * h * 4;
                if (_scratch.Length < bytes) _scratch = new byte[bytes];
                _scratch.AsSpan(0, bytes).Fill(0xFF);
                onPixels(id, _scratch.AsSpan(0, bytes), w, h);
                onComplete(id, true, w, h, ImageFailureKind.None, 1);
            }
        }
    }

    // T10 idle-wake gate root: an image-free page, so the only image work in the host is the leftover the gate plants.
    sealed class LeftoverWakeRoot : Component
    {
        public override Element Render() => new BoxEl { Width = 200f, Height = 120f };
    }

static class ImageSuite
{
    public static void Run(StringTable strings)
    {
        BudgetChecks();
        AtlasPackerChecks();
        IconChecks(strings);
        ImageCacheChecks();
        ImageElChecks(strings);
        ImageCornerClampChecks(strings);
        ImageFitChecks(strings);
        DecodeSchedulerChecks();
        PixelBufferPoolChecks();
        BlurHashChecks(strings);
        ImageTransitionChecks();
        ImageEvictChecks();
        VramShedGraceAndFloorChecks();
        ExhaustedPinnedRetryChecks();
        PinnedCanceledLeftoverChecks();
        PinnedCanceledLeftoverIdleWakeChecks(strings);
        ImageLifecycleChecks(strings);
        UseImageChecks(strings);
        HoldLastGoodChecks(strings);
        OverscanPriorityChecks(strings);
    }

    // ── gate.budgets.* — the three ctor-captured image budgets, driven at BOTH tiers ───────────────────────────────
    // This gate exists because its absence is what let a 2x over-sizing ship on every UMA machine for a whole release
    // cycle. The three budgets read GpuProfile.IsWeak at construction, the tier is published during device init, and
    // device init happens AFTER the host builds the image pipeline — so the weak branch was unreachable in production
    // and, because GpuProfile.IsWeak is ALWAYS false headlessly, unreachable here too. Taking `weak` as an argument is
    // the fix on both counts (the LayerTargetTrim.Classify precedent, LayerPoolSuite's header says the same thing).
    static void BudgetChecks()
    {
        var weak = GpuMemoryBudgets.For(weak: true);
        var strong = GpuMemoryBudgets.For(weak: false);

        Check("gate.budgets.weak-tier-shrinks-every-image-budget a weak (UMA/iGPU) adapter gets 16/40/8 MB where a discrete one gets 32/64/16 — the pixel pool, the image-cache cap and the derived/blur cap, all three, because all three are captured once at construction and a tier read that lands late is indistinguishable from a discrete adapter",
            weak.PixelPool == 16L * 1024 * 1024 && weak.ImageCache == 40L * 1024 * 1024 && weak.Derived == 8L * 1024 * 1024,
            $"pool={weak.PixelPool / (1024 * 1024)}MB image={weak.ImageCache / (1024 * 1024)}MB derived={weak.Derived / (1024 * 1024)}MB");

        Check("gate.budgets.strong-tier-unchanged the discrete budgets are exactly what shipped (32/64/16 MB), so the ordering fix cannot have quietly re-sized a desktop GPU",
            strong.PixelPool == 32L * 1024 * 1024 && strong.ImageCache == 64L * 1024 * 1024 && strong.Derived == 16L * 1024 * 1024,
            $"pool={strong.PixelPool / (1024 * 1024)}MB image={strong.ImageCache / (1024 * 1024)}MB derived={strong.Derived / (1024 * 1024)}MB");

        Check("gate.budgets.weak-is-strictly-smaller every weak cap is strictly below its discrete twin — the property that actually matters, stated independently of the numbers so a future re-tune cannot invert one of the three by accident",
            weak.PixelPool < strong.PixelPool && weak.ImageCache < strong.ImageCache && weak.Derived < strong.Derived,
            $"pool={weak.PixelPool}<{strong.PixelPool} image={weak.ImageCache}<{strong.ImageCache} derived={weak.Derived}<{strong.Derived}");

        // The cache charges what the GPU COMMITS, not what the decoder produced. 150px is the case that motivated it:
        // a 256 bucket, so 262 144 B committed against 90 000 B of pixels — the 2.9x that made a 24 MB cap describe
        // ~84 MB of real memory.
        long c150 = ImageCache.CommittedBytesFor(150, 150);
        long c64 = ImageCache.CommittedBytesFor(64, 64);
        long c513 = ImageCache.CommittedBytesFor(513, 200);
        Check("gate.budgets.image-charge-is-committed-not-decoded the cache charges the square bucket the texture store actually commits (64 KiB-aligned), not width x height x 4 — a 150px cover costs a 256 bucket, and charging the decoded figure is what let a 24 MB budget hold ~84 MB of GPU memory",
            c150 == 256L * 256 * 4 && c64 == 64L * 1024 && c150 > 150L * 150 * 4
            && c513 >= 513L * 200 * 4 && c513 % (64L * 1024) == 0,
            $"150px={c150} (decoded {150 * 150 * 4}) 64px={c64} oversize513={c513}");

        // The cache must honour a supplied derived budget rather than re-deriving one from the process-global tier,
        // which is the specific mistake this whole change removes. 0 means "not supplied" and falls back to discrete.
        var suppliedWeak = new ImageCache(new FakeImageDecoder(), weak.ImageCache, weak.Derived);
        var suppliedNone = new ImageCache(new FakeImageDecoder(), strong.ImageCache);
        Check("gate.budgets.cache-takes-the-derived-budget-it-is-given ImageCache uses the derived/blur cap passed by the host and falls back to the discrete default only when none is supplied — it never reads GpuProfile itself, because that read is a field initializer and would run before the tier is published",
            suppliedWeak.DerivedBudgetBytes == weak.Derived && suppliedNone.DerivedBudgetBytes == GpuMemoryBudgets.DerivedDefault,
            $"supplied={suppliedWeak.DerivedBudgetBytes} default={suppliedNone.DerivedBudgetBytes}");
    }

    // ── gate.imgatlas.* — the small-image atlas packer (ImageAtlasPacker) ─────────────────────────────────────────
    // The GPU-side store (FluentGpu.Windows/D3D12/ImageTextureStore) is TerraFX-bound and cannot run here, so the
    // POLICY it drives lives in the portable packer and is gated headlessly: cell geometry (no overlap, real gutters,
    // a bilinear footprint that cannot leave its cell), page growth, eviction reuse without growth, the O(pages)
    // census, and the barrier-free invariant that keeps a CPU-written (UMA/Adreno) page off the COPY_DEST path.
    static void AtlasPackerChecks()
    {
        const int Side = 1024;
        long pageBytes = (long)Side * Side * 4;   // 4 MiB — the store passes the device's real committed size

        // gate.imgatlas.grid — geometry. Every cell of every packed bucket is inside the page, no two cells overlap,
        // every pair is separated by at least the gutter on one axis, and the sampler footprint the store hands the GPU
        // ([origin … origin+size], the half-texel-inset UV's bilinear reach) never leaves the cell it belongs to.
        {
            var p = new ImageAtlasPacker(Side, pageBytes, ImageAtlasUpload.CpuWrite);
            bool ok = true;
            var detail = "";
            foreach (int bucket in new[] { 64, 128 })
            {
                int n = p.CellsPerAxis(bucket);
                int cap = p.CellCapacity(bucket);
                if (n <= 0 || cap != n * n) { ok = false; detail += $" cap({bucket})={cap}/n={n};"; continue; }
                var xs = new int[cap];
                var ys = new int[cap];
                for (int i = 0; i < cap; i++)
                {
                    if (!p.TryCellOrigin(bucket, i, out xs[i], out ys[i])) { ok = false; detail += $" origin({bucket},{i});"; break; }
                    // inside the page, gutter included on all four sides
                    if (xs[i] < p.Gutter || ys[i] < p.Gutter ||
                        xs[i] + bucket + p.Gutter > Side || ys[i] + bucket + p.Gutter > Side)
                    { ok = false; detail += $" bounds({bucket},{i})=({xs[i]},{ys[i]});"; }
                }
                // an out-of-range slot is refused rather than aliased onto cell 0
                if (p.TryCellOrigin(bucket, cap, out _, out _)) { ok = false; detail += $" oob({bucket});"; }
                for (int a = 0; a < cap && ok; a++)
                    for (int b = a + 1; b < cap; b++)
                    {
                        bool sepX = xs[a] + bucket + p.Gutter <= xs[b] || xs[b] + bucket + p.Gutter <= xs[a];
                        bool sepY = ys[a] + bucket + p.Gutter <= ys[b] || ys[b] + bucket + p.Gutter <= ys[a];
                        if (!sepX && !sepY) { ok = false; detail += $" overlap({bucket},{a},{b});"; break; }
                    }
            }
            Check("gate.imgatlas.grid cells fit the page, never overlap, keep a full gutter apart, and an out-of-range slot is refused",
                ok, $"gutter={p.Gutter} cells64={p.CellCapacity(64)} cells128={p.CellCapacity(128)}{detail}");
        }

        // gate.imgatlas.barrier-free — THE Adreno invariant, as a value rather than a comment: a CPU-written page is
        // never a CopyTextureRegion destination, so the store never emits the COPY_DEST→PIXEL_SHADER_RESOURCE pair the
        // Qualcomm UMD mishandles (adreno-hang-fixes.md M1). The discrete staging page still needs it.
        {
            var uma = new ImageAtlasPacker(Side, pageBytes, ImageAtlasUpload.CpuWrite);
            var discrete = new ImageAtlasPacker(Side, pageBytes, ImageAtlasUpload.GpuCopy);
            Check("gate.imgatlas.barrier-free a CPU-written page requires NO COPY_DEST transition; a GPU-copied one does",
                !uma.RequiresCopyDestTransition && discrete.RequiresCopyDestTransition,
                $"uma={uma.RequiresCopyDestTransition} discrete={discrete.RequiresCopyDestTransition}");
        }

        // gate.imgatlas.growth — one page fills completely before a second is created, and the census counts PAGES.
        {
            var p = new ImageAtlasPacker(Side, pageBytes, ImageAtlasUpload.CpuWrite);
            int cap = p.CellCapacity(64);
            var cells = new ImageAtlasCell[cap + 1];
            bool ok = true;
            for (int i = 0; i < cap; i++)
            {
                if (!p.TryAcquire(64, out cells[i]))
                {
                    int slot = p.TryReservePage(64);
                    cells[i] = slot >= 0 ? p.CommitPage(slot) : default;
                }
                if (!cells[i].IsValid) ok = false;
            }
            bool onePage = p.LivePageCount == 1 && p.CellsInUse == cap && p.TotalPageBytes == pageBytes;
            bool fullNeedsGrowth = !p.TryAcquire(64, out _);
            int second = p.TryReservePage(64);
            cells[cap] = second >= 0 ? p.CommitPage(second) : default;
            bool grew = p.LivePageCount == 2 && p.TotalPageBytes == 2 * pageBytes && cells[cap].Page == 1;
            Check("gate.imgatlas.growth a page fills to capacity before the atlas grows; bytes/count are per PAGE",
                ok && onePage && fullNeedsGrowth && grew,
                $"cap={cap} pages={p.LivePageCount} cells={p.CellsInUse} bytes={p.TotalPageBytes} full={fullNeedsGrowth}");
        }

        // gate.imgatlas.reuse — eviction reuses cells instead of growing: fill a page, release every cell, refill it,
        // and the atlas must still be ONE page (peak included). Then the last release retires the page and the census
        // drops to zero — bytes are freed, not merely unreferenced.
        {
            var p = new ImageAtlasPacker(Side, pageBytes, ImageAtlasUpload.CpuWrite);
            int cap = p.CellCapacity(128);
            var cells = new ImageAtlasCell[cap];
            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < cap; i++)
                {
                    if (!p.TryAcquire(128, out cells[i]))
                    {
                        int slot = p.TryReservePage(128);
                        cells[i] = slot >= 0 ? p.CommitPage(slot) : default;
                    }
                }
                if (round == 2) break;
                for (int i = 0; i < cap; i++) p.Release(cells[i], out _);
            }
            bool noGrowth = p.LivePageCount == 1 && p.PeakLivePageCount == 1 && p.CellsInUse == cap;
            bool emptyOnLast = true;
            for (int i = 0; i < cap; i++)
            {
                p.Release(cells[i], out bool empty);
                if (empty != (i == cap - 1)) emptyOnLast = false;
            }
            p.RetirePage(cells[0].Page);
            bool freed = p.LivePageCount == 0 && p.TotalPageBytes == 0 && p.CellsInUse == 0;
            // A placement from the retired page must not corrupt the next page that reuses the slot (generations).
            int reused = p.TryReservePage(64);
            var fresh = reused >= 0 ? p.CommitPage(reused) : default;
            int freeBefore = p.PageFreeCells(fresh.Page);
            p.Release(cells[0], out bool staleEmptied);
            bool staleRejected = !staleEmptied && p.PageFreeCells(fresh.Page) == freeBefore && p.CellsInUse == 1;
            Check("gate.imgatlas.reuse evict+refill never grows the atlas; the last cell retires the page (bytes freed); a stale placement is rejected by generation",
                noGrowth && emptyOnLast && freed && reused == cells[0].Page && staleRejected,
                $"pages={p.LivePageCount} peak={p.PeakLivePageCount} reusedSlot={reused} stale={staleRejected} bytes={p.TotalPageBytes}");
        }

        // gate.imgatlas.census — 1000 thumbnails: the atlas byte line is O(pages) and strictly cheaper than one
        // committed texture per image, which is rounded up to the 64 KiB placement granularity (a 64² BGRA8 thumb is
        // 16 KiB of pixels in a 64 KiB commit — the 4× waste this packing exists to remove).
        {
            const int Thumbs = 1000;
            const long Placement = 65536;
            var p = new ImageAtlasPacker(Side, pageBytes, ImageAtlasUpload.CpuWrite);
            for (int i = 0; i < Thumbs; i++)
                if (!p.TryAcquire(64, out _))
                {
                    int slot = p.TryReservePage(64);
                    if (slot >= 0) p.CommitPage(slot);
                }
            int cap = p.CellCapacity(64);
            int expectPages = (Thumbs + cap - 1) / cap;
            long perImage = Thumbs * Placement;                 // today: one committed 64 KiB resource per thumbnail
            bool ok = p.LivePageCount == expectPages
                   && p.CellsInUse == Thumbs
                   && p.TotalPageBytes == (long)expectPages * pageBytes
                   && p.TotalPageBytes < perImage;
            Check("gate.imgatlas.census 1000 packed thumbs cost O(pages) bytes and O(pages) resources, strictly under one 64 KiB-granular texture each",
                ok, $"pages={p.LivePageCount}/{expectPages} cells={p.CellsInUse} packed={p.TotalPageBytes}B perImage={perImage}B eff={p.FullPageEfficiency(64):0.000}");
        }

        // gate.imgatlas.alloc — acquire/release is zero-allocation once the pages exist (the store runs it inside the
        // render-thread image drain; a per-thumbnail allocation there is a Gen0 tax on every scroll).
        {
            var p = new ImageAtlasPacker(Side, pageBytes, ImageAtlasUpload.CpuWrite);
            int cap = p.CellCapacity(64);
            var cells = new ImageAtlasCell[cap];
            for (int i = 0; i < cap; i++)
                if (!p.TryAcquire(64, out cells[i]))
                {
                    int slot = p.TryReservePage(64);
                    cells[i] = slot >= 0 ? p.CommitPage(slot) : default;
                }
            for (int i = 0; i < cap; i++) p.Release(cells[i], out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 8; round++)
            {
                for (int i = 0; i < cap; i++) p.TryAcquire(64, out cells[i]);
                for (int i = 0; i < cap; i++) p.Release(cells[i], out _);
            }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("gate.imgatlas.alloc warm acquire/release of a full page ×8 allocates nothing",
                delta == 0, $"delta={delta}B cap={cap}");
        }
    }

    static void IconChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        static bool SameRgb(ColorF a, ColorF b) => Near(a.R, b.R, 0.02f) && Near(a.G, b.G, 0.02f) && Near(a.B, b.B, 0.02f);

        // gate.icon.parse — the SVG path parser + geometry table: a straight-line polygon interns as an exact contour
        // (1 contour / 3 points / view-box-normalized bounds); a cubic flattens to many points in one contour; malformed
        // input never throws (clamp-not-crash, validation.md). Uses a LOCAL table so the shared RasterCount stays clean.
        {
            var tbl = new IconGeometryTable();
            int tri = tbl.Register("M4 3 L13 8 L4 13 Z", 16f, 16f, evenOdd: false);
            var (triC, triP) = tbl.ShapeOf(tri);
            var tb = tbl.BoundsOf(tri);
            bool triOk = triC == 1 && triP == 3
                && Near(tb.MinX, 4f / 16f, 0.01f) && Near(tb.MaxX, 13f / 16f, 0.01f)
                && Near(tb.MinY, 3f / 16f, 0.01f) && Near(tb.MaxY, 13f / 16f, 0.01f);

            int cur = tbl.Register("M2 8 C2 2 14 2 14 8 Z", 16f, 16f, evenOdd: false);
            var (curC, curP) = tbl.ShapeOf(cur);
            var cb = tbl.BoundsOf(cur);
            bool curveOk = curC == 1 && curP > 6 && cb.MinX >= 0f && cb.MaxX <= 1.001f && cb.MinY >= 0f && cb.MaxY <= 1.001f;

            bool noThrow = true;
            try { tbl.Register("M q z 9-.,,", 16f, 16f); tbl.Register("!!!garbage###", 16f, 16f); tbl.Register("A5 5 0 0", 16f, 16f); }
            catch { noThrow = false; }

            Check("gate.icon.parse polygon interns exact (1/3 + bounds), a cubic flattens to one many-point contour, malformed input never throws",
                triOk && curveOk && noThrow, $"tri=({triC}/{triP}) curve=({curC}/{curP}) bounds=({tb.MinX:0.00}..{tb.MaxX:0.00}) noThrow={noThrow}");
        }

        // gate.icon.raster — the scanline fill: a full 0..1 square covers the whole buffer (center 255); two same-wound
        // overlapping squares DIVERGE by fill rule — even-odd punches a hole (center ~0), nonzero keeps it filled (~255).
        {
            var sq = new float[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
            var starts1 = new[] { 0 }; var counts1 = new[] { 4 };
            var full = new byte[16 * 16];
            IconRaster.Rasterize(sq, starts1, counts1, evenOdd: false, 16, 16, full);
            byte center = full[8 * 16 + 8];

            // outer 0..1 + inner 0.25..0.75, IDENTICAL vertex order (same winding).
            var two = new float[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f,   0.25f, 0.25f, 0.75f, 0.25f, 0.75f, 0.75f, 0.25f, 0.75f };
            var starts2 = new[] { 0, 4 }; var counts2 = new[] { 4, 4 };
            var eo = new byte[16 * 16]; var nz = new byte[16 * 16];
            IconRaster.Rasterize(two, starts2, counts2, evenOdd: true, 16, 16, eo);
            IconRaster.Rasterize(two, starts2, counts2, evenOdd: false, 16, 16, nz);
            byte eoCenter = eo[8 * 16 + 8]; byte nzCenter = nz[8 * 16 + 8];

            Check("gate.icon.raster a 0..1 square fills solid (center 255); even-odd holes the overlap (center ~0) while nonzero fills it (~255)",
                center == 255 && eoCenter < 40 && nzCenter > 200, $"full={center} eoCenter={eoCenter} nzCenter={nzCenter}");
        }

        // gate.icon.record — mounting a layered ThemedIcon ("Copy" = 4 role layers) emits 4 DrawIconMask ops, each with a
        // live PathId and an opaque tint; the neutral Base + the accent highlight are present with their resolved tints.
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("icon-record", new Size2(240, 240), 1f)); w.Show();
            var dev = new HeadlessGpuDevice();
            using var host = new AppHost(app, w, dev, fonts, strings, new IconProbe { Name = "Copy" });
            host.RunFrame();
            int n = dev.LastIconMasks.Count;
            bool allLive = n > 0; bool base_ = false, accent = false;
            foreach (var m in dev.LastIconMasks)
            {
                if (m.PathId == 0 || m.Tint.A <= 0f) allLive = false;
                if (SameRgb(m.Tint, Tok.IconBase)) base_ = true;
                if (SameRgb(m.Tint, Tok.AccentDefault)) accent = true;
            }
            Check("gate.icon.record a layered ThemedIcon emits one DrawIconMask per role layer with live PathIds + resolved Base/Accent tints",
                n == 4 && allLive && base_ && accent, $"masks={n} allLive={allLive} base={base_} accent={accent}");
        }

        // gate.icon.retheme — an accent swap re-fires the bound Tint thunk → the accent layer's DrawIconMask tint changes
        // in the stream, WITHOUT any re-raster (masks are colorless: IconGeometryTable.Shared.RasterCount is unchanged).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("icon-retheme", new Size2(240, 240), 1f)); w.Show();
            var dev = new HeadlessGpuDevice();
            using var host = new AppHost(app, w, dev, fonts, strings, new IconProbe { Name = "Play" });   // single accent layer
            host.RunFrame();
            ColorF tint0 = dev.LastIconMasks.Count > 0 ? dev.LastIconMasks[0].Tint : default;
            long rc0 = IconGeometryTable.Shared.RasterCount;
            try
            {
                Tok.SetAccent(ColorF.FromRgba(0xE0, 0x40, 0x40));   // red override
                host.Reconciler.RethemeAll();
                host.RunFrame();
                ColorF tint1 = dev.LastIconMasks.Count > 0 ? dev.LastIconMasks[0].Tint : default;
                long rc1 = IconGeometryTable.Shared.RasterCount;
                bool changed = !SameRgb(tint0, tint1) && tint1.R > tint1.B + 0.1f;   // now reddish
                bool noReRaster = rc1 == rc0;
                Check("gate.icon.retheme an accent swap recolors the icon's DrawIconMask tint with NO re-raster (RasterCount unchanged)",
                    changed && noReRaster, $"tint0=({tint0.R:0.00},{tint0.G:0.00},{tint0.B:0.00}) tint1=({tint1.R:0.00},{tint1.G:0.00},{tint1.B:0.00}) raster {rc0}->{rc1}");
            }
            finally { Tok.SetAccent(null); host.Reconciler.RethemeAll(); }   // restore global accent for later gates
        }

        // gate.theme.accent-ramp — an accent override resolves the accent FILL THEME-AWARE: AccentDefault is the WinUI
        // Dark1 shade in LIGHT and the Light2 shade in DARK (the fix for the light-theme accent bug, where one flat color
        // was reused in both themes). The exact shades come from AccentRamp.Derive. Restores theme + accent in finally.
        {
            var savedTheme = Tok.Theme;
            try
            {
                var baseC = ColorF.FromRgba(0x00, 0x78, 0xD4);   // the WinUI default accent
                var ramp = AccentRamp.Derive(baseC);
                Tok.SetAccent(baseC);
                Tok.Use(ThemeKind.Light); var light = Tok.AccentDefault;
                Tok.Use(ThemeKind.Dark);  var dark  = Tok.AccentDefault;
                bool lightIsDark1 = SameRgb(light, ramp.Dark1);
                bool darkIsLight2 = SameRgb(dark, ramp.Light2);
                bool differ = !SameRgb(light, dark);
                Check("gate.theme.accent-ramp an accent override resolves AccentDefault theme-aware (Dark1 in light, Light2 in dark, distinct)",
                    lightIsDark1 && darkIsLight2 && differ,
                    $"light=({light.R:0.00},{light.G:0.00},{light.B:0.00}) dark=({dark.R:0.00},{dark.G:0.00},{dark.B:0.00}) dark1=({ramp.Dark1.R:0.00},{ramp.Dark1.G:0.00},{ramp.Dark1.B:0.00}) light2=({ramp.Light2.R:0.00},{ramp.Light2.G:0.00},{ramp.Light2.B:0.00})");
            }
            finally { Tok.SetAccent(null); Tok.Use(savedTheme); }
        }

        // gate.icon.alloc — icons on screen record as pure POD: 0 managed bytes in phases 6–13 across steady frames
        // (warm frames skipped for JIT, per the flaky-alloc note).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("icon-alloc", new Size2(240, 240), 1f)); w.Show();
            var dev = new HeadlessGpuDevice();
            using var host = new AppHost(app, w, dev, fonts, strings, new IconProbe { Name = "Copy" });
            long worst = 0;
            for (int i = 0; i < 12; i++) { var f = host.RunFrame(); if (i >= 3 && f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
            Check("gate.icon.alloc icons on screen record with 0 managed alloc in phases 6–13 (steady frames)",
                worst == 0, $"worstHotAlloc={worst}B");
        }

        // gate.icon.outline / gate.icon.disabled — the role resolution: Outline mode paints the Base (foreground) role
        // and returns a single leaf; a disabled layer resolves to TextDisabled regardless of role, and a status recolor
        // routes the Accent layer to the severity fill.
        {
            ColorF baseOn = ThemedIcon.ResolveRole(IconRole.Base, IconColorType.Normal, enabled: true, onAccent: false, 1f);
            ColorF accOn = ThemedIcon.ResolveRole(IconRole.Accent, IconColorType.Normal, enabled: true, onAccent: false, 1f);
            ColorF accOff = ThemedIcon.ResolveRole(IconRole.Accent, IconColorType.Normal, enabled: false, onAccent: false, 1f);
            ColorF critical = ThemedIcon.ResolveRole(IconRole.Accent, IconColorType.Critical, enabled: true, onAccent: false, 1f);
            var outline = ThemedIcon.Create("Folder", 16f, mode: IconMode.Outline);   // Folder ships a single Outline path
            bool outlineOk = outline is IconLayerEl && SameRgb(baseOn, Tok.IconBase);
            bool disabledOk = SameRgb(accOff, Tok.TextDisabled) && !SameRgb(accOn, accOff) && SameRgb(accOn, Tok.AccentDefault);
            bool statusOk = SameRgb(critical, Tok.SystemFillCritical);
            Check("gate.icon.outline/disabled Outline paints Base + returns a leaf; a disabled layer is TextDisabled; a status recolor routes Accent to the severity fill",
                outlineOk && disabledOk && statusOk, $"outline={outline.GetType().Name} baseOk={SameRgb(baseOn, Tok.IconBase)} disabled={disabledOk} status={statusOk}");
        }
    }




    static void ImageCacheChecks()
    {
        // Budget sized in the currency the cache CHARGES (committed bucket bytes, not decoded pixels): room for two
        // images, so admitting a third must evict. A 10x10 image costs one 64-bucket = 64 KiB committed, not 400 B.
        long oneImage = ImageCache.CommittedBytesFor(10, 10);
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: oneImage * 2 + oneImage / 2);
        var a = cache.Request("a", 10, 10);
        bool pending = cache.StateOf(a) == ImageState.Pending;
        cache.Pump();
        bool ready = cache.StateOf(a) == ImageState.Ready && cache.SizeOf(a) == (10, 10);
        bool dedup = cache.Request("a", 10, 10).Id == a.Id;

        cache.Pin(a);                                                            // a is "on screen"
        var b = cache.Request("b", 10, 10);
        var c = cache.Request("c", 10, 10);
        cache.Pump();                                                           // a+b+c over budget → evict LRU unpinned (b)
        bool keptPinned = cache.StateOf(a) == ImageState.Ready;                  // pinned survived eviction
        bool withinBudget = cache.UsedBytes <= oneImage * 2 + oneImage / 2;
        bool evictedTombstone = cache.StateOf(b) == ImageState.None;
        cache.Pin(b);                                                            // retained ImageEl re-enters with the old handle
        bool rehydratePending = cache.StateOf(b) == ImageState.Pending;
        cache.Pump();
        bool rehydrated = cache.StateOf(b) == ImageState.Ready && cache.RefsOf(b) == 1;
        Check("45. ImageCache: states, dedup, liveness-pinned LRU evict, re-pin rehydrates evicted handles",
            pending && ready && dedup && keptPinned && withinBudget && evictedTombstone && rehydratePending && rehydrated,
            $"used={cache.UsedBytes} ready={cache.ReadyCount} aRefs={cache.RefsOf(a)} b={cache.StateOf(b)} bRefs={cache.RefsOf(b)}");

        // GPU admission is part of readiness: a decoded image rejected by the backend must be Failed with zero
        // residency bytes, not a texture-less Ready handle. It retries only after the visible owner unpins it.
        bool admit = false;
        var admission = new ImageCache(new FakeImageDecoder());
        admission.SetPixelAttemptSink((_, _, _, _) =>
            admit ? ImageUploadResult.Accepted : ImageUploadResult.ResourceExhausted);
        var rejected = admission.Request("capacity", 32, 32);
        admission.Pin(rejected);
        admission.Pump();
        bool rejectedClean = admission.StateOf(rejected) == ImageState.Failed
            && admission.FailureOf(rejected) == ImageFailureKind.GpuResourceExhausted
            && admission.UsedBytes == 0 && admission.ReadyCount == 0;
        admission.Request("capacity", 32, 32);                                  // still pinned: no retry loop
        bool noPinnedRetry = admission.PendingCount == 0;
        admission.Unpin(rejected);
        admit = true;
        var retried = admission.Request("capacity", 32, 32);                    // later remount: retry same handle
        bool retryPending = retried == rejected && admission.StateOf(retried) == ImageState.Pending;
        admission.Pump();
        bool retryReady = admission.StateOf(retried) == ImageState.Ready && admission.UsedBytes == ImageCache.CommittedBytesFor(32, 32);
        Check("45b. ImageCache: GPU rejection never becomes Ready; unpinned remount retries",
            rejectedClean && noPinnedRetry && retryPending && retryReady,
            $"state={admission.StateOf(retried)} fail={admission.FailureOf(retried)} used={admission.UsedBytes} pending={admission.PendingCount}");

        // A saturated prefetch lane must not poison the real visible image. The scheduler reports "not accepted";
        // ImageCache leaves a non-pending tombstone under the same key, so the following Visible request restarts it.
        var droppedPrefetch = new ImageCache(new DropPrefetchDecoder());
        var warm = droppedPrefetch.Prefetch("warm", 64, 64);
        bool dropDidNotStick = droppedPrefetch.StateOf(warm) == ImageState.None && droppedPrefetch.PendingCount == 0;
        var visible = droppedPrefetch.Request("warm", 64, 64, ImagePriority.Visible);
        bool visibleRestarted = visible == warm && droppedPrefetch.StateOf(visible) == ImageState.Pending
            && droppedPrefetch.PendingCount == 1;
        droppedPrefetch.Pump();
        bool visibleReady = droppedPrefetch.StateOf(visible) == ImageState.Ready && droppedPrefetch.SizeOf(visible) == (64, 64);
        Check("45c. ImageCache: dropped prefetch does not leave a forever-pending handle; visible request restarts it",
            dropDidNotStick && visibleRestarted && visibleReady,
            $"afterDrop={droppedPrefetch.StateOf(warm)} pending={droppedPrefetch.PendingCount} ready={visibleReady}");

        // Static derivatives are keyed only by source pixels + bake parameters, never viewport/style state. They stay
        // Pending until the render handoff posts completion, then account against the derived residency budget.
        var baked = new ImageCache(new FakeImageDecoder());
        var bakeQueue = new FluentGpu.Hosting.Threading.BakedBlurQueue();
        baked.SetBakedBlurQueue(bakeQueue);
        var source = baked.Request("baked-source", 512, 256);
        baked.Pump();
        var spec = new BakedBlurSpec(26f, 0.5f);
        var d0 = baked.RequestBakedBlur(source, 512, 256, in spec);
        var d0Again = baked.RequestBakedBlur(source, 512, 256, in spec);
        var otherSpec = new BakedBlurSpec(18f, 0.5f);
        var d1 = baked.RequestBakedBlur(source, 512, 256, in otherSpec);
        bool jobs = bakeQueue.TryDequeueJob(out var j0) && bakeQueue.TryDequeueJob(out var j1);
        bool keying = d0 == d0Again && d0 != d1 && jobs && j0.SourceId == source.Id && j0.OutputW == 256 && j0.OutputH == 128;
        bakeQueue.Post(new FluentGpu.Hosting.Threading.BakedBlurQueue.Result(j0.Id, j0.Generation, true, j0.OutputW, j0.OutputH));
        baked.Pump();
        bool derivedReady = baked.StateOf(d0) == ImageState.Ready && baked.SizeOf(d0) == (256, 128)
            && baked.DerivedUsedBytes == ImageCache.CommittedBytesFor(256, 128);
        Check("45d. ImageCache baked blur: position/style-independent dedup, parameter fork, queued completion, derived byte accounting",
            keying && derivedReady,
            $"dedup={d0==d0Again} fork={d0!=d1} job={jobs} size={baked.SizeOf(d0)} bytes={baked.DerivedUsedBytes}");
    }

    static void ImageElChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("img", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ImageProbe());

        host.RunFrame();
        bool drawn = device.LastImages.Count == 1;
        var cmd = drawn ? device.LastImages[0] : default;
        var h = new ImageHandle(cmd.ImageId);
        bool ready = drawn && cmd.Ready == 1 && cmd.ImageId != 0 && host.Images.StateOf(h) == ImageState.Ready;
        bool pinned = host.Images.RefsOf(h) == 1;
        bool placeholder = Near(cmd.Placeholder.R, 0x33 / 255f) && Near(cmd.Radii.TopLeft, 6f);
        Check("46. ImageEl: decode→ready, residency-pinned, DrawImage emitted", drawn && ready && pinned && placeholder,
            $"images={device.LastImages.Count} ready={cmd.Ready} refs={host.Images.RefsOf(h)}");

        // The decode's pixels must reach the GPU backend via the UploadImage seam (media-pipeline §4.1) at the decoded
        // bucket size — proves the decoder→cache.Pump→host sink→device texture-upload chain end to end.
        bool uploaded = device.Uploads.Count == 1
            && device.Uploads[0].id == cmd.ImageId && device.Uploads[0].w == 80 && device.Uploads[0].h == 80
            && device.ResidentImages.ContainsKey(cmd.ImageId);
        int uw = device.Uploads.Count > 0 ? device.Uploads[0].w : 0;
        int uh = device.Uploads.Count > 0 ? device.Uploads[0].h : 0;
        Check("46b. ImageEl: decoded pixels uploaded to the GPU backend at bucket size", uploaded,
            $"uploads={device.Uploads.Count} dims={uw}x{uh}");

        using var bakedApp = new HeadlessPlatformApp();
        var bakedWindow = new HeadlessWindow(new WindowDesc("baked-img", new Size2(480, 320), 1f));
        bakedWindow.Show();
        var bakedDevice = new HeadlessGpuDevice();
        using var bakedHost = new AppHost(bakedApp, bakedWindow, bakedDevice, fonts, strings, new BakedImageProbe());
        bakedHost.RunFrame();
        // Frame 1: the un-baked SOURCE stands in (never an empty draw) while the blur bake is queued.
        int fallbackId = bakedDevice.LastImages.Count == 1 ? bakedDevice.LastImages[0].ImageId : 0;
        bool fallbackFirst = fallbackId != 0;
        int bakeSettleFrames = 0;
        while (bakeSettleFrames++ < 60 && bakedDevice.LastImages.Count == 1
               && bakedDevice.LastImages[0].ImageId == fallbackId)
            bakedHost.RunFrame();
        var bakedCmd = bakedDevice.LastImages.Count == 1 ? bakedDevice.LastImages[0] : default;
        bool oneQuad = bakedDevice.LastImages.Count == 1 && bakedDevice.LastLayers.Count == 0;
        bool selectedDerived = bakedCmd.ImageId != 0 && bakedCmd.ImageId != fallbackId
            && bakedHost.Images.StateOf(new ImageHandle(bakedCmd.ImageId)) == ImageState.Ready;
        bool styling = Near(bakedCmd.Overlay.A, 0.42f) && bakedCmd.MaskEdges == (int)EdgeMask.Top
            && Near(bakedCmd.MaskTop, 24f) && Near(bakedCmd.MaskIntensity, 1f);
        // The bake must land within a couple of frames of the source, NOT wait for a globally quiet frame: a page that
        // reconciles every frame (the real homepage) never has one, which starved the queue outright (W2.75-B).
        bool bakesPromptly = bakeSettleFrames <= 3;
        Check("46c. Baked ImageEl: source fallback then persistent derived handle within a frame or two (no quiet-frame wait); overlay+mask stay in one DrawImage with zero layers",
            fallbackFirst && bakesPromptly && oneQuad && selectedDerived && styling,
            $"fallbackFirst={fallbackFirst} settleFrames={bakeSettleFrames} fallback={fallbackId} derived={bakedCmd.ImageId} draws={bakedDevice.LastImages.Count} layers={bakedDevice.LastLayers.Count} mask={bakedCmd.MaskEdges}");
    }

    static void ImageCornerClampChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("image-radius", new Size2(320, 160), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ImageCornerClampProbe());
        host.RunFrame();

        bool count = device.LastImages.Count == 2;
        var square = count ? device.LastImages[0].Radii : default;
        var nonSquare = count ? device.LastImages[1].Radii : default;
        bool clamped = Near(square.TopLeft, 18f) && Near(square.TopRight, 18f)
            && Near(square.BottomRight, 18f) && Near(square.BottomLeft, 18f)
            && Near(nonSquare.TopLeft, 12f) && Near(nonSquare.TopRight, 6f)
            && Near(nonSquare.BottomRight, 12f) && Near(nonSquare.BottomLeft, 0f);
        Check("46d. ImageEl: record-time radii clamp honors square/non-square boxes and preserves smaller corners",
            count && clamped,
            $"draws={device.LastImages.Count} square={square} nonSquare={nonSquare}");
    }

    static (RectF art, float innerW) RenderAspectTile(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("aspect", new Size2(640, 520), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new AspectTileProbe());
        host.RunFrame();
        var art = device.LastImages.Count > 0 ? device.LastImages[0].Rect : default;
        return (art, AspectTileProbe.CardWidth - 24f);   // 12px padding each side
    }

    static void ImageFitChecks(StringTable strings)
    {
        // A) Content-fit math (pure function the recorder uses). Source vs box aspect drives the crop/inset.
        var (drId, uvId) = SceneRecorder.ImageContentFit(ImageFit.Cover, new RectF(0, 0, 100, 100), 300, 300);  // square→square: no crop
        bool coverSquare = Near(uvId.X, 0) && Near(uvId.Y, 0) && Near(uvId.W, 1) && Near(uvId.H, 1) && Near(drId.W, 100) && Near(drId.H, 100);
        var (_, uvWide) = SceneRecorder.ImageContentFit(ImageFit.Cover, new RectF(0, 0, 200, 100), 100, 100);   // wide box, square src → crop top/bottom, centered
        bool coverWide = Near(uvWide.X, 0) && Near(uvWide.Y, 0.25f, 0.001f) && Near(uvWide.W, 1) && Near(uvWide.H, 0.5f, 0.001f);
        var (_, uvTall) = SceneRecorder.ImageContentFit(ImageFit.Cover, new RectF(0, 0, 100, 200), 100, 100);   // tall box → crop left/right
        bool coverTall = Near(uvTall.X, 0.25f, 0.001f) && Near(uvTall.Y, 0) && Near(uvTall.W, 0.5f, 0.001f) && Near(uvTall.H, 1);
        var (drContain, uvContain) = SceneRecorder.ImageContentFit(ImageFit.Contain, new RectF(0, 0, 200, 100), 100, 100);   // wide box, square src → quad shrinks to 100, centered; uv full
        bool contain = Near(uvContain.W, 1) && Near(uvContain.H, 1) && Near(drContain.X, 50) && Near(drContain.W, 100) && Near(drContain.H, 100);
        var (drFill, uvFill) = SceneRecorder.ImageContentFit(ImageFit.Fill, new RectF(0, 0, 200, 100), 100, 100);   // identity (stretch)
        bool fill = Near(uvFill.W, 1) && Near(uvFill.H, 1) && Near(drFill.W, 200) && Near(drFill.H, 100);
        var (drUnk, uvUnk) = SceneRecorder.ImageContentFit(ImageFit.Cover, new RectF(0, 0, 200, 100), 0, 0);   // unknown source → identity
        bool unknown = Near(uvUnk.W, 1) && Near(drUnk.W, 200);
        Check("46e. ImageFit math: Cover crops centered (wide/tall), Contain insets, Fill/unknown identity",
            coverSquare && coverWide && coverTall && contain && fill && unknown,
            $"coverWide uv=({uvWide.X:0.##},{uvWide.Y:0.##},{uvWide.W:0.##},{uvWide.H:0.##}) contain dr.x={drContain.X:0}");

        // B) Aspect-ratio sizing end-to-end: a responsive square tile fills its card's content width (no fixed extent),
        // stays square, and scales with the card — so a narrow cell can't overflow a hard-coded tile (the reported bug).
        AspectTileProbe.CardWidth = 200f;
        var (artN, innerN) = RenderAspectTile(strings);
        AspectTileProbe.CardWidth = 360f;
        var (artW, innerW2) = RenderAspectTile(strings);
        bool squareN = Near(artN.W, artN.H) && Near(artN.W, innerN);     // fills the 176px content width, square
        bool squareW = Near(artW.W, artW.H) && Near(artW.W, innerW2);    // fills the 336px content width, square
        bool noOverflow = artN.W <= innerN + 0.5f && artW.W <= innerW2 + 0.5f;
        bool responsive = artW.W > artN.W + 100f;                        // scales with the card, not a fixed 64/150 tile
        Check("46f. responsive image: art fills its card width & stays square (fixed-size overflow fixed)",
            squareN && squareW && noOverflow && responsive,
            $"narrow={artN.W:0}x{artN.H:0} (inner {innerN:0}) wide={artW.W:0}x{artW.H:0} (inner {innerW2:0})");
    }

    static (bool ok, ImageFailureKind fail, int att) DrainOne(DecodeScheduler sched, int id)
    {
        sched.Begin(id, "x", 8, 8);
        (bool ok, ImageFailureKind fail, int att) res = (false, ImageFailureKind.None, 0);
        bool got = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!got && sw.ElapsedMilliseconds < 5000)
        {
            sched.Pump((cid, ok, w, h, f, a) => { res = (ok, f, a); got = true; }, (cid, px, w, h) => { });
            System.Threading.Thread.Sleep(3);
        }
        return res;
    }

    static void DecodeSchedulerChecks()
    {
        static bool WaitPublished(DecodeScheduler scheduler, int timeoutMs = 5000)
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while ((scheduler.RequestCount != 0 || scheduler.Inflight != 0) && wait.ElapsedMilliseconds < timeoutMs)
                System.Threading.Thread.Sleep(2);
            return scheduler.RequestCount == 0 && scheduler.Inflight == 0;
        }

        int cur = 0, maxc = 0; object g = new();
        var codec = new TestCodec(() =>
        {
            int c = System.Threading.Interlocked.Increment(ref cur);
            lock (g) { if (c > maxc) maxc = c; }
            System.Threading.Thread.Sleep(60);                       // hold the worker so decodes overlap
            System.Threading.Interlocked.Decrement(ref cur);
        });
        int done = 0;
        using (var sched = new DecodeScheduler(codec, new TestFetcher(), new DecodeOptions { MaxConcurrency = 4 }))
        {
            const int M = 8;
            for (int i = 1; i <= M; i++) sched.Begin(i, "t" + i, 8, 8);   // non-blocking enqueues
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (done < M && sw.ElapsedMilliseconds < 5000)
            {
                sched.Pump((id, ok, w, h, f, a) => { if (ok) done++; }, (id, px, w, h) => { });
                System.Threading.Thread.Sleep(3);                    // UI stays responsive while workers decode
            }
            Check("46c. DecodeScheduler: off-thread, parallel (N-way), non-blocking decode",
                done == 8 && maxc >= 2, $"done={done}/8 maxConcurrent={maxc} workers={sched.WorkerCount}");
        }

        (bool ok, ImageFailureKind fail, int att) r1;
        using (var sched = new DecodeScheduler(new TestCodec(), new TestFetcher(_ => FetchResult.Fail(ImageFailureKind.NotFound)),
                   new DecodeOptions { MaxAttempts = 3, BackoffBase = TimeSpan.FromMilliseconds(1) }))
            r1 = DrainOne(sched, 1);

        int calls = 0;
        var flaky = new TestFetcher(_ =>
        {
            int c = System.Threading.Interlocked.Increment(ref calls);
            return c < 3 ? FetchResult.Fail(ImageFailureKind.ServerError) : FetchResult.Pooled(ArrayPool<byte>.Shared.Rent(16), 16);
        });
        (bool ok, ImageFailureKind fail, int att) r2;
        using (var sched = new DecodeScheduler(new TestCodec(), flaky, new DecodeOptions { MaxAttempts = 3, BackoffBase = TimeSpan.FromMilliseconds(1) }))
            r2 = DrainOne(sched, 1);

        bool permanent = !r1.ok && r1.fail == ImageFailureKind.NotFound && r1.att == 1;   // 404 → fail fast, no retry
        bool transient = r2.ok && r2.att == 3;                                            // 5xx ×2 then 200 → success on attempt 3
        Check("46d. DecodeScheduler: 404 fails fast (no retry); transient 5xx retried to success",
            permanent && transient, $"404=(ok={r1.ok} {r1.fail} att={r1.att}) flaky=(ok={r2.ok} att={r2.att})");

        // Cancellation/failure notifications are control work, not texture uploads. A recycle storm can therefore
        // resolve every canceled handle in one Pump while the surviving texture still gets the frame's upload slot.
        using (var sched = new DecodeScheduler(new TestCodec(), new TestFetcher(),
                   new DecodeOptions { MaxConcurrency = 1 }))
        {
            const int N = 24;
            for (int i = 1; i <= N; i++) sched.Begin(i, "cancel-storm/" + i, 8, 8);
            for (int i = 1; i < N; i++) sched.Cancel(i);
            bool published = WaitPublished(sched);
            int canceled = 0, ready = 0, pixels = 0;
            sched.Pump(
                (id, ok, w, h, failure, attempts) =>
                {
                    if (ok) ready++;
                    else if (failure == ImageFailureKind.Canceled) canceled++;
                },
                (id, px, w, h) => pixels++);
            Check("46d2. DecodeScheduler: cancellation cleanup drains independently and does not consume the surviving upload slot",
                published && canceled == N - 1 && ready == 1 && pixels == 1
                && sched.LastPumpAppliedCount == 1 && sched.LastPumpAppliedBytes == 8 * 8 * 4,
                $"published={published} canceled={canceled}/{N - 1} ready={ready} pixels={pixels} apply={sched.LastPumpAppliedCount}/{sched.LastPumpAppliedBytes}B");
        }

        // A row can recycle after its worker published pixels but before Pump uploads them. Keep the second result queued
        // behind a 512 KiB upload budget (two 1 MiB covers: the head lands alone), cancel it, then prove cleanup reports
        // Canceled with zero upload/apply charge.
        FluentGpu.Rhi.UploadBudget.BytesPerTurn = 512 * 1024;
        try
        {
            using (var sched = new DecodeScheduler(new TestCodec(), new TestFetcher(),
                       new DecodeOptions { MaxConcurrency = 1 }))
            {
                sched.Begin(101, "late-cancel/1", 512, 512);
                sched.Begin(102, "late-cancel/2", 512, 512);
                bool published = WaitPublished(sched);
                int firstPixels = 0;
                sched.Pump((id, ok, w, h, failure, attempts) => { }, (id, px, w, h) => firstPixels++);
                bool firstBounded = sched.LastPumpAppliedCount == 1 && firstPixels == 1 && sched.HasReadyCompletions;
                sched.Cancel(102);
                int latePixels = 0, lateCanceled = 0;
                sched.Pump(
                    (id, ok, w, h, failure, attempts) =>
                    {
                        if (id == 102 && !ok && failure == ImageFailureKind.Canceled) lateCanceled++;
                    },
                    (id, px, w, h) => latePixels++);
                Check("46d3. DecodeScheduler: completed-but-unapplied cancellation suppresses pixels and costs zero upload budget",
                    published && firstBounded && lateCanceled == 1 && latePixels == 0
                    && sched.LastPumpAppliedCount == 0 && sched.LastPumpAppliedBytes == 0 && !sched.HasReadyCompletions,
                    $"published={published} first={firstBounded} canceled={lateCanceled} latePixels={latePixels} apply={sched.LastPumpAppliedCount}/{sched.LastPumpAppliedBytes}B pending={sched.HasReadyCompletions}");
            }

            // A normal 512x512 BGRA cover is 1 MiB, twice this budget — and it must STILL land: one completion per turn,
            // oldest first, whatever it weighs (the head of a turn is always admitted; the budget bounds only the applies
            // BEHIND it). ONE worker ⇒ 301 completes before 302, so this also pins that the two size lanes preserve
            // completion order rather than reordering by size.
            using (var sched = new DecodeScheduler(new TestCodec(), new TestFetcher(),
                       new DecodeOptions { MaxConcurrency = 1 }))
            {
                sched.Begin(301, "oversized-scroll/cover", 512, 512, ImagePriority.Visible);
                sched.Begin(302, "oversized-scroll/follower", 8, 8, ImagePriority.Visible);
                bool published = WaitPublished(sched);

                int firstId = 0, secondId = 0;
                sched.Pump(
                    (id, ok, w, h, failure, attempts) => { if (ok) firstId = id; },
                    (id, px, w, h) => { });
                int firstCount = sched.LastPumpAppliedCount;
                int firstBytes = sched.LastPumpAppliedBytes;
                bool followerPending = sched.HasReadyCompletions;

                sched.Pump(
                    (id, ok, w, h, failure, attempts) => { if (ok) secondId = id; },
                    (id, px, w, h) => { });

                Check("46d5. DecodeScheduler: an oversized cover lands DURING scroll — one completion per turn, in completion order across both size lanes",
                    published && firstId == 301 && firstCount == 1 && firstBytes == 512 * 512 * 4
                    && followerPending
                    && secondId == 302 && sched.LastPumpAppliedCount == 1 && sched.LastPumpAppliedBytes == 8 * 8 * 4
                    && !sched.HasReadyCompletions,
                    $"published={published} first={firstId}/{firstCount}/{firstBytes}B pending={followerPending} " +
                    $"second={secondId}/{sched.LastPumpAppliedCount}/{sched.LastPumpAppliedBytes}B left={sched.HasReadyCompletions}");
            }

            // 46d6: the budget meters BYTES, not items: under 512 KiB, two 256 KiB covers land per turn, and six tiny thumbs
            // all land in one — there is no per-turn item count.
            using (var sched = new DecodeScheduler(new TestCodec(), new TestFetcher(), new DecodeOptions { MaxConcurrency = 1 }))
            {
                for (int i = 401; i <= 404; i++) sched.Begin(i, "budget-bytes/" + i, 256, 256);
                for (int i = 405; i <= 410; i++) sched.Begin(i, "budget-thumbs/" + i, 8, 8);
                bool published = WaitPublished(sched);
                sched.Pump((id, ok, w, h, failure, attempts) => { }, (id, px, w, h) => { });
                int first = sched.LastPumpAppliedCount, firstBytes = sched.LastPumpAppliedBytes;
                sched.Pump((id, ok, w, h, failure, attempts) => { }, (id, px, w, h) => { });
                int second = sched.LastPumpAppliedCount;
                sched.Pump((id, ok, w, h, failure, attempts) => { }, (id, px, w, h) => { });
                int third = sched.LastPumpAppliedCount;
                Check("46d6. DecodeScheduler: the one upload budget meters bytes — two 256 KiB covers per 512 KiB turn, then all six thumbs in one turn (no item cap)",
                    published && first == 2 && firstBytes == 2 * 256 * 256 * 4 && second == 2 && third == 6 && !sched.HasReadyCompletions,
                    $"published={published} first={first}/{firstBytes}B second={second} third={third}");
            }

            // 46d6b: the head exemption under a small budget — an oversized cover lands ALONE, then the two small thumbs
            // clear on the next pump.
            using (var sched = new DecodeScheduler(new TestCodec(), new TestFetcher(), new DecodeOptions { MaxConcurrency = 1 }))
            {
                sched.Begin(501, "budget-oversized/cover", 512, 512, ImagePriority.Visible);
                sched.Begin(502, "budget-oversized/thumb1", 8, 8, ImagePriority.Visible);
                sched.Begin(503, "budget-oversized/thumb2", 8, 8, ImagePriority.Visible);
                bool published2 = WaitPublished(sched);

                int firstId = 0;
                sched.Pump((id, ok, w, h, failure, attempts) => { if (ok) firstId = id; }, (id, px, w, h) => { });
                int firstCount = sched.LastPumpAppliedCount;
                int firstBytes = sched.LastPumpAppliedBytes;
                bool moreLeft = sched.HasReadyCompletions;

                int secondApplied = 0;
                sched.Pump((id, ok, w, h, failure, attempts) => { if (ok) secondApplied++; }, (id, px, w, h) => { });

                Check("46d6b. DecodeScheduler: an oversized cover lands alone via the head exemption; the two thumbs clear on the next pump",
                    published2 && firstId == 501 && firstCount == 1 && firstBytes == 512 * 512 * 4
                    && moreLeft && secondApplied == 2 && !sched.HasReadyCompletions,
                    $"published={published2} first={firstId}/{firstCount}/{firstBytes}B moreLeft={moreLeft} second={secondApplied}");
            }
        }
        finally { FluentGpu.Rhi.UploadBudget.ResetToDefault(); }
    }

    static void PixelBufferPoolChecks()
    {
        // P1 — pow2 rounding + reuse identity + retained accounting. A non-pow2 minBytes rounds up to the next bucket;
        // a returned buffer is parked (RetainedBytes == its rounded length) and the next Rent pops that SAME array.
        {
            var pool = new FluentGpu.Media.PixelBufferPool();
            byte[] a = pool.Rent(20000);                     // 20000 → 32768 (2^15)
            bool rounded = a.Length == 32768;
            pool.Return(a);
            bool parked = pool.RetainedBytes == 32768;
            byte[] b = pool.Rent(20000);
            bool reused = ReferenceEquals(a, b) && pool.RetainedBytes == 0;
            Check("46p1. PixelBufferPool: pow2 rounding + reuse identity + retained accounting",
                rounded && parked && reused, $"len={a.Length} parked={parked} reused={ReferenceEquals(a, b)} retained={pool.RetainedBytes}");
        }

        // P2 — Rent ALWAYS succeeds past the cap (8×32KB live vs a 64KB cap); after returning all, RetainedBytes and
        // PeakRetainedBytes never exceed the cap (the surplus is dropped for the GC, not parked).
        {
            var pool = new FluentGpu.Media.PixelBufferPool(64 * 1024);
            var live = new byte[8][];
            bool allRented = true;
            for (int i = 0; i < 8; i++) { live[i] = pool.Rent(32 * 1024); if (live[i].Length != 32 * 1024) allRented = false; }
            for (int i = 0; i < 8; i++) pool.Return(live[i]);
            bool bounded = pool.RetainedBytes == 64 * 1024 && pool.PeakRetainedBytes == 64 * 1024
                           && pool.RetainedBytes <= pool.RetainedCapBytes && pool.PeakRetainedBytes <= pool.RetainedCapBytes;
            Check("46p2. PixelBufferPool: Rent never fails past the cap; retained/peak bounded by the cap after returns",
                allRented && bounded, $"rented={allRented} retained={pool.RetainedBytes} peak={pool.PeakRetainedBytes} cap={pool.RetainedCapBytes}");
        }

        // P3 — an oversize request (MaxBucketBytes+1) is served exact-size and is NEVER retained on Return.
        {
            var pool = new FluentGpu.Media.PixelBufferPool();
            byte[] big = pool.Rent(FluentGpu.Media.PixelBufferPool.MaxBucketBytes + 1);
            bool exact = big.Length == FluentGpu.Media.PixelBufferPool.MaxBucketBytes + 1;
            pool.Return(big);
            bool unretained = pool.RetainedBytes == 0;
            Check("46p3. PixelBufferPool: oversize is exact-size + unpooled (Return drops it)",
                exact && unretained, $"len={big.Length} retained={pool.RetainedBytes}");
        }

        // P4 — warm rent/return ×1000 allocates 0 managed bytes (bucket hit pops the parked array, Return pushes it back;
        // no fresh allocation once the bucket and its Stack backing are warm).
        {
            var pool = new FluentGpu.Media.PixelBufferPool();
            byte[] warm = pool.Rent(16 * 1024); pool.Return(warm);   // seed the bucket + grow the Stack backing once
            warm = pool.Rent(16 * 1024); pool.Return(warm);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { byte[] x = pool.Rent(16 * 1024); pool.Return(x); }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("46p4. PixelBufferPool: warm rent/return ×1000 is zero-alloc", delta == 0, $"delta={delta}B");
        }

        // P5 — Trim releases every parked array to the GC → RetainedBytes == 0.
        {
            var pool = new FluentGpu.Media.PixelBufferPool();
            pool.Return(pool.Rent(16 * 1024));
            pool.Return(pool.Rent(64 * 1024));
            pool.Trim();
            Check("46p5. PixelBufferPool: Trim() drops all parked arrays", pool.RetainedBytes == 0, $"retained={pool.RetainedBytes}");
        }

        // P6 — decode storm through a REAL DecodeScheduler on the shared pool: 48 varied-size decodes, 512KB cap; every
        // decode lands and the max observed retained stays ≤ cap (the FGGUARD double-return tripwire is live in Debug).
        {
            var storm = new FluentGpu.Media.PixelBufferPool(512 * 1024);
            long maxRetained = 0;
            int done = 0;
            var sizes = new (int w, int h)[] { (64, 64), (128, 64), (128, 128), (256, 128), (100, 100), (200, 150) };
            using (var sched = new DecodeScheduler(new TestCodec(), new TestFetcher(),
                       new DecodeOptions { MaxConcurrency = 4, PixelPool = storm }))
            {
                const int N = 48;
                for (int i = 1; i <= N; i++) { var (w, h) = sizes[i % sizes.Length]; sched.Begin(i, "s" + i, w, h); }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (done < N && sw.ElapsedMilliseconds < 8000)
                {
                    sched.Pump((id, ok, w, h, f, a) => { if (ok) done++; }, (id, px, w, h) => { });
                    long r = storm.RetainedBytes; if (r > maxRetained) maxRetained = r;
                    System.Threading.Thread.Sleep(2);
                }
            }
            Check("46p6. PixelBufferPool: 48-decode storm lands fully; retained never exceeds the cap",
                done == 48 && maxRetained <= storm.RetainedCapBytes && storm.PeakRetainedBytes <= storm.RetainedCapBytes,
                $"done={done}/48 maxRetained={maxRetained} peak={storm.PeakRetainedBytes} cap={storm.RetainedCapBytes}");
        }
    }

    static void BlurHashChecks(StringTable strings)
    {
        // (a) the decoder produces a valid, non-uniform preview from the canonical hash.
        Span<byte> px = stackalloc byte[8 * 8 * 4];
        bool decoded = BlurHash.Decode("LEHV6nWB2yk8pyo0adR*.7kCMdnj", 8, 8, px);
        bool varies = decoded && (px[0] != px[63 * 4] || px[1] != px[63 * 4 + 1] || px[2] != px[63 * 4 + 2]);

        // (b) pipeline: the 32×32 LQIP is uploaded at request (before the 64×64 full-res decode in the same frame).
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("blur", new Size2(320, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new BlurHashProbe());
        host.RunFrame();
        bool lqipFirst = device.Uploads.Count >= 2
            && device.Uploads[0].w == 32 && device.Uploads[0].h == 32   // blurhash preview, uploaded first
            && device.Uploads[1].w == 64 && device.Uploads[1].h == 64;  // full-res, replaces it

        Check("46e. BlurHash: decoder valid + LQIP uploaded instantly, replaced by full-res", varies && lqipFirst,
            $"decoded={decoded} varies={varies} uploads={device.Uploads.Count}");
    }

    static void ImageTransitionChecks()
    {
        var cache = new ImageCache(new FakeImageDecoder());
        var h = cache.Request("x", 16, 16);   // default reveal (220ms FluentDecelerate)
        cache.Pump();                          // decode completes → texture appears at t=0
        float cf0 = cache.CrossFadeOf(h);      // just appeared → ~0
        for (int i = 0; i < 20; i++) cache.Tick(16f);   // 320ms elapsed > 220ms
        float cf1 = cache.CrossFadeOf(h);      // settled → 1
        bool fades = cf0 < 0.2f && cf1 >= 0.999f;

        var hn = cache.Request("y", 16, 16, ImagePriority.Visible, null, ImageTransition.None);   // disabled
        cache.Pump();
        bool disabled = cache.CrossFadeOf(hn) >= 0.999f;   // instant, no fade

        Check("46f. ImageTransition: default fade eases 0→1; None disables (instant)", fades && disabled,
            $"cf0={cf0:0.00} cf1={cf1:0.00}");

        // 45e: ImageCache.WasReady — a re-decode of a key that has already been Ready once, landing fast, gets the SHORT
        // reveal (ImageCache.ShortRevealMs) EVEN AT REST — never an instant pop over the placeholder frame the +1-frame
        // contract always presents; a first-ever decode still gets its full authored fade (the synchronous test decoder
        // must not shorten a fresh reveal just because it lands fast too).
        var wr = new FakeImageDecoder();
        var wcache = new ImageCache(wr);
        var wHandle = wcache.Request("warm", 16, 16);
        wcache.Pump();                                    // first-ever decode — WasReady was false when this landed
        float firstCf = wcache.CrossFadeOf(wHandle);
        wcache.Tick(ImageCache.ShortRevealMs);
        bool firstFades = firstCf < 0.999f && wcache.CrossFadeOf(wHandle) < 0.999f;   // authored 220ms > ShortRevealMs
        wcache.Tick(1000f);

        wcache.ReRealizeAllResident();                    // forces a re-decode of the now-Ready entry (WasReady==true)
        wcache.Pump();                                    // lands immediately — well under InstantRevealWindowMs
        float warmCf = wcache.CrossFadeOf(wHandle);
        wcache.Tick(ImageCache.ShortRevealMs);
        float warmDone = wcache.CrossFadeOf(wHandle);
        bool warmShort = warmCf < 0.999f && warmDone >= 0.999f;

        Check("45e. ImageCache WasReady: a fast re-decode of a previously-Ready entry gets the short reveal (no pop); a first decode keeps its authored fade",
            firstFades && warmShort, $"firstCf={firstCf:0.00} warmCf={warmCf:0.00} warmDone={warmDone:0.00}");
    }

    static void ImageEvictChecks()
    {
        // Unpinned images over budget → LRU eviction, each freeing its GPU texture via the evict sink.
        var evicted = new List<int>();
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 50_000);
        cache.SetEvictSink(evicted.Add);
        for (int i = 0; i < 5; i++) cache.Request("img" + i, 64, 64);   // 5 × 16KB = 80KB > 50KB
        cache.Pump();                                                    // decode → ready → evict unpinned LRU
        bool freed = evicted.Count >= 1;

        // Pinned (on-screen) images are NEVER evicted, regardless of budget.
        var evicted2 = new List<int>();
        var pinned = new ImageCache(new FakeImageDecoder(), budgetBytes: 50_000);
        pinned.SetEvictSink(evicted2.Add);
        for (int i = 0; i < 5; i++) pinned.Pin(pinned.Request("p" + i, 64, 64));
        pinned.Pump();
        bool pinnedSafe = evicted2.Count == 0;

        Check("46g. Residency: evicts unpinned LRU + frees its GPU texture; never evicts pinned", freed && pinnedSafe,
            $"evicted={evicted.Count} pinnedEvicted={evicted2.Count}");
    }

    // E4: VRAM-pressure relief (ImageCache.EvictToVramPressure) respects a grace window on freshly-Ready entries and
    // never sheds below half this cache's own budget, unlike the ordinary byte-budget path (EvictToBudget, exercised
    // via Pump above in 46g / ImageCacheChecks) which has neither restriction.
    static void VramShedGraceAndFloorChecks()
    {
        // gate.img.vram-shed-grace: a just-landed Ready entry is exempt from pressure relief until ReadyGraceMs has
        // elapsed — otherwise the texture that just pushed VRAM over the arm ratio would be the very thing shed,
        // re-requested on the next scroll tick, and re-land into the same pressure (the loop adreno-hang-fixes.md M5
        // exists to break). A SMALL explicit budget matters here: the default 96 MB budget leaves this cache's five
        // small entries far below even the floor's Math.Min(_budgetBytes/2, UsedBytes/2), so `target` never drops
        // below `UsedBytes` and the shed loop would never start regardless of grace — proving nothing. Sized so the
        // cache sits comfortably under budget (no EvictToBudget interference) while the floor still sits below
        // UsedBytes, so `afterGrace` actually exercises a real shed.
        long graceUnit = ImageCache.CommittedBytesFor(64, 64);
        var graceCache = new ImageCache(new FakeImageDecoder(), budgetBytes: graceUnit * 10);
        for (int i = 0; i < 5; i++) graceCache.Request("grace" + i, 64, 64);
        graceCache.Pump();                                          // five Ready, unpinned entries; ReadyMs == clock (0)
        long freedImmediate = graceCache.EvictToVramPressure(100, 100);
        graceCache.Tick(ImageCache.ReadyGraceMs + 1f);
        long freedAfterGrace = graceCache.EvictToVramPressure(100, 100);
        Check("gate.img.vram-shed-grace a just-landed Ready entry is not shed by VRAM-pressure relief until ReadyGraceMs has elapsed, then it is",
            freedImmediate == 0 && freedAfterGrace > 0,
            $"immediate={freedImmediate} afterGrace={freedAfterGrace}");

        // gate.img.vram-shed-floor: pressure relief never sheds below _budgetBytes/2 even under extreme overage;
        // EvictToBudget (the ordinary cap, exercised via Pump) is unrestricted and still reaches the real cap.
        // Entry size is CommittedBytesFor(64,64) exactly — chosen budgets are whole multiples of it so the coarse
        // (whole-image) eviction loop lands EXACTLY on the cap/floor, not merely close to it.
        long unit = ImageCache.CommittedBytesFor(64, 64);
        long floorBudget = unit * 20;
        var floorCache = new ImageCache(new FakeImageDecoder(), budgetBytes: floorBudget);
        for (int i = 0; i < 30; i++) floorCache.Request("floor" + i, 64, 64);
        floorCache.Pump();                                          // EvictToBudget trims to exactly 20 images (== budget)
        long capUsed = floorCache.UsedBytes;
        bool reachesCap = capUsed == floorBudget;
        floorCache.Tick(ImageCache.ReadyGraceMs + 1f);              // clear the grace window for all 20 survivors
        long freedByPressure = floorCache.EvictToVramPressure(floorBudget, floorBudget * 100);   // extreme overage
        long floorUsed = floorCache.UsedBytes;
        bool neverBelowFloor = floorUsed == floorBudget / 2 && freedByPressure == floorBudget / 2;
        Check("gate.img.vram-shed-floor VRAM-pressure relief never sheds below budget/2 even under extreme overage; EvictToBudget is unrestricted and still reaches the real cap",
            reachesCap && neverBelowFloor,
            $"capUsed={capUsed} cap={floorBudget} floorUsed={floorUsed} floor={floorBudget / 2} freed={freedByPressure}");
    }

    // E7: a PINNED entry that goes GpuResourceExhausted cannot self-heal through Request/Pin (a realized on-screen
    // node calls neither again once mounted — see ImageCache.ReRealizeAllResident's remark). Pump's
    // RetryPinnedExhausted sweep is what retries it, paced by the same RestartBackoffMs every other restart uses.
    static void ExhaustedPinnedRetryChecks()
    {
        bool admit = false;
        var decoder = new CountingFakeDecoder();
        var cache = new ImageCache(decoder);
        cache.SetPixelAttemptSink((_, _, _, _) => admit ? ImageUploadResult.Accepted : ImageUploadResult.ResourceExhausted);
        var h = cache.Request("exhausted-pinned", 32, 32);
        cache.Pin(h);                                               // on screen the whole time — never unpinned/re-requested
        cache.Pump();                                                // rejected: Failed/GpuResourceExhausted, still pinned
        int beginAfterFirst = decoder.BeginCount;
        bool rejected = cache.StateOf(h) == ImageState.Failed && cache.FailureOf(h) == ImageFailureKind.GpuResourceExhausted;

        cache.Tick(1000f);
        cache.Pump();                                                // under RestartBackoffMs (2000ms) — no new Begin yet
        bool noRetryUnderBackoff = decoder.BeginCount == beginAfterFirst && cache.StateOf(h) == ImageState.Failed;

        cache.Tick(1001f);                                          // total 2001ms since the failure — backoff has elapsed
        admit = true;                                                // the backend can admit now
        cache.Pump();                                                // RetryPinnedExhausted scans and restarts the pinned entry
        bool retried = decoder.BeginCount > beginAfterFirst;
        bool pendingAfterRetry = cache.StateOf(h) == ImageState.Pending;
        cache.Pump();                                                // drains the queued re-decode → admitted this time → Ready
        bool ready = cache.StateOf(h) == ImageState.Ready;

        Check("gate.img.exhausted-pinned-retries-after-backoff a PINNED GpuResourceExhausted entry retries on its own once RestartBackoffMs elapses, not only on the next re-pin",
            rejected && noRetryUnderBackoff && retried && pendingAfterRetry && ready,
            $"begin1={beginAfterFirst} beginLater={decoder.BeginCount} state={cache.StateOf(h)} fail={cache.FailureOf(h)}");
    }

    // T10: a canceled leftover (None/Canceled — a dropped or canceled decode, not a URL failure) that a STILL-MOUNTED
    // node holds used to stay a placeholder forever: a static node never calls Request/Pin again, and Promote only runs
    // for virtualized rows turning visible. Pump's RestartPinnedLeftovers re-begins it at Visible once
    // CanceledLeftoverRetryMs has passed since its last restart; a genuine failure (NotFound) and an UNPINNED leftover
    // are left alone.
    static void PinnedCanceledLeftoverChecks()
    {
        var dec = new BackpressureCancelDecoder { NotFoundSource = "leftover-404" };
        var cache = new ImageCache(dec);

        // (a) canceled mid-decode by an unmount (UnpinImageNode → Cancel), remounted into a FULL Overscan lane.
        var a = cache.Request("leftover-cancel", 32, 32);
        cache.Pin(a);
        cache.Unpin(a);
        cache.Cancel(a);
        cache.Pump();                                                // completes Canceled with Refs==0 → Failed/Canceled
        dec.OffscreenFull = true;
        var a2 = cache.Request("leftover-cancel", 32, 32, ImagePriority.Overscan);   // restart refused (backpressure)
        cache.Pin(a2, ImagePriority.Overscan);                                       // re-Begin refused again, now pinned
        // (b) dropped at Request time (Overscan, queue full) and pinned — the other Begin-refusal entry point.
        var b = cache.Request("leftover-dropped", 32, 32, ImagePriority.Overscan);
        cache.Pin(b, ImagePriority.Overscan);
        // (c) a genuine permanent failure, pinned the whole time. (d) a leftover nobody pins.
        var c = cache.Request("leftover-404", 32, 32);
        cache.Pin(c);
        var d = cache.Request("leftover-unpinned", 32, 32, ImagePriority.Overscan);
        cache.Pump();                                                // (c) completes NotFound

        bool sameHandle = a2 == a;
        bool stuck = cache.StateOf(a) == ImageState.None && cache.FailureOf(a) == ImageFailureKind.Canceled && cache.RefsOf(a) == 1
            && cache.StateOf(b) == ImageState.None && cache.FailureOf(b) == ImageFailureKind.Canceled && cache.RefsOf(b) == 1
            && cache.StateOf(c) == ImageState.Failed && cache.FailureOf(c) == ImageFailureKind.NotFound
            && cache.StateOf(d) == ImageState.None && cache.RefsOf(d) == 0;
        int aBegins = dec.BeginsOf("leftover-cancel"), bBegins = dec.BeginsOf("leftover-dropped");
        int cBegins = dec.BeginsOf("leftover-404"), dBegins = dec.BeginsOf("leftover-unpinned");

        cache.Tick(ImageCache.CanceledLeftoverRetryMs * 0.2f);
        cache.Pump();                                                // inside the retry window — no new Begin yet
        bool noEarlyRetry = dec.BeginsOf("leftover-cancel") == aBegins && dec.BeginsOf("leftover-dropped") == bBegins
            && cache.StateOf(a) == ImageState.None;

        cache.Tick(ImageCache.CanceledLeftoverRetryMs);              // past the window; still no Request/Pin from the node
        cache.Pump();                                                // the sweep re-begins both pinned leftovers at Visible
        bool restarted = dec.BeginsOf("leftover-cancel") == aBegins + 1 && dec.BeginsOf("leftover-dropped") == bBegins + 1
            && cache.StateOf(a) == ImageState.Pending && cache.StateOf(b) == ImageState.Pending
            && dec.LastAccepted == ImagePriority.Visible;            // Visible: accepted although the Overscan lane is full
        cache.Pump();                                                // +1 frame: the re-decodes land
        bool ready = cache.StateOf(a) == ImageState.Ready && cache.StateOf(b) == ImageState.Ready;

        cache.Tick(ImageCache.CanceledLeftoverRetryMs * 10f);
        cache.Pump();
        cache.Pump();
        bool noThrash = dec.BeginsOf("leftover-cancel") == aBegins + 1 && dec.BeginsOf("leftover-dropped") == bBegins + 1;
        bool realFailureStays = dec.BeginsOf("leftover-404") == cBegins
            && cache.StateOf(c) == ImageState.Failed && cache.FailureOf(c) == ImageFailureKind.NotFound;
        bool unpinnedLeftAlone = dec.BeginsOf("leftover-unpinned") == dBegins && cache.StateOf(d) == ImageState.None;

        Check("gate.img.pinned-canceled-leftover-restarts a PINNED None/Canceled leftover (canceled mid-decode, or refused by Begin) re-begins at Visible after CanceledLeftoverRetryMs with no Request/Pin and reaches Ready; a pinned NotFound and an unpinned leftover are not restarted",
            sameHandle && stuck && noEarlyRetry && restarted && ready && noThrash && realFailureStays && unpinnedLeftAlone,
            $"same={sameHandle} stuck={stuck} noEarly={noEarlyRetry} restarted={restarted} ready={ready} noThrash={noThrash} " +
            $"404stays={realFailureStays} unpinned={unpinnedLeftAlone} a={cache.StateOf(a)}/{cache.FailureOf(a)} " +
            $"b={cache.StateOf(b)}/{cache.FailureOf(b)} c={cache.StateOf(c)}/{cache.FailureOf(c)} " +
            $"begins a={dec.BeginsOf("leftover-cancel")} b={dec.BeginsOf("leftover-dropped")} c={dec.BeginsOf("leftover-404")} d={dec.BeginsOf("leftover-unpinned")}");
    }

    // T10 host half: the owner's symptom was an IDLE page with a stuck grey photo — the sweep only runs inside Pump, so
    // the host must wake for it. A pending leftover shapes the idle wait to its due time (no wake bit, no polling), a due
    // one sets WakeReasons.ImageLeftoverDue and the woken frame's Pump restarts it with no input; nothing listed = no
    // effect. Headless fixed time only advances the image clock on a painted frame, so the gate stands in for the wall
    // time the blocked wait covers with ImageCache.Tick(wait) — in production ImageLeftoverDueInMs extrapolates the
    // image clock by the wall time since its last sample, which is exactly what makes the blocked wait end on time.
    static void PinnedCanceledLeftoverIdleWakeChecks(StringTable strings)
    {
        var dec = new BackpressureCancelDecoder();
        var cache = new ImageCache(dec);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("leftover-idle-wake", new Size2(200, 120), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
            new LeftoverWakeRoot(), cache);
        for (int i = 0; i < 8 || (i < 400 && host.HasActiveWork); i++) host.RunFrame();   // settle the mount
        bool settledIdle = !host.HasActiveWork && float.IsPositiveInfinity(cache.LeftoverRetryDueMs);
        int baselineWait = host.RecommendedWaitMs();

        // A pinned canceled leftover: dropped at the Overscan lane, and its pin's re-Begin dropped again.
        dec.OffscreenFull = true;
        var h = cache.Request("idle-leftover", 32, 32, ImagePriority.Overscan);
        cache.Pin(h, ImagePriority.Overscan);
        bool stuck = cache.StateOf(h) == ImageState.None && cache.FailureOf(h) == ImageFailureKind.Canceled && cache.RefsOf(h) == 1;

        int pendingWait = host.RecommendedWaitMs();
        bool pendingNoBit = (host.CurrentWakeReasons & WakeReasons.ImageLeftoverDue) == 0 && !host.HasActiveWork;
        float remainingMs = cache.LeftoverRetryDueMs - cache.ClockMs;   // headless: the image clock only moves on Paint
        // The wait reaches the due time (float due arithmetic may round the ceiling up one ms). If some other deadline
        // (cold maintenance) already woke the loop sooner, that is fine too — it then blocks again for the remainder.
        bool waitReachesDue = remainingMs > 0f && pendingWait >= 1 && pendingWait <= (int)MathF.Ceiling(remainingMs) + 1
            && ((baselineWait >= 1 && baselineWait <= remainingMs) || pendingWait >= (int)remainingMs);

        for (int i = 0; i < 32; i++) { _ = host.CurrentWakeReasons; _ = host.RecommendedWaitMs(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++) { _ = host.CurrentWakeReasons; _ = host.RecommendedWaitMs(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        cache.Tick(remainingMs + 1f);                                // the blocked idle wait elapses (see the remark above)
        bool dueBit = (host.CurrentWakeReasons & WakeReasons.ImageLeftoverDue) != 0 && host.HasActiveWork;
        int dueWait = host.RecommendedWaitMs();                      // due-now: a producing wait, never the -1 block
        int beginsBefore = dec.BeginsOf("idle-leftover");
        host.RunFrame();                                             // no input: the woken frame's Pump runs the sweep
        bool restarted = dec.BeginsOf("idle-leftover") == beginsBefore + 1 && cache.StateOf(h) != ImageState.None
            && dec.LastAccepted == ImagePriority.Visible;
        for (int i = 0; i < 4; i++) host.RunFrame();
        bool ready = cache.StateOf(h) == ImageState.Ready;
        bool quietAfter = float.IsPositiveInfinity(cache.LeftoverRetryDueMs)
            && (host.CurrentWakeReasons & WakeReasons.ImageLeftoverDue) == 0;

        Check("gate.img.pinned-canceled-leftover-idle-wake an idle host with a pinned canceled leftover shapes its wait to the retry due time (no wake bit, no alloc), wakes with WakeReasons.ImageLeftoverDue once due, and restarts it to Ready with no input; nothing listed leaves the wait alone",
            settledIdle && stuck && pendingNoBit && waitReachesDue && allocated == 0 && dueBit && dueWait >= 0
                && restarted && ready && quietAfter,
            $"settled={settledIdle} baselineWait={baselineWait} stuck={stuck} pendingWait={pendingWait} remaining={remainingMs:0.#}ms " +
            $"pendingNoBit={pendingNoBit} alloc={allocated} dueBit={dueBit} dueWait={dueWait} restarted={restarted} " +
            $"state={cache.StateOf(h)}/{cache.FailureOf(h)} begins={dec.BeginsOf("idle-leftover")} quiet={quietAfter} wake={host.CurrentWakeReasons}");
    }

    static void ImageLifecycleChecks(StringTable strings)
    {
        var retryDec = new TimeoutThenOkDecoder();
        var retryCache = new ImageCache(retryDec);
        var rh = retryCache.Request("retry-me", 64, 64, ImagePriority.Prefetch);
        retryCache.Pump();
        bool timedOut = retryCache.StateOf(rh) == ImageState.Failed
            && retryCache.FailureOf(rh) == ImageFailureKind.Timeout;
        retryCache.Tick(3000);
        retryCache.Pin(rh);
        bool restarted = retryCache.StateOf(rh) == ImageState.Pending;
        retryCache.Pump();
        bool retryReady = retryCache.StateOf(rh) == ImageState.Ready;
        Check("46i. image.retry.visible: transient Timeout retries after backoff when pinned",
            timedOut && restarted && retryReady,
            $"state={retryCache.StateOf(rh)} fail={retryCache.FailureOf(rh)}");

        var cancelDec = new CancelAwareDecoder();
        var cancelCache = new ImageCache(cancelDec);
        var ch = cancelCache.Request("cancel-me", 64, 64);
        cancelCache.Cancel(ch);
        cancelCache.Pump();
        bool canceled = cancelCache.StateOf(ch) == ImageState.Failed
            && cancelCache.FailureOf(ch) == ImageFailureKind.Canceled;
        cancelCache.Tick(3000);
        var ch2 = cancelCache.Request("cancel-me", 64, 64, ImagePriority.Visible);
        bool sameHandle = ch2 == ch && cancelCache.StateOf(ch2) == ImageState.Pending;
        cancelCache.Pump();
        bool cancelReady = cancelCache.StateOf(ch2) == ImageState.Ready;
        Check("46j. image.cancel.recycle: canceled decode restarts and completes",
            canceled && sameHandle && cancelReady,
            $"state={cancelCache.StateOf(ch2)} fail={cancelCache.FailureOf(ch2)}");

        var gated = new GatedDecoder();
        var gatedCache = new ImageCache(gated);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("imgdirty", new Size2(200, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ImageProbe(), gatedCache);
        host.RunFrame();
        bool pendingDraw = device.LastImages.Count == 1 && device.LastImages[0].Ready == 0;
        gated.Arm();
        host.RunFrame();
        bool readyDraw = device.LastImages.Count == 1 && device.LastImages[0].Ready == 1
            && gatedCache.StateOf(new ImageHandle(device.LastImages[0].ImageId)) == ImageState.Ready;
        Check("46k. image.status.marks-dirty: Pending→Ready repaints with ready=true",
            pendingDraw && readyDraw,
            $"frame1Ready={!pendingDraw} frame2Ready={device.LastImages.Count > 0 && device.LastImages[0].Ready == 1}");

        var entered = new System.Threading.ManualResetEventSlim(false);
        var release = new System.Threading.ManualResetEventSlim(false);
        int decodeCalls = 0;
        var blockingCodec = new TestCodec(() =>
        {
            if (System.Threading.Interlocked.Increment(ref decodeCalls) == 1)
            {
                entered.Set();
                release.Wait();
            }
        });
        bool queuedRestarted, queuedReady;
        using (var sched = new DecodeScheduler(blockingCodec, new TestFetcher(), new DecodeOptions { MaxConcurrency = 1 }))
        {
            var queuedCache = new ImageCache(sched);
            queuedCache.Request("blocker", 8, 8);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!entered.IsSet && sw.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(2);

            var victim = queuedCache.Request("queued-victim", 8, 8);
            queuedCache.Cancel(victim);
            var visibleVictim = queuedCache.Request("queued-victim", 8, 8, ImagePriority.Visible);
            queuedCache.Pin(visibleVictim);
            queuedCache.Pump();
            queuedRestarted = visibleVictim == victim
                && queuedCache.StateOf(victim) == ImageState.Pending
                && queuedCache.RefsOf(victim) == 1;

            release.Set();
            sw.Restart();
            while (queuedCache.StateOf(victim) != ImageState.Ready && sw.ElapsedMilliseconds < 5000)
            {
                queuedCache.Pump();
                System.Threading.Thread.Sleep(2);
            }
            queuedReady = queuedCache.StateOf(victim) == ImageState.Ready;
        }
        entered.Dispose(); release.Dispose();
        Check("46l. image.cancel.queued: queued scheduler cancel does not leave a visible handle forever-pending",
            queuedRestarted && queuedReady,
            $"restarted={queuedRestarted} ready={queuedReady}");

        var sharedDec = new GatedCancelAwareDecoder();
        var sharedCache = new ImageCache(sharedDec);
        using var sharedApp = new HeadlessPlatformApp();
        var sharedWindow = new HeadlessWindow(new WindowDesc("shared-img", new Size2(200, 120), 1f));
        sharedWindow.Show();
        var sharedDevice = new HeadlessGpuDevice();
        var sharedFonts = new HeadlessFontSystem(strings);
        var sharedProbe = new SharedImageSwapProbe();
        using var sharedHost = new AppHost(sharedApp, sharedWindow, sharedDevice, sharedFonts, strings, sharedProbe, sharedCache);
        sharedHost.RunFrame();
        int sharedId = sharedDevice.LastImages.Count >= 2 ? sharedDevice.LastImages[0].ImageId : 0;
        var sharedHandle = new ImageHandle(sharedId);
        bool sharedInitial = sharedId != 0
            && sharedDevice.LastImages.Count >= 2
            && sharedDevice.LastImages[1].ImageId == sharedId
            && sharedCache.RefsOf(sharedHandle) == 2
            && sharedCache.StateOf(sharedHandle) == ImageState.Pending;
        sharedProbe.SecondSource.Value = "album/other";
        sharedHost.RunFrame();
        bool sharedNotCanceled = sharedInitial
            && sharedDec.CancelCount(sharedId) == 0
            && sharedCache.RefsOf(sharedHandle) == 1
            && sharedCache.StateOf(sharedHandle) == ImageState.Pending;
        sharedDec.Arm();
        for (int i = 0; i < 4 && sharedCache.StateOf(sharedHandle) != ImageState.Ready; i++) sharedHost.RunFrame();
        bool sharedReady = sharedCache.StateOf(sharedHandle) == ImageState.Ready;
        Check("46m. image.shared-handle: rebinding one ImageEl does not cancel another visible owner",
            sharedNotCanceled && sharedReady,
            $"initial={sharedInitial} cancels={sharedDec.CancelCount(sharedId)} refs={sharedCache.RefsOf(sharedHandle)} state={sharedCache.StateOf(sharedHandle)}");
    }

    static void UseImageChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("useimg", new Size2(200, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new UseImageProbe());
        host.RunFrame();   // render → UseImage requests; pump completes the fake decode; status-change marks dirty
        host.RunFrame();   // re-render: UseImage now reports Ready → the component swaps the spinner for the image
        Check("46h. UseImage: hook surfaces load state to the component (spinner → ready)",
            UseImageProbe.LastState == ImageState.Ready, $"state={UseImageProbe.LastState}");

        // Each UseImage consumer observes its own handle epoch. Completing A must not re-render B (and vice versa):
        // this is the Home-card image-status fan-out regression in a deterministic two-leaf shape.
        {
            var selective = new SelectiveGatedDecoder();
            var selectiveCache = new ImageCache(selective);
            using var fanoutApp = new HeadlessPlatformApp();
            var fanoutWindow = new HeadlessWindow(new WindowDesc("useimg-fanout", new Size2(200, 100), 1f));
            fanoutWindow.Show();
            var fanoutDevice = new HeadlessGpuDevice();
            var fanoutFonts = new HeadlessFontSystem(strings);
            var fanoutProbe = new UseImageFanoutProbe();
            using var fanoutHost = new AppHost(fanoutApp, fanoutWindow, fanoutDevice, fanoutFonts, strings, fanoutProbe, selectiveCache);
            fanoutHost.RunFrame();
            int initialA = fanoutProbe.RendersA, initialB = fanoutProbe.RendersB;

            selective.Release("fanout/a");
            fanoutHost.RunFrame();   // Pump: A Pending -> Ready, schedules only A's render effect
            fanoutHost.RunFrame();   // flush A
            int afterA = fanoutProbe.RendersA, untouchedB = fanoutProbe.RendersB;
            bool onlyA = afterA == initialA + 1 && untouchedB == initialB
                && fanoutProbe.StateA == ImageState.Ready && fanoutProbe.StateB == ImageState.Pending;

            selective.Release("fanout/b");
            fanoutHost.RunFrame();
            fanoutHost.RunFrame();
            bool onlyB = fanoutProbe.RendersA == afterA && fanoutProbe.RendersB == untouchedB + 1
                && fanoutProbe.StateA == ImageState.Ready && fanoutProbe.StateB == ImageState.Ready;
            Check("46h2. UseImage: per-handle status epochs re-render only the image consumer whose handle completed",
                onlyA && onlyB,
                $"renders A={initialA}->{afterA}->{fanoutProbe.RendersA} B={initialB}->{untouchedB}->{fanoutProbe.RendersB} states={fanoutProbe.StateA}/{fanoutProbe.StateB}");
        }
    }

    // Hold-last-good (media-pipeline.md §hold-last-good, Reconciler.cs SwapImageId): when a mounted Image node's
    // cache key changes, the OLD Ready texture keeps drawing until the NEW key settles (Ready or Failed), then a
    // hard cut — no fade restart. Every case here uses SelectiveIdDecoder so the test controls EXACTLY when each id
    // completes, opening the window the reconciler's hold must cover.
    static void HoldLastGoodChecks(StringTable strings)
    {
        // 46n: unbound swap site (WriteColumns `case ImageEl`) — a decode-size change re-keys a Ready image.
        {
            var dec = new SelectiveIdDecoder();
            var cache = new ImageCache(dec);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("hold-n", new Size2(200, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new HoldLastGoodProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe, cache);

            host.RunFrame();                          // mount at Px=64 → Request/Begin id A (Pending)
            int idA = dec.LastBeginId;
            dec.Release(idA);
            host.RunFrame();                           // A completes → Ready
            bool aReady = device.LastImages.Count == 1 && device.LastImages[0].ImageId == idA
                && device.LastImages[0].Ready == 1 && cache.StateOf(new ImageHandle(idA)) == ImageState.Ready;

            probe.Px.Value = 128f;                     // decode-size change → a new SourceKey → a new Pending entry
            host.RunFrame();
            int idB = dec.LastBeginId;
            bool held = idB != 0 && idB != idA
                && device.LastImages.Count == 1 && device.LastImages[0].ImageId == idA   // still drawing the OLD id
                && device.LastImages[0].Ready == 1
                && cache.StateOf(new ImageHandle(idA)) == ImageState.Ready
                && cache.StateOf(new ImageHandle(idB)) == ImageState.Pending;

            dec.Release(idB);
            host.RunFrame();                           // B settles → MarkImageDirty commits the hard cut
            // The SAME picture: B draws at once (no fade restart) over the held A, which backs it for the swap window
            // with a see-through placeholder — a frame that cannot sample B yet (its copy still in flight) shows A.
            var cut = device.LastImages;
            bool committed = cut.Count == 2
                && cut[0].ImageId == idA && cut[0].Ready == 1 && cut[0].FadeEasing == ImageCache.SwapOutgoingEasing
                && cut[1].ImageId == idB && cut[1].Ready == 1 && cut[1].Placeholder.A == 0f
                && (float.IsNaN(cut[1].FadeStartMs) || cut[1].FadeDurationMs <= 0f)
                && cache.StateOf(new ImageHandle(idB)) == ImageState.Ready
                && cache.CrossFadeOf(new ImageHandle(idB)) >= 0.999f;   // hard cut — no fade restart
            for (int i = 0; i < 24; i++) host.RunFrame();   // ≥ window + release slack of fixed frames
            bool released = device.LastImages.Count == 1 && device.LastImages[0].ImageId == idB
                && cache.RefsOf(new ImageHandle(idA)) == 0;

            Check("46n. hold-last-good: a re-keyed Ready image holds its OLD texture while the NEW key decodes, then hard-cuts with no fade restart, the OLD texture backing it for the swap window",
                aReady && held && committed && released,
                $"idA={idA} idB={idB} aReady={aReady} held={held} committed={committed} released={released} draws={device.LastImages.Count}");
        }

        // 46n2a: the OLD pin survives a forced EvictToBudget while the hold is in progress; commit releases exactly
        // the OLD pin (the NEW one becomes the sole survivor).
        {
            var dec = new SelectiveIdDecoder();
            const long imgBytes = 64 * 64 * 4;
            var cache = new ImageCache(dec, budgetBytes: imgBytes + 100);   // room for ~1 image + slack, not 2
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("hold-n2a", new Size2(200, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new HoldLastGoodProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe, cache);

            host.RunFrame();
            int idA = dec.LastBeginId;
            dec.Release(idA);
            host.RunFrame();                           // A Ready, pinned (Refs=1)

            probe.Px.Value = 128f;
            host.RunFrame();                            // hold: B Begun+Pending, also pinned
            int idB = dec.LastBeginId;

            var extra = cache.Request("hold/unrelated", 64, 64);   // an unrelated, never-pinned entry
            dec.Release(extra.Id);
            cache.Pump();                                // extra completes Ready → UsedBytes now > budget → evicts
            bool survivedEvict = cache.StateOf(new ImageHandle(idA)) == ImageState.Ready
                && cache.RefsOf(new ImageHandle(idA)) >= 1;

            dec.Release(idB);
            host.RunFrame();                             // commit (A backs B for the swap window)
            for (int i = 0; i < 24; i++) host.RunFrame();
            bool commitReleasedOld = cache.RefsOf(new ImageHandle(idA)) == 0
                && cache.RefsOf(new ImageHandle(idB)) == 1
                && cache.StateOf(new ImageHandle(idB)) == ImageState.Ready;

            Check("46n2a. hold-last-good pin bookkeeping: the OLD pin survives a forced EvictToBudget mid-hold; the committed swap releases exactly the OLD pin",
                survivedEvict && commitReleasedOld,
                $"idA={idA} idB={idB} survivedEvict={survivedEvict} refsA={cache.RefsOf(new ImageHandle(idA))} refsB={cache.RefsOf(new ImageHandle(idB))}");
        }

        // 46n2b: re-keying back to the id currently on screen (while a different id is mid-hold) cancels the now-
        // orphaned pending decode (refs hit 0 → Cancel).
        {
            var dec = new SelectiveIdDecoder();
            var cache = new ImageCache(dec);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("hold-n2b", new Size2(200, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new HoldLastGoodProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe, cache);

            host.RunFrame();
            int idA = dec.LastBeginId;
            dec.Release(idA);
            host.RunFrame();

            probe.Px.Value = 128f;
            host.RunFrame();
            int idB = dec.LastBeginId;
            bool holding = cache.StateOf(new ImageHandle(idB)) == ImageState.Pending
                && cache.RefsOf(new ImageHandle(idB)) >= 1;

            probe.Px.Value = 64f;                        // back to the OLD (already-cached) key
            host.RunFrame();
            bool rekeyedBack = device.LastImages.Count == 1 && device.LastImages[0].ImageId == idA;
            bool pendingCanceled = dec.WasCanceled(idB) && cache.RefsOf(new ImageHandle(idB)) == 0;

            Check("46n2b. hold-last-good: re-keying back to the currently-drawn id cancels the orphaned pending decode",
                holding && rekeyedBack && pendingCanceled,
                $"idA={idA} idB={idB} rekeyedBack={rekeyedBack} canceled={dec.WasCanceled(idB)} refsB={cache.RefsOf(new ImageHandle(idB))}");
        }

        // 46n2c: an unmount mid-hold releases BOTH pins — the OLD (drawn) id and the pending NEW one.
        {
            var dec = new SelectiveIdDecoder();
            var cache = new ImageCache(dec);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("hold-n2c", new Size2(200, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new HoldLastGoodProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe, cache);

            host.RunFrame();
            int idA = dec.LastBeginId;
            dec.Release(idA);
            host.RunFrame();

            probe.Px.Value = 128f;
            host.RunFrame();
            int idB = dec.LastBeginId;
            bool bothPinnedBeforeUnmount = cache.RefsOf(new ImageHandle(idA)) >= 1
                && cache.RefsOf(new ImageHandle(idB)) >= 1;

            probe.Show.Value = false;                    // unmount mid-hold
            host.RunFrame();
            bool bothReleased = cache.RefsOf(new ImageHandle(idA)) == 0 && cache.RefsOf(new ImageHandle(idB)) == 0;

            Check("46n2c. hold-last-good: an unmount mid-hold releases BOTH pins (the OLD drawn id and the pending NEW one)",
                bothPinnedBeforeUnmount && bothReleased,
                $"idA={idA} idB={idB} refsA={cache.RefsOf(new ImageHandle(idA))} refsB={cache.RefsOf(new ImageHandle(idB))}");
        }

        // 46n3: the BOUND-source binding-effect swap site (Reconciler.BindNode's `ime.Source.IsBound` branch) takes
        // the same hold path as the unbound WriteColumns site above.
        {
            var dec = new SelectiveIdDecoder();
            var cache = new ImageCache(dec);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("hold-n3", new Size2(200, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new BoundHoldLastGoodProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe, cache);

            host.RunFrame();
            int idA = dec.LastBeginId;
            dec.Release(idA);
            host.RunFrame();
            bool aReady = device.LastImages.Count == 1 && device.LastImages[0].ImageId == idA
                && device.LastImages[0].Ready == 1;

            probe.Src.Value = "hold/bound-b";            // a different SOURCE (bound extent props are stable)
            host.RunFrame();
            int idB = dec.LastBeginId;
            bool held = idB != 0 && idB != idA
                && device.LastImages.Count == 1 && device.LastImages[0].ImageId == idA
                && device.LastImages[0].Ready == 1
                && cache.StateOf(new ImageHandle(idB)) == ImageState.Pending;

            // B is a DIFFERENT picture: the settle is a short dissolve FROM the held texture, not a one-frame cut — A is
            // drawn first, opaque for the swap window (SwapOutgoingEasing), and B fades in over it with a transparent
            // placeholder; the entry's own reveal stays settled (nothing fades in over a placeholder).
            dec.Release(idB);
            host.RunFrame();
            bool dissolving = device.LastImages.Count == 2
                && device.LastImages[0].ImageId == idA && device.LastImages[0].Ready == 1
                && device.LastImages[0].FadeEasing == ImageCache.SwapOutgoingEasing
                && device.LastImages[1].ImageId == idB && device.LastImages[1].Ready == 1
                && device.LastImages[1].Placeholder.A == 0f
                && Near(device.LastImages[1].FadeDurationMs, ImageCache.SwapCrossfadeMs, 0.01f)
                && cache.CrossFadeOf(new ImageHandle(idB)) >= 0.999f;
            for (int i = 0; i < 24; i++) host.RunFrame();   // ≥ window + release slack of fixed frames
            bool committed = device.LastImages.Count == 1 && device.LastImages[0].ImageId == idB
                && device.LastImages[0].Ready == 1
                && cache.RefsOf(new ImageHandle(idA)) == 0 && cache.RefsOf(new ImageHandle(idB)) == 1;

            Check("46n3. hold-last-good: the BOUND-source binding-effect swap site holds, then DISSOLVES from the held texture onto a different picture and releases it",
                aReady && held && dissolving && committed,
                $"idA={idA} idB={idB} aReady={aReady} held={held} dissolving={dissolving} committed={committed} draws={device.LastImages.Count}");
        }

        // A pending decode on a reused slot belongs to its NEW item, never the outgoing cover.
        {
            var dec = new SelectiveIdDecoder();
            var cache = new ImageCache(dec);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virtual-image-identity", new Size2(200, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new VirtualImageIdentityProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe, cache);
            host.RunFrame();
            int a = dec.LastBeginId;
            dec.Release(a);
            host.RunFrame();
            bool ready = device.LastImages.Count == 1 && device.LastImages[0].ImageId == a && device.LastImages[0].Ready == 1;
            probe.Src.Value = "virtual/b";
            host.RunFrame();
            int b = dec.LastBeginId;
            bool cleared = b != a && cache.RefsOf(new ImageHandle(a)) == 0
                && device.LastImages.Count == 1 && device.LastImages[0].ImageId == b && device.LastImages[0].Ready == 0;
            probe.Src.Value = "virtual/c";
            host.RunFrame();
            int c = dec.LastBeginId;
            dec.Release(b); // late completion of the superseded request cannot replace C
            host.RunFrame();
            bool staleIgnored = cache.RefsOf(new ImageHandle(b)) == 0
                && device.LastImages.Count == 1 && device.LastImages[0].ImageId == c;
            dec.Release(c);
            host.RunFrame();
            bool landed = device.LastImages.Count == 1 && device.LastImages[0].ImageId == c
                && device.LastImages[0].Ready == 1 && cache.RefsOf(new ImageHandle(c)) == 1;
            Check("46n3b. recycled images clear old artwork and ignore superseded decode completions",
                ready && cleared && staleIgnored && landed,
                $"ready={ready} cleared={cleared} staleIgnored={staleIgnored} landed={landed}");
        }

        // 46n4: the steady-frame alloc gate (gate.icon.alloc's idiom) extended through a hold + commit sequence — the
        // TRANSITION frames themselves legitimately allocate (a real component re-render for the Px change; a real
        // pixel upload — including this test's OWN SelectiveIdDecoder growing its scratch buffer to the new decode
        // size — for the commit), same as any other frame that does real reconcile/decode work. What must stay at
        // 0 is every STEADY frame once the sequence settles: no per-hold/per-commit residue (a leaked pin, a
        // dangling Dictionary entry) should show up as recurring hot-phase (6–13) allocation.
        {
            var dec = new SelectiveIdDecoder();
            var cache = new ImageCache(dec);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("hold-n4", new Size2(200, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new HoldLastGoodProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe, cache);

            host.RunFrame();
            int idA = dec.LastBeginId;
            dec.Release(idA);
            for (int i = 0; i < 3; i++) host.RunFrame();   // settle one-time/JIT allocs before measuring

            probe.Px.Value = 128f;
            host.RunFrame();                                // the hold transition frame (not measured — real reconcile work)
            int idB = dec.LastBeginId;
            dec.Release(idB);
            host.RunFrame();                                 // the commit transition frame (not measured — real upload work)

            long worst = 0;
            for (int i = 0; i < 8; i++) { var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }

            Check("46n4. hold-last-good: every steady frame after a hold + commit sequence keeps hot-phase (6–13) alloc at 0",
                worst == 0, $"worstSteady={worst}B idA={idA} idB={idB}");
        }
    }

    // ── W2-E3 (Reconciler.cs ImageRequestPriority / PromoteNewlyVisibleRows, ImageCache.Pin(priority) / Promote) ─────
    // Before this every reconciler image request was ImagePriority.Visible — a fling that realized 30 rows started 30
    // Visible decodes at once and DecodeScheduler's Overscan/Prefetch lanes + its backpressure drop arm were dead. Now a
    // row realized inside the viewport's visible band requests Visible, a row realized in the overscan halo (incl. the +1
    // guard row) requests Overscan, the pin no longer force-promotes, and a halo row that scrolls into view has its
    // still-Pending decode promoted to the Visible lane by the realize pass that follows. Both realize paths are covered:
    // the keyed RenderItem recycler (WriteColumns `case ImageEl`) and the persistent bound slots (the Source effect).
    static void OverscanPriorityChecks(StringTable strings)
    {
        // Pure cache: the scheduler's backpressure arm drops an off-screen request (Begin false ⇒ a None tombstone) and the
        // reconciler's pin at the SAME lane is dropped again, so the entry never becomes Pending. Promote — what the realize
        // pass calls when that row scrolls into view — must restart it at Visible rather than skip a non-Pending entry.
        var dropCache = new ImageCache(new DropPrefetchDecoder());
        var dropped = dropCache.Request("halo-cover", 32, 32, ImagePriority.Overscan);
        dropCache.Pin(dropped, ImagePriority.Overscan);
        bool stayedDropped = dropCache.StateOf(dropped) == ImageState.None && dropCache.PendingCount == 0 && dropCache.RefsOf(dropped) == 1;
        dropCache.Promote(dropped, ImagePriority.Visible);
        bool revived = dropCache.StateOf(dropped) == ImageState.Pending && dropCache.PendingCount == 1;
        dropCache.Pump();
        bool revivedReady = dropCache.StateOf(dropped) == ImageState.Ready;
        Check("gate.img.overscan-lane.promote-restarts-dropped: an Overscan request the scheduler dropped under backpressure stays a tombstone through its Overscan pin and is restarted at Visible by Promote when its row scrolls into view",
            stayedDropped && revived && revivedReady,
            $"afterPin={dropCache.StateOf(dropped)} pending={dropCache.PendingCount} refs={dropCache.RefsOf(dropped)} afterPromote={(revived ? "Pending" : "not-pending")} ready={revivedReady}");

        for (int pass = 0; pass < 2; pass++)
        {
            bool bound = pass == 1;
            string p = bound ? "bound/" : "static/";
            string path = bound ? "bound" : "keyed";
            var dec = new LaneRecordingDecoder();
            var cache = new ImageCache(dec);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("overscan-lane-" + path, new Size2(200, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new OverscanLaneProbe { Bound = bound }, cache);
            for (int i = 0; i < 6; i++) host.RunFrame();   // mount realizes the visible band; the at-rest catch-up / budget drip fills the halo
            var vp = ViewportWithItemCount(host.Scene, host.Scene.Root, 200);
            host.Scene.TryGetScroll(vp, out var sc0);

            // Rows 0..4 are unambiguously visible; row 5 (the engine's visible edge) and 6 (the +1 guard) are left
            // unasserted; everything realized from row 7 on is the overscan halo and must have been queued at Overscan.
            bool visibleLane = true, haloLane = true; int haloRows = 0;
            for (int i = 0; i <= 4; i++) visibleLane &= dec.BeginLaneOf(p + i) == ImagePriority.Visible;
            for (int i = 7; i < sc0.LastRealized; i++) { haloLane &= dec.BeginLaneOf(p + i) == ImagePriority.Overscan; haloRows++; }
            Check($"gate.img.overscan-lane.{path}: rows realized inside the visible band request Visible; rows realized in the overscan halo request Overscan (and the pin does not re-promote them)",
                !vp.IsNull && visibleLane && haloLane && haloRows > 0,
                $"realized=[{sc0.FirstRealized},{sc0.LastRealized}) visible0-4={visibleLane} halo7+={haloLane} haloRows={haloRows} begins={dec.BeginCount} lane7={dec.LaneOf(p + 7)}");

            // Scroll 6 rows (offset 240): rows 7..9 were realized in the halo (Overscan lane) and now sit inside the
            // visible band [6,12) — the realize pass that follows must move their still-Pending decodes to the Visible
            // lane; rows entering the NEW halo beyond the +1 guard (13..) must request Overscan.
            host.TryGetScrollHandle(vp)?.ScrollTo(40f * 6f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            for (int i = 0; i < 6; i++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc1);
            bool promoted = true;
            for (int i = 7; i <= 9; i++) promoted &= dec.LaneOf(p + i) == ImagePriority.Visible;
            bool newHaloLane = true; int newHaloRows = 0;
            for (int i = 13; i < sc1.LastRealized; i++) { newHaloLane &= dec.BeginLaneOf(p + i) == ImagePriority.Overscan; newHaloRows++; }
            Check($"gate.img.overscan-lane.{path}.promote: a halo row scrolling into the visible band has its pending decode promoted to the Visible lane, and the new halo still requests Overscan",
                promoted && newHaloLane && newHaloRows > 0 && sc1.FirstRealized <= 6 && sc1.LastRealized >= 12,
                $"realized=[{sc1.FirstRealized},{sc1.LastRealized}) lanes7-9={dec.LaneOf(p + 7)}/{dec.LaneOf(p + 8)}/{dec.LaneOf(p + 9)} newHaloRows={newHaloRows} begin13={dec.BeginLaneOf(p + 13)}");
        }
    }
}
