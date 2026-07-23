using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Wavee.SpotifyLive;

/// <summary>
/// Parses Spotify's v9 video manifest JSON (<c>/manifests/v9/json/sources/{manifestId}/options/supports_drm</c>) into a
/// native-descriptor-friendly model. IMPROVEMENT over the WaveeMusic reference: it does NOT synthesise a DASH MPD (that
/// round-trip existed only to feed WinRT AdaptiveMediaSource, which FluentGpu does not use). Instead it produces the
/// segment addressing the in-process CENC source needs directly (init URL + <c>base + prefix + &lt;timestamp&gt; + suffix</c>
/// with a TIMESTAMP STRIDE), plus PlayReady init data (PSSH/PRO) and the byte-swapped PlayReady key id.
/// <para>Pure C# (System.Text.Json + span parsing), TerraFX-free, headless-unit-testable. It reports which DRM systems
/// the manifest advertises so the caller can gate: FluentGpu ships PlayReady only (no Widevine lane).</para>
/// </summary>
sealed class SpotifyVideoManifest
{
    public string EncodingId { get; init; } = "";
    public int SegmentLengthSeconds { get; init; } = 4;
    public long DurationMs { get; init; }

    /// <summary>True when a <c>playready</c> encryption entry with an mp4/H.264 profile is present — the FluentGpu DRM lane.</summary>
    public bool HasPlayReadyMp4 { get; init; }
    /// <summary>True when the manifest advertises a <c>widevine</c> entry (NOT playable here — reported for the go/no-go).</summary>
    public bool HasWidevine { get; init; }

    // ── selected mp4/PlayReady video profile (a single conservative ≤480p representation) ──
    public int ProfileId { get; init; }
    public string VideoCodec { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }

    // ── segment addressing for the native CENC source (base + prefix + <startTs + i*strideSeconds> + suffix) ──
    public string InitUrl { get; init; } = "";
    public string SegmentBaseUrl { get; init; } = "";
    public string SegmentPrefix { get; init; } = "";
    public string SegmentSuffix { get; init; } = "";
    public int SegmentStrideSeconds { get; init; } = 4;   // Spotify names segments by absolute time; step == segment length
    public int SegmentCount { get; init; }

    // ── PlayReady protection ──
    public byte[]? Pssh { get; init; }
    public byte[]? Pro { get; init; }
    public string? CencKid { get; init; }            // hyphenated GUID
    public string? PlayReadyKid { get; init; }        // base64, first-8-bytes byte-swapped
    public string? LicenseServerEndpoint { get; init; }

