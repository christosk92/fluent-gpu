using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The Windows Media-Foundation video backend (spec §9.1) — the <see cref="IMediaBackend"/> registered for
/// <see cref="MediaKind.MfVideoOrFile"/>. <see cref="OpenAsync"/> LEASES a warm <see cref="IVideoEngine"/>
/// (<c>IMFMediaEngineEx</c> windowless swapchain, the PROVEN clear-video path) and posts a source switch onto it,
/// wrapping the result in an <see cref="MfMediaSession"/>. Clear (unprotected) video only; DRM (a protected surface
/// handle + a <see cref="MediaOpenOptions.LicenseRelay"/>) attaches at the same <c>BindSurfaceHandle</c> point via the
/// injected <see cref="_drmBackend"/>.
///
/// <para><b>Warm-engine lease/return</b> (<c>docs/plans/video-smooth-switching-implementation.md</c> §1.5 — fixes E3).
/// The old shape tore the ENTIRE engine down (<c>MFShutdown</c>, a 2 s join) and rebuilt it (<c>MFStartup</c> +
/// <c>D3D11CreateDevice</c> + <c>CoCreateInstance</c>) on every track switch — a teardown that raced the successor's
/// startup and paid the full bring-up cost every time. This backend now keeps ONE <see cref="IVideoEngine"/> warm
/// across many opens: <see cref="OpenAsync"/> leases it (<see cref="LeaseEngine"/>), posts <c>SetSource</c> for the new
/// track (a live source switch — no teardown, no rebuild), and hands the resulting <see cref="MfMediaSession"/> a
/// <see cref="ReturnEngine"/> callback as its release action. When that session is disposed (a track switch, or the
/// player itself closing), the engine is paused + detached and returned to the pool instead of destroyed — the next
/// <see cref="OpenAsync"/> reuses it. Sequencing: <c>MediaPlayer.OpenAsync</c> already awaits the OLD session's
/// <c>DisposeAsync</c> before calling <see cref="OpenAsync"/> on this backend, so <see cref="ReturnEngine"/>
/// happens-before the next <see cref="LeaseEngine"/> — there is never a race between a return and the next lease.
/// A <see cref="VideoEngineFlags.Faulted"/> engine (an unrecoverable bring-up failure) is discarded and rebuilt at the
/// NEXT lease, never reused. Two <see cref="FluentGpu.Media.MediaPlayer"/>s sharing one <see cref="MfMediaPlayer"/>
/// concurrently is an edge case, not the steady state: the second concurrent lease gets a throwaway engine instead of
/// fighting the warm one's single-writer ownership (documented decision — plan §8 risks).</para>
/// <para><see cref="OpenAsync"/> never throws for an MF-side open failure: <see cref="IVideoEngine.PostSetSource"/> is
/// fire-and-forget (no blocking bring-up left to await, no synchronous HRESULT to check) — a failure surfaces later as
/// a typed <see cref="MediaError"/> on the session's signal sink, exactly like every other in-flight media error.
/// Nothing is awaited on the way to the source switch: an <see cref="AdaptiveSource"/>'s manifest is fetched IN PARALLEL
/// with MF's own download of the same URL and attached to the session when it arrives
/// (<see cref="MfMediaSession.AttachManifest"/>), and a live manifest is re-fetched on its update period. A manifest that
/// cannot be loaded costs the catalog (tracks, qualities), never the playback.</para>
/// </summary>
public sealed class MfMediaPlayer : IMediaBackend, IAsyncDisposable
{
    private readonly Func<IVideoEngine> _engineFactory;
    private readonly IMediaBackend? _drmBackend;
    private readonly HttpClient _http;

    private readonly object _engineLock = new();
    private IVideoEngine? _warm;
    private bool _leased;
    /// <summary>Fires once, <see cref="WarmIdleDisposeMs"/> after the warm engine was returned, and disposes it if it
    /// is still idle. Re-armed on every return and cancelled by the next lease.</summary>
    private Timer? _warmIdle;
    private readonly int _warmIdleMs;

    /// <summary>How long a returned engine stays warm before it is torn down.
    /// <para>Keeping it forever — which is what "warm across many opens" meant until now — is not free, and the cost
    /// is invisible to every memory instrument the app has. A live <c>VideoMediaEngine</c> owns a SECOND D3D11 device
    /// (created with VIDEO_SUPPORT), an <c>IMFDXGIDeviceManager</c>, an <c>IMFMediaEngineEx</c> in windowless-swapchain
    /// mode, and whatever surfaces Media Foundation decided to keep — the decoder's NV12 reference frames and MF's own
    /// BGRA8 swapchain. None of it can be counted by the GPU resource tracker, which needs a caller-supplied byte
    /// count and only ever receives a swapchain HANDLE. So once a single video had played, tens to well over a hundred
    /// megabytes stayed resident for the rest of the session, with nothing in the census able to name it.</para>
    /// <para>30 s is chosen against what the warmth is FOR: skipping between tracks, which happens in seconds and must
    /// never pay a rebuild. Closing a video and going back to audio is not that, and should give the memory back.</para></summary>
    private const int WarmIdleDisposeMs = 30_000;

