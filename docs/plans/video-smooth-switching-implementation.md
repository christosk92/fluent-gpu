# Smooth video switching — ENGINE implementation plan (fluent-gpu)

Sibling app plan: `C:\wavee\WaveeMusic\docs\plans\wavee\video-smooth-switching-implementation.md`. The app depends on
this landing first.

## Problem

Enabling video / switching tracks with video freezes the UI. Verified root causes:

- **E1** `VideoMediaEngine.Invoke<T>` (`VideoMediaEngine.cs:510-534`) marshals to the engine MTA thread and blocks up
  to 50 ms; `MfMediaSession.PumpVideo` makes up to 7 such calls per pump **on the UI thread in frame phase 7.2**,
  re-driven at 10 Hz by the poll timer while natural size is pending → up to ~300 ms per frame, sustained during open
  (MF holds its engine lock through source resolution). Transport verbs block the same way; `ConnectSignals` does 4 in
  a row.
- **E2** DRM first open implicitly `LoadLibrary`s `FluentGpu.PlayReady.Native.dll` + MF chain **on the UI thread**
  (`ProtectedMediaBackend.OpenAsync` is fully synchronous; first P/Invoke in `DesktopProtectedVideoPlayer.Start`
  :173-179).
- **E3** Full engine teardown (`MFShutdown`, 2 s join) + full rebuild (`MFStartup` + `D3D11CreateDevice` +
  `CoCreateInstance`) per track change; teardown races the successor's startup.
- **E4** `videoReady` flip remounts the hole/poster subtree per switch (`MediaPlayerElement.cs:625, 869-899`); plus
  per-render `List<Element>(8)`/`ToArray`.
- **E5** `MediaPlayer.OpenAsync` continuation writes ~6 signals into `MediaPlayerCore` from a pool thread
  (`MediaPlayer.cs:327-356`) — sole-writer violation.
- **E6** `DCompVideoPresenter.Check` throws into AppHost's device-lost catch; cold bind inline after Present.

## Cross-plan contract (pinned)

- **The switch API is `MediaPlayer.OpenAsync(source, opts)` on a long-lived player** — no new API. On a warm
  `MfMediaPlayer`, `OpenAsync` = SetSource on the warm engine. The app keeps one `MediaPlayer` per surface.
- **Per-source DRM payload travels on the source**: `DrmConfig.SourceDescriptor` (`object?`, SEEDED — already in
  `MediaSeams.cs`) carries the parsed `DashSourceDescriptor`; `ProtectedMediaBackend.BuildRequest` uses
  `drm.SourceDescriptor as DashSourceDescriptor ?? _descriptor`. One
  `MfMediaPlayer(new ProtectedMediaBackend(defaultRelay: null, descriptor: null))` plays every source family.
- DRM→DRM residual: native PlayReady stays create-per-open (bounded, off-UI, pre-warmed); clear↔clear is the truly
  instant path.
- `MfMediaPlayer.OpenAsync` no longer throws for an MF open failure — errors surface as typed `MediaError` via
  `Player.Error`. Cancellation still throws `OperationCanceledException` (manifest load).
- New `ProtectedMediaBackend.WarmupNative()` — the app calls it once at startup idle.

## 1. New seam: snapshot out, commands in (fixes E1)

The engine MTA thread is the ONLY thread that touches COM and the SOLE writer of a POD state snapshot; the UI thread
only reads the snapshot (seqlock, alloc-free — phase 7.2 constraint) and posts fire-and-forget coalesced commands.
`Invoke<T>`, `InvokeSlot<T>`, `InvokeSlotPool<T>`, `NativeSizeAnswer`, and every blocking property are **deleted
outright** — no fallback.

### 1.1 NEW `src/FluentGpu.Engine/Media/Playback/VideoEngineSeam.cs`

Namespace `FluentGpu.Media`; TerraFX-free and engine-free (VerticalSlice gates it headlessly; macOS reuses it).
Pinned type shapes:

