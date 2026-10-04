using FluentGpu.Controls.Media;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The REALIZED placement geometry of a clear (non-DRM) video session, end to end over pure values: the element's fit
/// (<see cref="MediaPlayerElement.FitVideoRect"/>) → the session's swap-chain content size
/// (<see cref="MfMediaSession.ContentSizeFor"/>) → the <see cref="VideoSurfaceGeometry"/> the session publishes for the
/// host to read.
///
/// <para><b>Why this exists.</b> A letterbox has exactly two possible authors and they are indistinguishable from a
/// screenshot: (1) the BACKEND, when the destination it is handed is not the frame's shape — Media Foundation's
/// <c>UpdateVideoStream(NULL, &amp;dst, &amp;black)</c> does its own aspect-preserving fit with a black border inside
/// that rect, which no compositor scale can undo; or (2) the APP, when it wraps the player element in a box the
/// element cannot see, so the element's Aspect-ratio menu re-fits the frame inside a box that is already the frame's
/// shape and every mode looks identical. Case (1) is excluded structurally here: the content size ALWAYS carries the
/// frame's aspect, so the only thing that ever differs per aspect mode is the compositor's per-axis scale.</para>
/// </summary>
public sealed class VideoSurfaceGeometryTests
{
    private const int P = 3;

    private static (MfMediaSession session, MediaPlayerCore core, FakeVideoEngine engine) NewSession()
    {
        var core = new MediaPlayerCore();
        var engine = new FakeVideoEngine();
        var session = new MfMediaSession(engine, 0, new MediaOpenOptions { StartPaused = true });
        session.ConnectSignals(new MediaSignalSink(core));
        return (session, core, engine);
    }

    private static VideoBinding NewBinding(out VideoSurfaceRegistry registry)
    {
        registry = new VideoSurfaceRegistry();
        return new VideoBinding(registry, registry.Acquire());
    }

    /// <summary>The session publishes the three numbers a host needs to explain a bar, and the content it hands the
    /// backend keeps the frame's aspect for EVERY aspect mode — so the backend never letterboxes inside its own
    /// destination and the compositor owns the whole fit.</summary>
    [Theory]
    [InlineData(VideoAspectMode.Uniform)]
    [InlineData(VideoAspectMode.Fill)]
    [InlineData(VideoAspectMode.UniformToFill)]
    public void PublishedGeometry_AlwaysKeepsTheFramesAspectInTheContentSize(VideoAspectMode mode)
    {
        // A 16:9 stream in a card that is NOT the frame's shape — a splitter-grown rail cap — which is the case every
        // mode used to render identically back when a wrapper, not the element, owned the box.
        var area = new RectF(0, 0, 326, 300);
        var natural = new SizeI(1920, 1080);
        RectF fitted = MediaPlayerElement.FitVideoRect(area, natural, mode, 16.0 / 9.0);

        var (s, core, e) = NewSession();
        VideoBinding binding = NewBinding(out _);
        e.MetadataLoaded = true; e.NativeW = 1920; e.NativeH = 1080; e.Handle = 0xF00D;
        s.PumpVideo(binding, fitted, 1f);

        VideoSurfaceGeometry geo = core.SurfaceGeometry.Peek();
        Assert.True(geo.IsPlaced);
        Assert.Equal(natural, geo.Natural);
        Assert.Equal(fitted.W, geo.Place.W, P);
        Assert.Equal(fitted.H, geo.Place.H, P);
        // THE INVARIANT: the swap-chain content is the frame's shape, always. If this ever drifts, MF letterboxes
        // inside its own destination and no aspect mode can remove the bars.
        Assert.Equal(1920.0 / 1080.0, (double)geo.Content.Width / geo.Content.Height, 2);
    }

    /// <summary>Nothing has been placed before the first pump — a host must be able to tell "no geometry yet" from a
    /// degenerate one.</summary>
    [Fact]
    public void PublishedGeometry_IsEmptyUntilTheFirstPlacement()
    {
        var (_, core, _) = NewSession();
        Assert.False(core.SurfaceGeometry.Peek().IsPlaced);
        Assert.Equal(VideoSurfaceGeometry.Empty, core.SurfaceGeometry.Peek());
    }

