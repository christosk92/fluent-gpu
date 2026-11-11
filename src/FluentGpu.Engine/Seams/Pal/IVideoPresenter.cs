using System;
using FluentGpu.Foundation;

namespace FluentGpu.Pal;

/// <summary>
/// A POD, opaque-across-the-seam handle for one composited video surface. <c>0</c> == none. The portable core and
/// app never see the backing composition visual or surface — only this id. (Windows maps <see cref="Value"/> → a
/// presenter slot; macOS would map it → an <c>AVPlayerLayer</c>.)
/// </summary>
public readonly record struct VideoSurfaceId(uint Value)
{
    public bool IsNone => Value == 0;
}

/// <summary>
/// The video-compositing PAL seam (<c>docs/plans/video-compositing-spine-design.md §4</c>,
/// <c>docs/plans/video-phase1-plan.md §4</c> — DRM-free spine). Composites externally-produced video the engine never paints
/// into, z-BELOW the UI swapchain, revealed through a premultiplied-0 hole-punch in the UI back buffer. The portable core
/// references only this interface (it stays TerraFX-free); every composition object lives behind the Windows leaf
/// (<c>FluentGpu.Windows/Pal/CompositionVideoPresenter.cs</c>: a Windows.UI.Composition sprite whose surface brush stretches the
/// swap chain's current buffer to the placed rect, on its own window target beneath the UI's), render-thread-confined like the
/// rest of the device.
/// </summary>
/// <remarks>
/// Threading: every method is executed on the RENDER/submit thread (phase 11 for <see cref="Place"/>/
/// <see cref="SetVisible"/>/<see cref="Commit"/>); in the single-thread Phase-1 build that thread is the UI thread
/// (quarantine 0). Steady-state <see cref="Place"/>/<see cref="Commit"/> are 0 managed alloc (POD args, a
/// preallocated id→visual table); the only allocations are cold ComPtr roots on first surface creation.
/// </remarks>
/// <summary>One frosted plate over a video surface (<see cref="IVideoPresenter.SetBackdrops"/>), in device px: the plate's rect and
/// corner radii (window space), the blur's standard deviation, the acrylic's tint and luminosity opacity (the presenter pulls the
/// blurred video's lightness to the tint's by that much, keeping its hue: the recipe's luminosity step) and the plate's opacity (an
/// entering or leaving plate fades its blur with its veil). POD, so a turn passes a span of them without allocating.</summary>
public readonly record struct VideoBackdropPx(RectF Rect, CornerRadius4 Radii, float Sigma, ColorF Tint, float Luminosity, float Opacity);

/// <summary>The axis a <see cref="VideoAlphaRamp"/> runs along: down the picture (its <c>From</c>/<c>To</c> are y), or across it (x).</summary>
public enum VideoRampAxis : byte { TopToBottom = 0, LeftToRight = 1 }

/// <summary>One stop of a <see cref="VideoAlphaRamp"/>: the picture's alpha <paramref name="Alpha"/> (0..1) at <paramref name="At"/> (0..1 of the
/// ramp's span, <c>From</c> to <c>To</c>). Between stops the alpha is linear; before the first and after the last it holds.</summary>
public readonly record struct VideoAlphaStop(float At, float Alpha);

/// <summary>The fixed inline stops of one <see cref="VideoAlphaRamp"/> (no allocation: a ramp is copied by value through the publication).</summary>
[System.Runtime.CompilerServices.InlineArray(VideoAlphaRamp.MaxStops)]
public struct VideoAlphaStops
{
    private VideoAlphaStop _e0;
}

/// <summary>
/// An ALPHA RAMP on a video picture: the picture fades along one <see cref="Axis"/> through up to <see cref="MaxStops"/> stops laid between
/// <see cref="From"/> and <see cref="To"/>, so where its alpha falls the window's own backdrop shows through (as it does wherever the UI is
/// translucent) instead of an opaque ground the UI must paint over the picture. <see cref="Count"/> 0 is no ramp: the picture is opaque.
/// The span's units depend on the hop: a <c>MediaPlayerElement</c> takes it in DIP from its own box's leading edge, the registry carries
/// it in window DIP, and <see cref="IVideoPresenter.SetAlphaRamp"/> receives it in device px from the placed rect's. POD and
/// allocation-free; equality compares the live stops only.
/// </summary>
public struct VideoAlphaRamp : IEquatable<VideoAlphaRamp>
{
    /// <summary>Stops one ramp holds (an eased fade sampled finely enough to read as its curve).</summary>
    public const int MaxStops = 16;

