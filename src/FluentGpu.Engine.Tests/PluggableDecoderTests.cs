using System;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Proves the ADDITIVE decode-edge plug-point on <see cref="PcmAudioPlayer"/> (spec §5.5 <see cref="IAudioDecoder"/>): a
/// NON-WAV decoder injected via the <c>decoderFactory</c> is used by both <c>OpenAsync</c> and drives the graph to real
/// (non-silent) PCM and a natural <see cref="PlaybackState.Ended"/>. The default (no factory) stays the built-in
/// <see cref="WavAudioDecoder"/> — the other 91 M2/M3 tests exercise that path unchanged. This is the seam the app's
/// Vorbis/FLAC/MP3 codec adapter rides through the one engine graph.
/// </summary>
public sealed class PluggableDecoderTests
{
    // A minimal non-WAV codec: emits a constant 0.5 tone for a fixed number of mix-domain frames, then EOF. It ignores the
    // byte source entirely (a real adapter would decode it) — the point is that the FACTORY, not WavAudioDecoder, is used.
    private sealed class FakeConstantDecoder : IAudioDecoder
    {
        private readonly int _totalFrames;
        private readonly int _channels;
        private readonly int _rate;
        private long _cursor;

        public FakeConstantDecoder(MixFormat target, int totalFrames)
        { _rate = target.SampleRate; _channels = target.Channels; _totalFrames = totalFrames; }

        public GaplessInfo Gapless => GaplessInfo.None;

        public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
        {
            // A pluggable streaming codec knows its format/duration from container metadata, NOT from a full byte length.
            var codec = new MediaContentType(Container.Ogg, CodecId.None, CodecId.Vorbis);
            info = new DecodedInfo(codec, new MixFormat(_rate, _channels),
                TimeSpan.FromSeconds((double)_totalFrames / _rate), default);
            return true;
        }

        public int Read(Span<float> dst)
        {
            int want = dst.Length / _channels;
            long remaining = _totalFrames - _cursor;
            int frames = (int)Math.Min(want, remaining);
            if (frames <= 0) return 0;
            dst[..(frames * _channels)].Fill(0.5f);
            _cursor += frames;
            return frames;
        }

        public long Seek(long frame) { _cursor = Math.Clamp(frame, 0, _totalFrames); return _cursor; }
    }

    // A STREAMING codec that never fills the whole request in one Read: it returns tiny, ragged chunks (a few frames at a
    // time, never == the requested block), then returns 0 exactly once at true end-of-stream. This is the shape a Spotify
    // Ogg/FLAC/MP3 stream (bytes trickling in behind the head) presents to the engine's decode edge. The point is that the
    // session/mixer/drain loop must treat a short read (>0 but < requested) as "more later", NOT as EOF, and must reach a
    // clean natural Ended only on the terminal 0 — i.e. it must not assume a fixed-length / EOF-once source.
    private sealed class ShortReadStreamingDecoder : IAudioDecoder
    {
        private readonly int _totalFrames;
        private readonly int _channels;
        private readonly int _rate;
        private long _cursor;
        private int _step;

        public ShortReadStreamingDecoder(MixFormat target, int totalFrames)
        { _rate = target.SampleRate; _channels = target.Channels; _totalFrames = totalFrames; }

        public GaplessInfo Gapless => GaplessInfo.None;

        public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
        {
            var codec = new MediaContentType(Container.Ogg, CodecId.None, CodecId.Vorbis);
            info = new DecodedInfo(codec, new MixFormat(_rate, _channels),
                TimeSpan.FromSeconds((double)_totalFrames / _rate), default);
            return true;
        }

        public int Read(Span<float> dst)
        {
            int want = dst.Length / _channels;
            long remaining = _totalFrames - _cursor;
            if (remaining <= 0) return 0;   // the single terminal EOF — never returned before the stream truly ends
            // Ragged short reads: 1..17 frames, always fewer than a full 512-frame block, sometimes just 1 frame.
            int chunk = 1 + (_step++ * 7) % 17;
            int frames = (int)Math.Min(Math.Min(chunk, want), remaining);
            dst[..(frames * _channels)].Fill(0.5f);
            _cursor += frames;
            return frames;
        }

        public long Seek(long frame) { _cursor = Math.Clamp(frame, 0, _totalFrames); _step = 0; return _cursor; }
    }

