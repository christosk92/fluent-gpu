using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FluentGpu.Foundation;

/// <summary>Record kinds for <see cref="ScrollTrace"/>. Column meanings per kind are documented on the emit methods.</summary>
public enum ScrollTraceKind : byte
{
    Frame = 0,       // frame boundary (phase 7): f0=dtMs, i0=pumped events, i1=scrollActive
    RawWheel = 1,    // producer: WM_POINTERWHEEL/HWHEEL arrival + classifier verdict
    FbLift = 2,      // producer: hi-res silence lift → synthesized ScrollEnd
    Coalesce = 3,    // ring: an Update folded into the frame's pending event
    VelDeposit = 4,  // ring: a pre-coalesce sample pushed to the velocity side ring
    Phase = 5,       // dispatcher: a scroll-phase event consumed (OnScrollPhase)
    Latch = 6,       // dispatcher: gesture latched (axis+target resolved)
    VelSample = 7,   // dispatcher: IMPULSE estimator fed one sample (live Vx/Vy after)
    Release = 8,     // dispatcher: release velocity computed at ScrollEnd
    GestureEnd = 9,  // dispatcher: latched gesture ended/unlatched
    ApplyPan = 10,   // dispatcher: ApplyTouchPan wrote offset/band (the 1:1 contact write)
    WheelSeed = 11,  // dispatcher: detented-wheel fling seeded (ScrollBy smooth)
    WheelCancel = 12,// dispatcher: gesture cancelled for a detented wheel takeover
    AnimTick = 13,   // animator: per-active-node per-frame physics state
    AnimEvent = 14,  // animator: discrete transition (fling end / bounce seed / snap retarget / spring settle / fling seed)
    Note = 15,       // freeform marker (i0 = code)
    OffsetWrite = 16,// integrator/chokepoint: ONE offset write (i0=node, i1=Phase §2.2, i2=writer §ScrollWriter, f0=offset)
}

/// <summary>Who wrote a scroll offset (scroll-feel-rework-v2 §8 single-writer gate). The v2 invariant (§2.1) is that
/// the phase-7 <c>ScrollIntegrator</c> is the ONLY writer of the offset on the contact/wheel/fling/snap path — every
/// <see cref="OffsetWrite"/> record on that path must carry <see cref="Integrator"/>. Touch finger-pan / scrollbar
/// thumb-drag / pinch / drag-edge auto-scroll are sanctioned synchronous manipulations OUTSIDE the phase contract and
/// tag their own id (the single-writer gate never drives those, so it sees only <see cref="Integrator"/>).</summary>
public enum ScrollWriter : byte
{
    Direct = 0,          // an untagged synchronous SetScrollOffset caller (touch pan / scrollbar / auto-scroll / pinch)
    Integrator = 1,      // the phase-7 ScrollIntegrator tick (via WriteScrollOffset) — the ONLY legal writer on the phase path
}

/// <summary>
/// Full-pipeline scroll diagnostics (set <c>FG_SCROLL_TRACE=1</c>, or a file path, before launch). Records EVERY stage a
/// scroll packet passes through — raw message arrival + classifier verdict (Win32 producer), ring coalescing + velocity
/// side-ring deposits, phase-event dispatch, IMPULSE estimator samples + the computed release velocity, gesture
/// latch/end, the 1:1 offset/band writes, wheel-fling seeds, and the per-frame animator physics — as fixed POD records
/// in a preallocated ring, flushed to CSV on idle frames (never mid-gesture unless the ring fills), so the measurement
/// does not perturb the gesture being measured. Contrast <see cref="ScrollLog"/> (human-readable, per-event
/// string+console writes — visibly perturbs pacing): this one is built for offline numeric analysis.
///
/// CSV columns: <c>tMs,frame,kind,i0,i1,i2,f0,f1,f2,f3,f4,f5,auxMs</c> — tMs is ms since trace start (Stopwatch),
/// auxMs the event's own QPC stamp mapped to the same axis (empty when the event carried none). Zero work when
/// <see cref="On"/> is false (one branch per call site; nothing allocated — the headless alloc gates run with it off).
/// Default output: <c>%TEMP%\fg-scrolltrace.csv</c> (overwritten per run).
/// </summary>
public static class ScrollTrace
{
    /// <summary>True iff <c>FG_SCROLL_TRACE</c> was set (non-empty, not "0") at process start.</summary>
    public static readonly bool On;