```csharp
[Flags]
public enum VideoEngineFlags : uint
{
    None            = 0,
    MetadataLoaded  = 1 << 0,
    CanPlay         = 1 << 1,
    Playing         = 1 << 2,
    Seeking         = 1 << 3,
    Ended           = 1 << 4,
    Error           = 1 << 5,
    LiveSource      = 1 << 6,   // latched by the engine thread; never cleared within a source
    NaturalSizeKnown= 1 << 7,   // GetNativeVideoSize ANSWERED (Known + 0×0 == audio-only; !Known == still resolving)
    Faulted         = 1 << 8,   // unrecoverable bring-up failure — rebuild the engine, don't SetSource
}

public struct VideoEngineSnapshot
{
    public int SourceEpoch;            // which SetSource generation this state describes (stale-state guard)
    public int PresentationEpoch;      // FORMATCHANGE/RESOURCELOST counter
    public VideoEngineFlags Flags;
    public uint ReadyState;            // 0 HAVE_NOTHING … 4 HAVE_ENOUGH_DATA
    public uint NaturalW, NaturalH;    // valid iff NaturalSizeKnown
    public double DurationSeconds;     // 0 = unknown; live folded by the session as today
    public double PositionSeconds;     // clock sample …
    public long PositionTimestamp;     // … at this Stopwatch.GetTimestamp(); UI extrapolates pos + Δt·rate while Playing
    public double PlaybackRate;
    public double SeekableStart, SeekableEnd;   // DVR window; (0,0) = not answered
    public nuint SwapchainHandle;      // 0 until LOADEDMETADATA; re-queried on PresentationEpoch bumps
    public uint ErrorCode;             // MF_MEDIA_ENGINE_ERR
    public int ErrorHr;
}

/// Single-writer seqlock. Publish: engine thread only. Read: alloc-free, any thread (retry on torn read).
public sealed class VideoSnapshotBuffer
{
    private VideoEngineSnapshot _snap;
    private int _seq;   // even = stable, odd = write in progress
    public void Publish(in VideoEngineSnapshot s)
    { Interlocked.Increment(ref _seq); _snap = s; Interlocked.Increment(ref _seq); }
    public VideoEngineSnapshot Read()
    {
        while (true)
        {
            int s0 = Volatile.Read(ref _seq);
            if ((s0 & 1) != 0) { Thread.SpinWait(8); continue; }
            VideoEngineSnapshot copy = _snap;
            if (Volatile.Read(ref _seq) == s0) return copy;
        }
    }
}

public enum VideoCommandKind : byte
{
    Transport,    // A: 1 = play, 0 = pause
    Seek,         // A: seconds, I: 1 = approximate/keyframe
    Rate,         // A: rate
    Volume,       // A: 0..1
    Muted,        // I: 0/1
    Loop,         // I: 0/1
    StreamRect,   // I: w, J: h  (UpdateVideoStream dst)
    Repaint,      // no payload
    SetSource,    // Obj: url string, I: sourceEpoch (a superseded source is simply skipped)
    Detach,       // release-time source unload
}

/// One slot per kind, LAST-WINS coalescing; per-slot seqlock (payload never tears); Post is alloc-free;
/// Wake (set once by the engine) coalesces the engine-thread drain.
public sealed class VideoEngineCommandQueue
{
    private struct Slot { public int Seq; public int Pending; public double A; public int I, J; public object? Obj; }
    private const int KindCount = 10;
    private readonly Slot[] _slots = new Slot[KindCount];
    private int _wakeQueued;
    public Action? Wake;

    public void Post(VideoCommandKind kind, double a = 0, int i = 0, int j = 0, object? obj = null)
    {
        ref Slot s = ref _slots[(int)kind];
        Interlocked.Increment(ref s.Seq);
        s.A = a; s.I = i; s.J = j; s.Obj = obj;
        Interlocked.Increment(ref s.Seq);
        Volatile.Write(ref s.Pending, 1);
        if (Interlocked.Exchange(ref _wakeQueued, 1) == 0) Wake?.Invoke();
    }

    public bool TryTake(VideoCommandKind kind, out double a, out int i, out int j, out object? obj)
    {
        ref Slot s = ref _slots[(int)kind];
        a = 0; i = 0; j = 0; obj = null;
        if (Interlocked.Exchange(ref s.Pending, 0) == 0) return false;
        while (true)
        {
            int s0 = Volatile.Read(ref s.Seq);
            if ((s0 & 1) != 0) { Thread.SpinWait(8); continue; }
            a = s.A; i = s.I; j = s.J; obj = s.Obj;
            if (Volatile.Read(ref s.Seq) == s0) return true;
        }
    }

    /// Engine thread, top of a drain: re-open the wake gate so posts landing during the drain produce one more wake.
    public void BeginDrain() => Volatile.Write(ref _wakeQueued, 0);
}
```

