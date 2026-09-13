using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The native CENC demuxer gate: <c>FgPrProbeFile</c> (<c>ops/tools/playready-native/FgPlayReady.h</c>) runs the
/// runtime's own ParseInit + ParseSegment over a LOCAL fragmented MP4 and reports the keyframe table, the frame size,
/// the NAL length size and the protection it saw. No CDM, no D3D device, no network, no window, no runtime — but it
/// needs <c>FluentGpu.PlayReady.Native.dll</c> beside the test assembly, so every test skips (never fails) on a box
/// that has not built it. Fixtures live in <c>Fixtures/video/</c> (see its README): the generated DASH clip has a
/// documented fixed 2-second GOP, which is what makes the keyframe table checkable.
/// </summary>
public sealed unsafe class CencDemuxTests
{
    /// <summary>One frame of the generated clip (30 fps), rounded up — the tolerance on a keyframe time.</summary>
    private const long OneFrameMs = 34;

    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "video");

    private static readonly string[] GateClipParts =
    [
        "init-stream0.m4s",
        "chunk-stream0-00001.m4s",
        "chunk-stream0-00002.m4s",
        "chunk-stream0-00003.m4s",
        "chunk-stream0-00004.m4s",
        "chunk-stream0-00005.m4s",
    ];

    private static readonly Lazy<bool> s_hasProbeExport = new(() =>
        NativeLibrary.TryLoad(PrNative.LibraryName, typeof(ProtectedVideoRuntime).Assembly,
            DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.AssemblyDirectory, out nint handle)
        && NativeLibrary.TryGetExport(handle, "FgPrProbeFile", out _));

    private static void RequireNative()
    {
        Assert.SkipUnless(ProtectedVideoRuntime.IsAvailable, "FluentGpu.PlayReady.Native.dll is not beside the test assembly");
        Assert.SkipUnless(s_hasProbeExport.Value,
            "FluentGpu.PlayReady.Native.dll beside the test assembly predates FgPrProbeFile (a stale build) - rebuild ops/tools/playready-native");
    }

    private static string Fixture(string name)
    {
        string path = Path.Combine(FixtureDir, name);
        Assert.True(File.Exists(path), "missing fixture " + path);
        return path;
    }

    private static int Probe(string path, long[]? keyframes, int cap, out PrNative.ProbeResult result)
    {
        PrNative.ProbeResult r = default;
        r.StructSize = (uint)sizeof(PrNative.ProbeResult);
        int hr;
        fixed (long* k = keyframes)
            hr = PrNative.FgPrProbeFile(path, k, cap, &r);
        result = r;
        return hr;
    }

    /// <summary>The DASH clip's video representation as ONE self-contained fragmented MP4: the init segment's moov
    /// followed by the five media segments' moof/mdat runs. The caller deletes it.</summary>
    private static string BuildGateClip()
    {
        string path = Path.Combine(Path.GetTempPath(), "fluentgpu-cenc-gate-" + Guid.NewGuid().ToString("N") + ".mp4");
        using FileStream output = File.Create(path);
        foreach (string part in GateClipParts)
        {
            using FileStream input = File.OpenRead(Fixture(part));
            input.CopyTo(output);
        }
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void AssertSucceeded(int hr) => Assert.True(hr >= 0, $"FgPrProbeFile failed: hr=0x{unchecked((uint)hr):X8}");

    [Fact]
    public void GateClip_HasExactlyOneSyncSampleEveryTwoSeconds_At640x360_WithFourByteNalLengths()
    {
        RequireNative();
        string clip = BuildGateClip();
        try
        {
            var keyframes = new long[64];
            AssertSucceeded(Probe(clip, keyframes, keyframes.Length, out PrNative.ProbeResult r));

            Assert.Equal(10, r.KeyframeCount);   // 20 s, a 60-frame GOP at 30 fps
            for (int i = 1; i < r.KeyframeCount; i++)
                Assert.InRange(keyframes[i] - keyframes[i - 1], 2_000 - OneFrameMs, 2_000 + OneFrameMs);
            Assert.Equal(600, r.SampleCount);
            Assert.Equal(640, r.Width);
            Assert.Equal(360, r.Height);
            Assert.Equal(4, r.NalLengthSize);
            Assert.Equal(0, r.Encrypted);
            Assert.Equal(0, r.SubsampleCount);
        }
        finally
        {
            TryDelete(clip);
        }
    }

    [Fact]
    public void GateClip_KeyframeTimes_AreOnTheManifestTimeline()
    {
        // gate.mpd's SegmentTimeline starts at t=0 and every 4 s segment opens on a keyframe, so the table must read
        // 0, 2000, …, 18000 (± one frame). The init segment carries an edit list (elst media_time = 2 frames of B-frame
        // composition delay); presentation time is decode + composition offset MINUS that edit.
        RequireNative();
        string clip = BuildGateClip();
        try
        {
            var keyframes = new long[64];
            AssertSucceeded(Probe(clip, keyframes, keyframes.Length, out PrNative.ProbeResult r));

            Assert.Equal(10, r.KeyframeCount);
            for (int i = 0; i < r.KeyframeCount; i++)
                Assert.InRange(keyframes[i], i * 2_000L - OneFrameMs, i * 2_000L + OneFrameMs);
        }
        finally
        {
            TryDelete(clip);
        }
    }

    [Fact]
    public void AKeyframeBufferSmallerThanTheTable_StillReportsTheTotal_AndFillsOnlyTheBuffer()
    {
        RequireNative();
        string clip = BuildGateClip();
        try
        {
            var full = new long[64];
            AssertSucceeded(Probe(clip, full, full.Length, out PrNative.ProbeResult all));

            var small = new long[] { -7, -7, -7, -7, -7 };
            AssertSucceeded(Probe(clip, small, 3, out PrNative.ProbeResult capped));
            Assert.Equal(all.KeyframeCount, capped.KeyframeCount);
            Assert.Equal(full[..3], small[..3]);
            Assert.Equal(-7, small[3]);   // nothing written past cap

            AssertSucceeded(Probe(clip, null, 0, out PrNative.ProbeResult countOnly));
            Assert.Equal(all.KeyframeCount, countOnly.KeyframeCount);
        }
        finally
        {
            TryDelete(clip);
        }
    }

    [Fact]
    public void ClearFragmentedMp4_ReportsAnAscendingKeyframeTable_AndNoProtection()
    {
        RequireNative();
        var keyframes = new long[1024];

        AssertSucceeded(Probe(Fixture("bear-1280x720-av_frag.mp4"), keyframes, keyframes.Length, out PrNative.ProbeResult r));

        Assert.True(r.KeyframeCount > 0, "the video track's sync samples were not reported");
        int filled = Math.Min(r.KeyframeCount, keyframes.Length);
        for (int i = 1; i < filled; i++)
            Assert.True(keyframes[i] > keyframes[i - 1], $"keyframe {i} ({keyframes[i]} ms) is not after {keyframes[i - 1]} ms");
        Assert.Equal(1280, r.Width);
        Assert.Equal(720, r.Height);
        Assert.Equal(4, r.NalLengthSize);
        Assert.Equal(0, r.Encrypted);
        Assert.Equal(0, r.SubsampleCount);
    }

    [Fact]
    public void CencEncryptedAudioTrack_IsReportedEncrypted()
    {
        // An audio-only file whose `enca` sample entry carries sinf/schm/tenc (default_isProtected = 1). Its per-sample
        // aux info is saiz/saio rather than senc, so the subsample count may legitimately be 0.
        RequireNative();
        var keyframes = new long[16];

        AssertSucceeded(Probe(Fixture("bear-1280x720-a_frag-cenc.mp4"), keyframes, keyframes.Length, out PrNative.ProbeResult r));

        Assert.Equal(1, r.Encrypted);
        Assert.True(r.SubsampleCount >= 0);
        Assert.True(r.SampleCount > 0, "no audio samples were parsed");
    }

    [Fact]
    public void AMuxedFile_ReportsItsVideoTrack_AndTheTrexDefaultsMarkOnlyTheSyncSamples()
    {
        // bear-1280x720-av_frag muxes a video and an audio trak and interleaves their trafs in every moof; its video trun
        // carries no per-sample duration or flags, so they come from mvex/trex. Merging the traks (or reading the first
        // traf only, or ignoring trex) reports the audio track, every sample a zero-length keyframe, or nothing at all.
        RequireNative();
        var keyframes = new long[1024];

        AssertSucceeded(Probe(Fixture("bear-1280x720-av_frag.mp4"), keyframes, keyframes.Length, out PrNative.ProbeResult r));

        Assert.Equal(1280, r.Width);
        Assert.True(r.SampleCount > r.KeyframeCount, $"samples={r.SampleCount} keyframes={r.KeyframeCount}: every sample reads as sync");
        Assert.True(r.KeyframeCount >= 1);
        // No edit list in this file: the first frame presents at its own composition offset (two frames at 30 fps).
        Assert.InRange(keyframes[0], 60, 70);
        Assert.InRange(r.DurationMs, 1_000, 60_000);
    }

    [Fact]
    public void GateAudio_TheEditListTakesThePrimingFrameOffThePresentation()
    {
        // init-stream1's elst media_time is 1024 ticks at 44.1 kHz: the first AAC frame (the encoder priming) ends at
        // presentation time 0 and is not part of the presentation. The six segments' truns hold 863 frames.
        RequireNative();
        string clip = Path.Combine(Path.GetTempPath(), "fluentgpu-cenc-audio-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            using (FileStream output = File.Create(clip))
                foreach (string part in new[] { "init-stream1.m4s", "chunk-stream1-00001.m4s", "chunk-stream1-00002.m4s",
                                                "chunk-stream1-00003.m4s", "chunk-stream1-00004.m4s", "chunk-stream1-00005.m4s",
                                                "chunk-stream1-00006.m4s" })
                {
                    using FileStream input = File.OpenRead(Fixture(part));
                    input.CopyTo(output);
                }

            AssertSucceeded(Probe(clip, null, 0, out PrNative.ProbeResult r));

            Assert.Equal(862, r.SampleCount);                 // 863 - the priming frame
            Assert.InRange(r.DurationMs, 19_950, 20_050);     // the 20 s manifest presentation
            Assert.Equal(0, r.KeyframeCount);       // the keyframe table is the video seek planner's; audio reports none
            Assert.Equal(0, r.NalLengthSize);
            Assert.Equal(0, r.Encrypted);
        }
        finally
        {
            TryDelete(clip);
        }
    }

    [Fact]
    public void AMissingFile_ReturnsAFailureHresult()
    {
        RequireNative();
        string missing = Path.Combine(Path.GetTempPath(), "fluentgpu-cenc-missing-" + Guid.NewGuid().ToString("N") + ".mp4");

        int hr = Probe(missing, new long[4], 4, out _);

        Assert.True(hr < 0, $"expected a failure HRESULT for a missing file, got 0x{unchecked((uint)hr):X8}");
    }

    [Fact]
    public void AFileThatIsNotAnMp4_IsRefusedOrReportsNothing_NeverACrash()
    {
        RequireNative();
        string junk = Path.Combine(Path.GetTempPath(), "fluentgpu-cenc-junk-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(junk, "this is not an ISO BMFF file at all, just text long enough to look like boxes");
        try
        {
            int hr = Probe(junk, new long[4], 4, out PrNative.ProbeResult r);
            Assert.True(hr < 0 || (r.SampleCount == 0 && r.KeyframeCount == 0),
                $"hr=0x{unchecked((uint)hr):X8} samples={r.SampleCount} keyframes={r.KeyframeCount}");
        }
        finally
        {
            TryDelete(junk);
        }
    }
}
