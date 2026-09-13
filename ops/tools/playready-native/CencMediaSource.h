// CencMediaSource.h — the LAST MILE for native PlayReady video: a custom fragmented-MP4 / CENC demuxer + a custom
// IMFMediaSource/IMFMediaStream that emits ENCRYPTED IMFSamples (carrying the CENC per-sample attributes) to the
// media engine's protected pipeline + CDM, exactly like Microsoft's MediaEngineEMEUWPSample `CdmMediaSource`.
//
// Why this exists: the built-in IMFMediaSourceExtension (MSE) rejects protected byte streams
// (MF_E_UNSUPPORTED_BYTESTREAM_TYPE / MF_E_DRM_UNSUPPORTED — see the README/design-doc findings), and a URL/byte-stream
// SetSource hard-wedges the PMP protected pipeline. Microsoft's sample instead demuxes fMP4/CENC IN-APP and hands the
// engine already-encrypted samples with the CENC metadata the CDM needs to decrypt. This is that source.
//
// This header is #included from PrInternal.h AFTER the platform headers (Media Foundation, C++/WinRT) and the log
// entry points (`LogLine`, `fgpr::RaiseLog`) are declared. It pulls SegmentStore.h in right after `namespace cenc`
// closes: the streams' time-window retention, their buffered ranges and CanSeekTo are the store's algorithms applied to
// each stream's sample list. Nothing here depends on the runtime, which is what lets FgPrProbeFile run ParseInit +
// ParseMoof on a box with no CDM, no D3D device and no network.
//
// Scope of the demuxer (H.264 video or AAC audio, ONE track per InitInfo): moov{mvhd, mvex/trex, pssh (PlayReady init
// data), trak{tkhd, edts/elst, mdia(mdhd)/minf/stbl/stsd(encv|avc1|avc3 → avcC | enca|mp4a → esds, + sinf →
// schm(cenc/cbcs)/schi/tenc)}}, and per fragment moof{traf{tfhd, tfdt, trun…, senc | saiz+saio}} with its sample data.

#pragma once

#include <cstdint>
#include <cstring>
#include <vector>
#include <string>
#include <mutex>
#include <functional>
#include <algorithm>
#include <map>        // per-stream ITA cache (a single slot thrashes once there are two streams)
#include <set>        // announced / ended stream ids
#include <iterator>   // make_move_iterator — appending fetched samples without copying them

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Big-endian box reader helpers.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
namespace cenc {

static inline uint32_t rd32(const uint8_t* p) { return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3]; }
static inline uint16_t rd16(const uint8_t* p) { return (uint16_t)(((uint16_t)p[0] << 8) | p[1]); }
static inline uint64_t rd64(const uint8_t* p) { return ((uint64_t)rd32(p) << 32) | rd32(p + 4); }
static inline uint32_t fourcc(const char* s) { return ((uint32_t)(uint8_t)s[0] << 24) | ((uint32_t)(uint8_t)s[1] << 16) | ((uint32_t)(uint8_t)s[2] << 8) | (uint8_t)s[3]; }

struct Box { uint32_t type; const uint8_t* payload; size_t payloadLen; const uint8_t* boxStart; size_t boxLen; };

// Iterate the top-level boxes within [data,data+len). Full-box version/flags are NOT stripped (payload starts right
// after the 8-byte (or 16-byte for 64-bit size) header); callers strip version/flags themselves where needed.
static void ForEachBox(const uint8_t* data, size_t len, const std::function<void(const Box&)>& fn)
{
    size_t off = 0;
    while (off + 8 <= len)
    {
        uint64_t size = rd32(data + off);
        uint32_t type = rd32(data + off + 4);
        size_t hdr = 8;
        if (size == 1) { if (off + 16 > len) break; size = rd64(data + off + 8); hdr = 16; }
        else if (size == 0) { size = len - off; }
        if (size < hdr || off + size > len) break;
        Box b{ type, data + off + hdr, (size_t)(size - hdr), data + off, (size_t)size };
        fn(b);
        off += (size_t)size;
    }
}

