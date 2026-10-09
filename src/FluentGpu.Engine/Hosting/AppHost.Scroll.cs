using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Extent;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Hosting;

/// <summary>
/// The host's scroll wiring (scroll rework §2): one <see cref="PlanSlots"/> per window, one <see cref="ScrollHandle"/>
/// per scroller viewport node (created/destroyed with the node), the UI-side frame step that evaluates every plan at
/// the frame's PRESENT time (virtualization windows, the UI-side content pose for hit-testing, chrome, the motion
/// signals), the coverage table the publisher hands the render thread, and the render-thread <see cref="ScrollPoser"/>
/// that re-poses every covered viewport at the predicted present time of each compositor tick.
/// </summary>
public sealed partial class AppHost
{
    private readonly PlanSlots _planSlots = new();
    private readonly ScrollBarChrome _scrollChrome;
    private readonly ScrollPositionMemory _scrollMemory = new();
    // Viewport node index → its bound handle (+ whether the host minted it, so an authored handle can take over).
    private readonly Dictionary<int, ScrollHandle> _scrollHandles = new();
    private readonly HashSet<int> _internalScrollHandles = new();
    private readonly List<int> _scrollNodes = new();
    // UI-side pose (hit-testing + the published frame) and the coverage the publisher copies for the render poser.
    private readonly ScrollPoser _uiPoser = new();
    private readonly ScrollCoverageTable _uiCoverage = new();
    private readonly UiPoseSink _uiSink;
    private readonly ScrollEffectRow[] _effectScratch = new ScrollEffectRow[ScrollCoverageTable.EffectCapacity];
    // Render-thread poser (render thread only) over the adopted snapshot.
    private readonly ScrollPoser _renderPoser = new(recordsProbePoses: true);
    private readonly SnapshotScrollPoseSink _renderSink = new();
    private bool _scrollPoseChangedThisTick;
    // Motion latches (UI thread): this frame / last frame — the FLIP-suppression decision reads the 2-frame OR.
    private bool _anyUserScrollMovingNow, _anyUserScrollMovingLast;
    private bool _anyScrollMovedThisFrame;
    private int _scrollUnsettledCount;
    private double _lastScrollPresentSec;
    // The latest present time the RENDER poser has posed (render thread writes, UI reads) — the pose floor's source.
    private double _renderPosedPresentSec = double.NegativeInfinity;
    private Func<double>? _scrollShownFloorFn;

    /// <summary>The window's plan table (UI thread writes, render thread reads).</summary>
    public PlanSlots Plans => _planSlots;

    /// <summary>The scrollbar conscious-fade chrome ticker.</summary>
    public ScrollBarChrome ScrollChrome => _scrollChrome;

    /// <summary>True when any viewport was in USER-driven motion this frame or the last (design §7) — the ONLY input
    /// to the FLIP-suppression decision.</summary>
    public bool AnyUserScrollMoving => _anyUserScrollMovingNow || _anyUserScrollMovingLast;

    /// <summary>Viewports whose plan has not settled + live chrome cycles (the wake census).</summary>
    internal int ScrollActiveCensus => _scrollUnsettledCount + _scrollChrome.Count;

    /// <summary>The UI-side poser (headless gates read its feedback).</summary>
    internal ScrollPoser UiScrollPoser => _uiPoser;

    /// <summary>The scroll handle bound to <paramref name="viewport"/> (the authored one when the element supplied it,
    /// else the host-minted one), or null when the node is not a live scroller.</summary>
    public ScrollHandle? TryGetScrollHandle(NodeHandle viewport)
    {
        if (viewport.IsNull || !_scene.IsLive(viewport) || !_scene.HasScroll(viewport)) return null;
        return ResolveScrollHandle((int)viewport.Raw.Index);
    }

