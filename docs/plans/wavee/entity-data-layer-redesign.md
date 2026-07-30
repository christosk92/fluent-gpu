# Wavee entity data layer — root fixes at the chokepoints, nothing new to operate

Status: SHIPPING (supersedes `entity-completeness-healing-design.md`). Produced by a 3-design competition
(demand-driven read-through vs typed-references vs minimal-delta), synthesized by a judge scored on NET COMPLEXITY
REMOVED, then adversarially verified — verdict SHIP-WITH-AMENDMENTS; all amendments are folded in below and marked
[AMENDED]. Every claim was verified against the tree; file:line citations live in the competition record.

## Philosophy

The store is not broken and does not need a new shape. `StoreEntityMerge` is a carefully reasoned accretion
discipline (NonEmpty guards, null-means-unknown adornments, hydration levels, authoritative discriminators) that
never got finished: three of six kinds never received it, one line seals freshness on batch membership instead of
outcome, healing was built per-surface instead of inside the one method every surface already funnels through
(`SyncAllAsync`), and linking data the app already pays to download is discarded by the lean parser. Fix those
roots and **ordinary hydration becomes the healer**: no queue, no router, no thinness ledger, no completeness
registry, no per-row read-through, no parallel write plane. Thin writes stay legal (the wire is genuinely thin);
the system's answer to "a blank was written" is the same as its answer to everything else — the next batch through
the chokepoint closes it, the store's change signal re-runs the page's existing re-map, and `Skel.Region`/
`Loadable` render the gap meanwhile.

## The model — five rules, all in existing files

1. **Freshness records outcome, not membership.** A batched uri is sealed fresh only if projection reports it
   landed; a uri the response omitted stays a miss. An ETag is never persisted onto a `Missing` row; a 304 can
   never re-confirm one past its TTL. [AMENDED C1/P1: this rule applies to BOTH seal points —
   `MetadataService.SyncAllAsync` AND `ExtensionEtagCache.GetAsync`, whose absent-from-response fold seals a
   24h Missing today (worse than the 1h being fixed) and is what wedges the shipped video recovery. Seed from
   projection-reported landed uris, NOT store-version deltas — hearts/membership/association bumps share the
   version counter and would race.]
2. **Every entity kind gets the protective merge.** `StoreEntityMerge.{Playlist,Show,Episode}` join
   Track/Album/Artist; the strictest rules found anywhere (the cluster merge's `Title == uri` echo test, its
   quality-aware image pick) are lifted INTO `StoreEntityMerge.Track`; the duplicate merge implementations die.
   [AMENDED C2: the Playlist merge is NOT blanket-NonEmpty — it is an explicit per-field policy. The header
   writer is AUTHORITATIVE for Name/Description/Cover/Capabilities (absence = removal, so `ClearDescription`/
   `ClearPicture` and the dead-letter rollback keep working); IsPublic+BasePermissionRevision use the
   discriminator (adopt only when both present — the permission writer always writes both); Tuning/Owner/Tracks
   decided per-field with the clobber cases as repro asserts (ClearDescription, ClearPicture, rollback,
   Episode.ShowName, IsPublic).]
   [AMENDED P6: Episode merge needs an explicit `ProgressMs` rule — inventory the progress writer first; a
   `>0 ? incoming : current` guard makes a legitimate reset unrepresentable.]
   [AMENDED P3: the `ChooseBetter` image fold gets a same-source carve-out so a genuine cover change stays
   adoptable.]
   Companion: `CachedStore` persists the MERGED value for the three new kinds (re-read hot after upsert, as
   Track/Album/Artist already do) or disk diverges from memory.
