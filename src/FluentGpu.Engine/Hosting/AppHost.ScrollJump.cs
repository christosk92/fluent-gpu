using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.Hosting;

// The always-on unrequested-jump detector (2026-09-25, item J — the Library V3 rail "jumps when I select an item"). After
// the UI-side pose, every viewport's published frame is folded into its ScrollJumpWatch (ScrollJumpRules, pure): a viewport
// at rest whose anchor row moves on screen with no user input and no programmatic move is a JUMP — counted on the probe
// (ScrollProbe.Jumps / LastJump, what an app's auto evidence bundle triggers on) and written as one always-on
// `[scroll.jump]` line. Evidence only: nothing here changes a pose, a plan or a frame. Zero allocation on a frame without
// a jump; the line is formatted on a thread-pool thread from one preallocated work item (the [tiles.stale] pattern).
public sealed partial class AppHost
{
    private readonly JumpLogWork _jumpLog = new();

    /// <summary>7.7 (after <see cref="PoseScrollUi"/>): fold this frame into every bound viewport's jump watch.</summary>
    private void WatchScrollJumps()
    {
        for (int i = 0; i < _scrollNodes.Count; i++)
        {
            int idx = _scrollNodes[i];
            var node = _scene.HandleAt(idx);
            if (node.IsNull || !_scene.IsLive(node) || !_scene.HasScroll(node)) continue;
            if (!_scrollHandles.TryGetValue(idx, out var handle) || !handle.IsBound) continue;
            // A parked viewport is off screen; whatever changed while it was parked is not a jump the user saw.
            if ((_scene.Flags(node) & NodeFlags.Parked) != 0) { handle.JumpWatch = default; continue; }
            ref readonly ScrollState sc = ref _scene.ScrollRow(node);   // read-only: no write-intent ledger mark
            ScrollPlan plan = handle.Plan;
            double planPos = plan.Eval(_lastScrollPresentSec, out _, out bool settled);
            int anchor = 0;
            double anchorOffset = 0.0, prevAnchorOffsetNow = 0.0;
            if (sc.ItemCount > 0 && sc.Extent is { } ext && ext.Count > 0)
            {
                anchor = Math.Clamp(sc.AnchorIndex, 0, ext.Count - 1);
                anchorOffset = ext.OffsetOf(anchor);
                prevAnchorOffsetNow = handle.JumpWatch.HasBaseline
                    ? ext.OffsetOf(Math.Clamp(handle.JumpWatch.LastAnchorIndex, 0, ext.Count - 1))
                    : anchorOffset;
            }
            bool user = plan.Kind == MotionKind.Drag
                || (!settled && plan.Kind is MotionKind.Wheel or MotionKind.Fling or MotionKind.Thumb);
            var frame = new ScrollJumpFrame(_lastScrollPresentSec, sc.Offset, planPos, anchor, anchorOffset, prevAnchorOffsetNow,
                sc.ContentMain, _planSlots.FrameShiftOf(handle.Vp), plan.Seq, plan.Kind == MotionKind.Programmatic, user, settled,
                sc.ItemCount);
            if (!ScrollJumpRules.Step(ref handle.JumpWatch, in frame, out ScrollJump jump)) continue;
            long qpc = (long)(_lastScrollPresentSec * Stopwatch.Frequency);
            ScrollProbe.NoteJump(idx, qpc, in jump);
            _jumpLog.Post(idx, sc.ScrollKey, in jump, sc.FirstRealized, sc.LastRealized, sc.PersistentPrefixCount, qpc,
                _reconciler.LastRenderCensusDump);
        }
    }

    /// <summary>The <c>[scroll.jump]</c> line (pure formatting).</summary>
    internal static string JumpLine(int vp, string? key, in ScrollJump j, int firstRealized, int lastRealized, int prefix,
        long qpc, string? census)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(256);
        sb.Append("[scroll.jump] vp=").Append(key ?? "-").Append(" node=").Append(vp.ToString(ci))
          .Append(" from=").Append(j.From.ToString("0.##", ci))
          .Append(" to=").Append(j.To.ToString("0.##", ci))
          .Append(" moved=").Append(j.Displacement.ToString("0.##", ci))
          .Append(" cause=").Append(ScrollJumpRules.CauseName(j.Cause))
          .Append(" extent=").Append(j.ExtentBefore.ToString("0.##", ci)).Append("->").Append(j.ExtentAfter.ToString("0.##", ci))
          .Append(" frameShift=").Append(j.FrameShiftDelta.ToString("0.##", ci))
          .Append(" anchor=").Append(j.AnchorIndex.ToString(ci))
          .Append(" realized=[").Append(firstRealized.ToString(ci)).Append(',').Append(lastRealized.ToString(ci)).Append(')')
          .Append(" prefix=").Append(prefix.ToString(ci))
          .Append(" atMs=").Append((qpc * 1000.0 / Stopwatch.Frequency).ToString("0.0", ci))
          .Append(" census=").Append(string.IsNullOrEmpty(census) ? "-" : CensusBy(census));
        return sb.ToString();
    }

    /// <summary>The census line's <c>Type×n(... by=…)</c> entries reduced to <c>Type×n:by</c> — which components re-rendered in
    /// the jump's frame and what woke them.</summary>
    private static string CensusBy(string census)
    {
        int top = census.IndexOf(" top=", StringComparison.Ordinal);
        if (top < 0) return census;
        int end = census.IndexOf(" bytes=", top, StringComparison.Ordinal);
        string list = end < 0 ? census[(top + 5)..] : census[(top + 5)..end];
        var sb = new System.Text.StringBuilder(list.Length);
        int pos = 0;
        while (pos < list.Length)
        {
            int open = list.IndexOf('(', pos);
            if (open < 0) break;
            int close = list.IndexOf(')', open);
            if (close < 0) break;
            string type = list[pos..open];
            string inner = list[(open + 1)..close];
            int by = inner.IndexOf("by=", StringComparison.Ordinal);
            if (sb.Length > 0) sb.Append(',');
            sb.Append(type).Append(':').Append(by < 0 ? "?" : inner[(by + 3)..]);
            pos = close + 1;
            if (pos < list.Length && list[pos] == ',') pos++;
        }
        return sb.Length > 0 ? sb.ToString() : census;
    }

    /// <summary>The deferred <c>[scroll.jump]</c> writer: one preallocated work item, at most one line in flight (a newer
    /// jump while one is queued is dropped — <see cref="ScrollProbe.Jumps"/> still counts it).</summary>
    private sealed class JumpLogWork : IThreadPoolWorkItem
    {
        private int _queued;
        private int _vp, _first, _last, _prefix;
        private string? _key, _census;
        private ScrollJump _jump;
        private long _qpc;

        public void Post(int vp, string? key, in ScrollJump jump, int first, int last, int prefix, long qpc, string? census)
        {
            if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
            _vp = vp; _key = key; _jump = jump; _first = first; _last = last; _prefix = prefix; _qpc = qpc; _census = census;
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        public void Execute()
        {
            string line = JumpLine(_vp, _key, in _jump, _first, _last, _prefix, _qpc, _census);
            _key = null; _census = null;
            Volatile.Write(ref _queued, 0);
            Diag.Line(line);
        }
    }
}
