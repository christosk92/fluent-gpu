---
name: releasing
description: Use when cutting, publishing, or troubleshooting a FluentGpu or Wavee MSIX release — the gallery's `v*` tag + CI, or Wavee's local `ops/release/wavee-release.ps1` (no CI job) with its `wavee-v*` tag and rolling `wavee-stable` update feed, building/signing the NativeAOT MSIX, Azure Trusted Signing failures (Invalid tenant id, SignerSign 0x80004005, publisher 0x8007000B), the GitHub release, or the .appinstaller.
---

# Releasing FluentGpu (signed MSIX)

FluentGpu ships as a **NativeAOT, packaged-Win32 full-trust MSIX** (~7 MB, no bundled .NET runtime), signed by **Azure Trusted Signing** (publicly trusted) and published to GitHub Releases with a per-arch `.appinstaller` (auto-update). README download buttons point at `releases/latest/download/FluentGpu.<arch>.appinstaller`.

## Cut a release (normal path)

CI does everything on a `v*` tag — `.github/workflows/msix.yml`:
```bash
git checkout main && git pull
git tag vX.Y.Z && git push origin vX.Y.Z
```
Flow: version → build (arm64 on `windows-11-arm`, x64 on `windows-latest`; AOT can't cross-compile) → sign (Trusted Signing) → release. MSIX version = `X.Y.Z.<run-number>` (monotonic). Verify:
```bash
gh run list --workflow=msix.yml -L1
gh release view vX.Y.Z --json assets -q '.assets[].name'   # expect 2 .msix + 2 .appinstaller
```
`releases/latest` is repo-global and **belongs to the gallery** (its `.appinstaller` polls it). Wavee releases are
therefore always published with `--latest=false` (`Publish-WaveeRelease`); never mark a Wavee release as latest.

## Build / sign locally
```powershell
pwsh ops/build/pack-msix.ps1                            # host arch, self-signed dev cert
pwsh ops/build/pack-msix.ps1 -TrustedSigning            # Azure Trusted Signing (publicly trusted)
pwsh ops/build/pack-msix.ps1 -TrustedSigning -Install   # + Add-AppxPackage (installs clean, no cert prompt)
```
Local Trusted Signing needs `az login` as an identity with the **Artifact Signing Certificate Profile Signer** role on the `Wavee` account, with that subscription active.

## Config (already wired — reference)
| Thing | Value |
|---|---|
| Signing account / profile / endpoint | `Wavee` / `wavee-public-trust` / `https://weu.codesigning.azure.net/` |
| Subscription holding it | **`Azure subscription 1`** (tenant `2bc83c61-…`) — NOT REDLAB |
| Publisher (manifest Identity = cert subject) | `CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL` |
| CI service principal | app `fluentgpu-ci-signing` (`ad16e7be-55d9-4a60-9446-3e2f58b5688c`) |
| GitHub secrets / vars | `AZURE_TENANT_ID/CLIENT_ID/CLIENT_SECRET`; `TRUSTED_SIGNING_ACCOUNT/ENDPOINT/PROFILE`, `RELEASE_PUBLISHER` |
| Files | `ops/build/pack-msix.ps1`, `ops/build/AppxManifest.xml`, `ops/build/AppInstaller.template.xml`, `ops/build/signing/metadata.json` (gitignored), `.github/workflows/msix.yml` |

## Wavee (the second product in this tree)

Wavee releases **independently** of the gallery — and by a different mechanism: **there is no CI job**. A CI checkout
has no `src/apps/Wavee.PlayPlay` junction, so it would silently publish the public-only variant; Wavee is cut locally
by `ops/release/wavee-release.ps1` (both arches, x64 via the MSVC `HostArm64\x64` cross toolchain). Everything below
reuses the same signing account/secrets and the same gotchas.

| Thing | Gallery | **Wavee** |
|---|---|---|
| Trigger | tag `v*` (`git tag vX.Y.Z && git push origin vX.Y.Z`) | **`powershell -File ops\release\wavee-release.ps1`** (rehearse with `-DryRun -SkipTests`) |
| Workflow | `.github/workflows/msix.yml` | **none (local: `ops/release/wavee-release.ps1`)** |
| Tag | `v*` | **`wavee-v*`** — created *by the script*, annotated, never deleted |
| Script | `ops/build/pack-msix.ps1` | `ops/release/wavee-release.ps1` → `ops/build/pack-wavee-msix.ps1` (one arch per call) + `ops/build/Wavee.Build.psm1` + `ops/release/Wavee.Release.psm1` |
| Manifest | `ops/build/AppxManifest.xml` | `ops/build/Wavee.AppxManifest.xml` |
| `.appinstaller` template | `ops/build/AppInstaller.template.xml` | `ops/build/Wavee.AppInstaller.template.xml` |
| Package identity | `MarTeco.FluentGpu` | **`cproducts.Wavee`** |
| Version source | — | **`src/apps/Wavee/Wavee.Version.props`**: hand-edit `<WaveeVersion>` (semver) + `<WaveeCodename>` (one per MINOR, sea series); **`<WaveeBuild>` is script-owned — never hand-edit it**. MSIX quad = `M.m.p.<WaveeBuild>` |
| Notes source | — | `CHANGELOG.md` `## [X.Y.Z] - unreleased` + `ops/release/wavee/<semver>/whatsnew.json` (+ `media/`) |
| Version-release assets | `FluentGpu.WindowsApp_<ver>_<arch>.msix` | `Wavee_<quad>_arm64.msix`, `Wavee_<quad>_x64.msix`, `whatsnew.json`, the media files, `THIRD-PARTY-NOTICES.txt`, `MANIFEST.txt` |
| Update feed | `releases/latest/download/FluentGpu.<arch>.appinstaller` | the rolling release **`wavee-stable`** → `Wavee.arm64.appinstaller`, `Wavee.x64.appinstaller`, `whatsnew-index.json` (`--clobber`, repointed **last**) |

`releases/latest` is repo-global and belongs to the gallery — Wavee never links to it. Verify a Wavee release with:

```powershell
gh release view wavee-vX.Y.Z --repo christosk92/fluent-gpu --json assets -q '.assets[].name'
Import-Module ops\release\Wavee.Release.psm1
Get-WaveeFeedVersion christosk92/fluent-gpu wavee-stable arm64   # the feed head clients poll
```

**Full runbook — hand edits, prerequisites, the `-DryRun` review, the phase table, failure/recovery, rollback, and the
scratch-feed rehearsal: `docs/guide/releasing-wavee.md`.**

## Gotchas (every one of these actually happened)
- **CI sign job `Invalid tenant id` / `SignerSign() failed 0x80004005`** → a GitHub secret has a stray `\r`. **NEVER pipe to `gh secret set` from PowerShell** (CRLF leaves a trailing `\r`). Use `gh secret set NAME --body "value"`. Re-mint: `az ad app credential reset --id ad16e7be-… --years 1 --query password -o tsv`, set all three with `--body`, then `gh run rerun <id> --failed`.
- **Local `SignerSign() failed` / "Service request failed"** → wrong active subscription: `az account set --subscription "Azure subscription 1"`.
- **`0x8007000B` publisher mismatch** → manifest `Publisher` must EXACTLY equal the cert subject (`CN=cproducts, …`). `pack-msix.ps1 -TrustedSigning` sets it automatically.
- **`signtool verify /pa` fails for a self-signed build** → expected (untrusted chain); `-Install` trusts it. Trusted-Signing builds verify clean.
- **Timestamp**: `http://timestamp.acs.microsoft.com` (HTTP, not HTTPS).
- **AOT link fails / `vswhere` not found** → needs VS Build Tools (MSVC); `pack-msix.ps1` prepends the VS Installer dir to PATH.
- **`.appinstaller` 404** until that tag's release exists — expected.
- **Art changed?** regenerate then commit: `ops/build/generate-appicon.ps1`, `ops/build/generate-download-buttons.ps1`.

### Wavee-specific gotchas
- **The active Azure subscription is usually REDLAB** → Trusted Signing fails. `az account set --subscription "Azure subscription 1"` — `wavee-release.ps1` does this itself in preflight, so a failure here means `az login` is stale, not the subscription.
- **Tag pushed but the upload failed** → re-run with **`-Resume`**. Staging is hash-verified against `MANIFEST.txt`, then the run continues from the phase that died; the draft release was never public and the feed has not moved.
- **Never delete a pushed release tag.** The feed's `MainPackage/@Uri` and `whatsnew-index.json` resolve through it. A bad build is fixed by the next patch; a genuine rollback is `-RepointFeed <older semver> -AllowDowngrade` (`ForceUpdateFromAnyVersion="true"` does move clients back).
- **Never delete or re-tag `wavee-stable`.** It is the fixed address baked into every install; destroying it orphans every client permanently.
- **`.gitignore` has `[Rr]elease/`** (the build-output pattern), so `!ops/release/` is what keeps the release tooling tracked. Adding a new file under `ops/release/` that git "doesn't see" means that negation needs widening, not `-f`.
- **Testing the tooling** never touches production: `-FeedRelease wavee-stable-test -TagPrefix wavee-test-v -Branch release-test -Force -SkipTests [-InstallFromFeed]`. The feed name is baked into the package at pack time (`-FeedRelease`), so a test build really polls the test feed.

## Revoke CI signing
`az ad app delete --id ad16e7be-55d9-4a60-9446-3e2f58b5688c` (removes the SPN + its secret).
