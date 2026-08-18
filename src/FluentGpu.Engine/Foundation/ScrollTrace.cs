using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FluentGpu.Foundation;

/// <summary>Record kinds for <see cref="ScrollTrace"/>. Column meanings per kind are documented on the emit methods.</summary>
public enum ScrollTraceKind : byte
{
    Frame = 0,       // frame boundary (phase 7): f0=dtMs, f1=input-kind mask, i0=pumped events, i1=scrollActive
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
    OffsetWrite = 16,// kernel chokepoint (ApplyMotion): ONE offset write (i0=node, i1=Activity §2.1, i2=writer §ScrollWriter, f0=offset)
    FrameTiming = 17,// hitch attribution (emitted only for frames >12ms): f0..f5=flush/layout/anim/record/submit/fenceWait ms,
                     // i0=presentMs×100, i1=(measureCount<<10)|min(textShapeMisses,1023), i2=unclampedDtMs×100
    Latency = 18,    // input→offset→present correlation row (NOT dt-gated): i0=publishSeq low 32, i1=stampQuality|stageMask<<8
                     // | trackingSampleValid<<24,
                     // i2=missedVsyncs, f0=lagDip signed, f1=wakeOverheadMs, f2=frameOverrunMs signed,
                     // f3=clockSampleSkewMs signed, f4=presentIntervalMs, f5=velocityDipPerMs, aux=genQpc
}

/// <summary>Provenance of the input stamp a <see cref="ScrollTraceKind.Latency"/> row's <c>aux</c> column carries. The
/// packager REFUSES to publish sub-tick latency percentiles for anything below <see cref="Hardware"/> — a
/// <see cref="Receive"/> stamp is quantised by the producer's own pump rate and cannot resolve a fraction of a frame.
/// </summary>
public enum GenStampQuality : byte
{
    /// <summary>No stamp at all — the event carried QpcTicks == 0. Percentiles are meaningless.</summary>
    Tick = 0,
    /// <summary>Stamped when the engine dequeued the packet from the input ring.</summary>
    Dequeue = 1,
    /// <summary>Stamped when the producer received the packet (e.g. DirectManipulation's per-pump QPC) — quantised by
    /// the pump rate, which runs at roughly half the digitizer rate.</summary>
    Receive = 2,
    /// <summary>The OS-reported device stamp (<c>POINTER_INFO.PerformanceCount</c>).</summary>
    Hardware = 3,
}

/// <summary>Ambient state slots stamped into EVERY <see cref="ScrollTrace"/> record (packed into one int, so the POD
/// record does not grow a cache line). This is how a capture is sliced by phase / gesture state / A-B arm offline
/// without a per-frame filesystem poll on the scroll path. See <see cref="ScrollTrace.SetState"/>.</summary>
public enum ScrollTraceState : byte
{
    /// <summary>Capture-protocol phase ordinal, 0..15 (0 = none). Set by the app from the launcher's marker file.</summary>
    Phase = 0,
    /// <summary>Gesture-state word, 0..3, matching <see cref="FluentGpu.Scroll.ScrollActivity"/> plus a folded Bouncing
    /// flag: 0=Idle, 1=Drag (finger down), 2=Ballistic (fling coast), 3=Driven|Bouncing (wheel/programmatic glide, or
    /// the overscroll spring settling with no live contact) — same slots/values the pre-kernel integrator used, so
    /// <c>ops/diag/pack-feel-summary.ps1</c> keeps parsing unchanged.</summary>
    Gesture = 1,
    /// <summary>1 while the current phase is its first (cold) pass over the content.</summary>
    ColdPass = 2,
    /// <summary>Repetition index within the phase, 0..15.</summary>
    Repetition = 3,
    /// <summary>A/B arm ordinal, 0..3 (e.g. 0=Mica, 1=opaque).</summary>
    AbVariant = 4,
}

