using System.Diagnostics;
using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;

namespace FluentGpu.Hosting;

// ── engaged-edge evidence (always-on, edge-gated; F(ii) of the 2026-09-25 artist-page RCA) ──────────────────────────
//
// A sticky / sticky-clip row's `engaged:` signal is written on the UI thread (FillScrollCoverage) when the UI frame's
// offset crosses the threshold; anything an app switches off that signal (the artist page's magazine feather) arrives by
// a RE-RENDER, i.e. in a later publication — while the render thread poses the clip itself at composite time on the
// tick the pose crosses. Two lines measure that lag with no inference:
//
//   [scroll.engaged] node=<idx:gen> engaged=0|1 flipSeq=<last publish before the flip> uiOffset= flipMs=
//                    renderCrossTick= renderCrossPresentMs= renderCrossOffset= renderCrossAtMs= crossToFlipMs=
//                    (renderCross=none when the render poser recorded no matching crossing)
//   [scroll.engaged.present] node=<idx:gen> engaged= seq=<first publication after the flip whose flush ran the
//                    re-render> presentMs= tick= ticksAfterCross= msAfterCross=   (or `result=timeout`)
//
// Strings only on these edges (a handful per scroll); the per-frame poll is one count compare when nothing waits.

public sealed partial class AppHost
{
    private struct EngagedAwait
    {
        public int Node;
        public uint Gen;
        public bool Engaged;
        public ulong AwaitSeq;       // 0 until the first publication after the flip whose flush consumed the re-render
        public bool HasCross;
        public long CrossTick, CrossQpc;
        public long FlipQpc;
    }

    private const int EngagedAwaitCap = 8;
    private const double EngagedAwaitTimeoutSec = 2.0;
    private readonly EngagedAwait[] _engagedAwaits = new EngagedAwait[EngagedAwaitCap];
    private int _engagedAwaitCount;

    /// <summary>UI THREAD (FillScrollCoverage, right after the authored engaged signal flipped): log the flip against the
    /// render poser's crossing, and wait for the publication that carries the re-render it drives.</summary>
    private void NoteEngagedFlip(NodeHandle target, bool engaged, double uiOffset)
    {
        if (_isDetachedChild) return;
        long now = Stopwatch.GetTimestamp();
        double toMs = 1000.0 / Stopwatch.Frequency;
        int idx = (int)target.Raw.Index;
        uint gen = target.Raw.Gen;
        bool has = EngagedCrossings.TryFindLatest(idx, engaged, out EngagedCrossing c);
        string cross = has
            ? string.Create(CultureInfo.InvariantCulture,
                $"renderCrossTick={c.TickSeq} renderCrossPresentMs={c.PresentSec * 1000.0:F1} renderCrossOffset={c.Offset:F2} renderCrossAtMs={c.RecordedQpc * toMs:F1} crossToFlipMs={(now - c.RecordedQpc) * toMs:F1}")
            : "renderCross=none";
        Diag.Line(string.Create(CultureInfo.InvariantCulture,
            $"[scroll.engaged] node={idx}:{gen} engaged={(engaged ? 1 : 0)} flipSeq={PublishSequence} uiOffset={uiOffset:F2} flipMs={now * toMs:F1} {cross}"));
        if (_engagedAwaitCount == EngagedAwaitCap) return;   // a pathological flip storm: the flip lines above still say it
        _engagedAwaits[_engagedAwaitCount++] = new EngagedAwait
        {
            Node = idx, Gen = gen, Engaged = engaged, HasCross = has, CrossTick = c.TickSeq, CrossQpc = c.RecordedQpc,
            FlipQpc = now,
        };
    }

    /// <summary>Paint end: a pending flip whose re-render this Paint's flush has consumed (nothing left pending) waits for
    /// THIS frame's publication. Two field reads when nothing waits.</summary>
    private void ResolveEngagedAwaits()
    {
        if (_engagedAwaitCount == 0 || _runtime.HasPending || _framePublishSeq == 0) return;
        for (int i = 0; i < _engagedAwaitCount; i++)
            if (_engagedAwaits[i].AwaitSeq == 0) _engagedAwaits[i].AwaitSeq = _framePublishSeq;
    }

    /// <summary>Paint start: every awaited publication the render thread has presented gets its present line.</summary>
    private void PollEngagedPresents()
    {
        if (_engagedAwaitCount == 0) return;
        ulong ack = RenderPresentSeq;
        long now = Stopwatch.GetTimestamp();
        double toMs = 1000.0 / Stopwatch.Frequency;
        for (int i = _engagedAwaitCount - 1; i >= 0; i--)
        {
            ref EngagedAwait a = ref _engagedAwaits[i];
            bool timedOut = (now - a.FlipQpc) > (long)(EngagedAwaitTimeoutSec * Stopwatch.Frequency);
            string? line = null;
            if (a.AwaitSeq != 0 && ack >= a.AwaitSeq && PresentLedger.TryFindFirstAtOrAfter(a.AwaitSeq, out PresentRecord r))
            {
                string after = a.HasCross
                    ? string.Create(CultureInfo.InvariantCulture, $"ticksAfterCross={r.TickSeq - a.CrossTick} msAfterCross={(r.DoneQpc - a.CrossQpc) * toMs:F1}")
                    : "ticksAfterCross=? msAfterCross=?";
                line = string.Create(CultureInfo.InvariantCulture,
                    $"[scroll.engaged.present] node={a.Node}:{a.Gen} engaged={(a.Engaged ? 1 : 0)} seq={r.PublishSeq} awaited={a.AwaitSeq} presentMs={r.DoneQpc * toMs:F1} tick={r.TickSeq} {after}");
            }
            else if (timedOut)
                line = string.Create(CultureInfo.InvariantCulture,
                    $"[scroll.engaged.present] node={a.Node}:{a.Gen} engaged={(a.Engaged ? 1 : 0)} awaited={a.AwaitSeq} ack={ack} result=timeout");
            if (line is null) continue;
            Diag.Line(line);
            _engagedAwaits[i] = _engagedAwaits[--_engagedAwaitCount];
        }
    }
}