### 1.2 `IVideoEngine` v2 (`src/FluentGpu.Windows/Media/IVideoEngine.cs`, rewritten)

```csharp
internal interface IVideoEngine : IDisposable
{
    /// Raised BY THE ENGINE THREAD after a snapshot publish whose significant fields changed (position-only ≤ ~1 Hz).
    /// Publish-then-raise ordering: a pump woken by this reads at least the state that raised it.
    event Action? StateChanged;
    /// Spin up the MTA thread + D3D11/DXGI/MediaEngine bring-up. NON-BLOCKING: failure = Faulted flag, never a sync hr.
    void Start();
    /// Post a source switch (SetSource on the live engine). Returns the new source epoch.
    int PostSetSource(string url);
    /// Post a source unload when the engine returns warm to the backend pool.
    void PostDetach();
    VideoEngineSnapshot Snapshot { get; }        // one seqlock read; alloc-free
    VideoEngineCommandQueue Commands { get; }
    bool CanPlayHls { get; }
}
```

Everything else (`NativeSizeAnswer`, blocking properties/methods, `Initialize`) is deleted from the interface.

### 1.3 `VideoMediaEngine` rewrite

- `Start()` replaces `Initialize(url)`; delete `_initDone` + the unbounded `Wait()` (:109). Bring-up failure publishes
  `Faulted` and parks.
- Main loop: `_work.TryTake(out w, RefreshIntervalMs())` (250 ms resolving/playing/live, 1000 ms parked) →
  `_commands.BeginDrain()` → `DrainCommands()` → run `w` → `RefreshAndPublishSnapshot()`.
- `DrainCommands()` maps each kind to the raw COM call that today lives inside an `Invoke` closure (same bodies).
  `SetSource`: stamp `_sourceEpoch`, reset per-source bits + `_naturalAnswered` + `_cachedHandle`, `SetSource(url)`
  (failure → error bits, code 4 SRC_NOT_SUPPORTED).
- `RefreshAndPublishSnapshot()` (engine thread only): direct COM reads — ReadyState, Duration, CurrentTime +
  `Stopwatch.GetTimestamp()`, Seekable (release the time range inline), Characteristics, `GetNativeVideoSize` only
  while `MetadataLoaded && !_naturalAnswered`, `GetVideoSwapchainHandle` only when metadata just loaded or
  presentation epoch changed (cached). `Publish` then raise `StateChanged` iff significant change (position-only at
  most ~1 Hz).
- `OnEngineEvent` (MF workers): only sets volatile bits + bumps `_presentationEpoch` + posts ONE coalesced refresh via
  a cached `Action` behind an interlocked flag; it no longer raises `StateChanged` (fixes the woken-before-readable
  ordering hole).
- Delete: `Invoke<T>` machinery, `InvokeTimeoutMs`, all blocking members, and the stale `FG_VIDEO_NODXGI` /
  `FG_VIDEO_NOWINDOWLESS` / `FG_VIDEO_NOVIDSUP` env toggles (no-env-var rule).
- `Dispose()` keeps `CompleteAdding` + `Join(2000)` — now once per backend lifetime, not per track.

### 1.4 `MfMediaSession` rewrite

- Delete the 10 Hz poll timer + the whole `_cached*` block; the engine's self-refresh replaces retries and live-window
  slides.
- Ctor: `internal MfMediaSession(IVideoEngine engine, int sourceEpoch, MediaOpenOptions opts, AdaptiveManifest? manifest = null, Action<IVideoEngine>? release = null)`.
- Transport verbs are pure posts + the same optimistic sink publishes (`PlayAsync` → `Commands.Post(Transport, i:1)`;
  Seek/Rate/Volume/Muted/GoLive likewise; `ConnectSignals` re-asserts as posts). Zero blocking calls remain.
- `PumpVideo` reads ONE snapshot per pump; `if (s.SourceEpoch != _sourceEpoch) return;`; position extrapolated
  (`pos + elapsed·rate` while Playing); StreamRect/Repaint via posts; `binding.Bind/SetContentSize/Place/SetVisible`
  unchanged; latch rules (never regress duration, latch live) stay as session fields fed from the snapshot.
- `DisposeAsync` non-blocking: unhook `StateChanged`, null the sink, then `_release?.Invoke(_engine)` (else
  `_engine.Dispose()`). No `Task.Run`, no join on the switch path.

