using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Media;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The "real unprotected video" milestone (M3) of the DRM-free video compositing spine
/// (<c>docs/plans/video-compositing-spine-design.md</c>), rewritten to the snapshot/command seam
/// (<c>docs/plans/video-smooth-switching-implementation.md</c> §1). Drives <c>IMFMediaEngineEx</c> in <b>windowless
/// swap-chain mode</b> to decode CLEAR (non-DRM) media and hand its DirectComposition swap-chain HANDLE to the engine's
/// <see cref="FluentGpu.Pal.IVideoPresenter"/> (via <c>CreateSurfaceFromHandle</c> → <c>SetContent</c>), so decoded
/// frames composite as the sibling video visual z-BELOW the UI — the SAME path DRM reuses by attaching a CDM.
///
/// <para>Sequence (MS Learn <c>EnableWindowlessSwapchainMode</c> + the microsoft/media-foundation
/// <c>MediaEngineDCompWin32Sample</c>): create a D3D11 video device + <c>IMFDXGIDeviceManager</c> → create
/// <c>IMFMediaEngine</c> with an <c>IMFMediaEngineNotify</c> callback + the DXGI manager → QI <c>IMFMediaEngineEx</c> →
/// <c>EnableWindowlessSwapchainMode(TRUE)</c>. <see cref="Start"/> does ONLY this bring-up — <c>SetSource</c>/<c>Play</c>
/// happen later, driven by posted commands, which is what makes warm-engine reuse across a track switch possible: the
/// SAME engine instance lives across many <see cref="PostSetSource"/> calls instead of being torn down and rebuilt per
/// track (the E3 fix). On <c>LOADEDMETADATA</c>: <c>GetVideoSwapchainHandle(&amp;h)</c> is queried by the self-refresh
/// below and cached in the published snapshot; a bound consumer places it via <c>UpdateVideoStream</c>. Thereafter the
/// Media Engine auto-presents each decoded frame into its windowless swap chain.</para>
///
/// <para><b>Threading — snapshot out, commands in.</b> The app UI thread is an OleInitialize'd STA — with a hardware
/// <c>IMFDXGIDeviceManager</c> attached, the Media Engine's video-device setup on MF worker threads DEADLOCKS source
/// resolution against a blocked STA (empirically it never leaves HAVE_NOTHING/WAITING). So EVERY engine COM call
/// (device + DXGI manager + engine creation + <c>SetSource</c>/transport/<c>UpdateVideoStream</c>/teardown) runs on a
/// dedicated <b>MTA</b> thread here, and — this is the v2 change — NOTHING outside that thread ever waits for one of
/// those calls to complete. The old <c>Invoke&lt;T&gt;</c> pattern marshaled a UI-thread closure onto this thread and
/// blocked (bounded at <c>InvokeTimeoutMs</c>) for the answer; <see cref="MfMediaSession.PumpVideo"/> paid that cost up
/// to 7× per pump while a video was opening — the UI-thread freeze this rewrite exists to fix. Now the engine thread is
/// the SOLE writer of a POD <see cref="VideoEngineSnapshot"/>, published through a single-writer seqlock
/// (<see cref="VideoSnapshotBuffer"/>) every time its loop turns; every reader (any thread, including the UI/pump
/// thread) only ever reads that snapshot — alloc-free, wait-free, and never blocks. Callers post fire-and-forget,
/// coalesced commands through <see cref="VideoEngineCommandQueue"/> (LAST-WINS per kind — a burst of seek-drag or
/// transport calls collapses to the one the engine thread actually sees); <see cref="VideoEngineCommandQueue.Wake"/> (set
/// once, below) is the SAME coalesced-wake mechanism a native MF event uses to ask for an out-of-cadence refresh
/// (<see cref="OnEngineEvent"/>), so there is exactly one wake path regardless of whether a command or a native event
/// caused it. MF fires <c>EventNotify</c> on its own worker threads → the callback only sets volatile bits and requests
/// that one coalesced wake; it never raises <see cref="StateChanged"/> directly (a subscriber that acted on the STALE
/// state the raise raced against is exactly the ordering hole this design removes — publish always happens-before the
/// event that announces it). TerraFX (MF/D3D11) stays inside FluentGpu.Windows.</para>
/// </summary>
public sealed unsafe class VideoMediaEngine : IDisposable, IVideoEngine
{
    private const int S_OK = 0;
    private const int EFail = unchecked((int)0x80004005);
    // MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED — what a failed SetSource is reported as.
    private const uint MfMediaEngineErrSrcNotSupported = 4;
    // MF_MEDIA_ENGINE_ERR_DECODE — what a removed D3D11 device is reported as (the session maps it to a Retryable Decode error).
    private const uint MfMediaEngineErrDecode = 3;
    // HTML5 readyState HAVE_FUTURE_DATA: at or above it playback can continue, so a STALLED download alone is not a stall.
    private const uint HaveFutureData = 3;
    // How far the playhead must move past where a stall began before it counts as playing again with no PLAYING / CANPLAY.
    private const double WaitProgressSeconds = 0.25;
    // The MIME type MF names an HLS master playlist by (Apple's registered type).
    private const string HlsMimeType = "application/vnd.apple.mpegurl";
    // MFMEDIASOURCE_CHARACTERISTICS.MFMEDIASOURCE_IS_LIVE.
    private const uint MediaSourceIsLive = 0x1;
    // Self-refresh cadence: while resolving/playing/live (time-sensitive telemetry — position, the live DVR window
    // sliding with wall-clock time) vs. parked (paused/ready/ended/faulted — nothing time-sensitive to refresh; posted
    // commands still wake the loop immediately regardless of this timeout via VideoEngineCommandQueue.Wake).
    private const int ActiveRefreshMs = 250;
    private const int ParkedRefreshMs = 1000;
    // F066: while video is playing, the renderer's FRAMES_RENDERED / FRAMES_DROPPED are read at most this often (Chromium's 500 ms
    // statistics poll; an in-proc property read, never when paused, parked, starved or seeking).
    private const long FrameStatsPollMs = 500;
    // HRESULT_FROM_WIN32(ERROR_TIMEOUT): the HRESULT a rendered-frame hang (RenderedFrameWatch) is reported with, under MF_MEDIA_ENGINE_ERR_DECODE.
    private const int HrNoRenderedFrame = unchecked((int)0x800705B4);
    // Position-only snapshot changes coalesce StateChanged to at most this often (~1 Hz) — MediaSeekBar already treats a
    // native position report as a low-cadence anchor and interpolates on its own FrameClock ticker.
    private static readonly long s_positionRaiseTicks = Stopwatch.Frequency;

    // A cached no-op used purely to unblock _work.TryTake early (the wake signal); never allocated per-call.
    private static readonly Action s_wakeSignal = static () => { };

