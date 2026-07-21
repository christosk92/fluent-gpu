using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu;

/// <summary>
/// Deterministic, human-free repro/verify harness for the "Windows APIs" page DIALOGS-pillar crash
/// (<c>--filepicker-probe</c>). It exercises <see cref="FilePicker.OpenFile"/> / <see cref="FilePicker.SaveFile"/> /
/// <see cref="FilePicker.PickFolder"/> exactly as the gallery's <c>DialogsCard</c> button handlers do — passing a REAL
/// top-level owner HWND — but without a human to dismiss the modal. A watchdog thread finds the dialog window
/// (class <c>#32770</c>, owned by this process) ~1.5s after the call starts and posts <c>WM_CLOSE</c> to dismiss it;
/// the picker call must then return <see langword="null"/> (cancelled) WITHOUT the process crashing.
/// <para>
/// <b>Apartment faithfulness.</b> The gallery's UI thread is MTA (its <c>Main</c> carries no <c>[STAThread]</c> —
/// see <c>FluentGpu.WindowsApi/Network/NetworkListManagerEventSink.cs:27</c>), and the <c>DialogsCard</c> handler runs
/// the picker on that MTA UI thread. A default <c>new Thread</c> is also MTA, so by default the probe runs each picker
/// on an MTA worker thread — reproducing the exact apartment the crash needs. The shell common-item dialog
/// (<c>IFileOpenDialog</c> → <c>IModalWindow.Show</c>) does OLE init / <c>RegisterDragDrop</c> internally and REQUIRES
/// an STA; calling <c>Show</c> from an MTA thread fail-fasts/access-violates and kills the process — the user-visible
/// crash. Pass <c>--sta</c> to instead run the picker on a real STA worker (the post-fix shape) to prove it succeeds.
/// </para>
/// <para>
/// <b>Owner window.</b> A plain Win32 top-level window is created and its message pump runs on the MAIN thread for the
/// whole probe (so the dialog has a real, pumping owner — the gallery's exact situation, and a faithful test of the
/// modal owner-SendMessage path). The picker runs on a separate worker; the main thread pumps + watchdogs.
/// </para>
/// <para>Exit codes: <c>0</c> = all three dialogs opened and were auto-dismissed, every call returned null, no crash
/// (FIXED). <c>2</c> = a dialog window never appeared / a call did not return within budget (a hang — e.g. Option-2
/// done wrong). <c>3</c> = harness error. A NON-ZERO exit from a process-level fail-fast / access violation at
/// <c>Show</c> (the pre-fix crash) is observed by the PARENT launcher as a crash exit code — this managed code never
/// runs past the faulting <c>Show</c> in that case.</para>
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static unsafe class FilePickerProbe
{
    private const int DismissAfterMs = 1500;   // let the modal fully materialize before the watchdog closes it.
    private const int PerCallBudgetMs = 8000;  // a healthy open+auto-dismiss round trip is sub-second; 8s = "wedged".
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);

    // Minimal owner-window plumbing (no engine/GPU): a real top-level HWND with a live pump on the main thread.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW { public uint style; public nint lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public nint hInstance; public nint hIcon; public nint hCursor; public nint hbrBackground; public string? lpszMenuName; public string? lpszClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int ptx; public int pty; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(in WNDCLASSW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint h, uint m, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] private static extern int PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int TranslateMessage(in MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(in MSG msg);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(string? name);
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;

    // ── FAITHFUL mode: drive the REAL gallery engine window and run the picker on the UI thread mid-loop ───────────────
    // The headless RunOne path above proved the apartment theory alone does NOT crash (an MTA worker opens the dialog
    // fine). The live gallery crash needs the EXACT condition the DialogsCard reproduces: the picker runs on the UI
    // thread that owns the COMPOSITED Mica window AND is running the engine frame loop — Show() then pumps a NESTED modal
    // loop reentrantly into the engine on that same thread. --filepicker-auto reproduces that with the real FluentApp.Run
    // stack: a driver component posts the picker onto the UI thread (UsePost → the host's UI-thread drain), a watchdog
    // dismisses the dialog, and the window closes. A crash at Show kills the process; the parent sees the crash exit code.
    private static volatile bool s_autoReady;      // the driver captured its UsePost hook (UI thread is live).
    private static Action<Action>? s_autoPost;     // the UI-thread marshal captured by the driver component.
    private static volatile string s_autoResult = "(not-run)";
    private static volatile bool s_autoPickerReturned;

    public static int RunAuto(string[] args)
    {
        uint selfPid = (uint)Environment.ProcessId;
        Console.WriteLine($"[fp-auto] driving the REAL gallery window; picker runs on the UI thread (pid={selfPid}).");

        // Watchdog thread: once the UI thread is live and the post hook captured, hop the picker onto the UI thread,
        // dismiss the dialog it raises, then close the window so FluentApp.Run returns. Bounded so a hang can't wedge us.
        var watchdog = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            while (!s_autoReady && sw.ElapsedMilliseconds < 12000) Thread.Sleep(25);
            if (!s_autoReady) { Console.WriteLine("[fp-auto] FAIL: UI thread never became ready."); ForceClose(); return; }
            Console.WriteLine($"[fp-auto] UI thread ready (apt seen via driver). Posting OpenFile onto the UI thread…");

            // Post the picker onto the UI thread — it will run inside the host drain, on the thread that owns the
            // composited window and runs the engine loop (the faithful reentrancy condition).
            s_autoPost!(() =>
            {
                Console.WriteLine($"[fp-auto] (UI thread) apt={Thread.CurrentThread.GetApartmentState()} — calling FilePicker.OpenFile(realWindow)…");
                try
                {
                    string? p = FilePicker.OpenFile(WindowsApiInterop.WindowHandle, "Auto Probe Open", ("All", "*.*"));
                    s_autoResult = p ?? "(null/cancelled)";
                }
                catch (Exception ex) { s_autoResult = $"threw {ex.GetType().Name}: {ex.Message}"; }
                finally { s_autoPickerReturned = true; }
            });

            // Dismiss the modal once it materializes.
            var d = Stopwatch.StartNew();
            bool seen = false;
            while (d.ElapsedMilliseconds < 10000 && !s_autoPickerReturned)
            {
                nint dlg = FindOurDialog(selfPid);
                if (dlg != 0) { seen = true; Console.WriteLine($"[fp-auto] found dialog 0x{dlg:X} — WM_CLOSE."); PostMessageW(dlg, WM_CLOSE, 0, 0); }
                Thread.Sleep(60);
            }
            Console.WriteLine($"[fp-auto] picker returned={s_autoPickerReturned} dialogSeen={seen} result='{s_autoResult}'.");
            ForceClose();
        }) { IsBackground = true, Name = "fp-auto-watchdog" };

        // Hard process watchdog (the engine loop can wedge if Show's nested loop deadlocks vs the engine).
        StartProcessWatchdog(25000);
        watchdog.Start();

        // BLOCKS on the UI thread until the watchdog closes the window. The picker crash (if any) fail-fasts the process.
        FluentApp.Run(() => new FilePickerAutoDriver(),
            new AppOptions { Title = "FluentGpu — FilePicker Auto Probe", Width = 900, Height = 640 });

        // We only reach here if Run returned (window closed) WITHOUT a crash.
        if (!s_autoPickerReturned) { Console.WriteLine("[fp-auto] FAILED — the picker never returned (hang or window closed first)."); return 2; }
        Console.WriteLine($"[fp-auto] PASSED — picker ran on the UI thread of the live composited window and returned '{s_autoResult}', no crash.");
        return 0;
    }

    private static void ForceClose()
    {
        nint hwnd = WindowsApiInterop.WindowHandle;
        if (hwnd != 0) PostMessageW(hwnd, WM_CLOSE, 0, 0);
    }

    /// <summary>Driver component for <c>--filepicker-auto</c>: on its first render it captures the UI-thread post hook and
    /// flags the watchdog that the UI thread is live. Renders a trivial scene (the engine loop must be running).</summary>
    internal sealed class FilePickerAutoDriver : Component
    {
        public override Element Render()
        {
            var post = UsePost();
            if (!s_autoReady) { s_autoPost = post; s_autoReady = true; }
            return new BoxEl { Padding = Edges4.All(24), Children = [Heading("FilePicker auto probe — driving the real engine window.")] };
        }
    }

    public static int Run(string[] args)
    {
        if (Array.IndexOf(args, "--auto") >= 0)
            return RunAuto(args);

        bool sta = Array.IndexOf(args, "--sta") >= 0;   // opt-in: run pickers on an STA worker (post-fix shape).
        uint selfPid = (uint)Environment.ProcessId;
        Console.WriteLine($"[fp-probe] main thread apt={Thread.CurrentThread.GetApartmentState()} pickerWorkerApt={(sta ? "STA" : "MTA")} pid={selfPid}");

        // Create a real, pumping top-level owner window on the main thread (the gallery window's stand-in).
        nint owner = CreateOwnerWindow(out string ownerErr);
        if (owner == 0)
        {
            Console.WriteLine($"[fp-probe] FAIL: could not create owner window ({ownerErr}).");
            return 3;
        }
        Console.WriteLine($"[fp-probe] owner HWND=0x{owner:X} created + visible.");

        // Hard process watchdog: if any picker call wedges (a hang, e.g. a badly-done STA-per-dialog Join deadlock), the
        // whole probe is force-exited so the parent's outer timeout is never the only backstop.
        StartProcessWatchdog(PerCallBudgetMs * 3 + 6000);

        int failures = 0;
        failures += RunOne("OpenFile", owner, selfPid, sta, () => FilePicker.OpenFile(owner, "Probe Open", ("All", "*.*")));
        failures += RunOne("SaveFile", owner, selfPid, sta, () => FilePicker.SaveFile(owner, "Probe Save", "probe.m3u", ("Playlist", "*.m3u"), ("All", "*.*")));
        failures += RunOne("PickFolder", owner, selfPid, sta, () => FilePicker.PickFolder(owner, "Probe Folder"));

        DestroyWindow(owner);

        if (failures == 0)
        {
            Console.WriteLine("[fp-probe] PASSED — all three dialogs opened, auto-dismissed, returned null, no crash.");
            return 0;
        }
        Console.WriteLine($"[fp-probe] FAILED — {failures}/3 dialog(s) did not open+dismiss cleanly (hang or never-shown).");
        return 2;
    }

    /// <summary>
    /// Run one picker call on a worker thread of the requested apartment while the main thread pumps the owner window and
    /// a watchdog dismisses the dialog. Returns 0 if the dialog appeared and the call returned within budget; 1 on a
    /// no-show/hang. A crash at <c>Show</c> never returns here — the process dies and the parent sees the crash exit code.
    /// </summary>
    private static int RunOne(string name, nint owner, uint selfPid, bool sta, Func<string?> call)
    {
        Console.WriteLine($"[fp-probe] --- {name}: invoking on a {(sta ? "STA" : "MTA")} worker (owner=0x{owner:X}) ---");
        string? result = null;
        Exception? fault = null;
        bool returned = false;
        var done = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            try { result = call(); returned = true; }
            catch (Exception ex) { fault = ex; }
            finally { done.Set(); }
        }) { IsBackground = true, Name = $"fp-{name}" };
        worker.SetApartmentState(sta ? ApartmentState.STA : ApartmentState.MTA);
        worker.Start();

        // Watchdog: after the dialog has had time to materialize, find the #32770 owned by this process and close it.
        bool dialogSeen = false;
        var dismiss = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < PerCallBudgetMs && !done.IsSet)
            {
                if (sw.ElapsedMilliseconds >= DismissAfterMs)
                {
                    nint dlg = FindOurDialog(selfPid);
                    if (dlg != 0)
                    {
                        dialogSeen = true;
                        Console.WriteLine($"[fp-probe] {name}: found dialog HWND=0x{dlg:X} (#32770) — posting WM_CLOSE.");
                        PostMessageW(dlg, WM_CLOSE, 0, 0);
                        // Give the close a moment; if it lingers, keep nudging until the call returns.
                    }
                }
                Thread.Sleep(50);
            }
        }) { IsBackground = true, Name = $"fp-{name}-watchdog" };
        dismiss.Start();

        // Pump the OWNER window's message loop on the main thread while we wait — the gallery UI thread is always pumping
        // when a modal is up, so any SendMessage the dialog makes to the owner is serviced (no owner-SendMessage deadlock).
        bool finished = PumpUntil(done, PerCallBudgetMs);

        if (!finished)
        {
            Console.WriteLine($"[fp-probe] {name}: HUNG — call did not return within {PerCallBudgetMs}ms (dialogSeen={dialogSeen}). The picker is wedged.");
            DumpThreads();
            return 1;
        }
        if (fault != null)
        {
            // A managed exception is NOT the crash class (the crash is a process fail-fast at Show, which never reaches
            // here). A thrown InvalidOperationException from a failed COM step is reported but does not, by itself, fail
            // the gate as "no-show" — record it and treat a never-shown dialog as the failure signal.
            Console.WriteLine($"[fp-probe] {name}: call threw {fault.GetType().Name}: {fault.Message} (dialogSeen={dialogSeen}).");
            return dialogSeen ? 0 : 1;
        }
        Console.WriteLine($"[fp-probe] {name}: returned '{result ?? "(null/cancelled)"}' (dialogSeen={dialogSeen}).");
        return dialogSeen ? 0 : 1;   // a clean return WITHOUT the dialog ever appearing is also a failure to repro/verify.
    }

    /// <summary>Find a top-level common-dialog window (<c>#32770</c>) owned by THIS process, or 0.</summary>
    private static nint FindOurDialog(uint selfPid)
    {
        nint h = 0;
        while ((h = FindWindowExW(0, h, "#32770", null)) != 0)
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == selfPid && IsWindowVisible(h))
                return h;
        }
        return 0;
    }

    private static nint CreateOwnerWindow(out string err)
    {
        err = "";
        nint hinst = GetModuleHandleW(null);
        // A trivial DefWindowProc class — we never need custom handling, just a real pumping top-level HWND.
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(StaticWndProc),
            hInstance = hinst,
            lpszClassName = "FpProbeOwnerClass",
        };
        _wndProcKeepAlive = StaticWndProc;   // keep the delegate alive (no GC of the thunk).
        if (RegisterClassW(in wc) == 0)
        {
            int e = Marshal.GetLastWin32Error();
            if (e != 1410 /*ERROR_CLASS_ALREADY_EXISTS*/) { err = $"RegisterClassW failed ({e})"; return 0; }
        }
        nint h = CreateWindowExW(0, "FpProbeOwnerClass", "FilePicker Probe Owner", WS_OVERLAPPEDWINDOW,
            100, 100, 480, 320, 0, 0, hinst, 0);
        if (h == 0) { err = $"CreateWindowExW failed ({Marshal.GetLastWin32Error()})"; return 0; }
        return h;
    }

    private delegate nint WndProcDelegate(nint h, uint m, nuint w, nint l);
    private static WndProcDelegate? _wndProcKeepAlive;
    private static readonly WndProcDelegate StaticWndProc = (h, m, w, l) => DefWindowProcW(h, m, w, l);

    /// <summary>Pump the calling (main) thread's message queue until <paramref name="done"/> signals or the timeout fires
    /// — the gallery UI thread is always pumping while a modal is up, so the dialog's owner-SendMessages are serviced.</summary>
    private static bool PumpUntil(ManualResetEventSlim done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done.IsSet) return true;
            while (PeekMessageW(out MSG msg, 0, 0, 0, 1 /*PM_REMOVE*/) != 0)
            {
                TranslateMessage(in msg);
                DispatchMessageW(in msg);
            }
            done.Wait(10);
        }
        return done.IsSet;
    }

    private static void StartProcessWatchdog(int ms)
    {
        var wd = new Thread(() =>
        {
            Thread.Sleep(ms);
            Console.WriteLine($"[fp-probe] PROCESS WATCHDOG: did not exit within {ms}ms — forcing exit(2).");
            DumpThreads();
            Console.Out.Flush();
            Environment.Exit(2);
        }) { IsBackground = true, Name = "fp-probe-watchdog" };
        wd.Start();
    }

    private static void DumpThreads()
    {
        try
        {
            var p = Process.GetCurrentProcess();
            Console.WriteLine($"[fp-probe] process threads={p.Threads.Count}");
            foreach (ProcessThread pt in p.Threads)
            {
                try { Console.WriteLine($"[fp-probe]   os-tid={pt.Id} state={pt.ThreadState} waitReason={(pt.ThreadState == System.Diagnostics.ThreadState.Wait ? pt.WaitReason.ToString() : "-")}"); }
                catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[fp-probe] thread dump failed: {ex.Message}"); }
    }
}
