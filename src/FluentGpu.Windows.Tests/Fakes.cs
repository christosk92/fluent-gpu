using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Pal;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Media.PlayReady;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// A fully in-memory <see cref="IVideoEngine"/> v2 — no D3D11/MF device. Built around a REAL
/// <see cref="VideoSnapshotBuffer"/> (tests script it through the property setters below, which each build a fresh
/// <see cref="VideoEngineSnapshot"/> from the fake's current field values and <see cref="VideoSnapshotBuffer.Publish"/>
/// it — exercising the exact seqlock a production consumer reads) and a REAL <see cref="VideoEngineCommandQueue"/>
/// (nothing drains it automatically — there is no simulated engine thread — so a test asserts what
/// <see cref="MfMediaSession"/> posted by draining the queue itself, e.g. via the <see cref="FakeEngineCommandExtensions"/>
/// helpers below).
/// <para><see cref="Play"/>/<see cref="Playing"/> intentionally do not auto-couple — posting a Transport(play) command
/// does NOT flip <see cref="Playing"/> by itself (nothing is draining the queue) — so a test can model the "intent
/// accepted but engine not yet advancing" (buffering) gap exactly as the real engine's own worker-thread event lag
/// does.</para>
/// </summary>
internal sealed class FakeVideoEngine : IVideoEngine
{
    public event Action? StateChanged;

    private readonly VideoSnapshotBuffer _buffer = new();
    // Defaults mirror the v1 fake's ergonomics (1920x1080, answered) — the v2 engine always answers the natural size
    // synchronously in practice (see the class doc comment), so "already known" is the realistic default; a test
    // modeling the rare unanswered case sets NaturalSizeKnown = false explicitly.
    private VideoEngineSnapshot _s = new()
    { PlaybackRate = 1.0, NaturalW = 1920, NaturalH = 1080, Flags = VideoEngineFlags.NaturalSizeKnown };

    public VideoEngineCommandQueue Commands { get; } = new();
    /// <inheritdoc/>
    public VideoEngineSnapshot Snapshot => _buffer.Read();
    /// <inheritdoc/>
    public bool CanPlayHls { get; set; } = true;

    public int StartCalls, DisposeCalls;
    public int PostSetSourceCalls, PostDetachCalls;
    public string? LastSetSourceUrl;
    /// <summary>The next epoch <see cref="PostSetSource"/> hands out — mirrors the real engine's monotonic counter so a
    /// test can predict what a scripted <c>SourceEpoch</c> must match.</summary>
    public int NextEpoch = 1;

    public void Start() => StartCalls++;

    /// <inheritdoc/>
    public int PostSetSource(string url)
    {
        PostSetSourceCalls++;
        LastSetSourceUrl = url;
        int epoch = NextEpoch++;
        _s.SourceEpoch = epoch;
        Publish();
        return epoch;
    }

    /// <inheritdoc/>
    public void PostDetach() => PostDetachCalls++;

    public void Dispose() => DisposeCalls++;

    /// <summary>Republish the fake's current field values as a fresh snapshot (a real timestamp, so position
    /// extrapolation in a test behaves like the real engine's).</summary>
    private void Publish() { _s.PositionTimestamp = Stopwatch.GetTimestamp(); _buffer.Publish(_s); }

    /// <summary>Wake any subscriber exactly as the real engine's coalesced refresh does — a test calls this after
    /// scripting new field values to model the engine's own out-of-cadence publish-then-raise.</summary>
    public void RaiseStateChanged() => StateChanged?.Invoke();

    private void SetFlag(VideoEngineFlags flag, bool on) { _s.Flags = on ? _s.Flags | flag : _s.Flags & ~flag; Publish(); }

