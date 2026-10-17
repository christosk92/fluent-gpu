using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Text.DirectWrite;

namespace FluentGpu.Hosting;

/// <summary>What <see cref="OffscreenSurface.TryCreate"/> builds: the surface size in DIP, its DPI scale, and the adapter.</summary>
public sealed record OffscreenSurfaceOptions
{
    /// <summary>The surface's size in DIP. The host lays out and renders at <c>SizeDip × Scale</c> px whatever the monitors
    /// allow (the <see cref="HarnessOptions.SurfaceSizeDip"/> contract, without a window on screen).</summary>
    public Size2 SizeDip { get; init; } = new(1180f, 760f);

    /// <summary>The DPI scale (px per DIP before app zoom); default 1.</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>False (the default): the WARP software adapter, so the same tree gives the same pixels on every machine; the
    /// surface is not created when WARP is unavailable. True: the default hardware adapter (faster; pixels may differ by GPU).</summary>
    public bool UseHardwareAdapter { get; init; }
}

/// <summary>
/// A public offscreen render target for an <see cref="AppHost"/>: frames render through the real D3D12 device into a surface
/// nobody sees, and <see cref="CaptureBgra"/> reads the pixels back. Nothing ever appears on screen, takes focus, or shows in
/// the taskbar or Alt+Tab, so it is safe to create while another copy of the app runs.
/// <para><b>How it is built.</b> The D3D12 device's present path is DXGI's (a latency-waitable flip swapchain, partial
/// presents, frame statistics, occlusion probes), so instead of a second texture-only present path inside the device the
/// surface presents into a hidden HWND: a <c>WS_POPUP</c> with <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c>, shown without
/// activation 30000 px off every monitor (DXGI stands down presents to an invisible or minimized window). It is created on the
/// calling thread and only this surface pumps its messages. Callers never touch Win32.</para>
/// <para><b>Headless semantics.</b> <see cref="Window"/> forwards everything to a <see cref="HeadlessWindow"/>
/// (<see cref="Input"/>): the size and scale it reports, the queued input, scroll and text input, and it reports a
/// <see cref="NativeHandleKind.Headless"/> handle that carries the HWND. An <see cref="AppHost"/> over it therefore runs
/// exactly as over a plain headless window (single-thread inline submit, the fixed frame clock, deterministic animation),
/// except that every <see cref="AppHost.RunFrame"/> really draws and presents. Pair it with a real
/// <see cref="DirectWriteFontSystem"/> (<see cref="CreateFontSystem"/>) for truthful text metrics.</para>
/// <para>Thread: create, run frames, capture and dispose on ONE thread (the host's UI thread). Order: dispose the
/// <see cref="AppHost"/> first (it disposes <see cref="Device"/>), then this surface (it destroys the window).</para>
/// <code>
/// var strings = new StringTable();
/// if (!OffscreenSurface.TryCreate(strings, new OffscreenSurfaceOptions { SizeDip = new(400, 860), Scale = 1.5f },
///         out var surface, out string? why)) { /* fall back to HeadlessGpuDevice; report why */ }
/// using var app = new HeadlessPlatformApp();
/// using var fonts = surface.CreateFontSystem();
/// var host = new AppHost(app, surface.Window, surface.Device, fonts, strings, root);
/// surface.Attach(host);
/// for (int i = 0; i &lt; 4; i++) host.RunFrame();
/// surface.WritePng("shot.png");
/// host.Dispose(); surface.Dispose();
/// </code>
/// </summary>
public sealed partial class OffscreenSurface : IDisposable
{
    private readonly D3D12Device _device;
    private readonly StringTable _strings;
    private nint _hwnd;
    private bool _disposed;

    private OffscreenSurface(nint hwnd, D3D12Device device, StringTable strings, OffscreenWindow window, bool warp)
    {
        _hwnd = hwnd;
        _device = device;
        _strings = strings;
        Window = window;
        IsWarp = warp;
    }

