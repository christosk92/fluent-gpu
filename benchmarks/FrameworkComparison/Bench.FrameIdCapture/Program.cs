using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Bench.Contracts;

namespace Bench.FrameIdCapture;

/// <summary>
/// Samples desktop pixels at the host's frame-ID probe and joins them to the host mutation log.
/// This answers "which framework makes updates visible sooner?" without trusting PresentMon
/// process attribution or WinUI CompositionTarget.Rendering.
/// </summary>
internal static class Program
{
    /// <summary>Upper bound on the physical size of the 48-DIP probe (covers scale factors up to 4x).</summary>
    private const int MaxProbePx = 192;
    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    private static int Main(string[] args)
    {
        try
        {
            // Must be per-monitor DPI aware: ClientToScreen/BitBlt below work in physical pixels, and the
            // probe rect is authored in DIPs. Without this the two coordinate spaces disagree on a scaled
            // display and every sample decodes as garbage (parity check rejects it), yielding zero samples.
            _ = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

            Options options = Options.Parse(args);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.HostOutput))!);
            string mutationLog = FrameIdProbe.DefaultMutationLogPath(options.HostOutput);
            string sampleLog = FrameIdProbe.DefaultSampleLogPath(options.HostOutput);
            string visibilityPath = FrameIdProbe.DefaultVisibilityPath(options.HostOutput);

            var startInfo = new ProcessStartInfo(options.Executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(options.Executable)!,
            };
            startInfo.ArgumentList.Add("--scenario");
            startInfo.ArgumentList.Add(options.Scenario);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(options.HostOutput);
            startInfo.ArgumentList.Add("--pass");
            startInfo.ArgumentList.Add(options.Pass);
            startInfo.ArgumentList.Add("--iterations");
            startInfo.ArgumentList.Add(options.Iterations.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--warmup");
            startInfo.ArgumentList.Add(options.Warmup.ToString(CultureInfo.InvariantCulture));

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not launch {options.Executable}.");

            int lastId = int.MinValue;
            int samplesWritten = 0;
            // The probe rect is authored in DIPs; on a scaled display it is up to MaxProbePx physical pixels.
            var probeBuffer = new byte[MaxProbePx * MaxProbePx * 4];
            nint hwnd = WaitForMainWindow(process, TimeSpan.FromSeconds(30));

            using (var sampleWriter = new StreamWriter(
                       new FileStream(sampleLog, FileMode.Create, FileAccess.Write, FileShare.Read))
                   {
                       AutoFlush = true,
                       NewLine = "\n",
                   })
            {
                while (!process.HasExited)
                {
                    if (hwnd == 0 || !IsWindow(hwnd))
                    {
                        hwnd = FindMainWindow(process.Id);
                        if (hwnd == 0)
                        {
                            Thread.Sleep(1);
                            continue;
                        }
                    }

                    if (TrySampleProbe(hwnd, probeBuffer, out byte r, out byte g, out byte b) &&
                        FrameIdProbe.TryDecode(r, g, b, out int frameId) &&
                        frameId != lastId)
                    {
                        long qpc = Stopwatch.GetTimestamp();
                        lastId = frameId;
                        sampleWriter.WriteLine(
                            "{\"qpc\":" + qpc.ToString(CultureInfo.InvariantCulture) +
                            ",\"frameId\":" + frameId.ToString(CultureInfo.InvariantCulture) +
                            ",\"r\":" + r.ToString(CultureInfo.InvariantCulture) +
                            ",\"g\":" + g.ToString(CultureInfo.InvariantCulture) +
                            ",\"b\":" + b.ToString(CultureInfo.InvariantCulture) + "}");
                        samplesWritten++;
                    }

                    Thread.Sleep(options.SampleMilliseconds);
                }
            }

            process.WaitForExit();
            if (!File.Exists(mutationLog))
                throw new InvalidOperationException($"Host did not write mutation log: {mutationLog}");

            List<FrameIdMutation> mutations = ReadWithRetry(FrameIdLogReader.ReadMutations, mutationLog);
            List<FrameIdSample> samples = ReadWithRetry(FrameIdLogReader.ReadSamples, sampleLog);
            // Keep only measured-pass mutations (iteration index used by hosts during measure loop).
            mutations = mutations.FindAll(static m => m.Iteration >= 0);

            FrameIdVisibilityResult result = FrameIdVisibilityResult.Join(
                options.Framework,
                options.Scenario,
                mutations,
                samples,
                $"Desktop BitBlt of client probe ({FrameIdProbe.SizePx}px). samplesWritten={samplesWritten}; hostExit={process.ExitCode}. Not a photon measurement — use a high-speed camera for that.");
            result.Write(visibilityPath);
            Console.WriteLine(visibilityPath);
            Console.WriteLine(
                $"observed={result.Observed}/{result.Mutations} missing={result.Missing} " +
                $"p50={Fmt(result.P50Ms)} p95={Fmt(result.P95Ms)} p99={Fmt(result.P99Ms)} max={Fmt(result.MaxMs)}");
            return process.ExitCode == 0 && result.Missing == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>
    /// The logs are closed just before this runs, but a scanner can still hold a transient share lock on a
    /// freshly-closed file. Retry briefly rather than losing an entire capture run to it.
    /// </summary>
    private static List<T> ReadWithRetry<T>(Func<string, List<T>> read, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return read(path); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
        }
    }

    private static string Fmt(double? value)
        => value is null ? "n/a" : value.Value.ToString("0.000", CultureInfo.InvariantCulture) + "ms";

    private static nint WaitForMainWindow(Process process, TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline && !process.HasExited)
        {
            nint hwnd = FindMainWindow(process.Id);
            if (hwnd != 0) return hwnd;
            Thread.Sleep(20);
        }
        return FindMainWindow(process.Id);
    }

    private static nint FindMainWindow(int processId)
    {
        nint found = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if ((int)pid != processId || !IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            _ = GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (title.Contains("benchmark", StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    private static unsafe bool TrySampleProbe(nint hwnd, byte[] bgra, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        // FrameIdProbe.ClientX/Y/SizePx are DIPs against a 1200-DIP-wide client. Scale them to physical
        // pixels using the real client width, otherwise we sample scenario content instead of the probe.
        if (!GetClientRect(hwnd, out RECT client)) return false;
        int clientWidth = client.Right - client.Left;
        if (clientWidth <= 0) return false;
        double scale = clientWidth / (double)BenchWorkload.WindowWidth;
        int size = (int)Math.Round(FrameIdProbe.SizePx * scale);
        if (size < 8) size = 8;
        if (size > MaxProbePx) size = MaxProbePx;

        if (!ClientToScreen(hwnd, out POINT topLeft)) return false;
        topLeft.X += (int)Math.Round(FrameIdProbe.ClientX * scale);
        topLeft.Y += (int)Math.Round(FrameIdProbe.ClientY * scale);

        nint screenDc = GetDC(0);
        if (screenDc == 0) return false;
        nint memDc = CreateCompatibleDC(screenDc);
        nint bitmap = CreateCompatibleBitmap(screenDc, size, size);
        nint old = SelectObject(memDc, bitmap);
        try
        {
            if (!BitBlt(memDc, 0, 0, size, size,
                    screenDc, topLeft.X, topLeft.Y, 0x00CC0020 /* SRCCOPY */))
                return false;

            var info = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            fixed (byte* p = bgra)
            {
                int got = GetDIBits(memDc, bitmap, 0, (uint)size, (nint)p, ref info, 0);
                if (got == 0) return false;
            }
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }

        // Median of the center 8×8 to tolerate filtering / ClearType-adjacent bleed.
        int x0 = size / 2 - 4;
        int y0 = size / 2 - 4;
        Span<byte> rs = stackalloc byte[64];
        Span<byte> gs = stackalloc byte[64];
        Span<byte> bs = stackalloc byte[64];
        int n = 0;
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++, n++)
        {
            int i = ((y0 + y) * size + (x0 + x)) * 4;
            bs[n] = bgra[i + 0];
            gs[n] = bgra[i + 1];
            rs[n] = bgra[i + 2];
        }
        r = Median64(rs);
        g = Median64(gs);
        b = Median64(bs);
        return true;
    }

    private static byte Median64(Span<byte> values)
    {
        // Insertion sort of 64 bytes — capture path, not frame phases 6–13.
        for (int i = 1; i < values.Length; i++)
        {
            byte key = values[i];
            int j = i - 1;
            while (j >= 0 && values[j] > key)
            {
                values[j + 1] = values[j];
                j--;
            }
            values[j + 1] = key;
        }
        return values[values.Length / 2];
    }

    private sealed record Options(
        string Executable,
        string Framework,
        string Scenario,
        string HostOutput,
        string Pass,
        int Iterations,
        int Warmup,
        int SampleMilliseconds)
    {
        public static Options Parse(string[] args)
        {
            string exe = Req(args, "--exe") ?? throw new ArgumentException("--exe is required");
            string output = Req(args, "--output") ?? throw new ArgumentException("--output is required");
            return new Options(
                Path.GetFullPath(exe),
                Req(args, "--framework") ?? "unknown",
                Req(args, "--scenario") ?? BenchScenarios.VirtualScroll10K,
                Path.GetFullPath(output),
                Req(args, "--pass") ?? "cadence",
                Int(args, "--iterations", 400),
                Int(args, "--warmup", 60),
                Int(args, "--sample-ms", 1));
        }

        private static string? Req(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static int Int(string[] args, string name, int fallback)
        {
            string? value = Req(args, name);
            return value is null ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
        }
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint ho);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, nint bits, ref BITMAPINFOHEADER bmi, uint usage);
}
