# FluentGpu Media Playback — master API spec (unified audio + video)

**Status:** LANDED — M0–M5 implemented and green (Engine.Tests 91, Windows.Tests 59; solution builds clean),
as of 2026-07-19. This remains the single source of truth for the public media surface; §9.2/§16/§17 are
reconciled to the shipped reality (native in-process PlayReady is the DRM path via the managed `WithDrm`
relay). M6 (WaveeMusic app migration) is app-side and pending.
**Scope:** the *public API* and its two backends. This folds two completed research/design workflows
(video-centric unified-API synthesis + the audio-pillar DSP/mix design) and the established engine
decisions into one surface a team can implement against.
**Owner docs to reconcile on landing:** `design/subsystems/media-pipeline.md` (video present-tree §8,
lyrics playback clock §9), `design/hardened-v1-plan.md` (§2 threading, §4.1 PUBLISH→consume seam,
§6 build order). Register the new cross-cutting seams in `design/SPEC-INDEX.md §2` + the
`design/subsystems/README.md` ownership map, then run `design/check-canon.ps1`.
**Depends on (PROVEN, in `src/` today):** `IVideoPresenter` PAL seam
(`src/FluentGpu.Engine/Seams/Pal/IVideoPresenter.cs`), the working `VideoMediaEngine`
(`src/FluentGpu.Windows/Media/VideoMediaEngine.cs`, `IMFMediaEngineEx` windowless swapchain), the
signals core (`Signal<T>`/`FloatSignal`/`IReadSignal<T>`/`Memo<T>`/`Prop<T>` in
`src/FluentGpu.Engine/Foundation/Signals/`), the reconciler + hooks model, and the app's existing
`AudioPlayEngine`/`CrossfadeMixer` (fenced `app/` side — re-parented, never edited by the engine).

Every major decision cites the frustration it fixes (**FIX:**) or the strength it steals (**STEAL:**)
from the cross-API survey, and marks **PROVEN** (shipping code today) vs **NET-NEW** (this spec).

---

## 1. Overview, goals, non-goals, thesis

**Thesis.** *One headless `IMediaPlayer` — state as signals, transport as idempotent verbs — fulfilled by
two backends behind a router: a Media-Foundation backend (video + self-contained A/V files, decoded frames
composited by the existing `IVideoPresenter` DirectComposition spine, DRM via a CDM) and a custom PCM
audio-graph backend (Spotify/PlayPlay, crossfade, EQ, gapless, clocked by the WASAPI device). The engine
touches no video pixels and the device clock is the only clock. `player.Play(uri)` is the whole 90% case;
every powerful knob is an override you never see until you need it.*

**Goals.**
- A single headless playback contract usable with **zero UI attached** (harness-drivable, macOS-portable).
- State observed through **signals**, not events / KVO / `readyState` polling.
- **Pluggable backends** behind one seam; a router picks by source kind. The app binds signals identically
  regardless of which backend is playing.
- First-class **preroll + multiple concurrent voices** (crossfade/gapless need the next track decoding
  before the current ends).
- Custom byte-streams through **one seam** with the engine owning backpressure/buffering/thread-firewalling;
  **PlayPlay is a `DecryptingSource` decorator** over an app-supplied plaintext-on-read source.
- **NativeAOT-clean, near-zero-alloc, signals-first, portable** (Windows now; macOS later behind the same seams).
- Replace the current chrome-only `MediaPlayerElement` mockup with a real control bound to a headless player.

**Non-goals (v1).**
- MF (video) audio does **not** get graph effects (no EQ/crossfade on music-video audio) — the two engines
  never co-mix, so there is no cross-backend A/V PCM sync problem to solve.
- Cross-backend frame-synced multi-player (`MediaTimelineController` equivalent) — designed-in as orthogonal,
  deferred to a follow-up.
- Casting is a URL/relay-only story; raw callback bytes (`FromPull`/`FromFeed`) are not castable without a
  documented local HTTP shim.
- PlayPlay/AudioHost internals — the engine designs only the seam they plug into.
- Widevine-only content — **out of v1 scope**; native in-process PlayReady is the shipped v1 DRM path (§9.2,
  **SOLVED/SHIPPED** as of M5), and Widevine-via-WebView2 is only an *optional later fallback* for
  genuinely Widevine-only streams, not a v1 requirement.

---

## 2. Unified design principles (ranked, deduped from both syntheses)

The two workflows produced ~19 principles; below is the reconciled, ranked set. Each names the incumbent
pain it fixes or the strength it steals.

1. **Headless engine, swappable view — playback never references a `Control`.** The single best idea across
   the survey (WinUI `MediaPlayer`→`MediaPlayerElement`, AVFoundation `AVPlayer`→`AVPlayerLayer`, ExoPlayer's
   one `Player`). The FluentGpu control, the SMTC bridge, and any cast backend are all consumers of the same
   small `IMediaPlayer`. **This is exactly the seam a GPU UI engine wants** — the harness drives it headlessly,
   macOS re-hosts it unchanged.