    public static SpotifyVideoManifest FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromJson(doc.RootElement);
    }

    public static SpotifyVideoManifest FromJson(JsonElement root)
    {
        // v9 has two shapes: contents[0] carries profiles/encryption, root carries templates/base URLs; or everything
        // under sources[0]. Mirror the reference's host resolution.
        var content = root;
        var templateHost = root;
        if (root.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array && contents.GetArrayLength() > 0)
            content = contents[0];
        if (root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
        { content = sources[0]; templateHost = content; }

        string encodingId = Str(content, "encoding_id") ?? Str(content, "media_id") ?? "";
        int segLen = Int(content, "segment_length") ?? 4;
        if (segLen <= 0) segLen = 4;

        long durMs = Long(content, "duration") ?? 0;
        if (durMs <= 0)
        {
            long start = Long(content, "start_time_millis") ?? Long(root, "start_time_millis") ?? 0;
            long end = Long(content, "end_time_millis") ?? Long(root, "end_time_millis") ?? 0;
            if (end > start) durMs = end - start;
        }

        string initTpl = Str(templateHost, "initialization_template") ?? "";
        string segTpl = Str(templateHost, "segment_template") ?? "";
        string baseUrl = templateHost.TryGetProperty("base_urls", out var bus) && bus.ValueKind == JsonValueKind.Array && bus.GetArrayLength() > 0
            ? bus[0].GetString() ?? "" : "";

        // ── encryption_infos: locate the playready entry (its index gates profile selection) + note widevine presence ──
        byte[]? pssh = null, pro = null;
        string? licenseEndpoint = null;
        int? playReadyIndex = null;
        bool hasWidevine = false;
        var encHost = content.TryGetProperty("encryption_infos", out _) ? content : root;
        if (encHost.TryGetProperty("encryption_infos", out var encInfos) && encInfos.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var ei in encInfos.EnumerateArray())
            {
                string? ks = Str(ei, "key_system");
                if (string.Equals(ks, "widevine", StringComparison.Ordinal)) hasWidevine = true;
                if (string.Equals(ks, "playready", StringComparison.Ordinal) && playReadyIndex is null)
                {
                    playReadyIndex = index;
                    if (Str(ei, "encryption_data") is { Length: > 0 } b64)
                    {
                        try { pssh = Convert.FromBase64String(b64); pro = ExtractProFromPssh(pssh); } catch { pssh = null; }
                    }
                    licenseEndpoint = Str(ei, "license_server_endpoint");
                }
                index++;
            }
        }

        // ── profiles: the first mp4 profile matching the playready encryption index; conservative ≤480p ──
        int profileId = 0; string vcodec = ""; int w = 0, h = 0;
        string? cencKid = null, prKid = null;
        bool hasPlayReadyMp4 = false;
        if (content.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            SpotifyVideoManifestProfile? best = null;
            foreach (var p in profiles.EnumerateArray())
            {
                if (!string.Equals(Str(p, "file_type"), "mp4", StringComparison.Ordinal)) continue;
                if (!ProfileMatchesEncryptionIndex(p, playReadyIndex)) continue;
                if (Str(p, "video_codec") is not { Length: > 0 } vc) continue;   // skip audio-only profiles here

                hasPlayReadyMp4 = true;
                cencKid ??= FormatCencKeyId(Str(p, "key_id"));
                prKid ??= FormatPlayReadyKeyId(Str(p, "key_id"));

                int pw = Int(p, "video_width") ?? Int(p, "width") ?? 0;
                int ph = Int(p, "video_height") ?? Int(p, "height") ?? 0;
                int bw = Int(p, "max_bitrate") ?? Int(p, "bandwidth_estimate") ?? Int(p, "video_bitrate") ?? 0;
                int pid = Int(p, "id") ?? 0;
                var cand = new SpotifyVideoManifestProfile(pid, vc, pw, ph, bw);
                best = ChooseConservative(best, cand);
            }
            if (best is { } sel) { profileId = sel.Id; vcodec = sel.Codec; w = sel.Width; h = sel.Height; }
        }

        // ── segment addressing: substitute profile_id + file_type, split at {{segment_timestamp}} ──
        string initUrl = "", segBase = "", segPrefix = "", segSuffix = "";
        int segCount = 0;
        if (baseUrl.Length > 0 && initTpl.Length > 0 && segTpl.Length > 0 && hasPlayReadyMp4)
        {
            initUrl = baseUrl + Subst(initTpl, profileId);
            string media = baseUrl + Subst(segTpl, profileId);
            int tok = media.IndexOf("{{segment_timestamp}}", StringComparison.Ordinal);
            if (tok >= 0)
            {
                string before = media[..tok];
                segSuffix = media[(tok + "{{segment_timestamp}}".Length)..];
                int slash = before.LastIndexOf('/');
                if (slash >= 0) { segBase = before[..(slash + 1)]; segPrefix = before[(slash + 1)..]; }
                else { segBase = ""; segPrefix = before; }
            }
            double durS = durMs / 1000.0;
            segCount = durS > 0 ? (int)Math.Ceiling(durS / segLen) : 0;
        }

        return new SpotifyVideoManifest
        {
            EncodingId = encodingId,
            SegmentLengthSeconds = segLen,
            DurationMs = durMs,
            HasPlayReadyMp4 = hasPlayReadyMp4,
            HasWidevine = hasWidevine,
            ProfileId = profileId,
            VideoCodec = vcodec,
            Width = w,
            Height = h,
            InitUrl = initUrl,
            SegmentBaseUrl = segBase,
            SegmentPrefix = segPrefix,
            SegmentSuffix = segSuffix,
            SegmentStrideSeconds = segLen,
            SegmentCount = segCount,
            Pssh = pssh,
            Pro = pro,
            CencKid = cencKid,
            PlayReadyKid = prKid,
            LicenseServerEndpoint = licenseEndpoint,
        };
    }

    static string Subst(string template, int profileId) => template
        .Replace("{{profile_id}}", profileId.ToString(CultureInfo.InvariantCulture))
        .Replace("{{file_type}}", "mp4");

    static SpotifyVideoManifestProfile? ChooseConservative(SpotifyVideoManifestProfile? best, SpotifyVideoManifestProfile cand)
    {
        if (cand.Width <= 0 || cand.Height <= 0) return best ?? cand;   // keep something even if unsized
        if (cand.Height > 480) return best;                             // start ≤480p (native H.264 conservative)
        if (best is null) return cand;
        if (cand.Height > best.Height || (cand.Height == best.Height && cand.Bandwidth > best.Bandwidth)) return cand;
        return best;
    }

    static bool ProfileMatchesEncryptionIndex(JsonElement profile, int? encryptionIndex)
    {
        if (encryptionIndex is null) return true;
        if (profile.TryGetProperty("encryption_indices", out var indices) && indices.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in indices.EnumerateArray())
                if (i.TryGetInt32(out var v) && v == encryptionIndex.Value) return true;
            return false;
        }
        if (profile.TryGetProperty("encryption_index", out var one) && one.TryGetInt32(out var ov)) return ov == encryptionIndex.Value;
        return true;   // no per-profile index → assume it applies
    }

    // CENC KID: hyphenated GUID string (big-endian display order).
    internal static string? FormatCencKeyId(string? base64KeyId)
    {
        if (string.IsNullOrWhiteSpace(base64KeyId)) return null;
        try
        {
            var b = Convert.FromBase64String(base64KeyId);
            if (b.Length != 16) return null;
            return string.Create(36, b, static (span, kid) =>
            {
                const string hex = "0123456789abcdef";
                int o = 0;
                for (int i = 0; i < kid.Length; i++)
                {
                    if (i is 4 or 6 or 8 or 10) span[o++] = '-';
                    span[o++] = hex[kid[i] >> 4];
                    span[o++] = hex[kid[i] & 0x0F];
                }
            });
        }
        catch { return null; }
    }

    // PlayReady KID: byte-swap the first 8 bytes of the 16-byte CENC KID (mixed-endian GUID), then base64. A wrong swap
    // silently yields no license — ported byte-for-byte from the proven reference.
    internal static string? FormatPlayReadyKeyId(string? base64KeyId)
    {
        if (string.IsNullOrWhiteSpace(base64KeyId)) return null;
        try
        {
            var c = Convert.FromBase64String(base64KeyId);
            if (c.Length != 16) return null;
            var pr = new byte[16];
            pr[0] = c[3]; pr[1] = c[2]; pr[2] = c[1]; pr[3] = c[0];
            pr[4] = c[5]; pr[5] = c[4];
            pr[6] = c[7]; pr[7] = c[6];
            c.AsSpan(8, 8).CopyTo(pr.AsSpan(8));
            return Convert.ToBase64String(pr);
        }
        catch { return null; }
    }

    // Extract the PlayReady Object (PRO) from a CENC PSSH box (v0: …systemId(16)+dataLen(4)+PRO; v1 inserts KID list).
    internal static byte[]? ExtractProFromPssh(byte[] pssh)
    {
        if (pssh.Length < 32) return null;
        try
        {
            if (pssh[4] != (byte)'p' || pssh[5] != (byte)'s' || pssh[6] != (byte)'s' || pssh[7] != (byte)'h') return null;
            byte version = pssh[8];
            int offset = 28;
            if (version > 0)
            {
                if (pssh.Length < offset + 4) return null;
                int kidCount = BinaryPrimitives.ReadInt32BigEndian(pssh.AsSpan(offset, 4));
                offset += 4;
                if (kidCount < 0 || pssh.Length < offset + kidCount * 16 + 4) return null;
                offset += kidCount * 16;
            }
            int proLen = BinaryPrimitives.ReadInt32BigEndian(pssh.AsSpan(offset, 4));
            offset += 4;
            if (proLen <= 0 || offset + proLen > pssh.Length) return null;
            var pro = new byte[proLen];
            pssh.AsSpan(offset, proLen).CopyTo(pro);
            // sanity: a PRO's first 4 bytes are its little-endian total length
            return pro.Length >= 6 && BinaryPrimitives.ReadInt32LittleEndian(pro.AsSpan(0, 4)) == pro.Length ? pro : null;
        }
        catch { return null; }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
    static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : null;
}

sealed record SpotifyVideoManifestProfile(int Id, string Codec, int Width, int Height, int Bandwidth);
