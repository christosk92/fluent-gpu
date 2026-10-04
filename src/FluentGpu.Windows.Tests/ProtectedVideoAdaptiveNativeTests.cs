using System;
using System.IO;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The managed half of three native-side improvements, over the recording fakes (no DLL, no CDM, no GPU, no network):
/// F040 - the segment-store budget is derived from the selected representation's bandwidth and re-derived at every
/// switch (and agrees to the byte with the native twin, <c>fgpr::StoreBudgetForBitrate</c>, pinned in FeedTests.cpp);
/// F036 - the rungs next to the opening one have their init segments prefetched, once; F022 - the PlayReady security
/// policy reaches the native runtime before its create (probe SL3000 by default, SL2000 when forced).
/// </summary>
public sealed class ProtectedVideoAdaptiveNativeTests
{
    private const string Kid = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Pssh = { 7, 7, 7, 7 };
    private const long MiB = 1024 * 1024;

    private sealed class Rig : IDisposable
    {
        public readonly FakeRuntimeNative Runtime = new();
        public readonly FakeSessionNative Sessions = new();
        public readonly string StorePath = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        public readonly ProtectedVideoRuntime Rt;

        public Rig() => Rt = new ProtectedVideoRuntime(Runtime, StorePath, idleMs: 60_000, sessionNative: Sessions);

        public ProtectedVideoSession Create(ProtectedVideoRequest request) => ProtectedVideoSession.Create(Rt, request);

        public void Dispose()
        {
            Rt.Dispose();
            try { Directory.Delete(StorePath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ProtectedRepresentationDescriptor Rep(string id, int bitrate) => new()
    {
        Id = id,
        Quality = new QualityVariant(id, bitrate, new SizeI(1280, 720), 30,
            new MediaContentType(Container.Mp4, CodecId.H264, CodecId.None)),
        InitUrl = "https://cdn.test/" + id + "/init.mp4",
        SegmentBaseUrl = "https://cdn.test/" + id + "/",
        SegmentPrefix = "seg_",
        SegmentSuffix = ".m4s",
        SegmentCount = 10,
    };

    private static ProtectedVideoRequest Request(params ProtectedRepresentationDescriptor[] reps) => new()
    {
        InitUrl = reps[0].InitUrl,
        SegmentBaseUrl = reps[0].SegmentBaseUrl,
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
                Representations = reps,
            }],
        },
        LicenseRelay = _ => ValueTask.FromResult(new LicenseResponse(new byte[] { 1 })),
    };

    // ── F040 ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StoreBudgetFor_FollowsTheBitrate_ClampedToTheDerivedRange_AndAgreesWithTheNativeTwin()
    {
        // The same numbers fgpr::StoreBudgetForBitrate is pinned to (FeedTests.cpp Q57).
        Assert.Equal(ProtectedVideoSession.DefaultStoreBudgetBytes, ProtectedVideoSession.StoreBudgetFor(0, 60_000, 30_000));   // unknown bitrate
        Assert.Equal(16 * MiB, ProtectedVideoSession.StoreBudgetFor(500_000, 60_000, 30_000));                                  // the floor
        Assert.Equal(90_000_000, ProtectedVideoSession.StoreBudgetFor(5_000_000, 60_000, 30_000));                              // 90 s x 625 kB/s x 1.6
        Assert.Equal(128 * MiB, ProtectedVideoSession.StoreBudgetFor(20_000_000, 60_000, 30_000));                              // the ceiling
        Assert.Equal(90_000_000, ProtectedVideoSession.StoreBudgetFor(5_000_000, 0, 0));                                        // zero window = the defaults
        Assert.True(ProtectedVideoSession.StoreBudgetFor(2_000_000, 60_000, 30_000) < ProtectedVideoSession.StoreBudgetFor(4_000_000, 60_000, 30_000));
        // The video slice (3/4 of the budget) holds the whole 90 s window at the declared rate plus the 20 % headroom.
        Assert.True(ProtectedVideoSession.StoreBudgetFor(5_000_000, 60_000, 30_000) / 4 * 3 >= 67_500_000);
    }

    [Fact]
    public void DescribeOpen_DerivesTheBudgetFromTheOpeningRung_AndKeepsAnExplicitOne()
    {
        ProtectedRepresentationDescriptor low = Rep("r0", 960_000), high = Rep("r1", 2_160_000);

        // Opens on r0 (960 kbps): 90 s x 120 kB/s x 1.6 = 17.28 MB (just over the 16 MiB floor).
        Assert.Equal(17_280_000, ProtectedVideoSession.DescribeOpen(Request(low, high), Kid).StoreBudgetBytes);
        // Opens on r1 (2.16 Mbps): 38.88 MB.
        Assert.Equal(38_880_000, ProtectedVideoSession.DescribeOpen(Request(high, low), Kid).StoreBudgetBytes);
        // An explicit request budget is the caller's.
        Assert.Equal(64 * MiB, ProtectedVideoSession.DescribeOpen(Request(low, high) with { StoreBudgetBytes = 64 * MiB }, Kid).StoreBudgetBytes);
        // No catalog = no bitrate: the 32 MiB default.
        Assert.Equal(ProtectedVideoSession.DefaultStoreBudgetBytes,
            ProtectedVideoSession.DescribeOpen(Request(low, high) with { Catalog = null }, Kid).StoreBudgetBytes);
    }

