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
/// Tab / Shift+Tab moved keyboard focus (and the focus ring) onto controls scrolled out of a plain ScrollView and never
/// moved the viewport: Space/Enter then activated a control the user could not see. A keyboard focus move must bring
/// the focused node into view (WinUI keyboard focus → BringIntoView).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class TabFocusIntoViewTests
{
    private const float RowH = 48f;
    private const float ViewH = 240f;
    private const int Rows = 30;

    private sealed class Root : Component
    {
        public override Element Render()
        {
            var rows = new Element[Rows];
            for (int i = 0; i < Rows; i++)
                rows[i] = new BoxEl { Key = i.ToString(), Height = RowH, Width = 200f, OnClick = () => { } };
            return new BoxEl
            {
                Direction = 1, Width = 320f, Height = ViewH,
                Children = [Ui.ScrollView(new BoxEl { Direction = 1, Children = rows })],
            };
        }
    }

    [Fact]
    public void TabbingPastTheViewport_ScrollsTheFocusedRowIntoView_AndShiftTabScrollsBack()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("tab-focus-into-view", new Size2(320, (int)ViewH), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root());
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            NodeHandle vp = default;
            for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
            }
            Assert.False(vp.IsNull);

            void Tab(KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab, Mods: mods));
                host.RunFrame();
            }
            void Settle() { for (int i = 0; i < 120; i++) host.RunFrame(); }

            for (int i = 0; i < 10; i++) Tab();   // null → row 0, … → row 9 (content 432..480, viewport 0..240)
            Settle();
            double offset = scene.ScrollRef(vp).Offset;
            Assert.True(Math.Abs(offset - (10 * RowH - ViewH)) < 1.0, $"row 9 not brought into view (offset {offset})");

            for (int i = 0; i < 9; i++) Tab(KeyModifiers.Shift);   // back to row 0
            Settle();
            Assert.True(scene.ScrollRef(vp).Offset < 1.0, $"row 0 not brought back into view (offset {scene.ScrollRef(vp).Offset})");
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
