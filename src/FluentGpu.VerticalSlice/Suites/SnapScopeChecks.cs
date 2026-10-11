using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
// `FrameClock` is ambiguous between FluentGpu.Hooks (the app-facing static) and FluentGpu.Pal (the host's per-frame
// record) under these usings: the app-facing one is spelled through this alias.
using AppFrameClock = FluentGpu.Hooks.FrameClock;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// EP17 / E17a-b (<c>SnapScope</c>): while a transaction's snap scope is open, brush transitions, Layout FLIPs, Enter and Exit
/// all land at their END value in the publication that carries the commit (no animation row survives), the frame clock and the
/// host timers stand still without a catch-up burst on exit, image reveals complete instantly, and entering / leaving the
/// scope allocates nothing on the hot phases. The control gate runs the identical change OUTSIDE a scope and requires every
/// one of those to animate, so a regression that snaps unconditionally cannot satisfy the landing gate.
/// </summary>
static class SnapScopeChecks
{
    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var inside = Transact(strings, fonts, snap: true);
        var outside = Transact(strings, fonts, snap: false);

        Check("gate.snapscope.lands-at-end-values a brush with BrushTransitionMs=250, a Layout FLIP, an Enter and an Exit committed in ONE frame inside a SnapScope land at their end values in that publication: end colour with no BrushAnim, FLIP presented at its target with no tracks, Enter visible, Exit freed (no orphan), zero animation rows",
            inside.BrushAtEnd && !inside.BrushFading && inside.FlipAtTarget && inside.FlipDy == 0f && !inside.FlipTracks
            && inside.EnterOpacity == 1f && !inside.EnterTracks && inside.Orphans == 0 && inside.TotalTracks == 0,
            $"brushAtEnd={inside.BrushAtEnd} brushFading={inside.BrushFading} flipY={inside.FlipY:0.##} flipDy={inside.FlipDy:0.##} flipTracks={inside.FlipTracks} enterOpacity={inside.EnterOpacity:0.##} enterTracks={inside.EnterTracks} orphans={inside.Orphans} tracks={inside.TotalTracks}");

        Check("gate.snapscope.control-animates the identical commit OUTSIDE a SnapScope still animates (brush cross-fade armed, FLIP track presented at the old position, Enter fading in, Exit orphan fading)",
            outside.BrushFading && outside.FlipTracks && outside.FlipDy != 0f && outside.FlipAtTarget
            && outside.EnterOpacity < 1f && outside.EnterTracks && outside.Orphans > 0,
            $"brushFading={outside.BrushFading} flipTracks={outside.FlipTracks} flipDy={outside.FlipDy:0.##} enterOpacity={outside.EnterOpacity:0.##} enterTracks={outside.EnterTracks} orphans={outside.Orphans}");

