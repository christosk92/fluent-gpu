using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu;

/// <summary>How a <see cref="MessagePump.RunUntil"/> call ended.</summary>
public enum PumpOutcome
{
    /// <summary>The task finished (successfully, faulted, or cancelled — inspect the task itself).</summary>
    Completed,
    /// <summary>The timeout expired while the task was still running.</summary>
    TimedOut,
    /// <summary>Windows asked this process to EXIT while we were waiting (WM_QUERYENDSESSION / WM_ENDSESSION /
    /// WM_CLOSE — typically the Restart Manager arm of an MSIX deployment). The caller must stop waiting and let the
    /// process go away; see the remarks on <see cref="MessagePump"/>.</summary>
    ShutdownRequested,
}

/// <summary>
/// Keep a GUI thread ALIVE — i.e. still draining its message queue — while it waits for an off-thread
/// <see cref="Task"/> to finish, AND notice when Windows asks the process to exit rather than to merely answer.
/// The everyday frame loop does the first part for free; this is for the handful of moments AFTER (or before) the
/// loop where the thread must block on something long-running and must NOT look hung.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (part 1: the hang report).</b> Windows decides an app is hung by asking its GUI thread's message
/// queue, not by asking the app. A thread that owns any window — including the hidden ones the OS creates on your
/// behalf: the STA apartment's <c>OleMainThreadWndClass</c> COM-marshaling window, the WinRT/RoInitialize dispatcher
/// windows behind SMTC (<c>ISystemMediaTransportControlsInterop.GetForWindow</c>) and the notification/shell proxies
/// (<c>ITaskbarList3</c>, jump lists), plus DWM/DirectComposition proxies — and then sits in a blocking wait for ~5 s
/// is reported as "stopped responding". Pumping while we wait is what stops that.
/// </para>
/// <para>
/// <b>Why this exists (part 2: THE DEADLOCK — pumping is NOT enough).</b> Answering the shutdown messages does not
/// save us, because the deployment path is not asking permission — it is asking us to LEAVE.
/// <c>AddPackageByAppInstallerAsync</c> + <c>ForceTargetApplicationShutdown</c> runs the Restart Manager against the
/// package's running processes: it sends <c>WM_QUERYENDSESSION</c> with <c>ENDSESSION_CLOSEAPP</c>, then
/// <c>WM_ENDSESSION</c> / <c>WM_CLOSE</c> to the top-level windows, and then WAITS ~30 s for the process to exit
/// before killing it. If our quit path is meanwhile waiting on <c>ApplyAsync</c> — which is waiting on that very
/// deployment — nobody moves: the deployment waits for us to exit, we wait for the deployment. The measured shape is
/// exactly that: deployment started 23:23:07.6, WER <c>MoAppHang</c> + Application event 1002 at 23:23:40.05 (~32 s),
/// the package registered at 23:23:40.5 — the instant the OS killed us. A pumped run and an un-pumped run took the
/// same ~32 s, which is the proof that the missing piece was never the answering.
/// <br/>
/// So this helper does not just pump: it OWNS a hidden top-level window whose procedure records that the request
/// arrived, and reports <see cref="PumpOutcome.ShutdownRequested"/> so the caller can stop waiting and return, letting
/// the process exit normally. The deployment then completes in milliseconds. Nothing is lost by leaving: the update is
/// already staged, and <c>PackageUpdater.Deploy</c> calls <c>RegisterApplicationRestart</c> BEFORE the deployment
/// starts, so the Restart Manager brings the app back (with <c>--relaunched-after-update</c>) once it is done.
/// </para>
/// <para>
/// <b>Why a real top-level window.</b> The Restart-Manager/end-session messages are broadcast to TOP-LEVEL windows
/// only. A message-only window (<c>HWND_MESSAGE</c> parent) never receives them, and by the time this helper is
/// typically used our own app window is already destroyed (<c>Win32App.Dispose</c> → <c>DestroyWindow</c>), leaving
/// only OS-owned proxies whose procedures end in <c>DefWindowProc</c> — which answers TRUE to
/// <c>WM_QUERYENDSESSION</c> and tells us nothing. Hence a hidden, never-shown, zero-sized top-level window of our
/// own, created on the calling thread for the duration of the call and destroyed on the way out. The class is
/// registered once per process and left registered; the window procedure is an <c>[UnmanagedCallersOnly]</c> function
/// pointer (AOT-clean, no delegate, no <c>ComWrappers</c>), the same shape as <c>Win32App.StaticWndProc</c>.
/// </para>
/// <para>
/// <b>Cost.</b> One <see cref="WaitHandle"/> is materialized from the task (cold, once) and one window is created
/// (cold, once); the loop itself allocates nothing — <c>MSG</c> is a stack local and every call is a blittable
/// P/Invoke. The wait is a real kernel wait (<c>MsgWaitForMultipleObjectsEx</c> with <c>QS_ALLINPUT</c>), never a
/// spin, so an idle wait costs no CPU.
/// </para>
/// </remarks>
public static unsafe class MessagePump
{
    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const uint WM_QUIT = 0x0012;

