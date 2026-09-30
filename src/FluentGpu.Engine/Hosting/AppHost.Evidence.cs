using System.Globalization;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Scene;

namespace FluentGpu.Hosting;

/// <summary>A captured present (docs/plans/evidence-diagnostics-implementation.md §A.6): the publication it presented, the
/// tile-table frame whose ledgers describe it, the back buffer's BGRA8 pixels (null on a backend that cannot read back —
/// the headless model records the request only) and the composite item record of that SAME turn.</summary>
public sealed record FrameCaptureResult(ulong PublishSeq, int TableFrame, long Qpc, int WidthPx, int HeightPx, byte[]? Bgra,
    CompositeFrameCopy Ledger);

/// <summary>One live scroll viewport for evidence tooling (<c>wavee://diag?cmd=vps</c>): its node, its app-supplied
/// <c>ScrollKey</c>, its offset and extent/viewport along its axis (DIP).</summary>
public readonly record struct ViewportInfo(int NodeIndex, uint Gen, string? ScrollKey, double Offset, double Extent, double Viewport,
    bool Horizontal, float X = 0f, float Y = 0f, float W = 0f, float H = 0f, double CoverStart = 0.0, double CoverEnd = 0.0,
    double WindowOrigin = 0.0, int FirstRealized = 0, int LastRealized = 0, int ItemCount = 0, int PersistentPrefix = 0,
    bool MeasureAll = false);

// The host's evidence surface (docs/plans/evidence-diagnostics-implementation.md §A): the ledgers of the recorder pair
// that composites, the pixel query over the latest composite, node names for exports, the stale-tile log edge, the per-turn
// cost row and the on-demand frame capture. The recorders own the rings; this file only routes.
public sealed partial class AppHost
{
    /// <summary>The recorder pair whose composite turns reach the window: the UI pair inline (headless / SingleThread),
    /// the render thread's pair otherwise (a detached child's render pair is drained on its parent's thread).</summary>
    private SliceRecorder EvidenceSlices => _renderThread is null && _parentRenderThread is null ? _uiSlices : _renderSlices;
    private SliceTable EvidenceTiles => _renderThread is null && _parentRenderThread is null ? _uiTiles : _renderTiles;

    /// <summary>Constructor tail: the ledgers live on the pair that composites.</summary>
    private void EnableEvidence() => EvidenceSlices.EnableEvidence();

    /// <summary>Every tile raster the composite turns scheduled, newest last (any thread; drain by count).</summary>
    public RasterLedger? RasterLedger => EvidenceSlices.RasterLedger;

    /// <summary>Every slice re-record, with why (any thread; drain by count).</summary>
    public WalkLedger? WalkLedger => EvidenceSlices.WalkLedger;

    /// <summary>The composite item record of the latest turn (any thread; <see cref="CompositeLedger.CopyLatest"/>).</summary>
    public CompositeLedger? CompositeLedger => EvidenceSlices.CompositeLedger;

    /// <summary>The primary target's latest device counters (<see cref="GpuFrameCounters.ScratchRefused"/> among them); any thread.</summary>
    public bool TryGetDeviceCounters(out GpuFrameCounters counters) => _swapchain.TryGetFrameCounters(out counters);

    private CompositeFrameCopy? _evQueryCopy;

    /// <summary>UI THREAD. "What composited at window pixel (<paramref name="x"/>, <paramref name="y"/>) in the latest
    /// composite" (§A.3): the items in painter order with their feathers, alpha and — for tiles — the tile's raster ledger.
    /// <paramref name="frame"/> = the header of the frame the answer describes. Returns the count written.</summary>
    public int QueryPixel(int x, int y, Span<PixelHit> dst, out CompositeFrameHeader frame)
    {
        frame = default;
        if (CompositeLedger is not { } led) return 0;
        _evQueryCopy ??= new CompositeFrameCopy();
        if (!led.CopyLatest(_evQueryCopy)) return 0;
        frame = _evQueryCopy.Header;
        return PixelQuery.Query(_evQueryCopy.View, x, y, dst);
    }

    /// <summary>UI THREAD. <see cref="QueryPixel"/> at a window DIP position (scaled by the latest composite's raster scale).</summary>
    public int QueryPixelDip(float xDip, float yDip, Span<PixelHit> dst, out CompositeFrameHeader frame)
    {
        float s = _window.Scale > 0f ? _window.Scale : 1f;
        return QueryPixel((int)MathF.Floor(xDip * s), (int)MathF.Floor(yDip * s), dst, out frame);
    }

    /// <summary>UI THREAD. A readable name for (<paramref name="nodeIndex"/>, <paramref name="gen"/>) — the nearest keyed
    /// ancestor's key and the child-index path below it (<c>artist-under-band/1/0/3</c>), or <c>gone:&lt;index&gt;</c> when
    /// that node is no longer live at that generation (§A.5). Returns the chars written.</summary>
    public int DescribeNode(int nodeIndex, uint gen, Span<char> dst) => NodeDescriber.Describe(_scene, nodeIndex, gen, dst);

