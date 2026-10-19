using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// <c>AppHost.SizeToContent</c> (app-authoring-ergonomics plan §3): after a frame whose layout ran, the window's client
/// area follows the content's natural size at the effective scale (zoom included), clamped to the min size and the work
/// area; Grow-only content leaves the window alone; the user resizing the frame flips it to Manual; a relayout that does
/// not change the natural size issues no resize; a ping-pong latches; a show-pending window shows after its first fit.
/// </summary>
static class SizeToContentSuite
{
    // A column root holding one fixed-size box (the content-sized case). The inner child's height is bound to a signal so a
    // gate can force a relayout that leaves the natural size alone.
    sealed class Fixed(float w, float h, Signal<float>? inner = null) : Component
    {
        public override Element Render()
        {
            Element[] kids = inner is null ? Array.Empty<Element>() : new Element[] { new BoxEl { Width = 10f, Height = Prop.Of(() => inner.Value) } };
            return new BoxEl
            {
                Direction = 1,
                Children = [new BoxEl { Width = w, Height = h, Fill = ColorF.FromRgba(40, 120, 200), Children = kids }],
            };
        }
    }

    // Content that only fills (Grow): its natural size is 0 on both axes ⇒ the window is left alone.
    sealed class GrowOnly : Component
    {
        public override Element Render() => new BoxEl { Direction = 1, Children = [new BoxEl { Grow = 1f, Fill = ColorF.FromRgba(200, 40, 40) }] };
    }

    // Content whose natural width depends on the window it is in: 300 when the viewport is ≥ 400 DIP, 500 below it — a
    // fit to 300 makes it 500, a fit to 500 makes it 300 (A→B→A).
    sealed class PingPong : Component
    {
        public override Element Render()
        {
            float vw = UseContext(Viewport.Size).Width;
            return new BoxEl { Direction = 1, Children = [new BoxEl { Width = vw >= 400f ? 300f : 500f, Height = 100f }] };
        }
    }

