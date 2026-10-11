using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Tiers;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E22 / technical-plan §3.3-§3.4: a tier crossing never jumps. A resized frame lays out the OLD tier at the new size
/// (snapped, capture skipped); the crossing is committed by <c>AppHost.CommitPendingTiers</c> on the first frame that is NOT
/// resized, right before FLIP capture, so every keyed node whose rect changed gets a track, the nodes the new tier adds
/// ENTER, and the tracks settle to rest. Driven on <see cref="HeadlessWindow"/> (settable <c>ClientSizePx</c>, <c>Scale</c>,
/// <c>SetZoom</c>, <c>InModalLoop</c>, <c>SizedInModalLoop</c>, <c>Composited</c>) through the public <c>host.Paint(0, keepAlive: true)</c>
/// exactly as the VerticalSlice RZ-DEFER gate does (rig precedent: <c>ModalTickPolicyTests</c>). The freeze case belongs to EP10.
/// Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class TierCrossingTests
{
    // ── the probes ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A two-tier shell: a rail (56 / 220 wide, FLIPs its bounds), a Grow card (FLIPs its position), and an extra
    /// panel only the wide tier has (it ENTERs). The tier is the subject of each concrete subclass.</summary>
    private abstract class ShellBase : Component
    {
        public NodeHandle Rail, Card, Extra;
        public readonly List<int> Seen = new();

        protected Element BuildShell(bool wide, Action<RectF>? onBounds = null) => new BoxEl
        {
            Direction = 0, Grow = 1f,
            OnBoundsChanged = onBounds,
            Children = wide
                ? new Element[] { RailEl(220f), CardEl(), ExtraEl() }
                : new Element[] { RailEl(56f), CardEl() },
        };

        private BoxEl RailEl(float w) => new()
        {
            Key = "rail", Width = w, Shrink = 0f, Fill = ColorF.FromRgba(27, 32, 40),
            Animate = LayoutTransition.BoundsT(SizeMode.Reveal), OnRealized = h => Rail = h,
        };

        private BoxEl CardEl() => new()
        {
            Key = "card", Grow = 1f, MinWidth = 0f, Fill = ColorF.FromRgba(57, 61, 69),
            Animate = LayoutTransition.Slide, OnRealized = h => Card = h,
        };

        private BoxEl ExtraEl() => new()
        {
            Key = "extra", Width = 80f, Shrink = 0f, Fill = ColorF.FromRgba(80, 90, 100),
            Enter = new EnterExit(Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal, OnRealized = h => Extra = h,
        };
    }

    /// <summary>The subject: <c>UseTier</c> on the viewport width, edge at 600 (Narrow below, Wide from 600).</summary>
    private sealed class TierShell : ShellBase
    {
        public static readonly TierLadder Ladder = TierLadder.Of(new TierEdge(600f));

        public override Element Render()
        {
            int tier = UseTier(Ladder, TierSource.ViewportWidth).Value;
            Seen.Add(tier);
            return BuildShell(tier == 1);
        }
    }

    /// <summary>Negative control 1 (§3.3 rule 3, catch 1): the crossing is deferred with <c>UseTimeout(0)</c>. Timer drains run
    /// AFTER FLIP capture, so the reconcile it causes has no "before" rects and snaps.</summary>
    private sealed class TimeoutShell : ShellBase
    {
        public override Element Render()
        {
            var vp = UseContext(Viewport.Size);                       // same-frame, re-renders on every resized frame
            int want = vp.Width >= 600f ? 1 : 0;
            var tier = UseSignal(want);
            UseTimeout(() => tier.Value = want, 0f, want);
            int t = tier.Value;
            Seen.Add(t);
            return BuildShell(t == 1);
        }
    }

    /// <summary>Negative control 2 (catch 2): the tier is a plain signal written in the layout phase (a bounds callback).
    /// Written on a resized frame it is reconciled by the next frame's flush; when that frame is resized too (Win+Arrow from
    /// maximized, a DPI or zoom step) capture is skipped and the reconcile snaps.</summary>
    private sealed class LayoutWriteShell : ShellBase
    {
        private readonly Signal<int> _tier = new(0);

        public override Element Render()
        {
            int t = _tier.Value;
            Seen.Add(t);
            // A column wrapper so the observed row is an ordinary child (the scene root's own bounds callback is not the subject).
            return new BoxEl { Direction = 1, Grow = 1f, Children = [ BuildShell(t == 1, r => _tier.Value = r.W >= 600f ? 1 : 0) ] };
        }
    }

    private sealed class ModalFactProbe : Component
    {
        public readonly List<bool> Seen = new();

        public override Element Render()
        {
            Seen.Add(UseContext(Viewport.InModalLoop));
            return new BoxEl { Width = 10f, Height = 10f };
        }
    }

    // ── the rig ────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessWindow Window { get; }
        public AppHost Host { get; }

        public Rig(Component root, float widthPx, bool composited = true)
        {
            var strings = new StringTable();
            Window = new HeadlessWindow(new WindowDesc("tier-crossing", new Size2(widthPx, 320), 1f, Composited: composited));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
            Host.RunFrame();
            Settle();
        }

        public AnimEngine Anim => Host.Animation;
        public RectF Rect(NodeHandle n) => Host.Scene.AbsoluteRect(n);
        public bool HasTracks(NodeHandle n) => !n.IsNull && Host.Scene.IsLive(n) && Anim.HasTracks(n);
        public float TranslateX(NodeHandle n) => Anim.TryGetTrackValue(n, AnimChannel.TranslateX, out float v) ? v : float.NaN;
        public void Resize(float widthPx) => Window.ClientSizePx = new Size2(widthPx, 320);
        public void Frames(int n) { for (int i = 0; i < n; i++) Host.RunFrame(); }
        public void KeepAlive() => Host.Paint(0, keepAlive: true);
        public TierRegistry Tiers => Host.TiersForTest;

        /// <summary>Run frames until no animation track is left (bounded), pumping a paint when the loop would idle.</summary>
        public void Settle()
        {
            for (int i = 0; i < 400 && Anim.TrackCount > 0; i++)
            {
                double before = Host.FrameClockMsForTest;
                Host.RunFrame();
                if (Host.FrameClockMsForTest == before) Host.Paint(0);
            }
        }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    private static void AssertOldTierAtNewSize(Rig rig, ShellBase p, float widthPx, int tiers)
    {
        Assert.Equal(tiers, p.Seen.Count);                           // the old tier is still what was rendered
        Assert.Equal(56f, rig.Rect(p.Rail).W, 0.5f);                 // the rail is the OLD tier's ...
        Assert.Equal(widthPx - 56f, rig.Rect(p.Card).W, 1f);         // ... and the card is laid out at the NEW size
        Assert.False(rig.HasTracks(p.Rail));                         // a resized frame snaps: no tracks
        Assert.False(rig.HasTracks(p.Card));
        Assert.Equal(1, rig.Tiers.PendingCount);                     // the crossing is waiting
    }

    private static void AssertCrossedWithTracks(Rig rig, ShellBase p, float widthPx)
    {
        Assert.Equal(new[] { 0, 1 }, p.Seen.ToArray());
        Assert.Equal(0, rig.Tiers.PendingCount);
        Assert.Equal(220f, rig.Rect(p.Rail).W, 0.5f);
        Assert.Equal(widthPx - 220f - 80f, rig.Rect(p.Card).W, 1f);   // the wide tier: rail 220 + card + extra 80
        Assert.True(rig.HasTracks(p.Rail), "the rail (size changed) must FLIP on the crossing frame");
        Assert.True(rig.HasTracks(p.Card), "the card (position changed) must FLIP on the crossing frame");
        Assert.False(p.Extra.IsNull);
        Assert.True(rig.HasTracks(p.Extra), "the node the new tier adds must ENTER");
        float tx = rig.TranslateX(p.Card);
        Assert.True(tx < -50f, $"the card's FLIP delta is inverted (it starts where it was): translateX={tx}");
    }

    /// <summary>The crossing was committed and laid out, with no claim about motion (used inside an OS modal loop, where the
    /// WindowResize suppression bit mirrors <c>InModalLoop</c> and the commit frame snaps).</summary>
    private static void AssertCommitted(Rig rig, ShellBase p, float widthPx)
    {
        Assert.Equal(new[] { 0, 1 }, p.Seen.ToArray());
        Assert.Equal(0, rig.Tiers.PendingCount);
        Assert.Equal(220f, rig.Rect(p.Rail).W, 0.5f);
        Assert.Equal(widthPx - 220f - 80f, rig.Rect(p.Card).W, 1f);   // the wide tier: rail 220 + card + extra 80
    }

    private static void AssertSettled(Rig rig, ShellBase p, float widthPx)
    {
        rig.Settle();
        Assert.Equal(0, rig.Anim.TrackCount);
        Assert.Equal(0f, p.Card.IsNull ? 0f : rig.Host.Scene.Paint(p.Card).LocalTransform.Dx, 0.5f);
        Assert.Equal(widthPx - 220f - 80f, rig.Rect(p.Card).W, 1f);   // the wide tier: rail 220 + card + extra 80
    }

    // ── the crossings ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MountingNarrow_ResolvesTheNarrowTierInTheFirstFrame()
    {
        var narrow = new TierShell();
        using var a = new Rig(narrow, 400f);
        Assert.Equal(new[] { 0 }, narrow.Seen.ToArray());
        Assert.Equal(56f, a.Rect(narrow.Rail).W, 0.5f);               // laid out narrow from frame one
        Assert.Equal(0, a.Tiers.PendingCount);

        var wide = new TierShell();
        using var b = new Rig(wide, 900f);
        Assert.Equal(new[] { 1 }, wide.Seen.ToArray());               // and a wide mount is wide from frame one, with no correction
        Assert.Equal(220f, b.Rect(wide.Rail).W, 0.5f);
        b.Frames(3);
        Assert.Equal(1, wide.Seen.Count);
        Assert.Equal(0, b.Tiers.PendingCount);
    }

    [Fact]
    public void ACompositedDrag_DefersTheSizes_SettlesOnTheOldTier_ThenCrossesWithTracksOnTheNextFrame()
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f, composited: true);
        float cardW0 = rig.Rect(p.Card).W;

        rig.Window.InModalLoop = true;
        rig.Window.SizedInModalLoop = true;
        foreach (float w in new[] { 500f, 700f, 900f })
        {
            rig.Resize(w);
            rig.KeepAlive();                                          // the edge drag's keep-alive paint
            Assert.Equal(cardW0, rig.Rect(p.Card).W, 0.5f);           // deferred: no layout at the dragged size
            Assert.Equal(new[] { 0 }, p.Seen.ToArray());
            Assert.Equal(0, rig.Tiers.PendingCount);
            Assert.False(rig.HasTracks(p.Rail));
        }

        rig.Window.InModalLoop = false;                               // WM_EXITSIZEMOVE
        rig.Window.SizedInModalLoop = false;
        rig.KeepAlive();                                              // the settle paint: a resized frame, tier still old
        AssertOldTierAtNewSize(rig, p, 900f, tiers: 1);

        rig.KeepAlive();                                              // the next frame has the new tier
        AssertCrossedWithTracks(rig, p, 900f);
        AssertSettled(rig, p, 900f);
    }

    [Theory]
    [InlineData(false)]   // a keep-alive paint follows
    [InlineData(true)]    // RunFrame follows
    public void AMaximize_ResizedPaintFirst_ThenTheCrossingFrame(bool runFrameFollows)
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f);

        rig.Resize(900f);
        rig.KeepAlive();                                              // WM_SIZE: one synchronous resized paint
        AssertOldTierAtNewSize(rig, p, 900f, tiers: 1);

        if (runFrameFollows) rig.Host.RunFrame(); else rig.KeepAlive();
        AssertCrossedWithTracks(rig, p, 900f);
        AssertSettled(rig, p, 900f);
    }

    [Fact]
    public void ADpiChange_CrossesOnTheNonResizedPaintAfterTheResizedOne()
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f);

        rig.Window.Scale = 0.5f;                                      // WM_DPICHANGED: 400 px at 0.5 is 800 DIP
        rig.KeepAlive();                                              // the resized paint
        AssertOldTierAtNewSize(rig, p, 800f, tiers: 1);

        rig.KeepAlive();                                              // the second paint is not resized
        AssertCrossedWithTracks(rig, p, 800f);
        AssertSettled(rig, p, 800f);
    }

    [Fact]
    public void AZoomChange_CrossesOnTheNonResizedPaintAfterTheResizedOne()
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f);

        rig.Window.SetZoom(0.5f);                                     // the effective scale halves: 800 DIP
        rig.KeepAlive();
        AssertOldTierAtNewSize(rig, p, 800f, tiers: 1);

        rig.KeepAlive();
        AssertCrossedWithTracks(rig, p, 800f);
        AssertSettled(rig, p, 800f);
    }

    [Fact]
    public void TwoConsecutiveResizedKeepAlivePaints_BothKeepTheOldTier_AndTheCrossingLandsOnTheNextOne()
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f);

        rig.Window.Scale = 0.5f;                                      // the DPI path: resized paint ...
        rig.KeepAlive();
        AssertOldTierAtNewSize(rig, p, 800f, tiers: 1);
        rig.Resize(420f);                                             // ... and a second resized paint (840 DIP)
        rig.KeepAlive();
        AssertOldTierAtNewSize(rig, p, 840f, tiers: 1);

        rig.KeepAlive();                                              // the following non-resized paint crosses, animated
        AssertCrossedWithTracks(rig, p, 840f);
    }

    [Fact]
    public void ThePendingTierWake_DrivesRunFrame_AndCommitsWithTracks()
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f);

        rig.Resize(900f);
        rig.KeepAlive();                                              // resized: stores the pending tier, nothing else queued
        Assert.Equal(1, rig.Tiers.PendingCount);
        Assert.True(rig.Host.HasActiveWork);                          // pending tiers are work
        Assert.True((rig.Host.CurrentWakeReasons & WakeReasons.FrameNeeded) != 0);

        rig.Host.RunFrame();                                          // RunFrame, not Paint: the wake must reach it
        AssertCrossedWithTracks(rig, p, 900f);
        AssertSettled(rig, p, 900f);
    }

    [Fact]
    public void TheModalLoopKeepAliveIdleSkip_CannotSwallowThePendingCommit()
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f);

        rig.Resize(900f);
        rig.KeepAlive();                                              // resized: stores the pending tier
        Assert.Equal(1, rig.Tiers.PendingCount);

        // Inside an OS modal loop (a title-bar move: nothing resized, nothing else queued) the keep-alive idle skip bails on an
        // otherwise idle host. A pending tier is essential work (FrameNeeded), so the paint runs and commits it. (Motion
        // inside the loop is suppressed by the WindowResize bit, so only the commit is asserted here.)
        rig.Window.InModalLoop = true;
        rig.Window.SizedInModalLoop = false;
        rig.KeepAlive();
        AssertCommitted(rig, p, 900f);
    }

    private static void DragNotComposited(Rig rig, ShellBase p)
    {
        rig.Window.InModalLoop = true;
        rig.Window.SizedInModalLoop = true;
        foreach (float w in new[] { 450f, 520f, 580f, 640f, 700f, 760f })   // the ladder crosses at 600 mid-drag
        {
            rig.Resize(w);
            rig.KeepAlive();                                          // not composited: each tick lays out at its size
            Assert.Equal(new[] { 0 }, p.Seen.ToArray());              // resized ticks keep the old tier
            Assert.False(rig.HasTracks(p.Rail));
        }
        Assert.Equal(1, rig.Tiers.PendingCount);
    }

    [Fact]
    public void NotComposited_EveryTickIsResized_TheCrossingCommitsOnTheFirstTickThatIsNot()
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f, composited: false);
        DragNotComposited(rig, p);

        rig.KeepAlive();                                              // a tick whose size did not change (still inside the loop)
        AssertCommitted(rig, p, 760f);                                // one tick late, as the plan says; motion inside the loop is suppressed
    }

    [Fact]
    public void NotComposited_AnimatesWhenTheNonResizedFrameFollowsTheLoop()
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f, composited: false);
        DragNotComposited(rig, p);

        rig.Window.InModalLoop = false;                               // mouse-up: the settle paint has the size it already had
        rig.Window.SizedInModalLoop = false;
        rig.KeepAlive();
        AssertCrossedWithTracks(rig, p, 760f);
        AssertSettled(rig, p, 760f);
    }

    // ── a resize during a frame freeze (technical-plan §3.3 rule 8, E2) ────────────────────────────────────────────

    [Fact]
    public void AResizeDuringAFrameFreeze_CutsIt_EndsTheSnapScope_ThenTheCrossingAnimates()
    {
        var p = new TierShell();
        using var rig = new Rig(p, 400f, composited: true);

        var token = rig.Host.Freeze();
        rig.Host.Paint(0);                                            // the capture frame
        Assert.True(rig.Host.IsFrozen);
        Assert.True(rig.Host.IsSnapping);                             // the freeze owns the snap scope: a transaction's motion lands at its end value

        rig.Resize(900f);
        rig.KeepAlive();                                              // the resized frame: the CUT, before the frame flushes
        Assert.Equal(FreezeEnd.CutByResize, token.End);
        Assert.False(rig.Host.IsFrozen);
        Assert.False(rig.Host.IsSnapping);                            // the cut ended the snap scope ...
        Assert.False(rig.Host.Animation.Snapping);
        AssertOldTierAtNewSize(rig, p, 900f, tiers: 1);               // ... and the resized frame still lays out the OLD tier at the new size

        rig.KeepAlive();                                              // the next, non-resized frame commits the pending tier
        AssertCrossedWithTracks(rig, p, 900f);                        // so the crossing ANIMATES (tracks), it does not snap
        AssertSettled(rig, p, 900f);
    }

    [Fact]
    public void AnOpenSnapScope_WouldSwallowTheCrossing_SoTheCutMustEndIt()
    {
        // The negative control of the test above: the identical sequence with a snap scope still open lands the crossing at its end
        // values with no tracks. That is the jump the cut exists to prevent.
        var p = new TierShell();
        using var rig = new Rig(p, 400f, composited: true);

        rig.Resize(900f);
        rig.KeepAlive();                                              // the resized frame (no freeze, no scope)
        AssertOldTierAtNewSize(rig, p, 900f, tiers: 1);
        using (SnapScope.Enter(rig.Host))
        {
            rig.KeepAlive();                                          // the crossing frame, inside a snap scope
        }
        Assert.Equal(new[] { 0, 1 }, p.Seen.ToArray());
        Assert.Equal(220f, rig.Rect(p.Rail).W, 0.5f);                 // it crossed ...
        Assert.False(rig.HasTracks(p.Rail));                          // ... with nothing animating
        Assert.False(rig.HasTracks(p.Card));
    }

    // ── negative controls: the two catches that make the host commit step necessary ────────────────────────────────

    [Fact]
    public void NegativeControl_ACrossingDeferredWithUseTimeoutZero_DoesNotFlip()
    {
        var p = new TimeoutShell();
        using var rig = new Rig(p, 400f);

        rig.Resize(900f);
        bool anyTrack = false;
        for (int i = 0; i < 4; i++)
        {
            rig.Host.RunFrame();
            anyTrack |= rig.HasTracks(p.Rail) || rig.HasTracks(p.Card);
        }
        Assert.Equal(220f, rig.Rect(p.Rail).W, 0.5f);                 // the layout did cross ...
        Assert.False(anyTrack, "a timer-deferred write is reconciled after FLIP capture: it must snap");   // ... but it snapped
    }

    [Fact]
    public void NegativeControl_APlainLayoutPhaseWrite_ReconciledOnAResizedFrame_DoesNotFlip()
    {
        var p = new LayoutWriteShell();
        using var rig = new Rig(p, 400f);

        rig.Resize(900f);
        rig.Host.RunFrame();                                          // resized: the layout-phase write lands pending in the runtime
        rig.Resize(910f);
        rig.Host.RunFrame();                                          // the NEXT frame is resized too: capture is skipped, the write reconciles
        bool anyTrack = rig.HasTracks(p.Rail) || rig.HasTracks(p.Card);
        rig.Frames(2);
        anyTrack |= rig.HasTracks(p.Rail) || rig.HasTracks(p.Card);
        Assert.Equal(new[] { 0, 1 }, p.Seen.ToArray());               // it crossed ...
        Assert.False(anyTrack, "a plain layout-phase write reconciled on a resized frame has no capture: it must snap");   // ... without a track
    }

    // ── the InModalLoop host context signal ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void InModalLoop_IsAHostContextSignal_ThatFlipsOnTheEdges()
    {
        var probe = new ModalFactProbe();
        using var rig = new Rig(probe, 400f);
        Assert.Equal(new[] { false }, probe.Seen.ToArray());

        rig.Window.InModalLoop = true;
        rig.KeepAlive();
        Assert.Equal(new[] { false, true }, probe.Seen.ToArray());
        rig.KeepAlive();
        rig.KeepAlive();
        Assert.Equal(2, probe.Seen.Count);                            // an edge, never per paint

        rig.Window.InModalLoop = false;
        rig.KeepAlive();
        Assert.Equal(new[] { false, true, false }, probe.Seen.ToArray());
    }
}
