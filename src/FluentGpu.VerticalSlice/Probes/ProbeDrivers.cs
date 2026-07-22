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



static class ProbeDrivers
{
public static int RangedTooltipFreezeProbe()
{
    int Variant(string name, bool tooltipEnabled, bool overThumb, bool stillPhase, int moves = 60, int still = 90)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("tooltip-probe", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings,
            new RangedTooltipProbeRoot { TooltipEnabled = tooltipEnabled });
        host.RunFrame();

        var sliders = Roles(host.Scene, AutomationRole.Slider);
        if (sliders.Count == 0) { Console.WriteLine($"{name}: FAIL no slider node"); return 2; }
        var tr = host.Scene.AbsoluteRect(sliders[0]);
        float hx = overThumb ? tr.X + 11f : tr.X + 120f;   // ring center at value 0 vs mid-track (no thumb)
        float hy = tr.Y + tr.H * 0.5f;

        int totalComps = 0, renderedFrames = 0;
        for (int i = 0; i < moves; i++)
        {
            float jx = (i & 1) == 0 ? 0.6f : -0.6f;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(hx + jx, hy), 0, 0, TimestampMs: (uint)(i * 16)));
            var st = host.RunFrame();
            totalComps += st.ComponentsRendered;
            if (st.Rendered) renderedFrames++;
        }
        Console.WriteLine($"{name}: {moves} moves → renderedFrames={renderedFrames} totalComps={totalComps} active={host.HasActiveWork}");

        if (stillPhase)
        {
            for (int i = 0; i < still; i++)
            {
                var st = host.RunFrame();
                if (i % 15 == 0 || st.ComponentsRendered > 0)
                    Console.WriteLine($"  still {i,3}: rendered={st.Rendered} comps={st.ComponentsRendered} active={host.HasActiveWork} bubbleGlyphs={device.LastGlyphs.Count}");
            }
        }
        return 0;
    }

    // V4 — press opens the tooltip immediately; the drag scrubs the value and the bubble must FOLLOW the thumb
    // (the live-anchor follow in OverlayHost.AfterAnimations; WinUI's disambiguation UI tracks the scrub).
    int DragFollow()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("tooltip-probe", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings,
            new RangedTooltipProbeRoot { TooltipEnabled = true });
        host.RunFrame();
        var sliders = Roles(host.Scene, AutomationRole.Slider);
        if (sliders.Count == 0) { Console.WriteLine("V4: FAIL no slider node"); return 2; }
        var tr = host.Scene.AbsoluteRect(sliders[0]);
        var p0 = new Point2(tr.X + 11f, tr.Y + tr.H * 0.5f);

        window.QueueInput(new InputEvent(InputKind.PointerDown, p0, 0, 0));
        for (int i = 0; i < 3; i++) host.RunFrame();   // press → tip opens immediately; placement on the next layout pass
        // The press SCRUBS to the press point (WinUI press-to-set): local x=11 on a 0..200/200px track ⇒ value 11.
        var t0 = FindTextNode(host.Scene, strings, host.Scene.Root, "11");
        float x0 = t0.IsNull ? float.NaN : host.Scene.AbsoluteRect(t0).X;

        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(tr.X + 150f, p0.Y), 0, 0));
        for (int i = 0; i < 3; i++) host.RunFrame();   // drag → value 150 → thumb relayouts → follow re-places
        var t1 = FindTextNode(host.Scene, strings, host.Scene.Root, "150");
        float x1 = t1.IsNull ? float.NaN : host.Scene.AbsoluteRect(t1).X;
        Console.WriteLine($"V4 drag-follow: bubble@0 x={x0:0.#} → bubble@150 x={x1:0.#} (Δ={x1 - x0:0.#}, expect ≈139) follow={(x1 - x0 > 100f ? "OK" : "BROKEN")}");

        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(tr.X + 150f, p0.Y), 0, 0));
        for (int i = 0; i < 30; i++) host.RunFrame();  // release → CloseTip (167ms Raw fade → finalize)
        bool closed = FindTextNode(host.Scene, strings, host.Scene.Root, "150").IsNull;
        Console.WriteLine($"V4 release: bubble closed={closed}");
        return 0;
    }

    // Short diagnostic pass first: per-component render trace over 8 moves + 12 still frames.
    Diag.Enabled = true;
    Diag.Sink = s => Console.WriteLine("  diag " + s);
    Variant("V1d tooltip ON @thumb (diag, short)", tooltipEnabled: true, overThumb: true, stillPhase: true, moves: 8, still: 12);
    Diag.Enabled = false;
    Diag.Sink = null;

    Variant("V1 tooltip ON  @thumb", tooltipEnabled: true, overThumb: true, stillPhase: true);
    Variant("V2 tooltip OFF @thumb", tooltipEnabled: false, overThumb: true, stillPhase: false);
    Variant("V3 tooltip ON  @track", tooltipEnabled: true, overThumb: false, stillPhase: false);
    DragFollow();
    Console.WriteLine("PROBE COMPLETE");
    return 0;
}

