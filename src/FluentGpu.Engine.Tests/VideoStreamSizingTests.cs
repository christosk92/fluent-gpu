using FluentGpu.Foundation;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The stream-size policy both Windows video backends share (F071, F051): a destination asks for a BUCKET of the natural
/// frame (natural x {1, 3/4, 1/2, 1/3}, with hysteresis) rather than an arbitrary size, an unlaid-out rect asks for
/// nothing, and the size gate holds a change until the destination has been still, then keeps the compositor on the
/// previous content size until the backend echoes the new one as applied. Pure values and an injected clock: no engine,
/// no GPU, no window.
/// </summary>
public sealed class VideoStreamSizingTests
{
    private static readonly SizeI Hd = new(1920, 1080);

    private static RectF Rect(float w, float h) => new(0f, 0f, w, h);

    // ── the buckets ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1920f, 1080f, 1920, 1080)]    // the destination IS the frame
    [InlineData(3000f, 1700f, 1920, 1080)]    // bigger than the frame: never an upscale
    [InlineData(1440f, 810f, 1440, 810)]      // exactly 3/4
    [InlineData(1000f, 562f, 1440, 810)]      // 0.52 of the frame needs the 3/4 bucket, not 1/2
    [InlineData(960f, 540f, 960, 540)]        // exactly 1/2
    [InlineData(700f, 394f, 960, 540)]        // 0.36 needs the 1/2 bucket
    [InlineData(640f, 360f, 640, 360)]        // exactly 1/3
    [InlineData(100f, 56f, 640, 360)]         // never below the smallest bucket
    public void TheSmallestBucketThatCoversTheDestination_IsChosen(float w, float h, int cw, int ch)
        => Assert.Equal(new SizeI(cw, ch), VideoStreamSizing.BucketedSizeFor(Hd, Rect(w, h), 1f, default));

    [Fact]
    public void TheDeviceScaleCountsTowardsTheBucket()
        => Assert.Equal(new SizeI(960, 540), VideoStreamSizing.BucketedSizeFor(Hd, Rect(480f, 270f), 2f, default));

    [Fact]
    public void AnUnknownNaturalSize_FallsBackToTheDestination()
        => Assert.Equal(new SizeI(640, 360), VideoStreamSizing.BucketedSizeFor(SizeI.Zero, Rect(640f, 360f), 1f, default));

    [Fact]
    public void AllBucketsKeepTheFramesAspect()
    {
        var natural = new SizeI(3840, 2160);
        foreach (float w in new[] { 200f, 700f, 1100f, 1500f, 2500f })
        {
            SizeI c = VideoStreamSizing.BucketedSizeFor(natural, Rect(w, w * 9f / 16f), 1f, default);
            Assert.Equal(16.0 / 9.0, (double)c.Width / c.Height, 2);
        }
    }

    // ── hysteresis ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABucketThatStillCoversTheDestination_IsKept()
    {
        var current = new SizeI(1440, 810);
        Assert.Equal(current, VideoStreamSizing.BucketedSizeFor(Hd, Rect(1000f, 562f), 1f, current));   // same bucket
        // 0.469 of the frame would fit the 1/2 bucket, but not COMFORTABLY (10 % under it): no flip at the boundary.
        Assert.Equal(current, VideoStreamSizing.BucketedSizeFor(Hd, Rect(900f, 506f), 1f, current));
    }

    [Fact]
    public void ABucketIsLeftForASmallerOne_OnlyWhenTheDestinationFitsItComfortably()
        => Assert.Equal(new SizeI(960, 540), VideoStreamSizing.BucketedSizeFor(Hd, Rect(800f, 450f), 1f, new SizeI(1440, 810)));

    [Fact]
    public void ABucketThatNoLongerCoversTheDestination_IsReplacedAtOnce()
        => Assert.Equal(new SizeI(1440, 810), VideoStreamSizing.BucketedSizeFor(Hd, Rect(1000f, 562f), 1f, new SizeI(960, 540)));

