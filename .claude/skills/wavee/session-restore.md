# Wavee session restore

Reopen where you left off: the last nav stack (and, later, playback) round-trips through `%LOCALAPPDATA%\Wavee\WaveeMusic\session.json`, beside `history.json` / `play-log.json`.

Source: `src/apps/Wavee/App/SessionSnapshotStore.cs`. Shell wiring: `src/apps/Wavee/Features/Shell/WaveeShell.cs`. Deep-link verbs: [deep-linking.md](deep-linking.md).

Playback **write/consume is not in this wave** — the DTO is schema-stable so a later agent can fill `UpdatePlayback` / a restore consumer without bumping `Version`.

## Snapshot schema (v1)

JSON, camelCase, nulls omitted. `version` is `1`.

```json
{
  "version": 1,
  "nav": {
    "active": { "name": "album:spotify:album:…", "arg": "Random Access Memories" },
    "back": [ { "name": "home" }, { "name": "search", "arg": "daft" } ],
    "forward": [],
    "activeTabId": 3
  },
  "playback": {
    "contextUri": "spotify:album:…",
    "contextKind": "album",
    "trackUri": "spotify:track:…",
    "trackUid": "…",
    "trackIndex": 3,
    "positionMs": 123456,
    "paused": false,
    "shuffle": true,
    "repeatMode": "context",
    "userQueueUris": ["spotify:track:…"],
    "autoplayActive": false,
    "capturedAtUnixMs": 1700000000000
  }
}
```

| Section | Fields | Notes |
|---|---|---|
| `nav.active` | `name`, optional `arg` | Opaque Wavee route key (`home`, `search`, `album:<uri>`, `pl:<uri>`, …) — same pair as `FluentGpu.Controls.Route`. |
| `nav.back` / `nav.forward` | route arrays, **oldest first** | Cap **50**. Overflow drops the oldest (FIFO). In-memory shell stacks stay at 200. |
| `nav.activeTabId` | `OpenTab.Id` | Process-lifetime int. On cold start IDs are reassigned by pinned-tab restore; a miss keeps the pinned selection and still applies the route to the selected tab. |
| `playback` | see JSON | **Optional / nullable.** Schema only until a playback agent writes and consumes it. `repeatMode` is a string (`off` / `context` / `track`). `contextKind` is a string (`album` / `playlist` / …). |

A missing `playback` object is not a fault. Nav-only saves must not wipe a previously persisted playback section (the store keeps the last loaded/written playback DTO in memory).

## Write gates

| API | When | Debounce |
|---|---|---|
| `UpdateNav(route, back, forward, tabId)` | `WaveeShell.Go` / `Back` / `Forward` **after** their existing bookkeeping (`CaptureNav`). Pass the live lists — the store copies into reused 50-slot buffers (zero alloc on the call after warmup). | 2 s (`SaveDebounceMs`), coalesced. |
| `UpdatePlayback(dto)` | **Not wired this wave.** Call from the playback seam (track boundary / pause / seek / queue edit) when that agent lands. | Same 2 s debounce. |
| `Flush()` | Shell unmount cleanup (`UseEffect` + `DepKey.Empty`). **Synchronous** best-effort (cancel debounce, fsync, tmp→replace on the caller thread). No-op when nothing is dirty — a corrupt file is never replaced by an empty document. | none |

Atomic write: serialize → `session.json.tmp` → `Flush(true)` → `File.Move(..., overwrite: true)`. UI thread snapshots; the timer path writes the frozen DTO on the pool (PlayLogStore contract). `Flush` writes on the caller.

**Process-exit Flush is not in WaveeShell.** `Program.cs` owns shutdown and is another agent's file — wire `_session.Flush()` (or a `Services`-held store) there when that agent is ready. Unmount cleanup is the shell's best-effort hook and does not run on every kill.

## Restore order

Normal windowed boot only (probes / `--screenshot` skip the shell path as today):

1. **Settings** — `IAppSettings` is already loaded when `WaveeShell` is constructed.
2. **Pinned tabs** — `RestorePinnedWorkspace()` (ordinary tabs are session-only; pins are the cross-session subset).
3. **Session nav** — `RestoreSessionNav()`: `Load()` then `TryApplyNav` onto `_history` / `_forwardHistory` / `_route` / `_canBack` / `_canForward`, and the matching tab if `activeTabId` still exists.
4. **History log** — `HistoryStore.LoadFromDisk` + `Add` of the landing route (this session's first visit).
5. **Deep links** — first `Render` drains `DeepLinkChannel` (a cold-start `wavee://open` **wins** over the restored route).
6. **Playback restore** — **not this wave.** A later consumer reads `snapshot.Playback` after the player is alive.

Wrapped in try/catch: any restore failure falls back to `home` and empty stacks. Routes self-load from args, so a plain restore is the common path.

## Fail-soft rules

`Load()` never throws.

| On-disk | Result | File | Subsequent writes |
|---|---|---|---|
| Missing | `null` (first run) | untouched | enabled; first `Update*` creates the file |
| Corrupt / unreadable / `version < 1` | `null` | **left in place** (not moved to `.corrupt`, not deleted) | enabled; first successful save **replaces** it |
| `version > 1` (too new) | `null` | left in place | **blocked** (`WritesBlocked`) — a newer build owns the document |

Do not rewrite a corrupt file from `Load`. Do not empty-Flush over a file that was never successfully adopted this process.

## Store API

```csharp
var store = new SessionSnapshotStore();          // optional IWaveeLog
store.Init(SessionSnapshotStore.DefaultPath());  // or a temp path in tests
SessionSnapshotDto? snap = store.Load();         // sync, fail-soft

store.UpdateNav(route, back, forward, tabId);    // internal; WaveeShell is the caller. Debounce 2 s.
store.UpdatePlayback(dto);                       // debounce 2 s; schema-only caller
store.Flush();                                   // sync shutdown / tests (SaveAndWait aliases this)

SessionSnapshotStore.TryApplyNav(snap?.Nav, back, forward, out Route active, out int tabId);
```

Caps: `CurrentVersion = 1`, `MaxStack = 50`, `SaveDebounceMs = 2000`. AOT: `SessionSnapshotJsonCtx` in the same file.

## Deep-link consumer (shell)

`DeepLink.cs` + `DeepLinkChannel` already exist. WaveeShell drains on `Pending` (monotonic `Signal<int>`):

| Verb | Handling |
|---|---|
| `open` | Compose the opaque nav key (`album` + `spotify:album:…` → `album:spotify:album:…`; a full key is passed through) then `GoNav`. |
| `play` | `HandleDeepLinkPlayback` → `PlaybackBridge.Player.PlayAsync(ctx)`. |
| `resume` | `HandleDeepLinkPlayback` → `PlaybackBridge.Player.ResumeAsync()`. |

`HandleDeepLinkPlayback` is the marked playback hook. It no-ops when the playback context is not mounted yet (the effect is registered after `UseContext(PlaybackBridge.Slot)`). Parser / intake contract: [deep-linking.md](deep-linking.md).
