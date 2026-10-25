using FluentGpu.Foundation;
using FluentGpu.Scene;
using System.Diagnostics;

namespace FluentGpu.Layout;

// ── --fg measure-census (PERF r3 UI, C1): why a node is measured more than once ───────────────────────────────────────────
//
// Every REAL measure (after the within-pass memo and the cross-pass ring had their chance: the calls that do work) is
// classified against the node's earlier real measures in the same frame: its FIRST; a REPEAT at a width it already had (which
// the memo would have answered within one pass, so it comes from a later solve of the frame); or a NEW WIDTH, by how far it is
// from the nearest width it had (float noise under 0.01 DIP, under 1 DIP, at least 1 DIP, or one of the two unbounded). Each
// also by phase (inside Measure, or a re-measure from Arrange), by node kind (text leaf, row, column, other) and by solve kind
// (root or scoped subtree). One line every 5 s on stderr. Off: one static bool read per real measure.
public sealed partial class FlexLayout
{
    /// <summary><c>--fg measure-census</c>.</summary>
    public static bool MeasureCensusEnabled;

    private struct CensusNode { public float W0, W1, W2; public int Count, Pass; public float LastW, LastH; public bool HasResult, NewWidth; }
    private readonly Dictionary<int, CensusNode> _mcNodes = new();
    private int _mcPass;
    private bool _mcArranging;
    private long _mcLastDump;
    private long _mcFrames, _mcReal, _mcFirst, _mcRepeat, _mcNoise, _mcSmall, _mcReal1, _mcInf, _mcArrangePhase, _mcText, _mcRow, _mcCol,
                 _mcRootSolves, _mcSubtreeSolves, _mcTextNew, _mcRepeatText, _mcNewSame, _mcNewDiff, _mcNewSameText, _mcFlatHits,
                 _mcCleanOtherWidth, _mcCleanOtherWidthFirst, _mcFlatVoidVer, _mcFlatVoidGen;

    private void CensusFrameStart()
    {
        _mcNodes.Clear();
        _mcPass = 0;
        _mcFrames++;
    }

    private void CensusSolve(bool root) { _mcPass++; if (root) _mcRootSolves++; else _mcSubtreeSolves++; }

    private void CensusMeasure(NodeHandle node, float availW, bool text, bool row)
    {
        _mcReal++;
        // A real measure of a CLEAN subtree whose cross-pass ring holds another width: what a ring that carried intervals could answer.
        if (_scene.IsLayoutClean(node) && node.Raw.Index < (uint)_memo.Length)
        {
            ref MeasureMemo rm = ref _memo[node.Raw.Index];
            bool other = (rm.Ring0NodeGen == node.Raw.Gen && !rm.Ring0AvailW.Equals(availW)) || (rm.Ring1NodeGen == node.Raw.Gen && !rm.Ring1AvailW.Equals(availW));
            if (other) { _mcCleanOtherWidth++; if (!_mcNodes.ContainsKey((int)node.Raw.Index)) _mcCleanOtherWidthFirst++; }
        }
        if (_mcArranging) _mcArrangePhase++;
        if (text) _mcText++; else if (row) _mcRow++; else _mcCol++;
        int i = (int)node.Raw.Index;
        if (!_mcNodes.TryGetValue(i, out var r))
        {
            _mcFirst++;
            _mcNodes[i] = new CensusNode { W0 = availW, Count = 1, Pass = _mcPass };
        }
        else
        {
            bool same = r.W0.Equals(availW) || (r.Count > 1 && r.W1.Equals(availW)) || (r.Count > 2 && r.W2.Equals(availW));
            if (same) { _mcRepeat++; if (text) _mcRepeatText++; }
            else
            {
                if (text) _mcTextNew++;
                float d = Delta(r.W0, availW);
                if (r.Count > 1) d = MathF.Min(d, Delta(r.W1, availW));
                if (r.Count > 2) d = MathF.Min(d, Delta(r.W2, availW));
                if (float.IsPositiveInfinity(d)) _mcInf++;
                else if (d < 0.01f) _mcNoise++;
                else if (d < 1f) _mcSmall++;
                else _mcReal1++;
            }
            if (r.Count == 1) r.W1 = availW; else if (r.Count == 2) r.W2 = availW;
            r.Count++;
            r.Pass = _mcPass;
            r.NewWidth = !same;
            _mcNodes[i] = r;
        }
        long now = Stopwatch.GetTimestamp();
        if (_mcLastDump == 0) _mcLastDump = now;
        if (now - _mcLastDump < Stopwatch.Frequency * 5) return;
        _mcLastDump = now;
        long created = _scene.NodesCreatedTotal;
        Console.Error.WriteLine(
            $"[measure-census] frames={_mcFrames} realMeasures={_mcReal} first={_mcFirst} repeatSameWidth={_mcRepeat} (text {_mcRepeatText}) " +
            $"newWidth: noise<0.01={_mcNoise} <1={_mcSmall} >=1={_mcReal1} unbounded={_mcInf} (text {_mcTextNew}) arrangePhase={_mcArrangePhase} " +
            $"kind: text={_mcText} row={_mcRow} col/other={_mcCol} rootSolves={_mcRootSolves} subtreeSolves={_mcSubtreeSolves} nodesCreatedTotal={created} " +
            $"newWidthResult: same={_mcNewSame} (text {_mcNewSameText}) different={_mcNewDiff} intervalHits={_mcFlatHits} intervalVoided: midPassMark={_mcFlatVoidVer} recycledSlot={_mcFlatVoidGen} " +
            $"cleanRingOtherWidth={_mcCleanOtherWidth} (first in frame {_mcCleanOtherWidthFirst})");
        _mcFrames = _mcReal = _mcFirst = _mcRepeat = _mcNoise = _mcSmall = _mcReal1 = _mcInf = _mcArrangePhase = _mcText = _mcRow = _mcCol = 0;
        _mcRootSolves = _mcSubtreeSolves = _mcTextNew = _mcRepeatText = _mcNewSame = _mcNewDiff = _mcNewSameText = _mcFlatHits = 0;
        _mcCleanOtherWidth = _mcCleanOtherWidthFirst = _mcFlatVoidVer = _mcFlatVoidGen = 0;
    }

    /// <summary>The general path's result for a real measure: for a NEW-width re-measure, whether it came out the same size as the
    /// node's previous measure this frame (a re-measure whose outcome changed nothing: the upper bound of what an exact skip saves).</summary>
    private void CensusResult(NodeHandle node, Size2 result)
    {
        int i = (int)node.Raw.Index;
        if (!_mcNodes.TryGetValue(i, out var r)) return;
        if (r.NewWidth && r.HasResult)
        {
            bool same = r.LastW.Equals(result.Width) && r.LastH.Equals(result.Height);
            if (same) { _mcNewSame++; if (_scene.Paint(node).VisualKind == VisualKind.Text) _mcNewSameText++; } else _mcNewDiff++;
        }
        r.LastW = result.Width; r.LastH = result.Height; r.HasResult = true; r.NewWidth = false;
        _mcNodes[i] = r;
    }

    private static float Delta(float a, float b)
    {
        if (float.IsInfinity(a) != float.IsInfinity(b)) return float.PositiveInfinity;
        if (float.IsInfinity(a)) return 0f;
        return MathF.Abs(a - b);
    }
}
