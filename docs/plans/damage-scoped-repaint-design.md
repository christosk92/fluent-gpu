# Damage-scoped repaint — animated nodes and effect layers

**Status (updated after implementation):**

- **Step 1 — LANDED.** A1/A2/A3 are fixed. Measured: a small animated leaf three containers deep damages 0.53 % of
  the window where it damaged 100 %. Five gates (`gate.damage.compositor-*`), each verified to fail on the old
  behaviour. Two additions this doc did not anticipate: the record itself is elided on a no-change tick (not just
  the present), and `RecordDirtySelfBits` had to move to the self epoch too — leaving it on `HasOverlayPaint` kept a
  cadence-HELD pose damaging its band, because "has a paint row this epoch" is true for a held pose.
- **The layered 1-rect cap — LIFTED**, which this doc listed as out of scope. Its stated reason (a pool-leased group
  RT means the stream can only be walked once) is false; the real blocker was that the backend's layered submit
  interleaved per-frame work with the decode loop. Split into Begin/Walk/End. This turned out to be a prerequisite
  rather than an extra: `layerKind` flips to Groups for the whole frame as soon as ANY node is a fractional-opacity
  group, so at a cap of 1 two distant bands merged into a near-full-window union and fell to FullDirect.
- **Step 2, the σ=0 EdgeFade half — LANDED** (§2c items 4, 5, 6). This is the one that unlocks a real app: a
  scrolling UI has an edge fade on screen permanently, so the veto meant almost every frame was full regardless of
  its damage. Verified on device — `--repaint-identity` scenario `edge-fade-strip-straddle`, 2 replay rects,
  pixel-identical to a full redraw on an Adreno X1-85.
- **Step 2, the σ>0 BLUR half — LANDED.** §2a, §2b and §2c items 1, 2, 3 are done; a lyrics-open frame is no longer
  `FullDirect`. The halo is DERIVED from the open-group stack (`_layerHaloPx`, recomputed at the four places
  `_opacityGroups` changes) rather than pushed/popped by hand — the PushLayer arm has eight early-`continue` paths and a
  hand-paired counter would leak a halo on whichever one was missed, which is the trap the note below predicted. It
  inflates only the `_rootDamage` half of the scissor plus `_cullRect` (TapRadius is PHYSICAL px, `_cullRect` is DIP —
  the divide is in `Cull`), and PopLayer's existing order drops it before `BlurInPlace` and before every composite, so
  the composite still uses the uninflated `CurrentScissorRect()` with no extra sequencing. Item 3 (§2b) is
  `EnclosingBlurHaloDip`, an O(depth) ancestor walk applied to all three damage arms. A clamped frame refuses to mint a
  blur pin (`ReplayCoversRegion`). `RepaintStreamSafety.Scan` now vetoes only Acrylic and `PushStencilClip`.
  **Verified on the real Adreno X1-85: `--repaint-identity` 8/8 pixel-identical with the new `blur-group-straddle`
  scenario, which takes the partial route (`rects=2 partialFrames=2`) — and with the halo forced to 0 that same
  scenario fails by 10 321 px over a 565×163 box, so it is a real test of the fix and not a decoration.** The two
  stream-safety gates that encoded the old veto were rewritten to the new contract; §13.1a's *Replay-unsafe streams*
  row and `RepaintStreamSafety`'s XML contract are updated and `check-canon.ps1` passes.
- **Still not done from Step 2:** §2c item 5 (extending the headless `ReplayLayered` CPU reference with a σ-halo group)
  and item 7 (the perf-only narrowing of the blur source to `(R ∩ output) ⊕ halo`). Item 5 is a from-scratch separable
  blur — that reference models every PushLayer as flat alpha and has no Gaussian — so the σ>0 arithmetic is currently
  gated on device rather than headlessly. Item 7 is explicitly risky (it breaks the pin size-exactness contract unless
  `RegionBox` shrinks consistently) and buys fill, not correctness.

Written 2026-09-11 against the `repaint=1.0` finding on WaveeMusic (Snapdragon X Elite / Adreno X1-85).

## The finding

On the driving app, **every awake frame repaints the whole window**. Measured on the user's machine: a steady
state with only a scrolling title (`Marquee`, a looping `TranslateX` keyframe row at `Cadence.At(60)`) and a
playhead costs **~64 % of the Adreno's 3D engine** while the UI loop runs at 17 frames/s. `nav.frames` reports
`presented=480-497` per 4 s — the render thread is presenting at panel rate — and every `frame.slow` line carries
`repaint=1.0` with `gpu=2-6.5 ms`.

