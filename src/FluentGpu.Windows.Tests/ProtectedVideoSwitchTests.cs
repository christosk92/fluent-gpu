using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// <see cref="ProtectedVideoSession"/>'s half of a buffer-preserving representation switch, over the recording native
/// fakes: the retain window reaches the DLL call (negative = append after the buffered end), the snapshot's two
/// representation indices become two separate ids (the one ON SCREEN and the one being DOWNLOADED), and both representation
/// events request a pump. No DLL, no CDM, no GPU, no network.
/// </summary>
public sealed class ProtectedVideoSwitchTests
{
    private const string Kid = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Pssh = { 7, 7, 7, 7 };

    private sealed class Rig : IDisposable
    {
        public readonly FakeRuntimeNative Runtime = new();
        public readonly FakeSessionNative Sessions = new();
        public readonly string StorePath = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        public readonly ProtectedVideoRuntime Rt;

        public Rig() => Rt = new ProtectedVideoRuntime(Runtime, StorePath, idleMs: 60_000, sessionNative: Sessions);

        public ProtectedVideoSession Create(ProtectedVideoRequest request) => ProtectedVideoSession.Create(Rt, request);

        public void Event(ProtectedVideoSession s, int ev, long a = 0, long b = 0) => Rt.OnNativeEvent(s.Handle, ev, a, b, null);

        public void Dispose()
        {
            Rt.Dispose();
            try { Directory.Delete(StorePath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ProtectedRepresentationDescriptor Rep(string id, int height) => new()
    {
        Id = id,
        Quality = new QualityVariant(id, height * 2_000, new SizeI(height * 16 / 9, height), 30,
            new MediaContentType(Container.Mp4, CodecId.H264, CodecId.None)),
        InitUrl = "https://cdn.test/" + id + "/init.mp4",
        SegmentBaseUrl = "https://cdn.test/" + id + "/",
        SegmentPrefix = "seg_",
        SegmentSuffix = ".m4s",
        SegmentCount = 10,
    };

    private static ProtectedVideoRequest Request()
    {
        ProtectedRepresentationDescriptor low = Rep("r0", 480), high = Rep("r1", 1080);
        return new ProtectedVideoRequest
        {
            InitUrl = low.InitUrl,
            SegmentBaseUrl = low.SegmentBaseUrl,
            SegmentPrefix = "seg_",
            SegmentSuffix = ".m4s",
            StartNumber = 0,
            SegmentStride = 4,
            SegmentLengthMs = 4_000,
            DurationMs = 200_000,
            Pssh = Pssh,
            DefaultKid = Kid,
            StartPaused = true,
            Catalog = new ProtectedAdaptiveCatalog
            {
                Tracks = [new ProtectedTrackDescriptor
                {
                    Id = 1, Kind = FluentGpu.Media.TrackKind.Video, Label = "Video", IsDefault = true,
                    Representations = [low, high],
                }],
            },
            LicenseRelay = _ => ValueTask.FromResult(new LicenseResponse(new byte[] { 1 })),
        };
    }

    [Fact]
    public async Task SelectVideoRepresentation_PassesTheRetainWindowToNative_AndAppendsByDefault()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request());

        await s.SelectVideoRepresentationAsync("r1");
        Assert.Equal(-1, rig.Sessions.LastRetainMs);                     // append after the last buffered segment

        await s.SelectVideoRepresentationAsync("r1", retainMs: 0);       // a manual pin: right after the playhead
        Assert.Equal(0, rig.Sessions.LastRetainMs);

        await s.SelectVideoRepresentationAsync("r1", retainMs: 25_000);  // an upswitch that should show soon
        Assert.Equal(25_000, rig.Sessions.LastRetainMs);

        await s.SelectVideoRepresentationAsync("r1", retainMs: -7);      // any negative is append
        Assert.Equal(-1, rig.Sessions.LastRetainMs);
        Assert.Equal(4, rig.Sessions.CountOf("rep"));
    }

    [Fact]
    public void Pump_PublishesTheOnScreenAndTheDownloadingRepresentationAsTwoIds()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request());
        s.Start(s.Request);

        // Nothing has switched yet: native reports -1 for both, and neither id is invented.
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StateLoading, ActiveRepresentation = -1, DownloadingRepresentation = -1,
        };
        s.Pump(default);
        Assert.Null(s.ActiveVideoRepresentationId);
        Assert.Null(s.DownloadingVideoRepresentationId);

        // The splice landed (r1 is downloading) but delivery has not reached it: r0 is still on screen.
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StateLoading, ActiveRepresentation = 0, DownloadingRepresentation = 1,
        };
        s.Pump(default);
        Assert.Equal("r0", s.ActiveVideoRepresentationId);
        Assert.Equal("r1", s.DownloadingVideoRepresentationId);

        // Delivery crossed into r1: the picture changes, and the two ids agree again.
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StateLoading, ActiveRepresentation = 1, DownloadingRepresentation = 1,
        };
        s.Pump(default);
        Assert.Equal("r1", s.ActiveVideoRepresentationId);
        Assert.Equal("r1", s.DownloadingVideoRepresentationId);
    }

    [Fact]
    public void Pump_CrossingBackIntoTheOpeningRepresentation_PublishesItAsOnScreenAgain()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request());   // opens on r0: native never learns that index
        s.Start(s.Request);

        // r1 spliced in behind the buffer, not on screen yet: -1 is still just "the opening representation".
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StateLoading, ActiveRepresentation = -1, DownloadingRepresentation = 1,
        };
        s.Pump(default);
        Assert.Null(s.ActiveVideoRepresentationId);

        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StateLoading, ActiveRepresentation = 1, DownloadingRepresentation = 1,
        };
        s.Pump(default);
        Assert.Equal("r1", s.ActiveVideoRepresentationId);

        // A seek behind the splice: delivery re-enters the opening representation, which native reports as -1 again.
        rig.Sessions.Snapshot = new PrNative.Snapshot
        {
            State = PrNative.StateLoading, ActiveRepresentation = -1, DownloadingRepresentation = 1,
        };
        s.Pump(default);
        Assert.Equal("r0", s.ActiveVideoRepresentationId);
        Assert.Equal("r1", s.DownloadingVideoRepresentationId);
    }

    [Fact]
    public void BothRepresentationEvents_RequestAPump()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request());
        s.Start(s.Request);
        int pumps = 0;
        s.PumpRequested += () => Interlocked.Increment(ref pumps);

        rig.Event(s, PrNative.EvRepresentationQueued, 1);
        Assert.Equal(1, Volatile.Read(ref pumps));

        rig.Event(s, PrNative.EvRepresentation, 1);
        Assert.Equal(2, Volatile.Read(ref pumps));
    }
}
