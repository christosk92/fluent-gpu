using System.Diagnostics;
using FluentGpu.Hosting.Threading;
using FluentGpu.Theming;

namespace FluentGpu.Text;

/// <summary>The design metrics of one font face, in design units (positive-up from the baseline as DirectWrite reports
/// them). <c>Cap</c> is the OS/2 cap height (TextLineBounds.Tight trims the line box to cap..baseline); a face that reports
/// none carries the ~0.7 em convention.</summary>
public struct FontFaceMetrics
{
    public ushort Em;
    public short Asc, Desc, UnderPos, StrikePos;
    public ushort UnderThk;
    public ushort Cap;

    /// <summary>Segoe-ish design ratios (per 2048 em) for a lookup that produced no face, so metrics-dependent callers stay sane.</summary>
    public static FontFaceMetrics Faceless => new() { Em = 2048, Asc = 1500, Desc = 500, UnderPos = -200, UnderThk = 140, StrikePos = 600, Cap = 1434 };
}

/// <summary>
/// The platform seam of the <see cref="FontRegistry"/> (E9): creates and releases the OS font faces. The registry owns every
/// policy decision (chains, <c>#Family</c> selection, caching, counters); a source only answers "does this system family
/// exist", "what families does this file hold" and "make me a face". DirectWrite implements it on Windows; a headless fake
/// counts creations so a suite can prove the UI thread creates none after a preload. Face handles are opaque (an
/// <c>IDWriteFontFace*</c> on Windows); implementations must be callable from any thread.
/// </summary>
public interface IFontFaceSource
{
    /// <summary>True when the installed system collection has a family named <paramref name="family"/> (a single name).</summary>
    bool HasSystemFamily(string family);

    /// <summary>The family name of every face in the font file at <paramref name="path"/>, in face-index order (a .ttf holds
    /// one; a .ttc several), or null when the file is missing or unreadable.</summary>
    string[]? ReadFileFamilies(string path);

    /// <summary>Create the face of a system family nearest to the numeric DWRITE weight. 0 = none.</summary>
    nint CreateSystemFace(string family, int weight, out FontFaceMetrics metrics);

    /// <summary>Create face <paramref name="faceIndex"/> of a font file; a raw file has no weight family to pick from, so a
    /// source synthesizes bold for the heavy half (weight at or above 600). 0 = none.</summary>
    nint CreateFileFace(string path, int faceIndex, int weight, out FontFaceMetrics metrics);

    /// <summary>Release a face this source created.</summary>
    void ReleaseFace(nint face);
}

/// <summary>Thrown by <see cref="FontRegistry"/> when a family chain is exhausted and <see cref="FontRegistry.ThrowOnViolation"/> is set.</summary>
public sealed class FontChainException(string message) : InvalidOperationException(message);

/// <summary>
/// THE font registry (E9): the one cache of font faces, shared by the measure/layout engine (<c>TextLayoutEngine</c>) and the
/// glyph renderer, so a family is resolved, created and metered exactly once however many consumers ask. It replaces the two
/// private face caches, each with its own silent fallback.
/// <list type="bullet">
/// <item><b>Chains.</b> A family spec is a comma-separated CHAIN, primary first (<c>"Segoe UI Variable Display, Segoe UI"</c>),
/// as <see cref="FontSet.Body"/> declares it. Falling back along the chain is the style's choice, not a wrong frame. Falling
/// OFF the chain (no entry exists) is an error: <see cref="ChainViolations"/> counts it, the message goes to
/// <see cref="ErrorSink"/> (stderr by default, in Debug and Release alike, so a Release build logs it), and
/// <see cref="ThrowOnViolation"/> turns it into a <see cref="FontChainException"/> (the strict mode a gate or a debug run sets).
/// The face returned after falling off is the theme's body chain, then Segoe UI, so a missing font never produces no text.</item>
/// <item><b>Files.</b> An entry is a system family name or <c>"path/to.ttf#Family"</c>: the part after <c>#</c> SELECTS the face
/// of the file whose family name matches (case-insensitive), so a .ttc holding several families yields the one named. A
/// single-face file whose name does not match still resolves its only face, with a logged warning (it is unambiguous).</item>
/// <item><b>One cache.</b> Faces are cached per (entry, weight) and per (chain, weight); negative lookups are cached too.
/// Fallback faces the shaper maps for uncovered characters are cached here as well. Faces live as long as the registry.</item>
/// <item><b>Preload.</b> <see cref="Fonts.Preload"/> resolves a <see cref="FontSet"/> on a worker so the UI thread creates no
/// face after it (<see cref="FacesCreatedOnUiThread"/> is the proof counter).</item>
/// <item><b>Body font changes.</b> A run with no family shapes in the theme's body chain, which a text style cannot see.
/// <see cref="SyncBody"/> follows <c>Tok.Definition.Fonts.Body</c>; <see cref="BodyVersion"/> moves when it changes and every
/// cache keyed on it (the per-node measure cache, the shape and run caches) evicts.</item>
/// </list>
/// Thread-safe: the UI thread (measure) and the render thread (glyph raster) both resolve through it.
/// </summary>
public sealed class FontRegistry : IDisposable
{
    /// <summary>The process-wide registry both Windows text consumers use. Its source is installed by the first backend
    /// engine constructed (see <see cref="UseSourceIfNone"/>).</summary>
    public static FontRegistry Shared { get; } = new();

