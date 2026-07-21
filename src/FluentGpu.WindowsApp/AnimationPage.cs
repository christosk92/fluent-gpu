using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ===== AnimationPage — the COMPLETE AnimEngine surface, demo by demo =====
// Every capability of the animation engine, grouped into collapsible section cards (themselves running on the
// SizeMode.Reflow primitive): eased tweens over every named curve + authored KeySplines, multi-keyframe tracks with
// loops/delays/stagger, physical springs with velocity-carrying retarget, value-driven (scrubbed) timelines, additive
// composition, the geometry channels (clip reveal, stroke-trim draw-on, presented-size reveal), the declarative
// LayoutTransition family (Position FLIP, ScaleCorrect, Relayout, SizeMode.Reflow smooth reflow with the Trailing
// anchor, EnterExit terminals with per-item stagger), interaction motion (hover/press), and implicit brush
// transitions. The page header teaches the mental model: one engine, per-(node, channel) tracks, three ways to seed.
sealed class AnimationPage : Component
{
    public override Element Render()
    {
        // One open-signal per section, owned by the page so Expand/Collapse all can drive every card. The page itself
        // renders once; only the section components subscribe.
        var tracksOpen = UseSignal(true);
        var revealsOpen = UseSignal(true);
        var layoutOpen = UseSignal(true);
        var interactionOpen = UseSignal(true);
        var hooksOpen = UseSignal(true);
        Signal<bool>[] all = [tracksOpen, revealsOpen, layoutOpen, interactionOpen, hooksOpen];

        // Section anchors for the quick-nav: captured at realize, scrolled to with the SetScrollOffset idiom
        // (write Offset+Target, apply the layout-free -offset content transform, re-render — ItemsView.cs:212-254).
        var anchors = UseRef(new NodeHandle[5]);
        void ScrollToSection(int i)
        {
            var scene = Context.Scene;
            var target = anchors.Value[i];
            if (scene is null || target.IsNull || !scene.IsLive(target)) return;
            var vp = scene.Parent(target);
            while (!vp.IsNull && !scene.HasScroll(vp)) vp = scene.Parent(vp);
            if (vp.IsNull) return;
            ref ScrollState sc = ref scene.ScrollRef(vp);
            if (sc.ContentNode.IsNull || !scene.IsLive(sc.ContentNode)) return;
            float y = scene.AbsoluteRect(target).Y - scene.AbsoluteRect(sc.ContentNode).Y;   // content-space position
            float t = Math.Clamp(y - 8f, 0f, MathF.Max(0f, sc.ContentH - sc.ViewportH));
            sc.OffsetY = t; sc.TargetY = t;
            scene.Paint(sc.ContentNode).LocalTransform = Affine2D.Translation(0f, -t);
            scene.Mark(sc.ContentNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            scene.Mark(vp, NodeFlags.VirtualRangeDirty);
            Context.RequestRerender();
        }

        return GalleryPage.Shell("Animation",
            "The AnimEngine surface, end to end. Tracks are eased keyframes, physical springs, or value-driven timelines on " +
            "a fixed channel set (transform, opacity, clip, stroke trim, presented size, layout size); LayoutTransition makes " +
            "the same engine declarative — set Animate on an element and its layout changes glide, reveal, reflow, or stagger.",
            MentalModelCard(),
            new BoxEl
            {
                Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Margin = new Edges4(0, 4, 0, 0),
                Children =
                [
                    Button.Standard("Expand all", () => { foreach (var s in all) s.Value = true; }),
                    Button.Standard("Collapse all", () => { foreach (var s in all) s.Value = false; }),
                    new BoxEl { Width = 10f },
                    new TextEl("Jump to:") { Size = 12.5f, Color = Tok.TextTertiary },
                    HyperlinkButton.Create("Tracks", () => ScrollToSection(0)),
                    HyperlinkButton.Create("Reveals", () => ScrollToSection(1)),
                    HyperlinkButton.Create("Layout", () => ScrollToSection(2)),
                    HyperlinkButton.Create("Interaction", () => ScrollToSection(3)),
                    HyperlinkButton.Create("Hooks", () => ScrollToSection(4)),
                ],
            },
            SectionCard(h => anchors.Value[0] = h, tracksOpen, "Tracks & easing",
                "The track primitives. A track is one animated value on one (node, channel) pair: an eased tween over any " +
                "named curve or authored KeySpline, a multi-keyframe path with per-segment easing, a physical spring that " +
                "retargets without snapping, a timeline scrubbed by any value source, or an additive layer over a base track.",
                TracksExamples()),
            SectionCard(h => anchors.Value[1] = h, revealsOpen, "Geometry reveals",
                "Channels that reveal pixels WITHOUT moving layout: authored clip rects sweep an edge, stroke trim draws a " +
                "polyline along its own length, and the presented-size reveal eases the drawn extent while layout has " +
                "already landed.",
                RevealsExamples()),
            SectionCard(h => anchors.Value[2] = h, layoutOpen, "Layout transitions — declarative Animate",
                "Set Animate = LayoutTransition on an element and its LAYOUT changes animate by themselves: position " +
                "changes FLIP-glide, size changes scale-correct / re-wrap live / reflow smoothly through real layout " +
                "(SizeMode.Reflow — these section cards run on it), and inserted/removed nodes play enter/exit terminals " +
                "with per-item stagger.",
                LayoutExamples()),
            SectionCard(h => anchors.Value[3] = h, interactionOpen, "Interaction & implicit motion",
                "Motion the engine runs FOR you: hover/press visual states are declared on the element and eased by the " +
                "interaction animator; a logical state flip cross-fades brushes implicitly.",
                InteractionExamples()),
            SectionCard(h => anchors.Value[4] = h, hooksOpen, "Component sugar — the animation hooks",
                "The same engine tracks, seeded on a component's OWN node with one hook call: mount transitions, loops, " +
                "springs, and eased scalar drivers.",
                HooksExamples()));
    }

    // ── the mental model: how an element gets animated, end to end ──────────────────────────────────────────────
    static Element MentalModelCard()
    {
        string[] tree =
        [
            "state / signal write",
            "└─ render → reconcile          your new declaration lands in the scene",
            "   └─ layout                   new rects are solved (a snap, so far)",
            "      ├─ FLIP diff             every node with Animate = LayoutTransition:",
            "      │                        old rect vs new rect → seed / retarget tracks",
            "      ├─ UseLayoutEffect       imperative seeds: Context.Anim.Animate /",
            "      │                        .Keyframes / .Spring / .Drive on a node ref",
            "      └─ AnimEngine.Tick       every frame: advance ALL tracks, then →",
            "         ├─ compositor         LocalTransform · Opacity · clip · stroke trim ·",
            "         │  channels           presented size — pixels move, NO relayout",
            "         └─ SizeMode.Reflow    writes the LAYOUT size + boundary re-solve →",
            "            └─ record → GPU    siblings reflow smoothly (and rigidly)",
        ];
        var treeLines = new Element[tree.Length];
        for (int i = 0; i < tree.Length; i++)
            treeLines[i] = new TextEl(tree[i]) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" };

        return new BoxEl
        {
            Direction = 1, Gap = 10f, Padding = Edges4.All(16),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Corners = Radii.OverlayAll,
            Children =
            [
                new TextEl("How an element animates — the mental model") { Size = 16f, Bold = true, Color = Tok.TextPrimary },
                new TextEl(
                    "There is ONE engine. Everything on this page is a TRACK: one animated value on one (node, channel) pair, " +
                    "advanced every frame at phase 7. You seed tracks in three ways — declare them (Animate = LayoutTransition: " +
                    "the engine diffs your layout changes and animates the difference), seed them imperatively (capture a node " +
                    "ref with OnRealized and call Context.Anim), or use the component hooks (UseTransition/UseSpring/… target " +
                    "the component's own node). Channels compose: translate ∘ rotate ∘ scale + opacity fold into one transform; " +
                    "clip/trim/presented-size reveal pixels without layout; SizeMode.Reflow is the one deliberate exception — " +
                    "it eases the LAYOUT size itself, so neighbours reflow smoothly.")
                    { Size = 12.5f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                new BoxEl
                {
                    Direction = 1, Padding = Edges4.All(12), Corners = Radii.ControlAll,
                    Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Children = treeLines,
                },
                new TextEl("Declarative — the element animates its own layout changes:") { Size = 12.5f, Bold = true, Color = Tok.TextPrimary },
                CodeText.Block("""
                new BoxEl {
                    Height = open ? float.NaN : 0f,          // change the declaration…
                    Animate = new LayoutTransition(          // …and the engine animates the diff
                        TransitionChannels.Size,
                        TransitionDynamics.Tween(333f, Easing.FluentPopOpen),
                        Size: SizeMode.Reflow,               // ease REAL layout (siblings move)
                        Anchor: SizeAnchor.Trailing),
                }
                """),
                new TextEl("Imperative — capture a node and seed any channel:") { Size = 12.5f, Bold = true, Color = Tok.TextPrimary },
                CodeText.Block("""
                var dot = UseRef<NodeHandle>(default);
                // …in the element tree:  new BoxEl { OnRealized = h => dot.Value = h }
                UseLayoutEffect(() =>
                {
                    var anim = Context.Anim;                                  // the engine
                    anim.Animate(dot.Value, AnimChannel.TranslateX, 0f, 186f, 1100f, Easing.Bounce);
                    anim.Spring(dot.Value, AnimChannel.TranslateY, 40f, SpringParams.FromResponse(0.3f, 0.7f));
                }, replay);                                                   // deps re-seed on change
                """),
            ],
        };
    }

    // ── a collapsible section group: the REAL Expander control (controlled IsExpanded signal + content header),
    // customized purely through the generic template-parts door: the HEADER pins 8px under the scroll viewport top
    // (CSS position:sticky) and restyles OPAQUE while stuck (cross-faded by the implicit brush transition), the
    // CONTENT panel gets section padding — no control-specific knobs anywhere. The body reveal is the same
    // SizeMode.Reflow primitive, so collapsing a group reflows the whole page map. ──
    static Element SectionCard(Action<NodeHandle> capture, Signal<bool> open, string title, string blurb, Element[] items)
    {
        var stuck = new Signal<bool>(false);   // the :stuck observable for THIS card's header (same lifetime as `open`)
        return new BoxEl
        {
            Direction = 1,
            Margin = new Edges4(0, 0, 0, 12),
            OnRealized = capture,   // quick-nav anchor
            Children =
            [
                Embed.Comp(() => new Expander
                {
                    IsExpanded = open,
                    Parts = new()
                    {
                        [Expander.PartHeader] = b => b with
                        {
                            ScrollBinds = [ new() { PinTop = 8f, OnFlag = p => stuck.Value = p } ],   // generic sticky bind (engages early, keeps a gap)
                            Fill = stuck.Value ? Tok.FillSolidBase : b.Fill,   // opaque while stuck (reading subscribes)
                            BrushTransitionMs = Motion.ControlFast,            // …and the swap cross-fades
                        },
                        [Expander.PartContent] = p => p with { Padding = Edges4.All(14) },
                    },
                    HeaderContent = new BoxEl
                    {
                        Direction = 1, Gap = 4f, Grow = 1,
                        Margin = new Edges4(0, 12, 0, 12),
                        Children =
                        [
                            new TextEl(title) { Size = 16f, Bold = true, Color = Tok.TextPrimary },
                            new TextEl(blurb) { Size = 12.5f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                        ],
                    },
                    Content = new BoxEl { Direction = 1, Children = items },
                }),
            ],
        };
    }

    static Element[] TracksExamples() =>
    [
        ControlExample.Build("Easing curves — the race",
            Embed.Comp(() => new AnimEasingRace()),
            description: "Every named curve in the engine, racing the same 1100ms translate. The four Fluent curves at the bottom are the real WinUI motion vocabulary.",
            code: """
            var anim = Context.Anim;                     // the engine (RenderContext.Anim)
            anim.Animate(dot, AnimChannel.TranslateX,    // eased two-point tween
                0f, 186f, 1100f, Easing.Bounce);         // any named curve
            """),
        ControlExample.Build("Authored KeySpline (EasingSpec.CubicBezier)",
            Embed.Comp(() => new AnimKeySplinePlayground()),
            description: "Any cubic-bezier easing is expressible, not just the named curves — this is how WinUI storyboard KeySplines port 1:1 (the Expander collapse is KeySpline 1,1,0,1).",
            code: """
            anim.Animate(dot, AnimChannel.TranslateX, 0f, 200f, 900f,
                EasingSpec.CubicBezier(x1, y1, x2, y2));   // a WinUI KeySpline, verbatim
            """),
        ControlExample.Build("Channels — one node, every axis",
            Embed.Comp(() => new AnimChannelsPlayground()),
            description: "Transform channels compose into one LocalTransform (translate ∘ rotate ∘ scale) plus opacity — each button plays a there-and-back keyframe pair on a single channel of the same tile.",
            code: """
            anim.Keyframes(tile, AnimChannel.Rotation,
                [new(0f, 0f), new(0.5f, 360f, Easing.FluentStandard), new(1f, 0f, Easing.FluentPopOpen)],
                900f);
            """),
        ControlExample.Build("Multi-keyframe paths, loops & stagger (DelayMs)",
            Embed.Comp(() => new AnimKeyframesStagger()),
            description: "A four-keyframe path with a DIFFERENT easing per segment, replayed across three dots with 0/110/220ms seed delays — stagger is an engine primitive, not a timer. The right dot pulses on an infinite loop.",
            code: """
            Keyframe[] path = [new(0f, 0f), new(0.45f, 150f, Easing.Back),
                               new(0.7f, 70f, Easing.EaseOut), new(1f, 150f, Easing.Bounce)];
            anim.Keyframes(dotA, AnimChannel.TranslateX, path, 1400f);
            anim.Keyframes(dotB, AnimChannel.TranslateX, path, 1400f, delayMs: 110f);
            anim.Keyframes(dotC, AnimChannel.TranslateX, path, 1400f, delayMs: 220f);
            anim.Keyframes(pulse, AnimChannel.ScaleX, [new(0f, 1f), new(0.5f, 1.3f), new(1f, 1f)], 1200f, loop: true);
            """),
        ControlExample.Build("Springs — velocity-carrying retarget",
            Embed.Comp(() => new AnimSpringLab()),
            description: "Click Toggle repeatedly MID-FLIGHT: the tween restarts from its keyframe, the spring keeps position AND velocity (the iOS/Compose handoff). Tune the physics live.",
            code: """
            anim.Spring(dot, AnimChannel.TranslateX, target,
                SpringParams.FromResponse(response, dampingRatio));   // retarget = no snap, velocity carries
            """),
        ControlExample.Build("Driven timeline — scrub by value",
            Embed.Comp(() => new AnimDrivenScrub()),
            description: "A track's clock can be any value source instead of wall time (scroll offset, playback position, a slider). Three channels of the same dot ride one scrubbed timeline.",
            code: """
            int src = anim.Clocks.Register(() => t.Value);             // any Func<float>
            anim.Drive(dot, AnimChannel.TranslateX, [new(0f, 0f), new(1f, 220f, Easing.Linear)], src, 0f, 1f);
            anim.Drive(dot, AnimChannel.Rotation,   [new(0f, 0f), new(1f, 360f, Easing.Linear)], src, 0f, 1f);
            anim.Drive(dot, AnimChannel.Opacity,    [new(0f, 1f), new(0.5f, 0.35f), new(1f, 1f)], src, 0f, 1f);
            """),
        ControlExample.Build("Additive composition (CompositeOp.Add)",
            Embed.Comp(() => new AnimAdditive()),
            description: "Per (node, channel) tracks compose like CSS animation-composition: a slow Replace drift plus an additive wobble layer on the SAME channel.",
            code: """
            anim.Keyframes(dot, AnimChannel.TranslateX,
                [new(0f, 0f), new(0.5f, 170f, Easing.EaseInOut), new(1f, 0f, Easing.EaseInOut)], 4000f, loop: true);
            anim.Keyframes(dot, AnimChannel.TranslateX,
                [new(0f, -6f), new(0.5f, 6f, Easing.Sine), new(1f, -6f, Easing.Sine)], 260f, loop: true,
                composite: CompositeOp.Add);                            // layers, does not replace
            """),
    ];

    static Element[] RevealsExamples() =>
    [
        ControlExample.Build("Clip reveal (ClipL/T/R/B)",
            Embed.Comp(() => new AnimClipReveal()),
            description: "The authored clip-rect channels sweep one edge while the surface's rounded corners stay round (the WinUI composition-clip look). A settled clip clears its override automatically.",
            code: """
            anim.Animate(card, AnimChannel.ClipR, 0f, width, 450f, Easing.FluentPopOpen);   // reveal left → right
            anim.Animate(card, AnimChannel.ClipT, height, 0f, 450f, Easing.FluentPopOpen);  // reveal bottom → top
            """),
        ControlExample.Build("Stroke-trim draw-on (StrokeTrimStart/End)",
            Embed.Comp(() => new AnimDrawOn()),
            description: "An analytic polyline revealed along its own length — the CheckBox checkmark's exact mechanism, with the WinUI AnimatedAccept spline.",
            code: """
            Context.UseKeyframes(AnimChannel.StrokeTrimEnd,
                [new(0f, 0f, Easing.Linear), new(1f, 1f, EasingSpec.CubicBezier(0.55f, 0f, 0f, 1f))], 800f, deps);
            return new PolylineStrokeEl { P0 = ..., P1 = ..., P2 = ..., P3 = ..., PointCount = 4, Thickness = 3f };
            """),
        ControlExample.Build("Presented-size reveal (SizeMode.Reveal)",
            Embed.Comp(() => new AnimPresentedReveal()),
            description: "Layout lands at the final size IMMEDIATELY (the row below snaps); only the drawn extent eases — crisp, compositor-only. Compare with smooth reflow further down.",
            code: """
            new BoxEl { Height = open ? 120f : 48f,
                        Animate = LayoutTransition.BoundsT(SizeMode.Reveal) }
            """),
    ];

    static Element[] LayoutExamples() =>
    [
        ControlExample.Build("Position FLIP — slide between layout slots",
            Embed.Comp(() => new AnimFlipSlide()),
            description: "The element re-lays-out instantly; the engine projects the residual and plays it back. Projection is PARENT-RELATIVE: only the node's own slot change animates — an ancestor reflow never re-triggers it. Spring (top) retargets velocity-continuously; tween (bottom) restarts.",
            code: """
            new BoxEl { Animate = LayoutTransition.Slide }                         // spring dynamics (default)
            new BoxEl { Animate = new LayoutTransition(TransitionChannels.Position,
                            TransitionDynamics.Tween(420f, Easing.FluentStandard)) }
            """),
        ControlExample.Build("SizeMode.ScaleCorrect — chrome projection",
            Embed.Comp(() => new AnimScaleCorrect()),
            description: "A size change becomes a GPU scale that springs toward 1 (Framer-Motion projection). Cheap, but it distorts text/borders mid-flight — chrome only.",
            code: """
            new BoxEl { Width = wide ? 240f : 130f,
                        Animate = LayoutTransition.BoundsT(SizeMode.ScaleCorrect) }
            """),
        ControlExample.Build("SizeMode.Relayout — live re-wrap",
            Embed.Comp(() => new AnimRelayout()),
            description: "The subtree re-solves at the interpolated size every tick, so text re-wraps LIVE while the width animates — correct, costs scoped layout.",
            code: """
            new BoxEl { Width = wide ? 300f : 160f,
                        Animate = LayoutTransition.BoundsT(SizeMode.Relayout) }
            """),
        ControlExample.Build("SizeMode.Reflow — smooth reflow (the Expander's engine)",
            Embed.Comp(() => new AnimReflow()),
            description: "The one DELIBERATE divergence from WinUI: the interpolated size runs through REAL layout each tick (boundary-scoped), so everything below eases instead of snapping — and rides RIGIDLY (watch the ToggleSwitch pill stay locked to its track). SizeAnchor.Trailing keeps the panel's bottom edge on the reveal edge; ExitDynamics gives the asymmetric WinUI 333/167ms timing.",
            code: """
            static readonly LayoutTransition Reflow = new(TransitionChannels.Size,
                TransitionDynamics.Tween(333f, Easing.FluentPopOpen),                       // ExpandDown KeySpline 0,0,0,1
                Size: SizeMode.Reflow,
                ExitDynamics: TransitionDynamics.Tween(167f, EasingSpec.CubicBezier(1f, 1f, 0f, 1f)),  // CollapseUp
                Anchor: SizeAnchor.Trailing);                                               // slide out from under the header

            new BoxEl { ClipToBounds = true, Height = open ? float.NaN : 0f, Animate = Reflow, Children = [panel] }
            """),
        ControlExample.Build("Enter / Exit terminals + per-item stagger",
            Embed.Comp(() => new AnimEnterExit()),
            description: "An inserted node animates FROM its enter terminal (offset/scale/opacity); a removed node becomes an exit ORPHAN that stays alive until its tracks settle. DelayMs staggers a batch.",
            code: """
            new BoxEl { Animate = new LayoutTransition(
                TransitionChannels.Position | TransitionChannels.Opacity,
                TransitionDynamics.Tween(220f, Easing.FluentDecelerate),
                Enter: new EnterExit(Dy: 18f, Opacity: 0f, Active: true),
                Exit:  new EnterExit(Sx: 0.8f, Sy: 0.8f, Opacity: 0f, Active: true),
                DelayMs: index * 40f) }
            """),
    ];

    static Element[] InteractionExamples() =>
    [
        ControlExample.Build("Hover / press motion (InteractionAnimator)",
            Embed.Comp(() => new AnimInteraction()),
            description: "Visual states are DECLARED, not animated by hand: hover/press fills, scales, durations and easings on the element — the interaction animator eases the progress and the recorder cross-fades.",
            code: """
            new BoxEl { HoverFill = ..., PressedFill = ...,
                        HoverScale = 1.06f, PressScale = 0.94f,
                        HoverDurationMs = 167f, PressDurationMs = 83f,
                        HoverEasing = Easing.FluentPopOpen }
            """),
        ControlExample.Build("Implicit brush transition (BrushTransitionMs)",
            Embed.Comp(() => new AnimBrushTransition()),
            description: "A logical state flip cross-fades the surface brushes over the given duration — WinUI's implicit color animation, one field.",
            code: """
            new BoxEl { Fill = palette[i], BrushTransitionMs = 250f }   // click: i++ → 250ms cross-fade
            """),
        ControlExample.Build("Interaction recipes — the app-authoring surface (InteractionRecipe)",
            Embed.Comp(() => new AnimRecipeSurfaces()),
            description: "One .Interactive(recipe) modifier packages a whole interactive surface — the brush ramp (Fill/Hover/" +
                "Pressed + BrushTransitionMs) AND the motion (WhileHover/WhilePressed scale on a token). Theme-live presets read " +
                "Tok, so a theme flip re-resolves them. App-authoring only — framework controls keep their own WinUI ramps.",
            code: """
            // pure with-expansion at construction (zero closures, zero per-frame alloc):
            new BoxEl { OnClick = go }.Interactive(Interaction.Subtle);   // transparent → subtle hover/press
            new BoxEl { OnClick = go }.Interactive(Interaction.Card);     // card fills + stroke + 0.985 spring press
            new BoxEl { OnClick = go }.Interactive(Interaction.AccentGhost);
            """),
    ];

    static Element[] HooksExamples() =>
    [
        ControlExample.Build("UseTransition / UseKeyframes / UseSpring / UseAnimatedValue",
            Embed.Comp(() => new AnimHooksDemo()),
            description: "Hooks seed the same engine tracks on the component's OWN node: a mount transition, a looping pulse, a spring toggle, and an eased scalar driving a color lerp.",
            code: """
            UseTransition(AnimChannel.Opacity, 0f, 1f, 500f, Easing.EaseOut, "mount");
            UseKeyframes(AnimChannel.ScaleX, [new(0f, 1f), new(0.5f, 1.25f), new(1f, 1f)], 1400f, loop: true);
            UseSpring(AnimChannel.ScaleX, on ? 1.2f : 1f, SpringParams.FromResponse(0.3f, 0.5f), on);
            float t = UseAnimatedValue(on ? 1f : 0f, 300f);   // an eased 0..1 driver for anything (here: a color)
            """),
    ];
}

// ── Tracks & easing ───────────────────────────────────────────────────────────────────────────────────────────────

sealed class AnimEasingRace : Component
{
    static readonly (string Name, Easing Curve)[] Curves =
    [
        ("Linear", Easing.Linear), ("EaseIn", Easing.EaseIn), ("EaseOut", Easing.EaseOut),
        ("EaseInOut", Easing.EaseInOut), ("Sine", Easing.Sine), ("Quad", Easing.Quad), ("Cubic", Easing.Cubic),
        ("Expo", Easing.Expo), ("Back", Easing.Back), ("Elastic", Easing.Elastic), ("Bounce", Easing.Bounce),
        ("FluentStandard", Easing.FluentStandard), ("FluentDecelerate", Easing.FluentDecelerate),
        ("FluentAccelerate", Easing.FluentAccelerate), ("FluentPopOpen", Easing.FluentPopOpen),
    ];

    public override Element Render()
    {
        var (replay, setReplay) = UseState(0);
        var dots = UseRef(new NodeHandle[Curves.Length]);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;
            for (int i = 0; i < Curves.Length; i++)
            {
                var n = dots.Value[i];
                if (n.IsNull || !scene.IsLive(n)) continue;
                anim.Animate(n, AnimChannel.TranslateX, 0f, 186f, 1100f, Curves[i].Curve);
            }
        }, replay);

        var rows = new List<Element>(Curves.Length + 1);
        for (int i = 0; i < Curves.Length; i++)
        {
            int idx = i;
            rows.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, Height = 20f,
                Children =
                [
                    new TextEl(Curves[i].Name) { Size = 12f, Color = Tok.TextSecondary, Width = 118f },
                    new BoxEl
                    {
                        Width = 200f, Height = 6f, Corners = Radii.Circle(6f), Fill = Tok.FillSubtleSecondary,
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = 14f, Height = 14f, Corners = Radii.Circle(14f), Fill = Tok.AccentDefault,
                                OnRealized = h => dots.Value[idx] = h,
                            },
                        ],
                    },
                ],
            });
        }
        rows.Add(Button.Standard("Replay", () => setReplay(replay + 1)));
        return new BoxEl { Direction = 1, Gap = 4f, Children = rows.ToArray() };
    }
}

