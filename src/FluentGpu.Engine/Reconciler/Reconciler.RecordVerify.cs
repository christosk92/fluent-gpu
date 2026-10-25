using System.Diagnostics;
using System.Text;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Reconciler;

// --fg record-verify=PATH (PERF r3 MODEL M4b, phase P1): every place that hands the reconciler an OLD element (Update's oldEl,
// a single-child slot's old child, the keyed diff's old children, the root) is checked against the node records: the old
// element must be the very element the node's record came from, and the old child list must be the node's scene children.
// Any difference is written to PATH ({pid} = the process id), first occurrence of a site+stack with its stack, then one short
// line per repeat. Zero mismatches over the crossall routes is the proof that reading the records instead is the same reconcile.
public sealed partial class TreeReconciler
{
#if DEBUG || FLUENTGPU_DIAG
    private static StreamWriter? s_rvOut;
    private static readonly Dictionary<string, int> s_rvSeen = new();
    private static long s_rvChecks, s_rvMismatches;
    internal static bool RecordVerifyOn => FluentGpu.Hosting.EngineSwitches.RecordVerifyPath is not null;

    private void VerifyOld(string site, NodeHandle node, Element? callerOld)
    {
        s_rvOut ??= OpenRecordVerify();
        if (++s_rvChecks % 20000 == 0) s_rvOut?.WriteLine($"[record-verify] checks {s_rvChecks} mismatches {s_rvMismatches}");
        int idx = (int)node.Raw.Index;
        bool has = _records.Has(idx);
        if (callerOld is not { } o)
        {
            if (has) RecordVerifyReport(site, node, $"caller has no old element; record {Describe(_records.Get(idx))}");
            return;
        }
        if (!has) { RecordVerifyReport(site, node, $"no record; caller old {Describe(o)}"); return; }
        if (_records.IsSource(idx, o)) return;
        var rec = _records.Get(idx);
        RecordVerifyReport(site, node, $"record {Describe(rec)} serial {_records.SourceSerial(idx)} != caller old {Describe(o)} serial {SerialOf(o)} (value-equal {rec.Equals(o)})");
    }

    private void VerifyOldChildren(NodeHandle node, ReadOnlySpan<Element> oldKids)
    {
        int i = 0;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), i++)
        {
            if (i < oldKids.Length) VerifyOld("children", c, oldKids[i]);
        }
        if (i != oldKids.Length)
            RecordVerifyReport("children-count", node, $"scene children {i} != old children {oldKids.Length}");
    }

    private static uint SerialOf(Element e) => e.Obj is FrozenElement f ? f.Serial : 0;

    private static string Describe(Element e)
        => e.IsNone ? "none" : $"kind {e.ElementTypeId} key '{e.Key}'";

    private void RecordVerifyReport(string site, NodeHandle node, string detail)
    {
        s_rvMismatches++;
        var st = new StackTrace(2, false);
        var sb = new StringBuilder();
        for (int f = 0; f < Math.Min(7, st.FrameCount); f++) sb.Append(st.GetFrame(f)?.GetMethod()?.Name).Append('<');
        string sig = site + " " + sb;
        s_rvOut ??= OpenRecordVerify();
        if (s_rvOut is null) return;
        if (s_rvSeen.TryGetValue(sig, out int id)) { s_rvOut.WriteLine($"#{id} node {node.Raw.Index} {detail}"); return; }
        id = s_rvSeen.Count + 1;
        s_rvSeen[sig] = id;
        s_rvOut.WriteLine($"=={id} [{site}] node {node.Raw.Index} '{_scene.DebugKeyOf(node)}' {detail}");
        s_rvOut.WriteLine("   at " + sig);
    }

    private static StreamWriter? OpenRecordVerify()
    {
        string? path = FluentGpu.Hosting.EngineSwitches.RecordVerifyPath;
        if (path is null) return null;
        path = path.Replace("{pid}", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var w = new StreamWriter(path, append: true) { AutoFlush = true };
        w.WriteLine($"[record-verify] pid {Environment.ProcessId}");
        return w;
    }
#else
    internal static bool RecordVerifyOn => false;
    private void VerifyOld(string site, NodeHandle node, Element? callerOld) { }
    private void VerifyOldChildren(NodeHandle node, ReadOnlySpan<Element> oldKids) { }
#endif
}
