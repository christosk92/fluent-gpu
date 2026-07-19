using System;
using System.Runtime.Versioning;
using FluentGpu.WindowsApi.Notifications;   // HStringHandle (shared cold-COM helper)
using TerraFX.Interop;                       // INativeGuid (constraint for __uuidof<T>)
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.WinRT.WinRT;         // RoActivateInstance, RoGetActivationFactory
using static TerraFX.Interop.Windows.Windows;     // __uuidof<T>

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// Small shared WinRT call-OUT helpers reused across the PlayReady components: activation
/// (<c>RoActivateInstance</c>/<c>RoGetActivationFactory</c>), boxing scalars into <c>IPropertyValue</c>, inserting into a
/// WinRT <c>PropertySet</c> (via <c>IMap&lt;String, Object&gt;</c>), and building a <c>Windows.Foundation.Uri</c>. All of
/// it mirrors the exact patterns in <c>Media/SystemMediaControls.cs</c> (activation, <c>HStringHandle</c>,
/// AddRef/Release discipline) — no CsWinRT, no <c>ComWrappers</c> on the call-out path.
/// </summary>
[SupportedOSPlatform("windows10.0.10240.0")]
internal static unsafe class WinRtInterop
{
    /// <summary>Throw an <see cref="InvalidOperationException"/> carrying the HRESULT when <paramref name="hr"/> failed.</summary>
    public static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed (0x{(uint)hr:X8}).");
    }

    /// <summary>Activate a default-constructible runtime class and return its <c>IInspectable*</c> (AddRef-owned).</summary>
    public static IInspectable* ActivateInstance(string runtimeClass)
    {
        IInspectable* insp = null;
        using var hc = new HStringHandle(runtimeClass);
        ThrowIfFailed(RoActivateInstance(hc.Value, &insp), $"RoActivateInstance({runtimeClass})");
        return insp;
    }

    /// <summary>Activate a runtime class and QI straight to <typeparamref name="T"/> (AddRef-owned). Releases the
    /// intermediate <c>IInspectable</c>.</summary>
    public static T* ActivateInstance<T>(string runtimeClass) where T : unmanaged, INativeGuid
    {
        IInspectable* insp = ActivateInstance(runtimeClass);
        try
        {
            T* result = null;
            Guid iid = __uuidof<T>();
            ThrowIfFailed(insp->QueryInterface(&iid, (void**)&result), $"QI {typeof(T).Name} ({runtimeClass})");
            return result;
        }
        finally
        {
            insp->Release();
        }
    }

    /// <summary>Get the activation factory of <paramref name="runtimeClass"/> as <typeparamref name="T"/>
    /// (AddRef-owned) — the statics/factory accessor (e.g. <c>IAdaptiveMediaSourceStatics</c>).</summary>
    public static T* GetActivationFactory<T>(string runtimeClass) where T : unmanaged, INativeGuid
    {
        T* factory = null;
        using var hc = new HStringHandle(runtimeClass);
        Guid iid = __uuidof<T>();
        ThrowIfFailed(RoGetActivationFactory(hc.Value, &iid, (void**)&factory),
            $"RoGetActivationFactory({runtimeClass} → {typeof(T).Name})");
        return factory;
    }

    /// <summary>QI <paramref name="unknown"/> to <typeparamref name="T"/> (AddRef-owned). Throws on failure.</summary>
    public static T* QueryInterface<T>(IInspectable* unknown, string what) where T : unmanaged, INativeGuid
    {
        T* result = null;
        Guid iid = __uuidof<T>();
        ThrowIfFailed(unknown->QueryInterface(&iid, (void**)&result), $"QI {typeof(T).Name} ({what})");
        return result;
    }

    // ── PropertyValue boxing ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Box a string into an <c>IPropertyValue</c> (as <c>IInspectable*</c>, AddRef-owned) via
    /// <c>Windows.Foundation.PropertyValue.CreateString</c>.</summary>
    public static IInspectable* BoxString(IPropertyValueStatics* pvStatics, string value)
    {
        IInspectable* boxed = null;
        using var hs = new HStringHandle(value);
        ThrowIfFailed(pvStatics->CreateString(hs.Value, &boxed), "PropertyValue.CreateString");
        return boxed;
    }

    /// <summary>Box a boolean into an <c>IPropertyValue</c> (as <c>IInspectable*</c>, AddRef-owned) via
    /// <c>Windows.Foundation.PropertyValue.CreateBoolean</c> (WinRT boolean ABI: 1 byte).</summary>
    public static IInspectable* BoxBoolean(IPropertyValueStatics* pvStatics, bool value)
    {
        IInspectable* boxed = null;
        ThrowIfFailed(pvStatics->CreateBoolean((byte)(value ? 1 : 0), &boxed), "PropertyValue.CreateBoolean");
        return boxed;
    }

    // ── PropertySet / IMap<String, Object> ──────────────────────────────────────────────────────────────────────────

    /// <summary>Activate a fresh <c>Windows.Foundation.Collections.PropertySet</c> and return it as <c>IPropertySet*</c>
    /// (AddRef-owned).</summary>
    public static IPropertySet* CreatePropertySet() => ActivateInstance<IPropertySet>(PlayReadyGuids.RuntimeClass_PropertySet);

    /// <summary>
    /// Insert <paramref name="value"/> under <paramref name="key"/> into the <c>PropertySet</c> <paramref name="set"/>.
    /// <c>IPropertySet</c> is a marker with no members of its own, so this QIs it to
    /// <c>IMap&lt;HSTRING, IInspectable&gt;</c> (IID <see cref="PlayReadyGuids.IID_IMap_String_Object"/>) and calls
    /// <c>Insert</c> (vtable slot 10: <c>[6]Lookup [7]get_Size [8]HasKey [9]GetView [10]Insert [11]Remove [12]Clear</c>).
    /// </summary>
    public static void Insert(IPropertySet* set, string key, IInspectable* value)
    {
        var insp = (IInspectable*)set;
        IInspectable* map = null;
        Guid iid = PlayReadyGuids.IID_IMap_String_Object;
        ThrowIfFailed(insp->QueryInterface(&iid, (void**)&map), "QI IMap<String,Object>");
        try
        {
            using var hk = new HStringHandle(key);
            byte replaced;
            void** vtbl = *(void***)map;
            // Insert(HSTRING key, IInspectable* value, boolean* replaced) — slot 10.
            int hr = ((delegate* unmanaged<IInspectable*, HSTRING, IInspectable*, byte*, int>)vtbl[10])(
                map, hk.Value, value, &replaced);
            ThrowIfFailed(hr, $"IMap.Insert(\"{key}\")");
        }
        finally
        {
            map->Release();
        }
    }

    // ── Uri ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Build a <c>Windows.Foundation.Uri</c> from <paramref name="uri"/> (AddRef-owned) — same
    /// <c>IUriRuntimeClassFactory.CreateUri</c> path <c>SystemMediaControls.SetThumbnail</c> uses.</summary>
    public static IUriRuntimeClass* CreateUri(string uri)
    {
        IUriRuntimeClassFactory* factory = GetActivationFactory<IUriRuntimeClassFactory>(PlayReadyGuids.RuntimeClass_Uri);
        try
        {
            IUriRuntimeClass* result = null;
            using var hs = new HStringHandle(uri);
            ThrowIfFailed(factory->CreateUri(hs.Value, &result), "Uri.CreateUri");
            return result;
        }
        finally
        {
            factory->Release();
        }
    }
}