    /// <summary>The platform window to construct the <see cref="AppHost"/> with.</summary>
    public OffscreenWindow Window { get; }

    /// <summary>The headless input/size model behind <see cref="Window"/>: <see cref="HeadlessWindow.QueueInput"/>, the
    /// scroll producer, <see cref="HeadlessWindow.ClientSizePx"/> and <see cref="HeadlessWindow.Scale"/> (resize seams).</summary>
    public HeadlessWindow Input => Window.Inner;

    /// <summary>The D3D12 device to construct the <see cref="AppHost"/> with. The host owns it from then on and disposes it.</summary>
    public IGpuDevice Device => _device;

    /// <summary>True when the device runs on the WARP software adapter.</summary>
    public bool IsWarp { get; }

    /// <summary>The device's backend name, e.g. <c>"D3D12 (WARP)"</c>.</summary>
    public string BackendName => _device.BackendName;

    /// <summary>The surface size in physical px (<c>SizeDip × Scale</c>, rounded as the swapchain rounds).</summary>
    public Size2 SizePx => Input.ClientSizePx;

    /// <summary>Create a surface. False with <paramref name="reason"/> when it cannot be built here: no D3D12 device, WARP was
    /// asked for and is unavailable, or the hidden window could not be created. Nothing is left behind on failure.</summary>
    public static bool TryCreate(StringTable strings, OffscreenSurfaceOptions? options,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out OffscreenSurface? surface,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(strings);
        options ??= new OffscreenSurfaceOptions();
        surface = null;
        reason = null;
        float scale = options.Scale > 0f ? options.Scale : 1f;
        float wDip = options.SizeDip.Width, hDip = options.SizeDip.Height;
        if (!(wDip > 0f) || !(hDip > 0f)) { reason = "the surface size must be positive"; return false; }
        var sizePx = PxOf(wDip, hDip, scale);

        nint hwnd = 0;
        D3D12Device? device = null;
        try
        {
            // A visible, never-activated tool window far off every monitor: DXGI presents to it (not iconic, hidden or
            // cloaked), no one sees it, it has no taskbar button and is not in Alt+Tab. The swapchain's buffers take the
            // reported (pinned) size, not this window's, so its own size only has to be valid.
            hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "fluentgpu-offscreen", WS_POPUP,
                OffMonitor, OffMonitor, Math.Max(1, (int)sizePx.Width), Math.Max(1, (int)sizePx.Height), 0, 0, GetModuleHandleW(null), 0);
            if (hwnd == 0) { reason = $"CreateWindowEx failed (error {Marshal.GetLastPInvokeError()})"; return false; }
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);

            device = new D3D12Device(strings, composited: false) { ForceWarpAdapter = !options.UseHardwareAdapter };
            try { device.EnsureDeviceCreated(); }
            catch (InvalidOperationException e) { reason = "no D3D12 device: " + e.Message; return false; }
            bool warp = device.BackendName.EndsWith("(WARP)", StringComparison.Ordinal);
            if (!options.UseHardwareAdapter && !warp) { reason = "the WARP adapter is unavailable (got " + device.BackendName + ")"; return false; }
            // Offscreen frames are not paced to a display: present at interval 0 so RunFrame never waits for a vblank.
            device.Vsync = false;