// Find the first child box of a given type inside a parent payload; returns false if absent.
static bool FindBox(const uint8_t* data, size_t len, uint32_t type, Box& out)
{
    bool found = false; Box hit{};
    ForEachBox(data, len, [&](const Box& b) { if (!found && b.type == type) { found = true; hit = b; } });
    if (found) out = hit;
    return found;
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Parsed init-segment info.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Which kind of track an InitInfo describes. Spotify addresses every representation as its own single-track file (video
// profile N, audio profile M), but a self-contained fragmented MP4 (the Chromium vectors FgPrProbeFile reads) muxes a
// video and an audio `trak` in ONE moov and interleaves their `traf`s in every `moof`. So one InitInfo is ONE track,
// chosen by ParseInit, and the media parser follows that track's `track_ID` through the fragments.
enum class TrackKind { Video, Audio };

/// Which track ParseInit picks out of a moov. `Any` prefers the first usable video track and falls back to the first
/// usable audio track (what a probe of a muxed file wants); a representation's init segment asks for its own kind.
enum class TrackPick { Any, Video, Audio };

struct InitInfo
{
    TrackKind kind = TrackKind::Video;
    uint32_t trackId = 0;              // tkhd track_ID — which `traf` of a moof belongs to this track (0 = the only one)
    // The edit list's shift, in MEDIA timescale ticks: presentation time = decode time + composition offset - this.
    // An encoder with B-frames delays every composition time by the reorder depth and writes the same amount as the
    // first edit's media_time, so the first frame PRESENTS at 0 (ffmpeg: 2 frames; AAC: its priming samples). Ignoring
    // it put the gate clip's keyframes at 66, 2066, … ms — off the manifest's timeline, which is the one the seek planner
    // and the segment grid (index = ms / segmentLengthMs) are written against.
    int64_t editOffsetTicks = 0;
    // mvex/trex defaults for this track: the fallback when neither tfhd nor trun carries a sample's duration, size or
    // flags (a muxer that states them once per track, as the Chromium vectors do).
    uint32_t defaultSampleDuration = 0, defaultSampleSize = 0, defaultSampleFlags = 0;
    uint32_t codec4cc = 0;             // original sample format (e.g. 'avc1'/'avc3'/'mp4a') from frma, else stsd entry type
    std::vector<uint8_t> avcC;         // raw AVCDecoderConfigurationRecord (from the 'avcC' box)
    std::vector<uint8_t> spspps;       // SPS+PPS as Annex-B (for MF_MT_MPEG_SEQUENCE_HEADER)
    uint32_t width = 0, height = 0;
    // ── audio (mp4a/enca) ───────────────────────────────────────────────────────────────────────────────────────────
    uint32_t channels = 0;             // AudioSampleEntry channelcount
    uint32_t sampleRate = 0;           // AudioSampleEntry samplerate (integer part of the 16.16 fixed-point field)
    uint32_t bitsPerSample = 16;       // AudioSampleEntry samplesize
    uint32_t avgBitrate = 0;           // esds DecoderConfigDescriptor avgBitrate (bits/s), 0 when absent
    std::vector<uint8_t> asc;          // AudioSpecificConfig (esds → DecoderSpecificInfo) — AAC's real configuration
    uint64_t timescale = 90000;        // media timescale (mdhd)
    int scheme = 0;                    // 0 = cenc (AES-CTR), 1 = cbcs (AES-CBC pattern)
    bool encrypted = false;
    uint8_t kid[16] = {};              // tenc default_KID
    uint8_t perSampleIvSize = 8;       // tenc default_Per_Sample_IV_Size (0 => constant IV)
    uint8_t cryptByteBlock = 0, skipByteBlock = 0; // cbcs pattern
    std::vector<uint8_t> constIv;      // tenc default constant IV (when perSampleIvSize == 0)
    std::vector<uint8_t> pssh;         // concatenated pssh box(es) — the "cenc" init data for GenerateRequest
    uint8_t nalLenSize = 4;            // AVC NAL length prefix size (from avcC)
};

// Build SPS/PPS Annex-B from an AVCDecoderConfigurationRecord.
static void ExtractSpsPps(const std::vector<uint8_t>& avcC, InitInfo& info)
{
    if (avcC.size() < 7) return;
    info.nalLenSize = (uint8_t)((avcC[4] & 0x03) + 1);
    size_t p = 5;
    auto emit = [&](const uint8_t* nal, size_t n) {
        static const uint8_t sc[4] = { 0, 0, 0, 1 };
        info.spspps.insert(info.spspps.end(), sc, sc + 4);
        info.spspps.insert(info.spspps.end(), nal, nal + n);
    };
    if (p >= avcC.size()) return;
    int numSps = avcC[p++] & 0x1F;
    for (int i = 0; i < numSps && p + 2 <= avcC.size(); i++)
    {
        uint16_t n = rd16(&avcC[p]); p += 2;
        if (p + n > avcC.size()) return;
        emit(&avcC[p], n); p += n;
    }
    if (p >= avcC.size()) return;
    int numPps = avcC[p++];
    for (int i = 0; i < numPps && p + 2 <= avcC.size(); i++)
    {
        uint16_t n = rd16(&avcC[p]); p += 2;
        if (p + n > avcC.size()) return;
        emit(&avcC[p], n); p += n;
    }
}

// Parse tenc (Track Encryption box) — the CENC defaults.
static void ParseTenc(const Box& tenc, InitInfo& info)
{
    const uint8_t* p = tenc.payload; size_t n = tenc.payloadLen;
    if (n < 8) return;
    uint8_t version = p[0];
    // p[1..3] flags. p[4] reserved. p[5]: (v0) reserved | (v>0) crypt<<4|skip. p[6] default_isProtected. p[7] iv size.
    if (version > 0) { info.cryptByteBlock = p[5] >> 4; info.skipByteBlock = p[5] & 0x0F; }
    info.encrypted = p[6] != 0;
    info.perSampleIvSize = p[7];
    if (n >= 8 + 16) memcpy(info.kid, p + 8, 16);
    size_t off = 8 + 16;
    if (info.perSampleIvSize == 0 && off < n)
    {
        uint8_t civLen = p[off++];
        if (off + civLen <= n) info.constIv.assign(p + off, p + off + civLen);
    }
}

// Parse a VisualSampleEntry (encv/avc1/avc3): width/height + walk child boxes for avcC + sinf.
static void ParseVisualSampleEntry(const Box& entry, InitInfo& info)
{
    const uint8_t* p = entry.payload; size_t n = entry.payloadLen;
    if (n < 78) return;
    info.width = rd16(p + 24);
    info.height = rd16(p + 26);
    // Child boxes begin at offset 78 of the VisualSampleEntry payload.
    const uint8_t* kids = p + 78; size_t klen = n - 78;
    ForEachBox(kids, klen, [&](const Box& b) {
        if (b.type == fourcc("avcC")) { info.avcC.assign(b.payload, b.payload + b.payloadLen); }
        else if (b.type == fourcc("sinf"))
        {
            Box frma, schm, schi, tenc;
            if (FindBox(b.payload, b.payloadLen, fourcc("frma"), frma) && frma.payloadLen >= 4)
                info.codec4cc = rd32(frma.payload);
            if (FindBox(b.payload, b.payloadLen, fourcc("schm"), schm) && schm.payloadLen >= 8)
            {
                uint32_t st = rd32(schm.payload + 4);   // scheme_type (after version/flags)
                info.scheme = (st == fourcc("cbcs") || st == fourcc("cbc1")) ? 1 : 0;
            }
            if (FindBox(b.payload, b.payloadLen, fourcc("schi"), schi))
                if (FindBox(schi.payload, schi.payloadLen, fourcc("tenc"), tenc)) ParseTenc(tenc, info);
        }
    });
}

// Walk an MPEG-4 ES_Descriptor (the 'esds' box payload) down to the DecoderSpecificInfo, which for AAC IS the
// AudioSpecificConfig — the two bytes that tell the decoder the real object type, sample rate and channel configuration.
// Descriptor lengths use the 7-bit continuation encoding (top bit = "another length byte follows"), and getting that
// wrong yields a silently empty config rather than an error, which is why the walk is explicit here.
static void ParseEsds(const Box& esds, InitInfo& info)
{
    const uint8_t* p = esds.payload; size_t n = esds.payloadLen;
    if (n < 5) return;
    size_t o = 4;   // version + flags

    auto readTag = [&](uint8_t& tag, size_t& len) -> bool {
        if (o >= n) return false;
        tag = p[o++];
        len = 0;
        for (int i = 0; i < 4 && o < n; i++)
        {
            uint8_t b = p[o++];
            len = (len << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return o + len <= n || o <= n;   // tolerate a slightly over-declared length
    };

    uint8_t tag = 0; size_t len = 0;
    if (!readTag(tag, len) || tag != 0x03 /*ES_DescrTag*/) return;
    if (o + 3 > n) return;
    uint8_t esFlags = p[o + 2];
    o += 3;                                        // ES_ID(2) + flags(1)
    if (esFlags & 0x80) o += 2;                    // streamDependenceFlag → dependsOn_ES_ID
    if (esFlags & 0x40) { if (o >= n) return; o += 1 + p[o]; }   // URL_Flag → length-prefixed URL
    if (esFlags & 0x20) o += 2;                    // OCRstreamFlag → OCR_ES_Id

    if (!readTag(tag, len) || tag != 0x04 /*DecoderConfigDescrTag*/) return;
    if (o + 13 > n) return;
    info.avgBitrate = rd32(p + o + 9);
    o += 13;                                       // objectType(1) + streamType/bufferSize(4) + max(4) + avg(4)

    if (!readTag(tag, len) || tag != 0x05 /*DecSpecificInfoTag*/) return;
    if (len == 0 || o + len > n) return;
    info.asc.assign(p + o, p + o + len);
}

// Parse an AudioSampleEntry (mp4a/enca). Its fixed header is 28 bytes — HALF the VisualSampleEntry's 78 — and child
// boxes (esds, sinf) begin there; using the visual offset silently reads past the entry and finds nothing.
static void ParseAudioSampleEntry(const Box& entry, InitInfo& info)
{
    const uint8_t* p = entry.payload; size_t n = entry.payloadLen;
    if (n < 28) return;
    // 0..5 reserved, 6..7 data_reference_index, 8..15 reserved,
    // 16..17 channelcount, 18..19 samplesize, 20..21 pre_defined, 22..23 reserved, 24..27 samplerate (16.16 fixed).
    info.channels = rd16(p + 16);
    info.bitsPerSample = rd16(p + 18);
    info.sampleRate = rd16(p + 24);   // integer part; fractional part is always 0 in practice
    const uint8_t* kids = p + 28; size_t klen = n - 28;
    ForEachBox(kids, klen, [&](const Box& b) {
        if (b.type == fourcc("esds")) ParseEsds(b, info);
        else if (b.type == fourcc("wave"))   // QuickTime nesting: esds one level deeper
            ForEachBox(b.payload, b.payloadLen, [&](const Box& w) { if (w.type == fourcc("esds")) ParseEsds(w, info); });
        else if (b.type == fourcc("sinf"))
        {
            Box frma, schm, schi, tenc;
            if (FindBox(b.payload, b.payloadLen, fourcc("frma"), frma) && frma.payloadLen >= 4)
                info.codec4cc = rd32(frma.payload);
            if (FindBox(b.payload, b.payloadLen, fourcc("schm"), schm) && schm.payloadLen >= 8)
            {
                uint32_t st = rd32(schm.payload + 4);
                info.scheme = (st == fourcc("cbcs") || st == fourcc("cbc1")) ? 1 : 0;
            }
            if (FindBox(b.payload, b.payloadLen, fourcc("schi"), schi))
                if (FindBox(schi.payload, schi.payloadLen, fourcc("tenc"), tenc)) ParseTenc(tenc, info);
        }
    });
}

static void ParseStsd(const Box& stsd, InitInfo& info)
{
    // stsd: version/flags(4) + entry_count(4) + entries.
    if (stsd.payloadLen < 8) return;
    const uint8_t* entries = stsd.payload + 8; size_t elen = stsd.payloadLen - 8;
    bool done = false;
    ForEachBox(entries, elen, [&](const Box& b) {
        if (done) return;
        if (b.type == fourcc("encv") || b.type == fourcc("avc1") || b.type == fourcc("avc3"))
        {
            info.kind = TrackKind::Video;
            if (info.codec4cc == 0 && b.type != fourcc("encv")) info.codec4cc = b.type;
            ParseVisualSampleEntry(b, info);
            done = true;
        }
        else if (b.type == fourcc("enca") || b.type == fourcc("mp4a"))
        {
            // The video's OWN soundtrack. 'enca' is the CENC-protected wrapper whose sinf/frma names the real format.
            info.kind = TrackKind::Audio;
            if (info.codec4cc == 0 && b.type != fourcc("enca")) info.codec4cc = b.type;
            ParseAudioSampleEntry(b, info);
            done = true;
        }
    });
}

/// The timescale field of an `mvhd` / `mdhd` payload (version 1 widens creation + modification time to 64 bits), or 0
/// when the box is too short to hold it.
static uint32_t HeaderTimescale(const Box& box)
{
    if (box.payloadLen < 4) return 0;
    const size_t at = box.payload[0] == 1 ? 20 : 12;
    return box.payloadLen >= at + 4 ? rd32(box.payload + at) : 0;
}

/// The edit list's presentation shift for one trak, in media ticks (see InitInfo::editOffsetTicks). Only what a
/// fragmented stream uses is honoured: leading EMPTY edits (media_time = -1, a presentation delay stated in the MOVIE
/// timescale) and the first media edit's start. Every value is saturated — an `elst` is container data from a CDN, and a
/// hostile 64-bit media_time must yield a wrong clock, never an overflow.
static int64_t EditOffsetTicks(const Box& trak, uint32_t movieTimescale, uint64_t mediaTimescale)
{
    Box edts, elst;
    if (!FindBox(trak.payload, trak.payloadLen, fourcc("edts"), edts) ||
        !FindBox(edts.payload, edts.payloadLen, fourcc("elst"), elst) || elst.payloadLen < 8)
        return 0;
    static constexpr int64_t kLimit = (int64_t)1 << 62;
    const uint8_t version = elst.payload[0];
    const uint32_t count = rd32(elst.payload + 4);
    const size_t entrySize = version == 1 ? 20 : 12;
    int64_t delayTicks = 0;
    size_t off = 8;
    for (uint32_t i = 0; i < count && off + entrySize <= elst.payloadLen; i++, off += entrySize)
    {
        const uint8_t* e = elst.payload + off;
        const uint64_t segmentDuration = version == 1 ? rd64(e) : rd32(e);
        const int64_t mediaTime = version == 1 ? (int64_t)rd64(e + 8) : (int64_t)(int32_t)rd32(e + 4);
        if (mediaTime == -1)
        {
            if (movieTimescale == 0) continue;
            const uint64_t whole = segmentDuration / movieTimescale;
            const uint64_t part = segmentDuration % movieTimescale;
            if (whole > (uint64_t)kLimit / (mediaTimescale ? mediaTimescale : 1)) return 0;
            delayTicks += (int64_t)(whole * mediaTimescale + (part * mediaTimescale) / movieTimescale);
            if (delayTicks > kLimit) return 0;
            continue;
        }
        if (mediaTime < 0) return 0;
        return (mediaTime > kLimit ? kLimit : mediaTime) - delayTicks;
    }
    return 0;
}

/// The mvex/trex defaults of one track.
struct TrexDefaults { uint32_t trackId = 0, duration = 0, size = 0, flags = 0; };

/// Parse ONE trak into `info`. True when it is a usable H.264 video or AAC audio track.
static bool ParseTrak(const Box& trak, uint32_t movieTimescale, const std::vector<TrexDefaults>& trex, InitInfo& info)
{
    Box tkhd;
    if (FindBox(trak.payload, trak.payloadLen, fourcc("tkhd"), tkhd) && tkhd.payloadLen >= 4)
    {
        const size_t at = tkhd.payload[0] == 1 ? 20 : 12;   // version/flags + creation + modification
        if (tkhd.payloadLen >= at + 4) info.trackId = rd32(tkhd.payload + at);
    }
    Box mdia, mdhd, minf, stbl, stsd;
    if (!FindBox(trak.payload, trak.payloadLen, fourcc("mdia"), mdia)) return false;
    if (FindBox(mdia.payload, mdia.payloadLen, fourcc("mdhd"), mdhd)) info.timescale = HeaderTimescale(mdhd);
    if (!FindBox(mdia.payload, mdia.payloadLen, fourcc("minf"), minf) ||
        !FindBox(minf.payload, minf.payloadLen, fourcc("stbl"), stbl) ||
        !FindBox(stbl.payload, stbl.payloadLen, fourcc("stsd"), stsd))
        return false;
    ParseStsd(stsd, info);
    // Validate against the kind actually found: a video track needs its avcC, an audio track needs its
    // AudioSpecificConfig + sample rate. Validating audio against the VIDEO rule is how an audio init segment gets
    // reported as "no usable sample entry" even when it parsed perfectly.
    const bool ok = info.kind == TrackKind::Video ? (info.width > 0 && !info.avcC.empty())
                                                  : (info.sampleRate > 0 && !info.asc.empty());
    if (!ok) return false;
    if (info.kind == TrackKind::Video) ExtractSpsPps(info.avcC, info);
    if (info.timescale == 0) info.timescale = info.kind == TrackKind::Audio ? 48000 : 90000;
    info.editOffsetTicks = EditOffsetTicks(trak, movieTimescale, info.timescale);
    for (auto const& t : trex)
    {
        if (t.trackId != info.trackId) continue;
        info.defaultSampleDuration = t.duration;
        info.defaultSampleSize = t.size;
        info.defaultSampleFlags = t.flags;
        break;
    }
    return true;
}

/// Parse an init segment (moov + any pssh) into the ONE track `pick` asks for. Every trak is parsed into its own
/// InitInfo — merging them (what this once did) left a muxed file's video described by its audio trak's sample entry
/// and timescale, so a probe of an av file reported an audio track and no keyframes. `info` is written only on success.
static bool ParseInit(const std::vector<uint8_t>& data, InitInfo& info, TrackPick pick = TrackPick::Any)
{
    Box moov;
    if (!FindBox(data.data(), data.size(), fourcc("moov"), moov)) return false;
    std::vector<uint8_t> pssh;          // moov-level pssh boxes: the PlayReady init data for GenerateRequest
    uint32_t movieTimescale = 0;
    std::vector<TrexDefaults> trex;
    ForEachBox(moov.payload, moov.payloadLen, [&](const Box& b) {
        if (b.type == fourcc("pssh")) pssh.insert(pssh.end(), b.boxStart, b.boxStart + b.boxLen);
        else if (b.type == fourcc("mvhd")) movieTimescale = HeaderTimescale(b);
        else if (b.type == fourcc("mvex"))
            ForEachBox(b.payload, b.payloadLen, [&](const Box& t) {
                // trex: version/flags(4) track_ID(4) default_sample_description_index(4) duration(4) size(4) flags(4)
                if (t.type != fourcc("trex") || t.payloadLen < 24) return;
                trex.push_back(TrexDefaults{ rd32(t.payload + 4), rd32(t.payload + 12), rd32(t.payload + 16), rd32(t.payload + 20) });
            });
    });

    bool found = false, haveFallback = false;
    InitInfo chosen, fallback;
    ForEachBox(moov.payload, moov.payloadLen, [&](const Box& trak) {
        if (found || trak.type != fourcc("trak")) return;
        InitInfo candidate;
        if (!ParseTrak(trak, movieTimescale, trex, candidate)) return;
        const bool video = candidate.kind == TrackKind::Video;
        if ((pick == TrackPick::Video && !video) || (pick == TrackPick::Audio && video)) return;
        if (pick == TrackPick::Any && !video)
        {
            if (!haveFallback) { fallback = std::move(candidate); haveFallback = true; }
            return;
        }
        chosen = std::move(candidate);
        found = true;
    });
    if (!found && haveFallback) { chosen = std::move(fallback); found = true; }
    if (!found) return false;
    chosen.pssh = std::move(pssh);
    info = std::move(chosen);
    return true;
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Parsed sample (one access unit) with its CENC metadata.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
struct Subsample { uint32_t clearBytes; uint32_t encBytes; };
struct Sample
{
    std::vector<uint8_t> data;                 // Annex-B H.264; converted byte-for-byte from MP4/AVCC before delivery
    std::vector<uint8_t> iv;                   // CENC IV, preserved at the tenc-declared size (normally 8 bytes)
    std::vector<Subsample> subsamples;         // clear/encrypted byte runs (empty => whole-sample encrypted)
    uint64_t timeTicks = 0;                    // presentation time in media timescale ticks
    uint64_t decodeTicks = 0;                  // decode time in media timescale ticks (DTS; differs for B frames)
    uint64_t durTicks = 0;
    bool keyframe = false;
    bool encrypted = false;
};

// Media Foundation's H.264 decoder consumes Annex-B access units (start-code-prefixed NALs), while ISO BMFF stores
// AVC samples as AVCC (length-prefixed NALs). For the common four-byte MP4 NAL length field the conversion is exactly
// size preserving: replace each big-endian length with 00 00 00 01. That is crucial for CENC because the IV and the
// clear/encrypted subsample mapping describe byte offsets in this same buffer; inserting/removing bytes would corrupt
// those ranges. CENC video leaves the NAL length fields clear, so they remain readable before decryption.
static bool ConvertAvccToAnnexBInPlace(Sample& sample, uint8_t nalLenSize)
{
    if (nalLenSize != 4) return false; // This test vector (and Spotify AVC) use four-byte NAL lengths.

    auto rangeIsClear = [&](size_t begin, size_t len) {
        if (!sample.encrypted) return true;
        size_t cursor = 0;
        for (auto const& ss : sample.subsamples)
        {
            if (begin >= cursor && begin + len <= cursor + ss.clearBytes) return true;
            cursor += (size_t)ss.clearBytes + ss.encBytes;
        }
        return false;
    };

    size_t off = 0;
    while (off + 4 <= sample.data.size())
    {
        if (!rangeIsClear(off, 4)) return false;
        uint32_t nalBytes = rd32(sample.data.data() + off);
        if (nalBytes == 0 || (uint64_t)off + 4 + nalBytes > sample.data.size()) return false;
        sample.data[off + 0] = 0;
        sample.data[off + 1] = 0;
        sample.data[off + 2] = 0;
        sample.data[off + 3] = 1;
        off += 4 + nalBytes;
    }
    return off == sample.data.size();
}

/// One sample's CENC auxiliary information: its IV and its clear/encrypted byte runs.
struct AuxEntry { std::vector<uint8_t> iv; std::vector<Subsample> subs; };

/// Read one sample's aux entry at `p` (advancing it): `ivSize` IV bytes, then — when `withSubsamples` — a u16 run count
/// and that many {u16 clear, u32 encrypted} runs. False when the entry does not fit before `end`.
static bool ReadAuxEntry(const uint8_t*& p, const uint8_t* end, uint8_t ivSize, bool withSubsamples, AuxEntry& e)
{
    if ((size_t)(end - p) < ivSize) return false;
    e.iv.assign(p, p + ivSize);
    p += ivSize;
    if (!withSubsamples) return true;
    if (end - p < 2) return false;
    const uint16_t runs = rd16(p);
    p += 2;
    if ((size_t)(end - p) < (size_t)runs * 6) return false;
    e.subs.reserve(runs);
    for (uint16_t r = 0; r < runs; r++, p += 6) e.subs.push_back(Subsample{ rd16(p), rd32(p + 2) });
    return true;
}

/// The traf's per-sample encryption data. `senc` (inline, what every DASH packager writes) wins; otherwise the
/// `saiz` sizes + `saio` offsets point at the same records elsewhere in the file (the older CENC layout, and what
/// Chromium's `bear-*-cenc` vectors use). `base` is the traf's data base (saio offsets are relative to it, as data offsets
/// are). Unreadable aux data yields fewer entries, never a read past the buffer: the samples it misses are delivered with
/// the tenc defaults and the decryptor, not this parser, reports them.
static void ReadTrafAuxInfo(const uint8_t* file, size_t fileLen, const Box& traf, size_t base, const InitInfo& info,
                            size_t sampleCount, std::vector<AuxEntry>& aux)
{
    const uint8_t ivSize = info.perSampleIvSize;
    Box senc;
    if (FindBox(traf.payload, traf.payloadLen, fourcc("senc"), senc) && senc.payloadLen >= 8)
    {
        const bool withSubsamples = (rd32(senc.payload) & 0x000002) != 0;
        const uint32_t count = rd32(senc.payload + 4);
        const uint8_t* p = senc.payload + 8;
        const uint8_t* end = senc.payload + senc.payloadLen;
        for (uint32_t i = 0; i < count && i < sampleCount; i++)
        {
            AuxEntry e;
            if (!ReadAuxEntry(p, end, ivSize, withSubsamples, e)) break;
            aux.push_back(std::move(e));
        }
        return;
    }

    Box saiz, saio;
    if (!FindBox(traf.payload, traf.payloadLen, fourcc("saiz"), saiz) || saiz.payloadLen < 9 ||
        !FindBox(traf.payload, traf.payloadLen, fourcc("saio"), saio) || saio.payloadLen < 8)
        return;
    // saiz: version/flags(4) [aux_info_type(4) aux_info_type_parameter(4) when flags & 1] default_size(1) count(4) [sizes]
    const uint8_t* zp = saiz.payload + 4;
    const uint8_t* zend = saiz.payload + saiz.payloadLen;
    if (rd32(saiz.payload) & 0x000001) zp += 8;
    if (zend - zp < 5) return;
    const uint8_t defaultSize = zp[0];
    uint32_t count = rd32(zp + 1);
    zp += 5;
    if (defaultSize == 0 && (size_t)(zend - zp) < count) count = (uint32_t)(zend - zp);
    // saio: version/flags(4) [type(4) parameter(4) when flags & 1] entry_count(4) offsets (u32, or u64 for version 1)
    const uint8_t* op = saio.payload + 4;
    const uint8_t* oend = saio.payload + saio.payloadLen;
    if (rd32(saio.payload) & 0x000001) op += 8;
    if (oend - op < 4) return;
    const uint32_t offsets = rd32(op);
    op += 4;
    const size_t offsetSize = saio.payload[0] == 1 ? 8 : 4;
    if (offsets != 1 || (size_t)(oend - op) < offsetSize) return;   // one contiguous run per traf: every packager's layout
    const uint64_t rel = offsetSize == 8 ? rd64(op) : rd32(op);
    if (rel > fileLen || base > fileLen - rel) return;
    const uint8_t* p = file + base + rel;
    const uint8_t* end = file + fileLen;
    for (uint32_t i = 0; i < count && i < sampleCount; i++)
    {
        const uint8_t size = defaultSize ? defaultSize : zp[i];
        if ((size_t)(end - p) < size) break;
        const uint8_t* recordEnd = p + size;
        AuxEntry e;
        if (!ReadAuxEntry(p, recordEnd, ivSize, size > ivSize, e)) break;
        p = recordEnd;
        aux.push_back(std::move(e));
    }
}

// Parse ONE movie fragment (a `moof` and the sample data its runs point at, anywhere in `file`) and append this track's
// samples. runningDecodeTicks tracks decode time across fragments (tfdt resets it). `outKeyframeMs`, when supplied,
// receives the presentation time (ms) of every VIDEO sync sample — the per-session keyframe table (SegmentStore.h) the
// seek planner reads through FgPrSessionGetKeyframes.
//
// What a fragment can hold and this reads (ISO/IEC 14496-12 §8.8):
//   * several `traf`s — a muxed file interleaves its tracks in one moof; this track's is the one whose tfhd track_ID
//     matches the init's tkhd (the only traf when the init did not say, or the representation renumbered its track);
//   * several `trun`s per traf — each continues the previous one's data unless it states its own data_offset;
//   * sample duration / size / flags from the trun, else the tfhd defaults, else the init's trex defaults;
//   * the data base: tfhd base_data_offset (file-relative) when present, else the moof's first byte for EVERY traf (the
//     reading every muxer writes and Chromium parses; default-base-is-moof says the same thing explicitly);
//   * encryption records inline (`senc`) or through `saiz`/`saio`.
// Every count and offset is container data from a CDN: each one is bounded by the bytes that actually hold it.
static int ParseMoof(const uint8_t* file, size_t fileLen, const Box& moof, const InitInfo& info, std::vector<Sample>& out,
                     uint64_t& runningDecodeTicks, std::vector<int64_t>* outKeyframeMs)
{
    const size_t moofAbs = (size_t)(moof.boxStart - file);

    Box traf{}, firstTraf{};
    bool haveTraf = false;
    int trafCount = 0;
    ForEachBox(moof.payload, moof.payloadLen, [&](const Box& b) {
        if (b.type != fourcc("traf")) return;
        if (trafCount++ == 0) firstTraf = b;
        if (haveTraf) return;
        Box tfhd;
        if (!FindBox(b.payload, b.payloadLen, fourcc("tfhd"), tfhd) || tfhd.payloadLen < 8) return;
        if (info.trackId == 0 || rd32(tfhd.payload + 4) == info.trackId) { traf = b; haveTraf = true; }
    });
    if (!haveTraf)
    {
        if (trafCount != 1) return 0;
        traf = firstTraf;
    }

    // tfhd — defaults + the data base.
    uint32_t defSampleDur = info.defaultSampleDuration, defSampleSize = info.defaultSampleSize,
             defSampleFlags = info.defaultSampleFlags;
    size_t base = moofAbs;
    Box tfhd;
    if (FindBox(traf.payload, traf.payloadLen, fourcc("tfhd"), tfhd) && tfhd.payloadLen >= 8)
    {
        const uint32_t flags = rd32(tfhd.payload) & 0x00FFFFFF;
        const uint8_t* p = tfhd.payload + 8;   // version/flags(4) + track_ID(4)
        const uint8_t* end = tfhd.payload + tfhd.payloadLen;
        auto take = [&](size_t n) { if ((size_t)(end - p) < n) return false; p += n; return true; };
        if (flags & 0x000001)
        {
            if (!take(8)) return 0;
            const uint64_t o = rd64(p - 8);
            if (o >= fileLen) return 0;
            base = (size_t)o;
        }
        if ((flags & 0x000002) && !take(4)) return 0;                                   // sample_description_index
        if (flags & 0x000008) { if (!take(4)) return 0; defSampleDur = rd32(p - 4); }
        if (flags & 0x000010) { if (!take(4)) return 0; defSampleSize = rd32(p - 4); }
        if (flags & 0x000020) { if (!take(4)) return 0; defSampleFlags = rd32(p - 4); }
    }

    // tfdt — the base media decode time of this fragment (optional: without it decode time continues).
    Box tfdt;
    if (FindBox(traf.payload, traf.payloadLen, fourcc("tfdt"), tfdt) && tfdt.payloadLen >= 8)
    {
        if (tfdt.payload[0] == 1) { if (tfdt.payloadLen >= 12) runningDecodeTicks = rd64(tfdt.payload + 4); }
        else runningDecodeTicks = rd32(tfdt.payload + 4);
    }

    // How many samples the runs declare (bounded by the bytes that carry them), so the aux reader never over-reads.
    size_t declared = 0;
    ForEachBox(traf.payload, traf.payloadLen, [&](const Box& b) {
        if (b.type == fourcc("trun") && b.payloadLen >= 8) declared += rd32(b.payload + 4);
    });
    std::vector<AuxEntry> aux;
    if (info.encrypted) ReadTrafAuxInfo(file, fileLen, traf, base, info, declared, aux);

    static constexpr int64_t kTickLimit = (int64_t)1 << 62;
    int produced = 0;
    size_t cursor = base;
    size_t sampleIndex = 0;
    bool firstRun = true, stop = false;
    ForEachBox(traf.payload, traf.payloadLen, [&](const Box& trun) {
        if (stop || trun.type != fourcc("trun") || trun.payloadLen < 8) return;
        const uint8_t version = trun.payload[0];
        const uint32_t trFlags = rd32(trun.payload) & 0x00FFFFFF;
        uint32_t sampleCount = rd32(trun.payload + 4);
        const uint8_t* tp = trun.payload + 8;
        const uint8_t* tend = trun.payload + trun.payloadLen;
        if (trFlags & 0x000001)
        {
            if (tend - tp < 4) { stop = true; return; }
            const int64_t at = (int64_t)base + (int64_t)(int32_t)rd32(tp);
            tp += 4;
            if (at < 0 || (uint64_t)at > fileLen) { stop = true; return; }
            cursor = (size_t)at;
        }
        else if (firstRun)
        {
            cursor = base;
        }
        firstRun = false;
        uint32_t firstSampleFlags = 0;
        bool haveFirstFlags = false;
        if (trFlags & 0x000004)
        {
            if (tend - tp < 4) { stop = true; return; }
            firstSampleFlags = rd32(tp);
            tp += 4;
            haveFirstFlags = true;
        }
        const size_t perSample = ((trFlags & 0x100) ? 4u : 0u) + ((trFlags & 0x200) ? 4u : 0u) +
                                 ((trFlags & 0x400) ? 4u : 0u) + ((trFlags & 0x800) ? 4u : 0u);
        if (perSample > 0 && sampleCount > (size_t)(tend - tp) / perSample) sampleCount = (uint32_t)((size_t)(tend - tp) / perSample);

        for (uint32_t i = 0; i < sampleCount; i++)
        {
            uint32_t sz = defSampleSize, dur = defSampleDur, flags = defSampleFlags;
            // Per-sample fields in order: duration, size, flags, composition offset (unsigned in version 0).
            if (trFlags & 0x000100) { dur = rd32(tp); tp += 4; }
            if (trFlags & 0x000200) { sz = rd32(tp); tp += 4; }
            if (trFlags & 0x000400) { flags = rd32(tp); tp += 4; }
            int64_t cto = 0;
            if (trFlags & 0x000800) { const uint32_t raw = rd32(tp); tp += 4; cto = version == 0 ? (int64_t)raw : (int64_t)(int32_t)raw; }
            if (i == 0 && haveFirstFlags) flags = firstSampleFlags;
            const size_t auxIndex = sampleIndex++;

            if (sz > fileLen || cursor > fileLen - sz) { stop = true; return; }
            const size_t dataAt = cursor;
            cursor += sz;
            const uint64_t decodeTicks = runningDecodeTicks;
            runningDecodeTicks += dur;

            // PRESENTATION time on the manifest's timeline: decode + composition offset - the edit list's shift.
            int64_t pts = (int64_t)(decodeTicks > (uint64_t)kTickLimit ? (uint64_t)kTickLimit : decodeTicks) + cto -
                          info.editOffsetTicks;
            // An audio sample that ends at or before 0 is outside the edit (AAC priming): it is not part of the
            // presentation, and delivering it at 0 would put two access units on one timestamp.
            if (pts < 0 && info.kind == TrackKind::Audio && pts + (int64_t)dur <= 0) continue;
            if (pts < 0) pts = 0;

            Sample s;
            s.data.assign(file + dataAt, file + dataAt + sz);
            s.durTicks = dur;
            s.decodeTicks = decodeTicks;
            s.timeTicks = (uint64_t)pts;
            // Every AAC access unit is independently decodable, so an audio track is all sync samples regardless of what
            // the flags happen to say (some muxers set the non-sync bit on audio, which would leave a seek with no
            // reposition target and — worse — make the first delivered sample look like a non-clean point).
            s.keyframe = info.kind == TrackKind::Audio || (flags & 0x00010000) == 0;
            s.encrypted = info.encrypted;

            // IV: per-sample from the aux info, else the constant IV (cbcs). Preserve the declared byte length exactly:
            // MFSampleExtension_Encryption_SampleID expects m_bIVSize bytes; padding an 8-byte CENC IV to 16 changes the
            // counter block interpreted by the PlayReady decryptor and leaves the decoder with ciphertext.
            if (auxIndex < aux.size() && !aux[auxIndex].iv.empty()) s.iv = aux[auxIndex].iv;
            else if (!info.constIv.empty()) s.iv = info.constIv;
            if (auxIndex < aux.size()) s.subsamples = aux[auxIndex].subs;

            // ── AVC-ONLY transforms. An AAC access unit is already exactly what the decoder wants: no length prefixes to
            // rewrite and no parameter sets to prepend (its configuration travels out-of-band in the media type's
            // AudioSpecificConfig). Running either transform over audio would corrupt the payload AND desynchronise the
            // CENC subsample mapping, so both are gated on the track kind rather than on "did it happen to parse".
            if (info.kind == TrackKind::Audio)
            {
                out.push_back(std::move(s));
                produced++;
                continue;
            }

            // The decoder accepts Annex-B, not the MP4/AVCC payload stored in mdat. Four-byte replacement preserves every
            // CENC byte offset. Refuse malformed/unsupported samples rather than delivering a packet the decoder can only
            // report later as the opaque MF_E_INVALIDREQUEST (0xC00D36B2).
            if (!ConvertAvccToAnnexBInPlace(s, info.nalLenSize))
            {
                LogLine("[cenc] AVCC->AnnexB failed sample=" + std::to_string(i) +
                        " nalLenSize=" + std::to_string(info.nalLenSize) +
                        " bytes=" + std::to_string(s.data.size()) +
                        " subs=" + std::to_string(s.subsamples.size()));
                stop = true;
                return;
            }

            // Annex-B keyframes must carry their parameter sets IN-BAND: avc1 samples reference SPS/PPS only via the
            // container's avcC, and after the byte-stream conversion the decoder never sees them (the first NAL here is
            // typically an SEI) — MF_MT_MPEG_SEQUENCE_HEADER alone does not save the protected pipeline, which fails the
            // very first sample with MF_E_INVALIDREQUEST. Firefox's proven desktop MFCDM path prepends the Annex-B
            // SPS/PPS to every keyframe and widens the FIRST CLEAR subsample by the prepended length so the CENC byte
            // mapping still describes the same ciphertext (gecko AnnexB::ConvertAVCCSampleToAnnexB, aAddSPS).
            if (s.keyframe && !info.spspps.empty())
            {
                s.data.insert(s.data.begin(), info.spspps.begin(), info.spspps.end());
                if (s.encrypted)
                {
                    if (s.subsamples.empty())
                    {
                        Subsample ss;
                        ss.clearBytes = (uint32_t)info.spspps.size();
                        ss.encBytes = (uint32_t)(s.data.size() - info.spspps.size());
                        s.subsamples.push_back(ss);
                    }
                    else
                    {
                        s.subsamples[0].clearBytes += (uint32_t)info.spspps.size();
                    }
                }
            }

            if (outKeyframeMs && s.keyframe && info.timescale)
                outKeyframeMs->push_back((int64_t)((s.timeTicks * 1000ULL) / info.timescale));
            out.push_back(std::move(s));
            produced++;
        }
    });
    return produced;
}

// Parse one media segment and append this track's samples: every `moof` in it (a packager may cut a segment into
// several fragments), each against the segment's own bytes.
static int ParseSegment(const std::vector<uint8_t>& seg, const InitInfo& info, std::vector<Sample>& out, uint64_t& runningDecodeTicks,
                        std::vector<int64_t>* outKeyframeMs = nullptr)
{
    int produced = 0;
    ForEachBox(seg.data(), seg.size(), [&](const Box& b) {
        if (b.type == fourcc("moof")) produced += ParseMoof(seg.data(), seg.size(), b, info, out, runningDecodeTicks, outKeyframeMs);
    });
    return produced;
}

} // namespace cenc

// The session store's algorithms (time-window trim, buffered ranges, CanSeekTo, the keyframe table) are written against
// cenc::Sample, which is complete from here on.
#include "SegmentStore.h"

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Custom IMFMediaStream — serves the demuxed encrypted samples with CENC per-sample attributes.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
struct CencMediaSource;   // fwd

struct CencMediaStream : winrt::implements<CencMediaStream, IMFMediaStream>
{
    winrt::com_ptr<IMFMediaEventQueue> m_queue;
    winrt::com_ptr<IMFStreamDescriptor> m_sd;
    IMFMediaSource* m_source = nullptr;   // weak (the source owns this stream)
    std::vector<cenc::Sample> m_samples;
    cenc::InitInfo m_info;
    size_t m_next = 0;
    DWORD m_streamId = 1;                 // 1 = video, 2 = audio (matches the stream descriptors in BuildCencSource)
    const char* m_label = "video";        // log prefix only
    bool m_started = false, m_paused = false, m_eos = false, m_discontinuity = true, m_shutdown = false;
    // Incremental feeding: the fetcher appends segments while playback runs, so the first frame does not wait for the
    // whole track to download. Until m_complete is set, running out of samples is STARVATION (park the request and
    // satisfy it when more arrive), not end-of-stream — reporting EOS there would truncate the track to its prefix.
    bool m_complete = false;
    std::vector<winrt::com_ptr<::IUnknown>> m_pausedRequests;
    std::vector<winrt::com_ptr<::IUnknown>> m_starvedRequests;
    std::mutex m_mx;

    // ── the session this stream serves (PrSession.cpp) ──────────────────────────────────────────────────────────────
    uint64_t m_session = 0;                          // stamps every diagnostic line with its FgPrSession handle
    std::shared_ptr<fgpr::SegmentStore> m_store;     // the time window, the byte budget, the keyframe table
    uint64_t m_bytes = 0;                            // what m_samples costs the budget (m_mx); mirrored into the store
    // The feeder's demand hook, invoked under m_mx when delivery drains the forward window below bufferAheadMs. It only
    // sets a flag on a LEAF lock and notifies (Session::Kick) — which is what replaces the old 50 ms backpressure sleep:
    // the feeder is woken by the playhead consuming the buffer, never by a timer.
    std::function<void()> m_demand;
    // Below how many forward ms the hook fires; 0 = the store's bufferAheadMs. The feeder LOWERS it (by one segment)
    // when it could not fetch — over its byte budget, or backing off a failed GET — so a playhead that keeps delivering
    // samples does not wake it for every single one of them while nothing can change.
    std::atomic<int64_t> m_demandBelowMs{ 0 };

    CencMediaStream() { winrt::check_hresult(MFCreateEventQueue(m_queue.put())); }

    /// Every diagnostic line from this stream carries its session handle (shadows the runtime-level ::LogLine).
    void LogLine(const std::string& s) const { fgpr::RaiseLog(m_session, s); }

    // IMFMediaEventGenerator (delegate to the queue).
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* c, ::IUnknown* s) noexcept override { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->BeginGetEvent(c, s); }
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* r, IMFMediaEvent** e) noexcept override { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->EndGetEvent(r, e); }
    IFACEMETHODIMP GetEvent(DWORD f, IMFMediaEvent** e) noexcept override { winrt::com_ptr<IMFMediaEventQueue> q; { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; q = m_queue; } return q->GetEvent(f, e); }
    IFACEMETHODIMP QueueEvent(MediaEventType t, REFGUID g, HRESULT s, const PROPVARIANT* v) noexcept override { std::lock_guard<std::mutex> gd(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->QueueEventParamVar(t, g, s, v); }

    // IMFMediaStream
    IFACEMETHODIMP GetMediaSource(IMFMediaSource** ppSource) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!ppSource) return E_POINTER;
        if (!m_source) return MF_E_NOT_INITIALIZED;
        m_source->AddRef(); *ppSource = m_source; return S_OK;
    }
    IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** ppSD) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!ppSD) return E_POINTER;
        m_sd.copy_to(ppSD); return S_OK;
    }
    IFACEMETHODIMP RequestSample(::IUnknown* pToken) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!m_started) return MF_E_MEDIA_SOURCE_WRONGSTATE;
        // A paused source must ACCEPT sample requests but deliver nothing until the next Start (IMFMediaSource::Pause
        // contract). Retain the token so resume can satisfy the exact request instead of depending on a re-request.
        if (m_paused)
        {
            winrt::com_ptr<::IUnknown> token;
            if (pToken) token.copy_from(pToken);
            m_pausedRequests.push_back(std::move(token));
            return S_OK;
        }
        return DeliverSampleLocked(pToken);
    }

    HRESULT DeliverSampleLocked(::IUnknown* pToken)
    {
        if (m_next >= m_samples.size())
        {
            if (!m_complete)
            {
                // Starved, not finished: park the request. AppendSamples releases it the moment the next segment lands.
                winrt::com_ptr<::IUnknown> token;
                if (pToken) token.copy_from(pToken);
                m_starvedRequests.push_back(std::move(token));
                if (m_starvedRequests.size() == 1)
                    LogLine(std::string("[cenc-src] ") + m_label + " starved at sample " + std::to_string(m_next) +
                            " — awaiting fetch (not EOS)");
                return S_OK;
            }
            if (!m_eos) { m_eos = true; m_queue->QueueEventParamVar(MEEndOfStream, GUID_NULL, S_OK, nullptr); NotifySourceEnded(); }
            return S_OK;
        }
        if (m_next == 0)
        {
            auto const& first = m_samples[0];
            uint64_t clearTotal = 0, encryptedTotal = 0;
            for (auto const& part : first.subsamples)
            {
                clearTotal += part.clearBytes;
                encryptedTotal += part.encBytes;
            }
            LogLine("[cenc-src] sample#0 bytes=" + std::to_string(first.data.size()) +
                    " kf=" + std::to_string(first.keyframe ? 1 : 0) +
                    " iv=" + std::to_string(first.iv.size()) + "B subs=" +
                    std::to_string(first.subsamples.size()) + " clear=" +
                    std::to_string(clearTotal) + " encrypted=" + std::to_string(encryptedTotal) +
                    (first.data.size() >= 8
                        ? " head=" + std::to_string(first.data[0]) + "," + std::to_string(first.data[1]) +
                          "," + std::to_string(first.data[2]) + "," + std::to_string(first.data[3]) +
                          "," + std::to_string(first.data[4]) + "," + std::to_string(first.data[5]) +
                          "," + std::to_string(first.data[6]) + "," + std::to_string(first.data[7])
                        : " head=<short>"));
        }
        winrt::com_ptr<IMFSample> sample;
        HRESULT hr = MakeSample(m_samples[m_next], sample.put());
        if (FAILED(hr)) return hr;
        if (pToken) sample->SetUnknown(MFSampleExtension_Token, pToken);
        if (m_discontinuity)
        {
            sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE);
            m_discontinuity = false;
        }
        if (m_next == 0 || (m_next % 100) == 0) LogLine("[cenc-src] RequestSample #" + std::to_string(m_next) + " (encrypted sample delivered)");
        m_next++;
        hr = m_queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.get());
        // Demand-driven feeding: the playhead consuming the forward window is what wakes the feeder — only while more
        // can still arrive (a complete track has nothing left to fetch) and only once below the forward target.
        if (m_demand && !m_complete && m_store)
        {
            const int64_t below = m_demandBelowMs.load(std::memory_order_relaxed);
            if (AheadDurationMsLocked() < (below > 0 ? below : m_store->bufferAheadMs)) m_demand();
        }
        return hr;
    }

    // Complete one Start operation on the stream. An explicit position repositions to the nearest keyframe at or
    // before the requested presentation time; Media Foundation discards the decoded preroll before the exact target.
    // A resume passes VT_EMPTY and deliberately preserves m_next/decoder history.
    void Start(const PROPVARIANT* startPos, bool seeking, bool reposition)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (reposition)
        {
            LONGLONG requested100ns = startPos && startPos->vt == VT_I8
                ? std::max<LONGLONG>(0, startPos->hVal.QuadPart)
                : 0;
            uint64_t targetTicks = 0;
            if (m_info.timescale > 0)
            {
                uint64_t wholeSeconds = (uint64_t)requested100ns / 10000000ULL;
                uint64_t remainder100ns = (uint64_t)requested100ns % 10000000ULL;
                targetTicks = wholeSeconds * m_info.timescale +
                              (remainder100ns * m_info.timescale) / 10000000ULL;
            }

            size_t best = 0;
            uint64_t bestTime = 0;
            bool found = false;
            for (size_t i = 0; i < m_samples.size(); i++)
            {
                auto const& sample = m_samples[i];
                if (!sample.keyframe || sample.timeTicks > targetTicks) continue;
                if (!found || sample.timeTicks >= bestTime)
                {
                    best = i;
                    bestTime = sample.timeTicks;
                    found = true;
                }
            }
            m_next = found ? best : 0;
            m_discontinuity = true;
            LogLine("[cenc-src] seek target100ns=" + std::to_string((long long)requested100ns) +
                    " -> sample=" + std::to_string(m_next) +
                    " keyframe100ns=" + std::to_string(m_info.timescale > 0
                        ? (long long)((bestTime * 10000000ULL) / m_info.timescale) : 0));
        }
        m_started = true; m_paused = false; m_eos = false;
        m_queue->QueueEventParamVar(seeking ? MEStreamSeeked : MEStreamStarted, GUID_NULL, S_OK, startPos);
        // Requests accepted during Pause are released only AFTER MEStreamStarted, which is the exact boundary after
        // which the stream may resume data delivery. Deliver under the same stream lock to preserve request order.
        for (auto const& token : m_pausedRequests) DeliverSampleLocked(token.get());
        m_pausedRequests.clear();
        ReleaseStarvedLocked();   // a request parked while stopped/paused becomes deliverable again here
    }
    void Pause()
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (!m_started || m_paused) return;
        m_paused = true;
        m_queue->QueueEventParamVar(MEStreamPaused, GUID_NULL, S_OK, nullptr);
    }
    void Stop()
    {
        std::lock_guard<std::mutex> g(m_mx);
        m_started = false; m_paused = false;
        m_next = 0; m_eos = false; m_discontinuity = true;
        m_pausedRequests.clear();
        m_starvedRequests.clear();
        m_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }

    /// How much already-delivered history stays resident behind the playhead used to be `kRetainBehind = 300` SAMPLES
    /// (~10 s of 30 fps video, ~6 s of AAC — two different windows for two tracks of one presentation). It is now the
    /// session's `retainBehindMs` in presentation TIME, capped by its byte budget (SegmentStore.h TrimBehindByTime).
    /// Without a bound the vector grows for the whole track AND for every seek (the memory half of the feeder wedge).
    /// What is retained is also exactly what a short backward scrub can be served from without a refetch.

    /// Insert a freshly demuxed run so the buffer stays MONOTONIC in presentation time.
    ///
    /// The feeder rewinds on a seek and on a representation switch, so an incoming run can overlap what is already
    /// buffered. Appending it at the tail (what this once did) left the vector non-monotonic and duplicated:
    /// Ahead()/AheadDurationMs() then over-reported and the feeder parked in its backpressure sleep. The previous fix cut
    /// everything from the first sample at or after the run's start (searching from m_next) to the END and appended — which
    /// was monotonic only while the run lay AHEAD of the playhead. A seek BACKWARD into a range the time window had not
    /// retained put the run after history that is later in time, and the time-window trim, the buffered ranges and
    /// CanSeekTo all read an ascending vector. So the run now lands at its own presentation time: the overlapped samples
    /// [lo, hi) are replaced in place and everything past the run survives (it is still valid media ahead).
    ///
    /// NEVER re-deliver what Media Foundation already has: a run that straddles the delivery point is spliced from
    /// m_next forward (the original rule); a run wholly behind it only refreshes history and m_next keeps pointing at the
    /// same logical sample. `truncateAfter` restores the cut-to-end for a representation switch, whose samples past the
    /// splice are the OLD representation and must be refetched in the new one. Returns the index the run landed at.
    size_t SpliceLocked(std::vector<cenc::Sample>&& incoming, bool truncateAfter = false)
    {
        const uint64_t startTicks = incoming.front().timeTicks;
        uint64_t endTicks = startTicks;
        uint64_t incomingBytes = 0;
        for (auto const& s : incoming)
        {
            if (s.timeTicks + s.durTicks > endTicks) endTicks = s.timeTicks + s.durTicks;
            incomingBytes += fgpr::SampleFootprint(s);
        }

        size_t lo = m_samples.size();
        for (size_t i = 0; i < m_samples.size(); i++)
            if (m_samples[i].timeTicks >= startTicks) { lo = i; break; }
        size_t hi = lo;
        while (hi < m_samples.size() && m_samples[hi].timeTicks < endTicks) hi++;

        if (lo < m_next && hi > m_next)
        {
            lo = m_next;
            while (lo < m_samples.size() && m_samples[lo].timeTicks < startTicks) lo++;
            hi = lo;
            while (hi < m_samples.size() && m_samples[hi].timeTicks < endTicks) hi++;
        }
        if (truncateAfter && lo >= m_next) hi = m_samples.size();

        uint64_t freed = 0;
        for (size_t i = lo; i < hi; i++) freed += fgpr::SampleFootprint(m_samples[i]);
        const size_t removed = hi - lo;
        const size_t added = incoming.size();
        m_samples.erase(m_samples.begin() + (ptrdiff_t)lo, m_samples.begin() + (ptrdiff_t)hi);
        m_samples.insert(m_samples.begin() + (ptrdiff_t)lo, std::make_move_iterator(incoming.begin()),
                         std::make_move_iterator(incoming.end()));
        if (lo < m_next) m_next = m_next - removed + added;   // wholly behind the playhead: same logical sample
        m_bytes = (m_bytes > freed ? m_bytes - freed : 0) + incomingBytes;
        PublishBytesLocked();
        return lo;
    }

    /// Drop history beyond the retention window (time, then bytes), keeping m_next pointing at the same sample.
    void TrimBehindLocked()
    {
        if (!m_store) return;
        const uint64_t budget = m_streamId == 1 ? m_store->VideoBudget() : m_store->AudioBudget();
        fgpr::TrimBehindByTime(m_samples, m_next, m_info.timescale, m_store->retainBehindMs, budget, m_bytes);
        PublishBytesLocked();
    }

    void PublishBytesLocked()
    {
        if (!m_store) return;
        (m_streamId == 1 ? m_store->videoBytes : m_store->audioBytes).store(m_bytes, std::memory_order_relaxed);
    }

    /// Append freshly demuxed samples (the background fetcher) and release any request that was parked on starvation.
    /// Called from the fetch thread; the stream lock serialises it against RequestSample. A stream Media Foundation has
    /// already shut down (its source was replaced by a detach) still ACCEPTS samples: the session's buffer outlives the
    /// MF object, and the re-attach moves it into a fresh source (TakeSamples).
    void AppendSamples(std::vector<cenc::Sample>&& more)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (more.empty()) return;
        SpliceLocked(std::move(more));
        TrimBehindLocked();
        if (!m_shutdown) ReleaseStarvedLocked();
    }

    /// Trim history now (the feeder, before it weighs the byte budget): appends are not the only moment the playhead has
    /// moved, and a session over its budget that only trimmed on append could never append again.
    void TrimNow()
    {
        std::lock_guard<std::mutex> g(m_mx);
        TrimBehindLocked();
    }

    void SetDemandBelowMs(int64_t ms) { m_demandBelowMs.store(ms > 0 ? ms : 0, std::memory_order_relaxed); }

    /// Move the whole buffer out, with its byte cost, for the fresh source a re-attach builds.
    std::vector<cenc::Sample> TakeSamples(uint64_t& bytes)
    {
        std::lock_guard<std::mutex> g(m_mx);
        std::vector<cenc::Sample> out = std::move(m_samples);
        m_samples.clear();
        m_next = 0;
        bytes = m_bytes;
        m_bytes = 0;
        return out;
    }

    /// FgPrSessionGetBuffered for this stream: ascending (startMs, endMs) pairs, a hole starts a new pair.
    int BufferedPairs(int64_t* out, int capPairs)
    {
        std::lock_guard<std::mutex> g(m_mx);
        return fgpr::ComputeBufferedPairs(m_samples, m_info.timescale, out, capPairs);
    }

    /// A seek into a range the feeder has not fetched re-opens a track that had been marked complete — otherwise the
    /// stream would report end-of-stream the moment it drained the target segment while the next one is still on the wire.
    void MarkIncomplete()
    {
        std::lock_guard<std::mutex> g(m_mx);
        m_complete = false;
    }

    bool IsComplete()
    {
        std::lock_guard<std::mutex> g(m_mx);
        return m_complete;
    }

    /// Splice the target representation's segment into the timeline at ITS OWN presentation time, then announce the new
    /// H.264 geometry. The feeder calls this at a segment boundary at-or-after the playhead, so everything between the
    /// playhead and the splice point stays in the OLD representation and the timeline remains contiguous. (Erasing from
    /// m_next unconditionally — what this used to do — deleted every buffered sample under the playhead and refilled
    /// from the feeder's stale cursor, which is what punched a multi-second hole in the video while audio kept running:
    /// the frozen frame.) Already delivered samples remain owned by Media Foundation.
    void SwitchVideoRepresentation(const cenc::InitInfo& nextInfo, std::vector<cenc::Sample>&& replacement)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown || nextInfo.kind != cenc::TrackKind::Video || replacement.empty()) return;
        const uint64_t spliceTicks = replacement.front().timeTicks;
        const size_t at = SpliceLocked(std::move(replacement), true);
        m_info = nextInfo;
        // Only the sample delivered NEXT may be flagged discontinuous. When the splice lands ahead of the playhead the
        // next sample is still old-representation continuous video; flagging it would make the decoder drop frames all
        // the way to the new keyframe.
        if (at <= m_next) m_discontinuity = true;
        m_complete = false;

        // The protected wrapper remains the same stream type; update its display/config attributes and publish the
        // standard format-change event. The first replacement access unit also carries the new SPS/PPS, which is the
        // decoder-authoritative configuration for H.264 dynamic resolution changes.
        winrt::com_ptr<IMFMediaTypeHandler> handler;
        winrt::com_ptr<IMFMediaType> current;
        if (m_sd && SUCCEEDED(m_sd->GetMediaTypeHandler(handler.put())) && handler &&
            SUCCEEDED(handler->GetCurrentMediaType(current.put())) && current)
        {
            MFSetAttributeSize(current.get(), MF_MT_FRAME_SIZE, nextInfo.width, nextInfo.height);
            if (!nextInfo.spspps.empty())
                current->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, nextInfo.spspps.data(), (UINT32)nextInfo.spspps.size());
            m_queue->QueueEventParamUnk(MEStreamFormatChanged, GUID_NULL, S_OK, current.get());
        }
        const int64_t spliceMs = m_info.timescale ? (int64_t)((spliceTicks * 1000ULL) / m_info.timescale) : 0;
        const int64_t playheadMs = (m_info.timescale && m_next < m_samples.size())
            ? (int64_t)((m_samples[m_next].timeTicks * 1000ULL) / m_info.timescale) : 0;
        LogLine("[cenc-src] video switched to " + std::to_string(nextInfo.width) + "x" +
                std::to_string(nextInfo.height) + " spliced at sample " + std::to_string(at) +
                " (t=" + std::to_string((long long)spliceMs) + "ms) with the playhead at sample " +
                std::to_string(m_next) + " (t=" + std::to_string((long long)playheadMs) + "ms), buffer=" +
                std::to_string(m_samples.size()) + " sample(s)");
        ReleaseStarvedLocked();
    }

    /// No more samples are coming (the fetcher finished, or gave up). After this, running dry is a real end of stream —
    /// including for requests parked while starved, which must not hang forever if the fetch failed.
    void MarkComplete()
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_complete) return;
        m_complete = true;
        LogLine(std::string("[cenc-src] ") + m_label + " feed complete: " + std::to_string(m_samples.size()) + " sample(s)");
        ReleaseStarvedLocked();
    }

    /// How many demuxed samples are buffered ahead of the playhead — the fetcher's backpressure signal, so it stays a
    /// bounded distance in front of playback instead of pulling the whole track into memory.
    size_t Ahead()
    {
        std::lock_guard<std::mutex> g(m_mx);
        return m_samples.size() > m_next ? m_samples.size() - m_next : 0;
    }
    int64_t AheadDurationMs()
    {
        std::lock_guard<std::mutex> g(m_mx);
        return AheadDurationMsLocked();
    }
    int64_t AheadDurationMsLocked() const
    {
        if (m_info.timescale == 0 || m_next >= m_samples.size()) return 0;
        uint64_t start = m_samples[m_next].timeTicks;
        auto const& last = m_samples.back();
        uint64_t end = last.timeTicks + last.durTicks;
        return end > start ? (int64_t)(((end - start) * 1000ULL) / m_info.timescale) : 0;
    }
    int64_t NextSampleTimeMs()
    {
        std::lock_guard<std::mutex> g(m_mx);
        return m_info.timescale > 0 && m_next < m_samples.size()
            ? (int64_t)((m_samples[m_next].timeTicks * 1000ULL) / m_info.timescale)
            : 0;
    }

    /// Can a seek to `targetMs` be served from what is ALREADY buffered? This is what turns a backward scrub into an
    /// instant reposition instead of three serial TLS-handshaking GETs. Video needs a KEYFRAME at or before the target
    /// (that is what Start() repositions to); audio only needs coverage. Both need CONTIGUOUS coverage past the target
    /// — a buffer with a hole in it (left by an earlier forward seek) must not report the far side as seekable.
    /// The algorithm moved verbatim to SegmentStore.h (fgpr::CanSeekToIn). The m_shutdown refusal is gone: the samples
    /// are the SESSION's buffer and stay valid after Media Foundation shuts a detached source down.
    bool CanSeekTo(int64_t targetMs, bool requireKeyframe)
    {
        std::lock_guard<std::mutex> g(m_mx);
        return fgpr::CanSeekToIn(m_samples, m_info.timescale, targetMs, requireKeyframe);
    }

    /// The buffered presentation range, for the always-on log.
    void BufferedRangeMs(int64_t& startMs, int64_t& endMs)
    {
        std::lock_guard<std::mutex> g(m_mx);
        startMs = endMs = 0;
        if (m_info.timescale == 0 || m_samples.empty()) return;
        startMs = (int64_t)((m_samples.front().timeTicks * 1000ULL) / m_info.timescale);
        auto const& last = m_samples.back();
        endMs = (int64_t)(((last.timeTicks + last.durTicks) * 1000ULL) / m_info.timescale);
    }

    bool IsShutdown() { std::lock_guard<std::mutex> g(m_mx); return m_shutdown; }

    void ReleaseStarvedLocked()
    {
        if (m_starvedRequests.empty() || !m_started || m_paused) return;
        auto parked = std::move(m_starvedRequests);
        m_starvedRequests.clear();
        for (auto const& token : parked) DeliverSampleLocked(token.get());
    }
    void Shutdown() { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return; m_shutdown = true; if (m_queue) m_queue->Shutdown(); }

    void NotifySourceEnded();   // defined after CencMediaSource

    // Build one encrypted IMFSample with its CENC attributes.
    HRESULT MakeSample(const cenc::Sample& s, IMFSample** ppSample)
    {
        winrt::com_ptr<IMFSample> sample;
        winrt::com_ptr<IMFMediaBuffer> buf;
        HRESULT hr = MFCreateSample(sample.put());
        if (FAILED(hr)) return hr;
        hr = MFCreateMemoryBuffer((DWORD)s.data.size(), buf.put());
        if (FAILED(hr)) return hr;
        BYTE* dst = nullptr; DWORD maxLen = 0;
        hr = buf->Lock(&dst, &maxLen, nullptr);
        if (FAILED(hr)) return hr;
        memcpy(dst, s.data.data(), s.data.size());
        buf->Unlock();
        buf->SetCurrentLength((DWORD)s.data.size());
        sample->AddBuffer(buf.get());

        auto toMf = [&](uint64_t ticks) -> LONGLONG { return (LONGLONG)((ticks * 10000000ULL) / m_info.timescale); };
        sample->SetSampleTime(toMf(s.timeTicks));
        sample->SetSampleDuration(toMf(s.durTicks));
        sample->SetUINT64(MFSampleExtension_DecodeTimestamp, (UINT64)toMf(s.decodeTicks));
        if (s.keyframe) sample->SetUINT32(MFSampleExtension_CleanPoint, 1);

        if (s.encrypted)
        {
            sample->SetUINT32(MFSampleExtension_Encryption_ProtectionScheme,
                              m_info.scheme == 1 ? MF_SAMPLE_ENCRYPTION_PROTECTION_SCHEME_AES_CBC
                                                 : MF_SAMPLE_ENCRYPTION_PROTECTION_SCHEME_AES_CTR);
            // ISO BMFF tenc stores default_KID as a 16-byte big-endian UUID. MFSampleExtension_Content_KeyID is a
            // Windows GUID, whose Data1/Data2/Data3 fields have little-endian in-memory representation. A raw memcpy
            // asks the CDM for a different key even though the proactively licensed key reports USABLE.
            GUID kid{};
            kid.Data1 = cenc::rd32(m_info.kid);
            kid.Data2 = cenc::rd16(m_info.kid + 4);
            kid.Data3 = cenc::rd16(m_info.kid + 6);
            memcpy(kid.Data4, m_info.kid + 8, 8);
            sample->SetGUID(MFSampleExtension_Content_KeyID, kid);
            sample->SetBlob(MFSampleExtension_Encryption_SampleID, s.iv.data(), (UINT32)s.iv.size());
            if (!s.subsamples.empty())
            {
                // The modern protected/CDM path consumes SubSample_Mapping (the attribute Chromium uses). Do not also
                // publish legacy SubSampleMappingSplit: the PlayReady transform treats two maps as an ambiguous,
                // non-empty duplicate property and fails the sample with MF_E_PROPERTY_NOT_EMPTY.
                std::vector<uint32_t> map; map.reserve(s.subsamples.size() * 2);
                for (auto const& ss : s.subsamples) { map.push_back(ss.clearBytes); map.push_back(ss.encBytes); }
                sample->SetBlob(MFSampleExtension_Encryption_SubSample_Mapping,
                                (const UINT8*)map.data(), (UINT32)(map.size() * sizeof(uint32_t)));
            }
            if (m_info.scheme == 1)
            {
                sample->SetUINT32(MFSampleExtension_Encryption_CryptByteBlock, m_info.cryptByteBlock);
                sample->SetUINT32(MFSampleExtension_Encryption_SkipByteBlock, m_info.skipByteBlock);
            }
        }
        *ppSample = sample.detach();
        return S_OK;
    }
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Custom IMFMediaSource — one video stream of demuxed encrypted samples.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
struct CencMediaSource : winrt::implements<CencMediaSource, IMFMediaSource, IMFTrustedInput>
{
    winrt::com_ptr<IMFMediaEventQueue> m_queue;
    winrt::com_ptr<IMFPresentationDescriptor> m_pd;
    // Every stream this source can serve: video (id 1) and, for a music video, its own soundtrack (id 2). A vector
    // rather than a single member because the topology loader selects and starts them independently.
    std::vector<winrt::com_ptr<CencMediaStream>> m_streams;
    winrt::com_ptr<IMFTrustedInput> m_trustedInput;
    // One STABLE Input Trust Authority per stream id. Re-creating an authority for a stream the loader already asked
    // about resets PlayReady's policy/decrypter state and is rejected as DRM_E_LOGICERR — and with two streams the
    // loader alternates between them, so a single-slot cache would thrash on every other query. The cached value is the
    // exact IUnknown proxy the CDM handed back: re-querying another interface into this output can produce a
    // proxy/vtable mismatch across the PMP boundary (mirrors Firefox's MFCDMProxy cache).
    std::map<DWORD, winrt::com_ptr<::IUnknown>> m_itaByStream;
    std::set<DWORD> m_announcedStreams;   // MENewStream is sent once per stream; later Starts send MEUpdatedStream
    std::set<DWORD> m_endedStreams;       // MEEndOfPresentation waits for ALL streams (see NotifyStreamEnded)
    bool m_started = false, m_paused = false, m_shutdown = false;
    std::mutex m_mx;

