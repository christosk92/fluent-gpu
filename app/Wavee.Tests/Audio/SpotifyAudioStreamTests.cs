using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class SpotifyAudioStreamTests
{
    static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    [Fact]
    public async Task Composes_ClearHead_Plus_DecryptedBody()
    {
        var key = A.Key16(2);
        var plaintext = A.Bytes(1, 600);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);   // CTR is symmetric → this is the CDN ciphertext
        const int N = 120;
        var head = plaintext.AsSpan(0, N).ToArray();

        var http = new HttpClient(new FakeHttpMessageHandler { Responder = _ => (HttpStatusCode.OK, cdn) });
        var stream = await SpotifyAudioStream.CreateAsync(http, head, N, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        var got = ReadAll(stream);
        Assert.Equal(plaintext, got);   // head (clear) + body (decrypted from offset N) reconstruct the track
    }

    [Fact]
    public async Task NoHead_DecryptsWholeBodyFromZero()
    {
        var key = A.Key16(4);
        var plaintext = A.Bytes(3, 300);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new FakeHttpMessageHandler { Responder = _ => (HttpStatusCode.OK, cdn) });

        var stream = await SpotifyAudioStream.CreateAsync(http, ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.Equal(plaintext, ReadAll(stream));
    }

    [Fact]
    public async Task FailsOverToSecondMirror()
    {
        var key = A.Key16(5);
        var plaintext = A.Bytes(7, 400);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new FakeHttpMessageHandler
        {
            Responder = url => url.Contains("bad")
                ? (HttpStatusCode.InternalServerError, Array.Empty<byte>())
                : (HttpStatusCode.OK, cdn),
        });

        var stream = await SpotifyAudioStream.CreateAsync(http, ReadOnlyMemory<byte>.Empty, 0, key,
            new[] { "https://cdn/bad", "https://cdn/good" }, cdn.Length, CancellationToken.None);

        Assert.Equal(plaintext, ReadAll(stream));
    }

    [Fact]
    public async Task AllMirrorsFail_Throws()
    {
        var http = new HttpClient(new FakeHttpMessageHandler { Responder = _ => (HttpStatusCode.InternalServerError, Array.Empty<byte>()) });

        await Assert.ThrowsAnyAsync<Exception>(() => SpotifyAudioStream.CreateAsync(
            http, ReadOnlyMemory<byte>.Empty, 0, A.Key16(1), new[] { "https://cdn/a", "https://cdn/b" }, null, CancellationToken.None));
    }

    [Fact]
    public async Task Seek_BackAndForth_ReadsCorrectBytes()
    {
        var key = A.Key16(6);
        var plaintext = A.Bytes(2, 500);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new FakeHttpMessageHandler { Responder = _ => (HttpStatusCode.OK, cdn) });
        var stream = await SpotifyAudioStream.CreateAsync(http, plaintext.AsSpan(0, 64).ToArray(), 64, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        // let the background download settle
        while (stream.Length < cdn.Length) await Task.Delay(2);

        stream.Seek(200, SeekOrigin.Begin);
        var mid = new byte[16];
        int r = stream.Read(mid, 0, 16);
        Assert.Equal(16, r);
        Assert.Equal(plaintext.AsSpan(200, 16).ToArray(), mid);   // decrypted body at an arbitrary offset
    }
}