    [Fact]
    public void ASizeThatIsNotABucketOfThisNaturalSize_IsNotKept()
        => Assert.Equal(new SizeI(960, 540), VideoStreamSizing.BucketedSizeFor(Hd, Rect(960f, 540f), 1f, new SizeI(3000, 1688)));

    [Fact]
    public void Serves_IsTrueWhileTheContentIsWhatThePolicyWouldKeep()
    {
        var content = new SizeI(1440, 810);
        Assert.True(VideoStreamSizing.Serves(content, Hd, Rect(1000f, 562f), 1f));    // a resize inside the bucket
        Assert.False(VideoStreamSizing.Serves(content, Hd, Rect(1700f, 950f), 1f));   // the destination outgrew it
        Assert.False(VideoStreamSizing.Serves(default, Hd, Rect(1000f, 562f), 1f));   // nothing sized yet
    }

    // ── F051: an unlaid-out rect is no destination ──────────────────────────────────────────────────────────────────

    [Fact]
    public void IsLaidOut_NeedsBothExtentsPositive()
    {
        Assert.False(VideoStreamSizing.IsLaidOut(default));
        Assert.False(VideoStreamSizing.IsLaidOut(new RectF(0f, 0f, 640f, 0f)));
        Assert.False(VideoStreamSizing.IsLaidOut(new RectF(0f, 0f, float.NaN, 360f)));
        Assert.True(VideoStreamSizing.IsLaidOut(new RectF(10f, 10f, 2f, 2f)));
    }

    [Fact]
    public void ContentSizeFor_AnEmptyRect_IsNeverATwoByOneSwapChain()
    {
        Assert.Equal(Hd, VideoStreamSizing.ContentSizeFor(Hd, default, 1f));              // was 2x1
        Assert.Equal(SizeI.Zero, VideoStreamSizing.ContentSizeFor(SizeI.Zero, default, 1f));
    }

    [Fact]
    public void TheGate_AnEmptyRectDecidesNothing_AndKeepsWhatWasPublished()
    {
        var gate = new VideoStreamSizeGate();
        VideoStreamStep none = gate.Step(Hd, default, 1f, default, playing: false, nowMs: 0);
        Assert.True(none.Request.IsEmpty);
        Assert.True(none.Content.IsEmpty);
        Assert.Equal(0, none.RetryInMs);

        gate.Step(Hd, Rect(640f, 360f), 1f, default, playing: false, nowMs: 10);
        VideoStreamStep after = gate.Step(Hd, default, 1f, default, playing: false, nowMs: 20);
        Assert.True(after.Request.IsEmpty);
        Assert.Equal(new SizeI(640, 360), after.Content);   // the last good size, not a placeholder
    }

    // ── F071: settle, then request; echo, then publish ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheGate_TheFirstSizeIsRequestedAndPublishedAtOnce()
    {
        var gate = new VideoStreamSizeGate();
        VideoStreamStep first = gate.Step(Hd, Rect(640f, 360f), 1f, default, playing: false, nowMs: 0);
        Assert.Equal(new SizeI(640, 360), first.Request);
        Assert.Equal(new SizeI(640, 360), first.Content);
        Assert.Equal(0, first.RetryInMs);
    }

    [Fact]
    public void TheGate_ABackendThatAlreadyHoldsABuffer_IsScaledFromThatBufferUntilItEchoesTheRequest()
    {
        var gate = new VideoStreamSizeGate();
        // The swap chain already exists at the natural size (what the protected runtime creates it at): the compositor
        // must scale THAT buffer, not the size just asked for.
        VideoStreamStep first = gate.Step(Hd, Rect(640f, 360f), 1f, Hd, playing: false, nowMs: 0);
        Assert.Equal(new SizeI(640, 360), first.Request);
        Assert.Equal(Hd, first.Content);
        Assert.Equal(VideoStreamSizeGate.EchoPollMs, first.RetryInMs);   // the echo raises no event: the gate must ask for the follow-up pump
        VideoStreamStep echoed = gate.Step(Hd, Rect(640f, 360f), 1f, new SizeI(640, 360), playing: false, nowMs: 40);
        Assert.Equal(new SizeI(640, 360), echoed.Content);
    }

