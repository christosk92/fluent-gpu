using System.Linq;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.SpotifyLive;

namespace Wavee;

/// <summary>
/// The hand-wired composition root — plain <c>new</c>, NO reflection container (AOT-visible, zero startup tax). Holds the
/// Core service instances + the <see cref="PlaybackBridge"/>. Swap <see cref="CreateFake"/> for a real wiring later;
/// nothing else changes because the UI only ever sees the interfaces + the bridge.
/// </summary>
public sealed class Services
{
    /// <summary>Context slot — provide at the root, read with <c>UseContext(Services.Slot)</c>.</summary>
    public static readonly Context<Services?> Slot = new(null);

    /// <summary>When set (via the <c>--real-backend</c> flag), the app wires the persistent Store-backed catalog instead
    /// of the FakeData demo. Off by default until live sync (login → fetchers → dealer) is verified end to end.</summary>
    public static bool UseRealBackend;

    /// <summary>The persistent backend store (REAL backend only; null for the fake). Exposed so the live-session bootstrap
    /// can hydrate playlist headers into the SAME store the catalog reads (InMemoryStore is lock-guarded → safe).</summary>
    public Wavee.Backend.IStore? RealStore { get; private set; }

    /// <summary>The store-backed catalog source (REAL backend only) — exposed so the live bootstrap can wire on-open
    /// track hydration via <c>OnDemandFetch</c> (playlists/albums open empty otherwise).</summary>
    public Wavee.Backend.Library.StoreLibrarySource? RealLibrarySource { get; private set; }

    /// <summary>The user's LOCAL VIDEO OVERRIDE curation (REAL backend only — it is store-backed; null for the fake, where
    /// every override path is unreachable). The one instance shared by the resolver's tier 1, the playback bridge's
    /// has-video answer, and (in P3) the menu/settings surfaces: <c>Attach</c> / <c>Remove</c> / <c>All</c> / <c>Decide</c>.</summary>
    public VideoOverrideService? VideoOverrides { get; private set; }

    /// <summary>The switchable mutation transport (REAL backend only): stub until go-live, then the live dealer transport,
    /// back to stub on logout — so writes made while logged out queue in the durable outbox and replay on next login (§2.1).</summary>
    public Wavee.Backend.SwitchableTransport? MutTransport { get; private set; }
    /// <summary>The SQLite cold tier (REAL backend only) — exposed so the go-live block can wire the collection revision
    /// get/set + rootlist revision behind the sync loop.</summary>
    public Wavee.Backend.Persistence.SqliteColdStore? RealCold { get; private set; }
    /// <summary>The metadata-cache garbage collector (REAL backend only; null for the fake). Armed from the app-mount
    /// effect with the UI-thread <c>post</c> marshaller — it must snapshot <see cref="BuildPinSet"/> on the UI thread.
    /// Also owns the one-time post-migration VACUUM and the user-settable cache budget.</summary>
    public Wavee.Backend.Persistence.EntityCacheGc? CacheGc { get; private set; }
    /// <summary>The durable mutation engine (REAL backend only) — exposed so the sync loop drains it + the collection
    /// fetcher's mark-and-sweep can consult its pending-op shield.</summary>
    public Wavee.Backend.MutationEngine? RealMutations { get; private set; }
    /// <summary>The ambient session host (REAL backend only) — the real username is set into it on go-live so write bodies
    /// carry a valid account.</summary>
    public Wavee.Backend.SessionContextHost? RealSessionHost { get; private set; }
    /// <summary>The collection self-write echo registry (REAL backend only, §7.1) — the write strategy records accepted-write
    /// cuids here; the sync loop checks it to drop our own PubSubUpdate echoes before any store work.</summary>
    public Wavee.Backend.Collections.CollectionEchoRing? EchoRing { get; private set; }
    /// <summary>The single library-sync writer loop (REAL backend only, after go-live) — the on-open SWR + DetailPage
    /// live-refresh hooks reach it here. Null offline / fake backend.</summary>
    public Wavee.Backend.Sync.LibrarySync? RealSync { get; internal set; }
    /// <summary>Reactive live capability for server-driven automatic-playlist tuning.</summary>
    public Signal<IPlaylistTuningSource?> PlaylistTuning { get; } = new(null);
    /// <summary>The selected home facet chip id (Spotify <c>home.homeChips[].id</c>, e.g. "music-chip" or
    /// "podcasts-following-chip"); null/empty = the unfiltered feed. Written by the home chip row, read by the live
    /// home fetch when it builds the <c>facet</c> request variable. Opaque server token — never synthesised.</summary>
    public Signal<string?> HomeFacet { get; } = new(null);
    /// <summary>The engine-backed Mutations seam adapter (REAL backend only) — exposed so go-live can route its post-write
    /// drains through the sync loop (§6, <c>ScheduleDrain</c>) and GoOffline can reset them to inline.</summary>
    public Wavee.Backend.EngineMutationSource? RealMutationSource { get; private set; }
    /// <summary>Spotify playlist item/metadata/cover edits (REAL backend only).</summary>
    public Wavee.Backend.Playlists.PlaylistMutationSource? RealPlaylistMutations { get; private set; }
    /// <summary>Spotify "recommended songs" playlist extender (REAL backend only). Bound to the SAME switchable mutation
    /// transport, so it follows the go-live/logout lifecycle. Null when fake / logged out → the UI gates on it.</summary>
    public Wavee.Backend.Playlists.PlaylistExtenderClient? RealExtender { get; private set; }
    public Wavee.Backend.SpclientBaseUrlHolder? RealSpclientBaseUrl { get; private set; }

