# Releasing Wavee (signed MSIX)

The runbook for shipping the **Wavee** music client. Wavee and the FluentGpu gallery are two products in one tree and
release **independently**:

| | Gallery | Wavee |
|---|---|---|
| Tag prefix | `v*` | **`wavee-v*`** |
| Workflow | `.github/workflows/msix.yml` | `.github/workflows/wavee-msix.yml` |
| Script | `ops/build/pack-msix.ps1` | `ops/build/pack-wavee-msix.ps1` |
| Manifest | `ops/build/AppxManifest.xml` | `ops/build/Wavee.AppxManifest.xml` |
| `.appinstaller` template | `ops/build/AppInstaller.template.xml` | `ops/build/Wavee.AppInstaller.template.xml` |
| Package identity | `MarTeco.FluentGpu` | **`cproducts.Wavee`** |
| Assets | `FluentGpu.WindowsApp_<ver>_<arch>.msix` | `Wavee_<ver>_<arch>.msix` + `Wavee.<arch>.appinstaller` |

Signing (Azure Trusted Signing), the CI service principal, and the secrets/variables are **shared** — see the
`releasing` skill (`.claude/skills/releasing/SKILL.md`) for the config table and every signing gotcha.

Wavee ships as a **NativeAOT packaged-Win32 full-trust MSIX**: no bundled .NET runtime, no WindowsAppSDK. The MSIX
manifest is what makes `wavee://` deep links and toast activation work as a *registered* protocol/AUMID rather than the
HKCU shim the unpackaged build falls back to — so the packaged build is the one that must be smoke-tested, not just
`dotnet run`.

---

## 1. Preflight (before you tag)

1. **CHANGELOG** — move the release's entries out of `## [Unreleased]` into `## [X.Y.Z] - YYYY-MM-DD` in
   `CHANGELOG.md`. A release with no changelog entry is not a release.
2. **Bump the version** — `<Version>` **and** `<InformationalVersion>` in `src/apps/Wavee/Wavee.csproj`. Settings ›
   About reads `InformationalVersion` off assembly metadata, so a stale value ships a lying About page. CI passes the
   4-part `X.Y.Z.<run>` to the packaging script, which stamps `/p:InformationalVersion` for the packaged build; the
   csproj value is what a local `dotnet run` shows.
3. **Both configurations build clean** (`TreatWarningsAsErrors` is on and Debug/Release compile different diag arms):
   ```powershell
   dotnet build src/FluentGpu.slnx -c Debug
   dotnet build src/FluentGpu.slnx -c Release
   ```
4. **Tests + gates green**:
   ```powershell
   dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj
   dotnet run --project src/FluentGpu.VerticalSlice        # expect "ALL CHECKS PASSED"
   ```
5. **Local package + install smoke** — the only way to exercise the packaged identity:
   ```powershell
   pwsh ops/build/pack-wavee-msix.ps1 -Arch x64 -Version X.Y.Z.0 -NoSign
   pwsh ops/build/pack-wavee-msix.ps1 -Arch x64 -Version X.Y.Z.0 -Install   # elevated: trusts the dev cert + Add-AppxPackage
   ```
   Then walk this checklist against the **installed** app (not `dotnet run`):

   - [ ] **Fresh profile** — rename `%LOCALAPPDATA%\Wavee` aside (or use Settings › factory reset) so first-run paths
         actually run.
   - [ ] **First-run wizard** appears, cannot be dismissed with `Esc`, and completes.
   - [ ] **Sign-in** succeeds and the account shown is the right one ("Is this you?").
   - [ ] **Play a track** end to end — audio, seek, next/previous.
   - [ ] **Deep link**: `start wavee://open?route=settings` from a terminal focuses the *running* instance and lands on
         Settings (single-instance handoff, not a second window).
   - [ ] **Toast round-trip**: Settings › Developer › test notification → the toast appears with the Wavee icon, and
         clicking it activates the app (packaged AUMID path).
   - [ ] **About** shows the version you just bumped, and **Check for updates** completes without an error.
   - [ ] No crash notice on the *next* launch (the crash-notice path fires only if the previous run died).

## 2. Cut the release

```bash
git checkout main && git pull
git tag wavee-vX.Y.Z && git push origin wavee-vX.Y.Z
```

