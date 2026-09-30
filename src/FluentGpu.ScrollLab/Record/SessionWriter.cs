using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluentGpu.ScrollLab.Surfaces;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Diag.Analysis;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.ScrollLab.Record;

/// <summary>The host facts a session folder records beside the rows (captured at write time).</summary>
public sealed record LabMeta(float DpiScale, int MaxFrameLatency, string EngineVersion, string OsBuild, string Monitor);

/// <summary>A saved session folder, as the Record page lists it.</summary>
public sealed record SessionInfo(string Folder, string Name, DateTime Written);

/// <summary>
/// Writes/reads a session folder (scroll-lab plan §6) under <c>%LOCALAPPDATA%\FluentGpu\ScrollLab\sessions\
/// &lt;yyyy-MM-dd_HH-mm-ss&gt;-&lt;surface&gt;-&lt;device&gt;\</c>:
/// <list type="bullet">
/// <item><c>meta.json</c> — schema 1: clocks, refresh, dpi, latency, device, surface, feel set, probe level, versions.</item>
/// <item><c>feel.json</c> — <c>{motion, host, presetBase}</c> (<c>motion</c> = <c>ScrollTunables.ToJson</c>).</item>
/// <item><c>events.csv</c> — the analyze.py format (the row-based <c>ScrollProbe.ExportCsv</c>, schema
/// <c>ScrollProbe.CsvSchema</c>): the session's analysed viewport only (the <c>vp</c> column names it on every
/// viewport-bound row), both rings merged in time order; a ring the recorder fell behind on (<c>LostUi</c>/<c>LostRender</c>)
/// is marked wrapped and clips the file to the common window.</item>
/// <item><c>markers.json</c> — every session marker.</item>
/// <item><c>probe-ui.bin</c>/<c>probe-render.bin</c> — the raw drained rows (<c>FGPR</c> header + unmanaged
/// <see cref="ProbeRow"/>s), so a saved session re-analyzes exactly.</item>
/// <item><c>frames.csv</c> + <c>frames.bin</c>/<c>frame-passes.bin</c> — per-UI-frame render census, pacing snapshot and
/// GPU pass timeline (<see cref="FrameLog"/>).</item>
/// <item><c>turns.csv</c> — one row per render-thread present (the Turn rows of <c>probe-render.bin</c>): the tick it was
/// decided for, the missed ticks it charged, its wake lag, present-slot wait and work — what makes each missed tick
/// attributable (<see cref="ScrollMetrics.MissedTickAttribution"/>).</item>
/// </list>
/// All JSON is written/read by hand (Utf8JsonWriter / JsonDocument) — NativeAOT-safe, no reflection serializer.
/// </summary>
public static class SessionWriter
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentGpu", "ScrollLab");

    public static string SessionsDir => Path.Combine(Root, "sessions");

    private static ReadOnlySpan<byte> Magic => "FGPR"u8;
    private const int BinVersion = 1;
    private const int HeaderBytes = 16;

    /// <summary>Writes <paramref name="d"/> and returns the folder.</summary>
    public static string Write(SessionData d, LabMeta meta)
    {
        var series = d.ToSeries();
        double refreshHz = series.RefreshPeriodS > 0.0 ? 1.0 / series.RefreshPeriodS : 60.0;
        string device = DeviceKind(d.Ui);
        string folderName = d.StartedLocal.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)
                            + "-" + ScrollSurfaces.SlugOf(d.Surface) + "-" + device;
        string dir = Path.Combine(SessionsDir, folderName);
        if (Directory.Exists(dir)) dir += "-" + Environment.TickCount64.ToString(CultureInfo.InvariantCulture);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "meta.json"), MetaJson(d, meta, series, refreshHz, device), Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "feel.json"), FeelJson(d), Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "markers.json"), MarkersJson(d), Encoding.UTF8);
        using (var w = new StreamWriter(Path.Combine(dir, "events.csv"), false, new UTF8Encoding(false)))
            ScrollProbe.ExportCsv(w, d.Ui, d.Render, d.QpcFrequency, refreshHz, meta.DpiScale, d.Name, series.Viewport,
                uiWrapped: d.LostUi > 0, renderWrapped: d.LostRender > 0);
        WriteRows(Path.Combine(dir, "probe-ui.bin"), d.Ui);
        WriteRows(Path.Combine(dir, "probe-render.bin"), d.Render);
        using (var w = new StreamWriter(Path.Combine(dir, "frames.csv"), false, new UTF8Encoding(false)))
            FrameLog.WriteCsv(w, d.Frames, d.FramePassMs, d.StartQpc, d.QpcFrequency);
        using (var w = new StreamWriter(Path.Combine(dir, "turns.csv"), false, new UTF8Encoding(false)))
            WriteTurnsCsv(w, d.Render, d.StartQpc, d.QpcFrequency);
        WriteRows(Path.Combine(dir, "frames.bin"), d.Frames);
        WriteRows(Path.Combine(dir, "frame-passes.bin"), d.FramePassMs);
        return dir;
    }

    /// <summary><c>turns.csv</c>: one row per render-thread present; time is ms since <paramref name="startQpc"/>.</summary>
    public static void WriteTurnsCsv(TextWriter w, ReadOnlySpan<ProbeRow> render, long startQpc, double qpcFrequency)
    {
        var ci = CultureInfo.InvariantCulture;
        w.WriteLine("t_ms,tick_seq,missed_ticks,wake_lag_ms,slot_wait_ms,work_ms,span_ms,fresh,paced");
        for (int i = 0; i < render.Length; i++)
        {
            ref readonly ProbeRow r = ref render[i];
            if (r.Kind != ProbeRowKind.Turn) continue;
            w.WriteLine(string.Join(',',
                ((r.Qpc - startQpc) * 1000.0 / qpcFrequency).ToString("0.000", ci),
                r.TickSeq.ToString(ci), r.MissedTicks.ToString(ci),
                r.WakeLagMs.ToString("0.###", ci), r.SlotWaitMs.ToString("0.###", ci), r.WorkMs.ToString("0.###", ci),
                (r.WakeLagMs + r.SlotWaitMs + r.WorkMs).ToString("0.###", ci),
                r.TurnFresh ? "1" : "0", r.TurnPaced ? "1" : "0"));
        }
    }

    /// <summary>Writes the analysis verdicts beside the rows (<c>metrics.json</c>) — what the Analysis page showed, as
    /// data a script or a later session can diff.</summary>
    public static void WriteMetrics(string dir, IReadOnlyList<MetricResult> metrics)
    {
        using var stream = new MemoryStream(2048);
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            static void Num(Utf8JsonWriter w, string name, double v) { if (double.IsFinite(v)) w.WriteNumber(name, v); else w.WriteNull(name); }
            w.WriteStartArray();
            foreach (var m in metrics)
            {
                w.WriteStartObject();
                w.WriteString("name", m.Name);
                Num(w, "value", m.Value);
                Num(w, "baseline", m.Baseline);
                w.WriteString("verdict", m.Verdict.ToString());
                w.WriteString("unit", m.Unit);
                w.WriteNumber("samples", m.Samples);
                Num(w, "worstT", m.WorstT);
                w.WriteNumber("markerHits", m.MarkerHits.Length);
                w.WriteString("detail", m.Detail);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        File.WriteAllBytes(Path.Combine(dir, "metrics.json"), stream.ToArray());
    }

    /// <summary>Reads a folder written by <see cref="Write"/> back into a <see cref="SessionData"/>.</summary>
    public static SessionData Load(string dir)
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")));
        var root = meta.RootElement;
        var ui = ReadRows<ProbeRow>(Path.Combine(dir, "probe-ui.bin"));
        var render = ReadRows<ProbeRow>(Path.Combine(dir, "probe-render.bin"));
        var frames = ReadRows<FrameSample>(Path.Combine(dir, "frames.bin"));
        var framePassMs = ReadRows<float>(Path.Combine(dir, "frame-passes.bin"));
        if (framePassMs.Length != frames.Length * FrameLog.KindCount) framePassMs = new float[frames.Length * FrameLog.KindCount];

        var feel = FeelProfilesFallback();
        string preset = FeelProfiles.All[0].Name;
        string feelPath = Path.Combine(dir, "feel.json");
        if (File.Exists(feelPath))
        {
            using var fd = JsonDocument.Parse(File.ReadAllText(feelPath));
            if (fd.RootElement.TryGetProperty("motion", out var motion) && ScrollTunables.TryFromJson(motion.GetRawText(), out var f)) feel = f;
            if (fd.RootElement.TryGetProperty("presetBase", out var pb) && pb.ValueKind == JsonValueKind.String) preset = pb.GetString() ?? preset;
        }

        return new SessionData
        {
            Name = Str(root, "name", Path.GetFileName(dir)),
            Surface = ParseSurface(Str(root, "surface", "fixed100k")),
            Level = Str(root, "probeLevel", "Trace") == "Summary" ? ProbeLevel.Summary : ProbeLevel.Trace,
            StartQpc = Long(root, "startQpc"),
            EndQpc = Long(root, "endQpc"),
            StartedLocal = DateTime.TryParse(Str(root, "startedLocal", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started) ? started : Directory.GetCreationTime(dir),
            Ui = ui,
            Render = render,
            LostUi = Long(root, "lostUiRows"),
            LostRender = Long(root, "lostRenderRows"),
            Feel = feel,
            PresetBase = preset,
            Viewport = (int)Long(root, "viewport", -1),
            Frames = frames,
            FramePassMs = framePassMs,
            QpcFrequency = root.TryGetProperty("qpcFrequency", out var qf) && qf.TryGetDouble(out double q) && q > 0 ? q : System.Diagnostics.Stopwatch.Frequency,
        };
    }

    /// <summary>Saved sessions, newest first.</summary>
    public static IReadOnlyList<SessionInfo> List(int max = 200)
    {
        var result = new List<SessionInfo>();
        if (!Directory.Exists(SessionsDir)) return result;
        var dirs = Directory.GetDirectories(SessionsDir);
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        for (int i = dirs.Length - 1; i >= 0 && result.Count < max; i--)
        {
            if (!File.Exists(Path.Combine(dirs[i], "meta.json"))) continue;
            string name = Path.GetFileName(dirs[i]);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dirs[i], "meta.json")));
                name = Str(doc.RootElement, "name", name) + "  ·  " + Path.GetFileName(dirs[i]);
            }
            catch (Exception) { /* a torn meta still lists by folder name */ }
            result.Add(new SessionInfo(dirs[i], name, Directory.GetLastWriteTime(dirs[i])));
        }
        return result;
    }

    // ── json ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string MetaJson(SessionData d, LabMeta meta, SessionSeries series, double refreshHz, string device)
    {
        using var stream = new MemoryStream(1024);
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("name", d.Name);
            w.WriteString("startedLocal", d.StartedLocal.ToString("o", CultureInfo.InvariantCulture));
            w.WriteNumber("startQpc", d.StartQpc);
            w.WriteNumber("endQpc", d.EndQpc);
            w.WriteNumber("durationS", d.DurationS);
            w.WriteNumber("qpcFrequency", d.QpcFrequency);
            w.WriteNumber("refreshHz", refreshHz);
            w.WriteNumber("refreshPeriodQpc", (long)Math.Round(series.RefreshPeriodS * d.QpcFrequency));
            w.WriteNumber("dpiScale", meta.DpiScale);
            w.WriteString("monitor", meta.Monitor);
            w.WriteNumber("maxFrameLatency", meta.MaxFrameLatency);
            w.WriteString("deviceKind", device);
            w.WriteString("rawDevice", "");
            w.WriteString("surface", ScrollSurfaces.SlugOf(d.Surface));
            w.WriteString("feelSet", "A");
            w.WriteString("presetBase", d.PresetBase);
            w.WriteString("probeLevel", d.Level.ToString());
            w.WriteString("engineCommit", meta.EngineVersion);
            w.WriteString("osBuild", meta.OsBuild);
            w.WriteNumber("viewport", series.Viewport);
            w.WriteNumber("uiRows", d.Ui.Length);
            w.WriteNumber("renderRows", d.Render.Length);
            w.WriteNumber("lostUiRows", d.LostUi);
            w.WriteNumber("lostRenderRows", d.LostRender);
            w.WriteNumber("frames", d.Frames.Length);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string FeelJson(SessionData d)
    {
        using var stream = new MemoryStream(1024);
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WritePropertyName("motion");
            w.WriteRawValue(ScrollTunables.ToJson(d.Feel));
            w.WritePropertyName("host");
            w.WriteStartObject();
            w.WriteEndObject();
            w.WriteString("presetBase", d.PresetBase);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MarkersJson(SessionData d)
    {
        using var stream = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartArray();
            for (int i = 0; i < d.Ui.Length; i++)
            {
                ref readonly ProbeRow r = ref d.Ui[i];
                if (r.Kind != ProbeRowKind.Mark) continue;
                w.WriteStartObject();
                w.WriteString("code", r.MarkCode.ToString());
                w.WriteNumber("qpc", r.Qpc);
                w.WriteNumber("tMs", (r.Qpc - d.StartQpc) * 1000.0 / d.QpcFrequency);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Str(JsonElement root, string name, string fallback)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static long Long(JsonElement root, string name, long fallback = 0)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? x : fallback;

    private static ScrollSurfaceKind ParseSurface(string slug)
    {
        foreach (var (kind, _, s) in ScrollSurfaces.All) if (s == slug) return kind;
        return ScrollSurfaceKind.FixedList100k;
    }

    private static MotionFeel FeelProfilesFallback() => FeelProfiles.Standard;

    // ── device ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The session's dominant input device (a folder-name token).</summary>
    public static string DeviceKind(ReadOnlySpan<ProbeRow> ui)
    {
        Span<int> counts = stackalloc int[8];
        for (int i = 0; i < ui.Length; i++)
            if (ui[i].Kind == ProbeRowKind.Input && (uint)ui[i].B0 < 8u) counts[ui[i].B0]++;
        int best = -1, bestN = 0;
        for (int i = 0; i < counts.Length; i++) if (counts[i] > bestN) { bestN = counts[i]; best = i; }
        return best switch
        {
            (int)ScrollSourceCode.MouseWheel => "mouse",
            (int)ScrollSourceCode.MouseWheelHiRes => "hires",
            (int)ScrollSourceCode.Touchpad => "touchpad",
            (int)ScrollSourceCode.Touch => "touch",
            (int)ScrollSourceCode.Pen => "pen",
            (int)ScrollSourceCode.Keyboard => "keyboard",
            (int)ScrollSourceCode.Thumb => "thumb",
            (int)ScrollSourceCode.Programmatic => "programmatic",
            _ => "none",
        };
    }

    // ── raw rows ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriteRows<T>(string path, T[] rows) where T : unmanaged
    {
        using var fs = File.Create(path);
        Span<byte> header = stackalloc byte[HeaderBytes];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), BinVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8), Unsafe.SizeOf<T>());
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12), rows.Length);
        fs.Write(header);
        fs.Write(MemoryMarshal.AsBytes(rows.AsSpan()));
    }

    private static T[] ReadRows<T>(string path) where T : unmanaged
    {
        if (!File.Exists(path)) return Array.Empty<T>();
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderBytes || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException(Path.GetFileName(path) + " is not a lab row dump");
        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        if (version != BinVersion || size != Unsafe.SizeOf<T>())
            throw new InvalidDataException(Path.GetFileName(path) + ": row layout " + version + "/" + size + " differs from this build's");
        if (count < 0 || (long)count * size > bytes.Length - HeaderBytes)
            throw new InvalidDataException(Path.GetFileName(path) + " is truncated");
        var rows = new T[count];
        bytes.AsSpan(HeaderBytes, count * size).CopyTo(MemoryMarshal.AsBytes(rows.AsSpan()));
        return rows;
    }
}
