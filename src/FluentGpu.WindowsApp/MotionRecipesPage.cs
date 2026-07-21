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

// ===== MotionRecipesPage — the Expressive Motion Kit, recipe by recipe =====
// The transitions.dev adoption: a library of named, tuned transitions (MotionRecipes.*) expressed on the engine's
// springs, eased tracks, the per-node self-blur channel (AnimChannel.Blur), and the expressive curve/token vocabulary
// (Easing.SmoothOut/Overshoot/Pop, Expressive.*). Each card seeds a recipe imperatively on captured nodes — the same
// Context.Anim idiom as AnimationPage. This is an opt-in app-author palette; framework controls keep their Fluent curves.
[Route("motion-recipes")]
sealed class MotionRecipesPage : Component
{
    public override Element Render() => GalleryPage.Shell("Motion recipes",
        "The Expressive Motion Kit — production-tuned transitions adopted from transitions.dev, riding FluentGpu's own " +
        "animation engine. Number pop-in, error shake and the skeleton reveal lead; each recipe is one call on " +
        "Context.Anim (e.g. anim.PopIn(node), anim.Shake(node)). The new per-node BLUR channel is the perceptual " +
        "softener — a short travel reads as a full motion. All honour reduced-motion and allocate nothing per frame.",
        ControlExample.Build("Number pop-in — staggered, blurred digits",
            Embed.Comp(() => new RecipeNumberPopIn()),
            description: "Each digit re-enters from below with a blurred slide on the Pop curve; the last digits stagger 70ms so decimals feel alive. The blur (2px → 0) makes the 8px travel read as a full pop.",
            code: """
            var digits = UseRef(new NodeHandle[n]);            // captured via OnRealized
            Context.Anim.PopInStaggered(digits.Value, dirY: 1f,
                distance: 8f, blur: 2f, durationMs: 500f, staggerMs: 70f);
            """),
        ControlExample.Build("Error shake — per-segment cubic-bezier",
            Embed.Comp(() => new RecipeShake()),
            description: "A percussive left/right shake with overshoot: a multi-segment TranslateX keyframe path, the SmoothOut curve per leg. The border flips to the error color independently, so the shake can replay without re-flickering the error state.",
            code: """
            // on a validation failure:
            Context.Anim.Shake(field, distance: 6f, overshoot: 4f);   // 280ms, A,A,B,B legs
            """),
        ControlExample.Build("Skeleton loader → reveal",
            Embed.Comp(() => new RecipeSkeleton()),
            description: "The placeholder bars pulse (looping opacity); when data arrives the real content cross-fades in with a matching cross-blur (SoftReveal). The skeleton stays in the same slot, so the swap is layout-free.",
            code: """
            Context.Anim.SkeletonPulse(bar, min: 0.5f, durationMs: 1000f);   // loops
            // when loaded → swap to the content component, which calls:
            this.UseSoftReveal();                                            // opacity + blur rise
            """),
        ControlExample.Build("Success check — fade + rotate + bob + draw-on",
            Embed.Comp(() => new RecipeSuccessCheck()),
            description: "The badge fades in, un-rotates from 80°, bobs up with a settle, and un-blurs from 8px — while the checkmark path draws itself on via the engine's stroke-trim channel (the same mechanism as the CheckBox glyph).",
            code: """
            Context.Anim.SuccessCheck(badge);                              // container celebration
            Context.UseKeyframes(AnimChannel.StrokeTrimEnd,                 // the line draws on
                [new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.SmoothOut)], 500f, false, key);
            """),
        ControlExample.Build("Icon swap — blurred scale cross-fade",
            Embed.Comp(() => new RecipeIconSwap()),
            description: "The replacement glyph grows from 0.25 scale with a blurred fade-in (ease-in-out). Swap the glyph, then play the recipe on the new icon.",
            code: """
            Context.Anim.IconSwapIn(icon);                                 // scale 0.25→1 + blur + fade
            """),
        ControlExample.Build("Notification badge — overshoot pop",
            Embed.Comp(() => new RecipeBadge()),
            description: "The dot slides onto the trigger and pops with a low-damping spring (the overshoot as physics, so a re-trigger mid-flight stays velocity-continuous), fading and un-blurring as it lands.",
            code: """
            Context.Anim.BadgePop(dot, offsetX: -8f, offsetY: 12f);
            """),
        ControlExample.Build("Texts reveal — staggered blurred rise",
            Embed.Comp(() => new RecipeTextsReveal()),
            description: "Stacked lines rise in with a blurred fade, each 40ms behind the last (SoftRevealStaggered) — hero copy, empty states, onboarding steps.",
            code: """
            Context.Anim.SoftRevealStaggered(lines.Value, dy: 12f, blur: 3f,
                durationMs: 500f, staggerMs: 40f);
            """),
        ControlExample.Build("Avatar group hover — distance falloff",
            Embed.Comp(() => new RecipeNeighborLift()),
            description: "Hovering an item lifts it and its neighbours with an exponential falloff (lift·0.45^distance), each on a spring; on release the row springs back bouncy. Try the buttons to move the active item.",
            code: """
            Context.Anim.NeighborLift(avatars.Value, activeIndex, hovered: true,
                lift: -8f, falloff: 0.45f);
            """));
}

