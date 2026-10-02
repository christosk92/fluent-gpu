using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Demand lifetime, source epochs, and the real allocation-free audio/control level handoff — plus the SPECTRUM
/// tier (a second, reference-counted lease over the same source token that implies the level tap; double-buffered
/// magnitudes read only through <c>CopySpectrum</c>; the PRE-gain ring the RT fills only under that lease; the
/// content-domain window alignment).</summary>
public sealed class AudioVisualizerDemandTests
{
    [Fact]
    public void Demand_IsReferenceCounted_AndLeaseDisposalIsIdempotent()
    {
        var effects = new AudioEffects();
        long source = effects.BindVisualizerSource();
        Assert.Equal(0, effects.VisualizerDemand(source));
        using var first = effects.AcquireVisualizer();
        long epoch = effects.VisualizerDemand(source);
        Assert.True(epoch > 0);
        using var second = effects.AcquireVisualizer();
        Assert.Equal(epoch, effects.VisualizerDemand(source));
        Assert.True(effects.PublishVisualizerFrame(source, epoch, 0.2f, 0.4f));
        first.Dispose();
        first.Dispose();
        Assert.Equal(epoch, effects.VisualizerDemand(source));
        Assert.Equal(0.4f, effects.Visualizer.Peek().Peak);
        second.Dispose();
        Assert.Equal(0, effects.VisualizerDemand(source));
        Assert.Equal(VisualizerFrame.Silence, effects.Visualizer.Peek());
        second.Dispose();
        using var third = effects.AcquireVisualizer();
        Assert.True(effects.VisualizerDemand(source) > epoch);
    }

    [Fact]
    public void ReacquiringDemand_RejectsLatePublicationFromPreviousVisibilityEpoch()
    {
        var effects = new AudioEffects();
        long source = effects.BindVisualizerSource();
        var oldLease = effects.AcquireVisualizer();
        long staleEpoch = effects.VisualizerDemand(source);
        oldLease.Dispose();
        Assert.False(effects.PublishVisualizerFrame(source, staleEpoch, 0.3f, 0.6f));
        using var currentLease = effects.AcquireVisualizer();
        long currentEpoch = effects.VisualizerDemand(source);
        Assert.NotEqual(staleEpoch, currentEpoch);
        Assert.False(effects.PublishVisualizerFrame(source, staleEpoch, 0.3f, 0.6f));
        Assert.Equal(VisualizerFrame.Silence, effects.Visualizer.Peek());
        Assert.True(effects.PublishVisualizerFrame(source, currentEpoch, 0.1f, 0.2f));
        Assert.Equal(0.2f, effects.Visualizer.Value.Peak);
    }

    [Fact]
    public void ReplacedSource_InvalidatesOldDemandAndPublications_WithoutReleasingVisibleLease()
    {
        var effects = new AudioEffects();
        using var demand = effects.AcquireVisualizer();
        long oldSource = effects.BindVisualizerSource();
        long oldEpoch = effects.VisualizerDemand(oldSource);
        Assert.True(effects.PublishVisualizerFrame(oldSource, oldEpoch, 0.2f, 0.8f));
        long newSource = effects.BindVisualizerSource();
        long newEpoch = effects.VisualizerDemand(newSource);
        Assert.NotEqual(oldSource, newSource);
        Assert.NotEqual(oldEpoch, newEpoch);
        Assert.True(newEpoch > 0);
        Assert.Equal(0, effects.VisualizerDemand(oldSource));
        Assert.Equal(VisualizerFrame.Silence, effects.Visualizer.Peek());
        Assert.False(effects.PublishVisualizerFrame(oldSource, oldEpoch, 0.2f, 0.8f));
        Assert.False(effects.PublishVisualizerFrame(oldSource, newEpoch, 0.2f, 0.8f));
        Assert.False(effects.PublishVisualizerFrame(newSource, oldEpoch, 0.2f, 0.8f));
        Assert.False(effects.PublishVisualizerFrame(newSource, 0, 0.2f, 0.8f));
        Assert.True(effects.PublishVisualizerFrame(newSource, newEpoch, 0.3f, 0.7f));
    }