sealed class AnimKeySplinePlayground : Component
{
    public override Element Render()
    {
        var (replay, setReplay) = UseState(0);
        var x1 = UseFloatSignal(1f);
        var y1 = UseFloatSignal(1f);
        var x2 = UseFloatSignal(0f);
        var y2 = UseFloatSignal(1f);
        var dot = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || dot.Value.IsNull) return;
            anim.Animate(dot.Value, AnimChannel.TranslateX, 0f, 200f, 900f,
                EasingSpec.CubicBezier(Math.Clamp(x1.Peek(), 0f, 1f), y1.Peek(), Math.Clamp(x2.Peek(), 0f, 1f), y2.Peek()));
        }, replay);

        Element Knob(string label, FloatSignal sig) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
            Children =
            [
                new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = 22f },
                Slider.Bind(sig, width: 150f),
            ],
        };

        return new BoxEl
        {
            Direction = 1, Gap = 10f,
            Children =
            [
                new BoxEl
                {
                    Width = 214f, Height = 6f, Corners = Radii.Circle(6f), Fill = Tok.FillSubtleSecondary,
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [new BoxEl { Width = 14f, Height = 14f, Corners = Radii.Circle(14f), Fill = Tok.AccentDefault, OnRealized = h => dot.Value = h }],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 18f,
                    Children = [Knob("x1", x1), Knob("y1", y1)],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 18f,
                    Children = [Knob("x2", x2), Knob("y2", y2)],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Standard("Replay", () => setReplay(replay + 1)),
                        GalleryPage.LiveText(() => $"cubic-bezier({x1.Value:0.00}, {y1.Value:0.00}, {x2.Value:0.00}, {y2.Value:0.00})"),
                    ],
                },
            ],
        };
    }
}

