using System;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// <see cref="ProtectedMediaSession"/>'s ABR tick over a fake player whose switches land (and show) only when the test
/// says so: no decision while a switch is pending (F139), a forced probe judged only on a sample from its own rung, an
/// 8 s minimum interval between switches with an emergency exception, switches that append after the buffered end by
/// default (retain window -1), a retain window only for a manual pin (0) and for the first upswitch after a viewport
/// raise (25 s), and the on-screen rung following the PICTURE while the ABR follows the downloading rung. Time is passed
/// in (and drives the controller), so nothing waits.
/// </summary>
public sealed class ProtectedAbrSwitchTests
{
    private static readonly int[] Heights = [240, 480, 720, 1080];

    private static ProtectedRepresentationDescriptor Rep(int height) => new()
    {
        Id = "r" + height,
        Quality = new QualityVariant("r" + height, height * 2_000, new SizeI(height * 16 / 9, height), 30,
            new MediaContentType(Container.Mp4, CodecId.H264, CodecId.None)),
        InitUrl = "https://media/r" + height + "/init.mp4",
        SegmentBaseUrl = "https://media/r" + height + "/",
        SegmentPrefix = "seg-",
        SegmentSuffix = ".m4s",
        SegmentCount = 10,
    };

    private static ProtectedAdaptiveCatalog Catalog()
    {
        var reps = new ProtectedRepresentationDescriptor[Heights.Length];
        for (int i = 0; i < reps.Length; i++) reps[i] = Rep(Heights[i]);
        return new ProtectedAdaptiveCatalog
        {
            Tracks = [new ProtectedTrackDescriptor
            {
                Id = 1, Kind = FluentGpu.Media.TrackKind.Video, Label = "Video", IsDefault = true,
                Representations = reps,
            }],
        };
    }

    /// <summary>One session on the 240/480/720/1080 ladder (480 kbps, 960 kbps, 1.44 Mbps, 2.16 Mbps) over a fake player
    /// that applies nothing by itself.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public long Now;
        public readonly AdaptiveBitrateController Abr = new();
        public readonly FakeProtectedVideoPlayer Player = new() { ApplySelectionImmediately = false, ForwardBufferedMs = 30_000 };
        public readonly MediaPlayerCore Core = new();
        public readonly MediaSignalSink Sink;
        public readonly ProtectedMediaSession Session;

        public Rig(int initialRung = 1)
        {
            Abr.NowMs = () => Now;
            var request = new ProtectedVideoRequest { InitUrl = Rep(Heights[initialRung]).InitUrl, Catalog = Catalog() };
            Session = new ProtectedMediaSession(Player, request, new MediaOpenOptions { Abr = Abr });
            Sink = new MediaSignalSink(Core);
            Session.ConnectSignals(Sink);
        }

        /// <summary>One ABR tick at <paramref name="now"/> ms.</summary>
        public void Tick(long now)
        {
            Now = now;
            Session.UpdateAdaptiveState(Sink, now);
        }

        /// <summary>A transfer long enough (120 s) that it REPLACES whatever the estimator held: the estimate becomes
        /// <paramref name="kbps"/> whatever came before.</summary>
        public void Decisive(double kbps) => Measure(kbps, 120);

        /// <summary>One throughput sample of <paramref name="kbps"/> over <paramref name="seconds"/>.</summary>
        public void Measure(double kbps, double seconds)
            => Abr.RecordDownload((long)(kbps * seconds * 1000 / 8), TimeSpan.FromSeconds(seconds));

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    // ── F139: a pending switch is not re-decided ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APendingSwitch_IsNotRedecided_AndTheOnScreenRungFollowsThePicture_NotTheSplice()
    {
        await using var rig = new Rig(initialRung: 1);   // opens on 480
        rig.Decisive(40_000);

        rig.Tick(1_000);   // a fast link: climb to 1080
        Assert.Single(rig.Player.Selections);
        Assert.Equal("r1080", rig.Player.Selections[0].Id);
        Assert.Equal(IProtectedVideoPlayer.AppendAtBufferEnd, rig.Player.Selections[0].RetainMs);   // nothing discarded

        rig.Decisive(400);   // the link collapses while the switch is still on its way
        rig.Tick(2_000);
        rig.Tick(3_500);
        Assert.Single(rig.Player.Selections);                  // no second decision, no revert
        Assert.Equal(3, rig.Abr.CurrentIndex);                 // the controller was not even consulted

        // The splice lands: 1080 is DOWNLOADING (the pending gate clears, the ABR baseline moves) ...
        rig.Player.DownloadingVideoRepresentationId = "r1080";
        rig.Player.ForwardBufferedMs = 20_000;
        rig.Tick(5_000);
        Assert.Equal(3, rig.Abr.CurrentIndex);
        Assert.Equal("r480", rig.Core.Qualities.Active.Peek()?.Id);   // ... but 480 is still what is ON SCREEN

        // The picture changes 40 s later, when delivery reaches the new representation.
        rig.Player.ActiveVideoRepresentationId = "r1080";
        rig.Tick(6_000);
        Assert.Equal("r1080", rig.Core.Qualities.Active.Peek()?.Id);
        Assert.Equal(new SizeI(1920, 1080), rig.Core.NaturalSize.Peek());
    }