    /// <summary>Authenticated spclient HTTP pipeline (REAL backend, after go-live). Used by the API Console page for
    /// arbitrary requests with the same auth/client-token middleware as production fetchers.</summary>
    public Wavee.Backend.Spotify.IHttpExchange? LiveHttp { get; internal set; }

    /// <summary>The live Connect session host (REAL backend, after a successful login) — captured for logout teardown.
    /// Set via <see cref="AttachLive"/> BEFORE <see cref="GoLive"/> so a logout in the go-live window still tears down the
    /// live transport + dealer cleanly (not a no-op).</summary>
    public Wavee.SpotifyLive.LiveSessionHost? LiveHost { get; private set; }
    /// <summary>PlayPlay runtime provisioner (live session only) — drives the setup modal and banner.</summary>
    public Wavee.SpotifyLive.Audio.IPlayPlayProvisioner? PlayPlayProvisioner { get; internal set; }
    public Wavee.Backend.Audio.AudioBodyDiskCache? AudioBodyCache { get; internal set; }
    public Wavee.SpotifyLive.Audio.LicenseKeyDiskCache? AudioLicenseCache { get; internal set; }
    /// <summary>The persisted-credential store backing the live session — cleared on logout so the next launch can't
    /// silently re-login.</summary>
    public Wavee.Backend.Persistence.ICredentialStore? CredStore { get; private set; }