sealed class AnimChannelsPlayground : Component
{
    public override Element Render()
    {
        var tile = UseRef<NodeHandle>(default);

        void Play(AnimChannel ch, float rest, float target, float ms, Easing outE = Easing.FluentStandard)
        {
            if (Context.Anim is not { } anim || tile.Value.IsNull) return;
            anim.Keyframes(tile.Value, ch,
                [new(0f, rest), new(0.5f, target, Easing.FluentPopOpen), new(1f, rest, outE)], ms);
        }

        return new BoxEl
        {
            Direction = 0, Gap = 18f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = 120f, Height = 90f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 56f, Height = 56f, Corners = Radii.OverlayAll, Fill = Tok.AccentDefault,
                            OnRealized = h => tile.Value = h,
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Gap = 6f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = 6f, Children =
                        [
                            Button.Standard("Move X", () => Play(AnimChannel.TranslateX, 0f, 44f, 700f)),
                            Button.Standard("Move Y", () => Play(AnimChannel.TranslateY, 0f, -22f, 700f)),
                            Button.Standard("Scale", () => Play(AnimChannel.ScaleX, 1f, 1.35f, 700f)),
                        ] },
                        new BoxEl { Direction = 0, Gap = 6f, Children =
                        [
                            Button.Standard("Rotate", () => Play(AnimChannel.Rotation, 0f, 360f, 900f)),
                            Button.Standard("Fade", () => Play(AnimChannel.Opacity, 1f, 0.15f, 700f)),
                        ] },
                    ],
                },
            ],
        };
    }
}