3. **The hydrate chokepoint closes references one level.** After `SyncAllAsync` lands (or skips-as-fresh) a set of
   track uris, it scans those resident rows for refs with known identity and blank display
   (`Album.Uri != "" && Album.Name == ""`) and fire-and-forgets those album uris back through itself.
   Depth-bounded by construction (the second wave carries Album kinds only, never re-scanned for track refs);
   capped and session-deduped with exactly the bounds the deleted backfill shipped (300/batch, ≤900/pass,
   once-per-session set, every-512-rows lock yield). AlbumV4's projection rewrites every resident row of that
   album, so one fetch heals the playlist row, the now-playing panel, and the liked list at once.
   [AMENDED C3: there is NO ArtistRef arm — `ProjectArtist` writes only the Artist entity and nothing
   back-propagates names into resident tracks' denormalized artist lists, so an artist fetch cannot heal the row.
   Instead, a track whose OWN display is thin (per `StoreEntityGaps`) is re-entered as a TRACK uri — a TrackV4
   refetch carries named artists.]
4. **Every surface that shows entity data touches the chokepoint on open.** Playlists/albums/artists/contexts/
   now-playing already do. The two that don't get the same touch: Liked Songs (a fire-and-forget hydrate over its
   members on open — the existing `DetectVideos` hook pattern) and shows (below).
   [AMENDED P2: the liked-open touch MUST page at 300 (reuse CollectionFetcher's paging) — one
   `SyncAllConditionalAsync` over 10k members re-serializes all cached payloads into a single tens-of-MB array.]
5. **Track linking is an adornment, not a table.** `Track.CanonicalUri` (nullable; null = unknown-or-self) rides
   the existing null-coalesce merge like `Isrc`, persists free through `EntityJson`, and is populated from data
   the app already receives: `canonical_uri` is field 36 on the full TrackV4 message and the recovery pass proves
   the server sends it — the lean parser just drops it. One proto line + ~5 lines in `ProjectTrack` stamp it on
   every bulk hydrate; the shipped video recovery promotes what it derives onto the resident row instead of
   discarding it. Uri-keyed side planes (video associations, saved sets) bridge through it ONLY on a read miss,
   inside the store, under the already-held lock — zero cost on hits, no API change. Writes never canonicalize
   (playback licensing and playlist ops untouched).
   [AMENDED C6: the HEARTS bridge is read-display only until unsave routes to `row.CanonicalUri` — otherwise the
   bridge lights a heart the aliased surface cannot turn off (local unsave no-ops, server remove targets a uri
   not in the collection). Write-side canonicalization of unsave is a PRECONDITION of the hearts flag.]
   [AMENDED C7, honesty: the video miss-bridge does NOT fix the live playlist bug — the alias plane usually holds
   a fresh `None` ROW, not a miss. The live bug's fix is recovery + rule 1's un-wedging. The bridge helps
   never-detected aliases only.]

## The five audit gaps → why each becomes impossible-or-trivial

- **Blind-replace kinds:** merges (rule 2) make the ShowName clobber and the playlist header resets
  unrepresentable.
- **"0 episodes" forever:** ShowV4 already carries `repeated Episode episode = 70` and the lean parser drops it.
  Parse gid-only refs → `SetMembership(showUri, …)` (generic, persisted) → `GetShowAsync` joins membership to
  resident Episode entities (UI unchanged) → show-open hydrates the first episode page through the chokepoint.
  The playlist pattern verbatim. [AMENDED P4: precede with a kind-blind-consumer audit of the playlists/pin
  tables (GcSweepMemberships, NoteAdopted accounting), not just a field-70 size measurement.]
- **Thin writers / unhealed combos:** rules 3+4 make coverage structural — the closure scans ALL requested uris'
  resident rows, fresh-skips included, so rows cached thin by an earlier session heal on the next touch of any
  surface that shows them. [AMENDED C4: ONE combo is structurally out of reach and is named a wart, not hidden:
  artist appears-on stubs past the 20-item hydrate cap. Fix later with a windowed hydrate-on-shelf-scroll if it
  ever matters.]
- **Freshness lies:** rule 1 (both seal points + the ETag-on-Missing rule). This is the keystone — it also
  un-wedges the shipped video recovery.
- **Nine predicates / four merges:** the whether-to-refetch predicates die (call `SyncAllAsync`; freshness
  dedups); the which-rows pickers collapse onto one ~15-line `StoreEntityGaps` static (one notion of empty);
  render-local em-dash guards stay (presentation); `IsAlbumComplete` stays, documented as envelope semantics.
  [AMENDED C5: the now-playing site KEEPS one `StoreEntityGaps` gate — deleting both early-outs would ungate the
  Pathfinder getTrack fallback into one extra round-trip per track change.]
  `MergeClusterTrack` + its helpers are deleted AFTER their stricter rules are folded into the store merge; the
  ArtistOverview/Store stub-fold duplicate becomes one shared function ([AMENDED P5] via a small shape adapter —
  the two record types differ).

## What DIES

`LikedAlbumNameBackfill` (whole class + 3 wiring sites, ~145 lines) · `MergeClusterTrack`/`TitleMissing`/private
`MergeAlbumRef` (~25) · the stub-fold duplicate (~10) · the fetch-gating predicates (~50, one gate kept per C5) ·
the "0 episodes", header-clobber, ShowName-clobber, and 304-forever bug classes · **the entire incumbent design
unbuilt** (heal queue, thinness detector, field router, per-kind completeness registry, alias table; ~800-1,200
planned lines) · **designs A/B's machinery unbuilt** (per-read resolver overlay; RefHint plane + writer migration).

## What's NEW (complete)

Outcome seeding at both seal points (~17 lines) · three merges + Track strictness + CachedStore merged-persist
(~85) · chokepoint closure (~45, net −100 vs the backfill it deletes) · `StoreEntityGaps` (~15) · liked-open paged
touch (~10) · shows via membership (proto line + ~60) · `CanonicalUri` adornment + recovery promotion + store
miss-bridges (~45). Net ≈ +275 / −230 with zero new services, zero queues, no schema migration (membership,
video_assoc, EntityJson absorb everything; CanonicalUri is omit-null).

## Migration — independently shippable stages

- **S0 (hours, ship first, valuable alone):** rule 1 at BOTH seal points, projection-reported seeding, the
  ETag-on-Missing rule. Every existing opportunistic heal — including the shipped video recovery — starts working.
- **S1:** the three merges (per-field Playlist policy + ProgressMs rule + ChooseBetter carve-out) + delete
  `MergeClusterTrack` + stub-fold dedup + the five repro asserts + CachedStore merged-persist.
- **S2:** chokepoint closure (track-uri re-entry, no artist arm) + DELETE `LikedAlbumNameBackfill` + collapse the
  predicates onto `StoreEntityGaps` (keep the now-playing gate) + liked-open paged touch + relevance-arm
  broadening.
- **S3:** shows (preceded by the pin-table audit).
- **S4:** linked tracks — proto field 36 + `ProjectTrack` stamp + recovery promotion + video miss-bridge; hearts
  bridge ONLY behind write-side unsave canonicalization.

Each stage is green alone; S0-S2 touch no models and are safe against in-flight work; S3/S4 add omit-null fields
and proto lines only.

## Named warts (kept visible, not designed around)

1. An alias for which neither TrackV4 nor kind-212 yields a canonical keeps a dark video cell while search's row
   works — no design of any size fixes that.
2. Appears-on stubs past the 20-item cap stay thin until a windowed hydrate exists.
3. Permanently-omitted entities re-request once per hydrate touch until the session set trips — bounded,
   observable, ETag-cheapened.

## Verification

Unit: merge repro asserts (five clobber cases incl. rollback), `StoreEntityGaps`, outcome-seeding (an omitting
batch leaves the uri unsealed; a Missing row cannot 304 past TTL). Integration (user runs): the WP-ι
`video.assoc.*` capture recipe before/after S0 (the recovery counters must move); a playlist row with a thin album
heals without opening the album; a podcast page shows real episodes after S3; playlist privacy/tuning survives a
header refetch; `ClearDescription`/`ClearPicture` still clear.
