# Wavee PlayPlay / Audio-Key — implementation plan (behavior port, resilience-first)

*Scope: bring the Spotify audio-key subsystem (AP path + PlayPlay fallback + AES-CTR decrypt) into this app (`C:\wavee\fluent-gpu\app`), reproducing the **behavior** proven in the old WaveeMusic client but rebuilt with the resilience and diagnostics that client lacks. Mechanism-only; no per-build secrets. Status: **in progress (2026-07-04)** — P0+P1 scaffold landed; P3 decode/output still open.*

Related: `docs/architecture.md` §6 (playback seam), `Wavee/Backend/AudioHost.cs` (the deferral boundary), `Wavee/Backend/AudioKey.cs` (the AP correlation engine already here).

---

## 0. TL;DR

The old WaveeMusic client plays Premium audio by getting a 16-byte AES key two ways — the legacy **AP path** (`0x0c RequestKey` → `0x0d AesKey`) and, when Spotify refuses that, the modern **PlayPlay path** (fetch an *obfuscated* key over HTTPS, then unscramble it by loading the real Spotify binary in an isolated x64 child process and reading the plaintext key out of a CPU register at a breakpoint) — then decrypts the Ogg with AES-128-CTR. Everything build-specific lives in a **runtime-fetched signed manifest**, so a Spotify update is a JSON edit, not a client rebuild.

**This app already has the AP half and the seams; it is missing the entire PlayPlay half, the out-of-process host it must run in, and any real decrypt.** It also carries the *same latent CRITICAL* WaveeMusic has: the rotation-prone version strings are **compiled into `SpotifyHeaders.cs`**, so a routine Spotify server bump 403s every user at once.

The plan: port the behavior into the `Backend/` (portable, testable) + `SpotifyLive/` (live) split this repo already uses, standing up a new **x64 `Wavee.AudioHost` child process** as the home for the native cipher (and, later, the whole audio pipeline) — and fold in, from day one, the eight improvements WaveeMusic only has *on paper*: version-pins-as-data, one config parser (not two), typed failure reasons, correlation ids, a per-derive watchdog, a decaying per-file latch, runtime key-validation (which doubles as the headless test gate), and signed/mirrored delivery.

---

## 1. What WaveeMusic actually does (the behavior to reproduce)

Distilled from a source-level read of `C:\WAVEE\WaveeMusic` (canonical: `.agents/guides/playplay-drm.md`).

**The key, two ways** (`Core/Audio/AudioKeyManager.cs`):
1. **AP path.** Over the persistent AP socket, send `0x0c` (fileId 20B + trackGid 16B + seq + `00 00`), await `0x0d` (key) or `0x0e` (error). 2500 ms/attempt, 5 attempts, reconnect after 2 consecutive timeouts. Error `0x0002` = transient; everything else (incl. `0x0001` entitlement denial) = permanent. **This is the exact protocol this app already implements** in `Wavee/Backend/AudioKey.cs`.
2. **PlayPlay fallback** — fires on a permanent AP error / timeout exhaustion:
   - **Step A (obfuscated key, HTTPS).** `POST {spclient}/playplay/v1/key/{fileId-hex}` with protobuf `PlayPlayLicenseRequest{version=5, token=<from manifest>, interactivity=INTERACTIVE, content_type=AUDIO_TRACK, timestamp}` + Bearer/client-token/first-party desktop headers. Response `PlayPlayLicenseResponse{obfuscated_key}` = 16 bytes. 403 backoff, ≤3 attempts.
   - **Step B (de-obfuscate).** IPC to the AudioHost child, which runs the native cipher (below) and returns the plaintext 16-byte AES key.