    [Fact]
    public async Task APendingSwitchThatNeverLands_TimesOutAfter12Seconds_AndTheAbrDecidesAgain()
    {
        await using var rig = new Rig(initialRung: 1);
        rig.Decisive(40_000);
        rig.Tick(1_000);
        Assert.Single(rig.Player.Selections);

        rig.Tick(5_000);
        rig.Tick(13_000);   // exactly 12 s pending: not yet released
        Assert.Single(rig.Player.Selections);

        rig.Tick(14_001);   // released; the interval has long elapsed; the same pick is requested again
        Assert.Equal(2, rig.Player.Selections.Count);
        Assert.Equal("r1080", rig.Player.Selections[1].Id);
    }

    // ── F139: a forced probe is judged only on its own evidence ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AForcedProbe_IsNotRevertedBeforeASampleOfItsOwnRungExists()
    {
        await using var rig = new Rig(initialRung: 1);   // 960 kbps rung
        rig.Player.ForwardBufferedMs = 20_000;           // under 25 s: a measured downswitch would be IMMEDIATE
        rig.Decisive(1_300);                              // holds 960 kbps, cannot justify 1.44 Mbps by the estimate

        rig.Tick(1_000);                                  // steady from here
        Assert.Empty(rig.Player.Selections);
        rig.Tick(11_000);                                 // 10 s steady: the forced probe steps up one rung
        Assert.Single(rig.Player.Selections);
        Assert.Equal("r720", rig.Player.Selections[0].Id);
        Assert.Equal(AbrDecisionReason.ForcedProbe, rig.Abr.LastDecisionReason);

        rig.Player.DownloadingVideoRepresentationId = "r720";   // the probe rung is downloading
        rig.Tick(12_000);

        // Long after the 8 s interval, with the estimate (1.3 Mbps) over the probe rung's sustain budget: the OLD code
        // reverted here and counted a failure for a rung it never measured. Now the verdict waits for a sample.
        rig.Tick(20_000);
        Assert.Single(rig.Player.Selections);

        rig.Measure(800, 4);                              // one sample ON the probe rung, and it is poor
        rig.Tick(21_000);
        Assert.Equal(2, rig.Player.Selections.Count);     // now the revert happens
        Assert.Equal("r240", rig.Player.Selections[1].Id);
        Assert.Equal(IProtectedVideoPlayer.AppendAtBufferEnd, rig.Player.Selections[1].RetainMs);
    }

    [Fact]
    public async Task AProbeThatNeverLands_IsAbandoned_AndNothingWaitsForItsEvidence()
    {
        await using var rig = new Rig(initialRung: 1);
        rig.Player.ForwardBufferedMs = 20_000;
        rig.Decisive(1_300);
        rig.Tick(1_000);
        rig.Tick(11_000);   // the probe is requested ...
        Assert.Single(rig.Player.Selections);

        rig.Tick(23_001);   // ... never lands, and the 12 s pending timeout releases it: abandoned, not "failed"
        // A fresh decision is possible again (the controller is back on the downloading rung, the probe flag cleared):
        // the steady timer restarted, so no second probe yet, and crucially no hold either.
        Assert.Single(rig.Player.Selections);
        rig.Tick(34_000);   // 10 s of steady time since the abandon (base cadence, no backoff): the next probe
        Assert.Equal(2, rig.Player.Selections.Count);
        Assert.Equal("r720", rig.Player.Selections[1].Id);
    }

