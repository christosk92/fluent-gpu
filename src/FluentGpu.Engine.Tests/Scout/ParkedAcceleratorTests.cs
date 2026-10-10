using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// Keyboard accelerators and access keys are window-global, but only over the shown tree. A page that a KeepAlive
/// navigation parked stays live with its handlers, and FindAccelerator/FindAccessKey take the lowest slot index, so
/// two pages with the same Ctrl+R ran the hidden page's handler and the live page's button never fired.
/// </summary>
[Collection(SerialTestCollection.Name)]   // builds AppHosts; HostDispatch.Current is process-static (see SerialTestCollection)
public sealed class ParkedAcceleratorTests
{
    private sealed class Root : Component
    {
        public readonly Signal<string> Page = new("a");
        public int AccelA, AccelB, AccessA, AccessB;

        Element PageOf(string k) => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children =
            [
                new BoxEl
                {
                    Width = 40f, Height = 40f, Accelerator = new KeyAccelerator('R', KeyModifiers.Ctrl),
                    OnClick = () => { if (k == "a") AccelA++; else AccelB++; },
                },
                new BoxEl
                {
                    Width = 40f, Height = 40f, AccessKey = 'F',
                    OnClick = () => { if (k == "a") AccessA++; else AccessB++; },
                },
            ],
        };

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children = [Flow.KeepAlive(() => Page.Value, static k => k, PageOf)],
        };
    }

    [Fact]
    public void AParkedPagesChords_DoNotShadowTheLivePage()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("parked-accelerator", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            root.Page.Value = "b";   // page "a" is parked: live, detached, handlers intact
            for (int i = 0; i < 3; i++) host.RunFrame();

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, 'R', Mods: KeyModifiers.Ctrl));
            host.RunFrame();
            Assert.Equal(0, root.AccelA);
            Assert.Equal(1, root.AccelB);

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Alt, Mods: KeyModifiers.Alt));
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, 'F', Mods: KeyModifiers.Alt));
            host.RunFrame();
            Assert.Equal(0, root.AccessA);
            Assert.Equal(1, root.AccessB);

            root.Page.Value = "a";   // back: "a" is shown again and owns the chord, "b" is parked
            for (int i = 0; i < 3; i++) host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, 'R', Mods: KeyModifiers.Ctrl));
            host.RunFrame();
            Assert.Equal(1, root.AccelA);
            Assert.Equal(1, root.AccelB);
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
