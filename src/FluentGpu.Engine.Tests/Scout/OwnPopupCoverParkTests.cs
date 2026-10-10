using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>F118 vs windowed popups: a host's own out-of-window menu is a top-level window above it, so the OS occlusion walk
/// reports it as a cover. Parking under it froze the menu (its paint, reveal and close fade are the host's own frames), so it
/// could never close and the window stayed parked. A host holding a popup window is never cover-parked; the verdict returns
/// on the first frame without one.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class OwnPopupCoverParkTests
{
    private static readonly RectF Window = new(100, 100, 320, 240);

    private sealed class HooksProbe : Component
    {
        public InputHooks? Hooks;
        public override Element Render()
        {
            Hooks = UseContext(InputHooks.Current);
            return Ui.VStack(0);
        }
    }

    private static (HeadlessPlatformApp App, HeadlessWindow Win, AppHost Host, HooksProbe Root) Create()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var win = new HeadlessWindow(new WindowDesc("compact", new Size2(320, 240), 1f));
        win.Show();
        win.OuterBoundsPx = Window;
        var root = new HooksProbe();
        var host = new AppHost(app, win, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        host.RunFrame();
        return (app, win, host, root);
    }

    [Fact]
    public void Popup_over_its_own_window_never_parks_it()
    {
        var (app, win, host, root) = Create();
        try
        {
            var hooks = root.Hooks!;
            int token = hooks.OpenPopupWindow!(host.Scene.Root, PopupWindowMaterial.TransientAcrylic);
            Assert.True(token >= 0);

            // The menu escaped the compact window and its HWND (shadow margins included) is the only window above it.
            win.SetOccluders(new RectF(90, 98, 360, 420));
            host.RunFrame();
            Assert.False(host.IsParked);                // before the fix: cover-parked under its own menu
            Assert.False(host.WindowOccludedForTest);

            // Still painting, so the menu can close: the lease is released and the window hidden.
            hooks.ClosePopupWindow!(token);
            win.SetOccluders();                         // the popup HWND hid: an event bumped the epoch
            host.RunFrame();
            Assert.Empty(host.PopupWindows);
            Assert.False(host.IsParked);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void Cover_verdict_returns_on_the_first_frame_without_a_popup()
    {
        var (app, win, host, root) = Create();
        try
        {
            var hooks = root.Hooks!;
            int token = hooks.OpenPopupWindow!(host.Scene.Root, PopupWindowMaterial.TransientAcrylic);
            win.SetOccluders(new RectF(0, 0, 1920, 1080));   // a maximized window over everything while the menu is up
            host.RunFrame();
            Assert.False(host.IsParked);

            hooks.ClosePopupWindow!(token);                  // no fresh epoch: the verdict must still be re-walked
            host.RunFrame();
            Assert.True(host.IsParked);
            Assert.True(host.WindowOccludedForTest);
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
