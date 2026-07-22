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

static class ImageSuite
{
    public static void Run(StringTable strings)
    {
        IconChecks(strings);
        ImageCacheChecks();
        ImageElChecks(strings);
        ImageFitChecks(strings);
        DecodeSchedulerChecks();
        PixelBufferPoolChecks();
        BlurHashChecks(strings);
        ImageTransitionChecks();
        ImageEvictChecks();
        ImageLifecycleChecks(strings);
        UseImageChecks(strings);
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
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 1000);   // tiny budget to force eviction
        var a = cache.Request("a", 10, 10);                                      // 10×10×4 = 400 bytes when ready
        bool pending = cache.StateOf(a) == ImageState.Pending;
        cache.Pump();
        bool ready = cache.StateOf(a) == ImageState.Ready && cache.SizeOf(a) == (10, 10);
        bool dedup = cache.Request("a", 10, 10).Id == a.Id;

        cache.Pin(a);                                                            // a is "on screen"
        var b = cache.Request("b", 10, 10);
        var c = cache.Request("c", 10, 10);
        cache.Pump();                                                           // a+b+c = 1200 > 1000 → evict LRU unpinned (b)
        bool keptPinned = cache.StateOf(a) == ImageState.Ready;                  // pinned survived eviction
        bool withinBudget = cache.UsedBytes <= 1000;
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
        bool retryReady = admission.StateOf(retried) == ImageState.Ready && admission.UsedBytes == 32 * 32 * 4;
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
            && baked.DerivedUsedBytes == 256 * 128 * 4;
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
        int fallbackId = bakedDevice.LastImages.Count == 1 ? bakedDevice.LastImages[0].ImageId : 0;
        bakedHost.RunFrame();
        bool deferredUntilSettled = bakedDevice.LastImages.Count == 1
            && bakedDevice.LastImages[0].ImageId == fallbackId;
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
        Check("46c. Baked ImageEl: source fallback then persistent derived handle; overlay+mask stay in one DrawImage with zero layers",
            deferredUntilSettled && oneQuad && selectedDerived && styling,
            $"deferred={deferredUntilSettled} settleFrames={bakeSettleFrames} fallback={fallbackId} derived={bakedCmd.ImageId} draws={bakedDevice.LastImages.Count} layers={bakedDevice.LastLayers.Count} mask={bakedCmd.MaskEdges}");
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
    }
}