### 1.5 `MfMediaPlayer`: warm-engine lease/return (fixes E3)

```csharp
public sealed class MfMediaPlayer : IMediaBackend, IAsyncDisposable
{
    private readonly object _engineLock = new();
    private IVideoEngine? _warm;
    private bool _leased;

    private IVideoEngine LeaseEngine()
    {
        lock (_engineLock)
        {
            bool faulted = _warm is { } w && (w.Snapshot.Flags & VideoEngineFlags.Faulted) != 0;
            if (_warm is null || faulted) { _warm?.Dispose(); _warm = _engineFactory(); _warm.Start(); }
            if (_leased) { var extra = _engineFactory(); extra.Start(); return extra; }  // 2nd concurrent player: throwaway
            _leased = true;
            return _warm!;
        }
    }

    private void ReturnEngine(IVideoEngine engine)   // from MfMediaSession.DisposeAsync (any thread; non-blocking)
    {
        lock (_engineLock)
        {
            if (!ReferenceEquals(engine, _warm)) { engine.Dispose(); return; }
            _leased = false;
            engine.Commands.Post(VideoCommandKind.Transport, i: 0);
            engine.PostDetach();
        }
    }

    public async ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
    {
        // DRM route + manifest load: unchanged shape.
        IVideoEngine engine = LeaseEngine();
        int epoch = engine.PostSetSource(url);       // fire-and-forget; NO Task.Run, NO block, NO sync throw
        engine.Commands.Post(VideoCommandKind.Loop, i: IsInfiniteLoop(source) ? 1 : 0);
        if (opts.StartPaused) engine.Commands.Post(VideoCommandKind.Transport, i: 0);
        return new MfMediaSession(engine, epoch, opts, manifest, ReturnEngine);
    }
}
```

Sequencing: `MediaPlayer.OpenAsync` already awaits old-session `DisposeAsync` before `backend.OpenAsync` →
`ReturnEngine` happens-before the next `LeaseEngine`. Per-source state machine (engine thread): `Idle → SetSource(N)
→ Resolving(N) → Ready(N) → SetSource(N+1) → Resolving(N+1)` (live switch, no teardown); media ERROR → next
SetSource clears + retries same engine; bring-up/Shutdown failure → sticky `Faulted` → rebuild at next lease.
`SetSource(null)` failure on Detach: log + ignore. Swapchain handle: cleared at SetSource, re-queried at new
LOADEDMETADATA; same handle → registry value-gates (no rebind); new handle → registry `Drain` rebinds atomically at
the next Commit.

## 2. `MediaPlayer` facade: UI-thread marshaling (fixes E5)

```csharp
// after: var session = await backend.OpenAsync(source, opts, ct).ConfigureAwait(false);
await OnUiAsync(() =>
{
    _session = session; _currentKind = kind;
    AttachVideoPumpSource(session);
    session.ConnectSignals(_sink);
    RequestVideoPump();
}).ConfigureAwait(false);

private static ValueTask OnUiAsync(Action action)
{
    var post = FluentGpu.Hooks.HostDispatch.Current;      // process-static UI poster
    if (post is null) { action(); return ValueTask.CompletedTask; }   // headless: inline
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    post(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } });
    return new ValueTask(tcs.Task);
}
```

The catch-arm `SetError/SetState` and `LoadExternalSubtitlesAsync`'s core writes get the same treatment.

## 3. `MediaPlayerElement`: no remount on source switch (fixes E4)

