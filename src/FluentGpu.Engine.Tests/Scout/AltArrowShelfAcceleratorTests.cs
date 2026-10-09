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
/// Alt+Left / Alt+Right (an app's Back/Forward accelerators) with focus on a card in a horizontal shelf glided the shelf
/// instead: the focused-node keyboard scroll mapped the arrow by key code alone and consumed it before the accelerator
/// lookup. An Alt chord is a system key, never a scroll; a plain arrow still glides the shelf.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class AltArrowShelfAcceleratorTests
{
    private sealed class Root : Component
    {
        public int Back, Forward;

        public override Element Render()
        {
            var cards = new Element[20];
            for (int i = 0; i < cards.Length; i++)
                cards[i] = new BoxEl { Key = i.ToString(), Width = 100f, Height = 100f, OnClick = () => { } };
            return new BoxEl
            {
                Direction = 1, Width = 320f, Height = 240f,
                Children =
                [
                    Chord(Keys.Left, () => Back++),
                    Chord(Keys.Right, () => Forward++),
                    new BoxEl { Width = 320f, Height = 120f, Children = [Ui.ScrollView(new BoxEl { Children = cards }, horizontal: true)] },
                ],
            };
        }

        // The Wavee shell's chord shape: a zero-size, hit-test-invisible accelerator owner.
        private static BoxEl Chord(int key, Action onFire) => new()
        {
            Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
            Accelerator = new KeyAccelerator(key, KeyModifiers.Alt), OnClick = onFire,
        };
    }

    [Fact]
    public void AltArrows_WithFocusInAHorizontalShelf_ReachTheAccelerators_AndLeaveTheShelfStill()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("alt-arrow-shelf", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            NodeHandle vp = default, card = default;
            for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
            }
            Assert.False(vp.IsNull);
            Assert.True(scene.ScrollRef(vp).MaxOffset > 0.5);
            for (int i = 0; i < scene.Capacity && card.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (h.IsNull || !scene.IsLive(h) || (scene.Flags(h) & NodeFlags.Focusable) == 0) continue;
                for (var p = scene.Parent(h); !p.IsNull; p = scene.Parent(p))
                    if (p == vp) { card = h; break; }
            }
            Assert.False(card.IsNull);
            host.Input.SetFocus(card);
            host.RunFrame();

            void Key(int vk, KeyModifiers mods)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, vk, Mods: mods));
                for (int i = 0; i < 60; i++) host.RunFrame();
            }

            Key(Keys.Left, KeyModifiers.Alt);
            Key(Keys.Right, KeyModifiers.Alt);
            Assert.Equal(1, root.Back);
            Assert.Equal(1, root.Forward);
            Assert.True(scene.ScrollRef(vp).Offset < 0.5, $"Alt+Right glided the shelf (offset {scene.ScrollRef(vp).Offset})");

            Key(Keys.Right, KeyModifiers.None);   // a plain arrow still glides the shelf
            Assert.True(scene.ScrollRef(vp).Offset > 0.5, "a plain Right no longer glides the shelf");
            Assert.Equal(1, root.Forward);
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
