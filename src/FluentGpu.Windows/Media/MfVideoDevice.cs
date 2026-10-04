using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The process-wide owner of the D3D11 video device and <c>IMFDXGIDeviceManager</c> that every clear-video
/// <see cref="VideoMediaEngine"/> decodes on — ONE per adapter LUID, shared by all live engines and kept briefly after the last
/// one leaves, so a warm-engine miss (a faulted rebuild, the throwaway concurrent lease, the 30 s idle teardown) pays only
/// <c>CoCreateInstance</c> + engine creation instead of a fresh <c>D3D11CreateDevice</c> + <c>MFCreateDXGIDeviceManager</c>
/// + <c>ResetDevice</c> + <c>MFStartup</c>. Chromium and Firefox do the same through the process-singleton
/// <c>MFLockDXGIDeviceManager</c>; a static owner here keeps the policy (<see cref="SharedDeviceCache{T}"/>) unit-testable.
/// <para><b>When the device is replaced.</b> Only on an adapter change (the renderer's LUID differs from the cached device's)
/// or device removal (<see cref="SharedDeviceCache{T}.Lease.MarkRemoved"/>, called by the engine that observed it, or found by the
/// next acquire asking the device). The old device then lives exactly until its last lease is released. The device manager is
/// reset once, at creation; nothing resets it afterwards, so every engine's open handles stay valid.</para>
/// <para><b>MFStartup</b> is held for the device's lifetime (one start per device, not per engine): every lease therefore
/// runs with Media Foundation started, and the matching <c>MFShutdown</c> runs when the device is finally destroyed.</para>
/// </summary>
internal sealed unsafe class MfVideoDevice
{
    private const uint MFSTARTUP_FULL_ = 0;
    private const int EFail = unchecked((int)0x80004005);
    // How long an unleased device is kept for the next engine before it is destroyed (GPU memory vs. bring-up latency).
    private const int LingerMs = 60_000;

    private static readonly object s_timerGate = new();
    private static Timer? s_sweepTimer;

    /// <summary>The process-wide cache. Key = adapter LUID packed by <see cref="LuidKey"/> (0 = the default adapter).</summary>
    internal static readonly SharedDeviceCache<MfVideoDevice> Shared = new(
        create: static key => Create(key),
        destroy: static d => d.Destroy(),
        isRemoved: static d => d.IsRemoved(),
        clock: static () => Environment.TickCount64,
        lingerMs: LingerMs,
        scheduleSweep: static ms => ScheduleSweep(ms));

    private readonly long _key;
    private ID3D11Device* _d3d;
    private IMFDXGIDeviceManager* _manager;
    private bool _mfStarted;

    private MfVideoDevice(long key) { _key = key; }

    /// <summary>The shared D3D11 device. BORROWED: valid only while the lease it came from is held; never Release it.</summary>
    internal ID3D11Device* D3d => _d3d;

    /// <summary>The shared DXGI device manager over <see cref="D3d"/>. BORROWED, like <see cref="D3d"/>.</summary>
    internal IMFDXGIDeviceManager* Manager => _manager;

    /// <summary>Leases the shared device for the renderer's adapter; null when it cannot be created (logged). Dispose the lease
    /// (engine thread or any other) when the engine's COM objects are released.</summary>
    internal static SharedDeviceCache<MfVideoDevice>.Lease? Acquire()
    {
        long key = FluentGpu.Rhi.D3D12.GpuAdapterInfo.TryGetAdapterLuid(out LUID luid) ? LuidKey(luid.HighPart, luid.LowPart) : 0;
        return Shared.Acquire(key);
    }

    /// <summary>Packs an adapter LUID into the cache key. 0 is reserved for "no LUID published: the default adapter".</summary>
    internal static long LuidKey(int highPart, uint lowPart) => ((long)highPart << 32) | lowPart;

