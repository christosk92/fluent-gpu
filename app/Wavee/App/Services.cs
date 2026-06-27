using System.Linq;
using FluentGpu.Hooks;
using Wavee.Core;

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

    public IWaveeLog Log { get; }
    public ISpotifySession Session { get; }
    public IMusicLibrary Library { get; }
    public IPlaybackPlayer Player { get; }
    public IConnectDevices Devices { get; }
    public ILyricsProvider Lyrics { get; }
    public PlaybackBridge Playback { get; }
    /// <summary>The Mutations facet bridge (saved/liked/followed → engine Signal). Read via <see cref="LibraryBridge.Slot"/>.</summary>
    public LibraryBridge LibraryBridge { get; }
    /// <summary>The root library cache (collections + per-entity detail caches) for instant, off-page-fresh navigation.</summary>
    public LibraryStore LibraryStore { get; }
    /// <summary>Persisted app settings (sidebar width, etc.) — read/written through the interface + typed keys, never the
    /// concrete store. The real registry-backed store is wired here, in the composition root, not at the call sites.</summary>
    public IAppSettings Settings { get; }

    Services(IWaveeLog log, ISpotifySession session, IMusicLibrary library,
             IPlaybackPlayer player, IConnectDevices devices, ILyricsProvider lyrics, IAppSettings settings, IMutationSource mutations,
             UserPlaylistSource userPlaylists)
    {
        Log = log;
        Session = session;
        Library = library;
        Player = player;
        Devices = devices;
        Lyrics = lyrics;
        Settings = settings;
        Playback = new PlaybackBridge(player, devices, session);
        LibraryBridge = new LibraryBridge(mutations, userPlaylists);
        LibraryStore = new LibraryStore(library, mutations, userPlaylists, library as ICollectionEvents);
    }

    /// <summary>The fake wiring that drives the skeleton with in-memory data (no network). Persistence is real (the
    /// settings store), since it's local state, not catalog data.</summary>
    public static Services CreateFake(IAppSettings? settings = null)
    {
        var session = new FakeSpotifySession();
        var player = new FakePlaybackProvider();
        var devices = new FakeConnectDevices();
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

        // User-created playlists (the playlist-edit Mutations): a catalog source owning wavee:playlist:*. The player's
        // context resolver is pointed at it so a user playlist actually plays (it's not in the synthetic FakeData).
        var userPlaylists = new UserPlaylistSource();
        player.ContextResolver = uri => userPlaylists.ResolveContext(uri);

        // The unified source registry (docs/architecture.md §4.3): every connected source + the facets it declares. The
        // catalog façade federates the catalog sources; the player / session / devices register their Playback / Session /
        // Remote facets so the federation hook (registry.OfCapability(cap)) can route per facet. A future FederatedPlayback /
        // FederatedRemote attaches there — deferred per §4.3 until a second real source, but the seam ports are now live.
        var registry = new SourceRegistry(new ISource[]
        {
            new SpotifyExportSource(export),   // Catalog | Home | Search (owns spotify:*)
            new LocalSource(),                 // Catalog | Search | LocalDecode (owns local: / wavee:local:* — the peer source)
            userPlaylists,                     // Catalog (owns wavee:playlist:* — user-created playlists; before the fallback)
            new FakeSource(),                  // Catalog (synthetic collections + the non-spotify fallback)
            new FakePodcastSource(),           // Podcasts (synthetic shows / episodes; owns wavee:show:* / wavee:episode:*)
            mutations,                         // Mutations (save / like / follow)
            player,                            // Playback + Lyrics (the in-process player)
            session,                           // Session (auth / account / market)
            devices,                           // Remote (Connect devices / transfer)
        });
        var library = new AggregateCatalog(registry);
        var svc = new Services(WaveeLog.Instance, session, library, player, devices, player, settings, mutations, userPlaylists);
        svc.Log.Info("app", "Services created (sources: spotify-export, local-files, user-playlists, podcasts, fake + playback/session/remote facets; mutations: saved-state + playlists)");
        return svc;
    }

    /// <summary>The REAL backend wiring: the persistent Store-backed catalog (<see cref="Wavee.Backend.Library.StoreLibrarySource"/>
    /// over a SQLite cold tier) + the durable, multi-set mutation engine, behind the same Wavee.Core seams. Playback stays
    /// the in-process fake (audio is a later milestone); the live session/transport (login → spclient fetchers → the hm://
    /// dealer) are connected by a separate bootstrap. The catalog reads the persisted Store offline; a first run is empty
    /// until that bootstrap syncs. Gated behind <c>--real-backend</c> so the FakeData demo stays the default.</summary>
    public static Services CreateReal(IAppSettings? settings = null, string? accountDbPath = null)
    {
        var session = new FakeSpotifySession();     // Session facet — swapped for the real EngineSessionSource on live connect
        var player = new FakePlaybackProvider();     // audio deferred → playback stays fake
        var devices = new FakeConnectDevices();
        settings ??= AppDataSettings.ForUnpackaged("Wavee", "Wavee");

        // The persistent, offline-first backend store (its own SQLite file under LocalAppData by default).
        accountDbPath ??= System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Wavee", "library.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(accountDbPath)!);
        var cold = new Wavee.Backend.Persistence.SqliteColdStore(accountDbPath);
        var store = new Wavee.Backend.Persistence.CachedStore(cold);

        // The durable, multi-set mutation engine (set saves + playlist OpRebase edits) behind the IMutationSource seam.
        var mutEngine = new Wavee.Backend.MutationEngine(store,
            new Wavee.Backend.IMutationStrategy[] { new Wavee.Backend.SetReplayStrategy(), new Wavee.Backend.OpRebaseStrategy() }, cold);
        var transport = new Wavee.Backend.StubTransport();   // replaced by the live hm:// dealer transport on connect
        var sessionHost = new Wavee.Backend.SessionContextHost(
            new Wavee.Backend.SessionContext("", "US", "premium", "en", Wavee.Backend.Tier.Premium, false));
        var mutations = new Wavee.Backend.EngineMutationSource(store, mutEngine, transport, () => sessionHost.Current);

        // The catalog: the persistent Store-backed source (collection_items × shared entities; owns spotify:* + podcasts)
        // and the user-created playlists source (owns wavee:playlist:*).
        var storeLibrary = new Wavee.Backend.Library.StoreLibrarySource(store);
        var userPlaylists = new UserPlaylistSource();
        player.ContextResolver = uri => userPlaylists.ResolveContext(uri);

        var registry = new SourceRegistry(new ISource[]
        {
            storeLibrary,          // Catalog | Podcasts (the real persistent backend; owns spotify:*)
            new LocalSource(),     // Catalog | Search | LocalDecode (the local-files peer)
            userPlaylists,         // Catalog (wavee:playlist:* — user-created playlists)
            mutations,             // Mutations (durable, multi-set save/unsave + playlist edits)
            player,                // Playback + Lyrics (the in-process fake)
            session,               // Session
            devices,               // Remote
        });
        var library = new AggregateCatalog(registry);
        // Switchable facades over the fake playback/devices: a live Connect session swaps in at runtime (svc.GoLive)
        // without rebuilding the UI — the PlaybackBridge binds to these stable facades.
        var swPlayer = new Wavee.Backend.SwitchablePlayer(player);
        var swDevices = new Wavee.Backend.SwitchableDevices(devices);
        var swSession = new Wavee.Backend.SwitchableSession(session);
        var svc = new Services(WaveeLog.Instance, swSession, library, swPlayer, swDevices, player, settings, mutations, userPlaylists);
        svc.RealStore = store;
        svc.Log.Info("app", "Services created (REAL backend: persistent Store + StoreLibrarySource + durable multi-set mutations; live session/fetch/dealer connect on bootstrap)");
        return svc;
    }

    /// <summary>Swap the playback player + Connect device roster to a live backend at runtime. The PlaybackBridge bound to
    /// the switchable facades re-points without a rebuild (no-op if this Services wasn't built with switchables).</summary>
    public void GoLive(IPlaybackPlayer player, IConnectDevices devices, ISpotifySession? session = null)
    {
        (Player as Wavee.Backend.SwitchablePlayer)?.SetInner(player);
        (Devices as Wavee.Backend.SwitchableDevices)?.SetInner(devices);
        if (session is not null) (Session as Wavee.Backend.SwitchableSession)?.SetInner(session);
        Log.Info("app", "playback backend swapped to LIVE (Connect device + now-playing + remote control + account active)");
    }
}
