# PlayPlay: public/private split

The local playback-runtime package and its supporting material live in a **separate private repo**
(`wavee-playplay-private`), not in this public repo. The public app builds and runs without it; protected
playback is simply unavailable and the UI shows an "install the local package" state.

## Why

Keeps the public `fluent-gpu` repo (the UI engine + app) free of version/arch-specific
reverse-engineering / native-derivation material — out of git, out of AI-agent context, and off the
public GitHub record.

## How it links back in

The build is absence-tolerant. The presence of `app/Wavee.PlayPlay/Client/InProcessPlayPlayKeyDeriver.cs` flips the
`WAVEE_PLAYPLAY_LOCAL` MSBuild symbol (not the sibling csproj alone — a partial junction must not enable code paths
that reference types which never compile); the package's `**/*.cs` + `Protos/playplay.proto` then source-link
into the `Wavee` assembly (`app/Wavee/Wavee.csproj`), and the test project links `Tests/`. With the
package absent, the app compiles against the public seam only: `IPlayPlayKeyDeriver`/`NullPlayPlayKeyDeriver`,
`IPlayPlayProvisioner`/`NullPlayPlayProvisioner`, and the pure DTOs/status enums under
`SpotifyLive/Audio` + `Backend/Audio/Contracts`.

Use the (gitignored) helper to junction the private package in/out:

```powershell
./link-playplay.ps1 -Mode link      # junction the private package -> local DRM build works
./link-playplay.ps1 -Mode status
./link-playplay.ps1 -Mode unlink    # restore the clean/absent default (do this before AI-assisted sessions)
```

Default state is **unlinked/absent** — the clean state agents and CI see.

## Guardrails (do not commit out-of-scope material here)

- `.gitignore` ignores the whole out-of-scope surface (`app/.native/`, `app/Wavee.PlayPlay/`, `app/tmp_*`,
  `scripts/pyghidra*`, `tools/{pyghidra*,playplay_*,x64_*}`, the mechanism docs, runtime payloads).
- **Enable the pre-commit guard once per clone:** `git config core.hooksPath .githooks`. It blocks staging
  any out-of-scope path or mechanism keyword (bypass only for a verified false positive with
  `git commit --no-verify`).
- CI (`.github/workflows/no-drm-material.yml`) is the backstop: it fails if such material is ever tracked.
- Agent fences: `.claude/settings.json` (Claude), `.codex/config.toml` + `.codexignore` (Codex),
  and the steering blocks in `CLAUDE.md` / `AGENTS.md`.