That is the whole trigger. `wavee-v*` and `v*` are disjoint tag globs, so this runs **only** `wavee-msix.yml`; it never
publishes a gallery release. Flow: **version** (strip `wavee-v`, append the run number → `X.Y.Z.<run>`, monotonic so
App Installer always sees an upgrade) → **build** (x64 on `windows-latest`, arm64 on `windows-11-arm`; NativeAOT cannot
cross-compile) → **sign** (Trusted Signing; skipped if the repo variables are unset) → **release**.

A prerelease semver (anything containing `-`, e.g. `wavee-v0.2.0-rc1`) is published with `prerelease: true`
automatically — which also keeps it out of `releases/latest`, and therefore out of the `.appinstaller` update channel.

## 3. Verify

```bash
gh run list --workflow=wavee-msix.yml -L1
gh release view wavee-vX.Y.Z --json assets -q '.assets[].name'
```

Expect **five** assets:

```
Wavee_X.Y.Z.<run>_x64.msix
Wavee_X.Y.Z.<run>_arm64.msix
Wavee.x64.appinstaller
Wavee.arm64.appinstaller
THIRD-PARTY-NOTICES.txt
```

Then, on a **clean VM** (a machine that has never had Wavee installed):

1. Download `Wavee.x64.appinstaller` from `releases/latest/download/` and open it. App Installer should show the
   publisher as trusted (Trusted Signing) with **no** "untrusted app" banner and no cert-import step.
2. Install, launch, and repeat the smoke checklist from §1.5.
3. Confirm `Get-AppxPackage cproducts.Wavee` reports the version you shipped.

## 4. Verify the update path

Do this at least once per minor version — an update that never arrives is indistinguishable from no release at all.

1. Install the **previous** release from its `.appinstaller`.
2. Publish the next tag and wait for the release to be live.
3. Relaunch the installed app. Expect: the in-app **update available** notification (Wavee's own check, which reads the
   published version) and, on the App Installer side, the `ms-appinstaller` update prompt on next launch — because the
   installed `.appinstaller` URI points at the *stable* `releases/latest/download/Wavee.<arch>.appinstaller` URL while
   each release rewrites that file to point at that tag's `.msix`.
4. After update, About shows the new version.

If the prompt never appears: check that the new MSIX's 4-part version is **strictly greater** than the installed one
(the CI run number guarantees this only if the run number increased — a re-run of an *older* workflow run can emit a
lower 4th part), and that `releases/latest` really points at the new tag (a prerelease does not move `latest`).

## 5. Rollback

**App Installer only moves forward.** There is no "downgrade" verb: an `.appinstaller` whose `MainPackage` version is
*lower* than what is installed is ignored, and Windows will not replace a package with an older version. So you cannot
un-ship a bad release by re-pointing `latest` at the old one.

The rollback is therefore a **roll-forward**:

1. `git revert` the offending commits (or `git checkout wavee-vX.Y.Z-good -- <paths>`) onto `main`.
2. Bump to the **next patch** (`X.Y.Z+1`) in `Wavee.csproj` + `CHANGELOG.md`.
3. Tag `wavee-vX.Y.Z+1` and let CI publish it. Because the run number keeps climbing, the 4-part version is strictly
   greater and every installed client updates to the restored build.
4. Optionally mark the bad GitHub release as a **prerelease** (`gh release edit wavee-vX.Y.Z --prerelease`) so it stops
   being `latest` immediately, before the replacement finishes building.

Users who already installed the bad build and cannot wait: `Get-AppxPackage cproducts.Wavee | Remove-AppxPackage`, then
install the good `.msix` directly (per-package install, no `.appinstaller`), then re-open the `.appinstaller` once the
fixed release is out to re-join the update channel.

## 6. Signing gotchas

All of them — `Invalid tenant id`, `SignerSign() failed 0x80004005` (a CRLF in a GitHub secret), publisher mismatch
`0x8007000B`, `signtool verify /pa` failing for self-signed builds, the HTTP-not-HTTPS timestamp URL, and `vswhere`/
MSVC `link.exe` not found during the AOT link — are documented once in the **`releasing` skill**:
`.claude/skills/releasing/SKILL.md`. They apply verbatim to Wavee; only the script, template, and tag prefix differ.

## See also

- `ops/build/README.md` — the packaging scripts and their flags.
- `.claude/skills/releasing/SKILL.md` — signing config, secrets, and troubleshooting.
- `docs/guide/playplay-private-split.md` — why CI builds the public-only variant (`WaveeSkipPrivateSources`), and how a
  PlayPlay-inclusive local build differs.
