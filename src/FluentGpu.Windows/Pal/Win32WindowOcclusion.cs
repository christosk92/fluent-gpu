using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// F118: the Win32 half of per-window occlusion. A flip-model composition swapchain does not reliably report
/// <c>DXGI_STATUS_OCCLUDED</c> for a window another top-level covers (DWM keeps it "visible" and keeps presenting it), so a
/// covered window used to keep recording and presenting invisible frames. This is the tracker Chromium's native window occlusion
/// calculator is built on, reduced to what the engine needs: out-of-context <c>SetWinEventHook</c> hooks (foreground, minimize,
/// show/hide/reorder, location change, cloak/uncloak) installed on the UI thread, which only bump a process-wide epoch and wake
/// the registered windows' loops, and a Z-order walk (<c>EnumWindows</c>, topmost first) that collects the visible rects of the
/// OPAQUE top-level windows above one window, run only when the host sees the epoch move. The cheap rect test over those rects is
/// the engine's (<c>WindowCoverPolicy.CoveredByWindows</c>); nothing here reads pixels.
/// <para>A window counts as covering only when it is visible, not minimized, not DWM-cloaked, and neither layered nor
/// click-through (a layered window may show what is behind it, so it is never trusted to hide anything). Its rect is the DWM
/// extended frame bounds (the visible frame, without the invisible resize border) when available. Windows that do not intersect
/// the target are dropped before any DWM call. Hook callbacks run on the installing (UI) thread during message dispatch and
/// never throw; the shared state is UI-thread-only except the epoch and the wake flag.</para></summary>
internal static unsafe partial class Win32WindowOcclusion
{
    private const uint EventSystemForeground = 0x0003, EventSystemMinimizeStart = 0x0016, EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectShow = 0x8002, EventObjectReorder = 0x8004, EventObjectLocationChange = 0x800B;
    private const uint EventObjectCloaked = 0x8017, EventObjectUncloaked = 0x8018;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const uint GaParent = 1;
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000, WsExTransparent = 0x00000020;
    private const uint DwmwaExtendedFrameBounds = 9, DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, nint pfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(nint lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect32 rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttributeRect(nint hwnd, uint attr, out Rect32 value, uint size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttributeInt(nint hwnd, uint attr, out int value, uint size);

    // (min, max) event ranges, one hook each. Registered together, in this order, and released together.
    private static readonly (uint Min, uint Max)[] s_ranges =
    [
        (EventSystemForeground, EventSystemForeground),
        (EventSystemMinimizeStart, EventSystemMinimizeEnd),
        (EventObjectShow, EventObjectReorder),            // SHOW, HIDE, REORDER
        (EventObjectLocationChange, EventObjectLocationChange),
        (EventObjectCloaked, EventObjectUncloaked),       // CLOAKED, UNCLOAKED
    ];

    private static readonly object s_gate = new();
    private static readonly nint[] s_hooks = new nint[5];
    private static Action[] s_wakers = [];   // copy-on-write snapshot, read by the hook callback without a lock
    private static long s_epoch = 1;
    private static int s_wakePending;

    // Z-order walk state (UI thread only; EnumWindows is synchronous on the calling thread).
    private static nint s_target;
    private static Rect32 s_targetRect;
    private static int s_count;
    private static bool s_reached;
    private static RectF[]? s_rects;

    /// <summary>Register <paramref name="wake"/> (a window's thread-safe loop wake) and install the process-wide hooks on the first
    /// registration. UI thread. False when the hooks could not be installed: the caller reports "untracked" (epoch 0) and the host
    /// never parks a window for being covered.</summary>
    internal static bool Acquire(Action wake)
    {
        lock (s_gate)
        {
            bool first = s_wakers.Length == 0;
            if (first && !InstallHooks()) return false;
            var next = new Action[s_wakers.Length + 1];
            Array.Copy(s_wakers, next, s_wakers.Length);
            next[^1] = wake;
            s_wakers = next;
            return true;
        }
    }

    /// <summary>Unregister a window's wake and remove the hooks with the last one. UI thread (the installing thread).</summary>
    internal static void Release(Action wake)
    {
        lock (s_gate)
        {
            int at = Array.IndexOf(s_wakers, wake);
            if (at < 0) return;
            var next = new Action[s_wakers.Length - 1];
            for (int i = 0, j = 0; i < s_wakers.Length; i++) if (i != at) next[j++] = s_wakers[i];
            s_wakers = next;
            if (next.Length == 0) RemoveHooks();
        }
    }

    /// <summary>The current epoch, and re-arms the wake: the next event wakes the loops again. Read once per host frame, so an event
    /// that lands after the read always schedules a frame, and a burst of events costs one wake per frame, not one per event.</summary>
    internal static long ConsumeEpoch()
    {
        Volatile.Write(ref s_wakePending, 0);
        return Volatile.Read(ref s_epoch);
    }

    private static bool InstallHooks()
    {
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback = &OnWinEvent;
        bool ok = true;
        for (int i = 0; i < s_ranges.Length; i++)
        {
            nint hook = SetWinEventHook(s_ranges[i].Min, s_ranges[i].Max, 0, (nint)callback, 0, 0, WineventOutOfContext);
            s_hooks[i] = hook;
            if (hook == 0) ok = false;
        }
        if (!ok) RemoveHooks();
        return ok;
    }

    private static void RemoveHooks()
    {
        for (int i = 0; i < s_hooks.Length; i++)
        {
            if (s_hooks[i] != 0) UnhookWinEvent(s_hooks[i]);
            s_hooks[i] = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint timeMs)
    {
        try
        {
            if (hwnd == 0) return;
            if (eventType != EventSystemForeground && eventType != EventSystemMinimizeStart && eventType != EventSystemMinimizeEnd)
            {
                // The object events also fire for carets, cursors and child controls: only a TOP-LEVEL window's own events matter.
                if (idObject != ObjidWindow) return;
                if (GetAncestor(hwnd, GaParent) != GetDesktopWindow()) return;
            }
            Interlocked.Increment(ref s_epoch);
            if (Interlocked.Exchange(ref s_wakePending, 1) != 0) return;   // already woken for this frame
            var wakers = Volatile.Read(ref s_wakers);
            for (int i = 0; i < wakers.Length; i++)
            {
                try { wakers[i](); } catch { /* a window torn down mid-event */ }
            }
        }
        catch { /* never throw out of a native callback */ }
    }

    /// <summary>Copy the visible rects (physical virtual-screen px) of the opaque top-level windows above <paramref name="target"/>
    /// that intersect it into <paramref name="into"/>; returns the count. 0 when the target is not an enumerable top-level window
    /// (its Z-order is unknown, so nothing is claimed) or nothing covers any of it. UI thread.</summary>
    internal static int CopyOccluders(nint target, Span<RectF> into)
    {
        if (target == 0 || into.IsEmpty || !GetWindowRect(target, out s_targetRect)) return 0;
        s_target = target;
        s_count = 0;
        s_reached = false;
        s_rects ??= new RectF[FluentGpu.Hosting.WindowCoverPolicy.MaxOccluders];
        delegate* unmanaged<nint, nint, int> proc = &EnumProc;
        EnumWindows((nint)proc, 0);
        if (!s_reached) return 0;
        int n = Math.Min(s_count, into.Length);
        new ReadOnlySpan<RectF>(s_rects, 0, n).CopyTo(into);
        return n;
    }

    [UnmanagedCallersOnly]
    private static int EnumProc(nint hwnd, nint lParam)
    {
        try
        {
            if (hwnd == s_target) { s_reached = true; return 0; }   // everything above the target has been seen
            if (s_count >= s_rects!.Length) return 1;               // keep walking until the target (the extras only under-report)
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return 1;
            long exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
            if ((exStyle & (WsExLayered | WsExTransparent)) != 0) return 1;   // may show what is behind it: never a cover
            if (!GetWindowRect(hwnd, out Rect32 r)) return 1;
            // Coarse reject before any DWM call: most windows are nowhere near the target.
            if (r.Right <= s_targetRect.Left || r.Left >= s_targetRect.Right || r.Bottom <= s_targetRect.Top || r.Top >= s_targetRect.Bottom) return 1;
            if (DwmGetWindowAttributeInt(hwnd, DwmwaCloaked, out int cloaked, (uint)sizeof(int)) >= 0 && cloaked != 0) return 1;
            if (DwmGetWindowAttributeRect(hwnd, DwmwaExtendedFrameBounds, out Rect32 visible, (uint)sizeof(Rect32)) >= 0
                && visible.Right > visible.Left && visible.Bottom > visible.Top)
                r = visible;   // the visible frame, not the invisible resize border
            if (r.Right <= r.Left || r.Bottom <= r.Top) return 1;
            s_rects[s_count++] = new RectF(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return 1;
        }
        catch { return 0; }
    }
}
