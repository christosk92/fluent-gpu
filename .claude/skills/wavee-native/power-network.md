# Power + metered-network policy (Wavee)

App-side wiring over `FluentGpu.WindowsApi.Power` / `Network`. Not a WindowsApi pillar change.
Distinct from `AmbientPowerPolicy` (render cadence) — do not conflate.

## KeepAwake threading

`PowerSession.KeepAwake` is **per-thread** (`SetThreadExecutionState`). Create and dispose the handle on the **same UI thread**. Disposing on another thread clears *that* thread's flag and leaks the original request until the acquiring thread exits.

`PowerBridge` acquires/disposes only from:

- the auto-tracked `WaveeApp` effect (`PowerBridge.SyncFromSignals`) — UI thread
- the suspend/resume handlers after they hop through the stored `post` delegate

OS `Suspending` / `Resumed` fire on a power-broadcast worker. Handlers must not block and must not touch KeepAwake or `IPlaybackPlayer` until after the hop. Fail-soft: nothing thrown out of an OS callback.

### Rules

| State | KeepAwake | `keepDisplayOn` |
|---|---|---|
| Not playing | disposed | — |
| Playing audio | held | `false` (screen may dim) |
| Playing + fullscreen video (`VideoSurface.Requested` or `Live` is `Fullscreen`) | held | `true` |

Edge-triggered on `IsPlaying` / fullscreen flips. No per-frame work.

## Suspend flush order

On `Suspending` (after UI hop):

1. Drop KeepAwake (do not fight the OS sleep).
2. `IPlaybackPlayer.PauseAsync` (from `PlaybackBridge.Player` — no PlaybackBridge.cs edit).
3. `SessionSnapshotStore.FlushActive()` — the shell-owned instance registers itself as `Active` in its constructor/`Init`.

Pause first so a later playback-snapshot writer sees `Paused`; then fsync the document. Flush is fail-soft and never throws.

## Connect re-announce

**TODO.** `DeviceStatePublisher.PublishAsync(PutStateReasonKind.NewConnection)` is private. `LiveConnect` holds the publisher privately; the only public publish is `RepublishPlayerState` (`PlayerStateChanged`), which is not a NewConnection announce. `PowerBridge.ReannounceConnect()` is the hook — wire it when Connect exposes `AnnounceNewConnection()`. Reachable instance: `Services.LiveHost.Connect`.

## Metered cap math

`NetworkPolicy` caches the last `NetworkStatus.ReadCostAsync()` snapshot (Install + 60 s timer + NLM connectivity change). Not per-frame. Fail-soft to `NetworkCost.Unknown` (unmetered-conservative — a probe failure never throttles).

```
EffectiveQuality(userQuality, cap):
  q   = clamp(userQuality, 0, 2)
  cap = clamp(cap, 0, 2)
  if cost.Kind is Fixed or Variable:  return min(q, cap)
  else:                               return q
```

`WaveeSettings.MeteredQualityCap` default **1 = High160**. Applied only when metered. `AudioPlaybackStack` resolve lambda uses `NetworkPolicy.EffectiveQualityPreference(settings)` so the cap is enforced from the next track.

Signatures:

- `int EffectiveQuality(int userQuality, int meteredCap)`
- `int EffectiveQuality(IAppSettings settings)`
- `int EffectiveQuality()` — settings captured at Install
- `AudioQualityPreference EffectiveQualityPreference(IAppSettings settings)`

## IsMetered: quiet, not a nag

`NetworkPolicy.Metered` (`Signal<bool>`) drives a helper line on Settings ▸ Playback ▸ "On metered connections". Never a dialog, toast, or blocking prompt.

## Prefetch

- **Gated:** `FastTrackPlayback.Warm` (public `IFastTrackWarmer` kick in `AudioPlaybackStack.cs`) no-ops when `NetworkPolicy.ShouldDeferPrefetch`.
- **Not gated:** `DiscographyPrefetcher.RunAsync` is started from `LiveSessionHost` (not owned here). No public prefetch hook on that path without editing the session host.

## Install call site

`WaveeApp` mount effect, immediately after `PlaybackBridge.Activate`.
