using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>
/// The width structure is chosen for. While a WIDTH reflow is in flight (a docking panel's reservation easing through real layout),
/// every box it resizes takes a new laid-out width on each frame of the slide, but what the box SHOWS (a grid's column count, where a
/// wrapping row breaks, where a multi-line text run breaks its lines, a shelf's columns, a form or rung a component picks) belongs to the
/// width it will have when the reflow lands. The host solves the layout once at the reflows' targets on the frame that seeds or re-aims
/// them, and again on a commit while one flies (<c>AppHost</c>'s destination probe), and records here the width every box under each reflow's parent has in that solve. <see cref="StructureWidth"/> answers that width
/// while the reflow flies and the live width otherwise; the live width still sizes and places what was chosen. The record is dropped
/// when the last width reflow lands, so nothing stays decided for a width the box never reaches. Allocation-free once the columns have
/// grown to the scene's size.
/// <para>The structure width is a layout INPUT of the nodes whose solve reads it (<see cref="NoteStructureReader"/>): a grid's column
/// count, a wrap row's breaks, a virtual fill's columns, a multi-line run's breaks. So every change of what it answers marks those
/// readers layout-dirty, like any other input write: a record that enters a reader, a clear that drops one, and a flip of the layout's
/// landing breaks (FlexLayout.BreakTextAtLanding, <see cref="MarkRecordedStructureReaders"/>). Without the mark a measure-ring slot or an
/// arrange early-out stored under the old answer would keep answering for it (a grid laid out at its slide's start with the column count
/// of the frame before; a run measured before the flip keeping its live breaks through the slide).</para>
/// <para>The record must describe the tree the slide lands: a commit that ANSWERS it in place (a page that takes the sizes of the tier it
/// lands at, <see cref="ConsumeRecordAnswered"/>) and a layout that solves a structure reader it does not cover (a form's new layer,
/// <see cref="ConsumeUnrecordedReader"/>) have the host probe it again; a box that leaves as an exit orphan drops its record
/// (<see cref="DropOrphanRecord"/>), so it never breaks for a landing it will not reach.</para>
/// </summary>
public sealed partial class SceneStore
{
    private float[] _destW = [];
    private NodeHandle[] _destNode = [];   // the node each entry was recorded for: a recycled index never reads its predecessor's width
    private int[] _destWrote = [];
    private int _destCount;
    // The node each index was recorded for earlier in this slide (a probe's re-record follows its first record): a reader is new to a record only
    // once a slide. Valid for the slide it was kept in (_destSlide): a landing's clear starts the next slide afresh.
    private NodeHandle[] _destPrevNode = [];
    private int[] _destPrevSlide = [];
    private int _destSlide;
    private NodeHandle[] _structReader = [];   // per index: the node whose last solve read its structure width (a recycled index is no reader)

    /// <summary>Layout calls this for a node whose solve reads its structure width (or, for a multi-line run, may break at it): a change of
    /// what <see cref="StructureWidth"/> answers for it then marks it (see the type summary). <paramref name="audit"/> also notes a reader the
    /// record does not cover (<see cref="ConsumeUnrecordedReader"/>); the layout-verify oracle's re-solve observes and notes nothing.
    /// Allocation-free once grown.</summary>
    internal void NoteStructureReader(NodeHandle node, bool audit)
    {
        int idx = (int)node.Raw.Index;
        if (_structReader.Length <= idx) Array.Resize(ref _structReader, Math.Max(idx + 1, Math.Max(256, _structReader.Length * 2)));
        _structReader[idx] = node;
        if (audit && _destCount > 0 && !_unrecordedReader && !HasDestinationWidth(node) && HasRecordedAncestor(node)) _unrecordedReader = true;
    }

    /// <summary>Does a recorded box stand above <paramref name="node"/> (it lies under a flying width reflow's parent)?</summary>
    private bool HasRecordedAncestor(NodeHandle node)
    {
        for (var p = Parent(node); !p.IsNull; p = Parent(p))
            if (HasDestinationWidth(p)) return true;
        return false;
    }

    private void MarkIfStructureReader(int idx, NodeHandle n)
    {
        if (!n.IsNull && idx < _structReader.Length && _structReader[idx] == n && IsLive(n)) Mark(n, NodeFlags.LayoutDirty);
    }

