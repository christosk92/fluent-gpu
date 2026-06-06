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
        return Panel(new Edges4(28, 24, 28, 28), 16,
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
        int resizeTest = 0;
        string backend = "d3d12";   // the real backend by default; "gdi" for the bring-up renderer
        string present = "dcomp";   // dcomp = composited (Mica); "hwnd" = opaque flip swapchain (fallback)
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out int f)) maxFrames = f;
            if (args[i] == "--resize-test" && i + 1 < args.Length && int.TryParse(args[i + 1], out int r)) resizeTest = r;
            if (args[i] == "--backend" && i + 1 < args.Length) backend = args[i + 1].ToLowerInvariant();
            if (args[i] == "--present" && i + 1 < args.Length) present = args[i + 1].ToLowerInvariant();
        }
        bool composited = backend == "d3d12" && present != "hwnd";   // Mica needs the composited D3D12 path

        Diag.Sink = Console.WriteLine;   // route engine diagnostics to the console (stripped on release builds)

        var strings = new StringTable();
        using var app = new Win32App();
        var window = (Win32Window)app.CreateWindow(new WindowDesc("FluentGpu — Counter", new Size2(480, 320), 1f, composited));

        // Pull the real system accent + apply dark titlebar / Mica backdrop (Windows 11).
        if (Win32Theme.Accent() is { } a) Theme.Accent = ColorF.FromRgba(a.R, a.G, a.B);
        Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark);
        if (composited) Theme.WindowBackground = ColorF.Transparent;   // clear transparent → Mica shows through

        var fonts = new GdiFontSystem(strings);   // GDI metrics drive layout for both backends (DirectWrite font system is next)
        IGpuDevice device = backend == "gdi" ? new GdiGpuDevice(strings) : new D3D12Device(strings, composited);
        var root = new Counter();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        window.Show();

        if (resizeTest > 0)
        {
            host.RunFrame();
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            proc.Refresh();
            long before = proc.PrivateMemorySize64;
            for (int i = 0; i < resizeTest; i++)
            {
                window.SetClientSize(420 + (i * 7) % 360, 300 + (i * 5) % 240);   // vary like a live drag
                host.RunFrame();                                                  // pumps WM_SIZE → resize path
            }
            GC.Collect(); GC.WaitForPendingFinalizers();
            proc.Refresh();
            long after = proc.PrivateMemorySize64;
            Console.WriteLine($"RESIZE-TEST backend={device.BackendName} n={resizeTest} | private MB before={before / 1048576.0:0.0} after={after / 1048576.0:0.0} delta={(after - before) / 1048576.0:+0.0;-0.0} | managed MB={GC.GetTotalMemory(true) / 1048576.0:0.0}");
            Diag.Dump($"{backend} after {resizeTest} resizes");
            return 0;
        }

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
