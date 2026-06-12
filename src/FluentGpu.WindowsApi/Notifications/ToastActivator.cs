using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// The COM activator FluentGpu <i>implements</i> so the Shell can deliver toast clicks (and cold-launch the app) — the
/// one interface in the Notifications pillar that is implemented rather than consumed. It is wired through the
/// source-generated COM path (<c>[GeneratedComInterface]</c>/<c>[GeneratedComClass]</c> + a hand
/// <c>IClassFactory</c> + <c>CoRegisterClassObject</c>), exactly as the repo's COM doctrine prescribes for cold COM —
/// NO <c>[ComImport]</c>, NO <c>ComWrappers</c> subclassing, NO reflection (<c>design/dotnet10-csharp14-zero-alloc.md §4</c>;
/// docs/plans/windowsapi-implementation-research.md §2.1). The spike that proved the WinRT call-OUT path did NOT exercise
/// this implement side (it remains owed live validation — see the OPEN-QUESTIONs).
/// </summary>
/// <remarks>
/// <para>
/// <b>What lives here.</b> The <see cref="INotificationActivationCallback"/> interface declaration (IID from the Windows
/// SDK header <c>NotificationActivationCallback.h</c>), its implementation <see cref="ToastActivatorCallback"/>, a minimal
/// <see cref="IClassFactory"/> + <see cref="ToastActivatorClassFactory"/> whose <c>CreateInstance</c> hands out the
/// singleton callback, and <see cref="ComActivatorRegistration"/> which performs the runtime
/// <c>CoRegisterClassObject</c>/<c>CoRevokeClassObject</c>. <see cref="ToastNotifier"/> owns the lifecycle (it constructs
/// the callback, registers the factory in <c>Register</c>, revokes in <c>Unregister</c>).
/// </para>
/// <para>
/// <b>Why a source-generated <c>ComWrappers</c>, and why that is allowed.</b> To pass a managed class object to
/// <c>CoRegisterClassObject</c> the runtime must hand the OS a real <c>IUnknown*</c> vtable for it. The AOT-correct,
/// reflection-free way is <see cref="StrategyBasedComWrappers"/> (the concrete <c>ComWrappers</c> the COM source
/// generator emits against — instantiating it is NOT the forbidden "<c>ComWrappers</c> subclassing"; it is the sanctioned
/// generated-COM marshaller, see <see href="https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation">ComWrappers source generation</see>).
/// <c>GetOrCreateComInterfaceForObject</c> on it produces the <c>IUnknown*</c> we register.
/// </para>
/// <para>
/// <b>The <c>CoRegisterClassObject</c> AOT pitfall (§5 #2 / CsWin32 #1670).</b> The factory must be passed as a RAW
/// <c>IUnknown*</c> pointer (here a <see cref="StrategyBasedComWrappers"/>-produced <c>nint</c>), not a marshaled object,
/// or the registration silently no-ops in NativeAOT + ExeServer. TerraFX's <c>CoRegisterClassObject(Guid*, IUnknown*,
/// uint, uint, uint*)</c> already takes the pointer form, which is what we use.
/// </para>
/// <para>
/// <b>Threading.</b> <c>INotificationActivationCallback.Activate</c> fires on an arbitrary COM thread (the factory is
/// registered <c>REGCLS_AGILE</c>, so it serves the neutral apartment). <see cref="ToastActivatorCallback"/> therefore
/// does NOT raise the public event synchronously on that thread — it stashes the parsed args and invokes the supplied
/// dispatch callback, which <see cref="ToastNotifier"/> uses to hop to the UI thread. OPEN-QUESTION(#2): the cold-launch
/// leg (Shell starts a fresh AOT exe via <c>LocalServer32</c> and the process must wire the class object fast enough to
/// receive <c>Activate</c>, including the out-of-proc vtable exposure) needs end-to-end validation on the real AOT binary
/// (docs/plans/windowsapi-implementation-research.md §5 #2).
/// </para>
/// </remarks>
internal static class ToastActivatorComConstants
{
    /// <summary>IID of <c>INotificationActivationCallback</c> (Windows SDK <c>NotificationActivationCallback.h</c>, NOT
    /// WASDK — confirmed absent from <c>dev/</c>; docs/plans/windowsapi-implementation-research.md §2.1).</summary>
    public const string INotificationActivationCallbackIid = "53e31837-6600-4a81-9395-75cffe746f94";

