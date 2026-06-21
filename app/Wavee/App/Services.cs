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
    /// <summary>Persisted app settings (sidebar width, etc.) — read/written through the interface + typed keys, never the
    /// concrete store. The real registry-backed store is wired here, in the composition root, not at the call sites.</summary>
    public IAppSettings Settings { get; }

    Services(IWaveeLog log, ISpotifySession session, IMusicLibrary library,
             FakePlaybackProvider player, IConnectDevices devices, IAppSettings settings)
    {
        Log = log;
        Session = session;
        Library = library;
        Player = player;
        Devices = devices;
        Lyrics = player;                               // the fake player also provides lyrics
        Settings = settings;
        Playback = new PlaybackBridge(player, devices, session);
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
        // The source-agnostic catalog seam (docs/architecture.md): the UI binds one IMusicLibrary façade that federates
        // over ordered sources. SpotifyExportSource (real JSON, owns spotify:*) first; FakeSource (synthetic albums/
        // artists + fallback) last. Adding a live-Spotify or local-files source later is purely additive here.
        var library = new AggregateCatalog(new SourceRegistry(new ISource[]
        {
            new SpotifyExportSource(SpotifyExport.Load()),
            new FakeSource(),
        }));
        var svc = new Services(WaveeLog.Instance, session, library, player, devices, settings);
        svc.Log.Info("app", "Services created (federated catalog: spotify-export + fake)");
        return svc;
    }
}