    // ── Owned only on the engine (MTA) thread ──────────────────────────────────────────────────────────────────────
    // The shared D3D11 video device + DXGI manager (MfVideoDevice: one per adapter LUID, process-wide) and OUR lease on it. _d3d
    // and _dxgiManager are BORROWED from the lease — never Released here; the lease also keeps MFStartup alive for this engine.
    private SharedDeviceCache<MfVideoDevice>.Lease? _videoLease;
    private ID3D11Device* _d3d;
    private IMFDXGIDeviceManager* _dxgiManager;
    private IMFMediaEngine* _engine;
    private IMFMediaEngineEx* _engineEx;
    // Owns the GCHandle to this engine; our reference to it is dropped in DisposeCom, MF's own frees it (see the CCW).
    private MediaEngineNotifyCcw* _notify;
    // Probed ONCE on the engine thread right after the engine exists (a static capability of this machine's Media
    // Foundation install — it cannot change per source), so callers never pay a round-trip to ask.
    private volatile bool _canPlayHls;
    // Set when bring-up (Start's CreateEngine) failed, or when the engine's D3D11 device was found removed (a TDR, driver
    // update or adapter change — see CheckDeviceRemoved). Sticky for this instance's lifetime — MfMediaPlayer.LeaseEngine
    // discards a Faulted engine and builds a fresh one; DrainCommands/RefreshAndPublishSnapshot keep running against
    // null COM pointers after a failed bring-up (every call below is null-checked), so the loop stays alive and Dispose()
    // still joins cleanly.
    private volatile bool _faulted;
    // Engine thread only: the D3D11 device was found removed (GetDeviceRemovedReason != S_OK). Logged once.
    private bool _deviceRemoved;
    // Raised on an MF worker by a native ERROR / RESOURCELOST; consumed by RefreshAndPublishSnapshot, which asks the device
    // (a COM call that must stay on the engine thread) whether it was removed.
    private int _deviceProbePending;

    // ── Engine-thread scheduling ────────────────────────────────────────────────────────────────────────────────────
    private Thread? _thread;
    // Purely the blocking/wake primitive for the loop's TryTake below (see class doc): nothing else marshals arbitrary
    // closures onto this thread any more (that was Invoke<T>, deleted outright) — a post through Commands, or a native
    // MF event, wakes the loop by adding the cached no-op, which unblocks TryTake before ParkedRefreshMs/ActiveRefreshMs
    // elapse.
    private readonly BlockingCollection<Action> _work = new();
    private readonly VideoSnapshotBuffer _snapshot = new();
    // F197: this engine's creation number (the id both always-on lines and the census row carry) and its MediaCensus registration, live from
    // the thread's bring-up to its DisposeCom. Engine-thread only (Interlocked so DisposeCom stays idempotent).
    private static int s_nextOrdinal;
    private readonly int _ordinal = Interlocked.Increment(ref s_nextOrdinal);
    private int _censusToken;
    private readonly VideoEngineCommandQueue _commands = new();

    // ── Per-source event state (set on MF worker threads via OnEngineEvent; read only on the engine thread inside
    // RefreshAndPublishSnapshot) — reset by ResetPerSourceState on a Detach and at the start of each new source. ──────
    private volatile bool _metadataLoaded;
    private volatile bool _canPlay;
    private volatile bool _playing;
    private volatile bool _seeking;
    private volatile bool _ended;
    private volatile bool _error;
    // MF raised WAITING for this source after it first had enough data (CANPLAY / PLAYING): playback is intended but the
    // engine is starved. Cleared by PLAYING, CANPLAY, PAUSE, ENDED or the playhead moving past _waitAnchorPos. Published as
    // VideoEngineFlags.Waiting only while _playing — see RefreshAndPublishSnapshot.
    private volatile bool _waiting;
    // A STALLED (download stall) event is only a hint: refresh promotes it to _waiting when the engine's own ready state
    // confirms playback cannot continue (< HAVE_FUTURE_DATA).
    private volatile bool _stalledHint;
    // Counts stalls (Interlocked: MF workers) so the engine thread re-anchors its progress check for EACH stall, even two
    // that land between refreshes.
    private int _waitingSerial;
    private volatile uint _errorCode;
    private volatile int _errorHr;
    // Latched true once MF reports this source as live (+Infinity duration, or MFMEDIASOURCE_IS_LIVE — only judged after
    // LOADEDMETADATA, see EngineLivenessRule: GetDuration is NaN before metadata and after a detach, which is "not known
    // yet", never "live") — NEVER cleared within a source (only ResetPerSourceState clears it, for the NEXT source).
    private volatile bool _liveLatched;
    // Bumped on every PRESENTATION-affecting event: a native FORMATCHANGE/RESOURCELOST, AND a SetSource (a new source
    // invalidates whatever swap-chain handle the old one produced just as surely as a resource loss does). Interlocked
    // (not volatile): MF raises events from a pool of worker threads, so two format changes landing at once on a plain
    // ++ would collapse into one and a consumer would never re-query.
    private int _presentationEpoch;
    // The source-switch generation DrainCommands last committed (i.e. actually called SetSource for) — published
    // verbatim as VideoEngineSnapshot.SourceEpoch so a session can tell "this snapshot describes MY source" from stale.
    private int _committedSourceEpoch;
    // Allocated on whatever thread calls PostSetSource (Interlocked, so two callers can never hand out the same epoch).
    private int _nextSourceEpoch;

    // ── Engine-thread-only caches (no cross-thread visibility needed — only RefreshAndPublishSnapshot touches these) ──
    // Both "queried for epoch" trackers use the SAME presentation epoch, so a FORMATCHANGE/RESOURCELOST or a SetSource
    // (which also bumps it — see DrainCommands) re-asks natural size AND the swap-chain handle together, exactly once,
    // no matter how many refresh ticks pass before the next epoch change. -1 = never queried for any epoch.
    private uint _naturalW, _naturalH;
    private int _naturalQueriedForEpoch = -1;
    private nuint _cachedHandle;
    private int _handleQueriedForEpoch = -1;   // presentation epoch the cached handle was last queried at; -1 = never
    // F198: every GetVideoSwapchainHandle call returns a FRESH NT handle the caller must close (the MS sample, Chromium and Firefox all
    // own it). They are owned here: the current one stays open for as long as it is published (a device recovery binds it again), a
    // superseded one is closed one replacement or SwapchainHandleLedger.DefaultGraceMs later - after the render thread's
    // CreateSurfaceFromHandle took its own reference - and the rest at DisposeCom.
    private readonly SwapchainHandleLedger _handles = new(static h => CloseHandle((HANDLE)(nint)h));
    // F066: the renderer's frame counters accumulated across MF's post-flush resets, the next poll time and the no-rendered-frame watch.
    private VideoFrameCounters _frames;
    private RenderedFrameWatch _frameWatch;
    private long _nextStatsPollMs;
    private bool _frameStatsReadable;   // the last statistics poll read real counters; the hang watch only counts time while they are readable
    private double _cachedDuration;
    private double _lastRate = 1.0;            // the last Rate command applied; re-applied after every SetSource, and the
                                               // published fallback while there is no engine to read the rate back from
    // A source is set (SetSource drained, no Detach since). While false the engine is parked warm-idle: nothing
    // time-sensitive to refresh, so it polls at ParkedRefreshMs regardless of the (reset) per-source bits.
    private bool _hasSource;
    // One post-metadata-NaN diagnostic per source (the policy question EngineLivenessRule documents).
    private bool _nanAfterMetadataLogged;
    // Engine thread only: the last refresh found LOADEDMETADATA REAL — the event bit AND the engine's own ready state
    // agree. The bit alone is set on an MF worker with no source tag, so a late event of the PREVIOUS source can set it
    // right after a reset; nothing per-source (size, handle, Playing/Ended, first frame) is trusted before this holds.
    private bool _metadataTrusted;
    // Engine thread only: the stall serial the progress check is anchored to, and the playhead at that moment.
    private int _waitAnchorSerial = -1;
    private double _waitAnchorPos;
    // Stopwatch timestamp of the FIRST FRAME of the current source (FIRSTFRAMEREADY, then LOADEDDATA, then PLAYING as the
    // fallbacks when MF raises none of the earlier ones — a paused open raises no PLAYING); 0 until it lands. Written on MF workers (CompareExchange: first writer wins) and cleared by
    // ResetPerSourceState, published as VideoEngineSnapshot.FirstFrameTimestamp for the session's surface publish.
    private long _firstFrameTicks;
    // SEEKED events of the current source (Interlocked: MF workers). Counted, not a flag, so a paused seek whose SEEKING and
    // SEEKED coalesce into one refresh still moves a published value — see VideoEngineSnapshot.SeekedCount.
    private int _seekedCount;
    // Engine thread only: the stream size the last StreamRect actually applied (UpdateVideoStream(dst) succeeded) — published
    // as VideoEngineSnapshot.StreamW/H, the echo a session keeps its compositor content size behind. Per source.
    private uint _appliedStreamW, _appliedStreamH;
    private VideoEngineSnapshot _lastPublished;
    private long _lastRaiseTicks;

