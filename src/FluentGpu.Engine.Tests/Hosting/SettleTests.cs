using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;
// `FrameClock` is ambiguous between FluentGpu.Hooks (the app-facing static) and FluentGpu.Pal (the host's per-frame record).
using AppFrameClock = FluentGpu.Hooks.FrameClock;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E17c — keyed measurement (<see cref="MeasureMemory"/>, <c>UseMeasuredWidth(key)</c> / <c>UseMeasuredBounds(key)</c>) and the
/// settle loop (<see cref="AppHost.Settle"/>). A keyed region remounts at its remembered size, so a settle after the remount is
/// quiet in its first pass; an unkeyed one needs one corrective pass; a region that never stabilises hits the pass cap and is
/// named in the result; pinned images hold the settle up to their soft wait; the fonts gate and the time cap; a cut ends it.
/// Serial: it builds headless hosts and uses the process-wide measure memory.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class SettleTests
{
    // ── components ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads its own measured width (keyed or not) and records what each render saw.</summary>
    private sealed class Measured : Component
    {
        public readonly string? Key;
        public readonly List<float> Seen;
        public Measured(string? key, List<float> seen) { Key = key; Seen = seen; }

        public override Element Render()
        {
            float w = (Key is null ? UseMeasuredWidth() : UseMeasuredWidth(Key)).Value;
            Seen.Add(w);
            // The child's structure depends on the width (two columns when wide), as a responsive region's does.
            return new BoxEl { Height = 40f, Direction = (byte)(w > 200f ? 0 : 1), Children = [new BoxEl { Width = 10f, Height = 10f }] };
        }
    }

    /// <summary>Reads its measured bounds through the KEYED overload (a string argument must bind to it, not the caller-info slot).</summary>
    private sealed class MeasuredBounds : Component
    {
        public readonly List<RectF> Seen;
        public MeasuredBounds(List<RectF> seen) => Seen = seen;

        public override Element Render()
        {
            Seen.Add(UseMeasuredBounds("bounds-role").Value);
            return new BoxEl { Height = 30f };
        }
    }

    /// <summary>A feedback loop: its width is derived from its own measured width, so every pass changes the measurement.</summary>
    private sealed class Runaway : Component
    {
        public override Element Render()
        {
            float w = UseMeasuredWidth("runaway").Value;
            return new BoxEl { Width = w > 250f ? 100f : w + 30f, Height = 10f };
        }
    }

    /// <summary>The root: a column of the given child (mounted while <see cref="Show"/>), under an optional measure scope.</summary>
    private sealed class Root : Component
    {
        public readonly Signal<bool> Show = new(true);
        public readonly Signal<float> Width = new(300f);
        public Func<Element> Child = static () => new BoxEl();
        public string Scope = "";
        public bool Image;

        public override Element Render()
        {
            var kids = new List<Element>();
            if (Show.Value) kids.Add(Child());
            if (Image) kids.Add(new ImageEl { Source = "settle/cover", Width = 40f, Height = 40f });
            Element body = new BoxEl { Direction = 1, Width = Width.Value, Height = 200f, Children = kids.ToArray() };
            return Scope.Length == 0 ? body : Ctx.Provide(MeasureMemory.Scope, Scope, body);
        }
    }

    /// <summary>A decoder whose completions land only once <see cref="Open"/> is set.</summary>
    private sealed class GatedDecoder : IImageDecoder
    {
        private readonly FakeImageDecoder _inner = new();
        public bool Open;
        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
            => _inner.Begin(id, source, targetW, targetH, priority);
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            if (Open) _inner.Pump(onComplete, onPixels);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly HeadlessWindow Window;
        public readonly Root Root;
        public readonly AppHost Host;

        public Rig(Root root, ImageCache? images = null)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            MeasureMemory.Clear();
            Root = root;
            Window = new HeadlessWindow(new WindowDesc("settle", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(Strings), Strings, root, images: images);
            Paint(3);
        }

        public void Paint(int n = 1) { for (int i = 0; i < n; i++) Host.Paint(0); }

        /// <summary>Paint until <paramref name="t"/> completes (at most <paramref name="max"/> frames); returns the frames painted.</summary>
        public int PaintUntil(Task t, int max = 64)
        {
            int n = 0;
            while (!t.IsCompleted && n < max) { Host.Paint(0); n++; }
            return n;
        }

        public void Dispose()
        {
            Host.Dispose();
            App.Dispose();
            MeasureMemory.Clear();
        }
    }

    /// <summary>The result of a settle the test has already driven to completion (no blocking wait).</summary>
    private static SettleResult Done(Task<SettleResult> t)
    {
        Assert.True(t.IsCompletedSuccessfully, "the settle completed");
        return t.GetAwaiter().GetResult();
    }

    // ── keyed measurement ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AKeyedRemount_RendersItsFirstFrameAtTheRememberedWidth_AndTheMeasurementChangesNothing()
    {
        var seen = new List<float>();
        var root = new Root { Child = () => Embed.Comp(() => new Measured("region", seen)) };
        using var rig = new Rig(root);
        Assert.Equal(0f, seen[0]);                                    // nothing remembered at the first mount
        Assert.Equal(300f, seen[^1]);                                 // the measurement arrived one frame later
        Assert.True(MeasureMemory.TryGetSize("region", out var remembered));
        Assert.Equal(300f, remembered.Width);

        root.Show.Value = false;
        rig.Paint(2);
        seen.Clear();
        root.Show.Value = true;
        rig.Paint(4);
        Assert.Equal([300f], seen);                                   // one render, at the remembered width; no corrective frame
    }

    [Fact]
    public void AnUnkeyedRemount_StartsAtZero_AndCorrectsOneFrameLater()
    {
        var seen = new List<float>();
        var root = new Root { Child = () => Embed.Comp(() => new Measured(null, seen)) };
        using var rig = new Rig(root);
        root.Show.Value = false;
        rig.Paint(2);
        seen.Clear();
        root.Show.Value = true;
        rig.Paint(4);
        Assert.Equal([0f, 300f], seen);
        Assert.Equal(0, MeasureMemory.Count);                         // an unkeyed hook remembers nothing
    }

    [Fact]
    public void TheScopeSeparatesKeys_AndTheBoundsOverloadTakesAStringAsItsKey()
    {
        var seenA = new List<float>();
        var bounds = new List<RectF>();
        var root = new Root
        {
            Scope = "tab0/artist:1/",
            Child = () => new BoxEl { Direction = 1, Children = [Embed.Comp(() => new Measured("band", seenA)), Embed.Comp(() => new MeasuredBounds(bounds))] },
        };
        using var rig = new Rig(root);
        Assert.True(MeasureMemory.TryGetSize("tab0/artist:1/band", out _));
        Assert.False(MeasureMemory.TryGetSize("band", out _));
        Assert.True(MeasureMemory.TryGet("tab0/artist:1/bounds-role", out var b));
        Assert.Equal(300f, b.W);
        Assert.Equal(30f, b.H);

        root.Show.Value = false;
        rig.Paint(2);
        bounds.Clear();
        root.Show.Value = true;
        rig.Paint(3);
        Assert.Single(bounds);                                        // seeded with the remembered rect: no corrective render
        Assert.Equal(b, bounds[0]);
    }

    [Fact]
    public void TheMemoryIsBounded_TheLeastRecentlyCapturedKeyGoesFirst()
    {
        MeasureMemory.Clear();
        try
        {
            for (int i = 0; i < MeasureMemory.Capacity; i++) MeasureMemory.Capture("k" + i, new Size2(i, 1));
            MeasureMemory.Capture("k0", new Size2(0, 1));             // touched: now the most recent
            MeasureMemory.Capture("new", new Size2(5, 5));
            Assert.Equal(MeasureMemory.Capacity, MeasureMemory.Count);
            Assert.True(MeasureMemory.TryGetSize("k0", out _));
            Assert.False(MeasureMemory.TryGetSize("k1", out _));      // the oldest untouched key was dropped
            Assert.True(MeasureMemory.TryGetSize("new", out var s));
            Assert.Equal(new Size2(5, 5), s);
        }
        finally { MeasureMemory.Clear(); }
    }

    // ── the settle loop ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Settle_NeedsASnapScope_AndRunsOneAtATime()
    {
        using var rig = new Rig(new Root());
        Assert.Throws<InvalidOperationException>(() => { _ = rig.Host.Settle(); });
        using (SnapScope.Enter(rig.Host))
        {
            var t = rig.Host.Settle();
            Assert.True(rig.Host.IsSettling);
            Assert.Throws<InvalidOperationException>(() => { _ = rig.Host.Settle(); });
            rig.PaintUntil(t);
            Assert.True(t.IsCompletedSuccessfully);
            Assert.False(rig.Host.IsSettling);
        }
    }

    [Fact]
    public void AKeyedRemountUnderAFreeze_SettlesInItsFirstPass()
    {
        var seen = new List<float>();
        var root = new Root { Child = () => Embed.Comp(() => new Measured("page/region", seen)) };
        using var rig = new Rig(root);
        root.Show.Value = false;
        rig.Paint(2);

        var token = rig.Host.Freeze();
        rig.Paint();                                                   // the capture frame
        Assert.True(token.IsCaptured);
        root.Show.Value = true;                                        // the apply: the region remounts behind the frozen frame
        var settle = rig.Host.Settle();
        int frames = rig.PaintUntil(settle);
        var r = Done(settle);
        Assert.False(r.TimedOut);
        Assert.False(r.Cut);
        Assert.Equal(1, r.Passes);
        Assert.Equal(0, r.WorkPasses);
        Assert.Equal(1, frames);
        Assert.Empty(r.UnsettledKeys);
        Assert.Equal(FreezePhase.Frozen, rig.Host.FreezeState);        // the settle never thaws: the transaction does
        rig.Host.Thaw(token, DissolveSpec.Cut);
        rig.Paint(2);
        Assert.Equal(FreezePhase.None, rig.Host.FreezeState);
    }

    [Fact]
    public void AnUnkeyedRemount_SettlesInTwoPasses_OneOfThemWork()
    {
        var seen = new List<float>();
        var root = new Root { Child = () => Embed.Comp(() => new Measured(null, seen)) };
        using var rig = new Rig(root);
        root.Show.Value = false;
        rig.Paint(2);

        using var snap = SnapScope.Enter(rig.Host);
        root.Show.Value = true;
        var settle = rig.Host.Settle();
        rig.PaintUntil(settle);
        var r = Done(settle);
        Assert.False(r.TimedOut);
        Assert.Equal(2, r.Passes);
        Assert.Equal(1, r.WorkPasses);
        Assert.Equal(300f, seen[^1]);                                  // the settled frame is at the measured width
    }

    [Fact]
    public void ARegionThatNeverStabilises_HitsThePassCap_IsNamed_AndLogsATimeout()
    {
        var lines = new List<string>();
        var previous = Diag.Sink;
        Diag.Sink = l => { lock (lines) lines.Add(l); };
        try
        {
            var root = new Root { Child = static () => Embed.Comp(static () => new Runaway()) };
            using var rig = new Rig(root);
            int fired = 0;
            rig.Host.Settled += _ => fired++;
            using var snap = SnapScope.Enter(rig.Host);
            var settle = rig.Host.Settle(new SettleSpec(MaxPasses: 4, MaxMs: 10_000));
            rig.PaintUntil(settle);
            var r = Done(settle);
            Assert.True(r.TimedOut);
            Assert.Equal(4, r.WorkPasses);
            Assert.Contains("runaway", r.UnsettledKeys);
            Assert.Equal(1, fired);
            Assert.Contains(lines, l => l.StartsWith("[settle-timeout]", StringComparison.Ordinal) && l.Contains("runaway", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("[settle] ", StringComparison.Ordinal) && l.Contains("timedOut=1", StringComparison.Ordinal));
        }
        finally { Diag.Sink = previous; }
    }

    [Fact]
    public void APinnedImageStillDecoding_HoldsTheSettle_UntilItLands()
    {
        var decoder = new GatedDecoder();
        var root = new Root { Image = true };
        using var rig = new Rig(root, new ImageCache(decoder));
        using var snap = SnapScope.Enter(rig.Host);
        var settle = rig.Host.Settle();
        rig.Paint(3);                                                  // quiet, but the cover has no texture yet (48 ms < 150)
        Assert.False(settle.IsCompleted);
        decoder.Open = true;
        rig.PaintUntil(settle);
        var r = Done(settle);
        Assert.False(r.TimedOut);
        Assert.Equal(0, r.ImagesUndecoded);
        Assert.Empty(r.UnsettledKeys);
    }

    [Fact]
    public void APinnedImageThatNeverLands_ReleasesTheSettleAtItsSoftWait_WithoutATimeout()
    {
        var decoder = new GatedDecoder();
        var root = new Root { Image = true };
        using var rig = new Rig(root, new ImageCache(decoder));
        using var snap = SnapScope.Enter(rig.Host);
        var settle = rig.Host.Settle();
        rig.PaintUntil(settle);
        var r = Done(settle);
        Assert.False(r.TimedOut);
        Assert.Equal(1, r.ImagesUndecoded);
        Assert.InRange(r.ElapsedMs, 150.0, 250.0);
        Assert.Contains(r.UnsettledKeys, k => k.StartsWith("image:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFontsGateTheSettle_AndTheTimeCapStartsWhenTheyAreReady()
    {
        using var rig = new Rig(new Root());
        bool ready = false;
        using var snap = SnapScope.Enter(rig.Host);
        var settle = rig.Host.Settle(new SettleSpec { FontsReady = () => ready });
        rig.Paint(5);
        Assert.False(settle.IsCompleted);                              // quiet, but the fonts are not loaded
        ready = true;
        rig.PaintUntil(settle);
        var r = Done(settle);
        Assert.False(r.TimedOut);
        Assert.True(r.FontWaitMs > 0);
    }

    [Fact]
    public void FontsThatNeverLoad_EndTheSettleAsATimeoutNamingFonts()
    {
        using var rig = new Rig(new Root());
        using var snap = SnapScope.Enter(rig.Host);
        var settle = rig.Host.Settle(new SettleSpec { FontsReady = static () => false, FontWaitMs = 100 });
        rig.PaintUntil(settle);
        var r = Done(settle);
        Assert.True(r.TimedOut);
        Assert.Contains("fonts", r.UnsettledKeys);
    }

    [Fact]
    public void TickersAndIntervals_CannotKeepTheSettleBusy()
    {
        var root = new Root { Child = static () => Embed.Comp(static () => new Ticking()) };
        using var rig = new Rig(root);
        using var snap = SnapScope.Enter(rig.Host);
        var settle = rig.Host.Settle();
        rig.PaintUntil(settle);
        Assert.False(Done(settle).TimedOut);
        Assert.Equal(1, Done(settle).Passes);
    }

    private sealed class Ticking : Component
    {
        private readonly Signal<int> _n = new(0);
        public override Element Render()
        {
            long tick = UseContext(AppFrameClock.Tick);                   // a per-frame poller
            UseInterval(() => _n.Value++, 16f);                        // and an interval
            return new BoxEl { Width = 20f + (tick % 3) + _n.Value % 2, Height = 10f };
        }
    }

    [Fact]
    public void AResizeCutsTheFreeze_AndTheSettleEndsAsACut()
    {
        var root = new Root { Child = static () => Embed.Comp(static () => new Runaway()) };
        using var rig = new Rig(root);
        var token = rig.Host.Freeze();
        rig.Paint();
        var settle = rig.Host.Settle(new SettleSpec(MaxPasses: 50, MaxMs: 10_000));
        rig.Paint();
        rig.Window.ClientSizePx = new Size2(400, 240);                // a resize cuts the freeze and its snap scope
        rig.PaintUntil(settle);
        Assert.True(Done(settle).Cut);
        Assert.False(Done(settle).TimedOut);
        Assert.True(token.WasCut);
    }

    [Fact]
    public void DisposingTheHost_EndsARunningSettleAsACut()
    {
        var rig = new Rig(new Root());
        var snap = SnapScope.Enter(rig.Host);
        var settle = rig.Host.Settle();
        snap.Dispose();
        rig.Dispose();
        Assert.True(settle.IsCompleted);
        Assert.True(Done(settle).Cut);
    }
}
