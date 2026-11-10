using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentGpu.Foundation;

namespace FluentGpu.Media.Windows;

/// <summary>
/// A gapless loop for a short local MP4: the same clip <c>N</c> times back to back on ONE continuous timeline (its media data written
/// <c>N</c> times and its sample tables repeated: the layout <c>ffmpeg -stream_loop</c> produces).
/// <para><b>Why.</b> The Media Engine's own loop (<c>IMFMediaEngine::SetLoop</c>) restarts at the source's end of stream: it seeks to 0 and
/// flushes the pipeline while the last frames of the clip are still queued in the decoder and the renderer. Measured on a 7.16 s
/// 1080x1920 H.264 canvas (25 fps, B-frames; <see cref="LoopSeamProbe"/>, <c>[video] loop.seam</c>): the playhead jumped back to 0 from
/// 6.90-6.99 s every time, so each loop ran 6.916 s instead of 7.16 s: the clip's last ~244 ms (six frame periods) were cut or crushed at
/// every wrap, the "1-2 frames of stutter as it loops". A seek can never be gapless; a continuous timeline is (same clip: every loop
/// 7.153-7.173 s, 178-180 frames presented of the clip's 179, the playhead running straight through each seam): the
/// decoder just keeps decoding (the clip starts on a sync sample), the clock never stops, nothing is flushed. ExoPlayer's looping and
/// concatenating sources do the same at the stream level. The engine's own loop stays on as the backstop at the end of the
/// <c>N</c>-th repetition (ten minutes in for a small canvas, at least <see cref="MinSeconds"/> for the largest).</para>
/// <para><b>Why the data is written N times.</b> Repetitions that share ONE copy of the media data (every repetition's chunk offsets
/// pointing back at the same bytes: a far smaller file) are valid MP4 and decode in ffmpeg, but the Media Foundation MPEG-4 source
/// presented only a couple of frames of such a file (measured: 2 frames in the first 7 s), while the same clip concatenated with separate
/// copies (<c>ffmpeg -stream_loop 3 -c copy</c>) played continuously. So each repetition is a copy, and a loop file holds
/// <see cref="MaxBytes"/> of media at most.</para>
/// <para><b>Scope.</b> A progressive (non-fragmented) MP4 with exactly ONE track (a canvas, a silent background clip) and the sample tables
/// this rewrites: stts, ctts, stss, stsc, stsz, stco/co64, sdtp. Anything else (a second track, a fragmented file, stz2, sample groups,
/// sub-sample info) returns null with the reason, and the caller loops natively as before. Durations (mvhd, tkhd, mdhd, the edit list's
/// last segment) grow by <c>(N-1)</c> clips; an empty edit at the start stays a one-time delay.</para>
/// <para>The rewritten file lives in a per-user temp folder keyed by the source's path, size and write time, so a clip that loops again
/// (a skip back, a relaunch) reuses it; the folder keeps <see cref="FolderBytes"/> at most (the least recently written go first). Any
/// thread; pure managed I/O.</para>
/// </summary>
public static class Mp4LoopRemux
{
    /// <summary>The prepared loop: the file to play, the clip's length (seconds between seams) and the repetitions.</summary>
    public readonly record struct Result(string Path, double ClipSeconds, int Repeats);

    /// <summary>How long the repeated timeline aims to run before the engine's own loop takes over.</summary>
    public const double TargetSeconds = 600;

    /// <summary>The most samples the repeated tables may hold (they cost MF memory per sample at open).</summary>
    public const int MaxSamples = 60_000;

    /// <summary>The most media data one loop file holds (each repetition is a copy).</summary>
    public const long MaxBytes = 48L << 20;

    /// <summary>A clip whose budget buys less timeline than this loops natively (the repetitions would buy too little).</summary>
    public const double MinSeconds = 20;

    /// <summary>The loop folder's size cap.</summary>
    public const long FolderBytes = 256L << 20;

    private const long MaxSourceBytes = 64L << 20;