    /// <inheritdoc/>
    public event Action? StateChanged;
    /// <inheritdoc/>
    public VideoEngineSnapshot Snapshot => _snapshot.Read();
    /// <inheritdoc/>
    public VideoEngineCommandQueue Commands => _commands;
    /// <inheritdoc/>
    public bool CanPlayHls => _canPlayHls;

    public VideoMediaEngine()
    {
        // Wired up here (not in ThreadMain) so a Post/PostSetSource that races the thread's own startup is never lost:
        // the item just sits queued in _work until the loop starts consuming.
        _commands.Wake = WakeEngine;
    }

    /// <inheritdoc/>
    public void Start()
    {
        // F102: AboveNormal so Play/Seek/UpdateVideoStream commands are not queued behind image-decode bursts on a busy box
        // (MF decodes and presents on its own MMCSS work-queue threads; this thread only issues and polls).
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "VideoMediaEngine", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    // The queue's own gate decides whether a wake is already outstanding (commands and native MF events share it); this
    // is only the primitive it invokes once per drain cycle.
    private void WakeEngine()
    {
        // TryAdd never blocks (unbounded collection) and is safe even before the thread exists or after CompleteAdding
        // (a queue-completed race here just means Dispose already began; there is nothing left to wake for).
        try { _work.TryAdd(s_wakeSignal); } catch (InvalidOperationException) { }
    }

    private void ThreadMain()
    {
        int hr = CreateEngine();
        // Always-on (F197): one line per engine create and destroy, with the live count, so a per-rebuild leak reads straight off the log.
        _censusToken = MediaCensus.Register(MediaCensusKind.VideoEngine, DescribeCensus);
        Diag.Line($"[video] engine.create id={_ordinal} hr=0x{(uint)hr:X8} live={MediaCensus.Count(MediaCensusKind.VideoEngine)}");
        if (hr < 0)
        {
            _faulted = true;
            // Release whatever partial bring-up succeeded (e.g. the D3D11 device / DXGI manager if engine creation
            // itself failed) so a Faulted, never-leased-again engine does not leak GPU resources for the process
            // lifetime; DisposeCom() is idempotent, so the loop's own eventual DisposeCom() call below is a no-op.
            DisposeCom();
        }

        while (true)
        {
            _work.TryTake(out Action? w, RefreshIntervalMs());
            _commands.BeginDrain();
            try { DrainCommands(); }
            catch (Exception ex) { Console.Error.WriteLine($"VideoMediaEngine: DrainCommands threw: {ex.Message}"); }
            if (w is not null)
            {
                try { w(); } catch (Exception ex) { Console.Error.WriteLine($"VideoMediaEngine: work item threw: {ex.Message}"); }
            }
            try { RefreshAndPublishSnapshot(); }
            catch (Exception ex) { Console.Error.WriteLine($"VideoMediaEngine: RefreshAndPublishSnapshot threw: {ex.Message}"); }
            if (_work.IsCompleted) break;
        }
        DisposeCom();
    }

    private int RefreshIntervalMs() => !_hasSource ? ParkedRefreshMs : !_metadataTrusted || _playing || _liveLatched ? ActiveRefreshMs : ParkedRefreshMs;

    private int CreateEngine()
    {
        int hr;
        // Lease the process-wide video device for the renderer's adapter: the first engine (or the first after an adapter change
        // or device removal) creates it and runs MFStartup; every later bring-up reuses it, so a rebuild costs only the engine.
        var lease = MfVideoDevice.Acquire();
        if (lease == null) return EFail;
        _videoLease = lease;
        _d3d = lease.Device.D3d;
        _dxgiManager = lease.Device.Manager;

        // The CCW owns this GCHandle from here: it is freed when the struct's last reference goes (MediaEngineNotifyCcw.ReleaseRef).
        _notify = MediaEngineNotifyCcw.Create(GCHandle.ToIntPtr(GCHandle.Alloc(this)));

        IMFAttributes* attrs = null;
        IMFMediaEngineClassFactory* factory = null;
        try
        {
            if ((hr = MFCreateAttributes(&attrs, 4)) < 0) return Log("MFCreateAttributes", hr);
            Guid gCb = MF.MF_MEDIA_ENGINE_CALLBACK; attrs->SetUnknown(&gCb, (IUnknown*)_notify);
            if (_dxgiManager != null) { Guid gDm = MF.MF_MEDIA_ENGINE_DXGI_MANAGER; attrs->SetUnknown(&gDm, (IUnknown*)_dxgiManager); }
            // F249: BGRA is the long-standing output (a video-processor NV12 -> BGRA pass per decoded frame). NV12 only behind --fg video-nv12
            // and only where the output's overlay probe says NV12 can take a plane; the verdict is the newest published one (an engine is
            // created after its window's output was probed), and an unprobed output keeps BGRA.
            VideoOverlayCaps overlayCaps = VideoOverlayCaps.Latest;
            VideoOutputFormat outputFormat = VideoOverlayCaps.ChooseOutputFormat(FluentGpu.Hosting.EngineSwitches.Nv12VideoOutput, in overlayCaps);
            DXGI_FORMAT dxgiFormat = outputFormat == VideoOutputFormat.Nv12 ? DXGI_FORMAT.DXGI_FORMAT_NV12 : DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            if (FluentGpu.Hosting.EngineSwitches.Nv12VideoOutput)
                Diag.Line($"[video] output format={(outputFormat == VideoOutputFormat.Nv12 ? "nv12" : "bgra")} overlay[{overlayCaps.Describe()}] (--fg video-nv12)");
            Guid gFmt = MF.MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT; attrs->SetUINT32(&gFmt, (uint)dxgiFormat);

            Guid clsid = CLSID.CLSID_MFMediaEngineClassFactory;
            Guid iidF = IID.IID_IMFMediaEngineClassFactory;
            IMFMediaEngineClassFactory* fac;
            if ((hr = CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iidF, (void**)&fac)) < 0)
                return Log("CoCreateInstance(MFMediaEngineClassFactory)", hr);
            factory = fac;

            IMFMediaEngine* engine;
            if ((hr = factory->CreateInstance(0, attrs, &engine)) < 0 || engine == null)
                return Log("IMFMediaEngineClassFactory::CreateInstance", hr);
            _engine = engine;
        }
        finally
        {
            if (factory != null) factory->Release();
            if (attrs != null) attrs->Release();
        }

        Guid iidEx = IID.IID_IMFMediaEngineEx;
        IMFMediaEngineEx* ex;
        if ((hr = _engine->QueryInterface(&iidEx, (void**)&ex)) < 0 || ex == null) return Log("QI IMFMediaEngineEx", hr);
        _engineEx = ex;
        if ((hr = _engineEx->EnableWindowlessSwapchainMode(true)) < 0)
            return Log("EnableWindowlessSwapchainMode(TRUE)", hr);

        // Capability probe (once): can this machine's MF play an HLS master playlist at all? An MF install without the
        // HLS byte-stream handler answers NOT_SUPPORTED here, and an actual live URL would then fail as a generic
        // "the media source failed" — the app can show the honest reason instead.
        fixed (char* pHls = HlsMimeType)
        {
            MF_MEDIA_ENGINE_CANPLAY answer;
            _canPlayHls = _engine->CanPlayType(pHls, &answer) >= 0
                          && answer != MF_MEDIA_ENGINE_CANPLAY.MF_MEDIA_ENGINE_CANPLAY_NOT_SUPPORTED;
        }

        // NOTE: no SetSource/Play/SetLoop here any more — bring-up only stands up the device+engine. The first source
        // (and every subsequent one) arrives as a posted SetSource command, which is what lets this ONE engine instance
        // survive many track switches (warm-engine reuse — the E3 fix) instead of being torn down and rebuilt per track.
        return S_OK;
    }

