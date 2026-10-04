using System;
using System.Threading;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The video-engine seam (video-smooth-switching-implementation.md §1.1): snapshot out, commands in. The engine MTA
// thread is the ONLY thread that touches COM and the SOLE writer of a POD state snapshot; the UI thread only reads
// the snapshot (seqlock, alloc-free — phase 7.2 constraint) and posts fire-and-forget coalesced commands. TerraFX-free
// and engine-free (BCL only) — VerticalSlice gates it headlessly, and the macOS port reuses it unchanged.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Bit flags describing the live state of a video engine, as published in a <see cref="VideoEngineSnapshot"/>.
/// All bits are set/cleared by the engine thread only.</summary>
[Flags]
public enum VideoEngineFlags : uint
{
    /// <summary>No flags set.</summary>
    None = 0,
    /// <summary>Metadata (duration, natural size answer pending/known, tracks) has loaded.</summary>
    MetadataLoaded = 1 << 0,
    /// <summary>The engine has enough data buffered to begin/resume playback.</summary>
    CanPlay = 1 << 1,
    /// <summary>Playback is currently advancing.</summary>
    Playing = 1 << 2,
    /// <summary>A seek is in flight.</summary>
    Seeking = 1 << 3,
    /// <summary>Playback reached the end of the source.</summary>
    Ended = 1 << 4,
    /// <summary>The engine is in an error state (see <see cref="VideoEngineSnapshot.ErrorCode"/>/<see cref="VideoEngineSnapshot.ErrorHr"/>).</summary>
    Error = 1 << 5,
    /// <summary>Latched by the engine thread once the current source is known live (e.g. a DVR/live manifest); never
    /// cleared within a source — only reset on the next <c>SetSource</c> (or a <c>Detach</c>).</summary>
    LiveSource = 1 << 6,
    /// <summary>The engine has ANSWERED the native-video-size query (GetNativeVideoSize). <c>Known + 0×0</c> means
    /// audio-only; <c>!Known</c> means the answer is still resolving.</summary>
    NaturalSizeKnown = 1 << 7,
    /// <summary>Unrecoverable bring-up failure — the engine must be rebuilt (a fresh <see cref="IDisposable.Dispose"/>
    /// + reconstruct), never <c>SetSource</c>'d again.</summary>
    Faulted = 1 << 8,
    /// <summary>Playback is intended (<see cref="Playing"/>) but the engine is starved: MF raised WAITING after this
    /// source first had enough data and nothing has resumed it (PLAYING / CANPLAY, or the playhead moving again). The
    /// published position is frozen while this is set, so a consumer must not extrapolate it forward.</summary>
    Waiting = 1 << 9,
}

/// <summary>Plain-old-data snapshot of a video engine's observable state, published by the engine thread and read by
/// any thread (typically the UI thread on frame phase 7.2) via <see cref="VideoSnapshotBuffer"/>. No reference types,
/// no COM handles beyond the opaque <see cref="SwapchainHandle"/> — safe to copy by value under a seqlock.</summary>
public struct VideoEngineSnapshot
{
    /// <summary>Which <c>SetSource</c> generation this state describes — the stale-state guard. A reader must ignore
    /// a snapshot whose <see cref="SourceEpoch"/> doesn't match the epoch it's currently tracking.</summary>
    public int SourceEpoch;
    /// <summary>Bumped on every FORMATCHANGE/RESOURCELOST — signals that <see cref="SwapchainHandle"/> must be
    /// re-queried before the next presentation.</summary>
    public int PresentationEpoch;
    /// <summary>The current state flags (see <see cref="VideoEngineFlags"/>).</summary>
    public VideoEngineFlags Flags;
    /// <summary>0 HAVE_NOTHING … 4 HAVE_ENOUGH_DATA (mirrors the HTML5 media <c>readyState</c> ladder).</summary>
    public uint ReadyState;
    /// <summary>Natural (intrinsic) video width, valid iff <see cref="VideoEngineFlags.NaturalSizeKnown"/> is set.</summary>
    public uint NaturalW;
    /// <summary>Natural (intrinsic) video height, valid iff <see cref="VideoEngineFlags.NaturalSizeKnown"/> is set.</summary>
    public uint NaturalH;
    /// <summary>Total duration in seconds; 0 = unknown. For a live source, the session folds this as today.</summary>
    public double DurationSeconds;
    /// <summary>A clock sample of the playback position in seconds, valid AT <see cref="PositionTimestamp"/>.</summary>
    public double PositionSeconds;
    /// <summary>The <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> value at which <see cref="PositionSeconds"/>
    /// was sampled; the UI thread extrapolates <c>pos + Δt·rate</c> while <see cref="VideoEngineFlags.Playing"/>.</summary>
    public long PositionTimestamp;
    /// <summary>The current playback rate (1.0 = normal speed).</summary>
    public double PlaybackRate;
    /// <summary>Start of the seekable (DVR) window in seconds.</summary>
    public double SeekableStart;
    /// <summary>End of the seekable (DVR) window in seconds. <c>(SeekableStart, SeekableEnd) == (0, 0)</c> means not
    /// answered yet.</summary>
    public double SeekableEnd;
    /// <summary>Opaque native swapchain handle; 0 until LOADEDMETADATA, re-queried whenever <see cref="PresentationEpoch"/>
    /// bumps.</summary>
    public nuint SwapchainHandle;
    /// <summary>The MF_MEDIA_ENGINE_ERR code, valid iff <see cref="VideoEngineFlags.Error"/> is set.</summary>
    public uint ErrorCode;
    /// <summary>The underlying HRESULT for the error, valid iff <see cref="VideoEngineFlags.Error"/> is set.</summary>
    public int ErrorHr;
    /// <summary>The <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> value at which FIRSTFRAMEREADY landed for the
    /// current <see cref="SourceEpoch"/>; 0 until it does (or when the backend does not report it). The moment a
    /// surface may drop its poster — an event, never a state guess.</summary>
    public long FirstFrameTimestamp;
    /// <summary>How many SEEKED events the engine has raised for the current <see cref="SourceEpoch"/> (0 for a backend
    /// that does not report them). Lossless where the <see cref="VideoEngineFlags.Seeking"/> flag is not: a seek whose
    /// SEEKING and SEEKED land between two reads never shows the flag, but always moves this counter — what a consumer
    /// owing a repaint after a paused seek compares against its last-seen value.</summary>
    public int SeekedCount;
    /// <summary>Media buffered ahead of the playhead, in ms; 0 when empty or not reported by this backend. What a seek
    /// planner checks before it asks for a fetch, and what a scrub bar's loaded band starts from.</summary>
    public long BufferedAheadMs;
    /// <summary>The stream width (device px) the backend has APPLIED (the echo of the last <see cref="VideoCommandKind.StreamRect"/>
    /// it carried out); 0 until one has been, and again after a new source. A consumer keeps the content size it hands the
    /// compositor at the PREVIOUS value until this equals the size it asked for, so DirectComposition never scales a buffer
    /// still at the old size by the new size's factor.</summary>
    public uint StreamW;
    /// <summary>The stream height (device px) the backend has APPLIED; see <see cref="StreamW"/>.</summary>
    public uint StreamH;
}