    public VideoRampAxis Axis;
    /// <summary>Where the span starts and ends along <see cref="Axis"/> (see the type for the units). <c>To</c> &lt; <c>From</c> is allowed.</summary>
    public float From, To;
    /// <summary>Live stops in <see cref="Stops"/> (0 = no ramp).</summary>
    public int Count;
    public VideoAlphaStops Stops;

    /// <summary>No ramp: the picture is drawn opaque.</summary>
    public static VideoAlphaRamp None => default;

    public readonly bool IsNone => Count == 0;

    /// <summary>A ramp over <paramref name="from"/>..<paramref name="to"/> through <paramref name="stops"/> (at most <see cref="MaxStops"/> are
    /// kept; alphas and positions are clamped to 0..1).</summary>
    public static VideoAlphaRamp Create(VideoRampAxis axis, float from, float to, ReadOnlySpan<VideoAlphaStop> stops)
    {
        var r = new VideoAlphaRamp { Axis = axis, From = from, To = to, Count = Math.Min(stops.Length, MaxStops) };
        for (int i = 0; i < r.Count; i++)
            r.Stops[i] = new VideoAlphaStop(Math.Clamp(stops[i].At, 0f, 1f), Math.Clamp(stops[i].Alpha, 0f, 1f));
        return r;
    }

    /// <summary>The same stops over a span moved by <paramref name="offset"/> and then scaled by <paramref name="scale"/> (a change of origin and
    /// units between hops). A no-ramp stays none.</summary>
    public readonly VideoAlphaRamp Mapped(float offset, float scale)
    {
        if (Count == 0) return default;
        VideoAlphaRamp r = this;
        r.From = (From + offset) * scale;
        r.To = (To + offset) * scale;
        return r;
    }

    public readonly bool Equals(VideoAlphaRamp o)
    {
        if (Count != o.Count) return false;
        if (Count == 0) return true;
        if (Axis != o.Axis || From != o.From || To != o.To) return false;
        for (int i = 0; i < Count; i++)
            if (Stops[i] != o.Stops[i]) return false;
        return true;
    }

    public override readonly bool Equals(object? obj) => obj is VideoAlphaRamp o && Equals(o);

    public override readonly int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Count);
        if (Count == 0) return h.ToHashCode();
        h.Add(Axis); h.Add(From); h.Add(To);
        for (int i = 0; i < Count; i++) h.Add(Stops[i]);
        return h.ToHashCode();
    }

    public static bool operator ==(VideoAlphaRamp a, VideoAlphaRamp b) => a.Equals(b);
    public static bool operator !=(VideoAlphaRamp a, VideoAlphaRamp b) => !a.Equals(b);
}

public interface IVideoPresenter
{
    /// <summary>Allocate a video surface (hidden until <see cref="BindSurfaceHandle"/> + <see cref="Place"/>).</summary>
    VideoSurfaceId CreateSurface();