    // ── Commands (engine thread only) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Drain every pending command slot and apply it as the raw COM call it used to be issued from inside an
    /// <c>Invoke</c> closure — same bodies, now run directly on this (already-the-right) thread instead of being
    /// marshaled to it and waited on.
    /// <para>Processes Detach BEFORE SetSource. <c>MediaPlayer.OpenAsync</c> awaits the OLD session's
    /// <c>DisposeAsync</c> (which posts Detach via <c>MfMediaPlayer.ReturnEngine</c>) before opening the NEW one (which
    /// posts SetSource), so the posts always land in that program order — but nothing stops BOTH from still being
    /// pending together at the next drain if the engine thread hasn't woken in between. Draining SetSource first would
    /// let a same-cycle Detach immediately null out the fresh source it just established; Detach first means it only
    /// ever unloads whatever the PREVIOUS source left behind, exactly as intended.</para></summary>
    private void DrainCommands()
    {
        VideoEngineCommandQueue c = _commands;

        // Both are taken up front: a Detach drained together with the NEXT source's SetSource (the warm-engine switch)
        // must not unload the old source first — the new SetSource replaces it in one step, and the extra
        // SetSource(null) only provokes EMPTIED/ABORT events the new source then has to outrun.
        bool detach = c.TryTake(VideoCommandKind.Detach, out _, out _, out _, out _);
        bool setSource = c.TryTake(VideoCommandKind.SetSource, out _, out int epoch, out _, out object? urlObj);

        if (detach)
        {
            // Release-time source unload (the engine returning warm to MfMediaPlayer's pool). Some MF builds fail a
            // null SetSource; tolerated — there is no session left listening to a detached engine. Skipped when a
            // SetSource follows in this very drain (see above); ReturnEngine alone still releases the source.
            if (_engineEx != null && !setSource)
            {
                int hr = _engineEx->SetSource(null);
                if (hr < 0) Log("SetSource(null) [detach]", hr);
            }
            if (_engine != null) _engine->Pause();
            // The source is gone: drop every per-source bit (GetDuration is NaN with no source, which must not re-latch
            // as live) and park the loop. _faulted and _committedSourceEpoch stay, so a late snapshot is still dropped by
            // the session's SourceEpoch guard.
            ResetPerSourceState();
            _hasSource = false;
            _lastRate = 1.0;   // a parked engine carries no rate; the next session posts its own
        }

        if (setSource)
        {
            string url = (string)urlObj!;
            _committedSourceEpoch = epoch;
            // A warm engine reused across a switch must not let the PREVIOUS source's event state (Ended, an old error,
            // a stale live latch) leak into the new one.
            ResetPerSourceState();
            _hasSource = true;

            if (_engineEx != null)
            {
                int hr;
                fixed (char* pUrl = url) hr = _engineEx->SetSource(pUrl);
                if (hr < 0)
                {
                    Log("SetSource", hr);
                    _error = true; _errorCode = MfMediaEngineErrSrcNotSupported; _errorHr = hr;
                    // A removed device fails every source the same way; if that is the cause, fault the engine (and report the
                    // device loss instead of a bad source) so the next lease rebuilds it rather than reusing it.
                    CheckDeviceRemoved("SetSource");
                }
                else
                {
                    // MF can drop a rate set while the topology loads and falls back to the DEFAULT rate on Play, so the
                    // rate the session asked for is re-asserted against the new source (the session also re-posts it).
                    ApplyRate(_lastRate);
                    RebaseFrameStats();
                }
            }
            else
            {
                // No engine (Faulted bring-up) — there is nothing to set the source on; report it the same way an MF
                // SetSource failure would, so a session sees Failed instead of hanging in Opening forever.
                _error = true; _errorCode = MfMediaEngineErrSrcNotSupported; _errorHr = unchecked((int)0x80004005);
            }
        }

        if (c.TryTake(VideoCommandKind.Transport, out double play, out _, out _, out _))
        {
            if (_engine != null) { if (play != 0) _engine->Play(); else _engine->Pause(); }
        }

        if (c.TryTake(VideoCommandKind.Seek, out double seconds, out int approxFlag, out _, out _))
        {
            double t = seconds < 0 ? 0 : seconds;
            bool tookApprox = false;
            if (approxFlag != 0 && _engineEx != null)
            {
                int hr = _engineEx->SetCurrentTimeEx(t, MF_MEDIA_ENGINE_SEEK_MODE.MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE);
                if (hr < 0) Log("SetCurrentTimeEx(APPROXIMATE)", hr);
                else tookApprox = true;
            }
            if (!tookApprox && _engine != null) _engine->SetCurrentTime(t);
            if (Diag.Enabled)
                Diag.Event("media.seek", $"engine seconds={t:0.000} requestedApprox={approxFlag != 0} engineExAvailable={_engineEx != null} tookApprox={tookApprox}");
        }

        if (c.TryTake(VideoCommandKind.Rate, out double rate, out _, out _, out _))
        {
            _lastRate = rate;
            ApplyRate(rate);
        }

        if (c.TryTake(VideoCommandKind.Volume, out double volume, out _, out _, out _))
        {
            if (_engine != null) _engine->SetVolume(volume < 0 ? 0 : (volume > 1 ? 1 : volume));
        }

        if (c.TryTake(VideoCommandKind.Muted, out _, out int muted, out _, out _))
        {
            if (_engine != null) _engine->SetMuted(muted != 0);
        }

        if (c.TryTake(VideoCommandKind.Loop, out _, out int loop, out _, out _))
        {
            if (_engine != null) _engine->SetLoop(loop != 0);
        }

        if (c.TryTake(VideoCommandKind.StreamRect, out _, out int w, out int h, out _))
        {
            if (_engineEx != null)
            {
                RECT dst = new() { left = 0, top = 0, right = w, bottom = h };
                MFARGB border = new() { rgbBlue = 0, rgbGreen = 0, rgbRed = 0, rgbAlpha = 255 };
                int hr = _engineEx->UpdateVideoStream(null, &dst, &border);
                if (hr < 0) Log("UpdateVideoStream(dst)", hr);
                else if (w > 0 && h > 0)
                {
                    _appliedStreamW = (uint)w; _appliedStreamH = (uint)h;
                    _frameWatch.Restart(Environment.TickCount64);   // a re-sized (re-created) stream gets a fresh no-frame window
                }
            }
        }

        if (c.TryTake(VideoCommandKind.Repaint, out _, out _, out _, out _))
        {
            if (_engineEx != null) _engineEx->UpdateVideoStream(null, null, null);
        }
    }

