using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>M3 §5.4: the <see cref="DecryptingSource"/> decorator (the PlayPlay exemplar). AES-CTR re-derives its counter from
/// the byte offset so any offset/seek decrypts without replay, and it is INVISIBLE to the decoder above it.</summary>
public sealed class DecryptingSourceTests
{
    [Fact]
    public void DecryptingSource_RoundTripsPlaintext_AtArbitraryOffsetsAndSeeks()
    {
        var key = M3TestSupport.Key16();
        var iv = M3TestSupport.Iv16();
        var plain = new byte[1000];
        for (int i = 0; i < plain.Length; i++) plain[i] = (byte)(i * 31 + 7);

        var cipherBytes = M3TestSupport.CtrEncrypt(plain, key, iv);
        Assert.NotEqual(plain, cipherBytes);   // it really is encrypted

        // Open at 0 → sequential read yields plaintext.
        var src = new DecryptingSource(new BytesByteSource(cipherBytes), new M3TestSupport.TestCtrCipher(key, iv));
        Assert.True(src.TryOpen(new DataSpec { Position = 0, Length = -1 }));
        var got = new byte[400];
        int n = src.Read(got);
        Assert.True(n > 0);
        for (int i = 0; i < n; i++) Assert.Equal(plain[i], got[i]);

        // Open at an arbitrary NON-block-aligned offset → counter re-derived, still plaintext.
        var src2 = new DecryptingSource(new BytesByteSource(cipherBytes), new M3TestSupport.TestCtrCipher(key, iv));
        const int off = 517;
        Assert.True(src2.TryOpen(new DataSpec { Position = off, Length = -1 }));
        var got2 = new byte[200];
        int n2 = src2.Read(got2);
        for (int i = 0; i < n2; i++) Assert.Equal(plain[off + i], got2[i]);

        // Seek mid-stream → counter re-derived at the new offset.
        const int seekTo = 813;
        long p = src2.Seek(seekTo);
        Assert.Equal(seekTo, p);
        var got3 = new byte[100];
        int n3 = src2.Read(got3);
        for (int i = 0; i < n3; i++) Assert.Equal(plain[seekTo + i], got3[i]);
    }

    [Fact]
    public void DecryptingSource_IsInvisibleToTheDecoder()
    {
        var key = M3TestSupport.Key16();
        var iv = M3TestSupport.Iv16();
        var fmt = new MixFormat(48000, 2);

        var wav = M3TestSupport.MakeWavPcm16(48000, 2, M3TestSupport.ToneStereo(48000, 0.1, 440));
        var plainPcm = M3TestSupport.DecodeAll(new BytesByteSource(wav), fmt);

        var cipherWav = M3TestSupport.CtrEncrypt(wav, key, iv);
        var decrypting = new DecryptingSource(new BytesByteSource(cipherWav), new M3TestSupport.TestCtrCipher(key, iv));
        var decryptedPcm = M3TestSupport.DecodeAll(decrypting, fmt);

        Assert.Equal(plainPcm.Length, decryptedPcm.Length);
        for (int i = 0; i < plainPcm.Length; i++) Assert.Equal(plainPcm[i], decryptedPcm[i], 6);
    }

    [Fact]
    public void DecryptingSource_ForwardsCapabilitiesAndLength_Transparently()
    {
        var key = M3TestSupport.Key16();
        var iv = M3TestSupport.Iv16();
        var inner = new BytesByteSource(new byte[256]);
        var src = new DecryptingSource(inner, new M3TestSupport.TestCtrCipher(key, iv));
        Assert.Equal(256, src.Length);
        Assert.True(src.Caps.Seekable);
        Assert.True(src.Caps.KnownLength);
    }
}