    /// <summary>IID of the standard <c>IClassFactory</c> (combaseapi.h, <c>00000001-0000-0000-C000-000000000046</c>).</summary>
    public const string IClassFactoryIid = "00000001-0000-0000-C000-000000000046";
}

/// <summary>
/// The Shell-callback interface a toast-source app implements (IID
/// <c>53e31837-6600-4a81-9395-75cffe746f94</c>). The Shell QIs the registered class object for this and calls
/// <see cref="Activate"/> when the user interacts with a toast (or to relaunch the app on a cold click). Declared
/// <c>[GeneratedComInterface]</c> so the COM source generator emits the managed→native vtable with no reflection.
/// </summary>
/// <remarks>
/// The native signature is <c>HRESULT Activate(LPCWSTR appUserModelId, LPCWSTR invokedArgs,
/// NOTIFICATION_USER_INPUT_DATA* data, ULONG count)</c> (WASDK impl at
/// <c>dev/AppNotifications/AppNotificationManager.cpp:346-422</c>). The string parameters are taken as raw
/// <c>char*</c> (not marshaled <c>string</c>) and the method is <c>[PreserveSig]</c> returning the HRESULT directly,
/// keeping the generated stub a thin pointer pass-through (no implicit allocation on the callback thread, and no
/// ambiguity over who owns the LPCWSTR memory — the OS does).
/// </remarks>
[GeneratedComInterface]
[Guid(ToastActivatorComConstants.INotificationActivationCallbackIid)]
internal partial interface INotificationActivationCallback
{
    /// <summary>The Shell's activation callback. <paramref name="appUserModelId"/> is the toast's AUMID,
    /// <paramref name="invokedArgs"/> is the <c>launch=</c>/button <c>arguments=</c> string, and
    /// <paramref name="data"/>/<paramref name="count"/> are the user-input fields. Returns S_OK.</summary>
    [PreserveSig]
    unsafe int Activate(char* appUserModelId, char* invokedArgs, NOTIFICATION_USER_INPUT_DATA* data, uint count);
}

/// <summary>
/// The singleton implementation of <see cref="INotificationActivationCallback"/>. On <c>Activate</c> it reconstructs the
/// invoked-argument string and the user-input map (mirroring <c>AppNotificationManager::Activate</c>,
/// <c>AppNotificationManager.cpp:346-365</c>) into a <see cref="ToastActivatedArgs"/>, then forwards it to the dispatch
/// delegate supplied by <see cref="ToastNotifier"/> (never raising the public event on this arbitrary COM thread).
/// </summary>
[GeneratedComClass]
[SupportedOSPlatform("windows10.0.10240.0")]
internal sealed partial class ToastActivatorCallback : INotificationActivationCallback
{
    private readonly Action<ToastActivatedArgs> _dispatch;

    /// <summary>Construct with the dispatch sink (<see cref="ToastNotifier"/> passes a delegate that hops the args to the
    /// UI thread before raising <see cref="ToastNotifier.Activated"/>).</summary>
    public ToastActivatorCallback(Action<ToastActivatedArgs> dispatch) => _dispatch = dispatch;

    /// <inheritdoc/>
    public unsafe int Activate(char* appUserModelId, char* invokedArgs, NOTIFICATION_USER_INPUT_DATA* data, uint count)
    {
        try
        {
            string invoked = invokedArgs == null ? string.Empty : new string(invokedArgs);

            // Build the user-input map (key→value) from the native array (AppNotificationManager.cpp:360-363).
            IReadOnlyDictionary<string, string> userInput;
            if (data == null || count == 0)
            {
                userInput = ToastActivatedArgs.EmptyMap;
            }
            else
            {
                var map = new Dictionary<string, string>((int)count, StringComparer.Ordinal);
                for (uint i = 0; i < count; i++)
                {
                    NOTIFICATION_USER_INPUT_DATA item = data[i];
                    string key = item.Key == null ? string.Empty : new string(item.Key);
                    string value = item.Value == null ? string.Empty : new string(item.Value);
                    map[key] = value;
                }
                userInput = map;
            }

            _dispatch(new ToastActivatedArgs(invoked, userInput));
            return S.S_OK;
        }
        catch
        {
            // A managed exception must not propagate across the COM boundary; report failure to the Shell instead.
            return unchecked((int)0x80004005); // E_FAIL
        }
    }
}

