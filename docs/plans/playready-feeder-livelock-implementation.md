# PlayReady feeder live-lock after an early quality switch — investigation + fix

Status: **implemented 2026-09-22** (native: `FeedPlan.h`, `PrSession.cpp`, `PrInternal.h`, `CencMediaSource.h`,
`SegmentStore.h`, `tests/FeedTests.cpp`, `build.cmd`). Live verification on the incident track is still owed — see
Verification. Scope: `ops/tools/playready-native/` only; the C ABI (`FgPlayReady.h`) is unchanged.

## Context
DRM video (arm64 AOT, 2026-09-21 23:47, track `6ArJvRJVOneYQO0JtMP259`) froze/jumped a few seconds in while the user was
resizing it. The resize is a coincidence. This was the **first DRM session on the rewritten native runtime**
(`b5fb8cd20`/`ba24aac6d`; DLL built 09-20 21:11, after all nine healthy 09-20 sessions). Investigated: the production
log, all earlier sessions' logs, and a line-by-line audit of `ops/tools/playready-native/` + the managed ABR. Code lives in
the engine repo `C:\WAVEE\fluent-gpu` (not the fenced PlayPlay code). The earlier write-up
`docs/plans/playready-feeder-livelock-implementation.md` is superseded by this and gets rewritten in step 0.

## What happened (log-proven, session-relative ms)
| t | evidence | meaning |
|---|---|---|
| 333, 639 | `video rep=-1 seg=0`, `seg=1` | warm prefetch = exactly 2 segments in the descriptor's rung (≤480p pick, `Playback.Video.Source.cs:547-554`). Guard: `lastIdx=1, lastCovEnd=8007` |
| 1029 | `quality switch at segment index 0 (playhead t=0ms … cursor was 2)`; `spliced at sample 15 … buffer=111` | first ABR tick (1 s cadence, `ProtectedMediaSession.cs:559`) → `SpliceLocked(truncateAfter)` erases old-rung seg 1. Coverage shrinks to `[0,4003)`. Boundary was computed from a playhead sampled BEFORE the 200 ms segment GET → seg 0 re-inserted at `m_next=15`: 15 frames re-delivered, vector non-monotonic |
| 1375 | `video rep=2 seg=2 aheadMs=11219` | planner skipped seg 1 → permanent hole `[4003,8007)` |
| 1499–3142 | `seg=2` again with every audio seg 3…14 | live-lock, paced by the paired audio GET |
| 3300–5900 | 1 530 × `seg=2 transferMs=0 aheadMs=3962` (≤693/s) | audio satisfied → cache-hit spin; `m_next` crossed the hole (8133→3962): decoder handed t=8008 right after t=4003 with no discontinuity flag = the visible jump/freeze; every pass re-splices 690 KB under `m_mx` (the lock `RequestSample` needs), fires `FgPrEvent_Buffered` and `bytesDownloaded += 690 KB @ ~0 ms` |
| 6373 | `abr sample=40885024B/243ms=1346009kbps → 1280x720` | the spin's cached bytes poisoned the throughput estimate → forced up-switch (positive feedback) |
| 7445 | `quality switch at segment index 3 … cursor was 6` | truncating splice rebuilt the tail → loop ends |

Earlier sessions (9, 09-20): switches at index 2–3 with cursor 16–28 → no lock. **Trigger = a truncating switch whose
boundary is exactly one segment behind the last fetched index** (`cursor = boundary + 2`) — i.e. every warm start whose
first ABR pick differs from the descriptor's rung. Not rare on this build.

## Root causes
- **D1 guard survives coverage shrink.** `PlanTrack` (`PrSession.cpp:203-210`): `lastIdx==idx && cov.endMs <= lastCovEnd` reads
  a SHRINK as "no growth" → `floorIdx=2`; afterwards `:209` compares the PRE-floor idx so the floor never moves again → same
  segment forever. The guard is a feeder-stack local (`:788`), reset ONLY on `seekSeq` change (`:584-588`); none of the
  shrink paths (`SpliceLocked` truncate `CencMediaSource.h:1046`, `TakeSamples :1100`, byte-budget trim
  `SegmentStore.h:318-324`) can reach it. The byte-cap block (`:599-603`) also cancels a plan AFTER `PlanTrack` advanced the floor.
