using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Pal;

namespace FluentGpu.Engine.Tests;

/// <summary>Shared deterministic helpers for the M3 queue/scheduler/effects/decrypt tests: a real AES-CTR test cipher (the
/// PlayPlay-shaped offset-derived counter), WAV/PCM builders, ramp vectors, and a fake video/DRM backend that implements the
/// cross-backend prepare seam. No wall clock anywhere.</summary>
internal static class M3TestSupport
{
    // ── a genuine AES-CTR cipher: counter = base(IV) + byteOffset/16, re-derivable at any offset (spec §5.4) ──────────
    internal sealed class TestCtrCipher : ICtrCipher, IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _enc;
        private readonly byte[] _iv;
        private readonly byte[] _ctr = new byte[16];
        private readonly byte[] _ks = new byte[16];
        private long _block;
        private int _inBlock;
        private bool _ksValid;

        public TestCtrCipher(byte[] key, byte[] iv)
        {
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = key;
            _enc = _aes.CreateEncryptor();
            _iv = (byte[])iv.Clone();
            SeekCounter(0);
        }

        public void SeekCounter(long bytePosition)
        {
            _block = bytePosition / 16;
            _inBlock = (int)(bytePosition % 16);
            _ksValid = false;
        }

        public void XorInPlace(Span<byte> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (!_ksValid) GenerateKeystream();
                buffer[i] ^= _ks[_inBlock];
                if (++_inBlock == 16) { _inBlock = 0; _block++; _ksValid = false; }
            }
        }

        private void GenerateKeystream()
        {
            _iv.CopyTo(_ctr, 0);
            // Add the block index to the IV as a big-endian 64-bit counter in the low 8 bytes.
            ulong add = (ulong)_block;
            for (int i = 15; i >= 8 && add != 0; i--)
            {
                ulong v = _ctr[i] + (add & 0xFF);
                _ctr[i] = (byte)v;
                add = (add >> 8) + (v >> 8);
            }
            _enc.TransformBlock(_ctr, 0, 16, _ks, 0);
            _ksValid = true;
        }

        public void Dispose() { _enc.Dispose(); _aes.Dispose(); }
    }

    /// <summary>Encrypt <paramref name="plaintext"/> with an AES-CTR keystream (returns a NEW ciphertext buffer). Symmetric —
    /// decrypting the result with the same key/IV yields the plaintext.</summary>
    internal static byte[] CtrEncrypt(byte[] plaintext, byte[] key, byte[] iv)
    {
        var cipher = new byte[plaintext.Length];
        Array.Copy(plaintext, cipher, plaintext.Length);
        using var c = new TestCtrCipher(key, iv);
        c.XorInPlace(cipher);
        return cipher;
    }

    internal static byte[] Key16() { var k = new byte[16]; for (int i = 0; i < 16; i++) k[i] = (byte)(i * 7 + 3); return k; }
    internal static byte[] Iv16() { var v = new byte[16]; for (int i = 0; i < 16; i++) v[i] = (byte)(i * 5 + 1); return v; }

    // ── PCM/WAV builders ─────────────────────────────────────────────────────────────────────────────────────────────
    internal static float[] ToneStereo(int rate, double seconds, double freq, float amp = 0.5f)
    {
        int frames = (int)(rate * seconds);
        var a = new float[frames * 2];
        for (int f = 0; f < frames; f++) { float s = amp * MathF.Sin(2f * MathF.PI * (float)freq * f / rate); a[f * 2] = s; a[f * 2 + 1] = s; }
        return a;
    }

    internal static byte[] MakeWavPcm16(int rate, int channels, float[] interleaved)
    {
        int frames = interleaved.Length / channels;
        int dataBytes = frames * channels * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in interleaved) w.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Decode an entire <see cref="IMediaByteSource"/> as WAV into interleaved f32.</summary>
    internal static float[] DecodeAll(IMediaByteSource src, MixFormat fmt)
    {
        var dec = new WavAudioDecoder();
        Assert(dec.TryOpen(src, fmt, out _), "decoder open");
        var buf = new float[4096 * fmt.Channels];
        var acc = new System.Collections.Generic.List<float>();
        int n;
        while ((n = dec.Read(buf)) > 0) acc.AddRange(new ReadOnlySpan<float>(buf, 0, n * fmt.Channels).ToArray());
        return acc.ToArray();
    }

    private static void Assert(bool cond, string msg) { if (!cond) throw new InvalidOperationException(msg); }

    // ── a ramp memory source whose frame value == its (mixer) frame index, offset by a base ─────────────────────────
    internal static float[] Ramp(int len, float baseValue)
    {
        var a = new float[len];
        for (int i = 0; i < len; i++) a[i] = baseValue + i;
        return a;
    }

    // ── a fake video/DRM backend implementing BOTH the router seam and the cross-backend prepare seam ───────────────
    internal sealed class FakeVideoBackend : IMediaBackend, IPreparableBackend
    {
        public int PrepareCount;
        public long PreparedAtClock = -1;
        public int OpenCount;

        public MediaKind Kind => MediaKind.MfVideoOrFile;
        public MediaCapabilities Capabilities { get; } = new(SupportsVideo: true, SupportsAudioGraph: false, SupportsDrm: true);

        public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
        {
            OpenCount++;
            return ValueTask.FromResult<IMediaSession>(new FakeVideoSession());
        }

        public ValueTask<IPreparedItem> PrepareAsync(MediaSource next, PrepareContext ctx, CancellationToken ct)
        {
            PrepareCount++;
            // A non-audio prepared handle: the MF session is spun up + first-frame-readied ahead of the boundary (M5 fills
            // the real MF side). AudioVoice is null — the two engines never co-mix (a cross-backend join is a hard cut).
            return ValueTask.FromResult<IPreparedItem>(new FakeVideoPreparedItem());
        }
    }

    internal sealed class FakeVideoPreparedItem : IPreparedItem
    {
        public MediaKind Kind => MediaKind.MfVideoOrFile;
        public bool IsReady => true;
        public IAudioSource? AudioVoice => null;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public long TotalFrames => -1;
        public TimeSpan Duration => TimeSpan.FromSeconds(5);
        public object? BackendHandle => this;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FakeVideoSession : IMediaSession
    {
        public void ConnectSignals(MediaSignalSink sink) => sink.State(PlaybackState.Ready);
        public ValueTask PlayAsync() => ValueTask.CompletedTask;
        public ValueTask PauseAsync() => ValueTask.CompletedTask;
        public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
        public void SetRate(double rate) { }
        public void SetVolume(double volume) { }
        public void SetMuted(bool muted) { }
        public VideoDelivery Video => VideoDelivery.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
