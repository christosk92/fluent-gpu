using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.WindowsApi.Notifications;   // ToastActivatorClassFactory.ComWrappers (shared StrategyBasedComWrappers)
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;   // __uuidof<T>

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// Builds and owns a native <c>Windows.Media.Protection.MediaProtectionManager</c> configured for PlayReady, wired the
/// exact way UWP did (and the way WaveeMusic's proven recipe does) so the protected topology's Input-Trust-Authority
/// verification succeeds — the structural fix for the WinAppSDK <c>MediaFailed / SourceNotSupported / 0xC00D715B</c>
/// (<c>MF_E_TOPOLOGY_VERIFICATION_FAILED</c>) regression (<c>docs/plans/video-drm-layer-design.md §5</c>). Attach the
/// resulting manager to a <see cref="PlayReadyMediaPlayer"/> BEFORE its source is set.
/// </summary>
/// <remarks>
/// <para>
/// <b>The four properties (exact recipe).</b> On the manager's <c>Properties</c> (<c>IPropertySet</c>) it inserts:
/// <list type="number">
/// <item><c>MediaProtectionSystemIdMapping</c> → an inner <c>PropertySet</c> mapping the PlayReady system-id GUID string
/// (<c>{F4637010-…}</c>) to the class string <c>Windows.Media.Protection.PlayReady.PlayReadyWinRTTrustedInput</c>;</item>
/// <item><c>MediaProtectionSystemId</c> → the PlayReady system-id string;</item>
/// <item><c>MediaProtectionContainerGuid</c> → the DASH PlayReady container GUID (<c>{9A04F079-…}</c>);</item>
/// <item><c>UseSoftwareProtectionLayer</c> → boxed boolean <see langword="true"/> (SL2000 software path — works with no
/// hardware TEE).</item>
/// </list>
/// String/boolean values are boxed via <c>Windows.Foundation.PropertyValue.CreateString</c>/<c>CreateBoolean</c>.
/// </para>
/// <para>
/// <b>Events fire on MF worker threads.</b> <c>ServiceRequested</c> / <c>ComponentLoadFailed</c> arrive on arbitrary OS
/// threads (like SMTC's <c>ButtonPressed</c>); their CCWs (<see cref="ServiceRequestedHandler"/>,
/// <see cref="ComponentLoadFailedHandler"/>) signal <see cref="ServiceRequestFired"/> / <see cref="ComponentLoadFailed"/>
/// and complete the request. Callers that touch UI state must marshal.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.10240.0")]
public sealed unsafe class PlayReadyProtectionManager : IDisposable
{
    private static readonly StrategyBasedComWrappers ComWrappers = ToastActivatorClassFactory.ComWrappers;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private IMediaProtectionManager* _mpm;

    private ServiceRequestedHandler? _serviceHandler;
    private nint _serviceHandlerUnknown;
    private EventRegistrationToken _serviceToken;

    private ComponentLoadFailedHandler? _clfHandler;
    private nint _clfHandlerUnknown;
    private EventRegistrationToken _clfToken;

    private bool _disposed;

    /// <summary>Raised (on an MF worker thread) the instant a PlayReady service request fires — the authoritative proof
    /// that the protected topology built and ITA verification passed. The whole spike hinges on this.</summary>
    public event Action<PlayReadyServiceRequestKind>? ServiceRequestFired;

    /// <summary>Raised (on an MF worker thread) if the protected pipeline fails to load a component.</summary>
    public event Action? ComponentLoadFailed;

    /// <summary>The raw <c>IMediaProtectionManager*</c> as an <see cref="nint"/>, for
    /// <see cref="PlayReadyMediaPlayer.SetProtectionManager"/>.</summary>
    public nint ManagerPtr => (nint)_mpm;

    private PlayReadyProtectionManager() { }

    /// <summary>
    /// Activate a <c>MediaProtectionManager</c>, insert the four PlayReady properties, and subscribe
    /// <c>ServiceRequested</c> + <c>ComponentLoadFailed</c>.
    /// </summary>
    /// <param name="acquireLicense">The host license transport (the ONLY app/DRM-server-aware line): given the SOAP
    /// challenge bytes + any SOAP headers, POST to the license server and return the response bytes. Pass
    /// <see langword="null"/> to signal-only (license requests then complete <see langword="false"/>).</param>
    public static PlayReadyProtectionManager Create(
        Func<byte[], IReadOnlyDictionary<string, string>, CancellationToken, Task<byte[]>>? acquireLicense = null)
    {
        var self = new PlayReadyProtectionManager();
        try
        {
            self.Build(acquireLicense);
            return self;
        }
        catch
        {
            self.Dispose();
            throw;
        }
    }

    private void Build(Func<byte[], IReadOnlyDictionary<string, string>, CancellationToken, Task<byte[]>>? acquireLicense)
    {
        _mpm = WinRtInterop.ActivateInstance<IMediaProtectionManager>(PlayReadyGuids.RuntimeClass_MediaProtectionManager);

        ConfigureProperties();
        SubscribeServiceRequested(acquireLicense);
        SubscribeComponentLoadFailed();
    }

