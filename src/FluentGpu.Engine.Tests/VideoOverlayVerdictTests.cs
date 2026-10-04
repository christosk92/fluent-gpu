using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F249 / F087: the overlay-support verdict, the NV12 output choice, the promotion hysteresis and the way the render thread's
/// placement applier turns an occlusion verdict into a z-order change on the presenter. The recorder's verdict itself (what counts as
/// painted over the hole) is gated headlessly in the VerticalSlice (<c>gate.video.overlay-verdict</c>); here it arrives as the
/// <see cref="VideoPosedHole.Unoccluded"/> flag the composite publishes.
/// </summary>
public sealed class VideoOverlayVerdictTests
{
    // ── F249: the verdict and the NV12 output choice ──

    [Fact]
    public void TheVerdict_ReadsTheDxgiFlags_AndDropsTheBitsItDoesNotKnow()
    {
        var caps = VideoOverlayCaps.FromDxgiFlags(nv12: 3, yuy2: 0, bgra: 1 | 4);
        Assert.True(caps.Probed);
        Assert.Equal(OverlayPlaneSupport.Direct | OverlayPlaneSupport.Scaling, caps.Nv12);
        Assert.Equal(OverlayPlaneSupport.None, caps.Yuy2);
        Assert.Equal(OverlayPlaneSupport.Direct, caps.Bgra);
        Assert.Equal("nv12=direct+scaling yuy2=none bgra=direct", caps.Describe());
        Assert.True(caps.Nv12Promotable);
        Assert.True(caps.AnyPromotable);
    }

    [Fact]
    public void AnUnprobedOutput_ClaimsNothing()
    {
        VideoOverlayCaps none = default;
        Assert.False(none.Probed);
        Assert.False(none.Nv12Promotable);
        Assert.False(none.AnyPromotable);
        Assert.Equal("unprobed", none.Describe());
    }

    [Fact]
    public void AProbedOutputWithNoPlane_IsNotPromotable()
    {
        var caps = VideoOverlayCaps.FromDxgiFlags(0, 0, 0);
        Assert.True(caps.Probed);
        Assert.False(caps.AnyPromotable);
        Assert.Equal("nv12=none yuy2=none bgra=none", caps.Describe());
    }

    [Fact]
    public void Nv12IsChosen_OnlyWithTheSwitchAndAnNv12CapableOutput_ElseBgra()
    {
        var nv12Plane = VideoOverlayCaps.FromDxgiFlags(2, 0, 0);
        var bgraOnly = VideoOverlayCaps.FromDxgiFlags(0, 0, 1);
        Assert.Equal(VideoOutputFormat.Nv12, VideoOverlayCaps.ChooseOutputFormat(true, nv12Plane));
        Assert.Equal(VideoOutputFormat.Bgra, VideoOverlayCaps.ChooseOutputFormat(false, nv12Plane));   // switch off: always the old format
        Assert.Equal(VideoOutputFormat.Bgra, VideoOverlayCaps.ChooseOutputFormat(true, bgraOnly));     // the output cannot plane NV12
        Assert.Equal(VideoOutputFormat.Bgra, VideoOverlayCaps.ChooseOutputFormat(true, default));      // not probed yet
    }

    [Fact]
    public void BothNewSwitches_AreOffByDefault()
    {
        Assert.False(FluentGpu.Hosting.EngineSwitches.Nv12VideoOutput);
        Assert.False(FluentGpu.Hosting.EngineSwitches.VideoOverlay);
    }

    // ── F087: the promotion hysteresis ──

    private static long Hold => VideoOverlayGate.HoldQpc;

    [Fact]
    public void TheGate_PromotesAtTheFirstClearTurn_WhenEligible()
    {
        var g = default(VideoOverlayGate);
        Assert.False(g.Update(eligible: true, clear: false, nowQpc: 10));
        Assert.True(g.Update(eligible: true, clear: true, nowQpc: 20));
        Assert.True(g.Above);
    }

    [Fact]
    public void TheGate_NeverPromotes_WhenTheSwitchOrTheProbeSaysNo()
    {
        var g = default(VideoOverlayGate);
        Assert.False(g.Update(eligible: false, clear: true, nowQpc: 10));
        Assert.False(g.Above);
    }

    [Fact]
    public void TheGate_DemotesTheSameTurnSomethingCovers_AndHoldsTheUnderlayBeforeRepromoting()
    {
        var g = default(VideoOverlayGate);
        long t = 1_000;
        Assert.True(g.Update(true, true, t));
        Assert.False(g.Update(true, false, t + 1));          // chrome appears: back under the UI at once
        Assert.False(g.Update(true, true, t + 2));           // chrome gone a moment later: still held, the visual does not flap
        Assert.False(g.Update(true, true, t + 1 + Hold - 1));
        Assert.True(g.Update(true, true, t + 1 + Hold));     // held long enough: promoted again
    }

    [Fact]
    public void TheGate_IsIdempotentForOneTurnsInputs()
    {
        var g = default(VideoOverlayGate);
        Assert.True(g.Update(true, true, 100));
        Assert.True(g.Update(true, true, 100));
        Assert.False(g.Update(true, false, 200));
        Assert.False(g.Update(true, false, 200));            // the second read of the same covered turn does not extend the hold
        Assert.True(g.Update(true, true, 200 + Hold));
    }