    public bool MetadataLoaded { get => (_s.Flags & VideoEngineFlags.MetadataLoaded) != 0; set => SetFlag(VideoEngineFlags.MetadataLoaded, value); }
    public bool CanPlay { get => (_s.Flags & VideoEngineFlags.CanPlay) != 0; set => SetFlag(VideoEngineFlags.CanPlay, value); }
    public bool Playing { get => (_s.Flags & VideoEngineFlags.Playing) != 0; set => SetFlag(VideoEngineFlags.Playing, value); }
    public bool Seeking { get => (_s.Flags & VideoEngineFlags.Seeking) != 0; set => SetFlag(VideoEngineFlags.Seeking, value); }
    public bool Ended { get => (_s.Flags & VideoEngineFlags.Ended) != 0; set => SetFlag(VideoEngineFlags.Ended, value); }
    public bool HasError { get => (_s.Flags & VideoEngineFlags.Error) != 0; set => SetFlag(VideoEngineFlags.Error, value); }
    /// <summary>Mirrors <see cref="VideoEngineFlags.LiveSource"/> directly — the fake does NOT latch it (unlike the
    /// real engine): that monotonic-within-a-source guarantee is now an ENGINE contract (<c>VideoMediaEngine</c>), not
    /// something <see cref="MfMediaSession"/> defends against, so there is nothing session-level left to test by
    /// un-setting it after setting it.</summary>
    public bool IsLiveSource { get => (_s.Flags & VideoEngineFlags.LiveSource) != 0; set => SetFlag(VideoEngineFlags.LiveSource, value); }
    public bool NaturalSizeKnown { get => (_s.Flags & VideoEngineFlags.NaturalSizeKnown) != 0; set => SetFlag(VideoEngineFlags.NaturalSizeKnown, value); }
    public bool Faulted { get => (_s.Flags & VideoEngineFlags.Faulted) != 0; set => SetFlag(VideoEngineFlags.Faulted, value); }

    public uint ErrorCode { get => _s.ErrorCode; set { _s.ErrorCode = value; Publish(); } }
    public int ErrorHr { get => _s.ErrorHr; set { _s.ErrorHr = value; Publish(); } }
    public uint ReadyState { get => _s.ReadyState; set { _s.ReadyState = value; Publish(); } }
    /// <summary>The monotonic presentation epoch. <see cref="RaiseFormatChange"/> models MF's FORMATCHANGE/RESOURCELOST
    /// (bump it, then wake the session exactly as the real notify sink does).</summary>
    public int PresentationEpoch { get => _s.PresentationEpoch; set { _s.PresentationEpoch = value; Publish(); } }
    public double DurationSeconds { get => _s.DurationSeconds; set { _s.DurationSeconds = value; Publish(); } }
    public double CurrentTimeSeconds { get => _s.PositionSeconds; set { _s.PositionSeconds = value; Publish(); } }
    public double PlaybackRate { get => _s.PlaybackRate; set { _s.PlaybackRate = value; Publish(); } }
    // Live state the session reads (SeekableRange / CanPlayHls). Settable so a test can script "not answered yet"
    // ((0,0)) and then a real window.
    public (double Start, double End) SeekableRange
    {
        get => (_s.SeekableStart, _s.SeekableEnd);
        set { _s.SeekableStart = value.Start; _s.SeekableEnd = value.End; Publish(); }
    }

    public uint NativeW { get => _s.NaturalW; set { _s.NaturalW = value; Publish(); } }
    public uint NativeH { get => _s.NaturalH; set { _s.NaturalH = value; Publish(); } }
    public nuint Handle { get => _s.SwapchainHandle; set { _s.SwapchainHandle = value; Publish(); } }

    /// <summary>Model a mid-stream variant switch: bump the presentation epoch and wake the session exactly as a real
    /// FORMATCHANGE/RESOURCELOST would. Does NOT auto-clear <see cref="NaturalSizeKnown"/> — a test modeling "the new
    /// variant's size is not known yet" clears it explicitly (and sets it again once it scripts the new answer), the
    /// same two-step a real FORMATCHANGE drives through <c>VideoMediaEngine</c>'s per-epoch re-query.</summary>
    public void RaiseFormatChange() { PresentationEpoch++; RaiseStateChanged(); }
}