    private void InitScrollWiring()
    {
        _scene.PlanSlots = _planSlots;
        _scene.OnScrollNodeAdded = OnScrollNodeAdded;
        _scene.OnScrollNodeRemoved = OnScrollNodeRemoved;
        _scene.CaptureScrollCoverage = table => table.CopyFrom(_uiCoverage);
        _scene.ResolveScrollHandle = TryGetScrollHandle;
        _dispatcher.ScrollHandleFor = TryGetScrollHandle;
        _dispatcher.ScrollNowSec = ScrollNowSec;
        _dispatcher.ScrollQpcToSec = ScrollQpcToSec;
        _dispatcher.ScrollFrameQpcToSec = ScrollFrameQpcToSec;
        _dispatcher.Chrome = _scrollChrome;
        _reconciler.ScrollKeyChanged = OnScrollKeyChanged;
        _reconciler.SaveScrollPosition = SaveScrollPosition;
        // A plan write is urgent: wake the render thread (the next compositor tick poses it) and the UI loop (the
        // virtualizer re-windows on the new plan) without waiting for a frame.
        _planSlots.OnWritten = OnPlanWritten;
        // Wheel notches arrive synchronously from the message pump (never deferred behind a frame).
        _window.SetScrollInputSink(OnUrgentScrollInput);
    }

    private void OnUrgentScrollInput(ScrollInputEvent e)
    {
        Threading.ThreadGuard.AssertUi();
        _dispatcher.DispatchScroll(in e);
        WakeFrame();
    }

    private void OnPlanWritten()
    {
        if (_renderThread is not null && _asyncActive) _renderThread.WakeAsync();
        WakeFrame();
    }

    /// <summary>The plan clock: QPC seconds on a real window; the deterministic frame clock headless.</summary>
    private double ScrollNowSec() => _isHeadless ? _frameClockMs * 1e-3 : Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private double ScrollQpcToSec(long qpc) => _isHeadless || qpc == 0 ? ScrollNowSec() : qpc / (double)Stopwatch.Frequency;

    /// <summary>A frame-clock stamp (a lattice present, <c>ContactStamp.ForFrame</c> — what a composition-timed contact stream carries) on the
    /// plan clock: the frame clock IS the plan clock's timeline in both modes (QPC on a real window; headless, the
    /// deterministic frame clock the headless <c>FrameClock</c> is built from).</summary>
    private static double ScrollFrameQpcToSec(long qpc) => qpc / (double)Stopwatch.Frequency;

    /// <summary>The pose floor (plan clock) every <see cref="ScrollHandle"/> anchors a re-plan at
    /// (<see cref="ScrollHandle.Bind"/>): the latest present time a frame has ALREADY been posed for. With a render thread
    /// that is the render poser's last posed present — it runs ahead of the clock by the present lead, so a device stamp
    /// is routinely older than frames already on their way to the glass. When the render thread is parked (its last pose
    /// is in the past) the plan write wakes it for a turn presenting at <see cref="RenderPresentSec"/>(now): the floor is
    /// one refresh before that, so the first frame shows exactly one refresh of travel. Without a render thread (headless,
    /// single-thread) the UI frame step's own pose is what presents.</summary>
    private double ScrollShownFloorSec()
    {
        if (_renderThread is null) return _lastScrollPresentSec;
        double posed = Volatile.Read(ref _renderPosedPresentSec);
        double now = ScrollNowSec();
        if (posed > now) return posed;
        return RenderPresentSec(0) - RenderPeriodTicks() / (double)Stopwatch.Frequency;
    }

    private void OnScrollNodeAdded(int idx)
    {
        if (!_scrollNodes.Contains(idx)) _scrollNodes.Add(idx);
    }

    private void OnScrollNodeRemoved(int idx)
    {
        _scrollNodes.Remove(idx);
        if (_scrollHandles.Remove(idx, out var handle))
        {
            handle.Unbind();
            _internalScrollHandles.Remove(idx);
        }
    }

