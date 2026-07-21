using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Pal;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Media.PlayReady;

namespace FluentGpu.Windows.Tests;

/// <summary>A fully in-memory <see cref="IVideoEngine"/> — no D3D11/MF device. Tests set its flags to script exactly the
/// engine state the session must map. <see cref="Play"/>/<see cref="Pause"/> only COUNT (they do not flip
/// <see cref="Playing"/>) so a test can model the "intent accepted but engine not yet advancing" (buffering) gap.</summary>
internal sealed class FakeVideoEngine : IVideoEngine
{
    public int InitializeResult;
    public int InitializeCalls, PlayCalls, PauseCalls, DisposeCalls, RepaintCalls;
    public double LastSeek = double.NaN, LastRate = 1, LastVolume = 1;
    public bool LastMuted, LastLoop = true;
    public int StreamW, StreamH;

    public bool MetadataLoaded { get; set; }
    public bool CanPlay { get; set; }
    public bool Playing { get; set; }
    public bool Seeking { get; set; }
    public bool Ended { get; set; }
    public bool HasError { get; set; }
    public uint ErrorCode { get; set; }
    public int ErrorHr { get; set; }
    public string LastEventName { get; set; } = "<fake>";
    public uint ReadyState { get; set; }
    public double DurationSeconds { get; set; }
    public double CurrentTimeSeconds { get; set; }

    public uint NativeW = 1920, NativeH = 1080;
    public bool HasNativeSize = true;
    public nuint Handle;

    public int Initialize(string url) { InitializeCalls++; return InitializeResult; }
    public bool TryGetNativeVideoSize(out uint cx, out uint cy) { cx = NativeW; cy = NativeH; return HasNativeSize; }
    public nuint GetSwapchainHandle() => Handle;
    public int SetVideoStreamRect(int w, int h) { StreamW = w; StreamH = h; return 0; }
    public void RepaintCurrentFrame() => RepaintCalls++;
    public void Play() => PlayCalls++;
    public void Pause() => PauseCalls++;
    public void SeekTo(double seconds) => LastSeek = seconds;
    public void SetPlaybackRate(double rate) => LastRate = rate;
    public void SetVolume(double volume) => LastVolume = volume;
    public void SetMuted(bool muted) => LastMuted = muted;
    public void SetLoop(bool loop) => LastLoop = loop;
    public void Dispose() => DisposeCalls++;
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

    public void SetVisible(VideoSurfaceId id, bool visible) { LastVisible = visible; Calls.Add($"Visible({id.Value},{visible})"); }
    public void Destroy(VideoSurfaceId id) => Calls.Add($"Destroy({id.Value})");
    public void Commit() => Commits++;
}

/// <summary>An in-memory <see cref="IProtectedVideoPlayer"/> — no native CDM. Tests drive its signals to script the exact
/// snapshot the protected session must map, and set <see cref="ReadyOnStart"/> to model a first-frame-ready preroll.</summary>
internal sealed class FakeProtectedVideoPlayer : IProtectedVideoPlayer
{
    private readonly Signal<ProtectedVideoState> _state = new(ProtectedVideoState.Idle);
    private readonly Signal<long> _positionMs = new(0);
    private readonly Signal<long> _durationMs = new(0);
    private readonly Signal<Size2> _naturalSize = new(default);
    private readonly Signal<string?> _error = new(null);

    public ProtectedVideoRequest? StartedWith;
    public int StartCalls, PlayCalls, PauseCalls, StopCalls, DisposeCalls;
    public long LastSeekMs = -1;
    public float LastVolume = 1f;
    public bool ReadyOnStart;
    public bool HasSurface { get; set; }
    public TaskCompletionSource<bool>? PlayAck, PauseAck, SeekAck;

    public IReadSignal<ProtectedVideoState> State => _state;
    public IReadSignal<long> PositionMs => _positionMs;
    public IReadSignal<long> DurationMs => _durationMs;
    public IReadSignal<Size2> NaturalSize => _naturalSize;
    public IReadSignal<string?> Error => _error;

    // Test scripting helpers.
    public void SetState(ProtectedVideoState s) => _state.Value = s;
    public void SetError(string? e) => _error.Value = e;
    public void SetNaturalSize(int w, int h) => _naturalSize.Value = new Size2(w, h);
    public void SetDurationMs(long ms) => _durationMs.Value = ms;
    public void SetPositionMs(long ms) => _positionMs.Value = ms;

    public void Start(ProtectedVideoRequest request)
    {
        StartCalls++;
        StartedWith = request;
        if (!request.StartPaused) PlayCalls++;
        if (ReadyOnStart) { HasSurface = true; _naturalSize.Value = new Size2(1280, 720); _state.Value = ProtectedVideoState.Playing; }
    }

    public ValueTask PlayAsync() { PlayCalls++; return PlayAck is { } ack ? new ValueTask(ack.Task) : ValueTask.CompletedTask; }
    public ValueTask PauseAsync() { PauseCalls++; return PauseAck is { } ack ? new ValueTask(ack.Task) : ValueTask.CompletedTask; }
    public ValueTask SeekAsync(long positionMs)
    {
        LastSeekMs = positionMs;
        return SeekAck is { } ack ? new ValueTask(ack.Task) : ValueTask.CompletedTask;
    }
    public void SetVolume(float volume) => LastVolume = volume;
    public void SetRate(float rate) { }
    public void Stop() => StopCalls++;
    public void Pump(in VideoBinding binding) { /* snapshot is driven by the test via the Set* helpers */ }
    public void Dispose() => DisposeCalls++;
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