    [Fact]
    public void TheGate_ABackendThatHoldsABufferAndNeverEchoesWhilePaused_IsTrustedAtTheEchoTimeoutFromTheFirstStep()
    {
        var gate = new VideoStreamSizeGate();
        gate.Step(Hd, Rect(640f, 360f), 1f, Hd, playing: false, nowMs: 0);
        VideoStreamStep waiting = gate.Step(Hd, Rect(640f, 360f), 1f, Hd, playing: false, nowMs: VideoStreamSizeGate.EchoTimeoutMs - 1);
        Assert.Equal(Hd, waiting.Content);
        Assert.Equal(1, waiting.RetryInMs);
        VideoStreamStep trusted = gate.Step(Hd, Rect(640f, 360f), 1f, Hd, playing: false, nowMs: VideoStreamSizeGate.EchoTimeoutMs);
        Assert.Equal(new SizeI(640, 360), trusted.Content);
        Assert.Equal(0, trusted.RetryInMs);
    }

    [Fact]
    public void TheGate_AChangeWaitsForStableGeometry_ThenIsRequested_AndThePreviousContentSurvivesUntilTheEcho()
    {
        var gate = new VideoStreamSizeGate();
        var small = new SizeI(640, 360);
        var big = new SizeI(1440, 810);
        gate.Step(Hd, Rect(640f, 360f), 1f, default, playing: true, nowMs: 0);

        // The destination grew past the bucket: nothing is asked while it may still be moving.
        VideoStreamStep t100 = gate.Step(Hd, Rect(1000f, 562f), 1f, small, playing: true, nowMs: 100);
        Assert.True(t100.Request.IsEmpty);
        Assert.Equal(small, t100.Content);
        Assert.Equal(VideoStreamSizeGate.SettleMs, t100.RetryInMs);          // come back when it would have settled

        VideoStreamStep t200 = gate.Step(Hd, Rect(1000f, 562f), 1f, small, playing: true, nowMs: 200);
        Assert.True(t200.Request.IsEmpty);
        Assert.Equal(VideoStreamSizeGate.SettleMs - 100, t200.RetryInMs);

        // Still for 250 ms: asked for. The compositor keeps scaling the old buffer by the old size.
        VideoStreamStep t350 = gate.Step(Hd, Rect(1000f, 562f), 1f, small, playing: true, nowMs: 350);
        Assert.Equal(big, t350.Request);
        Assert.Equal(small, t350.Content);
        Assert.Equal(VideoStreamSizeGate.EchoPollMs, t350.RetryInMs);          // an echo is awaited: look again soon

        // Asked for ONCE: the next pump posts nothing new while the echo is outstanding.
        VideoStreamStep t380 = gate.Step(Hd, Rect(1000f, 562f), 1f, small, playing: true, nowMs: 380);
        Assert.True(t380.Request.IsEmpty);
        Assert.Equal(small, t380.Content);

        // The backend echoes the applied size: only now does the compositor learn it.
        VideoStreamStep t400 = gate.Step(Hd, Rect(1000f, 562f), 1f, big, playing: true, nowMs: 400);
        Assert.True(t400.Request.IsEmpty);
        Assert.Equal(big, t400.Content);
        Assert.Equal(0, t400.RetryInMs);
    }

    [Fact]
    public void TheGate_NeverRequestsMidAnimation()
    {
        var gate = new VideoStreamSizeGate();
        gate.Step(Hd, Rect(960f, 540f), 1f, default, playing: true, nowMs: 0);
        // A continuous resize past the 1/2 bucket: every frame is a new rect, so the size never settles.
        for (int i = 1; i <= 40; i++)
        {
            VideoStreamStep s = gate.Step(Hd, Rect(1000f + 6f * i, 562f + 3.4f * i), 1f, new SizeI(960, 540), playing: true, nowMs: i * 16L);
            Assert.True(s.Request.IsEmpty, $"frame {i}: no stream-size request while the rect is still changing");
            Assert.Equal(new SizeI(960, 540), s.Content);
        }
        Assert.Equal(new SizeI(960, 540), gate.Requested);
    }

