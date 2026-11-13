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
/// startup and paid the full bring-up cost every time. This backend keeps its engines warm across many opens:
/// <see cref="OpenAsync"/> leases one (<see cref="LeaseEngine"/>), posts <c>SetSource</c> for the new track (a live
/// source switch — no teardown, no rebuild), and hands the resulting <see cref="MfMediaSession"/> a
/// <see cref="ReturnEngine"/> callback as its release action. When that session is disposed (a track switch, or the
/// player itself closing), the engine is paused and returned to the pool instead of destroyed — a later
/// <see cref="OpenAsync"/> reuses it. Sequencing: <c>MediaPlayer.OpenAsync</c> already awaits the OLD session's
/// <c>DisposeAsync</c> before calling <see cref="OpenAsync"/> on this backend, so <see cref="ReturnEngine"/>
/// happens-before the next <see cref="LeaseEngine"/> — there is never a race between a return and the next lease.
/// A <see cref="VideoEngineFlags.Faulted"/> engine (an unrecoverable bring-up failure) is discarded and rebuilt at the
/// NEXT lease, never reused. Two <see cref="FluentGpu.Media.MediaPlayer"/>s sharing one <see cref="MfMediaPlayer"/>
/// concurrently is an edge case, not the steady state: a lease that finds no free engine gets a throwaway one instead
/// of fighting a warm one's single-writer ownership (documented decision — plan §8 risks).</para>
/// <para><b>The held picture across a switch: a warm PAIR.</b> An in-place switch keeps the element's slot on the outgoing
/// source's last frame until the incoming source's first frame (<c>MediaPlayerElement</c>'s hold,
/// <see cref="VideoBinding.ShowsOtherPicture"/>). That frame lives in the outgoing engine's own swap chain, and a media engine
/// clears its swap chain the moment it is given another source: one engine reused for the next track showed the slot black
/// from the switch to the successor's first frame. So the engine whose picture the slot shows is never the one the next open
/// takes: returned by a session that presented, it HOLDS (paused, its source kept, its last frame on its swap chain), and the
/// next open leases the pair's other engine (built at the first switch). The holder is detached once its successor's session
/// has presented its own first frame (<see cref="EnginePresented"/>), a <see cref="SwapchainHandleLedger.DefaultGraceMs"/>
/// later: the render thread has bound the successor's surface by then. The two engines alternate from track to track, and
/// each one is torn down <see cref="WarmIdleDisposeMs"/> after its last return.</para>
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

    /// <summary>One engine of the warm pair.</summary>
    private sealed class Pooled(IVideoEngine engine)
    {
        public readonly IVideoEngine Engine = engine;
        /// <summary>A live session owns it.</summary>
        public bool Leased;
        /// <summary>The session leased on it presented its own first frame (bound it to a slot).</summary>
        public bool Presented;
        /// <summary>Returned by a session that presented, and not detached: its last frame is the picture a slot holds until a
        /// successor presents. Never leased while it holds.</summary>
        public bool Holds;
        /// <summary>Tears it down <see cref="WarmIdleDisposeMs"/> after its return unless it is leased again.</summary>
        public Timer? Idle;
        /// <summary>Detaches it once a successor presented (the hand-over's grace).</summary>
        public Timer? Release;
    }

    /// <summary>The warm pair: at most two pooled engines (null = an empty place).</summary>
    private readonly Pooled?[] _pool = new Pooled?[2];
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

    /// <summary>Lease an engine of the warm pair and mark it leased: a free one that does not hold a slot's picture, else the
    /// pair's second engine (built now), else (both are leased, or one is leased and the other holds) a throwaway engine. A
    /// faulted pooled engine is discarded first. A discarded engine is disposed OFF the lock, on the pool: its dispose joins the
    /// engine thread (bounded at 2 s) and MF shuts down on it, and the caller may be the UI thread, with every concurrent lease
    /// queued behind the lock.</summary>
    private IVideoEngine LeaseEngine()
    {
        IVideoEngine? discarded = null;
        IVideoEngine leased;
        lock (_engineLock)
        {
            Pooled? pick = null;
            int free = -1;
            for (int i = 0; i < _pool.Length; i++)
            {
                Pooled? p = _pool[i];
                if (p is not null && !p.Leased && discarded is null && (p.Engine.Snapshot.Flags & VideoEngineFlags.Faulted) != 0)
                {
                    Diag.Line("[video] engine.discard reason=faulted");   // F197: why an engine goes away, next to its engine.destroy line
                    StopTimers(p);
                    discarded = p.Engine;
                    _pool[i] = p = null;
                }
                if (p is null) { if (free < 0) free = i; continue; }
                if (pick is null && !p.Leased && !p.Holds) pick = p;
            }
            if (pick is null && free >= 0)
            {
                pick = new Pooled(_engineFactory());
                pick.Engine.Start();
                _pool[free] = pick;
            }
            if (pick is null)
            {
                Diag.Line("[video] engine.extra reason=concurrent-lease (an engine beside the warm pair)");
                leased = _engineFactory();
                leased.Start();
            }
            else
            {
                StopTimers(pick);
                pick.Leased = true;
                pick.Presented = false;
                leased = pick.Engine;
            }
        }
        if (discarded is not null) DisposeOffLock(discarded);
        return leased;
    }

    private static void StopTimers(Pooled p)
    {
        p.Idle?.Change(Timeout.Infinite, Timeout.Infinite);
        p.Release?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private Pooled? Find(IVideoEngine engine)
    {
        foreach (Pooled? p in _pool)
            if (p is not null && ReferenceEquals(p.Engine, engine)) return p;
        return null;
    }

    /// <summary>Dispose an engine on the pool, never on the caller: the dispose joins the engine's MTA thread (bounded at
    /// 2 s) after MF's shutdown, which must not freeze a UI-thread caller or hold <see cref="_engineLock"/>.</summary>
    private static void DisposeOffLock(IVideoEngine engine) => _ = Task.Run(engine.Dispose);

    /// <summary>Called by a session (UI thread) the first time it binds its own swap chain with its first frame: that engine's
    /// picture is the one on screen now, so an engine still holding the previous source's last frame for the slot is let go,
    /// <see cref="SwapchainHandleLedger.DefaultGraceMs"/> later (the render thread binds the successor's surface within a few
    /// turns, and the holder's detach clears its swap chain).</summary>
    private void EnginePresented(IVideoEngine engine)
    {
        lock (_engineLock)
        {
            if (Find(engine) is { } presented) presented.Presented = true;
            foreach (Pooled? p in _pool)
            {
                if (p is null || !p.Holds || ReferenceEquals(p.Engine, engine)) continue;
                p.Holds = false;
                Pooled holder = p;
                holder.Release ??= new Timer(_ => DetachIfIdle(holder), null, Timeout.Infinite, Timeout.Infinite);
                holder.Release.Change(SwapchainHandleLedger.DefaultGraceMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>Timer callback: the hand-over's grace ended; detach the old holder unless it was leased again meanwhile (that
    /// lease's own SetSource replaces its source, and a detach posted after it would unload the new one).</summary>
    private void DetachIfIdle(Pooled holder)
    {
        lock (_engineLock)
        {
            if (holder.Leased || holder.Holds || !ReferenceEquals(Find(holder.Engine), holder)) return;
            holder.Engine.PostDetach();
        }
        Diag.Line("[video] engine.detach reason=handover (its successor presented)");
    }

    /// <summary>Called from <see cref="MfMediaSession.DisposeAsync"/> (any thread; non-blocking — every step is a post). A
    /// pooled engine is paused and kept warm: one whose session presented HOLDS its last frame for the slot (see the class
    /// remarks) until a successor presents; one whose session never presented is detached at once. Either way its idle
    /// teardown is armed. A throwaway engine is disposed outright, off the lock and off the caller.</summary>
    private void ReturnEngine(IVideoEngine engine)
    {
        Pooled? p;
        lock (_engineLock)
        {
            p = Find(engine);
            if (p is not null)
            {
                p.Leased = false;
                p.Engine.Commands.Post(VideoCommandKind.Transport, a: 0);   // pause — a warm-parked engine does not play
                p.Holds = p.Presented;
                if (!p.Holds) p.Engine.PostDetach();
                // Detaching releases the SOURCE, not the engine: the D3D11 device, the DXGI manager, the MF engine and its
                // surfaces all stay resident. The idle teardown gives that memory back to an app that stops watching video,
                // while a track skip (which re-leases within seconds) still never pays a rebuild.
                Pooled idle = p;
                idle.Idle ??= new Timer(_ => DisposeIfIdle(idle), null, Timeout.Infinite, Timeout.Infinite);
                idle.Idle.Change(_warmIdleMs, Timeout.Infinite);
            }
        }
        if (p is null) DisposeOffLock(engine);
    }

    /// <summary>Timer callback: tear a pooled engine down if nothing leased it in the meantime, OUTSIDE the lock (the dispose
    /// joins the engine thread and can block; a concurrent lease must not wait behind it).</summary>
    private void DisposeIfIdle(Pooled p)
    {
        lock (_engineLock)
        {
            if (p.Leased) return;   // re-leased between the timer firing and this lock: leave it warm
            int i = Array.IndexOf(_pool, p);
            if (i < 0) return;
            _pool[i] = null;
            p.Holds = false;
            p.Release?.Dispose();
            p.Idle?.Dispose();
        }
        Diag.Line($"[video] engine.discard reason=idle afterMs={_warmIdleMs}");
        p.Engine.Dispose();
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

        // A local clip that loops forever plays as a GAPLESS loop: its sample tables repeated on one continuous timeline (Mp4LoopRemux:
        // the engine's own loop seeks at the source's end and flushes the clip's last frames, a visible hitch every loop). Prepared off the
        // caller; a file it cannot repeat keeps the engine's loop.
        bool loop = IsInfiniteLoop(source);
        double seamPeriod = 0;
        if (loop && IsLocalFile(url) && await Task.Run(() => Mp4LoopRemux.TryPrepare(url), ct).ConfigureAwait(false) is { } gapless)
        {
            url = gapless.Path;
            seamPeriod = gapless.ClipSeconds;
        }

        ct.ThrowIfCancellationRequested();   // before an engine is leased; nothing below blocks

        IVideoEngine engine = LeaseEngine();
        int epoch = engine.PostSetSource(url);   // fire-and-forget: no Task.Run, no block, no synchronous MF throw

        // A media element does not loop by default (the M3 harness kept a live frame via loop), but a source the caller
        // explicitly wrapped in .Loop() must. Only an INFINITE loop maps onto the MF media engine, whose loop flag is a
        // bool with no repeat count — a finite count would silently become infinite, so it stays unlooped instead.
        // The gapless loop keeps it on as the backstop at the end of its repetitions, and names its clip length (the seam probe's period).
        engine.Commands.Post(VideoCommandKind.Loop, a: seamPeriod, i: loop ? 1 : 0);
        // The play/pause intent goes out WITH the source: MF accepts Play() before the source resolves, and the Transport
        // slot is last-wins, so this overwrites the pause ReturnEngine left behind (a warm engine drains Detach, SetSource,
        // then Transport) instead of leaving the engine paused until the session's first post-metadata pump re-asserts it.
        engine.Commands.Post(VideoCommandKind.Transport, a: opts.StartPaused ? 0 : 1);

        var session = new MfMediaSession(engine, epoch, opts, null, ReturnEngine, EnginePresented);
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

    /// <summary>A path on disk (not an http(s) or other scheme URL).</summary>
    internal static bool IsLocalFile(string url)
        => !url.Contains("://", StringComparison.Ordinal) && System.IO.Path.IsPathFullyQualified(url) && System.IO.File.Exists(url);

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

    /// <summary>Tear down the warm pair (app shutdown / backend replacement). Off-thread: <see cref="IVideoEngine.Dispose"/>
    /// joins its MTA thread (bounded at 2 s) and this must never block the caller.</summary>
    public async ValueTask DisposeAsync()
    {
        Pooled?[] pool;
        lock (_engineLock)
        {
            pool = (Pooled?[])_pool.Clone();
            Array.Clear(_pool);
        }
        foreach (Pooled? p in pool)
        {
            if (p is null) continue;
            p.Idle?.Dispose();
            p.Release?.Dispose();
            await Task.Run(p.Engine.Dispose).ConfigureAwait(false);
        }
    }
}