    /// <summary>A DPI-scaled destination asks the backend for more pixels but keeps the ratio, and the published
    /// scale is what a host needs to convert the DIP place rect to what DirectComposition actually sees.</summary>
    [Fact]
    public void PublishedGeometry_CarriesTheDeviceScale_AndScalesTheContentWithIt()
    {
        var (s, core, e) = NewSession();
        VideoBinding binding = NewBinding(out _);
        e.MetadataLoaded = true; e.NativeW = 1920; e.NativeH = 1080; e.Handle = 0xF00D;

        s.PumpVideo(binding, new RectF(0, 0, 326, 160), 1.5f);

        VideoSurfaceGeometry geo = core.SurfaceGeometry.Peek();
        Assert.Equal(1.5f, geo.Scale, P);
        // 326×160 DIP at 1.5 ⇒ 489×240 device px; the most magnified axis needs 489/1920 = 0.255 of the frame, so the
        // stream takes the smallest bucket that covers it (1/3) — 640×360, still exactly 16:9.
        Assert.Equal(new SizeI(640, 360), geo.Content);
        Assert.Equal(1920.0 / 1080.0, (double)geo.Content.Width / geo.Content.Height, 2);
    }

    /// <summary>THE RULE, at the rail cap's envelope: the player element must own its WHOLE box, or the Aspect-ratio
    /// menu is inert. The docked card is full-bleed at the rail's width and its height is the content fit, which the
    /// user can then GROW with the rail splitter — and a grown card is exactly the case where the box is no longer the
    /// frame's shape, so it is where the three aspect modes have to differ. When a wrapper centres a hard-coded 16:9
    /// child inside that box instead, the element's area IS 16:9 and every mode produces the identical rect: the menu
    /// provably cannot move a pixel and the bars are the wrapper's, permanently.
    ///
    /// <para>This rule outlived the fixed SQUARE art tile it was first written against (the Details body's own
    /// letterboxing face, deleted — the video follows its own aspect at the rail's width in every body now). It is the
    /// wrapper that was ever the defect, not the shape of the box.</para></summary>
    [Fact]
    public void RailCap_TheElementMustOwnTheWholeCard_OrTheAspectMenuIsInert()
    {
        var natural = new SizeI(1920, 1080);
        const float RailW = 326f, GrownH = 300f;           // a splitter-grown cap: taller than the 183.4 content fit
        var card = new RectF(0, 0, RailW, GrownH);
        const float BandH = RailW * 9f / 16f;              // the 16:9 band the frame actually occupies at Fit

        // (a) The element owns the whole card — the shipping shape.
        RectF fit = MediaPlayerElement.FitVideoRect(card, natural, VideoAspectMode.Uniform, 16.0 / 9.0);
        RectF fill = MediaPlayerElement.FitVideoRect(card, natural, VideoAspectMode.Fill, 16.0 / 9.0);
        RectF crop = MediaPlayerElement.FitVideoRect(card, natural, VideoAspectMode.UniformToFill, 16.0 / 9.0);

        // Fit: a 16:9 band centred in the card, with EQUAL bars top and bottom.
        Assert.Equal(RailW, fit.W, P);
        Assert.Equal(BandH, fit.H, P);
        Edges4 bars = MediaPlayerElement.LetterboxInsets(card, fit);
        Assert.Equal(bars.Top, bars.Bottom, P);
        Assert.Equal((GrownH - BandH) * 0.5f, bars.Top, P);
        Assert.Equal(0f, bars.Left, P);

        // Stretch and Crop both cover the card edge to edge — NO bars. This is what "selecting Stretch changes
        // nothing" was really reporting: with a wrapper in place these two rects could never be reached.
        Assert.Equal(card, fill);
        Assert.Equal(default, MediaPlayerElement.LetterboxInsets(card, fill));
        Assert.Equal(GrownH, crop.H, P);
        Assert.True(crop.W >= RailW);
        Assert.Equal(0f, MediaPlayerElement.LetterboxInsets(card, crop).Top, P);

        // (b) The wrapper shape: a 16:9 box centred inside the card. The element's area is already the frame's shape,
        // so all three modes collapse onto the same rect — the menu cannot move a pixel, and the bars above/below live
        // outside the element entirely, where no aspect mode can reach them.
        var inner = new RectF(0, (GrownH - BandH) * 0.5f, RailW, BandH);
        RectF innerFit = MediaPlayerElement.FitVideoRect(inner, natural, VideoAspectMode.Uniform, 16.0 / 9.0);
        RectF innerFill = MediaPlayerElement.FitVideoRect(inner, natural, VideoAspectMode.Fill, 16.0 / 9.0);
        RectF innerCrop = MediaPlayerElement.FitVideoRect(inner, natural, VideoAspectMode.UniformToFill, 16.0 / 9.0);
        Assert.Equal(innerFit.W, innerFill.W, P);
        Assert.Equal(innerFit.H, innerFill.H, P);
        Assert.Equal(innerFit.W, innerCrop.W, P);
        Assert.Equal(innerFit.H, innerCrop.H, P);

        // (c) And at REST the card IS the content fit, so Fit/Stretch/Crop all cover it with no bars at all — which is
        // the whole reason the docked card follows the content's own aspect instead of a fixed envelope.
        var fitted = new RectF(0, 0, RailW, BandH);
        Assert.Equal(default, MediaPlayerElement.LetterboxInsets(
            fitted, MediaPlayerElement.FitVideoRect(fitted, natural, VideoAspectMode.Uniform, 16.0 / 9.0)));
    }

