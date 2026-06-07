namespace FluentGpu.Scene;

public enum ImageState : byte { None = 0, Pending = 1, Ready = 2, Failed = 3 }

/// <summary>A generational-free image handle into the <see cref="ImageCache"/> (0 = none).</summary>
public readonly record struct ImageHandle(int Id)
{
    public bool IsNull => Id == 0;
    public static ImageHandle Null => default;
}

/// <summary>Forwards decoded PREMULTIPLIED BGRA8 pixels to the GPU backend. The span is valid only for the duration of
/// the synchronous call (it is never stored — the backend copies it into its upload heap), so the cache need not own
/// pixel memory: it flows decoder → cache.Pump → host sink → IGpuDevice.UploadImage in one stack.</summary>
public delegate void ImageReadyHandler(int id, System.ReadOnlySpan<byte> bgra8, int w, int h);

/// <summary>The decode seam: the portable cache asks a leaf to decode a source to a target size, off the UI thread.
/// The Windows leaf is WIC→GPU (needs-pixels); the headless leaf is deterministic. <see cref="Pump"/> drains completions
/// onto the UI thread (the +1-frame latency contract: a request is never ready the same frame), invoking
/// <paramref name="onPixels"/> with the bucket pixels then <paramref name="onComplete"/> with the state transition.</summary>
public interface IImageDecoder
{
    void Begin(int id, string source, int targetW, int targetH);
    void Pump(System.Action<int, bool, int, int> onComplete, ImageReadyHandler onPixels);   // (id, success, decodedW, decodedH) + pixels
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
    private static readonly ImageReadyHandler _noPixels = static (int id, System.ReadOnlySpan<byte> p, int w, int h) => { };
    private ImageReadyHandler _pixelSink;
    private int _nextId = 1;
    private long _clock = 1;
    private int _pumpCompleted;

    public ImageCache(IImageDecoder decoder, long budgetBytes = 96L * 1024 * 1024)
    {
        _decoder = decoder;
        _budgetBytes = budgetBytes;
        _onComplete = OnDecodeComplete;
        _pixelSink = _noPixels;
    }

    /// <summary>The host wires this to <c>IGpuDevice.UploadImage</c>; the cache forwards each decode's pixels through it
    /// during <see cref="Pump"/> (transiently — the sink must copy, not store). Set once at composition.</summary>
    public void SetPixelSink(ImageReadyHandler sink) => _pixelSink = sink ?? _noPixels;

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
        _decoder.Pump(_onComplete, _pixelSink);
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

/// <summary>Deterministic headless/offline decoder: completes on the NEXT <see cref="ImageCache.Pump"/> (the +1-frame
/// latency contract), reporting the requested target size and synthesizing a stable per-id BGRA pattern so the GPU
/// upload + sample path is exercisable without real codecs or network. Proves the residency/state logic.</summary>
public sealed class FakeImageDecoder : IImageDecoder
{
    private readonly Queue<(int id, int w, int h)> _pending = new();
    private byte[] _scratch = System.Array.Empty<byte>();

    public void Begin(int id, string source, int targetW, int targetH)
        => _pending.Enqueue((id, targetW <= 0 ? 1 : targetW, targetH <= 0 ? 1 : targetH));

    public void Pump(System.Action<int, bool, int, int> onComplete, ImageReadyHandler onPixels)
    {
        while (_pending.Count > 0)
        {
            var (id, w, h) = _pending.Dequeue();
            int bytes = w * h * 4;
            if (_scratch.Length < bytes) _scratch = new byte[bytes];
            FillPattern(_scratch, id, w, h);
            onPixels(id, _scratch.AsSpan(0, bytes), w, h);   // premultiplied BGRA (opaque ⇒ premul == straight)
            onComplete(id, true, w, h);
        }
    }

    // A stable per-id diagonal gradient with an id-derived hue so each decoded tile looks distinct on screen.
    private static void FillPattern(byte[] buf, int id, int w, int h)
    {
        uint s = unchecked((uint)id * 2654435761u);
        byte br = (byte)(80 + (s & 0x7F)), bg = (byte)(80 + ((s >> 7) & 0x7F)), bb = (byte)(80 + ((s >> 14) & 0x7F));
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float t = (x + y) / (float)(w + h);            // 0..1 corner→corner
                buf[i + 0] = (byte)(bb * (0.45f + 0.55f * t));  // B
                buf[i + 1] = (byte)(bg * (0.45f + 0.55f * t));  // G
                buf[i + 2] = (byte)(br * (0.45f + 0.55f * t));  // R
                buf[i + 3] = 255;                                // A (opaque)
            }
        }
    }
}