    // Cold path: runs under the cache's gate on the acquiring (engine MTA) thread.
    private static MfVideoDevice? Create(long key)
    {
        int hr = MFStartup((uint)MF.MF_VERSION, MFSTARTUP_FULL_);
        if (hr < 0) return Fail("MFStartup", hr);
        var device = new MfVideoDevice(key) { _mfStarted = true };
        if ((hr = device.CreateD3D11AndManager()) < 0) { device.Destroy(); return null; }
        return device;
    }

    private int CreateD3D11AndManager()
    {
        ID3D11DeviceContext* ctx = null;
        uint flags = (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT
                   | (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;

        // Land decode on the SAME adapter the D3D12 renderer chose (GpuAdapterInfo, published at device init). On a
        // hybrid machine the D3D11 default adapter can be the OTHER GPU. No texture sharing (DComp surface handle
        // only) — this is decode/present locality, not interop correctness. Cold path on the MTA engine thread.
        // Fallbacks: LUID unset or enum failure ⇒ the historical default-adapter path, unchanged.
        LUID renderLuid = default;
        IDXGIAdapter1* adapter = null;
        if (_key != 0)
        {
            renderLuid.HighPart = (int)(_key >> 32);
            renderLuid.LowPart = (uint)_key;
            IDXGIFactory4* factory = null;
            if ((int)CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) >= 0 && factory != null)
            {
                if ((int)factory->EnumAdapterByLuid(renderLuid, __uuidof<IDXGIAdapter1>(), (void**)&adapter) < 0)
                    adapter = null;
                factory->Release();
            }
        }

        // Explicit adapter REQUIRES D3D_DRIVER_TYPE_UNKNOWN (HARDWARE + adapter is E_INVALIDARG).
        ID3D11Device* d3d = null;
        int hr = D3D11CreateDevice((IDXGIAdapter*)adapter,
                                   adapter != null ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                                   HMODULE.NULL, flags, null, 0, 7 /*D3D11_SDK_VERSION*/, &d3d, null, &ctx);
        if (adapter != null && (hr < 0 || d3d == null))
        {
            // Pinned adapter refused a D3D11 device (driver quirk / feature gap): fall back rather than fail video —
            // decode on the wrong GPU beats no decode.
            Diag.Line($"[video.d3d11] adapter-pinned D3D11CreateDevice failed hr=0x{(uint)hr:X8}; falling back to default adapter");
            hr = D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.NULL, flags,
                                   null, 0, 7 /*D3D11_SDK_VERSION*/, &d3d, null, &ctx);
            adapter->Release(); adapter = null;
        }
        bool pinned = adapter != null;
        if (adapter != null) adapter->Release();
        if (hr < 0 || d3d == null) return LogFail("D3D11CreateDevice", hr);
        // ALWAYS-ON line (once per device creation — now once per adapter per process, never per engine or per frame):
        // WHICH GPU decodes — the field evidence that a hybrid machine decodes and renders on the same adapter.
        Diag.Line($"[video.d3d11] decodeAdapter={(pinned ? $"pinned-to-render-luid 0x{renderLuid.HighPart:X8}:{renderLuid.LowPart:X8}" : "default")}");
        _d3d = d3d;
        if (ctx != null) ctx->Release();

        // Mark multithread-protected. REQUIRED when the D3D11 device is shared with Media Foundation — MF drives the
        // device from its own worker threads and without this it deadlocks during source resolution (the hang the M3
        // probe misdiagnosed as a driver bug); it is doubly required now that several engines share the device.
        // ID3D10Multithread vtable: 0-2 IUnknown, 3 Enter, 4 Leave, 5 SetMultithreadProtected(BOOL)->BOOL,
        // 6 GetMultithreadProtected. Use slot 5 (an earlier version wrongly called slot 3 = Enter, which is why
        // protection was never actually enabled).
        Guid iidMt = new(0x9b7e4e00, 0x342c, 0x4106, 0xa1, 0x9f, 0x4f, 0x27, 0x04, 0xf6, 0x89, 0xf0);
        void* mt = null;
        if (d3d->QueryInterface(&iidMt, &mt) >= 0 && mt != null)
        {
            var setProt = (delegate* unmanaged[MemberFunction]<void*, int, int>)(*(void***)mt)[5];
            setProt(mt, 1);
            ((IUnknown*)mt)->Release();
        }

        uint resetToken = 0;
        IMFDXGIDeviceManager* dm = null;
        if ((hr = MFCreateDXGIDeviceManager(&resetToken, &dm)) < 0 || dm == null) return LogFail("MFCreateDXGIDeviceManager", hr);
        if ((hr = dm->ResetDevice((IUnknown*)d3d, resetToken)) < 0) { dm->Release(); return LogFail("IMFDXGIDeviceManager::ResetDevice", hr); }
        _manager = dm;
        return 0;
    }

