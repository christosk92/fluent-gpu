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
/// The stationary-pointer hover refresh hit-tests the last mouse position, which the platform captured in the window DIP
/// space of the scale at that time. An app-zoom step (Ctrl+wheel) or a DPI hop changes that scale under a cursor that
/// never moved, so the cached point must follow it into the new DIP space; otherwise the card that used to sit at those
/// DIP coordinates keeps (or takes) the hover instead of the one now under the cursor.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ZoomStationaryHoverTests
{
    // Three fixed-width cards in a row: their DIP rects do not move with the scale, only the cursor's DIP position does.
    private sealed class Row : Component
    {
        public override Element Render()
        {
            var cells = new Element[3];
            for (int i = 0; i < cells.Length; i++)
            {
                ColorF fill = ColorF.FromRgba((byte)(40 + 60 * i), 90, 200);
                cells[i] = new BoxEl { Width = 100f, Height = 100f, Fill = fill, HoverFill = fill, OnClick = static () => { } };
            }
            return new BoxEl { Direction = 0, Children = cells };
        }
    }

    private static void Settle(AppHost host)
    {
        for (int i = 0; i < 30 && (i < 2 || host.HasActiveWork); i++) host.RunFrame();
    }

    [Theory]
    [InlineData(false)]   // app zoom (Ctrl+wheel / Ctrl+=): IPlatformWindow.SetZoom
    [InlineData(true)]    // per-monitor DPI hop (WM_DPICHANGED): the raw OS scale
    public void TheHoverFollowsTheUnmovedCursorAcrossAScaleChange(bool dpiHop)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("zoom-stationary-hover", new Size2(640, 480), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Row());
        try
        {
            Settle(host);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(180f, 50f), 0, 0));
            Settle(host);

            var input = host.Input;
            var scene = host.Scene;
            NodeHandle second = input.HitTest(new Point2(180f, 50f));
            Assert.False(second.IsNull);
            Assert.True((scene.Flags(second) & NodeFlags.Hovered) != 0);

            if (dpiHop) window.Scale = 2f; else window.SetZoom(2f);
            Settle(host);

            // The cursor never moved: it still sits on client px (180, 50), which is DIP (90, 25) at 2x.
            Point2? p = input.PointerPosition;
            Assert.NotNull(p);
            Assert.Equal(90.0, p.Value.X, 3);
            Assert.Equal(25.0, p.Value.Y, 3);

            NodeHandle underCursor = input.HitTest(new Point2(90f, 25f));
            Assert.False(underCursor.IsNull);
            Assert.NotEqual(second, underCursor);
            Assert.True((scene.Flags(underCursor) & NodeFlags.Hovered) != 0);
            Assert.True((scene.Flags(second) & NodeFlags.Hovered) == 0);
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