/// <summary>Ergonomic drain helpers over a REAL <see cref="VideoEngineCommandQueue"/> so a test can assert exactly what
/// <see cref="MfMediaSession"/> (or <see cref="MfMediaPlayer"/>) posted without hand-unpacking the queue's generic
/// <c>(a, i, j, obj)</c> payload at every call site. Mirrors the field mapping <c>VideoMediaEngine.DrainCommands</c>
/// uses in production — the pinned comments on <c>VideoCommandKind</c>.</summary>
internal static class FakeEngineCommandExtensions
{
    public static bool TryTakeTransport(this VideoEngineCommandQueue q, out bool play)
    { bool ok = q.TryTake(VideoCommandKind.Transport, out double a, out _, out _, out _); play = a != 0; return ok; }

    public static bool TryTakeSeek(this VideoEngineCommandQueue q, out double seconds, out bool approximate)
    { bool ok = q.TryTake(VideoCommandKind.Seek, out double a, out int i, out _, out _); seconds = a; approximate = i != 0; return ok; }

    public static bool TryTakeRate(this VideoEngineCommandQueue q, out double rate)
    { bool ok = q.TryTake(VideoCommandKind.Rate, out double a, out _, out _, out _); rate = a; return ok; }

    public static bool TryTakeVolume(this VideoEngineCommandQueue q, out double volume)
    { bool ok = q.TryTake(VideoCommandKind.Volume, out double a, out _, out _, out _); volume = a; return ok; }

    public static bool TryTakeMuted(this VideoEngineCommandQueue q, out bool muted)
    { bool ok = q.TryTake(VideoCommandKind.Muted, out _, out int i, out _, out _); muted = i != 0; return ok; }

    public static bool TryTakeLoop(this VideoEngineCommandQueue q, out bool loop)
    { bool ok = q.TryTake(VideoCommandKind.Loop, out _, out int i, out _, out _); loop = i != 0; return ok; }

    public static bool TryTakeStreamRect(this VideoEngineCommandQueue q, out int w, out int h)
    { bool ok = q.TryTake(VideoCommandKind.StreamRect, out _, out int i, out int j, out _); w = i; h = j; return ok; }

    public static bool TryTakeRepaint(this VideoEngineCommandQueue q)
        => q.TryTake(VideoCommandKind.Repaint, out _, out _, out _, out _);

    public static bool TryTakeSetSource(this VideoEngineCommandQueue q, out int epoch, out string? url)
    {
        bool ok = q.TryTake(VideoCommandKind.SetSource, out _, out int i, out _, out object? obj);
        epoch = i; url = obj as string;
        return ok;
    }

    public static bool TryTakeDetach(this VideoEngineCommandQueue q)
        => q.TryTake(VideoCommandKind.Detach, out _, out _, out _, out _);
}

/// <summary>A recording <see cref="IVideoPresenter"/> — no DComp. Captures the calls the registry drain makes so a test
/// can assert the surface handoff (create → bind → place) without a GPU.</summary>
internal sealed class FakeVideoPresenter : IVideoPresenter
{
    public readonly List<string> Calls = new();
    public int NextId = 1;
    public VideoSurfaceId LastCreated;
    public nuint LastBoundHandle;
    public RectF LastPlaceRect;
    public RectF LastViewport;
    public uint LastContentW, LastContentH;
    public bool LastVisible;
    public int Commits;

    public VideoSurfaceId CreateSurface()
    {
        var id = new VideoSurfaceId((uint)NextId++);
        LastCreated = id;
        Calls.Add($"Create({id.Value})");
        return id;
    }

    public void BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
    {
        LastBoundHandle = dcompSurfaceHandle;
        Calls.Add($"Bind({id.Value},0x{dcompSurfaceHandle:X})");
    }

