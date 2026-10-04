using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Scene;

/// <summary>
/// Operation ultra-fast GPU engine, P8 — the parity half of incremental capture.
/// <para><b><see cref="EqualsForParity"/></b> is the definition of correct: an incremental refresh is right IFF the
/// snapshot it produces is column-for-column indistinguishable from a from-scratch capture of the same store at the
/// same instant. Every <c>gate.capture.*</c> check is that comparison against a scratch snapshot, and so is the DEBUG
/// self-check below.</para>
/// <para><b>The DEBUG self-check.</b> In a DEBUG (or <c>FLUENTGPU_DIAG</c>) build incremental captures are re-derived
/// in full into a scratch snapshot and compared — the first of every incremental streak, then every Nth (the cost is
/// bounded; see <see cref="VerifyIncrementalParity"/>). On a divergence it does not assert and die —
/// it reports the offending column to stderr and REDOES the capture as a full one, so the published frame is correct
/// either way. That makes the whole VerticalSlice suite, the engine tests and any DEBUG app run a continuous audit of
/// the store's capture ledger: the day someone adds a column write without a <c>NoteCaptureChanged</c> /
/// <c>NoteBulkMutation</c>, it says so instead of silently painting last frame's geometry. Release compiles all of it
/// out (the repo's "production safety == CI coverage" rule).</para>
/// </summary>
public sealed partial class SceneRecordingSnapshot
{
#if DEBUG || FLUENTGPU_DIAG
    /// <summary>Whether the DEBUG incremental-capture self-check is compiled in (false in Release).</summary>
    public const bool ParityVerifyCompiledIn = true;

    private SceneRecordingSnapshot? _parityScratch;
    private bool _inParityVerify;

    /// <summary>Incremental captures that diverged from a full one and were redone as full (DEBUG only). Always 0 in a
    /// correct build; a gate asserts it stays there.</summary>
    public int IncrementalParityFailures { get; private set; }

    /// <summary>Incremental captures the self-check actually re-derived and compared (DEBUG only).</summary>
    internal int ParityVerifications { get; private set; }

    /// <summary>The self-check never runs more often than every Nth publish of an incremental streak (after the first)…</summary>
    internal const int ParityVerifyMinInterval = 8;
    /// <summary>…and on a large scene not more than this many captured nodes' worth of verification per publish.</summary>
    internal const int ParityNodesPerPublish = 256;

    /// <summary>
    /// Re-derive a FULL capture and compare — bounded, deterministic, always compiled into Debug. A verification is one
    /// complete re-capture of the reachable scene plus a column-by-column compare; measured on the gallery's 100k-row
    /// list page (412 live nodes, Debug, the Adreno X1 dev machine) it costs ~1.8 ms and ~2.2 MB of allocation. Run on
    /// EVERY incremental publish that made each Debug frame 2 ms of UI work instead of 0.2 ms and drove 67 gen-0 GCs a
    /// second (266 MB/s) — the margin every Debug measurement is taken against, erased by the verifier.
    /// <para>The rule: within a streak of incremental captures (reset by every full capture) the FIRST one is always
    /// verified, then every Nth, N = max(<see cref="ParityVerifyMinInterval"/>, ceil(capturedNodes /
    /// <see cref="ParityNodesPerPublish"/>)). Amortized that is ≤ 1/8 of a verification per publish on a small scene
    /// and ≤ 256 nodes' worth on a large one. It stays a real verifier because the bug class it exists for — a store
    /// write without <c>NoteCaptureChanged</c> — leaves the snapshot's row stale until that node is captured again: the
    /// divergence PERSISTS, so the next verification (at most N publishes later) sees it and heals the frame by redoing
    /// it as a full capture; a unit fixture's first incremental capture after its full one is always checked. A
    /// divergence that heals itself inside the window (the node was re-captured for another reason) was on screen for
    /// fewer than N frames. Deterministic: a per-snapshot counter, no clock, no sampling.</para>
    /// </summary>
    private void VerifyIncrementalParity(SceneStore source, ReadOnlySpan<NodeHandle> extraRoots)
    {
        if (_inParityVerify) return;
        int every = Math.Max(ParityVerifyMinInterval, (_captured.Count + ParityNodesPerPublish - 1) / ParityNodesPerPublish);
        if (_incrementalStreak != 1 && _incrementalStreak % every != 0) return;
        ParityVerifications++;
        var scratch = _parityScratch ??= new SceneRecordingSnapshot();
        scratch.Capture(source, extraRoots);
        bool equal = EqualsForParity(scratch, out string mismatch);
        scratch.ReleaseResources();   // balance the span-run references the scratch capture took
        if (equal) return;

        IncrementalParityFailures++;
        Console.Error.WriteLine(
            $"[fg-capture-parity] an incremental capture diverged from a from-scratch one ({mismatch}). " +
            "Some store write mutated a captured column without NoteCaptureChanged/NoteBulkMutation. " +
            "Redoing this publication as a FULL capture so the frame stays correct.");
        _inParityVerify = true;
        try { CaptureCore(source, extraRoots, incremental: false, baseline: 0UL); }
        finally { _inParityVerify = false; }
    }
#else
    /// <summary>Whether the DEBUG incremental-capture self-check is compiled in (false in Release).</summary>
    public const bool ParityVerifyCompiledIn = false;

