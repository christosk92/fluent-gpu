using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Pal;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The Windows Media-Foundation video backend (spec §9.1) — the <see cref="IMediaBackend"/> registered for
/// <see cref="MediaKind.MfVideoOrFile"/>. <see cref="OpenAsync"/> stands up a <see cref="VideoMediaEngine"/>
/// (<c>IMFMediaEngineEx</c> windowless swapchain, the PROVEN clear-video path) over a source URL/path and wraps it in an
/// <see cref="MfMediaSession"/>. Clear (unprotected) video only in M1; DRM (a protected surface handle + a
/// <see cref="MediaOpenOptions.LicenseRelay"/>) attaches at the same <c>BindSurfaceHandle</c> point in a later milestone.
/// <para>The blocking engine startup (its MTA thread creation + <c>SetSource</c>) runs on a threadpool thread so
/// <see cref="OpenAsync"/> never blocks the UI thread; every ComPtr stays confined to the engine's own MTA thread.</para>
/// </summary>
public sealed class MfMediaPlayer : IMediaBackend
{
    private readonly Func<IVideoEngine> _engineFactory;
    private readonly IMediaBackend? _drmBackend;

    /// <summary>Create the production MF backend (each open builds a real <see cref="VideoMediaEngine"/>); no DRM support.</summary>
    public MfMediaPlayer() : this(static () => new VideoMediaEngine(), null) { }

    /// <summary>Create the MF backend with a protected (DRM) backend attached — a <see cref="MediaSource"/> carrying a
    /// <see cref="DrmConfig"/> routes to it (the native PlayReady CDM path); clear video keeps the proven engine path.
    /// The DRM backend is an Engine-layer <see cref="IMediaBackend"/> so this project stays decoupled from the concrete
    /// (WindowsApi) implementation — the app composition root injects it.</summary>
    public MfMediaPlayer(IMediaBackend drmBackend) : this(static () => new VideoMediaEngine(), drmBackend) { }

    /// <summary>Test/DI seam: supply a video-engine factory (a fake in unit tests) and an optional DRM backend.</summary>
    internal MfMediaPlayer(Func<IVideoEngine> engineFactory, IMediaBackend? drmBackend = null)
    {
        _engineFactory = engineFactory;
        _drmBackend = drmBackend;
        Capabilities = new(SupportsVideo: true, SupportsAudioGraph: false, SupportsDrm: drmBackend is not null)
        {
            // MF resolves the container/codec on open; report the common clear-video families as query-time supported.
            IsSupported = static ct => ct.Video is CodecId.None or CodecId.H264 or CodecId.Hevc or CodecId.Av1 or CodecId.Vp9,
        };
    }

    /// <inheritdoc/>
    public MediaCapabilities Capabilities { get; }

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

        var engine = _engineFactory();
        int hr;
        try
        {
            // Blocking engine bring-up (MTA thread + SetSource) off the UI thread; honor cancellation before/after.
            hr = await Task.Run(() => engine.Initialize(url), ct).ConfigureAwait(false);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
        if (hr < 0)
        {
            engine.Dispose();
            throw new InvalidOperationException($"Media Foundation failed to open the source (hr=0x{(uint)hr:X8}).");
        }
        if (ct.IsCancellationRequested)
        {
            engine.Dispose();
            ct.ThrowIfCancellationRequested();
        }

        // A media element does not loop by default (the M3 harness kept a live frame via loop); honor StartPaused.
        engine.SetLoop(false);
        if (opts.StartPaused) engine.Pause();

        return new MfMediaSession(engine, opts);
    }

    /// <summary>Extract the MF source URL from a <see cref="MediaSource"/> (a local path is passed through; MF accepts
    /// both file paths and http(s) URLs). Returns null for a shape MF can't open by URL.</summary>
    internal static string? ResolveUrl(MediaSource source) => source switch
    {
        FileSource f => f.Path,
        UriSource u => u.Url,
        ClipSource c => ResolveUrl(c.Inner),
        LoopSource l => ResolveUrl(l.Inner),
        _ => null,
    };
}