Fixed keyed child shape that never changes across a switch: `media-stage` (letterbox, constant per element) →
`media-hole` (**always mounted**; `VideoHole` toggles as a PROP) → `media-poster` (**always mounted**; visibility =
opacity crossfade via the existing `SeedEased` idiom — honors reduced-motion) → status → captions. A `hadVideo`
`UseRef` latch keeps the hole active across Opening; the poster (later sibling → painter-order covers the DestOut
erase at opacity 1) crossfades out when the new first frame arrives. Delete `PosterMotion`. `IsDecorative` keeps
`VideoHole = videoReady` (no latch — a decorative clip must never erase the caller's still). Replace the per-render
`List<Element>` builds with fixed `Element[]`. Natural-size change = prop patch + one pump, never a remount.

## 4. DRM preload (fixes E2)

`DesktopProtectedVideoPlayer`: move `FgPlayReadyResetAdaptive` + the transport-seed first-P/Invokes from `Start()` to
the top of `RunNative` (the `fgpu-playready-desktop` worker — the implicit LoadLibrary lands there; the
seed-before-RunEx ordering is preserved). Add idempotent `static Warmup()` → `NativeLibrary.TryLoad` on a pool thread.
`ProtectedMediaBackend.WarmupNative()` exposes it. `BuildRequest` uses
`drm.SourceDescriptor as DashSourceDescriptor ?? _descriptor` (per-open descriptor).

## 5. DComp presenter never throws (fixes E6)

`DCompVideoPresenter.Check` → non-throwing `Ok(hr, what)` (Diag log once per distinct hr per slot); failing placement
marks + skips; `CreateSurface` returns none on failure; `VideoSurfaceRegistry.Drain` keeps a failed slot dirty and
retries next drain; delete the `catch … when (s_diag)`; replace `FG_DRM_DIAG` with always-on `Diag`-gated lines.
`BindSurfaceHandle` failure self-heals via the next presentation-epoch handle change.

## 6. Verification

- **Windows.Tests**: fake `IVideoEngine` v2 (scripted `VideoSnapshotBuffer` + real `VideoEngineCommandQueue`); tests:
  stale-epoch snapshot ignored; FORMATCHANGE epoch → handle drop → rebind; lease/return + `Faulted` rebuild;
  last-wins transport; `ConnectSignals` writes land via `HostDispatch.Current`.
- **VerticalSlice** NEW `Suites/MediaSeamSuite.cs` (+ registry): `gate.media.seam.snapshot-alloc-free` (0 alloc across
  10k reads), `.snapshot-no-tear`, `.command-coalesce`, `.command-last-wins-transport`, `.wake-coalesce`.
- **ControlsSuite** media block: `gate.media.el.no-remount-on-source-switch` (hole+poster NodeHandles identical and
  live across a switch), `gate.media.el.poster-overlay`; update `gate.media.el.video-knockout/-poster` expectations
  (they pin the OLD hole-gating).
- Orchestrator: `dotnet build src/FluentGpu.slnx` Debug + Release; VerticalSlice "ALL CHECKS PASSED";
  `check-canon.ps1` exit 0 after updating `docs/design/subsystems/media-pipeline.md` (§8.3 snapshot/command seam +
  warm reuse; §8.1 presenter fault semantics; §8.4 DRM warmup); gallery MediaLabPage: 10× rapid source switch,
  volume-key hold during load, scrub during Opening, cold-start DRM clip.

## 7. Work split (disjoint files; only the orchestrator builds/tests)

| Agent | Files | Depends on |
|---|---|---|
| A — seam types + gates | NEW `Engine/Media/Playback/VideoEngineSeam.cs`; NEW `VerticalSlice/Suites/MediaSeamSuite.cs`; `VerticalSlice/Harness/SuiteRegistry.cs` | — |
| B — MF backend | `Windows/Media/{IVideoEngine,VideoMediaEngine,MfMediaSession,MfMediaPlayer}.cs`; `Windows.Tests/{Fakes,MfMediaSessionTests,MfMediaPlayerTests,LiveSessionTests}.cs` | A types (pinned above) |
| C — facade marshal | `Engine/Media/Playback/MediaPlayer.cs` | — |
| D — element | `Controls/Media/MediaPlayerElement.cs`; `VerticalSlice/Suites/ControlsSuite.cs` media block | — |
| E — DRM preload | `WindowsApi/Media/PlayReady/{DesktopProtectedVideoPlayer,ProtectedMediaBackend}.cs` | — |
| F — presenter/registry/canon | `Windows/Pal/DCompVideoPresenter.cs`; `Engine/Media/Playback/VideoSurfaceRegistry.cs`; `docs/design/subsystems/media-pipeline.md` | — |

## 8. Risks

Swapchain-handle lifetime across repeated queries (cache per source/epoch; 50-switch soak); `SetSource(null)` may fail
on some MF builds (tolerated); container-family switches (MP4→HLS→DASH) may hit byte-stream-handler edges
(Errored→retry→Faulted→rebuild is the containment); old `gate.media.el.*` expectations must be updated; a translucent
author `PosterContent` now sits over the erase (documented change); two `MediaPlayer`s over one backend = warm +
throwaway engines (documented decision).