sealed class AnimKeyframesStagger : Component
{
    static readonly Keyframe[] Path =
    [
        new(0f, 0f), new(0.45f, 150f, Easing.Back), new(0.7f, 70f, Easing.EaseOut), new(1f, 150f, Easing.Bounce),
    ];

    public override Element Render()
    {
        var (replay, setReplay) = UseState(0);
        var dots = UseRef(new NodeHandle[3]);
        var pulse = UseRef<NodeHandle>(default);
        var pulseSeeded = UseRef(false);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim) return;
            for (int i = 0; i < 3; i++)
                if (!dots.Value[i].IsNull)
                    anim.Keyframes(dots.Value[i], AnimChannel.TranslateX, Path, 1400f, delayMs: i * 110f);
            if (!pulseSeeded.Value && !pulse.Value.IsNull)
            {
                pulseSeeded.Value = true;
                anim.Keyframes(pulse.Value, AnimChannel.ScaleX, [new(0f, 1f), new(0.5f, 1.3f, Easing.EaseInOut), new(1f, 1f, Easing.EaseInOut)], 1200f, loop: true);
                anim.Keyframes(pulse.Value, AnimChannel.ScaleY, [new(0f, 1f), new(0.5f, 1.3f, Easing.EaseInOut), new(1f, 1f, Easing.EaseInOut)], 1200f, loop: true);
            }
        }, replay);

        var lanes = new Element[3];
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            lanes[i] = new BoxEl
            {
                Width = 190f, Height = 16f, Direction = 0, AlignItems = FlexAlign.Center,
                Children = [new BoxEl { Width = 14f, Height = 14f, Corners = Radii.Circle(14f), Fill = Tok.AccentDefault, OnRealized = h => dots.Value[idx] = h }],
            };
        }

        return new BoxEl
        {
            Direction = 0, Gap = 24f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Direction = 1, Gap = 8f, Children = lanes },
                new BoxEl { Width = 40f, Height = 40f, Corners = Radii.OverlayAll, Fill = Tok.FillSubtleSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, OnRealized = h => pulse.Value = h },
                Button.Standard("Replay", () => setReplay(replay + 1)),
            ],
        };
    }
}