/// <summary>Who wrote a scroll offset (scroll-v3 §3.1 single-writer token). The invariant is that every result-column
/// write goes through <c>SceneScrollSink.ApplyMotion(in ScrollWriteToken, in ScrollWrite)</c>, which stamps the writer
/// that produced it — <see cref="FluentGpu.Scroll.ScrollWriteSource"/> (in the portable kernel) declares the SAME byte
/// values so a kernel-side <c>ScrollWrite.Writer</c> and a trace-side <see cref="ScrollWriter"/> are interchangeable
/// without a conversion. <see cref="Tick"/> is the per-frame physics writer (§2.2 <c>ScrollKernel.Tick</c>);
/// <see cref="Reclamp"/> is the structural-only re-apply after layout (mount/bind/geometry changes, edge-pending
/// resolution — at most a handful of extra writes per touched body, never per frame steady-state);
/// <see cref="Lease"/> is the render-thread fling lease (§6) ticking a leased-out body. There is no "direct"/untagged
/// writer any more — the ref-struct token makes an untagged write a compile error outside <c>ApplyMotion</c>.</summary>
public enum ScrollWriter : byte
{
    Tick = 1,            // ScrollKernel.Tick — the per-frame physics writer
    Reclamp = 2,          // ScrollKernel.Reclamp — structural re-apply after layout (mount/bind/SetFrame/edge-pending)
    Lease = 3,            // the render-thread fling lease ticking a leased-out body (§6)
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
/// CSV columns: <c>tMs,frame,kind,i0,i1,i2,f0,f1,f2,f3,f4,f5,auxMs,state,ack</c> — tMs is ms since trace start
/// (Stopwatch), auxMs the event's own QPC stamp mapped to the same axis (empty when the event carried none), state the
/// packed <see cref="ScrollTraceState"/> word, and ack the render seam's acknowledged publish seq at emit (Latency
/// rows only; blank elsewhere, and blank means NOT RECORDED rather than seq 0). NOTE <c>frame</c> is NOT a join key
/// across artifacts (it counts Paint phase 7 only, is written unsynchronised, and suppresses spin frames) — join on
/// <c>tMs</c>, or exactly via <c>ack</c> → the row whose publishSeq matches. Debug pays one disabled
/// branch per guarded call. Release erases those call sites entirely unless <c>FLUENTGPU_DIAG</c> is explicitly defined.
/// Default output: <c>%TEMP%\fg-scrolltrace.csv</c> (overwritten per run).
///
/// GUARD SHAPE — every call site (here and in every consumer assembly) spells the armed condition as the TWO-operand
/// <c>if (ScrollTrace.CompiledIn &amp;&amp; ScrollTrace.Enabled)</c>, or <c>if (!CompiledIn || !Enabled) return;</c> for
/// an early exit. Both operands are required: the const <see cref="CompiledIn"/> is what the optimizer folds to erase
/// the body, while the non-const <see cref="Enabled"/> keeps the guard from being a *constant expression*, which is what
/// stops the compiler flagging the (intentionally) dead body as CS0162 unreachable code in a plain Release build. There
/// is deliberately no single combined <c>On</c> flag — a lone const-false gate warns at every one of ~56 sites.
/// </summary>
public static class ScrollTrace
{
    /// <summary>Compile-time master switch — <c>false</c> unless <c>DEBUG</c> or <c>FLUENTGPU_DIAG</c> is defined, so the
    /// jit/AOT folds <c>CompiledIn &amp;&amp; Enabled</c> to <c>false</c> and erases every guarded body from the shipping
    /// binary. Mirrors <see cref="Diag.CompiledIn"/>. Pair it with <see cref="Enabled"/> at EVERY guard site: the
    /// two-operand form keeps the guard a NON-constant expression, which is what stops the compiler from reporting the
    /// (intentionally) dead body as CS0162 unreachable code while still permitting the fold.</summary>
#if DEBUG || FLUENTGPU_DIAG
    public const bool CompiledIn = true;
#else
    public const bool CompiledIn = false;
#endif

    /// <summary>Runtime gate (only meaningful when <see cref="CompiledIn"/>): true iff <c>FG_SCROLL_TRACE</c> was set
    /// (non-empty, not "0") at process start. The armed condition is <c>CompiledIn &amp;&amp; Enabled</c>.</summary>
#if DEBUG || FLUENTGPU_DIAG
    public static readonly bool Enabled;
#else
    public static readonly bool Enabled = false;
#endif

    private static readonly string s_path = "";
#if DEBUG || FLUENTGPU_DIAG
    private static readonly Rec[] s_buf;
#else
    private static readonly Rec[] s_buf = Array.Empty<Rec>();
#endif
    private static readonly double s_msPerTick = 1000.0 / Stopwatch.Frequency;
    private static readonly long s_t0 = 0;   // initialized here (not #if-fenced) because FlushLocked() reads it unfenced
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
        public int State;           // packed ScrollTraceState slots as of this record (see SetState)
        public int Ack;             // Latency rows only: the render seam's acknowledged publish seq (low 32) at emit
        public ScrollTraceKind K;
    }

#if DEBUG || FLUENTGPU_DIAG
    static ScrollTrace()
    {
        string? v = Environment.GetEnvironmentVariable("FG_SCROLL_TRACE");
        // Same predicate as before (Enabled ⇔ FG_SCROLL_TRACE set, non-empty, not "0"), inverted first so `v` is
        // provably non-null below without a null-forgiving operator.
        if (string.IsNullOrEmpty(v) || v == "0") { s_buf = Array.Empty<Rec>(); return; }
        Enabled = true;
        s_path = v == "1" ? Path.Combine(Path.GetTempPath(), "fg-scrolltrace.csv") : v;
        s_buf = new Rec[1 << 17];   // ~36 s of worst-case continuous gesture records between flushes
        s_t0 = Stopwatch.GetTimestamp();
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush();
        try { Console.WriteLine("[scrolltrace] writing to " + s_path); } catch { }
        EnsureAnchor(s_t0);
    }
#endif

    // ── the shared time anchor ───────────────────────────────────────────────────────────────────────────────────────

    private static long s_anchorQpc;

    /// <summary>QPC origin of the <c>tMs</c> axis: 0 before <see cref="EnsureAnchor"/> has run. When the CSV ring is
    /// armed this is exactly the ring's own <c>t0</c>, so console lines and CSV rows share one axis with no offset.</summary>
    public static long AnchorQpc => s_anchorQpc;

    /// <summary>Milliseconds since the anchor — the timestamp prefix every diagnostic console line carries so that
    /// artifacts which are otherwise timestamp-free ([fps], [scrollperf], [wakediag], [render-census]) can be joined to
    /// the CSV and to the launcher's wall-clock phase markers. 0 before the anchor exists.</summary>
    public static double NowMs => s_anchorQpc == 0 ? 0.0 : (Stopwatch.GetTimestamp() - s_anchorQpc) * s_msPerTick;

