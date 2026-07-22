using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;


namespace FluentGpu.VerticalSlice.Harness;

/// <summary>Using-friendly headless AppHost bootstrap for suite checks.</summary>
public sealed class HeadlessFixture : IDisposable
{
    public HeadlessPlatformApp App { get; }
    public HeadlessWindow Window { get; }
    public HeadlessGpuDevice Device { get; }
    public HeadlessFontSystem Fonts { get; }
    public StringTable Strings { get; }
    public AppHost Host { get; }

    public HeadlessFixture(StringTable strings, Component root, string title = "FluentGpu slice", float w = 480, float h = 320)
    {
        Strings = strings;
        App = new HeadlessPlatformApp();
        Window = new HeadlessWindow(new WindowDesc(title, new Size2(w, h), 1f));
        Window.Show();
        Device = new HeadlessGpuDevice();
        Fonts = new HeadlessFontSystem(strings);
        Host = new AppHost(App, Window, Device, Fonts, strings, root);
    }

    public void Dispose()
    {
        Host.Dispose();
        App.Dispose();
    }
}
