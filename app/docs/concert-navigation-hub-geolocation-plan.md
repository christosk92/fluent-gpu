# Concert Navigation, Hub, and Generic Geolocation

Status: Phase 0 implemented; awaiting verification before Phase 1.

Companion UI plan: [Concert UI Component and Engine Plan](concert-ui-component-plan.md)

## Verification protocol

Every phase is a separate review gate:

1. Implement only the phase scope.
2. Run focused tests and the normal app build.
3. Report changed files, results, limitations, and manual verification.
4. Stop for user verification and iteration.
5. Continue only after explicit approval.

## Captured Pathfinder contracts

The supplied SAZ contains 156 sessions and 32 Pathfinder requests. Concert data is served by
`POST https://api-partner.spotify.com/pathfinder/v2/query` using persisted operations.

| Operation | Hash | Variables |
| --- | --- | --- |
| `ArtistConcerts` | `ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b` | `artistUri`, nullable `geoHash`, `includeNearby` |
| `ArtistConcertsPageLocation` | `320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03` | none |
| `userLocation` | `079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2` | none |
| `inferredUserLocation` | `5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba` | none |
| `concertConcepts` | `a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72` | `geohash`, nullable `conceptUri` |
| `concertFeed` | `9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a` | location, date/concept filters, radius, opaque pagination key |
| `concertLocationDetails` | `b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681` | `geonameId`, `isAnonymous` |
| `searchConcertLocations` | `43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3` | `query` |
| `concertLocationsByLatLon` | `8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96` | `lat`, `lon` |
| `saveLocation` | `5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353` | `geonameId` |
| `concert` | `21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e` | `uri`, `authenticated` |

## Important ArtistConcerts response difference

The dedicated artist response does not put the schedule under `artistUnion.goods.concerts`.

- `data.artistUnion` contains artist identity and artwork only.
- `data.nearby.locationName` describes the active nearby location.
- `data.nearby.concerts.items[]` contains the nearby subset.
- `data.concerts.concerts.items[]` contains the complete schedule.
- Both `nearby` and `concerts` can be empty objects rather than absent/null.
- Event summaries include artist items, location, local-offset ISO start time, title, and URI.

Mappers must read each response by operation-specific shape. The older artist-overview mapper remains valid for its own
`goods.concerts.items` response and must not be reused blindly.

## Product routes

- `concerts`: global Concert Hub.
- `artist-concerts:<spotify:artist:...>`: complete artist schedule.
- `concert:<spotify:concert:...>`: rich concert detail and ticket offers.

Concert navigation is never playback. Ticket URLs are external actions and are validated before `InputHooks.OpenUri`.

## Architecture

### Provider-neutral domain

Wavee.Core owns compact and rich concert records, artists, places, offers, concepts, schedules, feed sections, and opaque
pagination keys. Provider-specific typename strings, GeoNames assumptions, and Pathfinder JSON do not enter page code.

### Spotify adapter

The SpotifyLive layer owns operation constants, request variables, parsing, response quirks, deduplication, status
normalization, URI validation, and stale-query cancellation. The rest of the app consumes provider-neutral interfaces.

### Generic geolocation

FluentGpu receives a reusable one-shot geolocation abstraction with:

- a platform-neutral request/result contract;
- distinct permission-denied, unavailable, timeout, canceled, and failed outcomes;
- a Windows implementation using the platform location API;
- a deterministic headless implementation for tests;
- no Spotify or concert dependencies.

Wavee only requests OS location after the user activates **Use my location**. Manual search always remains available.

### Data behavior

- Default discovery radius is the captured 100 km.
- `dateRange` remains null initially.
- Concept selection sends zero or one concept URI.
- Pagination keys are replayed unchanged.
- Location/concept changes cancel visible requests, reset pagination, and ignore late completions.
- Feed append deduplicates by canonical concert URI.
- Empty branches and optional detail fields remove UI sections rather than creating placeholders.

## Implementation phases

### Phase 0 - Sanitized contracts and component proofs

Implemented in the current change:

- provider-neutral domain additions;
- all captured persisted-operation names/hashes;
- sanitized artist, feed, detail, concept, and location fixtures;
- regression tests for the new artist sibling branches, mixed feed wrappers, opaque pagination, offers, and sanitization;
- shared UI proof components and responsive metric tests.

Gate: focused tests, complete test suite, Wavee build, and user review.

### Phase 1 - Generic FluentGpu geolocation

- add the engine-level one-shot geolocation contract;
- add Windows and headless implementations;
- map all result states without exposing platform types;
- test success, denial, unavailable, timeout, cancel, and late completion.

Gate: engine harness reports `ALL CHECKS PASSED`; no Wavee UI or automatic permission prompt yet.

### Phase 2 - Concert services and mappers

- add provider-neutral concert service interfaces;
- implement Pathfinder request builders and response mappers;
- preserve local offsets and optional fields;
- map location search/reverse/detail and feed sections;
- test rich/empty/malformed responses, cancellation, deduplication, and pagination.

Gate: fixture-backed mapper/service tests pass without product routes.

### Phase 3 - Routing and clickability baseline

- add the three internal routes and minimal destination shells;
- make the existing artist tour banner open the schedule;
- make existing concert cards open concert detail;
- add button semantics, keyboard activation, and focus visuals;
- remove all concert playback command paths.

Gate: navigation is manually verified before full pages are added.

### Phase 4 - Artist schedule UI

- implement seeded loading, error, empty, nearby, and full schedule states;
- integrate shared location control without requesting permission automatically;
- apply schedule breakpoint hysteresis and one-scroll ownership.

Gate: accept wide/narrow artist schedule independently.

### Phase 5 - Concert detail UI

- implement event facts, lineup, ticket offers, venue data, and related concerts;
- omit unavailable sections cleanly;
- use a responsive ticket side panel without nested scrolling.

Gate: accept detail states and external-link behavior independently.

### Phase 6 - Concert Hub and location UI

- implement concepts, Nearby, Recommended, playlist promos, All Events, and load-more;
- implement saved/inferred/manual/OS location flows;
- keep pagination loading separate from initial page loading.

Gate: accept hub/filter/location behavior and stale-response handling.

### Phase 7 - Home editorial destination

- add the wide Home destination before dynamic shelves;
- use the supplied portrait artwork without distortion;
- keep one layered responsive tree with no root scale, image zoom, acrylic, or self-blur.

Gate: accept crop, typography, focus, themes, and narrow widths.

### Phase 8 - Integration hardening

- run complete tests, engine harness where applicable, AOT/build checks, visual matrix, keyboard checks, and repeated popup/navigation smoke tests;
- verify KeepAlive, scroll restoration, late completions, and no playback regressions.

Gate: final user acceptance.

## Scope boundaries

Deferred: map rendering, inline ticket purchase, concert saving, nested vertical virtualization, speculative engine
primitives, automatic background location permission, and decoding/interpreting the opaque pagination token.
