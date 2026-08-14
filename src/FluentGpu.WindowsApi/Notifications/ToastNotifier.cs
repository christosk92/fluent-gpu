using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using FluentGpu.WindowsApi.Packaging;
using TerraFX.Interop.Windows; // Pointer<T> (GetScheduledToastNotifications generic)
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
    private const string RuntimeClass_NotificationData = "Windows.UI.Notifications.NotificationData";
    private const string RuntimeClass_ScheduledToastNotification = "Windows.UI.Notifications.ScheduledToastNotification";

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
    private IToastNotificationManagerStatics2* _managerStatics2; // history accessor (RemoveByTag/RemoveGroup/Clear)
    private IToastNotificationHistory* _history;
    private IScheduledToastNotificationFactory* _scheduledFactory;

    // Monotonic NotificationData sequence per (tag, group). The OS drops an update whose sequence is ≤ the last
    // applied one; we increment under _gate so callers of Update never have to thread a counter.
    private readonly Dictionary<string, uint> _updateSequence = new(StringComparer.Ordinal);

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
            _updateSequence.Clear();
        }
    }

    /// <summary>
    /// Show a toast described by a <see cref="ToastBuilder"/> with no XML in sight — builds the payload and applies the
    /// builder's carried <see cref="ToastBuilder.Tag"/>/<see cref="ToastBuilder.Group"/> automatically. This is the
    /// "no need to render XML" entry point (mirror of <see cref="ToastBuilder.ShowVia"/>); the raw
    /// <see cref="Show(string,string?,string?)"/> stays available as the escape hatch.
    /// </summary>
    public bool Show(ToastBuilder toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        return Show(toast.BuildXml(), toast.TagValue, toast.GroupValue);
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

        lock (_gate)
        {
            if (!_registered)
                throw new InvalidOperationException("Call Register(activatorClsid) before Show.");

            EnsureRoInitialized();

            IXmlDocument* xmlDoc = LoadToastXml(toastXml);
            try
            {
                EnsureToastFactory();
                IToastNotification* toast = null;
                ThrowIfFailed(_toastFactory->CreateToastNotification(xmlDoc, &toast), "CreateToastNotification");
                try
                {
                    ApplyTagGroup(toast, tag, group);   // IToastNotification2.put_Tag/put_Group — enables Update/Remove-by-tag
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
                if (xmlDoc != null) xmlDoc->Release();
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

    /// <summary>Remove a shown/Action-Center toast by its <paramref name="tag"/> (and optional <paramref name="group"/>).</summary>
    public void RemoveByTag(string tag, string? group = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        lock (_gate)
        {
            if (!_registered) return;
            EnsureRoInitialized();
            EnsureHistory();
            using var hsTag = new HStringHandle(tag);
            using var hsAumid = new HStringHandle(_aumid);
            if (string.IsNullOrEmpty(group))
                _history->Remove(hsTag.Value);
            else
            {
                using var hsGroup = new HStringHandle(group);
                _history->RemoveGroupedTagWithId(hsTag.Value, hsGroup.Value, hsAumid.Value);
            }
        }
    }

    /// <summary>Remove every toast in <paramref name="group"/> for this app.</summary>
    public void RemoveGroup(string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        lock (_gate)
        {
            if (!_registered) return;
            EnsureRoInitialized();
            EnsureHistory();
            using var hsGroup = new HStringHandle(group);
            using var hsAumid = new HStringHandle(_aumid);
            _history->RemoveGroupWithId(hsGroup.Value, hsAumid.Value);
        }
    }

    /// <summary>
    /// Replace the data-bound placeholders of a live toast in place (<c>IToastNotifier2.UpdateWithTag</c> /
    /// <c>UpdateWithTagAndGroup</c>) without re-showing it. Keys match the placeholders
    /// <see cref="ToastBuilder.Progress"/> emits when <c>dataBound: true</c> (e.g. <c>progressValue</c>,
    /// <c>progressStatus</c>). Sequence numbers increment automatically per tag/group so out-of-order updates are
    /// dropped by the OS rather than flashing backwards. Same registration requirement as <see cref="Show(string,string?,string?)"/>.
    /// </summary>
    /// <param name="values">Placeholder name → replacement text. Null values are skipped; an empty map still bumps
    /// the sequence (a no-op visual update that keeps the counter honest).</param>
    /// <param name="tag">The tag the toast was shown with. Required — live update is tag-keyed.</param>
    /// <param name="group">The group the toast was shown with, or <see langword="null"/> to use the tag-only path.</param>
    /// <returns>The WinRT <c>NotificationUpdateResult</c> tri-state. An expired/dismissed toast is
    /// <see cref="ToastUpdateResult.NotificationNotFound"/>, not an exception.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Register"/> was not called, or a WinRT step failed.</exception>
    public ToastUpdateResult Update(IReadOnlyDictionary<string, string> values, string tag, string? group = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrEmpty(tag);

        lock (_gate)
        {
            if (!_registered)
                throw new InvalidOperationException("Call Register(activatorClsid) before Update.");

            EnsureRoInitialized();
            EnsureNotifier();

            INotificationData* data = CreateNotificationData(values, NextSequence(tag, group));
            IToastNotifier2* notifier2 = null;
            try
            {
                Guid iid = __uuidof<IToastNotifier2>();
                ThrowIfFailed(_notifier->QueryInterface(&iid, (void**)&notifier2), "QI IToastNotifier2");

                NotificationUpdateResult native;
                int hr;
                using var hsTag = new HStringHandle(tag);
                if (string.IsNullOrEmpty(group))
                {
                    hr = notifier2->UpdateWithTag(data, hsTag.Value, &native);
                }
                else
                {
                    using var hsGroup = new HStringHandle(group);
                    hr = notifier2->UpdateWithTagAndGroup(data, hsTag.Value, hsGroup.Value, &native);
                }
                if (hr < 0)
                    return ToastUpdateResult.Failed;
                return (ToastUpdateResult)(int)native;
            }
            finally
            {
                if (notifier2 != null) notifier2->Release();
                if (data != null) data->Release();
            }
        }
    }

    /// <summary>
    /// Schedule a toast for OS delivery at <paramref name="deliveryTime"/>. The process does NOT need to be running
    /// when the time arrives — the Shell posts the toast from the scheduled payload. Same registration requirement as
    /// <see cref="Show(ToastBuilder)"/>; returns the <c>AddToSchedule</c> HRESULT-as-bool (a past delivery time, or a
    /// suppressed AUMID, yields <see langword="false"/> rather than throwing). Tag/group are applied via
    /// <c>IScheduledToastNotification2</c> so <see cref="Unschedule"/> can find the entry.
    /// </summary>
    /// <param name="toast">The builder (XML + carried tag/group). <paramref name="tag"/> wins over the builder's tag.</param>
    /// <param name="deliveryTime">UTC instant the Shell should show the toast. Must be in the future.</param>
    /// <param name="tag">Identity for later <see cref="Unschedule"/> / replace. Required.</param>
    /// <param name="group">Optional group identity, paired with <paramref name="tag"/>.</param>
    public bool Schedule(ToastBuilder toast, DateTimeOffset deliveryTime, string tag, string? group = null)
    {
        ArgumentNullException.ThrowIfNull(toast);
        ArgumentException.ThrowIfNullOrEmpty(tag);
        return Schedule(toast.BuildXml(), deliveryTime, tag, group ?? toast.GroupValue);
    }

    /// <summary>Schedule a toast from its XML payload. See <see cref="Schedule(ToastBuilder, DateTimeOffset, string, string?)"/>.</summary>
    public bool Schedule(string toastXml, DateTimeOffset deliveryTime, string tag, string? group = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(toastXml);
        ArgumentException.ThrowIfNullOrEmpty(tag);

        lock (_gate)
        {
            if (!_registered)
                throw new InvalidOperationException("Call Register(activatorClsid) before Schedule.");

            EnsureRoInitialized();
            EnsureNotifier();
            EnsureScheduledFactory();

            IXmlDocument* xmlDoc = LoadToastXml(toastXml);
            IScheduledToastNotification* scheduled = null;
            try
            {
                WinRTDateTime nativeTime;
                nativeTime.UniversalTime = deliveryTime.UtcDateTime.ToFileTimeUtc();
                ThrowIfFailed(
                    _scheduledFactory->CreateScheduledToastNotification(xmlDoc, nativeTime, &scheduled),
                    "CreateScheduledToastNotification");

                ApplyScheduledTagGroup(scheduled, tag, group);
                int hr = _notifier->AddToSchedule(scheduled);
                return hr >= 0;
            }
            finally
            {
                if (scheduled != null) scheduled->Release();
                if (xmlDoc != null) xmlDoc->Release();
            }
        }
    }

    /// <summary>
    /// Remove every scheduled toast whose tag (and optional group) matches. A no-op when not registered or when nothing
    /// matches — same shape as <see cref="RemoveByTag"/>. Delivery of an already-queued toast that has passed its
    /// time is the OS's; this only affects still-pending entries.
    /// </summary>
    public void Unschedule(string tag, string? group = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        lock (_gate)
        {
            if (!_registered) return;
            EnsureRoInitialized();
            EnsureNotifier();

            IScheduledToastView* view = null;
            if (!TryGetScheduledView(&view) || view == null)
                return;
            try
            {
                uint size = 0;
                if (view->get_Size(&size) < 0 || size == 0)
                    return;
                for (uint i = 0; i < size; i++)
                {
                    IScheduledToastNotification* item = null;
                    if (view->GetAt(i, &item) < 0 || item == null)
                        continue;
                    try
                    {
                        if (ScheduledMatches(item, tag, group))
                            _notifier->RemoveFromSchedule(item);
                    }
                    finally { item->Release(); }
                }
            }
            finally { view->Release(); }
        }
    }

    /// <summary>
    /// Count of still-pending scheduled toasts for this AUMID (<c>IToastNotifier.GetScheduledToastNotifications</c>).
    /// Returns 0 when not registered or the read fails. Call on launch to reconcile: the OS keeps the schedule across
    /// process lifetimes, so a stale queue from a previous run is still there.
    /// </summary>
    public int CountScheduled()
    {
        lock (_gate)
        {
            if (!_registered) return 0;
            try
            {
                EnsureRoInitialized();
                EnsureNotifier();
                IScheduledToastView* view = null;
                if (!TryGetScheduledView(&view) || view == null)
                    return 0;
                try
                {
                    uint size = 0;
                    return view->get_Size(&size) >= 0 ? (int)size : 0;
                }
                finally { view->Release(); }
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>Clear this app's entire toast history (banners + Action Center entries).</summary>
    public void ClearHistory()
    {
        lock (_gate)
        {
            if (!_registered) return;
            EnsureRoInitialized();
            EnsureHistory();
            using var hsAumid = new HStringHandle(_aumid);
            _history->ClearWithId(hsAumid.Value);
        }
    }

    /// <summary>QI the manager statics to <c>…Statics2</c> and fetch the <c>IToastNotificationHistory</c>, cached.</summary>
    private void EnsureHistory()
    {
        if (_history != null) return;
        if (_managerStatics == null) EnsureNotifier();   // EnsureNotifier caches _managerStatics
        if (_managerStatics2 == null)
        {
            IToastNotificationManagerStatics2* s2 = null;
            Guid iid = __uuidof<IToastNotificationManagerStatics2>();
            ThrowIfFailed(_managerStatics->QueryInterface(&iid, (void**)&s2), "QI IToastNotificationManagerStatics2");
            _managerStatics2 = s2;
        }
        IToastNotificationHistory* hist = null;
        ThrowIfFailed(_managerStatics2->get_History(&hist), "get_History");
        _history = hist;
    }

    /// <summary>Set tag (and group) on a toast via <c>IToastNotification2</c> so it can later be updated/removed.</summary>
    private static void ApplyTagGroup(IToastNotification* toast, string? tag, string? group)
    {
        if (string.IsNullOrEmpty(tag) && string.IsNullOrEmpty(group)) return;
        IToastNotification2* t2 = null;
        Guid iid = __uuidof<IToastNotification2>();
        if (toast->QueryInterface(&iid, (void**)&t2) < 0 || t2 == null) return;
        try
        {
            if (!string.IsNullOrEmpty(tag)) { using var h = new HStringHandle(tag); ThrowIfFailed(t2->put_Tag(h.Value), "put_Tag"); }
            if (!string.IsNullOrEmpty(group)) { using var h = new HStringHandle(group); ThrowIfFailed(t2->put_Group(h.Value), "put_Group"); }
        }
        finally { t2->Release(); }
    }

    /// <summary>Set tag (and group) on a scheduled toast via <c>IScheduledToastNotification2</c>.</summary>
    private static void ApplyScheduledTagGroup(IScheduledToastNotification* toast, string tag, string? group)
    {
        IScheduledToastNotification2* t2 = null;
        Guid iid = __uuidof<IScheduledToastNotification2>();
        if (toast->QueryInterface(&iid, (void**)&t2) < 0 || t2 == null) return;
        try
        {
            using var hsTag = new HStringHandle(tag);
            ThrowIfFailed(t2->put_Tag(hsTag.Value), "IScheduledToastNotification2.put_Tag");
            if (!string.IsNullOrEmpty(group))
            {
                using var hsGroup = new HStringHandle(group);
                ThrowIfFailed(t2->put_Group(hsGroup.Value), "IScheduledToastNotification2.put_Group");
            }
        }
        finally { t2->Release(); }
    }

    /// <summary><c>RoActivateInstance(XmlDocument)</c> + <c>LoadXml</c>. Caller Releases the returned document.</summary>
    private static IXmlDocument* LoadToastXml(string toastXml)
    {
        IXmlDocument* xmlDoc = null;
        IInspectable* inspectable = null;
        IXmlDocumentIO* xmlIo = null;
        try
        {
            using (var hsClass = new HStringHandle(RuntimeClass_XmlDocument))
                ThrowIfFailed(RoActivateInstance(hsClass.Value, &inspectable), "RoActivateInstance(XmlDocument)");

            Guid iidXmlIo = __uuidof<IXmlDocumentIO>();
            ThrowIfFailed(inspectable->QueryInterface(&iidXmlIo, (void**)&xmlIo), "QI IXmlDocumentIO");
            Guid iidXmlDoc = __uuidof<IXmlDocument>();
            ThrowIfFailed(inspectable->QueryInterface(&iidXmlDoc, (void**)&xmlDoc), "QI IXmlDocument");

            using (var hsPayload = new HStringHandle(toastXml))
                ThrowIfFailed(xmlIo->LoadXml(hsPayload.Value), "IXmlDocumentIO.LoadXml");
            return xmlDoc;
        }
        catch
        {
            if (xmlDoc != null) xmlDoc->Release();
            throw;
        }
        finally
        {
            if (xmlIo != null) xmlIo->Release();
            if (inspectable != null) inspectable->Release();
        }
    }

    /// <summary>
    /// <c>RoActivateInstance(NotificationData)</c> (default ctor) → fill <c>Values</c> via the hand-rolled
    /// <see cref="IStringMap"/> (call-OUT Insert) → <c>put_SequenceNumber</c>. Caller Releases the returned data.
    /// </summary>
    private static INotificationData* CreateNotificationData(IReadOnlyDictionary<string, string> values, uint sequence)
    {
        IInspectable* inspectable = null;
        INotificationData* data = null;
        try
        {
            using (var hsClass = new HStringHandle(RuntimeClass_NotificationData))
                ThrowIfFailed(RoActivateInstance(hsClass.Value, &inspectable), "RoActivateInstance(NotificationData)");

            Guid iid = __uuidof<INotificationData>();
            ThrowIfFailed(inspectable->QueryInterface(&iid, (void**)&data), "QI INotificationData");

            IMap<HSTRING, HSTRING>* map = null;
            ThrowIfFailed(data->get_Values(&map), "INotificationData.get_Values");
            try
            {
                var smap = (IStringMap*)map;
                foreach (KeyValuePair<string, string> kv in values)
                {
                    using var hsK = new HStringHandle(kv.Key);
                    using var hsV = new HStringHandle(kv.Value ?? string.Empty);
                    byte replaced = 0;
                    ThrowIfFailed(smap->Insert(hsK.Value, hsV.Value, &replaced), "IMap.Insert");
                }
            }
            finally
            {
                if (map != null) ((IStringMap*)map)->Release();
            }

            ThrowIfFailed(data->put_SequenceNumber(sequence), "INotificationData.put_SequenceNumber");
            return data;
        }
        catch
        {
            if (data != null) data->Release();
            throw;
        }
        finally
        {
            if (inspectable != null) inspectable->Release();
        }
    }

    private uint NextSequence(string tag, string? group)
    {
        string key = string.IsNullOrEmpty(group) ? tag : tag + "\n" + group;
        _updateSequence.TryGetValue(key, out uint n);
        n++;
        _updateSequence[key] = n;
        return n;
    }

    private void EnsureScheduledFactory()
    {
        if (_scheduledFactory != null)
            return;
        IScheduledToastNotificationFactory* factory = null;
        using var hsClass = new HStringHandle(RuntimeClass_ScheduledToastNotification);
        Guid iid = __uuidof<IScheduledToastNotificationFactory>();
        ThrowIfFailed(RoGetActivationFactory(hsClass.Value, &iid, (void**)&factory),
            "RoGetActivationFactory(ScheduledToastNotification)");
        _scheduledFactory = factory;
    }

    private bool TryGetScheduledView(IScheduledToastView** view)
    {
        *view = null;
        IVectorView<Pointer<IScheduledToastNotification>>* native = null;
        int hr = _notifier->GetScheduledToastNotifications(&native);
        if (hr < 0 || native == null)
            return false;
        *view = (IScheduledToastView*)native;
        return true;
    }

    private static bool ScheduledMatches(IScheduledToastNotification* toast, string tag, string? group)
    {
        IScheduledToastNotification2* t2 = null;
        Guid iid = __uuidof<IScheduledToastNotification2>();
        if (toast->QueryInterface(&iid, (void**)&t2) < 0 || t2 == null)
            return false;
        try
        {
            HSTRING hsTag = default;
            if (t2->get_Tag(&hsTag) < 0)
                return false;
            try
            {
                if (!string.Equals(HStringHandle.ToManaged(hsTag), tag, StringComparison.Ordinal))
                    return false;
            }
            finally { WindowsDeleteString(hsTag); }

            HSTRING hsGroup = default;
            if (t2->get_Group(&hsGroup) < 0)
                return string.IsNullOrEmpty(group);
            try
            {
                string g = HStringHandle.ToManaged(hsGroup);
                return string.IsNullOrEmpty(group)
                    ? string.IsNullOrEmpty(g)
                    : string.Equals(g, group, StringComparison.Ordinal);
            }
            finally { WindowsDeleteString(hsGroup); }
        }
        finally { t2->Release(); }
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
        // Release derived/QI'd pointers before their parents (reverse acquisition order).
        if (_history != null) { _history->Release(); _history = null; }
        if (_managerStatics2 != null) { _managerStatics2->Release(); _managerStatics2 = null; }
        if (_scheduledFactory != null) { _scheduledFactory->Release(); _scheduledFactory = null; }
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