/// <summary>
/// The classic <c>IClassFactory</c> (IID <c>00000001-0000-0000-C000-000000000046</c>) the OS uses to instantiate the
/// activator class object. Declared <c>[GeneratedComInterface]</c>; FluentGpu implements it in
/// <see cref="ToastActivatorClassFactory"/>.
/// </summary>
[GeneratedComInterface]
[Guid(ToastActivatorComConstants.IClassFactoryIid)]
internal partial interface IClassFactory
{
    /// <summary>Create (or return the singleton) instance, QI'd to <paramref name="riid"/>. <c>pUnkOuter</c> must be null
    /// (no aggregation) — returns <c>CLASS_E_NOAGGREGATION</c> otherwise.</summary>
    [PreserveSig]
    unsafe int CreateInstance(nint pUnkOuter, Guid* riid, void** ppvObject);

    /// <summary>Increment/decrement the server lock count. FluentGpu's server lifetime is owned by the process, so this
    /// is a no-op returning S_OK.</summary>
    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

/// <summary>
/// An <see cref="IClassFactory"/> whose <see cref="CreateInstance"/> always hands out the single
/// <see cref="ToastActivatorCallback"/> instance (matching WASDK's "factory returns the singleton",
/// <c>AppNotificationManager.h:72-93</c>). The class object the OS holds is this factory, registered via
/// <c>CoRegisterClassObject</c>.
/// </summary>
[GeneratedComClass]
[SupportedOSPlatform("windows10.0.10240.0")]
internal sealed partial class ToastActivatorClassFactory : IClassFactory
{
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    private readonly ToastActivatorCallback _callback;

    /// <summary>The <see cref="StrategyBasedComWrappers"/> used to realize the callback's native interface pointers. One
    /// instance is shared so repeated QIs return the same COM identity for the same managed object.</summary>
    internal static readonly StrategyBasedComWrappers ComWrappers = new();

    public ToastActivatorClassFactory(ToastActivatorCallback callback) => _callback = callback;

