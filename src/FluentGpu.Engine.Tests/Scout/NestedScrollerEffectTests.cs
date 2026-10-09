using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A scroll effect binds to its NEAREST scroller (CSS position:sticky / scroll()): a sticky header inside a fixed-height
/// inner list is posed by that list alone. Scrolling the PAGE past the inner list must not evaluate the header against
/// the page too — that flipped its engaged edge true (outer row) then false (inner row) and re-marked it pinned +
/// paint-dirty on every frame the page moved, and left its pose to whichever coverage row happened to land last.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class NestedScrollerEffectTests
{
    private sealed class FlipCounter : IEqualityComparer<bool>
    {
        public int Flips;
        public bool Equals(bool a, bool b) { if (a != b) Flips++; return a == b; }
        public int GetHashCode(bool v) => v ? 1 : 0;
    }

    private sealed class Root(NestedScrollerEffectTests t) : Component
    {
        public override Element Render()
            => Ui.ScrollView(new BoxEl
            {
                Direction = 1,
                Children =
                [
                    new BoxEl { Height = 100f },
                    Ui.ScrollView(new BoxEl
                    {
                        Direction = 1,
                        Children =
                        [
                            new BoxEl { Height = 20f, OnRealized = h => t.Header = h }.Sticky(0f, engaged: t.Stuck),
                            new BoxEl { Height = 1_000f },
                        ],
                    }) with { Height = 100f, Grow = 0f, Shrink = 0f },
                    new BoxEl { Height = 2_000f },
                ],
            }) with { OnRealized = h => t.Outer = h };
    }

    private NodeHandle Header, Outer;
    private readonly FlipCounter _flips = new();
    private readonly Signal<bool> Stuck;
    public NestedScrollerEffectTests() => Stuck = new Signal<bool>(false, _flips);

    [Fact]
    public void APageScrollNeverDrivesAStickyInsideANestedList()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nested-sticky", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(this));
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            Assert.False(Header.IsNull);
            Assert.False(Outer.IsNull);
            var scene = host.Scene;

            // The page jumps 300 DIP past the inner list (page y 100..200, header at its top), then glides on: every
            // frame of the glide runs the scroll pose. The inner list never scrolled, so its header is never stuck.
            host.TryGetScrollHandle(Outer)!.ScrollTo(300.0, ScrollMove.Immediate);
            for (int i = 0; i < 4; i++) host.RunFrame();
            Assert.Equal(300.0, scene.ScrollRef(Outer).Offset);
            host.TryGetScrollHandle(Outer)!.ScrollTo(600.0);
            for (int i = 0; i < 5; i++) host.RunFrame();

            Assert.Equal(0, _flips.Flips);                                           // no per-frame true/false churn
            Assert.False(Stuck.Peek());
            Assert.Equal(0u, (uint)(scene.Flags(Header) & NodeFlags.StickyPinned));  // not lifted above its siblings
            Assert.Equal(0f, scene.Paint(Header).LocalTransform.Dy);                 // not translated to the page top

            // The inner list's own scroll still pins it.
            NodeHandle inner = scene.Parent(scene.Parent(Header));
            Assert.True(scene.HasScroll(inner));
            host.TryGetScrollHandle(inner)!.ScrollTo(200.0, ScrollMove.Immediate);
            for (int i = 0; i < 4; i++) host.RunFrame();
            Assert.True(Stuck.Peek());
            Assert.Equal(200f, scene.Paint(Header).LocalTransform.Dy);
        }
        finally { host.Dispose(); }
    }
}
