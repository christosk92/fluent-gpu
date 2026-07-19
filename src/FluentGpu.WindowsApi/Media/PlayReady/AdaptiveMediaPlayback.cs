using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.WindowsApi.Notifications;       // ToastActivatorClassFactory.ComWrappers
using TerraFX.Interop.Windows;                   // Pointer<T>
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;    // __uuidof<T>
using static TerraFX.Interop.WinRT.WinRT;        // RoInitialize, RO_INIT_TYPE

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>Apartment initialization for the native PlayReady path. Mirrors <c>SystemMediaControls</c>' tolerant
/// <c>RoInitialize</c> (S_FALSE / RPC_E_CHANGED_MODE are benign), but initializes the MULTITHREADED apartment MediaPlayer
/// wants (<c>docs/plans/video-drm-layer-design.md §12.6</c>).</summary>
[SupportedOSPlatform("windows10.0.10240.0")]
public static class PlayReadyRuntime
{
    private const int S_FALSE = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    /// <summary><c>RoInitialize(RO_INIT_MULTITHREADED)</c>, tolerating already-initialized apartments.</summary>
    public static void InitializeMultithreaded()
    {
        int hr = RoInitialize(RO_INIT_TYPE.RO_INIT_MULTITHREADED);
        if (hr < 0 && hr != S_FALSE && hr != RPC_E_CHANGED_MODE)
            WinRtInterop.ThrowIfFailed(hr, "RoInitialize(MULTITHREADED)");
    }
}

/// <summary>
/// A ready-to-play WinRT <c>MediaPlaybackItem</c> wrapping an <c>AdaptiveMediaSource</c> (built by
/// <see cref="AdaptiveMediaSourceFactory"/>). Hand it to <see cref="PlayReadyMediaPlayer.SetSource"/> LAST (after the
/// protection manager and surface size are set). Dispose releases the underlying WinRT item.
/// </summary>
[SupportedOSPlatform("windows10.0.10240.0")]
public sealed unsafe class AdaptivePlaybackItem : IDisposable
{
    private IMediaPlaybackItem* _item;

    internal AdaptivePlaybackItem(IMediaPlaybackItem* item) => _item = item;

    internal IMediaPlaybackItem* Item => _item;

    /// <summary>Release the WinRT item. Idempotent.</summary>
    public void Dispose()
    {
        if (_item != null) { _item->Release(); _item = null; }
    }
}

/// <summary>
/// Builds an <see cref="AdaptivePlaybackItem"/> from a DASH MPD URI: <c>AdaptiveMediaSource.CreateFromUriAsync</c> (the OS
/// does DASH/ABR/segment-fetching) → <c>MediaSource.CreateFromAdaptiveMediaSource</c> →
/// <c>MediaPlaybackItem</c>. The async creation is awaited via <see cref="WinRtAsync"/> (status polling).
/// </summary>
[SupportedOSPlatform("windows10.0.10240.0")]
public static class AdaptiveMediaSourceFactory
{
    /// <summary>Create a playback item from a DASH MPD URL. Throws if AMS creation does not report
    /// <c>Success</c>.</summary>
    public static async Task<AdaptivePlaybackItem> CreateFromUriAsync(string mpdUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mpdUrl);

        nint asyncOp = StartCreateFromUri(mpdUrl);
        nint resultPtr;
        try
        {
            resultPtr = await WinRtAsync.AwaitOperationResultAsync(asyncOp, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleasePtr(asyncOp);
        }

        try
        {
            return BuildItem(resultPtr);
        }
        finally
        {
            ReleasePtr(resultPtr);
        }
    }

    private static unsafe void ReleasePtr(nint p)
    {
        if (p != 0) ((IInspectable*)p)->Release();
    }

    private static unsafe nint StartCreateFromUri(string mpdUrl)
    {
        IUriRuntimeClass* uri = WinRtInterop.CreateUri(mpdUrl);
        try
        {
            IAdaptiveMediaSourceStatics* statics =
                WinRtInterop.GetActivationFactory<IAdaptiveMediaSourceStatics>(PlayReadyGuids.RuntimeClass_AdaptiveMediaSource);
            try
            {
                IAsyncOperation<Pointer<IAdaptiveMediaSourceCreationResult>>* op = null;
                WinRtInterop.ThrowIfFailed(statics->CreateFromUriAsync(uri, &op), "AdaptiveMediaSource.CreateFromUriAsync");
                return (nint)op;
            }
            finally { statics->Release(); }
        }
        finally { uri->Release(); }
    }