    /// <inheritdoc/>
    public unsafe int CreateInstance(nint pUnkOuter, Guid* riid, void** ppvObject)
    {
        if (ppvObject == null)
            return E_INVALIDARG;
        *ppvObject = null;
        if (pUnkOuter != 0)
            return CLASS_E_NOAGGREGATION;

        // Realize the singleton callback's IUnknown via the generated ComWrappers, then QI it to the requested IID. The
        // returned pointer carries one AddRef the caller (the Shell) owns; we release our QI helper reference.
        nint unknown = ComWrappers.GetOrCreateComInterfaceForObject(_callback, CreateComInterfaceFlags.None);
        try
        {
            Guid iid = *riid;
            int hr = Marshal.QueryInterface(unknown, in iid, out nint ppv);
            if (hr >= 0)
                *ppvObject = (void*)ppv;
            return hr >= 0 ? hr : E_NOINTERFACE;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <inheritdoc/>
    public int LockServer(bool fLock) => S.S_OK;
}

/// <summary>
/// Owns the runtime registration of the toast-activator class object: <c>CoRegisterClassObject</c> on
/// <see cref="Register"/> and <c>CoRevokeClassObject</c> on <see cref="Dispose"/>. Instantiated by
/// <see cref="ToastNotifier"/> in <c>Register</c> and disposed in <c>Unregister</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240.0")]
internal sealed unsafe class ComActivatorRegistration : IDisposable
{
    // REGCLS / CLSCTX values via TerraFX enums (verified present): CLSCTX_LOCAL_SERVER, REGCLS_AGILE,
    // REGCLS_MULTIPLEUSE/SINGLEUSE. AGILE is always set (an arbitrary thread registers into the neutral apartment);
    // MULTIPLEUSE iff in-proc handlers want repeated callbacks, else SINGLEUSE (AppNotificationManager.cpp:197).
    private readonly ToastActivatorClassFactory _factory;
    private nint _factoryUnknown;   // the IUnknown* passed to CoRegisterClassObject (we hold one ref for its lifetime)
    private uint _cookie;
    private bool _registered;

    public ComActivatorRegistration(ToastActivatorClassFactory factory) => _factory = factory;

    /// <summary>
    /// Register the class object under <paramref name="clsid"/> as a <c>CLSCTX_LOCAL_SERVER</c> | <c>REGCLS_AGILE</c>
    /// class object so the Shell can obtain it (the fifth unpackaged step,
    /// <c>AppNotificationManager.cpp:190-208</c>). <paramref name="multipleUse"/> selects
    /// <c>REGCLS_MULTIPLEUSE</c> (true — repeated in-proc callbacks) vs <c>REGCLS_SINGLEUSE</c>.
    /// </summary>
    public void Register(Guid clsid, bool multipleUse)
    {
        if (_registered)
            return;

        // Pass the factory as a RAW IUnknown* (the §5 #2 / CsWin32 #1670 pitfall: a marshaled object silently no-ops the
        // registration under NativeAOT + ExeServer). Hold one ref for the registration's lifetime; release it on revoke.
        _factoryUnknown = ToastActivatorClassFactory.ComWrappers
            .GetOrCreateComInterfaceForObject(_factory, CreateComInterfaceFlags.None);

        uint activationFlag = (uint)(multipleUse ? REGCLS.REGCLS_MULTIPLEUSE : REGCLS.REGCLS_SINGLEUSE);

        // REGCLS_AGILE associates the class object with the neutral apartment so a thread in another apartment can
        // activate it (WASDK sets it, AppNotificationManager.cpp:200-204). But REGCLS_AGILE only works when the class
        // object is itself agile (IAgileObject / free-threaded-marshaled). WASDK's factory is a winrt::implements,
        // which IS agile; OUR factory is a source-generated ComWrappers CCW, which the OS does NOT accept as agile —
        // CoRegisterClassObject then rejects REGCLS_AGILE with E_INVALIDARG (0x80070057) regardless of CLSCTX or the
        // SINGLEUSE/MULTIPLEUSE choice (verified: every AGILE combination fails, every non-AGILE one succeeds). Try the
        // WASDK-parity AGILE registration first, and fall back to a non-AGILE registration on E_INVALIDARG so the path
        // works for the ComWrappers factory. The non-AGILE class object lives in the registering thread's apartment;
        // the Shell still reaches it for out-of-proc (CLSCTX_LOCAL_SERVER) activation across the process boundary, and
        // cross-apartment in-proc callbacks marshal normally. OPEN-QUESTION(#2): if a future build makes the CCW agile
        // (e.g. a free-threaded-marshaler aggregation), the AGILE path will take over with no code change here.
        const int E_INVALIDARG = unchecked((int)0x80070057);

        uint cookie;
        int hr = Windows.CoRegisterClassObject(
            &clsid,
            (IUnknown*)_factoryUnknown,
            (uint)CLSCTX.CLSCTX_LOCAL_SERVER,
            activationFlag | (uint)REGCLS.REGCLS_AGILE,
            &cookie);

        if (hr == E_INVALIDARG)
        {
            // Retry without REGCLS_AGILE (the ComWrappers-CCW case described above).
            hr = Windows.CoRegisterClassObject(
                &clsid,
                (IUnknown*)_factoryUnknown,
                (uint)CLSCTX.CLSCTX_LOCAL_SERVER,
                activationFlag,
                &cookie);
        }

        if (hr < 0)
        {
            if (_factoryUnknown != 0) { Marshal.Release(_factoryUnknown); _factoryUnknown = 0; }
            throw new InvalidOperationException($"CoRegisterClassObject failed (0x{hr:X8}) for CLSID {clsid:B}.");
        }

        _cookie = cookie;
        _registered = true;
    }

    /// <summary>Revoke the class object (<c>CoRevokeClassObject</c>) and release the factory IUnknown ref. Idempotent.</summary>
    public void Dispose()
    {
        if (_registered)
        {
            Windows.CoRevokeClassObject(_cookie);
            _cookie = 0;
            _registered = false;
        }
        if (_factoryUnknown != 0)
        {
            Marshal.Release(_factoryUnknown);
            _factoryUnknown = 0;
        }
    }
}