    [Fact]
    public async Task ConcurrentDisposal_OfOneLease_DoesNotConsumeAnotherLease()
    {
        var effects = new AudioEffects();
        long source = effects.BindVisualizerSource();
        using var survivor = effects.AcquireVisualizer();
        var disposed = effects.AcquireVisualizer();
        long epoch = effects.VisualizerDemand(source);
        var tasks = new Task[8];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(() => { for (int j = 0; j < 100; j++) disposed.Dispose(); });
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(epoch, effects.VisualizerDemand(source));
        survivor.Dispose();
        Assert.Equal(0, effects.VisualizerDemand(source));
        using var fresh = effects.AcquireVisualizer();
        Assert.True(effects.VisualizerDemand(source) > epoch);
    }

    [Fact]
    public void Mailbox_IsInitiallyEmpty_AndPreservesExactFloatPayloads()
    {
        var mailbox = new AudioLevelMailbox();
        Assert.False(mailbox.TryRead(out _, out _, out _, out _));
        float rms = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        float peak = BitConverter.Int32BitsToSingle(0x3f654321);
        mailbox.Publish(rms, peak, 17);
        Assert.True(mailbox.TryRead(out float readRms, out float readPeak, out long epoch, out long firstVersion));
        Assert.Equal(BitConverter.SingleToInt32Bits(rms), BitConverter.SingleToInt32Bits(readRms));
        Assert.Equal(BitConverter.SingleToInt32Bits(peak), BitConverter.SingleToInt32Bits(readPeak));
        Assert.Equal(17, epoch);
        mailbox.Publish(0.25f, 0.5f, 18);
        Assert.True(mailbox.TryRead(out readRms, out readPeak, out epoch, out long nextVersion));
        Assert.True(nextVersion > firstVersion);
        Assert.Equal(0.25f, readRms);
        Assert.Equal(0.5f, readPeak);
        Assert.Equal(18, epoch);
    }

