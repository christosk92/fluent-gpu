// CencMediaSource.h — the LAST MILE for native PlayReady video: a custom fragmented-MP4 / CENC demuxer + a custom
// IMFMediaSource/IMFMediaStream that emits ENCRYPTED IMFSamples (carrying the CENC per-sample attributes) to the
// media engine's protected pipeline + CDM, exactly like Microsoft's MediaEngineEMEUWPSample `CdmMediaSource`.
//
// Why this exists: the built-in IMFMediaSourceExtension (MSE) rejects protected byte streams
// (MF_E_UNSUPPORTED_BYTESTREAM_TYPE / MF_E_DRM_UNSUPPORTED — see the README/design-doc findings), and a URL/byte-stream
// SetSource hard-wedges the PMP protected pipeline. Microsoft's sample instead demuxes fMP4/CENC IN-APP and hands the
// engine already-encrypted samples with the CENC metadata the CDM needs to decrypt. This is that source.
//
// This header is #included from Helper.cpp (FG_UWP build) AFTER all the shared helpers it leans on are defined:
//   LogLine, HttpGetBytes, CreateAndPrepareCdm, MediaEngineProtectionManager, EmeNeedKeyNotify, MediaEngineNotify,
//   CdmSessionCallbacks, HandleCdmKeyMessage, QueryCdmKeyStatus, WriteCoord, StopRequested, and the g_* CDM globals.
//
// Scope of the demuxer (H.264 video, single track): moov{trak/mdia(mdhd)/minf/stbl/stsd(encv|avc1 → avcC + sinf →
// schm(cenc/cbcs)/schi/tenc)}, pssh (PlayReady init data), and per media segment moof{traf/tfhd/trun/senc}+mdat.

#pragma once

#include <cstdint>
#include <cstring>
#include <vector>
#include <string>
#include <mutex>
#include <functional>
#include <algorithm>

// ── MF_MT_PROTECTED is not in the 26100 SDK headers; its documented GUID (media type "content is protected"). ──
// {5FA1B54B-B61A-4d76-A99B-8FD7F0EA8F55}
static const GUID FG_MF_MT_PROTECTED = { 0x5FA1B54B, 0xB61A, 0x4d76, { 0xA9, 0x9B, 0x8F, 0xD7, 0xF0, 0xEA, 0x8F, 0x55 } };

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
struct InitInfo
{
    uint32_t codec4cc = 0;             // original sample format (e.g. 'avc1'/'avc3') from frma, else stsd entry type
    std::vector<uint8_t> avcC;         // raw AVCDecoderConfigurationRecord (from the 'avcC' box)
    std::vector<uint8_t> spspps;       // SPS+PPS as Annex-B (for MF_MT_MPEG_SEQUENCE_HEADER)
    uint32_t width = 0, height = 0;
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
            if (info.codec4cc == 0 && b.type != fourcc("encv")) info.codec4cc = b.type;
            ParseVisualSampleEntry(b, info);
            done = true;
        }
    });
}