2. **The device clock is the only clock.** Position, crossfade start, gapless join, and lyric sync derive from
   *frames rendered out of the device* (WASAPI `IAudioClock`), never wall-clock or frames-handed-to-the-API.
   Mirrors the engine's analytical-`t` animation discipline. `Position` is **derived + QPC-extrapolated,
   never stored, never read on the RT thread.** (For MF video, MF's own presentation clock plays the same role.)
3. **State is signals, not events.** One exhaustive `Signal<PlaybackState>` + separate intent + why-not signals;
   everything else derived. **FIX:** AVFoundation's ~6 KVO'd booleans, web's ~20 events, ExoPlayer poll-only position.
4. **Intent ⊥ actual ⊥ why-not.** Three separate signals — `IsPlayRequested` (intent), `PlaybackState`
   (actual), `Suppression` (why-not); `IsPlaying` is derived. **STEAL:** ExoPlayer `playWhenReady` /
   `STATE_*` / `playbackSuppressionReason`. Answers "user pressed play but we're buffering" vs "we paused
   ourselves because headphones unplugged" directly.
5. **Position rides the clock at frame cadence, alloc-free.** A `FloatSignal` sampled every vsync from the
   clock, bindable straight to a seek-bar node channel — never a 4 Hz tick. **FIX:** web `timeupdate`,
   ExoPlayer `getCurrentPosition()`.
6. **One fixed internal mix format; the audio device opens once per session.** Normalize every source to
   `f32`/device-rate/stereo at the decode edge. Kills the 44.1→48k boundary-reopen gap and the WASAPI-exclusive
   click-on-track-change class **outright**. Format heterogeneity is a resampler concern, never a device concern.
7. **One `MediaSource`, many factories, three custom seams underneath; the engine owns the hard parts.**
   Read-coalescing, buffering, eviction, blocking-bridge, thread-firewall, seek emulation live in the engine,
   not the app. **FIX:** the custom-stream minefield across every incumbent.
8. **The audio render thread is a THIRD real-time thread, stricter than phases 6–13.** A GC pause here is an
   audible dropout, not a dropped frame. Copy+mix ONLY; its **own** `[Conditional]` alloc/lock/blocking
   tripwire, MMCSS "Pro Audio". A natural extension of the near-zero-alloc regime with a different failure mode.
9. **Two-voice mixer is THE primitive; gapless = crossfade with overlap 0.** Model N voices summed then
   mastered; gapless, crossfade, and future automix all fall out of one node. **FIX:** NAudio's fade-one-source
   non-crossfade. The app's `CrossfadeMixer.MixEqualPower` is already this shape.
10. **Effects graph is an immutable value PUBLISHED by atomic swap, never mutated live.** Maps 1:1 onto the
    engine's UI-thread-reconciles → `PUBLISH(13a)` → render-thread-consumes seam with consume-gated quarantine
    (`RenderInFlightDepth + 1`). **FIX:** the AVAudioEngine mutate-a-live-graph uncatchable-exception class.
11. **Params are a separate smoothed signal plane; a param change is never a topology edit.** Every effect
    param is a `Signal<float>` feeding a smoothed `AudioParam` target the RT thread interpolates per block —
    the reduced-motion-as-a-value idiom applied to audio. **FIX:** JUCE/Web Audio zipper noise.
12. **Transport verbs are idempotent, coalescing `ValueTask`s that complete (never throw) on supersession.**
    **FIX:** web `play()` rejecting with `AbortError` on a benign pause/src-swap.
13. **Errors are typed and contextual.** One `Error` signal: category + failing item/segment/sample +
    underlying code + human message + recovery hint. Silent DRM downgrade and nil-error are *unrepresentable*.
    **FIX:** WinUI bare HRESULT, AVFoundation nil+log, web single `MediaError`.
14. **Video is a layer, not a child window.** Decoded frames arrive as a composited DirectComposition surface
    (today via `IVideoPresenter` hole-punch) or, later, as a shared texture + PTS pulled per-vsync. Never an
    airspace-violating HWND. **STEAL:** `AVPlayerItemVideoOutput.copyPixelBuffer(forItemTime:)`, libmpv render ctx.
15. **Loudness normalization = metadata + one reversible scalar + a terminal limiter, never live AGC.**
    Per-track AND per-album gain; a permanent brickwall limiter (~−1.5 dBTP) is the terminal node. **FIX:**
    live AGC that pumps and destroys dynamics.
16. **Decrypt-on-read is a stateless-per-offset decorator, invisible above.** PlayPlay AES-CTR re-derives its
    counter from byte offset; a `DecryptingSource` decorator returns plaintext and the decode/DSP/mixer stages
    never know. The decorator seam **is** the deliverable.
17. **Prepare/preroll-next is a first-class, seek-invalidated state machine.** `Idle→Preparing→Ready→Active`,
    pre-rolled to cover worst-case decode+decrypt+seek latency, **invalidated and re-prepared on seek/queue-edit.**
    **FIX:** the codebase's own `crossfade-prepared-next` scar (a Seek that silently dropped the prepared slot).
18. **System integration is orthogonal opt-in.** SMTC/now-playing, PiP, casting, multi-player sync compose
    freely and are never mutually exclusive. **FIX:** WinUI's auto-SMTC-vs-`MediaTimelineController` cryptic
    state exception.
19. **Progressive disclosure.** `Play(uri)` one-liner with working defaults (auto SMTC, auto buffering, auto
    default-track selection); a builder for power; raw seams underneath. **STEAL:** web `el.src=url;el.play()`;
    **FIX:** ExoPlayer's "too much ceremony to play one file".
20. **Deterministic, engine-owned, race-free lifetime.** Crisp reuse-vs-recreate; disposal safe at any state;
    every callback marshaled to a declared safe context. **FIX:** LibVLCSharp "sleep 1s before Dispose",
    WinUI "Shutdown() has been called" races, mpv reentrant-callback deadlock.

---

## 3. Architecture

### 3.1 The shape

```
                         ┌──────────────────────────────────────────────┐
   Component + hooks ───▶│  MediaPlayerElement / UseMediaPlayer/UseVideo │  Facade (Controls)
                         └───────────────────────┬──────────────────────┘
                                                 │ binds signals + transport verbs
                         ┌───────────────────────▼──────────────────────┐
                         │              IMediaPlayer  (ONE contract)      │  Engine/Media (portable, TerraFX-free)
                         │  signals: PlaybackState/Position/Duration/…    │
                         │  verbs:   Play/Pause/Seek/Rate/Enqueue/Prepare │
                         └───────────────────────┬──────────────────────┘
                                                 │ MediaRouter.Resolve(source.Kind)
                        ┌────────────────────────┴────────────────────────┐
                        ▼                                                  ▼
        ┌───────────────────────────────┐               ┌────────────────────────────────────┐
        │ PcmAudioPlayer (custom graph) │               │ MfMediaPlayer (Media Foundation)     │
        │ Spotify/PlayPlay, crossfade,  │               │ video + self-contained A/V files,    │
        │ EQ, gapless, WASAPI clock     │               │ DRM via CDM                          │
        └──────────────┬────────────────┘               └───────────────┬────────────────────┘
   IMediaByteSource→IAudioDecoder→per-voice DSP          VideoMediaEngine (IMFMediaEngineEx windowless
   →CrossfadeMixer→master DSP→IAudioSink (WASAPI)         swapchain) → IVideoPresenter.BindSurfaceHandle
                        │                                                 │ (DComp child visual, no HWND)
             device clock is the only clock                MF presentation clock; engine paints no pixels
```

### 3.2 The two load-bearing tenets

- **"The engine touches no video pixels."** MF decodes into its own windowless swapchain; the resulting
  DirectComposition surface handle is handed to `IVideoPresenter.BindSurfaceHandle`; the engine composites it
  as a **sibling** child visual z-below the UI swapchain, revealed through a premultiplied-0 hole-punch in the
  UI back buffer. The engine never reads or writes a video texel. (PROVEN: this spine ships today.)
- **"The device clock is the only clock."** For audio, WASAPI `IAudioClock` played-frames is the master
  timeline; `Position` is derived + extrapolated off it (§7.6). For MF video, MF's presentation clock is the
  master. Each backend owns its own clock and publishes its own `Position` signal — the router never mixes them.

### 3.3 Mapping onto the engine's frame loop, render-thread seam, and IVideoPresenter spine

- **Video path** rides the existing 13-phase loop: the player publishes a `VideoSurfaceId`; `MediaPlayerElement`
  emits a hole-punch `BoxEl` at the video rect during reconcile/layout; the render thread calls
  `Place`/`SetVisible`/`Commit` on the presenter at **phase 11** (the same frame-turn's `Present` — the
  two-clock-tear lock). Every `IDCompositionVisual`/`IDCompositionSurface` ComPtr stays render-thread-confined.
- **Audio path** adds a **third real-time thread** (the WASAPI feed callback) analogous to — not the same as —
  the render thread. The DSP graph is published from the control thread onto the exact `PUBLISH(13a)`→consume
  seam with `RenderInFlightDepth + 1` quarantine; the audio thread consumes it lock-free (§7).
- **Signals-first integration:** `Position`/`Buffered`/`PlaybackState`/`IsBuffering` are `IReadSignal<>`
  written from a **non-RT clock-poll tick** (the frame loop or a dedicated low-rate clock thread). The UI binds
  them into `Prop<T>` channels; nothing polls.

### 3.4 Assembly placement (obeys the TerraFX-free / subsystem-folder invariants)

| Assembly | Holds |
|---|---|
| `FluentGpu.Engine/Media/` (portable, TerraFX-free) | `IMediaPlayer`, `MediaSource` + algebra, all signals + POD state types, `MediaError`, `MediaRouter`, the queue/scheduler, `CrossfadeMixer`, `AudioGraphHost` + all `IDspStage` nodes, `GaplessInfo`, the effects surface, every seam interface (`IMediaByteSource`, `IAudioDecoder`, `IAudioSink`, `IAudioClockSource`, `IDeviceWatcher`, `IMediaBackend`/`IMediaSession`, `IVideoPresenter` already lives in `Seams/Pal/`). `PcmAudioPlayer` lives here (no backend needed to run headless). |
| `FluentGpu.Controls/Media/` (TerraFX-free) | `MediaPlayerElement`, `UseMediaPlayer`/`UseVideo` hooks, `SeekBar`, transport chrome. Binds the video layer by `VideoSurfaceId` only. |
| `FluentGpu.Windows/Media/` | `MfMediaPlayer` / the MF `IMediaBackend`, `VideoMediaEngine` (grows into it), WIC thumbnails. |
| `FluentGpu.Windows/Wasapi/` | `IAudioSink` / `IAudioClockSource` / `IDeviceWatcher` leaves (WASAPI + `IMMNotificationClient`). |
| `FluentGpu.WindowsApi/Media/` | the SMTC now-playing bridge (an OS-services pillar); the PlayReady CDM interop (§9). |

The whole `Engine/Media/` surface links into the `FluentGpu.VerticalSlice` closure with **no backend** — the
state machine, source algebra, queue, and DSP graph are all headlessly drivable.

---

## 4. The layered public surface

### 4.1 Layer 1 — the dead-simple facade

**The one-call easy path** (**STEAL:** web `el.src=url; el.play()`). `MediaPlayer` is a thin owner that holds
the currently-routed `IMediaPlayer` and forwards its signals; `Play(source)` routes by source kind and swaps
the inner backend only when the kind changes (§12 reuse-vs-recreate).

```csharp
namespace FluentGpu.Media;

public sealed class MediaPlayer : IMediaPlayer, IAsyncDisposable
{
    public static MediaPlayer Create();                    // working defaults, backend auto-selected on first Play
    public static MediaPlayerBuilder Build();              // Layer 2 power path

    // one call, no wiring — auto buffering, auto SMTC, auto default tracks
    public ValueTask Play(string uriOrPath);
    public ValueTask Play(Stream stream);
    public ValueTask Play(ReadOnlyMemory<byte> bytes);
    public ValueTask Play(MediaSource source);             // the general form all overloads funnel into

    // IMediaPlayer members (§4.2) are forwarded to the routed inner backend.
}
```

**The control + hooks** (idiomatic to `UseImage`/`UseAsyncResource`; engine-owned lifetime tied to the component):

```csharp
public abstract partial class Component
{
    // A MediaPlayer whose lifetime is bound to this component; auto-disposed (async) on unmount.
    protected MediaPlayer UseMediaPlayer(Action<MediaPlayerBuilder>? configure = null);

    // Convenience: a player already pointed at a source that re-loads when `source` (a signal/thunk) changes.
    protected MediaPlayer UseVideo(Func<MediaSource> source);
}
```

```csharp
public sealed class ClipCard : Component
{
    public required string Url { get; init; }
    public override Element Render()
    {
        var player = UseVideo(() => MediaSource.FromUri(Url));   // auto SMTC/buffering/default tracks
        return new MediaPlayerElement { Player = player };        // degrades to audio-only chrome; never crashes
    }
}
```

### 4.2 Layer 2 — the `IMediaPlayer` contract (the full signal surface)

This interface is the ONE contract both backends implement. State is signals; transport is idempotent verbs.

```csharp
namespace FluentGpu.Media;   // the ONE public surface for audio AND video

public enum PlaybackState : byte
{
    Idle,       // no source / stopped
    Opening,    // source resolving, metadata not yet available
    Buffering,  // opened, filling initial buffer
    Ready,      // paused-and-ready (playable, not advancing)
    Playing,    // advancing
    Paused,     // user-paused after having played
    Stalled,    // was playing, ran out of buffer (transient — never becomes Failed)
    Ended,      // reached natural end
    Failed      // terminal error; see Error
}

public enum SuppressionReason : byte      // STEAL: ExoPlayer playbackSuppressionReason — the "why-not"
{
    None, TransientAudioFocusLoss, AudioRouteLost /* headphones unplugged */,
    Unattended /* autoplay/no-gesture — a STATE, never a thrown NotAllowedError */,
    BufferingUnderrun, BackgroundedNoPermission
}

public interface IMediaPlayer : IAsyncDisposable
{
    // ---- reactive state (all IReadSignal<> — the backend is the SOLE writer; UI binds read-only) ----
    IReadSignal<PlaybackState>     State           { get; }
    IReadSignal<bool>              IsPlayRequested { get; }   // intent
    IReadSignal<SuppressionReason> Suppression     { get; }   // why-not
    IReadSignal<bool>              IsPlaying       { get; }   // DERIVED: State==Playing && Suppression==None
    IReadSignal<bool>              IsBuffering     { get; }   // DERIVED memo
    FloatSignal                    PositionSeconds { get; }   // hot path: clock-sampled, alloc-free, node-bindable
    IReadSignal<TimeSpan>          Position        { get; }   // TimeSpan view of the same value
    IReadSignal<TimeSpan>          Duration        { get; }   // TimeSpan.MinValue == unknown (live/streaming)
    IReadSignal<BufferHealth>      Buffer          { get; }   // ranges (in TIME) + forward seconds + stall policy
    IReadSignal<SizeI>             NaturalSize     { get; }   // video px; (0,0) audio-only
    IReadSignal<MediaError?>       Error           { get; }   // single typed error signal (§11)

    FloatSignal                    Volume          { get; }   // read-WRITE 0..1 (master, smoothed)
    IReadSignal<bool>              Muted           { get; }
    FloatSignal                    Rate            { get; }   // read-WRITE; pitch-preserved by default

    // ---- tracks, queue, effects, now-playing, capabilities ----
    TrackSet          Tracks     { get; }                     // observable audio/video/text collections (§6)
    PlayQueue         Queue      { get; }                     // first-class queue w/ gapless+crossfade (§8)
    IAudioEffects     Effects    { get; }                     // EQ/crossfade/normalization (§9); MF video = null-object
    NowPlaying        NowPlaying { get; }                     // SMTC-shaped, opt-in, orthogonal (§12)
    MediaCommands     Commands   { get; }                     // capability bitset

    // ---- video surface binding (for the control; §10) ----
    IReadSignal<VideoSurfaceId> VideoSurface { get; }         // 0 until first video frame; the DComp child-visual id

    // ---- transport: idempotent, coalescing — complete (never throw) on supersession ----
    ValueTask PlayAsync();                                    // resume current source
    ValueTask PauseAsync();
    void      Stop();                                         // idempotent; → Idle, releases decode residency
    ValueTask SeekAsync(TimeSpan to, SeekMode mode = SeekMode.Accurate);
    ValueTask StepFrame(int delta);                          // +1 / −1
    void      SetRate(double rate);
    void      SetVolume(double volume);                      // 0..1
    void      SetMuted(bool muted);

    // ---- source + queue + preroll (MediaSource model: multiple concurrent voices, not a single SetSource) ----
    ValueTask OpenAsync(MediaSource source, CancellationToken ct = default);
    void        Enqueue(MediaSource next);
    PrepareToken PrepareNext(MediaSource next);              // explicit preroll verb (§8.4)
}

public readonly record struct BufferHealth(
    IReadOnlyList<TimeRange> Ranges, TimeSpan ForwardBuffered, bool IsStalled, StallPolicy Policy);
public readonly record struct TimeRange(TimeSpan Start, TimeSpan End);
public enum SeekMode : byte { Keyframe, Accurate }          // fast scrub vs decode-to-PTS. STEAL: mpv
```

`IsPlaying` and `IsBuffering` are `Memo`s the backend exposes read-only. `PositionSeconds` is the hot scalar
`FloatSignal` — a scrubber binds it directly to a node channel, skipping render/reconcile/layout on every tick.

**Binding in a component (no event subscription, no teardown):**

```csharp
public sealed class TransportBar : Component
{
    public required IMediaPlayer Player { get; init; }
    public override Element Render()
    {
        var p = Player;
        return new BoxEl { Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, Children = new Element[] {
            new IconButton {
                Glyph   = Prop.Of(() => p.IsPlaying.Value ? Icons.Pause : Icons.Play),
                OnClick = () => { if (p.IsPlaying.Peek()) _ = p.PauseAsync(); else _ = p.PlayAsync(); },
            },
            new SeekBar {
                Position = p.PositionSeconds,                          // hot signal → node channel, no re-render
                Duration = Prop.Of(() => (float)p.Duration.Value.TotalSeconds),
                Buffered = Prop.Of(() => p.Buffer.Value.ForwardBuffered),
                OnScrub  = s => _ = p.SeekAsync(TimeSpan.FromSeconds(s), SeekMode.Keyframe),
                OnCommit = s => _ = p.SeekAsync(TimeSpan.FromSeconds(s), SeekMode.Accurate),
            },
            new TextEl("") { Text = Prop.Of(() => Fmt.Clock(p.Position.Value, p.Duration.Value)) },
        }};
    }
}
```

### 4.3 The view — `MediaPlayerElement` (replaces the chrome-only mockup)

The current `src/FluentGpu.Controls/MediaPlayerElement.cs` is a static chrome factory with no real video. It
becomes a real `Component` that binds a headless `IMediaPlayer` to a hole-punched video layer + transport
chrome, and **degrades to audio-only chrome when `NaturalSize == (0,0)`**.

```csharp
namespace FluentGpu.Controls.Media;

public sealed class MediaPlayerElement : Component
{
    public required IMediaPlayer Player { get; init; }
    public bool  AreTransportControlsEnabled { get; init; } = true;   // must NEVER crash across OS versions
    public MediaStretch Stretch { get; init; } = MediaStretch.Uniform;
    public Element? PosterContent { get; init; }                      // shown until first frame / audio-only
    public Element? TransportOverride { get; init; }                  // bring-your-own transport, still bound

    public override Element Render() { /* hole-punch BoxEl at VideoSurface rect + bound transport; §10 */ }
}
```

**FIX (WinUI `MediaTransportControls` #7702 `E_NOINTERFACE` process-crash):** the default transport is pure
FluentGpu (our own GPU text + `SeekBar`), so there is no OS control to crash on — the easy path is the safe path.

### 4.4 Layer 3 — the seams

The raw escape hatches, all in portable `Engine/Media/`, all implemented in terms of which every Layer-1/2
capability is built: `IMediaByteSource` / `IMediaFeed` / `IMediaSampleSource` (sources, §5),
`IAudioDecoder` / `IAudioSink` / `IAudioClockSource` / `IDeviceWatcher` (audio graph, §7),
`IMediaBackend` / `IMediaSession` (per-platform video, §10), the DRM license relay (§9), and `IAbrPolicy`.

---

## 5. Source model

**FIX:** the recurring custom-stream pain. `MediaSource` is an immutable record; the *hard parts*
(read-coalescing, buffering, eviction, blocking-bridge, thread-firewall, seek emulation) live in the engine.

```csharp
namespace FluentGpu.Media;

public enum MediaKind : byte { Auto, PcmAudio, MfVideoOrFile }   // routing hint; Auto = the router sniffs

public abstract record MediaSource
{
    public MediaKind Kind { get; init; } = MediaKind.Auto;

    // ---- factories: all yield ONE player code path ----
    public static MediaSource FromFile(string path);
    public static MediaSource FromUri(string url, NetworkOptions? net = null);
    public static MediaSource FromStream(Stream stream, MediaContentType? hint = null);   // easy case verbatim
    public static MediaSource FromBytes(ReadOnlyMemory<byte> bytes, MediaContentType? hint = null);
    public static MediaSource FromPull(IMediaByteSource source);       // random-access byte seam (§5.1)
    public static MediaSource FromFeed(IMediaFeed feed);               // MSE-style push/append (§5.2)
    public static MediaSource FromSamples(IMediaSampleSource source);  // already-demuxed encoded samples (§5.3)

    // ---- deferred/lazy resolution (STEAL: MediaBinder) ----
    public static MediaSource Deferred(Func<CancellationToken, ValueTask<MediaSource>> bind);

    // ---- composable source algebra (STEAL: ExoPlayer decorator tree) ----
    public static MediaSource Concat(params MediaSource[] parts);
    public MediaSource Clip(TimeSpan start, TimeSpan end);
    public MediaSource Loop(int count = -1);                           // -1 = infinite
    public static MediaSource Merge(params MediaSource[] tracks);      // e.g. video + external audio
    public MediaSource Silence(TimeSpan duration);

    // ---- per-source overrides (player default → per-source, ExoPlayer DI layering) ----
    public MediaSource With(NetworkOptions net);
    public MediaSource With(DrmConfig drm);
    public MediaSource WithMetadata(MediaMetadata meta);               // seeds NowPlaying without a round-trip
    public MediaSource WithExternalSubtitle(SubtitleSource sub);
    public MediaSource WithKind(MediaKind kind);                      // force routing (e.g. PlayPlay → PcmAudio)
}
```

### 5.1 Pull seam — `IMediaByteSource` (THE load-bearing seam; PlayPlay's front door)

Reconciling the two workflows: the video synthesis wanted an async `ReadAsync`; the audio pillar needs a
synchronous, re-openable, `Seek`-with-counter-re-derive shape for the decrypt decorator. **Decision: the
canonical low-level seam is synchronous `read(2)`-style** — the engine runs it on a firewalled worker thread
so blocking is fine, and it is the shape the `DecryptingSource` (CTR `SeekCounter`) composes over. Async /
`Stream` / `PipeReader` producers plug in at the convenience layer (`FromStream`/`FromFeed`), where the engine
drives the pump.

```csharp
public readonly struct DataSpec        // random access = a RE-OPEN-able Range op (ExoPlayer), not in-place seek
{
    public StringId Uri; public long Position; public long Length /* -1 == to-EOF */; public HeaderList Headers;
}
public readonly struct SourceCaps { public bool Seekable, ExpensiveSeek, KnownLength; }

public interface IMediaByteSource
{
    bool  TryOpen(in DataSpec spec);   // a fresh signed/expiring CDN URL or key can be injected per open
    int   Read(Span<byte> dst);        // read(2): >0 (short reads legal), 0 = EOF, <0 = error — decode ALWAYS loops
    long  Seek(long offset);           // re-open Range under the hood for HTTP; may FAIL (declared via Caps)
    long? Length { get; }              // NULLABLE — streaming/decrypting may not know until the last chunk
    SourceCaps Caps { get; }           // seekability is a DECLARED capability, not an assumed guarantee
    void  Cancel();                    // CROSS-THREAD, NON-BLOCKING abort (mpv cancel_fn) — keeps scrub responsive
    void  Close();
}
```

- **`Length` nullable** — seekbars/prefetch tolerate unknown length and drive progress off the **sample clock**,
  never divide by an assumed total or preallocate a ring by byte-size. **FIX:** ExoPlayer's mandatory known-length.
- **`Cancel()` is the responsiveness primitive** — a seek/stop/track-change aborts an in-flight expensive
  network `Read`; teardown never serializes behind a socket timeout. **FIX:** AVAssetResourceLoader poor cancel.
- Engine coalesces reads to a configurable chunk (kills the ExoPlayer 1-byte-read death), prefetches, bridges
  blocking demux internally, and **never calls a possibly-expensive `Seek`/`Read` on the RT feed thread** — the
  callback runs only against already-resident ring bytes (§7).

### 5.2 Push seam — `IMediaFeed`

**FIX:** MSE's `updating` flag, hand-maintained queue keyed on `updateend`, manual `remove()` eviction, and
`QuotaExceededError`-as-fullness-probe (the ManagedMediaSource lesson).

```csharp
public interface IMediaFeed
{
    MediaContentType ContentType { get; }
    TimeSpan TargetBuffer { get; }                    // declarative target; engine owns eviction/flow control
    IReadSignal<bool> NeedData { get; }               // backpressure as a SIGNAL — producer just responds
    IReadOnlyList<TimeRange> BufferedRanges { get; }  // introspectable; never catch an exception to probe fullness
    ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);   // no updating flag; awaitable
    void Complete();
}
```

Producers may instead hand the engine a `PipeReader`, `IAsyncEnumerable<ReadOnlyMemory<byte>>`, or a
fill-callback — the engine drives the pump and honors `NeedData`.

### 5.3 Sample seam — `IMediaSampleSource` (the blessed FFmpeg-class path)

**FIX:** first-class, not an escape hatch (WinUI `MediaStreamSource` done right, async-native — no COM
deferrals). **STEAL:** ExoPlayer `Extractor` separation.

```csharp
public interface IMediaSampleSource
{
    IReadOnlyList<StreamDescriptor> Streams { get; }   // typed Audio/VideoStreamDescriptor up front
    DrmConfig? Drm { get; }
    ValueTask<MediaSample?> GetSampleAsync(int streamIndex, CancellationToken ct);   // null = EOS
    ValueTask SeekAsync(TimeSpan to, CancellationToken ct);
}
public readonly record struct MediaSample(
    int StreamIndex, ReadOnlyMemory<byte> Data, TimeSpan Pts, TimeSpan? Duration, bool IsKeyframe, SampleFlags Flags);
```

### 5.4 The `DecryptingSource` decorator (the PlayPlay exemplar)

PlayPlay is AES-CTR: the counter re-derives from byte offset (`counter = base + offset/16`), so any offset
decrypts **without replay**. It is an `IMediaByteSource` decorator invisible to everything above it.
**PlayPlay/AudioHost internals are OUT OF SCOPE — this decorator seam IS the deliverable:** it accepts an
app-supplied plaintext key/cipher and a raw encrypted inner source and returns plaintext on `Read`.

```csharp
public interface IAudioKeyProvider          // app-side (PlayPlay); prefetched/cached/rotated OUT-OF-BAND
{
    ValueTask<AudioKey> ResolveKeyAsync(StringId trackUri, CancellationToken ct);  // NEVER called inside Read
}

public sealed class DecryptingSource : IMediaByteSource   // portable; PlayPlay plugs in AT THE FRONT
{
    readonly IMediaByteSource _inner;   // the raw encrypted chunk-fetch source (app-provided)
    readonly ICtrCipher       _cipher;  // seeded with the pre-resolved AudioKey; app owns the primitive

    public bool TryOpen(in DataSpec spec) { bool ok = _inner.TryOpen(spec); _cipher.SeekCounter(spec.Position); return ok; }
    public int  Read(Span<byte> dst)      { int n = _inner.Read(dst); if (n > 0) _cipher.XorInPlace(dst[..n]); return n; }
    public long Seek(long o)              { long p = _inner.Seek(o); _cipher.SeekCounter(p); return p; }
    public long? Length => _inner.Length; public SourceCaps Caps => _inner.Caps;
    public void Cancel() => _inner.Cancel(); public void Close() => _inner.Close();
}
```

Decrypt is ~free; the cost that matters is the wrapped chunk fetch + audio-key latency, both handled at
**Prepare** time off the RT path (§8.4). Decode/DSP/mixer above it never know it decrypts.

### 5.5 The decoder / extractor seam — `IAudioDecoder`

```csharp
public interface IAudioDecoder                 // Vorbis / AAC / Opus / FLAC / MP3 — leaf codecs behind the seam
{
    bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info);   // sniffs container/codec
    int  Read(Span<float> dst);                // decodes → resamples INTO the fixed mix format; short reads legal
    long Seek(long frame);
    GaplessInfo Gapless { get; }               // parsed from container side-metadata — §8.3
}

// A decoded source voice is a decorator stack:  Resample( Decode( [Decrypt]( Fetch ) ) ) : IAudioSource
```

`Fetch`/`Decrypt`/`Decode`/`Resample` compose as decorators; the mixer receives a uniform `IAudioSource` in the
fixed mix format. **Format heterogeneity is fully resolved at the decode edge** — 44.1k Vorbis and 48k AAC both
arrive as `f32/48k/stereo`; the device never learns they differed.

### 5.6 Typed codec/container descriptors

**FIX:** stringly-typed MIME/codec strings that fail deep in `append` (`video/webm;codecs=…`, mpv/VLC bags).

```csharp
public readonly record struct MediaContentType(Container Container, CodecId Video, CodecId Audio) { public static MediaContentType Sniff(); }
public enum Container : byte { Unknown, Mp4, Mkv, WebM, Ogg, Mpeg2Ts, Flac, Wav, Adts, Mp3, Hls, Dash }
public enum CodecId : ushort { None, H264, Hevc, Av1, Vp9, Aac, Opus, Flac, Mp3, Vorbis, Pcm /* … */ }
```

Capability is answered **early** at query time (`MediaCapabilities.IsSupported(MediaContentType)`), never as
an opaque failure inside append.

---

## 6. Tracks & subtitles

**FIX:** flat/opaque track lists; VTT-only web subtitles. Unified observable model; external subs rendered by
the engine's own GPU text stack (not an OS overlay).

```csharp
public sealed class TrackSet
{
    public ObservableList<MediaTrack> Audio { get; }
    public ObservableList<MediaTrack> Video { get; }
    public ObservableList<MediaTrack> Text  { get; }
    public IReadSignal<MediaTrack?> SelectedAudio { get; }
    public IReadSignal<MediaTrack?> SelectedVideo { get; }
    public IReadSignal<MediaTrack?> SelectedText  { get; }

    public void Select(MediaTrack track);        // selection by intent; engine flips IsSelected
    public void DisableText();
    public MediaTrack AddExternalSubtitle(SubtitleSource src, string language, string label);
    public void SetSyncOffset(MediaTrack textTrack, TimeSpan offset);   // per-track drift correction
}

public sealed record MediaTrack(
    int Id, TrackKind Kind, string? Language, string Label, TrackRole Role,
    IReadSignal<bool> IsSelected, MediaContentType Codec);
public enum TrackRole : byte { Main, Alternate, Commentary, Descriptions, Captions, Subtitles, Sign, Karaoke }
```

### 6.1 Structured timed cues (synced lyrics / karaoke)

**STEAL:** push cue objects with styling + per-cue enter/exit — not serialized WebVTT. Directly serves the
Wavee synced-lyrics workload, which slaves to the **audio sample clock** via `IPlaybackClock`
(`media-pipeline.md §9`), not the frame clock — so syllable timing stays correct under dropped frames.

```csharp
public readonly record struct TimedCue(TimeSpan Start, TimeSpan End, string Text, CueStyle Style, object? Tag);
public sealed class CueTrack
{
    public IReadSignal<TimedCue?> ActiveCue { get; }   // frame-accurate active cue (no C# event)
    public void OnCueEnter(Action<TimedCue> handler);  // per-cue enter (karaoke highlight)
    public void OnCueExit(Action<TimedCue> handler);
}
```

---

## 7. Audio pillar — the DSP graph (`PcmAudioPlayer`)

**Thesis.** Everything decoded-and-mixed sample-by-sample — Spotify/PlayPlay, local files routed to the graph,
crossfade, EQ, ReplayGain, gapless — is **one `IMediaPlayer`** whose internal fulfillment is a 5-stage pull
graph ending at a WASAPI sink opened **once per device session**. The app sees only `IMediaPlayer` signals +
`player.Effects`. (Re-parents the app's existing `AudioPlayEngine`/`CrossfadeMixer` — PROVEN shapes — under
this seam; the engine designs the seam, the app team wires its internals.)

### 7.1 The 5-stage pull pipeline

```
IMediaByteSource (fetch/decrypt) → IAudioDecoder → [per-voice IDspStage chain]
    → CrossfadeMixer (N voices) → [master IDspStage chain] → IAudioSink (WASAPI)
```

**Internal format is fixed: `f32` interleaved, `mixRate` (device rate), stereo.** Every source resamples INTO
it at the decode edge (a `Resample` `IDspStage`), so the mixer sums homogeneous PCM and the device opens once.
A track boundary is a **splice at a frame index**, not a device reopen — the single decision that kills the
entire boundary-gap and exclusive-mode-click class.

### 7.2 Uniform POD node shape (pull-based, zero-alloc)

```csharp
public readonly struct MixFormat { public int SampleRate; public int Channels; }   // f32 implied; e.g. {48000, 2}

public interface IDspStage
{
    int  Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx);  // frames produced
    int  LatencySamples { get; }             // summed into the §7.6 position compensation
    bool Bypassed { get; set; }              // a bypass is a value, not a topology edit
}
public interface IAudioSource                // leaf voices — read(2) short-read semantics
{
    int  Read(Span<float> dst);              // framesWritten; 0 = EOF, <0 = error, short reads legal
    long PositionFrames { get; }             // in the fixed mix-rate domain
    GaplessInfo    Gapless  { get; }         // a SOURCE property, carried through the chain unchanged (§8.3)
    ReplayGainInfo Loudness { get; }         // per-track AND per-album scalar + true-peak (§7.7)
}
public readonly ref struct BlockCtx { public long StartFrame; public int MixRate; public ParamPlane Params; }
```

Buffers are pooled native `f32` spans (a `PixelBufferPool`-analog / `ChunkedArena`-style native pool), sized
to a `MaxBlock`; pull sizes vary without reallocation (the JUCE "never assume a constant block size" lesson).

### 7.3 Effects composition — node order is itself the contract

```
[Source (carries ReplayGain scalar + GaplessInfo)]
  → [Gain]      // track/album scalar — DATA on the source, applied at the voice (before the mix)
  → [EQ]        // cascade of RBJ biquad bands, PER-CHANNEL, PER-VOICE (a fading-out track keeps its own EQ)
  → [Channel]   // mono/balance/crossfeed
  → [CrossfadeMixer]                         // ← the N-voice sum happens here
  → [MasterEQ?] // optional global post-mix EQ (a separate stage)
  → [Limiter]   // TERMINAL, always present, ~-1.5 dBTP — after ANY gain/EQ boost
  → [SRC]       // ONLY if device-rate != mix-rate (normally elided)
  → [Sink (WASAPI)]
```

- **EQ lives PER-VOICE, pre-mixer** — during a crossfade the outgoing track keeps its own EQ curve while the
  incoming one has its own. A global post-mix EQ is a *separate*, optional stage.
- A read-only **`Tap` node** at a named point (default post-limiter / pre-SRC) copies into a lock-free ring for
  the FFT/visualizer — never a duplicated buffer path bolted on later. Exposed as `IReadSignal<VisualizerFrame>`.

### 7.4 The graph as a PUBLISHED value (atomic swap; the AVAudioEngine fix)

The DSP chain is an immutable record (ordered `EffectSpec` list + params). A **control-thread reconcile**
compiles it to a flat pre-ordered POD `node[]` (native-backed, no per-callback managed alloc) and publishes it
by a **single `Volatile.Write` pointer swap**; the old graph keeps rendering until the swap and is freed only
under **consume-gated quarantine (`RenderInFlightDepth + 1`)** — exactly the engine's ComPtr model. **NEVER
mutate a live graph** — that in-place topology edit is the AVAudioEngine uncatchable-exception class.

```csharp
public sealed record AudioGraphSpec(ImmutableArray<EffectSpec> PerVoiceChain, ImmutableArray<EffectSpec> MasterChain, LimiterSpec Limiter);
public abstract record EffectSpec { public bool Bypassed; }
public sealed record EqSpec(ImmutableArray<BiquadBand> Bands) : EffectSpec;   // BiquadBand POD {type,freqHz,q,gainDb}

sealed class AudioGraphHost
{
    AudioGraph* _live;                        // the audio thread reads this each block via Volatile
    readonly QuarantineRing<IntPtr> _retire;
    public void Publish(AudioGraphSpec spec)  // CONTROL thread
    {
        var compiled = Compile(spec);         // flat POD node[]; coefficients precomputed OFF the RT thread
        var old = Interlocked.Exchange(ref _liveRef, compiled.Ptr);
        _retire.Enqueue(old, atConsumeSeq: _mixer.ConsumeSeq + RenderInFlightDepth + 1);
    }
    // AUDIO thread: reads *_live per block; NEVER walks a graph another thread edits.
}
```

### 7.5 Params bind to signals — two planes; commands are messages

Two planes: **topology** (swapped, rare) vs **params** (smoothed, frequent). Each public param (`Volume`,
`Rate`, each EQ band gain, `CrossfadeMs`, `NormMode`, `ReferenceLufs`) is a `FloatSignal`/`Signal<T>`; a
control-thread effect writes the smoothed `AudioParam.Target` (never the live `Current`); the audio thread
interpolates per block — **linear for gain/time, multiplicative for Hz/dB.** Set-vs-ramp is a *value*, not a
branch (the reduced-motion idiom). **Commands** (`SetEq`, `Seek`, `Enqueue`, `Play/Pause`) are POD **messages**
posted over an SPSC ring correlated by token — never direct mutation of RT-thread state.

```csharp
public struct AudioParam { public float Current, Target; public SmoothKind Kind; public float RampSamples; public float Step(int frames); }
```

### 7.6 The clock / timeline — WASAPI `IAudioClock` is the master

Formalizes the app's `AudioPlayEngine` treating `WasapiRenderer.ReleasedFrames` as the domain (fade envelopes
baked against it, pause-aware because it stalls under pause) as **THE clock**.

```csharp
public interface IAudioClockSource        // FluentGpu.Windows Wasapi/ → IAudioClock/IAudioClock2 (hand-vtable, HOT)
{
    long WrittenFrames { get; }           // cheap high-rate counter on the FEED thread (the estimate)
    bool TryGetPlayed(out long playedFrames, out long qpc);   // GetPosition — AUTHORITATIVE, QPC-correlated
    long StreamLatencyFrames { get; }     // GetStreamLatency — MEASURED, re-read on every device rebuild
    int  MixRate { get; }
}
```

- **Two-clock discipline:** a cheap written-frames counter gives the estimate; `IAudioClock::GetPosition`
  (QPC-correlated) gives the authoritative played count, reconciled to detect drift/glitch.
- **`GetPosition` is a user→kernel→user transition and MUST NOT run on the RT feed callback** (per-callback =
  crackle — naudio #613, portaudio #303). Sampled rarely off-thread; QPC-extrapolated between polls.
- **Position is derived + extrapolated, never stored, gated by warmup:**

```
Position = (playedFrames + (nowQpc − sampleQpc)·MixRate/qpcFreq) / MixRate − ΣLatencySamples/MixRate
```

  Published as `Signal<TimeSpan> Position` from a **non-RT clock-poll tick.** An `IsValid` warmup gate holds
  the signal until `GetPosition` returns non-zero (it reads 0 for the first several seconds on some drivers).
- **Latency is measured, not assumed** (nominal ~22 ms vs measured ~38 ms in shared mode). Sum **every**
  stage's `LatencySamples` (decoder trim, time-stretch, crossfade overlap, resampler, device buffer) and
  subtract, so lyrics/visualizers/scrub slave to what's **audible now**. Re-measure on every device rebuild.

### 7.7 Loudness normalization — metadata + one scalar + terminal limiter (never live AGC)

```csharp
public readonly struct ReplayGainInfo   // scanned OFFLINE / from tags — NEVER computed live on the mix
{ public float TrackGainDb, AlbumGainDb; public float TrackPeak, AlbumPeak; }
```

The active `NormMode {Off,Track,Album}` selects the dB, applied as the per-source `Gain` scalar **at the voice,
before the mixer**. **Album gain is the default** (preserves inter-track dynamics for gapless; track gain
equalizes for shuffle). A permanent brickwall limiter (~−1.5 dBTP) is the terminal node — any positive gain or
EQ boost can clip. **CRITICAL:** when crossfading two tracks under normalization, gains are baked **per-source
BEFORE the mix**, not globally after — you are mixing two tracks at *different* gains.

### 7.8 Speed/pitch, EQ, visualizer

- **EQ** — `EqStage` holds a per-channel cascade of RBJ biquads; `BiquadBand` POD `{type,freqHz,q,gainDb}`.
  A gain-only tweak ramps via the param plane; a freq/Q change recomputes coefficients **off-RT** and publishes
  via `SwapGraph`, cross-ramping old→new coefficients to avoid a step transient. Presets are `BiquadBand[]`.
- **Speed/pitch** — a `TimeStretchStage` (WSOLA/phase-vocode) bound to `Rate`; pitch-preserving by default.
  It reports a variable output count **and a non-zero `LatencySamples` that must be re-summed into §7.6** —
  else the progress bar lies during a rate change.
- **Visualizer** — the `Tap` node's lock-free ring; a non-RT tick runs the FFT and publishes
  `IReadSignal<VisualizerFrame>`, bound like any other signal.

### 7.9 The RT feed thread + headless parity (must-haves)

- **Copy+mix ONLY:** zero managed alloc, zero managed locks, zero syscalls, no logging, no
  reconciler/scene/signal-write, no decode/decrypt. It drains a lock-free SPSC ring a worker filled ahead.
  MMCSS "Pro Audio", bounded per-callback work (MMCSS *demotes* overruns). **Underrun writes silence + bumps an
  xrun signal — never blocks or crashes.**
- **A dedicated `[Conditional("FG_AUDIO_TRIPWIRE")]` RT tripwire**, separate from the phases-6–13 one: per
  callback it asserts 0 managed allocations (`GC.GetAllocatedBytesForCurrentThread()` delta == 0), 0 managed
  lock acquisitions, 0 blocking calls, and bounded duration. Compiled out of the shipping AOT binary
  (production safety == CI coverage).
- **Headless parity:** a null sink + synthetic clock pulls deterministic frames — "pull N frames
  deterministically" is a first-class harness op for golden-PCM diffs + the RT tripwire, exactly like the
  headless `Rhi`/`Pal` seams.
- **Device-loss / follow-default** is a first-class state machine `{Building,Running,Reinitializing,Faulted}`
  via `IDeviceWatcher` (`IMMNotificationClient`, cold `[GeneratedComInterface]`): a default-device change /
  unplug rebuilds **ONLY the sink** under a live graph — sources, queue, `PreparedSlot`, and position survive —
  off the RT thread, with a short fade-in on resume and a latency re-measure.
- **Primary-voice lifetime is publish-don't-mutate (as-built).** The per-voice decode rings live in an immutable
  `RingEntry[]` published via `Volatile` and read by the RT feed + worker as a snapshot; installs (`SetVoice`
  `Wrap`) and crossfade adds (`WrapAdditional`) rebuild it under a control/worker-only table lock the RT thread
  never takes — the **same model as the crossfade retire path**. A `SetVoice` retires the previous primary by
  **reference** through a control→worker SPSC (ids would collide — the old and new primary share the primary
  voice id, which the ring table is tagged with so the RT natural-end retire resolves). **The worker is the sole
  ring disposer**; the RT thread only reports a finished voice id. **Seek is worker-applied**: control posts the
  frame to a one-slot mailbox, the worker applies the inner-decoder `Seek` between pumps (the sole toucher of the
  inner decoder) and hands the RT a consumer-side ring flush — a control-thread seek mid-decode is the resampler
  torn-`Reset` crash, so it is routed away. The RT/worker/clock loops are **fault-contained** per iteration: a
  fault latches and surfaces as a `MediaError` off the RT thread (Lifecycle/Decode + Retryable) — never
  process-fatal — and a failing decoder marks its ring exhausted → natural retire → clean off-RT disposal.

### 7.10 The `player.Effects` surface

```csharp
public interface IAudioEffects            // MF video backend returns an inert null-object in v1
{
    Equalizer          Equalizer   { get; }
    FloatSignal        CrossfadeMs { get; }   // 0 == gapless; N ms == overlap N frames
    Signal<CrossCurve> CrossfadeCurve { get; }// EqualPower (default) | Linear | Auto(correlation-hinted)
    Signal<NormMode>   Normalization { get; } // Off | Track | Album (default Album)
    Signal<float>      ReferenceLufs { get; } // -11 | -14 | -17 | -19
    FloatSignal        Balance     { get; }
    Signal<bool>       PreservePitchOnRate { get; }
    IReadSignal<VisualizerFrame> Visualizer { get; }
}
public sealed class Equalizer
{
    public Signal<bool> Enabled { get; }
    public EqBand[]     Bands   { get; }      // each band's Gain/Freq/Q is a signal — set-vs-ramp is a value
    public void Apply(EqPreset preset);
}
public sealed class EqBand { public FloatSignal GainDb; public FloatSignal FreqHz; public FloatSignal Q; public BiquadType Type; }
```

---

## 8. Queue + gapless/crossfade

**FIX:** encoding-dependent gapless accidents and no crossfade at all (dual-player hacks). **STEAL:** ExoPlayer
`PreloadManager`, `MediaPlaybackList.MaxPrefetchTime`. Directly serves the repo's prepared-next / crossfade
workload.

### 8.1 Public surface — `PlayQueue`

```csharp
public sealed class PlayQueue
{
    public ObservableList<QueueItem> Items { get; }
    public IReadSignal<int> CurrentIndex { get; }
    public IReadSignal<QueueItem?> Current { get; }

    public QueueItem Add(MediaSource source, MediaMetadata? meta = null);
    public void InsertNext(MediaSource source);
    public void Remove(QueueItem item); public void Clear();
    public ValueTask GoToAsync(int index); public ValueTask NextAsync(); public ValueTask PreviousAsync();

    public PrefetchPolicy Prefetch { get; set; }        // { LookaheadItems = 2, MaxPrefetchTime = 30s }
    public TransitionMode DefaultTransition { get; set; } = TransitionMode.Gapless;
    public void SetTransition(QueueItem from, ScheduledTransition transition);
    public RepeatMode Repeat { get; set; } public bool Shuffle { get; set; }
}
public readonly record struct ScheduledTransition(
    TransitionKind Kind /* Gapless|Crossfade|HardCut */, TimeSpan Overlap,
    TimeSpan? TrimStart, TimeSpan? TrimEnd, Easing Curve);   // trim is encoder-INDEPENDENT
```

### 8.2 Internal fulfillment — the `CrossfadeMixer` (audio backend)

**ONE MECHANISM, overlap ∈ {0, N}.** Gapless = butt-join trimmed PCM with overlap 0; crossfade = overlap N
frames of two live voices through per-sample gain envelopes. This is the shape of the app's
`CrossfadeMixer.MixEqualPower(outgoing, incoming, dst, startFrame, fadeFrames, channels)` — lifted verbatim.

```csharp
public sealed class CrossfadeMixer
{
    readonly List<MixVoice> _voices = new(4);   // ≥2 during overlap; pre-sized, no per-block alloc
    public long ConsumeSeq;                      // frames consumed — drives §7.4 quarantine + §7.6 position
    public int Render(Span<float> dst, int frames, in BlockCtx ctx)
    {
        dst[..(frames*ctx.Channels)].Clear();
        for (int i = 0; i < _voices.Count; i++) _voices[i].MixInto(dst, frames, ctx);  // per-voice EQ+gain inside
        ConsumeSeq += frames; return frames;
    }
}
public struct MixVoice { public IAudioSource Src; public GainEnvelope Env; public long StartFrame; public byte EqNodeIx; }
```

### 8.3 Sample-accurate join + encoder-delay trim

The transition point is a **frame index** in the mixer's fixed-rate stream:
`crossfadeStart = (A.exactFrames − A.trailPadFrames) − overlapFrames`, off the device clock (the `ReleasedFrames`
domain). **No timers, no wall-clock** — drift can't accumulate; pause-awareness is free (the counter stalls
under pause).

```csharp
public readonly struct GaplessInfo    // populated from side-metadata; NEVER a hardcoded constant
{
    public int  LeadInFrames;   // skip on the FIRST read (LAME 576 / iTunes 528 / FhG 672; AAC 1024 FFmpeg / 2048 FDK — encoder-specific)
    public int  TrailPadFrames; // stop this many early on the LAST read
    public long ExactFrames;    // true length after trim; -1 until known (streaming trailPad)
    public bool TailKnown;      // false while a streaming/decrypting source hasn't reached the padding tag
}
```

Sources populate it from LAME/Xing, iTunSMPB / MP4 edit-list, Opus pre-skip, Vorbis granulepos; lossless
reports zero. One mechanism delivers true gapless across every codec **including PlayPlay-decrypted OGG/Vorbis
or AAC.** Curve default is **equal-power** (`gain1=cos(p·π/2)`, `gain2=sin(p·π/2)`) for uncorrelated material;
`Linear` for correlated/beatmatched joins; precomputed as a per-sample LUT applied branch-free.

### 8.4 The `PreparedSlot` state machine (preroll, seek-invalidated — the scar)

```csharp
public enum PrepState : byte { Idle, Preparing, Ready, Active, Failed }
public struct PreparedSlot
{
    public PrepState State; public IAudioSource Source; public long TargetStartFrame;
    public uint Epoch;      // bumped on Seek/queue-edit — defeats a stale late Prepare (the named bug)
}
```

An `ending-soon` event off the sample clock (fired `overlapFrames + worstCaseLatencyMargin` before
`trimmedLength(A)`) drives `Prepare(next)`: open the byte-source, **resolve/cache the audio key** (prefetched,
per-track, rotated out-of-band — never inside `Read`), prime the decoder, prefill opening samples, resolve
`GaplessInfo`. The mixer starts a crossfade **only if `State == Ready`** by the join; otherwise it degrades to
gapless-butt-join or a bounded micro-gap — it **never truncates A or starts B mid-fill.**

**The named scar (memory: `crossfade-prepared-next-campaign`):** a Seek that silently dropped the prepared slot
with no `Missed` → no re-prepare → crossfade never fired for scrubbed tracks. **Fix:** `Seek`/queue-edit bump
`Epoch`, INVALIDATE, and RE-PREPARE; a late `Prepare` completion whose `Epoch` mismatches is dropped. Streaming
sources whose `TrailPad` is unknown until near EOF emit a `tail-known` event; if it arrives too late the mixer
degrades crossfade → gapless rather than guess. **Declick** (2–5 ms ramp) everywhere a discontinuity can occur.

---

## 9. Video pillar + DRM

### 9.1 The MF backend (fulfills the same `IMediaPlayer`)

`MfMediaPlayer` wraps the **PROVEN** `VideoMediaEngine` (`IMFMediaEngineEx` windowless swapchain, decoding real
MP4 today). Flow: MF decodes into its own DComp swapchain → the swapchain **surface handle** is handed to
`IVideoPresenter.BindSurfaceHandle` → the engine composites it as a DComp **child visual** z-below the UI,
revealed through a premultiplied-0 hole-punch. The engine paints **no** video pixels (tenet §3.2).

**The `SetMultithreadProtected` lesson (PROVEN, in `VideoMediaEngine.cs`):** `IMFMediaEngine` decodes on MF
worker threads while the app renders on its own thread; the shared D3D device **must** have
`ID3D10Multithread::SetMultithreadProtected(TRUE)` (vtable slot 5) or the two threads corrupt the device.
This is a hard requirement, not a tuning knob — it is baked into the MF backend's device setup.

**The per-platform video seam** (so macOS `AVPlayerLayer` and a future shared-texture path both fit):

```csharp
public interface IMediaBackend { ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct); MediaCapabilities Capabilities { get; } }
public interface IMediaSession : IAsyncDisposable
{
    void ConnectSignals(MediaSignalSink sink);              // backend → engine, marshaled to the safe context
    ValueTask PlayAsync(); ValueTask PauseAsync(); ValueTask SeekAsync(TimeSpan t, SeekMode m);
    void SetRate(double r); void SetVolume(double v); void SetMuted(bool m);
    VideoDelivery Video { get; }
}
public abstract record VideoDelivery
{
    // Path A — composited surface (MF windowless swapchain / AVPlayerLayer): engine gets an id. SHIPPING.
    public sealed record CompositedSurface(VideoSurfaceId Id, SizeI NaturalSize, bool IsHdr) : VideoDelivery;
    // Path B — shared texture pulled per present time (STEAL: copyPixelBuffer(forItemTime:), libmpv). FORWARD HOOK.
    public sealed record SharedTexture(Func<TimeSpan, VideoFrame?> AcquireForPresentTime, bool IsHdr) : VideoDelivery;
}
```

Path A is the current shipping spine (no `IVideoPresenter` change). Path B is the forward hook: the compositor
asks "give me the frame for THIS present time" and gets a shared D3D texture + PTS + explicit `ColorSpace`/HDR
pulled into the 13-phase loop at zero per-frame managed alloc (**FIX:** WinUI frame-server HDR wash-out /
16-bit-surface throw). Neither path uses an OS child HWND.

### 9.2 DRM — the protection hook + the honest posture

**Posture: the engine never sees a decrypted pixel or a content key.** DRM attaches at the single
`BindSurfaceHandle` point with a *protected* handle — nothing else in the seam or renderer changes.

The normalized hook is one async license relay over Widevine/PlayReady/FairPlay (**STEAL:** EME
message→update shape), headlessly testable:

```csharp
public sealed record LicenseRequest(DrmSystem System, ReadOnlyMemory<byte> Challenge, string? KeyId, MediaLocus Locus);
public sealed record LicenseResponse(ReadOnlyMemory<byte> License);
// MediaPlayerBuilder.WithDrm(Func<LicenseRequest, ValueTask<LicenseResponse>> licenseRelay)
```

**Status (per `docs/plans/video-drm-layer-design.md`, empirical findings 2026-07-19 — SOLVED/SHIPPED as M5):**

- **PlayReady (native, in-process) is SOLVED and SHIPPED — it is the v1 DRM path.** Protected video renders
  end-to-end in the normal FluentGpu desktop process (no UWP sidecar): a custom CENC `IMFMediaSource` → the
  modern MF-CDM decryptor → decode → a non-zero protected windowless-swapchain handle →
  `IVideoPresenter.BindSurfaceHandle`, composited z-below the UI (captures BLACK — output protection working).
  The pass-1/pass-2 walls (`0xC00D715B`, `SetPMPHostApp E_FAIL`, `DRM_E_LOGICERR`) were cleared **not** by a
  UWP app-model / trusted-cert path but by three stacked native fixes (memory `desktop-playready-solved.md`):
  (1) licensing from the **right server for the content** (the wrong-key license reports USABLE but fails
  downstream); (2) `MFWrapMediaType(MFMediaType_Protected)` + `MF_SD_PROTECTED` — the *wrapped* type is the
  modern EME mechanism (this refines the earlier "never mark protected" finding, which was about the raw
  `MF_MT_PROTECTED` attribute on an *unwrapped* type → the legacy ITA/OTA topology → `0xC00D715B`); (3) a
  **persistent-license** EME session (Axinom's token allows persistence, so a TEMPORARY session rejects the
  license with `MF_TYPE_ERR`). The gecko tree `C:\WAVEE\gecko-dev\dom\media\platforms\wmf\*` is the
  authoritative working reference. This is the DRM code path of the spec's `MfMediaPlayer`; the three fixes are
  guarded by the `FG_CENC_*` A/B env vars kept as diagnostics.
- **License acquisition is the managed `WithDrm` relay — the CDM sees no key or decrypted pixel.** The native
  CDM raises the `KeyMessage`/challenge; a managed `Func<LicenseRequest, ValueTask<LicenseResponse>>` performs
  the license HTTP POST (the app supplies the server + token per source) and returns the license bytes for the
  native `Update()`. The hardcoded `AxinomLicenseUrl`/baked JWT/URL-rewrite were removed from `Helper.cpp`;
  native keeps only CDM/CENC/decrypt. Posture holds: **the engine never sees a decrypted pixel or a content
  key** — protection is enforced entirely below the surface handle by MF/DWM/the GPU (§8).
- **Widevine via WebView2 is an OPTIONAL LATER FALLBACK for genuinely Widevine-only content**, not the v1 DRM
  path. There is no embeddable self-provisioned native Widevine CDM, so Widevine necessarily means hosting a
  Chromium; it stays a lazily-instantiated escape hatch, torn down when idle.
- **FairPlay** (`AVContentKeySession`) is the macOS story, later.

The `WithDrm` relay and the protected-handle attach point mean a new DRM system slots in at `BindSurfaceHandle`
with no surface change. **A DRM capability shortfall is `MediaError{Category.Drm,
Recovery.NeedsLicense/PickLowerQuality}` — never a silent drop to 480p/black.**

### 9.3 Scrub-preview / trickplay

```csharp
public sealed class ScrubPreview { public bool Available { get; } public ValueTask<ImageHandle> ThumbnailAt(TimeSpan t, CancellationToken ct); }
```

A supported track type (sprite / image-VTT / on-the-fly decode), decoupled from the main decode.

---

## 10. System integration — orthogonal opt-in

**FIX:** WinUI auto-SMTC vs `MediaTimelineController` mutual exclusion + undocumented packaged/unpackaged
behavior. **STEAL:** Media Session API shape. Now-playing, PiP, casting, and multi-player sync are independent
composable capabilities.

```csharp
public sealed class NowPlaying
{
    public bool Enabled { get; set; } = true;       // auto-populated from source metadata by default
    public MediaMetadata Metadata { get; set; }     // Title/Artist/Album/Artwork[] (multi-res)
    public void SetPositionState(TimeSpan position, TimeSpan duration, double rate);   // engine keeps it fresh
    public Action? OnNextRequested { get; set; }
    public Action? OnPreviousRequested { get; set; }
    public Action<TimeSpan>? OnSeekRequested { get; set; }
}
public sealed record MediaMetadata(string? Title, string? Artist, string? Album, IReadOnlyList<ArtworkRef> Artwork, MediaKind Kind);
```

The SMTC bridge (`FluentGpu.WindowsApi/Media/`) is **just another consumer** of the headless `IMediaPlayer` —
it subscribes the same signals the control does; macOS `MPNowPlayingInfoCenter` binds identically. PiP is a
`MediaPlayerElement` presentation mode (an engine-composited window), not a new surface. Casting is the honest
URL/relay story (§14 open items).

**Capability bitset** so one control kit drives file/stream/live/cast backends and greys out unsupported
affordances generically (**STEAL:** `IsCommandAvailable`):

```csharp
public sealed class MediaCommands { public IReadSignal<MediaCommandFlags> Available { get; } public bool Can(MediaCommandFlags cmd); }
[Flags] public enum MediaCommandFlags : uint
{ Play=1, Pause=2, Seek=4, SeekBackward=8, SeekForward=16, Rate=32, Next=64, Previous=128,
  SelectAudioTrack=256, SelectTextTrack=512, SelectVideoQuality=1024, StepFrame=2048, PictureInPicture=4096, Cast=8192 }
```

A live stream reports `Available` without `Seek`; the `SeekBar` disables itself off the signal, no per-backend
branching in the control.

---

## 11. Typed error model

**FIX:** opaque HRESULT / nil+log / single `MediaError` / silent DRM downgrade. Silent downgrade is
*unrepresentable*.

```csharp
public sealed record MediaError(
    MediaErrorCategory Category, string Message /* always populated, no nil-error */,
    long? UnderlyingCode /* raw HRESULT / CoreMedia int, preserved */,
    MediaLocus? Locus /* WHICH item/segment/sample */, MediaRecovery Recovery);
public enum MediaErrorCategory : byte { Network, Decode, Drm, UnsupportedCodec, Quota, Source, Lifecycle, Output }
public readonly record struct MediaLocus(int? QueueIndex, MediaSource? Item, TimeSpan? Position, int? StreamIndex, long? ByteOffset);
public enum MediaRecovery : byte { None, Retryable, NeedsUserGesture, NeedsNetwork, NeedsLicense, PickLowerQuality, Fatal }
```

Surfaced on the single `Error` signal. A recoverable stall never becomes `Failed` — it becomes `State==Stalled`
and clears when buffer refills.

---

## 12. Lifecycle & threading

**FIX:** LibVLCSharp "sleep 1s before Dispose" / dispose-during-load crash / event-handler freeze; WinUI
"Shutdown() has been called" races and uncatchable display-callback crashes; mpv reentrant-callback deadlock.

**Guarantees the API makes:**

1. **Engine owns lifetime.** A `MediaPlayer` from `UseMediaPlayer` is disposed (async) on component unmount by
   the reconciler — no manual `IDisposable` race pushed to the app. Standalone players are `IAsyncDisposable`;
   disposal is **always safe at any state**, including mid-open (the open `ValueTask` completes as canceled,
   never crashes). No `sleep`, no finalizer reliance; audio-thread-ordered teardown.
2. **Reuse-vs-recreate is crisp.** A player is reusable across sources indefinitely (`Play(newSource)` swaps
   atomically, coalescing with any in-flight open). Only a **backend switch** — a source whose `MediaKind`
   differs from the current inner backend (PCM ↔ MF), or adding DRM that needs a fresh CDM — recreates the
   inner `IMediaPlayer`/`IMediaSession`, transparently behind the stable facade.
3. **Declared safe context.** Every signal write and user callback is marshaled onto a documented context:
   signal/state updates land on the **UI thread** (via the existing `HostDispatch.Post` poster, the same
   mechanism `ImageCache.Pump` uses); byte-source/feed callbacks land on a **firewalled media-IO thread,
   non-reentrant by construction** — calling back into the player from inside them is always safe (**FIX:**
   libmpv `stream_cb` deadlock). Every ComPtr stays render-thread-confined (video) or audio-RT/cold-device-thread
   confined (WASAPI) per the engine's threading model; the control thread touches **zero** device COM.
4. **No uncatchable teardown crash reaches or bypasses app handlers.** Backend faults become a `MediaError` on
   the `Error` signal; teardown never throws.

**Thread-ownership at a glance** (obeys `hardened-v1-plan.md §2` plus a third RT thread):

| State | Sole writer |
|---|---|
| `IMediaPlayer` signals, queue model, `PreparedSlot`, `EffectSpec`, param signals | **CONTROL thread** (UI/app-loop) |
| published `AudioGraph` node[], `MixVoice` list, smoothed `AudioParam`, ring drains, `IAudioClock` counters, drained flag | **AUDIO RT thread** (WASAPI feed) — copy+mix ONLY |
| per-source decode/decrypt/network fill, SPSC ring producer, key prefetch, **ring dispose + inner-decoder seek** | **WORKER pool** |
| the published `RingEntry[]` ring table (rebuild) | **CONTROL ∪ WORKER under the table lock** (RT reads snapshots only) |
| every WASAPI/DComp ComPtr | **AUDIO RT + cold DEVICE thread** (audio) / **RENDER thread** (video) |

As-built refinement (M4): the `MixVoice` list is mutated on the **render thread** only — control-side voice changes
(`SetVoice`/`AddCrossfadeVoice`/`SetVoiceEnvelope`) go through a mixer-command SPSC drained at the top of the render
block. Ring **dispose** is the **worker**'s alone (both retire queues); the "all voices drained" signal the control
state machine reads is an **RT-published flag**, never a read of the render-thread-owned voice list.

---

## 13. Portability — one surface, two backends, two platforms

The entire public surface lives in TerraFX-free `FluentGpu.Engine/Media/`; only the leaf seams are per-platform.

| Public concept | Windows (today) | macOS (later) |
|---|---|---|
| Video `IMediaSession.OpenAsync` | `IMFMediaEngineEx` windowless swapchain (`VideoMediaEngine`) | `AVPlayer` + `AVPlayerItem` |
| Video delivery | `CompositedSurface` via `IVideoPresenter.BindSurfaceHandle` (DComp, no HWND) | `AVPlayerLayer` id, or `SharedTexture` via `copyPixelBuffer(forItemTime:)` |
| Audio sink (opened once; RT feed) | `Wasapi/` `IAudioClient`/`IAudioRenderClient` (hand-vtable), MMCSS Pro Audio | CoreAudio `AudioUnit` (RemoteIO) render callback, or `AVAudioSourceNode` |
| Audio clock (played-frames master) | `IAudioClock`/`IAudioClock2` + `GetStreamLatency` | CoreAudio `AudioTimeStamp` (`mSampleTime`/`mHostTime`) |
| Device-watcher (follow-default) | `IMMNotificationClient` (`[GeneratedComInterface]`, cold) | `kAudioHardwarePropertyDefaultOutputDevice` listener |
| `IMediaByteSource` | engine-owned coalescing over the sync seam | same seam, honest — no scheme magic |
| DRM license relay | PlayReady native in-process MF-CDM (SHIPPED, §9.2) via the managed `WithDrm` relay / Widevine-WebView2 optional fallback | FairPlay `AVContentKeySession` |
| Now-playing | SMTC (`FluentGpu.WindowsApi/Media/`) | `MPNowPlayingInfoCenter` |

The whole DSP graph, mixer, queue, envelopes, trim, EQ math, and published-value discipline are portable C#
over POD + `Span<float>`. The AVAudioEngine port is deliberately a **source-node** shape (`AVAudioSourceNode`
render block), NOT an attach/detach node graph — we own the DSP graph as a published value and hand CoreAudio
only a render callback, sidestepping AVAudioEngine's mutate-live-graph exception hell by construction.
`IVideoPresenter` already abstracts the OS surface by *id only*, so the video path is portable today.

---

## 14. End-to-end usage examples

**(A) Simplest URL** (auto SMTC/buffering/default tracks; degrades gracefully; never crashes):

```csharp
sealed class VideoCard : Component
{
    public required string Url { get; init; }
    public override Element Render()
    {
        var player = UseVideo(() => MediaSource.FromUri(Url));
        return new MediaPlayerElement { Player = player };
    }
}
```

**(B) Local file + external subtitle with a sync offset** (engine GPU text stack renders the subs):

```csharp
sealed class MoviePlayer : Component
{
    public required string FilePath { get; init; }
    public required string SrtPath { get; init; }
    public override Element Render()
    {
        var player = UseMediaPlayer();
        UseEffect(() =>
        {
            var src = MediaSource.FromFile(FilePath).WithExternalSubtitle(SubtitleSource.FromFile(SrtPath));
            _ = player.Play(src);
            var sub = player.Tracks.Text.FirstOrDefault(t => t.Role == TrackRole.Subtitles);
            if (sub is not null) player.Tracks.SetSyncOffset(sub, TimeSpan.FromMilliseconds(-250));
        }, FilePath, SrtPath);
        return new BoxEl { Direction = 1, Children = new Element[] {
            new MediaPlayerElement { Player = player, Stretch = MediaStretch.Uniform },
            new TrackPicker { Tracks = player.Tracks },
        }};
    }
}
```

**(C) Custom pull byte-stream (encrypted local vault) with typed errors** — the app writes ONE seam; the engine
owns coalescing/prefetch/firewall:

```csharp
sealed class VaultByteSource : IMediaByteSource
{
    readonly VaultHandle _h;
    public long? Length => _h.PlainLength;             // known here, but null is allowed
    public SourceCaps Caps => new() { Seekable = true, KnownLength = true };
    public bool TryOpen(in DataSpec s) => _h.Open(s.Position);
    public int  Read(Span<byte> dst) => _h.DecryptInto(dst);   // firewalled thread; reentry into player is safe
    public long Seek(long o) => _h.Seek(o);
    public void Cancel() => _h.Abort();  public void Close() => _h.Dispose();
}

sealed class VaultPlayer : Component
{
    public required VaultHandle Handle { get; init; }
    public override Element Render()
    {
        var player = UseMediaPlayer();
        UseEffect(() => { _ = player.Play(MediaSource.FromPull(new VaultByteSource(Handle))); }, Handle.Id);
        return new BoxEl { Direction = 1, Children = new Element[] {
            new MediaPlayerElement { Player = player },
            new BoundBanner {
                Visible = Prop.Of(() => player.Error.Value is not null),
                Text    = Prop.Of(() => player.Error.Value is { } e ? $"{e.Category}: {e.Message} ({e.Recovery})" : ""),
            },
        }};
    }
}
```

**(D) Adaptive stream, quality/track selection, auth header injection** (music-video workload):

```csharp
var player = UseMediaPlayer(b => b
    .WithNetwork(new NetworkOptions(OnRequest: r => { r.Headers.Add("Authorization", Auth.Bearer()); return r; }))
    .WithBuffering(new BufferPolicy { TargetForward = TimeSpan.FromSeconds(30) })
    .WithAbr(AbrPolicy.Auto));                 // manual pin still available
// player.Qualities is a read-only variant list; TransportBar greys out Seek on live via the capability bitset.
```

**(E) Local audio file with a 5-band EQ** (each band gain is a signal — a slider write ramps smoothly, no zipper):

```csharp
sealed class LocalPlayerCard : Component
{
    public required string FilePath { get; init; }
    public override Element Render()
    {
        var player = UseMediaPlayer();                                    // routed to PcmAudioPlayer
        UseEffect(() => { _ = player.OpenAsync(MediaSource.FromFile(FilePath).WithKind(MediaKind.PcmAudio)); _ = player.PlayAsync(); }, FilePath);
        var eq = player.Effects.Equalizer;
        UseEffect(() => eq.Apply(EqPreset.FiveBand(defaults: true)), Array.Empty<object>());   // 31/125/500/2k/8k Hz
        return new BoxEl { Direction = 1, Children = new Element[] {
            new TextEl("") { Text = Prop.Of(() => Fmt.Clock(player.Position.Value, player.Duration.Value)) },
            new BoxEl { Direction = 0, Children = eq.Bands.Select(b => (Element)new Slider { Min=-12, Max=+12, Value = b.GainDb }).ToArray() },
        }};
    }
}
```

**(F) A gapless album** (encoder-delay trim per source makes joins sample-accurate across every codec):

```csharp
var player = UseMediaPlayer();
UseEffect(() =>
{
    player.Effects.CrossfadeMs.Value   = 0f;             // 0 == gapless (overlap 0, butt-join, trimmed PCM)
    player.Effects.Normalization.Value = NormMode.Album;  // preserves inter-track dynamics
    _ = player.OpenAsync(MediaSource.FromFile(tracks[0]));
    for (int i = 1; i < tracks.Length; i++) player.Enqueue(MediaSource.FromFile(tracks[i]));  // prerolls off the sample clock
    _ = player.PlayAsync();
}, tracks);
```

**(G) Crossfade with prepare-next** (a Seek re-prepares the slot — the scar is fixed):

```csharp
var player = UseMediaPlayer();
player.Effects.CrossfadeMs.Value    = 8000f;              // 8 s equal-power overlap
player.Effects.CrossfadeCurve.Value = CrossCurve.EqualPower;
UseEffect(() => { _ = player.OpenAsync(MediaSource.FromFile(current)); _ = player.PlayAsync(); }, current);
UseEffect(() => { player.PrepareNext(MediaSource.FromFile(next)); }, next);   // manual verb; scheduler also auto-prepares
// SeekBar.OnCommit → player.SeekAsync(...) invalidates + re-prepares the slot; crossfade still fires.
```

**(H) A Spotify track via `DecryptingSource` + ReplayGain** (decrypt invisible to decode/DSP/mixer):

```csharp
sealed class SpotifyTrackPlayer : Component
{
    public required StringId TrackUri { get; init; }
    public required IAudioKeyProvider Keys { get; init; }   // app-supplied (PlayPlay); internals out of scope
    public override Element Render()
    {
        var player = UseMediaPlayer();
        UseEffect(() =>
        {
            var bytes  = new DecryptingSource(SpotifyChunkSource.Open(TrackUri), Keys.CipherFor(TrackUri)); // key OUT-OF-BAND, not in Read
            var source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);
            player.Effects.Normalization.Value = NormMode.Album;
            player.Effects.ReferenceLufs.Value = -14f;
            _ = player.Play(source);
        }, TrackUri);
        return new BoxEl { Direction = 1, Children = new Element[] {
            new NowPlayingView { Player = player },
            new BoundSpinner { Visible = Prop.Of(() => player.IsBuffering.Value) },     // mixer fades-to-silence, never clicks
            new BoundBanner  { Visible = Prop.Of(() => player.Error.Value is not null),
                               Text    = Prop.Of(() => player.Error.Value?.Message ?? "") },
        }};
    }
}
```

**(I) A music video** — routes to `MfVideoOrFile`; MF owns its own audio+video, presentation clock, and DComp
sibling visual. It does **not** enter the PCM graph (v1 gives MF audio no graph EQ), so there is no cross-backend
sync problem:

```csharp
var player = UseVideo(() => MediaSource.FromUri(musicVideoUrl, net).WithKind(MediaKind.MfVideoOrFile));
return new MediaPlayerElement { Player = player, Stretch = MediaStretch.Uniform };
```

---

## 15. Relationship to existing code + the Wavee rework

- **`IVideoPresenter` / `VideoMediaEngine` slot in as the video backend unchanged.** `MfMediaPlayer` is a thin
  `IMediaPlayer` over `VideoMediaEngine`; Path A `CompositedSurface` handoff is exactly today's
  `BindSurfaceHandle` flow. The `SetMultithreadProtected(TRUE)` requirement (PROVEN in `VideoMediaEngine.cs`) is
  preserved. No spine change; DRM attaches later at the same handle.
- **The app's `AudioPlayEngine`/`CrossfadeMixer` re-parent under `PcmAudioPlayer`.** Their proven shapes are the
  design targets: `CrossfadeMixer.MixEqualPower` becomes the seam `CrossfadeMixer`; `WasapiRenderer.ReleasedFrames`
  becomes the formal `IAudioClockSource` master; the existing fade-envelope-in-`ReleasedFrames`-domain becomes
  the `GainEnvelope`/clock contract. **One source of truth for position, no de-sync** — the app engine stops
  owning its own clock and consumes the seam's. The engine designs the seam; **the app team wires the internals**
  (PlayPlay chunk fetch, key rotation, `AudioHost`) — those stay app-private and fenced.
- **The app-side rework binds to the unified surface.** The Wavee player UI binds `IMediaPlayer` signals; PlayPlay
  becomes a `DecryptingSource` at the front of a `MediaKind.PcmAudio` source; lyrics slave to the audio
  `IPlaybackClock` produced by `PcmAudioPlayer.Position` (`media-pipeline.md §9`). The current chrome-only
  `MediaPlayerElement` mockup is replaced by the §4.3 control.

---

## 16. Phased implementation plan (milestone-by-milestone)

Each milestone lands behind its gate before the next; the build-order rule (`hardened-v1-plan.md §6`) applies —
**single-thread-correct first, then flip parallelism behind a green race gate.**

**Status (2026-07-19): M0–M5 are LANDED and green** (Engine.Tests 91, Windows.Tests 59; the solution builds
clean). The as-built regime moved off the `FluentGpu.VerticalSlice` harness onto two new xUnit-v3 unit-test
projects — `src/FluentGpu.Engine.Tests` (portable engine: state machine, source model, audio graph, error
model, zero-alloc via `GC.GetAllocatedBytesForCurrentThread()` delta==0) and `src/FluentGpu.Windows.Tests`
(Windows backend: DRM relay marshaling, ABI struct layout, device-watcher). **The execution plan reordered
this M-list:** DRM (M5) was **pulled forward** from "blocked/deferred" into a real, gated milestone (native
in-process PlayReady is now the shipped DRM path, §9.2), and a cleanup milestone **M1.5** (delete the dead UWP
sidecar + Module system) was inserted after M1.

**Two cross-cutting invariants folded in as of the landed build:**

- **Cross-backend mixed queue (audio ↔ DRM-video) is a v1 requirement.** A queue may interleave a PCM-audio
  track A and a DRM-video B (a `VideoAssociation`); one `IMediaPlayer` plays `[A, B]`, the `MediaRouter`
  swapping the inner backend by `MediaSource.Kind` and the UI binding identical signals throughout. **A↔B is a
  declicked HARD CUT, never a cross-backend audio crossfade** (the two engines never co-mix — this removes the
  cross-backend A/V-sync problem; gapless/crossfade stays audio→audio inside `PcmAudioPlayer`).
  **Cross-backend PREROLL** extends M3's audio-only preroll: while A plays, the `PlayQueue` scheduler prepares
  the next item on *whichever* backend it routes to — for a DRM video that means spinning up the MF/CDM
  session, running the `WithDrm` relay, and decoding B's first protected frame ahead of time, so B binds its
  protected surface the instant A ends (no spinner, no black gap). This is a queue/router-level `PreparedSlot`
  generalization across backends (folded into M3 + M5 + M6), not just the audio voice preroll.
- **`PositionSeconds` is a lossy one-way UI projection; the authoritative timeline is integer.** The
  `FloatSignal PositionSeconds` exists only for hot, alloc-free node binding (sub-millisecond for real content;
  ≈1 ms only past ~2.3 h) and is **never the source of truth.** The authoritative timeline is integer: audio =
  WASAPI played-frames (`long`, sample-accurate, §7.6); video = MF presentation clock in 100-ns `TimeSpan`
  ticks. Every precision-critical operation (`SeekAsync` takes `TimeSpan`; gapless trim; crossfade start-frame;
  lyric-cue sync; the cross-backend preroll boundary) computes in the frame/tick domain. `Position` is exposed
  as `IReadSignal<TimeSpan>` (100-ns) for exact-ms logic. **Never round-trip a precision-critical value through
  the float.**

**M0 — the seam + signals (portable, no backend). NET-NEW. ✅ LANDED (green).**
`IMediaPlayer`, `MediaPlayer` facade + `MediaRouter`, `MediaSource` + algebra, all signal types + POD state,
`MediaError`, the state machine, `MediaSignalSink`, the seam interfaces (`IMediaByteSource`/`IMediaFeed`/
`IMediaSampleSource`/`IAudioDecoder`/`IAudioSink`/`IAudioClockSource`/`IDeviceWatcher`/`IMediaBackend`/
`IMediaSession`/`VideoDelivery`), the `WithDrm` `LicenseRequest`/`LicenseResponse` relay types, and a
`HeadlessScriptedPlayer` (fed by a scripted `IMediaSampleSource`) exercising transitions with no GPU. All in
TerraFX-free `src/FluentGpu.Engine/Media/`. *Gate (as-built):* the M0 checks moved to
`src/FluentGpu.Engine.Tests` (state-machine transitions, zero-alloc position sampling, coalescing-transport
supersession, callback-firewall reentrancy) — **NOT** `FluentGpu.VerticalSlice` (user directive; do not add
media gates there). Green.

**M1 — the MF video backend on the working spine. Mostly PROVEN. ✅ LANDED.**
`MfMediaPlayer`/`MfMediaSession` over `VideoMediaEngine`; `VideoSurface` signal → the real `MediaPlayerElement`
hole-punch → `IVideoPresenter.Place/Commit` at phase 11. Replaced the chrome-only `MediaPlayerElement` with the
§4.3 control; fixed the `DCompVideoPresenter` z-below/hole-punch regression. Depends on: the shipping spine
(proven) + M0. *Gate:* `--screenshot` shows a real decoded frame in the hole-punch rect; audio-only degrade
renders chrome; transport never crashes across OS versions.

**M1.5 — remove the dead UWP sidecar + Module system. ✅ LANDED.**
The UWP AppContainer sidecar and the `IModule`/`ModuleManager` installable-capability system (built only to
support the sidecar) were **deleted** — protected video now runs through the in-process
`DesktopProtectedVideoPlayer` folded into `MfMediaPlayer` (M5). *Gate:* the solution builds clean; the native
DLL still builds + plays; no `Module*`/`Sidecar*`/`UwpVideo` symbols remain.

**M2 — the audio-graph backend + WASAPI clock (single-thread-correct). NET-NEW (proven shapes). ✅ LANDED (green).**
`PcmAudioPlayer` 5-stage pull graph + `CrossfadeMixer` + `IDspStage` nodes + `GaplessInfo` trim + EQ/ReplayGain
nodes + the `AudioGraphHost` atomic-swap + `RenderInFlightDepth+1` quarantine published-value discipline,
driven single-thread by a synthetic clock / null sink; `IAudioSink`/`IAudioClockSource` WASAPI leaf in
`src/FluentGpu.Windows/Wasapi/`; device opens once; `Position` derived + `IsValid`-gated. *Gate (as-built):*
golden-PCM diffs (crossfade math, gapless trim, EQ coefficients, mixer sum) + the `[Conditional]` RT tripwire
in "pull N frames" mode, all in `src/FluentGpu.Engine.Tests`. Depends on: M0.

**M3 — queue, gapless, crossfade, effects/EQ, preroll, decrypt seam. NET-NEW. ✅ LANDED (green).**
`PlayQueue` + `VoiceScheduler` + `PreparedSlot` (seek-invalidated `Epoch` — the `crossfade-prepared-next`
scar fix) + `ScheduledTransition`; live `IAudioEffects` (EQ bands, `CrossfadeMs`, `NormMode`, `ReferenceLufs`,
visualizer Tap); `DecryptingSource` decorator + `IAudioKeyProvider` seam; the cross-backend `IPreparableBackend`
prepare hook (the cross-backend preroll generalization above). *Gate (as-built):* gapless/crossfade golden-PCM;
a Seek mid-preroll re-prepares and the crossfade still fires; `DecryptingSource` round-trips plaintext; param
ramps show no zipper under the RT tripwire — in `src/FluentGpu.Engine.Tests`. Depends on: M2.

**M4 — flip the RT thread (parallelism behind the race gate). NET-NEW. ✅ LANDED (green).**
The WASAPI feed callback became the real RT thread on a dedicated MMCSS RT thread behind a green race gate
(graph render + ring drain moved onto it; the retire ring made genuine SPSC); `IAudioClock` polling +
`Position` publish moved to the non-RT clock-poll tick; the device-loss state machine (`IMMNotificationClient`,
`IDeviceWatcher`) + sink rebuild moved to the cold device thread. Nothing in the signal model / queue /
`EffectSpec` / param signals moved. *Gate:* the seam **race gate** green — now covering `SetVoice`, crossfade
commit, and seek each racing a **live feed** (published ring table vs RT/worker snapshots; worker-only ring dispose
and inner-decoder seek; dispose under a blocked worker never frees a ring under it), not only graph publish;
default-device change rebuilds only the sink under a live graph (sources/queue/position survive); underrun writes
silence + bumps xrun. Depends on: M3.

**M5 — DRM: native in-process PlayReady via the `WithDrm` relay. UNBLOCKED. ✅ LANDED (pulled forward).**
Native in-process PlayReady productionized into `MfMediaPlayer`'s DRM code path (§9.2): the generalized native
ABI (`FgPlayReadyRunEx` — a struct-based open carrying the source descriptor + a license-callback fn-ptr,
replacing the baked-vector `FgPlayReadyRun`); license acquisition moved out of `Helper.cpp` into the managed
`WithDrm` relay via `DrmLicenseBridge` + a `[UnmanagedCallersOnly]` thunk (native raises the challenge →
managed `LicenseRequest` → app callback → `LicenseResponse` bytes → native `Update()`); `VideoSurface` binds
the protected handle at the unchanged `BindSurfaceHandle`. The three proven native fixes (§9.2) + the
`FG_CENC_*` A/B switches are preserved. **Widevine via WebView2 remains only an optional later fallback for
Widevine-only content**, not gated here. *Gate:* the gallery `playready-video` page plays protected video
(black-in-capture) through `IMediaPlayer` with the app supplying the license via a `WithDrm` relay; a bad
license surfaces a typed `MediaError{Category.Drm}`; clear video still plays. Relay marshaling / state mapping /
ABI struct layout are gated in `src/FluentGpu.Windows.Tests` (the CDM behavior itself is an on-box check).
Depends on: M1.

**M6 — the Wavee migration. Integration.**
Re-parent `AudioPlayEngine`/`CrossfadeMixer` under `PcmAudioPlayer`; bind the Wavee player UI to `IMediaPlayer`;
PlayPlay behind `DecryptingSource`; lyrics on the audio `IPlaybackClock`. *Gate:* one source of truth for
position (no de-sync); the app's existing crossfade/gapless feel-tests pass on the unified surface. Depends on:
M2–M4; app-team-owned internals.

**Milestone numbering note (2026-07, G7 reconcile).** The canonical milestone list is **M0–M6** (plus the
inserted **M1.5**). An early commit (`f247613`, "Add unified Media Playback API (M0–M5, M7)…") carried an
**`M7`** label for a then-anticipated *system-integration* milestone (SMTC / lock-screen / media-keys). That
number is **superseded** — the SMTC surface landed **app-side in the WaveeMusic sweep** (commit `d308987`,
"G6c: SMTC wired end-to-end"), driven from the app's unified `NowPlayingProjection` (the Spotify-Connect
LOCAL+REMOTE fold), so it is part of M6 integration rather than a separate engine milestone. There is no engine
"M7". **Residual seam gap (tracked, not a milestone):** `SystemMediaControls` (`FluentGpu.WindowsApi/Media/`)
exposes one-way timeline push (`UpdateTimeline`/`UpdatePosition`) but no
`PlaybackPositionChangeRequested` event, so lock-screen scrub-drag is display-only; wrapping
`add_PlaybackPositionChangeRequested` + routing a seek is the open follow-up.