    [Fact]
    public void AReset_ForgetsTheStateAndTheHold()
    {
        var g = default(VideoOverlayGate);
        g.Update(true, true, 1);
        g.Update(true, false, 2);
        g.Reset();
        Assert.False(g.Above);
        Assert.True(g.Update(true, true, 3));                // no hold survives a presenter swap
    }

    // ── F087: the applier turns the verdict into a z-order change on the presenter ──

    private sealed class OverlayPresenter : IVideoPresenter
    {
        public readonly List<string> Calls = new();
        public bool Supported = true;
        public bool? LastOverlay;
        public int Overlays;
        private uint _next = 1;

        public bool SupportsOverlay => Supported;
        public VideoSurfaceId CreateSurface() => new VideoSurfaceId(_next++);
        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle) => true;
        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z) => Calls.Add("Place");
        public void SetVisible(VideoSurfaceId id, bool visible) { }
        public void SetOverlay(VideoSurfaceId id, bool above) { LastOverlay = above; Overlays++; Calls.Add($"Overlay({above})"); }
        public void Destroy(VideoSurfaceId id) { }
        public void ApplyPending() { }
        public void Commit() { }
    }

    private static readonly RectF Rect = new(100f, 50f, 320f, 180f);

    private static VideoPosedHole Posed(bool unoccluded) => new() { Token = 1, Hole = Rect, EffClip = RectF.Infinite, Unoccluded = unoccluded };

    private static readonly VideoPresentIntent[] Frame =
    [
        new VideoPresentIntent
        {
            Token = 1, Gen = 1, HandleSeq = 1, DesiredHandle = 0x10, HasGeometry = true, Visible = true,
            RectDip = Rect, ViewportDip = Rect, HasHoleOrigin = true, HoleX = Rect.X, HoleY = Rect.Y,
        },
    ];

    // An applier + presenter with a surface already placed under its hole (the first turn creates, binds and places it; its verdict is "covered").
    private static (VideoPlacementApplier Applier, OverlayPresenter P) Placed(bool overlayOn, bool supported = true)
    {
        var applier = new VideoPlacementApplier(new VideoSurfaceRegistry()) { OverlayEnabled = overlayOn };
        var p = new OverlayPresenter { Supported = supported };
        applier.ApplyTurn(p, Frame, [Posed(unoccluded: false)], 1f, VideoApplyScope.Full, deferCommit: false);
        return (applier, p);
    }

    private static void Turn(VideoPlacementApplier applier, OverlayPresenter p, bool unoccluded)
        => applier.ApplyTurn(p, Frame, [Posed(unoccluded)], 1f, VideoApplyScope.Full, deferCommit: false);

    [Fact]
    public void WithTheSwitchOff_ACleanHole_NeverMakesAPresenterCall()
    {
        var (applier, p) = Placed(overlayOn: false);
        Turn(applier, p, unoccluded: true);
        Turn(applier, p, unoccluded: true);
        Assert.Equal(0, p.Overlays);
        Assert.Null(p.LastOverlay);
    }

    [Fact]
    public void AnOutputWithNoPlane_KeepsEverySurfaceAnUnderlay_EvenWithTheSwitchOn()
    {
        var (applier, p) = Placed(overlayOn: true, supported: false);
        Turn(applier, p, unoccluded: true);
        Assert.Equal(0, p.Overlays);
    }

    [Fact]
    public void ACleanHole_IsPromotedAboveTheUi_AndDemotedTheTurnSomethingCoversIt()
    {
        var (applier, p) = Placed(overlayOn: true);
        Assert.Equal(0, p.Overlays);                      // the placing turn's verdict was "covered"

        Turn(applier, p, unoccluded: true);    // chrome hidden: nothing paints over the video
        Assert.True(p.LastOverlay);
        Assert.Equal(1, p.Overlays);

        Turn(applier, p, unoccluded: true);    // steady: no repeat call
        Assert.Equal(1, p.Overlays);

        Turn(applier, p, unoccluded: false);   // chrome shown: back under the UI in the same turn
        Assert.False(p.LastOverlay);
        Assert.Equal(2, p.Overlays);
    }

    [Fact]
    public void ADemotedSurface_IsNotRepromotedWithinTheHold_SoTheVisualDoesNotFlap()
    {
        var (applier, p) = Placed(overlayOn: true);
        Turn(applier, p, unoccluded: true);
        Turn(applier, p, unoccluded: false);
        Assert.Equal(2, p.Overlays);
        Turn(applier, p, unoccluded: true);    // clear again within the hold (these turns are microseconds apart)
        Assert.Equal(2, p.Overlays);
        Assert.False(p.LastOverlay);
    }

    [Fact]
    public void ANewPresenter_StartsEverySurfaceBelowTheUi()
    {
        var (applier, p) = Placed(overlayOn: true);
        Turn(applier, p, unoccluded: true);
        Assert.True(p.LastOverlay);

        var fresh = new OverlayPresenter();               // device recovery: the old presenter's visuals are gone
        applier.ApplyTurn(fresh, Frame, [Posed(unoccluded: false)], 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(0, fresh.Overlays);                  // re-created below the UI; promotion waits for a clean verdict again
    }
}
