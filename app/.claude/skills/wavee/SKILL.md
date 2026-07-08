---
name: wavee
description: Wavee Spotify desktop client under app/ (Wavee, Wavee.Core, Wavee.Tests). Use for app architecture, seams, playlist mutations, and build/test commands. Engine work belongs in the repo-root fluentgpu skill.
---

# Wavee app

Scope: `app/Wavee/**`, `app/Wavee.Core/**`, `app/Wavee.Tests/**` only.

## Build & verify

```powershell
dotnet build app/Wavee/Wavee.csproj
dotnet test app/Wavee.Tests/Wavee.Tests.csproj
```

Architecture hub: `app/docs/architecture.md`.

## Wiring discipline (mandatory)

Read [wiring-discipline.md](wiring-discipline.md) before any seam/composition-root change. **Never** use optional nullable dependencies with `?? Task.CompletedTask` or empty-string defaults on hot paths.

## Sub-skills

- [wiring-discipline.md](wiring-discipline.md) — required deps, fail-loud stubs, go-live hooks
- `wavee-playlist-mutations/` — Spotify playlist editing (when present)