    // The three ways Windows says "go away" (WinUser.h). WM_QUERYENDSESSION arrives first, with ENDSESSION_CLOSEAPP
    // (0x00000001) in lParam when it is the Restart Manager rather than a real logoff/shutdown; WM_ENDSESSION confirms
    // it (wParam TRUE); WM_CLOSE is what the Restart Manager sends to a top-level window it wants closed.
    private const uint WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;

    /// <summary>Longest single kernel wait before the loop re-checks the task and the deadline. A ceiling (rather than
    /// "wait for the whole remaining time") means a task that completes without ever signalling its wait handle — or a
    /// handle we failed to materialize — still ends this call promptly instead of parking for the full timeout.</summary>
    private const uint MaxWaitSliceMs = 250;

    private const string SentinelClassName = "FluentGpuPumpSentinel";

    /// <summary>The sentinel window class atom. Registered on first use and deliberately never unregistered: the class
    /// is process-wide and free to keep, and unregistering it would race a second pump on another thread.</summary>
    private static ushort s_sentinelAtom;

    /// <summary>Set by <see cref="SentinelWndProc"/> when Windows asks the process to exit. Process-wide on purpose —
    /// an end-session request is addressed to the whole process, so a concurrent pump on another thread should see it
    /// too. Reset at the top of every <see cref="RunUntil"/> call; the window procedure runs on the pumping thread
    /// itself (sent messages are delivered from inside <c>PeekMessageW</c>), so there is no cross-thread write in the
    /// common case, and <c>volatile</c> covers the uncommon one.</summary>
    private static volatile bool s_shutdownRequested;

    /// <summary>
    /// Block until <paramref name="task"/> completes, <paramref name="timeout"/> elapses, or Windows asks this process
    /// to exit — draining and dispatching Win32 messages the whole time so the calling thread keeps answering Windows.
    /// </summary>
    /// <param name="task">The work to wait for. It must run somewhere OTHER than this thread (a thread-pool task);
    /// this call is the thread's message loop for the duration.</param>
    /// <param name="timeout">Ceiling on the wait. A non-positive value means "check once and return".</param>
    /// <returns>How the wait ended — see <see cref="PumpOutcome"/>. On
    /// <see cref="PumpOutcome.ShutdownRequested"/> the caller MUST stop waiting and let the process exit: the thing it
    /// is waiting for is very likely the same deployment that is asking us to leave.</returns>
    [SupportedOSPlatform("windows")]
    public static PumpOutcome RunUntil(Task task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);

        long deadline = Environment.TickCount64 + (long)Math.Max(0d, timeout.TotalMilliseconds);

        s_shutdownRequested = false;
        // One hidden top-level window for the duration of this call, on THIS thread (a window belongs to the thread
        // that created it, and only that thread may destroy it). If creation fails we still pump — we simply lose the
        // ability to notice an end-session request, which is exactly the old behaviour.
        HWND sentinel = CreateSentinel();

        // AsyncWaitHandle lazily materializes a ManualResetEvent the task signals on completion. Cold, once, outside
        // the loop. If it cannot be obtained for any reason we still pump — the MaxWaitSliceMs ceiling plus the
        // IsCompleted re-check below make the handle an optimization, not a correctness requirement.
        WaitHandle? handle = null;
        try { handle = ((IAsyncResult)task).AsyncWaitHandle; }
        catch (ObjectDisposedException) { }

        HANDLE h = HANDLE.NULL;
        if (handle is not null)
        {
            try { h = (HANDLE)handle.SafeWaitHandle.DangerousGetHandle(); }
            catch (ObjectDisposedException) { h = HANDLE.NULL; }
        }

