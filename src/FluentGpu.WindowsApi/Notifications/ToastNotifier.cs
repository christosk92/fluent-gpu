using System;
using System.Runtime.Versioning;
using System.Threading;
using FluentGpu.WindowsApi.Packaging;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.WinRT.WinRT;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// The cold-COM owner of the local-toast pillar: registers the app's AUMID + activator (unpackaged) and shows toasts via
/// the in-box public WinRT <c>ToastNotificationManager</c>/<c>IToastNotifier</c> surface, hand-bound through
/// <c>TerraFX.Interop.WinRT</c> vtable structs with zero CsWinRT, zero <c>ComWrappers</c> on the call-out path, and zero
/// reflection. This is the exact pattern the AOT spike proved end-to-end (S_OK from a NativeAOT binary on win-arm64 and
/// win-x64; docs/plans/windowsapi-implementation-research.md §2.1, spike verdict WORKS-AOT).
/// </summary>
/// <remarks>
/// <para>
/// <b>The Show chain</b> (each step returned S_OK in the spike; <see cref="Show(string,string?,string?)"/>):
/// <c>RoActivateInstance("Windows.Data.Xml.Dom.XmlDocument")</c> → QI <c>IXmlDocumentIO</c>/<c>IXmlDocument</c> →
/// <c>IXmlDocumentIO.LoadXml(payload)</c> → <c>RoGetActivationFactory("…ToastNotification") → IToastNotificationFactory.CreateToastNotification(xmlDoc)</c>
/// → <c>RoGetActivationFactory("…ToastNotificationManager") → IToastNotificationManagerStatics.CreateToastNotifierWithId(aumid)</c>
/// → <c>IToastNotifier.Show(toast)</c>. Note the asymmetry: <c>XmlDocument</c> is an activatable class (created via
/// <c>RoActivateInstance</c>), while the toast types are reached through their static activation factories
/// (<c>RoGetActivationFactory</c>) — calling <c>RoGetActivationFactory</c> on <c>XmlDocument</c> would hand back
/// <c>IXmlDocumentStatics</c>, not an instance.
/// </para>
/// <para>
/// <b>Factory/notifier caching.</b> The activation factories are process-stable; this class caches the
/// <c>IToastNotificationFactory</c>, the <c>IToastNotificationManagerStatics</c>, and the per-AUMID
/// <c>IToastNotifier</c> as AddRef-owned fields for the manager's lifetime and releases them in <see cref="Unregister"/> —
/// it does NOT re-<c>RoGetActivationFactory</c> per <c>Show</c> (spike guidance). It also does NOT
/// <c>RoUninitialize</c> after <c>Show</c>: the Action Center pulls the toast XML asynchronously after <c>Show</c>
/// returns, so tearing the apartment down can drop the toast.
/// </para>
/// <para>
/// <b><c>Show</c> returning S_OK does not guarantee a visible banner.</b> It only means the platform accepted the toast.
/// If the user disabled this app's toasts (or Focus Assist is on), nothing paints — the spike hit exactly this. Read
/// <see cref="Setting"/> (<c>IToastNotifier.get_Setting</c>) to detect a suppressed state rather than treating "no
/// banner" as an error.
/// </para>
/// <para>
/// <b>Activation.</b> Clicks arrive through <see cref="ToastActivatorCallback"/>'s <c>INotificationActivationCallback</c> on an
/// arbitrary COM thread; this class raises <see cref="Activated"/> only after hopping through the
/// <see cref="ActivationDispatcher"/> the host installs. Register <see cref="Activated"/> handlers BEFORE calling
/// <see cref="Register"/> so the class object is registered <c>REGCLS_MULTIPLEUSE</c> (in-proc repeated callbacks) rather
/// than <c>REGCLS_SINGLEUSE</c> (<c>AppNotificationManager.cpp:197</c>).
/// </para>
/// <para>
/// <b>AOT/CA1416.</b> The csproj targets a bare <c>net10.0</c> TFM, so the WinRT toast types (which carry
/// <c>[SupportedOSPlatform("windows6.1")]</c>) would warn under <c>TreatWarningsAsErrors</c>; this type is annotated
/// <c>[SupportedOSPlatform("windows10.0.10240.0")]</c> (toast notifications shipped in Windows 10 1507) to keep the
/// analyzer silent, per the spike's TFM guidance.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.10240.0")]
public sealed unsafe class ToastNotifier : IDisposable
{
    // WinRT runtime class names (the in-box public surface we build against).
    private const string RuntimeClass_XmlDocument = "Windows.Data.Xml.Dom.XmlDocument";
    private const string RuntimeClass_ToastNotification = "Windows.UI.Notifications.ToastNotification";
    private const string RuntimeClass_ToastNotificationManager = "Windows.UI.Notifications.ToastNotificationManager";

