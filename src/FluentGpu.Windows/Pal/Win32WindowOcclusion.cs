using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// F118: the Win32 half of per-window occlusion. A flip-model composition swapchain does not reliably report
/// <c>DXGI_STATUS_OCCLUDED</c> for a window another top-level covers (DWM keeps it "visible" and keeps presenting it), so a
/// covered window used to keep recording and presenting invisible frames. This is the tracker Chromium's native window occlusion
/// calculator is built on, reduced to what the engine needs: out-of-context <c>SetWinEventHook</c> hooks (foreground, minimize,
/// show/hide/reorder, location change, cloak/uncloak) which bump a process-wide epoch and wake the loops of the registered
/// windows an event can concern, and a Z-order walk (<c>EnumWindows</c>, topmost first) that collects the visible rects of the
/// OPAQUE top-level windows above one window, run only when the host sees the epoch move. The cheap rect test over those rects is
/// the engine's (<c>WindowCoverPolicy.CoveredByWindows</c>); nothing here reads pixels.
/// <para>The hooks live on a thread of their own, as Chromium's do. An out-of-context win event is delivered through the
/// installing thread's message queue, and its mere delivery ends that thread's <c>MsgWaitForMultipleObjectsEx</c> whatever the
/// callback does. Installed on the UI thread, every top-level event on the desktop (another app's window moving, a tooltip or a
/// completion popup showing) woke the loop and ran a frame: a covered window, parked and painting nothing, ran 6-47 empty frames
/// a second for as long as the user worked in other windows (owner log 2026-11-15, for 5-7 minutes). On their own thread the
/// events wake no UI thread by being delivered, and the callback wakes a window's loop only for an event that can change that
/// window's verdict (<see cref="Concerns"/>): its own window, one of the occluders its last walk counted, or a window whose rect
/// meets its rect. Every event still bumps the epoch, so the next frame a host runs for any other reason (a cover-parked host
/// polls) walks again: a filtered event can delay a verdict, never strand a stale one.</para>
/// <para>A window counts as covering only when it is visible, not minimized, not DWM-cloaked, and neither layered nor
/// click-through (a layered window may show what is behind it, so it is never trusted to hide anything). Its rect is the DWM
/// extended frame bounds (the visible frame, without the invisible resize border) when available. Windows that do not intersect
/// the target are dropped before any DWM call. Hook callbacks run on the hook thread and never throw; the walk's state is
/// UI-thread-only, and a registration's occluder set reaches the hook thread as an immutable array.</para></summary>
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
    private const uint WmQuit = 0x0012, PmNoRemove = 0x0000;
    // The occluder windows a registration remembers (their events always concern it). More than this above one window: the set
    // is dropped (null) and every top-level event concerns that window, as before the filter.
    private const int MaxOccluderHwnds = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public nint Hwnd; public uint Message; public nint WParam; public nint LParam; public uint Time; public int X, Y, Private; }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, nint pfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(Msg* msg, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(Msg* msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

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

    // (min, max) event ranges, one hook each. Registered together, in this order, and released together, on the hook thread.
    private static readonly (uint Min, uint Max)[] s_ranges =
    [
        (EventSystemForeground, EventSystemForeground),
        (EventSystemMinimizeStart, EventSystemMinimizeEnd),
        (EventObjectShow, EventObjectReorder),            // SHOW, HIDE, REORDER
        (EventObjectLocationChange, EventObjectLocationChange),
        (EventObjectCloaked, EventObjectUncloaked),       // CLOAKED, UNCLOAKED
    ];

    /// <summary>One window's hold on the hooks: its handle, its loop wake, the wake latch, and the occluders its last walk
    /// counted. Made by <see cref="Acquire"/>, handed back to <see cref="Release"/>.</summary>
    internal sealed class Registration
    {
        internal readonly nint Hwnd;
        private readonly Action _wake;
        private bool _released;       // under the registration's own lock: no wake starts after Release returns
        internal int WakePending;     // 1 = woken and not yet read by a frame (ConsumeEpoch re-arms it)
        internal nint[]? Occluders;   // null = not walked yet, or too many: every top-level event concerns this window

        internal Registration(nint hwnd, Action wake) { Hwnd = hwnd; _wake = wake; }

        /// <summary>Hook thread: wake the window's loop, unless it was released. The window closes the handles its wake uses
        /// only after its Release, so the lock keeps a wake from racing that teardown. Uncontended but for that one moment.</summary>
        internal void WakeLoop()
        {
            lock (this) { if (!_released) _wake(); }
        }

        internal void MarkReleased()
        {
            lock (this) _released = true;
        }
    }

    private sealed class HookThreadStart
    {
        internal const int Pending = 0, Installed = 1, Failed = 2, Abandoned = 3;
        internal readonly ManualResetEventSlim Ready = new();
        internal int State;           // Pending until the thread answers, or the waiting UI thread gives up (Abandoned)
        internal uint ThreadId;
    }

    private static readonly object s_gate = new();
    private static Registration[] s_regs = [];   // copy-on-write snapshot, read by the hook callback without a lock
    private static uint s_hookThreadId;           // the live hook thread (0 = none); the WM_QUIT posted to it removes the hooks
    private static long s_epoch = 1;

    // Z-order walk state (UI thread only; EnumWindows is synchronous on the calling thread).
    private static nint s_target;
    private static Rect32 s_targetRect;
    private static int s_count;
    private static bool s_reached;
    private static RectF[]? s_rects;
    private static readonly nint[] s_hwnds = new nint[MaxOccluderHwnds];
    private static int s_hwndCount;   // MaxOccluderHwnds + 1 = overflow

    /// <summary>Register <paramref name="hwnd"/> with <paramref name="wake"/> (its thread-safe loop wake) and start the hook thread
    /// on the first registration. UI thread. Null when the hooks could not be installed: the caller reports "untracked" (epoch 0)
    /// and the host never parks a window for being covered.</summary>
    internal static Registration? Acquire(nint hwnd, Action wake)
    {
        lock (s_gate)
        {
            if (s_regs.Length == 0 && !StartHookThread()) return null;
            var reg = new Registration(hwnd, wake);
            var next = new Registration[s_regs.Length + 1];
            Array.Copy(s_regs, next, s_regs.Length);
            next[^1] = reg;
            s_regs = next;
            return reg;
        }
    }

    /// <summary>Unregister a window, and stop the hook thread with the last one. UI thread.</summary>
    internal static void Release(Registration reg)
    {
        lock (s_gate)
        {
            reg.MarkReleased();
            int at = Array.IndexOf(s_regs, reg);
            if (at < 0) return;
            var next = new Registration[s_regs.Length - 1];
            for (int i = 0, j = 0; i < s_regs.Length; i++) if (i != at) next[j++] = s_regs[i];
            s_regs = next;
            if (next.Length == 0 && s_hookThreadId != 0)
            {
                PostThreadMessage(s_hookThreadId, WmQuit, 0, 0);   // the thread unhooks and ends; a later Acquire starts a new one
                s_hookThreadId = 0;
            }
        }
    }

    /// <summary>The current epoch, and re-arms <paramref name="reg"/>'s wake: the next event that concerns it wakes its loop
    /// again. Read once per host frame, so an event that lands after the read always schedules a frame, and a burst of events
    /// costs one wake per frame, not one per event.</summary>
    internal static long ConsumeEpoch(Registration reg)
    {
        Volatile.Write(ref reg.WakePending, 0);
        return Volatile.Read(ref s_epoch);
    }

    /// <summary>Under <see cref="s_gate"/>: start the hook thread and wait for its install verdict.</summary>
    private static bool StartHookThread()
    {
        var start = new HookThreadStart();
        var thread = new Thread(HookThreadMain, maxStackSize: 256 * 1024) { IsBackground = true, Name = "fgpu-occlusion" };
        thread.Start(start);
        // The thread installs five hooks and answers at once; the bound only keeps a wedged desktop from hanging the UI thread
        // (a thread that answers after it gives up sees Abandoned, removes its hooks and ends).
        start.Ready.Wait(2000);
        int state = Interlocked.CompareExchange(ref start.State, HookThreadStart.Abandoned, HookThreadStart.Pending);
        if (state != HookThreadStart.Installed) return false;
        s_hookThreadId = start.ThreadId;
        return true;
    }

    /// <summary>The hook thread: install the hooks, report, then pump until <see cref="Release"/> posts WM_QUIT. The win-event
    /// callbacks are dispatched inside <c>GetMessage</c>; nothing else is ever posted to this thread.</summary>
    private static void HookThreadMain(object? state)
    {
        var start = (HookThreadStart)state!;
        nint[] hooks = new nint[s_ranges.Length];
        bool ok = false;
        try
        {
            Msg msg;
            PeekMessage(&msg, 0, 0, 0, PmNoRemove);   // create this thread's queue before anyone can post WM_QUIT to it
            start.ThreadId = GetCurrentThreadId();
            ok = InstallHooks(hooks);
        }
        catch { ok = false; }
        int verdict = ok ? HookThreadStart.Installed : HookThreadStart.Failed;
        bool kept = Interlocked.CompareExchange(ref start.State, verdict, HookThreadStart.Pending) == HookThreadStart.Pending;
        start.Ready.Set();
        if (!ok || !kept) { RemoveHooks(hooks); return; }   // failed, or the UI thread stopped waiting: nobody will post WM_QUIT
        try
        {
            Msg msg;
            while (GetMessage(&msg, 0, 0, 0) > 0) { }
        }
        finally { RemoveHooks(hooks); }
    }

    private static bool InstallHooks(nint[] hooks)
    {
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback = &OnWinEvent;
        bool ok = true;
        for (int i = 0; i < s_ranges.Length; i++)
        {
            nint hook = SetWinEventHook(s_ranges[i].Min, s_ranges[i].Max, 0, (nint)callback, 0, 0, WineventOutOfContext);
            hooks[i] = hook;
            if (hook == 0) ok = false;
        }
        if (!ok) RemoveHooks(hooks);
        return ok;
    }

    private static void RemoveHooks(nint[] hooks)
    {
        for (int i = 0; i < hooks.Length; i++)
        {
            if (hooks[i] != 0) UnhookWinEvent(hooks[i]);
            hooks[i] = 0;
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
            var regs = Volatile.Read(ref s_regs);
            bool rectRead = false, rectOk = false;
            Rect32 rect = default;
            for (int i = 0; i < regs.Length; i++)
            {
                var reg = regs[i];
                if (Volatile.Read(ref reg.WakePending) != 0) continue;   // already woken: its next frame reads the epoch
                if (!Concerns(reg, hwnd, ref rectRead, ref rectOk, ref rect)) continue;
                if (Interlocked.Exchange(ref reg.WakePending, 1) != 0) continue;
                try { reg.WakeLoop(); } catch { /* a window torn down mid-event */ }
            }
        }
        catch { /* never throw out of a native callback */ }
    }

    /// <summary>Hook thread: can an event of top-level window <paramref name="hwnd"/> change <paramref name="reg"/>'s cover verdict?
    /// Its own window (moved, raised, minimized, cloaked); an occluder its last walk counted (moved away, hidden, minimized,
    /// closed); or any window whose rect meets its rect now (moved in, shown, brought to the front, uncloaked). Anything else lies
    /// wholly elsewhere and was not covering it, so it can neither cover nor uncover it. A rect that cannot be read concerns it.
    /// The event window's rect is read once per event (<paramref name="rectRead"/>), for every registration.</summary>
    private static bool Concerns(Registration reg, nint hwnd, ref bool rectRead, ref bool rectOk, ref Rect32 rect)
    {
        if (hwnd == reg.Hwnd) return true;
        nint[]? occluders = Volatile.Read(ref reg.Occluders);
        if (occluders is null) return true;
        for (int i = 0; i < occluders.Length; i++) if (occluders[i] == hwnd) return true;
        if (!rectRead) { rectRead = true; rectOk = GetWindowRect(hwnd, out rect); }
        if (!rectOk || !GetWindowRect(reg.Hwnd, out Rect32 target)) return true;
        return rect.Right > target.Left && rect.Left < target.Right && rect.Bottom > target.Top && rect.Top < target.Bottom;
    }

    /// <summary><see cref="CopyOccluders(nint, Span{RectF})"/> for a registered window: the walk also publishes the occluders it
    /// counted to <paramref name="reg"/>, so their events keep waking its loop (<see cref="Concerns"/>). Allocates only when that
    /// set changed. UI thread.</summary>
    internal static int CopyOccluders(Registration reg, Span<RectF> into)
    {
        int n = CopyOccluders(reg.Hwnd, into);
        nint[]? current = Volatile.Read(ref reg.Occluders);
        if (!s_reached || s_hwndCount > MaxOccluderHwnds)
        {
            if (current is not null) Volatile.Write(ref reg.Occluders, null);   // unknown Z-order or too many: everything concerns it
            return n;
        }
        var seen = new ReadOnlySpan<nint>(s_hwnds, 0, s_hwndCount);
        if (current is null || !seen.SequenceEqual(current)) Volatile.Write(ref reg.Occluders, seen.ToArray());
        return n;
    }

    /// <summary>Copy the visible rects (physical virtual-screen px) of the opaque top-level windows above <paramref name="target"/>
    /// that intersect it into <paramref name="into"/>; returns the count. 0 when the target is not an enumerable top-level window
    /// (its Z-order is unknown, so nothing is claimed) or nothing covers any of it. UI thread.</summary>
    internal static int CopyOccluders(nint target, Span<RectF> into)
    {
        s_reached = false;
        s_hwndCount = 0;
        if (target == 0 || into.IsEmpty || !GetWindowRect(target, out s_targetRect)) return 0;
        s_target = target;
        s_count = 0;
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
            // Every counted occluder is remembered for the event filter, past the rect cap too (one more than the cap = overflow).
            if (s_hwndCount < MaxOccluderHwnds) s_hwnds[s_hwndCount] = hwnd;
            if (s_hwndCount <= MaxOccluderHwnds) s_hwndCount++;
            if (s_count < s_rects!.Length) s_rects[s_count++] = new RectF(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return 1;   // keep walking until the target (rects past the cap only under-report)
        }
        catch { return 0; }
    }
}