    // ── F148: buffer-gated downswitch and the minimum interval ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADownswitch_WithAFullBuffer_IsDeferred_ThenAppendsOnceTheBufferThins()
    {
        await using var rig = new Rig(initialRung: 3);   // on 1080 (2.16 Mbps)
        rig.Decisive(400);                                // cannot hold even the bottom rung

        rig.Tick(1_000);                                  // 30 s buffered: ride the dip out
        Assert.Empty(rig.Player.Selections);
        Assert.Equal(AbrDecisionReason.DeferredDecrease, rig.Abr.LastDecisionReason);

        rig.Player.ForwardBufferedMs = 20_000;            // thin enough: leave the rung
        rig.Tick(2_000);
        Assert.Single(rig.Player.Selections);
        Assert.Equal("r240", rig.Player.Selections[0].Id);
        Assert.Equal(IProtectedVideoPlayer.AppendAtBufferEnd, rig.Player.Selections[0].RetainMs);
    }

    [Fact]
    public async Task ASecondSwitchWithin8Seconds_IsRefused_AndAllowedOnceTheIntervalHasElapsed()
    {
        await using var rig = new Rig(initialRung: 1);
        rig.Decisive(40_000);
        rig.Tick(1_000);                                  // climb to 1080: the interval starts
        Assert.Single(rig.Player.Selections);
        rig.Player.DownloadingVideoRepresentationId = "r1080";
        rig.Player.ForwardBufferedMs = 20_000;            // under 25 s (a downswitch is immediate), over 10 s (no emergency)
        rig.Tick(2_000);

        rig.Decisive(400);                                // the link collapses
        rig.Tick(3_000);
        rig.Tick(7_000);                                  // 6 s since the request
        Assert.Single(rig.Player.Selections);
        Assert.Equal(3, rig.Abr.CurrentIndex);            // refused before the controller moved

        rig.Tick(9_000);                                  // 8 s: allowed
        Assert.Equal(2, rig.Player.Selections.Count);
        Assert.Equal("r240", rig.Player.Selections[1].Id);
    }

    [Fact]
    public async Task AnEmergencyDownswitch_BreaksTheInterval_AndStillAppends()
    {
        await using var rig = new Rig(initialRung: 1);
        rig.Decisive(40_000);
        rig.Tick(1_000);
        rig.Player.DownloadingVideoRepresentationId = "r1080";
        rig.Player.ForwardBufferedMs = 20_000;
        rig.Tick(2_000);
        Assert.Single(rig.Player.Selections);

        rig.Decisive(400);
        rig.Player.ForwardBufferedMs = 5_000;             // under 10 s: an emergency
        rig.Tick(3_000);                                  // 2 s after the last switch

        Assert.Equal(2, rig.Player.Selections.Count);
        Assert.Equal("r240", rig.Player.Selections[1].Id);
        // Not a truncating splice: the 5 s already buffered are playable and free, throwing them away at the slowest
        // moment of the link gains nothing.
        Assert.Equal(IProtectedVideoPlayer.AppendAtBufferEnd, rig.Player.Selections[1].RetainMs);
    }

    // ── the retain window ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AManualPin_LandsAtTheBoundaryAfterThePlayhead()
    {
        await using var rig = new Rig(initialRung: 1);

        await rig.Session.SelectQualityAsync(QualitySelection.Pin("r720"));

        Assert.Equal("r720", rig.Player.LastSelectedRepresentationId);
        Assert.Equal(0, rig.Player.LastSelectedRetainMs);
    }

    [Fact]
    public async Task TheFirstUpswitchAfterAViewportRaise_KeepsOnly25SecondsOfTheOldBuffer()
    {
        await using var rig = new Rig(initialRung: 1);
        rig.Decisive(40_000);
        rig.Session.ApplyViewportHeight(400, 100);        // docked: floored at 720 (the first cap commits at once)
        rig.Session.ApplyViewportHeight(1080, 5_000);     // fullscreen: a RAISE

        rig.Tick(6_000);

        Assert.Single(rig.Player.Selections);
        Assert.Equal("r1080", rig.Player.Selections[0].Id);
        Assert.Equal(25_000, rig.Player.Selections[0].RetainMs);
    }

    [Fact]
    public async Task AnUpswitchLongAfterTheViewportRaise_Appends()
    {
        await using var rig = new Rig(initialRung: 1);
        rig.Decisive(40_000);
        rig.Session.ApplyViewportHeight(400, 100);
        rig.Session.ApplyViewportHeight(1080, 5_000);

        rig.Tick(30_000);                                 // 25 s after the raise: it no longer wants the sharper picture NOW

        Assert.Single(rig.Player.Selections);
        Assert.Equal("r1080", rig.Player.Selections[0].Id);
        Assert.Equal(IProtectedVideoPlayer.AppendAtBufferEnd, rig.Player.Selections[0].RetainMs);
    }
}