    [Fact]
    public void TheGate_AJitterInsideTheBucket_AsksForNothing()
    {
        var gate = new VideoStreamSizeGate();
        gate.Step(Hd, Rect(1000f, 562f), 1f, default, playing: true, nowMs: 0);   // 3/4 bucket
        for (int i = 1; i <= 20; i++)
        {
            VideoStreamStep s = gate.Step(Hd, Rect(900f + 8f * i, 506f + 4.5f * i), 1f, new SizeI(1440, 810), playing: true, nowMs: i * 400L);
            Assert.True(s.Request.IsEmpty);
            Assert.Equal(0, s.RetryInMs);
        }
    }

    [Fact]
    public void TheGate_ANewNaturalSize_IsAskedForAtOnce_EvenWhenTheDerivedSizeIsUnchanged()
    {
        var gate = new VideoStreamSizeGate();
        gate.Step(new SizeI(1280, 720), Rect(640f, 360f), 1f, default, playing: true, nowMs: 0);
        // The ABR upgraded the rung: the destination is the same, no settle to wait out, and the same derived size is
        // re-asserted (a backend that keeps its stream size per source must hear it again).
        VideoStreamStep s = gate.Step(Hd, Rect(640f, 360f), 1f, new SizeI(640, 360), playing: true, nowMs: 10);
        Assert.Equal(new SizeI(640, 360), s.Request);
    }

    [Fact]
    public void TheGate_APausedBackendIsTrustedAfterTheEchoTimeout_APlayingOneIsNot()
    {
        SizeI small = new(640, 360), big = new(1440, 810);
        foreach (bool playing in new[] { false, true })
        {
            var gate = new VideoStreamSizeGate();
            gate.Step(Hd, Rect(640f, 360f), 1f, default, playing, nowMs: 0);
            gate.Step(Hd, Rect(1000f, 562f), 1f, small, playing, nowMs: 100);
            Assert.Equal(big, gate.Step(Hd, Rect(1000f, 562f), 1f, small, playing, nowMs: 350).Request);

            VideoStreamStep mid = gate.Step(Hd, Rect(1000f, 562f), 1f, small, playing, nowMs: 600);
            Assert.Equal(small, mid.Content);   // 250 ms in: still waiting, either way

            VideoStreamStep late = gate.Step(Hd, Rect(1000f, 562f), 1f, small, playing, nowMs: 850);
            if (playing)
            {
                // A playing backend that has echoed something else is not guessed at: its buffer IS still the old size.
                Assert.Equal(small, late.Content);
                Assert.Equal(0, late.RetryInMs);
            }
            else
            {
                Assert.Equal(big, late.Content);   // nothing else will re-present while paused: the timeout fallback
            }
        }
    }

    [Fact]
    public void TheGate_ABackendThatNeverEchoes_IsTrustedAfterTheTimeout_EvenWhilePlaying()
    {
        var gate = new VideoStreamSizeGate();
        gate.Step(Hd, Rect(640f, 360f), 1f, default, playing: true, nowMs: 0);
        gate.Step(Hd, Rect(1000f, 562f), 1f, default, playing: true, nowMs: 100);
        Assert.Equal(new SizeI(1440, 810), gate.Step(Hd, Rect(1000f, 562f), 1f, default, playing: true, nowMs: 350).Request);
        Assert.Equal(new SizeI(640, 360), gate.Step(Hd, Rect(1000f, 562f), 1f, default, playing: true, nowMs: 600).Content);
        Assert.Equal(new SizeI(1440, 810), gate.Step(Hd, Rect(1000f, 562f), 1f, default, playing: true, nowMs: 850).Content);
    }
}