// ── Number pop-in ────────────────────────────────────────────────────────────────────────────────────────────────
sealed class RecipeNumberPopIn : Component
{
    static readonly string[] Chars = ["1", "2", "8", ".", "4", "2"];

    public override Element Render()
    {
        var (replay, setReplay) = UseState(0);
        var digits = UseRef(new NodeHandle[Chars.Length]);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim) return;
            anim.PopInStaggered(digits.Value, dirY: 1f, distance: 8f, blur: 2f, durationMs: 500f, staggerMs: 70f);
        }, replay);

        var tiles = new Element[Chars.Length];
        for (int i = 0; i < Chars.Length; i++)
        {
            int idx = i;
            tiles[i] = new BoxEl
            {
                Width = Chars[i] == "." ? 10f : 26f, Height = 44f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                OnRealized = h => digits.Value[idx] = h,
                Children = [new TextEl(Chars[i]) { Size = 34f, Bold = true, Color = Tok.TextPrimary }],
            };
        }
        return new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.End, Children = tiles },
                Button.Standard("Replay", () => setReplay(replay + 1)),
            ],
        };
    }
}

// ── Error shake ──────────────────────────────────────────────────────────────────────────────────────────────────
sealed class RecipeShake : Component
{
    public override Element Render()
    {
        var error = UseSignal(false);
        var field = UseRef<NodeHandle>(default);

        return new BoxEl
        {
            Direction = 1, Gap = 10f,
            Children =
            [
                new BoxEl
                {
                    Width = 240f, Height = 40f, Padding = new Edges4(12, 0, 12, 0),
                    AlignItems = FlexAlign.Center,
                    Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary,
                    BorderWidth = 1f,
                    BorderColor = error.Value ? ColorF.FromRgba(0xE8, 0x11, 0x23) : Tok.StrokeCardDefault,
                    BrushTransitionMs = Motion.ControlNormal,
                    OnRealized = h => field.Value = h,
                    Children = [new TextEl(error.Value ? "Enter a valid email." : "name@example.com")
                        { Size = 13f, Color = error.Value ? ColorF.FromRgba(0xE8, 0x11, 0x23) : Tok.TextTertiary }],
                },
                Button.Standard("Trigger error", () =>
                {
                    error.Value = true;
                    if (Context.Anim is { } anim && !field.Value.IsNull) anim.Shake(field.Value, distance: 6f, overshoot: 4f);
                }),
            ],
        };
    }
}

// ── Skeleton loader → reveal ─────────────────────────────────────────────────────────────────────────────────────
sealed class RecipeSkeleton : Component
{
    public override Element Render()
    {
        var (loaded, setLoaded) = UseState(false);
        return new BoxEl
        {
            Direction = 1, Gap = 10f,
            Children =
            [
                new BoxEl
                {
                    Width = 260f, Height = 76f, Padding = Edges4.All(12),
                    Corners = Radii.OverlayAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Children = [loaded ? Embed.Comp(() => new RecipeSkeletonContent()) : Embed.Comp(() => new RecipeSkeletonBars())],
                },
                Button.Standard(loaded ? "Reset" : "Load", () => setLoaded(!loaded)),
            ],
        };
    }
}

