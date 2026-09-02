using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
/// Cancellation before the source is posted still throws (manifest load only — nothing else here is awaited).</para>
/// </summary>
public sealed class MfMediaPlayer : IMediaBackend, IAsyncDisposable
{
    private static readonly HttpClient s_http = new();
    private readonly Func<IVideoEngine> _engineFactory;
    private readonly IMediaBackend? _drmBackend;
    private readonly HttpClient _http;

    private readonly object _engineLock = new();
    private IVideoEngine? _warm;
    private bool _leased;

    /// <summary>Create the production MF backend (a warm <see cref="VideoMediaEngine"/> is built on first open); no DRM
    /// support.</summary>
    public MfMediaPlayer() : this(static () => new VideoMediaEngine(), null) { }

    /// <summary>Create the MF backend with a protected (DRM) backend attached — a <see cref="MediaSource"/> carrying a
    /// <see cref="DrmConfig"/> routes to it (the native PlayReady CDM path); clear video keeps the proven engine path.
    /// The DRM backend is an Engine-layer <see cref="IMediaBackend"/> so this project stays decoupled from the concrete
    /// (WindowsApi) implementation — the app composition root injects it.</summary>
    public MfMediaPlayer(IMediaBackend drmBackend) : this(static () => new VideoMediaEngine(), drmBackend) { }

    /// <summary>Test/DI seam: supply a video-engine factory (a fake in unit tests) and an optional DRM backend.</summary>
    internal MfMediaPlayer(Func<IVideoEngine> engineFactory, IMediaBackend? drmBackend = null, HttpClient? http = null)
    {
        _engineFactory = engineFactory;
        _drmBackend = drmBackend;
        _http = http ?? s_http;
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
    /// ownership — the warm slot always belongs to at most one live session.</summary>
    private IVideoEngine LeaseEngine()
    {
        lock (_engineLock)
        {
            bool faulted = _warm is { } w && (w.Snapshot.Flags & VideoEngineFlags.Faulted) != 0;
            if (_warm is null || faulted)
            {
                _warm?.Dispose();
                _warm = _engineFactory();
                _warm.Start();
            }
            if (_leased)
            {
                var extra = _engineFactory();
                extra.Start();
                return extra;
            }
            _leased = true;
            return _warm!;
        }
    }

    /// <summary>Called from <see cref="MfMediaSession.DisposeAsync"/> (any thread; non-blocking — pause + detach are
    /// both posts). The warm engine goes back to the pool for the NEXT open; a throwaway (a second concurrent lease, or
    /// one leased while a rebuild already replaced <see cref="_warm"/>) is disposed outright instead.</summary>
    private void ReturnEngine(IVideoEngine engine)
    {
        lock (_engineLock)
        {
            if (!ReferenceEquals(engine, _warm)) { engine.Dispose(); return; }
            _leased = false;
            engine.Commands.Post(VideoCommandKind.Transport, a: 0);   // pause — a warm-parked engine does not play
            engine.PostDetach();
        }
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

        AdaptiveManifest? manifest = null;
        if (source is AdaptiveSource adaptive)
            manifest = await AdaptiveManifestLoader.LoadAsync(_http, adaptive,
                adaptive.Network ?? opts.Network, ct).ConfigureAwait(false);

        string url = ResolveUrl(source) ?? throw new NotSupportedException(
            "MfMediaPlayer supports a file path or a URL source (FromFile/FromUri).");

        ct.ThrowIfCancellationRequested();   // the manifest load above is the only awaited step; nothing below blocks

        IVideoEngine engine = LeaseEngine();
        int epoch = engine.PostSetSource(url);   // fire-and-forget: no Task.Run, no block, no synchronous MF throw

        // A media element does not loop by default (the M3 harness kept a live frame via loop), but a source the caller
        // explicitly wrapped in .Loop() must. Only an INFINITE loop maps onto the MF media engine, whose loop flag is a
        // bool with no repeat count — a finite count would silently become infinite, so it stays unlooped instead.
        engine.Commands.Post(VideoCommandKind.Loop, i: IsInfiniteLoop(source) ? 1 : 0);
        if (opts.StartPaused) engine.Commands.Post(VideoCommandKind.Transport, a: 0);

        return new MfMediaSession(engine, epoch, opts, manifest, ReturnEngine);
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
        lock (_engineLock) { warm = _warm; _warm = null; _leased = false; }
        if (warm is not null) await Task.Run(warm.Dispose).ConfigureAwait(false);
    }
}
