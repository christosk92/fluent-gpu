using System;
using System.Collections.Generic;
using FluentGpu.Animation;
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
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A bound list's staggered removal (RemovalOptions.StaggerMs) deals its exits over the realized rows that leave. Each row
/// was ranked among ALL removed indices, so after scrolling to row ~60 a "remove everything above" made every visible
/// row wait 40 ms x 60 at full opacity over the survivors sliding up, and the host's 2 s orphan backstop then cut them with
/// no exit at all. Drives a real headless host through the reconciler's removal seam, then advances the animation clock
/// by the budget the realized rows need and checks every exiting row actually faded out.
/// </summary>
[Collection(SerialTestCollection.Name)]   // builds an AppHost; HostDispatch.Current is process-static
public sealed class RemovalStaggerRankTests
{
    private const float RowH = 40f;
    private const int Total = 200;
    private const float StaggerMs = 40f;

    private sealed class Rows : Component
    {
        public readonly Signal<int> Count = new(Total);
        public Action<NodeHandle, IReadOnlyList<int>, EnterExit, MotionTokenId, float, Action>? Remove;

        public override Element Render()
        {
            Remove = Context.BeginVirtualRemoval;   // the seam ItemsViewController.BeginRemoval rides
            return new VirtualListEl
            {
                ItemCount = Count.Value, ItemLayout = new StackVirtualLayout(RowH), ScrollLineDip = RowH,
                Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
                RowBind = _ => new BoxEl { Width = 180, Height = RowH, Fill = ColorF.FromRgba(60, 60, 60) },
            };
        }
    }

    [Fact]
    public void RemovingEverythingAboveADeepScroll_DealsTheStaggerOverTheRealizedRowsOnly()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("removal-stagger-rank", new Size2(320, 320), 1f));
        window.Show();
        var rows = new Rows();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, rows);
        try
        {
            host.RunFrame();
            host.RunFrame();
            var scene = host.Scene;
            var vp = scene.Root;
            host.TryGetScrollHandle(vp)!.ScrollTo(60 * RowH, ScrollMove.Immediate);
            for (int i = 0; i < 4; i++) host.RunFrame();
            Assert.True(scene.TryGetScroll(vp, out var sc));
            // The premise: ranked among all removed indices, even the first realized row would wait past the backstop.
            Assert.True(sc.FirstRealized * StaggerMs >= 2000f, $"realized from {sc.FirstRealized}");

            var removed = new int[76];
            for (int i = 0; i < removed.Length; i++) removed[i] = i;
            rows.Remove!(vp, removed, new EnterExit(Opacity: 0f, Active: true), MotionTokenId.StandardExit, StaggerMs,
                () => rows.Count.Value = Total - removed.Length);

            int exiting = scene.OrphanCount;
            Assert.True(exiting > 0);
            float budget = StaggerMs * (exiting - 1) + MotionTok.StandardExit.DurationMs;
            Assert.True(budget < 2000f, $"the realized rows' deal ({budget} ms) must fit under the orphan backstop");

            for (float t = 0f; t <= budget + 50f; t += 16.67f) host.Animation.Tick(16.67f);
            for (int i = 0; i < scene.OrphanCount; i++)
            {
                var o = scene.OrphanAt(i, out _, out _);
                Assert.True(scene.Paint(o).Opacity <= 0.01f, $"exiting row {i} still at opacity {scene.Paint(o).Opacity}");
            }
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