public static int TitleBarResizeProbe()
{
    var strings = new StringTable();
    using var app = new HeadlessPlatformApp();
    var window = new HeadlessWindow(new WindowDesc("titlebar-probe", new Size2(1900, 300), 1f));
    window.Show();
    var device = new HeadlessGpuDevice();
    var fonts = new HeadlessFontSystem(strings);
    using var host = new AppHost(app, window, device, fonts, strings, new TitleBarResizeProbeRoot());

    int fail = 0;
    void P(string name, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "  ok " : "FAIL ")}{name}  ({detail})");
        if (!ok) fail++;
    }
    void Settle(int frames = 6) { for (int i = 0; i < frames; i++) host.RunFrame(); }
    // The Win32 backend fires PaintRequested from WM_SIZE; headless, a bare ClientSizePx write doesn't wake the
    // (idle-capable) host — fire the same edge, then settle (resize layout + the measured-width feedback frame).
    void Resize(int w, int h)
    {
        window.ClientSizePx = new Size2(w, h);
        window.PaintRequested?.Invoke();
        Settle();
    }

    // The search field root carries Role=ComboBox (unique in this tree); the close button is the LAST Button
    // in tree order (pane, [query icon], min, max, close — with or without the search present).
    RectF SearchRect() { var n = FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox); return n.IsNull ? default : host.Scene.AbsoluteRect(n); }
    bool SearchExists() => !FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox).IsNull;
    RectF CloseRect() { var b = Roles(host.Scene, AutomationRole.Button); return b.Count == 0 ? default : host.Scene.AbsoluteRect(b[^1]); }

    // Wide: the box rests at its 580 max; close is flush right.
    Settle();
    var s0 = SearchRect(); var c0 = CloseRect();
    P("wide(1900): search at its 580 max", Near(s0.W, 580f, 1.5f), $"searchW={s0.W:0.#}");
    P("wide(1900): close flush right", Near(c0.X + c0.W, 1900f, 1.5f), $"closeRight={c0.X + c0.W:0.#}");

    // THE BUG: resize down to the reported width. avail≈480 > 140 ⇒ no collapse — the box must SHRINK in place.
    Resize(887, 300);
    var s1 = SearchRect(); var c1 = CloseRect();
    P("narrow(887): close stays flush right (was: pushed out + clipped)", Near(c1.X + c1.W, 887f, 1.5f), $"closeRight={c1.X + c1.W:0.#}");
    P("narrow(887): search gave way below 580 (the symptom-patch discriminator)", SearchExists() && s1.W < 560f && s1.W > 300f, $"searchW={s1.W:0.#}");

    // Steady state after the resize settles: the paint half stays zero-alloc (the width signals write on the
    // resize edge only, never per-frame).
    var steady = host.RunFrame();
    P("narrow(887): steady frame zero-alloc on the hot half", steady.HotPhaseAllocBytes == 0, $"{steady.HotPhaseAllocBytes}B");

    // Back wide: the box re-expands to its max; close follows the edge.
    Resize(1900, 300);
    var s2 = SearchRect(); var c2 = CloseRect();
    P("re-wide(1900): search back at 580", Near(s2.W, 580f, 1.5f), $"searchW={s2.W:0.#}");
    P("re-wide(1900): close flush right", Near(c2.X + c2.W, 1900f, 1.5f), $"closeRight={c2.X + c2.W:0.#}");

    // Collapse: below the 140 floor the Content lambda swaps the box out entirely; the captions still fit.
    Resize(520, 300);
    var c3 = CloseRect();
    P("collapse(520): search collapsed below the 140 floor", !SearchExists(), "avail<140 path");
    P("collapse(520): close still flush right", Near(c3.X + c3.W, 520f, 1.5f), $"closeRight={c3.X + c3.W:0.#}");

    // Re-expand from the collapsed state (the remount path).
    Resize(1900, 300);
    var s4 = SearchRect(); var c4 = CloseRect();
    P("re-expand(1900): search remounted at 580", SearchExists() && Near(s4.W, 580f, 1.5f), $"searchW={s4.W:0.#}");
    P("re-expand(1900): close flush right", Near(c4.X + c4.W, 1900f, 1.5f), $"closeRight={c4.X + c4.W:0.#}");

    Console.WriteLine(fail == 0 ? "TITLEBAR-RESIZE PROBE PASS" : $"TITLEBAR-RESIZE PROBE: {fail} FAILURES");
    return fail == 0 ? 0 : 2;
}

