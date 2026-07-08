# Real Spotify Playlist Editing With Existing Protos — rescued plan

> Rescued 2026-07-08 from the Claude Code session that hit its usage limit
> (`b687ca6b-2c26-4fc7-93dc-18fcd2d058da`, cwd `C:\wavee\fluent-gpu\app`). The plan below was
> produced in the *prior* session from a full read of the Fiddler capture
> `C:\Users\ChristosKarapasias\Documents\Fiddler2\Captures\playlists.saz` (88 sessions) and a
> comparison against the repo's existing protos/mutation seams. The follow-up session had just
> launched three Explore agents to deepen it into a code-level + UI/UX plan when the limit hit —
> **none of the three finished** (see "Status" at the bottom). The raw transcript excerpt is in
> `00-original-prompt-with-prior-plan.md`.

## Facts established from the capture (evidence base)

- The capture has 88 sessions; the playlist-relevant writes are:
  - `POST {region}-spclient.spotify.com /playlist/v2/playlist/{id}/changes` — raw `ListChanges`
    protobuf request; response is **zstd-compressed** protobuf (`SelectedListContent`) carrying the
    new revision.
  - Cover flow: `POST https://image-upload.spotify.com/v4/playlist` (JPEG body, chunked JSON
    response with `uploadToken`) → `POST` JSON to `spclient.wg` `/playlist/v2/playlist/{id}/register-image`
    → response JSON `picture` (base64) **decodes to the exact 20-byte `ListAttributes.picture`
    value** used in the subsequent `/changes` — no derivation from the upload token needed.
  - `POST /playlist-permission/v1/playlist/{id}/permission/base/level` — 2-byte protobuf body
    `08 02` = `SetPermissionLevelRequest { permission_level = VIEWER }`.
  - Contributor invite: `POST` JSON to permission-grant; response
    `{"token":"…","permissionGrantOptions":{"permission":{"permissionLevel":"CONTRIBUTOR"},"ttlMs":"604800000"},…}`
    (seven-day TTL).
- Metadata edits (name/description/picture set + clear) are all `UPDATE_LIST_ATTRIBUTES` ops with
  `Delta.Info` populated (`User`, `Timestamp`, `Admin=true`, `Undo=true`, `Merge=true`); clearing an
  attribute uses `ListAttributesPartialState.NoValue` (e.g. `LIST_PICTURE`).
- Mutation headers observed: `spotify-apply-lenses: auto` on playlist-v2 mutation POSTs, plus an
  exact `Origin` matching the current spclient base URL.
- Proto coverage is already complete in-repo: `Wavee\SpotifyLive\Protos\playlist4_external.proto`
  (`ListChanges`, `Delta.Info`, `UpdateListAttributes`, `ListAttributesPartialState`,
  `ListAttributeKind`, `SelectedListContent`) and `playlist_permission.proto`
  (`SetPermissionLevelRequest`, `PermissionGrant*`, `PermissionLevel`). **No new .proto files.**
- Existing seams: `MutationEngine` + `OpRebaseStrategy` already POST `ListChanges` to `/changes`;
  `ITransport` already supports `Channel.SpclientWg` (register-image + permission-grant can reuse
  the authenticated pipeline); only `image-upload.spotify.com` is outside the current channel enum.
- The gap is **not** proto coverage: the domain `PlaylistOp.UpdateList` drops the list-attribute
  payload, `BuildChanges` doesn't stamp the captured playlist-edit `ChangeInfo`, and the app still
  routes playlist create/add UX through local `wavee:playlist:*`.

## Summary

- Use the existing generated protos (above). Implement the captured Spotify edit flows:
  name/description update, cover upload/register/set/clear, item add/remove/move, base permission
  level, and contributor invite grants.

## Key Changes

- **Extend playlist domain ops:**
  - Add `PlaylistListAttributePatch` carrying `Name`, `Description`, `PictureBytes`,
    `ClearPicture`, and `Collaborative`.
  - Extend `PlaylistOpKind.UpdateList` to preserve that patch through `MapOps`, `BuildChanges`,
    and `ParseChanges`, so durable outbox reload does not lose metadata edits.
  - Add `PlaylistRowRef(Index, Uri, ItemId)` for duplicate-safe remove/move UI commands.

- **Update `PlaylistWireMapper`:**
  - Emit real `UpdateListAttributes.NewAttributes = ListAttributesPartialState`.
  - Set name/description through `Values`; set cover through `Values.Picture`.
  - Clear cover through `NoValue = LIST_PICTURE`.
  - Add a playlist-edit `BuildChanges` overload that accepts username and nowMs, stamping
    `Delta.Info.User`, `Timestamp`, `Admin=true`, `Undo=true`, and `Merge=true`, matching the
    capture.
  - Keep rootlist follow/unfollow behavior separate.

