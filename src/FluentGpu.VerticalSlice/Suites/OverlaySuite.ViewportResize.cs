using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

// ── An open in-window popup is re-placed when only the WINDOW resizes (Controls/OverlayHost.cs AfterAnimations) ──────
//
// OverlayHost does not re-render on resize (it reads root bounds), so its placement layout effect never re-runs; the
// live follow re-placed only on anchor drift or a popup self-resize. A flyout on a still top-left anchor therefore kept
// the translation clamped against the OLD viewport and hung past the new right edge after a restore/narrow. The pin:
// OverlayProbe's anchor sits at (20,20) and never moves; a 240-wide Raw popup opens BottomLeft in a 600-wide window
// (right edge 260), then the window narrows to 250 — the popup must be re-clamped fully inside the new viewport.
static partial class OverlaySuite
{
    static void OverlayViewportResizeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("overlay-viewport-resize", new Size2(600, 400), 1f));
        window.Show();
        var root = new OverlayProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        host.RunFrame();
        var svc = (OverlayServiceImpl)root.Service!;
        svc.Open(() => root.Anchor, () => new BoxEl { Width = 240, Height = 120 }, FlyoutPlacement.BottomLeft,
            new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw));
        for (int i = 0; i < 8; i++) host.RunFrame();
        var e = svc.Entries[0];
        var a0 = host.Scene.AbsoluteRect(root.Anchor);
        var r0 = host.Scene.AbsoluteRect(e.WrapperNode);
        bool opened = e.PopupWindowToken < 0 && r0.W > 0f && r0.X + r0.W > 250.5f;   // in-window, would overflow 250

        window.ClientSizePx = new Size2(250, 400);   // restore/narrow: nothing bumps the overlay version
        window.PaintRequested?.Invoke();
        for (int i = 0; i < 6; i++) host.RunFrame();
        var a1 = host.Scene.AbsoluteRect(root.Anchor);
        var r1 = host.Scene.AbsoluteRect(e.WrapperNode);
        bool anchorStill = MathF.Abs(a1.X - a0.X) < 0.5f && MathF.Abs(a1.Y - a0.Y) < 0.5f;
        bool inside = r1.X >= -0.5f && r1.X + r1.W <= 250.5f && r1.Y + r1.H <= 400.5f;
        bool stillOpen = svc.Entries.Count == 1 && e.Phase != OverlayPhase.Closing;

        Check("gate.overlay.viewport-resize-replaces an open in-window popup over a still anchor is re-clamped into the viewport when only the window narrows (the follow re-placed only on anchor drift or popup self-resize)",
            opened && anchorStill && inside && stillOpen,
            $"opened={opened} anchorStill={anchorStill} inside={inside} open={stillOpen} anchor=({a1.X:0.#},{a1.Y:0.#}) "
            + $"before=({r0.X:0.#},{r0.Y:0.#},{r0.W:0.#}x{r0.H:0.#}) after=({r1.X:0.#},{r1.Y:0.#},{r1.W:0.#}x{r1.H:0.#})");
    }
}