    uint64_t m_session = 0;                           // the FgPrSession this source was built for
    std::shared_ptr<fgpr::SegmentStore> m_store;      // shared with the session: MF may hold this source past its session
    LONGLONG m_startPosition100ns = 0;                // m_mx — where the FIRST Start lands (consumed once; see Start)
    std::atomic<bool> m_handedToEngine{ false };      // an attach SetSource'd this object; a re-attach needs a fresh one

    CencMediaSource() { winrt::check_hresult(MFCreateEventQueue(m_queue.put())); }

    /// Every diagnostic line from this source carries its session handle (shadows the runtime-level ::LogLine).
    void LogLine(const std::string& s) const { fgpr::RaiseLog(m_session, s); }

    void SetStartPosition100ns(LONGLONG position100ns)
    {
        std::lock_guard<std::mutex> g(m_mx);
        m_startPosition100ns = position100ns > 0 ? position100ns : 0;
    }

    // IMFMediaEventGenerator
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* c, ::IUnknown* s) noexcept override { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->BeginGetEvent(c, s); }
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* r, IMFMediaEvent** e) noexcept override { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->EndGetEvent(r, e); }
    IFACEMETHODIMP GetEvent(DWORD f, IMFMediaEvent** e) noexcept override { winrt::com_ptr<IMFMediaEventQueue> q; { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; q = m_queue; } return q->GetEvent(f, e); }
    IFACEMETHODIMP QueueEvent(MediaEventType t, REFGUID g, HRESULT s, const PROPVARIANT* v) noexcept override { std::lock_guard<std::mutex> gd(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->QueueEventParamVar(t, g, s, v); }

    // IMFMediaSource
    IFACEMETHODIMP GetCharacteristics(DWORD* pdw) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!pdw) return E_POINTER;
        *pdw = MFMEDIASOURCE_CAN_PAUSE | MFMEDIASOURCE_CAN_SEEK;
        return S_OK;
    }
    IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** ppPD) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!ppPD) return E_POINTER;
        if (!m_pd) return MF_E_NOT_INITIALIZED;
        return m_pd->Clone(ppPD);
    }
    IFACEMETHODIMP Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPos) noexcept override
    {
        PROPVARIANT startVar; PropVariantInit(&startVar);
        std::vector<winrt::com_ptr<CencMediaStream>> starting;
        bool wasActive = false, seeking = false, explicitPosition = false;
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_shutdown) return MF_E_SHUTDOWN;
            if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
            if (!pd || !startPos || (startPos->vt != VT_I8 && startPos->vt != VT_EMPTY)) return E_INVALIDARG;
            if (FAILED(PropVariantCopy(&startVar, startPos))) return E_OUTOFMEMORY;

            wasActive = m_started;
            // THE CARRIED START POSITION (plan bug S1). The media engine opens a source from the beginning — VT_EMPTY or
            // an explicit 0 — so a switch to a video at 1:23 used to present 0:00 first and then jump. The session hands
            // the source its start position; the first Start that asks for "the beginning" is rewritten to it, the
            // streams reposition to the keyframe at or before it, and MESourceStarted carries the ACTUAL start time so the
            // presentation clock begins there too. Consumed once: a later Start(0) is a real seek to 0.
            if (!wasActive && m_startPosition100ns > 0 &&
                (startVar.vt == VT_EMPTY || (startVar.vt == VT_I8 && startVar.hVal.QuadPart == 0)))
            {
                PropVariantClear(&startVar);
                startVar.vt = VT_I8;
                startVar.hVal.QuadPart = m_startPosition100ns;
                LogLine("[cenc-src] first Start rewritten to the carried start position " +
                        std::to_string((long long)(m_startPosition100ns / 10000)) + "ms");
            }
            if (!wasActive) m_startPosition100ns = 0;
            explicitPosition = startVar.vt == VT_I8;
            seeking = wasActive && explicitPosition;

            // Honour the SELECTION the topology loader made: walk every descriptor in the presentation descriptor it
            // handed us and start exactly the streams marked selected. Assuming index 0 is the only stream is what makes
            // a second (audio) stream impossible — it would never be announced, so nothing would ever request from it.
            DWORD count = 0;
            if (FAILED(pd->GetStreamDescriptorCount(&count)) || count == 0)
            {
                PropVariantClear(&startVar);
                return MF_E_INVALIDREQUEST;
            }
            std::string sel;
            for (DWORD i = 0; i < count; i++)
            {
                BOOL selected = FALSE; winrt::com_ptr<IMFStreamDescriptor> sd;
                if (FAILED(pd->GetStreamDescriptorByIndex(i, &selected, sd.put())) || !sd) continue;
                DWORD id = 0;
                if (FAILED(sd->GetStreamIdentifier(&id))) continue;
                auto stream = FindStreamLocked(id);
                if (!stream) continue;
                sel += (sel.empty() ? "" : ",") + std::string(stream->m_label) + (selected ? ":on" : ":off");
                if (!selected) continue;

                bool isNew = m_announcedStreams.insert(id).second;
                m_queue->QueueEventParamUnk(isNew ? MENewStream : MEUpdatedStream, GUID_NULL, S_OK,
                                            (::IUnknown*)(IMFMediaStream*)stream.get());
                starting.push_back(stream);
            }
            if (starting.empty())
            {
                PropVariantClear(&startVar);
                return MF_E_INVALIDREQUEST;
            }

            LogLine("[cenc-src] Start previous=" + std::string(m_paused ? "paused" : (wasActive ? "started" : "stopped")) +
                    " position=" + (explicitPosition
                        ? std::to_string((long long)startVar.hVal.QuadPart) : std::string("current")) +
                    " event=" + (seeking ? "seeked" : "started") + " streams=[" + sel + "]");

            m_started = true;
            m_paused = false;
            if (explicitPosition || !wasActive) m_endedStreams.clear();   // a reposition un-ends the presentation
            // The source event precedes the corresponding stream events (the documented custom-source sequence).
            m_queue->QueueEventParamVar(seeking ? MESourceSeeked : MESourceStarted, GUID_NULL, S_OK, &startVar);
        }

        // A non-empty position also repositions a previously stopped source, but that operation is still a Start
        // (MESourceStarted/MEStreamStarted), not a seek. VT_EMPTY is pause-resume and preserves the sample cursor.
        for (auto const& stream : starting)
            stream->Start(&startVar, seeking, explicitPosition || !wasActive);
        PropVariantClear(&startVar);
        return S_OK;
    }

    winrt::com_ptr<CencMediaStream> FindStreamLocked(DWORD id)
    {
        for (auto const& s : m_streams) if (s && s->m_streamId == id) return s;
        return nullptr;
    }
    IFACEMETHODIMP Stop() noexcept override
    {
        std::vector<winrt::com_ptr<CencMediaStream>> streams;
        HRESULT hr;
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_shutdown) return MF_E_SHUTDOWN;
            m_started = false; m_paused = false;
            m_endedStreams.clear();
            streams = m_streams;
            hr = m_queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
        }
        for (auto const& s : streams) if (s) s->Stop();
        return hr;
    }
    IFACEMETHODIMP Pause() noexcept override
    {
        std::vector<winrt::com_ptr<CencMediaStream>> streams;
        HRESULT hr;
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_shutdown) return MF_E_SHUTDOWN;
            if (!m_started || m_paused) return MF_E_INVALID_STATE_TRANSITION;
            m_paused = true;
            streams = m_streams;
            LogLine("[cenc-src] Pause -> MESourcePaused + MEStreamPaused");
            hr = m_queue->QueueEventParamVar(MESourcePaused, GUID_NULL, S_OK, nullptr);
        }
        for (auto const& s : streams) if (s) s->Pause();
        return hr;
    }
    IFACEMETHODIMP Shutdown() noexcept override
    {
        // Shut the streams down OUTSIDE this lock. A stream that is mid-delivery holds its own lock and then reaches for
        // the source lock (end-of-stream notification); taking them in the opposite order here — source first, then
        // stream — is a textbook lock inversion, and with two streams delivering concurrently it is reachable.
        // Setting m_shutdown first makes the notification a no-op, so nothing is lost by releasing the lock.
        std::vector<winrt::com_ptr<CencMediaStream>> streams;
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_shutdown) return MF_E_SHUTDOWN;
            m_shutdown = true;
            streams = m_streams;
        }
        for (auto const& s : streams) if (s) s->Shutdown();
        std::lock_guard<std::mutex> g(m_mx);
        if (m_queue) m_queue->Shutdown();
        return S_OK;
    }

    // IMFTrustedInput. A normal desktop protected topology asks the source for the CDM's Input Trust Authority, which
    // supplies the PMP decrypter and output policy for this stream.
    IFACEMETHODIMP GetInputTrustAuthority(DWORD streamId, REFIID riid, IUnknown** value) noexcept override
    {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (!m_trustedInput) return MF_E_NOT_INITIALIZED;
        std::lock_guard<std::mutex> g(m_mx);

        // The topology loader asks repeatedly while resolving each protected branch, and with two streams it ALTERNATES
        // between them — so the cache must be keyed by stream id. A single slot would evict the video's authority when
        // the audio branch resolves (and vice versa), and the re-created authority is rejected as DRM_E_LOGICERR.
        HRESULT hr;
        bool cached = false;
        auto it = m_itaByStream.find(streamId);
        if (it != m_itaByStream.end() && it->second)
        {
            // Hand back the EXACT proxy pointer the CDM returned the first time. GetInputTrustAuthority's output is
            // IUnknown** even though riid normally requests IMFInputTrustAuthority; re-querying another interface into
            // this output can produce a proxy/vtable mismatch across the PMP boundary. Mirrors Firefox's MFCDMProxy.
            *value = it->second.get();
            (*value)->AddRef();
            hr = S_OK;
            cached = true;
        }
        else
        {
            winrt::com_ptr<::IUnknown> unknown;
            hr = m_trustedInput->GetInputTrustAuthority(streamId, riid, unknown.put());
            if (SUCCEEDED(hr) && unknown)
            {
                m_itaByStream[streamId] = unknown;      // keep the proxy identity itself, not a re-queried interface
                *value = unknown.detach();
                hr = S_OK;
            }
        }
        std::stringstream ss; ss << "[cenc-src] GetInputTrustAuthority stream=" << streamId
                                 << (cached ? " (cached)" : " (fresh)")
                                 << " hr=0x" << std::hex << (uint32_t)hr;
        LogLine(ss.str());
        return hr;
    }

    /// One stream drained. The PRESENTATION only ends when EVERY selected stream has: video and audio never hold the
    /// same number of samples, so a first-stream-wins MEEndOfPresentation truncates playback at whichever track runs out
    /// first — the shorter one, always, and usually well before the end of the song.
    void NotifyStreamEnded(DWORD streamId)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return;
        m_endedStreams.insert(streamId);
        // The denominator is the streams the topology actually SELECTED, not every stream we offered. If MF declined the
        // audio branch, that stream is never started and can never end — waiting for it would hold the presentation open
        // forever and the track would never advance.
        size_t total = m_announcedStreams.empty() ? m_streams.size() : m_announcedStreams.size();
        if (m_endedStreams.size() < total)
        {
            LogLine("[cenc-src] stream " + std::to_string(streamId) + " ended (" +
                    std::to_string(m_endedStreams.size()) + "/" + std::to_string(total) +
                    ") — holding MEEndOfPresentation");
            return;
        }
        LogLine("[cenc-src] all " + std::to_string(total) + " stream(s) ended -> MEEndOfPresentation");
        m_queue->QueueEventParamVar(MEEndOfPresentation, GUID_NULL, S_OK, nullptr);
    }
};