    /// <summary>Create the production MF backend (a warm <see cref="VideoMediaEngine"/> is built on first open); no DRM
    /// support.</summary>
    public MfMediaPlayer() : this(static () => new VideoMediaEngine(), null) { }

    /// <summary>Create the MF backend with a protected (DRM) backend attached — a <see cref="MediaSource"/> carrying a
    /// <see cref="DrmConfig"/> routes to it (the native PlayReady CDM path); clear video keeps the proven engine path.
    /// The DRM backend is an Engine-layer <see cref="IMediaBackend"/> so this project stays decoupled from the concrete
    /// (WindowsApi) implementation — the app composition root injects it.</summary>
    public MfMediaPlayer(IMediaBackend drmBackend) : this(static () => new VideoMediaEngine(), drmBackend) { }

    /// <summary>Test/DI seam: supply a video-engine factory (a fake in unit tests), an optional DRM backend, and an
    /// idle window short enough that a test does not have to wait <see cref="WarmIdleDisposeMs"/> for the teardown.</summary>
    internal MfMediaPlayer(Func<IVideoEngine> engineFactory, IMediaBackend? drmBackend = null, HttpClient? http = null,
        int warmIdleMs = WarmIdleDisposeMs)
    {
        _engineFactory = engineFactory;
        _drmBackend = drmBackend;
        _http = http ?? MediaHttp.Shared;
        _warmIdleMs = warmIdleMs;
        Capabilities = new(SupportsVideo: true, SupportsAudioGraph: false, SupportsDrm: drmBackend is not null)
        {
            // MF resolves the container/codec on open; report the common clear-video families as query-time supported.
            IsSupported = static ct => ct.Video is CodecId.None or CodecId.H264 or CodecId.Hevc or CodecId.Av1 or CodecId.Vp9,
        };
    }

    /// <inheritdoc/>
    public MediaCapabilities Capabilities { get; }

    /// <summary>Return the warm engine (building/replacing it first if absent or <see cref="VideoEngineFlags.Faulted"/>),
    /// and mark it leased. A second CONCURRENT lease (a second <see cref="OpenAsync"/> in flight before the first's
    /// session is disposed) gets its own throwaway engine rather than contend for the warm one's single-writer
    /// ownership — the warm slot always belongs to at most one live session. A discarded (faulted) engine is disposed
    /// OFF the lock, on the pool: its dispose joins the engine thread (bounded at 2 s) and MF shuts down on it, and the
    /// caller may be the UI thread, with every concurrent lease queued behind the lock.</summary>
    private IVideoEngine LeaseEngine()
    {
        IVideoEngine? discarded = null;
        IVideoEngine leased;
        lock (_engineLock)
        {
            _warmIdle?.Change(Timeout.Infinite, Timeout.Infinite);   // a lease cancels the pending idle teardown
            bool faulted = _warm is { } w && (w.Snapshot.Flags & VideoEngineFlags.Faulted) != 0;
            if (_warm is null || faulted)
            {
                // The lease flag belonged to the engine being replaced; the fresh warm engine is unleased, so this lease
                // takes it (no extra engine, and the flag cannot stay stuck true with every later return a throwaway).
                // A discarded engine that is still leased is left to its session: ReturnEngine sees it is no longer
                // _warm and disposes it off the lock, so each engine is disposed exactly once.
                if (!_leased) discarded = _warm;
                if (_warm is not null) Diag.Line($"[video] engine.discard reason=faulted leased={(_leased ? 1 : 0)}");   // F197: why an engine goes away, next to its engine.destroy line
                _leased = false;
                _warm = _engineFactory();
                _warm.Start();
            }
            if (_leased)
            {
                Diag.Line("[video] engine.extra reason=concurrent-lease (a second engine alongside the warm one)");
                var extra = _engineFactory();
                extra.Start();
                leased = extra;
            }
            else
            {
                _leased = true;
                leased = _warm!;
            }
        }
        if (discarded is not null) DisposeOffLock(discarded);
        return leased;
    }

