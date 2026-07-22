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


    enum DeviceLossProbeFailure { Submit, Present, PresentNonDevice }

    sealed class DeviceLossProbeDevice : IGpuDevice
    {
        readonly DeviceLossProbeFailure _failure;
        bool _armed = true;
        bool _lost;

        public DeviceLossProbeDevice(DeviceLossProbeFailure failure) => _failure = failure;
        public HeadlessGpuDevice Inner { get; } = new();
        public string BackendName => "DeviceLossProbe";
        public bool SupportsSecondarySwapchains => true;
        public int RecoverCount { get; private set; }
        public int DumpCount { get; private set; }
        public int NoteCount { get; private set; }

        public ISwapchain CreateSwapchain(in SwapchainDesc desc) => new DeviceLossProbeSwapchain(this, Inner.CreateSwapchain(desc));

        public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)
        {
            if (_failure == DeviceLossProbeFailure.Submit) ThrowOnce();
            Inner.SubmitDrawList(drawList, sortKeys, in ctx);
        }

        public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx, ISwapchain target)
            => SubmitDrawList(drawList, sortKeys, in ctx);

        public void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h) => Inner.UploadImage(imageId, pbgra8, w, h);
        public void EvictImage(int imageId) => Inner.EvictImage(imageId);
        public bool NoteIfDeviceLost() { NoteCount++; return _lost; }
        public int PollDeviceLost() => _lost ? unchecked((int)0x887A0006u) : 0;
        public void RecoverDevice() { RecoverCount++; _lost = false; }
        public void DumpDeviceLostDiagnostics(Action<string> write) { DumpCount++; write("[probe] device-lost diagnostics"); }
        public void Dispose() { }

        internal void Present(ISwapchain inner)
        {
            if (_failure is DeviceLossProbeFailure.Present or DeviceLossProbeFailure.PresentNonDevice) ThrowOnce();
            inner.Present();
        }

        void ThrowOnce()
        {
            if (!_armed) return;
            _armed = false;
            if (_failure != DeviceLossProbeFailure.PresentNonDevice) _lost = true;
            throw new InvalidOperationException(_lost ? "synthetic device lost" : "synthetic render bug");
        }
    }

    sealed class DeviceLossProbeSwapchain(DeviceLossProbeDevice owner, ISwapchain inner) : ISwapchain
    {
        public Size2 SizePx => inner.SizePx;
        public void Resize(Size2 px) => inner.Resize(px);
        public void Present() => owner.Present(inner);
        public void ConfigurePopupChrome(in PopupChromeMetrics m) => inner.ConfigurePopupChrome(in m);
        public void AnimatePopupOpen() => inner.AnimatePopupOpen();
        public void AnimatePopupClose() => inner.AnimatePopupClose();
        public bool PopupAnimating => inner.PopupAnimating;
        public void Dispose() => inner.Dispose();
    }

static class LayoutShellSuite
{
    public static void Run(StringTable strings)
    {
        SidebarCollapseFlipChecks(strings);
        DeviceLostRecoveryChecks(strings);
        FlexChecks(strings);
        ShellDockChecks(strings);
        ShellResizeChecks(strings);
        DetailResizeFlickerChecks(strings);
        ButterSmoothResizeChecks(strings);
        ResponsiveResizeChecks(strings);
        CollapsedHeroRebakeChecks(strings);
        CollapsedHeroFocusChecks(strings);
        SidebarResizeSimChecks(strings);
        LayoutBoundaryMeasuredChecks(strings);
        ShellSidebarScrollChecks(strings);
        WrapChecks(strings);
        WrapGrowChecks(strings);
        ConstrainedWrapChecks(strings);
        GridChecks(strings);
        GridOverflowChecks(strings);
        GridStretchChecks(strings);
        ColumnAlignChecks(strings);
        AutoGridChecks(strings);
        VirtualGridChecks(strings);
        ZStackRepeaterChecks(strings);
        G3TokenChecks();
        G3AspectChecks(strings);
        G3PrimitiveChecks(strings);
    }