sealed class AnimSpringLab : Component
{
    public override Element Render()
    {
        var (on, setOn) = UseState(false);
        var resp = UseFloatSignal(0.45f);     // response 0..1 → seconds (clamped below)
        var damp = UseFloatSignal(0.55f);     // damping ratio 0..1 → 0.2..1.2
        var springDot = UseRef<NodeHandle>(default);
        var tweenDot = UseRef<NodeHandle>(default);
        var armed = UseRef(false);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim) return;
            if (!armed.Value) { armed.Value = true; return; }   // first mount = rest, no motion
            float to = on ? 210f : 0f;
            if (!springDot.Value.IsNull)
                anim.Spring(springDot.Value, AnimChannel.TranslateX, to,
                    SpringParams.FromResponse(MathF.Max(0.12f, resp.Peek() * 0.8f), 0.2f + damp.Peek()));
            if (!tweenDot.Value.IsNull)
                anim.Animate(tweenDot.Value, AnimChannel.TranslateX, on ? 0f : 210f, to, 450f, Easing.FluentStandard);
        }, on);

        Element Lane(string label, Action<NodeHandle> capture, ColorF fill) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f,
            Children =
            [
                new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = 50f },
                new BoxEl
                {
                    Width = 226f, Height = 8f, Corners = Radii.Circle(8f), Fill = Tok.FillSubtleSecondary,
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [new BoxEl { Width = 16f, Height = 16f, Corners = Radii.Circle(16f), Fill = fill, OnRealized = capture }],
                },
            ],
        };

        return new BoxEl
        {
            Direction = 1, Gap = 10f,
            Children =
            [
                Lane("spring", h => springDot.Value = h, Tok.AccentDefault),
                Lane("tween", h => tweenDot.Value = h, Tok.TextSecondary),
                new BoxEl
                {
                    Direction = 0, Gap = 16f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Accent("Toggle", () => setOn(!on)),
                        Slider.Bind(resp, width: 110f, header: "response"),
                        Slider.Bind(damp, width: 110f, header: "damping"),
                        GalleryPage.LiveText(() => $"{MathF.Max(0.12f, resp.Value * 0.8f):0.00}s · ζ {0.2f + damp.Value:0.00}"),
                    ],
                },
            ],
        };
    }
}