        ClockChecks(strings, fonts);
        ImageRevealCheck();
        AllocChecks(strings, fonts);
    }

    // ══ the landing transaction ═════════════════════════════════════════════════════════════════════════════════════════

    static readonly ColorF Start = ColorF.FromRgba(200, 30, 30);
    static readonly ColorF End = ColorF.FromRgba(30, 30, 200);
    const float BrushW = 111f, FlipW = 112f, EnterW = 113f, ExitW = 114f;

    sealed class LandRoot : Component
    {
        public readonly Signal<bool> Alt = new(false);
        public readonly Signal<float> TopHeight = new(40f);
        public readonly Signal<bool> ShowEnter = new(false);
        public readonly Signal<bool> ShowExit = new(true);

        public override Element Render()
        {
            var kids = new List<Element>
            {
                new BoxEl { Width = BrushW, Height = 20f, Fill = Alt.Value ? End : Start, BrushTransitionMs = 250f },
                new BoxEl { Width = 100f, Height = TopHeight.Value, Fill = Start },
                new BoxEl { Width = FlipW, Height = 20f, Fill = End, Layout = LayoutTransition.Slide },
            };
            if (ShowEnter.Value) kids.Add(new BoxEl { Key = "enter", Width = EnterW, Height = 20f, Fill = End, Enter = new EnterExit(Opacity: 0f, Active: true) });
            if (ShowExit.Value) kids.Add(new BoxEl { Key = "exit", Width = ExitW, Height = 20f, Fill = End, Exit = new EnterExit(Opacity: 0f, Active: true) });
            return new BoxEl { Direction = 1, Width = 300f, Height = 300f, Children = kids.ToArray() };
        }
    }

    readonly record struct Landing(bool BrushAtEnd, bool BrushFading, bool FlipAtTarget, float FlipY, float FlipDy, bool FlipTracks,
                                   float EnterOpacity, bool EnterTracks, int Orphans, int TotalTracks);

    static NodeHandle FindWidth(SceneStore scene, NodeHandle n, float width)
    {
        if (n.IsNull) return n;
        if (scene.Layout(n).Width == width) return n;
        for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c))
        {
            var r = FindWidth(scene, c, width);
            if (!r.IsNull) return r;
        }
        return default;
    }

    static Landing Transact(StringTable strings, HeadlessFontSystem fonts, bool snap)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("snapscope-land", new Size2(320, 240), 1f));
        window.Show();
        var root = new LandRoot();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, root);
        for (int i = 0; i < 4; i++) host.Paint(0);   // mount + settle
        var scene = host.Scene;
        NodeHandle brush = FindWidth(scene, scene.Root, BrushW);
        NodeHandle flip = FindWidth(scene, scene.Root, FlipW);

        // ONE transaction: every change below is committed by the same Paint.
        var scope = snap ? SnapScope.Enter(host) : default;
        root.Alt.Value = true;
        root.TopHeight.Value = 100f;
        root.ShowEnter.Value = true;
        root.ShowExit.Value = false;
        host.Paint(0);
        scope.Dispose();

        NodeHandle enter = FindWidth(scene, scene.Root, EnterW);
        return new Landing(
            BrushAtEnd: scene.Paint(brush).Fill == End,
            BrushFading: scene.TryGetBrushAnim(brush, out _),
            FlipAtTarget: MathF.Abs(scene.Bounds(flip).Y - 120f) < 0.01f,   // below the 20-DIP brush row and the 100-DIP top
            FlipY: scene.Bounds(flip).Y,
            FlipDy: scene.Paint(flip).LocalTransform.Dy,
            FlipTracks: host.Animation.HasTracks(flip),
            EnterOpacity: enter.IsNull ? -1f : scene.Paint(enter).Opacity,
            EnterTracks: !enter.IsNull && host.Animation.HasTracks(enter),
            Orphans: scene.OrphanCount,
            TotalTracks: host.Animation.TrackCount);
    }

    // ══ timers and the frame clock ══════════════════════════════════════════════════════════════════════════════════════

    sealed class ClockRoot : Component
    {
        public int Ticks, Renders;
        public override Element Render()
        {
            _ = UseContext(AppFrameClock.Tick);   // a per-frame poller (the FrameTicker shape)
            Renders++;
            UseInterval(() => Ticks++, 48f);       // and a 48 ms interval
            return new BoxEl { Width = 10f, Height = 10f };
        }
    }

    static void ClockChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("snapscope-clock", new Size2(200, 120), 1f));
        window.Show();
        var root = new ClockRoot();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, root);
        for (int i = 0; i < 16; i++) host.Paint(0);
        int ticks0 = root.Ticks, renders0 = root.Renders;
        bool liveBefore = ticks0 > 0;

        int ticksIn, rendersIn;
        using (SnapScope.Enter(host))
        {
            for (int i = 0; i < 30; i++) host.Paint(0);   // ~480 ms = ten interval periods
            ticksIn = root.Ticks; rendersIn = root.Renders;
        }
        bool frozen = ticksIn == ticks0 && rendersIn == renders0;

        host.Paint(0);
        bool noImmediateBurst = root.Ticks - ticks0 <= 1;
        for (int i = 0; i < 30; i++) host.Paint(0);
        int resumedTicks = root.Ticks - ticks0;
        bool resumed = resumedTicks is >= 8 and <= 12 && root.Renders - renders0 >= 30;   // ~10 periods of live time, not ~20
        bool flagsRestored = !host.IsSnapping && !host.Animation.Snapping && !host.Images.SnapReveals && !host.TimersForTest.IsHeld;

        Check("gate.snapscope.clocks FrameClock.Tick and the host timer queue stand still inside a SnapScope (a per-frame poller and a 48 ms interval schedule nothing), and the exit resumes them from the exit time with no burst of the missed periods and every scope flag restored",
            liveBefore && frozen && noImmediateBurst && resumed && flagsRestored,
            $"live={liveBefore} frozen={frozen} (ticks {ticks0}->{ticksIn}, renders {renders0}->{rendersIn}) noBurst={noImmediateBurst} resumed={resumed} (+{resumedTicks} ticks, +{root.Renders - renders0} renders) flags={flagsRestored}");
    }

    // ══ image reveal ════════════════════════════════════════════════════════════════════════════════════════════════════

    static void ImageRevealCheck()
    {
        var cache = new ImageCache(new FakeImageDecoder());
        cache.PushSnap();
        var snapped = cache.Request("snapscope-a", 16, 16);
        cache.Pump();
        cache.PopSnap();
        bool instant = cache.CrossFadeOf(snapped) >= 0.999f && !cache.HasActiveCrossfades;

        var faded = cache.Request("snapscope-b", 16, 16);   // control: the same decode outside a scope
        cache.Pump();
        bool controlFades = cache.CrossFadeOf(faded) < 0.5f && cache.HasActiveCrossfades;

        Check("gate.snapscope.image-reveal an image decoded inside a SnapScope reveals instantly (no fade clock left running), while the same decode outside a scope still cross-fades",
            instant && controlFades, $"instant={instant} controlFades={controlFades}");
    }

    // ══ zero allocation ═════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class SteadyRoot : Component
    {
        public override Element Render() => new BoxEl { Width = 40f, Height = 40f, Fill = Start, BrushTransitionMs = 250f };
    }

    static void AllocChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("snapscope-alloc", new Size2(200, 120), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new SteadyRoot());
        for (int i = 0; i < 6; i++) host.Paint(0);

        for (int i = 0; i < 16; i++) { using var warm = SnapScope.Enter(host); }   // warm the JIT paths before measuring
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            using var outer = SnapScope.Enter(host);
            using var inner = SnapScope.Enter(host);   // nested: the depth counter path too
        }
        long enterExitBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // The hot phases (6-13) of frames inside the scope, and of the frames either side of the toggle.
        long worstHot = 0;
        for (int i = 0; i < 4; i++) host.Paint(0);
        using (SnapScope.Enter(host))
        {
            for (int i = 0; i < 6; i++) { long h = host.Paint(0).HotPhaseAllocBytes; if (i >= 2 && h > worstHot) worstHot = h; }
        }
        for (int i = 0; i < 6; i++) { long h = host.Paint(0).HotPhaseAllocBytes; if (i >= 1 && h > worstHot) worstHot = h; }

        Check("gate.snapscope.alloc-zero entering and leaving a SnapScope (1000 nested pairs) allocates 0 bytes, and the hot phases (6-13) of the frames inside the scope and either side of the toggle allocate 0 bytes",
            enterExitBytes == 0 && worstHot == 0, $"enterExitBytes={enterExitBytes} worstHotPhaseBytes={worstHot}");
    }
}