- **D2 no spin brake.** `RunOneJob` returns `Progress` at `:781` for ANY completed job (even all-cancelled / no samples);
  `FeederMain :802-808` re-enters immediately, no cap.
- **D3 splice re-delivery / broken invariant.** Straddle branch (`:1039-1045`) inserts the WHOLE run at `m_next`; with
  `truncateAfter` it also leaves `m_samples` non-monotonic, which `ComputeBufferedPairs`, `CanSeekToIn`, `TrimBehindByTime`
  all assume. Switch boundary (`PrSession.cpp:477-484`) is computed before the GET and never re-validated.
- **D4 hole-blind consumers.** `AheadDurationMsLocked` (`:1198`) = `last.end − next.start`; it drives the demand hook (`:930`).
  `RequestSample` (`:857-933`) delivers across a time gap silently.
- **D5 cache hits counted as throughput** (`:711-712`) → ABR inflation.
- **D6 same-shape stale state** (not in this incident, same fix): `st.failIdx/failCount`, `st.lastAhead/lastBehind`,
  `s.videoEndIndex/audioEndIndex` (a new rung is a new URL space) survive switch/re-attach; `RunRepresentationSwitch` never
  calls `RaiseBuffered`.

## Fix (all in `ops/tools/playready-native/` unless noted)
0. Rewrite `docs/plans/playready-feeder-livelock-implementation.md` from this file (repo rule: plan with real code).
1. **`FeedPlan.h` (new, pure, no MF/COM)** — `plan::Guard{lastIdx,lastCovEnd,floorIdx}`, `plan::Next(guard, cov, refMs,
   wantEndMs, segLen, endIndex)`: shrink (`cov.endMs < lastCovEnd`) ⇒ reset; apply floor FIRST then compare; `==` not `<=`;
   uncovered branch honours a generation (below) instead of `lastCovEnd < 0`; returns `guardStepped`. `PlanTrack` becomes
   the adapter and logs one `[cenc-feed] progress guard stepped …` line with ref/cov/lastCovEnd when it fires.
2. **Buffer generation, the structural reset.** `CencMediaStream` gets `std::atomic<uint32_t> m_cutGen`, bumped wherever
   coverage is cut: truncating splice, `TakeSamples`, a trim that drops past the reference. `FeederState` remembers
   `(seekSeq, stream ptr, cutGen)` per track; any change ⇒ reset guard + `failIdx/failCount` + `lastAhead/lastBehind`
   (replaces the seek-only reset at `:584-588`). A completed switch also clears `videoEndIndex` and calls
   `RaiseBuffered(force)`. The byte-cap block runs BEFORE `PlanTrack` commits the floor (plan on a copy, commit on fetch).
3. **Spin brake.** `RunOneJob` returns `Progress` only when coverage at `refMs` grew or a non-fetch job completed;
   landed-without-growth / nothing-landed ⇒ `Backoff(250 ms)` with the existing demand-floor shape (`:770-779`).
4. **Splice.** Non-truncating straddle: drop incoming samples earlier than `m_samples[m_next].time` before inserting.
   Truncating (switch): under `m_mx`, if `replacement.front().time < m_samples[m_next].time` the playhead passed the
   boundary during the GET ⇒ return "stale boundary"; `RunRepresentationSwitch` keeps the request pending and retries at
   `boundary+1` (never rewinds, vector stays ascending). Add a debug-only ascending assert after every splice.
5. **Holes are explicit.** `DeliverSampleLocked`: if `m_samples[m_next].time − prev.end > kContiguityToleranceMs` set
   `MFSampleExtension_Discontinuity` and log `[cenc-src] delivered across a hole [a,b)`. Demand hook uses a contiguous-ahead
   figure (walk from `m_next` until a gap > tolerance) instead of `AheadDurationMsLocked`; the ABR ledger prints both.
