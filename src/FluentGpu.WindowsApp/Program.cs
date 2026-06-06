using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Gdi;
using FluentGpu.Rhi.D3D12;
using static FluentGpu.Dsl.Ui;

// The same Counter as the headless slice — now drawn to a real Win32 window via the GDI bring-up backend.
sealed class Counter : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return VStack(12,
            Heading($"Count: {count}"),
            HStack(8,
                Button("-", () => setCount(count - 1)),
                Button("+", () => setCount(count + 1))));
    }
}

static class WindowsApp
{
    static int Main(string[] args)
    {
        // --frames N : render N frames then exit (for headless/CI). Default: run until the window closes.
        int maxFrames = -1;
        string backend = "d3d12";   // the real backend by default; "gdi" for the bring-up renderer
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out int f)) maxFrames = f;
            if (args[i] == "--backend" && i + 1 < args.Length) backend = args[i + 1].ToLowerInvariant();
        }

        Diag.Sink = Console.WriteLine;   // route engine diagnostics to the console (stripped on release builds)

        var strings = new StringTable();
        using var app = new Win32App();
        var window = (Win32Window)app.CreateWindow(new WindowDesc("FluentGpu — Counter", new Size2(480, 320), 1f));
        var fonts = new GdiFontSystem(strings);   // GDI metrics drive layout for both backends (DirectWrite font system is next)
        IGpuDevice device = backend == "gdi" ? new GdiGpuDevice(strings) : new D3D12Device(strings);
        var root = new Counter();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        window.Show();

        int n = 0;
        while (!window.IsClosed)
        {
            host.RunFrame();
            n++;
            if (n == 2) Diag.Dump($"{backend} frame 2");
            if (maxFrames > 0 && n >= maxFrames) break;
            Thread.Sleep(8);   // ~120 Hz cap; the real loop would block on the frame-latency waitable
        }

        Console.WriteLine($"FluentGpu WindowsApp: rendered {n} frame(s) on the {device.BackendName} backend; " +
                          $"window {(window.IsClosed ? "closed by user" : "exited after --frames")}.");
        return 0;
    }
}
