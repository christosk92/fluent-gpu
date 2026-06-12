using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using FluentGpu.WindowsApi.Notifications;   // HStringHandle + ToastActivatorClassFactory.ComWrappers (shared cold-COM helpers)
using TerraFX.Interop.Windows;              // Pointer<T>, HWND, HRESULT
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.WinRT.WinRT;
using static TerraFX.Interop.Windows.Windows;   // __uuidof<T>, RoInitialize, RO_INIT_TYPE
// The WinRT MediaPlayback* enums collide by simple name with this pillar's public MediaPlaybackStatus; alias the
// native ones so the public API surface (FluentGpu.WindowsApi.Media.MediaPlaybackStatus) stays unambiguous.
using MediaPlaybackStatusWinRT = TerraFX.Interop.WinRT.MediaPlaybackStatus;
using MediaPlaybackTypeWinRT = TerraFX.Interop.WinRT.MediaPlaybackType;

namespace FluentGpu.WindowsApi.Media;

/// <summary>
/// The System Media Transport Controls (SMTC) integration a music app needs: it wires this process's window to the OS
/// media surfaces (the now-playing flyout, the lock screen, and the hardware media keys / headset buttons), pushes the
/// playback state and now-playing metadata + album art, and raises <see cref="ButtonPressed"/> when the user hits a
/// transport button. Hand-bound through <c>TerraFX.Interop.WinRT</c> vtable structs with zero CsWinRT, zero
/// <c>ComWrappers</c> on the call-out path, and zero reflection — the exact call-OUT pattern proven by
/// <c>Notifications/ToastNotifier.cs</c> (the AOT spike's WORKS-AOT verdict).
/// </summary>
/// <remarks>
/// <para>
/// <b>Acquisition (desktop interop).</b> SMTC is window-bound for a Win32 app: the activation factory of
/// <c>Windows.Media.SystemMediaTransportControls</c> implements <c>ISystemMediaTransportControlsInterop</c> (interop
/// IID <c>DDB0472D-C911-4A1F-86D9-DC3D71A95F5A</c>, declared in <c>windows.media.h</c> and projected by
/// <c>TerraFX.Interop.WinRT.ISystemMediaTransportControlsInterop</c>). <see cref="GetForWindow(nint)"/> does
/// <c>RoGetActivationFactory(&quot;Windows.Media.SystemMediaTransportControls&quot;, IID_ISystemMediaTransportControlsInterop)</c>
/// → <c>interop-&gt;GetForWindow(hwnd, IID_ISystemMediaTransportControls)</c>, then caches the returned
/// <c>ISystemMediaTransportControls</c> (AddRef-owned) for the instance's lifetime. The factory itself is process-stable
/// but the controls object is per-window — there is one logical SMTC per top-level window.
/// </para>
/// <para>
/// <b>Thread &amp; HWND ownership.</b> The <c>hwnd</c> must be a top-level window this process owns; pass the
/// real FluentGpu window handle (the gallery reads it off the window's <c>NativeHandle</c> — do NOT invent one on the
/// Engine seam). Construct and drive this from the UI thread that owns the HWND. SMTC keys its session off the window,
/// so a destroyed/foreign HWND yields a controls object the OS ignores. The cross-thread <see cref="ButtonPressed"/>
/// callback (see below) is the only member that touches another thread.
/// </para>
/// <para>
/// <b>Button callbacks arrive on an arbitrary OS thread.</b> The <c>add_ButtonPressed</c> handler
/// (<see cref="MediaButtonHandler"/>) is invoked on an SMTC worker thread, not the UI thread. This class routes the
/// raised event through <see cref="ButtonDispatcher"/> if the host installs one (e.g. a <c>PostMessage</c> hop to the
/// <c>"FluentGpuWindow"</c> loop); otherwise it raises <see cref="ButtonPressed"/> inline on the OS thread (correct
/// only for a thread-safe handler). Register handlers and set the dispatcher BEFORE relying on affinity.
/// </para>
/// <para>
/// <b>WinRT boolean ABI.</b> The <c>get/put_Is*Enabled</c> and <c>get/put_IsEnabled</c> properties take a
/// <c>boolean</c> projected as a 1-byte value (TerraFX <c>System.Byte</c>): 1 = true, 0 = false. The helpers here
/// convert with <c>(byte)(value ? 1 : 0)</c>.
/// </para>
/// <para>
/// <b>Album art.</b> <see cref="UpdateDisplay"/> sets the thumbnail via
/// <c>IRandomAccessStreamReferenceStatics.CreateFromUri</c> over a <c>Windows.Foundation.Uri</c> built from a
/// <c>file:///</c> or <c>http(s)://</c> string (same dual-source model as the toast image path). A null thumbnail is
/// skipped cleanly (no <c>put_Thumbnail</c> call), leaving any previous art in place; pass an explicit value to replace
/// it. RISK: <c>CreateFromUri</c> + the async <c>OpenReadAsync</c> the OS performs to fetch the art are not exercised
/// by the AOT spike — the showcase verifies a real <c>https</c> album-art URL renders in the flyout.
/// </para>
/// <para>
/// <b>AOT/CA1416.</b> The csproj targets a bare <c>net10.0</c> TFM, so the WinRT SMTC types (annotated
/// <c>[SupportedOSPlatform("windows8.0")]</c>) would warn under <c>TreatWarningsAsErrors</c>; this type is annotated
/// to match, keeping the analyzer silent without per-call suppression.
/// </para>
/// <para>
/// References:
/// <list type="bullet">
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/systemmediatransportcontrolsinterop/nn-systemmediatransportcontrolsinterop-isystemmediatransportcontrolsinterop">ISystemMediaTransportControlsInterop</see></item>
/// <item><see href="https://learn.microsoft.com/en-us/uwp/api/windows.media.systemmediatransportcontrolsdisplayupdater">SystemMediaTransportControlsDisplayUpdater</see></item>
/// <item><see href="https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/system-media-transport-controls">Integrate with the SMTC</see></item>
/// </list>
/// </para>
/// </remarks>
[SupportedOSPlatform("windows8.0")]
public sealed unsafe class SystemMediaControls : IDisposable
{
    private const string RuntimeClass_Smtc = "Windows.Media.SystemMediaTransportControls";
    private const string RuntimeClass_Uri = "Windows.Foundation.Uri";
    private const string RuntimeClass_RandomAccessStreamReference = "Windows.Storage.Streams.RandomAccessStreamReference";