    /// <summary>The other half of the same defect: a card that falls back to <see cref="MediaPlayerElement"/>'s own
    /// 160-DIP video-area floor instead of the height it was handed renders the frame into a 2.04 box — visibly the
    /// wrong shape under Stretch, and bars nothing like the ones the real box would produce under Fit. Handing the
    /// element the whole card removes the dependency on a declared child size surviving measure.</summary>
    [Fact]
    public void RailCap_A160DipFloorDistortsTheFrame_WhereTheWholeCardDoesNot()
    {
        var natural = new SizeI(1920, 1080);
        const float RailW = 326f, GrownH = 300f;
        var floored = new RectF(0, 70f, RailW, 160f);      // the MinHeight=160 floor becoming the measured height
        RectF stretched = MediaPlayerElement.FitVideoRect(floored, natural, VideoAspectMode.Fill, 16.0 / 9.0);
        Assert.Equal(RailW / 160f, stretched.W / stretched.H, P);
        Assert.True(stretched.W / stretched.H > 2.0f);      // 2.04 — nothing like the frame's 1.78

        // The honest card: the element gets the height the rail declared, so Stretch fills THAT and Fit's bars are the
        // real remainder above and below the 16:9 band.
        var card = new RectF(0, 0, RailW, GrownH);
        RectF honestFill = MediaPlayerElement.FitVideoRect(card, natural, VideoAspectMode.Fill, 16.0 / 9.0);
        Assert.Equal(RailW / GrownH, honestFill.W / honestFill.H, P);
        RectF honest = MediaPlayerElement.FitVideoRect(card, natural, VideoAspectMode.Uniform, 16.0 / 9.0);
        Edges4 bars = MediaPlayerElement.LetterboxInsets(card, honest);
        Assert.Equal((GrownH - RailW * 9f / 16f) * 0.5f, bars.Top, P);
    }

    // ── whole-pixel placement (rule R: round X, Y, Right, Bottom independently, midpoint away from zero) ──────────────

    [Fact]
    public void SnapToDevicePixels_RoundsEveryEdgeIndependently()
    {
        // Right (110.7) and Bottom (70.8) round on their own: the width is 111 - 10 = 101, NOT round(100.3) = 100.
        RectF snapped = VideoSurfaceRegistry.SnapToDevicePixels(new RectF(10.4f, 20.6f, 100.3f, 50.2f));
        Assert.Equal(new RectF(10f, 21f, 101f, 50f), snapped);
    }