    /// <summary>UI THREAD. <see cref="DescribeNode"/> as a string (the exporter's convenience; allocates).</summary>
    public string DescribeNode(int nodeIndex, uint gen)
    {
        Span<char> buf = stackalloc char[256];
        int n = DescribeNode(nodeIndex, gen, buf);
        return new string(buf[..n]);
    }

    /// <summary>UI THREAD. Every live scroll viewport with a bound handle (evidence tooling; allocates the rows).</summary>
    public void CopyViewports(List<ViewportInfo> dst)
    {
        foreach (var kv in _scrollHandles)
        {
            var h = _scene.HandleAt(kv.Key);
            if (h.IsNull || !_scene.IsLive(h) || !_scene.TryGetScroll(h, out var sc)) continue;
            bool horizontal = sc.Orientation == 1;
            // The window rect (DIP) and the virtualizer's coverage — what a coverage-clamp capture compares the shown
            // offset against (evidence, 2026-09-25 item G: blank rows on fast non-wheel scrolls).
            RectF r = _scene.AbsoluteRect(h);
            dst.Add(new ViewportInfo(kv.Key, h.Raw.Gen, sc.ScrollKey, sc.Offset, horizontal ? sc.ContentW : sc.ContentH,
                horizontal ? sc.ViewportW : sc.ViewportH, horizontal, r.X, r.Y, r.W, r.H, sc.CoverStart, sc.CoverEnd,
                sc.WindowOrigin, sc.FirstRealized, sc.LastRealized, sc.ItemCount, sc.PersistentPrefixCount, sc.MeasureAll));
        }
    }

    // ── the stale-tile invariant's log edge ──────────────────────────────────────────────────────────────────────

    private readonly StaleTileSample[] _evStale = new StaleTileSample[8];
    private int _evStaleLoggedSlice = -1;

    /// <summary>After the census (the composite turn's owner): a turn that ended with stale tiles counts on
    /// <see cref="TileInvariants"/>, and a slice that BECOMES stale (it was not the offender logged since the last clean
    /// turn) writes one always-on <c>[tiles.stale]</c> line naming the node, the tiles, the two hashes and when the pixels
    /// were rastered. Never per frame. The line is formatted and written on a thread-pool thread from a preallocated work
    /// item, so the composite turn itself allocates nothing even on the edge (the render path's zero-alloc gates hold
    /// while a stale tile persists).</summary>
    private void NoteStaleTiles(SliceTable tiles, int staleTiles)
    {
        if (staleTiles <= 0) { _evStaleLoggedSlice = -1; return; }
        int n = tiles.CopyStale(_evStale);
        if (n <= 0) return;
        ref readonly StaleTileSample first = ref _evStale[0];
        TileInvariants.NoteStaleTurn(staleTiles, in first);
        if (first.SliceId == _evStaleLoggedSlice) return;
        _evStaleLoggedSlice = first.SliceId;
        _evStaleLog.Post(_evStale.AsSpan(0, n), staleTiles);
    }

    private readonly StaleLogWork _evStaleLog = new();

    /// <summary>The deferred <c>[tiles.stale]</c> writer: one preallocated work item, at most one line in flight (a newer
    /// edge while one is queued is dropped — the tally on <see cref="TileInvariants"/> still counts it).</summary>
    private sealed class StaleLogWork : IThreadPoolWorkItem
    {
        private readonly StaleTileSample[] _samples = new StaleTileSample[8];
        private int _count, _total, _queued;

        public void Post(ReadOnlySpan<StaleTileSample> samples, int total)
        {
            if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
            int n = Math.Min(samples.Length, _samples.Length);
            samples[..n].CopyTo(_samples);
            _count = n; _total = total;
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        public void Execute()
        {
            string line = StaleLine(_samples.AsSpan(0, _count), _total);
            Volatile.Write(ref _queued, 0);
            Diag.Line(line);
        }
    }