inline void CencMediaStream::NotifySourceEnded()
{
    if (m_source) static_cast<CencMediaSource*>(m_source)->NotifyStreamEnded(m_streamId);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Factory: build the media type / stream descriptor / presentation descriptor and wire the demuxed samples.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
/// The video's own soundtrack, handed to <see cref="BuildCencSource"/> alongside the video. Absent (null) ⇒ video only.
struct CencAudioFeed
{
    cenc::InitInfo info;
    std::vector<cenc::Sample> samples;
};

/// Build the AAC media type for the protected pipeline. Two things here are easy to get silently wrong:
///  • MF wants the AAC configuration as MF_MT_USER_DATA in the HEAACWAVEINFO layout — the 12 bytes that FOLLOW the
///    WAVEFORMATEX part (payload type, profile-level, struct type, two reserved fields) and THEN the raw
///    AudioSpecificConfig. Handing it the bare ASC produces a type MF accepts and then fails to decode.
///  • payload type 0 means "raw AAC access units" (what fMP4 stores). Anything else describes ADTS/ADIF framing that
///    our samples do not have.
static winrt::com_ptr<IMFMediaType> BuildAacMediaType(const cenc::InitInfo& a)
{
    winrt::com_ptr<IMFMediaType> mt;
    winrt::check_hresult(MFCreateMediaType(mt.put()));
    mt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    mt->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
    mt->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, a.sampleRate ? a.sampleRate : 44100);
    mt->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, a.channels ? a.channels : 2);
    mt->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
    mt->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 1);
    mt->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    if (a.avgBitrate) mt->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, a.avgBitrate / 8);
    // AAC-LC unless the AudioSpecificConfig says otherwise; 0x29 is the AAC-LC profile-level indication MF expects.
    const uint16_t profileLevel = 0x29;
    mt->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0);
    mt->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, profileLevel);

    std::vector<uint8_t> userData(12, 0);
    userData[0] = 0; userData[1] = 0;                                   // wPayloadType = 0 (raw AAC)
    userData[2] = (uint8_t)(profileLevel & 0xFF); userData[3] = (uint8_t)(profileLevel >> 8);
    userData[4] = 0; userData[5] = 0;                                   // wStructType = 0 (AudioSpecificConfig follows)
    userData.insert(userData.end(), a.asc.begin(), a.asc.end());
    mt->SetBlob(MF_MT_USER_DATA, userData.data(), (UINT32)userData.size());
    return mt;
}

