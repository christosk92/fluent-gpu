using System;
using System.Threading;
using FluentGpu.Media;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The protected composition keeper: ONE tiny hardware-protected swap chain the main window's video tree holds for the life of the
/// process (<see cref="CompositionVideoPresenter.SetKeeperSurface"/>), so Windows' desktop compositor enters hardware-protected
/// composition once instead of at every protected video's arrival and departure.
/// <para><b>Why.</b> The first hardware-protected (<c>DXGI_SWAP_CHAIN_FLAG_HW_PROTECTED</c>) surface that enters DWM's tree makes DWM
/// destroy and re-create its own swap-chain buffers protected, and the last one that leaves makes it do so again ("a heavyweight
/// operation", PFND3DWDDM2_0DDI_SETHARDWAREPROTECTION). Measured on the owner's machine 2026-11-12 with no DRM at all
/// (C:\WAVEE\scratch\fable-dwm\hwprot.cpp): the first attach froze the whole desktop 200-544 ms, the last destruction 140-380 ms, and
/// with a 16x16 protected keeper alive every later protected arrival, departure, move and resize was free. The keeper held while
/// occluded, at opacity 0.004, in another window, and static after two presents. In Wavee with an external keeper the protected music
/// video attached with no stall where earlier runs froze 583 and 675 ms.</para>
/// <para><b>What it is.</b> Its own D3D11 device on the renderer's adapter (video support, like the protected runtime's device), a
/// DirectComposition surface handle, and a 16x16 NV12 flip-sequential swap chain with <c>YUV_VIDEO | HW_PROTECTED</c> over that handle,
/// cleared to video black under hardware protection and presented twice, then never touched again (zero steady-state work). The
/// adapter's hardware-content-protection teardown event (sleep / resume, a driver reset) is watched so the owner can build a new one.</para>
/// <para>Cold: created on a pool thread, disposed on the render thread (or the device owner at exit). Nothing here throws: a step
/// that fails ends the creation with a reason and leaves nothing behind.</para>
/// </summary>
internal sealed unsafe class ProtectedCompositionKeeper : IDisposable
{
    private const int Side = 16;
    private const uint D3D11SdkVersion = 7;

    private ID3D11Device* _d3d;
    private IDXGISwapChain1* _chain;
    private HANDLE _handle;
    private IDXGIAdapter3* _adapter3;
    private uint _teardownCookie;
    private bool _teardownRegistered;
    private AutoResetEvent? _teardownEvent;
    private RegisteredWaitHandle? _teardownWait;
    private int _tornDown;

    private ProtectedCompositionKeeper() { }

    /// <summary>The DirectComposition surface handle the swap chain presents into (what the presenter wraps).</summary>
    internal nuint SurfaceHandle => (nuint)(nint)_handle;

    /// <summary>The adapter the keeper lives on (<see cref="ProtectedKeeperPolicy.AdapterKey"/>).</summary>
    internal ulong AdapterKey { get; private set; }

    /// <summary>The adapter reported a hardware content protection teardown (sleep / resume, a driver reset): the protected surface
    /// is gone and DWM has left protected composition. Any thread.</summary>
    internal bool TornDown => Volatile.Read(ref _tornDown) != 0;

    /// <summary>The keeper's own device's removal reason (0 = healthy). Render thread, at a low cadence.</summary>
    internal int DeviceRemovedReason => _d3d == null ? 0 : (int)_d3d->GetDeviceRemovedReason();

