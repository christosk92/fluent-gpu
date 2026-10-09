using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── An open popup whose OWN content changes size is re-placed (Controls/OverlayHost.cs AfterAnimations) ─────────────
//
// The live-anchor follow re-placed a popup only when its anchor rect drifted, and always against the size measured at
// open. Content that re-renders while open (Wavee's device picker re-keying its rows, a suggestion list filtering per
// keystroke) re-solves the wrapper without re-rendering OverlayHost, so an upward popup kept its old top-left: grown,
// it hung down over its anchor; shrunk, it floated above it. The pin: a Raw popup opened TopLeft over an anchor docked
// at the window's bottom edge, whose body height is a signal — grow it by 100 and shrink it below the open size, and
// the popup's bottom edge stays the same gap above the anchor with MeasuredH/PlacementInfo tracking the new height.
static partial class OverlaySuite
{
    sealed class ContentResizeProbe : Component
    {
        public IOverlayService? Service;
        public NodeHandle Anchor;
        public readonly Signal<float> BodyHeight = new(60f);
        public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new ContentResizeProbeInner(this)) });
    }

    sealed class ContentResizeProbeInner(ContentResizeProbe p) : Component
    {
        readonly ContentResizeProbe _p = p;
        public override Element Render()
        {
            _p.Service = UseContext(Overlay.Service);
            return new BoxEl
            {
                Width = 240, Grow = 1, Direction = 1, Justify = FlexJustify.End, Padding = Edges4.All(8),
                Children = [new BoxEl { Width = 120, Height = 32, OnRealized = h => _p.Anchor = h }],
            };
        }
    }

    // The popup body: re-renders on its own signal, never through OverlayHost.
    sealed class ContentResizeBody(Signal<float> height) : Component
    {
        readonly Signal<float> _height = height;
        public override Element Render() => new BoxEl { Width = 120, Height = _height.Value };
    }

    static void OverlayContentResizeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("overlay-content-resize", new Size2(360, 400), 1f));
        window.Show();
        var root = new ContentResizeProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        host.RunFrame();
        var svc = (OverlayServiceImpl)root.Service!;
        svc.Open(() => root.Anchor, () => Embed.Comp(() => new ContentResizeBody(root.BodyHeight)), FlyoutPlacement.TopLeft,
            new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw));
        for (int i = 0; i < 8; i++) host.RunFrame();
        var e = svc.Entries[0];
        var a = host.Scene.AbsoluteRect(root.Anchor);
        var r0 = host.Scene.AbsoluteRect(e.WrapperNode);
        float gap0 = a.Y - (r0.Y + r0.H);
        bool opened = e.OpensUp && gap0 > -0.5f;

        root.BodyHeight.Value = 160f;   // the roster gained rows while open
        for (int i = 0; i < 4; i++) host.RunFrame();
        var r1 = host.Scene.AbsoluteRect(e.WrapperNode);
        float gap1 = a.Y - (r1.Y + r1.H);
        bool grew = Near(r1.H - r0.H, 100f) && Near(gap1, gap0) && e.OpensUp
                    && Near(e.MeasuredH, r1.H) && Near(e.PlacementInfo.Peek().PopupHeight, r1.H);

        root.BodyHeight.Value = 30f;    // and lost them again
        for (int i = 0; i < 4; i++) host.RunFrame();
        var r2 = host.Scene.AbsoluteRect(e.WrapperNode);
        float gap2 = a.Y - (r2.Y + r2.H);
        bool shrank = Near(r0.H - r2.H, 30f) && Near(gap2, gap0) && Near(e.MeasuredH, r2.H);

        Check("gate.overlay.content-resize-replaces an open upward popup whose own content grows/shrinks is re-placed and stays bottom-glued to its anchor (the follow re-placed only on anchor drift, against the size measured at open)",
            opened && grew && shrank,
            $"opened={opened} grew={grew} shrank={shrank} anchorY={a.Y:0.#} h0={r0.H:0.#} gap0={gap0:0.#} "
            + $"h1={r1.H:0.#} gap1={gap1:0.#} h2={r2.H:0.#} gap2={gap2:0.#} measuredH={e.MeasuredH:0.#}");
    }
}
