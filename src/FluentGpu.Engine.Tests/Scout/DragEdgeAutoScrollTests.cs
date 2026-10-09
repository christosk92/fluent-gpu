using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// Drag edge auto-scroll with the pointer held STILL in the edge zone. <see cref="DragDropContext"/> posts its velocity
/// through the dispatcher-wired <c>AutoScroll</c> seam only when the value or the viewport changes, so a stationary
/// pointer posts exactly once. The dispatcher turned that post into a one-shot <c>ScrollBy(v / 60, Immediate)</c>: the
/// list stepped one frame's worth and stopped, and a drop slot below the fold was out of reach without wiggling the mouse.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DragEdgeAutoScrollTests
{
    private const float W = 320f, H = 240f;

    private sealed class Root : Component
    {
        public override Element Render()
            => Ui.ScrollView(new BoxEl { Height = 40_000f, Direction = 1 });
    }

    private static void WithList(Action<AppHost, NodeHandle> body)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("drag-edge-autoscroll", new Size2(W, H), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root());
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            NodeHandle vp = default;
            var scene = host.Scene;
            for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
            }
            Assert.False(vp.IsNull);
            body(host, vp);
        }
        finally { host.Dispose(); }
    }

    private static float OffsetOf(AppHost host, NodeHandle vp)
    {
        Assert.True(host.Scene.TryGetScroll(vp, out var sc));
        return sc.OffsetY;
    }

    /// <summary>Hover 10 DIP above the bottom edge (~1365 DIP/s) and never move again: half a second of frames must carry
    /// the list hundreds of DIP. Before the fix it moved ~23 DIP (one 60th of the velocity) and then held.</summary>
    [Fact]
    public void StillPointerInEdgeZone_KeepsScrolling()
        => WithList((host, vp) =>
        {
            var at = new Point2(W * 0.5f, H - 10f);
            Assert.True(host.Input.DragDrop.ExternalBegin("files", null, at, KeyModifiers.None));
            host.Input.DragDrop.Move(host.Input.DiagHitTest(at), at, 0f, 0f, KeyModifiers.None);
            for (int i = 0; i < 6; i++) host.RunFrame();   // past the 50ms delay-start
            float early = OffsetOf(host, vp);
            for (int i = 0; i < 30; i++) host.RunFrame();
            float late = OffsetOf(host, vp);
            host.Input.DragDrop.Cancel();
            Assert.True(late - early > 300f, $"early={early} late={late}");
        });

    /// <summary>The held velocity stops in place when the pointer leaves the zone, and a cancelled session leaves nothing
    /// coasting.</summary>
    [Fact]
    public void LeavingTheZoneOrCancelling_StopsInPlace()
        => WithList((host, vp) =>
        {
            var edge = new Point2(W * 0.5f, H - 10f);
            Assert.True(host.Input.DragDrop.ExternalBegin("files", null, edge, KeyModifiers.None));
            host.Input.DragDrop.Move(host.Input.DiagHitTest(edge), edge, 0f, 0f, KeyModifiers.None);
            for (int i = 0; i < 12; i++) host.RunFrame();
            var middle = new Point2(W * 0.5f, H * 0.5f);   // > 100 DIP from both edges: no edge velocity
            host.Input.DragDrop.Move(host.Input.DiagHitTest(middle), middle, 0f, 0f, KeyModifiers.None);
            for (int i = 0; i < 2; i++) host.RunFrame();
            float left = OffsetOf(host, vp);
            Assert.True(left > 0f);
            for (int i = 0; i < 20; i++) host.RunFrame();
            Assert.Equal(left, OffsetOf(host, vp), 3);

            host.Input.DragDrop.Move(host.Input.DiagHitTest(edge), edge, 0f, 0f, KeyModifiers.None);
            for (int i = 0; i < 12; i++) host.RunFrame();
            host.Input.DragDrop.Cancel();
            for (int i = 0; i < 2; i++) host.RunFrame();
            float cancelled = OffsetOf(host, vp);
            for (int i = 0; i < 20; i++) host.RunFrame();
            Assert.Equal(cancelled, OffsetOf(host, vp), 3);
        });
}
