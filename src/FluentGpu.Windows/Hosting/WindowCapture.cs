using System;
using System.IO;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu;

/// <summary>A pixel rectangle in physical virtual-screen px, edges exclusive on the right/bottom (a Win32 <c>RECT</c>).</summary>
internal readonly record struct CaptureRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;
}

/// <summary>
/// Capture the COMPOSED window — what the user sees: DWM's composite of the swapchain, the Mica/acrylic backdrop, the DWM
/// border and shadow-free frame, and DirectComposition children (video) — as a PNG. The swapchain readback
/// (<c>--screenshot</c>) sees none of those.
/// </summary>
/// <remarks>
/// <c>DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)</c> gives the visible frame (without the invisible resize border)
/// in physical px — the process is per-monitor-v2 aware, so nothing is virtualized; it is grown by the padding and clipped
/// to the virtual screen (a window partly off-screen, monitors at negative coordinates). The pixels are read from the
/// screen DC with <c>BitBlt(SRCCOPY | CAPTUREBLT)</c> (CAPTUREBLT includes layered windows) into a 32bpp top-down DIB
/// and written opaque through <see cref="PngWriter"/>. A window another window covers is raised topmost for the grab
/// (two <c>DwmFlush</c> waits so the composite catches up) and put back at its previous z-order. UI thread.
/// </remarks>
internal static unsafe class WindowCapture
{
    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private const uint BI_RGB = 0, DIB_RGB_COLORS = 0;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint GW_HWNDPREV = 3;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    /// <summary>The capture rectangle: <paramref name="frame"/> grown by <paramref name="padPx"/> on every side (negative is
    /// 0) and clipped to <paramref name="virtualScreen"/>. False when nothing of it is on screen.</summary>
    public static bool TryCaptureRect(in CaptureRect frame, int padPx, in CaptureRect virtualScreen, out CaptureRect rect)
    {
        rect = default;
        if (frame.IsEmpty || virtualScreen.IsEmpty) return false;
        int pad = Math.Max(0, padPx);
        long l = (long)frame.Left - pad, t = (long)frame.Top - pad, r = (long)frame.Right + pad, b = (long)frame.Bottom + pad;
        var clipped = new CaptureRect(
            (int)Math.Max(l, virtualScreen.Left), (int)Math.Max(t, virtualScreen.Top),
            (int)Math.Min(r, virtualScreen.Right), (int)Math.Min(b, virtualScreen.Bottom));
        if (clipped.IsEmpty) return false;
        rect = clipped;
        return true;
    }

    /// <summary>Capture <paramref name="hwnd"/>'s composed frame (plus <paramref name="padPx"/> of what surrounds it) to a
    /// PNG at <paramref name="path"/> (directory created, file overwritten). False — with a <c>[shot-window]</c> stderr
    /// line — when the window is minimized or hidden, entirely off-screen, the input desktop is not this window's (a locked
    /// workstation, the UAC secure desktop, a disconnected session), the grab came back entirely black (display off), or a
    /// GDI/IO step fails.</summary>
    public static bool CaptureToPng(nint hwnd, string path, int padPx = 0)
    {
        if (hwnd == 0 || string.IsNullOrWhiteSpace(path)) { Console.Error.WriteLine("[shot-window] no window to capture"); return false; }
        HWND h = (HWND)hwnd;
        if (IsWindowVisible(h) == 0 || IsIconic(h) != 0)
        {
            Console.Error.WriteLine("[shot-window] the window is minimized or hidden — not captured");
            return false;
        }

        RECT fr;
        if ((int)DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, &fr, (uint)sizeof(RECT)) < 0 && GetWindowRect(h, &fr) == 0)
        {
            Console.Error.WriteLine("[shot-window] the window's bounds could not be read");
            return false;
        }
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var screen = new CaptureRect(vx, vy, vx + GetSystemMetrics(SM_CXVIRTUALSCREEN), vy + GetSystemMetrics(SM_CYVIRTUALSCREEN));
        if (!TryCaptureRect(new CaptureRect(fr.left, fr.top, fr.right, fr.bottom), padPx, screen, out CaptureRect cap))
        {
            Console.Error.WriteLine($"[shot-window] the window ({fr.left},{fr.top})-({fr.right},{fr.bottom}) is off the virtual screen — not captured");
            return false;
        }