/// <param name="totalDuration100ns">The WHOLE track's duration when only a prefix has been demuxed so far. Required for a
/// streaming build: deriving the presentation duration from the samples in hand would declare the track to be as long as
/// the initial burst (~8s), and the media engine then treats every later position as past the end — the seek bar pins,
/// the clock overshoots ("0:36 / 0:08"), and Play() after a Pause does nothing because the presentation already ended.</param>
/// <param name="sessionHandle">The FgPrSession the source serves — every diagnostic line it writes carries it.</param>
/// <param name="store">The session's SegmentStore: the streams trim against its time window and byte budget.</param>
/// <param name="startPosition100ns">Where the source's FIRST Start lands (plan bug S1); 0 = the beginning.</param>
static winrt::com_ptr<CencMediaSource> BuildCencSource(const cenc::InitInfo& info, std::vector<cenc::Sample>&& samples,
                                                       CencAudioFeed* audio, bool streaming,
                                                       uint64_t totalDuration100ns, uint64_t sessionHandle,
                                                       std::shared_ptr<fgpr::SegmentStore> store,
                                                       LONGLONG startPosition100ns)
{
    auto hx = [](HRESULT h) { std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };

    winrt::com_ptr<IMFMediaType> mt;
    winrt::check_hresult(MFCreateMediaType(mt.put()));
    mt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    mt->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    MFSetAttributeSize(mt.get(), MF_MT_FRAME_SIZE, info.width, info.height);
    mt->SetUINT32(MF_MT_INTERLACE_MODE, 2 /*MFVideoInterlace_Progressive*/);
    if (!info.spspps.empty()) mt->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, info.spspps.data(), (UINT32)info.spspps.size());
    mt->SetUINT32(MF_MT_ORIGINAL_4CC, info.codec4cc ? info.codec4cc : cenc::fourcc("avc1"));
    // Protected-stream advertisement, the Firefox desktop-MFCDM way (gecko MFMediaEngineVideoStream::CreateMediaType +
    // MFMediaEngineStream::GenerateStreamDescriptor): WRAP the fully-populated clear H.264 type into a
    // MFMediaType_Protected envelope with MFWrapMediaType and set MF_SD_PROTECTED=1 on the stream descriptor. That is
    // what tells the media engine's modern EME pipeline "insert the CDM's decryptor before the decoder for this
    // stream"; without it the engine wires our encrypted samples STRAIGHT into the H.264 decoder, which rejects the
    // first ciphertext-bearing sample as MF_E_INVALIDREQUEST (decode error 3). NOTE this is NOT the earlier failed
    // experiment: stamping the raw MF_MT_PROTECTED *attribute* on an UNWRAPPED clear type (the retired
    // FG_CENC_MARK_SD_PROTECTED diagnostics) selects the legacy ITA/OTA topology whose trust verification fails
    // 0xC00D715B. The wrapped type is unwrapped by the pipeline (MFUnwrapMediaType) after the decryptor, so the decoder
    // still sees the real H.264 type. The wrap is now the ONLY wiring: the environment A/B arms (the raw attribute, and
    // FG_CENC_NO_PROTECTED_WRAP's clear-typed stream) were deleted with every other env switch in this directory.
    winrt::com_ptr<IMFMediaType> streamType = mt;
    const bool wrapProtected = info.encrypted;
    if (wrapProtected)
    {
        winrt::com_ptr<IMFMediaType> wrapped;
        HRESULT hrWrap = MFWrapMediaType(mt.get(), MFMediaType_Protected, MFVideoFormat_H264, wrapped.put());
        LogLine("[cenc-src] MFWrapMediaType(Protected) hr=" + hx(hrWrap));
        if (SUCCEEDED(hrWrap)) streamType = wrapped;
    }

    winrt::com_ptr<IMFStreamDescriptor> sd;
    IMFMediaType* mts[1] = { streamType.get() };
    winrt::check_hresult(MFCreateStreamDescriptor(1 /*streamId*/, 1, mts, sd.put()));
    if (wrapProtected) sd->SetUINT32(MF_SD_PROTECTED, 1);
    {
        winrt::com_ptr<IMFMediaTypeHandler> mth;
        winrt::check_hresult(sd->GetMediaTypeHandler(mth.put()));
        winrt::check_hresult(mth->SetCurrentMediaType(streamType.get()));
    }

    // ── the audio stream (the video's own soundtrack), same protected envelope as the video ─────────────────────────
    winrt::com_ptr<IMFStreamDescriptor> audioSd;
    if (audio)
    {
        auto audioMt = BuildAacMediaType(audio->info);
        winrt::com_ptr<IMFMediaType> audioStreamType = audioMt;
        const bool wrapAudio = audio->info.encrypted;
        if (wrapAudio)
        {
            winrt::com_ptr<IMFMediaType> wrapped;
            HRESULT hrWrap = MFWrapMediaType(audioMt.get(), MFMediaType_Protected, MFAudioFormat_AAC, wrapped.put());
            LogLine("[cenc-src] MFWrapMediaType(Protected, AAC) hr=" + hx(hrWrap));
            if (SUCCEEDED(hrWrap)) audioStreamType = wrapped;
        }
        IMFMediaType* amts[1] = { audioStreamType.get() };
        winrt::check_hresult(MFCreateStreamDescriptor(2 /*streamId*/, 1, amts, audioSd.put()));
        if (wrapAudio) audioSd->SetUINT32(MF_SD_PROTECTED, 1);
        winrt::com_ptr<IMFMediaTypeHandler> amth;
        winrt::check_hresult(audioSd->GetMediaTypeHandler(amth.put()));
        winrt::check_hresult(amth->SetCurrentMediaType(audioStreamType.get()));
    }

    winrt::com_ptr<IMFPresentationDescriptor> pd;
    IMFStreamDescriptor* sds[2] = { sd.get(), audioSd.get() };
    winrt::check_hresult(MFCreatePresentationDescriptor(audioSd ? 2 : 1, sds, pd.put()));
    pd->SelectStream(0);
    if (audioSd) pd->SelectStream(1);

    // Presentation duration = the longer of the two tracks. Reporting only the video's would cut the last audio samples
    // off (they routinely extend past the final frame), and reporting a partial prefix would make the seek bar lie while
    // the rest of the track is still being fetched — so a STREAMING build reports the caller-supplied total instead.
    auto endOf = [](const std::vector<cenc::Sample>& v, uint64_t timescale) -> uint64_t {
        uint64_t endTicks = 0;
        for (auto const& s : v) { uint64_t e = s.timeTicks + s.durTicks; if (e > endTicks) endTicks = e; }
        return timescale ? (endTicks * 10000000ULL) / timescale : 0;
    };
    uint64_t dur100ns = endOf(samples, info.timescale);
    if (audio) dur100ns = std::max<uint64_t>(dur100ns, endOf(audio->samples, audio->info.timescale));
    if (totalDuration100ns > dur100ns) dur100ns = totalDuration100ns;   // streaming: the WHOLE track, not the prefix
    if (dur100ns) pd->SetUINT64(MF_PD_DURATION, (UINT64)dur100ns);

    auto source = winrt::make_self<CencMediaSource>();
    source->m_session = sessionHandle;
    source->m_store = store;
    source->m_startPosition100ns = startPosition100ns > 0 ? startPosition100ns : 0;
    auto stream = winrt::make_self<CencMediaStream>();
    stream->m_session = sessionHandle;
    stream->m_store = store;
    stream->m_sd = sd;
    stream->m_source = (IMFMediaSource*)source.get();   // weak — source holds the strong ref
    stream->m_info = info;
    stream->m_samples = std::move(samples);
    stream->m_streamId = 1;
    stream->m_label = "video";
    stream->m_complete = !streaming;
    stream->m_bytes = fgpr::SampleFootprint(stream->m_samples);
    stream->PublishBytesLocked();   // not yet visible to any other thread
    source->m_pd = pd;
    source->m_streams.push_back(stream);

    if (audio)
    {
        auto astream = winrt::make_self<CencMediaStream>();
        astream->m_session = sessionHandle;
        astream->m_store = store;
        astream->m_sd = audioSd;
        astream->m_source = (IMFMediaSource*)source.get();
        astream->m_info = audio->info;
        astream->m_samples = std::move(audio->samples);
        astream->m_streamId = 2;
        astream->m_label = "audio";
        astream->m_complete = !streaming;
        astream->m_bytes = fgpr::SampleFootprint(astream->m_samples);
        astream->PublishBytesLocked();
        source->m_streams.push_back(astream);
    }

    LogLine("[cenc-src] built source: " + std::to_string(info.width) + "x" + std::to_string(info.height) +
            " scheme=" + std::string(info.scheme == 1 ? "cbcs" : "cenc") +
            " ivSize=" + std::to_string((int)info.perSampleIvSize) +
            " spspps=" + std::to_string(info.spspps.size()) + "B samples=" + std::to_string(source->m_streams[0]->m_samples.size()) +
            (audio ? (" + AUDIO " + std::to_string(source->m_streams[1]->m_info.channels) + "ch/" +
                      std::to_string(source->m_streams[1]->m_info.sampleRate) + "Hz asc=" +
                      std::to_string(source->m_streams[1]->m_info.asc.size()) + "B samples=" +
                      std::to_string(source->m_streams[1]->m_samples.size()))
                    : std::string(" (no audio track)")) +
            (streaming ? "  [streaming: more segments arriving]" : ""));
    return source;
}