    [Fact]
    public async Task NonWavDecoder_PlugsIn_ProducesPcm_AndReachesEnded()
    {
        var fmt = new MixFormat(48000, 2);
        const int totalFrames = 48000 / 5;   // 0.2 s

        HeadlessAudioEndpoint? captured = null;
        var backend = new PcmAudioPlayer(
            fmt,
            endpointFactory: f => captured = new HeadlessAudioEndpoint(f, captureFrames: totalFrames * 2),
            maxBlock: 512,
            decoderFactory: f => new FakeConstantDecoder(f, totalFrames));

        var player = MediaPlayer.Build().WithBackend(MediaKind.PcmAudio, backend).Build();

        // Non-WAV bytes — the built-in WavAudioDecoder would FAIL to parse these; only the injected factory can decode.
        await player.OpenAsync(MediaSource.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).WithKind(MediaKind.PcmAudio))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var session = Assert.IsType<PcmAudioSession>(player.Session);
        Assert.NotNull(captured);

        session.PumpAudio(512);   // Opening → Buffering
        session.PumpAudio(512);   // Buffering → Ready
        Assert.Equal(PlaybackState.Ready, player.State.Peek());
        Assert.Equal(0.2, player.Duration.Peek().TotalSeconds, 2);

        await player.PlayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 400 && player.State.Peek() != PlaybackState.Ended; i++) session.PumpAudio(512);

        Assert.Equal(PlaybackState.Ended, player.State.Peek());
        Assert.True(player.Position.Peek() > TimeSpan.Zero);

        // The graph actually rendered the decoder's PCM (not silence) — proves the pluggable codec fed the mixer.
        var pcm = ((NullAudioSink)captured!.Sink).Captured;
        bool anyAudible = false;
        for (int i = 0; i < pcm.Length; i++) if (MathF.Abs(pcm[i]) > 0.1f) { anyAudible = true; break; }
        Assert.True(anyAudible);

        await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StreamingDecoder_ShortReads_ReachEndedWithoutTruncation()
    {
        var fmt = new MixFormat(48000, 2);
        const int totalFrames = 48000 / 4;   // 0.25 s

        HeadlessAudioEndpoint? captured = null;
        var backend = new PcmAudioPlayer(
            fmt,
            endpointFactory: f => captured = new HeadlessAudioEndpoint(f, captureFrames: totalFrames * 2),
            maxBlock: 512,
            decoderFactory: f => new ShortReadStreamingDecoder(f, totalFrames));

        var player = MediaPlayer.Build().WithBackend(MediaKind.PcmAudio, backend).Build();

        await player.OpenAsync(MediaSource.FromBytes(new byte[] { 9, 9, 9, 9 }).WithKind(MediaKind.PcmAudio))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var session = Assert.IsType<PcmAudioSession>(player.Session);
        Assert.NotNull(captured);

        session.PumpAudio(512);   // Opening → Buffering
        session.PumpAudio(512);   // Buffering → Ready
        Assert.Equal(PlaybackState.Ready, player.State.Peek());
        Assert.Equal(0.25, player.Duration.Peek().TotalSeconds, 2);

        await player.PlayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // Even though every Read returns only a ragged handful of frames (never a full block), the session must keep
        // pulling and advance the clock — a short read must NOT be mistaken for end-of-stream. Give it enough pumps to
        // consume the full 0.25 s at ≤17 frames/read, then confirm a CLEAN natural end (only the terminal 0 ends it).
        for (int i = 0; i < 20000 && player.State.Peek() != PlaybackState.Ended; i++) session.PumpAudio(512);

        Assert.Equal(PlaybackState.Ended, player.State.Peek());
        // The whole stream played — position reached (within a block of) the declared duration, proving no early truncation.
        Assert.True(player.Position.Peek() >= TimeSpan.FromSeconds(0.24),
            $"streamed position {player.Position.Peek().TotalSeconds:0.###}s truncated below 0.24s");

        var pcm = ((NullAudioSink)captured!.Sink).Captured;
        bool anyAudible = false;
        for (int i = 0; i < pcm.Length; i++) if (MathF.Abs(pcm[i]) > 0.1f) { anyAudible = true; break; }
        Assert.True(anyAudible);

        await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