    public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z)
    {
        LastPlaceRect = deviceRect;
        Calls.Add($"Place({id.Value})");
    }

    public void SetContentSize(VideoSurfaceId id, uint width, uint height)
    {
        LastContentW = width; LastContentH = height;
        Calls.Add($"Content({id.Value},{width}x{height})");
    }

    public void SetViewport(VideoSurfaceId id, RectF deviceRect) { LastViewport = deviceRect; Calls.Add($"Viewport({id.Value})"); }
    public void SetVisible(VideoSurfaceId id, bool visible) { LastVisible = visible; Calls.Add($"Visible({id.Value},{visible})"); }
    public void Destroy(VideoSurfaceId id) => Calls.Add($"Destroy({id.Value})");
    public void Commit() => Commits++;
}

/// <summary>
/// An in-memory <see cref="IProtectedVideoPlayer"/> — no native runtime, no CDM, no GPU. Tests script the exact state the
/// protected session must map through the <c>Set*</c> helpers and the settable event-derived properties
/// (<see cref="Phase"/>, <see cref="FirstFrameEpoch"/>, <see cref="IsSeeking"/>, …), then call <see cref="RaisePump"/>
/// to model the native runtime's "state changed, one pump is due" event.
/// <para>Mirrors the production contract where it matters to the session: the transport verbs return COMPLETED tasks
/// (their acknowledgement is the next event), <see cref="SeekAsync(long, SeekMode, long)"/> marks
/// <see cref="IsSeeking"/> synchronously and clears <see cref="LastSeekLandedMs"/> (the test lands the seek by clearing
/// <see cref="IsSeeking"/>), and <see cref="Pump"/> binds <see cref="SurfaceHandle"/> through the binding it is given on
/// EVERY pump (recording the token) so a placement move is observable at the registry.</para>
/// <para><see cref="ReadyOnStart"/> models a first-frame-ready preroll at <see cref="Start"/>; <see cref="ReadyOnPrefetch"/>
/// makes <see cref="PrefetchAsync"/> land 4 s of forward media when it completes; <see cref="PrefetchResult"/> (null ⇒
/// completes at once) lets a test hold the prefetch open.</para>
/// </summary>
internal sealed class FakeProtectedVideoPlayer : IProtectedVideoPlayer
{
    private readonly Signal<ProtectedVideoState> _state = new(ProtectedVideoState.Idle);
    private readonly Signal<long> _positionMs = new(0);
    private readonly Signal<long> _durationMs = new(0);
    private readonly Signal<Size2> _naturalSize = new(default);
    private readonly Signal<string?> _error = new(null);
    private readonly List<string> _diagnostics = new();

    public ProtectedVideoRequest? StartedWith;
    public int StartCalls, PlayCalls, PauseCalls, SeekCalls, StopCalls, DisposeCalls;
    public int PumpCalls, PrefetchCalls, SetStreamSizeCalls, SelectRepresentationCalls;
    public long LastSeekMs = -1;
    public SeekMode LastSeekMode = SeekMode.Accurate;
    /// <summary>The keyframe hint of the last seek (-1 = "native decides", also what the two-argument overload sends).</summary>
    public long LastKeyframeHint = -1;
    public SizeI LastStreamSize;
    public float LastVolume = 1f;
    public float LastRate = 1f;
    public int LastPrefetchSegments;
    public bool ReadyOnStart;
    public bool ReadyOnPrefetch;
    /// <summary>Holds <see cref="PrefetchAsync"/> open until the test completes it; null ⇒ the prefetch completes at once.</summary>
    public TaskCompletionSource? PrefetchResult;
    /// <summary>The scripted keyframe table (ascending ms) <see cref="GetKeyframes"/> copies out.</summary>
    public long[] Keyframes = Array.Empty<long>();
    /// <summary>The scripted buffered ranges as flattened (start, end) ms pairs <see cref="GetBuffered"/> copies out.</summary>
    public long[] Buffered = Array.Empty<long>();
    /// <summary>The swap-chain handle <see cref="Pump"/> binds through the binding while <see cref="HasSurface"/> (0 = none).</summary>
    public nuint SurfaceHandle;
    /// <summary>Every binding token the player was pumped with, in order (0 for an inert/default binding).</summary>
    public readonly List<int> PumpedTokens = new();
    public string? LastSelectedRepresentationId;