**Dependencies / proven-vs-net-new summary.** Proven and reused: the `IVideoPresenter` spine, `VideoMediaEngine`
+ `SetMultithreadProtected`, the app's `CrossfadeMixer`/`ReleasedFrames` shapes, the signals core, the
PUBLISH→consume + quarantine seam, the alloc-tripwire regime. Net-new: the unified `IMediaPlayer` seam + router,
`PcmAudioPlayer`'s graph + RT thread split + its own tripwire, `PreparedSlot`/queue, `DecryptingSource` seam,
the `MediaError` model. **DRM SOLVED (M5, landed):** native in-process PlayReady is the shipped DRM path via
the managed `WithDrm` relay — no longer blocked (§9.2).

---

## 17. Open questions / risks

- **Sync vs async `IMediaByteSource`.** This spec picks the synchronous `read(2)` seam as canonical (the shape
  `DecryptingSource` composes over and the worker firewalls). Confirm no video-side backend needs a truly async
  pull that the convenience layer can't bridge; if one does, the seam stays sync and the async producer plugs in
  at `FromStream`/`FromFeed`.
- **Native PlayReady is SOLVED and shipped (M5).** It renders end-to-end in-process (no UWP sidecar, no
  trusted PMP/PRND cert, no package identity) — the three stacked native fixes (right license server /
  `MFWrapMediaType(Protected)`+`MF_SD_PROTECTED` / persistent-license session; §9.2 + memory
  `desktop-playready-solved.md`) cleared the `0xC00D715B` / `SetPMPHostApp E_FAIL` / `DRM_E_LOGICERR` walls the
  earlier passes hit. License acquisition is the managed `WithDrm` relay. Remaining open item: generalize the
  CENC demuxer / PSSH-KID parsing from the single-key Axinom test vector to Spotify's segmented protected video
  (the app-side manifest→MPD synthesis exists in Wavee to port). Widevine-WebView2 stays an optional later
  fallback for Widevine-only content.
