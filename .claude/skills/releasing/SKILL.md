---
name: releasing
description: Use when cutting, publishing, or troubleshooting a FluentGpu or Wavee MSIX release — tagging a version (`v*` for the gallery, `wavee-v*` for Wavee), building/signing the NativeAOT MSIX locally or in CI, Azure Trusted Signing failures (Invalid tenant id, SignerSign 0x80004005, publisher 0x8007000B), the GitHub release, or the .appinstaller.
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

Wavee releases **independently** of the gallery — different tag prefix, workflow, script, manifest, and package
identity. Everything below reuses the same signing account/secrets and the same gotchas.

| Thing | Gallery | **Wavee** |
|---|---|---|
| Tag | `v*` | **`wavee-v*`** (`git tag wavee-vX.Y.Z && git push origin wavee-vX.Y.Z`) |
| Workflow | `.github/workflows/msix.yml` | `.github/workflows/wavee-msix.yml` |
| Script | `ops/build/pack-msix.ps1` | `ops/build/pack-wavee-msix.ps1` |
| Manifest | `ops/build/AppxManifest.xml` | `ops/build/Wavee.AppxManifest.xml` |
| `.appinstaller` template | `ops/build/AppInstaller.template.xml` | `ops/build/Wavee.AppInstaller.template.xml` |
| Package identity | `MarTeco.FluentGpu` | **`cproducts.Wavee`** |
| Assets | `FluentGpu.WindowsApp_<ver>_<arch>.msix` | `Wavee_<ver>_<arch>.msix`, `Wavee.<arch>.appinstaller`, `THIRD-PARTY-NOTICES.txt` |
| Version source | — | `<Version>`/`<InformationalVersion>` in `src/apps/Wavee/Wavee.csproj` (bump before tagging) |

`v*` and `wavee-v*` are disjoint globs, so a Wavee tag never triggers a gallery release. Verify with
`gh run list --workflow=wavee-msix.yml -L1` and `gh release view wavee-vX.Y.Z --json assets`.

**Full runbook — preflight checklist, install smoke, update-path check, rollback: `docs/guide/releasing-wavee.md`.**
(Rollback is roll-*forward*: App Installer never downgrades, so ship the next patch tag.)

## Gotchas (every one of these actually happened)
- **CI sign job `Invalid tenant id` / `SignerSign() failed 0x80004005`** → a GitHub secret has a stray `\r`. **NEVER pipe to `gh secret set` from PowerShell** (CRLF leaves a trailing `\r`). Use `gh secret set NAME --body "value"`. Re-mint: `az ad app credential reset --id ad16e7be-… --years 1 --query password -o tsv`, set all three with `--body`, then `gh run rerun <id> --failed`.
- **Local `SignerSign() failed` / "Service request failed"** → wrong active subscription: `az account set --subscription "Azure subscription 1"`.
- **`0x8007000B` publisher mismatch** → manifest `Publisher` must EXACTLY equal the cert subject (`CN=cproducts, …`). `pack-msix.ps1 -TrustedSigning` sets it automatically.
- **`signtool verify /pa` fails for a self-signed build** → expected (untrusted chain); `-Install` trusts it. Trusted-Signing builds verify clean.
- **Timestamp**: `http://timestamp.acs.microsoft.com` (HTTP, not HTTPS).
- **AOT link fails / `vswhere` not found** → needs VS Build Tools (MSVC); `pack-msix.ps1` prepends the VS Installer dir to PATH.
- **`.appinstaller` 404** until that tag's release exists — expected.
- **Art changed?** regenerate then commit: `ops/build/generate-appicon.ps1`, `ops/build/generate-download-buttons.ps1`.

## Revoke CI signing
`az ad app delete --id ad16e7be-55d9-4a60-9446-3e2f58b5688c` (removes the SPN + its secret).
