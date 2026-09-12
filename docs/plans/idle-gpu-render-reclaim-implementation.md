# Render-owned exit reclamation and repeated flip verification

Date: 2026-09-12. Working-tree implementation; verification results belong in the Wavee handoff.

## Evidence and scope

The earlier idle-GPU reduction came from allowing clamped replay for positive-sigma blur groups. Preserve that work.
The remaining observation is four animation rows and one orphan persisting with ongoing GPU presentation.
Four rows are consistent with a keyed replacement: incoming Dy and opacity, outgoing Dy and opacity. Only the
outgoing pair belongs to the orphan. UI slab counts alone do not prove four active render-thread generators.

The render thread publishes completion feedback and wakes the UI. `RunFrame` imports feedback before its idle
guard, setting frame-needed when a Done pose arrives. Therefore masking `WakeReasons.Orphans` does not by itself
prove that an ordinary healthy flip is stranded. It does leave the existing wall-clock reclamation backstop
without an independent wake when feedback does not retire an orphan. Baseline live-probe evidence must distinguish
these cases; do not report a successful baseline as reproducing the original incident.

## AppHost implementation contract

Retain render ownership of the compositor. Add an allocation-free orphan readiness predicate using the same
conditions as reclamation: no remaining tracks, expired per-orphan animation deadline, or the existing 2000 ms
wall-clock backstop. Render-owned active exits must not request continuous UI production solely because they exist.
An orphan ready for reclamation must request a UI frame even when compositor animation belongs to the render thread.

`HasReclaimableOrphan` supplies the wake decision and `OrphanDeadlinePassed` shares the timeout decision with
`ReclaimSettledOrphans`. Together they evaluate the following existing operations:

```csharp
var orphan = _scene.OrphanAt(i, out _, out _);
double ageMs = (nowTicks - _scene.OrphanEnqueuedTicks(i)) * 1000.0 / Stopwatch.Frequency;
float ownMaxAge = _scene.OrphanMaxAgeMs(i);
bool ready = !_anim.HasTracks(orphan)
    || ageMs >= OrphanSettleTimeoutMs
    || (ownMaxAge > 0f && _scene.OrphanAnimAgeMs(i) >= ownMaxAge);
```

Clamp an otherwise indefinite/long wait to the earliest outstanding wall-clock orphan deadline, respecting
shorter existing waits. This clamp is in `RecommendedWaitMs`, after `ClampWaitToTimers`, for non-minimized
render-owned orphans. Preserve minimized sleep because paint/reclamation cannot run while minimized. Avoid a
zero-timeout spin; the existing timer-wait clamp uses a 1 ms floor for the same reason. Reuse the readiness predicate
in reclamation so scheduling and cleanup agree. Keep reclamation, scene mutation, and UI animation slab mutation
on the UI thread. Add behavioral coverage for render-owned readiness and deadline wake behavior, alongside the
existing healthy headless flip gate.

## Real Windows reproduction probe

`src/FluentGpu.WindowsApp/Probes/FlipIdleProbe.cs` is routed from `Program.Main` by `--flip-idle-probe`. It creates
a real Windows/D3D12 host through `FluentAppHarness`, takes over its diagnostic loop, and explicitly rejects a host
whose `Animation.RenderOwnsCompositor` is false. Twelve signal writes replace the digit key at one-second intervals;
the final replacement is followed by over five seconds of observation. The implemented scene shape is:

```csharp
new BoxEl
{
    Key = "digit-" + value, Width = 80f, Height = 80f,
    Enter = new EnterExit(Dy: 14f, Opacity: 0f, Active: true),
    Exit = new EnterExit(Dy: -14f, Opacity: 0f, Active: true),
    Transition = MotionTok.ControlFast,
    Children = [new TextEl(value.ToString()) { Color = Tok.TextPrimary }],
}
```

The outer fixed cell clips its child. Signal reads occur in component render; the changed key deliberately models
the identity replacement made by countdown digits. The probe uses the production wait request, shortened only to
the next diagnostic sample or scheduled replacement:

```csharp
var wait = host.WaitRequestWithDetached();
wait = wait with { TimeoutMs = wait.TimeoutMs < 0 ? remaining : Math.Min(remaining, wait.TimeoutMs) };
window.WaitForWork(in wait);
```