- **`SharedTexture` (Path B) video.** Designed-in but the shipping backend uses `CompositedSurface`; Path B lands
  when a decoder that yields textures directly (custom `IMediaSampleSource` → engine decode) is wired — needed
  for correct HDR/16-bit and per-vsync `copyPixelBuffer`-style delivery.
- **Multi-player frame sync** (`MediaTimelineController` equivalent) — proposed as an orthogonal
  `MediaClockGroup { Attach(IMediaPlayer) }`; must stay independent of SMTC (the WinUI trap). Deferred.
- **Casting** — honest URL/relay-only: `FromUri` casts directly; `FromPull`/`FromFeed`/`FromSamples` require a
  documented local HTTP shim (raw callback bytes can't be cast). Scope the shim.
- **Time-stretch latency reporting.** `TimeStretchStage.LatencySamples` changes with `Rate`; verify the §7.6
  position re-sum keeps the progress bar honest across rate ramps under the golden-PCM harness.
- **ABR policy default.** Ship a non-oscillating auto with a manual pin + max-bitrate cap; the `IAbrPolicy` seam
  (Shaka `AbrManager` / dash.js rule-pipeline as reference) is the override.

---

*Grounding note for whoever commits this: real engine types — `Signal<T>`/`FloatSignal`/`Prop<T>`/`Memo<T>`/
`IReadSignal<T>` in `src/FluentGpu.Engine/Foundation/Signals/`; `IVideoPresenter`/`VideoSurfaceId` in
`src/FluentGpu.Engine/Seams/Pal/IVideoPresenter.cs`; the working `VideoMediaEngine`
(`src/FluentGpu.Windows/Media/VideoMediaEngine.cs`, `SetMultithreadProtected` at line ~174); the chrome-only
`src/FluentGpu.Controls/MediaPlayerElement.cs` that §4.3 replaces; DRM findings in
`docs/plans/video-drm-layer-design.md`; the video spine in `docs/plans/video-compositing-spine-design.md`; the
lyrics playback clock in `design/subsystems/media-pipeline.md §9`. On landing: update `media-pipeline.md`,
register the new seams in `SPEC-INDEX.md §2` + `subsystems/README.md`, run `design/check-canon.ps1`.*
