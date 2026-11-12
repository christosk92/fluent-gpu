using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using FluentGpu.Foundation;

namespace FluentGpu.Media;

/// <summary>
/// The protected-video freeze trail: one clock (QPC) for every step of a protected start and end that could block the GPU or
/// the desktop compositor, and the arming state of the DWM stall probe (<c>DwmStallProbe</c>, FluentGpu.WindowsApi) that
/// measures the compositor against those steps.
/// <para><b>Phase stamps.</b> <see cref="Stamp"/> writes one always-on <c>[pr.phase] &lt;name&gt; qpc=&lt;ms&gt;</c> line and
/// keeps the stamp in a small ring the probe reads when it names the step nearest a stall. Native steps (the PlayReady DLL's
/// <c>FgPrEvent_Phase</c>) carry the QPC they were taken at, so a stamp that reached the log late still sits where it happened
/// (<c>lag=</c> says how late). A blocking step is a <c>.begin</c> / <c>.end</c> pair; the end carries <c>dur=</c>.
/// Lifecycle cadence only: a handful per protected start or end, never per frame.</para>
/// <para><b>Arming.</b> The probe samples only while a protected session is alive (<see cref="EnterSession"/> /
/// <see cref="ExitSession"/>) and for <see cref="LingerMs"/> after the last one ended or the last stamp landed. At rest it is a
/// thread parked on an event (or no thread at all, when no protected video ever played): no sampling, no wake-ups.</para>
/// <para><b>Our own demand.</b> <see cref="NotePresent"/> is the render thread's present submit while armed: the probe uses it
/// only to tell a compositor that is idle (nothing asked it to compose) from one that is frozen (we presented and it did not
/// compose). Not armed, it is two loads and a compare.</para>
/// </summary>
public static class ProtectedVideoTrace
{
    /// <summary>How long the probe keeps sampling after the last protected session ended or the last phase stamp landed.</summary>
    public const double LingerMs = 3000;

    private const int RingSize = 64;

    private static int s_live;                  // protected sessions alive
    private static long s_lingerUntilQpc;       // armed until this QPC even with no session (0 = never armed)
    private static long s_lastPresentQpc;       // the render thread's last present submit while armed
    private static long s_presents;             // presents submitted while armed (monotonic)
    private static int s_commitStampDue;        // a WUC tree edit for a protected surface owes the next controller Commit a stamp
    private static readonly ManualResetEventSlim s_wake = new(false);

    private static readonly object s_gate = new();
    private static readonly long[] s_qpc = new long[RingSize];
    private static readonly long[] s_dur = new long[RingSize];
    private static readonly ulong[] s_session = new ulong[RingSize];
    private static readonly string?[] s_name = new string?[RingSize];
    private static int s_next;                  // ring write index (monotonic; slot = s_next % RingSize)

