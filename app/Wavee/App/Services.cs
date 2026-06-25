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
             FakePlaybackProvider player, IConnectDevices devices, IAppSettings settings, IMutationSource mutations,
             UserPlaylistSource userPlaylists)
    {
        Log = log;
        Session = session;
        Library = library;
        Player = player;
        Devices = devices;
        Lyrics = player;                               // the fake player also provides lyrics
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
        var svc = new Services(WaveeLog.Instance, session, library, player, devices, settings, mutations, userPlaylists);
        svc.Log.Info("app", "Services created (sources: spotify-export, local-files, user-playlists, podcasts, fake + playback/session/remote facets; mutations: saved-state + playlists)");
        return svc;
    }
}
