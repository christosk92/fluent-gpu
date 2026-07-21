# Video — Phase 1 consolidated plan (verified)

**Status:** design verified, ready to build. This is the master plan for Phase 1 of video in FluentGpu.
It supersedes the open-questions in the two design docs it sits on:

- `docs/plans/video-compositing-spine-design.md` — the DRM-free compositing spine.
- `docs/plans/video-drm-layer-design.md` — the licensed PlayReady/Widevine layer on top.

It records the outcome of a three-track verification pass (compositing/DComp correctness; no-CsWinRT interop
feasibility; PlayReady correctness + packaging), the corrections that pass forced, the one architectural
decision that was still open, and the sequenced milestone plan with its two gating spikes.

Framing (unchanged): this is the **standard licensed-playback path** — driving the OS's own PlayReady
components (and, as a fallback, the browser-provisioned Widevine CDM) against Spotify's official license
servers with the user's authenticated session. No key extraction, no protection bypass. It is exactly the
capability WinUI issue microsoft/microsoft-ui-xaml#10918 asks Microsoft to ship natively.

---

## 1. What verification CONFIRMED (build on these)

- **The graded-hole crossfade is real and free.** The composited swapchain is created
  `DXGI_ALPHA_MODE_PREMULTIPLIED` (`D3D12Device.InitSwapChain`), and the image blend state is
  `ONE/INV_SRC_ALPHA` on both color and alpha. Drawing poster/art at coverage `(1−VideoReady)` over a
  premultiplied-0 hole yields exactly `poster·(1−w) + video·w` under DWM's premultiplied over-operator — a true
  art→poster→live cross-dissolve with **no new shader and no black first frame**. Confirmed against the code and
  the compositing math. (One caveat to state in the spine doc: the video child must present opaque content, or
  the desktop shows through the hole — true of any real video surface.)
- **The seam count/shape is right; the hole-punch, registry, painter-order (not a PassClass), and zero-alloc
  posture all check out.** The `DrawVideo` opcode, `VideoSurfaceRegistry`, `UseVideoSurface`, and the
  poster/art reuse of the existing `ImagePipeline`/`DrawImageCmd` path are all sound.
- **No-CsWinRT interop is feasible (YELLOW-GREEN).** The repo already drives inbox WinRT with **no CsWinRT** via
  `TerraFX.Interop.WinRT` (`RoGetActivationFactory`/`RoActivateInstance` + hand-vtable calls, one
  `[GeneratedComInterface]` CCW for events) — the proven precedent is `FluentGpu.WindowsApi/Media/`
  (`SystemMediaControls.cs`, `MediaButtonHandler.cs`). ~90% of the media/DRM WinRT surface is **already
  projected** by the exact package version the repo pins (`TerraFX.Interop.Windows 10.0.26100.6`):
  `MediaPlayer` (+`IMediaPlayerSource2.put_ProtectionManager`/`put_Source`, `IMediaPlayer4.SetSurfaceSize`/
  `GetSurface`), `MediaProtectionManager` (+ `ServiceRequested`/`Completion`/args), `MediaSource`,
  `AdaptiveMediaSource` (+ `CreateFromStreamAsync`), `MediaPlaybackItem`, `PropertySet`, `IAsyncAction`,
  `IMediaPlayerSurface`.
- **`ServiceRequested` is *easier* than the existing SMTC button event.** It is a **non-generic named delegate**
  (`IServiceRequestedEventHandler`) with a fixed metadata IID — so its CCW needs **no** parameterized-IID
  derivation (the single most failure-prone value in the SMTC precedent). Individualization is
  `IAsyncAction`-based (fully projected, awaitable for free).
