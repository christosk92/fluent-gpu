---
name: wavee
description: Wavee Spotify desktop client under src/apps/ (Wavee, Wavee.Core, Wavee.Tests). Use for app architecture, seams, playlist mutations, and build/test commands. Engine work belongs in the repo-root fluentgpu skill.
---

# Wavee app

Scope: `src/apps/Wavee/**`, `src/apps/Wavee.Core/**`, `src/apps/Wavee.Tests/**` only.

## Build & verify

```powershell
dotnet build src/apps/Wavee/Wavee.csproj
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj
```

Architecture hub: `docs/plans/wavee/` (see `wavee-native-backend-architecture.md`).

## Wiring discipline (mandatory)

Read [wiring-discipline.md](wiring-discipline.md) before any seam/composition-root change. **Never** use optional nullable dependencies with `?? Task.CompletedTask` or empty-string defaults on hot paths.

## Sub-skills

- [wiring-discipline.md](wiring-discipline.md) — required deps, fail-loud stubs, go-live hooks
- [home-layout.md](home-layout.md) — Home visibility + order (`home-layout.json`, reducer,
  `HomeLandingProjection`, `HomeCustomizerPage`). Read before adding a landing module.
- [session-restore.md](session-restore.md) — `session.json`: nav stacks **and** the playback session. Write gates,
  the cluster → snapshot → paused restore order, and the uid→uri→index→head identity ladder. Read before touching
  restore, `PlaybackController`'s recovery paths, or anything that decides what plays at launch.
- [audio-handoff.md](audio-handoff.md) — gapless & crossfade: one `IAudioClient` per queue, the 0 ms butt-join vs the
  overlap path, prepared-next timing, codec pre-roll trim, and how to read the `[gapless]` log. Read before touching
  `SpotifyLive/Audio/**` or prepared-next scheduling.
- [palette-shortcuts.md](palette-shortcuts.md) — the Ctrl+K command palette registry, the shortcut table, `Announcer`.
- [bridges.md](bridges.md) — the OS-mirror bridges hanging off `PlaybackBridge` (SMTC, taskbar, jump list).
- [deep-linking.md](deep-linking.md) — the `wavee://` verb map and the one activation entry every surface routes through.
- [receipts.md](receipts.md) — the About "Wavee right now" perf receipts and the GPU-vs-app memory split.
- [focus-pitfalls.md](focus-pitfalls.md) — programmatic focus: focus the editable node, not its chrome.
- **`wavee-sidebar` skill** (`.claude/skills/wavee-sidebar/`) — the left sidebar: the three designs as documents
  over ONE `SidebarPane` renderer, the layout document/reducer/persistence, the projection→binder→planner
  pipeline, the customizer, and the extension registries. Read it before touching `Features/Sidebar/**`,
  `Wavee.Core/Sidebar/**` or `Actions/Extensibility/**`.
- **`dnd` skill** (`.claude/skills/dnd/`) — **drag & drop**, engine and app. Read it before touching
  `Features/DragDrop/**` (`WaveeResourceDrag` payloads/commit seams, the engine-free `WaveeDragRules` /
  `TabDropRules` decision tables, the chip model, the insertion preview) or any surface that declares a
  `Drag.Source` / `Drop.Target` / `InsertionOptions` / `Reorderable` — the detail track list, the tab strip, the
  player bar, the queue panel and the sidebar rows all do.
- `wavee-playlist-mutations/` — Spotify playlist editing (when present)