    private static readonly ConcurrentDictionary<string, Result?> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The gapless loop of <paramref name="path"/>, or null (the reason is logged once) when the file is not one this can repeat.</summary>
    public static Result? TryPrepare(string path)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) return null; }
        catch { return null; }
        string key = fi.FullName + "|" + fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        if (s_cache.TryGetValue(key, out var hit))
        {
            // A remembered loop is only as good as its file: Prune (below) deletes old loops to make room, and a player handed a
            // path that is gone opens nothing and shows a frozen poster for the whole track. Gone: forget it and build it again.
            if (hit is not { } known || Reuse(known.Path)) return hit;
            s_cache.TryRemove(key, out _);
            Diag.Line("[video] loop.gapless rebuilt file=" + fi.Name + " why=pruned");
        }
        Result? result = null;
        var sw = Stopwatch.StartNew();
        try { result = Prepare(fi, key, out string why); if (result is null) Diag.Line("[video] loop.gapless skip file=" + fi.Name + " why=" + why); }
        catch (Exception ex) { Diag.Line("[video] loop.gapless failed file=" + fi.Name + " " + ex.GetType().Name + ": " + ex.Message); }
        if (result is { } r)
            Diag.Line(string.Create(CultureInfo.InvariantCulture, $"[video] loop.gapless file={fi.Name} clipS={r.ClipSeconds:0.000} repeats={r.Repeats} ms={sw.Elapsed.TotalMilliseconds:0}"));
        s_cache[key] = result;
        return result;
    }

    private static Result? Prepare(FileInfo fi, string key, out string why)
    {
        why = "";
        if (fi.Length > MaxSourceBytes) { why = "too-large"; return null; }
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FluentGpu", "loops");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 12);
        byte[] src = File.ReadAllBytes(fi.FullName);
        var plan = Plan.Build(src, out why);
        if (plan is null) return null;
        string target = System.IO.Path.Combine(dir, hash + "-x" + plan.Repeats.ToString(CultureInfo.InvariantCulture) + ".mp4");
        var result = new Result(target, plan.ClipSeconds, plan.Repeats);
        if (Reuse(target)) return result;
        Directory.CreateDirectory(dir);
        Prune(dir, plan.OutputBytes);
        string tmp = target + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        plan.Write(tmp);
        File.Move(tmp, target, overwrite: true);
        return result;
    }

    /// <summary>Is the loop file at <paramref name="target"/> still there? A file that is gets its write time renewed, so
    /// <see cref="Prune"/> (oldest write first) evicts by last USE: a loop played an hour ago outlives one made yesterday and never
    /// played since.</summary>
    private static bool Reuse(string target)
    {
        try
        {
            if (!File.Exists(target)) return false;
            try { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); } catch { }   // held open by a player: it stays put anyway
            return true;
        }
        catch { return false; }
    }

    /// <summary>Make room for <paramref name="incoming"/> bytes under <see cref="FolderBytes"/>: the least recently written loops go first
    /// (best effort: a file a player holds open stays).</summary>
    private static void Prune(string dir, long incoming)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles();
            Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            long total = incoming;
            foreach (var f in files) total += f.Length;
            foreach (var f in files)
            {
                if (total <= FolderBytes) break;
                try { long len = f.Length; f.Delete(); total -= len; } catch { }
            }
        }
        catch { }
    }

    // ── the box model ────────────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct Box(string Type, int Start, int Header, int Size)
    {
        public int End => Start + Size;
        public int Body => Start + Header;
    }

    private static List<Box> Children(byte[] b, int start, int end)
    {
        var list = new List<Box>();
        int o = start;
        while (o + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
            int header = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o + 8)); header = 16; }
            else if (size == 0) size = end - o;
            if (size < header || o + size > end) throw new InvalidDataException("box overruns its parent");
            list.Add(new Box(Encoding.ASCII.GetString(b, o + 4, 4), o, header, (int)size));
            o += (int)size;
        }
        return list;
    }

    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));

    private sealed class Plan
    {
        public required byte[] Src;
        public required List<Box> Top;
        public required Box Moov;
        public required int Repeats;
        public required double ClipSeconds;
        // The clip's decode span (the stts sum) in the track's and the movie's timescale.
        public required ulong ClipMedia, ClipMovie;
        public required int SampleCount, ChunkCount;
        // Every top-level box but ftyp and moov (the media data and whatever sits beside it): written once per repetition.
        public required long MediaBytes;

        public long OutputBytes => MediaBytes * Repeats;

        public static Plan? Build(byte[] src, out string why)
        {
            why = "";
            var top = Children(src, 0, src.Length);
            Box? moov = null;
            foreach (var t in top)
            {
                if (t.Type == "moov") moov = t;
                if (t.Type == "moof") { why = "fragmented"; return null; }
            }
            if (moov is not { } m) { why = "no-moov"; return null; }
            var kids = Children(src, m.Body, m.End);
            Box? trak = null; uint movieScale = 0;
            foreach (var k in kids)
            {
                if (k.Type == "mvex") { why = "fragmented"; return null; }
                if (k.Type == "trak") { if (trak is not null) { why = "several-tracks"; return null; } trak = k; }
                if (k.Type == "mvhd") movieScale = src[k.Body] == 1 ? U32(src, k.Body + 20) : U32(src, k.Body + 12);
            }
            if (trak is not { } tr || movieScale == 0) { why = "no-track"; return null; }
            var stbl = Find(src, tr, "mdia", "minf", "stbl");
            var mdhd = Find(src, tr, "mdia", "mdhd");
            if (stbl is not { } st || mdhd is not { } md) { why = "no-stbl"; return null; }
            uint mediaScale = src[md.Body] == 1 ? U32(src, md.Body + 20) : U32(src, md.Body + 12);
            if (mediaScale == 0) { why = "no-timescale"; return null; }
            ulong clipMedia = 0; int samples = 0, chunks = 0; bool stsz = false, stco = false, stts = false;
            foreach (var k in Children(src, st.Body, st.End))
            {
                switch (k.Type)
                {
                    case "stsd": case "ctts": case "stss": case "stsc": case "sdtp": case "cslg": break;
                    case "stts":
                        stts = true;
                        for (uint i = 0, n = U32(src, k.Body + 4); i < n; i++)
                        {
                            int e = k.Body + 8 + (int)i * 8;
                            clipMedia += (ulong)U32(src, e) * U32(src, e + 4);
                        }
                        break;
                    case "stsz": stsz = true; samples = (int)U32(src, k.Body + 8); break;
                    case "stco": case "co64": stco = true; chunks = (int)U32(src, k.Body + 4); break;
                    default: why = "table-" + k.Type; return null;
                }
            }
            if (!stts || !stsz || !stco || samples == 0 || clipMedia == 0) { why = "incomplete-tables"; return null; }
            double clip = clipMedia / (double)mediaScale;
            long media = 0;
            foreach (var t in top) if (t.Type is not ("ftyp" or "moov")) media += t.Size;
            int repeats = (int)Math.Min(Math.Min(Math.Ceiling(TargetSeconds / clip), MaxSamples / samples), MaxBytes / Math.Max(1, media));
            if (repeats < 2 || repeats * clip < MinSeconds) { why = "clip-too-long"; return null; }
            ulong clipMovie = (ulong)Math.Round(clip * movieScale);
            return new Plan
            {
                Src = src, Top = top, Moov = m, Repeats = repeats, ClipSeconds = clip, ClipMedia = clipMedia, ClipMovie = clipMovie,
                SampleCount = samples, ChunkCount = chunks, MediaBytes = media,
            };
        }

        private static Box? Find(byte[] b, Box from, params string[] path)
        {
            Box cur = from;
            foreach (string p in path)
            {
                Box? next = null;
                foreach (var k in Children(b, cur.Body, cur.End)) if (k.Type == p) { next = k; break; }
                if (next is not { } n) return null;
                cur = n;
            }
            return cur;
        }

        // ── writing ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Write ftyp, the new moov, then every other top-level box in its original order once per repetition (the media data
        /// verbatim) to <paramref name="file"/>.</summary>
        public void Write(string file)
        {
            // The moov's size depends only on the entry counts, so it is built once with placeholder offsets, then again with the real ones.
            var map = new List<(int OldStart, int OldEnd, long NewStart)>();
            byte[] probe = BuildMoov(static (_, _) => 0);
            long pos = 0;
            foreach (var t in Top) if (t.Type == "ftyp") pos += t.Size;
            pos += probe.Length;
            foreach (var t in Top)
            {
                if (t.Type is "ftyp" or "moov") continue;
                map.Add((t.Start, t.End, pos));
                pos += t.Size;
            }
            // Repetition r's copy of a box sits r media blocks after the first copy.
            long Remap(int r, ulong old)
            {
                foreach (var (s, e, n) in map) if (old >= (ulong)s && old < (ulong)e) return n + (long)old - s + r * MediaBytes;
                throw new InvalidDataException("a chunk offset outside every box");
            }
            byte[] moov = BuildMoov(Remap);
            if (moov.Length != probe.Length) throw new InvalidDataException("moov size changed between passes");
            using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            foreach (var t in Top) if (t.Type == "ftyp") fs.Write(Src, t.Start, t.Size);
            fs.Write(moov);
            for (int r = 0; r < Repeats; r++)
                foreach (var t in Top) if (t.Type is not ("ftyp" or "moov")) fs.Write(Src, t.Start, t.Size);
        }

        private byte[] BuildMoov(Func<int, ulong, long> remap)
        {
            using var ms = new MemoryStream();
            WriteContainer(ms, Moov, remap);
            return ms.ToArray();
        }

        private static readonly HashSet<string> s_containers = ["moov", "trak", "mdia", "minf", "stbl", "edts"];

        private void WriteContainer(MemoryStream ms, Box box, Func<int, ulong, long> remap)
        {
            using var body = new MemoryStream();
            foreach (var k in Children(Src, box.Body, box.End))
            {
                if (s_containers.Contains(k.Type)) { WriteContainer(body, k, remap); continue; }
                byte[]? rewritten = Rewrite(k, remap);
                if (rewritten is null) body.Write(Src, k.Start, k.Size);
                else WriteBox(body, k.Type, rewritten);
            }
            WriteBox(ms, box.Type, body.ToArray());
        }

        private static void WriteBox(Stream s, string type, ReadOnlySpan<byte> body)
        {
            Span<byte> h = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(h, (uint)(body.Length + 8));
            Encoding.ASCII.GetBytes(type, h[4..]);
            s.Write(h);
            s.Write(body);
        }

        /// <summary>The box's new body, or null to copy it as it is.</summary>
        private byte[]? Rewrite(Box k, Func<int, ulong, long> remap)
        {
            int n = Repeats, b0 = k.Body;
            byte[] s = Src;
            var w = new BodyWriter();
            switch (k.Type)
            {
                case "mvhd": return Duration(k, s[b0] == 1 ? 24 : 16, ClipMovie);
                case "tkhd": return Duration(k, s[b0] == 1 ? 28 : 20, ClipMovie);
                case "mdhd": return Duration(k, s[b0] == 1 ? 24 : 16, ClipMedia);
                case "elst":
                {
                    byte[] body = s.AsSpan(b0, k.Size - k.Header).ToArray();
                    int count = (int)U32(s, b0 + 4), stride = s[b0] == 1 ? 20 : 12;
                    if (count == 0) return null;
                    int last = 8 + (count - 1) * stride;
                    if (s[b0] == 1) BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(last), U64(s, b0 + last) + ClipMovie * (ulong)(n - 1));
                    else BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(last), Checked32(U32(s, b0 + last) + ClipMovie * (ulong)(n - 1)));
                    return body;
                }
                case "stts":
                {
                    // Repeated entries, with the seam's two runs merged when they share a delta.
                    int count = (int)U32(s, b0 + 4);
                    var runs = new List<(uint Count, uint Delta)>(count * 2);
                    for (int r = 0; r < n; r++)
                        for (int i = 0; i < count; i++)
                        {
                            uint c = U32(s, b0 + 8 + i * 8), d = U32(s, b0 + 12 + i * 8);
                            if (runs.Count > 0 && runs[^1].Delta == d) runs[^1] = (runs[^1].Count + c, d);
                            else runs.Add((c, d));
                        }
                    w.Head(s, b0); w.U32((uint)runs.Count);
                    foreach (var (c, d) in runs) { w.U32(c); w.U32(d); }
                    return w.ToArray();
                }
                case "ctts":
                {
                    int count = (int)U32(s, b0 + 4);
                    w.Head(s, b0); w.U32((uint)(count * n));
                    for (int r = 0; r < n; r++) w.Raw(s, b0 + 8, count * 8);
                    return w.ToArray();
                }
                case "stss":
                {
                    int count = (int)U32(s, b0 + 4);
                    w.Head(s, b0); w.U32((uint)(count * n));
                    for (int r = 0; r < n; r++)
                        for (int i = 0; i < count; i++) w.U32(U32(s, b0 + 8 + i * 4) + (uint)(r * SampleCount));
                    return w.ToArray();
                }
                case "stsc":
                {
                    int count = (int)U32(s, b0 + 4);
                    w.Head(s, b0); w.U32((uint)(count * n));
                    for (int r = 0; r < n; r++)
                        for (int i = 0; i < count; i++)
                        {
                            int e = b0 + 8 + i * 12;
                            w.U32(U32(s, e) + (uint)(r * ChunkCount)); w.U32(U32(s, e + 4)); w.U32(U32(s, e + 8));
                        }
                    return w.ToArray();
                }
                case "stsz":
                {
                    uint fixedSize = U32(s, b0 + 4);
                    w.Head(s, b0); w.U32(fixedSize); w.U32((uint)(SampleCount * n));
                    if (fixedSize == 0) for (int r = 0; r < n; r++) w.Raw(s, b0 + 12, SampleCount * 4);
                    return w.ToArray();
                }
                case "stco":
                {
                    w.Head(s, b0); w.U32((uint)(ChunkCount * n));
                    for (int r = 0; r < n; r++)
                        for (int i = 0; i < ChunkCount; i++) w.U32(Checked32((ulong)remap(r, U32(s, b0 + 8 + i * 4))));
                    return w.ToArray();
                }
                case "co64":
                {
                    w.Head(s, b0); w.U32((uint)(ChunkCount * n));
                    for (int r = 0; r < n; r++)
                        for (int i = 0; i < ChunkCount; i++) w.U64((ulong)remap(r, U64(s, b0 + 8 + i * 8)));
                    return w.ToArray();
                }
                case "sdtp":
                {
                    w.Head(s, b0);
                    int bytes = k.Size - k.Header - 4;
                    for (int r = 0; r < n; r++) w.Raw(s, b0 + 4, bytes);
                    return w.ToArray();
                }
                default: return null;
            }
        }

        /// <summary>A full box whose duration field (at <paramref name="at"/> in its body; 64-bit in version 1) grows by N-1 clips.</summary>
        private byte[] Duration(Box k, int at, ulong clip)
        {
            byte[] body = Src.AsSpan(k.Body, k.Size - k.Header).ToArray();
            ulong add = clip * (ulong)(Repeats - 1);
            if (Src[k.Body] == 1) BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(at), U64(Src, k.Body + at) + add);
            else BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(at), Checked32(U32(Src, k.Body + at) + add));
            return body;
        }

        private static uint Checked32(ulong v) => v <= uint.MaxValue ? (uint)v : throw new InvalidDataException("a 32-bit field overflows");
    }

    private sealed class BodyWriter
    {
        private readonly MemoryStream _ms = new();
        public void Head(byte[] s, int body) => _ms.Write(s, body, 4);   // version + flags
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _ms.Write(b); }
        public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _ms.Write(b); }
        public void Raw(byte[] s, int at, int len) => _ms.Write(s, at, len);
        public byte[] ToArray() => _ms.ToArray();
    }
}