// Parse the whole init segment (moov + any pssh).
static bool ParseInit(const std::vector<uint8_t>& data, InitInfo& info)
{
    Box moov;
    if (!FindBox(data.data(), data.size(), fourcc("moov"), moov)) return false;
    // Collect pssh boxes at moov level (PlayReady init data for GenerateRequest).
    ForEachBox(moov.payload, moov.payloadLen, [&](const Box& b) {
        if (b.type == fourcc("pssh")) info.pssh.insert(info.pssh.end(), b.boxStart, b.boxStart + b.boxLen);
    });
    // trak → mdia → (mdhd timescale) + minf → stbl → stsd.
    bool ok = false;
    ForEachBox(moov.payload, moov.payloadLen, [&](const Box& trak) {
        if (trak.type != fourcc("trak")) return;
        Box mdia;
        if (!FindBox(trak.payload, trak.payloadLen, fourcc("mdia"), mdia)) return;
        Box mdhd;
        if (FindBox(mdia.payload, mdia.payloadLen, fourcc("mdhd"), mdhd) && mdhd.payloadLen >= 20)
        {
            uint8_t v = mdhd.payload[0];
            info.timescale = v == 1 ? rd32(mdhd.payload + 4 + 16) : rd32(mdhd.payload + 4 + 8);
        }
        Box minf, stbl, stsd;
        if (FindBox(mdia.payload, mdia.payloadLen, fourcc("minf"), minf) &&
            FindBox(minf.payload, minf.payloadLen, fourcc("stbl"), stbl) &&
            FindBox(stbl.payload, stbl.payloadLen, fourcc("stsd"), stsd))
        {
            ParseStsd(stsd, info);
            ok = info.width > 0 && !info.avcC.empty();
        }
    });
    if (ok) ExtractSpsPps(info.avcC, info);
    if (info.timescale == 0) info.timescale = 90000;
    return ok;
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

// Parse one media segment (moof + mdat) and append its samples. runningDecodeTicks tracks decode time across segments.
static int ParseSegment(const std::vector<uint8_t>& seg, const InitInfo& info, std::vector<Sample>& out, uint64_t& runningDecodeTicks)
{
    // Locate moof + mdat at the top level (record moof's absolute start for trun data_offset base).
    Box moof{}, mdat{}; bool haveMoof = false, haveMdat = false; size_t moofAbs = 0;
    ForEachBox(seg.data(), seg.size(), [&](const Box& b) {
        if (b.type == fourcc("moof") && !haveMoof) { moof = b; haveMoof = true; moofAbs = (size_t)(b.boxStart - seg.data()); }
        else if (b.type == fourcc("mdat") && !haveMdat) { mdat = b; haveMdat = true; }
    });
    if (!haveMoof || !haveMdat) return 0;

    Box traf;
    if (!FindBox(moof.payload, moof.payloadLen, fourcc("traf"), traf)) return 0;

    // tfhd — defaults + base offset flags.
    uint32_t defSampleDur = 0, defSampleSize = 0, defSampleFlags = 0;
    bool defaultBaseIsMoof = false; uint64_t baseDataOffset = 0; bool haveBaseDataOffset = false;
    Box tfhd;
    if (FindBox(traf.payload, traf.payloadLen, fourcc("tfhd"), tfhd) && tfhd.payloadLen >= 8)
    {
        uint32_t flags = rd32(tfhd.payload) & 0x00FFFFFF;
        const uint8_t* p = tfhd.payload + 8; // skip version/flags(4) + track_ID(4)
        if (flags & 0x000001) { baseDataOffset = rd64(p); haveBaseDataOffset = true; p += 8; }
        if (flags & 0x000002) { p += 4; } // sample_description_index
        if (flags & 0x000008) { defSampleDur = rd32(p); p += 4; }
        if (flags & 0x000010) { defSampleSize = rd32(p); p += 4; }
        if (flags & 0x000020) { defSampleFlags = rd32(p); p += 4; }
        defaultBaseIsMoof = (flags & 0x020000) != 0;
    }

    // tfdt — base media decode time (optional).
    Box tfdt;
    if (FindBox(traf.payload, traf.payloadLen, fourcc("tfdt"), tfdt) && tfdt.payloadLen >= 8)
        runningDecodeTicks = tfdt.payload[0] == 1 ? rd64(tfdt.payload + 4) : rd32(tfdt.payload + 4);

    // trun — per-sample sizes/durations/flags/composition offsets.
    Box trun;
    if (!FindBox(traf.payload, traf.payloadLen, fourcc("trun"), trun) || trun.payloadLen < 8) return 0;
    uint32_t trFlags = rd32(trun.payload) & 0x00FFFFFF;
    uint32_t sampleCount = rd32(trun.payload + 4);
    const uint8_t* tp = trun.payload + 8;
    int32_t dataOffset = 0; bool haveDataOffset = false;
    if (trFlags & 0x000001) { dataOffset = (int32_t)rd32(tp); tp += 4; haveDataOffset = true; }
    uint32_t firstSampleFlags = 0; bool haveFirstFlags = false;
    if (trFlags & 0x000004) { firstSampleFlags = rd32(tp); tp += 4; haveFirstFlags = true; }

    // senc — per-sample IVs + subsample mapping (inline aux info).
    struct SencEntry { std::vector<uint8_t> iv; std::vector<Subsample> subs; };
    std::vector<SencEntry> senc;
    Box sencBox;
    bool haveSenc = FindBox(traf.payload, traf.payloadLen, fourcc("senc"), sencBox);
    if (haveSenc && sencBox.payloadLen >= 8)
    {
        uint32_t sflags = rd32(sencBox.payload) & 0x00FFFFFF;
        uint32_t count = rd32(sencBox.payload + 4);
        const uint8_t* sp = sencBox.payload + 8;
        const uint8_t* send = sencBox.payload + sencBox.payloadLen;
        uint8_t ivSize = info.perSampleIvSize ? info.perSampleIvSize : 0;
        for (uint32_t i = 0; i < count && sp <= send; i++)
        {
            SencEntry e;
            if (ivSize > 0) { if (sp + ivSize > send) break; e.iv.assign(sp, sp + ivSize); sp += ivSize; }
            if (sflags & 0x000002)
            {
                if (sp + 2 > send) break;
                uint16_t subCount = rd16(sp); sp += 2;
                for (uint16_t s = 0; s < subCount && sp + 6 <= send; s++)
                {
                    Subsample ss; ss.clearBytes = rd16(sp); ss.encBytes = rd32(sp + 2); sp += 6;
                    e.subs.push_back(ss);
                }
            }
            senc.push_back(std::move(e));
        }
    }

    // Sample data base: default-base-is-moof => moof start; else explicit base-data-offset; else 0 (segment-relative).
    size_t base = defaultBaseIsMoof ? moofAbs : (haveBaseDataOffset ? (size_t)baseDataOffset : moofAbs);
    size_t cursor = base + (haveDataOffset ? (size_t)(int64_t)dataOffset : 0);

    int produced = 0;
    for (uint32_t i = 0; i < sampleCount; i++)
    {
        uint32_t sz = defSampleSize, dur = defSampleDur, flags = defSampleFlags;
        // Per-sample fields in order: duration, size, flags, composition-offset.
        if (trFlags & 0x000100) { dur = rd32(tp); tp += 4; }
        if (trFlags & 0x000200) { sz = rd32(tp); tp += 4; }
        if (trFlags & 0x000400) { flags = rd32(tp); tp += 4; }
        int64_t cto = 0;
        if (trFlags & 0x000800) { cto = (int32_t)rd32(tp); tp += 4; }
        if (i == 0 && haveFirstFlags) flags = firstSampleFlags;

        if (cursor + sz > seg.size()) break;
        Sample s;
        s.data.assign(seg.data() + cursor, seg.data() + cursor + sz);
        s.durTicks = dur;
        s.decodeTicks = runningDecodeTicks;
        int64_t t = (int64_t)s.decodeTicks + cto;
        s.timeTicks = t < 0 ? 0 : (uint64_t)t;
        s.keyframe = (flags & 0x00010000) == 0;   // sample_is_non_sync_sample bit clear => sync/keyframe
        s.encrypted = info.encrypted;

        // IV: per-sample from senc, else constant IV (cbcs). Preserve the declared byte length exactly.
        // MFSampleExtension_Encryption_SampleID expects m_bIVSize bytes; padding an 8-byte CENC IV to 16 changes
        // the counter block interpreted by the PlayReady decryptor and leaves the decoder with ciphertext.
        std::vector<uint8_t> iv;
        const std::vector<uint8_t>* src = nullptr;
        if (i < senc.size() && !senc[i].iv.empty()) src = &senc[i].iv;
        else if (!info.constIv.empty()) src = &info.constIv;
        if (src) iv = *src;
        s.iv = std::move(iv);
        if (i < senc.size()) s.subsamples = senc[i].subs;

        // The decoder accepts Annex-B, not the MP4/AVCC payload stored in mdat. Four-byte replacement preserves every
        // CENC byte offset. Refuse malformed/unsupported samples rather than delivering a packet the decoder can only
        // report later as the opaque MF_E_INVALIDREQUEST (0xC00D36B2).
        if (!ConvertAvccToAnnexBInPlace(s, info.nalLenSize))
        {
            LogLine("[cenc] AVCC->AnnexB failed sample=" + std::to_string(i) +
                    " nalLenSize=" + std::to_string(info.nalLenSize) +
                    " bytes=" + std::to_string(s.data.size()) +
                    " subs=" + std::to_string(s.subsamples.size()));
            break;
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

        out.push_back(std::move(s));
        cursor += sz;
        runningDecodeTicks += dur;
        produced++;
    }
    return produced;
}

} // namespace cenc

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
    bool m_started = false, m_eos = false, m_shutdown = false;
    std::mutex m_mx;

    CencMediaStream() { winrt::check_hresult(MFCreateEventQueue(m_queue.put())); }

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
        if (m_next >= m_samples.size())
        {
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
        if (m_next == 0) sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE);
        if (m_next == 0 || (m_next % 100) == 0) LogLine("[cenc-src] RequestSample #" + std::to_string(m_next) + " (encrypted sample delivered)");
        m_next++;
        return m_queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.get());
    }

    void Start(const PROPVARIANT* startPos)
    {
        std::lock_guard<std::mutex> g(m_mx);
        m_started = true; m_eos = false;
        m_queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, startPos);
    }
    void Stop() { std::lock_guard<std::mutex> g(m_mx); m_started = false; m_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr); }
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
    winrt::com_ptr<CencMediaStream> m_stream;
    winrt::com_ptr<IMFTrustedInput> m_trustedInput;
    winrt::com_ptr<IMFInputTrustAuthority> m_inputTrustAuthority;   // one stable ITA per stream (PMP contract)
    DWORD m_inputTrustAuthorityStreamId = UINT32_MAX;
    bool m_started = false, m_shutdown = false;
    std::mutex m_mx;

    CencMediaSource() { winrt::check_hresult(MFCreateEventQueue(m_queue.put())); }

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
        *pdw = MFMEDIASOURCE_CAN_PAUSE;
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
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;

        PROPVARIANT startVar; PropVariantInit(&startVar);
        if (startPos && (startPos->vt == VT_I8 || startPos->vt == VT_EMPTY)) PropVariantCopy(&startVar, startPos);
        else { startVar.vt = VT_I8; startVar.hVal.QuadPart = 0; }

        bool isNew = !m_started;
        // Raise MENewStream/MEUpdatedStream on the SOURCE for the (single) stream, then start the stream.
        if (pd)
        {
            BOOL selected = FALSE; winrt::com_ptr<IMFStreamDescriptor> sd;
            if (SUCCEEDED(pd->GetStreamDescriptorByIndex(0, &selected, sd.put())) && selected && m_stream)
            {
                if (isNew)
                    m_queue->QueueEventParamUnk(MENewStream, GUID_NULL, S_OK, (::IUnknown*)(IMFMediaStream*)m_stream.get());
                else
                    m_queue->QueueEventParamUnk(MEUpdatedStream, GUID_NULL, S_OK, (::IUnknown*)(IMFMediaStream*)m_stream.get());
                m_stream->Start(&startVar);
            }
        }
        m_started = true;
        m_queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &startVar);
        PropVariantClear(&startVar);
        return S_OK;
    }
    IFACEMETHODIMP Stop() noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        m_started = false;
        if (m_stream) m_stream->Stop();
        return m_queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
    }
    IFACEMETHODIMP Pause() noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        return m_queue->QueueEventParamVar(MESourcePaused, GUID_NULL, S_OK, nullptr);
    }
    IFACEMETHODIMP Shutdown() noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        m_shutdown = true;
        if (m_stream) m_stream->Shutdown();
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

        // The topology loader can ask repeatedly while resolving the protected branch. Keep the same per-stream ITA;
        // creating a fresh authority on every query resets PlayReady's policy/decrypter state and is rejected as
        // DRM_E_LOGICERR. This mirrors Firefox's MFCDMProxy cache.
        HRESULT hr;
        if (m_inputTrustAuthority && m_inputTrustAuthorityStreamId == streamId)
        {
            // GetInputTrustAuthority's output is IUnknown**, even though riid normally requests
            // IMFInputTrustAuthority. Copy the cached interface pointer directly: querying another interface into
            // this output can produce a proxy/vtable mismatch across the PMP boundary. This deliberately mirrors
            // Firefox's MFCDMProxy rather than wrapping or re-querying the CDM-owned proxy.
            *value = static_cast<IUnknown*>(m_inputTrustAuthority.get());
            (*value)->AddRef();
            hr = S_OK;
        }
        else
        {
            winrt::com_ptr<IUnknown> unknown;
            hr = m_trustedInput->GetInputTrustAuthority(streamId, riid, unknown.put());
            if (SUCCEEDED(hr))
            {
                winrt::com_ptr<IMFInputTrustAuthority> inner;
                hr = unknown->QueryInterface(IID_PPV_ARGS(inner.put()));
                if (SUCCEEDED(hr))
                {
                    m_inputTrustAuthority = std::move(inner);
                    m_inputTrustAuthorityStreamId = streamId;
                    // Preserve the exact COM proxy identity/vtable returned by the CDM on the first request.
                    *value = unknown.detach();
                    hr = S_OK;
                }
            }
        }
        std::stringstream ss; ss << "[cenc-src] GetInputTrustAuthority stream=" << streamId
                                 << " hr=0x" << std::hex << (uint32_t)hr;
        LogLine(ss.str());
        return hr;
    }

    void QueueSourceEndOfStream() { std::lock_guard<std::mutex> g(m_mx); if (!m_shutdown) m_queue->QueueEventParamVar(MEEndOfPresentation, GUID_NULL, S_OK, nullptr); }
};

