using System.IO;
using System.Text;

namespace FluentGpu.Lottie;

/// <summary>
/// An immutable handle to one Lottie/Bodymovin JSON document. Parsing + compiling to a <see cref="LottiePlan"/> is
/// lazy and cached (<see cref="Plan"/>) — a source can be constructed cheaply (just holds the UTF-8 bytes) and
/// compiled once, off the UI thread if desired (<c>LottieView.Preload</c>): the parser is a pure
/// <see cref="System.Text.Json.Utf8JsonReader"/> walk and the compiler mints each geometry's
/// <see cref="FluentGpu.Foundation.PathContentEpoch"/> via <see cref="FluentGpu.Foundation.PathContentEpoch.Mint"/>
/// (Interlocked — thread-safe) into a freshly-instanced <c>PathBuilder</c> per compile (never the UI-thread-affine
/// static builder <c>PathDataParser</c> uses), so nothing here has UI-thread affinity.
/// </summary>
public sealed class LottieSource
{
    private readonly byte[] _utf8;
    private readonly object _gate = new();
    private LottiePlan? _plan;

    /// <summary>A short name for diagnostics (asset file name, say) — never parsed, purely informational.</summary>
    public string? DebugName { get; }

    private LottieSource(byte[] utf8, string? name)
    {
        _utf8 = utf8;
        DebugName = name;
    }

    public static LottieSource FromUtf8(byte[] utf8, string? name = null) => new(utf8, name);
    public static LottieSource FromString(string json, string? name = null) => new(Encoding.UTF8.GetBytes(json), name);
    public static LottieSource FromFile(string path) => new(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>The compiled plan — parsed + compiled exactly once (double-checked lock; cheap re-entry after the
    /// first caller pays the cost). Throws <see cref="System.Text.Json.JsonException"/> for malformed input, the same
    /// way every call site would if it compiled inline (a Lottie asset is authored content, not user input — loud
    /// failure is correct).</summary>
    public LottiePlan Plan
    {
        get
        {
            if (_plan is { } p) return p;
            lock (_gate)
            {
                return _plan ??= LottieCompiler.Compile(LottieParser.Parse(_utf8));
            }
        }
    }
}