**The native cipher** (`Wavee.AudioHost/PlayPlay/PlayPlayKeyEmulator.cs`) — runs only in the x64 child:
- `LoadLibraryExW(Spotify.dll, LOAD_WITH_ALTERED_SEARCH_PATH)` after SHA-256 + arch check.
- Patch the RNG export to `xor eax,eax; ret` (x64) so derivation is **deterministic**.
- `vm_runtime_init(...)` once; snapshot the VM object for fast per-call reset.
- Per key: write `int3` (`0xCC`) at the trigger address, install a **transient** Vectored Exception Handler *only for the duration of the call* (a persistent VEH reads as an anti-debug hook to EDR), call `vm_object_transform(...)`, and in the handler read the finalized key pointer out of `CONTEXT[regOffset]`, copy 16 bytes, restore the byte, rewind RIP by 1, resume.
- Breakpoint never fires → throw *"RVAs may have drifted"* (the rotation signal). `Rebase(va) = moduleBase + (va - analysisBase)`.
- Extraction is a strategy: `TriggerRipBreakpoint` (x64 today), `OutputBufferSlice`, `PostProcessCall` (ARM64, not finished).

**Provisioning** (`Core/Audio/AudioRuntimeProvisioner.cs`): first run, `GET https://cproducts.dev/r/manifest.json` → for each pack, cache `Spotify.dll` under `%LOCALAPPDATA%\...\packs\<id>\` verified by **SHA-256 of the decompressed bytes + Authenticode + atomic write**; Brotli or none. Returns `RuntimeAsset(Path, Config, PackJson)`. Any failure ⇒ no deriver ⇒ AP-only.

**Decrypt** (`Wavee.AudioHost/Audio/Streaming/AudioDecryptStream.cs`): AES-128-CTR over the **whole file from byte 0**, public IV `72 e0 67 fb dd cb cf 77 eb e8 bc 64 3f 63 0d 93`, keystream = `AES-ECB(IV + blockIndex) XOR ciphertext`. The custom 167-byte (`0xa7`) header is stripped *downstream* of decrypt (`SkipStream`) before the Vorbis decoder.

**Process model** (`Wavee.AudioHost/`, `AudioIpc/`): a **separate `Wavee.AudioHost.exe` child**, spawned with a per-launch pipe name + launch token, tied to the parent via a **Job Object** (dies with the UI). IPC = length-prefixed JSON over a named pipe (`[4B BE length][UTF-8 JSON]`, 4 MB cap, source-gen STJ), a **single serial command loop** (the emulator relies on one-at-a-time). This isolation exists because (a) the cipher is x86-64, so it needs an x64 process, and (b) running foreign native code + self-patching next to the UI is a crash/EDR liability.

**The behavior in one sentence:** *provision a signed pack describing the current Spotify build; per file, try the AP key, else fetch an obfuscated key with the user's own token and de-obfuscate it in an isolated x64 process running the real binary; decrypt AES-128-CTR from byte 0; strip the 0xa7 header.*

---

## 2. What this app has vs. what's missing

| Capability | This app today | Source |
|---|---|---|
| **AP key path** (`0x0c/0x0d/0x0e` correlation) | ✅ built **and unit-tested** | `Backend/AudioKey.cs` (`AudioKeyDispatcher`), `SpotifyLive/ApConnection.cs` (`LiveAudioKeySource`, 5 s timeout, adopted socket) |
| **`IAudioKeySource` seam** | ✅ + `StubAudioKeySource` | `Backend/AudioKey.cs:15` |
| **Track resolve** (metadata → file select → storage-resolve → key → handle) | ✅ implemented **but never constructed** | `SpotifyLive/LiveTrackResolver.cs` (on key fail → `key=default`, "host derives it") |
| **Audio-host seam** (`IAudioHost`, `AudioStreamHandle`) | ✅ + `SilentAudioHost` | `Backend/AudioHost.cs` |
| **Protobuf pipeline** | ✅ `Google.Protobuf` + `Grpc.Tools`, 27 `.proto` → `Wavee.Protocol` | `Wavee.csproj:29,40-46`, `SpotifyLive/Protos/**` |
| **Authenticated spclient transport** (Bearer + client-token + app-version middleware) | ✅ | `Backend/Transport.cs`, `Backend/Spotify/HttpAuth.cs` |
| **Persistence** (SQLite cold store, DPAPI credential protector) | ✅ | `Backend/Persistence/SqliteColdStore.cs`, `Backend/Persistence/Credentials.cs` |
| **PlayPlay obfuscated-key HTTP** (Step A) | ❌ | — (need `playplay.proto` + `SpClient`-style method) |
| **Out-of-process host + IPC** | ❌ (only a *comment* at `AudioHost.cs:17`) | — |
| **Native cipher** (LoadLibrary+VEH deriver) | ❌ (explicitly deferred, `AudioHost.cs:12`) | — |
| **Runtime provisioner** (manifest fetch/verify/cache) | ❌ | — |
| **Real AES-CTR decrypt** | ❌ (`StubCrypto.Decrypt` is a passthrough, `Backend/Playback.cs:186`) | — |
| **Ogg/Vorbis decode + DSP + WASAPI output** | ❌ (all deferred; `UnsupportedPlaybackPlayer` rejects local play) | `docs/architecture.md §6` |
| **Version pins as data** | ❌ — **hardcoded** (`AppVersion="128800483"`, `ClientVersion="1.2.88.483.g8aa8628e"`) | `Backend/Spotify/SpotifyHeaders.cs:9-16` ⚠️ |

**The gap is exactly the PlayPlay half plus its host.** The AP half, the seams, protobuf, transport, and persistence are all here and proven — this is an *additive* feature landing on solid rails, not a rewrite.

⚠️ **The one thing that is already broken-in-waiting:** `SpotifyHeaders.cs` pins `1.2.88.483` / `128800483`. Spotify has since shipped `1.2.89.539` with a different token and build. The moment Spotify stops honoring `128800483` server-side, **Step A (and every spclient call) 403s for the whole fleet, fixable only by a rebuild.** Fixing this is the highest-leverage change in the whole plan and is independent of everything else.

---

## 3. Scope of this plan (and non-goals)

**In scope — "the PlayPlay behavior":** the key subsystem end-to-end — provision → obfuscated key (Step A) → derive in the x64 host (Step B) → a **validated** 16-byte AES key — plus the AES-128-CTR decrypt and the runtime magic-check that *proves* a key is correct. This is independently buildable and, crucially, **independently testable without audio output** (derive → decrypt first page → assert `OggS` at `0xa7`).

**Sibling track, explicitly out of scope here (same host, later):** Ogg/Vorbis decode, DSP chain, and WASAPI/PortAudio output — the part that turns a correct key into *audible* sound. It shares the `Wavee.AudioHost` process and IPC this plan stands up; §5.4 marks the seam. Until it lands, PlayPlay is validated by the magic-check harness, not by ears.

**Deferred by decision (see prior design sessions):** replacing the native LoadLibrary+VEH cipher with an **in-process CPU emulator** (the "C1" Unicorn-class port). *"For now this is fine"* — ship the proven native path; the emulator is P3 (§6), the long-term arch/EDR answer, and the native path stays as its fallback.

---

## 4. Target architecture

```
        UI process (Wavee.exe — x64 OR arm64, PublishAot)                x64 child (Wavee.AudioHost.exe)
 ┌───────────────────────────────────────────────────────┐        ┌────────────────────────────────────────┐
 │ LiveTrackResolver ─ ResolveAsync(track) ──────────────┐│        │  serial command loop (one msg at a time) │
 │   metadata → file → storage-resolve(CDN)              ││        │                                          │
 │   AudioKeyResolver (NEW seam):                        ││        │  DeriveKey:  PlayPlayDeriver             │
 │     1. AP  (AudioKeyDispatcher, already here)         ││ named  │    LoadLibrary+VEH  (native cipher)      │
 │     2. PlayPlay fallback:                             ││ pipe   │    ↳ strategy from Config               │
 │        A. obfuscated key  POST /playplay/v1/key/{id}  ││ ◄────► │                                          │
 │           (playplay.proto, token+version FROM MANIFEST)││ JSON   │  Decrypt+Decode+Output  (SIBLING, later) │
 │        B. derive  ──IPC {corrId, obfKey, contentId}──►││ frames │                                          │
 │           ◄──────── {corrId, aesKey | typed reason} ──┘│        │  Job Object: dies with UI               │
 │                                                        │        └────────────────────────────────────────┘
 │  AudioRuntimeProvisioner ─ signed manifest → pack ─────┼──(pack path + parsed PlayPlayConfig travel in the derive cmd)
 │  AudioRuntimeStatusService ─ typed outcomes → banner   │
 └───────────────────────────────────────────────────────┘
                    key (validated) → AudioStreamHandle.Key → host decrypts
```

### Project shape (3 → 4 projects — only the host is new)
- **`Wavee`** (existing WinExe) — UI + `Backend/` + `SpotifyLive/`. Gains: the `AudioRuntimeProvisioner` + manifest model + `ToConfig` (UI-only), the Step-A HTTP, the `IPlayPlayKeyDeriver` client (IPC proxy), the status service, the child-process manager. **Owns the shared contract source** — the IPC records, `PlayPlayConfig`/`AesKeyExtraction`, and the AES-CTR core — in `Backend/Audio/Contracts/`, pure POCO.
- **`Wavee.AudioHost`** (NEW — *the only new project* — x64-pinned exe) — the child. Contains the native cipher (`PlayPlayDeriver`: P/Invoke LoadLibrary/VEH), the IPC pipe server + serial loop, process lifecycle, and later the decode/output pipeline. **Depends on nothing**: it `<Compile Include>`-links the shared contract source, so it stays a lean, isolated native sidecar with zero assembly references.
- **`Wavee.Core`**, **`Wavee.Tests`** — unchanged (tests gain PlayPlay unit + golden-vector suites; they see the contract source through the existing `Wavee` reference).

> **How we get "one parser" with no new project.** In WaveeMusic the pack JSON is parsed **twice by two hand-written parsers** (`RuntimeManifest.ToConfig` UI-side and `AudioHostService.ParsePackJson` host-side) *because the host can't reference the UI assembly* — a documented drift hazard. We dissolve it two ways at once: (1) the manifest is hand-parsed **only once, UI-side** (`ToConfig`), and the **already-parsed `PlayPlayConfig` is sent over IPC** — the host never hand-parses a manifest, it just STJ-deserializes a structured object; (2) the record **shapes** are defined in one source file compiled into both assemblies (linked), so the two sides can't drift structurally, and JSON bridges them (each side deserializes into its own structurally-identical copy — exactly how JSON IPC works). One parser, zero shared-assembly ceremony. *(Alternative: put the shapes in the existing `Wavee.Core` and have the host reference it — one line instead of linked globs, but couples the native sidecar to the domain lib.)*

### The eight improvements baked in from the start
WaveeMusic has these only as a *paper* rework plan (`.agents/plans/playplay-resilience-rework.md`); we get them by construction:

| # | Improvement | Fixes (WaveeMusic weakness) | Where |
|---|---|---|---|
| 1 | **Version pins → signed manifest** | compiled-in `Spotify-App-Version`/request `version` = fleet-wide rebuild-to-recover 403 | `SpotifyHeaders.cs` reads from `RuntimeAsset.Config` |
| 2 | **One config parser** | two hand-parsers drift | parse manifest once UI-side, send parsed `PlayPlayConfig` over IPC; shapes via linked source (no new project) |
| 3 | **Typed outcomes, no silent `null`** | every fault collapses to one undiagnosable AP-only fallback | `PlayPlayOutcome`/`ProvisioningOutcome` enums + `AudioRuntimeStatusService` |
| 4 | **Correlation id UI→pipe→host** | two log files, no join key | threaded through the derive IPC command |
| 5 | **Per-derive watchdog + host recycle** | one wedged derive freezes the key for *every* track (no timeout, globally serialized) | proxy-side timeout; recycle child on true wedge |
| 6 | **Decaying, per-file, re-probeable latch** | session-permanent latch trips on a transient blip and strands the session on the fragile path | replace the bool latch with a time-boxed per-path health tracker |
| 7 | **Runtime key validation (magic check)** | a wrong (drifted) key is cached and heard as noise | check `OggS`@`0xa7` after first decrypt *before* trusting/caching — **also the headless test gate** |
| 8 | **Signed + mirrored + baked-in delivery** | single hardcoded host = first-run SPOF; captive portals; no resume | ECDSA-signed manifest, content-addressed `urls[]`, embedded last-known-good |

---

## 5. Component design

Each: *what WaveeMusic does → what we do (better) → where it lives here.*

### 5.1 Config + manifest — parsed once, shapes shared by source
- **Shared shapes** (`Backend/Audio/Contracts/`, linked into the host): `PlayPlayConfig { Version, Arch, byte[] Sha256, byte[] Token, byte[] VmInitValue, ulong AnalysisBase, VmRuntimeInitVa, VmObjectTransformVa, RuntimeContextVa, FillRandomBytesVa, AesKeyExtraction AesKey, + buffer sizes }`; `AesKeyExtraction` = `TriggerRipBreakpoint(RipVa, RegOffset) | OutputBufferSlice(Offset, Len) | PostProcessCall(Va, Offset)`; the IPC records; the AES-CTR core. Pure POCO, no deps.
- **Manifest pack + `ToConfig` live UI-only** (`SpotifyLive/Audio/`) — only the UI fetches/parses the manifest. Source-gen STJ, same field names as WaveeMusic (`id, url(s), compression, sha256_hex, playplay_token_hex, spotify_version, arch, analysis_base_hex, *_va_hex, vm_init_value_hex, trigger_rip_va_hex, trigger_rip_reg_offset, sizes…`), **plus `app_version` + `request_version` as first-class fields** (WaveeMusic has `request_version` but it's dead) and an explicit **extraction-strategy** field (not `TriggerRipBreakpoint` hardcoded in two places).
- **The host never hand-parses a manifest.** The UI runs `ToConfig` once and puts the structured `PlayPlayConfig` in the derive command; the host STJ-deserializes it. `byte[]` fields ride as base64/hex; `AesKeyExtraction` uses an STJ polymorphic discriminator. One parser, by construction — the "two parsers" drift can't exist.

### 5.2 Provisioner — tiered, signed, off the startup path
Port `AudioRuntimeProvisioner` into `SpotifyLive/Audio/` with the WaveeMusic verify discipline (SHA-256 of *decompressed* bytes → Authenticode on a `.tmp` real path → atomic `File.Move`; Brotli/none; `SanitiseId` against path traversal) **plus**:
- **Tiered resolution:** T0 baked-in signed last-known-good (embedded, zero-network first run) → T1 disk cache (TTL + ETag) → T2 mirror list (`urls[]`, content-addressed, connection-level retry + circuit-break + resumable Range). Because the pack hash is inside the signed manifest, *any* host can serve the pack safely.
- **ECDSA-P256 detached signature** over the exact served manifest bytes, verified against two embedded public keys (current + next → one key rotation with no rebuild).
- **Arch-filter before download** (skip packs whose `arch ≠ host` so a mis-ordered manifest can't brick derivation).
- **Off the audio-startup critical path**: a background task the first Premium track awaits; separate fast-manifest vs adaptive-pack budgets; `DefaultProxyCredentials` for integrated-auth 407; reject non-verifying responses (captive-portal HTML fails the signature → next tier); re-provision on `NetworkChange`.
- **Typed `ProvisioningOutcome{ reason }`** — never `null`-collapse.
- Cache root: `%LOCALAPPDATA%\Wavee\AudioRuntime\` (mirror WaveeMusic layout).

### 5.3 Step A — obfuscated-key HTTP
- Drop `playplay.proto` into `SpotifyLive/Protos/` (same `Wavee.Protocol` namespace, same build pipeline — trivial).
- New `SpotifyLive/Audio/PlayPlayLicenseClient.cs`: `POST /playplay/v1/key/{fileId-hex}` via `ITransport.Request(Channel.Spclient, route, protoBody, ct, "POST")` — the middleware already attaches Bearer + client-token + app identity. Request `version` and `token` come from **`RuntimeAsset.Config`** (improvement #1), not constants. 403 backoff ≤3, `2^(n-1)`s; validate 16 bytes; typed `License403` on exhaustion (distinct from network).
- Cache the **obfuscated** key only (SQLite) — useless without the cipher.

### 5.4 The x64 host + IPC
This is the heaviest new piece (this app has *no* second process today). Port the WaveeMusic model faithfully — it is battle-tested:
- **`Wavee.AudioHost` exe**, `RuntimeIdentifier=win-x64` pinned (runs under arm64 x64-emulation on Snapdragon). Zero assembly references — the shared contract source is `<Compile Include>`-linked in.
- **Spawn/manage** (`SpotifyLive/Audio/AudioProcessManager.cs`): per-launch pipe name `WaveeAudio_{pid}_{guid}`, launch-token + parent-pid + session-id args, **Job Object** so it dies with the UI, `Process.Exited` crash detection, stdout/stderr → logger, a dedicated exit code for provisioning failure.
- **Framing** (`IpcPipeTransport`): `[4B BE length][UTF-8 JSON]`, 4 MB cap, **source-gen STJ** (AOT-safe — mandatory, this exe is AOT), envelope `{ Type, Id, Payload }`. Keys transit as **hex strings**.
- **Serial command loop** on the host — one message at a time (the native breakpoint/restore is not reentrant). Startup handshake validates the launch token (refuse standalone start).
- **The `IAudioHost` impl on the UI side** becomes `RemoteAudioHost : IAudioHost` — proxies `Load/Play/Pause/Seek/Signals` over the pipe (replaces `SilentAudioHost` in `LiveConnect.cs:53` when the real host lands). For *this* plan only the **`DeriveKey` RPC** is needed; the transport it rides on is the same one decode/output will use.
- **Improvements:** thread a **`correlationId`** into every command; **per-derive watchdog** on the proxy (`SendRequestAsync` with a timeout) and **recycle the child** on a true wedge (a native call can't be cancelled mid-flight — the client-side timeout alone would leak a hung worker). This is the single most important reliability fix over WaveeMusic (their derive has *no* timeout and is globally serialized → one stuck derive freezes all playback).

### 5.5 Step B — the native deriver
Port `PlayPlayKeyEmulator` into `Wavee.AudioHost/PlayPlay/PlayPlayDeriver.cs`, faithfully (it works), with the cheap robustness fixes WaveeMusic only planned:
- SHA-256 + `ProcessArchitecture` check; `LoadLibraryExW(..., LOAD_WITH_ALTERED_SEARCH_PATH)`.
- NOP the RNG (`31 C0 C3`), `vm_runtime_init` once + snapshot, per-derive **transient** VEH (add/remove around the single call), read key from `CONTEXT[regOffset]`, rewind RIP, `Rebase = base + (va - analysisBase)`.
- **Strategy dispatched from `Config.AesKeyExtraction`** (not hardcoded).
- **Add an instruction-cache flush after self-patching** — the missing flush is the ARM64/x64-emulated "stale translation block → breakpoint never fires → misdiagnosed as drift" hazard, and this child *will* run under emulation on arm64 devices.
- **Emulator cached by pack id** (rebuild only when the active pack changes).
- Breakpoint-never-fired → typed **`RotationDrift`** (the un-ambiguous "Spotify updated, re-derive" signal), not a generic failure.

### 5.6 Key-manager seam — AP-first + PlayPlay fallback
The integration point. Today `LiveTrackResolver` calls `IAudioKeySource.GetKeyAsync` (AP-only) and, on failure, returns `key=default`. Introduce an `AudioKeyResolver` that wraps both paths (mirrors WaveeMusic's `AudioKeyManager`, improved):
- In-mem → SQLite cache (AP keys **and** validated derived keys, the latter **DPAPI-protected / TTL-bounded** — `Credentials.cs` already does DPAPI here).
- AP path (existing `AudioKeyDispatcher`), then PlayPlay fallback (Step A → Step B) when a deriver is registered.
- **Decaying, per-file, re-probeable latch** (improvement #6) instead of WaveeMusic's session-permanent bool — never abandon AP for the whole session on one transient blip, never latch the session on one permanently-refused track.
- Register the deriver on the session **only if** provisioning returned a `RuntimeAsset` (else AP-only, cleanly, with a typed `NeverProvisioned`/`ProvisioningUnavailable` status — not silence).

### 5.7 Decrypt + runtime validation (the gate)
- **Put the pure CTR core in `Backend/Audio/Contracts/`** (AES-128-CTR, public IV, `AES-ECB(IV+blockIndex) XOR`, `_decryptionStartOffset`) — proto-free, no native deps, so it is unit-testable AND linked into the host (no duplication). The host wraps it as its decrypt stream, replacing the passthrough `StubCrypto.Decrypt`.
- **Runtime magic-check (improvement #7):** at the first decrypted page, verify the plaintext container magic (`OggS`) at the known header boundary (`0xa7`) *before* the key is trusted or cached. Pass → cache (protected) + play. Fail → discard, **do not cache**, raise `RotationDrift`. The 0xa7 header is stripped downstream (`SkipStream`), exactly as WaveeMusic.
- This same check, run in a test with a few fetched CDN bytes (or a fixture), is the **headless validation gate** for the whole subsystem — no decoder or speaker required.

### 5.8 Diagnostics / status
- `AudioRuntimeStatusService` (UI-side, single source of truth): typed reasons `RotationDrift | ArchUnsupported | SecurityBlocked | License403 | EmulationFault | Network | ProvisioningUnavailable | NeverProvisioned`, surfaced as (a) a log line with the correlation id, (b) a non-modal banner with Retry (re-provision + reset latch), (c) a local, user-initiated diagnostics bundle. **No fleet phone-home** (a de-anonymizing signal from a DRM client — see §8).
- Wire it into the existing `Telemetry.cs` / toast host rather than inventing UI.

---

## 6. Phased roadmap

Ordered by leverage-per-effort. Criticals never gate behind the big pieces.

### P0 — Version pins as data + the seams + typed outcomes (S–M, no new process)
The cheap, high-leverage foundation; no child process yet.
- **Uncompile the version pins** (`SpotifyHeaders.cs` → sourced from `RuntimeAsset.Config`; wire `app_version`/`request_version` manifest fields). *This alone converts the looming fleet-wide 403 into a JSON edit.* **[S]**
- Stand up `Wavee.Audio.Contracts` (records + the one `ToConfig`). **[S]**
- Add the `AudioKeyResolver` seam + `IPlayPlayKeyDeriver` interface + typed `PlayPlayOutcome`/`ProvisioningOutcome` + `AudioRuntimeStatusService`; null deriver returns `NeverProvisioned` (behavior identical to today — AP-only — but now *observable*). **[M]**
- Decaying, per-file, re-probeable latch. **[S]**

**Exit:** a Spotify version bump is a signed-JSON fix, not a rebuild; every not-yet-implemented path reports a distinct reason instead of silence. No audible change yet.

### P1 — The x64 host + native derive + Step A + provisioner + magic-check (L — the meat)
End-to-end key derivation, validated headlessly.
- `Wavee.AudioHost` exe + `AudioProcessManager` + `IpcPipeTransport` + serial loop + Job Object + launch token + **correlation id + per-derive watchdog + recycle**. **[L]**
- `PlayPlayDeriver` (LoadLibrary+VEH, strategy-from-config, i-cache flush). **[M]**
- `playplay.proto` + `PlayPlayLicenseClient` (Step A, version/token from manifest, typed `License403`). **[M]**
- `AudioRuntimeProvisioner` (single-host first, verify discipline, arch-filter, typed outcomes). **[M]**
- `AudioDecryptStream` (CTR core in `Backend/`) + the **magic-check gate**. **[S]**

**Exit:** on a Premium account where AP refuses, the app **derives a real AES key and proves it decrypts** (`OggS`@`0xa7`) — headlessly, in a test — with a wedged derive no longer freezing other tracks, a wrong key never cached, and every failure typed.

### P2 — Resilience delivery (L)
- Signed + content-addressed + mirrored manifest (`urls[]`, two embedded pubkeys); embedded last-known-good; robust HTTP off the startup path (resume, retry/backoff/circuit-break, ETag/TTL, proxy creds, captive-portal rejection, `NetworkChange` re-provision). **[L]**
- Persist validated derived keys (DPAPI/TTL-guarded). **[S]**
- Publish-time golden-key gate (operator tooling: re-derive a committed known-answer vector byte-for-byte before signing; generate the manifest from source-of-truth constants). **[M]**

**Exit:** first run works on a hostile/slow/proxied network; a single delivery-host outage is survivable; no broken pack can be published.

### P3 — (deferred) emulator + the audible pipeline (XL)
- Port the in-process CPU emulator (C1) behind the strategy seam; keep native as fallback; one universal x86-64 guest ⇒ ARM64 stops being special. **[XL]**
- The **sibling audio pipeline** (Ogg/Vorbis decode + DSP + WASAPI output) in the same host — this is what finally makes local playback *audible* and flips `UnsupportedPlaybackPlayer` off.

---

## 7. Testing & gates (headless-first — matches this repo's culture)

This app already unit-tests the AP correlation engine proto-free/socket-free (`Wavee.Tests/ConnectAudioKeyTests.cs`); PlayPlay follows the same discipline:
- **Unit (Backend/, no host):** the manifest `ToConfig` parser; the decaying-latch state machine; the AES-CTR core (known IV + known key → known keystream); the Step-A request builder (proto-free where possible).
- **Golden-vector gate:** mirror `another-unplayplay/tests/test_keyemu.py` — a handful of `(obfuscated_key → aes_key)` vectors asserted **byte-for-byte**. Runs (a) as a `Wavee.Tests` case against the deriver and (b) as the **operator publish gate** (no pack ships that fails to re-derive the known answer). This is the drift-proofing WaveeMusic lacks.
- **Runtime gate:** the §5.7 magic-check — the integration proof that a derived key actually decrypts, without a decoder or speaker.
- **Live check:** on a real Premium account, an AP-refused track derives + validates end-to-end (the one thing only a live wire confirms).

---

## 8. Security / OpSec (invariant across phases)

Carried over from the WaveeMusic discipline — assume this tree may be read by an adversary:
- **Neutral terminology** in code/commits/manifests/UI ("audio runtime support pack", "the upstream runtime binary", "derivation") — never the blunt framing.
- **No committed secrets, ever.** Token bytes, SHA, VAs, VM init value, the populated manifest, and the new ECDSA **private** key stay out of the repo — server-side / gitignored-and-stubbed (this repo already has a public/proprietary split precedent via `Fakes/` and stubs). The public build compiles and runs (AP-only) but cannot derive. The AES IV is public (it's in librespot) and fine to embed.
- **Mechanism, not per-build data, in this doc.**
- **Derivation stays on the client, per-user** (the user's own token gates Step A upstream at Spotify). **No remote de-obfuscation oracle** and **no fleet telemetry** — both were considered and rejected in prior design sessions: a live public unscramble service is a categorically sharper legal target and a play-history honeypot; a beacon from a DRM client is a de-anonymizing fingerprint. Diagnostics stay **local + user-initiated**.
- **Own the reality:** this subsystem obtains a decryption key for DRM-protected content. The job of this plan is *resilience and observability*, not legitimacy.

---

## 9. Open decisions for you

1. **Host scope now.** Stand up `Wavee.AudioHost` for **derive-only** in P1 (smallest correct step; decode/output later in the same process), or wait and build derive + decode + output together as one "real audio host" milestone? *Recommendation: derive-only first — it's independently validatable via the magic-check and de-risks the IPC/process spine before the bigger decode/output work.*
2. ~~Shared contracts project~~ — **RESOLVED: no new contracts project.** `Wavee.AudioHost` is the *only* new project; shared shapes ride as linked source + the manifest is parsed once UI-side with the parsed `PlayPlayConfig` sent over IPC (§4, §5.1), so there's one parser and the host depends on nothing.
3. **Where the pack + its secrets are provisioned from.** Reuse the existing `cproducts.dev` redirector + manifest (fastest — it already serves `1.2.88.483`), or stand up delivery fresh for this app? Either way P0's version-pin fix and P2's signing apply.
4. **First target build.** Ship pinned to the **current** `1.2.88.483` pack (matches today's hardcoded headers, proven), and rotate to `1.2.89.539` as the first *manifest-driven* rotation (proving the whole point) — or jump straight to `1.2.89.539`? *Recommendation: land on `1.2.88.483` to prove parity, then rotate as data.*

*Bottom line: the AP half, the seams, protobuf, transport, and persistence are already here — this is the PlayPlay half plus its isolated x64 host, ported faithfully from a client that works, but rebuilt around version-pins-as-data, one config parser, typed failures, a derive watchdog, and a runtime key-check, so it survives the networks, machines, and Spotify updates the original silently dies on.*
