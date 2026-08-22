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
        var session = new MfMediaSession(engine, new MediaOpenOptions { StartPaused = true });
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
        // A 16:9 stream in a SQUARE card — the Details pinned hero's shape, and the case every mode used to render
        // identically.
        var area = new RectF(0, 0, 326, 326);
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
        // 326×160 DIP at 1.5 ⇒ 489×240 device px; the cap follows the MOST magnified axis (240/1080), so the frame is
        // rendered at 489×275 — still exactly 16:9.
        Assert.Equal(new SizeI(489, 275), geo.Content);
        Assert.Equal(1920.0 / 1080.0, (double)geo.Content.Width / geo.Content.Height, 2);
    }

    /// <summary>THE ART-TILE FIX, as geometry. When the player element owns the WHOLE square tile, the aspect mode is
    /// the only thing that decides whether there are bars — which is the behaviour the Aspect-ratio menu promises.
    /// When a wrapper centres a hard-coded 16:9 child inside that square instead, the element's area IS 16:9 and every
    /// mode produces the identical rect: the menu is inert and the bars are the wrapper's, permanently.</summary>
    [Fact]
    public void SquareTile_TheElementMustOwnTheWholeSquareOrTheAspectMenuIsInert()
    {
        var natural = new SizeI(1920, 1080);
        var square = new RectF(0, 0, 326, 326);

        // (a) The element owns the square — the fix.
        RectF fit = MediaPlayerElement.FitVideoRect(square, natural, VideoAspectMode.Uniform, 16.0 / 9.0);
        RectF fill = MediaPlayerElement.FitVideoRect(square, natural, VideoAspectMode.Fill, 16.0 / 9.0);
        RectF crop = MediaPlayerElement.FitVideoRect(square, natural, VideoAspectMode.UniformToFill, 16.0 / 9.0);

        // Fit: a 16:9 band centred in the square, with EQUAL bars top and bottom.
        Assert.Equal(326f, fit.W, P);
        Assert.Equal(326f * 9f / 16f, fit.H, P);
        Edges4 bars = MediaPlayerElement.LetterboxInsets(square, fit);
        Assert.Equal(bars.Top, bars.Bottom, P);
        Assert.Equal((326f - 326f * 9f / 16f) * 0.5f, bars.Top, P);
        Assert.Equal(0f, bars.Left, P);

        // Stretch and Crop both cover the square edge to edge — NO bars. This is what "selecting Stretch changes
        // nothing" was really reporting: with the wrapper in place these two rects could never be reached.
        Assert.Equal(square, fill);
        Assert.Equal(default, MediaPlayerElement.LetterboxInsets(square, fill));
        Assert.Equal(326f, crop.H, P);
        Assert.True(crop.W >= 326f);
        Assert.Equal(0f, MediaPlayerElement.LetterboxInsets(square, crop).Top, P);

        // (b) The pre-fix wrapper: a 16:9 box centred inside the square. The element's area is already the frame's
        // shape, so all three modes collapse onto the same rect — the menu provably cannot move a pixel, and the bars
        // above/below live outside the element entirely.
        var inner = new RectF(0, (326f - 326f * 9f / 16f) * 0.5f, 326f, 326f * 9f / 16f);
        RectF innerFit = MediaPlayerElement.FitVideoRect(inner, natural, VideoAspectMode.Uniform, 16.0 / 9.0);
        RectF innerFill = MediaPlayerElement.FitVideoRect(inner, natural, VideoAspectMode.Fill, 16.0 / 9.0);
        RectF innerCrop = MediaPlayerElement.FitVideoRect(inner, natural, VideoAspectMode.UniformToFill, 16.0 / 9.0);
        Assert.Equal(innerFit.W, innerFill.W, P);
        Assert.Equal(innerFit.H, innerFill.H, P);
        Assert.Equal(innerFit.W, innerCrop.W, P);
        Assert.Equal(innerFit.H, innerCrop.H, P);
    }

    /// <summary>The other half of the same defect: a card that falls back to <see cref="MediaPlayerElement"/>'s own
    /// 160-DIP video-area floor instead of its declared aspect renders the frame into a 2.04 box — visibly the wrong
    /// shape under Stretch, and 83-DIP bars (not the designed ~71) under Fit. Handing the element the whole square is
    /// what removes the dependency on a declared child aspect surviving measure.</summary>
    [Fact]
    public void SquareTile_A160DipFloorDistortsTheFrame_WhereTheFullSquareDoesNot()
    {
        var natural = new SizeI(1920, 1080);
        var floored = new RectF(0, 83f, 326f, 160f);       // what the pre-fix tree actually laid out
        RectF stretched = MediaPlayerElement.FitVideoRect(floored, natural, VideoAspectMode.Fill, 16.0 / 9.0);
        Assert.Equal(326f / 160f, stretched.W / stretched.H, P);
        Assert.True(stretched.W / stretched.H > 2.0f);      // 2.04 — nothing like the frame's 1.78

        var square = new RectF(0, 0, 326f, 326f);
        RectF honest = MediaPlayerElement.FitVideoRect(square, natural, VideoAspectMode.Uniform, 16.0 / 9.0);
        Edges4 bars = MediaPlayerElement.LetterboxInsets(square, honest);
        Assert.InRange(bars.Top, 70f, 72f);                 // the designed ~71, not 83
    }
}