    private static unsafe AdaptivePlaybackItem BuildItem(nint resultPtr)
    {
        var result = (IAdaptiveMediaSourceCreationResult*)resultPtr;

        AdaptiveMediaSourceCreationStatus status;
        WinRtInterop.ThrowIfFailed(result->get_Status(&status), "AdaptiveMediaSourceCreationResult.get_Status");
        if (status != AdaptiveMediaSourceCreationStatus.AdaptiveMediaSourceCreationStatus_Success)
            throw new InvalidOperationException($"AdaptiveMediaSource creation failed: {status}.");

        IAdaptiveMediaSource* ams = null;
        WinRtInterop.ThrowIfFailed(result->get_MediaSource(&ams), "get_MediaSource");
        try
        {
            IMediaSourceStatics* msStatics =
                WinRtInterop.GetActivationFactory<IMediaSourceStatics>(PlayReadyGuids.RuntimeClass_MediaSource);
            try
            {
                IMediaSource2* ms = null;
                WinRtInterop.ThrowIfFailed(msStatics->CreateFromAdaptiveMediaSource(ams, &ms), "MediaSource.CreateFromAdaptiveMediaSource");
                try
                {
                    IMediaPlaybackItemFactory* itemFactory =
                        WinRtInterop.GetActivationFactory<IMediaPlaybackItemFactory>(PlayReadyGuids.RuntimeClass_MediaPlaybackItem);
                    try
                    {
                        IMediaPlaybackItem* item = null;
                        WinRtInterop.ThrowIfFailed(itemFactory->Create(ms, &item), "MediaPlaybackItem.Create");
                        return new AdaptivePlaybackItem(item);
                    }
                    finally { itemFactory->Release(); }
                }
                finally { ms->Release(); }
            }
            finally { msStatics->Release(); }
        }
        finally { ams->Release(); }
    }
}

/// <summary>
/// Owns a native <c>Windows.Media.Playback.MediaPlayer</c> and drives the ordering that defeats <c>0xC00D715B</c>: attach
/// the PlayReady <see cref="PlayReadyProtectionManager"/> and set the surface size FIRST, then set the source LAST so the
/// protected topology builds with the trust chain already present (<c>docs/plans/video-drm-layer-design.md §3.2/§5</c>).
/// </summary>
[SupportedOSPlatform("windows10.0.10240.0")]
public sealed unsafe class PlayReadyMediaPlayer : IDisposable
{
    private static readonly StrategyBasedComWrappers ComWrappers = ToastActivatorClassFactory.ComWrappers;

    private IMediaPlayer* _player;
    private MediaFailedHandler? _failedHandler;
    private nint _failedUnknown;
    private EventRegistrationToken _failedToken;
    private bool _disposed;

    /// <summary>Raised (on an MF worker thread) on <c>MediaFailed</c>: (extended HRESULT, <c>MediaPlayerError</c> ordinal,
    /// message). Watch for the extended HRESULT to distinguish <c>0xC00D715B</c> from other failures. Best-effort — see
    /// <see cref="MediaFailedHandler"/> (its parameterized IID is derived).</summary>
    public event Action<int, int, string>? MediaFailed;

    private PlayReadyMediaPlayer(IMediaPlayer* player) => _player = player;

    /// <summary>Activate a <c>MediaPlayer</c> and best-effort subscribe <c>MediaFailed</c>.</summary>
    public static PlayReadyMediaPlayer Activate()
    {
        IMediaPlayer* player = WinRtInterop.ActivateInstance<IMediaPlayer>(PlayReadyGuids.RuntimeClass_MediaPlayer);
        var self = new PlayReadyMediaPlayer(player);
        self.TrySubscribeMediaFailed();
        return self;
    }