            var inner = new HeadlessWindow(new WindowDesc("fluentgpu-offscreen", sizePx, scale));
            var window = new OffscreenWindow(hwnd, inner);
            window.Show();
            surface = new OffscreenSurface(hwnd, device, strings, window, warp);
            hwnd = 0; device = null;   // owned by the surface now
            return true;
        }
        finally
        {
            device?.Dispose();
            if (hwnd != 0) DestroyWindow(hwnd);
        }
    }

    /// <summary>As <see cref="TryCreate"/>, throwing <see cref="InvalidOperationException"/> with the reason on failure.</summary>
    public static OffscreenSurface Create(StringTable strings, OffscreenSurfaceOptions? options = null)
        => TryCreate(strings, options, out var s, out string? why) ? s : throw new InvalidOperationException("Offscreen surface unavailable: " + why);

    /// <summary>A DirectWrite font system over the surface's string table: real shaping, wrapping and trimming, the same
    /// engine the D3D12 glyph renderer draws with. Pass it to the <see cref="AppHost"/>; the caller disposes it after the host
    /// (the host does not own its font system).</summary>
    public DirectWriteFontSystem CreateFontSystem() => new(_strings);

    /// <summary>Bind the surface to the host built over it. Windowed popups (menus and flyouts in their own OS windows) need
    /// real popup HWNDs the headless platform app does not create, so they are turned off and every popup lays out in the
    /// window, as on a backend without windowed popups.</summary>
    public void Attach(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.PopupWindowsEnabled = false;
    }

    /// <summary>Resize the surface to <paramref name="sizeDip"/> (DIP) at <paramref name="scale"/> (null keeps the DPI scale).
    /// The next <see cref="AppHost.RunFrame"/> lays out and resizes the swapchain.</summary>
    public void Resize(Size2 sizeDip, float? scale = null)
    {
        ThrowIfDisposed();
        if (!(sizeDip.Width > 0f) || !(sizeDip.Height > 0f)) throw new ArgumentOutOfRangeException(nameof(sizeDip));
        float dpi = scale is > 0f ? scale.Value : Input.Scale / Math.Max(0.01f, Input.Zoom);
        Input.Scale = dpi;
        Input.ClientSizePx = PxOf(sizeDip.Width, sizeDip.Height, dpi);
    }

    /// <summary>The last presented frame as tightly packed, top-down, PREMULTIPLIED BGRA8 (stride = width × 4). Call between
    /// frames on the host's thread, after at least one <see cref="AppHost.RunFrame"/>; it waits for the GPU.</summary>
    public byte[] CaptureBgra(out int width, out int height)
    {
        ThrowIfDisposed();
        return _device.CaptureBgra(out width, out height);
    }

    /// <summary>Capture and write a PNG. <paramref name="backdrop"/> set: opaque RGB flattened over that colour (what the window
    /// shows over a solid ground, the review form). Null: RGBA with the translucency kept (un-premultiplied).</summary>
    public void WritePng(string path, ColorF? backdrop = null)
    {
        byte[] px = CaptureBgra(out int w, out int h);
        if (backdrop is { } ground) PngWriter.WriteBgra(path, px, w, h, ground);
        else PngWriter.WriteBgra(path, px, w, h, keepAlpha: true);
    }

    /// <summary>Destroys the hidden window, and the device when no host took it (a disposed device is not disposed twice).
    /// Dispose the <see cref="AppHost"/> first.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.Dispose();   // idempotent: a no-op once the host disposed it
        Window.DisposeCore();
        if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static Size2 PxOf(float wDip, float hDip, float scale)
        => new(MathF.Max(1f, MathF.Round(wDip * scale)), MathF.Max(1f, MathF.Round(hDip * scale)));

    // ── Win32 (private: callers never touch it) ─────────────────────────────────────────────────────────────────────

    private const int OffMonitor = -30000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint hinst, nint param);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);
}

/// <summary>
/// The <see cref="IPlatformWindow"/> of an <see cref="OffscreenSurface"/>: a <see cref="HeadlessWindow"/> (<see cref="Inner"/>,
/// which owns the size, scale, input queue and every test seam) whose handle carries the surface's hidden HWND, so the D3D12
/// device presents into it while the host keeps headless semantics. Each input pump also drains the hidden window's own
/// messages, so the OS never sees it as hung.
/// </summary>
public sealed partial class OffscreenWindow : IPlatformWindow
{
    private nint _hwnd;

    internal OffscreenWindow(nint hwnd, HeadlessWindow inner)
    {
        _hwnd = hwnd;
        Inner = inner;
    }

    /// <summary>The headless model every member forwards to.</summary>
    public HeadlessWindow Inner { get; }