sealed class AnimDrivenScrub : Component
{
    public override Element Render()
    {
        var t = UseFloatSignal(0f);
        var dot = UseRef<NodeHandle>(default);
        var wired = UseRef(false);

        UseLayoutEffect(() =>
        {
            if (wired.Value || Context.Anim is not { } anim || dot.Value.IsNull) return;
            wired.Value = true;
            int src = anim.Clocks.Register(() => t.Value);
            anim.Drive(dot.Value, AnimChannel.TranslateX, [new(0f, 0f), new(1f, 220f, Easing.Linear)], src, 0f, 1f);
            anim.Drive(dot.Value, AnimChannel.Rotation, [new(0f, 0f), new(1f, 360f, Easing.Linear)], src, 0f, 1f);
            anim.Drive(dot.Value, AnimChannel.Opacity, [new(0f, 1f), new(0.5f, 0.35f, Easing.Linear), new(1f, 1f, Easing.Linear)], src, 0f, 1f);
        }, 0);

        return new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                new BoxEl
                {
                    Width = 250f, Height = 34f, Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [new BoxEl { Width = 26f, Height = 26f, Corners = Radii.ControlAll, Fill = Tok.AccentDefault, OnRealized = h => dot.Value = h }],
                },
                Slider.Bind(t, width: 250f, header: "timeline"),
            ],
        };
    }
}

sealed class AnimAdditive : Component
{
    public override Element Render()
    {
        var dot = UseRef<NodeHandle>(default);
        var wired = UseRef(false);

        UseLayoutEffect(() =>
        {
            if (wired.Value || Context.Anim is not { } anim || dot.Value.IsNull) return;
            wired.Value = true;
            anim.Keyframes(dot.Value, AnimChannel.TranslateX,
                [new(0f, 0f), new(0.5f, 170f, Easing.EaseInOut), new(1f, 0f, Easing.EaseInOut)], 4000f, loop: true);
            anim.Keyframes(dot.Value, AnimChannel.TranslateX,
                [new(0f, -6f), new(0.5f, 6f, Easing.Sine), new(1f, -6f, Easing.Sine)], 260f, loop: true,
                composite: CompositeOp.Add);
        }, 0);

        return new BoxEl
        {
            Width = 220f, Height = 30f, Direction = 0, AlignItems = FlexAlign.Center,
            Children = [new BoxEl { Width = 18f, Height = 18f, Corners = Radii.Circle(18f), Fill = Tok.AccentDefault, OnRealized = h => dot.Value = h }],
        };
    }
}

// ── Geometry reveals ─────────────────────────────────────────────────────────────────────────────────────────────

sealed class AnimClipReveal : Component
{
    const float W = 220f, H = 72f;

    public override Element Render()
    {
        var card = UseRef<NodeHandle>(default);

        void Reveal(AnimChannel ch, float from, float to)
        {
            if (Context.Anim is not { } anim || card.Value.IsNull) return;
            anim.Animate(card.Value, ch, from, to, 450f, Easing.FluentPopOpen);
        }

        return new BoxEl
        {
            Direction = 0, Gap = 18f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = W, Height = H, Corners = Radii.OverlayAll, Fill = Tok.AccentDefault,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    OnRealized = h => card.Value = h,
                    Children = [new TextEl("Surface") { Size = 14f, Color = Tok.TextOnAccentPrimary }],
                },
                new BoxEl
                {
                    Direction = 1, Gap = 6f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = 6f, Children =
                        [
                            Button.Standard("Left → right", () => Reveal(AnimChannel.ClipR, 0f, W)),
                            Button.Standard("Right → left", () => Reveal(AnimChannel.ClipL, W, 0f)),
                        ] },
                        new BoxEl { Direction = 0, Gap = 6f, Children =
                        [
                            Button.Standard("Top → bottom", () => Reveal(AnimChannel.ClipB, 0f, H)),
                            Button.Standard("Bottom → top", () => Reveal(AnimChannel.ClipT, H, 0f)),
                        ] },
                    ],
                },
            ],
        };
    }
}