// Base64-encode raw bytes (crypt32).
static std::string CencBase64(const uint8_t* data, size_t n)
{
    DWORD cch = 0;
    CryptBinaryToStringA(data, (DWORD)n, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, nullptr, &cch);
    std::string out(cch, '\0');
    if (cch) CryptBinaryToStringA(data, (DWORD)n, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, out.data(), &cch);
    while (!out.empty() && out.back() == '\0') out.pop_back();
    return out;
}

// Build a PlayReady 'pssh' box (system id 9A04F079-9840-4286-AB92-E65BE0885F95) wrapping a WRMHEADER for the given KID
// + license URL. Fallback init data for GenerateRequest when the DASH init segment carries no pssh box. The KID in the
// WRMHEADER VALUE is the GUID-ordered (mixed-endian) form of the big-endian tenc KID.
static std::vector<uint8_t> BuildPlayReadyPssh(const uint8_t kidBE[16], const std::wstring& laUrl)
{
    // tenc KID is big-endian; PlayReady KID VALUE uses GUID byte order (first 3 fields little-endian).
    uint8_t g[16];
    g[0] = kidBE[3]; g[1] = kidBE[2]; g[2] = kidBE[1]; g[3] = kidBE[0];
    g[4] = kidBE[5]; g[5] = kidBE[4];
    g[6] = kidBE[7]; g[7] = kidBE[6];
    memcpy(g + 8, kidBE + 8, 8);
    std::string kidB64 = CencBase64(g, 16);

    std::wstring xml = L"<WRMHEADER xmlns=\"http://schemas.microsoft.com/DRM/2007/03/PlayReadyHeader\" version=\"4.3.0.0\">"
                       L"<DATA><PROTECTINFO><KIDS><KID ALGID=\"AESCTR\" VALUE=\"" +
                       std::wstring(kidB64.begin(), kidB64.end()) + L"\"></KID></KIDS></PROTECTINFO>";
    if (!laUrl.empty()) xml += L"<LA_URL>" + laUrl + L"</LA_URL>";
    xml += L"</DATA></WRMHEADER>";

    // WRMHEADER stored UTF-16LE.
    const uint8_t* xmlBytes = (const uint8_t*)xml.data();
    uint32_t xmlLen = (uint32_t)(xml.size() * sizeof(wchar_t));

    // PlayReady Object: [u32 size][u16 count=1][u16 type=1][u16 length][WRMHEADER].
    std::vector<uint8_t> pro;
    auto put16le = [&](uint16_t v) { pro.push_back((uint8_t)(v & 0xFF)); pro.push_back((uint8_t)(v >> 8)); };
    auto put32le = [&](uint32_t v) { for (int i = 0; i < 4; i++) pro.push_back((uint8_t)((v >> (8 * i)) & 0xFF)); };
    uint32_t proSize = 4 + 2 + 2 + 2 + xmlLen;
    put32le(proSize); put16le(1); put16le(1); put16le((uint16_t)xmlLen);
    pro.insert(pro.end(), xmlBytes, xmlBytes + xmlLen);

    // pssh box (version 0): [u32 size]['pssh'][u32 version+flags=0][SystemID 16][u32 dataSize][PRO].
    static const uint8_t prSystemId[16] = { 0x9A,0x04,0xF0,0x79,0x98,0x40,0x42,0x86,0xAB,0x92,0xE6,0x5B,0xE0,0x88,0x5F,0x95 };
    std::vector<uint8_t> box;
    uint32_t boxSize = 8 + 4 + 16 + 4 + (uint32_t)pro.size();
    auto putBE32 = [&](uint32_t v) { box.push_back((uint8_t)(v >> 24)); box.push_back((uint8_t)(v >> 16)); box.push_back((uint8_t)(v >> 8)); box.push_back((uint8_t)v); };
    putBE32(boxSize);
    box.push_back('p'); box.push_back('s'); box.push_back('s'); box.push_back('h');
    putBE32(0);   // version+flags
    box.insert(box.end(), prSystemId, prSystemId + 16);
    putBE32((uint32_t)pro.size());
    box.insert(box.end(), pro.begin(), pro.end());
    return box;
}