/// <summary>Single-writer seqlock around one <see cref="VideoEngineSnapshot"/>. Publish: engine thread only, never
/// concurrent with itself. Read: alloc-free, any thread, any number of concurrent readers (retries on a torn read —
/// i.e. a read that raced a concurrent publish).</summary>
public sealed class VideoSnapshotBuffer
{
    private VideoEngineSnapshot _snap;
    private int _seq;   // even = stable, odd = write in progress

    /// <summary>Publish a new snapshot. Engine-thread-only; must never be called concurrently from two threads.</summary>
    public void Publish(in VideoEngineSnapshot s)
    {
        Interlocked.Increment(ref _seq);
        _snap = s;
        Interlocked.Increment(ref _seq);
    }

    /// <summary>Read the latest published snapshot. Alloc-free; safe from any thread; spins briefly and retries if it
    /// observes a write in progress or races one.</summary>
    public VideoEngineSnapshot Read()
    {
        while (true)
        {
            int s0 = Volatile.Read(ref _seq);
            if ((s0 & 1) != 0) { Thread.SpinWait(8); continue; }
            VideoEngineSnapshot copy = _snap;
            // Full fence: on weak memory models (ARM64) the acquire-only re-read below does not stop the struct
            // copy's field loads from being reordered AFTER it — the validation would then approve a torn copy.
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _seq) == s0) return copy;
        }
    }
}

/// <summary>The kind of a coalesced command posted to a <see cref="VideoEngineCommandQueue"/>. Each kind owns exactly
/// one slot — a later <see cref="VideoEngineCommandQueue.Post"/> of the same kind overwrites (last-wins) any pending,
/// undrained payload for that kind; distinct kinds never collide.</summary>
public enum VideoCommandKind : byte
{
    /// <summary>Transport verb. <c>A</c>: 1 = play, 0 = pause.</summary>
    Transport,
    /// <summary>Seek. <c>A</c>: target seconds. <c>I</c>: 1 = approximate/keyframe seek.</summary>
    Seek,
    /// <summary>Playback rate. <c>A</c>: rate.</summary>
    Rate,
    /// <summary>Volume. <c>A</c>: 0..1.</summary>
    Volume,
    /// <summary>Mute. <c>I</c>: 0/1.</summary>
    Muted,
    /// <summary>Loop. <c>I</c>: 0/1.</summary>
    Loop,
    /// <summary>The destination stream rect for UpdateVideoStream. <c>I</c>: width. <c>J</c>: height.</summary>
    StreamRect,
    /// <summary>Force a repaint of the current frame. No payload.</summary>
    Repaint,
    /// <summary>Switch source. <c>Obj</c>: the url string. <c>I</c>: the new source epoch (a superseded source is
    /// simply skipped by the engine thread).</summary>
    SetSource,
    /// <summary>Release-time source unload (the engine returns warm to the backend pool). No payload.</summary>
    Detach,
}