    /// <summary>Establish the shared time axis and emit the ONE line that publishes it, to STDERR (the stdout banner is
    /// a separate, human-facing line) so it lands in the same teed console as every other diagnostic stream. Idempotent
    /// — the first caller wins. Callable in ANY build: the CSV ring may be compiled out while the console streams, which
    /// still need an axis, are not.
    ///
    /// The packager HARD-FAILS a bundle whose console has no anchor line. That is deliberate: without it <c>tMs</c> is
    /// milliseconds since an arbitrary in-process instant — not process start, not wall clock — so nothing in the bundle
    /// can be correlated with anything else, and a summary computed anyway would be confidently wrong rather than empty.
    ///
    /// Build identity here is size+mtime, deliberately NOT a SHA256: hashing a ~100 MB NativeAOT image would cost tens
    /// of milliseconds inside whatever frame first touches this class. The launcher's manifest.json carries the real
    /// sha256 and the packager cross-checks size+mtime to prove the console belongs to that exe.</summary>
    public static void EnsureAnchor(long qpc = 0)
    {
        if (s_anchorQpc != 0) return;
        s_anchorQpc = qpc != 0 ? qpc : Stopwatch.GetTimestamp();
        try
        {
            long size = 0, mtime = 0;
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length != 0)
            {
                var fi = new FileInfo(exe);
                if (fi.Exists) { size = fi.Length; mtime = fi.LastWriteTimeUtc.ToFileTimeUtc(); }
            }
            Console.Error.WriteLine(
                "[scrolltrace] anchor wallUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                " qpc=" + s_anchorQpc.ToString(CultureInfo.InvariantCulture) +
                " qpcFreq=" + Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture) +
                " tMs=0 pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) +
                " trace=" + (CompiledIn && Enabled ? "1" : "0") +
                " exeSize=" + size.ToString(CultureInfo.InvariantCulture) +
                " exeMtimeUtc=" + mtime.ToString(CultureInfo.InvariantCulture) +
                " exePath=" + exe);
        }
        catch { /* best-effort diagnostic */ }
    }

    // ── ambient state slots (packed; stamped into every record) ──────────────────────────────────────────────────

    private static int s_state;

    // Slot widths, packed low-to-high. 13 bits total, so Rec grows by one int and stays inside its existing padding.
    private static readonly byte[] s_stateShift = { 0, 4, 6, 7, 11 };
    private static readonly int[] s_stateMask = { 0xF, 0x3, 0x1, 0xF, 0x3 };

    /// <summary>Stamp an ambient state slot into every subsequent record (0-alloc, one masked int write). The capture
    /// protocol sets <see cref="ScrollTraceState.Phase"/>/<see cref="ScrollTraceState.Repetition"/>/
    /// <see cref="ScrollTraceState.AbVariant"/> from the launcher's marker file — from the host loop, NEVER from inside
    /// a frame — and the integrator sets <see cref="ScrollTraceState.Gesture"/> per tick, which is the split (drag →
    /// inertia → settle) a human phase marker structurally cannot record. Values are clamped to the slot width.</summary>
    public static void SetState(ScrollTraceState slot, int value)
    {
        if (!CompiledIn || !Enabled) return;   // CompiledIn is const false in plain Release ⇒ the body folds away and the call inlines to nothing
        int i = (int)slot;
        if ((uint)i >= (uint)s_stateShift.Length) return;
        int mask = s_stateMask[i], shift = s_stateShift[i];
        if (value < 0) value = 0; else if (value > mask) value = mask;
        s_state = (s_state & ~(mask << shift)) | (value << shift);
    }

    /// <summary>The packed state word as stamped into records (diagnostics/gates only).</summary>
    public static int StateWord => s_state;

    /// <summary>Ambient provenance of the newest input stamp feeding the scroll path, set by the Win32 producer /
    /// dispatcher (which know the device) and read by the host when it emits a <see cref="ScrollTraceKind.Latency"/>
    /// row (which does not). A plain static POD field — the same ambient-diagnostic-state pattern as
    /// <see cref="Audit"/> — chosen over widening the Input→Animation delegate seams for a diagnostics-only value.
    /// Defaults to <see cref="GenStampQuality.Tick"/>, i.e. "assume the worst until a producer says otherwise", so an
    /// unstamped path can never masquerade as hardware-timed.</summary>
    public static GenStampQuality ContactStampQuality;

    // ── frame boundary + idle flush ──────────────────────────────────────────────────────────────────────────────

    private static int s_spinSuppressed;   // suppressed no-input micro-frames since the last recorded Frame row

    /// <summary>Frame marker (call once per frame, at the scroll tick): f0=dtMs, f1=input-kind bit mask,
    /// i0=events pumped this frame,
    /// i1=1 when any gesture/scroll animation is live, i2=no-input micro-frames (dt&lt;1ms, 0 events) suppressed since
    /// the previous Frame row — a busy-spinning loop shows as rows ~64 spins apart with i2=63, so the spin RATE is
    /// still measurable without the spin exhausting the ring and forcing mid-gesture flushes. Drives the idle flush.</summary>
    public static void Frame(float dtMs, int pumped, uint inputKindMask, bool scrollActive)
    {
        if (!CompiledIn || !Enabled) return;
        s_frame++;
        // Busy-spin guard: a skip-submit loop can run this tens of thousands of times per second.
        if (pumped == 0 && dtMs < 1f && scrollActive && ++s_spinSuppressed < 64) return;
        // Only record frames near activity (a marker per idle frame would swamp the file); the idle counter still runs.
        if (scrollActive || pumped > 0 || s_idleFrames < IdleFlushFrames)
        {
            Add(new Rec { K = ScrollTraceKind.Frame, F0 = dtMs, F1 = inputKindMask,
                I0 = pumped, I1 = scrollActive ? 1 : 0, I2 = s_spinSuppressed });
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
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.RawWheel, I0 = notch, I1 = flags, I2 = seq, F0 = dip, F1 = gapMs, Aux = qpc });
    }

    /// <summary>Hi-res silence lift (synthesized ScrollEnd): f0=observed silence ms, aux=last packet's QPC.</summary>
    public static void FbLift(float silenceMs, long lastQpc)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.FbLift, F0 = silenceMs, Aux = lastQpc });
    }

    // ── input ring ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ring coalesce fold: i0=InputKind folded, f0=added ΔY, f1=added ΔX, f2=summed ΔY after, f3=summed ΔX
    /// after, aux=incoming packet QPC.</summary>
    public static void Coalesce(byte evKind, float addY, float addX, float sumY, float sumX, long qpc)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.Coalesce, I0 = evKind, F0 = addY, F1 = addX, F2 = sumY, F3 = sumX, Aux = qpc });
    }

    /// <summary>Velocity side-ring deposit: f0=sample X field, f1=sample Y field (NOTE: producers/coalescer decide the
    /// axis mapping — this records what was actually stored), i0=ms stamp, aux=QPC stamp.</summary>
    public static void VelDeposit(float x, float y, uint ms, long qpc)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.VelDeposit, F0 = x, F1 = y, I0 = unchecked((int)ms), Aux = qpc });
    }

    // ── dispatcher (phase consumer + writes) ─────────────────────────────────────────────────────────────────────

    /// <summary>Phase event consumed: i0=InputKind, i1=deviceClass | latched&lt;&lt;8 | momentum&lt;&lt;9, i2=seq,
    /// f0=ΔY (coalesced), f1=ΔX, f2=gesture accumX BEFORE this event folded, f3=accumY before, aux=event QPC.</summary>
    public static void Phase(byte evKind, int deviceFlags, int seq, float dY, float dX, float accX, float accY, long qpc)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.Phase, I0 = evKind, I1 = deviceFlags, I2 = seq, F0 = dY, F1 = dX, F2 = accX, F3 = accY, Aux = qpc });
    }

    /// <summary>Gesture latch: i0=node index, i1=device | horizontal&lt;&lt;8, f0=anchor offset (incl. band excess),
    /// f1=accumX at latch, f2=accumY at latch.</summary>
    public static void Latch(int nodeIdx, int deviceHoriz, float anchor, float accX, float accY)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.Latch, I0 = nodeIdx, I1 = deviceHoriz, F0 = anchor, F1 = accX, F2 = accY });
    }

    /// <summary>IMPULSE estimator sample: i0=source (0=side ring, 1=direct post-fold, 2=reset), f0=sampled pos X,
    /// f1=sampled pos Y, f2=live Vx after, f3=live Vy after, aux=sample QPC.</summary>
    public static void VelSample(int src, float px, float py, float vx, float vy, long qpc)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.VelSample, I0 = src, F0 = px, F1 = py, F2 = vx, F3 = vy, Aux = qpc });
    }

    /// <summary>Release velocity at ScrollEnd: i0=horizontal | seeded&lt;&lt;1 | chainRedirected&lt;&lt;2, f0=Vx, f1=Vy,
    /// f2=chosen axis velocity, f3=band at lift, f4=trailing-32ms displacement velocity (the stop-detector gate),
    /// aux=lift QPC.</summary>
    public static void Release(int flags, float vx, float vy, float chosen, float band, long qpc, float trailing = 0f)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.Release, I0 = flags, F0 = vx, F1 = vy, F2 = chosen, F3 = band, F4 = trailing, Aux = qpc });
    }

    /// <summary>Gesture end/unlatch: i0=reason (0=ScrollEnd, 1=reserved, 2=restart-on-Begin, 3=wheel takeover,
    /// 4=target died), i1=wasMomentum (always 0 now — PTP inertia is engine-owned, no OS-momentum kind exists),
    /// f0=band handed to the spring.</summary>
    public static void GestureEnd(int reason, int wasMomentum, float band)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.GestureEnd, I0 = reason, I1 = wasMomentum, F0 = band });
    }

    /// <summary>Contact 1:1 write: i0=node index, i1=1 when excess chained to an outer scroller, f0=raw desired offset,
    /// f1=offset after clamp, f2=past-edge excess, f3=band written, f4=max offset, f5=outer offset (chained only).</summary>
    public static void ApplyPan(int nodeIdx, int chained, float raw, float offAfter, float excess, float band, float max, float outerOff)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.ApplyPan, I0 = nodeIdx, I1 = chained, F0 = raw, F1 = offAfter, F2 = excess, F3 = band, F4 = max, F5 = outerOff });
    }

    /// <summary>Detented-wheel fling seed: i0=node index, i1=flags (1=sameDir accumulate, 2=atEdge-rejected),
    /// f0=notch DIP delta, f1=fling velocity after, f2=offset. Drop-marker rows may additionally use f3/f4 for the
    /// pointer's window-DIP position; their context-valid flag distinguishes a legitimate zero from absent context.</summary>
    public static void WheelSeed(int nodeIdx, int flags, float deltaDip, float v, float off,
        float hitX = 0f, float hitY = 0f)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec
        {
            K = ScrollTraceKind.WheelSeed, I0 = nodeIdx, I1 = flags, F0 = deltaDip, F1 = v, F2 = off,
            F3 = hitX, F4 = hitY,
        });
    }

    /// <summary>Live gesture cancelled by a detented wheel: f0=offset at takeover, f1=band snapped away.</summary>
    public static void WheelCancel(float off, float band)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.WheelCancel, F0 = off, F1 = band });
    }

    // ── animator ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static long s_lastAnimKey;     // dedup key of the previous AnimTick (node+mode+quantized off/band/vel)
    private static int s_animSuppressed;   // identical consecutive AnimTicks suppressed (i2 on the next emitted row)

    /// <summary>Per-active-node physics tick: i0=node index, i1=<paramref name="activityAndFlags"/> — the low nibble is
    /// <see cref="FluentGpu.Scroll.ScrollActivity"/> (Idle/Drag/Ballistic/Driven), the high nibble (&lt;&lt;4) is
    /// <see cref="FluentGpu.Scroll.ScrollActivityFlags"/> (Programmatic/Wheel/Chained/Banding/Bouncing/Autoscroll) —
    /// i2=identical rows suppressed since the previous emitted row (a frozen band on a spinning loop repeats
    /// verbatim — 1-in-64 sampled), f0=offset, f1=target, f2=velocity, f3=band px, f4=band spring velocity,
    /// f5=frame dtMs.</summary>
    public static void AnimTick(int nodeIdx, byte activityAndFlags, float off, float tgt, float v, float band, float bandVel, float dtMs)
    {
        if (!CompiledIn || !Enabled) return;
        long key = (long)((ulong)(uint)nodeIdx
                 ^ ((ulong)(uint)activityAndFlags << 24)
                 ^ ((ulong)(uint)BitConverter.SingleToInt32Bits(off) << 8)
                 ^ ((ulong)(uint)BitConverter.SingleToInt32Bits(band) << 20)
                 ^ ((ulong)(uint)BitConverter.SingleToInt32Bits(v) << 32));
        if (key == s_lastAnimKey && ++s_animSuppressed < 64) return;   // unchanged physics on a spinning loop
        s_lastAnimKey = key;
        Add(new Rec { K = ScrollTraceKind.AnimTick, I0 = nodeIdx, I1 = activityAndFlags, I2 = s_animSuppressed, F0 = off, F1 = tgt, F2 = v, F3 = band, F4 = bandVel, F5 = dtMs });
        s_animSuppressed = 0;
    }

    /// <summary>Discrete animator transition: i0=node index, i1=event (0=fling end, 1=edge bounce seed, 2=snap
    /// retarget, 3=spring settle, 4=fling seed), f0..f2 = per-event payload (documented at the call sites).</summary>
    public static void AnimEvent(int nodeIdx, int ev, float f0, float f1, float f2)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.AnimEvent, I0 = nodeIdx, I1 = ev, F0 = f0, F1 = f1, F2 = f2 });
    }

    /// <summary>Freeform marker: i0 = caller-defined code, f0 = payload; optional i1/i2/f1 for per-code context.
    /// THE code registry — every live emitter is listed here; adding a code without adding its row is how the next
    /// reader mis-decodes a capture (100/102/103/104 were documented, 110/111/113 were not, and drifted):
    /// <list type="bullet">
    /// <item>100 = anchor re-pin (i1=node, i2=anchorIndex, f0=delta, f1=offset) — Layout/FlexLayout.cs</item>
    /// <item>101 = resampler hit the no-extrapolation clamp — Scroll/ScrollPhysics.cs (ResampleContact)</item>
    /// <item>102 / 103 = RE-HOMED to <see cref="FluentGpu.Scroll.ScrollInputKind.AnchorShift"/> (scroll-v3 §2.1): a
    ///       posted <c>AnchorShift</c> command IS the "pending anchor shift" — the kernel folds it into the live body
    ///       on the next <c>Tick</c>/<c>Reclamp</c> (no separate "drained with no tracked gesture" case; an
    ///       <c>AnchorShift</c> on an untracked body is simply a coordinate-frame rebase applied verbatim). These two
    ///       codes are retired; a kernel-side re-pin now shows up as an ordinary <see cref="OffsetWrite"/> row whose
    ///       writer is <see cref="ScrollWriter.Reclamp"/>.</item>
    /// <item>104 = stale pre-latch shift discarded (i1=node)</item>
    /// <item>105 = touchpad slop-crossing latch REFUSED → wheel fallback (f0/f1 = accumX/accumY;
    ///       i1 bit0-3 = reason 1 wheelHandlerFallback / 2 noScrollerEitherAxis, bit4 = dominant axis was horizontal,
    ///       bit5 = the fallback's DispatchWheel was consumed, bit6 = hit context recorded; when bit6 is set,
    ///       i2 = top hit node (-1 = no hit) and f2/f3 = pointer window DIP) — Input/InputDispatcher.cs</item>
    /// <item>106 = wheel fallback RE-LATCHED on a later pan packet (f0=anchor, i1=node, i2=horiz, f1=gesture travel DIP)
    ///       — Input/InputDispatcher.cs. A 105 with no following 106 for the same gesture IS the dead-scroll signature.</item>
    /// <item>107 = DirectManipulation recovery strike (f0 = ms since DM last engaged, f1 = ms since the last
    ///       DM_POINTERHITTEST, i2 = strikes on the ladder; i1 = detector | action&lt;&lt;4 | dmStatus&lt;&lt;8, where
    ///       detector 1 engage-wedge / 2 suspended / 3 inertia-stall / 4 silent-owner / 5 recovered, action 0 none /
    ///       1 stop / 2 recycle / 3 disable, dmStatus = DIRECTMANIPULATION_STATUS) — Pal/Win32DirectManipulation.cs.
    ///       This block survives a session run with FG_SCROLL_LOG off, which is what made the 218-second input
    ///       blackout forensically silent; a detector-5 row names WHICH rung revived input.</item>
    /// <item>108 = DirectManipulation IDLE HEARTBEAT surfaced something (f0 = ms since the last DM_POINTERHITTEST,
    ///       f1 = ms since DM last engaged, i1 = statusBefore | statusAfter&lt;&lt;8 (DIRECTMANIPULATION_STATUS),
    ///       i2 = sink callbacks that fired inside the heartbeat's UpdateManager.Update) — Pal/Win32DirectManipulation.cs.
    ///       Emitted ONLY when the 250 ms disarmed-pacer Update actually flushed queued DM state, so any 108 row is
    ///       direct evidence of the third stall class: manual-update DM sitting silently on a queue no other pump
    ///       would have drained (zero phase events, zero hit-tests, zero WM motion, ending in a burst).</item>
    /// <item>109 = DM_POINTERHITTEST arrival, rate-limited to one row per 250 ms (f0 = ms since DM last engaged,
    ///       i1 = DIRECTMANIPULATION_STATUS, i2 = unserved hit-tests since the last engage) —
    ///       Pal/Win32DirectManipulation.cs. The disambiguator for the NEXT blackout: 109 rows running THROUGH it mean
    ///       contacts arrived and went unserved (silent-owner shape); no 109 rows at all mean no contact ever reached
    ///       the window. Note 107's f1 carries the same clock, so the two blocks join directly.</item>
    /// <item>110 = extent-table reseed (app: Wavee HomePage)</item>
    /// <item>111 = per-row extent correction — Layout/FlexLayout.cs</item>
    /// <item>113 = hitch slack + GC deltas + last requested wait — Hosting/AppHost.cs</item>
    /// <item>210 / 211 = capture-protocol phase begin / end (i1=phase ordinal, i2=repetition) — ops/diag launcher.
    ///       The 100-block is per-subsystem engine context; the deliberately distant 210-block is capture protocol.</item>
    /// </list>
    /// Next free engine code: 112 (also &gt;=114). Register here in the same commit as the emitter.</summary>
    public static void Note(int code, float f0 = 0f, int i1 = 0, int i2 = 0, float f1 = 0f,
        float f2 = 0f, float f3 = 0f)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.Note, I0 = code, F0 = f0, I1 = i1, I2 = i2, F1 = f1, F2 = f2, F3 = f3 });
    }

    /// <summary>One pure encoded trace row used to pin diagnostic producer schemas in VerticalSlice without arming the
    /// file-backed trace. It is constructed only inside already-compiled diagnostic guards on runtime paths.</summary>
    internal readonly record struct EncodedRow(
        int I0, int I1, int I2,
        float F0, float F1, float F2, float F3, float F4 = 0f, float F5 = 0f);

    internal const int LatchRefusalHitContextBit = 1 << 6;
    internal const int WheelDropHitContextBit = 1 << 10;
    internal const int WheelDropPhaseFallbackBit = 1 << 11;

    /// <summary>Encode note 105 through the same helper the dispatcher emits, so zero-valued pointer coordinates remain
    /// distinguishable from absent context through <see cref="LatchRefusalHitContextBit"/>.</summary>
    internal static EncodedRow EncodeLatchRefusal(float accumX, float accumY, int refusal, bool horizontal,
        bool fallbackHandled, int hitNode, float pointerXDip, float pointerYDip)
        => new(105,
            refusal | (horizontal ? 1 << 4 : 0) | (fallbackHandled ? 1 << 5 : 0) | LatchRefusalHitContextBit,
            hitNode, accumX, accumY, pointerXDip, pointerYDip);

    /// <summary>Encode a wheel-drop row through the same helper the dispatcher emits. The caller supplies the marker,
    /// class and axis/notch flags; this helper owns the context-valid and live-phase-fallback bits plus f3/f4 placement.</summary>
    internal static EncodedRow EncodeWheelDrop(int hitNode, int baseFlags, float delta, float pointerXDip,
        float pointerYDip, bool phaseFallback = false)
        => new(hitNode,
            baseFlags | WheelDropHitContextBit | (phaseFallback ? WheelDropPhaseFallbackBit : 0),
            0, delta, 0f, 0f, pointerXDip, pointerYDip);

    /// <summary>Hitch attribution (emit only for frames whose dt exceeded the hitch threshold): the frame's per-phase
    /// wall-clock split so a lurch in the SAME CSV as the offset writes is directly attributable. f0..f5 =
    /// flush/layout/anim/record/submit/fenceWait ms; i0 = presentMs×100; i1 = (measureCount&lt;&lt;10)|min(shapeMisses,1023);
    /// i2 = UNCLAMPED frame dt ×100 (the animator's dt is clamped at 34ms — this records the true gap).</summary>
    public static void FrameTiming(float flushMs, float layoutMs, float animMs, float recordMs, float submitMs,
        float fenceWaitMs, float presentMs, int measureCount, int shapeMisses, float rawDtMs)
    {
        if (!CompiledIn || !Enabled) return;
        int packed = (measureCount << 10) | Math.Min(shapeMisses, 1023);
        Add(new Rec
        {
            K = ScrollTraceKind.FrameTiming,
            F0 = flushMs, F1 = layoutMs, F2 = animMs, F3 = recordMs, F4 = submitMs, F5 = fenceWaitMs,
            I0 = (int)(presentMs * 100f), I1 = packed, I2 = (int)(rawDtMs * 100f),
        });
    }

    /// <summary>Bit in a Latency row's i1 field proving that f0/f3/f5 describe a contact frame that actually resampled.
    /// Older captures do not carry this bit; offline readers may use their documented non-empty-f3 fallback only for
    /// those captures. This bit is deliberately outside the nine stage bits (i1 bits 8..16).</summary>
    public const int LatencyTrackingSampleValidBit = 1 << 24;

    /// <summary>Pack the latency row's i1 producer field. Kept pure so the bit-layout gate exercises the exact encoder
    /// used by live rows rather than duplicating its arithmetic in a parser fixture.</summary>
    internal static int EncodeLatencyI1(GenStampQuality quality, int stageMask, bool trackingSampleValid)
        => (int)((uint)(byte)quality | ((uint)stageMask << 8)
            | (trackingSampleValid ? (uint)LatencyTrackingSampleValidBit : 0u));

    /// <summary>Input→offset→present correlation row: the ONE record that carries a frame identity (<paramref
    /// name="publishSeq"/>) across the render seam, so an offset write can be joined to the present that actually
    /// carried it. Deliberately NOT gated on a dt threshold the way <see cref="FrameTiming"/> is — a session whose
    /// cadence is perfect and whose feel is bad produces no hitch rows at all, and that is exactly the case this row
    /// exists to measure.
    ///
    /// The join contract (ops/diag/README.md): a sample tagged <c>publishSeq = S</c> joins to the FIRST present whose
    /// acknowledged seq is <c>&gt;= S</c> — never to an equal one. The publisher is DropOldest last-writer-wins, so a
    /// published frame may never present at all; a strict-equality join silently drops exactly the coalesced frames
    /// that a cadence investigation is about.
    ///
    /// Columns: i0 = publishSeq low 32; i1 = (byte)<see cref="GenStampQuality"/> | stageMask&lt;&lt;8 (bit per stage that
    /// exceeded one refresh period) | <see cref="LatencyTrackingSampleValidBit"/> when f0/f3/f5 carry a real
    /// resampled-contact observation. The explicit bit makes a valid numeric zero distinguishable from an empty CSV
    /// cell (no tracking sample); i2 = missedVsyncs in the LOW 16 bits, and in the HIGH 16 bits the OS-ATTESTED
    /// missed-slot count biased by +1 (0 there = not attested, so "no data" is distinguishable from "zero missed" —
    /// they are opposite conclusions and a bare 0 would conflate them). The attested form comes from DXGI
    /// PresentRefreshCount deltas and supersedes the stamp-derived one wherever it is present, because it is what the
    /// display pipeline actually did rather than what our post-Present timestamp implies; f0 = lagDip SIGNED (applied
    /// offset behind the resampled finger is positive); f1 = wakeOverheadMs; f2 = frameOverrunMs SIGNED (negative =
    /// headroom); f3 = clockSampleSkewMs SIGNED; f4 = presentIntervalMs; f5 = velocityDipPerMs; aux = the input packet's
    /// own QPC stamp (0 when the producer carried none — then <paramref name="quality"/> is
    /// <see cref="GenStampQuality.Tick"/> and the packager refuses sub-tick percentiles).</summary>
    public static void Latency(ulong publishSeq, GenStampQuality quality, int stageMask, int missedVsyncs,
        float lagDip, float wakeOverheadMs, float frameOverrunMs, float clockSampleSkewMs,
        float presentIntervalMs, float velocityDipPerMs, long genQpc, ulong ackedPublishSeq = 0UL,
        bool trackingSampleValid = false)
    {
        if (!CompiledIn || !Enabled) return;
        Add(new Rec
        {
            K = ScrollTraceKind.Latency,
            I0 = unchecked((int)(uint)publishSeq),
            I1 = EncodeLatencyI1(quality, stageMask, trackingSampleValid),
            I2 = missedVsyncs,
            F0 = lagDip, F1 = wakeOverheadMs, F2 = frameOverrunMs, F3 = clockSampleSkewMs,
            F4 = presentIntervalMs, F5 = velocityDipPerMs, Aux = genQpc,
            Ack = unchecked((int)(uint)ackedPublishSeq),
        });
    }

    // ── single-writer offset-write trace + audit (scroll-v3 §3.1 token / §9 gate.scroll.single-writer-structural) ────
    // The kernel's ONE chokepoint — SceneScrollSink.ApplyMotion(in ScrollWriteToken, in ScrollWrite) — calls
    // OffsetWrite() after every real offset move, carrying the §2.1 Activity and the ScrollWriter that produced it
    // (Tick | Reclamp | Lease). A lightweight, ALWAYS-AVAILABLE, 0-alloc audit (independent of the CSV ring / see
    // Enabled) lets gate.scroll.single-writer-structural assert the §3.3 shape per node per frame — at most one Tick
    // write and a small bounded number of Reclamp writes (one per SetFrame recipient), never a raw/untagged write —
    // without turning on the (StreamWriter-backed) CSV. All state is static POD; the gate calls AuditBegin() /
    // per-frame AuditResetFrame() / AuditStop() and reads the per-writer counters below.

    /// <summary>Gate-only offset-write audit toggle (0-alloc; separate from the CSV <see cref="Enabled"/> path).</summary>
    public static bool Audit;
    /// <summary>Sticky across the audited run: an offset write carried a writer value the audit doesn't recognise
    /// (i.e. not <see cref="ScrollWriter.Tick"/>/<see cref="ScrollWriter.Reclamp"/>/<see cref="ScrollWriter.Lease"/>) —
    /// the "untagged write" signature the ref-struct token is supposed to make impossible.</summary>
    public static bool AuditForeignWriter;
    /// <summary>Offset writes recorded since the last <see cref="AuditResetFrame"/> (one frame's worth, any writer).</summary>
    public static int AuditWritesThisFrame;
    /// <summary><see cref="ScrollWriter.Tick"/> writes recorded since the last <see cref="AuditResetFrame"/>.</summary>
    public static int AuditTickWritesThisFrame;
    /// <summary><see cref="ScrollWriter.Reclamp"/> writes recorded since the last <see cref="AuditResetFrame"/>.</summary>
    public static int AuditReclampWritesThisFrame;
    /// <summary>Sticky max of <see cref="AuditWritesThisFrame"/> seen at a frame boundary.</summary>
    public static int AuditMaxWritesPerFrame;
    /// <summary>Sticky max of <see cref="AuditTickWritesThisFrame"/> seen at a frame boundary (must stay ≤ 1 for one node).</summary>
    public static int AuditMaxTickWritesPerFrame;
    /// <summary>Sticky max of <see cref="AuditReclampWritesThisFrame"/> seen at a frame boundary.</summary>
    public static int AuditMaxReclampWritesPerFrame;

    /// <summary>Begin an offset-write audit window (resets the counters + arms <see cref="Audit"/>).</summary>
    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void AuditBegin()
    {
        Audit = true; AuditForeignWriter = false;
        AuditWritesThisFrame = 0; AuditTickWritesThisFrame = 0; AuditReclampWritesThisFrame = 0;
        AuditMaxWritesPerFrame = 0; AuditMaxTickWritesPerFrame = 0; AuditMaxReclampWritesPerFrame = 0;
    }
    /// <summary>Frame boundary: fold this frame's write counts into the running maxes, then zero them for the next frame.</summary>
    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void AuditResetFrame()
    {
        if (AuditWritesThisFrame > AuditMaxWritesPerFrame) AuditMaxWritesPerFrame = AuditWritesThisFrame;
        if (AuditTickWritesThisFrame > AuditMaxTickWritesPerFrame) AuditMaxTickWritesPerFrame = AuditTickWritesThisFrame;
        if (AuditReclampWritesThisFrame > AuditMaxReclampWritesPerFrame) AuditMaxReclampWritesPerFrame = AuditReclampWritesThisFrame;
        AuditWritesThisFrame = 0; AuditTickWritesThisFrame = 0; AuditReclampWritesThisFrame = 0;
    }
    /// <summary>End the audit window.</summary>
    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void AuditStop() { Audit = false; }

    /// <summary>Record ONE real offset write (scroll-v3 §3.1): the sole offset-mutation chokepoint (<c>ApplyMotion</c>)
    /// calls this after an actual move. Feeds the 0-alloc single-writer audit always, and the CSV ring when the trace is
    /// armed (<see cref="CompiledIn"/> &amp;&amp; <see cref="Enabled"/>). <paramref name="activity"/> is a
    /// <see cref="FluentGpu.Scroll.ScrollActivity"/> value; <paramref name="writer"/> is a <see cref="ScrollWriter"/>
    /// value (both plain <c>byte</c> here so this file has zero compile dependency on <c>FluentGpu.Scroll</c> — the
    /// portable kernel assembly does not reference <c>FluentGpu.Foundation</c> either). Never allocates.</summary>
    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void OffsetWrite(int nodeIdx, byte activity, byte writer, float offset)
    {
        if (Audit)
        {
            AuditWritesThisFrame++;
            switch (writer)
            {
                case (byte)ScrollWriter.Tick: AuditTickWritesThisFrame++; break;
                case (byte)ScrollWriter.Reclamp: AuditReclampWritesThisFrame++; break;
                case (byte)ScrollWriter.Lease: break;
                default: AuditForeignWriter = true; break;
            }
        }
        if (!CompiledIn || !Enabled) return;
        Add(new Rec { K = ScrollTraceKind.OffsetWrite, I0 = nodeIdx, I1 = activity, I2 = writer, F0 = offset });
    }

    // ── storage + flush ──────────────────────────────────────────────────────────────────────────────────────────

    private static void Add(Rec r)
    {
        r.Qpc = Stopwatch.GetTimestamp();
        r.Frame = s_frame;
        r.State = s_state;
        lock (s_gate)
        {
            if (s_count == s_buf.Length) FlushLocked();   // ring full mid-gesture: pay the write rather than drop data
            s_buf[s_count++] = r;
        }
    }

    /// <summary>Write all pending records to the CSV (called automatically on idle + process exit).</summary>
    public static void Flush()
    {
        if (!CompiledIn || !Enabled) return;
        lock (s_gate) FlushLocked();
    }

    /// <summary>One name per <see cref="ScrollTraceKind"/> ordinal, in ordinal order. MUST be extended in the SAME edit
    /// that adds a kind: <see cref="FlushLocked"/> indexes this unguarded, inside a swallow-all catch that then zeroes
    /// the pending count — so a missing name throws on the first row of the new kind, the catch eats it, and EVERY
    /// buffered row of the session is silently discarded with no error anywhere. <c>gate.latency.kind-names-parity</c>
    /// in the VerticalSlice diagnostics suite exists solely to make that failure impossible to ship.</summary>
    internal static readonly string[] s_kindNames =
    {
        "frame", "rawWheel", "fbLift", "coalesce", "velDeposit", "phase", "latch",
        "velSample", "release", "gestureEnd", "applyPan", "wheelSeed", "wheelCancel",
        "animTick", "animEvent", "note", "offsetWrite", "frameTiming", "latency",
    };

    /// <summary>Kind-name table length, for the parity gate (the array itself stays internal).</summary>
    public static int KindNameCount => s_kindNames.Length;

    private static void FlushLocked()
    {
        if (s_count == 0) return;
        try
        {
            if (s_writer is null)
            {
                var fs = new FileStream(s_path, FileMode.Create, FileAccess.Write, FileShare.Read);
                s_writer = new StreamWriter(fs) { AutoFlush = false };
                // `state` and `ack` are APPENDED, never inserted: existing parsers read this file by column position.
                // `ack` is the render seam's acknowledged publish seq at the moment a Latency row was emitted, which
                // is what turns "the frame before the one that observed a present" (an INFERENCE) into an exact join.
                s_writer.WriteLine("tMs,frame,kind,i0,i1,i2,f0,f1,f2,f3,f4,f5,auxMs,state,ack");
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
                sb.Append(',').Append(r.State);
                // Blank rather than 0 when there is no ack: a consumer must be able to tell "not recorded" from
                // "acked seq 0", which are opposite facts. Every optional column in this file follows that rule.
                sb.Append(',');
                if (r.K == ScrollTraceKind.Latency) sb.Append(r.Ack);
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