At 700 ms and 950 ms after each second boundary, before calling `RunFrame`, it checks zero UI tracks and zero
orphans. Each 950 ms sample also requires an unchanged `PresentedSequence` since 700 ms. Sampling before the frame
prevents that diagnostic wake from hiding pending cleanup. The probe requires twelve replacements, at least 34
samples, peak tracks at least four, peak orphans at least one, and actual GPU presentations. It returns nonzero on
failure or early window closure. No service data/daylist window is required.

`--flip-idle-probe-stress` extends the normal phase to 48 replacements. Every third replacement deliberately blocks
the UI thread for 300 ms while the render thread continues; other live-animation intervals receive paint-property
publications every 20 ms for the first 250 ms. These timings are diagnostic fault stimuli, not animation policy.
The same pre-frame quiet assertions remain in place.

`--flip-idle-probe-backstop` follows the normal phase with a synthetic orphan whose own animation deadline is zero
and whose opacity and Dy tracks loop forever. It uses public scene/animation operations to inject the stuck-exit
condition. The observation loop uses the production wait, capped only by a four-second watchdog, and therefore
does not manufacture a frame at the two-second deadline. It requires an explicitly finite idle wait, reclamation
before three seconds, and zero tracks/orphans. This isolates the timeout scheduling bug from healthy completion
feedback, and is intended to fail on the old engine and pass after the host fix. Both flags can be combined.

## Orchestrator validation

Only the orchestrator builds, runs tests, or launches apps. Rebuild the Release VerticalSlice before trusting its
check count; the initial baseline expected 1519 checks, not the stale 1518 executable. Run the live probe before and
after the host fix, retaining its full stderr and exit code:

```powershell
dotnet run --project src/FluentGpu.WindowsApp -c Release -- --flip-idle-probe
```

Run required engine and Wavee checks in Debug and Release, then launch and monitor Wavee. Compare process-specific
GPU counter instances before summing; report actual present-count deltas separately from UI rendered-frame counts.
Use `apply_patch` for edits and check diff size afterward; never round-trip source through PowerShell text writes.

## Playback load: stencil admission (2026-09-12 continuation)

The settled Liked Songs page with lyrics produced 3601 submissions in 30 seconds, all full redraws and all marked
`BackendUnsupported`, while mean damage was 2.04 percent. The cover's heart is a real `ClipPath` whose children are
crisp mosaic images; the blurred ground is a sibling using baked image derivatives. The old blanket stencil veto
therefore forced full-window work for unrelated lyric damage. This is separate from idle orphan reclamation.

`RepaintStreamSafety.Scan` now tracks `layerDepth` and `stencilDepth`. It admits balanced masks outside layers,
including nested masks, and layer siblings. The critical checks are real implementation code:

```csharp
// PushLayer:
if (stencilDepth != 0) return false;
layerDepth++;
// PushStencilClip:
if (layerDepth != 0) return false;
stencilDepth++;
// Final stream condition (each pop also rejects depth zero before decrementing):
return layerDepth == 0 && stencilDepth == 0;
```

The existing layer-kind filter still rejects acrylic. Target switches and blur-halo changes within a stencil scope
remain excluded. Backend stencil clear/mask/erase behavior is unchanged. `DamageSuite` checks balanced nested masks
with sibling blur, both directions of forbidden nesting, and unmatched pushes/pops. The owner is
`gpu-renderer.md` §6/§13.1a; `SPEC-INDEX.md` references that policy rather than duplicating its case list.

Identity scenarios 8 and 9 exercise a static heart beside changing blur content, then nested fractional heart masks
with intersecting damage and a sibling opacity lease. The orchestrator's pre-policy baseline was 8/10 identical,
zero mismatches and two inconclusive cases: both new scenarios failed to reach `FullIntoCanvas` because of the veto.
After the policy change, require 10/10 pixel identity with actual partial routing, then repeat the live workload
and compare GPU and route counters at the same window geometry. Do not claim improved playback cost from the
headless scanner tests alone.

## Image fade clock rewind (user-visible flashing)

Real-window image time previously advanced through the animation delta, which can be zero after idle resync or
clamped during a hitch. The renderer extrapolated that time with wall time. A fresh sparse UI publication could
therefore replace an already completed fade with an older clock anchor, both flashing artwork and extending the
full-redraw/crossfade interval. This is separate from orphan cleanup.