    public IWaveeLog Log { get; }
    public ISpotifySession Session { get; }
    public IMusicLibrary Library { get; }
    public IPlaybackPlayer Player { get; }
    public IConnectDevices Devices { get; }
    /// <summary>Realtime (dealer socket) connection status — so the UI can surface "Reconnecting…" on a network drop
    /// instead of silently going stale. Driven by the live transport's socket lifecycle; offline until go-live.</summary>
    public IConnectivity Connectivity { get; }
    public ILyricsProvider Lyrics { get; }
    /// <summary>Progressive, below-the-fold album data. Stable wrapper; the live Spotify implementation is installed
    /// after login while mounted pages keep the same service identity.</summary>
    public SwitchableAlbumEnrichmentService AlbumEnrichment { get; }
    /// <summary>Standalone-artist-page header stats (monthly listeners / followers / world rank / top-track play counts)
    /// via the lazy <c>queryArtistOverview</c>. Stable wrapper; the live provider is installed after login, offline/fake
    /// it is the permanently-offline <see cref="NullArtistStatsService"/>. The Library artist surface never reads it.</summary>
    public SwitchableArtistStatsService ArtistStats { get; }
    /// <summary>The artist chart's step two: the SpClient <c>artist-top-tracks-extensions</c> list (up to 50) enriched over
    /// the shared extended-metadata transport and merged onto the overview seed. Same page scope as <see cref="ArtistStats"/>
    /// — only the standalone <c>ArtistPage</c> chart drives it. Offline/fake it is <see cref="NullArtistPopularTracksService"/>,
    /// which hands the seed straight back.</summary>
    public SwitchableArtistPopularTracksService ArtistPopularTracks { get; }
    /// <summary>The signed-in user's own top artists and tracks (<c>userTopContent</c>, 4-week affinity) — Home's
    /// top-artist row and its personal track badges.
    /// Stable wrapper; the live provider is installed after login, offline/fake it is <see cref="NullUserTopService"/>,
    /// which returns an empty list so the row simply does not render.</summary>
    public SwitchableUserTopService UserTop { get; }
    /// <summary>Playlist save counts (the spclient <c>popcount</c> endpoint) for the playlist header's meta line.
    /// Stable wrapper; the live provider is installed on go-live. Offline it is <see cref="NullPlaylistPopcountService"/>,
    /// which returns null so the header simply omits the segment.</summary>
    public SwitchablePlaylistPopcountService PlaylistPopcount { get; }
    /// <summary>Upcoming-release resolution (extended-metadata kind 138): prerelease uri ↔ album uri ↔ release instant.
    /// Stable wrapper; the live provider is installed on go-live. Offline it is <see cref="NullPreReleaseService"/>, and
    /// every prerelease surface then degrades to "announced, but not pre-savable / not click-through-resolvable".</summary>
    public SwitchablePreReleaseService PreRelease { get; }
    /// <summary>Spotify's curated Liked Songs content-filter chips. Stable wrapper; the live provider is installed on
    /// go-live. Offline it is <see cref="NullContentFilterService"/> (empty), and the Liked chip bar then derives its
    /// chips from the tracks' own kind-6 descriptors instead of showing nothing.</summary>
    public SwitchableContentFilterService ContentFilters { get; }
    /// <summary>Music-video detection + the video↔audio file-id map (extended-metadata, etag-cached). Stable wrapper; the
    /// live Spotify implementation is installed after login. Offline it is a no-op (<see cref="NoVideoService"/>).</summary>
    public SwitchableVideoService Video { get; }
    /// <summary>Track row adornments — cover tint (extended-metadata kind 179) + tempo/key (kind 222). REAL backend
    /// only; null offline/fake, in which case rows fall back to the neutral placeholder and hide the tempo column.
    /// Set on go-live, cleared on GoOffline.</summary>
    public Wavee.SpotifyLive.SpotifyTrackAdornmentService? TrackAdornments { get; internal set; }
    /// <summary>Spotify user profile cache for playlist owners and added-by contributors. Stable wrapper; offline/fake
    /// returns null so raw ids remain visible until a live resolver is installed.</summary>
    public SwitchableUserProfileService UserProfiles { get; }
    /// <summary>Spotify friend-activity (presence) feed — what friends are listening to. Stable wrapper; the live provider
    /// is installed after login, offline/fake it is the permanently-offline <see cref="NullFriendActivityService"/>.</summary>
    public SwitchableFriendActivityService Friends { get; }
    public PlaybackBridge Playback { get; }
    /// <summary>The Mutations facet bridge (saved/liked/followed → engine Signal). Read via <see cref="LibraryBridge.Slot"/>.</summary>
    public LibraryBridge LibraryBridge { get; }
    /// <summary>The friends-feed facet bridge (presence snapshot → engine Signals). Read via <see cref="FriendsBridge.Slot"/>.</summary>
    public FriendsBridge FriendsBridge { get; }
    /// <summary>The local activity log (library-mutation history + Undo source). Durable (SQLite) on real, in-memory on fake.</summary>
    public ActivityLog Activity { get; }
    /// <summary>Spotify social notifications (gander). Stable wrapper; the live provider is installed after login, offline/fake
    /// it is the permanently-offline <see cref="NullSpotifyNotificationsService"/>.</summary>
    public SwitchableSpotifyNotificationsService SpotifyNotifications { get; }
    /// <summary>"What's New" (new releases/episodes from followed artists). Stable wrapper; live provider installed after login.</summary>
    public SwitchableWhatsNewService WhatsNew { get; }
    /// <summary>Concert discovery (artist schedules, hub feed, location controls). Stable wrapper; the live Spotify
    /// Pathfinder adapter is installed after login, offline/fake it is the permanently-offline <see cref="NullConcertService"/>.</summary>
    public SwitchableConcertService Concerts { get; }
    /// <summary>Spotify Browse (the category directory + category pages). Stable wrapper; the live Pathfinder adapter
    /// is installed on go-live, and offline/fake it is <see cref="NullBrowseService"/> so the directory renders its
    /// empty state instead of the UI holding a null.</summary>
    public Wavee.SpotifyLive.SwitchableBrowseService Browse { get; }
    /// <summary>Alternate versions (music videos / live / remix) + available audio formats for the expanded track
    /// drawer. Everything it serves is fetched ON EXPAND, never with the row bundle.</summary>
    public Wavee.SpotifyLive.SwitchableTrackExpansionService TrackExpansion { get; }
    /// <summary>One-shot OS geolocation (the "Use my location" concert flow). App/OS-scoped → hand-wired here like the other
    /// OS services (never switchable). Requested ONLY on an explicit user action; constructing it prompts nothing.</summary>
    public FluentGpu.Pal.IGeolocationProvider Geolocation { get; }
    /// <summary>The app-update seam. App-scoped → no switchable; the Null stub is permanent until a real updater ships.</summary>
    public IAppUpdateService AppUpdate { get; }
    /// <summary>The notification-center bridge (four categories → one aggregated feed + bell badge). Read via <see cref="NotificationCenterBridge.Slot"/>.</summary>
    public NotificationCenterBridge Notifications { get; }
    /// <summary>The root library cache (collections + per-entity detail caches) for instant, off-page-fresh navigation.</summary>
    public LibraryStore LibraryStore { get; }
    /// <summary>Persisted app settings (sidebar width, etc.) — read/written through the interface + typed keys, never the
    /// concrete store. The real registry-backed store is wired here, in the composition root, not at the call sites.</summary>
    public IAppSettings Settings { get; }
    /// <summary>The immutable UI/Spotify locale captured at process startup.</summary>
    public AppLocale Locale { get; }
    /// <summary>All sidebar state: the active design, per-design pane/view state, the shared unlimited pin store, the entry
    /// projection cell, and the Curated layout document with its 50-step undo stack. Owned HERE (not by the shell) so the
    /// pin store, the Settings picker and the customizer's undo history survive the login-gate shell swap; provided at the
    /// app root via <c>SidebarPreferences.Slot</c>. Constructed in the shared private ctor, so both CreateFake and
    /// CreateReal get one — local preferences are real on every backend (the settings store already is).</summary>
    public SidebarPreferences Sidebar { get; }
    /// <summary>The local "recently played" log (§C1.8.1) — a ring of the last 200 playback starts beside
    /// <c>history.json</c>. Appended by <see cref="PlaybackBridge"/> at every real track boundary and read by the
    /// sidebar's <c>wavee.history.played</c> source. Local state, so it is real on BOTH backends (the settings-store
    /// precedent).</summary>
    public PlayLogStore PlayLog { get; } = new();
    /// <summary>The sidebar's contribution lookup: the nine first-party data sources, keyed by their namespaced ids
    /// (<c>wavee.library</c>, <c>wavee.artist.topTracks</c>, …). This is what a section's contribution id resolves through
    /// — nothing in the UI may <c>switch</c> on an extension id. Published into the platform registry by
    /// <see cref="RegisterSidebarSources"/>.</summary>
    public SidebarDataSourceTable SidebarSources { get; }
    /// <summary>The ONE driver of <c>SidebarPreferences.Entries</c> and of the Curated planner input: it rebuilds the
    /// unified projection whenever the library / history / play log / pins / view state / culture move, and resolves every
    /// extension section to a row slice. Mount <c>SidebarBinder.MountPoint()</c> once at the app root (see its remarks) —
    /// the binder does nothing until that pump is alive.</summary>
    public SidebarProjectionBinder SidebarBinder { get; }
    /// <summary>The cross-arena memory-shedding coordinator (Backend/Residency/MemoryGovernor.cs), instantiated + wired here
    /// and driven by a periodic OS-memory-pressure poll (WaveeApp). Steady-state growth is already bounded by each cache's
    /// own LRU cap; the governor sheds FURTHER under real memory pressure. (Was dead code — only referenced by tests.)</summary>
    public Wavee.Backend.Residency.MemoryGovernor Residency { get; } = new();

