using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Pal.Headless;

public sealed class HeadlessPlatformApp : IPlatformApp
{
    private readonly List<HeadlessPopupWindow> _popupWindows = new();

    public IPlatformWindow CreateWindow(in WindowDesc desc) => new HeadlessWindow(desc);
    public IClipboard Clipboard { get; } = new HeadlessClipboard();

    /// <summary>The synthetic monitor work area returned by <see cref="GetWorkArea"/> (physical virtual-screen px).
    /// Tests set this to simulate a taskbar-clipped / multi-monitor desktop; default = a 1920×1080 primary monitor.</summary>
    public RectF WorkArea { get; set; } = new(0f, 0f, 1920f, 1080f);

    /// <summary>Optional per-point resolver for multi-monitor tests (wins over <see cref="WorkArea"/> when set) —
    /// e.g. return a different work-area rect for points on a synthetic secondary monitor.</summary>
    public Func<Point2, RectF>? WorkAreaResolver { get; set; }

    public RectF GetWorkArea(Point2 screenPointPx) => WorkAreaResolver?.Invoke(screenPointPx) ?? WorkArea;

    /// <summary>Every popup window created this run (never removed — disposal is observable via
    /// <see cref="HeadlessPopupWindow.Disposed"/>), for placement/lifecycle assertions.</summary>
    public IReadOnlyList<HeadlessPopupWindow> PopupWindows => _popupWindows;

    public IPlatformPopupWindow? CreatePopupWindow(in PopupWindowDesc desc)
    {
        var w = new HeadlessPopupWindow(desc);
        _popupWindows.Add(w);
        return w;
    }

    /// <summary>URIs passed to <see cref="OpenUri"/>, in call order — golden checks assert HyperlinkButton's WinUI
    /// Click→launch sequence (HyperLinkButton_Partial.cpp:149-177) without touching the OS.</summary>
    public List<string> OpenedUris { get; } = new();

    /// <summary>Headless analogue of Launcher::TryInvokeLauncher: record, never launch.</summary>
    public void OpenUri(string uri) => OpenedUris.Add(uri);

    public void Dispose() { }
}

/// <summary>Synthetic window: the test harness pushes input via <see cref="QueueInput"/>; the host drains it.</summary>
public sealed class HeadlessWindow : IPlatformWindow
{
    private readonly Queue<InputEvent> _queue = new();

    public HeadlessWindow(in WindowDesc desc)
    {
        ClientSizePx = desc.SizePx;
        Scale = desc.Scale <= 0 ? 1f : desc.Scale;
        CustomFrame = desc.CustomFrame;
        Composited = desc.Composited;
    }

    /// <summary>The <see cref="WindowDesc.CustomFrame"/> opt-in, recorded for assertions (no real NC concept headless).</summary>
    public bool CustomFrame { get; }

    public NativeHandle Handle => new(0, NativeHandleKind.Headless);
    /// <summary>Settable (test seam): simulate a window resize / per-monitor DPI change (WM_DPICHANGED) mid-session —
    /// the host's EnsureSize watches BOTH px size and scale every frame, so the next RunFrame re-lays-out in the new
    /// DIP viewport (the multi-monitor DPI-hop regression).</summary>
    public Size2 ClientSizePx { get; set; }
    /// <inheritdoc cref="ClientSizePx"/>
    public float Scale { get; set; }
    public Action? PaintRequested { get; set; }   // unused headless (no modal resize loop)
    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.InModalLoop"/>
    public bool InModalLoop { get; set; }
    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.SizedInModalLoop"/>
    public bool SizedInModalLoop { get; set; }
    /// <inheritdoc cref="FluentGpu.Pal.IPlatformWindow.Composited"/>
    public bool Composited { get; set; }
    public CursorId LastCursor { get; private set; }
    public bool Shown { get; private set; }

    /// <summary>Settable synthetic screen position of client (0,0) — lets multi-monitor placement tests put the
    /// window anywhere on the virtual desktop. Default (0,0).</summary>
    public Point2 ClientOriginPx { get; set; }

    public void QueueInput(in InputEvent e) => _queue.Enqueue(e);

    public int PumpInto(InputEventRing ring)
    {
        int n = 0;
        while (_queue.Count > 0) { ring.Write(_queue.Dequeue()); n++; }
        return n;
    }

    public void WaitForWork(int timeoutMs) { }

    public void SetCursor(CursorId id) => LastCursor = id;
    public void SetTitle(StringId title) { }
    public void Show() => Shown = true;
    public IPlatformTextInput TextInput { get; } = new HeadlessTextInput();

    // ── custom-titlebar mirror: recorded call-lists + settable state (golden checks assert against these) ────────────

    /// <summary>Settable placement (test seam). <see cref="ToggleMaximize"/> flips it and emits
    /// <see cref="InputKind.WindowStateChanged"/> exactly like the Win32 backend's WM_SIZE transition.</summary>
    public WindowState State { get; set; } = WindowState.Normal;

    private bool _active = true;
    /// <summary>Settable activation (test seam): flipping emits WindowFocus/WindowBlur like Win32 WM_ACTIVATE.</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            QueueInput(new InputEvent(value ? InputKind.WindowFocus : InputKind.WindowBlur, default, 0, 0));
        }
    }

    public int MinimizeCount { get; private set; }
    public int ToggleMaximizeCount { get; private set; }
    public int SetFullscreenCount { get; private set; }
    public bool IsFullscreen { get; private set; }
    public int CloseCount { get; private set; }

    public void Minimize() { MinimizeCount++; State = WindowState.Minimized; }

    public void ToggleMaximize()
    {
        ToggleMaximizeCount++;
        State = State == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        QueueInput(new InputEvent(InputKind.WindowStateChanged, default, 0, 0));
    }

    public void SetFullscreen(bool fullscreen) { SetFullscreenCount++; IsFullscreen = fullscreen; }

    public void CloseWindow() => CloseCount++;

    /// <summary>The most recent region push (copied), for drag-band/island/button-rect assertions.</summary>
    public TitleBarRegion[] LastTitleBarRegions { get; private set; } = [];
    public void SetTitleBarRegions(ReadOnlySpan<TitleBarRegion> regions) => LastTitleBarRegions = regions.ToArray();

    public void Dispose() { }
}