sealed class RecipeSkeletonBars : Component
{
    public override Element Render()
    {
        var bars = UseRef(new NodeHandle[3]);
        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim) return;
            for (int i = 0; i < 3; i++)
                if (!bars.Value[i].IsNull) anim.SkeletonPulse(bars.Value[i], min: 0.45f, durationMs: 1000f);
        }, 0);

        Element Bar(int idx, float w) => new BoxEl
        {
            Width = w, Height = 12f, Corners = Radii.Circle(12f), Fill = Tok.FillSubtleSecondary,
            OnRealized = h => bars.Value[idx] = h,
        };
        return new BoxEl { Direction = 1, Gap = 8f, Children = [Bar(0, 200f), Bar(1, 150f), Bar(2, 110f)] };
    }
}

sealed class RecipeSkeletonContent : Component
{
    public override Element Render()
    {
        this.UseSoftReveal(dy: 8f, blur: 2f);
        return new BoxEl
        {
            Direction = 1, Gap = 4f,
            Children =
            [
                new TextEl("Ada Lovelace") { Size = 15f, Bold = true, Color = Tok.TextPrimary },
                new TextEl("Analytical Engine · 1843") { Size = 12.5f, Color = Tok.TextSecondary },
            ],
        };
    }
}

// ── Success check ────────────────────────────────────────────────────────────────────────────────────────────────
sealed class RecipeSuccessCheck : Component
{
    public override Element Render()
    {
        var (replay, setReplay) = UseState(0);
        var badge = UseRef<NodeHandle>(default);
        var replaySig = UseSignal(0);
        if (replaySig.Peek() != replay) replaySig.Value = replay;

        UseLayoutEffect(() =>
        {
            if (Context.Anim is { } anim && !badge.Value.IsNull) anim.SuccessCheck(badge.Value);
        }, replay);

        return new BoxEl
        {
            Direction = 0, Gap = 20f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = 64f, Height = 64f, Corners = Radii.Circle(64f), Fill = ColorF.FromRgba(0x10, 0x7C, 0x10),
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    OnRealized = h => badge.Value = h,
                    Children = [Embed.Comp(() => new RecipeCheckStroke { Replay = replaySig })],
                },
                Button.Standard("Replay", () => setReplay(replay + 1)),
            ],
        };
    }
}

sealed class RecipeCheckStroke : Component
{
    public required Signal<int> Replay;

    public override Element Render()
    {
        int seed = Replay.Value;
        Context.UseKeyframes(AnimChannel.StrokeTrimEnd,
            [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 1f, EasingSpec.CubicBezier(0.55f, 0f, 0f, 1f))],
            500f, false, seed);
        const float S = 34f;
        return new PolylineStrokeEl
        {
            Width = S, Height = S,
            P0 = new Point2(0.18f * S, 0.52f * S),
            P1 = new Point2(0.42f * S, 0.74f * S),
            P2 = new Point2(0.82f * S, 0.30f * S),
            P3 = new Point2(0.82f * S, 0.30f * S),
            PointCount = 3,
            Color = ColorF.FromRgba(0xFF, 0xFF, 0xFF), Thickness = 4f, RoundCaps = true,
        };
    }
}

// ── Icon swap ────────────────────────────────────────────────────────────────────────────────────────────────────
sealed class RecipeIconSwap : Component
{
    static readonly string[] Glyphs = [Icons.Accept, Icons.Refresh, Icons.More, Icons.Share];

    public override Element Render()
    {
        var (i, setI) = UseState(0);
        var icon = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is { } anim && !icon.Value.IsNull) anim.IconSwapIn(icon.Value);
        }, i);

        return new BoxEl
        {
            Direction = 0, Gap = 18f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = 56f, Height = 56f, Corners = Radii.OverlayAll, Fill = Tok.FillCardSecondary,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    OnRealized = h => icon.Value = h,
                    Children = [new TextEl(Glyphs[i % Glyphs.Length]) { Size = 24f, FontFamily = Theme.IconFont, Color = Tok.TextPrimary }],
                },
                Button.Standard("Swap icon", () => setI(i + 1)),
            ],
        };
    }
}