        // A locked workstation, the UAC secure desktop or a disconnected session puts another desktop on the screen: the
        // screen DC then reads black (or the lock screen), which must not be written as "what the user sees". Checked
        // before the z-order is touched.
        if (!InputDesktopIsOurs(out string? why))
        {
            Console.Error.WriteLine($"[shot-window] {why} — not captured");
            return false;
        }

        byte[]? pixels;
        bool raised = RaiseIfCovered(hwnd, out HWND above, out bool wasTopmost);
        try
        {
            if (raised) { DwmFlush(); DwmFlush(); }   // let the composite show the raised window before the grab
            pixels = Grab(cap);
        }
        finally
        {
            if (raised) Restore(h, above, wasTopmost);
        }
        if (pixels is null)
        {
            Console.Error.WriteLine($"[shot-window] BitBlt of {cap.Width}x{cap.Height} failed");
            return false;
        }
        if (IsAllBlack(pixels))
        {
            // The DWM frame alone is never pure black, so an all-zero grab is a display that is off / asleep, a desktop
            // switch that raced the check above, or protected content — not the composed window.
            Console.Error.WriteLine($"[shot-window] the {cap.Width}x{cap.Height} grab is entirely black (display off, session switching or protected content) — not written");
            return false;
        }

        try
        {
            string full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            PngWriter.WriteBgra(full, pixels, cap.Width, cap.Height);
            Console.Error.WriteLine($"[shot-window] wrote {full} ({cap.Width}x{cap.Height} at {cap.Left},{cap.Top}{(raised ? ", raised for the grab" : "")})");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[shot-window] could not write {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>True when every pixel of a 32bpp BGRx buffer is black (the undefined fourth byte ignored). An empty
    /// buffer is not "all black" — there is nothing to judge.</summary>
    internal static bool IsAllBlack(ReadOnlySpan<byte> bgrx)
    {
        if (bgrx.Length < 4) return false;
        for (int i = 0; i + 3 < bgrx.Length; i += 4)
            if ((bgrx[i] | bgrx[i + 1] | bgrx[i + 2]) != 0) return false;
        return true;
    }

    /// <summary>Is the desktop receiving input (the one on the screen) this thread's desktop? No when the workstation is
    /// locked or the UAC secure desktop is up (the Winlogon desktop: <c>OpenInputDesktop</c> is denied to the user, or it
    /// opens under another name), or the session is disconnected (no input desktop at all).</summary>
    private static bool InputDesktopIsOurs(out string? why)
    {
        const uint DESKTOP_READOBJECTS = 0x0001;
        why = null;
        HDESK input = OpenInputDesktop(0, BOOL.FALSE, DESKTOP_READOBJECTS);
        if (input == HDESK.NULL)
        {
            why = $"the input desktop cannot be opened (error {System.Runtime.InteropServices.Marshal.GetLastSystemError()}: workstation locked, secure desktop or disconnected session)";
            return false;
        }
        try
        {
            Span<char> inputName = stackalloc char[128];
            Span<char> ownName = stackalloc char[128];
            int inputLen = DesktopName((HANDLE)(void*)input, inputName);
            int ownLen = DesktopName((HANDLE)(void*)GetThreadDesktop(GetCurrentThreadId()), ownName);
            if (inputLen <= 0 || ownLen <= 0) return true;   // names unreadable: no evidence of a switch — capture
            ReadOnlySpan<char> inputN = inputName[..inputLen], ownN = ownName[..ownLen];
            if (!MemoryExtensions.Equals(inputN, ownN, StringComparison.OrdinalIgnoreCase))
            {
                why = $"the input desktop is '{new string(inputN)}', not this window's '{new string(ownN)}' (workstation locked or secure desktop)";
                return false;
            }
            return true;
        }
        finally
        {
            CloseDesktop(input);
        }
    }

    // UOI_NAME of a desktop handle into `into` (UTF-16, no terminator); the char count, or 0 when it cannot be read.
    private static int DesktopName(HANDLE desk, Span<char> into)
    {
        const int UOI_NAME = 2;
        if (desk.Value == null) return 0;
        uint needed = 0;
        fixed (char* p = into)
        {
            if (GetUserObjectInformationW(desk, UOI_NAME, p, (uint)(into.Length * sizeof(char)), &needed) == 0) return 0;
        }
        int n = into.IndexOf('\0');
        return n < 0 ? into.Length : n;
    }

    private static byte[]? Grab(in CaptureRect cap)
    {
        int w = cap.Width, hgt = cap.Height;
        HDC screen = GetDC(HWND.NULL);
        if (screen == HDC.NULL) return null;
        HDC mem = CreateCompatibleDC(screen);
        byte[]? result = null;
        if (mem != HDC.NULL)
        {
            BITMAPINFO bmi = default;
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -hgt;   // top-down rows
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;
            void* bits = null;
            HBITMAP dib = CreateDIBSection(screen, &bmi, DIB_RGB_COLORS, &bits, HANDLE.NULL, 0);
            if (dib != HBITMAP.NULL && bits != null)
            {
                HGDIOBJ old = SelectObject(mem, (HGDIOBJ)(void*)dib);
                if (BitBlt(mem, 0, 0, w, hgt, screen, cap.Left, cap.Top, SRCCOPY | CAPTUREBLT) != 0)
                {
                    GdiFlush();
                    result = new ReadOnlySpan<byte>(bits, w * hgt * 4).ToArray();   // alpha is undefined under BI_RGB; the PNG is opaque RGB
                }
                SelectObject(mem, old);
            }
            if (dib != HBITMAP.NULL) DeleteObject((HGDIOBJ)(void*)dib);
            DeleteDC(mem);
        }
        ReleaseDC(HWND.NULL, screen);
        return result;
    }

    /// <summary>Raise <paramref name="h"/> topmost when another opaque top-level window covers part of it. Returns whether
    /// it did, with what to restore: the window that was directly above it and whether it was already topmost.</summary>
    private static bool RaiseIfCovered(nint hwnd, out HWND above, out bool wasTopmost)
    {
        HWND h = (HWND)hwnd;
        above = HWND.NULL;
        wasTopmost = ((uint)GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        // The engine's own occlusion walk (opaque, visible, uncloaked top-levels above it that intersect it). Layered
        // windows never count as covers there, so a translucent overlay above the window is captured as the user sees it.
        Span<RectF> covers = stackalloc RectF[4];
        if (FluentGpu.Pal.Windows.Win32WindowOcclusion.CopyOccluders(hwnd, covers) == 0) return false;
        above = GetWindow(h, GW_HWNDPREV);
        SetWindowPos(h, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        return true;
    }

    private static void Restore(HWND h, HWND above, bool wasTopmost)
    {
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        if (!wasTopmost) SetWindowPos(h, HwndNoTopmost, 0, 0, 0, 0, flags);   // the top of the non-topmost band
        // Back under whatever was directly above it — unless that is a topmost window and this one is not: inserting after
        // a topmost window would pull it into the topmost band, and the top of the normal band already is "right under it".
        bool aboveTopmost = above != HWND.NULL && ((uint)GetWindowLongW(above, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        if (above != HWND.NULL && (wasTopmost || !aboveTopmost)) SetWindowPos(h, above, 0, 0, 0, 0, flags);
    }

    private static HWND HwndTopmost => (HWND)(void*)(nint)(-1);
    private static HWND HwndNoTopmost => (HWND)(void*)(nint)(-2);
}

/// <summary>What the <c>--fg shot-window</c> schedule does on a frame.</summary>
internal enum WindowShotStep : byte
{
    /// <summary>Nothing yet: no frame has been presented (and the fallback has not elapsed), or the delay is running.</summary>
    Wait,
    /// <summary>The first frame was presented this turn: the delay starts now (arm a wake for its end).</summary>
    Armed,
    /// <summary>The delay has elapsed: capture now.</summary>
    Capture,
    /// <summary>Captured already: nothing more.</summary>
    Done,
}

/// <summary>The pure timing of <c>--fg shot-window</c>: arm at the first presented frame (or, should none be reported,
/// <see cref="ArmFallbackMs"/> after start), capture <c>delayMs</c> later, once.</summary>
internal sealed class WindowShotSchedule(long startMs, int delayMs)
{
    /// <summary>Arm anyway this long after start when no present has been reported.</summary>
    public const int ArmFallbackMs = 5000;

    private long _armedAt = -1;
    private bool _done;

    public int DelayMs { get; } = Math.Max(0, delayMs);

    public WindowShotStep Step(bool presented, long nowMs)
    {
        if (_done) return WindowShotStep.Done;
        if (_armedAt < 0)
        {
            if (!presented && nowMs - startMs < ArmFallbackMs) return WindowShotStep.Wait;
            _armedAt = nowMs;
            if (DelayMs > 0) return WindowShotStep.Armed;
        }
        if (nowMs - _armedAt < DelayMs) return WindowShotStep.Wait;
        _done = true;
        return WindowShotStep.Capture;
    }
}

/// <summary>The FluentApp frame loop's half of <c>--fg shot-window=PATH</c> (+ <c>shot-delay=MS</c>, <c>shot-exit</c>):
/// steps the schedule each frame, keeps an otherwise idle loop awake for the delay's end (a one-shot timer that calls the
/// thread-safe <see cref="IPlatformWindow.Wake"/>), captures, and says whether the run should now end.</summary>
internal sealed class WindowShot : IDisposable
{
    private readonly string _path;
    private readonly bool _exit;
    private readonly IPlatformWindow _window;
    private readonly nint _hwnd;
    private readonly WindowShotSchedule _schedule;
    private Timer? _wake;

    private WindowShot(string path, int delayMs, bool exit, IPlatformWindow window, nint hwnd)
    {
        _path = path; _exit = exit; _window = window; _hwnd = hwnd;
        _schedule = new WindowShotSchedule(Environment.TickCount64, delayMs);
        _wake = WakeAfter(WindowShotSchedule.ArmFallbackMs + 1);   // an idle loop must still see the arm fallback
    }

    private Timer WakeAfter(int ms) => new(static s => ((IPlatformWindow)s!).Wake(), _window, ms, Timeout.Infinite);

    /// <summary>The run's shot, or null when <c>--fg shot-window</c> was not given.</summary>
    public static WindowShot? FromSwitches(IPlatformWindow window, nint hwnd)
        => EngineSwitches.ShotWindowPath is { Length: > 0 } path
            ? new WindowShot(path, EngineSwitches.ShotWindowDelayMs, EngineSwitches.ShotWindowExit, window, hwnd)
            : null;

    /// <summary>One frame-loop turn. True when the capture has happened and <c>shot-exit</c> asks the run to end.</summary>
    public bool Tick(ulong presentedSequence)
    {
        switch (_schedule.Step(presentedSequence > 0, Environment.TickCount64))
        {
            case WindowShotStep.Armed:
                _wake?.Dispose();
                _wake = WakeAfter(_schedule.DelayMs + 1);   // the loop may be idle-blocked when the delay ends
                return false;
            case WindowShotStep.Capture:
                Dispose();
                WindowCapture.CaptureToPng(_hwnd, _path);
                return _exit;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        _wake?.Dispose();
        _wake = null;
    }
}