    private void ConfigureProperties()
    {
        IPropertySet* properties = null;
        IPropertyValueStatics* pvStatics = null;
        try
        {
            WinRtInterop.ThrowIfFailed(_mpm->get_Properties(&properties), "MediaProtectionManager.get_Properties");
            pvStatics = WinRtInterop.GetActivationFactory<IPropertyValueStatics>(PlayReadyGuids.RuntimeClass_PropertyValue);

            // 1. MediaProtectionSystemIdMapping → inner PropertySet { systemId → PlayReadyWinRTTrustedInput }.
            IPropertySet* mapping = WinRtInterop.CreatePropertySet();
            try
            {
                IInspectable* classNameBoxed = WinRtInterop.BoxString(pvStatics, PlayReadyGuids.PlayReadyWinRTTrustedInput);
                try
                {
                    WinRtInterop.Insert(mapping, PlayReadyGuids.PlayReadyProtectionSystemId, classNameBoxed);
                }
                finally { classNameBoxed->Release(); }

                WinRtInterop.Insert(properties, PlayReadyGuids.KeyMediaProtectionSystemIdMapping, (IInspectable*)mapping);
            }
            finally { mapping->Release(); }

            // 2. MediaProtectionSystemId → system-id string.
            InsertBoxedString(properties, pvStatics, PlayReadyGuids.KeyMediaProtectionSystemId, PlayReadyGuids.PlayReadyProtectionSystemId);
            // 3. MediaProtectionContainerGuid → container GUID string.
            InsertBoxedString(properties, pvStatics, PlayReadyGuids.KeyMediaProtectionContainerGuid, PlayReadyGuids.PlayReadyContainerGuid);
            // 4. UseSoftwareProtectionLayer → boxed boolean true.
            IInspectable* boolBoxed = WinRtInterop.BoxBoolean(pvStatics, true);
            try { WinRtInterop.Insert(properties, PlayReadyGuids.KeyUseSoftwareProtectionLayer, boolBoxed); }
            finally { boolBoxed->Release(); }
        }
        finally
        {
            if (pvStatics != null) pvStatics->Release();
            if (properties != null) properties->Release();
        }
    }

    private static void InsertBoxedString(IPropertySet* set, IPropertyValueStatics* pv, string key, string value)
    {
        IInspectable* boxed = WinRtInterop.BoxString(pv, value);
        try { WinRtInterop.Insert(set, key, boxed); }
        finally { boxed->Release(); }
    }

    private void SubscribeServiceRequested(
        Func<byte[], IReadOnlyDictionary<string, string>, CancellationToken, Task<byte[]>>? acquireLicense)
    {
        _serviceHandler = new ServiceRequestedHandler(
            kind => ServiceRequestFired?.Invoke(kind),
            acquireLicense,
            _cts.Token);

        _serviceHandlerUnknown = ComWrappers.GetOrCreateComInterfaceForObject(_serviceHandler, CreateComInterfaceFlags.None);
        EventRegistrationToken token;
        int hr = _mpm->add_ServiceRequested((IServiceRequestedEventHandler*)_serviceHandlerUnknown, &token);
        if (hr < 0)
        {
            Marshal.Release(_serviceHandlerUnknown);
            _serviceHandlerUnknown = 0;
            _serviceHandler = null;
            WinRtInterop.ThrowIfFailed(hr, "add_ServiceRequested");
        }
        _serviceToken = token;
    }

    private void SubscribeComponentLoadFailed()
    {
        _clfHandler = new ComponentLoadFailedHandler(() => ComponentLoadFailed?.Invoke());
        _clfHandlerUnknown = ComWrappers.GetOrCreateComInterfaceForObject(_clfHandler, CreateComInterfaceFlags.None);
        EventRegistrationToken token;
        int hr = _mpm->add_ComponentLoadFailed((IComponentLoadFailedEventHandler*)_clfHandlerUnknown, &token);
        if (hr < 0)
        {
            // Best-effort — the diagnostic is optional; don't fail construction over it.
            Marshal.Release(_clfHandlerUnknown);
            _clfHandlerUnknown = 0;
            _clfHandler = null;
            return;
        }
        _clfToken = token;
    }

    /// <summary>Unhook the event handlers and release the manager. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _cts.Cancel(); } catch { /* ignore */ }

            if (_mpm != null)
            {
                if (_serviceHandlerUnknown != 0)
                {
                    _mpm->remove_ServiceRequested(_serviceToken);
                    Marshal.Release(_serviceHandlerUnknown);
                    _serviceHandlerUnknown = 0;
                }
                if (_clfHandlerUnknown != 0)
                {
                    _mpm->remove_ComponentLoadFailed(_clfToken);
                    Marshal.Release(_clfHandlerUnknown);
                    _clfHandlerUnknown = 0;
                }
                _mpm->Release();
                _mpm = null;
            }

            _serviceHandler = null;
            _clfHandler = null;
            _cts.Dispose();
        }
    }
}