`ImagePresentationClock` belongs to the shared `ImageCache`, not individual AppHosts, and samples unclamped wall
time. AppHost samples before reconcile-time swaps and both regular and idle/hidden completion pumps.
`ImageCache.ClockCapturedAtMs` travels through `ImageRecordingSnapshot`,
so render extrapolation uses the actual image sample timestamp, not the later animation capture timestamp:

```csharp
clockMs + (float)Math.Max(0, nowMs - sampledAtMs)
```

Image reveals finish across hidden time without rendering. Authored compositor animations retain their existing
per-window pause semantics. Fixed/manual headless clocks still advance through their deterministic delta. Four unit
tests cover sparse publications, restore publication ordering, a late decode, and shared parent/popout clocks. Real-window
track-change verification is required in addition to those tests.

## Adopted-but-elided publication continuity

The later live census showed 133 `PublishGap` causes in 30 seconds. Producer adoption and GPU submission have
different sequence histories: the render consumer can adopt a publication, prove its command stream byte-identical
with empty damage, and elide submission. The publisher correctly advances its adopted baseline, but D3D's submitted
baseline then appears to have an uncovered gap on the next real draw.

`RenderSubmissionContinuity` tracks the last successful submission and the newest proven equivalent publication.
AppHost records elisions only after `ShouldSkipRenderSubmit` succeeds, extends `CarriedFromSeq` before the next
submission, and consumes the proof only after a successful device submit. A target-epoch change resets it. The
implemented extension is:

```csharp
if (carriedFrom == 0 || _submitted == 0 || _verified <= _submitted || carriedFrom - 1 > _verified)
    return carriedFrom;
return System.Math.Min(carriedFrom, _submitted + 1);
```

Thus submit 10, elide 11/12, then producer carry 13 becomes GPU carry 11. Unknown carry, a gap beyond the verified
range, and an absent submitted baseline remain unmodified. `gate.repaint.elided-submission-continuity` covers
these positive and negative cases. This preserves the backend's conservative gap rule and avoids keeping all
producer damage alive merely because equivalent GPU work was skipped.

The always-on census now distinguishes direct redraws from canvas rebuilds, named safety causes, high raw coverage
on full frames, post-merge coverage, and empty-canvas fallback. It logs one representative high-coverage region per
interval with target dimensions and rect coordinates. The raw-coverage early scan veto was removed from D3D:
offscreen rectangles must pass to the policy's existing clipped/coalesced coverage check. Identity scenario 10
(`offscreen-damage-visible-tail`) adds a huge offscreen dirty bar with a small visible tail and a fully offscreen
second bar; partial rendering must remain pixel-identical to full rendering.

## Continuous lyric damage after navigation

Live playback exposed independent retention failures. An ancestor's consumed self-content stamp shared a lifetime
with fresh descendant content; continuous lyric updates kept the whole-page mark alive. `SceneStore` now stores
four contribution stamps (self/descendant by content/transform), retires only consumed contributions, and records
partial retirement as an incremental-capture change:

```csharp
byte self = UnconsumedRecordBits(_recordDirtySelf[idx], stamps.SelfTransform, stamps.SelfContent, consumedSeq);
byte descendant = UnconsumedRecordBits(_recordDirtyDescendant[idx], stamps.DescendantTransform, stamps.DescendantContent, consumedSeq);
// On a change:
_recordDirtySelf[idx] = self;
_recordDirtyDescendant[idx] = descendant;
_recordDirty[idx] = (byte)(self | descendant);
NoteCaptureChanged(idx);
```

A third publisher slot used during consumer handover could then be abandoned by first-fit selection. The writer
now starts its existing generation-checked CAS sweep at the oldest initialized writable snapshot. Reading/published
slots remain excluded and ordinary two-slot reuse needs no third allocation. This lets the retention floor advance.

Negative controls: four new dirty-lifetime gates fail against the old engine, including 128 frames of 100% repaint
coverage; the fixed case stays at 3.08%. The three-slot handover test also fails against the old engine and passes
with oldest-writable selection. Steady retirement allocates zero bytes; stamp storage increases by 24 bytes per
scene-capacity slot. Final real-device identity remains 10/10 identical, zero inconclusive/dropped instances.
