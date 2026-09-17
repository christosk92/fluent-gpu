using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;

// Scroll pacing / wheel-distance evidence (the 2026-09 scroll fix, S6 "Windows-like notch distance"): the per-notch
// distance is WheelScrollLines × the scroller's declared line height when it has one (ScrollState.LineDip — the platform
// already folded WheelScrollLines/3 into the notch, so the router's factor is the Windows baseline 3), else the WinUI
// viewport rule max(48 DIP, 10 %·viewport). Both branches go through the ONE scale, ScrollFeel.PerNotchDip(viewport,
// lineDip), which ScrollInputRouter.WheelAxis (detented notch) and the hi-res notch-unit phase path share. Headless: the
// notch is injected through the same InputKind.Wheel event the Win32 producer emits, the kernel glides it (Driven|Wheel),
// and the settled offset is the distance. Registered under the `scroll` tag (SuiteRegistry: "scroll-pacing").
static class ScrollPacingChecks
{
    const float RowH = 40f;        // the tracklist row height the user decision was sized on: one notch = 3 rows = 120 DIP
    const float ListViewportH = 400f;
    const float PlainViewportH = 600f;   // 10 %·600 = 60 > the 48-DIP floor, so the viewport term is what is being pinned

    /// <summary>A virtual list whose rows are <see cref="RowH"/> tall; the gate declares <c>LineDip = RowH</c> on its
    /// viewport (the same value the virtualizer publishes for a Virtual list), so one notch must travel 3 × RowH.</summary>
    sealed class LineListProbe : Component
    {
        public override Element Render()
            => Virtual.ListBound(200, RowH, idx => new BoxEl
               {
                   Height = RowH,
                   Fill = Prop.Of(() => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 30 : 50))),
               })
               with { Width = 300, Height = ListViewportH };
    }

    /// <summary>A plain (non-virtual) scroller with NO line hint (<c>LineDip = 0</c>): keeps the viewport rule.</summary>
    sealed class PlainScrollerProbe : Component
    {
        public override Element Render()
        {
            var rows = new Element[80];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl { Width = 300, Height = 50f, Fill = ColorF.FromRgba(40, (byte)(i % 2 == 0 ? 40 : 70), 40) };
            return ScrollView(new BoxEl { Direction = 1, Children = rows }) with { Width = 300, Height = PlainViewportH };
        }
    }

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // ── gate.scroll.wheel-line-dip ───────────────────────────────────────────────────────────────────────────
        float lineTravel = NotchTravel(strings, fonts, new LineListProbe(), ListViewportH, lineDip: RowH,
            out float lineDipRead, out bool lineSettled, out long lineUrgent, out long lineDeclines);
        float plainTravel = NotchTravel(strings, fonts, new PlainScrollerProbe(), PlainViewportH, lineDip: 0f,
            out float plainDipRead, out bool plainSettled, out long plainUrgent, out long plainDeclines);

        float wantLine = ScrollFeel.Shipping.PerNotchDip(ListViewportH, RowH);          // 3 × 40 = 120 DIP
        float wantPlain = ScrollFeel.Shipping.PerNotchDip(PlainViewportH, 0f);          // max(48, 0.10 × 600) = 60 DIP
        bool lineOk = lineSettled && lineDipRead == RowH && MathF.Abs(wantLine - 3f * RowH) < 0.001f
                      && MathF.Abs(lineTravel - wantLine) <= 1f;
        bool plainOk = plainSettled && plainDipRead == 0f
                       && MathF.Abs(wantPlain - MathF.Max(48f, 0.10f * PlainViewportH)) < 0.001f
                       && MathF.Abs(plainTravel - wantPlain) <= 1f;
        // Headless windows implement no IInputPacingSource and never decline production, so both P0 counters read 0 here —
        // this pins that the FrameStats fields exist and are wired (the live numbers are the app's scroll.frames rollup).
        bool countersOk = lineUrgent == 0 && lineDeclines == 0 && plainUrgent == 0 && plainDeclines == 0;
        Check("gate.scroll.wheel-line-dip one WheelNotch travels 3×LineDip (=120 DIP) on a list that declares LineDip=40 and max(48, 10%·viewport) (=60 DIP) on a scroller with no line hint — the one ScrollFeel.PerNotchDip(viewport, lineDip) scale; FrameStats.PacedUrgentBreaks/ProductionDeclines are wired (0 headless)",
            lineOk && plainOk && countersOk,
            $"list: travel={lineTravel:0.##} want={wantLine:0.##} lineDip={lineDipRead} settled={lineSettled}; " +
            $"plain: travel={plainTravel:0.##} want={wantPlain:0.##} lineDip={plainDipRead} settled={plainSettled}; " +
            $"urgent={lineUrgent}/{plainUrgent} declines={lineDeclines}/{plainDeclines}");
    }

    /// <summary>Mount <paramref name="root"/> headlessly, declare <paramref name="lineDip"/> on its viewport, inject ONE
    /// detented wheel notch over it and run frames until the kernel settles. Returns the main-axis travel; also reports
    /// the LineDip the router saw, whether the glide settled, and the two P0 pacing counters of the last frame.</summary>
    static float NotchTravel(StringTable strings, HeadlessFontSystem fonts, Component root, float viewportH, float lineDip,
        out float lineDipRead, out bool settled, out long urgentBreaks, out long productionDeclines)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("wheel-line-dip", new Size2(300, viewportH), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, root);
        host.RunFrame();
        NodeHandle vp = FindScrollable(host.Scene, host.Scene.Root);
        // The line hint is a plain scene column the layout/virtualizer write per field (never a whole-struct reset), so a
        // declaration here survives the wheel frames; the gate reads it back after the glide to prove the router saw it.
        host.Scene.ScrollRef(vp).LineDip = lineDip;
        host.RunFrame();
        host.Scene.TryGetScroll(vp, out var before);

        var prod = new HeadlessScrollProducer(window, host, new Point2(150f, viewportH / 2f));
        prod.WheelNotch(1f);                    // +1 notch = toward the content end (the producer already flips the raw sign)
        FrameStats last = prod.Step(16);
        settled = false;
        for (int i = 0; i < 240; i++)
        {
            last = prod.Step(16);
            host.Scene.TryGetScroll(vp, out var s);
            if (s.Activity == ScrollActivity.Idle) { settled = true; break; }
        }
        host.Scene.TryGetScroll(vp, out var after);
        lineDipRead = after.LineDip;
        urgentBreaks = last.PacedUrgentBreaks;
        productionDeclines = last.ProductionDeclines;
        return after.Orientation == 1 ? after.OffsetX - before.OffsetX : after.OffsetY - before.OffsetY;
    }

    static NodeHandle FindScrollable(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if ((s.Flags(n) & NodeFlags.Scrollable) != 0 && s.HasScroll(n)) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindScrollable(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
}