    /// <summary>The layout's landing breaks flipped (FlexLayout.BreakTextAtLanding): mark every recorded run that reads where it lands and
    /// stands away from that width. The flip changes what a multi-line run breaks at and nothing else reads it (a grid's count, a wrap row's
    /// breaks read their structure width either way); a run whose box stands at its landing width breaks there at either setting.</summary>
    internal void MarkRecordedStructureReaders()
    {
        for (int i = 0; i < _destCount; i++)
        {
            int idx = _destWrote[i];
            if (_paint[idx].VisualKind == VisualKind.Text && MathF.Abs(_bounds[idx].W - _destW[idx]) > 0.5f) MarkIfStructureReader(idx, _destNode[idx]);
        }
    }

    /// <summary>The reconciler calls this for a node whose own layout input it rewrote in a render (a static size, flex, gap, padding, text or
    /// style, or a bound one whose re-pushed binding wrote another value): see <see cref="ConsumeRecordAnswered"/>. A signal firing a binding is
    /// no such write: that is the slide itself.</summary>
    internal void NoteRecordedInputWrite(NodeHandle node)
    {
        if (_destCount > 0 && HasDestinationWidth(node)) _recordedInputWritten = true;
    }
    private bool _recordedInputWritten;
    // The last record handed a landing width to a box with a bounds reader this slide had not recorded (RecordDestinationSubtree): the reader
    // decides its structure there (a page's tier and form) and the tree answers on the next commit.
    private bool _recordAnnounced;
    // A structure reader with no record of its own was solved under a recorded box (a form's new layer, a row that mounted mid-slide): it chose
    // its structure at its live width, which the slide does not land at.
    private bool _unrecordedReader;

    /// <summary>Once per commit, after its flush: did it ANSWER the record, re-rendering a recorded box in place with new layout inputs on the
    /// commit after the record announced a landing width to its readers? The tree took the form of the width it lands at (a page that keeps
    /// its nodes and takes the next tier's sizes), so every width recorded under it describes the form it left and the host probes the
    /// destination again. A probe's record announces only to a reader the slide had not recorded, so a slide answers once per new reader; a
    /// re-render on any other commit (a part that follows the live width) keeps the record: two more solves a frame for nothing.</summary>
    public bool ConsumeRecordAnswered()
    {
        bool answered = _recordAnnounced && _recordedInputWritten;
        _recordAnnounced = false;
        _recordedInputWritten = false;
        return answered;
    }

    /// <summary>Once per frame, after its layout: did it solve a structure reader the record does not cover under a recorded box
    /// (<see cref="NoteStructureReader"/>)? Its column count, wrap or line breaks were chosen at its live width: the host probes the destination
    /// so it is recorded where it lands. A box the commit built that reads no structure (a pager's dot, a card at its bound width) needs no
    /// record and probes nothing.</summary>
    public bool ConsumeUnrecordedReader()
    {
        bool unrecorded = _unrecordedReader;
        _unrecordedReader = false;
        return unrecorded;
    }

    /// <summary>A box that leaves as an exit orphan (<see cref="Orphan"/>) drops its subtree's record: it lands nowhere, so a run of it never
    /// breaks for a landing it will not reach and keeps the lines it leaves with.</summary>
    private void DropOrphanRecord(NodeHandle n)
    {
        int idx = (int)n.Raw.Index;
        if (idx < _destNode.Length && _destNode[idx] == n)
        {
            _destNode[idx] = NodeHandle.Null;
            _destW[idx] = float.NaN;
            MarkIfStructureReader(idx, n);
        }
        for (var c = FirstChild(n); !c.IsNull; c = NextSibling(c)) DropOrphanRecord(c);
    }

    /// <summary>True while destination widths are recorded (a width reflow is in flight and the host probed its target).</summary>
    public bool HasDestinationWidths => _destCount > 0;

    /// <summary>The width <paramref name="node"/>'s structure is chosen for: its width at the in-flight width reflow's target when the
    /// host recorded one for it, else <paramref name="live"/> (its width in the layout being solved). Discrete choices read this
    /// (column counts, wrap breaks, forms); sizes and positions read the live width.</summary>
    public float StructureWidth(NodeHandle node, float live)
    {
        if (_destCount == 0 || node.IsNull) return live;
        uint idx = node.Raw.Index;
        if (idx >= (uint)_destW.Length || _destNode[idx] != node) return live;
        float w = _destW[idx];
        return float.IsNaN(w) ? live : w;
    }

    /// <summary>True when a destination width is recorded for <paramref name="node"/>.</summary>
    internal bool HasDestinationWidth(NodeHandle node)
    {
        if (_destCount == 0 || node.IsNull) return false;
        uint idx = node.Raw.Index;
        return idx < (uint)_destW.Length && _destNode[idx] == node;
    }