    // Entity-residency caps (hardcoded good defaults, no env knobs). The governor arena (priority 3, CRITICAL-only) sheds
    // resident entities down to EntityResidencyCap using the reachability pin-set; SavedHeadPinCount bounds how many members
    // of each saved set the pin-set keeps warm so a collection page still paints without a cold round-trip. The always-on
    // 12k→8k upsert backstop in InMemoryStore is the real week-long bound; these size the pressure-driven top-up.
    const int EntityResidencyCap = 4000;
    const int SavedHeadPinCount = 200;

    /// <summary>Floor for the user-settable metadata-cache budget (§G) — below this the pin set alone overflows it.</summary>
    public const long MinMetadataCacheBudgetBytes = 32L * 1024 * 1024;

    /// <summary>App-side census contributor for the engine's FG_MEM_DIAG <c>[memcensus]</c> block: the entity-store + cache
    /// attribution line. The Windows host composes this into <c>AppHost.GpuDetail</c> (wired in <c>Program</c>'s
    /// DiagnosticRun, which runs once per launch); set on app mount. Built on demand at census cadence — never per frame.
    /// Null ⇒ no app line. Last-writer-wins is fine (one Services per process; tests don't set it).</summary>
    public static System.Func<string>? MemCensusHook;

    Services(IWaveeLog log, ISpotifySession session, IMusicLibrary library,
             IPlaybackPlayer player, IConnectDevices devices, ILyricsProvider lyrics, IAppSettings settings, IMutationSource mutations,
             UserPlaylistSource userPlaylists, IPlaylistMutationSource playlistEdits, IActivityStore activityStore, AppLocale appLocale)
    {
        Log = log;
        Session = session;
        Library = library;
        Player = player;
        Devices = devices;
        Connectivity = new Wavee.Backend.SwitchableConnectivity(new Wavee.Backend.Connectivity());
        Lyrics = lyrics;
        AlbumEnrichment = new SwitchableAlbumEnrichmentService(new CatalogAlbumEnrichmentService(library));
        ArtistStats = new SwitchableArtistStatsService(new NullArtistStatsService());
        ArtistPopularTracks = new SwitchableArtistPopularTracksService(new NullArtistPopularTracksService());
        UserTop = new SwitchableUserTopService(new NullUserTopService());
        PlaylistPopcount = new SwitchablePlaylistPopcountService(NullPlaylistPopcountService.Instance);
        PreRelease = new SwitchablePreReleaseService(NullPreReleaseService.Instance);
        ContentFilters = new SwitchableContentFilterService(NullContentFilterService.Instance);
        Video = new SwitchableVideoService(new NoVideoService());
        UserProfiles = new SwitchableUserProfileService(new NullUserProfileService());
        Friends = new SwitchableFriendActivityService(new NullFriendActivityService());
        Settings = settings;
        Locale = appLocale;
        // Sidebar preferences + the sidebar-layout document. Both CreateFake and CreateReal funnel through this ctor, so
        // both backends get one; the store's Load() is the only I/O and it never throws (a missing file is a first run, a
        // bad one is a fault that suppresses writes and loads the built-in Curated default in memory).
        Sidebar = new SidebarPreferences(settings, SidebarLayoutStore.ForApp());
        Playback = new PlaybackBridge(player, devices, session);
        // Seed the movable video surface from persisted settings BEFORE the first frame, so the remembered placement is
        // already in effect rather than popping in after the shell mounts.
        Playback.SeedVideoSurfaceFromSettings(settings);
        Activity = new ActivityLog(activityStore);
        LibraryBridge = new LibraryBridge(mutations, userPlaylists, playlistEdits, Activity);
        FriendsBridge = new FriendsBridge(Friends);
        // Notification center: the four category sources + the aggregation bridge (the friend-activity seam pattern).
        SpotifyNotifications = new SwitchableSpotifyNotificationsService(new NullSpotifyNotificationsService());
        WhatsNew = new SwitchableWhatsNewService(new NullWhatsNewService());
        Concerts = new SwitchableConcertService(new NullConcertService());   // live Pathfinder adapter installed on go-live
        Browse = new Wavee.SpotifyLive.SwitchableBrowseService();            // ditto — Null until go-live
        TrackExpansion = new Wavee.SpotifyLive.SwitchableTrackExpansionService();
        Geolocation = new FluentGpu.WindowsApi.Location.WindowsGeolocationProvider();   // OS one-shot; no prompt until used
        AppUpdate = new NullAppUpdateService();
        Notifications = new NotificationCenterBridge(Activity, SpotifyNotifications, WhatsNew, AppUpdate, settings,
            new ActivityUndoExecutor(LibraryBridge, library, Activity));
        LibraryStore = new LibraryStore(library, mutations, userPlaylists, library as ICollectionEvents);

        // ── the local play log + the extension platform + the sidebar projection driver (M1) ──
        // Both Create paths funnel through this ctor, so the fake backend gets the same wiring over its own fake stores.
        // The play log is LOCAL state (like the settings store), so it is real on both backends.
        PlayLog.Init(PlayLogStore.DefaultPath());
        PlayLog.LoadFromDisk();
        Playback.AttachPlayLog(PlayLog);

        // The binder IS the sources' projection snapshot, so it is constructed first and handed to the registration.
        // The ACTION half of the first-party extension (BuiltInExtensionTable) is registered by
        // WaveeExtensionRegistry.Build(ActionServices) from the shell — it needs the service bag the shell owns — and this
        // table is published into that registry by RegisterSidebarSources below. The sidebar never waits for it: its own
        // host resolves the first-party table directly, and consults the registry only for contributed (third-party) ids.
        SidebarBinder = new SidebarProjectionBinder(Sidebar, LibraryStore, PlayLog, Playback);
        SidebarSources = WaveeBuiltInDataSources.RegisterAll(registrar: null, SidebarBinder, library,
            ArtistPopularTracks, WhatsNew, Concerts, Playback);
        SidebarBinder.UseHost(new WaveeBuiltInDataSources.ContributionHost(SidebarSources), SidebarSources);
        Sidebar.Binder = SidebarBinder;
        // Wire the detail caches as a sheddable arena (priority 2 = shed under MODERATE+ pressure, so at-rest A→B→A stays
        // instant; the LRU insert-cap already bounds steady state). The entity-store "unpinned drop" (priority 3/4) is the
        // documented follow-up — it needs a reachability pin-set to evict live entities safely.
        Residency.Register(2, "detail-cache", () => LibraryStore.ShedDetails(keep: 16));
        // Priority 3 = "unpinned entities" (shed only under CRITICAL OS pressure, per the governor tiers). The reachability
        // pin-set is built ON DEMAND inside the callback — no steady-state bookkeeping. Null RealStore (fake backend) ⇒ a
        // no-op 0, and the ?. short-circuit means BuildPinSet never even runs there. The closure captures `this`; RealStore
        // is populated by CreateReal after construction, long before the 30 s poll first fires.
        Residency.Register(3, "entity-store",
            () => (RealStore as Wavee.Backend.Persistence.CachedStore)?.ShedEntities(BuildPinSet(), EntityResidencyCap) ?? 0L);
    }