    /// <summary>The <c>[tiles.stale]</c> line (pure formatting; <c>node=</c> carries index:gen — names resolve UI-side at export).</summary>
    internal static string StaleLine(ReadOnlySpan<StaleTileSample> s, int total)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(160);
        sb.Append("[tiles.stale] frame=").Append(s[0].Frame.ToString(ci))
          .Append(" stale=").Append(total.ToString(ci))
          .Append(" slice=").Append(s[0].SliceId.ToString(ci))
          .Append(" role=").Append(((FluentGpu.Render.SliceRole)(s[0].Sub & 7)).ToString()).Append(" seg=").Append((s[0].Sub >> 3).ToString(ci))
          .Append(" node=").Append(s[0].NodeIndex.ToString(ci)).Append(':').Append(s[0].Gen.ToString(ci))
          .Append(" tiles=");
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('(').Append(s[i].Tx.ToString(ci)).Append(',').Append(s[i].Ty.ToString(ci)).Append(')');
        }
        sb.Append(" want=").Append(s[0].Want.ToString("x16", ci))
          .Append(" have=").Append(s[0].Have.ToString("x16", ci))
          .Append(" rasterFrame=").Append(s[0].RasterFrame.ToString(ci));
        return sb.ToString();
    }

    // ── the per-turn cost row (ScrollProbe TurnCost) ─────────────────────────────────────────────────────────────

    private long _evBuildTicks, _evSubmitTicks;

    /// <summary>The composite turn's owner, after its submit (or its elision): hand the turn's cost split to the probe; the
    /// render thread's per-present <c>Turn</c> row carries it out with the tick seq it belongs to.</summary>
    private void NoteTurnCost(SliceRecorder slices, double recordMs, bool compositeOnly, bool keptAll, bool skipSubmit, bool capture)
    {
        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double gpuMs = _swapchain.TryGetGpuRenderSample(out GpuRenderSample g) ? g.ExecutionMs : 0.0;
        byte flags = (byte)((compositeOnly ? FluentGpu.Scroll.Diag.ProbeRow.TurnCostCompositeOnly : 0)
                            | (keptAll ? FluentGpu.Scroll.Diag.ProbeRow.TurnCostKeptAll : 0)
                            | (skipSubmit ? FluentGpu.Scroll.Diag.ProbeRow.TurnCostSkipSubmit : 0)
                            | (capture ? FluentGpu.Scroll.Diag.ProbeRow.TurnCostCapture : 0));
        FluentGpu.Scroll.Diag.ScrollProbe.NoteTurnCost(recordMs,
            skipSubmit ? 0.0 : _evBuildTicks * toMs, skipSubmit ? 0.0 : _evSubmitTicks * toMs, gpuMs,
            skipSubmit ? 0 : slices.LastRasteredTiles, skipSubmit ? 0 : (int)(slices.LastRasteredBytes / 1024),
            slices.LastStats.Walked, skipSubmit ? 0 : slices.LastItems.Length, flags, compositeOnly ? 0u : slices.PassFrame);
    }

    // ── on-demand frame capture (§A.6) ───────────────────────────────────────────────────────────────────────────

    private int _evCaptureArmed;
    private CompositeFrameCopy? _evCaptureCopy;
    private FrameCaptureResult? _evCaptureResult;
    private readonly object _evCaptureLock = new();

    /// <summary>UI THREAD. Arm a one-shot capture of the NEXT composited present: its back buffer (read back through the
    /// device — the <c>--repaint-identity</c> readback; the turn stalls once, on demand) and its composite record, both of
    /// the SAME turn, tagged with its publication. The next turn is made to submit even when nothing changed (the skip
    /// baselines are cleared — no tile is invalidated, so the capture shows exactly the retained pixels). Collect it with
    /// <see cref="TryTakeFrameCapture"/>.</summary>
    public void RequestFrameCapture()
    {
        var copy = new CompositeFrameCopy();   // a fresh slab per request: a previous result may still be being written out
        lock (_evCaptureLock) { _evCaptureCopy = copy; _evCaptureResult = null; }
        Volatile.Write(ref _evCaptureArmed, 1);
        _lastPresentedDrawListHash = 0UL;
        WakeFrame();
    }

    /// <summary>True while a requested capture has not landed yet.</summary>
    public bool FrameCapturePending => Volatile.Read(ref _evCaptureArmed) != 0;

    /// <summary>Any thread. The capture <see cref="RequestFrameCapture"/> armed, once it landed (then cleared).</summary>
    public bool TryTakeFrameCapture(out FrameCaptureResult? result)
    {
        lock (_evCaptureLock)
        {
            result = _evCaptureResult;
            _evCaptureResult = null;
        }
        return result is not null;
    }

    /// <summary>The composite turn's owner, right after the present of a COMPOSITED turn: complete an armed capture with
    /// this turn's back buffer and ledger frame.</summary>
    private void CompleteFrameCapture(SliceRecorder slices, ulong publishSeq)
    {
        if (Volatile.Read(ref _evCaptureArmed) == 0) return;
        CompositeFrameCopy? copy;
        lock (_evCaptureLock) copy = _evCaptureCopy;
        if (copy is null) return;
        slices.CompositeLedger?.CopyLatest(copy);
        byte[]? px = null;
        int w = 0, h = 0;
        try
        {
            if (!_device.TryCaptureBackBuffer(out px, out w, out h)) { px = null; w = h = 0; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
        {
            Diag.Line("[evidence] capture readback failed: " + ex.GetType().Name + ": " + ex.Message);
            px = null; w = h = 0;
        }
        var result = new FrameCaptureResult(publishSeq, copy.Header.Frame, System.Diagnostics.Stopwatch.GetTimestamp(), w, h, px, copy);
        lock (_evCaptureLock) { _evCaptureResult = result; _evCaptureCopy = null; }
        Volatile.Write(ref _evCaptureArmed, 0);
    }
}
