using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A <c>.StickyClip</c> whose node casts a drop shadow cannot be a composite clip: the recorder bakes the clip into the
/// node's bytes and watches the posed value (<c>SliceRecorder.BakeClip</c>). Collapsed (<c>Visible=false</c>, the node
/// itself or an ancestor), the node is never walked again, so nothing re-bakes it, yet its posed clip keeps moving: it
/// snaps open on the node's own 0x0 box, and under a collapsed ancestor it rides the scroll at its stale box. The watch
/// must not ask a node that paints nothing to re-record its chain, or every turn records and none composites for as
/// long as it stays hidden. Shown again, the watch resumes. Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class BakedStickyClipCollapseTests
{
    private sealed class Page(Signal<bool> shown, bool viaParent) : Component
    {
        public override Element Render()
        {
            // the shadow keeps the sticky clip off the composite route: it is recorded inline (baked)
            Element card = new BoxEl
            {
                Width = 320f, Height = 100f, Fill = ColorF.FromRgba(40, 90, 220),
                Shadow = new ShadowSpec(8f, 2f, 0f, ColorF.FromRgba(0, 0, 0, 80)),
                Visible = viaParent ? true : Prop.Of(() => shown.Value),
            }.StickyClip(0f);
            return new ScrollEl
            {
                Width = 320f, Height = 200f, SuppressScrollBar = true, EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new BoxEl { Width = 320f, Height = 40f },
                        viaParent ? new BoxEl { Direction = 1, Visible = Prop.Of(() => shown.Value), Children = [card] } : card,
                        new BoxEl { Width = 320f, Height = 800f },
                    ],
                },
            };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACollapsedBakedStickyClip_KeepsTheTurnCompositeOnly_AndIsWatchedAgainOnceShown(bool viaParent)
    {
        var shown = new Signal<bool>(true);
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("baked-sticky-collapse", new Size2(320, 200), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Page(shown, viaParent));
        try
        {
            void Settle() { for (int i = 0; i < 30 && (i < 2 || host.HasActiveWork); i++) host.RunFrame(); }
            Settle();
            var scene = host.Scene;
            NodeHandle vp = default;
            for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
            }
            Assert.False(vp.IsNull);
            var slices = new SliceRecorder();
            var spans = new SpanTable();
            void Record() => SceneRecorder.Record(scene, new DrawList(), spans: spans, slices: slices);
            bool Compatible() => slices.PosesCompatible(scene.Recording.CaptureInline(scene, null));

            // the card spans content y 40..140: at offset 60 its clip is engaged 20 DIP down, baked at that value
            host.TryGetScrollHandle(vp)!.ScrollTo(60.0, ScrollMove.Immediate);
            Settle();
            Record();
            Assert.True(Compatible());

            shown.Value = false;
            Settle();
            Record();
            Assert.True(Compatible(), "a collapsed baked sticky clip still asks for a re-record");

            host.TryGetScrollHandle(vp)!.ScrollTo(80.0, ScrollMove.Immediate);
            Settle();
            Record();
            Assert.True(Compatible(), "scrolling past a collapsed baked sticky clip still asks for a re-record");

            // shown at offset 80 its clip sits 40 DIP down, off the 20 it baked: that must still re-record
            shown.Value = true;
            Settle();
            Assert.False(Compatible());
            Record();
            Assert.True(Compatible());
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
