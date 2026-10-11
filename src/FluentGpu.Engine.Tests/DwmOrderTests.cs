using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using FluentGpu.Theming;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// EP9: the window material and the order of its DWM writes against presents. The headless window and swapchain record every present
/// (<see cref="HeadlessSwapchain.PresentObserved"/>) and the material sink records every DWM write, so a test reads the order directly:
/// an ordered write lands immediately before the first present of a publication newer than the one current when it was queued, and
/// exactly once. Also the material / composited / frame combinations (<see cref="WindowMaterialPolicy"/>) and the video-needs-composited
/// guard. Serial: it constructs hosts and writes the process-global window background.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DwmOrderTests
{
    private sealed class Root : Component
    {
        public override Element Render() => new BoxEl { Width = 100f, Height = 100f, Fill = ColorF.FromRgba(40, 40, 40) };
    }

    private sealed class Rig : IDisposable
    {
        private readonly HeadlessPlatformApp _app = new();
        public readonly HeadlessWindow Window;
        public readonly HeadlessGpuDevice Device = new();
        public readonly AppHost Host;
        /// <summary>The ordered record of DWM writes ("dwm:Material:dark") and presents ("present").</summary>
        public readonly List<string> Log = new();

        public Rig(bool composited = true)
        {
            var strings = new StringTable();
            Window = new HeadlessWindow(new WindowDesc("dwm-order", new Size2(320, 240), 1f, Composited: composited));
            Window.Show();
            Host = new AppHost(_app, Window, Device, new HeadlessFontSystem(strings), strings, new Root());
            Device.PrimarySwapchain!.PresentObserved = () => Log.Add("present");
            Host.WindowMaterialSink = (m, dark) => Log.Add($"dwm:{m}:{(dark ? "dark" : "light")}");
            for (int i = 0; i < 4; i++) Host.RunFrame();   // settle: the first frames present
            Log.Clear();
        }

        public void Dispose()
        {
            Tok.ClearWindowBackground();   // the process-global override the material change wrote
            Host.Dispose();
            _app.Dispose();
        }
    }

    [Fact]
    public void An_Ordered_Material_Write_Lands_Immediately_Before_The_Present_That_Shows_The_New_Ground()
    {
        using var rig = new Rig();

        rig.Host.SetWindowMaterial(WindowMaterial.None, dark: true);   // ordered by default
        Assert.Empty(rig.Log);                                          // queued, not written

        rig.Host.RunFrame();

        Assert.Equal(new[] { "dwm:None:dark", "present" }, rig.Log);            // the write, then the present - never the reverse
        rig.Log.Clear();
        for (int i = 0; i < 4; i++) rig.Host.RunFrame();
        Assert.DoesNotContain(rig.Log, l => l.StartsWith("dwm"));       // exactly once
    }

    [Fact]
    public void Two_Queued_Writes_Run_In_Order_Before_The_Same_Present()
    {
        using var rig = new Rig();
        rig.Host.SetWindowMaterial(WindowMaterial.Acrylic, dark: false);
        rig.Host.SetWindowMaterial(WindowMaterial.MicaAlt, dark: false);

        rig.Host.RunFrame();

        Assert.Equal(new[] { "dwm:Acrylic:light", "dwm:MicaAlt:light", "present" }, rig.Log);
        Assert.Equal(WindowMaterial.MicaAlt, rig.Host.WindowMaterial);
    }

    [Fact]
    public void An_Unordered_Write_Is_Immediate_On_The_Calling_Thread()
    {
        using var rig = new Rig();
        rig.Host.SetWindowMaterial(WindowMaterial.Acrylic, dark: true, ordered: false);
        Assert.Equal(new[] { "dwm:Acrylic:dark" }, rig.Log);                    // before any present
        rig.Host.RunFrame();
        Assert.Equal(new[] { "dwm:Acrylic:dark", "present" }, rig.Log);
    }

    [Fact]
    public void QueueBeforePresent_Runs_Its_Work_Once_Right_Before_The_Next_Present()
    {
        using var rig = new Rig();
        rig.Host.QueueBeforePresent(() => rig.Log.Add("work"));
        rig.Host.RunFrame();
        rig.Host.RunFrame();
        rig.Host.RunFrame();
        Assert.Equal("work", rig.Log[0]);
        Assert.Equal("present", rig.Log[1]);
        Assert.Single(rig.Log, l => l == "work");
    }

    [Fact]
    public void A_Hidden_Window_Has_No_Present_To_Order_Against_So_The_Write_Is_Immediate()
    {
        using var rig = new Rig();
        rig.Window.Hide();
        rig.Host.SetWindowMaterial(WindowMaterial.None, dark: true);
        Assert.Equal(new[] { "dwm:None:dark" }, rig.Log);
    }

    [Fact]
    public void A_Write_No_Present_Picks_Up_Is_Run_By_The_UI_Thread_After_The_Stale_Window()
    {
        using var rig = new Rig();
        rig.Host.QueueBeforePresent(() => rig.Log.Add("work"));
        rig.Window.Hide();                 // the window stops presenting before the next publication
        Thread.Sleep(650);
        rig.Host.RunFrame();
        Assert.Contains("work", rig.Log);
        Assert.DoesNotContain("present", rig.Log);
    }

    [Fact]
    public void The_Material_Decides_The_Ground_On_A_Composited_Window()
    {
        using var rig = new Rig(composited: true);
        rig.Host.SetWindowMaterial(WindowMaterial.Mica, dark: true, ordered: false);
        Assert.True(Tok.WindowBackground.A <= 0.004f, "a backdrop clears transparent so it shows through");

        rig.Host.SetWindowMaterial(WindowMaterial.None, dark: true, ordered: false);
        Assert.True(Tok.WindowBackground.A > 0.99f, "no material: the clear is the opaque theme ground");
        Assert.Equal(Tok.T.WindowBackground, Tok.WindowBackground);

        rig.Host.SetWindowMaterial(WindowMaterial.Acrylic, dark: true, ordered: false);
        Assert.True(Tok.WindowBackground.A <= 0.004f);
    }

    [Fact]
    public void A_NonComposited_Window_Keeps_An_Opaque_Ground_Whatever_The_Material()
    {
        using var rig = new Rig(composited: false);
        rig.Host.SetWindowMaterial(WindowMaterial.Mica, dark: true, ordered: false);
        Assert.True(Tok.WindowBackground.A > 0.99f);
    }

    [Theory]
    [InlineData(WindowMaterial.None, true, false, false, false, true)]
    [InlineData(WindowMaterial.None, false, false, false, false, false)]
    [InlineData(WindowMaterial.Mica, true, false, true, true, true)]
    [InlineData(WindowMaterial.MicaAlt, true, false, true, true, true)]
    [InlineData(WindowMaterial.Acrylic, true, false, true, true, true)]
    [InlineData(WindowMaterial.Mica, true, true, true, false, true)]       // a custom frame owns zero margins
    [InlineData(WindowMaterial.Mica, false, false, false, false, false)]   // an opaque swapchain cannot show a backdrop through
    [InlineData(WindowMaterial.Acrylic, false, true, false, false, false)]
    public void Material_Composited_And_Frame_Combinations(WindowMaterial material, bool composited, bool customFrame,
        bool transparentGround, bool extendGlass, bool videoCapable)
    {
        var r = WindowMaterialPolicy.Resolve(material, composited, customFrame);
        Assert.Equal(material, r.Material);
        Assert.Equal(transparentGround, r.TransparentGround);
        Assert.Equal(extendGlass, r.ExtendGlass);
        Assert.Equal(videoCapable, r.VideoCapable);
    }

    [Theory]
    [InlineData(WindowMaterial.None)]
    [InlineData(WindowMaterial.Mica)]
    [InlineData(WindowMaterial.MicaAlt)]
    [InlineData(WindowMaterial.Acrylic)]
    public void Video_Requires_A_Composited_Swapchain_Whatever_The_Material(WindowMaterial material)
    {
        Assert.True(WindowMaterialPolicy.Resolve(material, composited: true, customFrame: false).VideoCapable);
        Assert.False(WindowMaterialPolicy.Resolve(material, composited: false, customFrame: false).VideoCapable);
        Assert.False(WindowMaterialPolicy.Resolve(material, composited: false, customFrame: true).VideoCapable);
    }

    [Fact]
    public void The_Legacy_Mica_Flags_Map_To_A_Material()
    {
        Assert.Equal(WindowMaterial.Mica, WindowMaterialPolicy.FromLegacy(mica: true, micaAlt: false));
        Assert.Equal(WindowMaterial.MicaAlt, WindowMaterialPolicy.FromLegacy(mica: true, micaAlt: true));
        Assert.Equal(WindowMaterial.None, WindowMaterialPolicy.FromLegacy(mica: false, micaAlt: true));
    }

    [Fact]
    public void A_Theme_Material_Policy_Names_A_Material_Per_Kind_And_Counts_As_A_NonColour_Change()
    {
        var none = ThemeDefinition.FluentDefault;
        Assert.Null(none.Materials.For(ThemeKind.Dark));
        var zune = none with { Materials = new MaterialPolicy { Light = WindowMaterial.None, Dark = WindowMaterial.None } };
        Assert.Equal(WindowMaterial.None, zune.Materials.For(ThemeKind.Dark));
        Assert.Equal(WindowMaterial.None, zune.Materials.For(ThemeKind.Light));
        Assert.False(none.DiffersOnlyInColours(zune));   // a material change is applied as a unit, not cross-faded
    }
}