    // Any thread (the cache calls it outside its gate once no lease remains). Idempotent; releases whatever was created.
    private void Destroy()
    {
        if (_manager != null) { _manager->Release(); _manager = null; }
        if (_d3d != null) { _d3d->Release(); _d3d = null; }
        if (_mfStarted) { MFShutdown(); _mfStarted = false; }
    }

    private bool IsRemoved() => _d3d != null && (int)_d3d->GetDeviceRemovedReason() != 0;

    private static int LogFail(string what, int hr)
    {
        Console.Error.WriteLine($"VideoMediaEngine: {what} hr=0x{(uint)hr:X8}");
        return hr < 0 ? hr : EFail;
    }

    private static MfVideoDevice? Fail(string what, int hr)
    {
        LogFail(what, hr);
        return null;
    }

    private static void ScheduleSweep(int delayMs)
    {
        lock (s_timerGate)
        {
            s_sweepTimer ??= new Timer(static _ => Shared.Sweep(), null, Timeout.Infinite, Timeout.Infinite);
            s_sweepTimer.Change(Math.Max(1, delayMs), Timeout.Infinite);
        }
    }
}

/// <summary>
/// The pure refcount / key policy behind <see cref="MfVideoDevice"/>, generic over the device so it runs headless: one entry
/// per key (adapter LUID), reference-counted by <see cref="Lease"/>s, lingering unleased for <c>lingerMs</c> so the next
/// acquire reuses it, replaced only when the key's device is reported removed, and — once an acquire for ANOTHER key arrives
/// (an adapter change) — destroyed as soon as it has no lease. A replaced or removed device is destroyed when its LAST lease
/// is released, never while an engine still decodes on it. <c>create</c> runs under the gate (so two racing acquires share
/// one device); <c>destroy</c> and <c>scheduleSweep</c> run outside it.
/// </summary>
internal sealed class SharedDeviceCache<T> where T : class
{
    internal sealed class Entry
    {
        internal readonly long Key;
        internal readonly T Device;
        internal int Refs;
        // Replaced (removed device / superseded): no new lease may take it; destroyed at Refs == 0.
        internal bool Retired;
        internal long IdleSince;
        internal Entry(long key, T device) { Key = key; Device = device; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _current = new();
    private readonly Func<long, T?> _create;
    private readonly Action<T> _destroy;
    private readonly Func<T, bool> _isRemoved;
    private readonly Func<long> _clock;
    private readonly int _lingerMs;
    private readonly Action<int> _scheduleSweep;
    private int _alive;

    internal SharedDeviceCache(Func<long, T?> create, Action<T> destroy, Func<T, bool> isRemoved, Func<long> clock,
                               int lingerMs, Action<int> scheduleSweep)
    {
        _create = create; _destroy = destroy; _isRemoved = isRemoved; _clock = clock;
        _lingerMs = lingerMs; _scheduleSweep = scheduleSweep;
    }

    /// <summary>Devices created and not yet destroyed (current plus retired-but-still-leased).</summary>
    internal int AliveDevices { get { lock (_gate) return _alive; } }

    /// <summary>Leases currently held on the key's current device (0 when none, or when it is only lingering).</summary>
    internal int LeaseCount(long key)
    {
        lock (_gate) return _current.TryGetValue(key, out Entry? e) ? e.Refs : 0;
    }

    /// <summary>Leases the key's device, creating it when there is none usable; null when <c>create</c> returned null.</summary>
    internal Lease? Acquire(long key)
    {
        List<T>? doomed = null;
        Lease? lease = null;
        lock (_gate)
        {
            // Adapter change: devices of other keys that nobody uses are dead weight now.
            List<long>? stale = null;
            foreach (var kv in _current)
                if (kv.Key != key && kv.Value.Refs == 0) (stale ??= new List<long>()).Add(kv.Key);
            if (stale != null)
                foreach (long k in stale) Retire(_current[k], ref doomed);

            if (_current.TryGetValue(key, out Entry? e) && _isRemoved(e.Device))
            {
                Retire(e, ref doomed);
                e = null;
            }
            if (e == null)
            {
                T? device = _create(key);
                if (device != null)
                {
                    e = new Entry(key, device);
                    _current[key] = e;
                    _alive++;
                }
            }
            if (e != null)
            {
                e.Refs++;
                lease = new Lease(this, e);
            }
        }
        DestroyAll(doomed);
        return lease;
    }

    /// <summary>Destroys every unleased device whose linger elapsed and re-arms the sweep for the rest. Driven by the owner's
    /// timer (and by tests with a fake clock).</summary>
    internal void Sweep()
    {
        List<T>? doomed = null;
        int nextDelay = -1;
        lock (_gate)
        {
            long now = _clock();
            List<Entry>? expired = null;
            foreach (Entry e in _current.Values)
            {
                if (e.Refs != 0) continue;
                long left = _lingerMs - (now - e.IdleSince);
                if (left <= 0) (expired ??= new List<Entry>()).Add(e);
                else if (nextDelay < 0 || left < nextDelay) nextDelay = (int)left;
            }
            if (expired != null)
                foreach (Entry e in expired) Retire(e, ref doomed);
        }
        DestroyAll(doomed);
        if (nextDelay >= 0) _scheduleSweep(nextDelay);
    }

    // Under the gate. Takes the entry out of circulation; it is destroyed now if nobody holds it, else at its last release.
    private void Retire(Entry e, ref List<T>? doomed)
    {
        if (_current.TryGetValue(e.Key, out Entry? cur) && ReferenceEquals(cur, e)) _current.Remove(e.Key);
        if (e.Retired) return;
        e.Retired = true;
        if (e.Refs == 0) (doomed ??= new List<T>()).Add(e.Device);
    }

    private void Release(Entry e)
    {
        List<T>? doomed = null;
        bool armSweep = false;
        lock (_gate)
        {
            if (--e.Refs != 0) return;
            if (e.Retired) (doomed ??= new List<T>()).Add(e.Device);
            else { e.IdleSince = _clock(); armSweep = true; }
        }
        DestroyAll(doomed);
        if (armSweep) _scheduleSweep(_lingerMs);
    }

    private void MarkRemoved(Entry e)
    {
        List<T>? doomed = null;
        lock (_gate) Retire(e, ref doomed);
        DestroyAll(doomed);
    }

    private void DestroyAll(List<T>? doomed)
    {
        if (doomed == null) return;
        foreach (T d in doomed)
        {
            lock (_gate) _alive--;
            _destroy(d);
        }
    }

    /// <summary>One engine's hold on a shared device. Disposing it (idempotent) releases the hold.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly SharedDeviceCache<T> _owner;
        private readonly Entry _entry;
        private int _released;

        internal Lease(SharedDeviceCache<T> owner, Entry entry) { _owner = owner; _entry = entry; }

        /// <summary>The shared device; valid until this lease is disposed.</summary>
        internal T Device => _entry.Device;

        /// <summary>The key (adapter LUID) the device was created for.</summary>
        internal long Key => _entry.Key;

        /// <summary>This holder observed the device removed: the next acquire creates a fresh one. The removed device stays
        /// alive for the leases still holding it and is destroyed when the last of them is disposed.</summary>
        internal void MarkRemoved()
        {
            if (Volatile.Read(ref _released) == 0) _owner.MarkRemoved(_entry);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _owner.Release(_entry);
        }
    }
}