sealed class AnimDrawOn : Component
{
    public override Element Render()
    {
        var (replay, setReplay) = UseState(0);
        var replaySig = UseSignal(0);
        if (replaySig.Peek() != replay) replaySig.Value = replay;
        return new BoxEl
        {
            Direction = 0, Gap = 20f, AlignItems = FlexAlign.Center,
            Children =
            [
                Embed.Comp(() => new AnimDrawOnStroke { Replay = replaySig }),
                Button.Standard("Replay", () => setReplay(replay + 1)),
            ],
        };
    }
}

sealed class AnimDrawOnStroke : Component
{
    public required Signal<int> Replay;

    public override Element Render()
    {
        int seed = Replay.Value;   // subscribe: a replay re-renders this component and re-seeds the draw-on
        Context.UseKeyframes(AnimChannel.StrokeTrimEnd,
            [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 1f, EasingSpec.CubicBezier(0.55f, 0f, 0f, 1f))],
            800f, false, seed);

        const float S = 64f;
        return new PolylineStrokeEl
        {
            Width = S, Height = S,
            P0 = new Point2(0.05f * S, 0.75f * S),
            P1 = new Point2(0.35f * S, 0.25f * S),
            P2 = new Point2(0.62f * S, 0.80f * S),
            P3 = new Point2(0.95f * S, 0.20f * S),
            PointCount = 4,
            Color = Tok.AccentDefault,
            Thickness = 3f,
            RoundCaps = true,
        };
    }
}

sealed class AnimPresentedReveal : Component
{
    public override Element Render()
    {
        var (open, setOpen) = UseState(false);
        return new BoxEl
        {
            Direction = 1, Gap = 10f,
            Children =
            [
                Button.Standard(open ? "Shrink" : "Grow", () => setOpen(!open)),
                new BoxEl
                {
                    Width = 220f, Height = open ? 120f : 48f,
                    Corners = Radii.OverlayAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Animate = LayoutTransition.BoundsT(SizeMode.Reveal),
                    Padding = Edges4.All(12),
                    Children = [new TextEl("Presented extent eases; layout already landed.") { Size = 12.5f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap }],
                },
                new TextEl("← this row SNAPS (layout is instant; only pixels ease)") { Size = 12f, Color = Tok.TextTertiary },
            ],
        };
    }
}

// ── Layout transitions ────────────────────────────────────────────────────────────────────────────────────────────

sealed class AnimFlipSlide : Component
{
    static readonly LayoutTransition TweenSlide = new(TransitionChannels.Position,
        TransitionDynamics.Tween(420f, Easing.FluentStandard));

    public override Element Render()
    {
        var (right, setRight) = UseState(false);

        Element Lane(LayoutTransition spec, ColorF fill) => new BoxEl
        {
            Width = 260f, Height = 26f, Direction = 0,
            Justify = right ? FlexJustify.End : FlexJustify.Start,
            AlignItems = FlexAlign.Center,
            Fill = Tok.FillSubtleSecondary, Corners = Radii.Circle(26f), Padding = new Edges4(4, 0, 4, 0),
            Children = [new BoxEl { Width = 18f, Height = 18f, Corners = Radii.Circle(18f), Fill = fill, Animate = spec }],
        };

        return new BoxEl
        {
            Direction = 1, Gap = 8f,
            Children =
            [
                Lane(LayoutTransition.Slide, Tok.AccentDefault),
                Lane(TweenSlide, Tok.TextSecondary),
                Button.Standard(right ? "Slide left" : "Slide right", () => setRight(!right)),
            ],
        };
    }
}

sealed class AnimScaleCorrect : Component
{
    public override Element Render()
    {
        var (wide, setWide) = UseState(false);
        return new BoxEl
        {
            Direction = 1, Gap = 10f, AlignItems = FlexAlign.Start,
            Children =
            [
                Button.Standard(wide ? "Narrow" : "Widen", () => setWide(!wide)),
                new BoxEl
                {
                    Width = wide ? 240f : 130f, Height = 64f,
                    Corners = Radii.OverlayAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Animate = LayoutTransition.BoundsT(SizeMode.ScaleCorrect),
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl("chrome scales") { Size = 12.5f, Color = Tok.TextSecondary }],
                },
            ],
        };
    }
}

sealed class AnimRelayout : Component
{
    public override Element Render()
    {
        var (wide, setWide) = UseState(false);
        return new BoxEl
        {
            Direction = 1, Gap = 10f, AlignItems = FlexAlign.Start,
            Children =
            [
                Button.Standard(wide ? "Narrow" : "Widen", () => setWide(!wide)),
                new BoxEl
                {
                    Width = wide ? 300f : 160f,
                    Corners = Radii.OverlayAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Animate = LayoutTransition.BoundsT(SizeMode.Relayout),
                    Padding = Edges4.All(12),
                    Children =
                    [
                        new TextEl("The quick brown fox jumps over the lazy dog — and the line breaks move every tick while the width animates.")
                            { Size = 12.5f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                    ],
                },
            ],
        };
    }
}

sealed class AnimReflow : Component
{
    static readonly LayoutTransition Reflow = new(TransitionChannels.Size,
        TransitionDynamics.Tween(333f, Easing.FluentPopOpen),
        Size: SizeMode.Reflow,
        ExitDynamics: TransitionDynamics.Tween(167f, EasingSpec.CubicBezier(1f, 1f, 0f, 1f)),
        Anchor: SizeAnchor.Trailing);