    // Benign RoInitialize results (already initialized / changed apartment mode) — gate on FAILED, not != S_OK.
    private const int S_FALSE = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    private readonly object _gate = new();
    private readonly nint _hwnd;
    private bool _roInitialized;
    private bool _disposed;

    // Cached, AddRef-owned WinRT call-out pointers (released in Dispose).
    private ISystemMediaTransportControls* _smtc;
    private IRandomAccessStreamReferenceStatics* _streamRefStatics;
    private IUriRuntimeClassFactory* _uriFactory;

    // The implemented button-pressed delegate (the one "implement" side) + its registration token + CCW pointer.
    private MediaButtonHandler? _buttonHandler;
    private nint _buttonHandlerUnknown;     // the IUnknown* (CCW) we passed to add_ButtonPressed; we own one ref
    private EventRegistrationToken _buttonToken;

    /// <summary>The <see cref="StrategyBasedComWrappers"/> used to realize the button handler's native pointer — the
    /// sanctioned generated-COM marshaller (NOT forbidden subclassing), shared with the Notifications activator.</summary>
    private static readonly StrategyBasedComWrappers ComWrappers = ToastActivatorClassFactory.ComWrappers;

    private SystemMediaControls(nint hwnd) => _hwnd = hwnd;

    /// <summary>
    /// Acquire the SMTC for a top-level window this process owns. Runs the interop chain and caches the controls object.
    /// </summary>
    /// <param name="hwnd">The native window handle (HWND) — pass the real FluentGpu window handle; must be a top-level
    /// window owned by this process and the call must be made on the UI thread that owns it.</param>
    /// <returns>A live <see cref="SystemMediaControls"/> bound to <paramref name="hwnd"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="hwnd"/> is zero.</exception>
    /// <exception cref="InvalidOperationException">A WinRT step failed (e.g. the platform refused the interop).</exception>
    public static SystemMediaControls GetForWindow(nint hwnd)
    {
        if (hwnd == 0)
            throw new ArgumentException("A real top-level window handle is required.", nameof(hwnd));

        var controls = new SystemMediaControls(hwnd);
        controls.AcquireControls();
        return controls;
    }

