using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

// ── SizeMode.FlowReveal — the smooth-reveal primitive (docs/plans/smooth-reveal-implementation.md §9). Every gate runs
// headless at a FIXED dt (8.33 / 16.67 ms) and reads PRESENTED geometry (SceneStore.PresentedAbsoluteRect), never layout:
// a FlowReveal lands layout once and moves only at paint time.
static class RevealSuite
{
    static readonly LayoutTransition Reveal = new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(), Size: SizeMode.FlowReveal);
    static readonly LayoutTransition RevealEnterExit = Reveal with { Enter = new EnterExit(Active: true), Exit = new EnterExit(Active: true) };

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        NoLayoutNoRenderNoAlloc(strings, fonts);
        FirstFrameAndStepBounds(strings, fonts);
        Mirror(strings, fonts);
        Lockstep(strings, fonts);
        TallRevealInViewport(strings, fonts);
        Reverse(strings, fonts);
        ConcurrentAndNested(strings, fonts);
        NestedClose(strings, fonts);
        HitTest(strings, fonts);
        ScrollClampAndAnchor(strings, fonts);
        RecycleSuppression(strings);
        AsyncGrowth(strings, fonts);
        ReducedMotionSnaps(strings, fonts);
        SuppressedCloseSettles(strings, fonts);
        SettleOnFree(strings);
        GrowChildOfScrollContent(strings, fonts);
    }

    /// <summary>The root column holds ONE inner column ("col": a 40-DIP head, N reveal clip wrappers whose declared Height
    /// toggles 0 ↔ auto over a body of BodyH[i], then two 30-DIP followers). The inner column is not the scene root, so its
    /// PresentedH is the presented parent bottom (the root is a flow boundary).</summary>
    sealed class RevealColumnProbe : Component
    {
        public required Signal<bool>[] Open;
        public required Signal<float>[] BodyH;
        public readonly Signal<int> Clicks = new(0);
        public override Element Render()
        {
            var kids = new List<Element>(Open.Length + 3) { new BoxEl { Key = "head", Height = 40f } };
            for (int i = 0; i < Open.Length; i++)
            {
                int k = i;
                kids.Add(new BoxEl
                {
                    Key = "clip" + k, Direction = 1, Height = Open[k].Value ? float.NaN : 0f, Animate = Reveal,
                    Children = [new BoxEl { Key = "body" + k, Height = Prop.Of(() => BodyH[k].Value), Fill = Tok.FillCardDefault }],
                });
            }
            kids.Add(new BoxEl { Key = "after", Height = 30f, OnClick = () => Clicks.Value = Clicks.Peek() + 1 });
            kids.Add(new BoxEl { Key = "after2", Height = 30f });
            return new BoxEl { Direction = 1, Children = [new BoxEl { Key = "col", Direction = 1, Width = 300f, Children = kids.ToArray() }] };
        }
    }

    sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly HeadlessWindow Window;
        public readonly AppHost Host;
        public Rig(StringTable strings, HeadlessFontSystem fonts, Component root, float dtMs, float w = 360f, float h = 640f)
        {
            Window = new HeadlessWindow(new WindowDesc("reveal", new Size2(w, h), 1f));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), fonts, strings, root, frameTime: new FixedFrameTimeSource(dtMs));
            for (int i = 0; i < 4; i++) Host.RunFrame();
        }
        public SceneStore Scene => Host.Scene;
        public NodeHandle Col => Child(Scene, Scene.Root, 0);
        public NodeHandle ColChild(int i) => Child(Scene, Col, i);
        public float Y(NodeHandle n) => Scene.PresentedAbsoluteRect(n).Y;
        public float LayoutY(NodeHandle n) => Scene.AbsoluteRect(n).Y;
        public bool Revealing(NodeHandle n) => Host.Animation.TryGetTrackValue(n, AnimChannel.RevealExtent, out _);
        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    static RevealColumnProbe Column(params float[] bodies)
    {
        var open = new Signal<bool>[bodies.Length];
        var body = new Signal<float>[bodies.Length];
        for (int i = 0; i < bodies.Length; i++) { open[i] = new Signal<bool>(false); body[i] = new Signal<float>(bodies[i]); }
        return new RevealColumnProbe { Open = open, BodyH = body };
    }

    // Run frames while `clip` reveals (cap 90), recording the PRESENTED y of `track` after each one.
    static List<float> Trace(Rig rig, NodeHandle clip, NodeHandle track)
    {
        var ys = new List<float>();
        for (int i = 0; i < 90 && (i < 2 || rig.Revealing(clip)); i++) { rig.Host.RunFrame(); ys.Add(rig.Y(track)); }
        return ys;
    }

    static float MaxStep(float start, List<float> ys)
    {
        float m = 0f, prev = start;
        foreach (float y in ys) { m = MathF.Max(m, MathF.Abs(y - prev)); prev = y; }
        return m;
    }

    // rv.1 + rv.2 — the whole point: once seeded, a reveal is paint-only.
    static void NoLayoutNoRenderNoAlloc(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(160f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();   // the commit frame: render + layout + seed (presents the old geometry)
        rig.Host.RunFrame();   // the first advance
        bool noLayout = true, noRender = true, noAlloc = true;
        int frames = 0;
        string first = "";
        while (rig.Revealing(clip) && frames < 60)
        {
            var s = rig.Host.RunFrame();
            frames++;
            if ((s.MeasureCount != 0 || s.ArrangeCount != 0) && first.Length == 0) first = $"layout@{frames} m={s.MeasureCount} a={s.ArrangeCount}";
            if (s.ComponentsRendered != 0) noRender = false;
            if (s.HotPhaseAllocBytes != 0) noAlloc = false;
            noLayout &= s.MeasureCount == 0 && s.ArrangeCount == 0;
        }
        Check("rv.1 FlowReveal: every animation tick after the seed runs NO layout pass and renders NO component",
            noLayout && noRender && frames > 5, $"frames={frames} render={!noRender} {first}");
        Check("rv.2 FlowReveal: every animation tick allocates nothing in phases 6–13", noAlloc, $"frames={frames}");
    }

    // rv.3 — no lurch: the commit frame holds, the first advance is small, no frame jumps, it lands on layout.
    static void FirstFrameAndStepBounds(StringTable strings, HeadlessFontSystem fonts)
    {
        foreach (float dt in new[] { 8.33f, 16.67f })
        {
            var probe = Column(200f);
            using var rig = new Rig(strings, fonts, probe, dt);
            var clip = rig.ColChild(1);
            var after = rig.ColChild(2);
            float y0 = rig.Y(after);
            probe.Open[0].Value = true;
            rig.Host.RunFrame();
            float yCommit = rig.Y(after);
            var ys = Trace(rig, clip, after);
            const float travel = 200f;
            bool fast = dt < 10f;
            float firstStep = ys.Count > 0 ? ys[0] - yCommit : -1f;
            float maxStep = MaxStep(yCommit, ys);
            bool holds = Near(yCommit, y0, 0.5f);
            bool first = firstStep >= 0f && firstStep <= travel * (fast ? 0.02f : 0.05f);
            bool step = maxStep <= travel * (fast ? 0.08f : 0.15f);
            bool lands = ys.Count > 0 && Near(ys[^1], rig.LayoutY(after), 0.5f) && Near(rig.LayoutY(after), y0 + travel, 0.5f);
            Check($"rv.3 FlowReveal @{dt:0.##}ms: the commit frame shows the old geometry, the first advance moves ≤{(fast ? 2 : 5)}% of the travel, no frame steps >{(fast ? 8 : 15)}%, and it lands on layout",
                holds && first && step && lands,
                $"y0={y0:0.0} commit={yCommit:0.0} first={firstStep:0.00} maxStep={maxStep:0.00} end={(ys.Count > 0 ? ys[^1] : -1f):0.0} layout={rig.LayoutY(after):0.0}");
        }
    }

    // rv.4 — symmetric: the close is the open mirrored (same spring, same frames).
    static void Mirror(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(200f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        var after = rig.ColChild(2);
        float y0 = rig.Y(after);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        var open = Trace(rig, clip, after);
        for (int i = 0; i < 10; i++) rig.Host.RunFrame();
        probe.Open[0].Value = false;
        rig.Host.RunFrame();
        var close = Trace(rig, clip, after);
        int n = Math.Min(open.Count, close.Count);
        float worst = 0f;
        for (int i = 0; i < n; i++) worst = MathF.Max(worst, MathF.Abs((open[i] - y0) + (close[i] - y0) - 200f));
        Check("rv.4 FlowReveal: collapse mirrors expand frame for frame (open(t) + close(t) = travel, within 1 px)",
            n > 10 && worst <= 1f, $"frames={n} worst={worst:0.00}");
    }

    // rv.5 — lockstep: followers move rigidly together, the parent bottom tracks the edge, the last frame is layout.
    static void Lockstep(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(180f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        var after = rig.ColChild(2);
        var after2 = rig.ColChild(3);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        bool rigid = true, parent = true;
        int frames = 0;
        float lastY = 0f;
        while (frames < 90 && (frames < 2 || rig.Revealing(clip)))
        {
            rig.Host.RunFrame();
            frames++;
            float p = rig.Scene.PresentedAbsoluteRect(clip).H;
            rigid &= Near(rig.Y(after2) - rig.Y(after), 30f, 0.01f);
            parent &= Near(rig.Scene.PresentedAbsoluteRect(rig.Col).H, 40f + p + 60f, 0.5f);
            lastY = rig.Y(after);
        }
        rig.Host.RunFrame();
        bool rest = Near(rig.Y(after), rig.LayoutY(after), 0.01f) && float.IsNaN(rig.Scene.Paint(rig.Col).PresentedH)
            && rig.Scene.Paint(rig.Col).FlowBits == 0;
        bool noSettleJump = Near(lastY, rig.LayoutY(after), 0.6f);
        Check("rv.5 FlowReveal lockstep: followers move rigidly, the parent's presented bottom tracks the edge, and the settle frame is layout (no jump, flow columns at rest)",
            rigid && parent && rest && noSettleJump, $"frames={frames} rigid={rigid} parent={parent} rest={rest} lastY={lastY:0.00} layout={rig.LayoutY(after):0.00}");
    }

    sealed class TallRevealProbe : Component
    {
        public readonly Signal<bool> Open = new(false);
        public override Element Render() => new ScrollEl
        {
            Width = 300f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Key = "head", Height = 40f },
                    new BoxEl { Key = "clip", Direction = 1, Height = Open.Value ? float.NaN : 0f, Animate = Reveal,
                                Children = [new BoxEl { Key = "body", Height = 1300f }] },
                    new BoxEl { Key = "after", Height = 30f },
                    new BoxEl { Key = "tail", Height = 400f },
                ],
            },
        };
    }

    // rv.6 — a 1300-DIP reveal in a 300-DIP view animates only what can be seen: the moving edge stays on screen.
    static void TallRevealInViewport(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new TallRevealProbe();
        using var rig = new Rig(strings, fonts, probe, 16.67f, 300f, 300f);
        var vp = FindScrollNode(rig.Scene, rig.Scene.Root);
        rig.Scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        var clip = Child(rig.Scene, content, 1);
        var after = Child(rig.Scene, content, 2);
        float top = rig.Scene.AbsoluteRect(vp).Y, bottom = top + sc.ViewportH;
        probe.Open.Value = true;
        rig.Host.RunFrame();
        int frames = 0, onScreen = 0;
        while (frames < 90 && (frames < 2 || rig.Revealing(clip)))
        {
            rig.Host.RunFrame();
            frames++;
            float y = rig.Y(after);
            if (y >= top && y <= bottom + RevealPlan.VisibleSlackDip + 0.5f) onScreen++;   // the clamp parks the edge in the slack
        }
        float share = frames == 0 ? 0f : onScreen / (float)frames;
        Check("rv.6 FlowReveal visible-span clamp: a 1300-DIP reveal in a 300-DIP view keeps its moving edge on screen for ≥70% of its frames, and lands on layout",
            share >= 0.7f && Near(rig.Y(after), rig.LayoutY(after), 0.5f), $"frames={frames} onScreen={onScreen} share={share:0.00}");
    }

    // rv.7 — interruptible: a reverse at ~50% departs from the live value with its velocity; a toggle storm ends at rest.
    static void Reverse(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(200f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        var after = rig.ColChild(2);
        float y0 = rig.Y(after);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        float prev = rig.Y(after), maxStep = 0f;
        for (int i = 0; i < 30 && rig.Y(after) - y0 < 100f; i++) { rig.Host.RunFrame(); maxStep = MathF.Max(maxStep, MathF.Abs(rig.Y(after) - prev)); prev = rig.Y(after); }
        probe.Open[0].Value = false;   // reverse mid-flight
        for (int i = 0; i < 60; i++) { rig.Host.RunFrame(); maxStep = MathF.Max(maxStep, MathF.Abs(rig.Y(after) - prev)); prev = rig.Y(after); }
        bool reversed = Near(rig.Y(after), y0, 0.5f) && !rig.Revealing(clip) && maxStep <= 200f * 0.15f;
        for (int i = 0; i < 12; i++) { probe.Open[0].Value = !probe.Open[0].Peek(); rig.Host.RunFrame(); rig.Host.RunFrame(); }
        for (int i = 0; i < 60; i++) rig.Host.RunFrame();
        bool open = probe.Open[0].Peek();
        bool storm = !rig.Revealing(clip) && Near(rig.Y(after), open ? y0 + 200f : y0, 0.5f) && rig.Scene.Paint(rig.Col).FlowBits == 0;
        Check("rv.7 FlowReveal reverse: a mid-flight reverse is continuous (no frame steps >15%) and lands closed; a 12-toggle storm ends at rest on layout",
            reversed && storm, $"maxStep={maxStep:0.00} y={rig.Y(after):0.0} y0={y0:0.0} storm={storm}");
    }

    sealed class NestedProbe : Component
    {
        public readonly Signal<bool> Outer = new(false), Inner = new(false);
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl
                {
                    Key = "col", Direction = 1, Width = 300f,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = "outer", Direction = 1, Height = Outer.Value ? float.NaN : 0f, Animate = Reveal,
                            Children =
                            [
                                new BoxEl { Key = "inner", Direction = 1, Height = Inner.Value ? float.NaN : 0f, Animate = Reveal,
                                            Children = [new BoxEl { Height = 120f }] },
                                new BoxEl { Key = "innerTail", Height = 40f },
                            ],
                        },
                        new BoxEl { Key = "after", Height = 30f },
                    ],
                },
            ],
        };
    }

    // rv.8 — any number at once; nested reveals compose.
    static void ConcurrentAndNested(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(100f, 150f);
        using (var rig = new Rig(strings, fonts, probe, 16.67f))
        {
            var c0 = rig.ColChild(1);
            var c1 = rig.ColChild(2);
            var after = rig.ColChild(3);
            float y0 = rig.Y(after);
            probe.Open[0].Value = true;
            probe.Open[1].Value = true;
            rig.Host.RunFrame();
            bool sums = true;
            for (int i = 0; i < 60; i++)
            {
                rig.Host.RunFrame();
                float p0 = rig.Scene.PresentedAbsoluteRect(c0).H, p1 = rig.Scene.PresentedAbsoluteRect(c1).H;
                sums &= Near(rig.Y(after) - y0, p0 + p1, 0.6f);
            }
            Check("rv.8a FlowReveal concurrency: two reveals in one column run together and the follower rides their SUM every frame",
                sums && Near(rig.Y(after), y0 + 250f, 0.5f) && !rig.Revealing(c0) && !rig.Revealing(c1), $"sums={sums} y={rig.Y(after) - y0:0.0}");
        }
        var nested = new NestedProbe();
        using (var rig = new Rig(strings, fonts, nested, 16.67f))
        {
            var outer = Child(rig.Scene, rig.Col, 0);
            var after = Child(rig.Scene, rig.Col, 1);
            float y0 = rig.Y(after), prev = y0;
            nested.Outer.Value = true;
            nested.Inner.Value = true;
            rig.Host.RunFrame();
            bool monotone = Near(rig.Y(after), y0, 0.5f);   // the seed frame: −160 against +160 (a sum would be −280)
            prev = rig.Y(after);
            for (int i = 0; i < 70; i++) { rig.Host.RunFrame(); monotone &= rig.Y(after) >= prev - 0.01f; prev = rig.Y(after); }
            var inner = Child(rig.Scene, outer, 0);
            Check("rv.8b FlowReveal nesting: an inner reveal inside an outer one COMBINES (min, never a sum): the seed frame holds the follower, it moves monotonically, and both land on layout",
                monotone && Near(rig.Y(after), y0 + 160f, 0.5f) && !rig.Revealing(outer) && !rig.Revealing(inner),
                $"monotone={monotone} y={rig.Y(after) - y0:0.0}");
        }
    }

    // rv.8c — nesting, the CLOSE legs. A collapse lays out at its FINAL height, so the nesting bound must come from what
    // the content presents (and apply only while an inner reveal runs), never from the laid-out height (§4.1 worked check).
    // (1) The outer closes over an inner one that stays open; (2) both close in one commit. Each commit frame holds the
    // follower where it was, each close is monotone and mirrors the both-open trace frame for frame, and both land on layout.
    static void NestedClose(StringTable strings, HeadlessFontSystem fonts)
    {
        var nested = new NestedProbe();
        using var rig = new Rig(strings, fonts, nested, 16.67f);
        var outer = Child(rig.Scene, rig.Col, 0);
        var after = Child(rig.Scene, rig.Col, 1);
        var inner = Child(rig.Scene, outer, 0);
        float y0 = rig.Y(after);
        nested.Outer.Value = true;
        nested.Inner.Value = true;
        rig.Host.RunFrame();
        var open = Trace(rig, outer, after);   // the reference: both open together (the follower rides y0 + P_outer)
        for (int i = 0; i < 10; i++) rig.Host.RunFrame();

        // Mirror + monotone + commit-hold check of one close leg against the open trace.
        bool CloseLeg(float yCommit, List<float> close, out float worst)
        {
            int n = Math.Min(open.Count, close.Count);
            worst = 0f;
            bool monotone = close.Count > 0 && close[0] <= yCommit + 0.01f;
            for (int i = 0; i < n; i++) worst = MathF.Max(worst, MathF.Abs((open[i] - y0) + (close[i] - y0) - 160f));
            for (int i = 1; i < close.Count; i++) monotone &= close[i] <= close[i - 1] + 0.01f;
            return Near(yCommit, y0 + 160f, 0.5f) && n > 10 && worst <= 1f && monotone;
        }

        // (1) the outer closes; the inner stays open (no inner row, so no bound).
        nested.Outer.Value = false;
        rig.Host.RunFrame();
        float yCommit1 = rig.Y(after);
        var close1 = Trace(rig, outer, after);
        for (int i = 0; i < 10; i++) rig.Host.RunFrame();
        bool mirrored1 = CloseLeg(yCommit1, close1, out float worst1);
        bool leg1 = mirrored1 && Near(rig.Y(after), y0, 0.5f) && !rig.Revealing(outer);
        Check("rv.8c.1 FlowReveal nesting, close over an open inner reveal: the commit frame holds, the close mirrors the open (within 1 px, monotone) and lands on layout",
            leg1, $"commit={yCommit1 - y0:0.0} frames={close1.Count} worst={worst1:0.00} y={rig.Y(after) - y0:0.0}");

        // (2) reopen the outer (the inner is still open), let it rest, then close BOTH in one commit (both rows live: the
        // bound applies, ContentExtent 40 + the inner's 120·(1−x) never falls below the outer's 160·(1−x)).
        nested.Outer.Value = true;
        for (int i = 0; i < 60; i++) rig.Host.RunFrame();
        bool reopened = Near(rig.Y(after), y0 + 160f, 0.5f);
        nested.Outer.Value = false;
        nested.Inner.Value = false;
        rig.Host.RunFrame();
        float yCommit2 = rig.Y(after);
        var close2 = Trace(rig, outer, after);
        for (int i = 0; i < 10; i++) rig.Host.RunFrame();
        bool mirrored2 = CloseLeg(yCommit2, close2, out float worst2);   // first: worst2 is read by the message below
        bool leg2 = reopened && mirrored2 && Near(rig.Y(after), y0, 0.5f)
            && !rig.Revealing(outer) && !rig.Revealing(inner) && rig.Scene.Paint(rig.Col).FlowBits == 0;
        Check("rv.8c.2 FlowReveal nesting, outer and inner closing together: the commit frame holds, the close mirrors the open (within 1 px, monotone) and both land on layout",
            leg2, $"reopened={reopened} commit={yCommit2 - y0:0.0} frames={close2.Count} worst={worst2:0.00} y={rig.Y(after) - y0:0.0}");
    }

    // rv.9 — input follows paint: mid-flight the follower is hit where it is DRAWN, not where layout put it.
    static void HitTest(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(200f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var after = rig.ColChild(2);
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        for (int i = 0; i < 5; i++) rig.Host.RunFrame();
        var pr = rig.Scene.PresentedAbsoluteRect(after);
        var lr = rig.Scene.AbsoluteRect(after);
        var atPresented = rig.Host.Input.HitTest(new Point2(pr.X + pr.W * 0.5f, pr.Y + pr.H * 0.5f));
        var atLayout = rig.Host.Input.HitTest(new Point2(lr.X + lr.W * 0.5f, lr.Y + lr.H * 0.5f));
        Check("rv.9 FlowReveal hit-testing mirrors the presented flow: the follower is hit at its drawn rect and not at its laid-out one mid-flight",
            pr.Y < lr.Y - 20f && atPresented == after && atLayout != after, $"presentedY={pr.Y:0.0} layoutY={lr.Y:0.0} hitP={atPresented == after} hitL={atLayout == after}");
    }

    sealed class ScrollRevealProbe : Component
    {
        public readonly Signal<bool> Open = new(true), OpenTop = new(true);
        public override Element Render() => new ScrollEl
        {
            Width = 300f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Key = "topclip", Direction = 1, Height = OpenTop.Value ? float.NaN : 0f, Animate = Reveal, Children = [new BoxEl { Height = 200f }] },
                    new BoxEl { Key = "filler", Height = 1200f },
                    new BoxEl { Key = "clip", Direction = 1, Height = Open.Value ? float.NaN : 0f, Animate = Reveal, Children = [new BoxEl { Height = 200f }] },
                    new BoxEl { Key = "tail", Height = 50f },
                ],
            },
        };
    }

    // rv.10 — scrolling. (a) A collapse at the end rides the max-offset edge down: the commit frame HOLDS the offset (no
    // clamp against the laid-out extent), then it eases monotonically to the new max. (b) A reveal wholly above the
    // viewport snaps AND anchors: the scroll frame shifts by the change, so the content being read stays exactly where it
    // was, in both directions.
    static void ScrollClampAndAnchor(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new ScrollRevealProbe();
        using var rig = new Rig(strings, fonts, probe, 16.67f, 300f, 300f);
        var vp = FindScrollNode(rig.Scene, rig.Scene.Root);
        var handle = rig.Host.TryGetScrollHandle(vp)!;
        handle.ScrollTo(handle.MaxOffset, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var sc0);
        double start = sc0.Offset;
        probe.Open.Value = false;
        rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var scCommit);
        bool commitHolds = Math.Abs(scCommit.Offset - start) <= 0.5;
        double prev = scCommit.Offset, maxStep = 0;
        bool monotone = true;
        for (int i = 0; i < 70; i++)
        {
            rig.Host.RunFrame();
            rig.Scene.TryGetScroll(vp, out var sc);
            monotone &= sc.Offset <= prev + 0.01;
            maxStep = Math.Max(maxStep, Math.Abs(sc.Offset - prev));
            prev = sc.Offset;
        }
        bool edge = Near((float)start, 1350f, 1f) && commitHolds && Near((float)prev, 1150f, 1f) && monotone && maxStep <= 200 * 0.15;

        // (b) Content is now 200 + 1200 + 0 + 50; at offset 900 the filler covers the whole view.
        handle.ScrollTo(900, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var sc2);
        var topclip = Child(rig.Scene, sc2.ContentNode, 0);
        var filler = Child(rig.Scene, sc2.ContentNode, 1);
        float fy0 = rig.Y(filler);
        probe.OpenTop.Value = false;   // 200 DIP collapse wholly above the view
        rig.Host.RunFrame();
        bool snappedClose = !rig.Revealing(topclip);
        float fyClose = rig.Y(filler);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var sc3);
        bool closeAnchored = snappedClose && Near(fyClose, fy0, 0.5f) && Near(rig.Y(filler), fy0, 0.5f) && Near((float)sc3.Offset, 700f, 0.5f);
        probe.OpenTop.Value = true;    // and back open: the frame shifts the other way
        rig.Host.RunFrame();
        bool snappedOpen = !rig.Revealing(topclip);
        float fyOpen = rig.Y(filler);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        rig.Scene.TryGetScroll(vp, out var sc4);
        bool openAnchored = snappedOpen && Near(fyOpen, fy0, 0.5f) && Near(rig.Y(filler), fy0, 0.5f) && Near((float)sc4.Offset, 900f, 0.5f);
        Check("rv.10a FlowReveal scrolling: a collapse at the end holds the offset in its commit frame, then eases it down the presented max (monotone, no step >15%) to the new max",
            edge, $"offset {start:0.0}→commit {scCommit.Offset:0.0}→{prev:0.0} maxStep={maxStep:0.00} monotone={monotone}");
        Check("rv.10b FlowReveal scroll anchoring: a reveal wholly above the viewport snaps and shifts the offset by its change, so the content in view does not move (close and reopen)",
            closeAnchored && openAnchored,
            $"fillerY {fy0:0.0}→close {fyClose:0.0}/{sc3.Offset:0.0}→open {fyOpen:0.0}/{sc4.Offset:0.0} snapped={snappedClose}/{snappedOpen}");
    }

    // rv.11 — a recycle (a rebind flush) is the SAME row: no reveal on the remount, no orphan on the unmount.
    static void RecycleSuppression(StringTable strings)
    {
        var scene = new SceneStore();
        var rec = new TreeReconciler(scene, strings);
        var anim = new AnimEngine(scene);
        rec.Anim = anim;
        static Element Tree(bool drawer) => new BoxEl
        {
            Direction = 1,
            Children = drawer
                ? [new BoxEl { Key = "row", Height = 40f }, new BoxEl { Key = "drawer", Height = 80f, Animate = RevealEnterExit }]
                : [new BoxEl { Key = "row", Height = 40f }],
        };
        var t0 = Tree(false);
        rec.ReconcileRoot(t0, null);
        var t1 = Tree(true);
        using (rec.PushSuppressBoundTransitions()) rec.ReconcileRoot(t1, t0);
        bool enterSuppressed = anim.PendingEnterReveal.Count == 0;
        var t2 = Tree(false);
        using (rec.PushSuppressBoundTransitions()) rec.ReconcileRoot(t2, t1);
        bool exitSuppressed = scene.OrphanCount == 0;
        var t3 = Tree(true);
        rec.ReconcileRoot(t3, t2);
        bool enterSeeded = anim.PendingEnterReveal.Count == 1;
        anim.PendingEnterReveal.Clear();
        rec.ReconcileRoot(Tree(false), t3);
        bool exitOrphaned = scene.OrphanCount == 1 && anim.RevealExitCarriers.Count == 1;
        Check("rv.11 FlowReveal recycle: a mount/unmount under the recycle scope seeds no reveal and orphans nothing; the same edits outside it reveal",
            enterSuppressed && exitSuppressed && enterSeeded && exitOrphaned,
            $"enterSuppressed={enterSuppressed} exitSuppressed={exitSuppressed} enterSeeded={enterSeeded} exitOrphaned={exitOrphaned}");
    }

    // rv.12 — async content growth mid-flight retargets the live spring (no second animation, no jump).
    static void AsyncGrowth(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = Column(120f);
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        var clip = rig.ColChild(1);
        var after = rig.ColChild(2);
        float y0 = rig.Y(after), prev = y0, maxStep = 0f;
        probe.Open[0].Value = true;
        rig.Host.RunFrame();
        bool monotone = true;
        for (int i = 0; i < 70; i++)
        {
            if (i == 4) probe.BodyH[0].Value = 240f;   // the content grows while the reveal runs
            rig.Host.RunFrame();
            float y = rig.Y(after);
            monotone &= y >= prev - 0.01f;
            maxStep = MathF.Max(maxStep, y - prev);
            prev = y;
        }
        Check("rv.12 FlowReveal async growth: a mid-flight content growth retargets the live row (monotone, no step >15%) and lands on the new layout",
            monotone && maxStep <= 240f * 0.15f && Near(rig.Y(after), y0 + 240f, 0.5f) && !rig.Revealing(clip),
            $"monotone={monotone} maxStep={maxStep:0.00} y={rig.Y(after) - y0:0.0}");
    }

    // rv.13 — reduced motion snaps the size (the token's SnapEnd policy, read as a value at the seed).
    static void ReducedMotionSnaps(StringTable strings, HeadlessFontSystem fonts)
    {
        bool prevReduced = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = true;
            var probe = Column(200f);
            using var rig = new Rig(strings, fonts, probe, 16.67f);
            var clip = rig.ColChild(1);
            var after = rig.ColChild(2);
            probe.Open[0].Value = true;
            rig.Host.RunFrame();
            Check("rv.13 FlowReveal reduced motion: the toggle lands on layout in its commit frame with no reveal row",
                !rig.Revealing(clip) && Near(rig.Y(after), rig.LayoutY(after), 0.01f), $"y={rig.Y(after):0.0} layout={rig.LayoutY(after):0.0}");
        }
        finally { Motion.ReducedMotion = prevReduced; }
    }

    static NodeHandle FindText(SceneStore s, StringTable strings, NodeHandle n, string text)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).VisualKind == VisualKind.Text && strings.Resolve(s.Paint(n).Text) == text) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindText(s, strings, c, text);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    sealed class ExpanderProbe : Component
    {
        public readonly Signal<bool> Open = new(true);
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                Embed.Comp(() => new Expander { Header = "Section", IsExpanded = Open, Content = new TextEl("rv-expander-body") { Size = 14f } }),
                new BoxEl { Key = "after", Height = 30f },
            ],
        };
    }

    // rv.14 — the settle ALWAYS fires. A close while projections are suppressed (as during a user scroll) starts no
    // reveal row, yet the panel unmounts at once and the toggle window ends: no poller, nothing stuck mounted. A reopen
    // after the suppression lifts reveals normally.
    static void SuppressedCloseSettles(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new ExpanderProbe();
        using var rig = new Rig(strings, fonts, probe, 16.67f);
        NodeHandle Body() => FindText(rig.Scene, strings, rig.Scene.Root, "rv-expander-body");
        var body = Body();
        var clip = body.IsNull ? NodeHandle.Null : rig.Scene.Parent(rig.Scene.Parent(body));   // text → panel → clip wrapper
        bool mountedOpen = !clip.IsNull;
        bool noRow = false, unmounted = false;
        int frames = 0, pollers = 0;
        try
        {
            Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, true);
            probe.Open.Value = false;
            rig.Host.RunFrame();
            noRow = mountedOpen && !rig.Revealing(clip);
            while (frames < 4 && !Body().IsNull) { rig.Host.RunFrame(); frames++; pollers = Math.Max(pollers, rig.Host.FrameClockPollerCount); }
            unmounted = Body().IsNull;
        }
        finally { Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, false); }
        probe.Open.Value = true;
        bool reveals = false;
        for (int i = 0; i < 4 && !reveals; i++) { rig.Host.RunFrame(); reveals = mountedOpen && rig.Revealing(clip); }
        Check("rv.14 FlowReveal settle always fires: an Expander closed under suppressed projections starts no row, still unmounts its panel within 2 frames with no poller, and reopens with a reveal",
            mountedOpen && noRow && unmounted && frames <= 2 && pollers == 0 && reveals,
            $"mounted={mountedOpen} noRow={noRow} unmounted={unmounted} frames={frames} pollers={pollers} reveals={reveals}");
    }

    // rv.15 — a settle callback whose node dies (a drawer recycled away, an orphan reclaimed by a reopen) runs exactly
    // once and is forgotten: nothing waits forever, nothing leaks.
    static void SettleOnFree(StringTable strings)
    {
        var scene = new SceneStore();
        var rec = new TreeReconciler(scene, strings);
        var anim = new AnimEngine(scene);
        rec.Anim = anim;
        scene.OnFreeIndex = anim.ClearForIndex;   // AppHost wires the same fan-out (OnSceneSlotFreed)
        var t0 = new BoxEl { Direction = 1, Children = [new BoxEl { Key = "drawer", Height = 80f }] };
        rec.ReconcileRoot(t0, null);
        var drawer = Child(scene, scene.Root, 0);
        int runs = 0;
        anim.WhenSettled(drawer, AnimChannel.RevealExtent, () => runs++);
        bool registered = anim.SettleCallbackCount == 1;
        var t1 = new BoxEl { Direction = 1, Children = [] };
        rec.ReconcileRoot(t1, t0);   // no transition: the drawer is freed outright
        bool queued = anim.HasSettledCallbacks;
        anim.DrainSettledCallbacks();
        anim.DrainSettledCallbacks();
        Check("rv.15 FlowReveal settle on free: a callback registered on a node that is freed runs once and is dropped",
            registered && queued && runs == 1 && !anim.HasSettledCallbacks && anim.SettleCallbackCount == 0,
            $"registered={registered} queued={queued} runs={runs} left={anim.SettleCallbackCount}");
    }

    sealed class GrowRevealProbe : Component
    {
        public readonly Signal<bool> Open = new(false);
        public override Element Render() => new ScrollEl
        {
            Width = 300f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Key = "page", Direction = 1, Grow = 1f,
                        Children =
                        [
                            new BoxEl { Key = "head", Height = 40f },
                            new BoxEl { Key = "clip", Direction = 1, Height = Open.Value ? float.NaN : 0f, Animate = Reveal,
                                        Children = [new BoxEl { Height = 200f }] },
                            new BoxEl { Key = "tail", Height = 500f },
                        ],
                    },
                ],
            },
        };
    }

    // rv.16 — a Grow child of a scroll content follows its content (the content's main axis is unbounded), so it is NOT
    // a flow boundary: a reveal inside it reaches the scroll content, and the presented extent tracks it mid-flight.
    static void GrowChildOfScrollContent(StringTable strings, HeadlessFontSystem fonts)
    {
        var probe = new GrowRevealProbe();
        using var rig = new Rig(strings, fonts, probe, 16.67f, 300f, 300f);
        var vp = FindScrollNode(rig.Scene, rig.Scene.Root);
        rig.Scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        var clip = Child(rig.Scene, Child(rig.Scene, content, 0), 1);
        probe.Open.Value = true;
        rig.Host.RunFrame();
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        float revealed = rig.Scene.PresentedAbsoluteRect(clip).H - rig.Scene.Bounds(clip).H;   // < 0 mid-flight
        float contentDelta = rig.Scene.Paint(content).FlowDelta;
        Check("rv.16 FlowReveal inside a Grow child of a scroll content reaches the content: its presented extent tracks the reveal mid-flight",
            revealed < -1f && Near(contentDelta, revealed, 0.5f), $"revealed={revealed:0.00} contentDelta={contentDelta:0.00}");
    }
}
