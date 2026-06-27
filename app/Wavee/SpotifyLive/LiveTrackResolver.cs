using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;
using M = Wavee.Protocol.Metadata;
using Wavee.Protocol.Storage;

namespace Wavee.SpotifyLive;

// ── Stage F — the live track resolver: Track -> AudioStreamHandle ─────────────────────────────────────────────────────
// gid (base62) -> full metadata.proto Track -> select the OGG file by quality -> storage-resolve (CDN) + audio-key.
// IMPORTANT: a track's own file[] is often EMPTY (market/licensing); the playable file then lives in an ALTERNATIVE track
// (Track.alternative[], which typically carries only files). We fall through to the first alternative with files and use
// THAT alternative's gid for the audio-key request (the file belongs to the alternative). Resolution is in scope; the
// decrypt/decode/output it feeds is the deferred host.
public sealed class LiveTrackResolver : ITrackResolver
{
    readonly ITransport _transport;
    readonly IAudioKeySource _keys;
    readonly Action<string>? _log;

    public LiveTrackResolver(ITransport transport, IAudioKeySource keys, Action<string>? log = null)
    {
        _transport = transport;
        _keys = keys;
        _log = log;
    }

    public async Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
    {
        try
        {
            var gid = Base62.Decode(track.Id, 16);
            var meta = await _transport.Request(Channel.Spclient, "/metadata/4/track/" + Convert.ToHexStringLower(gid), default, ct).ConfigureAwait(false);
            if (!meta.Ok) { _log?.Invoke("metadata fetch failed (" + meta.Status + ") for " + track.Uri); return Fallback(track); }

            var t = M.Track.Parser.ParseFrom(meta.Body);
            var sel = SelectFile(t);
            if (sel is null) { _log?.Invoke("no playable OGG file (incl. alternatives) for " + track.Uri); return Fallback(track); }
            var (fileId, fileGid, fmt, durMs) = sel.Value;
            string fileIdHex = Convert.ToHexStringLower(fileId);

            string cdn = "";
            var sr = await _transport.Request(Channel.Spclient, "/storage-resolve/files/audio/interactive/" + fileIdHex, default, ct).ConfigureAwait(false);
            if (sr.Ok)
            {
                var r = StorageResolveResponse.Parser.ParseFrom(sr.Body);
                if (r.Cdnurl.Count > 0) cdn = r.Cdnurl[0];
            }

            ReadOnlyMemory<byte> key = default;
            try { key = await _keys.GetKeyAsync(fileId, fileGid, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke("audio-key fetch failed (" + ex.Message + ") for " + track.Uri + " — host will derive"); }

            return new AudioStreamHandle(track.Uri, fileIdHex, cdn, key, fmt, durMs > 0 ? durMs : track.DurationMs, 0f);
        }
        catch (Exception ex) { _log?.Invoke("resolve failed for " + track.Uri + ": " + ex.Message); return Fallback(track); }
    }

    static AudioStreamHandle Fallback(Track t) => new(t.Uri, "", "", default, AudioFormat.OggVorbis320, t.DurationMs, 0f);

    // Prefer the main track's files; if none, fall through to the FIRST alternative that has files (and its gid).
    static (byte[] fileId, byte[] gid, AudioFormat fmt, long durMs)? SelectFile(M.Track track)
    {
        var pick = PickFrom(track.File);
        if (pick is not null)
            return (pick.Value.fileId, track.Gid.ToByteArray(), pick.Value.fmt, track.HasDuration ? track.Duration : 0);

        foreach (var alt in track.Alternative)
        {
            var ap = PickFrom(alt.File);
            if (ap is not null)
                return (ap.Value.fileId, alt.Gid.ToByteArray(), ap.Value.fmt,
                    alt.HasDuration ? alt.Duration : (track.HasDuration ? track.Duration : 0));
        }
        return null;
    }

    // Quality ladder: OGG 320 > 160 > 96 (only OGG Vorbis — we advertise VeryHigh, not HIFI/FLAC).
    static (byte[] fileId, AudioFormat fmt)? PickFrom(IEnumerable<M.AudioFile> files)
    {
        M.AudioFile? best = null;
        int bestRank = 0;
        foreach (var f in files)
        {
            if (f.FileId.Length == 0) continue;
            int rank = f.Format switch
            {
                M.AudioFile.Types.Format.OggVorbis320 => 3,
                M.AudioFile.Types.Format.OggVorbis160 => 2,
                M.AudioFile.Types.Format.OggVorbis96 => 1,
                _ => 0,
            };
            if (rank > bestRank) { bestRank = rank; best = f; }
        }
        if (best is null) return null;
        var fmt = bestRank == 3 ? AudioFormat.OggVorbis320 : bestRank == 2 ? AudioFormat.OggVorbis160 : AudioFormat.OggVorbis96;
        return (best.FileId.ToByteArray(), fmt);
    }
}
