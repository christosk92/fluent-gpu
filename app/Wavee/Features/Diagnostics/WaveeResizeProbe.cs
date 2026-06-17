using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace Wavee;

internal static partial class WaveeResizeProbe
{
    const int Height = 760;
    const uint WM_ENTERSIZEMOVE = 0x0231;
    const uint WM_EXITSIZEMOVE = 0x0232;
    const uint WM_TIMER = 0x0113;
    const nuint MoveLoopTimerId = 1;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (!Diag.EnvFlag("WAVEE_RESIZE_PROBE")) return false;
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Console.Error.WriteLine("[wavee-resize-probe] unavailable: requires Win32Window + D3D12Device");
            return true;
        }

        Run(host, w, gpu);
        return true;
    }

    static void Run(AppHost host, Win32Window window, D3D12Device gpu)
    {
        Directory.CreateDirectory(@"C:\tmp");
        string csvPath = @"C:\tmp\wavee-resize-probe.csv";
        string summaryPath = @"C:\tmp\wavee-resize-probe-summary.txt";
        int[] widths =
        [
            1500, 1320, 1241, 1240, 1239, 1181, 1180, 1179, 1101, 1100, 1099,
            1011, 1010, 1009, 901, 900, 899, 861, 860, 859, 761, 760, 759,
            701, 700, 699, 681, 680, 679, 621, 620, 619, 561, 560, 559,
            541, 540, 539, 521, 520, 519, 441, 440, 439, 401, 400, 399,
            360, 520, 760, 1180, 1500
        ];
        int[] screenshots = [1500, 1180, 1010, 760, 680, 540, 440, 360];

        for (int i = 0; i < 8 && !window.IsClosed; i++) host.RunFrame();

        RunBandAnimCapture(host, window, gpu);

        var csv = new StringBuilder(4096);
        csv.AppendLine("step,width,kind,elapsedMs,frameMs,rendered,components,nodes,drawCommands,hotAlloc,fps");
        double maxResize = 0, sumResize = 0;
        int resizeRows = 0, maxResizeWidth = 0;

        for (int i = 0; i < widths.Length && !window.IsClosed; i++)
        {
            int width = widths[i];
            long start = Stopwatch.GetTimestamp();
            window.SetClientSize(width, Height);                 // real WM_SIZE -> PaintRequested -> AppHost.Paint
            double resizeMs = ElapsedMs(start);
            var resizeStats = host.LastStats;
            AddRow(csv, i, width, "wm-size", resizeMs, resizeStats);
            if (resizeMs > maxResize) { maxResize = resizeMs; maxResizeWidth = width; }
            sumResize += resizeMs; resizeRows++;

            start = Stopwatch.GetTimestamp();
            var settle = host.RunFrame();                        // drains any follow-up frame requested by the resize paint
            AddRow(csv, i, width, "settle", ElapsedMs(start), settle);

            if (Contains(screenshots, width))
            {
                host.RunFrame();
                string png = $@"C:\tmp\wavee-resize-{width}.png";
                var px = gpu.CaptureBgra(out int cw, out int ch);
                PngWriter.WriteBgra(png, px, cw, ch);
                Console.Error.WriteLine($"[wavee-resize-probe] screenshot {png} ({cw}x{ch})");
            }
        }

        RunWithinBandProbe(host, window, csv);
        RunModalResizeProbe(host, window, csv);
        RunMoveProbe(host, window, csv);

        File.WriteAllText(csvPath, csv.ToString());
        string summary =
            $"rows={resizeRows}, avgResizeMs={(resizeRows == 0 ? 0 : sumResize / resizeRows).ToString("0.00", CultureInfo.InvariantCulture)}, " +
            $"maxResizeMs={maxResize.ToString("0.00", CultureInfo.InvariantCulture)} at width={maxResizeWidth}";
        File.WriteAllText(summaryPath, summary + Environment.NewLine);
        Console.Error.WriteLine($"[wavee-resize-probe] wrote {csvPath}");
        Console.Error.WriteLine($"[wavee-resize-probe] {summary}");
    }

    // Within-ONE-band resizes (all widths >= 1240 => PlayerBar band 15 => NO re-render): isolates the pure full-root
    // FlexLayout cost from the band-crossing reconcile, so we can tell whether the "layout" segment is flex or reconcile.
    static void RunWithinBandProbe(AppHost host, Win32Window window, StringBuilder csv)
    {
        int[] widths = [1480, 1300, 1481, 1301, 1482, 1302, 1483, 1303, 1484, 1304, 1485, 1305];
        Console.Error.WriteLine("[wavee-resize-probe] within-band start (expect comps=0)");
        for (int i = 0; i < widths.Length && !window.IsClosed; i++)
        {
            long start = Stopwatch.GetTimestamp();
            window.SetClientSize(widths[i], Height);
            AddRow(csv, i, widths[i], "within-band", ElapsedMs(start), host.LastStats);
            host.RunFrame();   // drain settle
        }
        Console.Error.WriteLine("[wavee-resize-probe] within-band done");
    }

    // WAVEE_ANIM_CAP=1: capture the Devices-button Enter (cross 1180 upward) then Exit (cross back) frame-by-frame from
    // the swapchain (no DWM/screen-margin issues). Real wall-time sleeps advance the one-shot transition so consecutive
    // frames show the button fading/scaling if the ItemMotion enter/exit is actually playing on a resize band crossing.
    static void RunBandAnimCapture(AppHost host, Win32Window window, D3D12Device gpu)
    {
        if (!Diag.EnvFlag("WAVEE_ANIM_CAP")) return;
        // Bands are DIP; SetClientSize is PHYSICAL px → multiply by the DPI scale so we actually cross 1180 DIP.
        float scale = window.Scale <= 0f ? 1f : window.Scale;
        int belowPx = (int)(1160 * scale);   // 1160 DIP — Devices hidden
        int abovePx = (int)(1240 * scale);   // 1240 DIP — Devices (1180) AND Expand-edge clear; well past the band
        Console.Error.WriteLine($"[anim-cap] scale={scale:0.00} belowPx={belowPx} abovePx={abovePx}");
        Console.Error.WriteLine("[anim-cap] enter: cross 1180 upward (Devices appears)");
        window.SetClientSize(belowPx, Height); for (int i = 0; i < 5 && !window.IsClosed; i++) host.RunFrame();
        window.SetClientSize(abovePx, Height);  // crosses 1180 → Devices mounts → SeedEnter
        for (int k = 0; k < 16 && !window.IsClosed; k++)
        {
            host.RunFrame();
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra($@"C:\tmp\anim_enter_{k:D2}.png", px, cw, ch);
            System.Threading.Thread.Sleep(8);
        }
        Console.Error.WriteLine("[anim-cap] exit: cross 1180 downward (Devices disappears)");
        window.SetClientSize(belowPx, Height);  // crosses 1180 → Devices unmounts → SeedExit (orphan fades)
        for (int k = 0; k < 10 && !window.IsClosed; k++)
        {
            host.RunFrame();
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra($@"C:\tmp\anim_exit_{k:D2}.png", px, cw, ch);
            System.Threading.Thread.Sleep(22);
        }
        Console.Error.WriteLine("[anim-cap] done");
    }

    static void RunMoveProbe(AppHost host, Win32Window window, StringBuilder csv)
    {
        nint hwnd = window.Handle.Value;
        var origin = window.ClientOriginPx;
        Console.Error.WriteLine("[wavee-resize-probe] modal-move start");
        SendMessageW(hwnd, WM_ENTERSIZEMOVE, 0, 0);
        for (int i = 0; i < 20 && !window.IsClosed; i++)
        {
            long start = Stopwatch.GetTimestamp();
            SetWindowPos(hwnd, 0, (int)origin.X + 12 + i * 3, (int)origin.Y + 12 + (i & 1), 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            double moveMs = ElapsedMs(start);
            AddRow(csv, i, -1, "wm-move", moveMs, host.LastStats);

            start = Stopwatch.GetTimestamp();
            SendMessageW(hwnd, WM_TIMER, MoveLoopTimerId, 0);
            AddRow(csv, i, -1, "move-timer", ElapsedMs(start), host.LastStats);
        }
        SendMessageW(hwnd, WM_EXITSIZEMOVE, 0, 0);
        Console.Error.WriteLine("[wavee-resize-probe] modal-move done");
    }

    static void RunModalResizeProbe(AppHost host, Win32Window window, StringBuilder csv)
    {
        nint hwnd = window.Handle.Value;
        int[] burst = [1480, 1460, 1440, 1420, 1400, 1380, 1360, 1340, 1320, 1300, 1280, 1260, 1240, 1220, 1200, 1180];
        Console.Error.WriteLine("[wavee-resize-probe] modal-resize burst start");
        SendMessageW(hwnd, WM_ENTERSIZEMOVE, 0, 0);
        for (int i = 0; i < burst.Length && !window.IsClosed; i++)
        {
            long start = Stopwatch.GetTimestamp();
            window.SetClientSize(burst[i], Height);
            AddRow(csv, i, burst[i], "modal-size-msg", ElapsedMs(start), host.LastStats);
        }

        long pumpStart = Stopwatch.GetTimestamp();
        var pumped = host.RunFrame();
        AddRow(csv, 0, burst[^1], "modal-size-pump", ElapsedMs(pumpStart), pumped);

        long exitStart = Stopwatch.GetTimestamp();
        SendMessageW(hwnd, WM_EXITSIZEMOVE, 0, 0);
        AddRow(csv, 0, burst[^1], "modal-size-exit", ElapsedMs(exitStart), host.LastStats);
        Console.Error.WriteLine("[wavee-resize-probe] modal-resize burst done");
    }

    static void AddRow(StringBuilder csv, int step, int width, string kind, double elapsedMs, FrameStats s)
    {
        csv.Append(step.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(width.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(kind).Append(',')
           .Append(elapsedMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
           .Append(s.FrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
           .Append(s.Rendered ? "1" : "0").Append(',')
           .Append(s.ComponentsRendered.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.NodesVisited.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.DrawCommandCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.HotPhaseAllocBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.Fps.ToString("0.0", CultureInfo.InvariantCulture)).AppendLine();
    }

    static double ElapsedMs(long start)
        => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    static bool Contains(int[] values, int value)
    {
        for (int i = 0; i < values.Length; i++)
            if (values[i] == value) return true;
        return false;
    }
}