6. **Throughput hygiene.** Cache hits (`timing.fromStore` / `transferMs==0 && headerMs==0`) do not add to
   `bytesDownloaded/downloadElapsedMs`. Managed side unchanged.
7. **Tests** — new `ops/tools/playready-native/tests/FeedTests.cpp`, dependency-free console exe built + run by `build.cmd`
   (exit code = failures): the incident replay (prefetch 0,1 → truncate to `[0,4003)` → plans 1,2,3); genuine short
   segment steps the floor once; floor+stagnation advances; byte-cap cancel does not advance the floor; splice fixtures
   (straddle keeps `m_next`'s time, ascending; stale-boundary switch is refused); `ComputeBufferedPairs` /
   `TrimBehindByTime` / contiguous-ahead on holed vectors. Managed: `AdaptiveMediaTests` case — a cache-hit burst does not
   move the estimate (through the `ProtectedVideoSession` fake's `BytesDownloaded`).

Out of scope (recorded in the doc): fetching the prefetch in the ABR's rung (structurally impossible today — no ABR object
during prepare, `ProtectedMediaBackend.cs:107/134-140`); `prefetchAroundMs` never cleared; `st.ticks` without `tfdt`;
stale gap-register entry G-152.

## As built — deviations from the plan
- **The vector's ordering key is `decodeTicks`, coverage stays in `timeTicks`.** Samples are stored in decode order and a
  B-frame's presentation time dips inside a GOP, so "ascending" is defined on decode time (`IsAscending`, `SpliceSamples`),
  while everything cursor-relative ("already delivered", the stale-boundary test) and every coverage figure
  (`ComputeBufferedPairs`, `ContiguousAheadMs`, the hole detector's running reach) stays on presentation time. The
  decision and its evidence are in the comment block above `SpliceOutcome` in `SegmentStore.h`.
- **Cache hits are identified from the HTTP response, not inferred.** `HttpFetchTiming::fromStore` is set in
  `HttpFetch::OnHeaders` from `HttpResponseMessage::Source() == Cache` — the zero-latency refetches in the incident were
  the WinRT HttpClient's own cache. No second body cache was added.
- **The generation counter is not bumped by the normal time-window trim** — only by a truncating splice, `TakeSamples`,
  a short replacement, and the byte-budget rule of `TrimBehindByTime` (new `outByteBudgetCut` out-param). A per-segment
  bump would reset the feeder's guard every job.
- **A stale switch boundary retries one segment later.** `SwitchVideoRepresentation` returns
  `SwitchResult { Spliced, StaleBoundary, Rejected }`; on `StaleBoundary` nothing is mutated, the request stays pending and
  `FeederState::switchMinBoundary` lower-bounds the next attempt (a `Backoff(250)` wait when that is past the buffered
  cursor). Session fields (`initUrl`, `segBase…`, `videoInfo`, `activeRepresentation`) are committed only on `Spliced`.
- **Floor commits survive the byte cap.** `plan::Next` plans into a candidate `next` guard; `RunOneJob` copies it into
  `FeederState` only for a plan the byte-cap block did not cancel.

## Verification
1. `build.cmd arm64` + `x64`; `FeedTests.exe` exits 0.
2. `dotnet build src/FluentGpu.slnx` Debug + Release; `FluentGpu.Windows.Tests` + `FluentGpu.Engine.Tests` green.
3. Wavee arm64 AOT (Wavee closed); warm-start the same track. Log must show: `video rep=… seg=` strictly ascending after
   the first `quality switch`; no repeated index; no `delivered across a hole`; no `abr sample=` above real line rate; no
   `progress guard stepped`. Then: seek into an unbuffered range, force a second switch (resize/quality menu), detach/re-attach
   (dock ↔ floating) — same checks.
4. Log scan script from this investigation (repeated-segment counter per session) reports `livelocked=0`.