    // Benign RoInitialize results (already initialized / changed apartment mode) — gate on FAILED, not != S_OK.
    private const int S_FALSE = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    private readonly object _gate = new();

    private string _aumid = string.Empty;
    private Guid _activatorClsid;
    private bool _registered;
    private bool _roInitialized;

    // Cached, AddRef-owned WinRT call-out interface pointers (released in Unregister/Dispose).
    private IToastNotificationFactory* _toastFactory;
    private IToastNotificationManagerStatics* _managerStatics;
    private IToastNotifier* _notifier;

    // The activator (implement side): the singleton callback + its class-object registration.
    private ToastActivatorCallback? _callback;
    private ComActivatorRegistration? _activatorRegistration;

    /// <summary>The process-wide default instance. Most apps want exactly one toast notifier.</summary>
    public static ToastNotifier Default { get; } = new();

    /// <summary>
    /// Raised when the user interacts with one of this app's toasts. The producer (the <c>INotificationActivationCallback</c>)
    /// fires on an arbitrary COM thread, so this event is raised through <see cref="ActivationDispatcher"/> — set that to
    /// the host's UI-thread marshaller before relying on thread affinity.
    /// <para>
    /// OPEN-QUESTION(#2): the host wiring (a <c>PostMessage</c> hop onto the <c>"FluentGpuWindow"</c> message loop) lives
    /// in the Win32 PAL, which this pillar does not own; install it via <see cref="ActivationDispatcher"/>. The cold-launch
    /// leg (Shell relaunches the AOT exe via <c>LocalServer32</c>; the args arrive on the command line tagged
    /// <c>----AppNotificationActivated:</c> and must be re-dispatched after the window exists) still needs end-to-end
    /// validation on the real AOT binary (docs/plans/windowsapi-implementation-research.md §5 #2).
    /// </para>
    /// </summary>
    public event Action<ToastActivatedArgs>? Activated;

    /// <summary>
    /// The marshaller the cross-thread activation callback is routed through before <see cref="Activated"/> is raised.
    /// The host installs a delegate that posts to its UI thread (e.g. <c>PostMessage</c> to the <c>"FluentGpuWindow"</c>
    /// HWND, where the existing WndProc + <c>WakeFrame</c> run a frame). If left null, the callback raises
    /// <see cref="Activated"/> inline on the COM thread (correct only for a handler that is itself thread-safe).
    /// </summary>
    public Action<Action>? ActivationDispatcher { get; set; }

    /// <summary><see langword="true"/> when toasts are supported for this process. Toasts genuinely do not work in an
    /// elevated process (<c>IsSupported == !IsElevated</c>, <c>AppNotificationManager.cpp:107</c>).</summary>
    public static bool IsSupported => !ProcessElevation.IsElevated();

    /// <summary>The AUMID this notifier attributes toasts to (set by <see cref="Register"/>); empty until registered.</summary>
    public string Aumid => _aumid;

    /// <summary>
    /// Register the app to show toasts: derive/attach the AUMID, write the unpackaged registry registration (AUMID assets +
    /// <c>LocalServer32</c>) when unpackaged, and <c>CoRegisterClassObject</c> the activator so clicks (and cold launches)
    /// reach this process. Idempotent. Packaged processes skip the registry writes (the manifest owns them) but still
    /// register the runtime class object.
    /// </summary>
    /// <param name="activatorClsid">The toast-activator CLSID — define it ONCE as a <c>static readonly Guid</c> and pass
    /// the SAME value here and (for a packaged build) in the manifest's <c>ToastActivatorCLSID</c> + <c>com:ExeServer</c>
    /// class id (docs/plans/windowsapi-implementation-research.md §3).</param>
    /// <param name="displayName">The app name shown in the Action Center (unpackaged only); defaults to the exe name.</param>
    /// <param name="iconPath">Optional LOCAL icon file path for the Action Center entry (unpackaged only;
    /// <c>http(s)://</c> is not allowed for the AUMID icon).</param>
    public void Register(Guid activatorClsid, string? displayName = null, string? iconPath = null)
    {
        lock (_gate)
        {
            if (_registered)
                return;

            _activatorClsid = activatorClsid;

            // 1-4: AUMID derivation + (unpackaged) registry assets / LocalServer32. Packaged: returns the manifest AUMID.
            _aumid = AumidRegistration.Register(activatorClsid, displayName, iconPath);

            // CoRegisterClassObject requires a COM apartment on the calling thread — a thread that has never
            // CoInitializeEx'd gets E_INVALIDARG (0x80070057) from the register call. A WinUI/XAML host has already
            // initialized COM by the time it registers, but a plain Win32/console caller (e.g. the --windowsapi-smoke
            // harness, or an app that registers before pumping) has not. Initialize the apartment here, before the
            // class-object registration, exactly as Show()/Setting already do. REGCLS_AGILE keeps the class object in the
            // neutral apartment regardless of this thread's model, so MULTITHREADED is the correct, side-effect-free init.
            EnsureRoInitialized();

            // 5: CoRegisterClassObject the activator class object. MULTIPLEUSE iff in-proc handlers are already attached
            // (so repeated foreground clicks are delivered), else SINGLEUSE (AppNotificationManager.cpp:197).
            bool multipleUse = Activated is not null;
            _callback = new ToastActivatorCallback(DispatchActivation);
            var factory = new ToastActivatorClassFactory(_callback);
            _activatorRegistration = new ComActivatorRegistration(factory);
            _activatorRegistration.Register(activatorClsid, multipleUse);

            _registered = true;
        }
    }