    [Fact]
    public async Task Mailbox_ConcurrentWriterReader_NeverAcceptsMixedSamplesOrEpochs()
    {
        var holder = new MailboxHolder(); // keep both tasks operating on the same struct, not copies
        using var start = new ManualResetEventSlim();
        const int count = 200000;
        var writer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 1; i <= count; i++) holder.Mailbox.Publish(i, -i, i + 11L);
        });
        var reader = Task.Run(() =>
        {
            start.Wait();
            long previousVersion = 0;
            for (int i = 0; i < count; i++)
            {
                if (!holder.Mailbox.TryRead(out float rms, out float peak, out long epoch, out long version)) continue;
                Assert.Equal(-rms, peak);
                Assert.Equal((long)rms + 11, epoch);
                Assert.True(version >= previousVersion);
                previousVersion = version;
            }
        });
        start.Set();
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(holder.Mailbox.TryRead(out float finalRms, out float finalPeak, out long finalEpoch, out _));
        Assert.Equal((float)count, finalRms);
        Assert.Equal((float)-count, finalPeak);
        Assert.Equal(count + 11L, finalEpoch);
    }

    [Fact]
    public void Mailbox_SteadyPublishAndRead_AllocateNothing()
    {
        var mailbox = new AudioLevelMailbox();
        for (int i = 0; i < 1000; i++) { mailbox.Publish(0.2f, 0.8f, i); mailbox.TryRead(out _, out _, out _, out _); }
        int accepted = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            mailbox.Publish(0.2f, 0.8f, i);
            if (mailbox.TryRead(out _, out _, out _, out _)) accepted++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10000, accepted);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task PcmRendering_OnlyScansSamplesWhileDemandExists()
    {
        var effects = new AudioEffects();
        await using var session = CreateSession(effects);
        for (int i = 0; i < 8; i++) Assert.Equal(256, session.RenderBlock(256));
        Assert.Equal(0, session.MeterSamples);
        using var demand = effects.AcquireVisualizer();
        for (int i = 0; i < 8; i++) Assert.Equal(256, session.RenderBlock(256));
        Assert.Equal(8L * 256 * 2, session.MeterSamples);
        demand.Dispose();
        for (int i = 0; i < 8; i++) Assert.Equal(256, session.RenderBlock(256));
        Assert.Equal(8L * 256 * 2, session.MeterSamples);
        Assert.Equal(VisualizerFrame.Silence, effects.Visualizer.Peek());
        using var resumedDemand = effects.AcquireVisualizer();
        Assert.Equal(256, session.RenderBlock(256));
        Assert.Equal(9L * 256 * 2, session.MeterSamples);
    }

    [Fact]
    public async Task PcmSourceReplacement_StopsOldSessionAnalysis_AndEnablesNewSession()
    {
        var effects = new AudioEffects();
        using var demand = effects.AcquireVisualizer();
        await using var oldSession = CreateSession(effects);
        Assert.Equal(256, oldSession.RenderBlock(256));
        Assert.Equal(512, oldSession.MeterSamples);
        await using var newSession = CreateSession(effects);
        Assert.Equal(256, oldSession.RenderBlock(256));
        Assert.Equal(512, oldSession.MeterSamples);
        Assert.Equal(256, newSession.RenderBlock(256));
        Assert.Equal(512, newSession.MeterSamples);
    }

    [Fact]
    public async Task RolledBackSession_ReclaimsDemand_AndRejectsBothStaleMailboxes()
    {
        var effects = new AudioEffects();
        using var demand = effects.AcquireVisualizer();
        await using var oldSession = CreateSession(effects);
        oldSession.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = oldSession.PlayAsync();
        for (int i = 0; i < 3; i++) oldSession.TickControl(256);
        Assert.Equal(PlaybackState.Playing, oldSession.CurrentState);
        Assert.Equal(256, oldSession.RenderBlock(256)); // old epoch's unread mailbox

        await using var freshSession = CreateSession(effects);
        freshSession.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = freshSession.PlayAsync();
        for (int i = 0; i < 3; i++) freshSession.TickControl(256);
        Assert.Equal(PlaybackState.Playing, freshSession.CurrentState);
        Assert.Equal(256, freshSession.RenderBlock(256)); // replacement epoch's unread mailbox
        var graphBefore = oldSession.Graph.Live;

        oldSession.ActivateVisualizerSource();
        Assert.Same(graphBefore, oldSession.Graph.Live);
        Assert.Equal(VisualizerFrame.Silence, effects.Visualizer.Peek());
        freshSession.TickControl(256); // late control publication from abandoned replacement is rejected
        oldSession.TickControl(256); // original session's pre-replacement epoch is stale too
        Assert.Equal(VisualizerFrame.Silence, effects.Visualizer.Peek());
        long freshSamples = freshSession.MeterSamples;
        Assert.Equal(256, freshSession.RenderBlock(256));
        Assert.Equal(freshSamples, freshSession.MeterSamples); // only restored source has demand
        await freshSession.DisposeAsync();

        // Match the app's ordering: the fresh session is disposed, the old session is restored, then its token rotates.
        oldSession.ActivateVisualizerSource();
        Assert.Equal(256, oldSession.RenderBlock(256));
        oldSession.TickControl(256);
        Assert.Equal(1024, oldSession.MeterSamples);
        Assert.True(effects.Visualizer.Peek().Peak > 0f);
        Assert.True(effects.Visualizer.Peek().Rms > 0f);
    }

    [Fact]
    public void NullEffects_LeaseIsInertAndReusable()
    {
        var effects = NullAudioEffects.Instance;
        using var first = effects.AcquireVisualizer();
        using var second = effects.AcquireVisualizer();
        first.Dispose();
        first.Dispose();
        Assert.Equal(VisualizerFrame.Silence, effects.Visualizer.Peek());
    }

    // ── the SPECTRUM tier ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SpectrumLease_IsReferenceCounted_AndImpliesTheLevelTap()
    {
        var effects = new AudioEffects();
        long source = effects.BindVisualizerSource();
        Assert.Equal(0, effects.SpectrumDemand(source));
        var first = effects.AcquireSpectrum();
        long spectrumEpoch = effects.SpectrumDemand(source);
        Assert.True(spectrumEpoch > 0);
        Assert.True(effects.VisualizerDemand(source) > 0);              // the FFT tier implies the level tap
        var second = effects.AcquireSpectrum();
        Assert.Equal(spectrumEpoch, effects.SpectrumDemand(source));    // reference-counted: a second lease keeps the epoch
        first.Dispose();
        first.Dispose();                                                // idempotent
        Assert.Equal(spectrumEpoch, effects.SpectrumDemand(source));
        Assert.True(effects.VisualizerDemand(source) > 0);
        second.Dispose();
        Assert.Equal(0, effects.SpectrumDemand(source));
        Assert.Equal(0, effects.VisualizerDemand(source));              // no level lease of its own: the tap is off too
        second.Dispose();                                               // a second dispose is a no-op
        Assert.Equal(0, effects.SpectrumDemand(source));
        Assert.Equal(0, effects.VisualizerDemand(source));
        using var levelOnly = effects.AcquireVisualizer();              // a plain level lease never turns the FFT tier on
        Assert.True(effects.VisualizerDemand(source) > 0);
        Assert.Equal(0, effects.SpectrumDemand(source));
    }

    [Fact]
    public void PublishSpectrum_SwapsBuffersAndNeverTearsACopy()
    {
        var effects = new AudioEffects();
        long source = effects.BindVisualizerSource();
        using var lease = effects.AcquireSpectrum();
        long epoch = effects.SpectrumDemand(source);
        var dst = new float[SpectrumAnalyzer.DefaultBandCount];
        Assert.Equal(0, effects.CopySpectrum(dst, out var beforeAny));
        Assert.Equal(0, beforeAny.Sequence);
        Assert.True(beforeAny.Live);

        var first = new float[SpectrumAnalyzer.DefaultBandCount];
        for (int i = 0; i < first.Length; i++) first[i] = i;
        Assert.True(effects.PublishSpectrum(source, epoch, first, muted: false, windowRms: 0.25f, alignFrames: 321, fftMs: 0.05f));
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, effects.CopySpectrum(dst, out var info1));
        Assert.Equal(first, dst);
        Assert.Equal(1, info1.Sequence);
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, info1.BandCount);
        Assert.False(info1.Muted);
        Assert.Equal(0.25f, info1.WindowRms);
        Assert.Equal(321, info1.AlignFrames);
        Assert.Equal(0.05f, info1.FftMs);
        Assert.True(info1.Live);

        var second = new float[SpectrumAnalyzer.DefaultBandCount];
        for (int i = 0; i < second.Length; i++) second[i] = -10f - i;
        Assert.True(effects.PublishSpectrum(source, epoch, second, muted: true, windowRms: 0.5f, alignFrames: 99, fftMs: 0.07f));
        Assert.Equal(first, dst);                                       // the earlier COPY is a snapshot, not a view of the live buffer
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, effects.CopySpectrum(dst, out var info2));
        Assert.Equal(second, dst);
        Assert.Equal(2, info2.Sequence);
        Assert.True(info2.Muted);
        Assert.Equal(0.5f, info2.WindowRms);
        Assert.Equal(99, info2.AlignFrames);
        Assert.Equal(0.07f, info2.FftMs);

        // O2: the level frame is untouched and its Magnitudes stay EMPTY — the spectrum has exactly one read path.
        Assert.True(effects.Visualizer.Peek().Magnitudes.IsEmpty);
        Assert.Equal(VisualizerFrame.Silence, effects.Visualizer.Peek());
        Assert.Equal(7, effects.CopySpectrum(new float[7], out var narrow));   // a short destination copies what fits
        Assert.Equal(7, narrow.BandCount);
    }

    [Fact]
    public async Task CopySpectrum_ConcurrentWithPublish_NeverReturnsAMixedBuffer()
    {
        var effects = new AudioEffects();
        long source = effects.BindVisualizerSource();
        using var lease = effects.AcquireSpectrum();
        long epoch = effects.SpectrumDemand(source);
        using var start = new ManualResetEventSlim();
        const int count = 20000;
        var writer = Task.Run(() =>
        {
            var bands = new float[SpectrumAnalyzer.DefaultBandCount];
            start.Wait();
            for (int i = 1; i <= count; i++)
            {
                Array.Fill(bands, (float)i);
                Assert.True(effects.PublishSpectrum(source, epoch, bands, muted: false, windowRms: i, alignFrames: i, fftMs: 0f));
            }
        });
        var reader = Task.Run(() =>
        {
            var dst = new float[SpectrumAnalyzer.DefaultBandCount];
            start.Wait();
            long lastSequence = 0;
            while (!writer.IsCompleted)
            {
                int n = effects.CopySpectrum(dst, out var info);
                if (n == 0) continue;
                Assert.Equal(SpectrumAnalyzer.DefaultBandCount, n);
                for (int b = 1; b < n; b++) Assert.Equal(dst[0], dst[b]);   // one publish's values only — never a mix of two
                Assert.Equal(info.WindowRms, dst[0]);                        // and the info belongs to the same publish
                Assert.True(info.Sequence >= lastSequence);
                lastSequence = info.Sequence;
            }
        });
        start.Set();
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, effects.CopySpectrum(new float[SpectrumAnalyzer.DefaultBandCount], out var last));
        Assert.Equal(count, last.Sequence);
    }

    [Fact]
    public void PublishSpectrum_IsRejectedForAStaleEpochOrSource()
    {
        var effects = new AudioEffects();
        long source = effects.BindVisualizerSource();
        var bands = new float[SpectrumAnalyzer.DefaultBandCount];
        var dst = new float[SpectrumAnalyzer.DefaultBandCount];
        var oldLease = effects.AcquireSpectrum();
        long staleEpoch = effects.SpectrumDemand(source);
        oldLease.Dispose();
        Assert.False(effects.PublishSpectrum(source, staleEpoch, bands, false, 0f, 0, 0f));   // the lease is gone

        using var currentLease = effects.AcquireSpectrum();
        long currentEpoch = effects.SpectrumDemand(source);
        Assert.NotEqual(staleEpoch, currentEpoch);
        Assert.False(effects.PublishSpectrum(source, staleEpoch, bands, false, 0f, 0, 0f));   // a late publish from the previous lease
        Assert.False(effects.PublishSpectrum(source, 0, bands, false, 0f, 0, 0f));
        Assert.Equal(0, effects.CopySpectrum(dst, out _));
        Assert.True(effects.PublishSpectrum(source, currentEpoch, bands, false, 0f, 0, 0f));

        long newSource = effects.BindVisualizerSource();                                       // a replaced session
        long newEpoch = effects.SpectrumDemand(newSource);
        Assert.NotEqual(source, newSource);
        Assert.True(newEpoch > 0);                                                             // the lease survives the rebind
        Assert.NotEqual(currentEpoch, newEpoch);
        Assert.Equal(0, effects.SpectrumDemand(source));
        Assert.Equal(0, effects.CopySpectrum(dst, out _));                                     // the old session's bands are cleared
        Assert.False(effects.PublishSpectrum(source, currentEpoch, bands, false, 0f, 0, 0f));
        Assert.False(effects.PublishSpectrum(source, newEpoch, bands, false, 0f, 0, 0f));
        Assert.False(effects.PublishSpectrum(newSource, currentEpoch, bands, false, 0f, 0, 0f));
        Assert.True(effects.PublishSpectrum(newSource, newEpoch, bands, false, 0f, 0, 0f));
    }

    [Fact]
    public void ReleasingTheSpectrumLease_ZeroesCopySpectrum_AndKeepsTheLevelFrameUnderALevelLease()
    {
        var effects = new AudioEffects();
        long source = effects.BindVisualizerSource();
        using var levelLease = effects.AcquireVisualizer();             // an extra level lease keeps the tap alive (V-E14)
        var spectrumLease = effects.AcquireSpectrum();
        long levelEpoch = effects.VisualizerDemand(source);
        long spectrumEpoch = effects.SpectrumDemand(source);
        var bands = new float[SpectrumAnalyzer.DefaultBandCount];
        var dst = new float[SpectrumAnalyzer.DefaultBandCount];
        Assert.True(effects.PublishVisualizerFrame(source, levelEpoch, 0.2f, 0.4f));
        Assert.True(effects.PublishSpectrum(source, spectrumEpoch, bands, false, 0.2f, 100, 0.02f));
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, effects.CopySpectrum(dst, out var live));
        Assert.True(live.Live);

        spectrumLease.Dispose();
        Assert.Equal(0, effects.CopySpectrum(dst, out var gone));
        Assert.False(gone.Live);
        Assert.Equal(0, effects.SpectrumDemand(source));
        Assert.Equal(levelEpoch, effects.VisualizerDemand(source));     // the level tap is still demanded
        Assert.Equal(0.4f, effects.Visualizer.Peek().Peak);
        Assert.Equal(0.2f, effects.Visualizer.Peek().Rms);
        Assert.True(effects.Visualizer.Peek().Magnitudes.IsEmpty);
    }

    [Fact]
    public void SpectrumOffsetMs_IsClampedToPlusMinus500()
    {
        var effects = new AudioEffects();
        Assert.Equal(0f, effects.SpectrumOffsetMs);
        effects.SpectrumOffsetMs = 120f;
        Assert.Equal(120f, effects.SpectrumOffsetMs);
        effects.SpectrumOffsetMs = -37.5f;
        Assert.Equal(-37.5f, effects.SpectrumOffsetMs);
        effects.SpectrumOffsetMs = 9999f;
        Assert.Equal(500f, effects.SpectrumOffsetMs);
        effects.SpectrumOffsetMs = -9999f;
        Assert.Equal(-500f, effects.SpectrumOffsetMs);
    }

    [Fact]
    public void NullEffects_SpectrumLeaseIsInertAndCopyReturnsNothing()
    {
        var effects = NullAudioEffects.Instance;
        using var first = effects.AcquireSpectrum();
        using var second = effects.AcquireSpectrum();
        first.Dispose();
        first.Dispose();
        Assert.Equal(0, effects.CopySpectrum(new float[SpectrumAnalyzer.DefaultBandCount], out var info));
        Assert.Equal(default(SpectrumInfo), info);
    }

    [Fact]
    public async Task PcmRendering_FillsTheSpectrumRingOnlyUnderASpectrumLease()
    {
        var effects = new AudioEffects();
        await using var session = CreateSession(effects);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        var dst = new float[SpectrumAnalyzer.DefaultBandCount];
        for (int i = 0; i < 12; i++) session.PumpAudio(512);            // Opening→Buffering→Playing, then renders
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
        Assert.Equal(0, session.SpectrumPublishes);
        Assert.Equal(0, effects.CopySpectrum(dst, out _));

        session.SetVolume(0.25);                                        // the picture must not follow the volume slider (asserted below)
        using var lease = effects.AcquireSpectrum();
        for (int i = 0; i < 12; i++) session.PumpAudio(512);            // control tick creates the ring, the RT arms and fills it, then the window is analysed
        int n = effects.CopySpectrum(dst, out var info);
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, n);
        int expectedBand = new SpectrumAnalyzer(48000).BandOf(440f);
        Assert.True(expectedBand >= 0);
        Assert.Equal(expectedBand, ArgMax(dst));                        // the generator's 440 Hz sine is the loudest band
        Assert.True(session.SpectrumPublishes > 0);
        Assert.True(info.Live);
        Assert.False(info.Muted);
        // PRE-volume RMS of the analysed window: at a quarter volume the tap (before _masterGain) still sees the generator's
        // 0.3-amplitude sine → 0.3/√2, within 10 %. A post-volume tap would read a quarter of that.
        float expectedRms = 0.3f / MathF.Sqrt(2f);
        Assert.InRange(info.WindowRms, expectedRms * 0.9f, expectedRms * 1.1f);
        Assert.True(effects.Visualizer.Peek().Magnitudes.IsEmpty);      // O2: the level frame never carries the spectrum
    }

    /// <summary>THE PRODUCTION OPEN PATH (Wavee #166: on a real track every live-FFT face sat at rest — a flat row of identical
    /// dashes — while the `--fake` demo moved). A real track's session is built by <see cref="PcmAudioPlayer.OpenAsync"/>; every
    /// other test in this file hand-builds its session and calls <c>BindEffects</c> itself, the one step that path skipped, so the
    /// level tap and the spectrum ring never ran and <c>CopySpectrum</c> returned 0 under a live lease. Open a 440 Hz WAV through
    /// a backend that holds the effects surface, play it under a spectrum lease, and the tone must come back out of
    /// <c>CopySpectrum</c>: its band the loudest, tens of dB above the analyzer floor — and the level tap must see it too.</summary>
    [Fact]
    public async Task BackendOpenedSession_PublishesTheToneSpectrumAndLevel_UnderASpectrumLease()
    {
        var effects = new AudioEffects();
        effects.Normalization.Value = NormMode.Off;                     // an untagged WAV stays at unity (CreateSession's rationale)
        var player = new PcmAudioPlayer(new MixFormat(48000, 2), effects: effects, maxBlock: 512);   // headless endpoint, no feed: PumpAudio renders inline
        byte[] wav = M3TestSupport.MakeWavPcm16(48000, 2, M3TestSupport.ToneStereo(48000, 2.0, 440, amp: 0.3f));
        var opened = await player.OpenAsync(MediaSource.FromBytes(wav).WithKind(MediaKind.PcmAudio), new MediaOpenOptions(),
            TestContext.Current.CancellationToken);
        await using var session = Assert.IsType<PcmAudioSession>(opened);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        using var lease = effects.AcquireSpectrum();
        _ = session.PlayAsync();
        for (int i = 0; i < 16; i++) session.PumpAudio(512);            // Opening→Buffering→Playing; the tick creates the ring, the RT arms and fills it
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        Assert.True(session.SpectrumPublishes > 0, "no window was analysed: the backend-opened session is not bound to its effects surface");
        var dst = new float[SpectrumAnalyzer.DefaultBandCount];
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, effects.CopySpectrum(dst, out var info));
        Assert.True(info.Live);
        Assert.False(info.Muted);
        int toneBand = new SpectrumAnalyzer(48000).BandOf(440f);
        Assert.True(toneBand >= 0);
        Assert.Equal(toneBand, ArgMax(dst));
        // A 0.3-amplitude sine reads ≈ 20·log10(0.3) ≈ −10.5 dB before the +3 dB/oct tilt (≈ +10 dB at 440 Hz): nowhere near the −80 floor.
        Assert.True(dst[toneBand] > -20f, $"the tone's band read {dst[toneBand]} dB");
        float expectedRms = 0.3f / MathF.Sqrt(2f);
        Assert.InRange(info.WindowRms, expectedRms * 0.9f, expectedRms * 1.1f);
        Assert.True(effects.Visualizer.Peek().Rms > 0.1f, "the level tap never published for the backend-opened session");
    }

    [Fact]
    public async Task SpectrumDemandEdge_StopsTheTap_AndReArmsTheRingOnReacquire()
    {
        var effects = new AudioEffects();
        await using var session = CreateSession(effects);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        var dst = new float[SpectrumAnalyzer.DefaultBandCount];
        var lease = effects.AcquireSpectrum();
        for (int i = 0; i < 12; i++) session.PumpAudio(512);
        long published = session.SpectrumPublishes;
        Assert.True(published > 0);

        lease.Dispose();
        for (int i = 0; i < 12; i++) session.PumpAudio(512);
        Assert.Equal(published, session.SpectrumPublishes);             // no demand: no analysis…
        Assert.Equal(0, effects.CopySpectrum(dst, out var released));   // …and nothing readable
        Assert.False(released.Live);

        using var again = effects.AcquireSpectrum();
        for (int i = 0; i < 12; i++) session.PumpAudio(512);            // the first block re-arms the ring: no window straddles the gap
        Assert.True(session.SpectrumPublishes > published);
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, effects.CopySpectrum(dst, out var live));
        Assert.True(live.Live);
        Assert.Equal(new SpectrumAnalyzer(48000).BandOf(440f), ArgMax(dst));
    }

    [Fact]
    public async Task SpectrumWindow_IsCentredOnTheAudibleInstant_AndANegativeOffsetSaturatesAtTheNewestSample()
    {
        const int streamLatency = 4096;
        var effects = new AudioEffects();
        await using var session = CreateSession(effects, latencyFrames: streamLatency);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        using var lease = effects.AcquireSpectrum();
        var dst = new float[SpectrumAnalyzer.DefaultBandCount];
        for (int i = 0; i < 48; i++) session.PumpAudio(512);            // enough rendered content after the arm for the deepest offset below
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
        int halfWindow = SpectrumAnalyzer.DefaultFftSize / 2;

        // Headless clock: PlayedFrames == the newest rendered content frame after every pump, so the window ends
        // streamLatency behind it, pushed forward by half a window (centred on the audible instant). The graph's own latency is NOT
        // subtracted: the tap sits after the master EQ + limiter, so the ring is already keyed by (delayed) output frame.
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, effects.CopySpectrum(dst, out var info));
        Assert.Equal(streamLatency - halfWindow, info.AlignFrames);

        effects.SpectrumOffsetMs = 100f;                                // positive = read EARLIER: 100 ms = 4 800 frames at 48 kHz
        session.PumpAudio(512);
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, effects.CopySpectrum(dst, out info));
        Assert.Equal(streamLatency - halfWindow + 4800, info.AlignFrames);

        effects.SpectrumOffsetMs = -500f;                               // a negative offset would end past the newest sample…
        session.PumpAudio(512);
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, effects.CopySpectrum(dst, out info));
        Assert.Equal(0, info.AlignFrames);                              // …and saturates there instead of blanking the picture
    }

    [Fact]
    public async Task OutputDelayFrames_IsSubmittedMinusPlayedPlusLatency_Diagnostic()
    {
        const int streamLatency = 480;
        var effects = new AudioEffects();
        await using var session = CreateSession(effects, latencyFrames: streamLatency);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        for (int i = 0; i < 6; i++) session.PumpAudio(512);
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
        Assert.Equal(Math.Max(0L, session.SubmittedFrames - session.PlayedFrames + streamLatency), session.OutputDelayFrames);

        for (int i = 0; i < 3; i++) Assert.Equal(256, session.RenderBlock(256));   // submitted moves; played is refreshed only by the next control tick
        Assert.True(session.SubmittedFrames > session.PlayedFrames);
        Assert.Equal(Math.Max(0L, session.SubmittedFrames - session.PlayedFrames + streamLatency), session.OutputDelayFrames);
        Assert.True(session.OutputDelayFrames >= streamLatency + 3 * 256);
    }

    [Fact]
    public async Task RenderBlock_UnderASpectrumLease_AllocatesNothing()
    {
        var effects = new AudioEffects();
        await using var session = CreateSession(effects, totalFrames: 48000L * 600);   // long enough that the voice never drains mid-measurement
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        using var lease = effects.AcquireSpectrum();
        _ = session.PlayAsync();
        for (int i = 0; i < 12; i++) session.PumpAudio(512);            // FIRST: the control tick creates the ring and the RT arms it — the one-time allocations
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
        Assert.True(session.SpectrumPublishes > 0, "the ring exists and is armed, so the RT path below really writes it");

        for (int i = 0; i < 2000; i++) session.RenderBlock(256);        // warm the JIT
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++) session.RenderBlock(256);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++) if (values[i] > values[best]) best = i;
        return best;
    }

    private static PcmAudioSession CreateSession(AudioEffects effects, long latencyFrames = 0, long totalFrames = 480000)
    {
        // The voice is baked under NormMode.Off, and production opens every voice under the effects surface's own mode
        // (PcmAudioPlayer.ResolveNorm). Keep the two in agreement: AudioEffects defaults to Album, and the first control tick would
        // otherwise rebase the voice's normalization (+4 dB for an untagged source at −14 LUFS), which every post-mixer tap sees.
        effects.Normalization.Value = NormMode.Off;
        var format = new MixFormat(48000, 2);
        var endpoint = new HeadlessAudioEndpoint(format, latencyFrames: latencyFrames);
        var session = new PcmAudioSession(format, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        session.SetVoice(new SignalGeneratorSource(2, 48000, 440, 0.3f, totalFrames),
            TimeSpan.FromSeconds(totalFrames / 48000.0), totalFrames, NormMode.Off, -14f, initialVolume: 1f);
        session.BindEffects(effects);
        return session;
    }

    private sealed class MailboxHolder { public AudioLevelMailbox Mailbox; }
}