    public bool SupportsAdaptiveSelection { get; set; }
    public string? ActiveVideoRepresentationId { get; set; }
    public bool HasSurface { get; set; }
    public ProtectedVideoPhase Phase { get; set; }
    public long FirstFrameEpoch { get; set; }
    public long PositionQpc { get; set; }
    public bool IsSeeking { get; set; }
    public long LastSeekLandedMs { get; set; } = -1;
    public long ForwardBufferedMs { get; set; }
    public long RetainedBehindMs { get; set; }
    public int IndexEpoch { get; set; }
    public long BytesDownloaded { get; set; }
    public long DownloadElapsedMs { get; set; }

    public IReadSignal<ProtectedVideoState> State => _state;
    public IReadSignal<long> PositionMs => _positionMs;
    public IReadSignal<long> DurationMs => _durationMs;
    public IReadSignal<Size2> NaturalSize => _naturalSize;
    public IReadSignal<string?> Error => _error;

    public event Action? PumpRequested;

    /// <summary>How many handlers are subscribed to <see cref="PumpRequested"/> (the session subscribes once, unsubscribes on dispose).</summary>
    public int PumpRequestedSubscribers => PumpRequested?.GetInvocationList().Length ?? 0;

    /// <summary>Model the native runtime's event: native state changed and one coalesced UI pump is due.</summary>
    public void RaisePump() => PumpRequested?.Invoke();

    /// <summary>The lifecycle lines the session appended through <see cref="LogDiagnostic"/>.</summary>
    public string[] Diagnostics { get { lock (_diagnostics) return _diagnostics.ToArray(); } }

    // Test scripting helpers.
    public void SetState(ProtectedVideoState s) => _state.Value = s;
    public void SetError(string? e) => _error.Value = e;
    public void SetNaturalSize(int w, int h) => _naturalSize.Value = new Size2(w, h);
    public void SetDurationMs(long ms) => _durationMs.Value = ms;
    public void SetPositionMs(long ms) => _positionMs.Value = ms;

    public Task PrefetchAsync(int segments, CancellationToken ct)
    {
        PrefetchCalls++;
        LastPrefetchSegments = segments;
        if (PrefetchResult is not { } pending)
        {
            if (ReadyOnPrefetch) ForwardBufferedMs = 4000;
            return Task.CompletedTask;
        }
        return AwaitPrefetch(pending.Task, ct);
    }

    private async Task AwaitPrefetch(Task pending, CancellationToken ct)
    {
        await pending.WaitAsync(ct).ConfigureAwait(false);
        if (ReadyOnPrefetch) ForwardBufferedMs = 4000;
    }

    public void Start(ProtectedVideoRequest request)
    {
        StartCalls++;
        StartedWith = request;
        if (request.Catalog is { } catalog)
            for (int t = 0; t < catalog.Tracks.Count; t++)
                for (int r = 0; r < catalog.Tracks[t].Representations.Count; r++)
                    if (catalog.Tracks[t].Kind == FluentGpu.Media.TrackKind.Video &&
                        catalog.Tracks[t].Representations[r].InitUrl == request.InitUrl)
                        ActiveVideoRepresentationId = catalog.Tracks[t].Representations[r].Id;
        if (!request.StartPaused) PlayCalls++;
        if (ReadyOnStart)
        {
            HasSurface = true;
            _naturalSize.Value = new Size2(1280, 720);
            _state.Value = ProtectedVideoState.Playing;
            FirstFrameEpoch++;
            Phase = ProtectedVideoPhase.Playing;
        }
    }

    public ValueTask PlayAsync() { PlayCalls++; return ValueTask.CompletedTask; }
    public ValueTask PauseAsync() { PauseCalls++; return ValueTask.CompletedTask; }
    public ValueTask SeekAsync(long positionMs, SeekMode mode) => SeekAsync(positionMs, mode, -1);
    public ValueTask SeekAsync(long positionMs, SeekMode mode, long keyframeMs)
    {
        SeekCalls++;
        LastSeekMs = positionMs;
        LastSeekMode = mode;
        LastKeyframeHint = keyframeMs;
        LastSeekLandedMs = -1;
        IsSeeking = true;   // production marks the seek pending synchronously; Seeked clears it
        return ValueTask.CompletedTask;
    }