    /// <summary>
    /// Reverse <see cref="Register"/>: revoke the class object, release the cached WinRT factories/notifier, and (when
    /// unpackaged) delete the registry registration. Safe to call when not registered.
    /// </summary>
    public void Unregister()
    {
        lock (_gate)
        {
            _activatorRegistration?.Dispose();
            _activatorRegistration = null;
            _callback = null;

            ReleaseWinRtPointers();

            if (_registered && _activatorClsid != Guid.Empty)
                AumidRegistration.Unregister(_activatorClsid);

            _registered = false;
            _aumid = string.Empty;
        }
    }

    /// <summary>
    /// Show a toast from its XML payload (typically <see cref="ToastBuilder.BuildXml"/>). Runs the spike-proven WinRT
    /// chain, caching the activation factories and the per-AUMID notifier on first use. Returns the <c>Show</c>
    /// HRESULT-as-bool: <see langword="true"/> when the platform accepted the toast (NOT a guarantee it painted — see
    /// <see cref="Setting"/>).
    /// </summary>
    /// <param name="toastXml">The toast XML (≤ 5120 bytes). For an unpackaged app, any <c>http(s)://</c> image source
    /// must already be localized via <see cref="ToastImageCache"/>.</param>
    /// <param name="tag">Optional toast tag (for replace/remove); reserved — applied when the tag-bearing
    /// <c>IToastNotification2</c> path lands.</param>
    /// <param name="group">Optional toast group (for replace/remove); reserved alongside <paramref name="tag"/>.</param>
    /// <exception cref="InvalidOperationException"><see cref="Register"/> was not called, or a WinRT step failed.</exception>
    public bool Show(string toastXml, string? tag = null, string? group = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(toastXml);
        _ = tag;
        _ = group;   // OPEN-QUESTION: tag/group set-once needs IToastNotification2 (CreateToastNotifierWithId path covers Show).

        lock (_gate)
        {
            if (!_registered)
                throw new InvalidOperationException("Call Register(activatorClsid) before Show.");

            EnsureRoInitialized();

            // ── Build the XmlDocument and LoadXml the payload (XmlDocument is activatable → RoActivateInstance). ──
            IXmlDocument* xmlDoc = null;
            IInspectable* inspectable = null;
            IXmlDocumentIO* xmlIo = null;
            try
            {
                using (var hsClass = new HStringHandle(RuntimeClass_XmlDocument))
                {
                    int hr = RoActivateInstance(hsClass.Value, &inspectable);
                    ThrowIfFailed(hr, "RoActivateInstance(XmlDocument)");
                }

                Guid iidXmlIo = __uuidof<IXmlDocumentIO>();
                ThrowIfFailed(inspectable->QueryInterface(&iidXmlIo, (void**)&xmlIo), "QI IXmlDocumentIO");

                Guid iidXmlDoc = __uuidof<IXmlDocument>();
                ThrowIfFailed(inspectable->QueryInterface(&iidXmlDoc, (void**)&xmlDoc), "QI IXmlDocument");

                using (var hsPayload = new HStringHandle(toastXml))
                    ThrowIfFailed(xmlIo->LoadXml(hsPayload.Value), "IXmlDocumentIO.LoadXml");

                // ── Create the IToastNotification from the cached factory, then Show via the cached notifier. ──
                EnsureToastFactory();
                IToastNotification* toast = null;
                ThrowIfFailed(_toastFactory->CreateToastNotification(xmlDoc, &toast), "CreateToastNotification");
                try
                {
                    EnsureNotifier();
                    int showHr = _notifier->Show(toast);
                    // Show failure is surfaced as false rather than thrown — a disabled/suppressed toast can also land
                    // here on some builds; callers consult Setting to disambiguate.
                    return showHr >= 0;
                }
                finally
                {
                    if (toast != null) toast->Release();
                }
            }
            finally
            {
                if (xmlIo != null) xmlIo->Release();
                if (xmlDoc != null) xmlDoc->Release();
                if (inspectable != null) inspectable->Release();
            }
        }
    }

