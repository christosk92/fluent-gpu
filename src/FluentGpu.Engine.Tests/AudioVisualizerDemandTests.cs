using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Demand lifetime, source epochs, and the real allocation-free audio/control level handoff.</summary>
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

    private static PcmAudioSession CreateSession(AudioEffects effects)
    {
        var format = new MixFormat(48000, 2);
        var endpoint = new HeadlessAudioEndpoint(format);
        var session = new PcmAudioSession(format, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        session.SetVoice(new SignalGeneratorSource(2, 48000, 440, 0.3f, 480000),
            TimeSpan.FromSeconds(10), 480000, NormMode.Off, -14f, initialVolume: 1f);
        session.BindEffects(effects);
        return session;
    }

    private sealed class MailboxHolder { public AudioLevelMailbox Mailbox; }
}
