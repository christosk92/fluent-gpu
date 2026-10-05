using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The protected output's OPM window (F264): the presenting window's handle travels registry → <see cref="VideoBinding"/> →
/// <see cref="ProtectedMediaSession.PumpVideo"/> → <see cref="IProtectedVideoPlayer.PlaceOutputProtectionWindow"/> →
/// <see cref="ProtectedVideoSession"/> → <c>FgPrSessionPlaceOpmWindow</c> with the video's rect in that window's device pixels.
/// Everything runs over the fakes (<see cref="FakeProtectedVideoPlayer"/>, <see cref="FakeSessionNative"/>): the DLL's own window
/// is covered by tests/FeedTests.cpp (OpmScreenRect and the window lifecycle), and what the engine does with it has no harness.
/// </summary>
public sealed class ProtectedOpmWindowTests : IAsyncDisposable
{
    private const string Kid = "0123456789abcdef0123456789abcdef";
    private static readonly RectF Rect = new(10, 20, 640, 360);

    private readonly List<IAsyncDisposable> _cleanup = new();

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable d in _cleanup) await d.DisposeAsync();
    }

    private static VideoBinding NewBinding(VideoSurfaceRegistry registry) => new(registry, registry.Acquire());

    private (ProtectedMediaSession Session, FakeProtectedVideoPlayer Player) NewSession()
    {
        var player = new FakeProtectedVideoPlayer();
        var request = new ProtectedVideoRequest { InitUrl = "https://cdn.test/a/init.mp4", StartPaused = true };
        var session = new ProtectedMediaSession(player, request, new MediaOpenOptions { StartPaused = true });
        _cleanup.Add(session);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        return (session, player);
    }

    private static void Present(FakeProtectedVideoPlayer player)
    {
        player.HasSurface = true;
        player.SurfaceHandle = 0xBEEF;
        player.SetNaturalSize(1920, 1080);
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Playing);
    }

    // ── the handle's route ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRegistrysWindowHandle_ReachesItsBindings_AndAnInertBindingHasNone()
    {
        var registry = new VideoSurfaceRegistry();
        Assert.Equal((nuint)0, registry.WindowHandle);
        Assert.Equal((nuint)0, NewBinding(registry).WindowHandle);

        registry.WindowHandle = 0x1234;

        Assert.Equal((nuint)0x1234, NewBinding(registry).WindowHandle);
        Assert.Equal((nuint)0, default(VideoBinding).WindowHandle);
    }

    [Fact]
    public void ThePlacedRect_IsInDevicePixels_EachEdgeRoundedAwayFromZeroIndependently()
    {
        // 10.4 / 20.5 / 110.7 / 70.7 DIP at 1.5x = 15.6 / 30.75 / 166.05 / 106.05 px.
        Assert.Equal((16, 31, 166, 106),
            ProtectedMediaSession.OutputProtectionRect(new RectF(10.4f, 20.5f, 100.3f, 50.2f), 1.5f));
        // Midpoints round away from zero (Math.Round's default would give 0 and 2).
        Assert.Equal((1, 0, 2, 1), ProtectedMediaSession.OutputProtectionRect(new RectF(0.5f, 0f, 1f, 1f), 1f));
        // A scale that is not positive means 1x, exactly as the surface geometry treats it.
        Assert.Equal((10, 20, 650, 380), ProtectedMediaSession.OutputProtectionRect(Rect, 0f));
    }

    // ── the media session ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PumpVideo_PlacesTheOpmWindowOverTheVideo_InThePresentingWindowsDevicePixels()
    {
        var (session, player) = NewSession();
        var registry = new VideoSurfaceRegistry { WindowHandle = 0xABC };
        Present(player);

        session.PumpVideo(NewBinding(registry), Rect, 2f);

        var placement = Assert.Single(player.OutputProtectionPlacements);
        Assert.Equal(((nuint)0xABC, 20, 40, 1300, 760), placement);   // (10 + 640) * 2 and (20 + 360) * 2
    }

    [Fact]
    public void PumpVideo_APopOutsOwnRegistry_CarriesThePopOutsWindow()
    {
        var (session, player) = NewSession();
        var main = new VideoSurfaceRegistry { WindowHandle = 0xABC };
        var popOut = new VideoSurfaceRegistry { WindowHandle = 0xDEF };
        Present(player);

        session.PumpVideo(NewBinding(main), Rect, 1f);
        session.PumpVideo(NewBinding(popOut), Rect, 1f);   // the video moved to the pop-out window

        Assert.Equal(new nuint[] { 0xABC, 0xDEF }, player.OutputProtectionPlacements.ConvertAll(p => p.Host).ToArray());
    }

    [Fact]
    public void PumpVideo_PlacesNothing_WithoutAWindowHandle_AValidBinding_OrASurface()
    {
        var (session, player) = NewSession();
        Present(player);

        session.PumpVideo(NewBinding(new VideoSurfaceRegistry()), Rect, 1f);                  // a registry never told its window (headless)
        session.PumpVideo(default, Rect, 1f);                                                  // an inert binding
        Assert.Empty(player.OutputProtectionPlacements);

        player.HasSurface = false;                                                             // detached: nothing is on screen to protect
        session.PumpVideo(NewBinding(new VideoSurfaceRegistry { WindowHandle = 0xABC }), Rect, 1f);
        Assert.Empty(player.OutputProtectionPlacements);
    }

    // ── the production player ───────────────────────────────────────────────────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public readonly FakeRuntimeNative Runtime = new();
        public readonly FakeSessionNative Sessions = new();
        public readonly string StorePath = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        public readonly ProtectedVideoRuntime Rt;

        public Rig() => Rt = new ProtectedVideoRuntime(Runtime, StorePath, idleMs: 60_000, sessionNative: Sessions);

        public ProtectedVideoSession CreateAttached()
        {
            var request = new ProtectedVideoRequest
            {
                InitUrl = "https://cdn.test/v/init.mp4",
                SegmentBaseUrl = "https://cdn.test/v/",
                SegmentPrefix = "seg_",
                SegmentSuffix = ".m4s",
                SegmentStride = 4,
                SegmentLengthMs = 4_000,
                DurationMs = 200_000,
                Pssh = new byte[] { 7, 7, 7, 7 },
                DefaultKid = Kid,
                StartPaused = true,
                LicenseRelay = _ => ValueTask.FromResult(new LicenseResponse(new byte[] { 1 })),
            };
            ProtectedVideoSession s = ProtectedVideoSession.Create(Rt, request);
            s.Start(s.Request);
            return s;
        }

        public void Dispose()
        {
            Rt.Dispose();
            try { Directory.Delete(StorePath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void TheSession_ForwardsAPlacementToNative_OncePerRealChange_AndAgainAfterAReattach()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.CreateAttached();

        s.PlaceOutputProtectionWindow(0x1234, 10, 20, 330, 200);
        s.PlaceOutputProtectionWindow(0x1234, 10, 20, 330, 200);
        Assert.Equal(1, rig.Sessions.CountOf("opm"));                                         // value-gated
        Assert.Equal((s.Handle, 0x1234ul, 10, 20, 330, 200), Assert.Single(rig.Sessions.OpmPlacements));

        s.PlaceOutputProtectionWindow(0x1234, 12, 20, 330, 200);                              // the video moved
        s.PlaceOutputProtectionWindow(0x9999, 12, 20, 330, 200);                              // ... to the pop-out window
        Assert.Equal(3, rig.Sessions.CountOf("opm"));

        s.Stop();
        s.Start(s.Request);                                                                    // another session may have moved the window
        s.PlaceOutputProtectionWindow(0x9999, 12, 20, 330, 200);
        Assert.Equal(4, rig.Sessions.CountOf("opm"));
    }

    [Fact]
    public void TheSession_NeverPlaces_WhileDetached_Disposed_OrWithoutAHostWindow()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.CreateAttached();

        s.PlaceOutputProtectionWindow(0, 10, 20, 330, 200);                                    // no host window
        Assert.Equal(0, rig.Sessions.CountOf("opm"));

        s.Stop();
        s.PlaceOutputProtectionWindow(0x1234, 10, 20, 330, 200);                               // detached
        Assert.Equal(0, rig.Sessions.CountOf("opm"));

        s.Start(s.Request);
        s.Dispose();
        s.PlaceOutputProtectionWindow(0x1234, 10, 20, 330, 200);                               // disposed
        Assert.Equal(0, rig.Sessions.CountOf("opm"));
    }

    [Fact]
    public void TheSession_AsksAgain_WhileNativeAnswersNotYet_ButNotAfterAFailureForTheSamePlacement()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.CreateAttached();

        rig.Sessions.PlaceOpmHr = 1;                                                           // S_FALSE: native is not attached to it yet
        s.PlaceOutputProtectionWindow(0x1234, 10, 20, 330, 200);
        s.PlaceOutputProtectionWindow(0x1234, 10, 20, 330, 200);
        Assert.Equal(2, rig.Sessions.CountOf("opm"));                                          // asked again at the next pump

        rig.Sessions.PlaceOpmHr = 0;
        s.PlaceOutputProtectionWindow(0x1234, 10, 20, 330, 200);                               // it landed
        s.PlaceOutputProtectionWindow(0x1234, 10, 20, 330, 200);
        Assert.Equal(3, rig.Sessions.CountOf("opm"));

        rig.Sessions.PlaceOpmHr = unchecked((int)0x80070578);                                  // ERROR_INVALID_WINDOW_HANDLE
        s.PlaceOutputProtectionWindow(0x7777, 1, 2, 3, 4);
        s.PlaceOutputProtectionWindow(0x7777, 1, 2, 3, 4);
        Assert.Equal(4, rig.Sessions.CountOf("opm"));                                          // a failure is not retried every pump
    }

    // ── the ABI lockstep ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADllWithoutThePlacementExport_IsAStaleBuild()
    {
        Assert.Contains("FgPrSessionPlaceOpmWindow", PrRuntimeNative.RequiredExports);
        Assert.Equal("FgPrSessionPlaceOpmWindow", PrRuntimeNative.FirstMissingExport(name => name != "FgPrSessionPlaceOpmWindow"));
    }
}
