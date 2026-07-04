using System;
using ZstdSharp;

namespace Wavee.Backend.Spotify;

// ── §2.7 — the zstd response guard ────────────────────────────────────────────────────────────────────────────────────
// The /changes and rootlist /changes (and /diff) playlist-v2 responses can come back Content-Encoding: zstd. Our
// ITransport.Resp surfaces only the buffered body — no response headers — so magic-sniffing the 4-byte zstd frame magic
// (28 B5 2F FD) IS the guard. Decode manually (pure-managed ZstdSharp, AOT-safe): .NET's automatic zstd HTTP decode is
// known to truncate multi-frame bodies, so we never lean on it. A non-zstd body is returned unchanged.
public static class SpotifyZstd
{
    public static byte[] MaybeDecompressZstd(byte[] body)
    {
        if (body is null || body.Length < 4) return body ?? Array.Empty<byte>();
        if (!(body[0] == 0x28 && body[1] == 0xB5 && body[2] == 0x2F && body[3] == 0xFD)) return body;   // not a zstd frame
        using var d = new Decompressor();
        return d.Unwrap(body).ToArray();
    }
}
