using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F070 (frame-sourced video placement): the render thread places video from the registry snapshot the PRESENTED frame carries,
/// moved by the hole's own posed travel, never from whatever the UI thread has written since. The applier is driven by hand against
/// a recording presenter, with a real <see cref="SceneFramePublisher"/> carrying the snapshots exactly as a hosted frame does.
/// </summary>
public sealed class VideoPlacementSameTurnTests
{
    private sealed class PlacementPresenter : IVideoPresenter
    {
        public readonly List<string> Calls = new();
        public RectF LastPlace, LastViewport;
        public bool LastVisible = true;
        public int Places, Destroys, Binds, Applies, Commits;
        private uint _next = 1;

        public VideoSurfaceId CreateSurface()
        {
            var id = new VideoSurfaceId(_next++);
            Calls.Add($"Create({id.Value})");
            return id;
        }

        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
        {
            Binds++;
            Calls.Add($"Bind({id.Value},0x{dcompSurfaceHandle:X})");
            return true;
        }

        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z)
        {
            Places++;
            LastPlace = deviceRect;
            Calls.Add($"Place({id.Value})");
        }

        public void SetViewport(VideoSurfaceId id, RectF deviceRect) => LastViewport = deviceRect;
        public void SetVisible(VideoSurfaceId id, bool visible) => LastVisible = visible;
        public void Destroy(VideoSurfaceId id) { Destroys++; Calls.Add($"Destroy({id.Value})"); }
        public void ApplyPending() => Applies++;
        public void Commit() => Commits++;
    }

    private static readonly byte[] One = [1];
    private static readonly RectF A = new(100f, 50f, 320f, 180f);
    private static readonly RectF B = new(160f, 90f, 320f, 180f);
    private static readonly RectF C = new(220f, 130f, 320f, 180f);

    private static void BindUi() => ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);

    // Publish the registry's current state, then claim the newest publication the way the render thread does.
    private static RenderFrame PublishAndAcquire(SceneFramePublisher seam, VideoSurfaceRegistry reg)
    {
        seam.Publish(One, default, default, video: reg);
        Assert.True(seam.TryAcquire(out RenderFrame rf));
        return rf;
    }

    private static VideoPosedHole Posed(RectF hole, RectF clip) => new() { Token = 1, Hole = hole, EffClip = clip };

    private static VideoPresentIntent HoleIntent(RectF rect) => new()
    {
        Token = 1, Gen = 1, HandleSeq = 1, DesiredHandle = 0x10, HasGeometry = true, Visible = true,
        RectDip = rect, ViewportDip = rect, HasHoleOrigin = true, HoleX = rect.X, HoleY = rect.Y,
    };

    [Fact]
    public void Placement_IsTheSnapshotOfThePresentedFrame_NotTheLiveRegistry()
    {
        BindUi();
        var seam = new SceneFramePublisher();
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new PlacementPresenter();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, A);
        RenderFrame frameN = PublishAndAcquire(seam, reg);

        // The UI keeps going: frame N+1's pump has already written its rect while the render turn for frame N is still in flight.
        reg.Place(token, B);

        applier.ApplyTurn(p, seam.VideoIntents(frameN), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(A, p.LastPlace);   // the video sits under the hole of the frame being presented, not under N+1's

        RenderFrame frameN1 = PublishAndAcquire(seam, reg);
        applier.ApplyTurn(p, seam.VideoIntents(frameN1), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(B, p.LastPlace);
    }

    [Fact]
    public void AnElidedTurn_AppliesContentButHoldsGeometry_UntilATurnThatPresentsTheMatchingHole()
    {
        BindUi();
        var seam = new SceneFramePublisher();
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new PlacementPresenter();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, A);
        applier.ApplyTurn(p, seam.VideoIntents(PublishAndAcquire(seam, reg)), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(1, p.Places);

        reg.Place(token, B);
        reg.Bind(token, 0x20);   // a new handle AND a new rect arrive together
        RenderFrame next = PublishAndAcquire(seam, reg);

        applier.ApplyTurn(p, seam.VideoIntents(next), default, 1f, VideoApplyScope.ContentOnly, deferCommit: false);
        Assert.Contains("Bind(1,0x20)", p.Calls);   // content applies on any turn...
        Assert.Equal(1, p.Places);                   // ...geometry waits for the turn that presents its hole
        Assert.Equal(A, p.LastPlace);

        applier.ApplyTurn(p, seam.VideoIntents(next), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(2, p.Places);
        Assert.Equal(B, p.LastPlace);
    }

    [Fact]
    public void ASkippedPublication_IsSupersededByTheNext_AndNeverDropsARelease()
    {
        BindUi();
        var seam = new SceneFramePublisher();
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new PlacementPresenter();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, A);
        applier.ApplyTurn(p, seam.VideoIntents(PublishAndAcquire(seam, reg)), default, 1f, VideoApplyScope.Full, deferCommit: false);

        reg.Place(token, B);
        seam.Publish(One, default, default, video: reg);   // a publication the render thread never adopts
        reg.Place(token, C);
        reg.Release(token);
        RenderFrame latest = PublishAndAcquire(seam, reg);

        Assert.Equal(3UL, latest.PublishSeq);   // the middle one was skipped
        applier.ApplyTurn(p, seam.VideoIntents(latest), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(1, p.Destroys);             // the release rode the surviving publication: a skip loses nothing
        reg.PumpPending(1f);
        Assert.Equal(token, reg.Acquire());      // and the slot came back to the UI thread
    }

    [Fact]
    public void ACompositeOnlyTurn_MovesTheVideoByTheHolesPosedTravel_AndReClipsItsViewport()
    {
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new PlacementPresenter();
        var rect = new RectF(100f, 200f, 320f, 180f);
        VideoPresentIntent[] frame = [HoleIntent(rect)];

        // Turn 1: the frame presents at the pose it was published at.
        applier.ApplyTurn(p, frame, [Posed(rect, RectF.Infinite)], 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(rect, p.LastPlace);
        Assert.False(applier.LastTurnMovedGeometry);   // the first placement is not motion

        // Turn 2: the SAME publication, but the render thread's scroll poser moved the hole up 60 DIP - no UI pump, no new intent.
        var moved = new RectF(100f, 140f, 320f, 180f);
        applier.ApplyTurn(p, frame, [Posed(moved, RectF.Infinite)], 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(moved, p.LastPlace);
        Assert.True(applier.LastTurnMovedGeometry);

        // Turn 3: a pinned header now cuts the composite at y = 160: the video's clip follows the hole's.
        applier.ApplyTurn(p, frame, [Posed(moved, new RectF(0f, 0f, 1000f, 160f))], 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(new RectF(100f, 140f, 320f, 20f), p.LastViewport);
        Assert.True(p.LastVisible);

        // Turn 4: the hole is scrolled wholly under the header: nothing of the video may show there.
        applier.ApplyTurn(p, frame, [Posed(moved, new RectF(0f, 0f, 1000f, 100f))], 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.False(p.LastVisible);
    }

    [Fact]
    public void AnUnchangedPose_IsNoMotion_AndCostsNoPresenterCall()
    {
        var applier = new VideoPlacementApplier(new VideoSurfaceRegistry());
        var p = new PlacementPresenter();
        VideoPresentIntent[] frame = [HoleIntent(A)];
        applier.ApplyTurn(p, frame, [Posed(A, RectF.Infinite)], 1f, VideoApplyScope.Full, deferCommit: false);
        int places = p.Places;

        applier.ApplyTurn(p, frame, [Posed(A, RectF.Infinite)], 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(places, p.Places);
        Assert.False(applier.LastTurnMovedGeometry);
    }

    [Fact]
    public void PrepareGeometry_ReportsMotion_OnlyForAnAlreadyPlacedSurface()
    {
        var applier = new VideoPlacementApplier(new VideoSurfaceRegistry());
        var p = new PlacementPresenter();
        VideoPresentIntent[] frame = [HoleIntent(A)];
        Assert.False(applier.PrepareGeometry(frame, [Posed(A, RectF.Infinite)], 1f));   // nothing on screen yet: no motion to synchronise

        applier.ApplyTurn(p, frame, [Posed(A, RectF.Infinite)], 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.False(applier.PrepareGeometry(frame, [Posed(A, RectF.Infinite)], 1f));   // steady playback never arms the motion present

        var moved = new RectF(A.X, A.Y - 30f, A.W, A.H);
        Assert.True(applier.PrepareGeometry(frame, [Posed(moved, RectF.Infinite)], 1f));
        applier.ApplyTurn(p, frame, [Posed(moved, RectF.Infinite)], 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.True(applier.LastTurnMovedGeometry);
    }

    [Fact]
    public void TheEarlyDrain_CreatesAndBindsFromTheMailbox_ButKeepsTheSurfaceHiddenUntilAPublicationPlacesIt()
    {
        BindUi();
        var seam = new SceneFramePublisher();
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new PlacementPresenter();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, A);
        Assert.True(applier.HasStructuralWork);

        applier.ApplyStructural(p, 1f, deferCommit: true);   // a handle with no publication behind it
        Assert.Equal(1, p.Binds);
        Assert.Equal(0, p.Places);                            // the registry's live rect is NOT the early drain's to read
        Assert.False(p.LastVisible);                          // ...and the committed surface must not sit unplaced and visible at the window origin
        Assert.False(applier.HasStructuralWork);

        applier.ApplyTurn(p, seam.VideoIntents(PublishAndAcquire(seam, reg)), default, 1f, VideoApplyScope.Full, deferCommit: true);
        Assert.Equal(1, p.Places);
        Assert.Equal(A, p.LastPlace);
        Assert.True(p.LastVisible);                           // the publication's placement shows it
        Assert.Equal(1, p.Binds);                             // the publication carries the same handle: nothing is bound twice
    }

    [Fact]
    public void AMailboxApplyOfANewSlot_ThatNoPublicationDescribed_LeavesItHiddenEvenOnAFullTurn()
    {
        BindUi();
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new PlacementPresenter();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);

        // The failed pop-out's drain (AppHost.DrainVideoAfterRenderFailure): Full scope over the content mailbox alone.
        applier.ApplyMailbox(p, 1f, VideoApplyScope.Full, deferCommit: false, default);
        Assert.Equal(1, p.Binds);
        Assert.Equal(0, p.Places);
        Assert.False(p.LastVisible);
    }

    [Fact]
    public void AnElidedTurn_ThatCreatesASurface_PlacesItAtThePublishedRect()
    {
        BindUi();
        var seam = new SceneFramePublisher();
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new PlacementPresenter();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, A);
        RenderFrame frame = PublishAndAcquire(seam, reg);

        applier.ApplyTurn(p, seam.VideoIntents(frame), default, 1f, VideoApplyScope.ContentOnly, deferCommit: false);
        Assert.Equal(1, p.Binds);
        Assert.Equal(1, p.Places);                            // a created surface is never left bound, committed and unplaced
        Assert.Equal(A, p.LastPlace);
        Assert.True(p.LastVisible);

        reg.Place(token, B);
        RenderFrame next = PublishAndAcquire(seam, reg);
        applier.ApplyTurn(p, seam.VideoIntents(next), default, 1f, VideoApplyScope.ContentOnly, deferCommit: false);
        Assert.Equal(1, p.Places);                            // an existing surface still holds its move for a presenting turn
        applier.ApplyTurn(p, seam.VideoIntents(next), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(B, p.LastPlace);
    }

    [Fact]
    public void TwoPosedHolesSharingAToken_MoveTheVideoByTheOwnersHoleOnly()
    {
        // The fullscreen hand-off shares one binding between the inline element and the overlay: both holes are live and carry the same
        // token. The surface follows the hole whose size the UI published, wherever it is listed.
        var inline = new RectF(40f, 300f, 320f, 180f);
        var overlay = new RectF(0f, 0f, 1280f, 720f);
        var intent = new VideoPresentIntent
        {
            Token = 1, Gen = 1, HandleSeq = 1, DesiredHandle = 0x10, HasGeometry = true, Visible = true,
            RectDip = overlay, ViewportDip = overlay, HasHoleOrigin = true,
            HoleX = overlay.X, HoleY = overlay.Y, HoleW = overlay.W, HoleH = overlay.H,
        };
        var applier = new VideoPlacementApplier(new VideoSurfaceRegistry());
        var p = new PlacementPresenter();
        var scrolledInline = new RectF(inline.X, inline.Y - 120f, inline.W, inline.H);
        // The inline page hole is placed before the overlay, and a page scroller clips it (the overlay is not scrolled).
        VideoPosedHole[] posed =
        [
            Posed(scrolledInline, new RectF(0f, 0f, 1280f, 100f)),
            Posed(overlay, RectF.Infinite),
        ];

        applier.ApplyTurn(p, [intent], posed, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(overlay, p.LastPlace);                   // not dragged by the inline hole's offset
        Assert.Equal(overlay, p.LastViewport);                // not cut by the inline scroller's clip
        Assert.True(p.LastVisible);

        // Two same-size holes cannot be told apart: no pose and no clip rather than a guess.
        var applier2 = new VideoPlacementApplier(new VideoSurfaceRegistry());
        var p2 = new PlacementPresenter();
        VideoPosedHole[] ambiguous = [Posed(new RectF(0f, -50f, overlay.W, overlay.H), new RectF(0f, 0f, 10f, 10f)), Posed(overlay, RectF.Infinite)];
        applier2.ApplyTurn(p2, [intent], ambiguous, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(overlay, p2.LastPlace);
        Assert.Equal(overlay, p2.LastViewport);
        Assert.True(p2.LastVisible);
    }
}