    private static readonly string s_path = "";
    private static readonly Rec[] s_buf;
    private static readonly double s_msPerTick = 1000.0 / Stopwatch.Frequency;
    private static readonly long s_t0;
    private static int s_count;
    private static int s_frame;
    private static int s_idleFrames;
    private static StreamWriter? s_writer;
    private static readonly object s_gate = new();
    private static readonly StringBuilder s_sb = new(256);

    /// <summary>Idle frames (no scroll activity) before pending records flush — keeps file writes out of live gestures.</summary>
    private const int IdleFlushFrames = 30;

    private struct Rec
    {
        public long Qpc;            // Stopwatch stamp at record time
        public long Aux;            // the event's own QPC stamp (0 = none)
        public int Frame;
        public int I0, I1, I2;
        public float F0, F1, F2, F3, F4, F5;
        public ScrollTraceKind K;
    }

    static ScrollTrace()
    {
        string? v = Environment.GetEnvironmentVariable("FG_SCROLL_TRACE");
        On = !string.IsNullOrEmpty(v) && v != "0";
        if (!On) { s_buf = Array.Empty<Rec>(); return; }
        s_path = v == "1" ? Path.Combine(Path.GetTempPath(), "fg-scrolltrace.csv") : v!;
        s_buf = new Rec[1 << 17];   // ~36 s of worst-case continuous gesture records between flushes
        s_t0 = Stopwatch.GetTimestamp();
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush();
        try { Console.WriteLine("[scrolltrace] writing to " + s_path); } catch { }
    }

    // ── frame boundary + idle flush ──────────────────────────────────────────────────────────────────────────────

    private static int s_spinSuppressed;   // suppressed no-input micro-frames since the last recorded Frame row

    /// <summary>Frame marker (call once per frame, at the scroll tick): f0=dtMs, i0=events pumped this frame,
    /// i1=1 when any gesture/scroll animation is live, i2=no-input micro-frames (dt&lt;1ms, 0 events) suppressed since
    /// the previous Frame row — a busy-spinning loop shows as rows ~64 spins apart with i2=63, so the spin RATE is
    /// still measurable without the spin exhausting the ring and forcing mid-gesture flushes. Drives the idle flush.</summary>
    public static void Frame(float dtMs, int pumped, bool scrollActive)
    {
        if (!On) return;
        s_frame++;
        // Busy-spin guard: a skip-submit loop can run this tens of thousands of times per second.
        if (pumped == 0 && dtMs < 1f && scrollActive && ++s_spinSuppressed < 64) return;
        // Only record frames near activity (a marker per idle frame would swamp the file); the idle counter still runs.
        if (scrollActive || pumped > 0 || s_idleFrames < IdleFlushFrames)
        {
            Add(new Rec { K = ScrollTraceKind.Frame, F0 = dtMs, I0 = pumped, I1 = scrollActive ? 1 : 0, I2 = s_spinSuppressed });
            s_spinSuppressed = 0;
        }
        if (scrollActive) { s_idleFrames = 0; return; }
        if (++s_idleFrames == IdleFlushFrames && s_count > 0) Flush();
    }

    // ── producer (Win32 wheel classifier) ────────────────────────────────────────────────────────────────────────

