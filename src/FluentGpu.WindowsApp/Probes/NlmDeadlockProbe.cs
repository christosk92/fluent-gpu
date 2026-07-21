using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.WindowsApi.Network;

namespace FluentGpu;

/// <summary>
/// Headless, deterministic repro/regression probe for the "Windows APIs" page deadlock
/// (<c>--nlm-deadlock-probe</c>). It exercises the exact pattern the migrated <c>NetworkCard</c> uses — read NLM
/// (<see cref="NetworkStatus.IsOnline"/> / <see cref="NetworkStatus.GetConnectivity"/>) off the UI thread via
/// <see cref="Task.Run(Action)"/> — WITHOUT the window/GPU/UI stack, so a hang here pins the fault to NLM-on-a-pool-thread
/// rather than the dispatcher or input plumbing.
/// <para>
/// The fault needs the gallery's real apartment shape: the UI thread is an <b>STA that pumps</b>
/// (MsgWaitForMultipleObjectsEx) and holds a live NLM connection-point <c>Advise</c>; the off-thread reader then does
/// <c>CoInitializeEx(APARTMENTTHREADED)</c> on a ThreadPool thread, which creates a <b>non-pumping STA</b>. An NLM call
/// that must marshal cross-apartment from a non-pumping STA can block forever (and corrupts the pool thread's apartment
/// for reuse). A bare console (default MTA) hides the bug — pool threads are MTA there and need no pump — so this probe
/// drives an explicit STA host thread (<c>--sta</c>, the default) to make the apartment shape match the live gallery.
/// </para>
/// <para>Exit codes: <c>0</c> = all reads completed in time (NOT reproduced / fixed); <c>2</c> = a read hung past the
/// timeout (reproduced); <c>3</c> = an unexpected exception. Always runs under a watchdog so the process exits even when a
/// worker is wedged.</para>
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static unsafe class NlmDeadlockProbe
{
    private const int PerReadTimeoutMs = 8000;   // a healthy NLM read is sub-second; 8s is a generous "it's wedged" line.
    private const int ConcurrentReaders = 6;     // surface pool-thread apartment corruption across several reused threads.

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(void* p, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern int PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int TranslateMessage(in MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(in MSG msg);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint n, nint h, uint ms, uint mask, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int ptx; public int pty; }
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;

    public static int Run(string[] args)
    {
        bool mta = Array.IndexOf(args, "--mta") >= 0;   // opt-out: run the host thread MTA (the bug hides — used to prove the STA condition matters).
        bool subscribe = Array.IndexOf(args, "--no-subscribe") < 0;   // hold a live NLM Advise on the host STA (the gallery's NetworkCard does).
        bool repeat = Array.IndexOf(args, "--repeat") >= 0;

        int code = 0;
        Exception? hostEx = null;
        // The "UI thread" surrogate: an STA (the gallery's real apartment) that pumps while the off-thread NLM reads run.
        var host = new Thread(() =>
        {
            try { code = HostBody(mta, subscribe, repeat); }
            catch (Exception ex) { hostEx = ex; code = 3; }
        })
        { Name = "nlm-probe-ui", IsBackground = false };
        host.SetApartmentState(mta ? ApartmentState.MTA : ApartmentState.STA);

        // Hard process watchdog so the caller's outer timeout is never the only backstop (a wedged worker can otherwise
        // keep the process alive). Generous over the sum of the sub-timeouts.
        int budget = PerReadTimeoutMs * (repeat ? 6 : 2) + 8000;
        StartWatchdog(budget);

        host.Start();
        host.Join();
        if (hostEx != null) Console.WriteLine($"[probe] host EXCEPTION: {hostEx}");
        return code;
    }

    private static int HostBody(bool mta, bool subscribe, bool repeat)
    {
        Console.WriteLine($"[probe] host thread apt={Thread.CurrentThread.GetApartmentState()} (gallery UI thread is STA) subscribe={subscribe} perReadTimeout={PerReadTimeoutMs}ms");

        // Initialize this STA exactly like a UI thread that has touched COM, then keep a pump running on it (a wedged
        // cross-apartment marshal looks for THIS pump to service the reply; a UI STA always has one).
        if (!mta) { int hr = CoInitializeEx(null, COINIT_APARTMENTTHREADED); Console.WriteLine($"[probe] host CoInitializeEx(STA) hr=0x{hr:X8}"); }

        IDisposable? sub = null;
        try
        {
            if (subscribe)
            {
                // The gallery's NetworkCard mount: Advise the NLM connection point ON THE UI STA. This pins NLM's proxy to
                // this apartment, so a cross-apartment call from a non-pumping pool STA must marshal back here.
                sub = NetworkStatus.Subscribe(_ => { });
                Console.WriteLine("[probe] NLM Subscribe() Advised on host STA");
            }

            int loops = repeat ? 5 : 1;
            for (int i = 0; i < loops; i++)
            {
                Console.WriteLine($"[probe] off-thread read #{i} via Task.Run (pool thread, the NetworkCard pattern)…");
                if (!OffThreadReadWhilePumping(out string detail))
                {
                    Console.WriteLine($"[probe] HUNG on off-thread read #{i}: {detail}");
                    DumpThreads();
                    return 2;
                }
                Console.WriteLine($"[probe] off-thread read #{i} OK: {detail}");
            }

            Console.WriteLine($"[probe] {ConcurrentReaders} concurrent off-thread reads while STA pumps…");
            if (!ConcurrentOffThreadReadsWhilePumping(out string cdetail))
            {
                Console.WriteLine($"[probe] HUNG on concurrent reads: {cdetail}");
                DumpThreads();
                return 2;
            }
            Console.WriteLine($"[probe] concurrent reads OK: {cdetail}");
        }
        finally
        {
            sub?.Dispose();
            if (!mta) CoUninitialize();
        }

        Console.WriteLine("[probe] PASSED — no hang; NLM off-thread reads completed while a live STA pumped.");
        return 0;
    }

    /// <summary>One <c>Task.Run</c> NLM read; the host STA PUMPS while waiting (so any cross-apartment reply can be
    /// serviced) until the read finishes or the per-read timeout fires.</summary>
    private static bool OffThreadReadWhilePumping(out string detail)
    {
        bool on = false; NetworkConnectivityLevel lvl = default; string aptSeen = "?"; Exception? fault = null;
        var done = new ManualResetEventSlim(false);
        Task.Run(() =>
        {
            try
            {
                aptSeen = Thread.CurrentThread.GetApartmentState().ToString();   // BEFORE NetworkStatus touches COM.
                on = NetworkStatus.IsOnline;            // creates NLM + CoInitializeEx(APARTMENTTHREADED) on this pool thread
                lvl = NetworkStatus.GetConnectivity();  // second NLM create + GetConnectivity — the cross-apartment-marshal suspect
            }
            catch (Exception ex) { fault = ex; }
            finally { done.Set(); }
        });

        bool finished = PumpUntil(done, PerReadTimeoutMs);
        if (!finished) { detail = $"timed out after {PerReadTimeoutMs}ms (worker apt={aptSeen})"; return false; }
        if (fault != null) { detail = $"faulted: {fault.Message}"; return false; }
        detail = $"online={on} level={lvl} workerApt={aptSeen}";
        return true;
    }

    private static bool ConcurrentOffThreadReadsWhilePumping(out string detail)
    {
        int n = ConcurrentReaders;
        var done = new CountdownEvent(n);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
            Task.Run(() =>
            {
                try { _ = NetworkStatus.IsOnline; _ = NetworkStatus.GetConnectivity(); }
                catch { /* a fault still counts as "did not wedge" */ }
                finally { done.Signal(); }
            });

        bool all = PumpUntil(done.WaitHandle, PerReadTimeoutMs);
        detail = all ? $"all {n} finished in {sw.ElapsedMilliseconds}ms" : $"only {n - done.CurrentCount}/{n} finished in {PerReadTimeoutMs}ms (the rest are wedged)";
        return all;
    }

    /// <summary>Block the host STA until <paramref name="done"/> signals or <paramref name="timeoutMs"/> elapses, pumping
    /// the message queue the whole time (the gallery UI thread waits via MsgWaitForMultipleObjectsEx exactly so).</summary>
    private static bool PumpUntil(ManualResetEventSlim done, int timeoutMs) => PumpUntil(done.WaitHandle, timeoutMs);
    private static bool PumpUntil(WaitHandle done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        nint h = done.SafeWaitHandle.DangerousGetHandle();
        while (true)
        {
            if (done.WaitOne(0)) return true;
            int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0) return done.WaitOne(0);
            // Wait for the handle OR any message; service the message queue so a cross-apartment NLM reply can complete.
            MsgWaitForMultipleObjectsEx(1, h, (uint)Math.Min(remaining, 50), QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            while (PeekMessageW(out MSG msg, 0, 0, 0, 1 /*PM_REMOVE*/) != 0)
            {
                TranslateMessage(in msg);
                DispatchMessageW(in msg);
                if (done.WaitOne(0)) return true;
            }
        }
    }

    private static void StartWatchdog(int ms)
    {
        var wd = new Thread(() =>
        {
            Thread.Sleep(ms);
            Console.WriteLine($"[probe] WATCHDOG: probe did not exit within {ms}ms — forcing exit(2). Threads:");
            DumpThreads();
            Console.Out.Flush();
            Environment.Exit(2);
        })
        { IsBackground = true, Name = "nlm-probe-watchdog" };
        wd.Start();
    }

    /// <summary>No-debugger thread snapshot (count + OS state + wait reason), enough to spot a wedged pool thread before a
    /// forced exit.</summary>
    private static void DumpThreads()
    {
        try
        {
            var p = Process.GetCurrentProcess();
            Console.WriteLine($"[probe] process threads={p.Threads.Count}");
            foreach (ProcessThread pt in p.Threads)
            {
                try { Console.WriteLine($"[probe]   os-tid={pt.Id} state={pt.ThreadState} waitReason={(pt.ThreadState == System.Diagnostics.ThreadState.Wait ? pt.WaitReason.ToString() : "-")}"); }
                catch { /* a thread can exit mid-enumeration */ }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[probe] thread dump failed: {ex.Message}"); }
    }
}