    /// <summary>Dispose an engine on the pool, never on the caller: the dispose joins the engine's MTA thread (bounded at
    /// 2 s) after MF's shutdown, which must not freeze a UI-thread caller or hold <see cref="_engineLock"/>. The same shape
    /// <see cref="DisposeAsync"/> already uses.</summary>
    private static void DisposeOffLock(IVideoEngine engine) => _ = Task.Run(engine.Dispose);

    /// <summary>Called from <see cref="MfMediaSession.DisposeAsync"/> (any thread; non-blocking — pause + detach are
    /// both posts). The warm engine goes back to the pool for the NEXT open; a throwaway (a second concurrent lease, or
    /// one leased while a rebuild already replaced <see cref="_warm"/>) is disposed outright instead.</summary>
    private void ReturnEngine(IVideoEngine engine)
    {
        bool throwaway;
        lock (_engineLock)
        {
            throwaway = !ReferenceEquals(engine, _warm);
            if (!throwaway)
            {
                _leased = false;
                engine.Commands.Post(VideoCommandKind.Transport, a: 0);   // pause — a warm-parked engine does not play
                engine.PostDetach();
                // Detaching releases the SOURCE, not the engine: the D3D11 device, the DXGI manager, the MF engine and
                // its surfaces all stay resident. Arm the idle teardown so an app that stops watching video gets that
                // memory back, while a track skip (which re-leases within seconds) still never pays a rebuild.
                _warmIdle ??= new Timer(static s => ((MfMediaPlayer)s!).DisposeWarmIfIdle(), this, Timeout.Infinite, Timeout.Infinite);
                _warmIdle.Change(_warmIdleMs, Timeout.Infinite);
            }
        }
        // A throwaway lease (or one orphaned by a rebuild) is disposed off the lock and off the caller — see DisposeOffLock.
        if (throwaway) DisposeOffLock(engine);
    }