    /// <summary>Build the entity-eviction pin-set on demand (inside the shed callback): the now-playing + queue uris, the
    /// uris a still-cached detail model will re-render, and the saved-set heads. UI-thread (the governor Trim is posted to
    /// the UI thread); Peek reads never subscribe.
    ///
    /// UI-THREAD-AFFINE (critique #10): <see cref="LibraryStore.CollectPinnedUris"/> walks the detail caches, which only
    /// the UI thread mutates. Both callers marshal: the MemoryGovernor through its <c>post(() =&gt; Residency.Trim(...))</c>
    /// timer, and the cache GC through the very same <c>post</c> handed to <see cref="Wavee.Backend.Persistence.EntityCacheGc.Start"/>.
    /// Never call it from a background thread.</summary>
    internal System.Collections.Generic.ISet<string> BuildPinSet()
    {
        var pins = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        if (Playback.CurrentTrack.Peek()?.Uri is { Length: > 0 } cur) pins.Add(cur);
        if (Playback.CurrentContext.Peek() is { Length: > 0 } ctx) pins.Add(ctx);
        var queue = Playback.Queue.Peek();
        for (int i = 0; i < queue.Count; i++) if (queue[i].Track.Uri is { Length: > 0 } qu) pins.Add(qu);
        LibraryStore.CollectPinnedUris(pins);
        (RealStore as Wavee.Backend.Persistence.CachedStore)?.CollectSavedHeads(pins, SavedHeadPinCount);
        return pins;
    }

    /// <summary>Publish the first-party sidebar data sources into the platform registry. Call it ONCE from the composition
    /// root immediately after <c>WaveeExtensionRegistry.Build(actionServices)</c> — that build owns the ACTION half (it
    /// needs the shell's service bag), and this adds the DATA-SOURCE half, so the customizer's palette and M3's permission
    /// checks see one complete "wavee" extension. Skipping it does not break the sidebar (its host resolves
    /// <see cref="SidebarSources"/> directly); only the registry's enumeration would be incomplete.</summary>
    public void RegisterSidebarSources(WaveeExtensionRegistry registry)
        => WaveeBuiltInDataSources.Publish(registry, SidebarSources);