- **Packaged-identity is NOT a showstopper.** Inbox WinRT PlayReady types activate from an unpackaged NativeAOT
  process (same as the repo's SMTC), and — decisively — the product **already ships as a packaged full-trust
  MSIX** (`ops/build/pack-msix.ps1`, `build/AppxManifest.xml`, the `releasing` skill), so it has identity in the
  ship vehicle regardless. Only the dev loop and `FluentGpu.VerticalSlice` are unpackaged, and neither exercises
  video DRM.
- **SL2000 software DRM needs no special packaging;** the protected surface composites like any other; and
  captured pixels read **black** (DWM capture-exclusion) — so no golden-image gate can validate live protected
  playback. This is a real testing limit, not a bug.

---

## 2. Corrections verification FORCED on the design docs

These are things the docs got **wrong or over-confident**; the plan below already incorporates the fixes.

1. **Spine: the surface-handoff primitive was wrong.** The spine doc named
   `ICompositorInterop::CreateCompositionSurfaceForHandle` — that is a **Windows.UI.Composition** call, and this
   engine is **raw DirectComposition** (`IDCompositionDevice`/`IDCompositionVisual`, no WUC `Compositor`). The
   correct raw-DComp call is **`IDCompositionDevice::CreateSurfaceFromHandle(handle, out surface)`** →
   `childVisual->SetContent(surface)`. TerraFX 10.0.26100.6 already exposes it (and
   `DCompositionCreateSurfaceHandle`), so **no WinRT and no `[GeneratedComInterface]` are needed for the raw
   handle path.** *(Applied inline to the spine doc.)*
2. **Spine: commit-atomicity wording.** The hole is flushed by `IDXGISwapChain::Present`; the child
   transform/visibility is flushed by `IDCompositionDevice::Commit`. These are **two independent flushes on the
   same frame-turn**, not "the same Commit" — DWM offers no hard atomicity across a flip Present and a DComp
   Commit. Also: the present loop does **not** call `Commit` today (only `BindDComp` does, once) — video adds a
   **new per-frame `Commit()` at phase 11**, gated on dirty placement.
3. **Spine: interactive resize.** Promote **snap-`VideoReady`-to-0 (poster-only, hole closed) for the duration of
   an interactive resize/move** from "verify" to the **default** — the Present/Commit desync makes a one-frame
   edge slip otherwise unavoidable. Keep the DComp clip to `deviceRect` as belt-and-suspenders.
4. **Spine: M0 doesn't exercise the handle seam as written.** An engine-painted surface uses
   `IDCompositionDevice::CreateSurface` (no shareable handle). To truly cover `BindSurfaceHandle`, M0 must either
   relabel or explicitly `DCompositionCreateSurfaceHandle` + paint. (Folded into the milestones below.)
5. **DRM: `ProtectedVideoRequest.SegmentHttpHandler` has the wrong type.** It is `System.Net.Http.HttpMessageHandler`;
   `AdaptiveMediaSource` uses **`Windows.Web.Http`** — inject CDN auth via a `Windows.Web.Http.HttpClient`+filter
   or the `AdaptiveMediaSource.DownloadRequested` hook. (And segment auth may be **in-URL**, in which case this
   handler is dead weight — settle in the spike.)
6. **DRM: drop the "no packaged-identity assumption" pillar** — it's unverified and unnecessary; state instead
   that DRM is validated only in the packaged MSIX (which has identity). *(Applied inline to the DRM doc.)*
7. **DRM: the reference is proven-*written*, not proven-*rendering*.** WaveeMusic's PlayReady engine is dead-coded
   behind an early `return;` and the author reports it didn't render in their WinUI 3 stack. Align §3/§5's
   confidence to §12 risk 1: the recipe faithfully matches Microsoft's documented pattern, but "clears
   `0xC00D715B` and renders" is the **unproven** gate. *(Partially applied to the DRM doc; see §5 below.)*

---

## 3. The one real decision: how the video surface reaches the raw-DComp tree

Both the compositing and PlayReady tracks independently surfaced the same fork, and it is the crux of Phase 1:

| | **Path M — MF-direct (`IMFMediaEngineEx`)** | **Path W — WinRT `MediaPlayer` + `AdaptiveMediaSource`** |
|---|---|---|
| Surface it hands you | a **raw DComp surface HANDLE** (`EnableWindowlessSwapchainMode` + `GetVideoSwapchainHandle`) | a **Windows.UI.Composition `ICompositionSurface`** (`GetSurface(Compositor)`; `IMediaPlayerSurfaceHandleProvider` is private/undocumented) |
| Spine seam | **`BindSurfaceHandle(nuint)`** — the clean seam the spine was designed around; `CreateSurfaceFromHandle` → `SetContent` | **`BindChildVisual(IntPtr)`** — needs a WUC `Compositor`+`SpriteVisual` and a WUC↔raw-DComp bridge |
| Adaptive streaming (DASH/ABR) | **you build it** (custom MF source/byte-stream) or ship single-rendition | **free** — AMS does segment-fetching + ABR |
| DRM wiring | MF level (`IMFContentProtectionManager`/CDM) — **no proven reference** | `MediaProtectionManager` — **proven-shaped** (Wavee), ~90% projected, only `PlayReady.*` to hand-author |
| Interop cost | pure COM (best AOT/ComPtr fit, no WinRT) | bounded WinRT: hand-author `PlayReady.*` (~6 interfaces + `PlayReadyWinRTTrustedInput` + GUIDs) + one async CCW |
| Compositing risk | **low** — raw handle drops straight into the existing DComp tree | **the open risk** — can a WUC `MediaPlayerSurface` be composited into this engine's *raw*-DComp tree at all? (WindowsAppSDK #892 is unanswered on exactly this) |

**Decision for Phase 1 — don't force the choice; sequence so the data decides, with a clear lean:**

- **Non-DRM (canvas) uses Path M — `IMFMediaEngineEx` → `BindSurfaceHandle`.** Unambiguously the best fit:
  pure COM, raw handle, the clean seam, no WinRT, no WUC. This ships first and proves the whole compositing
  spine.
- **DRM trust is proven with Path W's minimal stack, headless, first** (the spike in §5) — it is the cheapest
  way to prove `0xC00D715B` is cleared, and it reuses Wavee's proven recipe.
- **The DRM *rendering* engine is chosen only after two facts are known** (both cheap to get, see §5 spikes):
  (a) does the WinRT trust pipeline actually clear `0xC00D715B` here; (b) can a WUC media surface bridge into
  this engine's raw-DComp tree — investigate against the **existing WUC `Backdrop`/acrylic compositor** already
  in the engine, which is prior evidence some WUC↔engine interop already works.
  - **Lean:** prefer **Path M for rendering too** (`IMFMediaEngine` + `IMFContentProtectionManager`,
    single-rendition first, ABR later) *because it unifies DRM and non-DRM on the one `BindSurfaceHandle` seam
    and sidesteps the WUC-into-raw-DComp bridge entirely* — **unless** the bridge proves clean and MF-level
    PlayReady proves materially heavier than hand-authoring `PlayReady.*`, in which case take **Path W +
    `BindChildVisual`** for the free ABR and proven recipe.
- **Either way the spine exposes two handoff seams**, both terminating as a child visual under the raw-DComp
  root with identical hole-punch/crossfade/registry: `BindSurfaceHandle(nuint)` (Path M, canvas + possibly DRM)
  and `BindChildVisual(IntPtr dcompVisual)` (Path W MediaPlayer **and** the WebView2/Widevine fallback, which
  also yields a WUC visual via `CoreWebView2CompositionController`). This is the reconciled contract.

---

## 4. Milestone plan (sequenced, single-thread-correct first)

Each milestone is independently demonstrable. Spikes S1/S2 gate the DRM investment and are cheap.

**M0 — present-tree restructure + engine-owned surface (no media, no DRM).**
`BindDComp` goes from one visual to `root → AddVisual(uiVisual, above) + lazy AddVisual(videoChild, below)`.
Prove `BindSurfaceHandle` end-to-end with a `DCompositionCreateSurfaceHandle` surface the engine paints a test
pattern into (per correction #4). **Verify the graded hole with a `--screenshot`** — this is the assumption
everything else rests on; fallback is a binary reveal.

**M1 — `DrawVideo` opcode + graded crossfade.** Add `DrawOp.DrawVideo`, the `VideoHole` painter-order pass, the
premul-0 hole-punch clear, and the album-art→poster→live crossfade over `VideoReady` (reusing `ImagePipeline`).
Still fed by the M0 engine-owned surface.

**M2 — `VideoSurfaceRegistry` + `UseVideoSurface` + placement/PiP.** Priority arbitration (theatre>PiP>sidebar),
one live surface, atomic handoff, per-frame `Commit` at phase 11, snap-to-poster on interactive resize.

**M3 — unprotected real video via `IMFMediaEngineEx` (Path M).** Windowless-swapchain mode →
`GetVideoSwapchainHandle` → `BindSurfaceHandle`. Drive **Spotify canvas** (unencrypted looping MP4/GIF — no
license server involved). This is the first end-to-end real-video milestone and closes the non-DRM spine.

**S1 — PlayReady trust spike (headless, gates all DRM work).** *Before* any DRM rendering. In the **packaged
MSIX build**, against the public **Axinom** PlayReady DASH test vector (isolates interop from Spotify):
`AdaptiveMediaSource.CreateFromStreamAsync` → build `MediaProtectionManager` with the four properties (system-id
map → `PlayReadyWinRTTrustedInput`, `MediaProtectionSystemId` `{F4637010-…}`, container `{9A04F079-…}`,
`UseSoftwareProtectionLayer=true`) → subscribe `ServiceRequested` (+`ComponentLoadFailed`) → `new MediaPlayer {
ProtectionManager = mpm }` → `SetSurfaceSize` → set `Source` **last**. **The single pass/fail signal: does
`ServiceRequested` fire and `MediaOpened` raise, instead of `MediaFailed 0xC00D715B`?** (In #10873 with
`MediaPlayerElement`, `ServiceRequested` never fires — so "it fires" alone proves the protected topology built
and ITA verification passed. Rendering is a separate, later check and captures black anyway.)

**S2 — compositing-bridge spike (parallel to S1).** Determine whether a WUC `MediaPlayerSurface` can be
composited into this engine's raw-DComp tree, using the existing `Backdrop`/acrylic WUC interop as the
reference. Output: does Path W's surface reach the screen here, or is Path M (raw handle) required for rendering?

**M4 — native PlayReady playback (engine chosen by S1+S2).** Port Wavee's proven manifest→MPD/PSSH/tenc/key-id
parsing app-side; wire the chosen rendering engine (Path M MF-CDM `BindSurfaceHandle`, or Path W MediaPlayer
`BindChildVisual`) into the spine. Drive one real Spotify PlayReady track. Manual verification only (pixels
capture black).

**M5 (deferred) — Widevine/WebView2 fallback.** Only for content Spotify serves *exclusively* as Widevine/WebM.
Port Wavee's live EME path; the `CoreWebView2CompositionController` visual lands via `BindChildVisual`. Kept
**secondary** because it is memory-heavy (the user's own complaint) and unavoidable only because there is no
embeddable native Widevine CDM. Prefer PlayReady whenever the manifest offers an mp4/playready profile.

---

## 5. Interop hand-authoring inventory (for the DRM path, if Path W)

Already projected (use directly): `MediaPlayer`(+Source/Source2/4), `MediaProtectionManager` +
`ServiceRequested`/`Completion`/args + the **named** event delegate, `MediaSource`(+statics),
`AdaptiveMediaSource`(+statics), `MediaPlaybackItem`(+factory), `PropertySet`, `IAsyncAction`/
`IAsyncActionCompletedHandler`/`IAsyncInfo`, `IMediaPlayerSurface`.

Must be hand-authored:
- **The entire `Windows.Media.Protection.PlayReady.*` namespace** (not projected): ~6 cold-COM interfaces
  (`IPlayReadyLicenseAcquisitionServiceRequest`, `IPlayReadyIndividualizationServiceRequest`,
  `IPlayReadySoapMessage`, `IPlayReadyServiceRequest`, `IPlayReadyStatics`, …) + the `PlayReadyWinRTTrustedInput`
  activatable class (needs only its runtime-class string) + system-id GUIDs/enums. Interface shapes/IIDs from
  the Windows SDK `windows.media.protection.playready.h` metadata. Mechanical but real.
- **One `IAsyncOperationCompletedHandler<AdaptiveMediaSourceCreationResult>` CCW** — the only generic await site;
  compute its parameterized IID via the RFC-4122-v5 algorithm `MediaButtonHandler.cs` already implements, and
  hand-declare the two `IAsyncOperation<T>` slots (`put_Completed`, `GetResults`) if TerraFX's generic proves
  shape-only.
- **Only if Path 1 surface is ever attempted:** the undocumented `IMediaPlayerSurfaceHandleProvider` — **avoid**;
  no public IID. Use `GetSurface(Compositor)` (Path W) or `IMFMediaEngineEx` (Path M) instead.

---

## 6. Gates, testing, and honesty

- **What the automated gates CAN cover:** the compositing spine headlessly (present-tree, opcode, registry,
  zero-alloc phases 6–13); the graded hole via `--screenshot` (M0); and the pure parsing — manifest→MPD, PSSH/
  tenc box parsing, key-id formatting — as unit tests mirroring Wavee's `SpotifyWebEmeVideoManifestTests`.
- **What they CANNOT:** live DRM playback. Protected pixels capture **black**, and the license path needs a real
  Spotify session + real license servers. M4 is **manual/integration verification only** — state this plainly,
  don't pretend a golden-image gate covers it.
- **S1 is the honest gate for the whole DRM investment:** `ServiceRequested` fires ⇒ proceed; `0xC00D715B` ⇒
  the native fix doesn't hold on this stack and Widevine/WebView2 becomes the primary (roles swap on the same
  seams).

---

## 7. Remaining open questions

1. **S1 outcome** — does owning the `MediaPlayer`+`MediaProtectionManager` wiring (attach-before-`Source`, no
   `MediaPlayerElement`) actually clear `0xC00D715B` where WaveeMusic's stack failed? The whole DRM path hinges
   here. *(The "why did they use Widevine" question is already answered: platform bug, not content scarcity.)*
2. **S2 outcome** — can a WUC media surface composite into this engine's raw-DComp tree (leveraging the existing
   `Backdrop` interop), or is Path M's raw handle required for rendering? Decides Path M vs Path W for M4.
3. **PlayReady content coverage** (secondary) — measure the fraction of real tracks that resolve an mp4/playready
   profile; any Widevine-only tracks force the M5 fallback regardless.
4. **Segment auth** — is Spotify's CDN segment auth in-URL (Wavee suggests yes) or does it need
   `Windows.Web.Http` headers via `DownloadRequested`? Determines whether the segment-handler seam is load-bearing.
5. **ABR under Path M** — if rendering goes MF-direct, decide single-rendition-first vs. building a custom
   adaptive MF source.

---

## 8. Canon follow-ups (when Phase 1 lands in code)

Update `design/subsystems/media-pipeline.md §8` (the two-seam handoff, `CreateSurfaceFromHandle` not
`ICompositorInterop`, snap-on-resize default), `design/subsystems/gpu-renderer.md §3.1` (the `DrawVideo`
opcode / `VideoHole` painter pass), `architecture-spec.md §5.1` (the multi-child present tree); register the new
opcode + PAL seam in `SPEC-INDEX.md §2` and the `subsystems/README.md` ownership map; run `check-canon.ps1`.
