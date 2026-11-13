using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Signals;

namespace FluentGpu;

/// <summary>
/// <c>--live-resize-probe [outDir]</c>: the real-window proof of <see cref="DetachedWindowRequest.LiveResize"/>. Opens a
/// detached window twice, once opted in and once with the default composited defer, and drives each window's own size loop
/// from the UI thread without touching the cursor: <c>WM_ENTERSIZEMOVE</c>, 30 <c>SetWindowPos</c> steps that grow and
/// shrink both axes (the second half from the left/top edge, so the origin moves too; each raises <c>WM_SIZE</c>
/// synchronously while the loop flag is set), then <c>WM_EXITSIZEMOVE</c>. For every step it records whether the child laid
/// out at that step's client size (the <see cref="Viewport.Size"/> its content rendered with) and presented that very frame
/// (<see cref="FrameStats.PresentedPublishSeq"/> reached its <see cref="FrameStats.PublishSeq"/>) before the step returned, and prints one verdict line per window. The
/// engine's own always-on <c>[window.size] end ... paints=</c> line follows each loop. stderr is teed to
/// <c>&lt;outDir&gt;/live-resize.log</c>. Needs the Win32 + D3D12 backend and a composited desktop. Exit 0 when the opted-in
/// window followed every step and the default window laid out none of them before mouse-up; 1 otherwise; 2 unsupported.
/// </summary>
internal static class LiveResizeProbe
{
    /// <summary>Set by Program.cs from the CLI; null unless <c>--live-resize-probe</c> was passed.</summary>
    public static string? OutDir;

    /// <summary>The verdict, read by Program.cs after <c>FluentAppHarness.Run</c> returns.</summary>
    public static int ExitCode;