    /// <summary>Raw wheel packet at the producer: i0=raw notch units, i1=flag bits (1=horizontal, 2=thisPacketHiRes,
    /// 4=streamIdle, 8=latchedHiRes, 16=ptTouchpadCorroborated, 32=ctrlDown, 64=tookPhasePath, 128=fbActiveBefore),
    /// i2=phase seq, f0=emitted DIP, f1=gap since previous wheel packet (ms), aux=packet QPC.</summary>
    public static void RawWheel(int notch, int flags, int seq, float dip, float gapMs, long qpc)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.RawWheel, I0 = notch, I1 = flags, I2 = seq, F0 = dip, F1 = gapMs, Aux = qpc });
    }

    /// <summary>Hi-res silence lift (synthesized ScrollEnd): f0=observed silence ms, aux=last packet's QPC.</summary>
    public static void FbLift(float silenceMs, long lastQpc)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.FbLift, F0 = silenceMs, Aux = lastQpc });
    }

    // ── input ring ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ring coalesce fold: i0=InputKind folded, f0=added ΔY, f1=added ΔX, f2=summed ΔY after, f3=summed ΔX
    /// after, aux=incoming packet QPC.</summary>
    public static void Coalesce(byte evKind, float addY, float addX, float sumY, float sumX, long qpc)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.Coalesce, I0 = evKind, F0 = addY, F1 = addX, F2 = sumY, F3 = sumX, Aux = qpc });
    }

    /// <summary>Velocity side-ring deposit: f0=sample X field, f1=sample Y field (NOTE: producers/coalescer decide the
    /// axis mapping — this records what was actually stored), i0=ms stamp, aux=QPC stamp.</summary>
    public static void VelDeposit(float x, float y, uint ms, long qpc)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.VelDeposit, F0 = x, F1 = y, I0 = unchecked((int)ms), Aux = qpc });
    }

    // ── dispatcher (phase consumer + writes) ─────────────────────────────────────────────────────────────────────

    /// <summary>Phase event consumed: i0=InputKind, i1=deviceClass | latched&lt;&lt;8 | momentum&lt;&lt;9, i2=seq,
    /// f0=ΔY (coalesced), f1=ΔX, f2=gesture accumX BEFORE this event folded, f3=accumY before, aux=event QPC.</summary>
    public static void Phase(byte evKind, int deviceFlags, int seq, float dY, float dX, float accX, float accY, long qpc)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.Phase, I0 = evKind, I1 = deviceFlags, I2 = seq, F0 = dY, F1 = dX, F2 = accX, F3 = accY, Aux = qpc });
    }

    /// <summary>Gesture latch: i0=node index, i1=device | horizontal&lt;&lt;8, f0=anchor offset (incl. band excess),
    /// f1=accumX at latch, f2=accumY at latch.</summary>
    public static void Latch(int nodeIdx, int deviceHoriz, float anchor, float accX, float accY)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.Latch, I0 = nodeIdx, I1 = deviceHoriz, F0 = anchor, F1 = accX, F2 = accY });
    }

    /// <summary>IMPULSE estimator sample: i0=source (0=side ring, 1=direct post-fold, 2=reset), f0=sampled pos X,
    /// f1=sampled pos Y, f2=live Vx after, f3=live Vy after, aux=sample QPC.</summary>
    public static void VelSample(int src, float px, float py, float vx, float vy, long qpc)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.VelSample, I0 = src, F0 = px, F1 = py, F2 = vx, F3 = vy, Aux = qpc });
    }

    /// <summary>Release velocity at ScrollEnd: i0=horizontal | seeded&lt;&lt;1 | chainRedirected&lt;&lt;2, f0=Vx, f1=Vy,
    /// f2=chosen axis velocity, f3=band at lift, f4=trailing-32ms displacement velocity (the stop-detector gate),
    /// aux=lift QPC.</summary>
    public static void Release(int flags, float vx, float vy, float chosen, float band, long qpc, float trailing = 0f)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.Release, I0 = flags, F0 = vx, F1 = vy, F2 = chosen, F3 = band, F4 = trailing, Aux = qpc });
    }

    /// <summary>Gesture end/unlatch: i0=reason (0=ScrollEnd, 1=MomentumEnd, 2=restart-on-Begin, 3=wheel takeover,
    /// 4=target died), i1=wasMomentum, f0=band handed to the spring.</summary>
    public static void GestureEnd(int reason, int wasMomentum, float band)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.GestureEnd, I0 = reason, I1 = wasMomentum, F0 = band });
    }

    /// <summary>Contact 1:1 write: i0=node index, i1=1 when excess chained to an outer scroller, f0=raw desired offset,
    /// f1=offset after clamp, f2=past-edge excess, f3=band written, f4=max offset, f5=outer offset (chained only).</summary>
    public static void ApplyPan(int nodeIdx, int chained, float raw, float offAfter, float excess, float band, float max, float outerOff)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.ApplyPan, I0 = nodeIdx, I1 = chained, F0 = raw, F1 = offAfter, F2 = excess, F3 = band, F4 = max, F5 = outerOff });
    }

    /// <summary>Detented-wheel fling seed: i0=node index, i1=flags (1=sameDir accumulate, 2=atEdge-rejected),
    /// f0=notch DIP delta, f1=fling velocity after, f2=offset.</summary>
    public static void WheelSeed(int nodeIdx, int flags, float deltaDip, float v, float off)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.WheelSeed, I0 = nodeIdx, I1 = flags, F0 = deltaDip, F1 = v, F2 = off });
    }

    /// <summary>Live gesture cancelled by a detented wheel: f0=offset at takeover, f1=band snapped away.</summary>
    public static void WheelCancel(float off, float band)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.WheelCancel, F0 = off, F1 = band });
    }

    // ── animator ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static long s_lastAnimKey;     // dedup key of the previous AnimTick (node+mode+quantized off/band/vel)
    private static int s_animSuppressed;   // identical consecutive AnimTicks suppressed (i2 on the next emitted row)

    /// <summary>Per-active-node physics tick: i0=node index, i1=ScrollMode, i2=identical rows suppressed since the
    /// previous emitted row (a frozen band on a spinning loop repeats verbatim — 1-in-64 sampled), f0=offset, f1=target,
    /// f2=fling velocity, f3=band px, f4=band spring velocity, f5=frame dtMs.</summary>
    public static void AnimTick(int nodeIdx, int mode, float off, float tgt, float v, float band, float bandVel, float dtMs)
    {
        if (!On) return;
        long key = nodeIdx | ((long)mode << 24)
                 ^ ((long)BitConverter.SingleToInt32Bits(off) << 8)
                 ^ ((long)BitConverter.SingleToInt32Bits(band) << 20)
                 ^ ((long)BitConverter.SingleToInt32Bits(v) << 32);
        if (key == s_lastAnimKey && ++s_animSuppressed < 64) return;   // unchanged physics on a spinning loop
        s_lastAnimKey = key;
        Add(new Rec { K = ScrollTraceKind.AnimTick, I0 = nodeIdx, I1 = mode, I2 = s_animSuppressed, F0 = off, F1 = tgt, F2 = v, F3 = band, F4 = bandVel, F5 = dtMs });
        s_animSuppressed = 0;
    }

    /// <summary>Discrete animator transition: i0=node index, i1=event (0=fling end, 1=edge bounce seed, 2=snap
    /// retarget, 3=spring settle, 4=fling seed), f0..f2 = per-event payload (documented at the call sites).</summary>
    public static void AnimEvent(int nodeIdx, int ev, float f0, float f1, float f2)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.AnimEvent, I0 = nodeIdx, I1 = ev, F0 = f0, F1 = f1, F2 = f2 });
    }

    /// <summary>Freeform marker: i0 = caller-defined code, f0 = payload.</summary>
    public static void Note(int code, float f0 = 0f)
    {
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.Note, I0 = code, F0 = f0 });
    }

    // ── §8 single-writer offset-write trace + audit ──────────────────────────────────────────────────────────────
    // The v2 offset-write chokepoint records one row per real offset move, carrying the §2.2 Phase and the ScrollWriter
    // id. A lightweight, ALWAYS-AVAILABLE, 0-alloc audit (independent of the CSV ring / <see cref="On"/>) lets the §8
    // single-writer gate assert (a) no write carries a writer ≠ Integrator on the phase path, and (b) at most one offset
    // write happens per active node per frame — without turning on the (StreamWriter-backed) CSV. All state is static
    // POD; the gate calls <see cref="AuditBegin"/> / per-frame <see cref="AuditResetFrame"/> / <see cref="AuditStop"/>.

    /// <summary>Gate-only offset-write audit toggle (0-alloc; separate from the CSV <see cref="On"/> path).</summary>
    public static bool Audit;
    /// <summary>Sticky across the audited run: an offset write carried a writer ≠ <see cref="ScrollWriter.Integrator"/>.</summary>
    public static bool AuditForeignWriter;
    /// <summary>Offset writes recorded since the last <see cref="AuditResetFrame"/> (one frame's worth).</summary>
    public static int AuditWritesThisFrame;
    /// <summary>Sticky max of <see cref="AuditWritesThisFrame"/> seen at a frame boundary (must stay ≤ 1 for one node).</summary>
    public static int AuditMaxWritesPerFrame;

    /// <summary>Begin an offset-write audit window (resets the counters + arms <see cref="Audit"/>).</summary>
    public static void AuditBegin() { Audit = true; AuditForeignWriter = false; AuditWritesThisFrame = 0; AuditMaxWritesPerFrame = 0; }
    /// <summary>Frame boundary: fold this frame's write count into the running max, then zero it for the next frame.</summary>
    public static void AuditResetFrame()
    {
        if (AuditWritesThisFrame > AuditMaxWritesPerFrame) AuditMaxWritesPerFrame = AuditWritesThisFrame;
        AuditWritesThisFrame = 0;
    }
    /// <summary>End the audit window.</summary>
    public static void AuditStop() { Audit = false; }

    /// <summary>Record ONE real offset write (scroll-feel-rework-v2 §8): the sole offset-mutation chokepoint calls this
    /// after an actual move. Feeds the 0-alloc single-writer audit always, and the CSV ring when <see cref="On"/>. Never
    /// allocates.</summary>
    public static void OffsetWrite(int nodeIdx, byte phase, ScrollWriter writer, float offset)
    {
        if (Audit)
        {
            AuditWritesThisFrame++;
            if (writer != ScrollWriter.Integrator) AuditForeignWriter = true;
        }
        if (!On) return;
        Add(new Rec { K = ScrollTraceKind.OffsetWrite, I0 = nodeIdx, I1 = phase, I2 = (int)writer, F0 = offset });
    }

    // ── storage + flush ──────────────────────────────────────────────────────────────────────────────────────────

    private static void Add(Rec r)
    {
        r.Qpc = Stopwatch.GetTimestamp();
        r.Frame = s_frame;
        lock (s_gate)
        {
            if (s_count == s_buf.Length) FlushLocked();   // ring full mid-gesture: pay the write rather than drop data
            s_buf[s_count++] = r;
        }
    }

    /// <summary>Write all pending records to the CSV (called automatically on idle + process exit).</summary>
    public static void Flush()
    {
        if (!On) return;
        lock (s_gate) FlushLocked();
    }

    private static readonly string[] s_kindNames =
    {
        "frame", "rawWheel", "fbLift", "coalesce", "velDeposit", "phase", "latch",
        "velSample", "release", "gestureEnd", "applyPan", "wheelSeed", "wheelCancel",
        "animTick", "animEvent", "note", "offsetWrite",
    };

    private static void FlushLocked()
    {
        if (s_count == 0) return;
        try
        {
            if (s_writer is null)
            {
                var fs = new FileStream(s_path, FileMode.Create, FileAccess.Write, FileShare.Read);
                s_writer = new StreamWriter(fs) { AutoFlush = false };
                s_writer.WriteLine("tMs,frame,kind,i0,i1,i2,f0,f1,f2,f3,f4,f5,auxMs");
            }
            var sb = s_sb;
            var ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < s_count; i++)
            {
                ref Rec r = ref s_buf[i];
                sb.Clear();
                sb.Append(((r.Qpc - s_t0) * s_msPerTick).ToString("0.000", ci)).Append(',');
                sb.Append(r.Frame).Append(',');
                sb.Append(s_kindNames[(int)r.K]).Append(',');
                sb.Append(r.I0).Append(',').Append(r.I1).Append(',').Append(r.I2).Append(',');
                AppendF(sb, r.F0, ci); AppendF(sb, r.F1, ci); AppendF(sb, r.F2, ci);
                AppendF(sb, r.F3, ci); AppendF(sb, r.F4, ci); AppendF(sb, r.F5, ci);
                if (r.Aux != 0) sb.Append(((r.Aux - s_t0) * s_msPerTick).ToString("0.000", ci));
                s_writer.WriteLine(sb);
            }
            s_writer.Flush();
        }
        catch { /* best-effort diagnostic */ }
        s_count = 0;
    }

    private static void AppendF(StringBuilder sb, float f, CultureInfo ci)
    {
        if (f != 0f) sb.Append(f.ToString("0.###", ci));
        sb.Append(',');
    }
}