/// <summary>Fire-and-forget, alloc-free command channel into the engine thread, safe for ANY number of producer threads
/// (the UI thread, a pool continuation after an awaited open, a settle worker) and exactly one consumer (the engine
/// thread). One slot per <see cref="VideoCommandKind"/>, LAST-WINS coalescing within a kind. Each slot is a seqlock whose
/// odd-sequence write section is CAS-acquired, so two producers can never interleave field writes (no torn payload) and
/// the pending flag is set and cleared INSIDE that section (no duplicate or lost apply). <see cref="Post"/> never
/// allocates; its only wait is the few-store write section of a concurrent producer/take on the SAME slot.
/// <see cref="Wake"/> — set once by the engine — is invoked at most once per drain cycle, coalescing however many posts
/// (and <see cref="RequestWake"/> calls) land between two drains.</summary>
public sealed class VideoEngineCommandQueue
{
    private struct Slot { public int Seq; public int Pending; public double A; public int I, J; public object? Obj; }

    private const int KindCount = 10;
    private readonly Slot[] _slots = new Slot[KindCount];
    private int _wakeQueued;

    /// <summary>Invoked (at most once per drain cycle) when a post transitions the queue from empty to non-empty.
    /// Set once by the engine thread at construction/start; never itself allocates on the hot Post path.</summary>
    public Action? Wake;

    /// <summary>Post a command, overwriting any pending, undrained payload of the same <paramref name="kind"/>
    /// (last-wins coalescing). Alloc-free. May be called from any number of threads concurrently.</summary>
    public void Post(VideoCommandKind kind, double a = 0, int i = 0, int j = 0, object? obj = null)
    {
        ref Slot s = ref _slots[(int)kind];
        int s0 = AcquireWriteSection(ref s);
        s.A = a; s.I = i; s.J = j; s.Obj = obj;
        // Pending is raised INSIDE the write section: a consumer that sees it set will wait for the section to close, so
        // it can neither read this payload half-written nor clear a flag that belongs to a later post.
        Volatile.Write(ref s.Pending, 1);
        Volatile.Write(ref s.Seq, unchecked(s0 + 2));
        RequestWake();
    }

    /// <summary>Ask for one out-of-cadence engine-thread turn WITHOUT posting a command (a native MF event): invokes
    /// <see cref="Wake"/> only if no wake is already outstanding for the current drain cycle — the same gate
    /// <see cref="Post"/> uses, so command-driven and event-driven wakes coalesce together. Alloc-free, any thread.</summary>
    public void RequestWake()
    {
        if (Interlocked.Exchange(ref _wakeQueued, 1) == 0) Wake?.Invoke();
    }

    // CAS the slot's sequence from even to odd; returns the even value it was acquired from (publish s0 + 2 to release).
    private static int AcquireWriteSection(ref Slot s)
    {
        SpinWait spin = default;
        while (true)
        {
            int s0 = Volatile.Read(ref s.Seq);
            if ((s0 & 1) == 0 && Interlocked.CompareExchange(ref s.Seq, unchecked(s0 + 1), s0) == s0) return s0;
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>Take the pending payload for <paramref name="kind"/>, if any, clearing its pending flag. Returns
    /// <see langword="false"/> with all outputs zeroed if nothing is pending. Engine-thread-only.</summary>
    public bool TryTake(VideoCommandKind kind, out double a, out int i, out int j, out object? obj)
    {
        ref Slot s = ref _slots[(int)kind];
        a = 0; i = 0; j = 0; obj = null;
        SpinWait spin = default;
        while (true)
        {
            int s0 = Volatile.Read(ref s.Seq);
            if ((s0 & 1) != 0) { spin.SpinOnce(sleep1Threshold: -1); continue; }
            if (Volatile.Read(ref s.Pending) == 0) return false;
            double ta = s.A; int ti = s.I, tj = s.J; object? to = s.Obj;
            // Same weak-memory-model fence as VideoSnapshotBuffer.Read: the payload loads must complete before the
            // validation, or a torn payload could validate.
            Interlocked.MemoryBarrier();
            // Validate AND claim in one step: only if no producer entered since s0 do we own the section, and only then
            // do we clear Pending — so a post that raced the copy is left pending for the next take, never lost.
            if (Interlocked.CompareExchange(ref s.Seq, unchecked(s0 + 1), s0) != s0) continue;
            Volatile.Write(ref s.Pending, 0);
            Volatile.Write(ref s.Seq, unchecked(s0 + 2));
            a = ta; i = ti; j = tj; obj = to;
            return true;
        }
    }

    /// <summary>Call at the top of an engine-thread drain: re-opens the wake gate so any post that lands during (or
    /// after) this drain produces exactly one more <see cref="Wake"/> invocation.</summary>
    public void BeginDrain() => Volatile.Write(ref _wakeQueued, 0);
}