    // Content whose width follows a signal (an app toggling its content A→B→A on purpose).
    sealed class Toggle(Signal<float> w) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children = [new BoxEl { Width = Prop.Of(() => w.Value), Height = 100f }],
        };
    }

    const string ParagraphHead = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron ";
    const string ParagraphLink = "pi rho sigma";

    // A wrapping paragraph with a hyperlink span (it publishes span artifacts) next to a box whose height a signal moves (a
    // relayout that does not touch the text).
    sealed class LinkParagraph(Signal<float> spacer) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                new SpanTextEl(new TextSpan[] { new(ParagraphHead), new(ParagraphLink, IsLink: true) }) { Size = 14f },
                new BoxEl { Width = 10f, Height = Prop.Of(() => spacer.Value) },
            ],
        };
    }

    public static void Run(StringTable strings)
    {
        FitsBothAxes(strings);
        HeightKeepsWidth(strings);
        ZoomUsesEffectiveScale(strings);
        GrowOnlyLeavesWindowAlone(strings);
        UserResizeFlipsToManual(strings);
        NoOpRelayoutIssuesNoResize(strings);
        ClampsToMinAndWorkArea(strings);
        PingPongLatches(strings);
        ShowsAfterFirstFit(strings);
        OsClampShowsAndSettles(strings);
        ProbeLeavesTextStateAlone(strings);
        ExplicitVisibilityWins(strings);
        ContentToggleDoesNotLatch(strings);
    }

    static (HeadlessPlatformApp app, HeadlessWindow window, AppHost host) Make(StringTable strings, Component root,
        Size2 sizePx, float zoom = 1f, Size2 minDip = default, bool show = true)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("size-to-content", sizePx, 1f, MinClientSizeDip: minDip, Zoom: zoom));
        if (show) window.Show();
        else window.IsVisible = false;
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        return (app, window, host);
    }

    static void Settle(AppHost host, int frames = 6) { for (int i = 0; i < frames; i++) host.RunFrame(); }

    // ── gate.size-to-content.fits-both-axes ──────────────────────────────────────────────────────────────────────
    static void FitsBothAxes(StringTable strings)
    {
        var (app, window, host) = Make(strings, new Fixed(300f, 200f), new Size2(480, 320));
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.WidthAndHeight;
            Settle(host);
            Check("gate.size-to-content.fits-both-axes a content-sized root resizes the window to its natural size",
                window.ClientSizePx == new Size2(300, 200) && host.SizeToContentResizes == 1,
                $"size={window.ClientSizePx} resizes={host.SizeToContentResizes}");
        }
    }

    // ── gate.size-to-content.height-keeps-width ──────────────────────────────────────────────────────────────────
    static void HeightKeepsWidth(StringTable strings)
    {
        var (app, window, host) = Make(strings, new Fixed(200f, 150f), new Size2(480, 320));
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.Height;
            Settle(host);
            Check("gate.size-to-content.height-keeps-width Height fits the height and keeps the width",
                window.ClientSizePx == new Size2(480, 150), $"size={window.ClientSizePx}");
        }
    }

    // ── gate.size-to-content.zoom-effective-scale ────────────────────────────────────────────────────────────────
    static void ZoomUsesEffectiveScale(StringTable strings)
    {
        // 201 × 1.25 = 251.25 → 252 px; 101 × 1.25 = 126.25 → 127 px (ceil, zoom included: the layout then gets ≥ 201×101 DIP).
        var (app, window, host) = Make(strings, new Fixed(201f, 101f), new Size2(600, 400), zoom: 1.25f);
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.WidthAndHeight;
            Settle(host);
            Check("gate.size-to-content.zoom-effective-scale at zoom 1.25 px = ceil(dip × effective scale)",
                window.Scale == 1.25f && window.ClientSizePx == new Size2(252, 127), $"scale={window.Scale} size={window.ClientSizePx}");
        }
    }

    // ── gate.size-to-content.grow-only-left-alone ────────────────────────────────────────────────────────────────
    static void GrowOnlyLeavesWindowAlone(StringTable strings)
    {
        var (app, window, host) = Make(strings, new GrowOnly(), new Size2(480, 320));
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.WidthAndHeight;
            Settle(host);
            Check("gate.size-to-content.grow-only-left-alone content that only fills (natural size 0) leaves the window alone",
                window.ClientSizePx == new Size2(480, 320) && window.SetClientSizeCount == 0,
                $"size={window.ClientSizePx} calls={window.SetClientSizeCount}");
        }
    }

    // ── gate.size-to-content.user-resize-manual ──────────────────────────────────────────────────────────────────
    static void UserResizeFlipsToManual(StringTable strings)
    {
        var (app, window, host) = Make(strings, new Fixed(300f, 200f), new Size2(480, 320));
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.WidthAndHeight;
            Settle(host);
            int before = window.SetClientSizeCount;
            window.SimulateUserResize(new Size2(420, 360));
            Settle(host);
            Check("gate.size-to-content.user-resize-manual the user resizing the frame switches the mode to Manual and keeps their size",
                host.SizeToContent == SizeToContent.Manual && window.ClientSizePx == new Size2(420, 360)
                && window.SetClientSizeCount == before,
                $"mode={host.SizeToContent} size={window.ClientSizePx} calls={window.SetClientSizeCount - before}");
            host.SizeToContent = SizeToContent.WidthAndHeight;   // re-enabled by code: fits again
            Settle(host);
            Check("gate.size-to-content.user-resize-manual re-enabling the mode fits the window again",
                window.ClientSizePx == new Size2(300, 200), $"size={window.ClientSizePx}");
        }
    }

    // ── gate.size-to-content.no-op-no-resize ─────────────────────────────────────────────────────────────────────
    static void NoOpRelayoutIssuesNoResize(StringTable strings)
    {
        var inner = new Signal<float>(10f);
        var (app, window, host) = Make(strings, new Fixed(300f, 200f, inner), new Size2(480, 320));
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.WidthAndHeight;
            Settle(host);
            int before = window.SetClientSizeCount;
            for (int i = 0; i < 6; i++) { inner.Value = i % 2 == 0 ? 20f : 30f; host.RunFrame(); }
            Settle(host, 4);
            Check("gate.size-to-content.no-op-no-resize relayouts that keep the natural size issue no resize",
                window.SetClientSizeCount == before && window.ClientSizePx == new Size2(300, 200),
                $"calls={window.SetClientSizeCount - before} size={window.ClientSizePx}");
        }
    }

    // ── gate.size-to-content.clamps ──────────────────────────────────────────────────────────────────────────────
    static void ClampsToMinAndWorkArea(StringTable strings)
    {
        var (app, window, host) = Make(strings, new Fixed(100f, 100f), new Size2(480, 320), minDip: new Size2(250f, 180f));
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.WidthAndHeight;
            Settle(host);
            Check("gate.size-to-content.clamps the target never goes below the minimum client size",
                window.ClientSizePx == new Size2(250, 180), $"size={window.ClientSizePx}");
        }
        var (app2, window2, host2) = Make(strings, new Fixed(1200f, 900f), new Size2(480, 320));
        using (app2) using (host2)
        {
            app2.WorkArea = new RectF(0f, 0f, 800f, 600f);
            host2.SizeToContent = SizeToContent.WidthAndHeight;
            Settle(host2);
            Check("gate.size-to-content.clamps the target never exceeds the monitor work area",
                window2.ClientSizePx == new Size2(800, 600), $"size={window2.ClientSizePx}");
        }
    }

    // ── gate.size-to-content.ping-pong-latch ─────────────────────────────────────────────────────────────────────
    static void PingPongLatches(StringTable strings)
    {
        var (app, window, host) = Make(strings, new PingPong(), new Size2(480, 320));
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.Width;
            Settle(host, 16);
            int calls = window.SetClientSizeCount;
            Settle(host, 8);
            Check("gate.size-to-content.ping-pong-latch content that flips with the window size latches instead of resizing every frame",
                host.SizeToContentLatched && calls <= 3 && window.SetClientSizeCount == calls,
                $"latched={host.SizeToContentLatched} calls={calls}→{window.SetClientSizeCount} size={window.ClientSizePx}");
        }
    }

    // ── gate.size-to-content.show-after-fit ──────────────────────────────────────────────────────────────────────
    static void ShowsAfterFirstFit(StringTable strings)
    {
        var (app, window, host) = Make(strings, new Fixed(300f, 200f), new Size2(480, 320), show: false);
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.WidthAndHeight;
            host.ShowWhenFitted();
            host.RunFrame();
            bool hiddenWhileFitting = !window.Shown && window.ClientSizePx == new Size2(300, 200);
            Settle(host);
            Check("gate.size-to-content.show-after-fit a show-pending window is resized while hidden and shown once, at the fitted size",
                hiddenWhileFitting && window.Shown && window.ShowCalls == 1 && window.ClientSizePx == new Size2(300, 200),
                $"hiddenWhileFitting={hiddenWhileFitting} shown={window.Shown} calls={window.ShowCalls} size={window.ClientSizePx}");
        }
    }

    // ── gate.size-to-content.os-clamp ────────────────────────────────────────────────────────────────────────────
    static void OsClampShowsAndSettles(StringTable strings)
    {
        // The "OS" will not go below 400×300 (USER32's minimum track size): the 300×200 target can never be reached.
        var inner = new Signal<float>(10f);
        var (app, window, host) = Make(strings, new Fixed(300f, 200f, inner), new Size2(480, 320), show: false);
        using (app) using (host)
        {
            window.MinTrackSizePx = new Size2(400, 300);
            host.SizeToContent = SizeToContent.WidthAndHeight;
            host.ShowWhenFitted();
            Settle(host, 12);
            int calls = window.SetClientSizeCount;
            Check("gate.size-to-content.os-clamp a clamped resize still shows the window, once, at the size the OS gave it",
                window.Shown && window.ShowCalls == 1 && !host.SizeToContentShowPending && window.ClientSizePx == new Size2(400, 300),
                $"shown={window.Shown} showCalls={window.ShowCalls} pending={host.SizeToContentShowPending} size={window.ClientSizePx}");
            for (int i = 0; i < 6; i++) { inner.Value = i % 2 == 0 ? 20f : 30f; host.RunFrame(); }
            Settle(host, 4);
            Check("gate.size-to-content.os-clamp the clamped target is settled: laid-out frames on the visible window re-issue nothing",
                calls == 1 && window.SetClientSizeCount == calls && host.SizeToContentResizes == 1,
                $"calls={calls}→{window.SetClientSizeCount} resizes={host.SizeToContentResizes}");
        }
    }

    // ── gate.size-to-content.probe-pure ──────────────────────────────────────────────────────────────────────────
    static void ProbeLeavesTextStateAlone(StringTable strings)
    {
        // A 200 px work area holds the window narrower than the paragraph's natural width, so the laid paragraph wraps at a
        // finite width while every probe measures it unconstrained (+Inf). The probe must neither store that +Inf measure
        // into the node's TextMeasureCache nor republish the link's span artifacts at it.
        var spacer = new Signal<float>(10f);
        var (app, window, host) = Make(strings, new LinkParagraph(spacer), new Size2(480, 320));
        using (app) using (host)
        {
            app.WorkArea = new RectF(0f, 0f, 200f, 600f);
            host.SizeToContent = SizeToContent.Width;
            Settle(host);
            for (int i = 0; i < 4; i++) { spacer.Value = i % 2 == 0 ? 20f : 30f; host.RunFrame(); }
            var node = FluentGpu.VerticalSlice.Harness.Asserts.FindTextNode(host.Scene, strings, host.Scene.Root, ParagraphHead + ParagraphLink);
            bool found = !node.IsNull;
            FluentGpu.Scene.TextMeasureCache mc = found ? host.Scene.MeasureCacheRef(node) : default;
            bool cacheFinite = found && mc.Valid
                && (!mc.E0.Valid || !float.IsInfinity(mc.E0.MaxW)) && (!mc.E1.Valid || !float.IsInfinity(mc.E1.MaxW));
            var rects = found ? SpanRunTable.Shared.Resolve(host.Scene.Layout(node).TextStyle.SpanRunId)?.Rects : null;
            bool artifactsLaid = rects is not null && !float.IsInfinity(rects.MaxWidth) && rects.MaxWidth <= 200f;
            Check("gate.size-to-content.probe-pure the natural probe leaves the text measure cache and the span artifacts at the laid width",
                window.ClientSizePx.Width == 200f && cacheFinite && artifactsLaid,
                $"found={found} size={window.ClientSizePx} e0={(mc.E0.Valid ? mc.E0.MaxW : float.NaN)} e1={(mc.E1.Valid ? mc.E1.MaxW : float.NaN)} " +
                $"artifactsMaxW={(rects is null ? float.NaN : rects.MaxWidth)}");
        }
    }

    // ── gate.size-to-content.explicit-visibility ─────────────────────────────────────────────────────────────────
    static void ExplicitVisibilityWins(StringTable strings)
    {
        // The app hides its window during mount (FluentApp.SetWindowVisible(false) → CancelShowWhenFitted) after the show
        // was armed: the fit still happens, the window stays hidden — the safety net included.
        var (app, window, host) = Make(strings, new Fixed(300f, 200f), new Size2(480, 320), show: false);
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.WidthAndHeight;
            host.ShowWhenFitted();
            host.CancelShowWhenFitted();
            Settle(host, 40);
            Check("gate.size-to-content.explicit-visibility an explicit hide cancels the pending show-when-fitted",
                !window.Shown && window.ShowCalls == 0 && !host.SizeToContentShowPending,
                $"shown={window.Shown} showCalls={window.ShowCalls} pending={host.SizeToContentShowPending}");
        }
        // …and one made before the show was armed wins too: ShowWhenFitted is then a no-op.
        var (app2, window2, host2) = Make(strings, new Fixed(300f, 200f), new Size2(480, 320), show: false);
        using (app2) using (host2)
        {
            host2.SizeToContent = SizeToContent.WidthAndHeight;
            host2.CancelShowWhenFitted();
            host2.ShowWhenFitted();
            Settle(host2, 40);
            Check("gate.size-to-content.explicit-visibility an explicit visibility call before the show is armed makes ShowWhenFitted a no-op",
                !window2.Shown && window2.ShowCalls == 0 && !host2.SizeToContentShowPending,
                $"shown={window2.Shown} showCalls={window2.ShowCalls} pending={host2.SizeToContentShowPending}");
        }
    }

    // ── gate.size-to-content.toggle-no-latch ─────────────────────────────────────────────────────────────────────
    static void ContentToggleDoesNotLatch(StringTable strings)
    {
        // The app toggles its CONTENT 500 → 300 → 500 inside the oscillation window (an A→B→A of targets), each change
        // followed by the frame that settles it (the real shape of a click / data change). That is one ping-pong per toggle,
        // never two in a row: the window must follow back to 500, not latch at 300. (Back-to-back flips with no settled frame
        // between them are indistinguishable from a window-driven oscillation and latch by design — ping-pong-latch.)
        var w = new Signal<float>(300f);
        var (app, window, host) = Make(strings, new Toggle(w), new Size2(480, 320));
        using (app) using (host)
        {
            host.SizeToContent = SizeToContent.Width;
            Settle(host);
            bool settled = window.ClientSizePx.Width == 300f;
            w.Value = 500f; host.RunFrame(); host.RunFrame();
            bool atA = window.ClientSizePx.Width == 500f;
            w.Value = 300f; host.RunFrame(); host.RunFrame();
            bool atB = window.ClientSizePx.Width == 300f;
            w.Value = 500f; host.RunFrame();
            Settle(host, 4);
            Check("gate.size-to-content.toggle-no-latch a content toggle A→B→A is followed, not latched",
                settled && atA && atB && window.ClientSizePx.Width == 500f && !host.SizeToContentLatched,
                $"settled={settled} atA={atA} atB={atB} size={window.ClientSizePx} latched={host.SizeToContentLatched}");
        }
    }
}
