using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// ONE protected source on the process <see cref="ProtectedVideoRuntime"/> — the managed half of <c>FgPrSession</c>.
/// It owns a native session handle (a <c>CencMediaSource</c> over a byte-capped, time-windowed segment store, opened AT
/// the request's start position), has no thread of its own, and implements <see cref="IProtectedVideoPlayer"/> for
/// <see cref="ProtectedMediaSession"/>. Every native call goes through the runtime's <see cref="IPrSessionNative"/>
/// seam (the DLL in production, a recording fake in the engine's tests).
/// <para><b>The switch.</b> <see cref="Start"/> is one <c>FgPrSessionAttach</c> — a <c>SetSource</c> on an engine that
/// is already alive, with a license that is (usually) already usable and an init segment plus the first segments at
/// the start position (usually) already in memory. Metadata, CANPLAY and FIRSTFRAMEREADY arrive as events; each asks
/// for ONE coalesced UI pump; <see cref="Pump"/> reads ONE native snapshot.</para>
/// <para><b>Seeking.</b> Flush, not recreate: the native side repositions the source on its runtime thread
/// immediately (or registers the target as the feeder's next fetch and repositions from the fetch's completion) — no
/// tick, no ack poll, no suppression window. <see cref="IsSeeking"/> is a JOINING state; the landed position arrives
/// with the Seeked event.</para>
/// <para><b>Allocation.</b> Nothing per pump: the snapshot is a POD read into a local and published into the same
/// <see cref="VideoSnapshotBuffer"/> shape the clear path uses; the index reads copy into the caller's spans. Lifecycle
/// log lines (attach, first frame, seek landed, error) allocate their text — a handful per source.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class ProtectedVideoSession : IProtectedVideoPlayer
{
    /// <summary>Retention behind the playhead: a backward seek inside 30 s never touches the network.</summary>
    public const long DefaultRetainBehindMs = 30_000;
    /// <summary>Forward buffering target.</summary>
    public const long DefaultBufferAheadMs = 60_000;
    /// <summary>The per-session segment-store cap (≤ 2 live sessions ⇒ ≤ 64 MiB of counted media bytes).</summary>
    public const long DefaultStoreBudgetBytes = 32L << 20;
    /// <summary>What a prepare fetches for a not-yet-attached session: init + two segments (8 s on a 4 s grid) at the
    /// start position, both streams.</summary>
    public const int DefaultPrefetchSegments = 2;

    private readonly ProtectedVideoRuntime _rt;
    private readonly IPrSessionNative _native;
    private readonly ProtectedVideoRequest _request;
    private readonly ProtectedTrackDescriptor? _videoTrack;
    private readonly string _logKey;
    private ulong _s;
    private long _nativeStartMs;         // the start position the native session currently opens at
    private bool _holdsRuntimeRef;
    private bool _licensePinned;
    private bool _disposed;
    private string? _startupError;
    private volatile string? _kid;      // the license cache key: the descriptor's KID, the PSSH's, or the init segment's

    private readonly Signal<ProtectedVideoState> _state = new(ProtectedVideoState.Idle);
    private readonly Signal<long> _positionMs = new(0);
    private readonly Signal<long> _durationMs = new(0);
    private readonly Signal<Size2> _naturalSize = new(default);
    private readonly Signal<string?> _error = new(null);
    private readonly VideoSnapshotBuffer _snapshots = new();

    // ── fields written from native threads (the event callback), read by the UI pump ──────────────────────────────
    private int _attached;              // 1 between a successful Start and Stop
    private int _attachEpoch;           // bumps per attach — FirstFrame/CanPlay flags are scoped to it
    private int _firstFrameAttachEpoch; // the attach epoch the last FirstFrame belonged to
    private long _firstFrameEpoch;      // bumps once per source (per attach) when FIRSTFRAMEREADY lands
    private int _canPlayAttachEpoch;
    private int _metadataAttachEpoch;
    private int _licenseUsable;         // 1 once the attached KID's license is usable
    private int _licenseFailedHr;
    private int _nativeErrorHr;
    private int _nativeErrorCode;
    private int _indexEpoch;
    private int _seekPending;           // 1 from a managed Seek until the native Seeked event
    private long _seekTargetMs = -1;
    private long _seekLandedMs = -1;
    private long _seekIssuedTimestamp;
    private long _attachTimestamp;
    private long _bytes, _bytesMs;

    // The store's window around the playhead: written by the Buffered event (a prepared session is never pumped, and its
    // prepare decides readiness from this) and refreshed by every pump from the snapshot.
    private long _forwardBufferedMs, _retainedBehindMs;

    // ── fields owned by the UI pump ────────────────────────────────────────────────────────────────────────────────
    private ProtectedVideoPhase _phase = ProtectedVideoPhase.Idle;
    private long _positionQpc;
    private bool _hasSurface;
    private string? _activeRepresentationId;
    private int _streamW = -1, _streamH = -1;

    /// <inheritdoc/>
    public event Action? PumpRequested;

    private ProtectedVideoSession(ProtectedVideoRuntime rt, ProtectedVideoRequest request)
    {
        _rt = rt;
        _native = rt.SessionNative;
        _request = request;
        _nativeStartMs = StartMsOf(request);
        _videoTrack = FindDefaultVideoTrack(request.Catalog);
        _logKey = KeyTail(request.InitUrl);
        _kid = KeyIdFor(request);
    }

    /// <summary>The license-cache key for <paramref name="request"/>: its declared KID, else the one its PSSH names, else
    /// null (a bare-MPD test vector — the init segment answers it after a prefetch).</summary>
    public static string? KeyIdFor(ProtectedVideoRequest request)
        => request.DefaultKid is { Length: > 0 } kid ? kid.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()
                                                    : LicenseKeyId.FromPssh(request.Pssh.Span);

    private static long StartMsOf(ProtectedVideoRequest request) => (long)Math.Max(0, request.StartPosition.TotalMilliseconds);

    /// <summary>The native open descriptor for <paramref name="request"/>: the defaults a request leaves at zero (a
    /// stride of 1, the 30 s / 60 s / 32 MiB window), the normalised KID, the clamped start position. Pure.</summary>
    internal static PrOpenDescription DescribeOpen(ProtectedVideoRequest request, string? kid) => new()
    {
        InitUrl = request.InitUrl,
        SegmentBaseUrl = request.SegmentBaseUrl,
        SegmentPrefix = request.SegmentPrefix,
        SegmentSuffix = request.SegmentSuffix,
        StartNumber = request.StartNumber,
        SegmentCount = Math.Max(0, request.SegmentCount),
        SegmentStrideSeconds = request.SegmentStride > 0 ? request.SegmentStride : 1,
        SegmentLengthMs = Math.Max(0, request.SegmentLengthMs),
        AudioInitUrl = request.AudioInitUrl,
        AudioSegmentBaseUrl = request.AudioSegmentBaseUrl,
        AudioSegmentPrefix = request.AudioSegmentPrefix,
        AudioSegmentSuffix = request.AudioSegmentSuffix,
        Pssh = request.Pssh,
        KeyIdHex = kid,
        HttpHeaders = request.HttpHeaders,
        DurationMs = Math.Max(0, request.DurationMs),
        StartPositionMs = StartMsOf(request),
        StartPaused = request.StartPaused,
        RetainBehindMs = request.RetainBehindMs > 0 ? request.RetainBehindMs : DefaultRetainBehindMs,
        BufferAheadMs = request.BufferAheadMs > 0 ? request.BufferAheadMs : DefaultBufferAheadMs,
        StoreBudgetBytes = request.StoreBudgetBytes > 0 ? request.StoreBudgetBytes : DefaultStoreBudgetBytes,
    };

    /// <summary>
    /// Create a session for <paramref name="request"/> on <paramref name="runtime"/>: takes a runtime reference
    /// (bringing the native runtime up on first use) and creates the native store + source + feeder. No engine call —
    /// whatever is attached keeps playing. A missing component or a failed bring-up yields a session whose first
    /// <see cref="Pump"/> publishes a typed error; it never throws.
    /// </summary>
    public static ProtectedVideoSession Create(ProtectedVideoRuntime runtime, ProtectedVideoRequest request)
    {
        var session = new ProtectedVideoSession(runtime, request);
        if (!runtime.Acquire())
        {
            session._startupError = runtime.StartupError ?? "The protected-video runtime is not available.";
            return session;
        }
        session._holdsRuntimeRef = true;

        int hr;
        ulong handle = 0;
        try { hr = session._native.SessionCreate(runtime.Handle, DescribeOpen(request, session._kid), out handle); }
        catch (Exception e)
        {
            hr = PrNative.EFail;
            session._startupError = "The protected-video session could not be created: " + e.Message;
        }

        if (hr < 0 || handle == 0)
        {
            session._startupError ??= $"The protected-video session could not be created (0x{unchecked((uint)hr):X8}).";
            runtime.Log($"session.create FAILED key={session._logKey} hr=0x{unchecked((uint)hr):X8}");
            return session;
        }
        session._s = handle;
        runtime.RegisterSession(handle, session);
        runtime.Log($"session.create s={handle} key={session._logKey} startMs={session._nativeStartMs} " +
                    $"kid={session._kid ?? "(from init)"} license={runtime.LicenseStateFor(session._kid)}");
        return session;
    }

    /// <summary>The request this session was created for.</summary>
    public ProtectedVideoRequest Request => _request;

    /// <summary>The native session handle (0 when creation failed).</summary>
    internal ulong Handle => _s;

    /// <summary>The last mapped snapshot, in the SAME POD shape the clear path publishes — one seam for both backends.
    /// Alloc-free from any thread.</summary>
    public VideoEngineSnapshot ReadSnapshot() => _snapshots.Read();

    // ── IProtectedVideoPlayer: state ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public IReadSignal<ProtectedVideoState> State => _state;
    /// <inheritdoc/>
    public IReadSignal<long> PositionMs => _positionMs;
    /// <inheritdoc/>
    public IReadSignal<long> DurationMs => _durationMs;
    /// <inheritdoc/>
    public IReadSignal<Size2> NaturalSize => _naturalSize;
    /// <inheritdoc/>
    public IReadSignal<string?> Error => _error;
    /// <inheritdoc/>
    public bool HasSurface => _hasSurface;
    /// <inheritdoc/>
    public ProtectedVideoPhase Phase => _phase;
    /// <inheritdoc/>
    public long FirstFrameEpoch => Volatile.Read(ref _firstFrameEpoch);
    /// <inheritdoc/>
    public long PositionQpc => _positionQpc;
    /// <inheritdoc/>
    public bool IsSeeking => Volatile.Read(ref _seekPending) != 0;
    /// <inheritdoc/>
    public long LastSeekLandedMs => Volatile.Read(ref _seekLandedMs);
    /// <inheritdoc/>
    public long ForwardBufferedMs => Volatile.Read(ref _forwardBufferedMs);
    /// <inheritdoc/>
    public long RetainedBehindMs => Volatile.Read(ref _retainedBehindMs);
    /// <inheritdoc/>
    public int IndexEpoch => Volatile.Read(ref _indexEpoch);
    /// <inheritdoc/>
    public long BytesDownloaded => Volatile.Read(ref _bytes);
    /// <inheritdoc/>
    public long DownloadElapsedMs => Volatile.Read(ref _bytesMs);
    /// <inheritdoc/>
    public string? ActiveVideoRepresentationId => _activeRepresentationId;
    /// <inheritdoc/>
    public bool SupportsAdaptiveSelection => _videoTrack is { Representations.Count: > 1 };

    // ── IProtectedVideoPlayer: verbs ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task PrefetchAsync(int segments, CancellationToken ct) => PrefetchCoreAsync(segments, ct);

    /// <summary>The prefetch, with its outcome: true when media landed, false when it did not (cancelled, the session
    /// failed, or the native call was refused).</summary>
    internal Task<bool> PrefetchCoreAsync(int segments, CancellationToken ct)
    {
        if (_disposed || _s == 0) return Task.FromResult(false);
        int n = segments > 0 ? segments : DefaultPrefetchSegments;
        long t0 = Stopwatch.GetTimestamp();
        // The wait is registered BEFORE the native call: a store that already holds the window answers from the feeder
        // thread at once, and a waiter registered after that answer would wait for a Buffered event that never comes.
        Task<bool> wait = _rt.WaitBufferedAsync(_s, ct);
        int hr = _native.SessionPrefetch(_rt.Handle, _s, _nativeStartMs, n);
        if (hr < 0)
        {
            _rt.Log($"prefetch.fail s={_s} key={_logKey} hr=0x{unchecked((uint)hr):X8}");
            _rt.CompleteBufferedWait(_s, buffered: false);
        }
        return wait.ContinueWith(static (t, st) =>
        {
            var (self, started, segs) = ((ProtectedVideoSession, long, int))st!;
            bool landed = t.Result;
            self.LogVideo((landed ? "prefetch.ok" : "prefetch.none") +
                          $" key={self._logKey} segs={segs} bytes={Volatile.Read(ref self._bytes)} " +
                          $"ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} at={self._nativeStartMs}ms");
            return landed;
        }, (this, t0, n), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <inheritdoc/>
    public void Start(ProtectedVideoRequest request)
    {
        if (_disposed) return;
        if (_s == 0) { RequestPump(); return; }

        if (_kid is null || (_request.Pssh.IsEmpty && _rt.LicenseHandleFor(_kid) == 0))
        {
            // No KID/PSSH in the descriptor (a bare-MPD test vector): the init segment names them. Fetch the init + one
            // segment, read the protection it carried, start the license, THEN attach. Cold path only — every
            // manifest-described source (Spotify's included) knows its key before this line.
            LogVideo($"attach.deferred key={_logKey} reason={(_kid is null ? "no-kid" : "no-pssh")}");
            _ = PrefetchCoreAsync(1, CancellationToken.None).ContinueWith(static (t, st) =>
            {
                var (self, req) = ((ProtectedVideoSession, ProtectedVideoRequest))st!;
                self.CompleteDeferredStart(req);
            }, (this, request), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            RequestPump();
            return;
        }
        Attach(request);
    }

    private void CompleteDeferredStart(ProtectedVideoRequest request)
    {
        if (_disposed || _s == 0) return;
        if (Volatile.Read(ref _nativeErrorHr) != 0)
        {
            // The init never arrived (the session reported an error): there is nothing to license or attach — the pump
            // publishes the error that ended it.
            LogVideo($"attach.deferred.abandoned key={_logKey} hr=0x{unchecked((uint)Volatile.Read(ref _nativeErrorHr)):X8}");
            RequestPump();
            return;
        }
        Span<byte> pssh = stackalloc byte[4096];
        Span<char> kidBuf = stackalloc char[40];
        kidBuf.Clear();
        int len = _native.SessionGetInitProtection(_rt.Handle, _s, pssh, kidBuf);
        int nul = kidBuf.IndexOf('\0');
        string kid = len > 0 ? new string(nul >= 0 ? kidBuf[..nul] : kidBuf) : string.Empty;
        if (len <= 0 || len > pssh.Length || kid.Length != 32)
        {
            // Nothing to license by (a clear source, or an init without tenc): attach without a license handle and let the
            // engine's key-needed path speak for itself.
            LogVideo($"attach.deferred.nokey key={_logKey} psshLen={len}");
            Attach(request);
            return;
        }
        _kid = kid;
        _rt.EnsureLicense(pssh[..len], kid, _request.LicenseRelay, _request.Drm?.System ?? DrmSystem.PlayReady);
        Attach(request);
    }

    private void Attach(ProtectedVideoRequest request)
    {
        string? kid = _kid;
        // The backend normally started the license at manifest/prepare time; a caller that did not (a direct session
        // user) gets it started here, still without waiting for it.
        if (kid is not null && _rt.LicenseHandleFor(kid) == 0 && !_request.Pssh.IsEmpty)
            _rt.EnsureLicense(_request.Pssh.Span, kid, _request.LicenseRelay, _request.Drm?.System ?? DrmSystem.PlayReady);
        ulong lic = _rt.LicenseHandleFor(kid);
        if (!_licensePinned && lic != 0) { _rt.PinLicense(kid, pinned: true); _licensePinned = true; }
        Volatile.Write(ref _licenseUsable, _rt.LicenseStateFor(kid) == LicenseCacheState.Usable ? 1 : 0);

        // The open may carry a newer start position than the one the session was created (and prefetched) at — a
        // prepared session opened seconds later. It is moved BEFORE the attach: on a session that is not on the engine
        // yet the native seek only moves the position the source's first Start lands at, so the engine never loads at
        // the old position and seeks afterwards.
        long start = StartMsOf(request);
        if (start != _nativeStartMs && Volatile.Read(ref _attached) == 0)
        {
            int hs = _native.SessionSeek(_rt.Handle, _s, start, PrNative.SeekExact, PrNative.NoKeyframeHint);
            if (hs >= 0) _nativeStartMs = start;
            else LogVideo($"attach.start.fail key={_logKey} startMs={start} hr=0x{unchecked((uint)hs):X8}");
        }

        Interlocked.Increment(ref _attachEpoch);
        Volatile.Write(ref _attachTimestamp, Stopwatch.GetTimestamp());
        int hr = _native.SessionAttach(_rt.Handle, _s, lic);
        if (hr < 0)
        {
            Volatile.Write(ref _nativeErrorHr, hr);
            LogVideo($"attach.fail key={_logKey} hr=0x{unchecked((uint)hr):X8}");
            RequestPump();
            return;
        }
        Volatile.Write(ref _attached, 1);
        _streamW = -1; _streamH = -1;   // a fresh SetSource: the stream size must be re-asserted

        if (!request.StartPaused)
            _native.SessionPlay(_rt.Handle, _s);

        LogVideo($"attach key={_logKey} startMs={_nativeStartMs} license={(lic == 0 ? "none" : _rt.LicenseStateFor(kid).ToString())} " +
                 $"paused={request.StartPaused}");
        RequestPump();
    }

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (!_disposed && _s != 0) _native.SessionPlay(_rt.Handle, _s);
        return ValueTask.CompletedTask;   // no ack poll: Playing arrives as an event
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (!_disposed && _s != 0) _native.SessionPause(_rt.Handle, _s);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(long positionMs, SeekMode mode) => SeekAsync(positionMs, mode, PrNative.NoKeyframeHint);

    /// <inheritdoc/>
    public ValueTask SeekAsync(long positionMs, SeekMode mode, long keyframeMs)
    {
        if (_disposed || _s == 0) return ValueTask.CompletedTask;
        long target = Math.Max(0, positionMs);
        Volatile.Write(ref _seekTargetMs, target);
        Volatile.Write(ref _seekLandedMs, -1);
        Volatile.Write(ref _seekIssuedTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _seekPending, 1);
        int hr = _native.SessionSeek(_rt.Handle, _s, target,
            mode == SeekMode.Keyframe ? PrNative.SeekKeyframe : PrNative.SeekExact, keyframeMs);
        if (hr < 0)
        {
            Volatile.Write(ref _seekPending, 0);
            LogVideo($"seek.fail key={_logKey} target={target} hr=0x{unchecked((uint)hr):X8}");
        }
        RequestPump();
        return ValueTask.CompletedTask;   // Seeking/Seeked arrive as events
    }

    /// <inheritdoc/>
    public int GetKeyframes(Span<long> into)
    {
        if (_disposed || _s == 0) return 0;
        int n = _native.SessionGetKeyframes(_rt.Handle, _s, into);
        return n < 0 ? 0 : n;
    }

    /// <inheritdoc/>
    public int GetBuffered(Span<long> pairs)
    {
        if (_disposed || _s == 0) return 0;
        int n = _native.SessionGetBuffered(_rt.Handle, _s, pairs);
        return n < 0 ? 0 : n;
    }

    /// <inheritdoc/>
    public ValueTask SelectVideoRepresentationAsync(string representationId)
    {
        if (_disposed || _s == 0 || _videoTrack is null) return ValueTask.CompletedTask;
        for (int i = 0; i < _videoTrack.Representations.Count; i++)
        {
            ProtectedRepresentationDescriptor rep = _videoTrack.Representations[i];
            if (!string.Equals(rep.Id, representationId, StringComparison.Ordinal)) continue;
            if (rep.InitUrl is not { Length: > 0 } initUrl)
            {
                LogVideo($"representation.fail key={_logKey} id={representationId} reason=no-init-url");
                return ValueTask.CompletedTask;
            }
            int hr = _native.SessionSelectRepresentation(_rt.Handle, _s, i, initUrl, rep.SegmentBaseUrl,
                rep.SegmentPrefix, rep.SegmentSuffix);
            if (hr < 0) LogVideo($"representation.fail key={_logKey} id={representationId} hr=0x{unchecked((uint)hr):X8}");
            return ValueTask.CompletedTask;   // FgPrEvent_Representation reports the applied index
        }
        throw new ArgumentOutOfRangeException(nameof(representationId));
    }

    /// <inheritdoc/>
    public void SetVolume(float volume)
    {
        if (!_disposed && _s != 0) _native.SessionSetVolume(_rt.Handle, _s, Math.Clamp(volume, 0f, 1f));
    }

    /// <inheritdoc/>
    public void SetRate(float rate)
    {
        if (_disposed || _s == 0) return;
        int hr = _native.SessionSetRate(_rt.Handle, _s, rate);
        if (hr < 0) LogVideo($"rate.fail key={_logKey} rate={rate} hr=0x{unchecked((uint)hr):X8} (rate NOT applied)");
    }

    /// <inheritdoc/>
    public void SetStreamSize(SizeI size)
    {
        if (_disposed || _s == 0 || Volatile.Read(ref _attached) == 0) return;
        if (size.Width == _streamW && size.Height == _streamH) return;   // value-gated: one native call per real change
        _streamW = size.Width; _streamH = size.Height;
        _native.SessionSetStreamSize(_rt.Handle, _s, Math.Max(0, size.Width), Math.Max(0, size.Height));
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_disposed || _s == 0) return;
        if (Interlocked.Exchange(ref _attached, 0) == 0) return;
        _native.SessionDetach(_rt.Handle, _s);
        if (_licensePinned) { _rt.PinLicense(_kid, pinned: false); _licensePinned = false; }
        LogVideo($"detach key={_logKey}");
    }

    /// <inheritdoc/>
    public void LogDiagnostic(string message) => LogVideo(message);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PumpRequested = null;
        if (_s != 0)
        {
            ulong s = _s;
            _rt.UnregisterSession(s);
            _s = 0;
            Volatile.Write(ref _attached, 0);
            try { _native.SessionDestroy(_rt.Handle, s); } catch { }
        }
        if (_licensePinned) { _rt.PinLicense(_kid, pinned: false); _licensePinned = false; }
        if (_holdsRuntimeRef) { _holdsRuntimeRef = false; _rt.Release(); }
    }

    // ── the pump (UI thread) ───────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Pump(in VideoBinding binding)
    {
        if (_startupError is { } startup)
        {
            if (_error.Peek() is null) _error.Value = startup;
            if (_state.Peek() != ProtectedVideoState.Error) _state.Value = ProtectedVideoState.Error;
            _phase = ProtectedVideoPhase.Failed;
            return;
        }
        if (_disposed || _s == 0) return;

        PrNative.Snapshot n = default;
        if (_native.SessionSnapshot(_rt.Handle, _s, ref n) < 0) return;

        bool attached = Volatile.Read(ref _attached) != 0;
        int epoch = Volatile.Read(ref _attachEpoch);
        bool firstFrame = attached && Volatile.Read(ref _firstFrameAttachEpoch) == epoch && epoch != 0;
        bool metadata = attached && Volatile.Read(ref _metadataAttachEpoch) == epoch && epoch != 0;
        bool canPlay = attached && Volatile.Read(ref _canPlayAttachEpoch) == epoch && epoch != 0;

        // 1. Errors: the relay's own words beat an HRESULT; a native decode/network error beats nothing.
        int licenseHr = Volatile.Read(ref _licenseFailedHr);
        int errHr = n.ErrorHr != 0 ? n.ErrorHr : Volatile.Read(ref _nativeErrorHr);
        if (_error.Peek() is null)
        {
            if (licenseHr != 0)
                _error.Value = _rt.LicenseFailureFor(_kid) ?? $"The PlayReady license was not granted (0x{unchecked((uint)licenseHr):X8}).";
            else if (n.State == PrNative.StateError || errHr < 0)
                _error.Value = $"Protected playback failed (MF_MEDIA_ENGINE_ERR {Volatile.Read(ref _nativeErrorCode)}, 0x{unchecked((uint)errHr):X8}).";
        }

        // 2. State.
        ProtectedVideoState state = _error.Peek() is not null ? ProtectedVideoState.Error : MapNativeState(n.State, attached,
            firstFrame, Volatile.Read(ref _licenseUsable) != 0, n.BufferedAheadMs, n.ReadyState);
        if (_state.Peek() != state) _state.Value = state;

        // 3. Phase — from events, never inferred from a timer.
        _phase = DerivePhase(state, attached, firstFrame, metadata || canPlay, Volatile.Read(ref _licenseUsable) != 0,
                             _kid is not null, n.BufferedAheadMs);

        // 4. Clock, duration, size, index.
        if (_positionMs.Peek() != n.PositionMs) _positionMs.Value = n.PositionMs;
        _positionQpc = n.PositionQpc;
        if (n.DurationMs > 0 && _durationMs.Peek() != n.DurationMs) _durationMs.Value = n.DurationMs;
        if (n.Width > 0 && n.Height > 0)
        {
            var size = new Size2(n.Width, n.Height);
            if (!_naturalSize.Peek().Equals(size)) _naturalSize.Value = size;
        }
        Volatile.Write(ref _forwardBufferedMs, Math.Max(0, n.BufferedAheadMs));
        Volatile.Write(ref _retainedBehindMs, Math.Max(0, n.RetainedBehindMs));
        if (n.ActiveRepresentation >= 0 && _videoTrack is { } track && n.ActiveRepresentation < track.Representations.Count)
            _activeRepresentationId = track.Representations[n.ActiveRepresentation].Id;
        if (n.BytesDownloaded > 0) Volatile.Write(ref _bytes, unchecked((long)n.BytesDownloaded));
        if (n.DownloadElapsedMs > 0) Volatile.Write(ref _bytesMs, unchecked((long)n.DownloadElapsedMs));
        if (n.Seeking == 0 && Volatile.Read(ref _seekLandedMs) >= 0) Volatile.Write(ref _seekPending, 0);

        // 5. The same POD the clear path publishes (gate.media.seam.snapshot-alloc-free covers both backends).
        _snapshots.Publish(Map(in n, epoch, firstFrame, metadata, canPlay));

        // 6. The surface. Bound EVERY pump (the registry value-gates a repeat): a placement move (docked → PiP →
        //    pop-out) targets a NEW registry token that must receive the SAME handle — no open, no seek.
        if (n.Handle != 0 && attached)
        {
            binding.Bind((nuint)n.Handle);
            _hasSurface = true;
        }
        else if (!attached)
        {
            _hasSurface = false;
        }
    }

    /// <summary>The managed lifecycle state for a native <c>FgPrState</c> and the facts the events established. Pure.
    /// A loading source whose license is usable reads <see cref="ProtectedVideoState.Licensed"/>; a playing source that
    /// has presented a frame and has neither store ahead of the playhead nor HAVE_FUTURE_DATA is rebuffering.</summary>
    internal static ProtectedVideoState MapNativeState(int nativeState, bool attached, bool firstFrame, bool licenseUsable,
                                                       long bufferedAheadMs, int readyState) => nativeState switch
    {
        PrNative.StateLoading => licenseUsable ? ProtectedVideoState.Licensed : ProtectedVideoState.Loading,
        PrNative.StatePlaying => firstFrame && bufferedAheadMs <= 0 && readyState < 3 ? ProtectedVideoState.Buffering : ProtectedVideoState.Playing,
        PrNative.StatePaused => ProtectedVideoState.Paused,
        PrNative.StateStopped => ProtectedVideoState.Stopped,
        PrNative.StateError => ProtectedVideoState.Error,
        PrNative.StateEnded => ProtectedVideoState.Ended,
        _ => attached ? ProtectedVideoState.Loading : ProtectedVideoState.Idle,
    };

    /// <summary>The switch phase from the facts the events established. Pure; ordered from terminal to earliest.</summary>
    internal static ProtectedVideoPhase DerivePhase(ProtectedVideoState state, bool attached, bool firstFrame,
        bool metadataOrCanPlay, bool licenseUsable, bool licenseExpected, long bufferedAheadMs)
    {
        if (state == ProtectedVideoState.Error) return ProtectedVideoPhase.Failed;
        if (!attached) return ProtectedVideoPhase.Idle;
        if (firstFrame) return state == ProtectedVideoState.Playing ? ProtectedVideoPhase.Playing : ProtectedVideoPhase.Presenting;
        if (licenseExpected && !licenseUsable) return ProtectedVideoPhase.Licensing;
        if (metadataOrCanPlay) return ProtectedVideoPhase.Attaching;
        return bufferedAheadMs > 0 ? ProtectedVideoPhase.Attaching : ProtectedVideoPhase.Buffering;
    }

    private static VideoEngineSnapshot Map(in PrNative.Snapshot n, int epoch, bool firstFrame, bool metadata, bool canPlay)
    {
        VideoEngineFlags flags = VideoEngineFlags.None;
        if (metadata) flags |= VideoEngineFlags.MetadataLoaded;
        if (canPlay) flags |= VideoEngineFlags.CanPlay;
        if (n.State == PrNative.StatePlaying) flags |= VideoEngineFlags.Playing;
        if (n.Seeking != 0) flags |= VideoEngineFlags.Seeking;
        if (n.State == PrNative.StateEnded) flags |= VideoEngineFlags.Ended;
        if (n.State == PrNative.StateError) flags |= VideoEngineFlags.Error;
        if (n.Width > 0 && n.Height > 0) flags |= VideoEngineFlags.NaturalSizeKnown;
        return new VideoEngineSnapshot
        {
            SourceEpoch = epoch,
            Flags = flags,
            ReadyState = (uint)Math.Max(0, n.ReadyState),
            NaturalW = (uint)Math.Max(0, n.Width),
            NaturalH = (uint)Math.Max(0, n.Height),
            DurationSeconds = n.DurationMs / 1000.0,
            PositionSeconds = n.PositionMs / 1000.0,
            PositionTimestamp = n.PositionQpc,
            PlaybackRate = 1.0,
            SwapchainHandle = (nuint)n.Handle,
            ErrorHr = n.ErrorHr,
            FirstFrameTimestamp = firstFrame ? n.FirstFrameQpc : 0,
            BufferedAheadMs = n.BufferedAheadMs,
        };
    }

    // ── native events (runtime/MF thread) ──────────────────────────────────────────────────────────────────────────

    /// <summary>A session event from the runtime's sink. Runs on the runtime or an MF thread: it flips POD fields,
    /// writes the lifecycle line, and asks for ONE coalesced pump. It never touches a signal.</summary>
    internal void OnNativeEvent(int ev, long a, long b)
    {
        if (_disposed) return;
        int epoch = Volatile.Read(ref _attachEpoch);
        switch (ev)
        {
            case PrNative.EvPosition:
            case PrNative.EvBytes:
                if (ev == PrNative.EvBytes) { Volatile.Write(ref _bytes, a); Volatile.Write(ref _bytesMs, b); }
                return;   // never a pump trigger — the snapshot carries the timestamped sample

            case PrNative.EvBuffered:
                Volatile.Write(ref _forwardBufferedMs, Math.Max(0, a));
                Volatile.Write(ref _retainedBehindMs, Math.Max(0, b));
                Interlocked.Increment(ref _indexEpoch);
                break;

            case PrNative.EvKeyframes:
                Interlocked.Increment(ref _indexEpoch);
                break;

            case PrNative.EvAttached:
                LogVideo($"attach.ok key={_logKey} setSourceMs={a}");
                break;

            case PrNative.EvMetadata:
                Volatile.Write(ref _metadataAttachEpoch, epoch);
                LogVideo($"metadata key={_logKey} dur={a}ms size={(b >> 32) & 0xFFFFFFFF}x{b & 0xFFFFFFFF} " +
                         $"sinceAttachMs={SinceAttachMs()}");
                break;

            case PrNative.EvCanPlay:
                Volatile.Write(ref _canPlayAttachEpoch, epoch);
                LogVideo($"canplay key={_logKey} sinceAttachMs={SinceAttachMs()}");
                break;

            case PrNative.EvFirstFrame:
                if (Volatile.Read(ref _firstFrameAttachEpoch) != epoch)
                {
                    Volatile.Write(ref _firstFrameAttachEpoch, epoch);
                    Interlocked.Increment(ref _firstFrameEpoch);
                    LogVideo($"first.frame key={_logKey} at={a}ms sinceAttachMs={SinceAttachMs()}");
                }
                break;

            case PrNative.EvSeeked:
            {
                long target = Volatile.Read(ref _seekTargetMs);
                long issued = Volatile.Read(ref _seekIssuedTimestamp);
                Volatile.Write(ref _seekLandedMs, a);
                Volatile.Write(ref _seekPending, 0);
                LogVideo($"seek.done key={_logKey} target={target} landed={a} " +
                         $"ms={(issued != 0 ? Stopwatch.GetElapsedTime(issued).TotalMilliseconds : b):F0}");
                break;
            }

            case PrNative.EvError:
                Volatile.Write(ref _nativeErrorCode, (int)a);
                Volatile.Write(ref _nativeErrorHr, b != 0 ? (int)b : PrNative.EFail);
                LogVideo($"error key={_logKey} code={a} hr=0x{unchecked((uint)b):X8}");
                break;

            case PrNative.EvEnded:
                LogVideo($"ended key={_logKey}");
                break;

            case PrNative.EvDetached:
            case PrNative.EvHandle:
            case PrNative.EvSeeking:
            case PrNative.EvPlaying:
            case PrNative.EvPaused:
            case PrNative.EvRepresentation:
                break;

            default:
                return;
        }
        RequestPump();
    }

    /// <summary>A license event for SOME KID; this session reacts only when it names the key it decodes with.</summary>
    internal void OnLicenseEvent(ulong licenseHandle, int ev, long a)
    {
        if (_disposed || licenseHandle == 0) return;
        if (_rt.LicenseHandleFor(_kid) != licenseHandle) return;
        if (ev == PrNative.EvLicenseUsable) Volatile.Write(ref _licenseUsable, 1);
        else if (ev == PrNative.EvLicenseFailed) Volatile.Write(ref _licenseFailedHr, a != 0 ? (int)a : PrNative.EFail);
        else if (ev == PrNative.EvLicenseExpired) Volatile.Write(ref _licenseUsable, 0);
        RequestPump();
    }

    private void RequestPump()
    {
        try { PumpRequested?.Invoke(); } catch { /* a pump request must never throw into native */ }
    }

    private long SinceAttachMs()
    {
        long t = Volatile.Read(ref _attachTimestamp);
        return t == 0 ? -1 : (long)Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    }

    private void LogVideo(string line) => ProtectedVideoRuntime.WriteVideoLine(line);

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string KeyTail(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "-";
        int q = url.IndexOf('?');
        string path = q >= 0 ? url[..q] : url;
        return path.Length <= 24 ? path : "…" + path[^24..];
    }

    private static ProtectedTrackDescriptor? FindDefaultVideoTrack(ProtectedAdaptiveCatalog? catalog)
    {
        if (catalog is null) return null;
        ProtectedTrackDescriptor? first = null;
        for (int i = 0; i < catalog.Tracks.Count; i++)
        {
            ProtectedTrackDescriptor track = catalog.Tracks[i];
            if (track.Kind != FluentGpu.Media.TrackKind.Video) continue;
            first ??= track;
            if (track.IsDefault) return track;
        }
        return first;
    }
}
