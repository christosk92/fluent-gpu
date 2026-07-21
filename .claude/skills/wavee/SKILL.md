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
- `wavee-playlist-mutations/` — Spotify playlist editing (when present)
