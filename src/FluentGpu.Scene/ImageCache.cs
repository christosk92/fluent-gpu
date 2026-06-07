namespace FluentGpu.Scene;

public enum ImageState : byte { None = 0, Pending = 1, Ready = 2, Failed = 3 }

/// <summary>A generational-free image handle into the <see cref="ImageCache"/> (0 = none).</summary>
public readonly record struct ImageHandle(int Id)
{
    public bool IsNull => Id == 0;
    public static ImageHandle Null => default;
}

/// <summary>The decode seam: the portable cache asks a leaf to decode a source to a target size, off the UI thread.
/// The Windows leaf is WIC→GPU (needs-pixels); the headless leaf is deterministic. <see cref="Pump"/> drains completions
/// onto the UI thread (the +1-frame latency contract: a request is never ready the same frame).</summary>
public interface IImageDecoder
{
    void Begin(int id, string source, int targetW, int targetH);
    void Pump(System.Action<int, bool, int, int> onComplete);   // (id, success, decodedW, decodedH)
}

/// <summary>
/// Portable image residency: source→handle dedup, a Pending/Ready/Failed state machine, **liveness ref-counting**
/// (a pinned/on-screen image is never evicted — the single biggest real-world cache bug, per the research), and an
/// LRU byte budget over decoded images. Decode happens off-thread behind <see cref="IImageDecoder"/>; <see cref="Pump"/>
/// applies completions (+1-frame latency). The GPU texture upload is a needs-pixels leaf concern; this owns the logic.
/// </summary>
public sealed class ImageCache
{
    private sealed class Entry
    {
        public string Key = "";
        public ImageState State;
        public int W, H;
        public int Refs;        // liveness: >0 ⇒ on screen ⇒ never evicted
        public long LastUsed;
        public long Bytes;
    }

    private readonly Dictionary<string, int> _byKey = new();
    private readonly Dictionary<int, Entry> _byId = new();
    private readonly IImageDecoder _decoder;
    private readonly long _budgetBytes;
    private readonly System.Action<int, bool, int, int> _onComplete;   // cached → Pump allocates nothing
    private int _nextId = 1;
    private long _clock = 1;
    private int _pumpCompleted;

    public ImageCache(IImageDecoder decoder, long budgetBytes = 96L * 1024 * 1024)
    {
        _decoder = decoder;
        _budgetBytes = budgetBytes;
        _onComplete = OnDecodeComplete;
    }

    public long UsedBytes { get; private set; }
    public int Count => _byId.Count;
    public int ReadyCount { get { int n = 0; foreach (var e in _byId.Values) if (e.State == ImageState.Ready) n++; return n; } }
    public int PendingCount { get { int n = 0; foreach (var e in _byId.Values) if (e.State == ImageState.Pending) n++; return n; } }

    /// <summary>Request (or dedup) a decode of <paramref name="source"/> at a target size; returns a stable handle.</summary>
    public ImageHandle Request(string source, int targetW, int targetH)
    {
        string key = $"{source}@{targetW}x{targetH}";
        if (_byKey.TryGetValue(key, out int id)) { _byId[id].LastUsed = _clock++; return new ImageHandle(id); }

        id = _nextId++;
        _byKey[key] = id;
        _byId[id] = new Entry { Key = key, State = ImageState.Pending, LastUsed = _clock++ };
        _decoder.Begin(id, source, targetW, targetH);
        return new ImageHandle(id);
    }

    public ImageState StateOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.State : ImageState.None;
    public (int W, int H) SizeOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? (e.W, e.H) : (0, 0);

    /// <summary>Pin = "on screen" (a realized node holds it); never evicted while pinned. Unpin on recycle/unmount.</summary>
    public void Pin(ImageHandle h) { if (_byId.TryGetValue(h.Id, out var e)) { e.Refs++; e.LastUsed = _clock++; } }
    public void Unpin(ImageHandle h) { if (_byId.TryGetValue(h.Id, out var e) && e.Refs > 0) e.Refs--; }
    public int RefsOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.Refs : 0;

    /// <summary>Apply finished decodes (UI thread, once per frame) then evict to budget. Returns completions this pump.
    /// Allocation-free when idle (cached callback; empty-queue Pump does nothing) — safe in the hot phase.</summary>
    public int Pump()
    {
        _pumpCompleted = 0;
        _decoder.Pump(_onComplete);
        if (_pumpCompleted > 0) EvictToBudget();
        return _pumpCompleted;
    }

    private void OnDecodeComplete(int id, bool ok, int w, int h)
    {
        if (!_byId.TryGetValue(id, out var e)) return;
        e.State = ok ? ImageState.Ready : ImageState.Failed;
        e.W = w; e.H = h;
        e.Bytes = ok ? (long)w * h * 4 : 0;
        UsedBytes += e.Bytes;
        _pumpCompleted++;
    }

    private void EvictToBudget()
    {
        while (UsedBytes > _budgetBytes)
        {
            int victim = 0; long oldest = long.MaxValue;
            foreach (var (id, e) in _byId)
                if (e.Refs == 0 && e.State == ImageState.Ready && e.LastUsed < oldest) { oldest = e.LastUsed; victim = id; }
            if (victim == 0) break;   // everything left is pinned (on screen) — never evict it
            var e2 = _byId[victim];
            UsedBytes -= e2.Bytes;
            _byKey.Remove(e2.Key);
            _byId.Remove(victim);
        }
    }
}

/// <summary>Deterministic headless decoder: completes on the NEXT <see cref="ImageCache.Pump"/> (the +1-frame latency
/// contract), reporting the requested target size. Proves the residency/state logic without real codecs or files.</summary>
public sealed class FakeImageDecoder : IImageDecoder
{
    private readonly Queue<(int id, int w, int h)> _pending = new();
    public void Begin(int id, string source, int targetW, int targetH)
        => _pending.Enqueue((id, targetW <= 0 ? 1 : targetW, targetH <= 0 ? 1 : targetH));
    public void Pump(System.Action<int, bool, int, int> onComplete)
    {
        while (_pending.Count > 0) { var (id, w, h) = _pending.Dequeue(); onComplete(id, true, w, h); }
    }
}