    /// <summary>Always 0 in Release — the self-check is compiled out.</summary>
    public int IncrementalParityFailures => 0;

    /// <summary>Always 0 in Release — the self-check is compiled out.</summary>
    internal int ParityVerifications => 0;

    private void VerifyIncrementalParity(SceneStore source, ReadOnlySpan<NodeHandle> extraRoots) { }
#endif

    /// <summary>
    /// Column-level equality against another snapshot of the same scene — the invariant an incremental capture must
    /// preserve. Compares the frame scalars, the removal ledger, the orphan/overlay/spotlight lists, every dense column
    /// of every LIVE slot, every per-node sparse table, the scroll chrome and bind topology, and the referenced-image
    /// set (as a SET: capture order is documented as arbitrary). Dead slots are compared only for deadness — their
    /// dense rows are stale leftovers in BOTH snapshots and nothing may read them.
    /// </summary>
    public bool EqualsForParity(SceneRecordingSnapshot other, out string mismatch)
    {
        if (!Scalars(other, out mismatch)) return false;

        int count = Math.Min(_nodeCount, Math.Max(Capacity, other.Capacity));
        for (int i = 1; i < count; i++)
        {
            // A fresh/trimmed snapshot has no storage for an unreachable high-index slot; a high-water
            // snapshot has a cleared handle there. Both mean dead, while every live row is still compared.
            NodeHandle mine = i < Capacity ? _handles[i] : default;
            NodeHandle theirs = i < other.Capacity ? other._handles[i] : default;
            if (mine != theirs) { mismatch = $"n#{i} handle {mine.Raw.Index}:{mine.Raw.Gen} vs {theirs.Raw.Index}:{theirs.Raw.Gen}"; return false; }
            if (mine.IsNull) continue;
            if (!Node(other, i, out mismatch)) return false;
        }
        return true;
    }

    private bool Scalars(SceneRecordingSnapshot other, out string mismatch)
    {
        mismatch = "";
        if (_nodeCount != other._nodeCount) return Fail(out mismatch, $"nodeCount {_nodeCount} vs {other._nodeCount}");
        if (Root != other.Root) return Fail(out mismatch, "Root");
        if (DeviceScale != other.DeviceScale) return Fail(out mismatch, "DeviceScale");
        if (DragGhost != other.DragGhost) return Fail(out mismatch, "DragGhost");
        if (DragOverlay != other.DragOverlay) return Fail(out mismatch, "DragOverlay");
        if (DragGhostBackplate != other.DragGhostBackplate) return Fail(out mismatch, "DragGhostBackplate");
        if (DropSpotlightActive != other.DropSpotlightActive) return Fail(out mismatch, "DropSpotlightActive");
        if (DropSpotlightRootCount != other.DropSpotlightRootCount) return Fail(out mismatch, "DropSpotlightRootCount");
        if (OverlayCount != other.OverlayCount) return Fail(out mismatch, "OverlayCount");
        if (OverlayClip != other.OverlayClip) return Fail(out mismatch, "OverlayClip");
        if (SpotlightScrimClip != other.SpotlightScrimClip) return Fail(out mismatch, "SpotlightScrimClip");
        if (OrphanCount != other.OrphanCount) return Fail(out mismatch, "OrphanCount");
        if (HasActiveVirtualDisclosures != other.HasActiveVirtualDisclosures) return Fail(out mismatch, "HasActiveVirtualDisclosures");
        if (PendingRemovalOverflow != other.PendingRemovalOverflow) return Fail(out mismatch, "PendingRemovalOverflow");
        if (_removalCount != other._removalCount) return Fail(out mismatch, "removalCount");
        for (int i = 0; i < _removalCount; i++)
            if (_removals[i] != other._removals[i]) return Fail(out mismatch, $"removal[{i}]");
        if (CapturedNodeCount != other.CapturedNodeCount) return Fail(out mismatch, $"capturedNodeCount {CapturedNodeCount} vs {other.CapturedNodeCount}");
        for (int i = 0; i < OrphanCount; i++)
            if (_orphans[i] != other._orphans[i]) return Fail(out mismatch, $"orphan[{i}]");
        for (int i = 0; i < OverlayCount; i++)
            if (_overlays[i] != other._overlays[i]) return Fail(out mismatch, $"overlay[{i}]");
        for (int i = 0; i < DropSpotlightRootCount; i++)
            if (_spotlights[i] != other._spotlights[i]) return Fail(out mismatch, $"spotlight[{i}]");
        if (_referencedImageIdCount != other._referencedImageIdCount)
            return Fail(out mismatch, $"referencedImageIds count {_referencedImageIdCount} vs {other._referencedImageIdCount}");
        for (int i = 0; i < _referencedImageIdCount; i++)
        {
            int id = _referencedImageIds[i];
            bool found = false;
            for (int j = 0; j < other._referencedImageIdCount && !found; j++) found = other._referencedImageIds[j] == id;
            if (!found) return Fail(out mismatch, $"referencedImageId {id} missing from the full capture");
        }
        return true;
    }