    /// <summary>Timer callback: tear the warm engine down if nothing leased it in the meantime. Disposing off the
    /// timer thread is the same shape <see cref="LeaseEngine"/> already uses for a faulted engine — the engine owns
    /// its own MTA thread and joins it in Dispose — but it must happen OUTSIDE the lock, because that join can block
    /// and a concurrent lease would otherwise wait behind it.</summary>
    private void DisposeWarmIfIdle()
    {
        IVideoEngine? idle;
        lock (_engineLock)
        {
            if (_leased || _warm is null) return;   // re-leased between the timer firing and this lock: leave it warm
            idle = _warm;
            _warm = null;
        }
        Diag.Line($"[video] engine.discard reason=idle afterMs={_warmIdleMs}");
        idle.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
    {
        // Protected source → the DRM backend (native PlayReady CDM). It binds its protected DComp handle at the SAME
        // BindSurfaceHandle point as clear video; the license flows via opts.LicenseRelay (WithDrm).
        if (source.Drm is not null)
        {
            if (_drmBackend is null)
                throw new NotSupportedException(
                    "This MfMediaPlayer has no DRM backend; construct it with a protected backend (new MfMediaPlayer(protectedBackend)) to play protected sources.");
            return await _drmBackend.OpenAsync(source, opts, ct).ConfigureAwait(false);
        }

        string url = ResolveUrl(source) ?? throw new NotSupportedException(
            "MfMediaPlayer supports a file path or a URL source (FromFile/FromUri).");

        ct.ThrowIfCancellationRequested();   // before an engine is leased; nothing below blocks

        IVideoEngine engine = LeaseEngine();
        int epoch = engine.PostSetSource(url);   // fire-and-forget: no Task.Run, no block, no synchronous MF throw

        // A media element does not loop by default (the M3 harness kept a live frame via loop), but a source the caller
        // explicitly wrapped in .Loop() must. Only an INFINITE loop maps onto the MF media engine, whose loop flag is a
        // bool with no repeat count — a finite count would silently become infinite, so it stays unlooped instead.
        engine.Commands.Post(VideoCommandKind.Loop, i: IsInfiniteLoop(source) ? 1 : 0);
        // The play/pause intent goes out WITH the source: MF accepts Play() before the source resolves, and the Transport
        // slot is last-wins, so this overwrites the pause ReturnEngine left behind (a warm engine drains Detach, SetSource,
        // then Transport) instead of leaving the engine paused until the session's first post-metadata pump re-asserts it.
        engine.Commands.Post(VideoCommandKind.Transport, a: opts.StartPaused ? 0 : 1);

        var session = new MfMediaSession(engine, epoch, opts, null, ReturnEngine);
        // The catalog is not on the critical path: MF is already downloading the same URL, so the manifest loads alongside it
        // (its own retries and backoff included) and is attached when it lands. Tied to the SESSION's lifetime, not the open's
        // token: it keeps refreshing a live manifest for as long as the session lives.
        if (source is AdaptiveSource adaptive)
            _ = LoadManifestAsync(_http, session, adaptive, adaptive.Network ?? opts.Network);
        return session;
    }

    /// <summary>The floor and ceiling of a live manifest's refresh period, and the period used when the manifest names none
    /// (DASH <c>minimumUpdatePeriod</c> / HLS target duration are carried in <see cref="AdaptiveManifest.MinimumUpdatePeriod"/>).</summary>
    internal static readonly TimeSpan MinLiveRefresh = TimeSpan.FromSeconds(1), MaxLiveRefresh = TimeSpan.FromSeconds(30),
        DefaultLiveRefresh = TimeSpan.FromSeconds(6);

    /// <summary>How long to wait before re-fetching a live manifest: its own update period, clamped to a sane range.</summary>
    internal static TimeSpan LiveRefreshInterval(AdaptiveManifest manifest)
    {
        TimeSpan period = manifest.MinimumUpdatePeriod > TimeSpan.Zero ? manifest.MinimumUpdatePeriod : DefaultLiveRefresh;
        return period < MinLiveRefresh ? MinLiveRefresh : period > MaxLiveRefresh ? MaxLiveRefresh : period;
    }

    /// <summary>Load the manifest and attach it to <paramref name="session"/>; for a live one keep re-fetching it every
    /// <see cref="LiveRefreshInterval"/> so the live edge keeps moving (without a refresh the edge freezes at open and
    /// "go live" targets a stale one). Runs until the session is disposed. A failed first load leaves the session without a
    /// catalog (MF still plays the URL); a failed refresh keeps the previous manifest and tries again next period. Never
    /// faults: nobody observes this task.</summary>
    private static async Task LoadManifestAsync(HttpClient http, MfMediaSession session, AdaptiveSource source, NetworkOptions? network)
    {
        CancellationToken ct = session.Lifetime;
        bool attached = false;
        TimeSpan interval = DefaultLiveRefresh;
        try
        {
            while (true)
            {
                try
                {
                    AdaptiveManifest manifest = await AdaptiveManifestLoader.LoadAsync(http, source, network, ct).ConfigureAwait(false);
                    session.AttachManifest(manifest);
                    attached = true;
                    if (!manifest.IsLive) return;   // a VOD (or a finished live) manifest never changes
                    interval = LiveRefreshInterval(manifest);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (Diag.Enabled) Diag.Event("media.manifest", $"load failed ({(attached ? "refresh" : "first")}): {ex.Message}");
                    if (!attached) return;
                }
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The session was disposed: the load/refresh ends with it.
        }
    }

    /// <summary>True when the source asks to repeat forever (<c>.Loop()</c> with the default count of -1). Mirrors
    /// <see cref="ResolveUrl"/>'s wrapper walk so a loop survives being nested under a clip. A finite repeat count is
    /// deliberately NOT a loop here: <c>IMFMediaEngine.SetLoop</c> takes a bool, so honoring "play 3 times" as "play
    /// forever" would be worse than not looping at all.</summary>
    internal static bool IsInfiniteLoop(MediaSource source) => source switch
    {
        LoopSource l => l.Count < 0 || IsInfiniteLoop(l.Inner),
        ClipSource c => IsInfiniteLoop(c.Inner),
        _ => false,
    };

    /// <summary>Extract the MF source URL from a <see cref="MediaSource"/> (a local path is passed through; MF accepts
    /// both file paths and http(s) URLs). Returns null for a shape MF can't open by URL.</summary>
    internal static string? ResolveUrl(MediaSource source) => source switch
    {
        FileSource f => f.Path,
        UriSource u => u.Url,
        AdaptiveSource a => a.ManifestUri,
        ClipSource c => ResolveUrl(c.Inner),
        LoopSource l => ResolveUrl(l.Inner),
        _ => null,
    };

    /// <summary>Tear down the warm engine (app shutdown / backend replacement). Off-thread: <see cref="IVideoEngine.Dispose"/>
    /// joins its MTA thread (bounded at 2 s) and this must never block the caller.</summary>
    public async ValueTask DisposeAsync()
    {
        IVideoEngine? warm;
        Timer? idle;
        lock (_engineLock) { warm = _warm; _warm = null; _leased = false; idle = _warmIdle; _warmIdle = null; }
        idle?.Dispose();
        if (warm is not null) await Task.Run(warm.Dispose).ConfigureAwait(false);
    }
}
