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

    /// <summary>Runs inside <see cref="Dispose"/> before it is counted — a test blocks here to model the real engine's
    /// slow dispose (the MTA thread join plus MF shutdown) and asserts who waited for it.</summary>
    public Action? OnDispose;

    public void Dispose() { OnDispose?.Invoke(); DisposeCalls++; }

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
    /// <summary>Mirrors <see cref="VideoEngineFlags.Waiting"/> (a mid-playback stall: the engine is starved while Playing).</summary>
    public bool Waiting { get => (_s.Flags & VideoEngineFlags.Waiting) != 0; set => SetFlag(VideoEngineFlags.Waiting, value); }

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
    /// <summary>The engine's first-frame stamp for the current source (0 until the first frame lands — the real engine
    /// clears it per source). Setting it publishes a fresh snapshot but does NOT wake the session: pair it with
    /// <see cref="RaiseStateChanged"/> where the wake matters.</summary>
    public long FirstFrameTimestamp { get => _s.FirstFrameTimestamp; set { _s.FirstFrameTimestamp = value; Publish(); } }
    /// <summary>The engine's SEEKED counter (see <see cref="VideoEngineSnapshot.SeekedCount"/>). Setting it publishes a
    /// fresh snapshot but does NOT wake the session: pair it with <see cref="RaiseStateChanged"/>.</summary>
    public int SeekedCount { get => _s.SeekedCount; set { _s.SeekedCount = value; Publish(); } }
    /// <summary>The stream size the engine reports as APPLIED (see <see cref="VideoEngineSnapshot.StreamW"/>). Nothing drains
    /// the command queue here, so a test models the engine's echo of a <see cref="VideoCommandKind.StreamRect"/> itself with
    /// <see cref="EchoStreamRect"/>.</summary>
    public (uint W, uint H) AppliedStream { get => (_s.StreamW, _s.StreamH); set { _s.StreamW = value.W; _s.StreamH = value.H; Publish(); } }
    /// <summary>The renderer's rendered/dropped frame counters the snapshot carries (F066). Setting them publishes a fresh snapshot but
    /// does NOT wake the session: pair it with <see cref="RaiseStateChanged"/> where the wake matters.</summary>
    public (long Rendered, long Dropped) Frames { get => (_s.FramesRendered, _s.FramesDropped); set { _s.FramesRendered = value.Rendered; _s.FramesDropped = value.Dropped; Publish(); } }
    /// <summary>Echo a stream size as applied and wake the session, as the engine's refresh does after a StreamRect.</summary>
    public void EchoStreamRect(int w, int h) { AppliedStream = ((uint)w, (uint)h); RaiseStateChanged(); }

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
    public int FailBinds;   // the next N BindSurfaceHandle calls fail (return false), modelling a transient DComp failure
    public VideoSurfaceId LastCreated;
    public nuint LastBoundHandle;
    public RectF LastPlaceRect;
    public RectF LastViewport;
    public uint LastContentW, LastContentH;
    public bool LastVisible;
    public int Commits;
    public int Applies;              // ApplyPending calls: a deferred-commit drain applies here and leaves the ONE device commit to the host
    public bool CanAttach = true;    // false models a target whose first Present has not bound its composition graph yet
    public bool OverlaySupported;    // models an output whose overlay probe reported a plane (IVideoPresenter.SupportsOverlay, F087)
    public bool? LastOverlay;        // the last SetOverlay argument (null = never called): true = the visual was inserted ABOVE the UI visual

    public VideoSurfaceId CreateSurface()
    {
        var id = new VideoSurfaceId((uint)NextId++);
        LastCreated = id;
        Calls.Add($"Create({id.Value})");
        return id;
    }

    public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
    {
        if (FailBinds > 0)
        {
            FailBinds--;
            Calls.Add($"BindFail({id.Value},0x{dcompSurfaceHandle:X})");
            return false;
        }
        LastBoundHandle = dcompSurfaceHandle;
        Calls.Add($"Bind({id.Value},0x{dcompSurfaceHandle:X})");
        return true;
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
    public bool SupportsOverlay => OverlaySupported;
    public void SetOverlay(VideoSurfaceId id, bool above) { LastOverlay = above; Calls.Add($"Overlay({id.Value},{above})"); }
    public void Destroy(VideoSurfaceId id) => Calls.Add($"Destroy({id.Value})");
    public void ApplyPending() => Applies++;
    public bool CanAttachSurfaces => CanAttach;
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
/// <see cref="IsSeeking"/>), and <see cref="Bind"/> binds <see cref="SurfaceHandle"/> through the binding it is given on
/// EVERY call (recording the token) so a placement move is observable at the registry; <see cref="Pump()"/> is the state
/// half and never touches a binding.</para>
/// <para><see cref="ReadyOnPrefetch"/> makes <see cref="PrefetchAsync"/> land 4 s of forward media when it completes; <see cref="PrefetchResult"/> (null ⇒
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
    public bool ReadyOnPrefetch;
    /// <summary>Holds <see cref="PrefetchAsync"/> open until the test completes it; null ⇒ the prefetch completes at once.</summary>
    public TaskCompletionSource? PrefetchResult;
    /// <summary>The scripted keyframe table (ascending ms) <see cref="GetKeyframes"/> copies out.</summary>
    public long[] Keyframes = Array.Empty<long>();
    /// <summary>The scripted buffered ranges as flattened (start, end) ms pairs <see cref="GetBuffered"/> copies out.</summary>
    public long[] Buffered = Array.Empty<long>();
    /// <summary>The swap-chain handle <see cref="Bind"/> binds through the binding while <see cref="HasSurface"/> (0 = none).</summary>
    public nuint SurfaceHandle;
    /// <summary>Every binding token <see cref="Bind"/> was called with, in order (0 for an inert/default binding): the
    /// surface half of the pump. <see cref="PumpCalls"/> counts the state half.</summary>
    public readonly List<int> PumpedTokens = new();
    public string? LastSelectedRepresentationId;
    /// <summary>The retain window of the last <see cref="SelectVideoRepresentationAsync"/> (-1 = append at the buffer end).</summary>
    public int LastSelectedRetainMs = IProtectedVideoPlayer.AppendAtBufferEnd;
    /// <summary>Every selection in order: the representation id and the retain window it asked for.</summary>
    public readonly List<(string Id, int RetainMs)> Selections = new();
    /// <summary>True (the default): a selection takes effect at once - the active representation becomes the requested one.
    /// False: the selection is only recorded, and the test moves <see cref="DownloadingVideoRepresentationId"/> /
    /// <see cref="ActiveVideoRepresentationId"/> itself to model a switch that lands (and shows) later.</summary>
    public bool ApplySelectionImmediately = true;

    public bool SupportsAdaptiveSelection { get; set; }
    public string? ActiveVideoRepresentationId { get; set; }
    public string? DownloadingVideoRepresentationId { get; set; }
    public bool HasSurface { get; set; }
    public ProtectedVideoPhase Phase { get; set; }
    public long FirstFrameEpoch { get; set; }
    /// <summary>The native first-frame QPC (F215); 0 = none, like a player with no native clock.</summary>
    public long FirstFrameQpc { get; set; }
    private bool? _hasFirstFrame;
    /// <summary>Whether THIS attach has presented its first frame. Unless a test sets it, it follows <see cref="FirstFrameEpoch"/>
    /// (non-zero = presented); setting it models a detach (false) or a re-attach before its first frame.</summary>
    public bool HasFirstFrame { get => _hasFirstFrame ?? FirstFrameEpoch != 0; set => _hasFirstFrame = value; }
    public long PositionQpc { get; set; }
    public bool IsSeeking { get; set; }
    public long LastSeekLandedMs { get; set; } = -1;
    public long ForwardBufferedMs { get; set; }
    public long RetainedBehindMs { get; set; }
    public int IndexEpoch { get; set; }
    public long BytesDownloaded { get; set; }
    public long DownloadElapsedMs { get; set; }
    /// <summary>The native renderer's frame counters (F066), as of the last pump.</summary>
    public long FramesRendered { get; set; }
    public long FramesDropped { get; set; }

    public IReadSignal<ProtectedVideoState> State => _state;
    public IReadSignal<long> PositionMs => _positionMs;
    public IReadSignal<long> DurationMs => _durationMs;
    public IReadSignal<Size2> NaturalSize => _naturalSize;
    public IReadSignal<string?> Error => _error;
    /// <summary>The HRESULT behind <see cref="Error"/> the player reports (0 = none).</summary>
    public int ErrorHr { get; set; }
    /// <summary>Whether the scripted <see cref="Error"/> is one a fresh runtime cures (the runtime was replaced).</summary>
    public bool ErrorNeedsRuntimeRebuild { get; set; }

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

    public ValueTask SelectVideoRepresentationAsync(string representationId, int retainMs = IProtectedVideoPlayer.AppendAtBufferEnd)
    {
        SelectRepresentationCalls++;
        LastSelectedRepresentationId = representationId;
        LastSelectedRetainMs = retainMs;
        Selections.Add((representationId, retainMs));
        if (ApplySelectionImmediately) ActiveVideoRepresentationId = representationId;
        return ValueTask.CompletedTask;
    }

    public void SetVolume(float volume) => LastVolume = volume;
    public void SetRate(float rate) => LastRate = rate;
    public void SetStreamSize(SizeI size) { SetStreamSizeCalls++; LastStreamSize = size; }
    /// <summary>The (registry token, host ordinal) of every attributed <see cref="SetStreamSize(SizeI, int, int)"/> request, in order (F235).</summary>
    public readonly List<(int Token, int Host)> StreamSizeTags = new();
    public void SetStreamSize(SizeI size, int token, int host) { StreamSizeTags.Add((token, host)); SetStreamSize(size); }
    /// <summary>True (the default): the native echo follows <see cref="SetStreamSize"/> at once, as the real runtime's snapshot
    /// does after a pump. False: <see cref="ScriptedAppliedStreamSize"/> is what the snapshot reports, so a test holds an echo back.</summary>
    public bool EchoesStreamSize = true;
    /// <summary>The applied stream size reported while <see cref="EchoesStreamSize"/> is false.</summary>
    public SizeI ScriptedAppliedStreamSize;
    public SizeI AppliedStreamSize => EchoesStreamSize ? LastStreamSize : ScriptedAppliedStreamSize;
    /// <summary>Every OPM window placement the session asked for, in order (host window, then the rect in device px).</summary>
    public readonly List<(nuint Host, int Left, int Top, int Right, int Bottom)> OutputProtectionPlacements = new();
    public void PlaceOutputProtectionWindow(nuint hostWindow, int left, int top, int right, int bottom)
        => OutputProtectionPlacements.Add((hostWindow, left, top, right, bottom));
    public void Stop() => StopCalls++;
    public void LogDiagnostic(string message) { lock (_diagnostics) _diagnostics.Add(message); }

    /// <summary>The state half: counted, and it touches no binding (a pump with no element must never need one).</summary>
    public void Pump() => PumpCalls++;

    /// <summary>The surface half: the handle is handed to the binding EVERY time, as production does.</summary>
    public void Bind(in VideoBinding binding)
    {
        PumpedTokens.Add(binding.Token);
        if (HasSurface && SurfaceHandle != 0) binding.Bind(SurfaceHandle);
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
    private readonly List<long> _luids = new();
    private readonly List<int> _formatsAtCreate = new();
    private readonly HashSet<ulong> _live = new();                 // key sessions the "native table" still holds
    private readonly Dictionary<ulong, int> _states = new();       // FgPrLicenseState overrides (a kill, an expiry)
    private ulong _nextHandle = 0x100;
    private int _creates, _destroys, _acquires, _releases, _stateProbes;

    /// <summary>Whether the "DLL" loads (<see cref="IPrRuntimeNative.IsAvailable"/>).</summary>
    public bool Available { get; set; } = true;
    /// <summary>A negative HRESULT forces <see cref="RuntimeCreate"/> to fail (0 = succeed).</summary>
    public int CreateHr { get; set; }
    /// <summary>A negative HRESULT forces <see cref="LicenseAcquire"/> to fail with no handle (0 = succeed).</summary>
    public int AcquireHr { get; set; }
    /// <summary>Runs INSIDE <see cref="LicenseAcquire"/> with the handle about to be returned and the KID, before the call
    /// returns — models a license event racing the runtime's handle assignment. May throw to model a native fault.</summary>
    public Action<ulong, string>? DuringAcquire { get; set; }
    /// <summary>Runs INSIDE <see cref="RuntimeDestroy"/> (after it is recorded, outside the fake's lock) with the runtime handle
    /// being destroyed - blocks to model the native join, which a racing <c>Acquire</c> must wait out.</summary>
    public Action<ulong>? DuringDestroy { get; set; }
    /// <summary>Runs INSIDE <see cref="LicenseState"/> (outside the fake's lock) with the probed handle - may throw to model an
    /// unanswerable native probe, which must never condemn a license.</summary>
    public Action<ulong>? DuringStateProbe { get; set; }

    /// <summary>The adapter LUID of the last <see cref="RuntimeCreate"/> (0 = default adapter), and of every create in order.</summary>
    public long LastAdapterLuid { get; private set; }
    public long[] AdapterLuids { get { lock (_gate) return _luids.ToArray(); } }

    /// <summary>The output format the runtime asked for before its last create (<c>FgPrRuntimeSetVideoOutputFormat</c>; 0 = BGRA, 1 = NV12) -
    /// the value in force at each <see cref="RuntimeCreate"/>, in order, and the last one set (0 until a set).</summary>
    public int LastVideoOutputFormat { get; private set; }
    public int[] VideoOutputFormatsAtCreate { get { lock (_gate) return _formatsAtCreate.ToArray(); } }

    /// <summary>Completes on the first <see cref="RuntimeDestroy"/> — a test awaits it with a bounded timeout instead of
    /// polling the runtime's warm-idle teardown (which runs on a timer thread).</summary>
    public TaskCompletionSource Destroyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CreateCount { get { lock (_gate) return _creates; } }
    public int DestroyCount { get { lock (_gate) return _destroys; } }
    public int AcquireCount { get { lock (_gate) return _acquires; } }
    public int ReleaseCount { get { lock (_gate) return _releases; } }
    /// <summary>How many times the runtime asked <see cref="LicenseState"/> (the stage-A "does native still hold this handle" probe).</summary>
    public int StateProbeCount { get { lock (_gate) return _stateProbes; } }
    public ulong LastRuntime { get; private set; }
    public string? LastStorePath { get; private set; }

    /// <summary>Every call in order: <c>create:{rt}</c>, <c>destroy:{rt}</c>, <c>acquire:{kid}:{lic}</c>, <c>release:{lic}</c>.</summary>
    public string[] Calls { get { lock (_gate) return _calls.ToArray(); } }
    public ulong[] Released { get { lock (_gate) return _released.ToArray(); } }
    public (string Kid, ulong License)[] Acquired { get { lock (_gate) return _acquired.ToArray(); } }

    public bool IsAvailable => Available;

    public void SetVideoOutputFormat(int format)
    {
        lock (_gate) LastVideoOutputFormat = format;
    }

    public int RuntimeCreate(string storePath, nint ctx, long adapterLuid, out ulong runtime)
    {
        lock (_gate)
        {
            _creates++;
            LastStorePath = storePath;
            LastAdapterLuid = adapterLuid;
            _luids.Add(adapterLuid);
            _formatsAtCreate.Add(LastVideoOutputFormat);
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
        DuringDestroy?.Invoke(runtime);
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
            _live.Add(lic);
            _acquired.Add((kid, lic));
            _calls.Add("acquire:" + kid + ":" + lic);
        }
        DuringAcquire?.Invoke(lic, kid);   // outside the lock: the callback re-enters the runtime
        license = lic;
        return 0;
    }

    public int LicenseState(ulong runtime, ulong license)
    {
        DuringStateProbe?.Invoke(license);
        lock (_gate)
        {
            _stateProbes++;
            if (_states.TryGetValue(license, out int forced)) return forced;
            return _live.Contains(license) ? 0 : unchecked((int)0x80070006);   // pending, or E_HANDLE for a key session native no longer holds
        }
    }

    /// <summary>Models the native table closing <paramref name="license"/>'s key session (its LRU, an eviction after a kill): the
    /// handle is unknown from now on, so <see cref="LicenseState"/> answers E_HANDLE. The test raises <c>EvLicenseEvicted</c> itself
    /// when it wants the event half.</summary>
    public void NativeCloses(ulong license)
    {
        lock (_gate) _live.Remove(license);
    }

    /// <summary>Makes <see cref="LicenseState"/> answer <paramref name="state"/> for <paramref name="license"/> (negative = failed,
    /// 2 = expired), whatever the fake otherwise holds.</summary>
    public void SetLicenseState(ulong license, int state)
    {
        lock (_gate) _states[license] = state;
    }

    public void LicenseRelease(ulong runtime, ulong license)
    {
        lock (_gate)
        {
            _releases++;
            _released.Add(license);
            _live.Remove(license);
            _calls.Add("release:" + license);
        }
    }
}

/// <summary>
/// A recording <see cref="IPrSessionNative"/> — every <c>FgPrSession*</c> call <see cref="ProtectedVideoSession"/> makes,
/// with no DLL. Calls are recorded in order as short strings (<c>create:{s}</c>, <c>attach:{s}:{lic}</c>,
/// <c>seek:{s}:{ms}:{mode}:{kf}</c>, <c>play:{s}</c>, …). The snapshot a pump reads is <see cref="Snapshot"/> (a test
/// edits it in place); <see cref="DuringPrefetch"/> runs inside the prefetch call to model the feeder answering at once.
/// </summary>
internal sealed class FakeSessionNative : IPrSessionNative
{
    private readonly object _gate = new();
    private readonly List<string> _calls = new();
    private ulong _next = 0x5000;

    /// <summary>A negative HRESULT fails <see cref="SessionCreate"/> with no handle.</summary>
    public int CreateHr { get; set; }
    /// <summary>A negative HRESULT fails <see cref="SessionAttach"/>.</summary>
    public int AttachHr { get; set; }
    /// <summary>A negative HRESULT fails <see cref="SessionPrefetch"/>.</summary>
    public int PrefetchHr { get; set; }
    /// <summary>A negative HRESULT fails <see cref="SessionSnapshot"/> (what a stale handle - a session whose runtime was
    /// replaced - answers with E_HANDLE); the snapshot is then left untouched.</summary>
    public int SnapshotHr { get; set; }
    /// <summary>What the last create was given.</summary>
    public PrOpenDescription? LastOpen { get; private set; }
    /// <summary>The snapshot every <see cref="SessionSnapshot"/> returns.</summary>
    public PrNative.Snapshot Snapshot;
    /// <summary>The init protection <see cref="SessionGetInitProtection"/> reports (empty PSSH ⇒ "not parsed yet").</summary>
    public byte[] InitPssh = Array.Empty<byte>();
    public string? InitKid;
    /// <summary>Runs inside <see cref="SessionPrefetch"/> with the session handle, before the call returns.</summary>
    public Action<ulong>? DuringPrefetch { get; set; }

    public string[] Calls { get { lock (_gate) return _calls.ToArray(); } }
    public int CountOf(string verb) { lock (_gate) return _calls.FindAll(c => c.StartsWith(verb + ":", StringComparison.Ordinal)).Count; }

    private int Record(string call, int hr = 0) { lock (_gate) _calls.Add(call); return hr; }

    public int SessionCreate(ulong runtime, PrOpenDescription desc, out ulong session)
    {
        lock (_gate)
        {
            LastOpen = desc;
            if (CreateHr < 0) { session = 0; _calls.Add("create-failed"); return CreateHr; }
            session = _next++;
            _calls.Add("create:" + session);
            return 0;
        }
    }

    public int SessionPrefetch(ulong runtime, ulong session, long aroundMs, int segments)
    {
        Record($"prefetch:{session}:{aroundMs}:{segments}");
        if (PrefetchHr < 0) return PrefetchHr;
        DuringPrefetch?.Invoke(session);
        return 0;
    }

    public int SessionAttach(ulong runtime, ulong session, ulong license) => Record($"attach:{session}:{license}", AttachHr);
    public int SessionDetach(ulong runtime, ulong session) => Record($"detach:{session}");
    public void SessionDestroy(ulong runtime, ulong session) => Record($"destroy:{session}");
    public int SessionPlay(ulong runtime, ulong session) => Record($"play:{session}");
    public int SessionPause(ulong runtime, ulong session) => Record($"pause:{session}");
    public int SessionSeek(ulong runtime, ulong session, long targetMs, int mode, long keyframeMs)
        => Record($"seek:{session}:{targetMs}:{mode}:{keyframeMs}");
    public int SessionSetVolume(ulong runtime, ulong session, double volume) => Record($"volume:{session}:{volume}");
    public int SessionSetRate(ulong runtime, ulong session, double rate) => Record($"rate:{session}:{rate}");
    public int SessionSetStreamSize(ulong runtime, ulong session, int width, int height) => Record($"size:{session}:{width}x{height}");
    /// <summary>What <see cref="SessionPlaceOpmWindow"/> answers (0 = S_OK posted; 1 = S_FALSE, not the attached session; negative = failed).</summary>
    public int PlaceOpmHr { get; set; }
    /// <summary>Every OPM window placement, in order: the session, the host window handle and the client-area rect.</summary>
    public readonly List<(ulong Session, ulong Host, int Left, int Top, int Right, int Bottom)> OpmPlacements = new();
    public int SessionPlaceOpmWindow(ulong runtime, ulong session, ulong hostWindow, int left, int top, int right, int bottom)
    {
        lock (_gate) OpmPlacements.Add((session, hostWindow, left, top, right, bottom));
        return Record($"opm:{session}:{hostWindow}:{left},{top},{right},{bottom}", PlaceOpmHr);
    }
    /// <summary>The retain window of the last <see cref="SessionSelectRepresentation"/> (-1 = append at the buffer end).</summary>
    public int LastRetainMs { get; private set; } = -1;

    public int SessionSelectRepresentation(ulong runtime, ulong session, int index, string initUrl, string? baseUrl,
                                           string? prefix, string? suffix, int retainMs)
    {
        lock (_gate) LastRetainMs = retainMs;
        return Record($"rep:{session}:{index}");
    }

    // The pump's hot calls record nothing: the allocation gate measures them.
    public int SessionSnapshot(ulong runtime, ulong session, ref PrNative.Snapshot snapshot)
    {
        if (SnapshotHr < 0) return SnapshotHr;
        snapshot = Snapshot;
        return 0;
    }

    public int SessionGetKeyframes(ulong runtime, ulong session, Span<long> into) => 0;
    public int SessionGetBuffered(ulong runtime, ulong session, Span<long> pairs) => 0;

    public int SessionGetInitProtection(ulong runtime, ulong session, Span<byte> pssh, Span<char> kid)
    {
        Record($"initprotection:{session}");
        if (InitPssh.Length == 0) return 0;
        InitPssh.AsSpan(0, Math.Min(InitPssh.Length, pssh.Length)).CopyTo(pssh);
        if (InitKid is { } k && kid.Length > k.Length) { k.AsSpan().CopyTo(kid); kid[k.Length] = '\0'; }
        return InitPssh.Length;
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