    [Fact]
    public void SnapToDevicePixels_MidpointRoundsAwayFromZero()
    {
        // Banker's rounding would give 0 / 2 / 2 / 2; away-from-zero gives 1 / 2 / 2 / 3.
        Assert.Equal(new RectF(1f, 2f, 1f, 1f),
            VideoSurfaceRegistry.SnapToDevicePixels(new RectF(0.5f, 1.5f, 1f, 1f)));
        // Negative edges (an oversized crop frame overflowing left/up) round away from zero too.
        Assert.Equal(new RectF(-1f, -2f, 2f, 1f),
            VideoSurfaceRegistry.SnapToDevicePixels(new RectF(-0.5f, -1.5f, 1f, 1f)));
    }

    [Fact]
    public void SnapToDevicePixels_RectsSharingAFractionalEdgeSnapToTheSameBoundary()
    {
        var left = new RectF(0f, 0f, 100.4f, 50f);
        var right = new RectF(100.4f, 0f, 99.6f, 50f);
        RectF a = VideoSurfaceRegistry.SnapToDevicePixels(left);
        RectF b = VideoSurfaceRegistry.SnapToDevicePixels(right);
        Assert.Equal(100f, a.Right);
        Assert.Equal(a.Right, b.X);   // no gap and no overlap between the two snapped rects
    }

    [Fact]
    public void SnapToDevicePixels_LeavesWholePixelRectsUntouched()
    {
        var r = new RectF(12f, 34f, 320f, 180f);
        Assert.Equal(r, VideoSurfaceRegistry.SnapToDevicePixels(r));
    }

    [Fact]
    public void FitVideoRectSnapped_IsBitIdenticalAtScaleOneWhenTheFitIsAlreadyWholePixels()
    {
        var area = new RectF(0, 0, 400, 180);
        var natural = new SizeI(1920, 1080);
        Assert.Equal(MediaPlayerElement.FitVideoRect(area, natural, VideoAspectMode.Uniform, 16.0 / 9.0),
            MediaPlayerElement.FitVideoRectSnapped(area, natural, VideoAspectMode.Uniform, 16.0 / 9.0, 1f));
    }

    [Theory]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    public void FitVideoRectSnapped_AndHoleInsets_LandOnTheRegistrysDeviceBoundary(float scale)
    {
        var area = new RectF(0, 0, 400, 300);
        var natural = new SizeI(1920, 1080);   // 400x225 centred: a fractional top/bottom edge at both scales
        RectF fit = MediaPlayerElement.FitVideoRect(area, natural, VideoAspectMode.Uniform, 16.0 / 9.0, scale);
        RectF registrySnap = VideoSurfaceRegistry.SnapToDevicePixels(MediaPlayerElement.ToDeviceRect(fit, scale));

        // The snapped fit's DEVICE edges are whole pixels and are exactly the boundary the registry snaps the raw fit to.
        RectF snappedDip = MediaPlayerElement.FitVideoRectSnapped(area, natural, VideoAspectMode.Uniform, 16.0 / 9.0, scale);
        RectF snappedDev = MediaPlayerElement.ToDeviceRect(snappedDip, scale);
        Assert.Equal(MathF.Round(snappedDev.X), snappedDev.X, 3);
        Assert.Equal(MathF.Round(snappedDev.Y), snappedDev.Y, 3);
        Assert.Equal(MathF.Round(snappedDev.Right), snappedDev.Right, 3);
        Assert.Equal(MathF.Round(snappedDev.Bottom), snappedDev.Bottom, 3);
        Assert.Equal(registrySnap.X, snappedDev.X, 3);
        Assert.Equal(registrySnap.Y, snappedDev.Y, 3);
        Assert.Equal(registrySnap.Right, snappedDev.Right, 3);
        Assert.Equal(registrySnap.Bottom, snappedDev.Bottom, 3);

        // The hole the element lays out (area minus the letterbox insets) snaps, in the registry's drain, to that same
        // boundary — so the UI hole's erase rect and the video visual share every edge.
        Edges4 insets = MediaPlayerElement.HoleInsets(area, natural, VideoAspectMode.Uniform, 16.0 / 9.0, scale);
        var hole = new RectF(area.X + insets.Left, area.Y + insets.Top,
            area.W - insets.Left - insets.Right, area.H - insets.Top - insets.Bottom);
        RectF holeSnap = VideoSurfaceRegistry.SnapToDevicePixels(MediaPlayerElement.ToDeviceRect(hole, scale));
        Assert.Equal(registrySnap, holeSnap);
    }