    private static double QpcMs(long qpc) => qpc * 1000.0 / Stopwatch.Frequency;
    private static long MsToQpc(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    /// <summary>True while the probe should sample: a protected session is alive, or the linger after the last one (or after the
    /// last stamp) has not run out.</summary>
    public static bool Active => IsActive(Stopwatch.GetTimestamp());

    /// <summary><see cref="Active"/> at a QPC the caller already read.</summary>
    public static bool IsActive(long nowQpc) => Volatile.Read(ref s_live) > 0 || nowQpc < Volatile.Read(ref s_lingerUntilQpc);

    /// <summary>Protected sessions alive now.</summary>
    public static int LiveSessions => Volatile.Read(ref s_live);

    /// <summary>The render thread's last present submit (QPC) while armed; 0 before the first.</summary>
    public static long LastPresentQpc => Volatile.Read(ref s_lastPresentQpc);

    /// <summary>Presents submitted while armed (monotonic): the probe differences it across a stall.</summary>
    public static long Presents => Volatile.Read(ref s_presents);

    /// <summary>A protected session came alive: arm the probe until <see cref="ExitSession"/> (plus the linger).</summary>
    public static void EnterSession()
    {
        Interlocked.Increment(ref s_live);
        Touch(Stopwatch.GetTimestamp());
    }

    /// <summary>A protected session ended: the probe keeps sampling for <see cref="LingerMs"/> after the last one.</summary>
    public static void ExitSession()
    {
        if (Interlocked.Decrement(ref s_live) < 0) Interlocked.Exchange(ref s_live, 0);
        Touch(Stopwatch.GetTimestamp());
    }

    /// <summary>Render thread, once per present submit, with the QPC it already read. Records only while armed.</summary>
    public static void NotePresent(long nowQpc)
    {
        if (!IsActive(nowQpc)) return;
        Volatile.Write(ref s_lastPresentQpc, nowQpc);
        Interlocked.Increment(ref s_presents);
    }

    /// <summary>A protected surface entered or left the WUC tree: the next controller Commit is stamped (<c>wuc.commit</c>).</summary>
    public static void RequestCommitStamp() => Volatile.Write(ref s_commitStampDue, 1);

    /// <summary>Whether the controller Commit about to run owes a stamp (clears the request).</summary>
    public static bool TakeCommitStamp() => Volatile.Read(ref s_commitStampDue) != 0 && Interlocked.Exchange(ref s_commitStampDue, 0) != 0;

    /// <summary>Probe thread: block until the probe is armed. Returns at once when it already is.</summary>
    public static void WaitUntilActive()
    {
        while (true)
        {
            s_wake.Reset();
            if (Active) return;
            s_wake.Wait();
        }
    }

    // Extend the linger to now + LingerMs and wake a parked probe.
    private static void Touch(long nowQpc)
    {
        long until = nowQpc + MsToQpc(LingerMs);
        long cur;
        do
        {
            cur = Volatile.Read(ref s_lingerUntilQpc);
            if (cur >= until) break;
        } while (Interlocked.CompareExchange(ref s_lingerUntilQpc, until, cur) != cur);
        s_wake.Set();
    }

    /// <summary>
    /// One protected-video step: logs <c>[pr.phase] &lt;text&gt; qpc=&lt;ms&gt; [dur=&lt;ms&gt;] [s=&lt;session&gt;] [lag=&lt;ms&gt;]</c>,
    /// keeps it for the probe's nearest-step summary and arms the probe for <see cref="LingerMs"/>. <paramref name="text"/> is the
    /// step name, optionally followed by a space and detail (the name is its first word). <paramref name="qpc"/> 0 = now;
    /// <paramref name="durTicks"/> &gt; 0 marks the end of a blocking step that began <paramref name="durTicks"/> earlier.
    /// </summary>
    public static void Stamp(string text, ulong session = 0, long qpc = 0, long durTicks = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        long now = Stopwatch.GetTimestamp();
        bool late = qpc != 0;
        if (qpc == 0) qpc = now;
        int space = text.IndexOf(' ');
        string name = space < 0 ? text : text.Substring(0, space);
        lock (s_gate)
        {
            int i = s_next++ % RingSize;
            s_qpc[i] = qpc;
            s_dur[i] = durTicks > 0 ? durTicks : 0;
            s_session[i] = session;
            s_name[i] = name;
        }
        Touch(now);
        var ci = CultureInfo.InvariantCulture;
        string line = "[pr.phase] " + text + " qpc=" + QpcMs(qpc).ToString("F1", ci);
        if (durTicks > 0) line += " dur=" + QpcMs(durTicks).ToString("F1", ci);
        if (session != 0) line += " s=" + session.ToString(ci);
        if (late) line += " lag=" + QpcMs(Math.Max(0, now - qpc)).ToString("F1", ci);
        Diag.Line(line);
    }

    /// <summary>
    /// The steps around a stall that began at <paramref name="startQpc"/>, as one line's tail:
    /// <c>after=&lt;step&gt; +&lt;ms&gt;</c> (the last step stamped at or before the start, and how long before it),
    /// <c>during=&lt;step&gt;</c> (a blocking step that was running when the stall began: its <c>.end</c> spans the start, or its
    /// <c>.begin</c> has no <c>.end</c> yet), and <c>next=&lt;step&gt; +&lt;ms&gt;</c> (the first step stamped after the start).
    /// Cold: once per stall.
    /// </summary>
    public static string DescribeAround(long startQpc)
    {
        var ci = CultureInfo.InvariantCulture;
        string? after = null, next = null, during = null;
        long afterQpc = 0, nextQpc = 0;
        lock (s_gate)
        {
            int count = Math.Min(s_next, RingSize);
            for (int k = 0; k < count; k++)
            {
                int i = (s_next - 1 - k) % RingSize;   // newest first
                string? n = s_name[i];
                if (n is null) continue;
                long q = s_qpc[i];
                if (q <= startQpc)
                {
                    if (after is null || q > afterQpc) { after = n; afterQpc = q; }
                    // A .begin before the start with no .end of the same step (same session) stamped after it: still running.
                    if (during is null && n.EndsWith(".begin", StringComparison.Ordinal) && !HasEndAfterLocked(n, s_session[i], q))
                        during = n.Substring(0, n.Length - ".begin".Length);
                }
                else if (next is null || q < nextQpc) { next = n; nextQpc = q; }
                // An .end whose span covers the start: the step was blocking when the stall began.
                if (during is null && s_dur[i] > 0 && q - s_dur[i] <= startQpc && q >= startQpc)
                    during = n.EndsWith(".end", StringComparison.Ordinal) ? n.Substring(0, n.Length - ".end".Length) : n;
            }
        }
        string s = after is null ? "after=- +-" : "after=" + after + " +" + QpcMs(startQpc - afterQpc).ToString("F0", ci);
        s += " during=" + (during ?? "-");
        s += next is null ? " next=-" : " next=" + next + " +" + QpcMs(nextQpc - startQpc).ToString("F0", ci);
        return s;
    }

    private static bool HasEndAfterLocked(string beginName, ulong session, long beginQpc)
    {
        string stem = beginName.Substring(0, beginName.Length - ".begin".Length);
        int count = Math.Min(s_next, RingSize);
        for (int k = 0; k < count; k++)
        {
            int i = (s_next - 1 - k) % RingSize;
            string? n = s_name[i];
            if (n is null || s_session[i] != session || s_qpc[i] < beginQpc) continue;
            if (n.Length == stem.Length + 4 && n.StartsWith(stem, StringComparison.Ordinal) && n.EndsWith(".end", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