    /// <summary>One compact app census line for the FG_MEM_DIAG report (see <see cref="MemCensusHook"/>). Built on demand.</summary>
    public string CensusLine()
    {
        var sb = new System.Text.StringBuilder(160);
        if (RealStore is Wavee.Backend.Persistence.CachedStore cs)
        {
            var c = cs.EntityCounts;
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"store t={c.Tracks} al={c.Albums} ar={c.Artists} pl={c.Playlists} sh={c.Shows} ep={c.Episodes} v={c.Versions} estMB={cs.EstimatedEntityBytes / (1024.0 * 1024):0.0}");
            if (cs.HasEvictedEntities) sb.Append(" evicted");
        }
        else sb.Append("store (fake)");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $" | detail={LibraryStore.DetailCount}");
        return sb.ToString();
    }

    /// <summary>The fake wiring that drives the skeleton with in-memory data (no network). Persistence is real (the
    /// settings store), since it's local state, not catalog data.</summary>
    public static Services CreateFake(IAppSettings? settings = null, AppLocale? appLocale = null)
    {
        var session = new FakeSpotifySession();
        // Local audio playback is not supported yet: the player rejects every play intent (surfaced as the standard
        // "choose a remote device" toast, wired below); real playback happens only on a Connect device after live login.
        var player = new UnsupportedPlaybackPlayer();
        var devices = new NoConnectDevices();   // no in-process devices — the roster comes from the live Connect cluster
        // The composition root may create the store early (to seed the theme before the first frame) and pass it in;
        // otherwise create it here. Same registry either way (the wrapper is stateless), so a second instance is harmless.
        settings ??= AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        var store = settings;
        var export = SpotifyExport.Load();

        // The Mutations facet (docs/architecture.md §4.2): the user's saved/liked/followed set, persisted via the settings
        // store (the in-process outbox). Seeded on first run from the first ~300 liked uris so the Liked page reads as
        // saved; later runs load the persisted set (incl. session likes). Registered as a capability-only source.
        string rawSaved = store.Get(WaveeSettings.SavedLibrary);
        IEnumerable<string> savedSeed = string.IsNullOrEmpty(rawSaved)
            ? FakeData.LikedSongs(System.Math.Min(System.Math.Max(0, export.LikedCount), 300)).Select(t => t.Uri)
            : rawSaved.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        var mutations = new LocalMutationSource(savedSeed, snap => store.Set(WaveeSettings.SavedLibrary, string.Join("\n", snap)));

        // User-created playlists (the playlist-edit Mutations): a catalog source owning wavee:playlist:*.
        var userPlaylists = new UserPlaylistSource();
        var playlistEdits = new LocalPlaylistMutationSource(userPlaylists);

        // The unified source registry (docs/architecture.md §4.3): every connected catalog source + the facets it declares.
        // Playback/Lyrics/Remote are NOT in-process sources anymore (local playback is unsupported; the roster is the live
        // Connect cluster's) — the session registers its Session facet; the catalog façade federates the catalog sources.
        var registry = new SourceRegistry(new ISource[]
        {
            new SpotifyExportSource(export),   // Catalog | Home | Search (owns spotify:*)
            new LocalSource(),                 // Catalog | Search | LocalDecode (owns local: / wavee:local:* — the peer source)
            userPlaylists,                     // Catalog (owns wavee:playlist:* — user-created playlists; before the fallback)
            new FakeSource(),                  // Catalog (synthetic collections + the non-spotify fallback)
            new FakePodcastSource(),           // Podcasts (synthetic shows / episodes; owns wavee:show:* / wavee:episode:*)
            mutations,                         // Mutations (save / like / follow)
            session,                           // Session (auth / account / market)
        });
        var library = new AggregateCatalog(registry);
        var svc = new Services(WaveeLog.Instance, session, library, player, devices, new NoLyricsProvider(), settings, mutations, userPlaylists, playlistEdits, new InMemoryActivityStore(), appLocale ?? AppLocale.English);
        player.OnPlayIntentRejected = () => svc.Playback.NotifyLocalPlaybackUnsupported();   // any play intent → the standard toast
        svc.Log.Info("app", "Services created (sources: spotify-export, local-files, user-playlists, podcasts, fake + session facet; playback remote-only; mutations: saved-state + playlists)");
        return svc;
    }

    /// <summary>The REAL backend wiring: the persistent Store-backed catalog (<see cref="Wavee.Backend.Library.StoreLibrarySource"/>
    /// over a SQLite cold tier) + the durable, multi-set mutation engine, behind the same Wavee.Core seams. Playback stays
    /// the in-process fake (audio is a later milestone); the live session/transport (login → spclient fetchers → the hm://
    /// dealer) are connected by a separate bootstrap. The catalog reads the persisted Store offline; a first run is empty
    /// until that bootstrap syncs. Gated behind <c>--real-backend</c> so the FakeData demo stays the default.</summary>
    public static Services CreateReal(IAppSettings? settings = null, string? accountDbPath = null, AppLocale? appLocale = null)
    {
        AppLocale locale = appLocale ?? AppLocale.English;
        var session = new FakeSpotifySession();     // Session facet — swapped for the real EngineSessionSource on live connect
        var player = new UnsupportedPlaybackPlayer();   // local audio unsupported → play intents toast until go-live swaps in the live controller
        var devices = new NoConnectDevices();           // empty roster until the live Connect cluster arrives on go-live
        settings ??= AppDataSettings.ForUnpackaged("Wavee", "Wavee");

        // The persistent, offline-first backend store (its own SQLite file under LocalAppData by default).
        accountDbPath ??= System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Wavee", "library.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(accountDbPath)!);
        // §G startup marks: the two ctors are the only synchronous disk work before first paint, so they are what
        // `boot.sqlite_open_ms` (open + migrate + open-time sweep) and `boot.identity_load_ms` (the DEFERRED ctor's
        // identity tier: saved sets, video map, overrides, pin mirrors) actually measure.
        long openStart = System.Environment.TickCount64;
        var cold = new Wavee.Backend.Persistence.SqliteColdStore(accountDbPath, Wavee.Backend.Persistence.SqliteColdStore.DefaultAccount, locale.SpotifyLanguage);
        long openMs = System.Environment.TickCount64 - openStart;
        long identityStart = System.Environment.TickCount64;
        var store = new Wavee.Backend.Persistence.CachedStore(cold);
        long identityMs = System.Environment.TickCount64 - identityStart;

        // The collection self-write echo registry (§7.1): the write strategy records accepted-write cuids; the sync loop
        // drops our own echoes. One instance shared between the write path and the read loop (wired below on go-live).
        var echoRing = new Wavee.Backend.Collections.CollectionEchoRing();
        var spclientBaseUrl = new Wavee.Backend.SpclientBaseUrlHolder();
        // The durable, multi-set mutation engine (set saves + playlist OpRebase edits) behind the IMutationSource seam.
        var mutEngine = new Wavee.Backend.MutationEngine(store,
            new Wavee.Backend.IMutationStrategy[]
            {
                new Wavee.Backend.SetReplayStrategy(echoRing),
                new Wavee.Backend.OpRebaseStrategy(store, () => spclientBaseUrl.Value),
                new Wavee.Backend.RootlistFollowStrategy(store),
            }, cold);
        // The mutation transport is SWITCHABLE (stub → live dealer on go-live, back to stub on logout) so writes made while
        // logged out queue durably and replay on next login (§2.1); the drain binds to this stable facade once.
        var mutTransport = new Wavee.Backend.SwitchableTransport(new Wavee.Backend.StubTransport());
        var sessionHost = new Wavee.Backend.SessionContextHost(
            new Wavee.Backend.SessionContext("", "US", "premium", locale.SpotifyLanguage, Wavee.Backend.Tier.Premium, false));
        var mutations = new Wavee.Backend.EngineMutationSource(store, mutEngine, mutTransport, () => sessionHost.Current);

        // The catalog: the persistent Store-backed source (collection_items × shared entities; owns spotify:* + podcasts)
        // and the user-created playlists source (owns wavee:playlist:*).
        var storeLibrary = new Wavee.Backend.Library.StoreLibrarySource(store);
        var userPlaylists = new UserPlaylistSource();
        userPlaylists.ExposeInCatalog = false;
        var playlistMutations = new Wavee.Backend.Playlists.PlaylistMutationSource(
            mutEngine, mutTransport, new Wavee.Backend.Spotify.HttpClientExchange(), () => sessionHost.Current,
            () => spclientBaseUrl.Value, userPlaylists, store);
        // The "recommended songs" extender rides the SAME switchable transport (stub → live dealer on go-live, back on logout).
        var extender = new Wavee.Backend.Playlists.PlaylistExtenderClient(mutTransport);

        var registry = new SourceRegistry(new ISource[]
        {
            storeLibrary,          // Catalog | Podcasts (the real persistent backend; owns spotify:*)
            new LocalSource(),     // Catalog | Search | LocalDecode (the local-files peer)
            userPlaylists,         // Catalog (wavee:playlist:* — user-created playlists)
            mutations,             // Mutations (durable, multi-set save/unsave + playlist edits)
            session,               // Session
        });
        var library = new AggregateCatalog(registry);
        // Switchable facades over the fake playback/devices: a live Connect session swaps in at runtime (svc.GoLive)
        // without rebuilding the UI — the PlaybackBridge binds to these stable facades.
        var swPlayer = new Wavee.Backend.SwitchablePlayer(player);
        var swDevices = new Wavee.Backend.SwitchableDevices(devices);
        var swSession = new Wavee.Backend.SwitchableSession(session);
        var swLyrics = new Wavee.Backend.SwitchableLyrics(new NoLyricsProvider());   // swapped to the real AggregatingLyricsProvider on live login
        var svc = new Services(WaveeLog.Instance, swSession, library, swPlayer, swDevices, swLyrics, settings, mutations, userPlaylists, playlistMutations, new Wavee.Backend.Persistence.SqliteActivityStore(accountDbPath), locale);
        player.OnPlayIntentRejected = () => svc.Playback.NotifyLocalPlaybackUnsupported();   // pre-go-live: play intents show the "choose a remote device" toast
        svc.RealStore = store;
        // The user's local video-override curation. Wired HERE (not on go-live) precisely so attaching a custom mp4 works
        // WITHOUT Spotify: the bridge's has-video answer consults it directly, and the resolver installed below has no
        // source tier at all — an override is the only video such a build can serve. LiveSessionHost later replaces that
        // resolver with the full two-tier composite, keeping tier 1 identical.
        var videoOverrides = new VideoOverrideService(store, new WaveeLogger(WaveeLog.Instance, VideoOverrideService.LogCategory));
        videoOverrides.OnChanged = (uri, kind) => svc.Playback.NotifyVideoOverrideChanged(uri, kind);
        videoOverrides.OnBrokenLink = uri => svc.Playback.NotifyVideoOverrideMissing(uri);
        svc.VideoOverrides = videoOverrides;
        svc.Playback.AttachVideoOverrides(videoOverrides);
        svc.Playback.ResolveVideoSource = CompositeVideoResolver.OverridesOnly(videoOverrides).ResolveAsync;
        svc.RealLibrarySource = storeLibrary;
        storeLibrary.UserProfiles = svc.UserProfiles;
        svc.MutTransport = mutTransport;
        svc.RealCold = cold;
        svc.RealMutations = mutEngine;
        svc.RealSessionHost = sessionHost;
        svc.EchoRing = echoRing;
        svc.RealMutationSource = mutations;
        svc.RealPlaylistMutations = playlistMutations;
        svc.RealExtender = extender;
        svc.RealSpclientBaseUrl = spclientBaseUrl;
        // The metadata-cache GC (design §C). Constructed here (composition root) but NOT started: it needs the UI-thread
        // marshaller to snapshot BuildPinSet, so WaveeApp's mount effect arms it — the same place, and the same `post`,
        // the MemoryGovernor poll uses. It also owns the one-time post-migration VACUUM that used to be kicked off here.
        svc.CacheGc = new Wavee.Backend.Persistence.EntityCacheGc(cold, store, svc.Log, svc.BuildPinSet,
            System.Math.Max(MinMetadataCacheBudgetBytes, settings.Get(WaveeSettings.MetadataCacheBudgetBytes)))
        {
            OpenMillis = openMs,
            IdentityLoadMillis = identityMs,
        };
        svc.Log.Info("app", "Services created (REAL backend: persistent Store + StoreLibrarySource + durable multi-set mutations; live session/fetch/dealer connect on bootstrap)");
        return svc;
    }

    /// <summary>Swap the playback player + Connect device roster to a live backend at runtime. The PlaybackBridge bound to
    /// the switchable facades re-points without a rebuild (no-op if this Services wasn't built with switchables).</summary>
    public void GoLive(IPlaybackPlayer player, IConnectDevices devices, ISpotifySession? session = null, IConnectivity? connectivity = null, ILyricsProvider? lyrics = null)
    {
        (Player as Wavee.Backend.SwitchablePlayer)?.SetInner(player);
        (Devices as Wavee.Backend.SwitchableDevices)?.SetInner(devices);
        if (session is not null) (Session as Wavee.Backend.SwitchableSession)?.SetInner(session);
        if (connectivity is not null) (Connectivity as Wavee.Backend.SwitchableConnectivity)?.SetInner(connectivity);
        if (lyrics is not null) (Lyrics as Wavee.Backend.SwitchableLyrics)?.SetInner(lyrics);
        Log.Info("app", "playback backend swapped to LIVE (Connect device + now-playing + remote control + account active)"
            + (lyrics is not null ? " + real lyrics feed (aggregator + reranker)" : ""));
    }

    /// <summary>Register the live-session teardown handles. MUST be called BEFORE <see cref="GoLive"/> (which flips the
    /// shell on and makes logout reachable), so a logout fired in that window still clears credentials + disposes the host
    /// instead of leaking the live transport/dealer.</summary>
    internal void AttachLive(Wavee.SpotifyLive.LiveSessionHost host, Wavee.Backend.Persistence.ICredentialStore credStore)
    {
        LiveHost = host;
        CredStore = credStore;
    }

    /// <summary>The inverse of <see cref="GoLive"/>: re-point the switchable facades back to the remote-only playback stub +
    /// an empty device roster + a fresh fake session, so the app returns to a clean logged-out state with no process restart
    /// (no-op if not built with switchables).</summary>
    public void GoOffline()
    {
        var player = new UnsupportedPlaybackPlayer();
        player.OnPlayIntentRejected = () => Playback.NotifyLocalPlaybackUnsupported();   // logged out: play intents toast again
        (Player as Wavee.Backend.SwitchablePlayer)?.SetInner(player);
        (Devices as Wavee.Backend.SwitchableDevices)?.SetInner(new NoConnectDevices());   // clears the device roster on logout
        (Session as Wavee.Backend.SwitchableSession)?.SetInner(new FakeSpotifySession());
        (Connectivity as Wavee.Backend.SwitchableConnectivity)?.SetInner(new Wavee.Backend.Connectivity());
        (Lyrics as Wavee.Backend.SwitchableLyrics)?.SetInner(new NoLyricsProvider());   // no lyrics until the next live login
        UserProfiles.SetInner(new NullUserProfileService());
        Friends.SetInner(new NullFriendActivityService());   // presence feed back offline until the next live login
        SpotifyNotifications.SetInner(new NullSpotifyNotificationsService());   // gander + what's-new feeds back offline
        WhatsNew.SetInner(new NullWhatsNewService());
        Concerts.SetInner(new NullConcertService());   // concert discovery back offline until the next live login
        Browse.Reset();                               // browse directory/pages unavailable until the next live login
        TrackExpansion.Reset();                       // the row drawer shows no versions/formats while logged out
        TrackAdornments = null;                       // row tint/tempo stop resolving; rows fall back to the neutral tile
        ArtistStats.SetInner(new NullArtistStatsService());   // drop the session-bound overview provider until the next live login
        ArtistPopularTracks.SetInner(new NullArtistPopularTracksService());   // …and its spclient/metadata-bound step two
        UserTop.SetInner(new NullUserTopService());            // …and the account-scoped top-artist ranking on Home
        PlaylistPopcount.SetInner(NullPlaylistPopcountService.Instance);      // …and the playlist save-count badge
        PreRelease.SetInner(NullPreReleaseService.Instance);                  // …and kind-138 upcoming-release resolution
        ContentFilters.SetInner(NullContentFilterService.Instance);           // …and the Liked content-filter chips
        MutTransport?.SetInner(new Wavee.Backend.StubTransport());   // writes return to the inert stub (queue in the durable outbox, replay on next login)
        if (RealMutationSource is { } mutSrc) mutSrc.ScheduleDrain = null;   // back to inline drains — the loop is torn down with the host
        if (RealPlaylistMutations is { } pmSrc)
        {
            pmSrc.ScheduleDrain = null;
            pmSrc.SetHttp(new Wavee.Backend.Spotify.HttpClientExchange());   // drop the live pipeline (session-bound auth) with the host
        }
        if (RealSpclientBaseUrl is { } baseUrl) baseUrl.Value = "";   // no spclient until the next go-live
        LiveHttp = null;
        RealSync = null;
        PlaylistTuning.Value = null;
        LiveHost = null;
        CredStore = null;
        PlayPlayProvisioner = null;
        AudioBodyCache = null;
        AudioLicenseCache = null;
        Log.Info("app", "session torn down → offline (playback remote-only stub + empty device roster restored)");
    }

    /// <summary>Sign out without a restart: flip the session logged-out (gate → takeover), wipe the persisted reusable
    /// credential (else the next launch silently re-logs-in), tear the live host down OFF the UI thread, then reset to the
    /// fake backend.</summary>
    public async System.Threading.Tasks.Task LogoutAsync()
    {
        // Wipe the persisted credential FIRST — BEFORE flipping the session — so the gate swap to the takeover (which
        // auto-restarts the login) can't read the old credential and silently sign back in. Clear BOTH the captured store
        // and a fresh open (robust even if no live session captured one / it was a silent resume).
        CredStore?.Clear();
        Wavee.SpotifyLive.SpotifyLiveLogin.ClearStoredCredential();
        Playback.Login.Value = new Wavee.Core.LoginSnapshot(Wavee.Core.LoginPhase.LoggedOut);
        await Session.LogoutAsync().ConfigureAwait(false);   // LiveSpotifySession → LoggedOut → gate swaps shell → takeover
        if (LiveHost is { } h)
        {
            LiveHost = null;
            await System.Threading.Tasks.Task.Run(async () => await h.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        GoOffline();
    }
}
