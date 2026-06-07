using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Rhi.Gdi;

namespace FluentGpu;

/// <summary>
/// Batteries-included entry point — the whole SDK in one call. <c>FluentApp.Run(() =&gt; new MyApp())</c> creates a
/// DPI-aware window, brings up D3D12, applies Mica + the real system accent, wires the font system + frame loop, and
/// renders your root component. No PAL/RHI/AppHost plumbing to think about — just write components.
/// </summary>
public static class FluentApp
{
    public static void Run(Func<Component> root, string title = "FluentGpu", int width = 800, int height = 600,
                           bool mica = true, int frames = -1)
    {
        Diag.Sink = Console.Error.WriteLine;   // engine diagnostics → console (Debug only; compiled out on Release)
        var strings = new StringTable();
        using var app = new Win32App();
        var window = (Win32Window)app.CreateWindow(new WindowDesc(title, new Size2(width, height), 1f, mica));

        if (Win32Theme.AccentLight2() is { } a) Theme.Accent = ColorF.FromRgba(a.R, a.G, a.B);
        else if (Win32Theme.Accent() is { } b) Theme.Accent = ColorF.FromRgba(b.R, b.G, b.B);
        Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark, mica);
        if (mica) Theme.WindowBackground = ColorF.Transparent;

        var fonts = new GdiFontSystem(strings);
        IGpuDevice device = new D3D12Device(strings, composited: mica);
        using var host = new AppHost(app, window, device, fonts, strings, root());
        host.SmoothScroll = true;   // inertial wheel scrolling + auto-hiding scrollbars (the real-app default)

        window.Show();

        int n = 0;
        while (!window.IsClosed)
        {
            host.RunFrame();
            n++;
            if (frames > 0 && n >= frames) break;
            window.WaitForWork(host.HasActiveWork ? 8 : -1);
        }
    }

    /// <summary><c>FluentApp.Run&lt;MyApp&gt;()</c> — same, for a parameterless root component.</summary>
    public static void Run<T>(string title = "FluentGpu", int width = 800, int height = 600) where T : Component, new()
        => Run(() => new T(), title, width, height);
}