- **Update mutation flow:**
  - Reuse `MutationEngine.Edit` / `OpRebaseStrategy` for item add/remove/move and metadata edits.
  - For adds, include `AddedBy = ctx.Account` and `AddedAt = nowMs`.
  - For removes, prefer index/range removes with expected row items and item ids where available.
  - Optimistically update membership for item edits and the playlist header for
    name/description/cover/collab edits.
  - Persist the full op body in SQLite and verify reload round-trips list-attribute patches.

- **Add playlist edit service/API:**
  - Add `IPlaylistMutationSource` with `AddTracksAsync`, `RemoveRowsAsync`, `MoveRowsAsync`,
    `UpdateDetailsAsync`, `SetCoverJpegAsync`, `ClearCoverAsync`, `SetBasePermissionAsync`, and
    `CreateContributorInviteAsync`.
  - Implement it in the real backend using the mutation engine and authenticated transport.
  - Keep `UserPlaylistSource` as the local `wavee:playlist:*` implementation for fake/offline
    playlists.

- **HTTP parity from capture:**
  - Add `spotify-apply-lenses: auto` to playlist-v2 mutation headers.
  - Stamp exact `Origin` from the current spclient base URL inside `LiveDealerTransport` for
    playlist mutation POSTs.
  - Add an image-upload route/channel for `https://image-upload.spotify.com/v4/playlist`.
  - Cover flow: POST JPEG to image-upload, parse `uploadToken`; POST JSON to `Channel.SpclientWg`
    `/playlist/v2/playlist/{id}/register-image`, parse JSON `picture` base64; use the decoded
    20-byte value in `UPDATE_LIST_ATTRIBUTES`.
  - Permission base-level flow: serialize `SetPermissionLevelRequest`; the captured `08 02` is
    `PermissionLevel.VIEWER`.
  - Contributor invite flow: POST JSON `{ permissionLevel: CONTRIBUTOR, ttlMs: 604800000 }` to
    permission-grant; treat as online-only.

- **App/UI wiring:**
  - Extend `LibraryBridge` with playlist edit methods backed by `IPlaylistMutationSource`.
  - Gate edit controls from `PlaylistCapabilities`.
  - Add playlist detail edit actions for title/description, cover set/clear, contributor invite,
    remove selected rows, and move selected rows up/down.

## Tests

- Fixture tests from the capture:
  - Metadata `/changes`: name + description emits two `UPDATE_LIST_ATTRIBUTES` ops with captured
    `Delta.Info`.
  - Cover set: register-image JSON `picture` base64 decodes to the exact `ListAttributes.Picture`
    bytes used in `/changes`.
  - Cover clear: emits `NoValue = LIST_PICTURE`.
  - Permission base-level serializes VIEWER to `08 02`.
  - Contributor grant JSON matches CONTRIBUTOR + seven-day TTL.
- Outbox persistence tests proving `UpdateList` patches survive Save/Load.
- Mutation replay tests for headers, explicit POST, zstd response revision capture, optimistic
  header update, and conflict retry.
- Bridge/UI-level tests for capability gating and editable-playlist picker behavior.

## Assumptions

- No new `.proto` files are needed.
- Real Spotify playlist creation and folders stay out of this pass — the capture includes no
  create-list or folder writes.
- Cover upload v1 accepts JPEG bytes only; image conversion/cropping can come after the wire path
  is stable.
- Permission/invite writes are online-only. Playlist item and metadata edits remain
  durable/offline-queueable.

## Status — what was lost at the session limit

The user's follow-up ("make a detailed technical plan, also for UI/UX, as if you are gonna write
the code") triggered three parallel Explore agents that were all cut off mid-exploration with **no
final reports**; the detailed code-level/UI-UX elaboration was never produced. Their last progress
notes:

- *Map playlist mutation backend* — had traced the mutation engine and was about to trace
  `SetMembership` → cold-store persistence and the Edit/add-to-playlist call sites, plus two
  remaining tests.
- *Map transport/HTTP seams* — was about to locate the image fetcher infra, `HttpClientExchange`,
  and how playlist mutations POST to `/changes`.
- *Map playlist UI surfaces* — was about to read the SelectionBar and menu-button sections of
  `DetailTracks`, plus `DetailConfig` and the `PlaylistCapabilities` definition.

Related sibling plan (separate work, already on disk): the realtime playlist *sync* fix plan at
`~\.claude-personal\plans\reactive-dreaming-heron.md` (MOV pre-removal semantics, frozen ItemCount,
sidebar staleness, UPDATE_LIST_ATTRIBUTES refetch) — same playlist wire layer, complementary to
this editing plan.