    /// <summary>
    /// The platform's current delivery setting for this AUMID's toasts (<c>IToastNotifier.get_Setting</c>). Use it to
    /// detect a suppressed state after a S_OK <c>Show</c> produced no banner. Returns
    /// <see cref="ToastDeliverySetting.Unknown"/> if not registered or the read fails.
    /// </summary>
    public ToastDeliverySetting Setting
    {
        get
        {
            lock (_gate)
            {
                if (!_registered)
                    return ToastDeliverySetting.Unknown;
                try
                {
                    EnsureRoInitialized();
                    EnsureNotifier();
                    NotificationSetting setting;
                    int hr = _notifier->get_Setting(&setting);
                    if (hr < 0)
                        return ToastDeliverySetting.Unknown;
                    return (ToastDeliverySetting)(int)setting;
                }
                catch
                {
                    return ToastDeliverySetting.Unknown;
                }
            }
        }
    }

    /// <summary>Lazily <c>RoInitialize</c> the apartment as multithreaded. Tolerates S_FALSE (already initialized) and
    /// RPC_E_CHANGED_MODE (already initialized with a different model) — both benign (spike pitfall #1).</summary>
    private void EnsureRoInitialized()
    {
        if (_roInitialized)
            return;
        int hr = RoInitialize(RO_INIT_TYPE.RO_INIT_MULTITHREADED);
        if (hr < 0 && hr != S_FALSE && hr != RPC_E_CHANGED_MODE)
            ThrowIfFailed(hr, "RoInitialize");
        _roInitialized = true;
    }

    /// <summary>Cache the <c>IToastNotificationManagerStatics</c> and the per-AUMID <c>IToastNotifier</c> on first use.</summary>
    private void EnsureNotifier()
    {
        if (_notifier != null)
            return;

        if (_managerStatics == null)
        {
            IToastNotificationManagerStatics* statics = null;
            using var hsClass = new HStringHandle(RuntimeClass_ToastNotificationManager);
            Guid iid = __uuidof<IToastNotificationManagerStatics>();
            ThrowIfFailed(RoGetActivationFactory(hsClass.Value, &iid, (void**)&statics),
                "RoGetActivationFactory(ToastNotificationManager)");
            _managerStatics = statics;
        }

        IToastNotifier* notifier = null;
        using (var hsAumid = new HStringHandle(_aumid))
            ThrowIfFailed(_managerStatics->CreateToastNotifierWithId(hsAumid.Value, &notifier),
                "CreateToastNotifierWithId");
        _notifier = notifier;
    }

    /// <summary>Cache the <c>IToastNotificationFactory</c> on first use.</summary>
    private void EnsureToastFactory()
    {
        if (_toastFactory != null)
            return;

        IToastNotificationFactory* factory = null;
        using var hsClass = new HStringHandle(RuntimeClass_ToastNotification);
        Guid iid = __uuidof<IToastNotificationFactory>();
        ThrowIfFailed(RoGetActivationFactory(hsClass.Value, &iid, (void**)&factory),
            "RoGetActivationFactory(ToastNotification)");
        _toastFactory = factory;
    }

    /// <summary>Route a cross-thread activation through the host's dispatcher (if installed) before raising
    /// <see cref="Activated"/>; otherwise raise inline. Called by <see cref="ToastActivatorCallback"/>.</summary>
    private void DispatchActivation(ToastActivatedArgs args)
    {
        Action raise = () => Activated?.Invoke(args);
        Action<Action>? dispatcher = ActivationDispatcher;
        if (dispatcher is not null)
            dispatcher(raise);
        else
            raise();
    }

    private void ReleaseWinRtPointers()
    {
        if (_notifier != null) { _notifier->Release(); _notifier = null; }
        if (_managerStatics != null) { _managerStatics->Release(); _managerStatics = null; }
        if (_toastFactory != null) { _toastFactory->Release(); _toastFactory = null; }
    }

    private static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed (0x{(uint)hr:X8}).");
    }

    /// <inheritdoc/>
    public void Dispose() => Unregister();
}
