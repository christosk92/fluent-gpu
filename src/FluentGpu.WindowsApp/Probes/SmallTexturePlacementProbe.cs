using System;
using System.IO;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace FluentGpu;

internal static class SmallTexturePlacementProbe
{
    public static int Run(string? outputDirectory, bool productionPool = false)
    {
        try
        {
            outputDirectory ??= ".tmp/small-texture-placement";
            Directory.CreateDirectory(outputDirectory);
            using var app = new Win32App();
            using var window = app.CreateWindow(new WindowDesc("FluentGpu - small texture placement", new Size2(512, 256), 1f));
            window.Show();
            var events = new InputEventRing(); window.PumpInto(events); events.Clear();
            using var device = new D3D12Device(new StringTable());
            device.EnsureDeviceCreated();
            device.CreateSwapchain(new SwapchainDesc(window.Handle, window.ClientSizePx));
            var result = productionPool ? device.ProbeSmallImagePool() : device.ProbeSmallTexturePlacement();
            if (!result.Supported)
            {
                Console.Error.WriteLine($"[small-texture] UNSUPPORTED {result.Detail}");
                return 2;
            }
            string[] names = ["placed-original", "committed-original", "placed-updated", "committed-updated"];
            for (int i = 0; i < names.Length; i++)
                PngWriter.WriteBgra(Path.Combine(outputDirectory, names[i] + ".png"), result.Frames[i], result.Width, result.Height);
            Console.Error.WriteLine($"[small-texture] PASS {result.Detail}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[small-texture] FAIL {ex}");
            return 1;
        }
    }
}