    /// <summary>Binds (or re-binds to the authored instance) the handle of scroll node <paramref name="idx"/>.</summary>
    private ScrollHandle ResolveScrollHandle(int idx)
    {
        var node = _scene.HandleAt(idx);
        // Read-only here, and resolved for every viewport several times a frame: an existing row is read without the
        // write-intent ledger mark (which made every scroller look changed on every frame); a missing one is created as before.
        ref readonly ScrollState sc = ref _scene.HasScroll(node) ? ref _scene.ScrollRow(node) : ref _scene.ScrollRef(node);
        bool horizontal = sc.Orientation == 1;
        var vp = new ScrollViewportId(idx, node.Raw.Gen);
        _scene.TryGetAuthoredScrollHandle(idx, out var authored);
        if (_scrollHandles.TryGetValue(idx, out var bound))
        {
            if (authored is not null && !ReferenceEquals(bound, authored))
            {
                bound.Unbind();
                _internalScrollHandles.Remove(idx);
                bound = authored;
                _scrollHandles[idx] = bound;
            }
            if (!bound.IsBound || bound.Vp != vp || bound.Horizontal != horizontal) bound.Bind(_planSlots, vp, ScrollNowSec, horizontal, _scrollShownFloorFn ??= ScrollShownFloorSec);
            return bound;
        }
        var handle = authored ?? new ScrollHandle();
        if (authored is null) _internalScrollHandles.Add(idx);
        handle.Bind(_planSlots, vp, ScrollNowSec, horizontal, _scrollShownFloorFn ??= ScrollShownFloorSec);
        handle.SetExtent(sc.ContentMain * (sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f), sc.ViewportMain);
        _scrollHandles[idx] = handle;
        return handle;
    }

    // ── ScrollKey restore (design §9 — replaces the reconciler's ScrollMemory) ─────────────────────────────────

    private void OnScrollKeyChanged(NodeHandle node, string? oldKey, string? newKey)
    {
        var handle = TryGetScrollHandle(node);
        if (handle is null) return;
        if (oldKey is not null) _scrollMemory.Save(oldKey, handle.LastShown);
        if (newKey is not null && _scrollMemory.TryGet(newKey, out double saved)) handle.Restore(saved);
        else if (oldKey is not null) handle.ScrollTo(0.0, ScrollMove.Immediate);   // fresh content → top
    }

    private void SaveScrollPosition(NodeHandle node)
    {
        if (!_scene.TryGetScroll(node, out var sc) || sc.ScrollKey is null) return;
        var handle = TryGetScrollHandle(node);
        if (handle is not null) _scrollMemory.Save(sc.ScrollKey, handle.LastShown);
    }

    // ── the UI frame step (phase 2.5) ───────────────────────────────────────────────────────────────────────────