    static AppHost DeviceLostHost(StringTable strings, DeviceLossProbeDevice device, out HeadlessPlatformApp app, out HeadlessWindow window)
    {
        app = new HeadlessPlatformApp();
        window = new HeadlessWindow(new WindowDesc("device loss", new Size2(320, 220), 1f));
        window.Show();
        return new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new Counter());
    }

    static void DeviceLostRecoveryChecks(StringTable strings)
    {
        var oldSink = Diag.Sink;
        var lines = new List<string>();
        Diag.Sink = lines.Add;
        try
        {
            var presentDevice = new DeviceLossProbeDevice(DeviceLossProbeFailure.Present);
            var presentHost = DeviceLostHost(strings, presentDevice, out var presentApp, out var presentWindow);
            using (presentApp)
            using (presentHost)
            {
                var lost = presentHost.RunFrame();
                var recovered = presentHost.RunFrame();
                Check("DL1. foreground Present device-loss is recovered; failed frame is dropped",
                    !lost.Rendered && !lost.Presented && recovered.Presented && presentDevice.RecoverCount == 1 && presentDevice.DumpCount == 1 && presentHost.DeviceLostRecoveryCountForTest == 1,
                    $"lostRendered={lost.Rendered} recoveredPresented={recovered.Presented} recover={presentDevice.RecoverCount} dump={presentDevice.DumpCount}");
            }

            Check("DL2. device-loss log includes frame breadcrumbs and opcode stats",
                lines.Exists(l => l.Contains("[device-lost]")) && lines.Exists(l => l.Contains("ops=")) && lines.Exists(l => l.Contains("[probe]")),
                $"lines={lines.Count}");

            var submitDevice = new DeviceLossProbeDevice(DeviceLossProbeFailure.Submit);
            var submitHost = DeviceLostHost(strings, submitDevice, out var submitApp, out var submitWindow);
            using (submitApp)
            using (submitHost)
            {
                var lost = submitHost.RunFrame();
                var recovered = submitHost.RunFrame();
                Check("DL3. foreground Submit device-loss is recovered; next frame submits cleanly",
                    !lost.Rendered && recovered.Presented && submitDevice.Inner.FrameCount == 1 && submitDevice.RecoverCount == 1,
                    $"frames={submitDevice.Inner.FrameCount} recover={submitDevice.RecoverCount}");
            }

            var bugDevice = new DeviceLossProbeDevice(DeviceLossProbeFailure.PresentNonDevice);
            var bugHost = DeviceLostHost(strings, bugDevice, out var bugApp, out var bugWindow);
            using (bugApp)
            using (bugHost)
            {
                bool threw = false;
                try { bugHost.RunFrame(); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("synthetic render bug")) { threw = true; }
                Check("DL4. non-device-loss render exception still propagates",
                    threw && bugDevice.RecoverCount == 0 && bugHost.DeviceLostRecoveryCountForTest == 0 && bugDevice.NoteCount == 1,
                    $"threw={threw} recover={bugDevice.RecoverCount} notes={bugDevice.NoteCount}");
            }
        }
        finally
        {
            Diag.Sink = oldSink;
        }
    }



    static void FlexChecks(StringTable strings)
    {
        // justify space-between: row 300 wide, two 40-wide boxes → x = 0 and 260
        var sb = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 300, Height = 50, Justify = FlexJustify.SpaceBetween,
            Children = [new BoxEl { Width = 40, Height = 20 }, new BoxEl { Width = 40, Height = 20 }],
        });
        var a = sb.AbsoluteRect(Child(sb, sb.Root, 0));
        var b = sb.AbsoluteRect(Child(sb, sb.Root, 1));
        Check("10. justify space-between", Near(a.X, 0) && Near(b.X, 260) && Near(b.W, 40), $"x0={a.X:0.#} x1={b.X:0.#}");

        // flex-grow: row 300 wide, two grow:1 children → each 150 wide at x = 0 and 150
        var gr = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 300, Height = 40,
            Children = [new BoxEl { Grow = 1, Height = 20 }, new BoxEl { Grow = 1, Height = 20 }],
        });
        var g0 = gr.AbsoluteRect(Child(gr, gr.Root, 0));
        var g1 = gr.AbsoluteRect(Child(gr, gr.Root, 1));
        Check("11. flex-grow splits free space", Near(g0.W, 150) && Near(g1.W, 150) && Near(g1.X, 150), $"w0={g0.W:0.#} x1={g1.X:0.#}");

        // A finite row's grow/Basis=0 child suppresses intrinsic width. This is the toolbar/AutoSuggestBox shape:
        // the field contains a fixed-width editor behind a clipped Basis=0 wrapper, while a fixed account cluster follows.
        // The field must take only the row's remaining 200px; its old 720px editor width must not become the flex base.
        var toolbar = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 400, Height = 48,
            Children =
            [
                new BoxEl
                {
                    Grow = 1, Basis = 0, Shrink = 1, Direction = 0, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1, Shrink = 1, Direction = 0, MaxWidth = 720,
                            Children =
                            [
                                new BoxEl
                                {
                                    Grow = 1, Basis = 0, Shrink = 1, ClipToBounds = true,
                                    Children = [new BoxEl { Width = 720, Height = 32 }],
                                },
                                new BoxEl { Width = 38, Height = 32 },
                            ],
                        },
                    ],
                },
                new BoxEl { Width = 200, Height = 32 },
            ],
        });
        var searchSlot = toolbar.AbsoluteRect(Child(toolbar, toolbar.Root, 0));
        var account = toolbar.AbsoluteRect(Child(toolbar, toolbar.Root, 1));
        var field = toolbar.AbsoluteRect(Child(toolbar, Child(toolbar, toolbar.Root, 0), 0));
        Check("11a. finite-row Basis=0 suppresses intrinsic width (toolbar children do not overlap)",
            Near(searchSlot.W, 200) && Near(field.W, 200) && Near(account.X, 200) && field.Right <= account.X + 0.01f,
            $"slot={searchSlot.X:0.#}+{searchSlot.W:0.#} field={field.X:0.#}+{field.W:0.#} accountX={account.X:0.#}");

        // align-items center on the cross axis: row 100 tall, child 20 tall → y = 40
        var al = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 200, Height = 100, AlignItems = FlexAlign.Center,
            Children = [new BoxEl { Width = 40, Height = 20 }],
        });
        var ac = al.AbsoluteRect(Child(al, al.Root, 0));
        Check("12. align-items center", Near(ac.Y, 40), $"y={ac.Y:0.#}");

        // padding + gap: column, padding 10, gap 8, two 20-tall children → y = 10 and 38
        var pg = LayoutTree(strings, new BoxEl
        {
            Direction = 1, Width = 100, Height = 200, Padding = Edges4.All(10), Gap = 8,
            Children = [new BoxEl { Width = 40, Height = 20 }, new BoxEl { Width = 40, Height = 20 }],
        });
        var p0 = pg.AbsoluteRect(Child(pg, pg.Root, 0));
        var p1 = pg.AbsoluteRect(Child(pg, pg.Root, 1));
        Check("13. padding + gap stacking", Near(p0.Y, 10) && Near(p1.Y, 38) && Near(p0.X, 10), $"y0={p0.Y:0.#} y1={p1.Y:0.#}");
    }

    static (RectF col, RectF mid, RectF row, RectF side, RectF bar, ScrollState sc, NodeHandle vp)
        ShellLayout(StringTable strings, float w, float h, bool withShrink, bool tallMiddle = false)
    {
        var s = LayoutTree(strings, ShellColumnTree(w, h, withShrink, tallMiddle));
        var column = Child(s, s.Root, 0);
        var middle = Child(s, column, 2);
        var bar = Child(s, column, 3);
        var mainRow = Child(s, middle, 0);
        var side = Child(s, mainRow, 0);
        var vp = PlainViewport(s, s.Root);
        s.TryGetScroll(vp, out var sc);
        return (s.AbsoluteRect(column), s.AbsoluteRect(middle), s.AbsoluteRect(mainRow),
                s.AbsoluteRect(side), s.AbsoluteRect(bar), sc, vp);
    }

    static void ShellDockChecks(StringTable strings)
    {
        const float W = 1180f, H = 420f;   // short: free < 0 (40+48+56 + a 1320px-content middle overflows H)
        var (col, mid, row, side, bar, sc, vp) = ShellLayout(strings, W, H, withShrink: true, tallMiddle: true);

        float expectMid = H - ShellTitleH - ShellToolbarH - ShellPlayerH;   // 420 − 144 = 276

        bool barDocked = Near(bar.Y + bar.H, H) && Near(bar.H, ShellPlayerH);
        bool midBounded = Near(mid.H, expectMid) && Near(mid.Y, ShellTitleH + ShellToolbarH);
        Check("S1. shell dock: player bar pinned to window bottom",
            barDocked && midBounded,
            $"bar.Y+H={bar.Y + bar.H:0.#} (want {H:0}) bar.H={bar.H:0.#} mid.H={mid.H:0.#} (want {expectMid:0})");

        bool vpFound = !vp.IsNull;
        bool vpBounded = Near(sc.ViewportH, expectMid, 1f) && Near(side.H, expectMid, 1f);
        bool scrollable = sc.ContentH > sc.ViewportH + 1f;   // content (1320px) overflows the bounded viewport ⇒ scrolls
        Check("S2. shell sidebar: ScrollView bounded + scrollable",
            vpFound && vpBounded && scrollable,
            $"vpH={sc.ViewportH:0.#} (want {expectMid:0}) contentH={sc.ContentH:0.#} side.H={side.H:0.#}");

    }

    static void ShellResizeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("resizeshell", new Size2(1180, 900), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new ResizeShellProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        host.RunFrame();                                            // first full layout at 1180×900
        RectF barBig = host.Scene.AbsoluteRect(probe.Bar);
        bool dockedBig = Near(barBig.Y + barBig.H, 900f, 1f) && Near(barBig.H, 56f);

        window.ClientSizePx = new Size2(1180, 440);                 // resize DOWN (the reported repro: shrink from big)
        // The real app resizes via WM_SIZE → PaintRequested → Paint (which runs EnsureSize); RunFrame would idle-skip
        // (HasActiveWork is false — a bare ClientSizePx write fires no wake), so drive Paint directly to model the resize.
        host.Paint(0, keepAlive: true);                             // resize tick: EnsureSize → PublishViewport → Flush → Run
        host.Paint(0, keepAlive: true);                             // one settle frame

        RectF rootR = host.Scene.AbsoluteRect(host.Scene.Root);
        RectF barR = host.Scene.AbsoluteRect(probe.Bar);
        bool rootTracks = Near(rootR.H, 440f, 1f);
        bool dockedSmall = Near(barR.Y + barR.H, 440f, 1f) && Near(barR.H, 56f);

        Check("S3. shell resize-down: root tracks the window + player bar re-docks",
            dockedBig && rootTracks && dockedSmall,
            $"big:bar.bottom={barBig.Y + barBig.H:0.#}(want 900) | small:root.H={rootR.H:0.#}(want 440) bar.bottom={barR.Y + barR.H:0.#}(want 440)");

    }

    static void ResponsiveResizeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("respresize", new Size2(1180, 760), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new RespResizeProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        // Settle at WIDE — Responsive needs a couple frames (first build at fallback → OnBoundsChanged → rebuild at real w).
        for (int i = 0; i < 8; i++) host.RunFrame();
        RectF bandWide = host.Scene.AbsoluteRect(probe.Band);
        RectF belowWide = host.Scene.AbsoluteRect(probe.Below);
        bool wideNoOverlap = belowWide.Y >= bandWide.Y + bandWide.H - 1f;

        // Resize NARROW (band flips row→column → ~2x taller). Model the real WM_SIZE → Paint path + settle the rebuild.
        window.ClientSizePx = new Size2(560, 760);
        for (int i = 0; i < 10; i++) host.Paint(0, keepAlive: true);
        RectF bandNarrow = host.Scene.AbsoluteRect(probe.Band);
        RectF belowNarrow = host.Scene.AbsoluteRect(probe.Below);
        bool bandGrew = bandNarrow.H > bandWide.H + 50f;                              // column band is taller than the row band
        bool narrowNoOverlap = belowNarrow.Y >= bandNarrow.Y + bandNarrow.H - 1f;     // sibling reflowed below the taller band

        Check("RZ-RESP. responsive band reflows its sibling on resize (no overlap)",
            wideNoOverlap && bandGrew && narrowNoOverlap,
            $"wide band.H={bandWide.H:0} below.Y={belowWide.Y:0}(bottom={bandWide.Y + bandWide.H:0}) | narrow band.H={bandNarrow.H:0} below.Y={belowNarrow.Y:0}(bottom={bandNarrow.Y + bandNarrow.H:0})");
    }

    static void CollapsedHeroRebakeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("collapse-rebake", new Size2(360, 240), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new CollapsedHeroRebakeProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        host.RunFrame();
        var s = host.Scene;
        var vp = PlainViewport(s, s.Root);
        var content = s.ScrollRef(vp).ContentNode;
        ref ScrollState st = ref s.ScrollRef(vp);
        st.OffsetY = 260f;
        st.TargetY = 260f;
        s.Paint(content).LocalTransform = Affine2D.Translation(0f, -260f);
        s.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        ScrollBindEval.ApplyContinuous(s, vp, ref st);
        float collapsedBefore = s.Paint(probe.Hero).PresentedH;

        float mediaBefore = s.Paint(probe.Media).Opacity;

        probe.HeroHeight.Value = 240f;
        host.Reconciler.Runtime.Flush();
        float afterRebake = s.Paint(probe.Hero).PresentedH;

        host.Paint(0, keepAlive: true);
        float afterFrame = s.Paint(probe.Hero).PresentedH;
        float afterFrameShift = s.Paint(probe.Hero).ChildShiftY;
        float mediaAfter = s.Paint(probe.Media).Opacity;

        Check("RZ-HERO. collapsed PresentedHTrailing hero stays collapsed across bind re-bake",
            Near(collapsedBefore, 0f, 0.5f)
            && !float.IsNaN(afterRebake) && Near(afterRebake, 0f, 0.5f)
            && Near(afterFrame, 0f, 0.5f) && Near(afterFrameShift, -240f, 0.5f),
            $"before={collapsedBefore:0.#} rebake={afterRebake:0.#} frame={afterFrame:0.#}/{afterFrameShift:0.#}");

        // The photo proxy's scroll-bound dissolve must survive the re-bake: the re-render just RESET paint.Opacity to
        // the element literal (1), and the fresh bind row's FIRST eval must write the corrective 0 — a LastWritten
        // seeded 0 change-gates it away (the hero photo popping back over the collapsed band on a re-theme/focus regain).
        Check("RZ-HERO. scroll-bound media opacity re-writes 0 across a bind re-bake (first-write not change-gated)",
            Near(mediaBefore, 0f, 0.01f) && Near(mediaAfter, 0f, 0.01f),
            $"before={mediaBefore:0.##} after={mediaAfter:0.##}");
    }

    static void CollapsedHeroFocusChecks(StringTable strings)
    {
        var priorBg = Theme.WindowBackground;
        Theme.WindowBackground = ColorF.Transparent;
        try
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("collapse-focus", new Size2(360, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new CollapsedHeroRebakeProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe);

            host.RunFrame();
            var s = host.Scene;
            var vp = PlainViewport(s, s.Root);
            var content = s.ScrollRef(vp).ContentNode;
            ref ScrollState st = ref s.ScrollRef(vp);
            st.OffsetY = 260f;
            st.TargetY = 260f;
            s.Paint(content).LocalTransform = Affine2D.Translation(0f, -260f);
            s.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            ScrollBindEval.ApplyContinuous(s, vp, ref st);
            host.Paint(0, keepAlive: true);
            float collapsed = s.Paint(probe.Hero).PresentedH;
            float mediaCollapsed = s.Paint(probe.Media).Opacity;

            window.IsActive = false;
            var blurStats = host.Paint(0, keepAlive: true);
            float afterBlur = s.Paint(probe.Hero).PresentedH;
            float mediaBlur = s.Paint(probe.Media).Opacity;

            window.IsActive = true;
            var focusStats = host.Paint(0, keepAlive: true);
            float afterFocus = s.Paint(probe.Hero).PresentedH;
            float mediaFocus = s.Paint(probe.Media).Opacity;

            var steadyStats = host.Paint(0, keepAlive: true);
            float afterSteady = s.Paint(probe.Hero).PresentedH;
            float afterSteadyShift = s.Paint(probe.Hero).ChildShiftY;
            float mediaSteady = s.Paint(probe.Media).Opacity;

            Check("RZ-HERO. collapsed hero survives window blur/focus (Mica re-theme) with PresentedHTrailing intact",
                Near(collapsed, 0f, 0.5f)
                && Near(afterBlur, 0f, 0.5f)
                && Near(afterFocus, 0f, 0.5f)
                && Near(afterSteady, 0f, 0.5f) && Near(afterSteadyShift, -200f, 0.5f),
                $"collapsed={collapsed:0.#} blur={afterBlur:0.#} focus={afterFocus:0.#} steady={afterSteady:0.#}/{afterSteadyShift:0.#} "
                + $"blurReuse={blurStats.SpansReused} focusReuse={focusStats.SpansReused} steadyReuse={steadyStats.SpansReused}");

            // The hero PHOTO's scroll-bound dissolve (Opacity → 0) must ALSO survive the re-theme re-bake: a fresh bind
            // row change-gating its first write away leaves the reconciled literal (1) — the photo band popping back
            // over the collapsed hero on focus regain (the user-visible regression this guards).
            Check("RZ-HERO. scroll-bound media opacity stays 0 across window blur/focus (re-bake first-write)",
                Near(mediaCollapsed, 0f, 0.01f)
                && Near(mediaBlur, 0f, 0.01f)
                && Near(mediaFocus, 0f, 0.01f)
                && Near(mediaSteady, 0f, 0.01f),
                $"collapsed={mediaCollapsed:0.##} blur={mediaBlur:0.##} focus={mediaFocus:0.##} steady={mediaSteady:0.##}");
        }
        finally { Theme.WindowBackground = priorBg; }
    }

    static void SidebarCollapseFlipChecks(StringTable strings)
    {
        // Mode 0 = the pre-fix wiring (FLIP on the card, parent-relative) — must SNAP (regression guard for the bug).
        // Mode 2 = the fix (FLIP on the card, RelativeTo the row) — must SLIDE with pane↔card edge coherence.
        (float toggleEdge, float minEdge, float maxAbsCoherence, float regionX) Run(int mode)
        {
            FluentGpu.Dsl.Motion.ReducedMotion = false;
            CollapseToggleProbe.Mode = mode;
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("collapse", new Size2(1180, 760), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new CollapseToggleProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe);
            for (int i = 0; i < 4; i++) host.RunFrame();
            var s = host.Scene;
            float CardEdge() => s.AbsoluteRect(probe.Card).X;
            float PaneRight() { var b = s.Bounds(probe.Pane); float pw = s.Paint(probe.Pane).PresentedW; return b.X + (float.IsNaN(pw) ? b.W : pw); }
            probe.CompactSig.Value = true;   // collapse 360 -> 56
            host.RunFrame();
            float toggleEdge = CardEdge();
            float minEdge = toggleEdge, maxCoh = 0f;
            for (int k = 0; k < 24; k++)
            {
                host.RunFrame();
                minEdge = MathF.Min(minEdge, CardEdge());
                maxCoh = MathF.Max(maxCoh, MathF.Abs(CardEdge() - PaneRight()));   // 0 ⇒ contiguous swept-band damage (Bug B)
            }
            return (toggleEdge, minEdge, maxCoh, s.Bounds(probe.Content).X);
        }

        var bug = Run(0);
        var fix = Run(2);
        FluentGpu.Dsl.Motion.ReducedMotion = false;
        // Regression guard: the pre-fix (parent-relative card FLIP) SNAPS — the sheet jumps to 56 on the toggle frame.
        Check("SBF0. pre-fix wiring SNAPS the content sheet (parent-relative card FLIP ⇒ zero delta) — the bug",
            bug.toggleEdge < 100f, $"toggleEdge={bug.toggleEdge:0.#} (want ~56 snapped)");
        // Bug A fixed: the sheet does NOT snap on the toggle frame (stays near its old 360 edge) and eases down to ~56.
        Check("SBF1. fix: content sheet does not snap on the toggle frame (Bug A) — eases from ~360",
            fix.toggleEdge > 300f && fix.minEdge < 60f, $"toggleEdge={fix.toggleEdge:0.#} (want >300) minEdge={fix.minEdge:0.#} (want <60)");
        // Bug B fixed: the card's left edge tracks the pane's revealing right edge EXACTLY every frame ⇒ the swept region
        // is damaged contiguously (no stale-backdrop band), and the static underlays stay put (regionX == final 56).
        Check("SBF2. fix: pane↔card edge coherence every frame (contiguous swept damage, no ghost band — Bug B) + static underlays",
            fix.maxAbsCoherence < 1.0f && System.MathF.Abs(fix.regionX - 56f) < 1f,
            $"maxEdgeGap={fix.maxAbsCoherence:0.##}px (want <1) regionX={fix.regionX:0.#} (want 56 static)");
    }

    static void SidebarResizeSimChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("resizesim", new Size2(1180, 760), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new ResizeSidebarProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();   // mount + first layout at width 400

        long maxHotAlloc = 0; int maxNodes = 0; int maxCmds = 0;
        var trace = new System.Text.StringBuilder();
        // One full grip-drag cycle: down (Dragging=true → reduced-motion ON, snap) → N moves (one frame each) → up
        // (Dragging=false → restored) → settle frames. Returns the WORST per-frame |actualPaneWidth − target|: 0 means
        // the (animated) pane width tracks the cursor exactly; > 0 means the reflow spring is easing instead of snapping.
        float DragCycle(int idx, float fromW, float toW, int steps, bool record)
        {
            probe.BeginDrag();
            bool rm = FluentGpu.Dsl.Motion.ReducedMotion;   // DIAGNOSTIC: is the reduced-motion gate already ON before frame #1?
            float worst = 0f;
            for (int s = 1; s <= steps; s++)
            {
                float target = fromW + (toW - fromW) * s / steps;
                probe.WidthSig.Value = target;
                var fs = host.RunFrame();
                if (s == 1) rm = FluentGpu.Dsl.Motion.ReducedMotion;
                float actual = host.Scene.AbsoluteRect(probe.Pane).W;
                worst = MathF.Max(worst, MathF.Abs(actual - target));
                if (record && s > 1) { if (fs.HotPhaseAllocBytes > maxHotAlloc) maxHotAlloc = fs.HotPhaseAllocBytes; if (fs.NodesVisited > maxNodes) maxNodes = fs.NodesVisited; if (fs.DrawCommandCount > maxCmds) maxCmds = fs.DrawCommandCount; }
              }
              probe.EndDrag();
            for (int k = 0; k < 6; k++) host.RunFrame();   // release-settle frames
            trace.Append($" drag{idx}={worst:0.#}(rm={rm})");
            return worst;
        }

        float err1 = DragCycle(1, 400f, 280f, 12, record: true);    // FIRST resize ("ok")
        float err2 = DragCycle(2, 280f, 380f, 12, record: false);   // SECOND resize (reported "dogshit")
        float err3 = DragCycle(3, 380f, 240f, 12, record: false);   // THIRD
        FluentGpu.Dsl.Motion.ReducedMotion = false;                  // defensive: never leak the gate to later checks
        Console.WriteLine($"  [resize-sim]{trace}  maxNodes={maxNodes} maxCmds={maxCmds} maxHotAlloc={maxHotAlloc}B");
        Check("RZ1. FIRST sidebar resize tracks the cursor 1:1 (pane width == target, reflow spring snaps)",
            err1 < 1f, $"drag1 worstErr={err1:0.##}");
        Check("RZ3. SUBSEQUENT sidebar resizes track 1:1 too — no degradation after drag #1 (the reported bug)",
            err2 < 1f && err3 < 1f, $"drag2={err2:0.##} drag3={err3:0.##} (drag1={err1:0.##})");
        Check("RZ2. sidebar resize re-flow is bounded per frame (no per-frame growth in alloc / nodes / draw commands)",
            maxHotAlloc < 256 && maxNodes < 200 && maxCmds < 60, $"maxHotAlloc={maxHotAlloc}B nodes={maxNodes} cmds={maxCmds}");
    }

    static void ShellSidebarScrollChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("navscroll", new Size2(1180, 440), 1f));   // short ⇒ sidebar overflows
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new ResizeShellProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();

        var vpn = PlainViewport(host.Scene, host.Scene.Root);
        window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(120f, 250f), 0, 0, 240f));
        for (int i = 0; i < 8; i++) host.RunFrame();
        host.Scene.TryGetScroll(vpn, out var ssc);

        bool overflow = ssc.ContentH > ssc.ViewportH + 1f;
        bool scrolled = ssc.OffsetY > 1f;
        bool barRevealed = ssc.FadeT > 0.01f;
        Check("S4. navview: overflowing sidebar scrolls on wheel + reveals the auto-hiding scrollbar",
            !vpn.IsNull && overflow && scrolled && barRevealed,
            $"offY={ssc.OffsetY:0.#} contentH={ssc.ContentH:0.#} vpH={ssc.ViewportH:0.#} fadeT={ssc.FadeT:0.00}");
    }

    static void WrapChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 100, Height = 100, Wrap = true,
            Children =
            [
                new BoxEl { Width = 40, Height = 20 },
                new BoxEl { Width = 40, Height = 20 },
                new BoxEl { Width = 40, Height = 20 },
            ],
        });
        var c0 = scene.AbsoluteRect(Child(scene, scene.Root, 0));
        var c1 = scene.AbsoluteRect(Child(scene, scene.Root, 1));
        var c2 = scene.AbsoluteRect(Child(scene, scene.Root, 2));
        Check("29. flex-wrap flows children to lines", Near(c0.X, 0) && Near(c1.X, 40) && Near(c2.X, 0) && Near(c2.Y, 20), $"c2=({c2.X:0.#},{c2.Y:0.#})");
    }

    static void WrapGrowChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 100, Height = 100, Wrap = true,   // gap 0
            Children =
            [
                new BoxEl { Width = 40, Height = 20, Grow = 1f },
                new BoxEl { Width = 40, Height = 20, Grow = 1f },
                new BoxEl { Width = 40, Height = 20, Grow = 1f },
            ],
        });
        var c0 = scene.AbsoluteRect(Child(scene, scene.Root, 0));
        var c1 = scene.AbsoluteRect(Child(scene, scene.Root, 1));
        var c2 = scene.AbsoluteRect(Child(scene, scene.Root, 2));
        // line 1 = [0,1]: 40+40 base, 20 free split → each 50 (c1 starts at 50). line 2 = [2]: lone grow fills → 100 wide.
        bool ok = Near(c0.X, 0) && Near(c0.W, 50) && Near(c1.X, 50) && Near(c1.W, 50)
                  && Near(c2.X, 0) && Near(c2.Y, 20) && Near(c2.W, 100);
        Check("29b. flex-wrap distributes grow per line (incl. the last) → lines fill", ok,
            $"c0.W={c0.W:0.#} c1.X={c1.X:0.#} c2.W={c2.W:0.#}");
    }

    static void ConstrainedWrapChecks(StringTable strings)
    {
        const string caption =
            "100,000 rows with real CDN thumbnails - only the visible window is realized and recycled over a slab free-list; " +
            "images decode off-thread, pack into the atlas, and evict off-screen, so memory stays flat. Wheel to scroll.";

        var tree = Ui.ZStack(new BoxEl
        {
            Direction = 0,
            Children =
            [
                new BoxEl { Width = 320f, Direction = 1 },
                new BoxEl
                {
                    Direction = 1,
                    Grow = 1f,
                    Gap = 16f,
                    Padding = Edges4.All(24f),
                    Children =
                    [
                        new TextEl("List virtualization") { Size = 28f, Bold = true },
                        new TextEl(caption) { Size = 14f, Wrap = TextWrap.Wrap },
                        Virtual.List(100000, 48f, _ => new BoxEl { Height = 48f }, keyOf: i => "r" + i) with { Grow = 1f },
                    ],
                },
            ],
        });

        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root, new Size2(900f, 720f));

        NodeHandle captionNode = default, listNode = default;
        void Visit(NodeHandle n)
        {
            if (n.IsNull) return;
            ref var paint = ref scene.Paint(n);
            if (paint.VisualKind == VisualKind.Text && strings.Resolve(paint.Text) == caption) captionNode = n;
            if (scene.TryGetScroll(n, out var sc) && sc.ItemCount == 100000) listNode = n;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) Visit(c);
        }
        Visit(scene.Root);

        var cap = scene.AbsoluteRect(captionNode);
        var list = scene.AbsoluteRect(listNode);
        bool captionWrapped = cap.H > 30f;                 // > one 14px line in the headless font system
        bool listConstrained = Near(list.X, 344f) && Near(list.W, 532f) && list.Right <= 900f;

        Check("29a. wrapping text in fixed-pane + grow content is measured to the content frame",
            captionWrapped && listConstrained,
            $"caption={cap.W:0}x{cap.H:0} list=({list.X:0},{list.W:0},right={list.Right:0})");
    }

    static void DetailResizeFlickerChecks(StringTable strings)
    {
        // Fix 1 — tier remount without cold stagger fills the viewport window in the remount frame.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("tier-remount", new Size2(400, 320), 1f, Composited: true));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new ColdStaggerRemountProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe);

            host.RunFrame();
            var firstVp = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(firstVp, out var firstSc);
            int staggeredFirst = BoundSlotCount(host.Scene, host.Scene.Root);
            int firstWindowRows = firstSc.LastRealized - firstSc.FirstRealized;
            for (int i = 0; i < 24 && host.Reconciler.HasWarmingVirtuals; i++) host.RunFrame();
            int staggeredSettled = BoundSlotCount(host.Scene, host.Scene.Root);
            bool wasWarming = host.Reconciler.HasWarmingVirtuals;
            probe.Tier.Value = 1;   // keyed remount — staggerColdRealize:false (list already realized once)
            host.RunFrame();
            var vp = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc);
            int remountSlots = BoundSlotCount(host.Scene, host.Scene.Root);
            int windowRows = sc.LastRealized - sc.FirstRealized;
            Check("RZ-TIER. cold stagger never presents a partial visible window; tier remount fills in one frame",
                staggeredFirst >= firstWindowRows && staggeredFirst >= 8 && firstWindowRows >= 8
                && !wasWarming && staggeredSettled >= staggeredFirst
                && remountSlots >= windowRows && remountSlots >= 8,
                $"first={staggeredFirst}/{firstWindowRows} settled={staggeredSettled} remount={remountSlots} window={windowRows} first={sc.FirstRealized} last={sc.LastRealized}");
        }

        // Fix 2 — modal-loop keep-alive must not swallow warming virtual refill when ambient animation is live.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("modal-warm", new Size2(400, 320), 1f, Composited: true));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new ModalWarmProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe);

            host.RunFrame();
            bool warming = host.Reconciler.HasWarmingVirtuals;
            int slots0 = BoundSlotCount(host.Scene, host.Scene.Root);
            var vp0 = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp0, out var sc0);
            int windowRows0 = sc0.LastRealized - sc0.FirstRealized;
            host.Animation.Keyframes(host.Scene.Root, AnimChannel.Opacity,
                [new Keyframe(0f, 0.5f, Easing.Linear), new Keyframe(1f, 1f, Easing.Linear)], 1000f, loop: true);
            bool ambient = host.CurrentWakeReasons.HasFlag(WakeReasons.Anim);
            window.InModalLoop = true;
            window.SizedInModalLoop = false;   // titlebar move (not edge resize) — ambient ticks must still paint
            host.Paint(0, keepAlive: true);
            int slots1 = BoundSlotCount(host.Scene, host.Scene.Root);
            Check("RZ-MODAL. modal-loop keep-alive preserves/refills the visible virtual window under ambient-only animation wake",
                ambient && slots0 >= windowRows0 && slots0 >= 8 && (warming ? slots1 > slots0 : slots1 >= slots0),
                $"warming={warming} ambient={ambient} slots {slots0}→{slots1} window={windowRows0} wake={host.CurrentWakeReasons}");
        }
    }

    static void ButterSmoothResizeChecks(StringTable strings)
    {
        // RZ-DEFER — composited edge-resize defer: HWND grows but layout/swapchain stay at last size until modal exit.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rz-defer", new Size2(400, 320), 1f, Composited: true));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ResizeBoxProbe());

            host.RunFrame();
            float rootHBefore = host.Scene.AbsoluteRect(host.Scene.Root).H;

            window.InModalLoop = true;
            window.SizedInModalLoop = true;
            window.ClientSizePx = new Size2(800, 640);
            host.Paint(0, keepAlive: true);
            float rootHDeferred = host.Scene.AbsoluteRect(host.Scene.Root).H;

            window.InModalLoop = false;
            window.SizedInModalLoop = false;
            host.Paint(0, keepAlive: true);
            float rootHSettled = host.Scene.AbsoluteRect(host.Scene.Root).H;

            Check("RZ-DEFER. composited modal grow keeps viewport until exit, then applies final size",
                Near(rootHBefore, 320f, 1f) && Near(rootHDeferred, rootHBefore, 1f) && Near(rootHSettled, 640f, 1f),
                $"rootH {rootHBefore:0.#}→{rootHDeferred:0.#}→{rootHSettled:0.#} (want 320,320,640)");
        }

        // RZ-LIVE — non-composited modal resize applies the new client size on each keep-alive tick.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rz-live", new Size2(400, 320), 1f, Composited: false));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ResizeBoxProbe());

            host.RunFrame();
            window.InModalLoop = true;
            window.SizedInModalLoop = true;
            window.ClientSizePx = new Size2(800, 640);
            host.Paint(0, keepAlive: true);
            float rootH = host.Scene.AbsoluteRect(host.Scene.Root).H;

            Check("RZ-LIVE. non-composited modal keep-alive applies the new client size",
                Near(rootH, 640f, 1f), $"rootH={rootH:0.#} (want 640)");
        }

        // RZ-SETTLE — settle frame presents, disables span reuse for resize, hints DWM sync.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rz-settle", new Size2(400, 320), 1f, Composited: true));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ResizeBoxProbe());

            host.RunFrame();
            window.InModalLoop = true;
            window.SizedInModalLoop = true;
            window.ClientSizePx = new Size2(800, 640);
            host.Paint(0, keepAlive: true);
            window.InModalLoop = false;
            window.SizedInModalLoop = false;
            var stats = host.Paint(0, keepAlive: true);

            Check("RZ-SETTLE. modal exit settle presents with span reuse disabled for resize + settle hint",
                stats.Presented
                && (stats.SpanReuseDisabledReasons & SpanReuseDisabledReason.Resize) != 0
                && device.HintSettlePresentCount == 1,
                $"presented={stats.Presented} spanDisable={stats.SpanReuseDisabledReasons} hint={device.HintSettlePresentCount}");
        }

        // RZ-THROTTLE — 30 Hz gate for non-composited modal edge-resize paints.
        {
            long last = 0;
            int paints = 0;
            for (int step = 0; step < 10; step++)
            {
                long now = step * 8;
                if (!ModalPaintThrottle.ShouldSkip(now, ref last, sized: true, minIntervalMs: 33))
                    paints++;
            }
            long gapLast = 0;
            bool gapPaint = !ModalPaintThrottle.ShouldSkip(40, ref gapLast, sized: true, minIntervalMs: 33);
            Check("RZ-THROTTLE. 10 steps @8ms yields ≤4 paints; 40ms gap allows paint",
                paints <= 4 && gapPaint, $"paints={paints} gapPaint={gapPaint} last={gapLast}");
        }

        // RZ-MOVE — composited titlebar move: ambient animation ticks still submit; span reuse stays enabled.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rz-move", new Size2(400, 320), 1f, Composited: true));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ResizeBoxProbe());

            host.RunFrame();
            host.Animation.Keyframes(host.Scene.Root, AnimChannel.Opacity,
                [new Keyframe(0f, 0.5f, Easing.Linear), new Keyframe(1f, 1f, Easing.Linear)], 1000f, loop: true);
            int framesBefore = device.FrameCount;
            window.InModalLoop = true;
            window.SizedInModalLoop = false;
            var stats = host.Paint(0, keepAlive: true);

            Check("RZ-MOVE. composited move keeps ambient modal ticks + span reuse",
                device.FrameCount > framesBefore && stats.Presented
                && (stats.SpanReuseDisabledReasons & SpanReuseDisabledReason.ModalPaint) == 0,
                $"frames {framesBefore}→{device.FrameCount} presented={stats.Presented} spanDisable={stats.SpanReuseDisabledReasons}");
        }

        // RZ-MOVE2 — composited edge resize: ambient-only ticks bail; span reuse disabled for modal paint.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rz-move2", new Size2(400, 320), 1f, Composited: true));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ResizeBoxProbe());

            host.RunFrame();
            host.Animation.Keyframes(host.Scene.Root, AnimChannel.Opacity,
                [new Keyframe(0f, 0.5f, Easing.Linear), new Keyframe(1f, 1f, Easing.Linear)], 1000f, loop: true);
            int framesBefore = device.FrameCount;
            window.InModalLoop = true;
            window.SizedInModalLoop = true;
            var stats = host.Paint(0, keepAlive: true);

            Check("RZ-MOVE2. composited edge resize bails ambient-only ticks + disables span reuse",
                device.FrameCount == framesBefore,
                $"frames {framesBefore}→{device.FrameCount} (idle-skip expected)");
        }
    }

    static void G3TokenChecks()
    {
        var savedPalette = Tok.Palette;
        var savedTheme = Tok.Theme;

        // gate.tok.onaccent-contrast
        // Extreme 1: white fill (dark theme -> Light2(white) == white) -> near-black ink, AA-passing, memoized per epoch.
        Tok.Use(savedPalette, ThemeKind.Dark);
        Tok.SetAccent(ColorF.FromRgba(0xFF, 0xFF, 0xFF));
        ColorF bgW = Tok.AccentDefault;
        int cA = Tok.OnAccentComputeCount;
        ColorF inkW = Tok.OnAccent;
        ColorF inkW2 = Tok.OnAccent;               // second read, same epoch -> cached (no recompute)
        int cB = Tok.OnAccentComputeCount;
        bool memoized = cB == cA + 1 && inkW2 == inkW;
        bool wMatches = inkW == ColorContrast.PickContrast(bgW) && ColorContrast.Ratio(inkW, bgW) >= 4.5f;

        // Extreme 2: near-black fill (light theme -> Dark1(#161616) very dark) -> white ink, AA-passing.
        Tok.Use(savedPalette, ThemeKind.Light);
        Tok.SetAccent(ColorF.FromRgba(0x16, 0x16, 0x16));
        ColorF bgB = Tok.AccentDefault;
        ColorF inkB = Tok.OnAccent;
        bool bMatches = inkB == ColorContrast.PickContrast(bgB) && ColorContrast.Ratio(inkB, bgB) >= 4.5f;

        // Mid-saturated fill: picks the WCAG-better ink of the pair.
        Tok.SetAccent(ColorF.FromRgba(0x2E, 0x6C, 0xE0));
        ColorF bgM = Tok.AccentDefault;
        bool mMatches = Tok.OnAccent == ColorContrast.PickContrast(bgM);
        Check("gate.tok.onaccent-contrast", wMatches && bMatches && mMatches && memoized,
            $"whiteAA={ColorContrast.Ratio(inkW, bgW):0.0} blackAA={ColorContrast.Ratio(inkB, bgB):0.0} midMatch={mMatches} memo={memoized}(compute {cA}->{cB})");

        // gate.tok.onmedia-static-identity: scrim/ink getters return identical instances/values (no per-read alloc).
        var s1 = Tok.ScrimBottom; var s2 = Tok.ScrimBottom;
        var t1 = Tok.ScrimTop; var t2 = Tok.ScrimTop;
        bool gradSame = ReferenceEquals(s1.Stops, s2.Stops) && ReferenceEquals(t1.Stops, t2.Stops);
        bool inkStatic = Tok.OnMediaPrimary == new ColorF(1f, 1f, 1f, 1f)
                      && Tok.OnMediaSecondary == new ColorF(1f, 1f, 1f, 0.80f)
                      && Tok.OnMediaTertiary == new ColorF(1f, 1f, 1f, 0.60f)
                      && Tok.MediaScrim == new ColorF(0f, 0f, 0f, 0.55f);
        Check("gate.tok.onmedia-static-identity", gradSame && inkStatic,
            $"scrimStopsIdentical={gradSame} inkScrimValues={inkStatic}");

        // gate.tok.generated-accessors: a GENERATED forward reflects the live TokenSet across a theme swap.
        Tok.Use(savedPalette, ThemeKind.Dark);
        ColorF darkVal = Tok.FillCardDefault;                     // generated `=> T.FillCardDefault`
        bool darkMatch = darkVal == Tok.T.FillCardDefault;
        Tok.Use(savedPalette, ThemeKind.Light);
        ColorF lightVal = Tok.FillCardDefault;
        bool lightMatch = lightVal == Tok.T.FillCardDefault;
        bool flipped = darkVal != lightVal;                       // the table swap is visible through the generated getter
        Check("gate.tok.generated-accessors", darkMatch && lightMatch && flipped,
            $"darkMatch={darkMatch} lightMatch={lightMatch} flipped={flipped}");

        // gate.icons.generated-table: the generated Icons table matches (golden few incl. a Wavee fold-in).
        // Compared by codepoint (ASCII-safe source; no raw PUA glyphs, per the engine icon-font convention).
        bool iconsOk = Icons.Play.Length == 1 && Icons.Play[0] == (char)0xE768
                    && Icons.Pause[0] == (char)0xE769 && Icons.Home[0] == (char)0xE80F
                    && Icons.ChromeClose[0] == (char)0xE8BB && Icons.Album[0] == (char)0xE93C
                    && Icons.Delete[0] == (char)0xE74D;
        Check("gate.icons.generated-table", iconsOk,
            $"Play=U+{(int)Icons.Play[0]:X4} Album=U+{(int)Icons.Album[0]:X4} Delete=U+{(int)Icons.Delete[0]:X4}");

        Tok.SetAccent(null);                                      // restore (headless slice runs with no injected accent)
        Tok.Use(savedPalette, savedTheme);
    }

    static void G3AspectChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("aspect", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new AspectProbe());
        host.RunFrame();

        var root = host.Scene.Root;
        var wBox = Child(host.Scene, root, 0);
        var hBox = Child(host.Scene, root, 1);
        var wb = host.Scene.Bounds(wBox);
        var hb = host.Scene.Bounds(hBox);
        bool wDerives = Near(wb.W, 320f, 0.5f) && Near(wb.H, 180f, 0.5f);   // 320 / (16/9) = 180
        bool hDerives = Near(hb.W, 160f, 0.5f) && Near(hb.H, 90f, 0.5f);    // 90 * (16/9) = 160
        // Not a layout boundary: exactly one authored dimension stays NaN (IsLayoutBoundary needs both set).
        bool notBoundary = float.IsNaN(host.Scene.Layout(wBox).Height) && float.IsNaN(host.Scene.Layout(hBox).Width);
        Check("gate.layout.aspect-derive", wDerives && hDerives && notBoundary,
            $"w=({wb.W:0}x{wb.H:0}) h=({hb.W:0}x{hb.H:0}) notBoundary={notBoundary}");
    }

    static void G3PrimitiveChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("primitives", new Size2(640, 640), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new PrimitivesProbe());
        host.RunFrame();

        var s = host.Scene;
        var root = s.Root;
        var row0 = Child(s, root, 0);
        var row1 = Child(s, root, 1);
        var wrap = Child(s, root, 2);
        var centerOuter = Child(s, root, 3);

        // Spacer() grows: the trailing box is pushed to the far edge (300 - 40 - 60 = 200 slack -> box2.X == 240).
        float grownX = s.AbsoluteRect(Child(s, row0, 2)).X - s.AbsoluteRect(row0).X;
        bool spacerGrows = Near(grownX, 240f, 1f);
        // Spacer(24) is rigid: trailing box sits right after the 24px gap (box2.X == 40 + 24 == 64).
        float fixedX = s.AbsoluteRect(Child(s, row1, 2)).X - s.AbsoluteRect(row1).X;
        bool spacerFixed = Near(fixedX, 64f, 1f);
        // Wrap: the third 120-wide box wraps to a new line (Y drops one row, X returns to the line start).
        var w0 = s.AbsoluteRect(Child(s, wrap, 0));
        var w2 = s.AbsoluteRect(Child(s, wrap, 2));
        bool wraps = w2.Y > w0.Y + 20f && Near(w2.X, w0.X, 1f);
        // Center: the 40x40 child is centered on both axes in the 200x200 box.
        var outer = s.AbsoluteRect(centerOuter);
        var inner = s.AbsoluteRect(Child(s, Child(s, centerOuter, 0), 0));
        bool centered = Near(inner.X - outer.X, (outer.W - inner.W) / 2f, 1f)
                     && Near(inner.Y - outer.Y, (outer.H - inner.H) / 2f, 1f);
        Check("gate.ui.spacer-wrap-center", spacerGrows && spacerFixed && wraps && centered,
            $"grow={grownX:0} fixed={fixedX:0} wrapDy={w2.Y - w0.Y:0} centerOff={inner.X - outer.X:0}");
    }

    static void GridChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("grid", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new GridProbe());
        host.RunFrame();

        var grid = host.Scene.Root;
        var b0 = host.Scene.Bounds(Child(host.Scene, grid, 0));
        var b1 = host.Scene.Bounds(Child(host.Scene, grid, 1));
        var b2 = host.Scene.Bounds(Child(host.Scene, grid, 2));
        var b3 = host.Scene.Bounds(Child(host.Scene, grid, 3));
        var b4 = host.Scene.Bounds(Child(host.Scene, grid, 4));
        bool cols = Near(b0.X, 0) && Near(b1.X, 110) && Near(b2.X, 220) && Near(b0.W, 100);
        bool rows = Near(b3.X, 0) && Near(b3.Y, 60) && Near(b4.X, 110) && Near(b4.Y, 60);
        Check("51. Grid: star tracks split width + row-major auto-flow", cols && rows,
            $"cols x={b0.X:0},{b1.X:0},{b2.X:0} w={b0.W:0}; row2 y={b3.Y:0}");
    }

    static void GridOverflowChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("gridoverflow", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new GridOverflowProbe());
        host.RunFrame();

        const float W = 120f;
        var grid = host.Scene.Root;
        bool within = true, ordered = true;
        float prevRight = 0f;
        var detail = new System.Text.StringBuilder();
        for (int i = 0; i < 4; i++)
        {
            var b = host.Scene.Bounds(Child(host.Scene, grid, i));
            detail.Append($" c{i}=({b.X:0.#},{b.W:0.#})");
            if (b.X < -0.5f || b.X + b.W > W + 0.5f) within = false;   // no column positioned/sized past the edge
            if (b.X + 0.5f < prevRight) ordered = false;               // no overlap (starts at/after the prev right edge)
            prevRight = b.X + b.W;
        }
        Check("51c. Grid: fixed columns exceeding width shrink-to-fit (no overflow / no overlap)", within && ordered,
            $"W={W:0}{detail}");
    }

    static void GridStretchChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("gridstretch", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new GridStretchProbe());
        host.RunFrame();

        var root = host.Scene.Root;
        var grid = Child(host.Scene, root, 0);
        var after = Child(host.Scene, root, 1);
        var gb = host.Scene.Bounds(grid);
        var ab = host.Scene.Bounds(after);
        bool gridHeight = Near(gb.H, 192f, 1f);          // grid measured its 2 real rows (not 0)
        bool gridWidth = Near(gb.W, 420f, 1f);           // stretched to fill the 420-wide column
        bool noOverlap = ab.Y >= 192f - 0.5f;            // sibling below the grid's real content, not on top of it
        Check("51b. Grid: stretch-width grid measures content height (sibling doesn't overlap)",
            gridHeight && gridWidth && noOverlap, $"gridW={gb.W:0} gridH={gb.H:0} afterY={ab.Y:0}");
    }

    static void ColumnAlignChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("colalign", new Size2(1000, 500), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ColumnAlignProbe());
        host.RunFrame();

        var scene = host.Scene;
        var root = scene.Root;
        var headerGrid = Child(scene, Child(scene, Child(scene, root, 0), 0), 0);   // root→chrome→headerBox→grid

        // Locate the scroller, its content panel, the first realized row (RowSkin) → lane → grid.
        static NodeHandle FindScroll(SceneStore s, NodeHandle n)
        {
            if (s.TryGetScroll(n, out _)) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) { var r = FindScroll(s, c); if (!r.IsNull) return r; }
            return NodeHandle.Null;
        }
        var vp = FindScroll(scene, root);
        NodeHandle firstRow = NodeHandle.Null;
        if (!vp.IsNull && scene.TryGetScroll(vp, out var sc)) firstRow = scene.FirstChild(sc.ContentNode);
        bool haveScroll = !firstRow.IsNull;
        var rowGrid = firstRow.IsNull ? NodeHandle.Null : Child(scene, Child(scene, firstRow, 0), 0);   // RowSkin→lane→grid

        var hsb = new System.Text.StringBuilder();
        var rsb = new System.Text.StringBuilder();
        bool aligned = !rowGrid.IsNull;
        if (!rowGrid.IsNull)
            for (int i = 0; i < 7; i++)
            {
                float hx = scene.AbsoluteRect(Child(scene, headerGrid, i)).X;   // window-space (sums the parent chain)
                float rx = scene.AbsoluteRect(Child(scene, rowGrid, i)).X;
                hsb.Append($"{hx:0.#} ");
                rsb.Append($"{rx:0.#} ");
                if (!Near(hx, rx, 0.5f)) aligned = false;
            }
        Check("51d. virtualized list items honor their Margin → a fixed header's columns align with the scrolled row columns", aligned,
            $"scroll={haveScroll} hdr=[ {hsb}] row=[ {rsb}]");
    }

    static void AutoGridChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("autogrid", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new AutoGridProbe());
        host.RunFrame();

        var grid = host.Scene.Root;
        var b0 = host.Scene.Bounds(Child(host.Scene, grid, 0));
        var b3 = host.Scene.Bounds(Child(host.Scene, grid, 3));
        var b4 = host.Scene.Bounds(Child(host.Scene, grid, 4));
        bool count = Near(b0.W, 122.5f, 0.5f) && Near(b0.X, 0) && Near(b3.X, 397.5f, 0.5f);   // 4 columns
        bool fillsWidth = Near(b3.X + b3.W, 520f, 0.5f);                                       // right edge flush — fills the width
        bool wraps = Near(b4.X, 0) && Near(b4.Y, 60f, 0.5f);                                   // 5th cell wraps to row 2 (50 + 10 gap)
        Check("51c. AutoGrid: auto-fill packs equal columns that fill the width + reflow",
            count && fillsWidth && wraps, $"col0w={b0.W:0.0} col3right={b3.X + b3.W:0.0} row2y={b4.Y:0}");
    }

    static void VirtualGridChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("vgrid", new Size2(800, 600), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VGridProbe());
        host.RunFrame();

        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        int realized = host.Scene.ChildCount(content);
        bool windowed = realized >= 16 && realized < 80;                 // ~visible rows × 4 cols, not 1000
        // ContentExtent = 250 rows × 100 + 249 gaps × 12 = 27,988
        bool contentSize = Near(sc.ContentH, 27988f, 2f);
        // colW = (520 − 3×12)/4 = 121; item 0 at (0,0), item 1 at (133,0), item 4 at (0,112)
        var b0 = host.Scene.Bounds(Child(host.Scene, content, 0));
        var b1 = host.Scene.Bounds(Child(host.Scene, content, 1));
        var b4 = host.Scene.Bounds(Child(host.Scene, content, 4));
        bool cells = Near(b0.X, 0) && Near(b0.W, 121, 1f) && Near(b1.X, 133, 1f) && Near(b4.X, 0) && Near(b4.Y, 112, 1f);

        long live0 = host.Scene.LiveCount;
        for (int s = 0; s < 40; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(200, 200), 0, 0, 4000f)); host.RunFrame(); }
        long liveEnd = host.Scene.LiveCount;
        bool recycled = liveEnd < 120 && Math.Abs(liveEnd - live0) < 40;

        Check("53. VirtualGrid: 2-D row-window + cell positions + recycle", windowed && contentSize && cells && recycled,
            $"realized={realized} content={sc.ContentH:0} cell0w={b0.W:0} live {live0}→{liveEnd}");
    }

    static void ZStackRepeaterChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, Ui.ZStack(
            new BoxEl { Width = 120, Height = 40, Fill = ColorF.FromRgba(200, 0, 0) },
            new BoxEl { Width = 60, Height = 20, Fill = ColorF.FromRgba(0, 200, 0) }) with { Width = 200, Height = 100 });
        var z0 = scene.AbsoluteRect(Child(scene, scene.Root, 0));
        var z1 = scene.AbsoluteRect(Child(scene, scene.Root, 1));
        bool zstack = Near(z0.X, 0) && Near(z0.Y, 0) && Near(z1.X, 0) && Near(z1.Y, 0) && Near(z0.W, 120) && Near(z1.W, 60);

        var inlineEl = Repeater.ItemsRepeater(5, i => new BoxEl { Width = 10, Height = 10 }, RepeatLayout.Inline(gap: 2f), keyOf: i => "k" + i);
        bool inlineN = inlineEl is BoxEl box && box.Children.Length == 5 && box.Children[0].Key == "k0";
        bool stackVirtual = Repeater.ItemsRepeater(1000, i => new BoxEl(), RepeatLayout.Stack(40f)) is VirtualListEl;

        Check("55. ZStack overlays at origin; ItemsRepeater builds (Inline) / virtualizes (Stack)", zstack && inlineN && stackVirtual,
            $"z0=({z0.X:0},{z0.Y:0}) z1=({z1.X:0},{z1.Y:0}) inlineN={inlineN} stackVirt={stackVirtual}");
    }

    static void LayoutBoundaryMeasuredChecks(StringTable strings)
    {
        // gate.layout.boundary-modifier (record contract): .Boundary() == IsolateLayout + ClipToBounds.
        {
            var b = new BoxEl { Grow = 1f }.Boundary();
            Check("gate.layout.boundary-modifier (record): .Boundary() sets IsolateLayout + ClipToBounds",
                b.IsolateLayout && b.ClipToBounds, $"isolate={b.IsolateLayout} clip={b.ClipToBounds}");
        }

        int EscapesOnDeepFlip(bool useBoundary)
        {
            BoundaryEscapeProbe.UseBoundary = useBoundary;
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("boundary", new Size2(800, 600), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new BoundaryEscapeProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe);
            for (int i = 0; i < 4; i++) host.RunFrame();   // mount + full layout + settle (_everLaidOut ⇒ scoped path next)
            probe.Deep.Value = 1;                          // deep re-render → deep node LayoutDirty → SCOPED relayout next frame
            return host.RunFrame().RootRelayoutEscapes;
        }

        int withBoundary = EscapesOnDeepFlip(true);
        int withoutBoundary = EscapesOnDeepFlip(false);
        BoundaryEscapeProbe.UseBoundary = false;   // restore the static
        Check("gate.layout.boundary-modifier: deep re-render inside a .Boundary() relayouts only the subtree (0 escapes to root)",
            withBoundary == 0, $"escapes={withBoundary} (want 0)");
        Check("gate.layout.escape-counter: the same scene WITHOUT .Boundary() escapes to root >= 1 (full-subtree relayout)",
            withoutBoundary >= 1, $"escapes={withoutBoundary} (want >=1)");

        // gate.bounds.measured-one-frame-late: a resize takes layout effect in frame A but the consumer re-renders in
        // frame B (the write happens during layout ⇒ MarksStale only), and layout is not re-entered in frame A.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("measured", new Size2(800, 600), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new MeasuredWidthProbe();
            using var host = new AppHost(app, window, device, fonts, strings, new MeasureHost(probe));
            for (int i = 0; i < 5; i++) host.RunFrame();   // mount + seed re-render + settle
            int r0 = probe.RenderCount; float seen0 = probe.LastSeen;
            probe.DriveWidth.Value = 108f;                 // resize the bound width
            host.RunFrame();                               // frame A: bind → scoped relayout → hook writes measured signal (DEFERRED)
            int rA = probe.RenderCount;
            host.RunFrame();                               // frame B: consumer re-renders with the new value
            int rB = probe.RenderCount; float seenB = probe.LastSeen;
            Check("gate.bounds.measured-one-frame-late: consumer does NOT re-render in the resize frame (no re-entrancy), then exactly next frame",
                seen0 == 100f && rA == r0 && rB == r0 + 1 && seenB == 108f,
                $"seen0={seen0} rA-r0={rA - r0}(want 0) rB-r0={rB - r0}(want 1) seenB={seenB}(want 108)");
        }

        // gate.bounds.measured-quantum: quantum=4 → round(w/4)*4; a +2px change stays in-bucket (suppressed), a +8px crosses.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("quantum", new Size2(800, 600), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new MeasuredWidthProbe { Quantum = 4f };
            probe.DriveWidth.Value = 199f;                 // round(199/4)*4 = 200
            using var host = new AppHost(app, window, device, fonts, strings, new MeasureHost(probe));
            for (int i = 0; i < 5; i++) host.RunFrame();
            int q0 = probe.RenderCount; float qseen0 = probe.LastSeen;
            probe.DriveWidth.Value = 201f;                 // Δ2: round(201/4)*4 = 200 (same bucket) → suppressed
            host.RunFrame(); host.RunFrame();
            int qSup = probe.RenderCount;
            probe.DriveWidth.Value = 209f;                 // Δ8: round(209/4)*4 = 208 → passes
            host.RunFrame(); host.RunFrame();
            int qPass = probe.RenderCount; float qseenPass = probe.LastSeen;
            Check("gate.bounds.measured-quantum: quantum=4 suppresses a 2px change, passes an 8px change",
                qseen0 == 200f && qSup == q0 && qPass == q0 + 1 && qseenPass == 208f,
                $"seen0={qseen0}(want 200) supΔ={qSup - q0}(want 0) passΔ={qPass - q0}(want 1) seenPass={qseenPass}(want 208)");
        }

        // gate.bounds.composes-with-onboundschanged: the element author's own OnBoundsChanged still fires with a
        // UseMeasuredBounds active on the same node (separate SceneStore slots, both dispatched).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("compose", new Size2(800, 600), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new ComposeBoundsProbe();
            using var host = new AppHost(app, window, device, fonts, strings, new MeasureHost(probe));
            for (int i = 0; i < 5; i++) host.RunFrame();
            int authorBefore = probe.AuthorFires;   // >= 1 (mount initial delivery)
            probe.DriveWidth.Value = 140f;
            host.RunFrame(); host.RunFrame();
            Check("gate.bounds.composes-with-onboundschanged: element OnBoundsChanged still fires with UseMeasuredBounds on the same node",
                authorBefore >= 1 && probe.AuthorFires > authorBefore && probe.LastSeenW == 140f,
                $"authorBefore={authorBefore} authorAfter={probe.AuthorFires} measuredW={probe.LastSeenW}(want 140)");
        }
    }
}