    /// <summary>A <see cref="NativeHandleKind.Headless"/> handle whose value is the hidden HWND the swapchain presents into.</summary>
    public NativeHandle Handle => new(_hwnd, NativeHandleKind.Headless);

    public Size2 ClientSizePx => Inner.ClientSizePx;
    public float Scale => Inner.Scale;
    public float Zoom => Inner.Zoom;
    public void SetZoom(float zoom) => Inner.SetZoom(zoom);
    public Point2 ClientOriginPx => Inner.ClientOriginPx;
    public RectF OuterBoundsPx => Inner.OuterBoundsPx;

    public int PumpInto(InputEventRing ring)
    {
        PumpOwnMessages();
        return Inner.PumpInto(ring);
    }

    public int PumpScroll(in FrameClock clock, InputEventRing ring) => Inner.PumpScroll(in clock, ring);
    public void SetScrollInputSink(Action<FluentGpu.Scroll.Runtime.ScrollInputEvent>? sink) => Inner.SetScrollInputSink(sink);
    public bool ScrollProducerLive => Inner.ScrollProducerLive;
    public void WaitForWork(int timeoutMs) => PumpOwnMessages();
    public Action? PaintRequested { get => Inner.PaintRequested; set => Inner.PaintRequested = value; }
    public Action? ModalLoopTick { get => Inner.ModalLoopTick; set => Inner.ModalLoopTick = value; }
    public bool InModalLoop => Inner.InModalLoop;
    public bool Composited => false;
    public bool SizedInModalLoop => Inner.SizedInModalLoop;
    public void SetCursor(CursorId id) => Inner.SetCursor(id);
    public void SetTitle(StringId title) => Inner.SetTitle(title);
    public void Show() => Inner.Show();
    public void Hide() => Inner.Hide();
    public bool IsVisible => Inner.IsVisible;
    public bool IsCloaked => Inner.IsCloaked;
    public long OcclusionEpoch => Inner.OcclusionEpoch;
    public int CopyOccluderRectsPx(Span<RectF> into) => Inner.CopyOccluderRectsPx(into);
    public IPlatformTextInput TextInput => Inner.TextInput;
    public void SetTitleBarRegions(ReadOnlySpan<TitleBarRegion> regions) => Inner.SetTitleBarRegions(regions);
    public WindowState State => Inner.State;
    public bool IsActive => Inner.IsActive;
    public void Minimize() => Inner.Minimize();
    public void ToggleMaximize() => Inner.ToggleMaximize();
    public bool IsFullscreen => Inner.IsFullscreen;
    public void SetFullscreen(bool fullscreen) => Inner.SetFullscreen(fullscreen);
    public void CloseWindow() => Inner.CloseWindow();
    public Func<CloseReason, bool>? CloseRequested { get => Inner.CloseRequested; set => Inner.CloseRequested = value; }
    public bool IsClosed => Inner.IsClosed;
    public void SetTopmost(bool topmost) => Inner.SetTopmost(topmost);
    public void MoveToPx(Point2 outerOriginPx) => Inner.MoveToPx(outerOriginPx);

    /// <summary>The host disposes its window on teardown; the hidden HWND stays until <see cref="OffscreenSurface.Dispose"/>.</summary>
    public void Dispose() => Inner.Dispose();

    internal void DisposeCore() => _hwnd = 0;

    // Drain ONLY this window's messages (never another window's on a shared thread): a top-level window whose thread stops
    // reading its queue for seconds is replaced by a DWM "not responding" ghost.
    private void PumpOwnMessages()
    {
        if (_hwnd == 0) return;
        for (int i = 0; i < 64 && PeekMessageW(out Msg msg, _hwnd, 0, 0, PM_REMOVE) != 0; i++)
            DispatchMessageW(in msg);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PtX, PtY;
        public uint LPrivate;
    }

    private const uint PM_REMOVE = 0x0001;

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static partial int PeekMessageW(out Msg msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessageW(in Msg msg);
}
