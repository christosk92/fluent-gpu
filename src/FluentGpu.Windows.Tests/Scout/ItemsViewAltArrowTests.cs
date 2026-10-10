using FluentGpu.Controls;
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

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>
/// Alt+Left / Alt+Right (an app's Back/Forward accelerators) with focus on a list row were eaten by the ItemsView's arrow
/// navigation, which set Handled without looking at the modifiers. An Alt chord is never list navigation: it falls
/// through to the accelerators, and a plain arrow still moves the current item.
/// </summary>
public sealed class ItemsViewAltArrowTests
{
    private sealed class Root : Component
    {
        public int Back, Forward;

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children =
            [
                Chord(Keys.Left, () => Back++),
                Chord(Keys.Right, () => Forward++),
                ItemsView.Create(30, static _ => new BoxEl { Height = 48f }, RepeatLayout.Stack(48f)),
            ],
        };

        // The Wavee shell's chord shape: a zero-size, hit-test-invisible accelerator owner.
        private static BoxEl Chord(int key, Action onFire) => new()
        {
            Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
            Accelerator = new KeyAccelerator(key, KeyModifiers.Alt), OnClick = onFire,
        };
    }

    [Fact]
    public void AltArrows_WithFocusOnARow_ReachTheAccelerators_AndPlainArrowsStillNavigate()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("items-alt-arrow", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            NodeHandle vp = default, row = default;
            for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
            }
            Assert.False(vp.IsNull);
            for (int i = 0; i < scene.Capacity && row.IsNull; i++)   // the roving tab stop: the first row's container
            {
                var h = scene.HandleAt(i);
                if (h.IsNull || !scene.IsLive(h) || (scene.Flags(h) & NodeFlags.Focusable) == 0) continue;
                for (var p = scene.Parent(h); !p.IsNull; p = scene.Parent(p))
                    if (p == vp) { row = h; break; }
            }
            Assert.False(row.IsNull);
            host.Input.SetFocus(row);
            host.RunFrame();

            void Key(int vk, KeyModifiers mods)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, vk, Mods: mods));
                for (int i = 0; i < 3; i++) host.RunFrame();
            }

            Key(Keys.Left, KeyModifiers.Alt);
            Key(Keys.Right, KeyModifiers.Alt);
            Key(Keys.Down, KeyModifiers.Alt);
            Assert.Equal(1, root.Back);
            Assert.Equal(1, root.Forward);
            Assert.Equal(row, host.Input.Focused);   // Alt+Down did not rove either

            Key(Keys.Down, KeyModifiers.None);       // a plain arrow still moves the current item
            Assert.NotEqual(row, host.Input.Focused);
            Assert.False(host.Input.Focused.IsNull);
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