    /// <summary>The weights <see cref="Fonts.Preload"/> resolves for a set's body chain (the WinUI type ramp: Regular,
    /// SemiBold, Bold). The icon family preloads at Regular.</summary>
    public static readonly int[] PreloadWeights = [400, 600, 700];

    private sealed class Face
    {
        public nint Handle;
        public FontFaceMetrics Metrics;
        public string Family = "";
        public bool OffChain;
    }

    private static readonly Face Missing = new() { Metrics = FontFaceMetrics.Faceless };

    private readonly object _gate = new();
    private readonly Func<string> _bodyProvider;
    private readonly Dictionary<(string Spec, int Weight), Face> _chains = new();
    private readonly Dictionary<(string Entry, int Weight), Face> _singles = new();
    private readonly Dictionary<(string Path, int Index, int Weight), Face> _fileFaces = new();
    private readonly Dictionary<string, bool> _systemFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]?> _fileFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Family, int Weight), nint> _fallbacks = new();
    // Lock-free read path (PERF r3 UI). Every text layout on the UI thread and every glyph-run shape on the render thread resolves
    // its faces here, so the two threads hammered `_gate` for what is, after warm-up, always a cache hit: nav-burst spent 112 ms
    // (UI) + 34 ms (render) per 10 s spinning in Monitor.Enter_Slowpath on it (Debug). A hit now reads an immutable
    // copy-on-write snapshot of the cache with no lock; a miss takes the lock and resolves exactly as before, then republishes.
    // A snapshot maps every key to the same Face (or face handle) the locked table holds, so a hit returns what the locked
    // lookup would have. Inserts are rare (one per new chain x weight or fallback family); the tables hold tens of entries.
    private volatile Dictionary<(string Spec, int Weight), Face> _chainsRead = new();
    private volatile Dictionary<(string Family, int Weight), nint> _fallbacksRead = new();
    private readonly List<nint> _owned = new();
    private IFontFaceSource? _source;
    private string? _bodyChain;
    private long _facesCreated, _facesCreatedOnUi;
    private int _violations;
    private string? _lastViolation;

    /// <summary>Create a registry. <paramref name="bodyProvider"/> reads the active body chain (default: the active theme
    /// definition's <c>Fonts.Body</c>, read at use); tests inject their own.</summary>
    public FontRegistry(IFontFaceSource? source = null, Func<string>? bodyProvider = null)
    {
        _source = source;
        _bodyProvider = bodyProvider ?? (static () => FluentGpu.Dsl.Tok.Definition.Fonts.Body);
    }

    /// <summary>The platform source. Set once by the backend; replacing it drops every cached face (releasing them through the old source).</summary>
    public IFontFaceSource? Source
    {
        get { lock (_gate) return _source; }
        set { lock (_gate) { if (ReferenceEquals(_source, value)) return; ReleaseAllLocked(); _source = value; } }
    }

    /// <summary>Install <paramref name="source"/> only when none is set yet (the backend engines call this from their constructors).</summary>
    public void UseSourceIfNone(Func<IFontFaceSource> create)
    {
        lock (_gate) { if (_source is null) _source = create(); }
    }

    // ── diagnostics ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Faces created since construction (every thread). A face is created at most once per (entry, weight).</summary>
    public long FacesCreated => Interlocked.Read(ref _facesCreated);

    /// <summary>Faces created ON THE UI THREAD (the thread bound to <c>ThreadRole.Ui</c>) - the number a preload drives to zero.</summary>
    public long FacesCreatedOnUiThread => Interlocked.Read(ref _facesCreatedOnUi);

    /// <summary>Chains that were exhausted (no entry exists) since the last <see cref="ResetViolations"/>.</summary>
    public int ChainViolations => Volatile.Read(ref _violations);

    /// <summary>The most recent violation message.</summary>
    public string? LastViolation { get { lock (_gate) return _lastViolation; } }

    /// <summary>Where violation messages go. Null = <see cref="Console.Error"/> (logged in Debug and Release).</summary>
    public Action<string>? ErrorSink { get; set; }

    /// <summary>When set, falling off a chain throws <see cref="FontChainException"/> after it is counted and logged (the strict
    /// DEBUG mode: a gate or a debug run sets it; the shipping default only logs).</summary>
    public bool ThrowOnViolation { get; set; }

    public void ResetViolations()
    {
        lock (_gate) { _violations = 0; _lastViolation = null; }
    }

    // ── the body chain ───────────────────────────────────────────────────────────────────────────────────────────────

    private int _bodyVersion;

    /// <summary>Moves (monotonically) each time <see cref="SyncBody"/> sees the body chain change. Caches whose entries were
    /// shaped in the body font key on it.</summary>
    public int BodyVersion => Volatile.Read(ref _bodyVersion);

    /// <summary>The body chain as of the last <see cref="SyncBody"/>.</summary>
    public string BodyChain => _bodyChain ?? BodyChainSynced();

    /// <summary>The first family of the body chain (what a single-name consumer such as a fallback map prefers).</summary>
    public string BodyPrimary
    {
        get
        {
            string c = BodyChain;
            int comma = c.IndexOf(',');
            return comma < 0 ? c.Trim() : c.Substring(0, comma).Trim();
        }
    }

    private string BodyChainSynced() { SyncBody(); return _bodyChain!; }

    /// <summary>Re-read the active body chain; true when it changed (the version bumped). The first call adopts the chain
    /// without bumping (nothing was cached against another). Cheap: one provider read and a string compare.</summary>
    public bool SyncBody()
    {
        string now = _bodyProvider();
        if (ReferenceEquals(now, Volatile.Read(ref _bodyChain))) return false;   // the common case: the very string already adopted
        lock (_gate)
        {
            if (_bodyChain is null) { _bodyChain = now; return false; }
            if (string.Equals(_bodyChain, now, StringComparison.Ordinal)) return false;
            _bodyChain = now;
            Interlocked.Increment(ref _bodyVersion);
            return true;
        }
    }

    // ── resolution ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static int ClampWeight(int weight) => weight <= 0 ? 400 : weight > 999 ? 999 : weight;

    /// <summary>Resolve (creating once) the face of <paramref name="spec"/> at <paramref name="weight"/>. <paramref name="spec"/>
    /// is a chain; empty = the body chain. Returns the handle (0 = no face at all - a source-less registry or nothing
    /// installed) and its metrics (Segoe-ish ratios when there is no face).</summary>
    public nint ResolveFace(string? spec, int weight, out FontFaceMetrics metrics)
        => ResolveFace(spec, weight, out metrics, out _);

    /// <summary>As <see cref="ResolveFace(string?, int, out FontFaceMetrics)"/>, also reporting the chain entry that supplied the face.</summary>
    public nint ResolveFace(string? spec, int weight, out FontFaceMetrics metrics, out string resolvedFamily)
    {
        weight = ClampWeight(weight);
        if (string.IsNullOrEmpty(spec)) spec = BodyChain;
        FontChainException? thrown = null;
        if (!_chainsRead.TryGetValue((spec, weight), out Face? f))
        {
            lock (_gate)
            {
                if (!_chains.TryGetValue((spec, weight), out f))
                {
                    f = WalkChainLocked(spec, weight, out string? violation);
                    _chains[(spec, weight)] = f;
                    _chainsRead = new Dictionary<(string Spec, int Weight), Face>(_chains);
                    if (violation is not null) thrown = ReportLocked(violation);
                }
            }
        }
        if (thrown is not null && ThrowOnViolation) throw thrown;
        metrics = f.Metrics;
        resolvedFamily = f.Family;
        return f.Handle;
    }

    /// <summary>True when <paramref name="spec"/> at <paramref name="weight"/> is already resolved (a cache probe: it creates nothing).</summary>
    public bool IsResolved(string? spec, int weight)
    {
        weight = ClampWeight(weight);
        if (string.IsNullOrEmpty(spec)) spec = BodyChain;
        if (_chainsRead.ContainsKey((spec, weight))) return true;
        lock (_gate) return _chains.ContainsKey((spec, weight));
    }

    /// <summary>The first SYSTEM family of <paramref name="spec"/>'s chain that exists (what the shaper hands the system
    /// fallback as its preferred family), else the body primary. A file entry names no system family and is skipped.</summary>
    public string PreferredSystemFamily(string? spec)
    {
        if (string.IsNullOrEmpty(spec)) return BodyPrimary;
        lock (_gate)
        {
            int pos = 0;
            while (NextEntry(spec, ref pos, out int s, out int len))
            {
                string entry = len == spec.Length ? spec : spec.Substring(s, len);
                if (entry.IndexOf('#') >= 0 || IsFilePath(entry)) continue;
                if (SystemFamilyExistsLocked(entry)) return entry;
            }
        }
        return BodyPrimary;
    }

    private Face WalkChainLocked(string spec, int weight, out string? violation)
    {
        violation = null;
        int pos = 0;
        while (NextEntry(spec, ref pos, out int s, out int len))
        {
            string entry = len == spec.Length ? spec : spec.Substring(s, len);
            Face one = ResolveSingleLocked(entry, weight);
            if (one.Handle != 0) return one;
        }

        // Off the chain: an error (counted, logged, optionally thrown), but never no text - fall back to the theme's body
        // chain, then Segoe UI, exactly the faces the old silent fallback reached.
        violation = $"[fonts] font chain exhausted: none of \"{spec}\" (weight {weight}) exists"
                  + (_source is null ? " (no IFontFaceSource installed)" : "") + "; falling back to the body chain.";
        string body = _bodyChain ?? _bodyProvider();
        if (!string.Equals(body, spec, StringComparison.Ordinal))
        {
            pos = 0;
            while (NextEntry(body, ref pos, out int s, out int len))
            {
                string entry = len == body.Length ? body : body.Substring(s, len);
                Face one = ResolveSingleLocked(entry, weight);
                if (one.Handle != 0) return new Face { Handle = one.Handle, Metrics = one.Metrics, Family = one.Family, OffChain = true };
            }
        }
        Face last = ResolveSingleLocked("Segoe UI", weight);
        if (last.Handle != 0) return new Face { Handle = last.Handle, Metrics = last.Metrics, Family = last.Family, OffChain = true };
        return new Face { Metrics = FontFaceMetrics.Faceless, OffChain = true };
    }

    private Face ResolveSingleLocked(string entry, int weight)
    {
        if (_singles.TryGetValue((entry, weight), out var cached)) return cached;
        Face result = Missing;
        var src = _source;
        if (src is not null)
        {
            int hash = entry.IndexOf('#');
            string path = hash >= 0 ? entry.Substring(0, hash).Trim() : entry;
            nint handle = 0;
            FontFaceMetrics m = default;
            if (IsFilePath(path))
            {
                string[]? families = FileFamiliesLocked(path);
                if (families is { Length: > 0 })
                {
                    int index = 0;
                    string want = hash >= 0 ? entry.Substring(hash + 1).Trim() : "";
                    if (want.Length > 0)
                    {
                        index = IndexOfFamily(families, want);
                        if (index < 0 && families.Length == 1)
                        {
                            index = 0;   // a single-face file is unambiguous: keep it, but say the name is wrong
                            ReportWarningLocked($"[fonts] \"{path}\" holds the family \"{families[0]}\", not \"{want}\"; using its only face.");
                        }
                    }
                    if (index >= 0)
                    {
                        // Keyed by the face the name SELECTED, not by the spelling: "f.ttc#Inter", "f.ttc#inter" and "f.ttc"
                        // are one face and are created once.
                        if (_fileFaces.TryGetValue((path, index, weight), out var shared)) { _singles[(entry, weight)] = shared; return shared; }
                        handle = src.CreateFileFace(path, index, weight, out m);
                        if (handle != 0)
                        {
                            Track(handle);
                            result = new Face { Handle = handle, Metrics = m, Family = entry };
                            _fileFaces[(path, index, weight)] = result;
                            _singles[(entry, weight)] = result;
                            return result;
                        }
                    }
                }
            }
            else if (SystemFamilyExistsLocked(entry))
            {
                handle = src.CreateSystemFace(entry, weight, out m);
            }
            if (handle != 0)
            {
                Track(handle);
                result = new Face { Handle = handle, Metrics = m, Family = entry };
            }
        }
        _singles[(entry, weight)] = result;
        return result;
    }

    private bool SystemFamilyExistsLocked(string family)
    {
        if (_systemFamilies.TryGetValue(family, out bool known)) return known;
        bool exists = _source?.HasSystemFamily(family) ?? false;
        _systemFamilies[family] = exists;
        return exists;
    }

    private string[]? FileFamiliesLocked(string path)
    {
        if (_fileFamilies.TryGetValue(path, out var cached)) return cached;
        string[]? families = _source?.ReadFileFamilies(path);
        _fileFamilies[path] = families;
        return families;
    }

    private static int IndexOfFamily(string[] families, string want)
    {
        for (int i = 0; i < families.Length; i++)
            if (string.Equals(families[i], want, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static bool IsFilePath(string path)
        => path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".otc", StringComparison.OrdinalIgnoreCase);

    private void Track(nint handle)
    {
        _owned.Add(handle);
        Interlocked.Increment(ref _facesCreated);
        if (ThreadGuard.IsUiThread) Interlocked.Increment(ref _facesCreatedOnUi);
    }

    // ── fallback faces (the system font-fallback map's results) ──────────────────────────────────────────────────────

    /// <summary>A cached fallback face for (<paramref name="family"/>, the RESOLVED <paramref name="weight"/>), or 0.</summary>
    public nint TryGetFallbackFace(string family, int weight)
    {
        if (_fallbacksRead.TryGetValue((family, weight), out nint hit)) return hit;
        lock (_gate) return _fallbacks.TryGetValue((family, weight), out var f) ? f : 0;
    }

    /// <summary>Hand the registry a fallback face the shaper created (it takes ownership and releases it with the registry).
    /// If another thread cached one first, <paramref name="face"/> is released through <paramref name="release"/> and the
    /// cached one is returned.</summary>
    public nint AddFallbackFace(string family, int weight, nint face, Action<nint> release)
    {
        lock (_gate)
        {
            if (_fallbacks.TryGetValue((family, weight), out var existing)) { release(face); return existing; }
            _fallbacks[(family, weight)] = face;
            _fallbacksRead = new Dictionary<(string Family, int Weight), nint>(_fallbacks);
            _fallbackOwners.Add((face, release));
            Interlocked.Increment(ref _facesCreated);
            if (ThreadGuard.IsUiThread) Interlocked.Increment(ref _facesCreatedOnUi);
            return face;
        }
    }

    private readonly List<(nint Face, Action<nint> Release)> _fallbackOwners = new();

    // ── chain parsing ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Advance <paramref name="pos"/> to the next non-empty entry of a comma-separated chain; <paramref name="start"/>/
    /// <paramref name="length"/> are its trimmed range. False at the end. Allocation-free.</summary>
    public static bool NextEntry(string chain, ref int pos, out int start, out int length)
    {
        while (pos < chain.Length)
        {
            int comma = chain.IndexOf(',', pos);
            int end = comma < 0 ? chain.Length : comma;
            int s = pos;
            pos = comma < 0 ? chain.Length : comma + 1;
            while (s < end && char.IsWhiteSpace(chain[s])) s++;
            int e = end;
            while (e > s && char.IsWhiteSpace(chain[e - 1])) e--;
            if (e > s) { start = s; length = e - s; return true; }
        }
        start = 0; length = 0;
        return false;
    }

    /// <summary>The entries of a chain, trimmed (cold callers and tests).</summary>
    public static string[] ParseChain(string chain)
    {
        var list = new List<string>();
        int pos = 0;
        while (NextEntry(chain, ref pos, out int s, out int len)) list.Add(chain.Substring(s, len));
        return list.ToArray();
    }

    // ── preload ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Resolve every face of <paramref name="set"/> (the body chain at <see cref="PreloadWeights"/>, the icon family
    /// at Regular) on a worker thread, so the UI thread creates none. A violation found there is counted and logged like any
    /// other (it is never thrown on the worker).</summary>
    public Task Preload(FontSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Task.Run(() => PreloadNow(set));
    }

    /// <summary>The synchronous body of <see cref="Preload"/> (tests and a caller already on a worker).</summary>
    public void PreloadNow(FontSet set)
    {
        bool strict = ThrowOnViolation;
        try
        {
            foreach (int w in PreloadWeights) ResolveFace(set.Body, w, out _);
            ResolveFace(set.Icon, 400, out _);
        }
        catch (FontChainException) when (!strict) { }
    }

    /// <summary>True when every face <see cref="Preload"/> resolves for <paramref name="set"/> is cached.</summary>
    public bool IsReady(FontSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (_gate)
        {
            if (_source is null) return false;
            foreach (int w in PreloadWeights) if (!_chains.ContainsKey((set.Body, w))) return false;
            return _chains.ContainsKey((set.Icon, 400));
        }
    }

    // ── reporting ────────────────────────────────────────────────────────────────────────────────────────────────────

    private FontChainException? ReportLocked(string message)
    {
        _violations++;
        _lastViolation = message;
        EmitLocked(message);
        return new FontChainException(message);
    }

    private void ReportWarningLocked(string message) => EmitLocked(message);

    private void EmitLocked(string message)
    {
        if (ErrorSink is { } sink) sink(message);
        else Console.Error.WriteLine(message);
        Debug.WriteLine(message);
    }

    // ── lifetime ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private void ReleaseAllLocked()
    {
        var src = _source;
        if (src is not null) foreach (nint f in _owned) src.ReleaseFace(f);
        foreach (var (face, release) in _fallbackOwners) release(face);
        _owned.Clear(); _fallbackOwners.Clear();
        _chains.Clear(); _singles.Clear(); _fileFaces.Clear(); _fallbacks.Clear(); _systemFamilies.Clear(); _fileFamilies.Clear();
        _chainsRead = new();
        _fallbacksRead = new();
    }

    /// <summary>Release every face. The shared registry lives for the process and is not disposed.</summary>
    public void Dispose()
    {
        lock (_gate) ReleaseAllLocked();
    }
}

/// <summary>
/// The application surface of the font registry (E9): <c>await Fonts.Preload(theme.Fonts)</c> before the first frame shows
/// (the reveal gate waits on <see cref="IsReady"/>), so the UI thread creates no face and no frame shapes in a wrong
/// family. Operates on <see cref="FontRegistry.Shared"/>.
/// </summary>
public static class Fonts
{
    /// <summary>Resolve every face of <paramref name="set"/> on a worker thread.</summary>
    public static Task Preload(FontSet set) => FontRegistry.Shared.Preload(set);

    /// <summary>True when every face of <paramref name="set"/> is resolved and cached.</summary>
    public static bool IsReady(FontSet set) => FontRegistry.Shared.IsReady(set);
}