    /// <summary>Attach the PlayReady protection manager (via <c>IMediaPlayerSource.put_ProtectionManager</c>) — do this
    /// BEFORE <see cref="SetSource"/>.</summary>
    public void SetProtectionManager(PlayReadyProtectionManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        IMediaPlayerSource* src = WinRtInterop.QueryInterface<IMediaPlayerSource>((IInspectable*)_player, "IMediaPlayerSource");
        try
        {
            WinRtInterop.ThrowIfFailed(src->put_ProtectionManager((IMediaProtectionManager*)manager.ManagerPtr), "put_ProtectionManager");
        }
        finally { src->Release(); }
    }

    /// <summary>Tell Media Foundation the render-target size (<c>IMediaPlayer4.SetSurfaceSize</c>) — there is no XAML
    /// element to infer it. Do this BEFORE <see cref="SetSource"/>.</summary>
    public void SetSurfaceSize(uint width, uint height)
    {
        IMediaPlayer4* p4 = WinRtInterop.QueryInterface<IMediaPlayer4>((IInspectable*)_player, "IMediaPlayer4");
        try
        {
            var size = new Size { Width = width, Height = height };
            WinRtInterop.ThrowIfFailed(p4->SetSurfaceSize(size), "SetSurfaceSize");
        }
        finally { p4->Release(); }
    }

    /// <summary>Set the source LAST (<c>IMediaPlayerSource2.put_Source</c>). The topology now builds with the protection
    /// manager present — this ordering is the structural fix for <c>0xC00D715B</c>.</summary>
    public void SetSource(AdaptivePlaybackItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        IMediaPlaybackSource* source = WinRtInterop.QueryInterface<IMediaPlaybackSource>((IInspectable*)item.Item, "IMediaPlaybackSource");
        try
        {
            IMediaPlayerSource2* src2 = WinRtInterop.QueryInterface<IMediaPlayerSource2>((IInspectable*)_player, "IMediaPlayerSource2");
            try
            {
                WinRtInterop.ThrowIfFailed(src2->put_Source(source), "put_Source");
            }
            finally { src2->Release(); }
        }
        finally { source->Release(); }
    }

    /// <summary>Begin playback (<c>IMediaPlayer.Play</c>).</summary>
    public void Play() => WinRtInterop.ThrowIfFailed(_player->Play(), "MediaPlayer.Play");

    /// <summary>Read the current playback state (<c>IMediaPlayer3.PlaybackSession.PlaybackState</c>).</summary>
    public MediaPlaybackState GetPlaybackState()
    {
        IMediaPlayer3* p3 = WinRtInterop.QueryInterface<IMediaPlayer3>((IInspectable*)_player, "IMediaPlayer3");
        try
        {
            IMediaPlaybackSession* session = null;
            WinRtInterop.ThrowIfFailed(p3->get_PlaybackSession(&session), "get_PlaybackSession");
            try
            {
                MediaPlaybackState state;
                WinRtInterop.ThrowIfFailed(session->get_PlaybackState(&state), "get_PlaybackState");
                return state;
            }
            finally { session->Release(); }
        }
        finally { p3->Release(); }
    }

    private void TrySubscribeMediaFailed()
    {
        try
        {
            _failedHandler = new MediaFailedHandler((hr, err, msg) => MediaFailed?.Invoke(hr, err, msg));
            _failedUnknown = ComWrappers.GetOrCreateComInterfaceForObject(_failedHandler, CreateComInterfaceFlags.None);
            EventRegistrationToken token;
            int hr = _player->add_MediaFailed(
                (ITypedEventHandler<Pointer<IMediaPlayer>, Pointer<IMediaPlayerFailedEventArgs>>*)_failedUnknown, &token);
            if (hr < 0)
            {
                // E_NOINTERFACE here ⇒ suspect the derived parameterized IID in MediaFailedHandler. Non-fatal.
                Marshal.Release(_failedUnknown);
                _failedUnknown = 0;
                _failedHandler = null;
                return;
            }
            _failedToken = token;
        }
        catch
        {
            _failedHandler = null;
        }
    }

    /// <summary>Unhook <c>MediaFailed</c> and release the player. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_player != null)
        {
            if (_failedUnknown != 0)
            {
                _player->remove_MediaFailed(_failedToken);
                Marshal.Release(_failedUnknown);
                _failedUnknown = 0;
            }
            _player->Release();
            _player = null;
        }
        _failedHandler = null;
    }
}
