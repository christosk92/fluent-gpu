using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// E8 (EP12): a scroller with a ScrollKey restores its remembered offset in the FIRST layout of the remount. Gates: a plain
/// scroller and a 2,000-row virtual list are at the remembered offset after exactly one frame (the virtual list never builds
/// its top rows), a shorter list clamps, an unknown key starts at 0, and the frames after the restore allocate nothing on
/// the hot phases. The matrix of the same rules lives in Engine.Tests/ScrollFirstLayoutTests.
/// </summary>
static class ScrollFirstLayoutSuite
{
    const float ViewH = 400f, RowH = 40f;

    sealed record Page(bool Virtual, string Key, int Size);

    sealed class Pages : Component
    {
        public readonly Signal<Page?> Current = new(null);
        public NodeHandle Vp;
        public readonly List<int> Built = new();

        public override Element Render()
        {
            var page = Current.Value;
            if (page is null) return new BoxEl { Width = 300f, Height = ViewH };
            if (page.Virtual)
                return new VirtualListEl
                {
                    ItemCount = page.Size, ItemLayout = new StackVirtualLayout(RowH), ScrollLineDip = RowH,
                    Width = 300f, Height = ViewH, ScrollKey = page.Key, OnRealized = h => Vp = h,
                    RenderItem = i => { Built.Add(i); return new BoxEl { Height = RowH, Width = 280f, Fill = ColorF.FromRgba(40, 40, 40) }; },
                };
            var rows = new Element[page.Size];
            for (int i = 0; i < rows.Length; i++) rows[i] = new BoxEl { Height = RowH, Fill = ColorF.FromRgba(40, 40, 50) };
            return new ScrollEl
            {
                Width = 300f, Height = ViewH, ScrollKey = page.Key, OnRealized = h => Vp = h,
                Content = new BoxEl { Direction = 1, Children = rows },
            };
        }
    }

    public static void Run(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-first-layout", new Size2(300, ViewH), 1f));
        window.Show();
        var pages = new Pages();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, pages);
        host.RunFrame();

        double OffsetOf() => host.Scene.TryGetScroll(pages.Vp, out var s) ? s.Offset : double.NaN;

        void Visit(Page page, double offset)
        {
            pages.Current.Value = page;
            for (int i = 0; i < 4; i++) host.RunFrame();
            host.TryGetScrollHandle(pages.Vp)!.ScrollTo(offset, ScrollMove.Immediate);
            for (int i = 0; i < 4; i++) host.RunFrame();
            pages.Current.Value = null;
            for (int i = 0; i < 4; i++) host.RunFrame();
        }

        void Return(Page page)
        {
            pages.Built.Clear();
            pages.Current.Value = page;
            host.RunFrame();   // exactly one frame
        }

        var plain = new Page(false, "plain:a", 100);
        Visit(plain, 500);
        Return(plain);
        double plainFirst = OffsetOf();
        host.RunFrame();
        Check("gate.scroll-first-layout.plain a remounted plain scroller is at its remembered offset after ONE frame and does not move afterwards",
            Math.Abs(plainFirst - 500.0) < 0.5 && Math.Abs(OffsetOf() - 500.0) < 0.5, $"first={plainFirst} next={OffsetOf()}");

        Visit(new Page(false, "plain:b", 100), 3000);
        Return(new Page(false, "plain:b", 25));
        Check("gate.scroll-first-layout.plain-clamp a remembered offset past shorter content lands at the end (600) in the first frame",
            Math.Abs(OffsetOf() - 600.0) < 0.5, $"offset={OffsetOf()}");

        Visit(new Page(false, "plain:c", 100), 700);
        Return(new Page(false, "plain:unknown", 100));
        Check("gate.scroll-first-layout.unknown-key an unknown key starts at 0", Math.Abs(OffsetOf()) < 0.5, $"offset={OffsetOf()}");

        var virt = new Page(true, "virt:a", 2000);
        Visit(virt, 40_000);
        Return(virt);
        host.Scene.TryGetScroll(pages.Vp, out var sc);
        int lowest = pages.Built.Count == 0 ? -1 : pages.Built.Min();
        Check("gate.scroll-first-layout.virtual a remounted 2,000-row list is at item 1000 after ONE frame, realized around it, and never built its top rows",
            Math.Abs(sc.Offset - 40_000.0) < 0.5 && sc.FirstRealized <= 1000 && 1000 < sc.LastRealized && lowest > 900,
            $"offset={sc.Offset} realized=[{sc.FirstRealized},{sc.LastRealized}) lowestBuilt={lowest}");

        Visit(new Page(true, "virt:b", 2000), 40_000);
        Return(new Page(true, "virt:b", 100));
        host.Scene.TryGetScroll(pages.Vp, out var shortSc);
        int lowestShort = pages.Built.Count == 0 ? -1 : pages.Built.Min();
        Check("gate.scroll-first-layout.virtual-clamp a remembered offset past a shorter list lands at the end (3600) and realizes the end window first",
            Math.Abs(shortSc.Offset - 3600.0) < 0.5 && shortSc.LastRealized == 100 && lowestShort > 40,
            $"offset={shortSc.Offset} realized=[{shortSc.FirstRealized},{shortSc.LastRealized}) lowestBuilt={lowestShort}");

        // Steady frames after the restore: the latch is gone, nothing allocates on the hot phases.
        Return(virt);
        for (int i = 0; i < 6; i++) host.RunFrame();
        long worst = 0;
        for (int i = 0; i < 16; i++) { var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
        Check("gate.scroll-first-layout.zero-alloc steady frames after a restore allocate nothing on the hot phases",
            worst == 0 && Math.Abs(OffsetOf() - 40_000.0) < 0.5, $"worst={worst}B offset={OffsetOf()}");
    }
}
