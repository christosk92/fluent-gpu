using System.Collections.Concurrent;
using FluentGpu.Text;

namespace FluentGpu.Text.Headless;

/// <summary>
/// Deterministic, TerraFX-free <see cref="IFontFaceSource"/> for tests and headless suites: a fixed set of "installed" system
/// families plus registered font "files" (path to the family names of its faces). It creates no OS object - a face is a
/// fresh opaque handle - and records what each handle stands for and how many were created, so a suite can prove the
/// registry creates a face once however many consumers ask and that the UI thread creates none after a preload.
/// </summary>
public sealed class HeadlessFontFaceSource : IFontFaceSource
{
    private readonly HashSet<string> _system;
    private readonly Dictionary<string, string[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<nint, string> _described = new();
    private long _next = 0x1000;
    private int _created, _released;

    public HeadlessFontFaceSource(params string[] systemFamilies)
        => _system = new HashSet<string>(systemFamilies, StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a font file holding the given faces (index order). A .ttf has one; a .ttc several.</summary>
    public HeadlessFontFaceSource AddFile(string path, params string[] families)
    {
        _files[path] = families;
        return this;
    }

    /// <summary>Faces created so far (every thread).</summary>
    public int Created => Volatile.Read(ref _created);

    /// <summary>Faces released so far.</summary>
    public int Released => Volatile.Read(ref _released);

    /// <summary>What a handle stands for: <c>system:Family@weight</c> or <c>file:path[index]@weight</c>.</summary>
    public string Describe(nint face) => _described.TryGetValue(face, out var d) ? d : "";

    public bool HasSystemFamily(string family) => _system.Contains(family);

    public string[]? ReadFileFamilies(string path) => _files.TryGetValue(path, out var f) ? f : null;

    public nint CreateSystemFace(string family, int weight, out FontFaceMetrics metrics)
        => Make($"system:{family}@{weight}", out metrics);

    public nint CreateFileFace(string path, int faceIndex, int weight, out FontFaceMetrics metrics)
        => Make($"file:{path}[{faceIndex}]@{weight}", out metrics);

    public void ReleaseFace(nint face) => Interlocked.Increment(ref _released);

    private nint Make(string description, out FontFaceMetrics metrics)
    {
        nint handle = (nint)Interlocked.Add(ref _next, 16);
        _described[handle] = description;
        Interlocked.Increment(ref _created);
        // The headless advance model's vertical ratios (HeadlessFontSystem: 1.1 baseline, 0.3 descent, ~0.7 cap) in a 2048 em.
        metrics = new FontFaceMetrics { Em = 2048, Asc = 2253, Desc = 614, UnderPos = -200, UnderThk = 140, StrikePos = 600, Cap = 1434 };
        return handle;
    }
}
