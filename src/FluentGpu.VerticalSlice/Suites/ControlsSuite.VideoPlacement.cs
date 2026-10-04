using System;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Pal;
using static FluentGpu.VerticalSlice.Harness.Gate;

// ── gate.video.hole-placement-same-frame (F070 / F183: frame-sourced video placement) ─────────────────────────────────────
// The video's DirectComposition rect is applied from the registry SNAPSHOT the presented frame carries, moved by the hole's own
// posed travel - never from the live UI-owned registry. The headless seam has no DComp presenter, so the render thread's
// VideoPlacementApplier is driven by hand against a recording presenter, with a real SceneFramePublisher carrying the
// snapshots exactly as a hosted frame does. Each turn is a (publication, scope, posed holes) triple, the same inputs the host
// derives per render turn (AppHost.DrainVideoForPresentTurn).
static partial class ControlsSuite
{
    sealed class PlacementLogPresenter : IVideoPresenter
    {
        public RectF LastPlace;
        public int Places, Destroys, Binds;
        private uint _next = 1;

        public VideoSurfaceId CreateSurface() => new VideoSurfaceId(_next++);
        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle) { Binds++; return true; }
        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z) { Places++; LastPlace = deviceRect; }
        public void SetVisible(VideoSurfaceId id, bool visible) { }
        public void Destroy(VideoSurfaceId id) => Destroys++;
        public void Commit() { }
    }

    static void VideoPlacementChecks()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        byte[] one = [1];
        var hole0 = new RectF(100f, 50f, 320f, 180f);
        var hole1 = new RectF(160f, 90f, 320f, 180f);
        var hole2 = new RectF(220f, 130f, 320f, 180f);

        RenderFrame PublishAndAcquire(SceneFramePublisher seam, VideoSurfaceRegistry reg)
        {
            seam.Publish(one, default, default, video: reg);
            seam.TryAcquire(out RenderFrame rf);
            return rf;
        }

        // Frame N presents while the UI has already written frame N+1's rect: the video lands under N's hole.
        {
            var seam = new SceneFramePublisher();
            var reg = new VideoSurfaceRegistry();
            var applier = new VideoPlacementApplier(reg);
            var p = new PlacementLogPresenter();
            int token = reg.Acquire();
            reg.Bind(token, 0x10);
            reg.Place(token, hole0);
            RenderFrame frameN = PublishAndAcquire(seam, reg);
            reg.Place(token, hole1);   // the UI's next pump: not published yet
            applier.ApplyTurn(p, seam.VideoIntents(frameN), default, 1f, VideoApplyScope.Full, deferCommit: false);
            bool presentedFrameWins = p.LastPlace == hole0;
            RenderFrame frameN1 = PublishAndAcquire(seam, reg);
            applier.ApplyTurn(p, seam.VideoIntents(frameN1), default, 1f, VideoApplyScope.Full, deferCommit: false);
            Check("gate.video.hole-placement-same-frame the video was placed from the presented frame's snapshot, then from the next one's, never from the live registry",
                presentedFrameWins && p.LastPlace == hole1,
                $"first={presentedFrameWins} second={p.LastPlace}");

            // An elided turn (no record, same publication) holds geometry; a stood-down present holds it too.
            reg.Place(token, hole2);
            RenderFrame frameN2 = PublishAndAcquire(seam, reg);
            int places = p.Places;
            applier.ApplyTurn(p, seam.VideoIntents(frameN2), default, 1f, VideoApplyScope.ContentOnly, deferCommit: false);
            bool elidedHeld = p.Places == places;
            applier.ApplyTurn(p, seam.VideoIntents(frameN2), default, 1f, VideoApplyScope.ReleasesOnly, deferCommit: false);
            bool standDownHeld = p.Places == places;
            applier.ApplyTurn(p, seam.VideoIntents(frameN2), default, 1f, VideoApplyScope.Full, deferCommit: false);
            Check("gate.video.hole-placement-same-frame.geometry-only-on-a-presenting-turn an elided turn and a stood-down present placed nothing; the presenting turn placed the held rect",
                elidedHeld && standDownHeld && p.Places == places + 1 && p.LastPlace == hole2,
                $"elided={elidedHeld} standDown={standDownHeld} places {places}->{p.Places} last={p.LastPlace}");
        }

        // A composite-only turn (scroll / compositor animation re-posed the hole, no UI pump) moves the video as far as the hole.
        {
            var applier = new VideoPlacementApplier(new VideoSurfaceRegistry());
            var p = new PlacementLogPresenter();
            var published = new RectF(100f, 200f, 320f, 180f);
            VideoPresentIntent[] frame =
            [
                new VideoPresentIntent
                {
                    Token = 1, Gen = 1, HandleSeq = 1, DesiredHandle = 0x10, HasGeometry = true, Visible = true,
                    RectDip = published, ViewportDip = published, HasHoleOrigin = true, HoleX = published.X, HoleY = published.Y,
                },
            ];
            applier.ApplyTurn(p, frame, [new VideoPosedHole { Token = 1, Hole = published, EffClip = RectF.Infinite }], 1f, VideoApplyScope.Full, deferCommit: false);
            var posed = new RectF(100f, 137.25f, 320f, 180f);   // the render thread's scroll poser moved the hole up 62.75 DIP
            applier.ApplyTurn(p, frame, [new VideoPosedHole { Token = 1, Hole = posed, EffClip = RectF.Infinite }], 1f, VideoApplyScope.Full, deferCommit: false);
            float skew = MathF.Abs(p.LastPlace.Y - posed.Y);
            Check("gate.video.hole-placement-same-frame.posed-travel a composite-only turn moved the video to the posed hole within half a device pixel",
                skew <= 0.5f && applier.LastTurnMovedGeometry && p.Places == 2,
                $"video.y={p.LastPlace.Y} hole.y={posed.Y} skew={skew} moved={applier.LastTurnMovedGeometry} places={p.Places}");
        }

        // A publication the render thread skipped is superseded by the next one: the release it carried is not lost.
        {
            var seam = new SceneFramePublisher();
            var reg = new VideoSurfaceRegistry();
            var applier = new VideoPlacementApplier(reg);
            var p = new PlacementLogPresenter();
            int token = reg.Acquire();
            reg.Bind(token, 0x10);
            reg.Place(token, hole0);
            applier.ApplyTurn(p, seam.VideoIntents(PublishAndAcquire(seam, reg)), default, 1f, VideoApplyScope.Full, deferCommit: false);
            seam.Publish(one, default, default, video: reg);   // never adopted
            reg.Release(token);
            RenderFrame latest = PublishAndAcquire(seam, reg);
            applier.ApplyTurn(p, seam.VideoIntents(latest), default, 1f, VideoApplyScope.Full, deferCommit: false);
            reg.PumpPending(1f);
            Check("gate.video.hole-placement-same-frame.skipped-publication-keeps-the-release the surface was destroyed and the slot came back to the UI thread",
                p.Destroys == 1 && reg.Acquire() == token, $"destroys={p.Destroys}");
        }
    }
}
