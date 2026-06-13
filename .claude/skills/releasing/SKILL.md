---
name: releasing
description: Use when cutting, publishing, or troubleshooting a FluentGpu MSIX release — tagging a version, building/signing the NativeAOT MSIX locally or in CI, Azure Trusted Signing failures (Invalid tenant id, SignerSign 0x80004005, publisher 0x8007000B), the GitHub release, or the .appinstaller.
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
pwsh build/pack-msix.ps1                            # host arch, self-signed dev cert
pwsh build/pack-msix.ps1 -TrustedSigning            # Azure Trusted Signing (publicly trusted)
pwsh build/pack-msix.ps1 -TrustedSigning -Install   # + Add-AppxPackage (installs clean, no cert prompt)
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
| Files | `build/pack-msix.ps1`, `build/AppxManifest.xml`, `build/AppInstaller.template.xml`, `build/signing/metadata.json` (gitignored), `.github/workflows/msix.yml` |

## Gotchas (every one of these actually happened)
- **CI sign job `Invalid tenant id` / `SignerSign() failed 0x80004005`** → a GitHub secret has a stray `\r`. **NEVER pipe to `gh secret set` from PowerShell** (CRLF leaves a trailing `\r`). Use `gh secret set NAME --body "value"`. Re-mint: `az ad app credential reset --id ad16e7be-… --years 1 --query password -o tsv`, set all three with `--body`, then `gh run rerun <id> --failed`.
- **Local `SignerSign() failed` / "Service request failed"** → wrong active subscription: `az account set --subscription "Azure subscription 1"`.
- **`0x8007000B` publisher mismatch** → manifest `Publisher` must EXACTLY equal the cert subject (`CN=cproducts, …`). `pack-msix.ps1 -TrustedSigning` sets it automatically.
- **`signtool verify /pa` fails for a self-signed build** → expected (untrusted chain); `-Install` trusts it. Trusted-Signing builds verify clean.
- **Timestamp**: `http://timestamp.acs.microsoft.com` (HTTP, not HTTPS).
- **AOT link fails / `vswhere` not found** → needs VS Build Tools (MSVC); `pack-msix.ps1` prepends the VS Installer dir to PATH.
- **`.appinstaller` 404** until that tag's release exists — expected.
- **Art changed?** regenerate then commit: `build/generate-appicon.ps1`, `build/generate-download-buttons.ps1`.

## Revoke CI signing
`az ad app delete --id ad16e7be-55d9-4a60-9446-3e2f58b5688c` (removes the SPN + its secret).