    [Fact]
    public async Task SelectVideoRepresentation_ReDerivesTheBudgetForTheNewRung_UnlessTheRequestPinnedOne()
    {
        ProtectedRepresentationDescriptor low = Rep("r0", 960_000), high = Rep("r1", 2_160_000);

        using (var rig = new Rig())
        {
            using ProtectedVideoSession s = rig.Create(Request(low, high));
            await s.SelectVideoRepresentationAsync("r1");
            Assert.Equal(38_880_000, rig.Sessions.LastStoreBudgetBytes);
            await s.SelectVideoRepresentationAsync("r0");
            Assert.Equal(17_280_000, rig.Sessions.LastStoreBudgetBytes);
        }
        using (var rig = new Rig())
        {
            using ProtectedVideoSession s = rig.Create(Request(low, high) with { StoreBudgetBytes = 48 * MiB });
            await s.SelectVideoRepresentationAsync("r1");
            Assert.Equal(0, rig.Sessions.LastStoreBudgetBytes);   // 0 = native leaves the pinned budget alone
        }
    }

    // ── F036 ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NeighbourRungs_AreTheNearestLowerAndHigherByBandwidth()
    {
        ProtectedRepresentationDescriptor[] ladder =
        [
            Rep("a", 3_000_000), Rep("b", 500_000), Rep("c", 1_200_000), Rep("d", 6_000_000),
        ];
        Assert.Equal((1, 0), ProtectedVideoSession.NeighbourRungs(ladder, 2));    // 1.2 Mbps: 0.5 below, 3 above
        Assert.Equal((-1, 2), ProtectedVideoSession.NeighbourRungs(ladder, 1));   // the bottom rung has no lower
        Assert.Equal((0, -1), ProtectedVideoSession.NeighbourRungs(ladder, 3));   // the top rung has no higher
        Assert.Equal((-1, -1), ProtectedVideoSession.NeighbourRungs(ladder, 9));  // not a rung
        Assert.Equal((-1, -1), ProtectedVideoSession.NeighbourRungs([Rep("only", 1_000_000)], 0));
    }

    [Fact]
    public async Task Start_PrefetchesTheNeighbourInitsOnce_AndASwitchAsksForNothingAlreadyKnown()
    {
        ProtectedRepresentationDescriptor low = Rep("r0", 960_000), high = Rep("r1", 2_160_000);
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(low, high));

        s.Start(s.Request);
        Assert.Equal(new[] { high.InitUrl }, rig.Sessions.PrefetchedInits.ToArray());   // r0 is the opening rung: only r1 is a neighbour

        s.Start(s.Request);                                                              // a re-attach asks nothing more
        Assert.Single(rig.Sessions.PrefetchedInits);

        await s.SelectVideoRepresentationAsync("r1");                                    // r1's neighbour is r0, whose init native already holds
        Assert.Single(rig.Sessions.PrefetchedInits);
    }

    [Fact]
    public void Start_OfASingleRungSource_PrefetchesNothing()
    {
        using var rig = new Rig();
        using ProtectedVideoSession s = rig.Create(Request(Rep("only", 1_000_000)));
        s.Start(s.Request);
        Assert.Empty(rig.Sessions.PrefetchedInits);
    }

    // ── F022 ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChooseSecurityPolicy_ProbesSl3000ByDefault_AndForcesSl2000OnlyWhenAsked()
    {
        Assert.Equal(PrNative.SecurityProbe3000, ProtectedVideoRuntime.ChooseSecurityPolicy(forceSl2000: false));
        Assert.Equal(PrNative.SecurityForce2000, ProtectedVideoRuntime.ChooseSecurityPolicy(forceSl2000: true));
        Assert.Equal(0, PrNative.SecurityProbe3000);
        Assert.Equal(2000, PrNative.SecurityForce2000);
    }

    [Fact]
    public void RuntimeCreate_IsAskedToProbeSl3000_ByDefault_SetBeforeTheCreate()
    {
        var native = new FakeRuntimeNative();
        string store = Path.Combine(Path.GetTempPath(), "fluentgpu-playready-tests", Guid.NewGuid().ToString("N"));
        using var runtime = new ProtectedVideoRuntime(native, store, 60_000, new FakeSessionNative(), () => 0L, 0);
        try
        {
            Assert.True(runtime.Acquire());
            Assert.Equal(PrNative.SecurityProbe3000, native.LastSecurityPolicy);                        // nothing forces SL2000 unless --fg playready-sl2000
            Assert.Equal(new[] { PrNative.SecurityProbe3000 }, native.SecurityPoliciesAtCreate);         // and it was in force when the runtime was created
        }
        finally
        {
            try { if (Directory.Exists(store)) Directory.Delete(store, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void TheNativeLibraryMustExportTheNewEntryPoints()
    {
        // A DLL built before this ABI lacks them: it is reported as a stale build up front, never called into.
        Assert.Contains("FgPrRuntimeSetSecurityPolicy", PrRuntimeNative.RequiredExports);
        Assert.Contains("FgPrSessionPrefetchInit", PrRuntimeNative.RequiredExports);
        Assert.Equal("FgPrSessionPrefetchInit", PrRuntimeNative.FirstMissingExport(name => name != "FgPrSessionPrefetchInit"));
    }
}