// ── Notification badge ───────────────────────────────────────────────────────────────────────────────────────────
sealed class RecipeBadge : Component
{
    public override Element Render()
    {
        var (replay, setReplay) = UseState(0);
        var dot = UseRef<NodeHandle>(default);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is { } anim && !dot.Value.IsNull) anim.BadgePop(dot.Value);
        }, replay);

        return new BoxEl
        {
            Direction = 0, Gap = 20f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = 48f, Height = 48f, Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    AlignItems = FlexAlign.End, Justify = FlexJustify.End,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 16f, Height = 16f, Corners = Radii.Circle(16f), Fill = ColorF.FromRgba(0xE8, 0x11, 0x23),
                            Margin = new Edges4(0, -4, -4, 0),
                            TransformOriginX = 1f, TransformOriginY = 0f,
                            OnRealized = h => dot.Value = h,
                        },
                    ],
                },
                Button.Standard("Notify", () => setReplay(replay + 1)),
            ],
        };
    }
}

// ── Texts reveal ─────────────────────────────────────────────────────────────────────────────────────────────────
sealed class RecipeTextsReveal : Component
{
    static readonly (string Text, float Size, bool Bold)[] Lines =
    [
        ("Welcome aboard", 20f, true),
        ("Let's set up your workspace.", 13.5f, false),
        ("It only takes a minute.", 13.5f, false),
    ];

    public override Element Render()
    {
        var (replay, setReplay) = UseState(0);
        var lines = UseRef(new NodeHandle[Lines.Length]);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim) return;
            anim.SoftRevealStaggered(lines.Value, dy: 12f, blur: 3f, durationMs: 500f, staggerMs: 40f);
        }, replay);

        var rows = new Element[Lines.Length];
        for (int i = 0; i < Lines.Length; i++)
        {
            int idx = i;
            rows[i] = new BoxEl
            {
                OnRealized = h => lines.Value[idx] = h,
                Children = [new TextEl(Lines[i].Text) { Size = Lines[i].Size, Bold = Lines[i].Bold, Color = Lines[i].Bold ? Tok.TextPrimary : Tok.TextSecondary }],
            };
        }
        return new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                new BoxEl { Direction = 1, Gap = 4f, Children = rows },
                Button.Standard("Replay", () => setReplay(replay + 1)),
            ],
        };
    }
}

// ── Avatar group hover (distance falloff) ────────────────────────────────────────────────────────────────────────
sealed class RecipeNeighborLift : Component
{
    static readonly ColorF[] Tints =
    [
        ColorF.FromRgba(0x4C, 0xC2, 0xFF), ColorF.FromRgba(0xE7, 0x4C, 0x8C), ColorF.FromRgba(0x6C, 0xCB, 0x5F),
        ColorF.FromRgba(0xFF, 0xB9, 0x00), ColorF.FromRgba(0xA0, 0x80, 0xFF),
    ];

    public override Element Render()
    {
        var (active, setActive) = UseState(-1);
        var avatars = UseRef(new NodeHandle[Tints.Length]);

        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim) return;
            anim.NeighborLift(avatars.Value, active, hovered: active >= 0, lift: -10f, falloff: 0.45f);
        }, active);

        var circles = new Element[Tints.Length];
        for (int i = 0; i < Tints.Length; i++)
        {
            int idx = i;
            circles[i] = new BoxEl
            {
                Width = 40f, Height = 40f, Corners = Radii.Circle(40f), Fill = Tints[i],
                Margin = new Edges4(i == 0 ? 0 : -8, 0, 0, 0),
                OnRealized = h => avatars.Value[idx] = h,
            };
        }
        var buttons = new List<Element> { new TextEl("Lift item:") { Size = 12.5f, Color = Tok.TextSecondary } };
        for (int i = 0; i < Tints.Length; i++) { int idx = i; buttons.Add(Button.Standard($"{i + 1}", () => setActive(idx))); }
        buttons.Add(Button.Standard("Release", () => setActive(-1)));

        return new BoxEl
        {
            Direction = 1, Gap = 14f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.End, Height = 56f, Children = circles },
                new BoxEl { Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Children = buttons.ToArray() },
            ],
        };
    }
}
