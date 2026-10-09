using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>A Compact NumberBox's popup spin buttons were focusable: pressing one moved focus out of the field into the
/// overlay layer, the field's blur closed the popup, and the close's focus restore re-focused the field, which opened a
/// fresh popup. Every press flickered the popup, and a held press never auto-repeated because the armed node died
/// with the old popup's fade.</summary>
public sealed class NumberBoxPopupSpinFocusTests
{
    private sealed class Root : Component
    {
        public readonly Signal<double> Value = new(5);
        public IOverlayService? Svc;

        public override Element Render() => Embed.Comp(() => new OverlayHost
        {
            IsPrimaryToastHost = false,
            Child = new BoxEl
            {
                Direction = 1, Width = 420f, Height = 320f,
                Children =
                [
                    Embed.Comp(() => new Spy { Owner = this }),
                    NumberBox.Create(value: Value, options: new NumberBox.NumberBoxOptions
                    {
                        Minimum = 0, Maximum = 10, SmallChange = 1, LargeChange = 5,
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    }),
                ],
            },
        });
    }

    private sealed class Spy : Component
    {
        public required Root Owner;
        public override Element Render() { Owner.Svc = UseContext(Overlay.Service); return new BoxEl(); }
    }

    private sealed class Rig : System.IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly HeadlessWindow Window;
        public readonly AppHost Host;
        public readonly Root Root = new();

        public Rig()
        {
            var strings = new StringTable();
            Window = new HeadlessWindow(new WindowDesc("nb-popup-spin", new Size2(420, 320), 1f));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, Root);
            Frames();
        }

        public void Frames(int n = 4) { for (int i = 0; i < n; i++) Host.RunFrame(); }

        // Tree order (FirstChild/NextSibling), so the popup's up spin comes before its down spin.
        public List<NodeHandle> Find(AutomationRole role)
        {
            var list = new List<NodeHandle>();
            Collect(Host.Scene, Host.Scene.Root, role, list);
            return list;
        }

        private static void Collect(SceneStore s, NodeHandle n, AutomationRole role, List<NodeHandle> into)
        {
            if (n.IsNull) return;
            if (s.Interaction(n).Role == role) into.Add(n);
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Collect(s, c, role, into);
        }

        private Point2 Center(NodeHandle n)
        {
            var r = Host.Scene.AbsoluteRect(n);
            return new Point2(r.X + r.W * 0.5f, r.Y + r.H * 0.5f);
        }

        public void Press(NodeHandle n) { Window.QueueInput(new InputEvent(InputKind.PointerDown, Center(n), 0, 0)); Host.RunFrame(); }
        public void Release(Point2 p) { Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0)); Host.RunFrame(); }
        public void Click(NodeHandle n) { var p = Center(n); Press(n); Release(p); }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    // Focus the field: the Compact popup opens with its two spin buttons.
    private static NodeHandle OpenPopup(Rig r, out NodeHandle field)
    {
        r.Click(r.Find(AutomationRole.Text)[0]);
        r.Frames();
        field = r.Host.Input.Focused;
        var spins = r.Find(AutomationRole.Button);
        Assert.Equal(2, spins.Count);
        Assert.True(r.Root.Svc!.AnyOpen);
        return spins[0];
    }

    [Fact]
    public void Pressing_a_popup_spin_keeps_the_field_focused_and_the_same_popup_open()
    {
        using var r = new Rig();
        var up = OpenPopup(r, out var field);
        var r0 = r.Host.Scene.AbsoluteRect(up);
        var at = new Point2(r0.X + r0.W * 0.5f, r0.Y + r0.H * 0.5f);

        r.Press(up);                                   // press and hold the up spin
        Assert.Equal(field, r.Host.Input.Focused);
        Assert.Equal(6, r.Root.Value.Peek());          // the press edge steps once

        r.Frames(10);                                  // past the ~83 ms fade a blur-close would have started
        var held = r.Find(AutomationRole.Button);
        Assert.Equal(2, held.Count);                   // no second popup fading in under the pointer
        Assert.Equal(up, held[0]);                     // the same popup: not closed and reopened
        Assert.True(r.Host.Scene.IsLive(up));
        Assert.True(r.Root.Svc!.AnyOpen);

        r.Release(at);
        r.Frames();
        Assert.Equal(field, r.Host.Input.Focused);
        Assert.Equal(up, r.Find(AutomationRole.Button)[0]);
    }

    [Fact]
    public void Holding_a_popup_spin_auto_repeats()
    {
        using var r = new Rig();
        var up = OpenPopup(r, out _);
        var r0 = r.Host.Scene.AbsoluteRect(up);
        var at = new Point2(r0.X + r0.W * 0.5f, r0.Y + r0.H * 0.5f);

        r.Press(up);
        r.Frames(40);                                  // 640 ms held: past the 500 ms RepeatButton delay
        Assert.True(r.Root.Value.Peek() > 6, $"value={r.Root.Value.Peek()}");
        r.Release(at);
    }
}