    /// <summary>
    /// The surface-handoff seam. An external owner produces a DirectComposition surface HANDLE
    /// (<c>DCompositionCreateSurfaceHandle</c> on its side); the Windows impl wraps it via
    /// <c>ICompositorInterop::CreateCompositionSurfaceForHandle</c> and binds it as the surface's picture. Phase 1
    /// (DRM-free) passes an UNPROTECTED handle; the DRM phase passes a PROTECTED handle here — and NOTHING else in
    /// this seam or the renderer changes. This is the single DRM attach point.
    /// <para>Returns <see langword="true"/> only when the handle is now the surface's content. On failure it returns
    /// <see langword="false"/> after DROPPING the previous content (a stale frame of an earlier source must never stay on
    /// screen under a new one), and the caller keeps the handle unbound and retries on a later drain.</para>
    /// </summary>
    bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle);

    /// <summary>
    /// Position the surface's picture, stretched, to <paramref name="deviceRect"/> (device px) at draw order <paramref name="z"/>
    /// (higher Z composites above lower Z, always below the UI swapchain). Queued for the frame's <see cref="Commit"/>; the
    /// matching hole-punch in the UI back buffer is the source of truth for the visible rect. <paramref name="opacity"/> is retained metadata (the graded reveal is done UI-side).
    /// </summary>
    void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z);

    /// <summary>Clip the placed content to a device-space viewport. This is distinct from <see cref="Place"/> so
    /// UniformToFill can place an oversized, centered frame and crop it to the element without distortion.</summary>
    void SetViewport(VideoSurfaceId id, RectF deviceRect) { }

    /// <summary>Show/hide the surface (queued for the next <see cref="Commit"/>). Hiding removes its visual from the
    /// composition tree rather than clipping it to nothing; showing re-inserts it at its Z with its content still bound.</summary>
    void SetVisible(VideoSurfaceId id, bool visible);

    /// <summary>Round the corners of the composited surface's clip (the viewport within the placed rect) by <paramref name="radiiPx"/>
    /// (device px, per corner; all <c>0</c> = square, the default). The compositor clips the CONTENT, which a UI-side rounded parent
    /// cannot do — the video composites outside the UI back buffer, so it ignores the caller's ClipToBounds. The caller
    /// passes the element's own radius and, at a corner a rounded ancestor clip cuts, that clip's arc. Half the shorter side gives a
    /// circle. Default no-op so headless/test presenters need not implement it.</summary>
    void SetCornerRadii(VideoSurfaceId id, CornerRadius4 radiiPx) { }

    /// <summary>The frosted plates over this surface's hole (an acrylic surface composited after the hole in painter order): show a
    /// BLURRED copy of the video beneath each, clipped to the plate, so the swapchain's translucent veil over the hole reads as acrylic
    /// over the video instead of a flat fill (<see cref="FluentGpu.Rhi.AcrylicRecipe.OverVideo"/>). The UI back buffer cannot sample
    /// video pixels, so the blur is the compositor's. An empty span removes them all. Queued for the next <see cref="Commit"/>; a
    /// presenter compares with what it shows and does nothing when nothing changed. Default no-op: a headless / test presenter keeps
    /// the plain video (the veil then lies over the sharp frame).</summary>
    void SetBackdrops(VideoSurfaceId id, ReadOnlySpan<VideoBackdropPx> backdrops) { }

    /// <summary>Fade the surface's picture along <paramref name="rampPx"/> (device px from the placed rect's leading edge along the ramp's
    /// axis; <see cref="VideoAlphaRamp.IsNone"/> = opaque again). Queued for the next <see cref="Commit"/>; a presenter compares with what it
    /// shows. Default no-op: a headless / test presenter has no picture to fade.</summary>
    void SetAlphaRamp(VideoSurfaceId id, in VideoAlphaRamp rampPx) { }

    /// <summary>Tear down one surface (removes its visuals, releases its content). Cold path.</summary>
    void Destroy(VideoSurfaceId id);

    /// <summary>
    /// Apply every queued <see cref="Place"/>/<see cref="SetVisible"/>/<see cref="BindSurfaceHandle"/> mutation to the
    /// presenter's own visuals WITHOUT the device-level flush (F080): the host then makes ONE
    /// <see cref="FluentGpu.Rhi.IGpuDevice.CommitVideoComposition"/> per render turn after the parent and every detached child
    /// applied theirs, instead of one commit per window. Default <see cref="Commit"/>: a presenter with no shared device
    /// flush has nothing to defer.
    /// </summary>
    void ApplyPending() => Commit();

    /// <summary>True when this presenter can attach new surfaces to a live composition root now. A composited swapchain binds
    /// its DirectComposition graph on its first Present, so a surface created before that would sit unattached while the UI
    /// already punched its hole: the early (structural) drain waits for this. Default true.</summary>
    bool CanAttachSurfaces => true;

    /// <summary>
    /// <see cref="ApplyPending"/> plus the device flush: all queued mutations become one
    /// compositor commit — the per-frame commit at phase 11 (the "two-clock tear" lock: the hole
    /// rides the same frame-turn's <c>Present</c>). No-op when nothing is dirty.
    /// </summary>
    void Commit();
}