    // Both rates: the DEFAULT rate is what the engine reverts to when Play() runs after a pause or seek (Chromium sets
    // both for the same reason), so SetPlaybackRate alone silently falls back to 1.0.
    private void ApplyRate(double rate)
    {
        if (_engine == null) return;
        int hr = _engine->SetDefaultPlaybackRate(rate);
        if (hr < 0) Log("SetDefaultPlaybackRate", hr);
        hr = _engine->SetPlaybackRate(rate);
        if (hr < 0) Log("SetPlaybackRate", hr);
    }

    // Reset every bit that describes the CURRENT source — shared by Detach (the source is unloaded) and SetSource (a new
    // one replaces it). Deliberately leaves _faulted (sticky bring-up failure) and _committedSourceEpoch (the caller
    // decides what epoch is published) alone.
    private void ResetPerSourceState()
    {
        _metadataLoaded = false; _canPlay = false; _playing = false; _seeking = false; _ended = false;
        _error = false; _errorCode = 0; _errorHr = 0;
        _waiting = false; _stalledHint = false;
        _liveLatched = false;
        _nanAfterMetadataLogged = false;
        _metadataTrusted = false;
        Interlocked.Exchange(ref _firstFrameTicks, 0);
        Interlocked.Exchange(ref _seekedCount, 0);
        _naturalW = 0; _naturalH = 0; _naturalQueriedForEpoch = -1;
        _appliedStreamW = 0; _appliedStreamH = 0;
        _cachedHandle = 0; _handleQueriedForEpoch = -1;
        _handles.Retire(Environment.TickCount64);   // F198: the old source's handle is superseded; it is closed a replacement or a grace later
        _frames.Reset(); _frameWatch.Reset(); _nextStatsPollMs = 0; _frameStatsReadable = false;
        _cachedDuration = 0;
        // The source changed, so whatever swap-chain handle the old one produced is invalid, exactly like a native
        // FORMATCHANGE/RESOURCELOST — bump the same epoch so a consumer re-queries it.
        Interlocked.Increment(ref _presentationEpoch);
    }