    public int GetKeyframes(Span<long> into)
    {
        long[] table = Keyframes;
        int n = Math.Min(into.Length, table.Length);
        table.AsSpan(0, n).CopyTo(into);
        return table.Length;
    }

    public int GetBuffered(Span<long> pairs)
    {
        long[] ranges = Buffered;
        int total = ranges.Length / 2;
        int n = Math.Min(pairs.Length / 2, total);
        ranges.AsSpan(0, n * 2).CopyTo(pairs);
        return total;
    }

    public ValueTask SelectVideoRepresentationAsync(string representationId)
    {
        SelectRepresentationCalls++;
        LastSelectedRepresentationId = representationId;
        ActiveVideoRepresentationId = representationId;
        return ValueTask.CompletedTask;
    }

    public void SetVolume(float volume) => LastVolume = volume;
    public void SetRate(float rate) => LastRate = rate;
    public void SetStreamSize(SizeI size) { SetStreamSizeCalls++; LastStreamSize = size; }
    public void Stop() => StopCalls++;
    public void LogDiagnostic(string message) { lock (_diagnostics) _diagnostics.Add(message); }

    public void Pump(in VideoBinding binding)
    {
        PumpCalls++;
        PumpedTokens.Add(binding.Token);
        if (HasSurface && SurfaceHandle != 0) binding.Bind(SurfaceHandle);   // bound EVERY pump, as production does
    }

    public void Dispose() => DisposeCalls++;
}

/// <summary>
/// A recording <see cref="IPrRuntimeNative"/> — the four native calls <see cref="ProtectedVideoRuntime"/> makes, with no
/// DLL, no CDM and no GPU. Handles are monotonically increasing and never 0. Every call is recorded (in order) under a
/// lock, because the runtime's warm-idle teardown runs on a timer thread while the test awaits <see cref="Destroyed"/>.
/// </summary>
internal sealed class FakeRuntimeNative : IPrRuntimeNative
{
    private readonly object _gate = new();
    private readonly List<string> _calls = new();
    private readonly List<ulong> _released = new();
    private readonly List<(string Kid, ulong License)> _acquired = new();
    private ulong _nextHandle = 0x100;
    private int _creates, _destroys, _acquires, _releases;

    /// <summary>Whether the "DLL" loads (<see cref="IPrRuntimeNative.IsAvailable"/>).</summary>
    public bool Available { get; set; } = true;
    /// <summary>A negative HRESULT forces <see cref="RuntimeCreate"/> to fail (0 = succeed).</summary>
    public int CreateHr { get; set; }
    /// <summary>A negative HRESULT forces <see cref="LicenseAcquire"/> to fail with no handle (0 = succeed).</summary>
    public int AcquireHr { get; set; }
    /// <summary>Runs INSIDE <see cref="LicenseAcquire"/> with the handle about to be returned and the KID, before the call
    /// returns — models a license event racing the runtime's handle assignment. May throw to model a native fault.</summary>
    public Action<ulong, string>? DuringAcquire { get; set; }

    /// <summary>Completes on the first <see cref="RuntimeDestroy"/> — a test awaits it with a bounded timeout instead of
    /// polling the runtime's warm-idle teardown (which runs on a timer thread).</summary>
    public TaskCompletionSource Destroyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CreateCount { get { lock (_gate) return _creates; } }
    public int DestroyCount { get { lock (_gate) return _destroys; } }
    public int AcquireCount { get { lock (_gate) return _acquires; } }
    public int ReleaseCount { get { lock (_gate) return _releases; } }
    public ulong LastRuntime { get; private set; }
    public string? LastStorePath { get; private set; }

    /// <summary>Every call in order: <c>create:{rt}</c>, <c>destroy:{rt}</c>, <c>acquire:{kid}:{lic}</c>, <c>release:{lic}</c>.</summary>
    public string[] Calls { get { lock (_gate) return _calls.ToArray(); } }
    public ulong[] Released { get { lock (_gate) return _released.ToArray(); } }
    public (string Kid, ulong License)[] Acquired { get { lock (_gate) return _acquired.ToArray(); } }