    public override Element Render()
    {
        var (open, setOpen) = UseState(false);
        var (on, setOn) = UseState(true);
        return new BoxEl
        {
            Direction = 1, Gap = 10f, AlignItems = FlexAlign.Start,
            Children =
            [
                Button.Accent(open ? "Collapse" : "Expand", () => setOpen(!open)),
                new BoxEl
                {
                    Direction = 1, ClipToBounds = true,
                    Width = 280f,
                    Height = open ? float.NaN : 0f,
                    Animate = Reflow,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Padding = Edges4.All(12), Gap = 6f,
                            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                            Corners = Radii.OverlayAll,
                            Children =
                            [
                                new TextEl("Real layout, eased") { Size = 13f, Bold = true, Color = Tok.TextPrimary },
                                new TextEl("The space itself animates through the WinUI curves — everything below reflows smoothly and rigidly.")
                                    { Size = 12.5f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                            ],
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 14f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        ToggleSwitch.Create(on, () => setOn(!on)),
                        new TextEl("← rides the reveal rigidly (pill stays locked to its track)") { Size = 12f, Color = Tok.TextTertiary },
                    ],
                },
            ],
        };
    }
}

sealed class AnimEnterExit : Component
{
    static LayoutTransition ItemSpec(int index) => new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.FluentDecelerate),
        Enter: new EnterExit(Dy: 18f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: 0.8f, Sy: 0.8f, Opacity: 0f, Active: true),
        DelayMs: index * 40f);

    public override Element Render()
    {
        var (count, setCount) = UseState(3);
        var tiles = new Element[count];
        for (int i = 0; i < count; i++)
        {
            tiles[i] = new BoxEl
            {
                Width = 44f, Height = 44f, Corners = Radii.ControlAll,
                Fill = Tok.AccentDefault, Animate = ItemSpec(i),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl((i + 1).ToString()) { Size = 13f, Color = Tok.TextOnAccentPrimary }],
            };
        }
        return new BoxEl
        {
            Direction = 1, Gap = 10f,
            Children =
            [
                new BoxEl { Direction = 0, Gap = 8f, MinHeight = 48f, AlignItems = FlexAlign.Center, Children = tiles },
                new BoxEl
                {
                    Direction = 0, Gap = 8f,
                    Children =
                    [
                        Button.Standard("Add", () => setCount(Math.Min(count + 1, 8))),
                        Button.Standard("Remove", () => setCount(Math.Max(count - 1, 0))),
                        Button.Standard("Reset (staggered)", () => setCount(count == 0 ? 6 : 0)),
                    ],
                },
            ],
        };
    }
}

// ── Interaction & implicit motion ────────────────────────────────────────────────────────────────────────────────

sealed class AnimInteraction : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 180f, Height = 72f,
        Corners = Radii.OverlayAll,
        Fill = Tok.FillCardSecondary,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        HoverScale = 1.06f, PressScale = 0.94f,
        HoverDurationMs = 167f, PressDurationMs = 83f,
        HoverEasing = Easing.FluentPopOpen,
        OnClick = () => { },                                  // interactive → owns hover/press progress
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl("hover · press me") { Size = 13f, Color = Tok.TextPrimary }],
    };
}

sealed class AnimBrushTransition : Component
{
    static readonly ColorF[] Palette =
    [
        ColorF.FromRgba(0x4C, 0xC2, 0xFF), ColorF.FromRgba(0xE7, 0x4C, 0x8C),
        ColorF.FromRgba(0x6C, 0xCB, 0x5F), ColorF.FromRgba(0xFF, 0xB9, 0x00),
    ];

    public override Element Render()
    {
        var (i, setI) = UseState(0);
        return new BoxEl
        {
            Direction = 0, Gap = 14f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Width = 96f, Height = 56f, Corners = Radii.OverlayAll, Fill = Palette[i % Palette.Length], BrushTransitionMs = 250f },
                Button.Standard("Next color", () => setI(i + 1)),
            ],
        };
    }
}

// The app-authoring InteractionRecipe surface: one .Interactive(recipe) writes the brush ramp + While* motion. The
// presets read Tok live, so they follow a theme flip. Framework controls keep their own hand ramps — these are for apps.
sealed class AnimRecipeSurfaces : Component
{
    static Element Surface(string label, InteractionRecipe recipe) => new BoxEl
    {
        Width = 150f, Height = 68f, Corners = Radii.OverlayAll,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        OnClick = static () => { },
        Children = [new TextEl(label) { Size = 12.5f, Color = Tok.TextPrimary }],
    }.Interactive(recipe);

    public override Element Render() => new BoxEl
    {
        Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, Wrap = true,
        Children =
        [
            Surface("Subtle", Interaction.Subtle),
            Surface("Card · spring press", Interaction.Card),
            Surface("Accent ghost", Interaction.AccentGhost),
        ],
    };
}

// ── Hooks sugar ──────────────────────────────────────────────────────────────────────────────────────────────────

sealed class AnimHooksDemo : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 0, Gap = 22f, AlignItems = FlexAlign.Center,
        Children =
        [
            Embed.Comp(() => new AnimHooksFadeIn()),
            Embed.Comp(() => new AnimHooksPulse()),
            Embed.Comp(() => new AnimHooksSpring()),
            Embed.Comp(() => new AnimHooksColor()),
        ],
    };
}

sealed class AnimHooksFadeIn : Component
{
    public override Element Render()
    {
        UseTransition(AnimChannel.Opacity, 0f, 1f, 500f, Easing.EaseOut, "mount");
        UseTransition(AnimChannel.TranslateY, 16f, 0f, 500f, Easing.EaseOut, "mount");
        return new BoxEl
        {
            Width = 110f, Height = 48f, Corners = Radii.OverlayAll,
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [new TextEl("faded in") { Size = 12.5f, Color = Tok.TextPrimary }],
        };
    }
}

sealed class AnimHooksPulse : Component
{
    public override Element Render()
    {
        UseKeyframes(AnimChannel.ScaleX, [new(0f, 1f, Easing.EaseInOut), new(0.5f, 1.25f, Easing.EaseInOut), new(1f, 1f, Easing.EaseInOut)], 1400f, loop: true, "pulse");
        UseKeyframes(AnimChannel.ScaleY, [new(0f, 1f, Easing.EaseInOut), new(0.5f, 1.25f, Easing.EaseInOut), new(1f, 1f, Easing.EaseInOut)], 1400f, loop: true, "pulse");
        return new BoxEl { Width = 44f, Height = 44f, Fill = Tok.AccentDefault, Corners = Radii.OverlayAll };
    }
}

sealed class AnimHooksSpring : Component
{
    public override Element Render()
    {
        var (on, setOn) = UseState(false);
        UseSpring(AnimChannel.ScaleX, on ? 1.2f : 1f, SpringParams.FromResponse(0.3f, 0.5f), on);
        UseSpring(AnimChannel.ScaleY, on ? 1.2f : 1f, SpringParams.FromResponse(0.3f, 0.5f), on);
        return Button.Standard(on ? "Spring back" : "Spring up", () => setOn(!on));
    }
}

sealed class AnimHooksColor : Component
{
    public override Element Render()
    {
        var (on, setOn) = UseState(false);
        float t = UseAnimatedValue(on ? 1f : 0f, 300f);
        var fill = ColorF.Lerp(ColorF.FromRgba(80, 80, 88), Tok.AccentDefault, t);
        return new BoxEl
        {
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Width = 56f, Height = 44f, Fill = fill, Corners = Radii.OverlayAll },
                Button.Standard(on ? "To grey" : "To accent", () => setOn(!on)),
            ],
        };
    }
}