    // ── Snapshot (engine thread only) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Direct COM reads — this IS the engine thread, so nothing here waits for anything. Builds the POD
    /// snapshot, publishes it unconditionally, and raises <see cref="StateChanged"/> only when something significant
    /// changed since the last raise (position-only changes are coalesced to ~1 Hz — see the class doc comment).</summary>
    private void RefreshAndPublishSnapshot()
    {
        if (Interlocked.Exchange(ref _deviceProbePending, 0) != 0) CheckDeviceRemoved("engine event");
        _handles.Sweep(Environment.TickCount64);
        uint readyState = _engine != null ? (uint)_engine->GetReadyState() : 0u;
        // LOADEDMETADATA is trusted only once the engine itself reports HAVE_METADATA (see _metadataTrusted).
        bool metadata = _metadataLoaded && readyState >= EngineLivenessRule.HaveMetadata;
        if (metadata && !_metadataTrusted)
        {
            // Crossing into HAVE_METADATA: a size or handle cached before it described whatever the engine held then.
            _naturalQueriedForEpoch = -1;
            _handleQueriedForEpoch = -1;
        }
        _metadataTrusted = metadata;

        VideoEngineFlags flags = VideoEngineFlags.None;
        if (_faulted) flags |= VideoEngineFlags.Faulted;
        if (metadata) flags |= VideoEngineFlags.MetadataLoaded;
        if (_canPlay) flags |= VideoEngineFlags.CanPlay;
        if (_playing && metadata) flags |= VideoEngineFlags.Playing;   // PLAYING / ENDED mean nothing before the source's own metadata
        if (_seeking) flags |= VideoEngineFlags.Seeking;
        if (_ended && metadata) flags |= VideoEngineFlags.Ended;
        if (_error) flags |= VideoEngineFlags.Error;

        double duration = 0, position = 0;
        double seekStart = 0, seekEnd = 0;
        // Read ONCE: the size and handle below are queried for this epoch, so the snapshot must publish this epoch with them. A
        // FORMATCHANGE landing mid-refresh would otherwise pair the NEW epoch with the OLD handle, which the consumer keeps and the
        // handle ledger (F198) closes after its delay.
        int epoch = Volatile.Read(ref _presentationEpoch);

        if (_engine != null)
        {
            double d = _engine->GetDuration();
            if (double.IsFinite(d) && d > 0 && _cachedDuration <= 0) _cachedDuration = d;   // never regress a known duration back to 0
            duration = _cachedDuration;

            double t = _engine->GetCurrentTime();
            position = double.IsFinite(t) && t > 0 ? t : 0.0;

            if (_engineEx != null)
            {
                if (metadata)
                {
                    // Liveness is judged only now: before metadata (and with no source) GetDuration is NaN, which means
                    // "not known yet". One-way latch per source.
                    if (!_liveLatched)
                    {
                        uint characteristics;
                        bool charIsLive = _engineEx->GetResourceCharacteristics(&characteristics) >= 0 && (characteristics & MediaSourceIsLive) != 0;
                        if (EngineLivenessRule.IsLive(_metadataLoaded, readyState, d, charIsLive)) _liveLatched = true;
                        else if (double.IsNaN(d) && readyState >= EngineLivenessRule.HaveMetadata && !_nanAfterMetadataLogged)
                        {
                            // Policy: NaN after metadata is NOT live. Report it once per source so a live HLS build that
                            // answers this way (and would now show as VOD) shows up in the field instead of silently.
                            _nanAfterMetadataLogged = true;
                            Diag.Event("media.live", "nan-after-metadata");
                        }
                    }

                    // Seekable range (the DVR window for a live source; its END is the live edge). Re-read every tick —
                    // for a live source it slides forward continuously. The IMFMediaTimeRange this call creates never
                    // leaves this thread and is released before returning.
                    IMFMediaTimeRange* range = null;
                    if (_engineEx->GetSeekable(&range) >= 0 && range != null)
                    {
                        try
                        {
                            uint count = range->GetLength();
                            if (count > 0)
                            {
                                double s, e;
                                if (range->GetStart(count - 1, &s) >= 0 && range->GetEnd(count - 1, &e) >= 0)
                                {
                                    if (!double.IsFinite(s) || s < 0) s = 0;
                                    if (double.IsFinite(e) && e >= s) { seekStart = s; seekEnd = e; }
                                }
                            }
                        }
                        finally { range->Release(); }
                    }

                    // Native decoded video size — re-query once per presentation epoch (0×0 IS a valid answer:
                    // audio-only). An ABR variant switch (FORMATCHANGE) changes the decoded frame size with no
                    // transport transition to ride in on, so this is what keeps the composited fit correct after one.
                    if (_naturalQueriedForEpoch != epoch)
                    {
                        uint cx, cy;
                        if (_engineEx->GetNativeVideoSize(&cx, &cy) >= 0)
                        {
                            _naturalQueriedForEpoch = epoch;
                            _naturalW = cx; _naturalH = cy;
                        }
                    }

                    // Swap-chain handle — same one-query-per-epoch discipline (a steady-state playing video costs zero
                    // extra marshaled reads here once both are answered for the current epoch). A video whose handle
                    // came back 0 (the swap chain is not ready yet at this refresh) is retried on the next one instead
                    // of freezing at 0 for the epoch, like the native path's "not ready yet"; an audio-only source
                    // (empty size) has no handle to wait for.
                    if (_handleQueriedForEpoch != epoch || (_cachedHandle == 0 && _naturalW != 0 && _naturalH != 0))
                    {
                        HANDLE h = default;
                        nuint queried = _engineEx->GetVideoSwapchainHandle(&h) >= 0 && (nint)h != -1 ? (nuint)(nint)h : 0;
                        // F198: a successful query hands over a handle this engine now owns; the one it supersedes is retired.
                        _handles.Adopt(queried, Environment.TickCount64);
                        _cachedHandle = queried;
                        _handleQueriedForEpoch = epoch;
                    }
                }
            }
        }

        // A STALLED hint counts only when the engine's own ready state agrees playback cannot continue.
        if (_stalledHint)
        {
            _stalledHint = false;
            if (readyState < HaveFutureData && CanStall) { Interlocked.Increment(ref _waitingSerial); _waiting = true; }
        }
        // A stall ends on the PLAYING / CANPLAY events (they clear the bit), or here, when the playhead has moved on from
        // where this stall began: the progress check covers MF builds that resume without either event.
        if (_waiting)
        {
            int serial = Volatile.Read(ref _waitingSerial);
            if (serial != _waitAnchorSerial) { _waitAnchorSerial = serial; _waitAnchorPos = position; }
            else if (position > _waitAnchorPos + WaitProgressSeconds) _waiting = false;
        }
        if (_waiting && _playing && metadata) flags |= VideoEngineFlags.Waiting;

        if (_naturalQueriedForEpoch == epoch) flags |= VideoEngineFlags.NaturalSizeKnown;
        if (_liveLatched) flags |= VideoEngineFlags.LiveSource;
        long firstFrame = metadata ? Volatile.Read(ref _firstFrameTicks) : 0;

        // F066: rendered/dropped-frame health. Polled only while VIDEO is actually playing - not paused, parked, starved, seeking or
        // failed, with a frame's worth of data (HAVE_FUTURE_DATA, as the native path gates it), and only once a stream rect was applied (an engine with no element mounted may never create a swap chain to render
        // into, which is no hang). The same turn judges Chromium's rendered-frame detection: no frame rendered within
        // RenderedFrameWatch.DefaultTimeoutMs of PLAYING or of the last UpdateVideoStream is a hung surface (a dead swap-chain handle, a
        // stuck topology, a lost device that never raised an error), reported as a typed Decode error and a Faulted engine so the retry
        // gets a fresh one.
        long nowMs = Environment.TickCount64;
        bool videoPlaying = (flags & (VideoEngineFlags.Playing | VideoEngineFlags.Waiting | VideoEngineFlags.Seeking | VideoEngineFlags.Error | VideoEngineFlags.Ended)) == VideoEngineFlags.Playing
                            && readyState >= HaveFutureData
                            && (flags & VideoEngineFlags.NaturalSizeKnown) != 0 && _naturalW != 0 && _naturalH != 0
                            && _appliedStreamW != 0 && _appliedStreamH != 0;
        if (videoPlaying && nowMs >= _nextStatsPollMs)
        {
            _nextStatsPollMs = nowMs + FrameStatsPollMs;
            PollFrameStats();
        }
        // A statistics read that fails or comes back empty is telemetry trouble, never a hang (Chromium skips its rendered-frame check
        // unless PopulateStatistics succeeded): the watch only counts time while the counters are actually readable.
        if (_frameWatch.Observe(nowMs, videoPlaying && _frameStatsReadable, _frames.Rendered))
        {
            _error = true; _errorCode = MfMediaEngineErrDecode; _errorHr = HrNoRenderedFrame;
            _faulted = true;
            flags |= VideoEngineFlags.Error | VideoEngineFlags.Faulted;
            Diag.Line($"[video.render] no frame rendered within {RenderedFrameWatch.DefaultTimeoutMs / 1000}s of playing (stream {_appliedStreamW}x{_appliedStreamH}, dropped={_frames.Dropped}); engine faulted, the retry rebuilds it");
        }

        var snap = new VideoEngineSnapshot
        {
            SourceEpoch = _committedSourceEpoch,
            PresentationEpoch = epoch,
            Flags = flags,
            ReadyState = readyState,
            NaturalW = _naturalW,
            NaturalH = _naturalH,
            DurationSeconds = duration,
            PositionSeconds = position,
            PositionTimestamp = Stopwatch.GetTimestamp(),
            PlaybackRate = ReadPlaybackRate(),
            SeekableStart = seekStart,
            SeekableEnd = seekEnd,
            SwapchainHandle = _cachedHandle,
            ErrorCode = _errorCode,
            ErrorHr = _errorHr,
            FirstFrameTimestamp = firstFrame,
            SeekedCount = Volatile.Read(ref _seekedCount),
            StreamW = _appliedStreamW,
            StreamH = _appliedStreamH,
            FramesRendered = _frames.Rendered,
            FramesDropped = _frames.Dropped,
        };
        _snapshot.Publish(snap);

        bool significant =
            snap.SourceEpoch != _lastPublished.SourceEpoch ||
            snap.PresentationEpoch != _lastPublished.PresentationEpoch ||
            snap.Flags != _lastPublished.Flags ||
            snap.ReadyState != _lastPublished.ReadyState ||
            snap.NaturalW != _lastPublished.NaturalW || snap.NaturalH != _lastPublished.NaturalH ||
            snap.DurationSeconds != _lastPublished.DurationSeconds ||
            snap.SeekableStart != _lastPublished.SeekableStart || snap.SeekableEnd != _lastPublished.SeekableEnd ||
            snap.PlaybackRate != _lastPublished.PlaybackRate ||
            snap.SwapchainHandle != _lastPublished.SwapchainHandle ||
            snap.FirstFrameTimestamp != _lastPublished.FirstFrameTimestamp ||
            snap.SeekedCount != _lastPublished.SeekedCount ||
            snap.StreamW != _lastPublished.StreamW || snap.StreamH != _lastPublished.StreamH ||
            snap.ErrorCode != _lastPublished.ErrorCode;

        long now = snap.PositionTimestamp;
        bool positionDue = now - _lastRaiseTicks >= s_positionRaiseTicks;
        if (!significant && !positionDue) return;

        _lastPublished = snap;
        _lastRaiseTicks = now;
        try { StateChanged?.Invoke(); } catch { }
    }

    // The rate the engine is ACTUALLY running at (a rate MF dropped or reverted shows here); the last applied command only
    // while there is no engine or it answers something unusable.
    private double ReadPlaybackRate()
    {
        if (_engine == null) return _lastRate;
        double r = _engine->GetPlaybackRate();
        return double.IsFinite(r) && r > 0 ? r : _lastRate;
    }