    /// <summary>Record the laid-out width of <paramref name="root"/> and every box under it as its destination width. The caller has
    /// just solved the layout at the reflows' targets. A box already recorded in this probe keeps its first width (a later pass of
    /// the seed lays some subtrees out at their start).</summary>
    internal void RecordDestinationWidths(NodeHandle root)
    {
        if (root.IsNull || !IsLive(root)) return;
        RecordDestinationSubtree(root);
    }

    private void RecordDestinationSubtree(NodeHandle n)
    {
        int idx = (int)n.Raw.Index;
        if (_destW.Length <= idx) GrowDestination(idx + 1);
        if (_destNode[idx].IsNull)   // a slot enters the list once per record (a clear empties both)
        {
            if (_destCount == _destWrote.Length) Array.Resize(ref _destWrote, Math.Max(64, _destWrote.Length * 2));
            _destWrote[_destCount++] = idx;
            _destNode[idx] = n;
            float dest = _destW[idx] = _bounds[idx].W;
            // A box whose OnBoundsChanged handler or measured-bounds hook last heard another width hears this one on its next arrange, even when its
            // rect does not move there (the commit's re-solve at the slide's start lays it out where it stood): a reader of StructureWidth in a
            // handler (a column count, a tier) decides on the slide's first frame instead of on the first frame its box moves.
            bool reader = GetBoundsChangedHandler(n) is not null || GetBoundsChangedHook(n) is not null;
            if (reader && MathF.Abs(BoundsDeliveredRef(n).W - dest) > 0.5f) Mark(n, NodeFlags.BoundsChangedPending);
            // A reader this slide had not recorded (every reader of its first record; a box a probe's record finds new): it decides its structure
            // at its landing, in this layout's delivery or the next arrange's, and its tree answers on the next commit. A reader recorded before
            // in the slide heard its landing then (a probe that moves it, a card easing to a re-fit, changes no form the tree has not taken).
            if (reader && !(_destPrevSlide[idx] == _destSlide && _destPrevNode[idx] == n)) _recordAnnounced = true;
            MarkIfStructureReader(idx, n);   // it answered its live width until now
        }
        for (var c = FirstChild(n); !c.IsNull; c = NextSibling(c)) RecordDestinationSubtree(c);
    }

    /// <summary>Drop every recorded destination width. <paramref name="markChanged"/> hands each live box whose structure was chosen for a width
    /// it does not have back to its live width on the next solve: layout-dirty where its laid-out width differs from that width (the box's own
    /// count or wrap re-decides), and its bounds re-delivered where its OnBoundsChanged handler or measured-bounds hook last heard a width it does
    /// not have (<see cref="NodeFlags.BoundsChangedPending"/>: a reader of <see cref="StructureWidth"/> in a handler, a structure-width hook, would
    /// otherwise keep the recorded width until the box's bounds next move, which after a landing may be never). A structure reader is marked
    /// either way: it answers its live width from now on.</summary>
    internal void ClearDestinationWidths(bool markChanged)
    {
        for (int i = 0; i < _destCount; i++)
        {
            int idx = _destWrote[i];
            NodeHandle n = _destNode[idx];
            MarkIfStructureReader(idx, n);
            _destPrevNode[idx] = n;
            _destPrevSlide[idx] = _destSlide;
            if (markChanged && !n.IsNull && IsLive(n))
            {
                float dest = _destW[idx];
                bool resolve = MathF.Abs(_bounds[idx].W - dest) > 0.5f;
                bool redeliver = (GetBoundsChangedHandler(n) is not null || GetBoundsChangedHook(n) is not null)
                                 && MathF.Abs(BoundsDeliveredRef(n).W - dest) > 0.5f;
                if (redeliver) Mark(n, NodeFlags.LayoutDirty | NodeFlags.BoundsChangedPending);
                else if (resolve) Mark(n, NodeFlags.LayoutDirty);
            }
            _destW[idx] = float.NaN;
            _destNode[idx] = NodeHandle.Null;
        }
        _destCount = 0;
        _recordedInputWritten = false;
        _recordAnnounced = false;
        _unrecordedReader = false;
        if (markChanged) _destSlide++;   // the slide landed: the next record is the next slide's first
    }

    private void GrowDestination(int need)
    {
        int old = _destW.Length;
        int n = Math.Max(need, Math.Max(256, old * 2));
        Array.Resize(ref _destW, n);
        Array.Resize(ref _destNode, n);
        Array.Resize(ref _destPrevNode, n);
        Array.Resize(ref _destPrevSlide, n);
        _destW.AsSpan(old).Fill(float.NaN);
        _destPrevSlide.AsSpan(old).Fill(-1);
    }
}
