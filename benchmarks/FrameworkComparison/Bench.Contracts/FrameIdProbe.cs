using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bench.Contracts;

/// <summary>
/// Desktop frame-ID color probe: each measured mutation paints a solid RGB patch that encodes the
/// iteration. A separate capture process samples desktop pixels at the patch and timestamps when
/// that ID first appears — independent of PresentMon process attribution and of
/// <c>CompositionTarget.Rendering</c>.
/// </summary>
public static class FrameIdProbe
{
    public const int SizePx = 48;
    public const int MarginPx = 8;
    /// <summary>Client-space top-left of the probe (top-right corner of the 1200×720 window).</summary>
    public const int ClientX = BenchWorkload.WindowWidth - MarginPx - SizePx;
    public const int ClientY = MarginPx;

    public static void Encode(int frameId, out byte r, out byte g, out byte b)
    {
        // Keep channels out of the crushed 0/255 extremes some pipelines produce. Two 7-bit channels carry a
        // lossless 14-bit frame ID (16,384 mutations — well above the benchmark limit); B carries its 4-bit parity.
        int id = frameId & 0x03FFF;
        int parity = PopCount(id) & 0x0F;
        r = (byte)(16 + (id & 0x7F));
        g = (byte)(16 + ((id >> 7) & 0x7F));
        b = (byte)(16 + parity);
    }

    public static bool TryDecode(byte r, byte g, byte b, out int frameId)
    {
        frameId = 0;
        if (r is < 16 or > 143 || g is < 16 or > 143 || b is < 16 or > 31) return false;
        int id =
            ((r - 16) & 0x7F) |
            (((g - 16) & 0x7F) << 7);
        int parity = b - 16;
        if (parity != (PopCount(id) & 0x0F)) return false;
        frameId = id;
        return true;
    }

    public static string DefaultMutationLogPath(string resultPath)
        => Path.ChangeExtension(Path.GetFullPath(resultPath), ".mutations.jsonl");

    public static string DefaultSampleLogPath(string resultPath)
        => Path.ChangeExtension(Path.GetFullPath(resultPath), ".samples.jsonl");

    public static string DefaultVisibilityPath(string resultPath)
        => Path.ChangeExtension(Path.GetFullPath(resultPath), ".visibility.json");

    private static int PopCount(int value)
    {
        uint v = (uint)value;
        v -= (v >> 1) & 0x55555555u;
        v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
        return (int)((((v + (v >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24);
    }
}

public sealed class FrameIdMutationLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FrameIdMutationLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    public void Write(int iteration, long qpc, byte r, byte g, byte b)
    {
        string line = "{\"iteration\":" + iteration.ToString(CultureInfo.InvariantCulture) +
                      ",\"qpc\":" + qpc.ToString(CultureInfo.InvariantCulture) +
                      ",\"r\":" + r.ToString(CultureInfo.InvariantCulture) +
                      ",\"g\":" + g.ToString(CultureInfo.InvariantCulture) +
                      ",\"b\":" + b.ToString(CultureInfo.InvariantCulture) + "}";
        lock (_gate) _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();
}

public sealed record FrameIdMutation(int Iteration, long Qpc, byte R, byte G, byte B);
public sealed record FrameIdSample(long Qpc, int FrameId, byte R, byte G, byte B);

public sealed record FrameIdVisibilityResult
{
    public required string Schema { get; init; }
    public required string Framework { get; init; }
    public required string Scenario { get; init; }
    public required string Method { get; init; }
    public required long QpcFrequency { get; init; }
    public required int Mutations { get; init; }
    public required int Observed { get; init; }
    public required int Missing { get; init; }
    public required double[] VisibilityLatencyMs { get; init; }
    public required double? P50Ms { get; init; }
    public required double? P95Ms { get; init; }
    public required double? P99Ms { get; init; }
    public required double? MaxMs { get; init; }
    public string? Notes { get; init; }

    public static FrameIdVisibilityResult Join(
        string framework,
        string scenario,
        IReadOnlyList<FrameIdMutation> mutations,
        IReadOnlyList<FrameIdSample> samples,
        string? notes = null)
    {
        var ordered = new List<FrameIdMutation>(mutations);
        ordered.Sort(static (a, b) => a.Iteration.CompareTo(b.Iteration));
        var latencies = new List<double>(ordered.Count);
        int missing = 0;
        int sampleIndex = 0;
        foreach (FrameIdMutation mutation in ordered)
        {
            while (sampleIndex < samples.Count &&
                   (samples[sampleIndex].Qpc < mutation.Qpc || samples[sampleIndex].FrameId < mutation.Iteration))
                sampleIndex++;

            if (sampleIndex >= samples.Count || samples[sampleIndex].FrameId != mutation.Iteration)
            {
                missing++;
                continue;
            }

            latencies.Add((samples[sampleIndex].Qpc - mutation.Qpc) * 1000d / System.Diagnostics.Stopwatch.Frequency);
            sampleIndex++;
        }

        double[] values = latencies.ToArray();
        Array.Sort(values);
        return new FrameIdVisibilityResult
        {
            Schema = "fluentgpu-framework-frameid-visibility/v1",
            Framework = framework,
            Scenario = scenario,
            Method = "desktop-pixel-frame-id",
            QpcFrequency = System.Diagnostics.Stopwatch.Frequency,
            Mutations = mutations.Count,
            Observed = values.Length,
            Missing = missing,
            VisibilityLatencyMs = values,
            P50Ms = Percentile(values, 50),
            P95Ms = Percentile(values, 95),
            P99Ms = Percentile(values, 99),
            MaxMs = values.Length == 0 ? null : values[^1],
            Notes = notes,
        };
    }

    public void Write(string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, BenchJsonContext.Default.FrameIdVisibilityResult));
        File.Move(temp, full, true);
    }

    private static double? Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return null;
        int rank = Math.Clamp((int)Math.Ceiling(p / 100d * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[rank];
    }
}

public static class FrameIdLogReader
{
    public static List<FrameIdMutation> ReadMutations(string path)
    {
        var list = new List<FrameIdMutation>();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            list.Add(new FrameIdMutation(
                root.GetProperty("iteration").GetInt32(),
                root.GetProperty("qpc").GetInt64(),
                root.GetProperty("r").GetByte(),
                root.GetProperty("g").GetByte(),
                root.GetProperty("b").GetByte()));
        }
        return list;
    }

    public static List<FrameIdSample> ReadSamples(string path)
    {
        var list = new List<FrameIdSample>();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            list.Add(new FrameIdSample(
                root.GetProperty("qpc").GetInt64(),
                root.GetProperty("frameId").GetInt32(),
                root.GetProperty("r").GetByte(),
                root.GetProperty("g").GetByte(),
                root.GetProperty("b").GetByte()));
        }
        return list;
    }
}