§13.1 of `gpu-renderer.md` promises the opposite: *"Animated transforms dirty only old∪new bounds → a spinner
repaints a tiny region."* Three mechanisms defeat it, each sufficient alone:

| # | Mechanism | Code |
|---|---|---|
| A1 | A render-thread pose marks **every ancestor up to the root** as `TransformDirty\|PaintDirty`; the recorder damages each such node's `SubtreeBounds`, and the root's is the window. | `SceneRecordingSnapshot.Animation.cs:240` `MarkCompositorDirty` → `SceneRecordingSnapshot.cs:502` `Flags()` → `SceneRecorder.cs:2678-2691` |
| A2 | Every clock-driven re-record on the render thread is forced full outright. | `AppHost.cs:1002` `ForceFull(RepaintFullReason.DetachedContent)` |
| A3 | Rows are re-posed **every tick even when their value is held or `Done`**, so A1 fires at panel rate for a 60 Hz row, and on every UI publication for a finished one. | `RenderCompositorAnimations.cs:156-177` (`Tick` poses all non-parked live rows; `Adopt` ends in `Tick`) |
| B | Any `PushLayer` that is not plain `Opacity` (Blur, **both** EdgeFade classes, Acrylic) or any `PushStencilClip` disqualifies the whole frame from partial replay. Wavee always has edge-fades on screen (the marquee's own `FadeBand = 24`, every `AutoEdgeFade` scroll view) and lyrics adds blur. | `RepaintPolicy.cs:359-378` `RepaintStreamSafety.Scan`, consumed at `D3D12Device.cs:1686,1709` |

**Step 1** fixes A1-A3. **Step 2** fixes B. Step 1 alone does not lower Wavee's GPU while an edge-fade is on
screen — B still forces `FullDirect` — but it is the prerequisite: without it the damage region is the window, so
there is nothing for Step 2 to clamp to.

## How the reference engines do it

Researched from the local checkouts under `C:\WAVEE` (see `docs/design/README.md` on reference repos):

- **Damage is per changed leaf, never per ancestor.** WebRender records a primitive's spatial node only when it
  differs from the tile-cache root (`webrender/src/picture.rs:3144`), and compares transforms *relative to the
  cache root* (`get_transform_key` :7034); stacking contexts are not primitives and carry no dependency of their
  own. Chromium's `DamageTracker::AccumulateDamageFromLayer` (`cc/trees/damage_tracker.cc:424`) unions a changed
  layer's **old and new** visible rect in target space. Slint's `compute_dirty_regions`
  (`internal/core/partial_renderer.rs:580`) carries both the old and the new screen transform down the walk and
  marks only items whose own transform differs. XAML's independent (compositor-driven) change dirties **bounds
  only** (`uielement.cpp:8751`), and an ancestor receives `m_fNWSubgraphDirty` — documented as *"one of the
  element's children has changed"*, a walk trail, not a content change (`uielement.h:3114`).
- **A value that did not change is not a change.** WebRender's opacity binding compares with `approx_eq` before
  marking the tile dirty (`picture.rs:2294`).
- **A blur grows the dirty rect; it never disables partial repaint.** WebRender inflates by
  `BLUR_SAMPLE_SCALE = 3.0` σ (`picture.rs:4327` `get_coverage`, `box_shadow.rs:279`) and maps a dirty primitive's
  rect through every blur surface between it and the tile cache (`picture.rs:3064-3100`). Chromium's
  `DirectRenderer::ExpandDamageForPixelMovingFilters`
  (`components/viz/service/display/direct_renderer.cc:992`) unions the filter's expanded rect when damage
  intersects it. Then the source is rendered over the grown rect and only the dirty sub-rect is written back
  (WebRender `take_context` / `new_tile_composite`, `picture.rs:5517-5598`).
- **Backdrop (acrylic) is a dependency, not a veto.** WebRender defers a dirty test for it and invalidates the
  whole backdrop rect only when damage intersects it (`picture.rs:3481-3515`, :3825-3853); Chromium does the same
  (`damage_tracker.cc:526`).

Our A1 is precisely the ancestor marking every one of them avoids; our B is the veto none of them needs.

## Step 1 — a pose damages the posed node, not its ancestors

### 1a. Split the overlay's "trail" from "this node's own pose changed"

`_overlayDirtyEpoch` is doing two unrelated jobs: it is the **walk trail** (so an ancestor re-records instead of
replaying a stale span that contains the child's old pixels) *and*, through `Flags()`, the claim **"this node
moved"**. Keep the first, and add a second, self-only stamp for the second.

`Scene/SceneRecordingSnapshot.Animation.cs`:

```csharp
    private uint[] _overlayDirtyEpoch = [];
    /// <summary>Per node: the epoch in which this node's OWN composited pose last CHANGED. The dirty trail above
    /// says "something under here moved, re-walk me"; this says "my pixels moved", which is the only claim that may
    /// produce repaint damage. Splitting them is what stops one animated leaf from damaging the window: the trail
    /// runs to the root by construction, so feeding it to <see cref="Flags"/> made every ancestor — ending at the
    /// root, whose SubtreeBounds IS the window — report TransformDirty to SceneRecorder's §13.1 damage block.</summary>
    private uint[] _overlaySelfEpoch = [];
```

Written by a new marker, called only when the composited value actually changed:

```csharp
    /// <summary>Mark this node's own pose as CHANGED this epoch (damage-producing), plus the ancestor trail. A pose
    /// that re-poses the same value calls <see cref="MarkCompositorDirty"/> alone — the node still reads its posed
    /// paint, its span still re-records if something else dirtied it, but it contributes no repaint band.</summary>
    internal void MarkCompositorSelfChanged(NodeHandle node)
    {
        _overlaySelfEpoch[node.Raw.Index] = _overlayEpoch;
        MarkCompositorDirty(node);
    }
```

The three overlay writers take the flag (default `true` keeps every existing caller's behaviour):

```csharp
    internal ref NodePaint CompositorPaint(NodeHandle node, bool changed = true)
    {
        uint index = node.Raw.Index;
        if (changed) MarkCompositorSelfChanged(node); else MarkCompositorDirty(node);
        ...
    }

    internal void SetCompositorInteraction(NodeHandle node, bool press, float value, bool changed = true)
    internal void SetCompositorBrush(NodeHandle node, float value, bool changed = true)
```

`PrepareCompositorOverlay` grows it (`Grow(ref _overlaySelfEpoch, count)`), `BeginCompositorOverlay`'s epoch-wrap
clears it beside `_overlayDirtyEpoch`, and `CompositorOverlayBytes` counts it (+4 B/node/snapshot).

`Scene/SceneRecordingSnapshot.cs` — the two readers that may claim damage:

```csharp
    public NodeFlags Flags(NodeHandle node) => _flags[node.Raw.Index]
        | (_overlaySelfEpoch[node.Raw.Index] == _overlayEpoch ? NodeFlags.TransformDirty | NodeFlags.PaintDirty : 0);
    public byte RecordDirtySelfBits(NodeHandle node) => (byte)(_dirtySelf[node.Raw.Index]
        | (_overlaySelfEpoch[node.Raw.Index] == _overlayEpoch ? SceneStore.RecordDirtyContent : 0));
```

`RecordDirtyBits` / `RecordDirtyDescendantBits` keep reading `_overlayDirtyEpoch` unchanged — the trail is exactly
what they want, and span reuse must still be denied along it. `HasOverlayPaint` is then unused by
`RecordDirtySelfBits`; keep it only if another caller needs it, otherwise delete it (no legacy paths).

Damage for the posed node stays the existing, proven path: `SceneRecorder.cs:2678` sees `TransformDirty` on the
node itself, emits `RepaintBand(result.SubtreeBounds)` — the superset of the node **and its descendants**, which is
what an animated transform/opacity/hover-fade actually moves — and unions the prior extent from the span table
(old ∪ new).

### 1b. Only pose a change when the value changed

`Animation/RenderCompositorAnimations.cs`. Per row, remember the value it last posed:

```csharp
    private struct State
    {
        ...
        /// <summary>The value this row last POSED onto a scene snapshot, and whether it ever did. A cadence-held row
        /// (`PeriodMs` not yet elapsed) and a `Done` row re-pose the same number every tick; posing it as a CHANGE is
        /// what made a 60 Hz marquee damage the window at 120 Hz.</summary>
        public float PosedValue;
        public bool HasPosed;
    }
```

`Tick`'s per-row block folds a per-node "changed" bit alongside the accumulator:

```csharp
    private readonly Dictionary<NodeHandle, NodeAcc> _accumulators = new(64);
    private struct NodeAcc { public AnimEngine.Accum Acc; public bool Changed; }
```

```csharp
            ref var state = ref _states[i];
            Evaluate(ref state, nowMs, _tickIntervalMs);
            ref readonly var row = ref state.Desired.Row;
            if (!state.Parked && scene.IsLive(row.Node))
            {
                // Bitwise compare: a held or Done row re-poses the IDENTICAL float, and a live row's next sample
                // differs in at least one ulp — there is nothing to tolerance here (WebRender's approx_eq guards a
                // property binding that can be re-sent unchanged; our Value is recomputed analytically).
                bool changed = !state.HasPosed || state.Value != state.PosedValue;
                state.PosedValue = state.Value;
                state.HasPosed = true;
                if (row.Channel is AnimChannel.HoverFade or AnimChannel.PressFade)
                    scene.SetCompositorInteraction(row.Node, row.Channel == AnimChannel.PressFade, state.Value, changed);
                else if (row.Channel == AnimChannel.BrushFade) scene.SetCompositorBrush(row.Node, state.Value, changed);
                else
                {
                    ref var acc = ref CollectionsMarshal.GetValueRefOrAddDefault(_accumulators, row.Node, out bool exists);
                    if (!exists) acc.Acc = AnimEngine.Accum.FromPaint(in scene.Paint(row.Node));
                    acc.Acc.Fold(row.Channel, state.Value, replace: true);
                    acc.Changed |= changed;            // any channel of this node moving damages the node once
                }
                HasActive |= !_paused && !state.Done;
            }
```

and `Compose(scene, entry.Key, entry.Value.Acc, entry.Value.Changed)` passes it to
`scene.CompositorPaint(node, changed)`.

**Reverting rows.** A node whose row disappears (instance dropped from the desired set) or becomes `Parked`
presents its AUTHORED pose again — a real pixel change that no row will report. `Adopt` collects those nodes
before the swap and `Tick` marks them:

```csharp
    private NodeHandle[] _reverted = [];
    private int _revertedCount;
```

In `Adopt`, after the retained/seeded loop and before `(_states, _nextStates)` swap, walk `_states[0.._count]` for
entries whose `Instance` is absent from `_nextIndices` (or whose carried state flipped to `Parked`) and whose
`HasPosed` is true, appending `row.Node` to `_reverted` (grown like `_nextStates`; a retained state that re-parks
also sets `HasPosed = false` so its un-park re-poses as a change). `Tick`, after the row loop:

```csharp
        for (int i = 0; i < _revertedCount; i++)
            if (scene.IsLive(_reverted[i])) scene.MarkCompositorSelfChanged(_reverted[i]);
        _revertedCount = 0;
```

Allocation: `_reverted` grows via `SceneRecordingSnapshot.Grow` on the publisher-driven `Adopt` path only, like
`_nextStates`/`_feedback`; the steady tick allocates nothing (`gate.compositor-alloc`, `gate.compositor-row-alloc`
must stay green).

### 1c. Stop forcing full on clock-driven re-records, and let held frames elide

`Hosting/AppHost.cs`, the render-thread record block (~L992-1011). `imageClockMs` moves above the damage decision:

```csharp
                var repaint = stats.RepaintDamage;
                repaint.Union(rf.Submit.RepaintDamage);
                float imageClockMs = RenderImageClock(rf, sceneFrame);
                if (rf.TargetEpoch != _lastRenderedTargetEpoch)
                {
                    repaint.ForceFull(RepaintFullReason.TargetInvalidated);
                    _lastRenderedTargetEpoch = rf.TargetEpoch;
                }
                // A clock-driven re-record USED to force full here ("no scene bit describes it"). Since §1a a pose
                // stamps the posed node's own overlay-self epoch, so the recorder's §13.1 block describes exactly the
                // nodes whose pixels moved. What is still undescribed is a crossfade: its pixels advance with
                // ImageClockMs under byte-identical commands and no dirty bit anywhere.
                else if (!fresh && sceneFrame.Images.HasCrossfades(imageClockMs))
                    repaint.ForceFull(RepaintFullReason.DetachedContent);
                ulong dlHash = DrawListHash(_renderCommands.Bytes, _renderCommands.SortKeys);
                bool clockActive = sceneFrame.Images.HasCrossfades(imageClockMs);
```

`clockActive` drops `_renderAnimations.HasActive` for the same reason: a tick whose poses did not change records a
byte-identical stream AND an empty repaint region, which is precisely `ShouldSkipRenderSubmit`'s question. A tick
that did change fails the hash compare on its own. Update that method's doc comment (`AppHost.cs:872-876`) to say
"no clock-driven pixels are live (image crossfades)".

**Known over-inclusion, out of scope:** a snapshot keeps its publication's authored dirty bits for the slot's life
(`SceneRecordingSnapshot.cs:434-436`), so a second, animation-driven record of the SAME publication re-damages that
publication's authored-dirty nodes. Over-inclusive, never under-inclusive, and today those frames are full anyway.

### 1d. Gates (headless, `FluentGpu.VerticalSlice`)

Model on `DamageSuite`'s existing record-level damage gates (`gate.damage.record-moved-node-old-union-new`) and on
`CompositorAnimationChecks.cs:106-118`, which already drives `RenderCompositorAnimations.Adopt/Tick` against a
snapshot headlessly.

| Gate | Asserts | Fails today with |
|---|---|---|
| `gate.damage.compositor-pose-scoped` | A looping `TranslateX` row on a small leaf, three container levels deep in an 800×600 scene: each posed frame's region is **not full**, its coverage is within a small multiple of (leaf old ∪ new)/window, and it CONTAINS both the leaf's prior and current device rects. | coverage 1.0 (root `SubtreeBounds`) |
| `gate.damage.compositor-held-empty` | A `Cadence.At(30)` row ticked at 120 Hz: between its steps the region is EMPTY, the DrawList hash is unchanged, and `ShouldSkipRenderSubmit(hash, hash, repaintPending:false, clockActive:false, false)` is true. | a band every tick |
| `gate.damage.compositor-done-quiet` | A finished row left in the desired set: re-`Adopt` + `Tick` produce an empty region. | a band every adoption |
| `gate.damage.compositor-revert` | Dropping a posed instance damages that node's band exactly once, then goes quiet. | (new behaviour) |
| `gate.damage.compositor-ancestors-clean` | With a leaf posed, the leaf's PARENT and the root report no `TransformDirty` from `Flags()` while `RecordDirtyDescendantBits` still marks the trail. | ancestors report TransformDirty |

`gate.compositor-authored-isolation`, `gate.compositor-row-sparse`, `gate.compositor-alloc`,
`gate.compositor-row-alloc`, `gate.compositor-row-overflow` and every `gate.damage.*` / `gate.repaint.*` must stay
green, plus the whole suite (`ALL CHECKS PASSED`) and both build configurations.

### 1e. Expected effect

A marquee tick damages the title's strip (old ∪ new) instead of the window, held ticks disappear entirely
(`Cadence.At(60)` on a 120 Hz panel halves the presents by itself), and a finished animation stops damaging at all.
The GPU saving only materialises once Step 2 lets such a frame take the Partial route — until then the frame is
still `FullDirect` via `BackendUnsupported`, but it is now correctly *described*.

## Step 2 — Blur and EdgeFade layers stop vetoing partial replay

Mapped against the backend. Two facts make this much smaller than §13.1a's blanket veto implies:

- **Every composite already respects the clamp.** `D3D12Device.CurrentScissorRect()` (`:2556-2558`) is
  *innermost clip ∩ root damage*, and its own comment states that every value consumer — layer composites, the
  acrylic clip, the strip fade's bound — goes through it, "so a partial frame can never paint outside its replay
  rect". Nothing in the blur/fade paths writes outside R today.
- **The plain σ=0 edge-fade strip fade is already structurally safe.** Its restore intersects every strip with
  `clip` (`OpacityLayerCompositor.cs:1229-1235`), so writes stay inside R; inside R the snapshot D is this frame's
  freshly replayed backdrop and F = D + subtree, so the `lerp(D, F, feather)` is exact. The feather multiplies
  premultiplied alpha and displaces nothing, so **σ=0 needs no damage inflation at all**.

This matters for the driving app: Wavee's steady state (marquee + `AutoEdgeFade` scroll views, lyrics closed)
contains EdgeFade but no Blur. **Admitting EdgeFade alone unlocks the common case.**

### 2a. The blur defect and its minimal fix

A σ>0 group draws its SOURCE into the group RT under the same clamped scissor, and `Cull` (`D3D12Device.cs:2611`)
drops straddling primitives, so the RT is transparent just outside R. `BlurInPlace`'s taps then pull that
transparency in and the pixels within a tap radius of R's edge composite too light. Fix: while a σ>0
Blur/EdgeFade group is open, inflate **only the `_rootDamage` half** of the scissor and `_cullRect` by
`SelfBlurRegion.TapRadius(σ)`, and drop the inflation before `BlurInPlace`/composite so the composite still uses
the uninflated `CurrentScissorRect()`. Leave `RegionBox` alone — `TryBoundedExactBlur` and the σ>4 downsample
derive sizes from it, and the pin cache's size-exactness contract (`OpacityLayerCompositor.cs:1439`) depends on it.

Nested groups inflate additively. A clamped frame must also not MINT a blur pin (`RetainPinFromScratch`,
`D3D12Device.cs:3802`): `BlurPinKey` is position/content-keyed and unaware of R, so a partial frame's pin would
poison later full frames. Force `pinTag = 0` whenever `_rootDamageActive` and R does not contain `RegionBox`.

### 2b. Record-side damage inflation

The recorder already stores a node's own self-blur halo as its span extent (`SceneRecorder.cs:1431-1443`,
`visualBounds = blurGeometry.OutputBounds`), so `RepaintBand` already covers a self-blur's halo. What is missing is
the ENCLOSING-group case: a dirty node inside someone else's blur group must grow its band by that group's reach
(WebRender maps a dirty primitive's rect through every blur surface between it and the tile cache,
`picture.rs:3064-3100`). Additive over nested groups; none for σ=0 fades.

### 2c. Ordered change list (risk ascending)

1. `D3D12Device.cs` — `_layerHaloPx` pushed at PushLayer for σ>0 Blur/EdgeFade, popped in `PopLayer` before
   `BlurInPlace`; inflates only `_rootDamage` + `_cullRect`. Inert when `_rootDamageActive == false`.
2. `D3D12Device.cs:3548/3802` — never mint a blur pin from a clamped frame.
3. `SceneRecorder.cs` — enclosing-blur-group halo inflation for dirty descendants (2b).
4. `RepaintPolicy.cs:371` — `Scan` admits `LayerKind.Blur` + `LayerKind.EdgeFade`; Acrylic and `PushStencilClip`
   stay vetoed. Update the XML contract **and** §13.1a's "Replay-unsafe streams (v1)" row (canon owner:
   `gpu-renderer.md`; run `check-canon.ps1`).
5. `DamageSuite.cs` — split `gate.repaint.stream-unsafe-layers` (`:350`) into acrylic/stencil-unsafe vs
   blur/edge-fade-safe; extend the `ReplayLayered` CPU reference with a σ-halo group so the "source covers
   R ⊕ TapRadius" arithmetic is gated headlessly.
6. `RepaintIdentityScene.cs` + `RepaintIdentityProbe.cs` — scenarios `blur-group-straddle` and
   `edge-fade-strip-straddle`, modelled on scenario 3 `opacity-group-straddle` (`:83-100`, scene `:191`); the
   mutation must be an involution. Run: `dotnet run --project src/FluentGpu.WindowsApp -- --repaint-identity`.
7. *(Optional, perf)* thread R into `SelfBlurRegion.ComputeWork` / `TryAcquireLocalBlur` so a clamped frame leases
   and blurs only `(R ∩ output) ⊕ halo`, and intersect the fade strips with R before the snapshot copy. Correctness
   holds without both; **risky** — shrinking the source without shrinking `RegionBox` consistently breaks the pin
   size-exactness contract.

**Out of scope.** Acrylic cannot join until `AcrylicCompositor.SnapshotTargetRegion` (`:947`) stops using the
canvas itself as scratch — it clears a canvas region and copies the target into it, destroying the retained scene.
That is a separate project (its own scratch RT + pool + barrier regime). The layered route's 1-rect cap stays:
pooled group RTs mean the stream can only be walked once (`D3D12Device.cs:3338-3346`).

Canon: §13.1a's *Replay-unsafe streams (v1)* row and §13.1 point 4 change with Step 2 — `gpu-renderer.md` is the
owner, and `check-canon.ps1` must pass after editing it.