    // Engine thread only. One FRAMES_RENDERED / FRAMES_DROPPED reading (a VT_UI4 PROPVARIANT each, as Chromium and Firefox read them).
    private bool TryReadFrameStats(out uint rendered, out uint dropped)
    {
        rendered = 0; dropped = 0;
        if (_engineEx == null) return false;
        return TryReadStat(MF_MEDIA_ENGINE_STATISTIC.MF_MEDIA_ENGINE_STATISTIC_FRAMES_RENDERED, out rendered)
               && TryReadStat(MF_MEDIA_ENGINE_STATISTIC.MF_MEDIA_ENGINE_STATISTIC_FRAMES_DROPPED, out dropped);
    }

    private bool TryReadStat(MF_MEDIA_ENGINE_STATISTIC stat, out uint value)
    {
        value = 0;
        PROPVARIANT pv = default;
        try
        {
            if (_engineEx->GetStatistics(stat, &pv) < 0) return false;
            if ((int)pv.vt != 19) return false;   // VT_UI4: an S_OK that left the PROPVARIANT empty is a failed read, not zero frames
            value = pv.ulVal;
            return true;
        }
        finally { PropVariantClear(&pv); }
    }

    private void PollFrameStats()
    {
        _frameStatsReadable = TryReadFrameStats(out uint rendered, out uint dropped);
        if (_frameStatsReadable) _frames.Observe(rendered, dropped);
    }

    // A new source starts counting from whatever the (warm) engine's counters read now: MF resets them itself once the new source
    // flushes in, which Observe folds in as a decrease.
    private void RebaseFrameStats()
    {
        if (TryReadFrameStats(out uint rendered, out uint dropped)) _frames.Rebase(rendered, dropped);
    }

    /// <inheritdoc/>
    public int PostSetSource(string url)
    {
        int epoch = Interlocked.Increment(ref _nextSourceEpoch);
        _commands.Post(VideoCommandKind.SetSource, i: epoch, obj: url);
        return epoch;
    }

    /// <inheritdoc/>
    public void PostDetach() => _commands.Post(VideoCommandKind.Detach);

    // ── Notify sink (MF worker threads) ────────────────────────────────────────────────────────────────────────────
    internal void OnEngineEvent(uint ev, nuint p1, uint p2)
    {
        bool relevant = true;
        switch ((MF_MEDIA_ENGINE_EVENT)ev)
        {
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA: _metadataLoaded = true; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_CANPLAY: _canPlay = true; _waiting = false; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_PLAY: _ended = false; break;
            // Events carry no source tag, so a late PLAYING / ENDED of the PREVIOUS source can land right after a reset: a
            // new source can legitimately raise neither before its own LOADEDMETADATA, and they are dropped until then.
            // Known residue: the gate reads the RAW _metadataLoaded bit, so a late LOADEDMETADATA followed by a late PLAYING
            // of the previous source can still leave the raw _playing / first-frame stamp set. They are published only once
            // the refresh trusts metadata (ready state >= HAVE_METADATA — the new source's own), so it surfaces as, at
            // worst, a premature Playing flag at that moment; MF's next PAUSE / the session's Transport post corrects it.
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_PLAYING:
                if (!_metadataLoaded) break;
                _playing = true; _canPlay = true; _ended = false; _waiting = false;
                // Frames are flowing: the LAST first-frame fallback (after FIRSTFRAMEREADY and LOADEDDATA; first writer wins).
                Interlocked.CompareExchange(ref _firstFrameTicks, Stopwatch.GetTimestamp(), 0);
                break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_FIRSTFRAMEREADY:
                if (_metadataLoaded) Interlocked.CompareExchange(ref _firstFrameTicks, Stopwatch.GetTimestamp(), 0);
                break;
            // HAVE_CURRENT_DATA: the frame at the current position is decoded. The engine never calls SetPreload, so a PAUSED
            // open may get no FIRSTFRAMEREADY and never raises PLAYING; this is the fallback that still lets the poster drop
            // (Chromium sets up its frame here; Gecko's OnLoadedData calls OnLoadedFirstFrame for the same reason).
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_LOADEDDATA:
                if (_metadataLoaded) Interlocked.CompareExchange(ref _firstFrameTicks, Stopwatch.GetTimestamp(), 0);
                break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_PAUSE: _playing = false; _waiting = false; break;
            // Starvation: the engine ran out of data to present. Only after this source first had enough (CANPLAY / PLAYING) —
            // a WAITING while the source is still loading or seeking is the normal opening / seek path, which the session
            // already shows as Opening / Buffering(Seeking). The bit is set here and cleared by PLAYING / CANPLAY / PAUSE /
            // ENDED, or by the engine thread seeing the playhead move (RefreshAndPublishSnapshot).
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_WAITING:
                if (CanStall) { Interlocked.Increment(ref _waitingSerial); _waiting = true; }
                else relevant = false;
                break;
            // A download stall is NOT necessarily a playback stall (the buffer may still hold seconds of data): only a hint,
            // judged by the engine thread against the ready state.
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_STALLED:
                if (CanStall) _stalledHint = true;
                else relevant = false;
                break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_SEEKING: _seeking = true; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_SEEKED: _seeking = false; Interlocked.Increment(ref _seekedCount); break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_ENDED: if (_metadataLoaded) { _ended = true; _playing = false; _waiting = false; } break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_ERROR:
                _error = true; _errorCode = (uint)p1; _errorHr = (int)p2;
                Volatile.Write(ref _deviceProbePending, 1);   // an error after a TDR looks like any other; the engine thread asks the device
                break;
            // The PRESENTATION itself changed underneath the engine, with no transport transition to ride in on:
            // FORMATCHANGE is an ABR variant switch (a NEW decoded frame size), RESOURCELOST is the swap chain going
            // away and being rebuilt. Both bump the monotonic epoch RefreshAndPublishSnapshot compares against to
            // decide whether to re-query the natural size / swap-chain handle.
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_FORMATCHANGE:
                Interlocked.Increment(ref _presentationEpoch);
                break;
            // RESOURCELOST can also be the device itself going away: the engine thread checks it (CheckDeviceRemoved).
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_RESOURCELOST:
                Interlocked.Increment(ref _presentationEpoch);
                Volatile.Write(ref _deviceProbePending, 1);
                break;
            default: relevant = false; break;
        }
        // EventNotify runs on an MF worker. Set the bits, then ask for exactly one coalesced out-of-cadence refresh —
        // through the SAME gate a posted command uses (VideoEngineCommandQueue.RequestWake), so a burst of native events
        // between two engine-thread turns produces at most one extra wake, not one per event. Never raise StateChanged
        // here: only RefreshAndPublishSnapshot does, AFTER the corresponding Publish, so a consumer woken by the event is
        // guaranteed to read at least the state that caused it.
        if (relevant) _commands.RequestWake();
    }

    // A stall is only meaningful for a source that has already had enough data to play (see the WAITING case).
    private bool CanStall => (_canPlay || _playing) && !_ended;