    /// <summary>The persisted key (<see cref="ProtectedKeeperPolicy.AdapterKey"/>) of the adapter with this packed LUID; 0 when it
    /// cannot be read. Cold (one factory and one adapter lookup).</summary>
    internal static ulong AdapterKeyFor(long packedLuid)
    {
        if (packedLuid == 0) return 0;
        IDXGIFactory4* factory = null;
        if ((int)CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) < 0 || factory == null) return 0;
        IDXGIAdapter1* adapter = null;
        LUID luid = default;
        luid.LowPart = unchecked((uint)packedLuid);
        luid.HighPart = (int)(packedLuid >> 32);
        ulong key = 0;
        if ((int)factory->EnumAdapterByLuid(luid, __uuidof<IDXGIAdapter1>(), (void**)&adapter) >= 0 && adapter != null)
        {
            DXGI_ADAPTER_DESC1 d = default;
            if ((int)adapter->GetDesc1(&d) >= 0) key = ProtectedKeeperPolicy.AdapterKey(d.VendorId, d.DeviceId);
            adapter->Release();
        }
        factory->Release();
        return key;
    }

    /// <summary>Build a keeper on the adapter with this packed LUID (0 = the default adapter). Null with <paramref name="failure"/>
    /// naming the step that failed (this adapter has no hardware protection, the driver refused NV12 protected swap chains, ...).
    /// Any thread; cold.</summary>
    internal static ProtectedCompositionKeeper? TryCreate(long packedLuid, out string failure)
    {
        var k = new ProtectedCompositionKeeper();
        failure = k.Create(packedLuid);
        if (failure.Length == 0) return k;
        k.Dispose();
        return null;
    }

    // "" on success, else what failed. Whatever was created stays in the fields for Dispose.
    private string Create(long packedLuid)
    {
        IDXGIFactory4* factory = null;
        IDXGIFactoryMedia* media = null;
        IDXGIAdapter1* pinned = null;
        ID3D11DeviceContext* ctx = null;
        ID3D11DeviceContext3* ctx3 = null;
        ID3D11Texture2D* buffer = null;
        ID3D11RenderTargetView* rtvY = null, rtvUv = null;
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        try
        {
            HRESULT hr = CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory);
            if (hr.FAILED || factory == null) return Hr("CreateDXGIFactory2", hr);
            hr = factory->QueryInterface(__uuidof<IDXGIFactoryMedia>(), (void**)&media);
            if (hr.FAILED) return Hr("QI IDXGIFactoryMedia", hr);
            if (packedLuid != 0)
            {
                LUID luid = default;
                luid.LowPart = unchecked((uint)packedLuid);
                luid.HighPart = (int)(packedLuid >> 32);
                if (factory->EnumAdapterByLuid(luid, __uuidof<IDXGIAdapter1>(), (void**)&pinned).FAILED) pinned = null;
            }
            // Explicit adapter REQUIRES D3D_DRIVER_TYPE_UNKNOWN (HARDWARE + an adapter is E_INVALIDARG).
            uint flags = (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
            ID3D11Device* d3d = null;
            hr = D3D11CreateDevice((IDXGIAdapter*)pinned,
                pinned != null ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                HMODULE.NULL, flags, null, 0, D3D11SdkVersion, &d3d, null, &ctx);
            if (hr.FAILED || d3d == null) return Hr("D3D11CreateDevice(video)", hr);
            _d3d = d3d;
            hr = ctx->QueryInterface(__uuidof<ID3D11DeviceContext3>(), (void**)&ctx3);
            if (hr.FAILED) return Hr("QI ID3D11DeviceContext3", hr);

            // The adapter's identity and its teardown event (sleep / resume ends hardware protection; the owner then builds anew).
            if (d3d->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgiDevice).SUCCEEDED && dxgiDevice->GetAdapter(&adapter).SUCCEEDED)
            {
                DXGI_ADAPTER_DESC d = default;
                if (adapter->GetDesc(&d).SUCCEEDED) AdapterKey = ProtectedKeeperPolicy.AdapterKey(d.VendorId, d.DeviceId);
                IDXGIAdapter3* a3 = null;
                if (adapter->QueryInterface(__uuidof<IDXGIAdapter3>(), (void**)&a3).SUCCEEDED) _adapter3 = a3;
            }

            HANDLE handle;
            hr = DCompositionCreateSurfaceHandle(COMPOSITIONOBJECT_ALL_ACCESS, null, &handle);
            if (hr.FAILED) return Hr("DCompositionCreateSurfaceHandle", hr);
            _handle = handle;

            DXGI_SWAP_CHAIN_DESC1 sd = default;
            sd.Width = Side;
            sd.Height = Side;
            sd.Format = DXGI_FORMAT.DXGI_FORMAT_NV12;
            sd.SampleDesc.Count = 1;
            sd.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
            sd.BufferCount = 2;
            sd.Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH;
            sd.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            sd.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;
            sd.Flags = (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_YUV_VIDEO | (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_HW_PROTECTED;
            IDXGISwapChain1* chain = null;
            hr = media->CreateSwapChainForCompositionSurfaceHandle((IUnknown*)d3d, handle, &sd, null, &chain);
            if (hr.FAILED || chain == null) return Hr("CreateSwapChainForCompositionSurfaceHandle(NV12, HW_PROTECTED)", hr);
            _chain = chain;

            // The NV12 back buffer's two planes as render targets (R8 = luma, R8G8 = chroma).
            hr = chain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&buffer);
            if (hr.FAILED) return Hr("GetBuffer", hr);
            D3D11_RENDER_TARGET_VIEW_DESC rd = default;
            rd.ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D;
            rd.Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM;
            hr = d3d->CreateRenderTargetView((ID3D11Resource*)buffer, &rd, &rtvY);
            if (hr.FAILED) return Hr("CreateRenderTargetView(Y)", hr);
            rd.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM;
            hr = d3d->CreateRenderTargetView((ID3D11Resource*)buffer, &rd, &rtvUv);
            if (hr.FAILED) return Hr("CreateRenderTargetView(UV)", hr);

            // Two presents of video black, each cleared under hardware protection (hwprot.cpp's Draw). The keeper holds static after.
            float* y = stackalloc float[4] { 16f / 255f, 0f, 0f, 1f };
            float* uv = stackalloc float[4] { 128f / 255f, 128f / 255f, 0f, 1f };
            for (int i = 0; i < 2; i++)
            {
                ctx3->SetHardwareProtectionState(BOOL.TRUE);
                ctx->ClearRenderTargetView(rtvY, y);
                ctx->ClearRenderTargetView(rtvUv, uv);
                ctx3->SetHardwareProtectionState(BOOL.FALSE);
                hr = chain->Present(0, 0);
                if (hr.FAILED) return Hr("Present #" + (i + 1), hr);
            }
            ctx->ClearState();
            ctx->Flush();

            if (_adapter3 != null)
            {
                var evt = new AutoResetEvent(false);
                uint cookie;
                if (_adapter3->RegisterHardwareContentProtectionTeardownStatusEvent((HANDLE)evt.SafeWaitHandle.DangerousGetHandle(), &cookie).SUCCEEDED)
                {
                    _teardownEvent = evt;
                    _teardownCookie = cookie;
                    _teardownRegistered = true;
                    _teardownWait = ThreadPool.RegisterWaitForSingleObject(evt, static (s, _) => ((ProtectedCompositionKeeper)s!).OnTeardown(),
                        this, Timeout.Infinite, executeOnlyOnce: true);
                }
                else evt.Dispose();
            }
            return "";
        }
        finally
        {
            if (rtvUv != null) rtvUv->Release();
            if (rtvY != null) rtvY->Release();
            if (buffer != null) buffer->Release();
            if (ctx3 != null) ctx3->Release();
            if (ctx != null) ctx->Release();
            if (adapter != null) adapter->Release();
            if (dxgiDevice != null) dxgiDevice->Release();
            if (pinned != null) pinned->Release();
            if (media != null) media->Release();
            if (factory != null) factory->Release();
        }
    }

    private static string Hr(string what, HRESULT hr) => what + " failed: 0x" + ((uint)(int)hr).ToString("X8");

    // Pool thread (the teardown wait): flag it and wake the main window's loop, whose next turn builds a new keeper.
    private void OnTeardown()
    {
        Volatile.Write(ref _tornDown, 1);
        try { ProtectedKeeperPolicy.Wake?.Invoke(); } catch { }
    }

    /// <summary>Release everything (the presenter has released its wrap of <see cref="SurfaceHandle"/> first). Idempotent.</summary>
    public void Dispose()
    {
        if (_teardownWait != null) { _teardownWait.Unregister(null); _teardownWait = null; }
        if (_teardownRegistered && _adapter3 != null) { _adapter3->UnregisterHardwareContentProtectionTeardownStatus(_teardownCookie); _teardownRegistered = false; }
        _teardownEvent?.Dispose();
        _teardownEvent = null;
        if (_adapter3 != null) { _adapter3->Release(); _adapter3 = null; }
        if (_chain != null) { _chain->Release(); _chain = null; }
        if (_d3d != null) { _d3d->Release(); _d3d = null; }
        if (_handle != HANDLE.NULL) { CloseHandle(_handle); _handle = HANDLE.NULL; }
    }
}
