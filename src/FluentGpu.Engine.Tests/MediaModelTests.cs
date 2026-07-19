using System;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// M0 tests for the portable value types: the <see cref="MediaSource"/> algebra + factories + overrides + the routing
/// sniffer, the typed <see cref="MediaError"/> model, the observable <see cref="PlayQueue"/> shell, and the
/// <see cref="DecryptingSource"/> decorator (spec §5/§8/§11).
/// </summary>
public sealed class MediaModelTests
{
    private static void Wait(ValueTask vt) => vt.AsTask().GetAwaiter().GetResult();

    // ── (e) source algebra + factories + overrides ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Factories_ProduceTheExpectedConcreteShapes()
    {
        Assert.IsType<FileSource>(MediaSource.FromFile("a.flac"));
        Assert.IsType<UriSource>(MediaSource.FromUri("https://h/x"));
        Assert.IsType<BytesSource>(MediaSource.FromBytes(new byte[] { 1, 2, 3 }));
        Assert.IsType<PullSource>(MediaSource.FromPull(new NullByteSource()));
        Assert.IsType<SampleSource>(MediaSource.FromSamples(new ScriptedSampleSource(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20))));
    }

    [Fact]
    public void Algebra_ConcatClipLoopMergeSilence_ProduceImmutableShapes()
    {
        var a = MediaSource.FromFile("1.flac");
        var b = MediaSource.FromFile("2.flac");

        var concat = MediaSource.Concat(a, b);
        var cc = Assert.IsType<ConcatSource>(concat);
        Assert.Equal(2, cc.Parts.Count);

        var clip = a.Clip(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        var cs = Assert.IsType<ClipSource>(clip);
        Assert.Equal(TimeSpan.FromSeconds(1), cs.Start);
        Assert.Equal(TimeSpan.FromSeconds(5), cs.End);

        var loop = a.Loop(3);
        Assert.Equal(3, Assert.IsType<LoopSource>(loop).Count);
        Assert.Equal(-1, Assert.IsType<LoopSource>(a.Loop()).Count);   // -1 == infinite

        var merge = MediaSource.Merge(a, b);
        Assert.Equal(2, Assert.IsType<MergeSource>(merge).Tracks.Count);

        var silence = a.Silence(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.IsType<SilenceSource>(silence).Duration);
    }

    [Fact]
    public void Overrides_AreNonDestructive_PreservingImmutability()
    {
        var baseSrc = MediaSource.FromFile("a.flac");

        var meta = new MediaMetadata("T", "A", "Al", Array.Empty<ArtworkRef>(), MediaKind.PcmAudio);
        var withMeta = baseSrc.WithMetadata(meta);
        Assert.Same(meta, withMeta.Metadata);
        Assert.Null(baseSrc.Metadata);   // original untouched

        var withSub = withMeta.WithExternalSubtitle(SubtitleSource.FromFile("a.srt"));
        Assert.Equal(1, withSub.ExternalSubtitles.Count);
        Assert.Equal(0, withMeta.ExternalSubtitles.Count);   // original untouched

        var net = new NetworkOptions(OnRequest: r => r);
        var withNet = baseSrc.With(net);
        Assert.Same(net, withNet.Network);

        var drm = new DrmConfig(DrmSystem.Widevine, "https://license");
        Assert.Same(drm, baseSrc.With(drm).Drm);

        Assert.Equal(MediaKind.PcmAudio, MediaSource.FromPull(new NullByteSource()).WithKind(MediaKind.PcmAudio).Kind);
    }

    [Fact]
    public void WithExternalSubtitle_AppendsAndKeepsPriorTracks()
    {
        var s = MediaSource.FromFile("m.mp4")
            .WithExternalSubtitle(SubtitleSource.FromFile("en.srt"))
            .WithExternalSubtitle(SubtitleSource.FromUri("https://h/fr.vtt"));
        Assert.Equal(2, s.ExternalSubtitles.Count);
    }

    // ── sniffer (Auto routing) ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("song.mp3", MediaKind.PcmAudio)]
    [InlineData("track.flac", MediaKind.PcmAudio)]
    [InlineData("audio.opus", MediaKind.PcmAudio)]
    [InlineData("clip.mp4", MediaKind.MfVideoOrFile)]
    [InlineData("movie.mkv", MediaKind.MfVideoOrFile)]
    [InlineData("stream.webm", MediaKind.MfVideoOrFile)]
    public void Sniffer_PicksKindByExtension(string path, MediaKind expected)
        => Assert.Equal(expected, MediaKindSniffer.Sniff(MediaSource.FromFile(path)));

    [Fact]
    public void Sniffer_HandlesQueryStrings_ExplicitKind_AndCallbackBytes()
    {
        Assert.Equal(MediaKind.MfVideoOrFile, MediaKindSniffer.Sniff(MediaSource.FromUri("https://h/clip.mp4?token=abc")));
        Assert.Equal(MediaKind.PcmAudio, MediaKindSniffer.Sniff(MediaSource.FromPull(new NullByteSource())));   // raw bytes ⇒ audio graph
        Assert.Equal(MediaKind.PcmAudio, MediaKindSniffer.Sniff(MediaSource.FromFile("x.unknownext").WithKind(MediaKind.PcmAudio)));   // explicit wins
    }

    [Fact]
    public void Sniffer_SampleSourceWithVideoStream_RoutesToVideo()
    {
        var withVideo = MediaSource.FromSamples(new ScriptedSampleSource(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40), new SizeI(640, 480)));
        var audioOnly = MediaSource.FromSamples(new ScriptedSampleSource(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40)));
        Assert.Equal(MediaKind.MfVideoOrFile, MediaKindSniffer.Sniff(withVideo));
        Assert.Equal(MediaKind.PcmAudio, MediaKindSniffer.Sniff(audioOnly));
    }

    // ── (f) typed error model ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MediaError_CarriesCategoryCodeLocusRecovery()
    {
        var err = new MediaError(
            MediaErrorCategory.Drm, "license expired", 0x8004C029,
            new MediaLocus(2, MediaSource.FromFile("x.mp4"), TimeSpan.FromSeconds(3), 0, 1024),
            MediaRecovery.NeedsLicense);

        Assert.Equal(MediaErrorCategory.Drm, err.Category);
        Assert.False(string.IsNullOrEmpty(err.Message));   // never nil-error
        Assert.Equal(0x8004C029, err.UnderlyingCode);
        Assert.Equal(MediaRecovery.NeedsLicense, err.Recovery);
        Assert.NotNull(err.Locus);
        Assert.Equal(2, err.Locus!.Value.QueueIndex);
        Assert.Equal(1024, err.Locus!.Value.ByteOffset);
    }

    [Fact]
    public void MediaError_NoBackend_IsLifecycleFatal()
    {
        var err = MediaError.NoBackend(MediaKind.MfVideoOrFile);
        Assert.Equal(MediaErrorCategory.Lifecycle, err.Category);
        Assert.Equal(MediaRecovery.Fatal, err.Recovery);
    }

    // ── PlayQueue shell (observable model + verbs) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void PlayQueue_AddNextGoToRemove_DriveCurrentIndexAndIntent()
    {
        var q = new PlayQueue();
        var a = q.Add(MediaSource.FromFile("1.flac"));
        q.Add(MediaSource.FromFile("2.flac"));
        q.Add(MediaSource.FromFile("3.flac"));

        Assert.Equal(0, q.CurrentIndex.Peek());
        Assert.Same(a, q.Current.Peek());

        int intents = 0;
        q.TransitionRequested += (_, _) => intents++;

        Wait(q.NextAsync());
        Assert.Equal(1, q.CurrentIndex.Peek());
        Assert.Equal(1, intents);

        Wait(q.GoToAsync(2));
        Assert.Equal(2, q.CurrentIndex.Peek());

        q.Remove(a);
        Assert.Equal(2, q.Items.Count);
    }

    [Fact]
    public void PlayQueue_RepeatModes()
    {
        var q = new PlayQueue { Repeat = RepeatMode.All };
        q.Add(MediaSource.FromFile("1.flac"));
        q.Add(MediaSource.FromFile("2.flac"));
        Wait(q.GoToAsync(1));
        Wait(q.NextAsync());   // wraps to 0 under RepeatMode.All
        Assert.Equal(0, q.CurrentIndex.Peek());

        q.Repeat = RepeatMode.One;
        Wait(q.NextAsync());   // stays put under RepeatMode.One
        Assert.Equal(0, q.CurrentIndex.Peek());
    }

    // ── DecryptingSource decorator (§5.4) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DecryptingSource_ReturnsPlaintextOnRead()
    {
        byte[] plaintext = MakePlaintext(256);
        byte[] key = { 0xA5, 0x5A, 0x0F, 0xF0 };
        byte[] ciphertext = (byte[])plaintext.Clone();
        XorKeystream(ciphertext, 0, key);

        var d = new DecryptingSource(new MemoryByteSource(ciphertext), new XorCtrCipher(key));
        Assert.True(d.TryOpen(new DataSpec { Position = 0, Length = -1 }));

        byte[] outBuf = new byte[plaintext.Length];
        int total = 0;
        while (total < outBuf.Length)
        {
            int n = d.Read(outBuf.AsSpan(total));
            if (n <= 0) break;
            total += n;
        }
        Assert.Equal(plaintext.Length, total);
        Assert.True(outBuf.AsSpan().SequenceEqual(plaintext));
    }

    [Fact]
    public void DecryptingSource_MidStreamReopen_RederivesCounter_NoReplay()
    {
        byte[] plaintext = MakePlaintext(256);
        byte[] key = { 0xA5, 0x5A, 0x0F, 0xF0 };
        byte[] ciphertext = (byte[])plaintext.Clone();
        XorKeystream(ciphertext, 0, key);

        var d = new DecryptingSource(new MemoryByteSource(ciphertext), new XorCtrCipher(key));
        d.TryOpen(new DataSpec { Position = 64, Length = -1 });   // open mid-stream
        byte[] mid = new byte[32];
        int got = d.Read(mid);
        Assert.Equal(32, got);
        Assert.True(mid.AsSpan().SequenceEqual(plaintext.AsSpan(64, 32)));
    }

    // ── effects null-object (§7.10) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NullAudioEffects_IsInertButUniformlyBindable()
    {
        IAudioEffects fx = NullAudioEffects.Instance;
        Assert.NotNull(fx.Equalizer);
        Assert.Equal(0f, fx.CrossfadeMs.Peek());
        Assert.Equal(NormMode.Off, fx.Normalization.Peek());
        Assert.Equal(VisualizerFrame.Silence, fx.Visualizer.Peek());
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static byte[] MakePlaintext(int n)
    {
        var p = new byte[n];
        for (int i = 0; i < n; i++) p[i] = (byte)(i * 7 + 3);
        return p;
    }

    private static void XorKeystream(byte[] buffer, long startOffset, byte[] key)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            long pos = startOffset + i;
            buffer[i] ^= (byte)(key[pos % key.Length] ^ (byte)(pos * 31));
        }
    }

    private sealed class XorCtrCipher : ICtrCipher
    {
        private readonly byte[] _key;
        private long _pos;
        public XorCtrCipher(byte[] key) => _key = key;
        public void SeekCounter(long bytePosition) => _pos = bytePosition;
        public void XorInPlace(Span<byte> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                long pos = _pos + i;
                buffer[i] ^= (byte)(_key[pos % _key.Length] ^ (byte)(pos * 31));
            }
            _pos += buffer.Length;
        }
    }

    private sealed class MemoryByteSource : IMediaByteSource
    {
        private readonly byte[] _data;
        private long _pos;
        public MemoryByteSource(byte[] data) => _data = data;
        public bool TryOpen(in DataSpec spec) { _pos = Math.Clamp(spec.Position, 0, _data.Length); return true; }
        public int Read(Span<byte> dst)
        {
            int n = (int)Math.Min(dst.Length, _data.Length - _pos);
            if (n <= 0) return 0;
            _data.AsSpan((int)_pos, n).CopyTo(dst);
            _pos += n;
            return n;
        }
        public long Seek(long offset) { _pos = Math.Clamp(offset, 0, _data.Length); return _pos; }
        public long? Length => _data.Length;
        public SourceCaps Caps => new() { Seekable = true, KnownLength = true };
        public void Cancel() { }
        public void Close() { }
    }

    private sealed class NullByteSource : IMediaByteSource
    {
        public bool TryOpen(in DataSpec spec) => true;
        public int Read(Span<byte> dst) => 0;
        public long Seek(long offset) => 0;
        public long? Length => null;
        public SourceCaps Caps => default;
        public void Cancel() { }
        public void Close() { }
    }
}