    public bool IsAvailable => Available;

    public int RuntimeCreate(string storePath, nint ctx, out ulong runtime)
    {
        lock (_gate)
        {
            _creates++;
            LastStorePath = storePath;
            if (CreateHr < 0)
            {
                runtime = 0;
                _calls.Add("create-failed");
                return CreateHr;
            }
            runtime = _nextHandle++;
            LastRuntime = runtime;
            _calls.Add("create:" + runtime);
            return 0;
        }
    }

    public void RuntimeDestroy(ulong runtime)
    {
        lock (_gate)
        {
            _destroys++;
            _calls.Add("destroy:" + runtime);
        }
        Destroyed.TrySetResult();
    }

    public int LicenseAcquire(ulong runtime, ReadOnlySpan<byte> pssh, string kid, nint ctx, out ulong license)
    {
        ulong lic;
        lock (_gate)
        {
            _acquires++;
            if (AcquireHr < 0)
            {
                _calls.Add("acquire-failed:" + kid);
                license = 0;
                return AcquireHr;
            }
            lic = _nextHandle++;
            _acquired.Add((kid, lic));
            _calls.Add("acquire:" + kid + ":" + lic);
        }
        DuringAcquire?.Invoke(lic, kid);   // outside the lock: the callback re-enters the runtime
        license = lic;
        return 0;
    }

    public void LicenseRelease(ulong runtime, ulong license)
    {
        lock (_gate)
        {
            _releases++;
            _released.Add(license);
            _calls.Add("release:" + license);
        }
    }
}

/// <summary>A recording <see cref="ILicenseDelivery"/>: captures the FIRST delivery's bytes and HRESULT, counts every
/// delivery (the relay contract is exactly once), and completes <see cref="Delivered"/> on the first one.</summary>
internal sealed class RecordingDelivery : ILicenseDelivery
{
    private readonly object _gate = new();
    private int _calls;
    private byte[]? _bytes;
    private int _hr;

    public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls { get { lock (_gate) return _calls; } }
    public byte[]? Bytes { get { lock (_gate) return _bytes; } }
    public int Hr { get { lock (_gate) return _hr; } }

    public void Deliver(ReadOnlySpan<byte> license, int hr)
    {
        lock (_gate)
        {
            _calls++;
            if (_calls == 1)
            {
                _bytes = license.ToArray();
                _hr = hr;
            }
        }
        Delivered.TrySetResult();
    }
}

/// <summary>A recording <see cref="IMediaBackend"/> that returns a scripted session — used to verify MfMediaPlayer routes
/// a DRM source to the injected DRM backend (without a real CDM).</summary>
internal sealed class FakeDrmBackend : IMediaBackend
{
    public int OpenCalls;
    public MediaSource? LastSource;
    public readonly FakeDrmSession Session = new();

    public MediaCapabilities Capabilities { get; } = new(SupportsVideo: true, SupportsAudioGraph: false, SupportsDrm: true);

    public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
    {
        OpenCalls++;
        LastSource = source;
        Session.LastRelay = opts.LicenseRelay;
        return ValueTask.FromResult<IMediaSession>(Session);
    }
}

/// <summary>A no-op <see cref="IMediaSession"/> returned by <see cref="FakeDrmBackend"/>.</summary>
internal sealed class FakeDrmSession : IMediaSession
{
    public Func<LicenseRequest, ValueTask<LicenseResponse>>? LastRelay;
    public void ConnectSignals(MediaSignalSink sink) { }
    public ValueTask PlayAsync() => ValueTask.CompletedTask;
    public ValueTask PauseAsync() => ValueTask.CompletedTask;
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
    public void SetRate(double rate) { }
    public void SetVolume(double volume) { }
    public void SetMuted(bool muted) { }
    public VideoDelivery Video => VideoDelivery.None;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
