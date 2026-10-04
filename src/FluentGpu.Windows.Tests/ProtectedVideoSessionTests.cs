using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// <see cref="ProtectedVideoSession"/> itself — create, start (attach), pump, seek, native events, teardown — over a
/// runtime built from <see cref="FakeRuntimeNative"/> + <see cref="FakeSessionNative"/>: the exact calls the session makes
/// into the DLL are recorded, the snapshot a pump reads is scripted, and native events are injected through
/// <see cref="ProtectedVideoRuntime.OnNativeEvent"/> exactly as the event thunk dispatches them. No DLL, no CDM, no GPU,
/// no network, no window.
/// </summary>
public sealed class ProtectedVideoSessionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly byte[] Pssh = { 7, 7, 7, 7 };
    private const string Kid = "0123456789abcdef0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IDisposable
    {
        public readonly FakeRuntimeNative Runtime = new();
        public readonly FakeSessionNative Sessions = new();
        public readonly string StorePath = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        public readonly ProtectedVideoRuntime Rt;

        public Rig() => Rt = new ProtectedVideoRuntime(Runtime, StorePath, idleMs: 60_000, sessionNative: Sessions);

        public ProtectedVideoSession Create(ProtectedVideoRequest request) => ProtectedVideoSession.Create(Rt, request);

        /// <summary>A native event for a session, as the thunk dispatches it.</summary>
        public void Event(ProtectedVideoSession s, int ev, long a = 0, long b = 0) => Rt.OnNativeEvent(s.Handle, ev, a, b, null);

        public void Dispose()
        {
            Rt.Dispose();
            try { Directory.Delete(StorePath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ProtectedVideoRequest Request(bool paused = true, TimeSpan start = default, string? kid = Kid, byte[]? pssh = null)
        => new()
        {
            InitUrl = "https://cdn.test/v/init.mp4",
            SegmentBaseUrl = "https://cdn.test/v/",
            SegmentPrefix = "seg_",
            SegmentSuffix = ".m4s",
            StartNumber = 0,
            SegmentStride = 4,
            SegmentLengthMs = 4_000,
            DurationMs = 200_000,
            AudioInitUrl = "https://cdn.test/a/init.mp4",
            Pssh = pssh ?? Pssh,
            DefaultKid = kid,
            StartPaused = paused,
            StartPosition = start,
            RetainBehindMs = 0,
            BufferAheadMs = 0,
            StoreBudgetBytes = 0,
            LicenseRelay = _ => ValueTask.FromResult(new LicenseResponse(new byte[] { 1 })),
        };

    private static int IndexOf(string[] calls, string prefix) => Array.FindIndex(calls, c => c.StartsWith(prefix, StringComparison.Ordinal));

    // ── create ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_TakesARuntimeReference_AndDescribesTheOpen_WithTheDefaultsTheRequestLeftUnset()
    {
        using var rig = new Rig();
        var request = Request(start: TimeSpan.FromSeconds(83.25)) with { DefaultKid = "01234567-89AB-CDEF-0123-456789ABCDEF", SegmentStride = 0 };

        using ProtectedVideoSession s = rig.Create(request);

        Assert.True(rig.Rt.IsRunning);
        Assert.Equal(1, rig.Rt.References);
        Assert.NotEqual(0ul, s.Handle);
        PrOpenDescription open = Assert.IsType<PrOpenDescription>(rig.Sessions.LastOpen);
        Assert.Equal(83_250, open.StartPositionMs);
        Assert.True(open.StartPaused);
        Assert.Equal(Kid, open.KeyIdHex);                                     // dashless, lower-case: the cache key
        Assert.Equal(1, open.SegmentStrideSeconds);                           // unstated stride = numbered segments
        Assert.Equal(4_000, open.SegmentLengthMs);
        Assert.Equal(200_000, open.DurationMs);
        Assert.Equal(ProtectedVideoSession.DefaultRetainBehindMs, open.RetainBehindMs);
        Assert.Equal(ProtectedVideoSession.DefaultBufferAheadMs, open.BufferAheadMs);
        Assert.Equal(ProtectedVideoSession.DefaultStoreBudgetBytes, open.StoreBudgetBytes);
        Assert.Equal(Pssh, open.Pssh.ToArray());
        Assert.Equal("https://cdn.test/a/init.mp4", open.AudioInitUrl);
    }

    [Fact]
    public void Create_ANegativeStartPosition_OpensAtZero()
    {
        Assert.Equal(0, ProtectedVideoSession.DescribeOpen(Request(start: TimeSpan.FromSeconds(-3)), Kid).StartPositionMs);
    }

    [Fact]
    public void Create_WhenTheComponentIsMissing_NeverCallsTheSessionNative_AndThePumpPublishesATypedError()
    {
        using var rig = new Rig();
        rig.Runtime.Available = false;

        using ProtectedVideoSession s = rig.Create(Request());
        s.Pump(default);

        Assert.Empty(rig.Sessions.Calls);
        Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
        Assert.Contains(PrNative.LibraryName, s.Error.Peek());
        Assert.Equal(ProtectedVideoPhase.Failed, s.Phase);
    }

    [Fact]
    public void Create_ARefusedNativeCreate_IsReportedByThePump_AndDisposeGivesTheRuntimeReferenceBack()
    {
        using var rig = new Rig();
        rig.Sessions.CreateHr = PrNative.EInvalidArg;

        ProtectedVideoSession s = rig.Create(Request());
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
        Assert.Contains("0x80070057", s.Error.Peek());
        Assert.Equal(1, rig.Rt.References);

        s.Dispose();
        Assert.Equal(0, rig.Rt.References);
        Assert.DoesNotContain(rig.Sessions.Calls, c => c.StartsWith("destroy:", StringComparison.Ordinal));   // no handle to destroy
    }

    // ── start ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Start_WithAKnownKidAndPssh_StartsTheLicense_AndAttachesPausedWithItsHandle()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: true));

        s.Start(s.Request);

        ulong license = rig.Rt.LicenseHandleFor(Kid);
        Assert.NotEqual(0ul, license);
        Assert.Contains($"attach:{s.Handle}:{license}", rig.Sessions.Calls);
        Assert.Equal(0, rig.Sessions.CountOf("play"));                         // opened paused: no play
        Assert.Equal(0, rig.Sessions.CountOf("seek"));                         // the start position did not move
    }

    [Fact]
    public void Start_NotPaused_PlaysRightAfterTheAttach()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));

        s.Start(s.Request);

        string[] calls = rig.Sessions.Calls;
        Assert.InRange(IndexOf(calls, "play:"), IndexOf(calls, "attach:") + 1, calls.Length - 1);
    }

    [Fact]
    public void Start_AnOpenAtANewerPosition_MovesTheStartBeforeTheAttach_NeverSeeksTheEngineAfterIt()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(start: TimeSpan.Zero));   // prepared from the start

        s.Start(Request(start: TimeSpan.FromSeconds(83)));                          // opened at the song's position

        string[] calls = rig.Sessions.Calls;
        int seek = Array.IndexOf(calls, $"seek:{s.Handle}:83000:{PrNative.SeekExact}:{PrNative.NoKeyframeHint}");
        Assert.InRange(seek, 0, IndexOf(calls, "attach:") - 1);
        Assert.Equal(1, rig.Sessions.CountOf("seek"));
    }

    [Fact]
    public async Task Start_WithNoKid_PrefetchesOneSegment_ReadsTheInitsProtection_ThenLicensesByItAndAttaches()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(kid: null, pssh: Array.Empty<byte>()));
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.PumpRequested += () => { if (rig.Sessions.CountOf("attach") > 0) attached.TrySetResult(); };
        rig.Sessions.InitPssh = new byte[] { 9, 9, 9 };
        rig.Sessions.InitKid = Kid;

        s.Start(s.Request);
        Assert.Contains($"prefetch:{s.Handle}:0:1", rig.Sessions.Calls);
        Assert.Equal(0, rig.Sessions.CountOf("attach"));                       // nothing to license by yet

        rig.Event(s, PrNative.EvBuffered, 4_000);                              // the init + first segment landed
        await attached.Task.WaitAsync(Bound, Ct);

        ulong license = rig.Rt.LicenseHandleFor(Kid);
        Assert.NotEqual(0ul, license);
        Assert.Contains($"attach:{s.Handle}:{license}", rig.Sessions.Calls);
        Assert.Contains(rig.Runtime.Acquired, a => a.Kid == Kid);
    }

    [Fact]
    public async Task Start_WithNoKid_WhoseInitNeverArrives_DoesNotAttach_AndThePumpReportsTheError()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(kid: null, pssh: Array.Empty<byte>()));
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pumps = 0;
        s.PumpRequested += () => { if (Interlocked.Increment(ref pumps) >= 3) abandoned.TrySetResult(); };

        s.Start(s.Request);                                                    // pump 1
        rig.Sessions.Snapshot.State = PrNative.StateError;
        rig.Sessions.Snapshot.ErrorHr = unchecked((int)0x80070002);
        rig.Event(s, PrNative.EvError, 2, unchecked((int)0x80070002));         // pump 2, and the prefetch wait ends false
        await abandoned.Task.WaitAsync(Bound, Ct);                             // pump 3: the deferred start gave up

        Assert.Equal(0, rig.Sessions.CountOf("attach"));
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
        Assert.Contains("0x80070002", s.Error.Peek());
    }

    [Fact]
    public void Start_ARefusedAttach_IsReportedByThePump()
    {
        using var rig = new Rig();
        rig.Sessions.AttachHr = unchecked((int)0xC00D36B2);
        using ProtectedVideoSession s = rig.Create(Request());

        s.Start(s.Request);
        s.Pump(default);

        Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
        Assert.Contains("0xC00D36B2", s.Error.Peek());
    }

    // ── prefetch ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prefetch_AStoreThatAnswersInsideTheNativeCall_IsNotMissed()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(start: TimeSpan.FromSeconds(12)));
        rig.Sessions.DuringPrefetch = h => rig.Rt.OnNativeEvent(h, PrNative.EvBuffered, 8_000, 0, null);   // already in the store

        bool landed = await s.PrefetchCoreAsync(2, Ct).WaitAsync(Bound, Ct);

        Assert.True(landed);
        Assert.Contains($"prefetch:{s.Handle}:12000:2", rig.Sessions.Calls);
        Assert.Equal(8_000, s.ForwardBufferedMs);                              // a prepared session is never pumped
        Assert.Equal(0, rig.Rt.PendingBufferedWaits);
    }

    [Fact]
    public async Task Prefetch_ARefusedNativeCall_CompletesFalseAtOnce()
    {
        using var rig = new Rig();
        rig.Sessions.PrefetchHr = PrNative.EHandle;
        using ProtectedVideoSession s = rig.Create(Request());

        Assert.False(await s.PrefetchCoreAsync(2, Ct).WaitAsync(Bound, Ct));
        Assert.Equal(0, rig.Rt.PendingBufferedWaits);
    }

    // ── pump ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pump_MapsTheSnapshot_AndBindsTheSwapChainHandleOnlyWhileAttached()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));
        var registry = new VideoSurfaceRegistry();
        var binding = new VideoBinding(registry, registry.Acquire());
        s.Start(s.Request);
        rig.Event(s, PrNative.EvMetadata, 200_000, (1920L << 32) | 1080);
        rig.Event(s, PrNative.EvFirstFrame, 83_000);
        rig.Rt.OnNativeEvent(rig.Rt.LicenseHandleFor(Kid), PrNative.EvLicenseUsable, 120, 0, Kid);
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StatePlaying, ReadyState = 4, Handle = 0xBEEF, Width = 1920, Height = 1080,
            PositionMs = 83_500, PositionQpc = 777, DurationMs = 200_000, BufferedAheadMs = 12_000, RetainedBehindMs = 3_000,
            FirstFrameQpc = 555,
        };

        s.Pump(binding);

        Assert.Equal(ProtectedVideoState.Playing, s.State.Peek());
        Assert.Equal(ProtectedVideoPhase.Playing, s.Phase);
        Assert.Equal(83_500, s.PositionMs.Peek());
        Assert.Equal(777, s.PositionQpc);
        Assert.Equal(200_000, s.DurationMs.Peek());
        Assert.Equal(new Size2(1920, 1080), s.NaturalSize.Peek());
        Assert.Equal(12_000, s.ForwardBufferedMs);
        Assert.True(s.HasSurface);
        VideoEngineSnapshot seam = s.ReadSnapshot();
        Assert.Equal(555, seam.FirstFrameTimestamp);
        Assert.True((seam.Flags & VideoEngineFlags.MetadataLoaded) != 0);
        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1f);
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);

        s.Stop();
        s.Pump(new VideoBinding(registry, registry.Acquire()));
        Assert.False(s.HasSurface);
        Assert.Contains($"detach:{s.Handle}", rig.Sessions.Calls);
    }

    [Fact]
    public void Pump_WithNoBinding_PublishesTheSurfaceFacts_AndBindHandsTheKnownHandleToALaterElement()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));
        var registry = new VideoSurfaceRegistry();
        s.Start(s.Request);
        rig.Event(s, PrNative.EvMetadata, 200_000, (1920L << 32) | 1080);
        rig.Event(s, PrNative.EvFirstFrame, 83_000);
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StatePlaying, ReadyState = 4, Handle = 0xBEEF, Width = 1920, Height = 1080,
            PositionMs = 83_500, PositionQpc = 777, DurationMs = 200_000, BufferedAheadMs = 12_000, FirstFrameQpc = 555,
        };

        s.Pump();                                                              // no element mounted anywhere: the state half alone

        Assert.Equal(ProtectedVideoState.Playing, s.State.Peek());
        Assert.Equal(83_500, s.PositionMs.Peek());
        Assert.Equal(new Size2(1920, 1080), s.NaturalSize.Peek());
        Assert.True(s.HasSurface);
        s.Bind(default);                                                       // an inert binding is never touched
        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1f);
        Assert.Equal((nuint)0, presenter.LastBoundHandle);

        // An element arrives later (a hand-off, a remount): it is handed the handle the state pump already saw.
        var binding = new VideoBinding(registry, registry.Acquire());
        s.Bind(binding);
        registry.Drain(presenter, scale: 1f);
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);

        // A native detach drops the handle: the next element gets nothing to bind.
        s.Stop();
        var later = new VideoBinding(registry, registry.Acquire());
        presenter.Calls.Clear();
        s.Bind(later);
        registry.Drain(presenter, scale: 1f);
        Assert.DoesNotContain(presenter.Calls, c => c.StartsWith("Bind(", StringComparison.Ordinal));
    }

    [Fact]
    public void Pump_APumpWithNoElement_StillPublishesAFailure()
    {
        using var rig = new Rig();
        rig.Sessions.CreateHr = PrNative.EInvalidArg;
        using ProtectedVideoSession s = rig.Create(Request());

        s.Pump();                                                              // never throws, never needs a binding

        Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
        Assert.Contains("0x80070057", s.Error.Peek());
    }

    [Fact]
    public void Pump_APlayingSourceWithNothingAheadAfterItsFirstFrame_IsRebuffering()
    {
        Assert.Equal(ProtectedVideoState.Buffering,
            ProtectedVideoSession.MapNativeState(PrNative.StatePlaying, attached: true, firstFrame: true, licenseUsable: true, bufferedAheadMs: 0, readyState: 2));
        Assert.Equal(ProtectedVideoState.Playing,
            ProtectedVideoSession.MapNativeState(PrNative.StatePlaying, attached: true, firstFrame: false, licenseUsable: true, bufferedAheadMs: 0, readyState: 2));
        Assert.Equal(ProtectedVideoState.Licensed,
            ProtectedVideoSession.MapNativeState(PrNative.StateLoading, attached: true, firstFrame: false, licenseUsable: true, bufferedAheadMs: 0, readyState: 0));
        Assert.Equal(ProtectedVideoState.Idle,
            ProtectedVideoSession.MapNativeState(PrNative.StateIdle, attached: false, firstFrame: false, licenseUsable: false, bufferedAheadMs: 0, readyState: 0));
    }

    [Fact]
    public void MapNativeState_APlayingSourceTheEngineIsWaitingFor_IsBufferingWhateverTheStoreSays()
    {
        // F030: WAITING / STALLED stop the clock with data still buffered and readyState 4 (a key wait, a decoder stall).
        Assert.Equal(ProtectedVideoState.Buffering,
            ProtectedVideoSession.MapNativeState(PrNative.StatePlaying, attached: true, firstFrame: true, licenseUsable: true, bufferedAheadMs: 12_000, readyState: 4, waiting: true));
        Assert.Equal(ProtectedVideoState.Playing,
            ProtectedVideoSession.MapNativeState(PrNative.StatePlaying, attached: true, firstFrame: true, licenseUsable: true, bufferedAheadMs: 12_000, readyState: 4, waiting: false));
        // Waiting only matters for a source that is playing with a frame up: a paused one stays paused, a loading one loading.
        Assert.Equal(ProtectedVideoState.Paused,
            ProtectedVideoSession.MapNativeState(PrNative.StatePaused, attached: true, firstFrame: true, licenseUsable: true, bufferedAheadMs: 12_000, readyState: 4, waiting: true));
        Assert.Equal(ProtectedVideoState.Playing,
            ProtectedVideoSession.MapNativeState(PrNative.StatePlaying, attached: true, firstFrame: false, licenseUsable: true, bufferedAheadMs: 12_000, readyState: 4, waiting: true));
    }

    [Fact]
    public void Pump_AnEngineWaitingEvent_ReadsAsBufferingUntilItResumes()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));
        int pumps = 0;
        s.PumpRequested += () => Interlocked.Increment(ref pumps);
        s.Start(s.Request);
        rig.Event(s, PrNative.EvFirstFrame, 0);
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StatePlaying, ReadyState = 4, Handle = 0xBEEF, Width = 1280, Height = 720,
            PositionMs = 9_000, DurationMs = 60_000, BufferedAheadMs = 20_000,
        };
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Playing, s.State.Peek());

        int before = Volatile.Read(ref pumps);
        rig.Event(s, PrNative.EvWaiting, 9_000);
        Assert.True(Volatile.Read(ref pumps) > before);                        // the event asks for a pump
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Buffering, s.State.Peek());           // readyState 4 and 20 s buffered, and still buffering

        rig.Event(s, PrNative.EvResumed, 9_400);
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Playing, s.State.Peek());
    }

    [Fact]
    public void Pump_AWaitFromAnEarlierAttach_DoesNotBufferTheNextOne()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));
        s.Start(s.Request);
        rig.Event(s, PrNative.EvFirstFrame, 0);
        rig.Event(s, PrNative.EvWaiting, 9_000);
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StatePlaying, ReadyState = 4, Handle = 0xBEEF, Width = 1280, Height = 720,
            PositionMs = 9_000, DurationMs = 60_000, BufferedAheadMs = 20_000,
        };
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Buffering, s.State.Peek());

        // Native clears its wait at detach and attach without an event; the managed flag is scoped to the attach.
        s.Stop();
        s.Start(s.Request);
        rig.Event(s, PrNative.EvFirstFrame, 9_000);
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Playing, s.State.Peek());
    }

    [Fact]
    public void Pump_AFeedStallWithNothingBuffered_IsBuffering_AndWithDataAheadIsNot()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));
        s.Start(s.Request);
        rig.Event(s, PrNative.EvFirstFrame, 0);
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StatePlaying, ReadyState = 4, Handle = 0xBEEF, Width = 1280, Height = 720,
            PositionMs = 9_000, DurationMs = 60_000, BufferedAheadMs = 12_000,
        };

        rig.Event(s, PrNative.EvFeedStalled, 5, 503);                          // seg#5 is failing, 12 s are still buffered
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Playing, s.State.Peek());             // the stall is invisible until the buffer drains

        rig.Sessions.Snapshot.BufferedAheadMs = 0;                             // it drained, with readyState still reading 4
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Buffering, s.State.Peek());

        rig.Event(s, PrNative.EvFeedRecovered);                                // a retry landed
        s.Pump(default);
        Assert.Equal(ProtectedVideoState.Playing, s.State.Peek());
    }

    [Fact]
    public void Pump_ALicenseFailure_PublishesTheRelaysOwnWords()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request());
        s.Start(s.Request);
        ulong license = rig.Rt.LicenseHandleFor(Kid);
        rig.Rt.OnNativeEvent(license, PrNative.EvLicenseFailed, unchecked((int)0x8004C600), 0, Kid);
        rig.Sessions.Snapshot.State = PrNative.StateLoading;

        s.Pump(default);

        Assert.Equal(ProtectedVideoState.Error, s.State.Peek());
        Assert.Equal(ProtectedVideoPhase.Failed, s.Phase);
        Assert.Contains("0x8004C600", s.Error.Peek());
    }

    [Fact]
    public void Pump_InSteadyState_AllocatesNothing()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));
        s.Start(s.Request);
        rig.Event(s, PrNative.EvFirstFrame, 0);
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StatePlaying, ReadyState = 4, Handle = 0xBEEF, Width = 1280, Height = 720,
            PositionMs = 750, DurationMs = 60_000, BufferedAheadMs = 20_000,
        };
        s.Pump(default);                                                       // warm: every branch the loop takes, once
        rig.Sessions.Snapshot.PositionMs = 1_000;
        s.Pump(default);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
        {
            rig.Sessions.Snapshot.PositionMs = 1_000 + i * 250;   // the clock moves; the value gates the signal write
            s.Pump(default);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    // ── events ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstFrame_BumpsTheEpochOncePerAttach()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request());
        s.Start(s.Request);

        rig.Event(s, PrNative.EvFirstFrame, 0);
        rig.Event(s, PrNative.EvFirstFrame, 0);
        Assert.Equal(1, s.FirstFrameEpoch);

        s.Stop();
        s.Start(s.Request);                                                    // back to the video: a re-attach
        rig.Event(s, PrNative.EvFirstFrame, 0);
        Assert.Equal(2, s.FirstFrameEpoch);
        Assert.Equal(2, rig.Sessions.CountOf("attach"));
    }

    [Fact]
    public void HasFirstFrame_IsTrueOnlyBetweenThisAttachsFirstFrameAndItsStop()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request());
        Assert.False(s.HasFirstFrame);

        s.Start(s.Request);
        Assert.False(s.HasFirstFrame);                                         // attached, nothing decoded yet

        rig.Event(s, PrNative.EvFirstFrame, 0);
        Assert.True(s.HasFirstFrame);

        s.Stop();
        Assert.False(s.HasFirstFrame);                                         // FirstFrameEpoch stays 1; this does not

        s.Start(s.Request);
        Assert.Equal(1, s.FirstFrameEpoch);
        Assert.False(s.HasFirstFrame);                                         // the swap chain holds the OLD picture until ...

        rig.Event(s, PrNative.EvFirstFrame, 0);
        Assert.True(s.HasFirstFrame);                                          // ... this attach's own first frame
    }

    [Fact]
    public void Pump_ANativeDetach_DropsTheSurfaceEvenWhileTheSessionThinksItIsAttached()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));
        s.Start(s.Request);
        rig.Event(s, PrNative.EvFirstFrame, 0);
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StatePlaying, ReadyState = 4, Handle = 0xBEEF, Width = 1280, Height = 720, DurationMs = 60_000,
        };
        s.Pump(default);
        Assert.True(s.HasSurface);

        // Another session's attach replaced this one on the shared engine: native zeroed the handle and raised Detached.
        // The managed side never called Stop, so only the snapshot tells it.
        rig.Sessions.Snapshot.Handle = 0;
        rig.Event(s, PrNative.EvDetached);
        s.Pump(default);
        Assert.False(s.HasSurface);

        // A re-attach publishes a fresh handle: the surface comes back with it, no matter when the late Detached landed.
        rig.Sessions.Snapshot.Handle = 0xCAFE;
        s.Pump(default);
        Assert.True(s.HasSurface);
    }

    [Fact]
    public void Pump_ANaturalSizeChange_ReassertsTheStreamSize_SoALargerRungIsNotPinnedToTheOpeningOne()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));
        int pumps = 0;
        s.PumpRequested += () => Interlocked.Increment(ref pumps);
        s.Start(s.Request);
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StatePlaying, ReadyState = 4, Handle = 0xBEEF, Width = 1280, Height = 720, DurationMs = 60_000,
        };
        s.Pump(default);
        s.SetStreamSize(new SizeI(1280, 720));
        s.SetStreamSize(new SizeI(1280, 720));
        Assert.Equal(1, rig.Sessions.CountOf("size"));                         // value-gated: one native call per real change

        // FORMATCHANGE after an ABR upgrade: native stores the new natural size in the snapshot and raises SizeChanged.
        int before = Volatile.Read(ref pumps);
        rig.Sessions.Snapshot.Width = 1920;
        rig.Sessions.Snapshot.Height = 1080;
        rig.Event(s, PrNative.EvSizeChanged, 1920, 1080);
        Assert.True(Volatile.Read(ref pumps) > before);                        // the event asks for a pump
        s.Pump(default);

        Assert.Equal(new Size2(1920, 1080), s.NaturalSize.Peek());
        s.SetStreamSize(new SizeI(1280, 720));                                 // the same derived size still reaches native once
        Assert.Equal(2, rig.Sessions.CountOf("size"));
        s.SetStreamSize(new SizeI(1920, 1080));                                // and a larger one is raised
        Assert.Equal(3, rig.Sessions.CountOf("size"));
        Assert.Contains($"size:{s.Handle}:1920x1080", rig.Sessions.Calls);
    }

    [Fact]
    public void Pump_TheSnapshotsAppliedStreamSize_IsTheEchoTheOwnerWaitsFor()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(paused: false));
        s.Start(s.Request);
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StatePlaying, ReadyState = 4, Handle = 0xBEEF, Width = 1280, Height = 720, DurationMs = 60_000,
        };
        s.Pump(default);
        Assert.True(s.AppliedStreamSize.IsEmpty);                      // native has applied nothing yet
        Assert.Equal(0u, s.ReadSnapshot().StreamW);

        rig.Sessions.Snapshot.StreamWidth = 640;
        rig.Sessions.Snapshot.StreamHeight = 360;
        s.Pump(default);
        Assert.Equal(new SizeI(640, 360), s.AppliedStreamSize);
        Assert.Equal(640u, s.ReadSnapshot().StreamW);
        Assert.Equal(360u, s.ReadSnapshot().StreamH);

        rig.Sessions.Snapshot.StreamWidth = 0;                         // detached: native zeroes the echo with the handle
        rig.Sessions.Snapshot.StreamHeight = 0;
        s.Pump(default);
        Assert.True(s.AppliedStreamSize.IsEmpty);
    }

    [Fact]
    public void Seek_IsInFlightUntilTheSeekedEvent_WhichCarriesTheLandedPosition()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request());
        s.Start(s.Request);

        ValueTask seek = s.SeekAsync(45_000, SeekMode.Keyframe, keyframeMs: 44_000);

        Assert.True(seek.IsCompletedSuccessfully);                             // no ack wait
        Assert.True(s.IsSeeking);
        Assert.Equal(-1, s.LastSeekLandedMs);
        Assert.Contains($"seek:{s.Handle}:45000:{PrNative.SeekKeyframe}:44000", rig.Sessions.Calls);

        rig.Event(s, PrNative.EvSeeked, 44_000, 90);

        Assert.False(s.IsSeeking);
        Assert.Equal(44_000, s.LastSeekLandedMs);
    }

    [Fact]
    public void ALicenseEventForAnotherKey_IsNotThisSessions()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request());
        s.Start(s.Request);
        rig.Rt.EnsureLicense(Pssh, "ffffffffffffffffffffffffffffffff", null);
        ulong other = rig.Rt.LicenseHandleFor("ffffffffffffffffffffffffffffffff");

        rig.Rt.OnNativeEvent(other, PrNative.EvLicenseFailed, unchecked((int)0x80004005), 0, "ffffffffffffffffffffffffffffffff");
        rig.Sessions.Snapshot.State = PrNative.StateLoading;
        s.Pump(default);

        Assert.Null(s.Error.Peek());
        Assert.Equal(ProtectedVideoPhase.Licensing, s.Phase);                  // its own key is still pending
    }

    // ── teardown ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DetachesViaStop_DestroysTheNativeSession_UnpinsTheKey_AndReleasesTheRuntime()
    {
        using var rig = new Rig();
        ProtectedVideoSession s = rig.Create(Request());
        s.Start(s.Request);
        ulong handle = s.Handle;

        s.Stop();
        s.Dispose();
        s.Dispose();                                                           // idempotent

        string[] calls = rig.Sessions.Calls;
        Assert.InRange(Array.IndexOf(calls, $"destroy:{handle}"), Array.IndexOf(calls, $"detach:{handle}") + 1, calls.Length - 1);
        Assert.Equal(1, rig.Sessions.CountOf("destroy"));
        Assert.Equal(0, rig.Rt.References);
        s.Pump(default);                                                       // a disposed session's pump is inert
        Assert.Null(s.Error.Peek());
    }
}