    private bool Node(SceneRecordingSnapshot other, int i, out string mismatch)
    {
        mismatch = "";
        if (_parent[i] != other._parent[i]) return Fail(out mismatch, $"n#{i} Parent");
        if (_firstChild[i] != other._firstChild[i]) return Fail(out mismatch, $"n#{i} FirstChild");
        if (_nextSibling[i] != other._nextSibling[i]) return Fail(out mismatch, $"n#{i} NextSibling");
        if (_bounds[i] != other._bounds[i]) return Fail(out mismatch, $"n#{i} Bounds {_bounds[i]} vs {other._bounds[i]}");
        if (!PaintEqual(in _paint[i], in other._paint[i])) return Fail(out mismatch, $"n#{i} NodePaint");
        if (!_interaction[i].Equals(other._interaction[i])) return Fail(out mismatch, $"n#{i} InteractionInfo");
        if (_flags[i] != other._flags[i]) return Fail(out mismatch, $"n#{i} NodeFlags {_flags[i]} vs {other._flags[i]}");
        if (_dirty[i] != other._dirty[i]) return Fail(out mismatch, $"n#{i} recordDirty {_dirty[i]} vs {other._dirty[i]}");
        if (_dirtySelf[i] != other._dirtySelf[i]) return Fail(out mismatch, $"n#{i} recordDirtySelf");
        if (_dirtyDescendant[i] != other._dirtyDescendant[i]) return Fail(out mismatch, $"n#{i} recordDirtyDescendant");
        if (!SparseEqual(_textStyle, other._textStyle, i, (in TextStyle a, in TextStyle b) => a.Equals(b), out string which)) return Fail(out mismatch, $"n#{i} TextStyle ({which})");
        if (!SparseEqual(_measurement, other._measurement, i, MeasureEqual, out which)) return Fail(out mismatch, $"n#{i} TextMeasureCache ({which})");

        if (!SparseEqual(_scroll, other._scroll, i, ScrollEqual, out which)) return Fail(out mismatch, $"n#{i} ScrollState ({which})");
        if (!SparseEqual(_interact, other._interact, i, (in InteractionAnim a, in InteractionAnim b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} InteractionAnim ({which})");
        if (!SparseEqual(_shadow, other._shadow, i, (in ShadowSpec a, in ShadowSpec b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} ShadowSpec ({which})");
        if (!SparseEqual(_arc, other._arc, i, (in ArcSpec a, in ArcSpec b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} ArcSpec ({which})");
        if (!SparseEqual(_polyline, other._polyline, i, (in PolylineStrokeSpec a, in PolylineStrokeSpec b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} PolylineStrokeSpec ({which})");
        if (!SparseEqual(_path, other._path, i, (in PathSpec a, in PathSpec b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} PathSpec ({which})");
        if (!SparseEqual(_series, other._series, i, SeriesEqual, out which)) return Fail(out mismatch, $"n#{i} SeriesSpec ({which})");
        if (!SparseEqual(_clipPath, other._clipPath, i, (in ClipPathSpec a, in ClipPathSpec b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} ClipPathSpec ({which})");
        if (!SparseEqual(_radialCenter, other._radialCenter, i, (in Point2 a, in Point2 b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} RadialCenter ({which})");
        if (!SparseEqual(_GradientTo, other._GradientTo, i, GradientEqual, out which)) return Fail(out mismatch, $"n#{i} GradientTo ({which})");
        if (!SparseEqual(_gradientMix, other._gradientMix, i, (in float a, in float b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} GradientMix ({which})");
        if (!SparseEqual(_blend, other._blend, i, (in byte a, in byte b) => a == b, out which)) return Fail(out mismatch, $"n#{i} Blend ({which})");
        if (!SparseEqual(_feedback, other._feedback, i, (in FeedbackState a, in FeedbackState b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} Feedback ({which})");
        if (!SparseEqual(_acrylic, other._acrylic, i, (in AcrylicSpec a, in AcrylicSpec b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} AcrylicSpec ({which})");
        if (!SparseEqual(_edgeFade, other._edgeFade, i, (in EdgeFadeSpec a, in EdgeFadeSpec b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} EdgeFadeSpec ({which})");
        if (!SparseEqual(_imageEffects, other._imageEffects, i, (in ImageVisualEffects a, in ImageVisualEffects b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} ImageVisualEffects ({which})");
        if (!SparseEqual(_brushAnim, other._brushAnim, i, (in BrushAnim a, in BrushAnim b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} BrushAnim ({which})");
        if (!SparseEqual(_textEdit, other._textEdit, i, (in TextEditState a, in TextEditState b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} TextEditState ({which})");
        if (!SparseEqual(_selectionHighlight, other._selectionHighlight, i, (in ColorF a, in ColorF b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} SelectionHighlight ({which})");
        if (!SparseEqual(_glyphWipe, other._glyphWipe, i, (in GlyphWipe a, in GlyphWipe b) => a.Equals(b), out which)) return Fail(out mismatch, $"n#{i} GlyphWipe ({which})");
        if (!SparseEqual(_Gradient, other._Gradient, i, GradientEqual, out which)) return Fail(out mismatch, $"n#{i} Gradient ({which})");
        if (!SparseEqual(_BorderBrush, other._BorderBrush, i, GradientEqual, out which)) return Fail(out mismatch, $"n#{i} BorderBrush ({which})");
        if (!SparseEqual(_HoverGradient, other._HoverGradient, i, GradientEqual, out which)) return Fail(out mismatch, $"n#{i} HoverGradient ({which})");
        if (!SparseEqual(_PressedGradient, other._PressedGradient, i, GradientEqual, out which)) return Fail(out mismatch, $"n#{i} PressedGradient ({which})");
        if (!SparseEqual(_HoverBorderBrush, other._HoverBorderBrush, i, GradientEqual, out which)) return Fail(out mismatch, $"n#{i} HoverBorderBrush ({which})");
        if (!SparseEqual(_PressedBorderBrush, other._PressedBorderBrush, i, GradientEqual, out which)) return Fail(out mismatch, $"n#{i} PressedBorderBrush ({which})");

        var node = _handles[i];
        if (!RectsEqual(GetTextEditSelectionRects(node), other.GetTextEditSelectionRects(node))) return Fail(out mismatch, $"n#{i} selection rects");
        if (!RectsEqual(GetTextEditUnderlineRects(node), other.GetTextEditUnderlineRects(node))) return Fail(out mismatch, $"n#{i} underline rects");

        bool hasSpans = TryGetSpanDecorations(node, out var mineStyles, out var mineRects);
        bool otherSpans = other.TryGetSpanDecorations(node, out var theirStyles, out var theirRects);
        if (hasSpans != otherSpans) return Fail(out mismatch, $"n#{i} span decorations presence");
        if (hasSpans)
        {
            if (mineStyles.Length != theirStyles.Length || mineRects.Length != theirRects.Length)
                return Fail(out mismatch, $"n#{i} span decoration lengths");
            for (int k = 0; k < mineStyles.Length; k++)
                if (!mineStyles[k].Equals(theirStyles[k])) return Fail(out mismatch, $"n#{i} span style[{k}]");
            for (int k = 0; k < mineRects.Length; k++)
                if (!mineRects[k].Equals(theirRects[k])) return Fail(out mismatch, $"n#{i} span rect[{k}]");
        }

        bool hasRow = TryGetRowCells(node, out var mineCells, out bool minePh, out ColorF minePhColor);
        bool otherRow = other.TryGetRowCells(node, out var theirCells, out bool theirPh, out ColorF theirPhColor);
        if (hasRow != otherRow) return Fail(out mismatch, $"n#{i} row cells presence");
        if (hasRow && !RowCellsEqual(mineCells, minePh, minePhColor, theirCells, theirPh, theirPhColor))
            return Fail(out mismatch, $"n#{i} row cells");

        bool hasSeries = TryGetSeriesSamples(node, out var mineSeries);
        bool otherSeries = other.TryGetSeriesSamples(node, out var theirSeries);
        if (hasSeries != otherSeries) return Fail(out mismatch, $"n#{i} series samples presence");
        if (hasSeries && !mineSeries.SequenceEqual(theirSeries)) return Fail(out mismatch, $"n#{i} series samples");

        bool hasSprites = TryGetSprites(node, out var mineSprites);
        bool otherSprites = other.TryGetSprites(node, out var theirSprites);
        if (hasSprites != otherSprites) return Fail(out mismatch, $"n#{i} sprites presence");
        if (hasSprites && !System.Runtime.InteropServices.MemoryMarshal.AsBytes(mineSprites).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(theirSprites)))
            return Fail(out mismatch, $"n#{i} sprites");
        if (TryGetSpriteSpec(node, out var mineSpec) != other.TryGetSpriteSpec(node, out var theirSpec) || mineSpec != theirSpec)
            return Fail(out mismatch, $"n#{i} sprite spec");

        if (!ScrollChrome.Get(i).Equals(other.ScrollChrome.Get(i))) return Fail(out mismatch, $"n#{i} scroll chrome");
        return true;
    }

    private delegate bool ValueEq<T>(in T a, in T b);

    private static bool SparseEqual<T>(SnapshotColumn<T> mine, SnapshotColumn<T> theirs, int index, ValueEq<T> eq, out string which)
    {
        bool a = mine.TryGet(index, out T va), b = theirs.TryGet(index, out T vb);
        if (a != b) { which = a ? "present only in the incremental capture" : "present only in the full capture"; return false; }
        if (!a) { which = ""; return true; }
        if (eq(in va, in vb)) { which = ""; return true; }
        which = "value";
        return false;
    }

    // ScrollState carries object references (Layout/SnapPoints/ScrollKey) that capture nulls out, so the default
    // structural comparison is both complete and correct here — and completeness is the point: a field this comparison
    // skipped would be a field an incremental capture could silently publish stale.
    private static bool ScrollEqual(in ScrollState a, in ScrollState b) => a.Equals(b);

    private static bool GradientEqual(in GradientSpec a, in GradientSpec b)
    {
        int an = a.Stops?.Length ?? 0, bn = b.Stops?.Length ?? 0;
        if (an != bn || a.Shape != b.Shape || a.AngleDeg != b.AngleDeg) return false;
        for (int i = 0; i < an; i++) if (!a.Stops![i].Equals(b.Stops![i])) return false;
        return true;
    }

    private static bool SeriesEqual(in SeriesSpec a, in SeriesSpec b)
    {
        if (a.Shape != b.Shape || !a.Color.Equals(b.Color) || a.Thickness != b.Thickness || !a.Baseline.Equals(b.Baseline)
            || a.Amplitude != b.Amplitude || a.Opacity != b.Opacity || (a.Gradient is null) != (b.Gradient is null)
            || (a.GradientTo is null) != (b.GradientTo is null) || a.GradientAxis != b.GradientAxis || a.AntiAlias != b.AntiAlias
            || a.Blend != b.Blend) return false;
        if (a.Gradient is { } ga && b.Gradient is { } gb && !GradientEqual(in ga, in gb)) return false;
        if (a.GradientTo is { } ta && b.GradientTo is { } tb && !GradientEqual(in ta, in tb)) return false;
        return true;
    }

    private static bool PaintEqual(in NodePaint a, in NodePaint b) => a.Equals(b);

    private static bool MeasureEqual(in TextMeasureCache a, in TextMeasureCache b)
        => EntryEqual(in a.E0, in b.E0) && EntryEqual(in a.E1, in b.E1);

    private static bool EntryEqual(in TextMeasureEntry a, in TextMeasureEntry b)
        => a.Valid == b.Valid && a.Text == b.Text && a.MaxW.Equals(b.MaxW) && a.Style == b.Style
           && a.Size.Width == b.Size.Width && a.Size.Height == b.Size.Height && a.FitSize == b.FitSize
           && a.UnderlineY == b.UnderlineY && a.UnderlineThickness == b.UnderlineThickness && a.StrikeY == b.StrikeY;

    private static bool RowCellsEqual(ReadOnlySpan<RowCellRecorded> a, bool aPh, ColorF aPhColor,
        ReadOnlySpan<RowCellRecorded> b, bool bPh, ColorF bPhColor)
    {
        if (aPh != bPh || !aPhColor.Equals(bPhColor) || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    private static bool RectsEqual(ReadOnlySpan<RectF> a, ReadOnlySpan<RectF> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool Fail(out string mismatch, string reason) { mismatch = reason; return false; }
}