        try
        {
            while (true)
            {
                // Drain first: a message that arrived while we were away is answered before we park again.
                Drain();

                // Completion wins over a shutdown request that landed in the same drain: if the work is done there is
                // nothing left to abandon.
                if (task.IsCompleted)
                    return PumpOutcome.Completed;

                if (s_shutdownRequested)
                    return PumpOutcome.ShutdownRequested;

                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    return task.IsCompleted ? PumpOutcome.Completed : PumpOutcome.TimedOut;

                uint slice = remaining < MaxWaitSliceMs ? (uint)remaining : MaxWaitSliceMs;
                uint r = h != HANDLE.NULL
                    ? MsgWaitForMultipleObjectsEx(1, &h, slice, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
                    : MsgWaitForMultipleObjectsEx(0, null, slice, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

                if (r == WAIT_FAILED)
                {
                    // The wait itself is broken (an invalid handle, a torn-down queue). Falling back to a plain
                    // blocking wait is strictly better than spinning: we lose the pump, not the update.
                    return task.Wait((int)Math.Max(0, deadline - Environment.TickCount64))
                        ? PumpOutcome.Completed
                        : PumpOutcome.TimedOut;
                }

                // WAIT_OBJECT_0      -> the task signalled.
                // WAIT_OBJECT_0 + n  -> messages are waiting; the top of the loop drains them.
                // WAIT_TIMEOUT       -> this slice expired; the top of the loop re-checks the deadline.
                if (r == WAIT_OBJECT_0 && h != HANDLE.NULL)
                {
                    Drain();   // answer anything queued before we hand the thread back
                    return PumpOutcome.Completed;
                }
            }
        }
        finally
        {
            if (sentinel != HWND.NULL) DestroyWindow(sentinel);
            // `h` is a raw copy of the task's wait handle. Keep the managed WaitHandle (and therefore its
            // SafeWaitHandle) reachable for the whole loop so a finalizer can never close it underneath us.
            GC.KeepAlive(handle);
        }
    }

    /// <summary>Remove and dispatch every queued message. Sent messages (WM_QUERYENDSESSION among them) are delivered
    /// to their window procedures by <c>PeekMessageW</c> itself, so they are handled by this call even though they
    /// never appear in the returned <c>MSG</c>.</summary>
    private static void Drain()
    {
        MSG msg;
        while (PeekMessageW(&msg, HWND.NULL, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT)
                continue;   // see the remarks: we are already on the way out; do not re-post.
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    /// <summary>Create the hidden, never-shown, zero-sized TOP-LEVEL sentinel window on the calling thread. Top-level
    /// is load-bearing: the end-session broadcast skips <c>HWND_MESSAGE</c> windows entirely. Returns
    /// <see cref="HWND.NULL"/> if the class or the window cannot be created — the pump then behaves exactly as it did
    /// before this window existed.</summary>
    private static HWND CreateSentinel()
    {
        HINSTANCE hinst = GetModuleHandleW(null);

        if (s_sentinelAtom == 0)
        {
            fixed (char* cn = SentinelClassName)
            {
                WNDCLASSEXW wc = default;
                wc.cbSize = (uint)sizeof(WNDCLASSEXW);
                wc.lpfnWndProc = &SentinelWndProc;
                wc.hInstance = hinst;
                wc.lpszClassName = cn;
                s_sentinelAtom = RegisterClassExW(&wc);
            }
            if (s_sentinelAtom == 0) return HWND.NULL;
        }

        fixed (char* cn = SentinelClassName)
        fixed (char* title = "FluentGpu shutdown sentinel")
        {
            // WS_OVERLAPPED (0) and no WS_VISIBLE: a real top-level window that is never shown, never painted, and
            // never appears in the taskbar or Alt-Tab (a zero-sized, hidden, unowned window with no WS_EX_APPWINDOW).
            return CreateWindowExW(0, cn, title, 0, 0, 0, 0, 0, HWND.NULL, HMENU.NULL, hinst, null);
        }
    }

    /// <summary>The sentinel's window procedure. Its ONLY job is to record that Windows asked the process to exit;
    /// the pump loop turns that into <see cref="PumpOutcome.ShutdownRequested"/>.</summary>
    [UnmanagedCallersOnly]
    private static LRESULT SentinelWndProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            // TRUE = "yes, you may end my session". Refusing here does not help: the Restart Manager kills us anyway,
            // and a refusal is what makes a real logoff hang on our window. The flag is the point.
            case WM_QUERYENDSESSION:
                s_shutdownRequested = true;
                return (LRESULT)1;

            // wParam TRUE = the session really is ending (FALSE = a WM_QUERYENDSESSION was cancelled by someone else).
            case WM_ENDSESSION:
                if ((nuint)wParam != 0) s_shutdownRequested = true;
                return (LRESULT)0;

            // Deliberately NOT forwarded to DefWindowProc, which would DestroyWindow this sentinel out from under the
            // pump. RunUntil owns its lifetime and destroys it in its finally block.
            case WM_CLOSE:
                s_shutdownRequested = true;
                return (LRESULT)0;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
