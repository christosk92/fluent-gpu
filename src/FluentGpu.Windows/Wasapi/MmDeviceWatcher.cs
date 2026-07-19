using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Media;
using FluentGpu.Signals;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Windows.Wasapi;

/// <summary>
/// The follow-default / device-loss watcher (spec §7.9) — an <c>IMMNotificationClient</c> registered on the device
/// enumerator. A default-render-device change fires <see cref="DefaultDeviceChanged"/> (which the audio session uses to
/// rebuild ONLY the sink under a live graph — sources/queue/position survive). Implemented as a hand-vtable CCW (the
/// established <see cref="FluentGpu.Windows"/> convention, modeled on <c>MediaEngineNotifyCcw</c>) so it is AOT-clean with
/// no <c>ComWrappers</c>. On-box only. The M4 flip moves the callback handling onto the cold device thread.
/// </summary>
public sealed unsafe class MmDeviceWatcher : IDeviceWatcher, IDisposable
{
    private const uint ClsctxAll = 0x17;   // CLSCTX_ALL

    private readonly Signal<AudioDeviceState> _state = new(AudioDeviceState.Building);
    private IMMDeviceEnumerator* _enumerator;
    private MmNotificationCcw* _ccw;
    private GCHandle _self;
    private bool _disposed;

    /// <summary>Register for default-device change notifications on the default render endpoint.</summary>
    public MmDeviceWatcher()
    {
        try { Register(); _state.Value = AudioDeviceState.Running; }
        catch (Exception ex) { Debug.WriteLine($"MmDeviceWatcher register failed: {ex.Message}"); _state.Value = AudioDeviceState.Faulted; }
    }

    /// <inheritdoc/>
    public IReadSignal<AudioDeviceState> State => _state;
    /// <inheritdoc/>
    public event Action? DefaultDeviceChanged;

    internal void RaiseDefaultChanged() => DefaultDeviceChanged?.Invoke();

    private void Register()
    {
        _ = CoInitializeEx(null, (uint)(COINIT.COINIT_MULTITHREADED | COINIT.COINIT_DISABLE_OLE1DDE));
        Guid clsid = CLSID.CLSID_MMDeviceEnumerator;
        Guid iid = IID.IID_IMMDeviceEnumerator;
        IMMDeviceEnumerator* enumerator;
        if (CoCreateInstance(&clsid, null, ClsctxAll, &iid, (void**)&enumerator) < 0) throw new InvalidOperationException("CoCreateInstance(MMDeviceEnumerator)");
        _enumerator = enumerator;

        _self = GCHandle.Alloc(this);
        _ccw = MmNotificationCcw.Create(GCHandle.ToIntPtr(_self));
        if (enumerator->RegisterEndpointNotificationCallback((IMMNotificationClient*)_ccw) < 0)
            throw new InvalidOperationException("RegisterEndpointNotificationCallback");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_enumerator is not null && _ccw is not null)
            _enumerator->UnregisterEndpointNotificationCallback((IMMNotificationClient*)_ccw);
        if (_ccw is not null) { MmNotificationCcw.Destroy(_ccw); _ccw = null; }
        if (_enumerator is not null) { _enumerator->Release(); _enumerator = null; }
        if (_self.IsAllocated) _self.Free();
    }
}

/// <summary>Hand-rolled CCW for <c>IMMNotificationClient</c> (8 vtable slots). Carries a <see cref="GCHandle"/> to its
/// owning <see cref="MmDeviceWatcher"/> so the static <c>[UnmanagedCallersOnly]</c> thunks route back to the instance.
/// Mirrors <c>MediaEngineNotifyCcw</c>. Only <c>OnDefaultDeviceChanged</c> (render/console) raises an event; the rest
/// return <c>S_OK</c>.</summary>
internal unsafe struct MmNotificationCcw
{
    public void** Vtbl;   // COM "this" vptr (first field)
    public int Rc;
    public nint Owner;    // GCHandle.ToIntPtr(owner)

    private static readonly void** _vtbl = Build();
    private static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(8, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<MmNotificationCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<MmNotificationCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<MmNotificationCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<MmNotificationCcw*, ushort*, uint, int>)&OnDeviceStateChanged;
        v[4] = (delegate* unmanaged[MemberFunction]<MmNotificationCcw*, ushort*, int>)&OnDeviceAdded;
        v[5] = (delegate* unmanaged[MemberFunction]<MmNotificationCcw*, ushort*, int>)&OnDeviceRemoved;
        v[6] = (delegate* unmanaged[MemberFunction]<MmNotificationCcw*, EDataFlow, ERole, ushort*, int>)&OnDefaultDeviceChanged;
        v[7] = (delegate* unmanaged[MemberFunction]<MmNotificationCcw*, ushort*, PROPERTYKEY, int>)&OnPropertyValueChanged;
        return v;
    }

    public static MmNotificationCcw* Create(nint owner)
    {
        var p = (MmNotificationCcw*)NativeMemory.Alloc((nuint)sizeof(MmNotificationCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->Owner = owner;
        return p;
    }
    public static void Destroy(MmNotificationCcw* p) => NativeMemory.Free(p);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int QueryInterface(MmNotificationCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return unchecked((int)0x80004003);
        Guid iunk = IID.IID_IUnknown, inc = IID.IID_IMMNotificationClient;
        if (*riid == iunk || *riid == inc) { Interlocked.Increment(ref self->Rc); *ppv = self; return 0; }
        *ppv = null; return unchecked((int)0x80004002);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint AddRef(MmNotificationCcw* self) => (uint)Interlocked.Increment(ref self->Rc);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint Release(MmNotificationCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int OnDeviceStateChanged(MmNotificationCcw* self, ushort* id, uint newState) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int OnDeviceAdded(MmNotificationCcw* self, ushort* id) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int OnDeviceRemoved(MmNotificationCcw* self, ushort* id) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int OnDefaultDeviceChanged(MmNotificationCcw* self, EDataFlow flow, ERole role, ushort* id)
    {
        try
        {
            if (flow == EDataFlow.eRender && role == ERole.eConsole
                && GCHandle.FromIntPtr(self->Owner).Target is MmDeviceWatcher w)
                w.RaiseDefaultChanged();
        }
        catch { /* never throw across the COM boundary */ }
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int OnPropertyValueChanged(MmNotificationCcw* self, ushort* id, PROPERTYKEY key) => 0;
}