    /// <summary>Evaluates every viewport's plan at <paramref name="presentSec"/> (the frame's predicted present):
    /// publishes the frame's Offset/Velocity/Motion to the scene, flags viewports whose realized window no longer
    /// covers the present-time window, feeds the handles' signals, arms the chrome, and computes the motion latches.</summary>
    private void RunScrollFrame(double presentSec)
    {
        _lastScrollPresentSec = presentSec;
        _anyUserScrollMovingLast = _anyUserScrollMovingNow;
        _anyUserScrollMovingNow = false;
        _anyScrollMovedThisFrame = false;
        _scrollUnsettledCount = 0;
        _scrollChrome.FrameIndex++;
        var feel = ScrollTunables.Current;
        for (int i = 0; i < _scrollNodes.Count; i++)
        {
            int idx = _scrollNodes[i];
            var node = _scene.HandleAt(idx);
            if (node.IsNull || !_scene.IsLive(node) || !_scene.HasScroll(node)) continue;
            // A parked viewport (a KeepAlive page not showing) is off screen: exactly as FillScrollCoverage leaves it out
            // of what the render poser sees, the frame step does not evaluate it — no plan evaluation, no virtualization
            // window, and an unsettled plan on a page nobody can see never holds the loop awake as live scroll motion.
            // Plans are functions of absolute time, so the unpark frame simply evaluates wherever the plan is by then.
            if ((_scene.Flags(node) & NodeFlags.Parked) != 0) continue;
            var handle = ResolveScrollHandle(idx);
            // This step runs for every viewport on every frame and usually arrives at the values already there. ScrollRef's
            // unconditional write-intent mark made every page with a scroller look changed on every frame, which published a
            // scene per wake (the no-op publication skip could never hold). So the step works on a COPY and takes ScrollRef
            // (the ledgered write) only when one of the captured fields it writes (Offset, Velocity, Motion, AnchorIndex) moved.
            ScrollState sc = _scene.ScrollRow(node);
            double velocity0 = sc.Velocity;
            var motion0 = sc.Motion;
            int anchor0 = sc.AnchorIndex;
            float zoom = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
            handle.SetExtent(sc.ContentMain * zoom, sc.ViewportMain);
            handle.SetSnap(ScrollContentPose.SnapGridOf(in sc));
            handle.SettleIfDue(presentSec);
            double p = handle.EvalAt(presentSec, out double v, out bool settled);
            var plan = handle.Plan;
            // A rubber-banded plan (a live drag, the fling's edge spring) SHOWS its overpan — empty space beyond the
            // content, never an un-realized row; every other plan is hard-clamped to the content.
            double shown = plan.Overpan == OverpanPolicy.RubberBand ? p : Math.Clamp(p, 0.0, handle.MaxOffset);
            bool moved = shown != sc.Offset;
            sc.Offset = shown;
            sc.Velocity = settled ? 0.0 : v;
            // Motion: the render poser's feedback when it posed this viewport (its answer is what the user saw), else
            // the UI-side evaluation of the same plan.
            if (_renderThread is not null && _renderPoser.TryGetFeedback(handle.Vp, out var fb)) handle.ApplyFeedback(in fb);
            else handle.ApplyShown(shown, v, plan.Kind, settled);
            sc.Motion = handle.Motion.Peek();
            if (!settled) _scrollUnsettledCount++;
            if (sc.Motion.UserDriven) _anyUserScrollMovingNow = true;
            if (moved)
            {
                _anyScrollMovedThisFrame = true;
                _scrollChrome.NotifyMoved(idx, plan.Kind);
            }
            // Virtualization: does the realized window cover the present-time window (velocity-sized overscan)?
            bool needsRealize = false;
            if (sc.ItemCount > 0 && sc.Extent is { } ext)
            {
                var rw = Virtualizer.Plan(ext, shown, v, sc.ViewportMain, in feel, sc.AnchorIndex);
                if (!rw.IsEmpty) sc.AnchorIndex = rw.AnchorIndex;
                needsRealize = ScrollContentPose.NeedsRealize(in sc, in rw);
            }
            if (moved || !sc.Velocity.Equals(velocity0) || sc.Motion != motion0 || sc.AnchorIndex != anchor0)
            {
                ref ScrollState row = ref _scene.ScrollRef(node);   // the ledgered write: only the fields this step owns
                row.Offset = sc.Offset;
                row.Velocity = sc.Velocity;
                row.Motion = sc.Motion;
                row.AnchorIndex = sc.AnchorIndex;
            }
            if (needsRealize) _scene.Mark(node, NodeFlags.VirtualRangeDirty);
        }
        Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.Scroll, AnyUserScrollMoving);
    }

    /// <summary>Re-publishes every viewport's current geometry to its handle, re-evaluates its plan at this frame's
    /// present time and publishes any offset that moved since the frame step — a plan authored MID-frame (a ScrollKey
    /// change's ScrollTo(0) in the reconcile, a layout effect's BringIntoView, a controller post) shows and windows in
    /// THIS frame. Returns true when any offset moved.</summary>
    private bool SyncScrollPlansMidFrame()
    {
        bool any = false;
        var feel = ScrollTunables.Current;
        for (int i = 0; i < _scrollNodes.Count; i++)
        {
            int idx = _scrollNodes[i];
            var node = _scene.HandleAt(idx);
            if (node.IsNull || !_scene.IsLive(node) || !_scene.HasScroll(node)) continue;
            if ((_scene.Flags(node) & NodeFlags.Parked) != 0) continue;   // off screen — see RunScrollFrame
            var handle = ResolveScrollHandle(idx);
            if (!_scene.TryGetScroll(node, out var peek)) continue;
            // This frame's laid-out geometry first: a move authored against a viewport that only now has an extent (a
            // freshly mounted list's BringIntoView from its layout effect) resolves its latched target HERE, so the first
            // presented frame already shows it — never a frame at 0 followed by the jump.
            handle.SetExtent(peek.ContentMain * (peek.ZoomFactor > 0f ? peek.ZoomFactor : 1f), peek.ViewportMain);
            double p = handle.EvalAt(_lastScrollPresentSec, out double v, out bool settled);
            var plan = handle.Plan;
            double shown = plan.Overpan == OverpanPolicy.RubberBand ? p : Math.Clamp(p, 0.0, handle.MaxOffset);
            if (shown == peek.Offset) continue;
            ref ScrollState sc = ref _scene.ScrollRef(node);
            sc.Offset = shown;
            sc.Velocity = settled ? 0.0 : v;
            handle.ApplyShown(shown, v, plan.Kind, settled);
            sc.Motion = handle.Motion.Peek();
            if (!settled) _scrollUnsettledCount++;
            if (sc.Motion.UserDriven) _anyUserScrollMovingNow = true;
            _anyScrollMovedThisFrame = true;
            _scrollChrome.NotifyMoved(idx, plan.Kind);
            _scene.NoteCaptureChanged(idx);
            if (sc.ItemCount > 0 && sc.Extent is { } ext)
            {
                var rw = Virtualizer.Plan(ext, shown, v, sc.ViewportMain, in feel, sc.AnchorIndex);
                if (!rw.IsEmpty) sc.AnchorIndex = rw.AnchorIndex;
                if (ScrollContentPose.NeedsRealize(in sc, in rw)) _scene.Mark(node, NodeFlags.VirtualRangeDirty);
            }
            any = true;
        }
        return any;
    }

    // ── the UI-side pose (after layout, before record/publish) ──────────────────────────────────────────────────

    /// <summary>Publishes every viewport's laid-out extent/viewport to its handle right after layout, so a plan authored
    /// between frames (a ScrollTo after mount, a wheel notch on an idle frame) is clamped against THIS frame's geometry,
    /// not the previous frame step's. (The 2.5 frame step re-publishes too; idle frames never reach it.)</summary>
    private void RefreshScrollExtents()
    {
        for (int i = 0; i < _scrollNodes.Count; i++)
        {
            int idx = _scrollNodes[i];
            var node = _scene.HandleAt(idx);
            if (node.IsNull || !_scene.IsLive(node) || !_scene.TryGetScroll(node, out var sc)) continue;
            float zoom = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
            ResolveScrollHandle(idx).SetExtent(sc.ContentMain * zoom, sc.ViewportMain);
        }
    }

    /// <summary>Fills the coverage table from the laid-out scene and poses every viewport's content + effects on the
    /// UI thread at the frame's present time (the SAME arithmetic the render poser runs): hit-testing, the headless
    /// recorder and the published frame all see the posed content; the render thread re-poses at each later tick.</summary>
    private void PoseScrollUi()
    {
        RefreshScrollExtents();
        FillScrollCoverage(_uiCoverage);
        _uiPoser.Adopt(_uiCoverage);
        _uiPoser.Tick(_planSlots, _lastScrollPresentSec, _scene.DeviceScale, _uiSink);
        WatchScrollJumps();   // always-on unrequested-jump detector (AppHost.ScrollJump.cs) — evidence only
    }

    private void FillScrollCoverage(ScrollCoverageTable table)
    {
        table.Clear();
        var effects = _scene.ScrollEffects;
        for (int i = 0; i < _scrollNodes.Count; i++)
        {
            int idx = _scrollNodes[i];
            var node = _scene.HandleAt(idx);
            if (node.IsNull || !_scene.IsLive(node) || !_scene.HasScroll(node)) continue;
            if ((_scene.Flags(node) & NodeFlags.Parked) != 0) continue;
            ref readonly ScrollState sc = ref _scene.ScrollRow(node);   // read-only: no write-intent ledger mark
            var content = sc.ContentNode;
            if (content.IsNull || !_scene.IsLive(content)) continue;
            bool horizontal = sc.Orientation == 1;
            double extentTotal = sc.Extent is { } ext ? ext.Total : sc.ContentMain;
            double coverEnd = sc.ItemCount > 0 ? sc.CoverEnd : extentTotal;
            var row = new ScrollCoverageRow(idx, node.Raw.Gen, (int)content.Raw.Index, sc.WindowOrigin, sc.CoverStart, coverEnd,
                sc.ViewportMain, extentTotal, horizontal, 0, 0, _planSlots.FrameShiftOf(new ScrollViewportId(idx, node.Raw.Gen)));

            int n = 0;
            if (effects.Count > 0)
            {
                foreach (var kv in effects)
                {
                    var target = _scene.HandleAt(kv.Key);
                    if (target.IsNull || !_scene.IsLive(target)) continue;
                    var rows = kv.Value;
                    for (int e = 0; e < rows.Length && n < _effectScratch.Length; e++)
                    {
                        ScrollEffect fx = rows[e];
                        if (fx.Kind == EffectKind.Thumb)
                        {
                            if (fx.ScopeNode != idx) continue;
                            var track = _scene.Parent(target);
                            float trackLen = track.IsNull ? 0f : (horizontal ? _scene.Bounds(track).W : _scene.Bounds(track).H);
                            float thumbLen = horizontal ? _scene.Bounds(target).W : _scene.Bounds(target).H;
                            _effectScratch[n++] = new ScrollEffectRow(kv.Key, fx,
                                new EffectGeometry(0, 0, extentTotal, extentTotal, sc.ViewportMain, trackLen, thumbLen));
                            continue;
                        }
                        if (!NodeInScroller(target, node, content, horizontal, out double nodeY, out double nodeH)) continue;
                        // The sticky scope (CSS containing block): a named scope, else the node's parent — a header pins
                        // only while its card is on screen; a direct child of the content pins for the whole extent.
                        double scopeEnd = extentTotal;
                        var scope = fx.ScopeNode != 0 ? _scene.HandleAt(fx.ScopeNode) : _scene.Parent(target);
                        if (!scope.IsNull && scope != content && _scene.IsLive(scope)
                            && NodeInScroller(scope, node, content, horizontal, out double sy, out double sh))
                            scopeEnd = sy + sh;
                        var geometry = new EffectGeometry(nodeY, nodeH, scopeEnd, extentTotal, sc.ViewportMain, 0f, 0f);
                        _effectScratch[n++] = new ScrollEffectRow(kv.Key, fx, geometry);
                        if (fx.Kind is EffectKind.Sticky or EffectKind.StickyClip)
                        {
                            bool engaged = ScrollEffectEval.StickyEngaged(in fx, sc.Offset, in geometry);
                            // Only a Sticky pin re-orders the parent's paint (a pinned header paints after the siblings
                            // that scroll under it): record content, not a pose — the sticky's translation itself is a
                            // composite parameter. A StickyClip guillotines its node IN PLACE; it never changes paint order.
                            if (ScrollEffectEval.PinsAboveSiblings(fx.Kind))
                            {
                                bool was = (_scene.Flags(target) & NodeFlags.StickyPinned) != 0;
                                if (engaged && !was) _scene.Mark(target, NodeFlags.StickyPinned | NodeFlags.PaintDirty);
                                else if (!engaged && was) { _scene.Unmark(target, NodeFlags.StickyPinned); _scene.Mark(target, NodeFlags.PaintDirty); }
                            }
                            // The authored engaged edge (CSS :stuck): UI thread, before this frame publishes, written only
                            // when it flips — compared against the signal itself, so a re-bake or a remount never replays
                            // an edge and nothing is written on a steady frame (allocation-free).
                            if (_scene.TryGetScrollEffectEngaged(kv.Key, out var edges) && (uint)e < (uint)edges.Length
                                && edges[e] is { } edge && edge.Peek() != engaged)
                            {
                                edge.Value = engaged;
                                NoteEngagedFlip(target, engaged, sc.Offset);   // edge-gated evidence line (AppHost.Engaged.cs)
                            }
                        }
                    }
                }
            }
            table.AddRow(row, _effectScratch.AsSpan(0, n));
            if (ScrollProbe.Level == ProbeLevel.Trace) ProbeCoverageIfChanged(in row, in sc);
        }
    }

    // The last coverage the probe recorded per viewport (Trace only): a Coverage row is written when a viewport's published
    // coverage CHANGES, not every frame — the ring holds history, not a per-frame repeat. Fixed arrays, zero allocation.
    private readonly int[] _probeCovVp = new int[ScrollCoverageTable.RowCapacity];
    private readonly (double Start, double End, double Origin, int First, int Last)[] _probeCovLast =
        new (double, double, double, int, int)[ScrollCoverageTable.RowCapacity];
    private int _probeCovCount;

    private void ProbeCoverageIfChanged(in ScrollCoverageRow row, in ScrollState sc)
    {
        var now = (row.Start, row.End, row.WindowOrigin, sc.FirstRealized, sc.LastRealized);
        int slot = -1;
        for (int i = 0; i < _probeCovCount; i++) if (_probeCovVp[i] == row.Vp) { slot = i; break; }
        if (slot >= 0 && _probeCovLast[slot] == now) return;
        if (slot < 0)
        {
            if (_probeCovCount < _probeCovVp.Length) slot = _probeCovCount++;
            else slot = row.Vp % _probeCovVp.Length;   // table full: reuse a slot (at worst one extra row later)
            _probeCovVp[slot] = row.Vp;
        }
        _probeCovLast[slot] = now;
        ScrollProbe.Coverage(row.Vp, 0, row.Start, row.End, row.WindowOrigin, sc.FirstRealized, sc.LastRealized);
    }

    /// <summary>Content-space position of <paramref name="node"/> along the scroller's axis (relative to its content
    /// start), false when the node is not inside <paramref name="content"/> or belongs to a nested viewport inside it.</summary>
    private bool NodeInScroller(NodeHandle node, NodeHandle scroller, NodeHandle content, bool horizontal, out double pos, out double extent)
    {
        double acc = 0.0;
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if (n == content) break;
            if (n == scroller) { pos = 0; extent = 0; return false; }
            // A nested viewport between the node and this content owns it: an effect binds to its NEAREST scroller (CSS
            // position:sticky / scroll()), so an outer scroller must not pose it too, or the two rows overwrite each other
            // and a sticky's engaged edge flips twice a frame. The node itself may be a viewport (pinned in this one).
            if (n != node && _scene.HasScroll(n)) { pos = 0; extent = 0; return false; }
            ref readonly RectF b = ref _scene.Bounds(n);
            acc += horizontal ? b.X : b.Y;
            if (_scene.Parent(n).IsNull) { pos = 0; extent = 0; return false; }
        }
        // Read-only: no write-intent ledger mark (a scroller always has its row; a missing one reads as origin 0, as before).
        double windowOrigin = _scene.HasScroll(scroller) ? _scene.ScrollRow(scroller).WindowOrigin : 0.0;
        ref readonly RectF nb = ref _scene.Bounds(node);
        pos = windowOrigin + acc;
        extent = horizontal ? nb.W : nb.H;
        return true;
    }

    /// <summary>The UI-thread pose sink: writes the live scene's paint (hit-testing / the headless recorder read it).
    /// Transform-class poses and the sticky clip (<see cref="EffectChannel.ClipTop"/>) are COMPOSITE parameters of the
    /// retained-tile slices (<c>SliceRecorder</c>): they re-record nothing, so they are ledgered for the publication (the
    /// capture ledger) but never marked record-dirty — a sticky clip the recorder had to bake inline is its own watched
    /// pose (<c>SliceRecorder.BakeClip</c>). The remaining non-transform channels change recorded bytes and mark the node.</summary>
    private sealed class UiPoseSink : IScrollPoseSink
    {
        private readonly SceneStore _scene;
        public UiPoseSink(SceneStore scene) => _scene = scene;

        public void PoseViewport(int vpNode, double shown) { }

        public void PoseContent(int node, bool horizontal, float trans, bool changed)
        {
            var h = _scene.HandleAt(node);
            if (h.IsNull || !_scene.IsLive(h)) return;
            ref NodePaint cp = ref _scene.Paint(h);
            var parent = _scene.Parent(h);
            float zoom = 1f;
            if (!parent.IsNull && _scene.TryGetScroll(parent, out var sc)) zoom = sc.ZoomFactor;
            Affine2D before = cp.LocalTransform;
            ScrollContentPose.WriteContentTransform(ref cp, in _scene.Bounds(h), horizontal, trans, zoom);
            if (before != cp.LocalTransform) _scene.NoteCaptureChanged((int)h.Raw.Index);
        }

        public void PoseEffect(int node, EffectChannel channel, float value, bool changed)
        {
            var h = _scene.HandleAt(node);
            if (h.IsNull || !_scene.IsLive(h)) return;
            ref NodePaint p = ref _scene.Paint(h);
            if (!ApplyEffectChannel(ref p, channel, value)) return;
            if (IsCompositeEffectChannel(channel)) _scene.NoteCaptureChanged((int)h.Raw.Index);
            else _scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }

        public void PoseTransform(int node, in EffectTransform transform, bool changed)
        {
            var h = _scene.HandleAt(node);
            if (h.IsNull || !_scene.IsLive(h)) return;
            ref NodePaint p = ref _scene.Paint(h);
            if (ApplyEffectTransform(ref p, in _scene.Bounds(h), in transform)) _scene.NoteCaptureChanged((int)h.Raw.Index);
        }
    }

    /// <summary>Writes a node's folded transform-class effects (<see cref="EffectTransform"/>) as its
    /// <see cref="NodePaint.LocalTransform"/> — the recorder and hit-testing conjugate it about the node's authored
    /// transform origin, so a plain scale pivots there while a stretch keeps its top-centre pivot
    /// (<see cref="EffectTransform.ToLocal"/>). Returns true when the paint changed.</summary>
    internal static bool ApplyEffectTransform(ref NodePaint p, in RectF bounds, in EffectTransform t)
    {
        var next = t.ToLocal(bounds.W, bounds.H, bounds.W * p.OriginX, bounds.H * p.OriginY);
        if (p.LocalTransform == next) return false;
        p.LocalTransform = next;
        return true;
    }

    /// <summary>A non-transform effect channel that is nevertheless a COMPOSITE parameter: the sticky clip
    /// (<see cref="EffectChannel.ClipTop"/>), which the slice recorder applies on the node's slice marker at placement
    /// (<c>CompositeSliceFlags.StickyClip</c>, gpu-renderer.md §13.1e) — posing it re-records nothing.</summary>
    internal static bool IsCompositeEffectChannel(EffectChannel channel) => channel == EffectChannel.ClipTop;

    /// <summary>Writes one NON-transform effect channel into a paint row (transform-class channels are folded per node and
    /// written by <see cref="ApplyEffectTransform"/>). Returns true when the paint changed.</summary>
    internal static bool ApplyEffectChannel(ref NodePaint p, EffectChannel channel, float value)
    {
        switch (channel)
        {
            case EffectChannel.Opacity:
                if (p.Opacity == value) return false;
                p.Opacity = value;
                return true;
            case EffectChannel.ClipTop:
            {
                RectF next = value > 0f
                    ? RectF.FromLTRB(-NodePaint.StickyClipSpan, value, NodePaint.StickyClipSpan, NodePaint.StickyClipSpan)
                    : RectF.Infinite;
                if (p.ClipRect == next) return false;
                p.ClipRect = next;
                return true;
            }
            case EffectChannel.PresentedH:
                if (p.PresentedH == value) return false;
                p.PresentedH = value;
                return true;
            case EffectChannel.ChildShiftY:
                if (p.ChildShiftY == value) return false;
                p.ChildShiftY = value;
                return true;
            case EffectChannel.ClipBottom:
            {
                // The leading collapse's cut (NodePaint.CollapseCut): open above, the recorder / hit-test cut at the
                // presented edge its sibling PresentedH row writes — so this row changes recorded bytes like that one.
                RectF next = NodePaint.CollapseCut(value);
                if (p.ClipRect == next) return false;
                p.ClipRect = next;
                return true;
            }
            default:
                return false;
        }
    }

    // ── render thread ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The predicted present time of the render turn woken by compositor tick <paramref name="tickQpc"/>
    /// (0 = no display clock ⇒ now): <c>tick + (1 + maxFrameLatency)·refresh</c> (design §0).</summary>
    private double RenderPresentSec(long tickQpc)
    {
        long refresh = RenderPeriodTicks();
        long baseQpc = tickQpc != 0 ? tickQpc : Stopwatch.GetTimestamp();
        return (baseQpc + (1 + Volatile.Read(ref _maxFrameLatency)) * refresh) / (double)Stopwatch.Frequency;
    }

    /// <summary>Render thread: adopt the publication's coverage on a fresh publish, then pose every covered viewport
    /// at <paramref name="presentSec"/>. Returns true when any posed value changed (a present is due).</summary>
    private bool TickRenderScroll(Threading.SceneRenderFrame sceneFrame, bool fresh, double presentSec)
    {
        Threading.ThreadGuard.AssertRender();
        if (fresh) _renderPoser.Adopt(sceneFrame.Scene.ScrollCoverage);
        _renderSink.Bind(sceneFrame.Scene);
        _scrollPoseChangedThisTick = _renderPoser.Tick(_planSlots, presentSec, sceneFrame.Scene.DeviceScale, _renderSink);
        Volatile.Write(ref _renderPosedPresentSec, presentSec);   // the pose floor (ScrollShownFloorSec)
        return _scrollPoseChangedThisTick;
    }
}
