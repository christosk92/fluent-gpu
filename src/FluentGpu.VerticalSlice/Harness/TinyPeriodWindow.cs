using FluentGpu.Foundation;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;

namespace FluentGpu.VerticalSlice.Harness;

/// <summary>A <see cref="HeadlessWindow"/> whose display reports a refresh period of <see cref="PeriodQpc"/> Stopwatch ticks
/// (default 4 — a panel no real display has) and forwards everything else. It is the deterministic stand-in for a UI
/// thread preempted mid-frame on a loaded box: any per-frame budget derived from the refresh period expires before the
/// frame's first unit of work. A gate that asserts what ONE frame achieves runs under it to prove the frame does not
/// depend on a wall-clock budget. Still a headless handle, so the host stays single-threaded and deterministic.</summary>
sealed class TinyPeriodWindow(HeadlessWindow inner, long periodQpc = 4) : IPlatformWindow
{
    readonly IPlatformWindow _w = inner;
    public long PeriodQpc { get; } = periodQpc;
    public HeadlessWindow Inner { get; } = inner;

    public long DisplayRefreshPeriodQpc => PeriodQpc;
    public IRenderDisplayClock? CreateRenderDisplayClock() => _w.CreateRenderDisplayClock();
    public NativeHandle Handle => _w.Handle;
    public Size2 ClientSizePx => _w.ClientSizePx;
    public float Scale => _w.Scale;
    public float Zoom => _w.Zoom;
    public void SetZoom(float zoom) => _w.SetZoom(zoom);
    public Point2 ClientOriginPx => _w.ClientOriginPx;
    public RectF OuterBoundsPx => _w.OuterBoundsPx;
    public int PumpInto(InputEventRing ring) => _w.PumpInto(ring);
    public int PumpScroll(in FrameClock clock, InputEventRing ring) => _w.PumpScroll(in clock, ring);
    public void SetScrollInputSink(Action<FluentGpu.Scroll.Runtime.ScrollInputEvent>? sink) => _w.SetScrollInputSink(sink);
    public bool ScrollProducerLive => _w.ScrollProducerLive;
    public void WaitForWork(int timeoutMs) => _w.WaitForWork(timeoutMs);
    public void WaitForWork(in PlatformWaitRequest request) => _w.WaitForWork(in request);
    public void Wake() => _w.Wake();
    public DisplayClockSample DisplayClock => _w.DisplayClock;
    public Action? PaintRequested { get => _w.PaintRequested; set => _w.PaintRequested = value; }
    public bool InModalLoop => _w.InModalLoop;
    public bool Composited => _w.Composited;
    public bool SizedInModalLoop => _w.SizedInModalLoop;
    public void SetCursor(CursorId id) => _w.SetCursor(id);
    public void SetTitle(StringId title) => _w.SetTitle(title);
    public void Show() => _w.Show();
    public void Hide() => _w.Hide();
    public bool IsVisible => _w.IsVisible;
    public IPlatformTextInput TextInput => _w.TextInput;
    public void SetTitleBarRegions(ReadOnlySpan<TitleBarRegion> regions) => _w.SetTitleBarRegions(regions);
    public WindowState State => _w.State;
    public bool IsActive => _w.IsActive;
    public void Minimize() => _w.Minimize();
    public void ToggleMaximize() => _w.ToggleMaximize();
    public bool IsFullscreen => _w.IsFullscreen;
    public void SetFullscreen(bool fullscreen) => _w.SetFullscreen(fullscreen);
    public void CloseWindow() => _w.CloseWindow();
    public Func<CloseReason, bool>? CloseRequested { get => _w.CloseRequested; set => _w.CloseRequested = value; }
    public bool IsClosed => _w.IsClosed;
    public void SetTopmost(bool topmost) => _w.SetTopmost(topmost);
    public void SetBoundsPx(RectF outerBoundsPx) => _w.SetBoundsPx(outerBoundsPx);
    public void MoveToPx(Point2 outerOriginPx) => _w.MoveToPx(outerOriginPx);
    public void SetMinClientSizePx(Size2 px) => _w.SetMinClientSizePx(px);
    public void SetHasLiveVideo(bool hasLiveVideo) => _w.SetHasLiveVideo(hasLiveVideo);
    public void Dispose() => _w.Dispose();
}