    private const int Steps = 30;
    private const float StepDip = 12f;
    private static readonly Size2 StartDip = new(360f, 240f);

    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (OutDir is not { } outDir) return false;
        if (window is not Win32Window)
        {
            Console.Error.WriteLine("[live-resize] needs the Win32 backend (a real window).");
            ExitCode = 2;
            return true;
        }
        Directory.CreateDirectory(outDir);
        var originalErr = Console.Error;
        using var file = new StreamWriter(File.Create(Path.Combine(outDir, "live-resize.log")), Encoding.UTF8) { AutoFlush = true };
        Console.SetError(new Tee(originalErr, file));
        try
        {
            for (int i = 0; i < 20 && !window.IsClosed; i++) host.RunFrame();
            bool liveOk = RunOne(host, live: true, out _);
            bool deferOk = RunOne(host, live: false, out _);
            ExitCode = liveOk && deferOk ? 0 : 1;
            Console.Error.WriteLine($"[live-resize] verdict={(ExitCode == 0 ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live-resize] threw: {ex}");
            ExitCode = 1;
        }
        finally
        {
            Console.Error.Flush();
            Console.SetError(originalErr);
        }
        return true;
    }

    private static bool RunOne(AppHost host, bool live, out string line)
    {
        string title = live ? "fg-live-resize-probe-live" : "fg-live-resize-probe-defer";
        ProbeChild.LastViewport = default;
        ProbeChild.Renders = 0;
        var child = host.OpenDetachedWindow(new DetachedWindowRequest(title, StartDip, new ProbeChild(), AlwaysOnTop: false,
            MinClientSizeDip: new Size2(232f, 48f), NoActivate: true, ToolWindow: true,
            LiveResize: live, MaxClientSizeDip: new Size2(720f, 900f)));
        if (child is null) { line = "[live-resize] OpenDetachedWindow returned null"; Console.Error.WriteLine(line); return false; }
        // The step's own frame reached the screen when, at the end of that frame, the newest presented publication was its own.
        bool lastPresentedOwn = false;
        int frames = 0;
        child.FrameCompleted = s => { lastPresentedOwn = s.PublishSeq != 0 && s.PresentedPublishSeq >= s.PublishSeq; frames++; };
        bool revealed = false;
        child.OnRevealed = _ => revealed = true;
        var sw = Stopwatch.StartNew();
        while (!revealed && sw.ElapsedMilliseconds < 3000) { host.RunFrame(); host.TickDetachedHosts(); }
        for (int i = 0; i < 10; i++) { host.RunFrame(); host.TickDetachedHosts(); }

        nint hwnd = FindWindowW(null, title);
        if (hwnd == 0) { line = $"[live-resize] {title}: window not found"; Console.Error.WriteLine(line); child.Close(); return false; }
        float scale = GetDpiForWindow(hwnd) / 96f;
        GetWindowRect(hwnd, out var outer);
        var stepMs = new List<double>(Steps);
        int followed = 0, laidOutAny = 0;

        SendMessageW(hwnd, WmEnterSizeMove, 0, 0);
        int x = outer.Left, y = outer.Top, w = outer.Right - outer.Left, h = outer.Bottom - outer.Top;
        int d = (int)MathF.Round(StepDip * scale);
        for (int i = 0; i < Steps; i++)
        {
            // First half: grow from the right/bottom edge (the origin stays). Second half: shrink from the left/top edge (the
            // origin moves with the edge, the shape a left/top drag has).
            if (i < Steps / 2) { w += d; h += d; }
            else { x += d; y += d; w -= d; h -= d; }
            int framesBefore = frames;
            lastPresentedOwn = false;
            long t0 = Stopwatch.GetTimestamp();
            SetWindowPos(hwnd, 0, x, y, w, h, SwpNoZOrder | SwpNoActivate);
            stepMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            GetClientRect(hwnd, out var rc);
            var want = new Size2(rc.Right / scale, rc.Bottom / scale);
            var got = ProbeChild.LastViewport;
            bool laidOut = frames > framesBefore && MathF.Abs(got.Width - want.Width) < 0.75f && MathF.Abs(got.Height - want.Height) < 0.75f;
            if (laidOut) laidOutAny++;
            if (laidOut && lastPresentedOwn) followed++;
        }
        SendMessageW(hwnd, WmExitSizeMove, 0, 0);
        for (int i = 0; i < 10; i++) { host.RunFrame(); host.TickDetachedHosts(); }
        GetClientRect(hwnd, out var end);
        var endWant = new Size2(end.Right / scale, end.Bottom / scale);
        bool settled = MathF.Abs(ProbeChild.LastViewport.Width - endWant.Width) < 0.75f && MathF.Abs(ProbeChild.LastViewport.Height - endWant.Height) < 0.75f;

        stepMs.Sort();
        double median = stepMs[stepMs.Count / 2], worst = stepMs[^1];
        bool ok = live ? followed == Steps && settled : laidOutAny == 0 && settled;
        line = $"[live-resize] window={(live ? "live" : "defer")} steps={Steps} laidOutAtSize={laidOutAny} presentedAtSize={followed} " +
            $"settledAtEnd={(settled ? 1 : 0)} stepMs.median={median:0.00} stepMs.worst={worst:0.00} scale={scale:0.##} verdict={(ok ? "PASS" : "FAIL")}";
        Console.Error.WriteLine(line);
        child.Close();
        for (int i = 0; i < 60 && child.IsOpen; i++) { host.RunFrame(); host.TickDetachedHosts(); }
        return ok;
    }

    /// <summary>The probe window's content: shows the viewport it laid out with and records it for the step check.</summary>
    private sealed class ProbeChild : Component
    {
        public static Size2 LastViewport;
        public static int Renders;

        public override Element Render()
        {
            Size2 vp = UseContext(Viewport.Size);
            LastViewport = vp;
            Renders++;
            return new BoxEl
            {
                Grow = 1f,
                Direction = 1,
                Padding = Edges4.All(12f),
                Fill = ColorF.FromRgba(30, 30, 38, 255),
                Children = [new TextEl($"{vp.Width:0} x {vp.Height:0} DIP") { Size = 16f }],
            };
        }
    }

    private sealed class Tee(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => a.Encoding;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { if (value is null) return; a.Write(value); b.Write(value); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
        public override void Flush() { a.Flush(); b.Flush(); }
    }

    private const uint WmEnterSizeMove = 0x0231, WmExitSizeMove = 0x0232;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? cls, string title);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rc);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rc);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