public static int ScrollFlickerProbe()
{
    const float Row = 40f;
    var strings = new StringTable();
    using var app = new HeadlessPlatformApp();
    var window = new HeadlessWindow(new WindowDesc("scroll-flicker", new Size2(640, 480), 1f));
    window.Show();
    var device = new HeadlessGpuDevice();
    var fonts = new HeadlessFontSystem(strings);
    using var host = new AppHost(app, window, device, fonts, strings, new VirtualProbe());
    host.SmoothScroll = true;   // engage the ScrollIntegrator (TargetChase ease) — the hypothesis' path.

    host.RunFrame();
    var vp = host.Scene.Root;
    host.Scene.TryGetScroll(vp, out var sc0);
    var content = sc0.ContentNode;
    Console.WriteLine($"mount: viewportH={sc0.ViewportH:0} contentH={sc0.ContentH:0} overscan={sc0.Overscan} " +
                      $"guardRows={Math.Max(1, sc0.Overscan / 2)} realized=[{sc0.FirstRealized},{sc0.LastRealized})");

    var ptr = new Point2(150, 200);
    int uncovered = 0, frames = 0;
    float worstGap = 0f;

    // One big wheel notch to set a far target, then let the ease run frame-by-frame (this is the fast-fling travel).
    window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 4000f));
    for (int f = 0; f < 120; f++)
    {
        host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc);
        // The offset the RECORDER drew this frame = the content child's applied -translation (post-Tick).
        float drawnOffset = -host.Scene.Paint(content).LocalTransform.Dy;
        float realizedTop = sc.FirstRealized * Row;
        float realizedBot = sc.LastRealized * Row;
        float viewTop = drawnOffset;
        float viewBot = drawnOffset + sc.ViewportH;
        // Gap at the LEADING (bottom) edge: drawn viewport extends past the realized band.
        float leadGap = viewBot - realizedBot;
        float trailGap = realizedTop - viewTop;   // gap at the TRAILING (top) edge
        bool covered = realizedTop <= viewTop + 0.01f && realizedBot >= viewBot - 0.01f;
        if (!covered)
        {
            uncovered++;
            float gap = MathF.Max(leadGap, trailGap);
            if (gap > worstGap) worstGap = gap;
            if (uncovered <= 12)
                Console.WriteLine($"  frame {f}: UNCOVERED drawnOffset={drawnOffset:0.#} " +
                    $"pending={sc.PendingTargetY:0.#} drawnView=[{viewTop:0.#},{viewBot:0.#}) " +
                    $"realized=[{realizedTop:0.#},{realizedBot:0.#}) leadGap={leadGap:0.#} trailGap={trailGap:0.#}");
        }
        frames++;
        if (sc.Phase == ScrollIntegrator.Idle && sc.OffsetY > 0f) break;   // WheelAnimating chase settled
    }

    Console.WriteLine($"\nSMOOTH ease over one 4000px notch: {uncovered}/{frames} frames had the drawn viewport NOT " +
                      $"fully covered by realized rows; worst leading/trailing gap = {worstGap:0.#}px");

    // Now the harsher case: a sustained fast wheel STORM (many big notches back-to-back) — the per-frame travel is
    // largest here, the regime the report describes as flickering.
    int uncovered2 = 0, frames2 = 0; float worstGap2 = 0f; float maxStep = 0f; float prevOff = -1f;
    for (int s = 0; s < 80; s++)
    {
        window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 6000f));
        host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc);
        float drawnOffset = -host.Scene.Paint(content).LocalTransform.Dy;
        if (prevOff >= 0f) maxStep = MathF.Max(maxStep, MathF.Abs(drawnOffset - prevOff));
        prevOff = drawnOffset;
        float realizedTop = sc.FirstRealized * Row, realizedBot = sc.LastRealized * Row;
        float viewBot = drawnOffset + sc.ViewportH;
        bool covered = realizedTop <= drawnOffset + 0.01f && realizedBot >= viewBot - 0.01f;
        if (!covered)
        {
            uncovered2++;
            worstGap2 = MathF.Max(worstGap2, MathF.Max(viewBot - realizedBot, realizedTop - drawnOffset));
        }
        frames2++;
    }
    Console.WriteLine($"FAST WHEEL STORM (80 × 6000px notches): {uncovered2}/{frames2} frames uncovered; " +
                      $"worst gap = {worstGap2:0.#}px; max single-frame drawn-offset step = {maxStep:0.#}px");

    bool reproduced = uncovered > 0 || uncovered2 > 0;
    Console.WriteLine(reproduced
        ? "\nSCROLL-FLICKER PROBE: REPRODUCED — realized rows trail the drawn offset on >=1 frame."
        : "\nSCROLL-FLICKER PROBE: NOT REPRODUCED — realized band covered the drawn viewport every frame.");
    return 0;
}
}