    [Fact]
    public void Drain_PlacesTheVideoAtWholeDevicePixels_AtAFractionalScale()
    {
        var (s, _, e) = NewSession();
        VideoBinding binding = NewBinding(out VideoSurfaceRegistry registry);
        e.MetadataLoaded = true; e.NativeW = 640; e.NativeH = 360; e.Handle = 0xF00D;
        var rectDip = new RectF(10.3f, 7.1f, 321f, 180.5f);
        binding.SetViewport(rectDip);
        s.PumpVideo(binding, rectDip, 1.5f);

        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1.5f);
        Assert.Equal(VideoSurfaceRegistry.SnapToDevicePixels(MediaPlayerElement.ToDeviceRect(rectDip, 1.5f)),
            presenter.LastPlaceRect);
        Assert.Equal(MathF.Round(presenter.LastPlaceRect.X), presenter.LastPlaceRect.X);
        Assert.Equal(MathF.Round(presenter.LastPlaceRect.Right), presenter.LastPlaceRect.Right);
        Assert.Equal(MathF.Round(presenter.LastViewport.Bottom), presenter.LastViewport.Bottom);
    }

    // F208 / F080 over a real session's pump: the surface its first bind produces is created, bound and placed by the EARLY
    // (structural) drain with no commit of its own, the host's one device commit follows, and the coupled drain that rides the
    // UI frame's present is still the only thing that applies a later placement or a destroy.
    [Fact]
    public void StructuralDrain_BindsASessionsFirstHandleEarly_WithoutCommitting_AndLeavesPlaceAndDestroyCoupled()
    {
        var (s, _, e) = NewSession();
        VideoBinding binding = NewBinding(out VideoSurfaceRegistry registry);
        e.MetadataLoaded = true; e.NativeW = 640; e.NativeH = 360; e.Handle = 0xF00D;
        var rectDip = new RectF(10f, 20f, 320f, 180f);
        s.PumpVideo(binding, rectDip, 1f);
        Assert.True(registry.HasStructuralWork);

        var presenter = new FakeVideoPresenter();
        registry.DrainStructural(presenter, 1f, deferCommit: true);
        Assert.Contains("Create(1)", presenter.Calls);
        Assert.NotEqual((nuint)0, presenter.LastBoundHandle);
        Assert.Equal(1, presenter.Applies);
        Assert.Equal(0, presenter.Commits);                  // the host commits once per turn, after every host applied
        Assert.False(registry.HasStructuralWork);

        // A move of the placed surface and the destroy of a released one wait for the coupled drain.
        int placesAfterCreate = presenter.Calls.FindAll(c => c.StartsWith("Place(", StringComparison.Ordinal)).Count;
        binding.SetViewport(new RectF(12f, 22f, 300f, 170f));
        registry.DrainStructural(presenter, 1f, deferCommit: true);
        Assert.Equal(placesAfterCreate, presenter.Calls.FindAll(c => c.StartsWith("Place(", StringComparison.Ordinal)).Count);
        binding.Release();
        registry.DrainStructural(presenter, 1f, deferCommit: true);
        Assert.DoesNotContain("Destroy(1)", presenter.Calls);

        registry.Drain(presenter, 1f, deferCommit: true);
        Assert.Contains("Destroy(1)", presenter.Calls);
        Assert.Equal(0, presenter.Commits);
    }
}