    // Engine thread only. Asks the D3D11 device MF decodes on whether it was removed (TDR, driver update, adapter change —
    // MF then errors or keeps handing out a swap chain that never presents, on every later source). If so the engine is
    // marked Faulted, with an error snapshot, so MfMediaPlayer.LeaseEngine rebuilds it at the next open instead of reusing a
    // dead one; the session that is playing on it sees a Retryable decode error. Always-on, once per engine.
    private bool CheckDeviceRemoved(string what)
    {
        if (_deviceRemoved) return true;
        if (_d3d == null) return false;
        int reason = (int)_d3d->GetDeviceRemovedReason();
        if (reason == 0) return false;
        _deviceRemoved = true;
        _faulted = true;
        // Retire the shared device so the next lease creates a fresh one instead of handing out the dead one again.
        _videoLease?.MarkRemoved();
        _error = true; _errorCode = MfMediaEngineErrDecode; _errorHr = reason;
        Diag.Line($"[video.d3d11] device removed ({what}) reason=0x{(uint)reason:X8}; engine faulted, the next lease rebuilds it");
        return true;
    }

    private static int Log(string what, int hr)
    {
        Console.Error.WriteLine($"VideoMediaEngine: {what} hr=0x{(uint)hr:X8}");
        return hr;
    }

    // Runs on the engine thread — never touch the COM ptrs off it. Idempotent: every field is nulled/cleared after
    // release, so a second call (Faulted bring-up releases partial state, then the loop's own exit calls this again) is
    // a safe no-op.
    private void DisposeCom()
    {
        // Stop delivering to us BEFORE the engine goes away (Chromium's order): an EventNotify already on an MF worker
        // returns without touching this instance.
        if (_notify != null) MediaEngineNotifyCcw.Shutdown(_notify);
        if (_engine != null) _engine->Shutdown();
        if (_engineEx != null) { _engineEx->Release(); _engineEx = null; }
        if (_engine != null) { _engine->Release(); _engine = null; }
        // F198: the swap-chain handles this engine queried are its to close (current and retired); the engine that produced them is gone.
        _handles.CloseAll();
        // Borrowed from the shared device (MfVideoDevice): drop the pointers only, the lease below gives them back.
        _dxgiManager = null;
        _d3d = null;
        // Drop only OUR reference. MF may still hold the callback (its final release can land on one of its workers after
        // Shutdown returns), so the struct and the GCHandle are freed by whoever releases last, never here.
        if (_notify != null) { MediaEngineNotifyCcw.ReleaseRef(_notify); _notify = null; }
        // Last: the device (and MFStartup) may only go away once this engine's COM objects are released. The device itself
        // lingers for the next engine; it is destroyed here only when this was its last lease and it was retired.
        if (_videoLease != null) { _videoLease.Dispose(); _videoLease = null; }
        int census = Interlocked.Exchange(ref _censusToken, 0);
        if (census != 0)
        {
            MediaCensus.Unregister(census);
            Diag.Line($"[video] engine.destroy id={_ordinal} live={MediaCensus.Count(MediaCensusKind.VideoEngine)}");
        }
    }

    // What the media census reads for this engine, from any thread: the published snapshot's natural size and whether it has a swap
    // chain handle out (attached to a source that produced one).
    private MediaCensusRow DescribeCensus()
    {
        VideoEngineSnapshot s = _snapshot.Read();
        return new MediaCensusRow(MediaCensusKind.VideoEngine, _ordinal, (int)s.NaturalW, (int)s.NaturalH, 0, s.SwapchainHandle != 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _work.CompleteAdding();     // the loop drains, then DisposeCom() on the engine thread, then exits
        _thread?.Join(2000);
    }
}

/// <summary>
/// Hand-rolled CCW for <c>IMFMediaEngineNotify</c> (single <c>EventNotify</c> method). Carries a <c>GCHandle</c> to its
/// owning <see cref="VideoMediaEngine"/> so the <c>[UnmanagedCallersOnly]</c> thunk (which cannot close over instance
/// state) routes events back to the instance. Mirrors the vtable pattern in
/// <c>src/FluentGpu.Windows/Interop/Win32DropTarget.cs</c>.
/// <para><b>Lifetime (com-interop.md §4.3).</b> A real COM refcount: the engine holds one reference (<see cref="Create"/>
/// starts at 1, dropped in <c>DisposeCom</c> through <see cref="ReleaseRef"/>), Media Foundation holds its own, and the
/// native struct and the <see cref="GCHandle"/> are freed by WHOEVER RELEASES LAST (<see cref="ReleaseRef"/> reaching 0).
/// MF's final release can land on one of its workers after <c>Shutdown</c> returned, so freeing earlier would make that
/// release (or an in-flight <c>EventNotify</c>) touch freed memory. <see cref="Shutdown"/> is the gate that stops
/// callbacks reaching a disposing engine; it never frees anything.</para>
/// </summary>
internal unsafe struct MediaEngineNotifyCcw
{
    public void** Vtbl;    // COM "this" vptr (first field)
    public int Rc;
    public nint Owner;     // GCHandle.ToIntPtr(owner); freed with the struct, when Rc reaches 0
    public int IsShutdown; // set once by Shutdown(): EventNotify then returns S_OK without calling back into the owner

    // Structs created and not yet freed — what the lifetime tests assert (a leak or an early free shows as a wrong count).
    private static int s_live;
    internal static int LiveInstances => Volatile.Read(ref s_live);

    private static readonly void** _vtbl = Build();
    private static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, uint, nuint, uint, int>)&EventNotify;
        return v;
    }

    public static MediaEngineNotifyCcw* Create(nint owner)
    {
        var p = (MediaEngineNotifyCcw*)NativeMemory.Alloc((nuint)sizeof(MediaEngineNotifyCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->Owner = owner; p->IsShutdown = 0;
        Interlocked.Increment(ref s_live);
        return p;
    }

    /// <summary>Stop forwarding events to the owner (call BEFORE the engine is shut down). Frees nothing.</summary>
    internal static void Shutdown(MediaEngineNotifyCcw* p) => Volatile.Write(ref p->IsShutdown, 1);

    internal static uint AddRefCore(MediaEngineNotifyCcw* p) => (uint)Interlocked.Increment(ref p->Rc);

    /// <summary>Drop one reference; the one that reaches zero frees the owner's <see cref="GCHandle"/> and the struct.
    /// Callable from managed code (the owner dropping its own reference) and from the COM <c>Release</c> thunk.</summary>
    internal static uint ReleaseRef(MediaEngineNotifyCcw* p)
    {
        int rc = Interlocked.Decrement(ref p->Rc);
        if (rc == 0)
        {
            if (p->Owner != 0) GCHandle.FromIntPtr(p->Owner).Free();
            NativeMemory.Free(p);
            Interlocked.Decrement(ref s_live);
        }
        return (uint)rc;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int QueryInterface(MediaEngineNotifyCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return unchecked((int)0x80004003);
        Guid iunk = IID.IID_IUnknown, icb = IID.IID_IMFMediaEngineNotify;
        if (*riid == iunk || *riid == icb) { Interlocked.Increment(ref self->Rc); *ppv = self; return 0; }
        *ppv = null; return unchecked((int)0x80004002);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint AddRef(MediaEngineNotifyCcw* self) => AddRefCore(self);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint Release(MediaEngineNotifyCcw* self) => ReleaseRef(self);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int EventNotify(MediaEngineNotifyCcw* self, uint ev, nuint p1, uint p2)
    {
        if (Volatile.Read(ref self->IsShutdown) != 0) return 0;   // the owner is disposing: nothing left to tell it
        try
        {
            var h = GCHandle.FromIntPtr(self->Owner);
            if (h.Target is VideoMediaEngine owner) owner.OnEngineEvent(ev, p1, p2);
        }
        catch { /* never throw across the COM boundary */ }
        return 0;
    }
}