inline void CencMediaStream::NotifySourceEnded()
{
    if (m_source) static_cast<CencMediaSource*>(m_source)->QueueSourceEndOfStream();
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Factory: build the media type / stream descriptor / presentation descriptor and wire the demuxed samples.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
static winrt::com_ptr<CencMediaSource> BuildCencSource(const cenc::InitInfo& info, std::vector<cenc::Sample>&& samples)
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
    // experiment: stamping the raw MF_MT_PROTECTED *attribute* on an UNWRAPPED clear type (FG_CENC_MARK_SD_PROTECTED=1
    // diagnostics) selects the legacy ITA/OTA topology whose trust verification fails 0xC00D715B. The wrapped type is
    // unwrapped by the pipeline (MFUnwrapMediaType) after the decryptor, so the decoder still sees the real H.264 type.
    // Set FG_CENC_NO_PROTECTED_WRAP=1 to A/B the old clear-typed wiring.
    bool markProtected = info.encrypted && GetEnvironmentVariableW(L"FG_CENC_MARK_SD_PROTECTED", nullptr, 0) != 0;
    if (markProtected) mt->SetUINT32(FG_MF_MT_PROTECTED, TRUE);

    winrt::com_ptr<IMFMediaType> streamType = mt;
    bool wrapProtected = info.encrypted && !markProtected &&
                         GetEnvironmentVariableW(L"FG_CENC_NO_PROTECTED_WRAP", nullptr, 0) == 0;
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
    if (markProtected || wrapProtected) sd->SetUINT32(MF_SD_PROTECTED, 1);
    {
        winrt::com_ptr<IMFMediaTypeHandler> mth;
        winrt::check_hresult(sd->GetMediaTypeHandler(mth.put()));
        winrt::check_hresult(mth->SetCurrentMediaType(streamType.get()));
    }

    winrt::com_ptr<IMFPresentationDescriptor> pd;
    IMFStreamDescriptor* sds[1] = { sd.get() };
    winrt::check_hresult(MFCreatePresentationDescriptor(1, sds, pd.put()));
    pd->SelectStream(0);
    if (!samples.empty())
    {
        uint64_t endTicks = 0;
        for (auto const& sample : samples)
        {
            uint64_t sampleEnd = sample.timeTicks + sample.durTicks;
            if (sampleEnd > endTicks) endTicks = sampleEnd;
        }
        pd->SetUINT64(MF_PD_DURATION, (UINT64)((endTicks * 10000000ULL) / info.timescale));
    }

    auto source = winrt::make_self<CencMediaSource>();
    auto stream = winrt::make_self<CencMediaStream>();
    stream->m_sd = sd;
    stream->m_source = (IMFMediaSource*)source.get();   // weak — source holds the strong ref
    stream->m_info = info;
    stream->m_samples = std::move(samples);
    source->m_pd = pd;
    source->m_stream = stream;

    LogLine("[cenc-src] built source: " + std::to_string(info.width) + "x" + std::to_string(info.height) +
            " scheme=" + std::string(info.scheme == 1 ? "cbcs" : "cenc") +
            " ivSize=" + std::to_string((int)info.perSampleIvSize) +
            " spspps=" + std::to_string(info.spspps.size()) + "B samples=" + std::to_string(stream->m_samples.size()));
    (void)hx;
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