    /// <summary>Whether the SMTC are enabled for this window (<c>get/put_IsEnabled</c>). The controls must be enabled
    /// for the OS to show the now-playing surface and deliver button presses; a media app sets this <see langword="true"/>
    /// while it owns playback and <see langword="false"/> when it relinquishes it.</summary>
    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                byte enabled;
                ThrowIfFailed(_smtc->get_IsEnabled(&enabled), "get_IsEnabled");
                return enabled != 0;
            }
        }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ThrowIfFailed(_smtc->put_IsEnabled(B(value)), "put_IsEnabled");
            }
        }
    }

    /// <summary>Set the playback status the OS reflects (<c>put_PlaybackStatus</c>). Keep this in lock-step with the
    /// app's transport so the flyout shows the correct play/pause glyph.</summary>
    public void SetPlaybackStatus(MediaPlaybackStatus status)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            // The public MediaPlaybackStatus values are defined to match the WinRT ordinals 1:1 (verified), so the cast
            // is exact: Closed=0,Changing=1,Stopped=2,Playing=3,Paused=4.
            ThrowIfFailed(_smtc->put_PlaybackStatus((MediaPlaybackStatusWinRT)(int)status), "put_PlaybackStatus");
        }
    }

    /// <summary>
    /// Enable/disable individual transport buttons (<c>put_IsPlayEnabled</c> &amp; friends). Only enabled buttons appear
    /// on the OS surface and raise <see cref="ButtonPressed"/>; a typical music app enables play+pause+next+previous and
    /// leaves stop off (set <paramref name="stop"/> true to surface it).
    /// </summary>
    public void SetEnabledButtons(bool play, bool pause, bool next, bool previous, bool stop = false)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfFailed(_smtc->put_IsPlayEnabled(B(play)), "put_IsPlayEnabled");
            ThrowIfFailed(_smtc->put_IsPauseEnabled(B(pause)), "put_IsPauseEnabled");
            ThrowIfFailed(_smtc->put_IsNextEnabled(B(next)), "put_IsNextEnabled");
            ThrowIfFailed(_smtc->put_IsPreviousEnabled(B(previous)), "put_IsPreviousEnabled");
            ThrowIfFailed(_smtc->put_IsStopEnabled(B(stop)), "put_IsStopEnabled");
        }
    }

    /// <summary>
    /// Push the now-playing metadata to the OS via the display updater: sets the media type to Music, fills the music
    /// title/artist/album fields, optionally sets the album-art thumbnail, then commits with <c>Update()</c>.
    /// </summary>
    /// <param name="title">Track title (required; empty clears it).</param>
    /// <param name="artist">Track artist.</param>
    /// <param name="albumTitle">Album name. RISK: WinRT <c>MusicProperties</c> exposes <c>AlbumArtist</c>, not an
    /// "album title" field — this maps <paramref name="albumTitle"/> to <c>put_AlbumArtist</c> (the closest semantic and
    /// what the flyout shows as the third line). Pass the album name here; pass null to leave it unset.</param>
    /// <param name="thumbnailFileOrUri">Album-art source — a <c>file:///</c> path or an <c>http(s)://</c> URL — or null
    /// to leave the current art untouched.</param>
    public void UpdateDisplay(string title, string artist, string? albumTitle = null, string? thumbnailFileOrUri = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(artist);

        lock (_gate)
        {
            ThrowIfDisposed();

            ISystemMediaTransportControlsDisplayUpdater* updater = null;
            IMusicDisplayProperties* music = null;
            try
            {
                ThrowIfFailed(_smtc->get_DisplayUpdater(&updater), "get_DisplayUpdater");

                // put_Type = Music BEFORE touching MusicProperties (the updater exposes the music view only for the
                // Music type; get_MusicProperties on a non-Music updater returns a stale/empty view).
                ThrowIfFailed(updater->put_Type(MediaPlaybackTypeWinRT.MediaPlaybackType_Music), "put_Type(Music)");

                ThrowIfFailed(updater->get_MusicProperties(&music), "get_MusicProperties");

                using (var hsTitle = new HStringHandle(title))
                    ThrowIfFailed(music->put_Title(hsTitle.Value), "put_Title");
                using (var hsArtist = new HStringHandle(artist))
                    ThrowIfFailed(music->put_Artist(hsArtist.Value), "put_Artist");
                if (albumTitle is not null)
                    using (var hsAlbum = new HStringHandle(albumTitle))
                        ThrowIfFailed(music->put_AlbumArtist(hsAlbum.Value), "put_AlbumArtist");

                // Thumbnail is optional; skip cleanly when null, leaving any previously-set art in place.
                if (thumbnailFileOrUri is not null)
                    SetThumbnail(updater, thumbnailFileOrUri);

                // Commit the staged display metadata to the OS surface.
                ThrowIfFailed(updater->Update(), "DisplayUpdater.Update");
            }
            finally
            {
                if (music != null) music->Release();
                if (updater != null) updater->Release();
            }
        }
    }

    /// <summary>Clear all now-playing metadata from the OS surface (<c>DisplayUpdater.ClearAll</c> + <c>Update</c>).</summary>
    public void ClearDisplay()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ISystemMediaTransportControlsDisplayUpdater* updater = null;
            try
            {
                ThrowIfFailed(_smtc->get_DisplayUpdater(&updater), "get_DisplayUpdater");
                ThrowIfFailed(updater->ClearAll(), "DisplayUpdater.ClearAll");
                ThrowIfFailed(updater->Update(), "DisplayUpdater.Update");
            }
            finally
            {
                if (updater != null) updater->Release();
            }
        }
    }

    /// <summary>
    /// Raised when the user presses a transport button on a system media surface. The producer fires on an arbitrary OS
    /// thread, so this is routed through <see cref="ButtonDispatcher"/> when one is installed; subscribe and set the
    /// dispatcher BEFORE relying on thread affinity. The <c>add_ButtonPressed</c> registration is made lazily on first
    /// subscription and torn down on the last unsubscribe (and on <see cref="Dispose"/>).
    /// </summary>
    public event Action<MediaButton>? ButtonPressed
    {
        add
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _buttonPressed += value;
                EnsureButtonHandlerRegistered();
            }
        }
        remove
        {
            lock (_gate)
            {
                _buttonPressed -= value;
                if (_buttonPressed is null)
                    RemoveButtonHandler();
            }
        }
    }
    private Action<MediaButton>? _buttonPressed;

    /// <summary>
    /// The marshaller the cross-thread button callback is routed through before <see cref="ButtonPressed"/> is raised.
    /// The host installs a delegate that posts to its UI thread (e.g. <c>PostMessage</c> to the <c>"FluentGpuWindow"</c>
    /// HWND). If left null, the callback raises the event inline on the OS thread.
    /// </summary>
    public Action<Action>? ButtonDispatcher { get; set; }

    // ── acquisition / WinRT plumbing ─────────────────────────────────────────────────────────────────────────────────

    private void AcquireControls()
    {
        lock (_gate)
        {
            EnsureRoInitialized();

            ISystemMediaTransportControlsInterop* interop = null;
            try
            {
                using (var hsClass = new HStringHandle(RuntimeClass_Smtc))
                {
                    Guid iidInterop = __uuidof<ISystemMediaTransportControlsInterop>();
                    ThrowIfFailed(RoGetActivationFactory(hsClass.Value, &iidInterop, (void**)&interop),
                        "RoGetActivationFactory(SystemMediaTransportControls interop)");
                }

                ISystemMediaTransportControls* smtc = null;
                Guid iidControls = __uuidof<ISystemMediaTransportControls>();
                ThrowIfFailed(interop->GetForWindow((HWND)_hwnd, &iidControls, (void**)&smtc),
                    "ISystemMediaTransportControlsInterop.GetForWindow");
                _smtc = smtc;
            }
            finally
            {
                if (interop != null) interop->Release();
            }
        }
    }

    private void EnsureButtonHandlerRegistered()
    {
        if (_buttonHandlerUnknown != 0)
            return; // already registered (one OS registration fans out to all managed subscribers)

        _buttonHandler = new MediaButtonHandler(DispatchButton);

        // Realize the managed handler's IUnknown via the generated ComWrappers, then add_ButtonPressed. The handler
        // pointer is interpreted by the OS as ITypedEventHandler<...>* (slot 3 == Invoke); the parameterized IID on
        // IMediaTransportButtonPressedHandler is what makes the OS's QI succeed. We keep one ref for the registration's
        // lifetime and release it when we remove the handler.
        nint unknown = ComWrappers.GetOrCreateComInterfaceForObject(_buttonHandler, CreateComInterfaceFlags.None);

        EventRegistrationToken token;
        int hr = _smtc->add_ButtonPressed(
            (ITypedEventHandler<Pointer<ISystemMediaTransportControls>, Pointer<ISystemMediaTransportControlsButtonPressedEventArgs>>*)unknown,
            &token);
        if (hr < 0)
        {
            Marshal.Release(unknown);
            _buttonHandler = null;
            ThrowIfFailed(hr, "add_ButtonPressed"); // E_NOINTERFACE here ⇒ suspect the parameterized IID (see handler).
        }

        _buttonHandlerUnknown = unknown;
        _buttonToken = token;
    }

    private void RemoveButtonHandler()
    {
        if (_buttonHandlerUnknown == 0)
            return;
        // remove_ButtonPressed by token; release our CCW ref afterward. Best-effort on the remove HRESULT — we are
        // tearing down regardless.
        if (_smtc != null)
            _smtc->remove_ButtonPressed(_buttonToken);
        Marshal.Release(_buttonHandlerUnknown);
        _buttonHandlerUnknown = 0;
        _buttonToken = default;
        _buttonHandler = null;
    }

    /// <summary>Build a <c>Windows.Foundation.Uri</c> from the file/URL string, wrap it in a
    /// <c>RandomAccessStreamReference</c> via <c>CreateFromUri</c>, and stage it as the thumbnail. The OS fetches the
    /// bytes lazily (async) when it renders the art.</summary>
    private void SetThumbnail(ISystemMediaTransportControlsDisplayUpdater* updater, string fileOrUri)
    {
        EnsureUriFactory();
        EnsureStreamRefStatics();

        IUriRuntimeClass* uri = null;
        IRandomAccessStreamReference* streamRef = null;
        try
        {
            using (var hsUri = new HStringHandle(fileOrUri))
                ThrowIfFailed(_uriFactory->CreateUri(hsUri.Value, &uri), "Uri.CreateUri");

            ThrowIfFailed(_streamRefStatics->CreateFromUri(uri, &streamRef),
                "RandomAccessStreamReference.CreateFromUri");

            // RISK: put_Thumbnail accepts the reference synchronously, but the OS only pulls the bytes when it paints
            // the flyout (OpenReadAsync). A bad/unreachable URL fails silently at paint time, not here — the showcase
            // verifies a real https art URL actually renders.
            ThrowIfFailed(updater->put_Thumbnail(streamRef), "put_Thumbnail");
        }
        finally
        {
            if (streamRef != null) streamRef->Release();
            if (uri != null) uri->Release();
        }
    }

    private void EnsureUriFactory()
    {
        if (_uriFactory != null)
            return;
        IUriRuntimeClassFactory* factory = null;
        using var hsClass = new HStringHandle(RuntimeClass_Uri);
        Guid iid = __uuidof<IUriRuntimeClassFactory>();
        ThrowIfFailed(RoGetActivationFactory(hsClass.Value, &iid, (void**)&factory),
            "RoGetActivationFactory(Windows.Foundation.Uri)");
        _uriFactory = factory;
    }

    private void EnsureStreamRefStatics()
    {
        if (_streamRefStatics != null)
            return;
        IRandomAccessStreamReferenceStatics* statics = null;
        using var hsClass = new HStringHandle(RuntimeClass_RandomAccessStreamReference);
        Guid iid = __uuidof<IRandomAccessStreamReferenceStatics>();
        ThrowIfFailed(RoGetActivationFactory(hsClass.Value, &iid, (void**)&statics),
            "RoGetActivationFactory(RandomAccessStreamReference)");
        _streamRefStatics = statics;
    }

    /// <summary>Route a cross-thread button press through the host dispatcher (if installed) before raising the public
    /// event; otherwise raise inline. Called by <see cref="MediaButtonHandler"/>.</summary>
    private void DispatchButton(MediaButton button)
    {
        Action raise = () => _buttonPressed?.Invoke(button);
        Action<Action>? dispatcher = ButtonDispatcher;
        if (dispatcher is not null)
            dispatcher(raise);
        else
            raise();
    }

    /// <summary>Lazily <c>RoInitialize</c> the apartment. Tolerates S_FALSE (already initialized) and RPC_E_CHANGED_MODE
    /// (already initialized with a different model) — both benign, the same pitfall the toast spike documented.</summary>
    private void EnsureRoInitialized()
    {
        if (_roInitialized)
            return;
        // SMTC is UI-thread bound; initialize the calling (UI) apartment as single-threaded — but tolerate a host that
        // already initialized it multithreaded (RPC_E_CHANGED_MODE), since GetForWindow works under either model.
        int hr = RoInitialize(RO_INIT_TYPE.RO_INIT_SINGLETHREADED);
        if (hr < 0 && hr != S_FALSE && hr != RPC_E_CHANGED_MODE)
            ThrowIfFailed(hr, "RoInitialize");
        _roInitialized = true;
    }

    private static byte B(bool v) => (byte)(v ? 1 : 0);

    private void ThrowIfDisposed()
    {
        if (_disposed || _smtc == null)
            throw new ObjectDisposedException(nameof(SystemMediaControls));
    }

    private static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed (0x{(uint)hr:X8}).");
    }

    /// <summary>Unhook the button handler and release every cached WinRT pointer. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            RemoveButtonHandler();
            _buttonPressed = null;

            if (_streamRefStatics != null) { _streamRefStatics->Release(); _streamRefStatics = null; }
            if (_uriFactory != null) { _uriFactory->Release(); _uriFactory = null; }
            if (_smtc != null) { _smtc->Release(); _smtc = null; }
        }
    }
}
