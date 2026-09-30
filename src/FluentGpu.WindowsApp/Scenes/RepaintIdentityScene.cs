using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;

namespace FluentGpu;

/// <summary>
/// The scene half of <c>--repaint-identity</c> (gpu-renderer.md §13; <see cref="RepaintIdentityProbe"/>): deterministic
/// sub-scenes with adversarial geometry — sub-device-pixel gaps between animated bands, text straddling edges, an
/// ancestor rebase, an opacity group, a video hole, edge fades, self-blur, stencil clips, off-screen content, a scrolling
/// list and an analytic feather. Scenarios 0–10 were first written against the retired partial-canvas route (their
/// per-scenario notes name the defect class each one pins); they stay as the tile-static-identity fixtures because the
/// same geometry is what stresses tile edges and fractional scales. Every scenario is absolutely placed in a ZStack
/// with FRACTIONAL margins — a flex-laid, integer-ish layout would quietly test nothing.
///
/// The scene must be STATIC apart from the scripted write: the harness renders the same state twice (retained tiles vs
/// every segment degraded to direct raster) and compares the two back buffers byte for byte, so any autonomous motion —
/// a hover fade, a shimmer, a crossfade — would show up as a false mismatch. Hence no hover/press fills, no
/// transitions, no images, and no time-driven anything anywhere below.
/// </summary>
sealed class RepaintIdentityScene : Component
{
    // ── The scripted state. Static because the harness drives them from outside the component tree (the props-freeze
    //    contract: a field would be frozen at mount, a signal is read every render / every bound re-evaluation).
    public static readonly Signal<int> Scenario = new(0);
    /// <summary>Generic "one small thing changed" knob — drives the bound fills the paint-only scenarios mutate.</summary>
    public static readonly Signal<int> Tick = new(0);
    /// <summary>Second independent animator (scenario 0/5), so two damage bands are produced by two different writes.</summary>
    public static readonly Signal<int> TickB = new(0);
    /// <summary>Third animator (scenario 5).</summary>
    public static readonly Signal<int> TickC = new(0);
    /// <summary>Scenario 2: the ancestor "scroll" offset, applied as a TRANSFORM (never a relayout) so the recorder
    /// takes the translated-span-copy path — the one that rebases a whole subtree without walking a descendant.</summary>
    public static readonly Signal<float> ScrollY = new(0f);
    /// <summary>Scenario 2: the row's OWN later move, which is what reads the by-then-stale prior extent.</summary>
    public static readonly Signal<float> RowX = new(0f);

    /// <summary>Scenario 13: how many opacity-group effect slices paint BEFORE the acrylic plate (0 or
    /// <see cref="AcrylicBudgetEffects"/> — past the effect budget), all far from the plate.</summary>
    public static readonly Signal<int> AcrylicEffects = new(0);
    /// <summary>Scenario 13: the acrylic plate is present (false = the crisp page the frost must differ from).</summary>
    public static readonly Signal<bool> AcrylicPlate = new(true);
    /// <summary>Scenario 19: the scroller's edge cue is OFF (<c>ScrollEdgeCues.None</c>) — the unfaded content.</summary>
    public static readonly Signal<bool> EdgeCueOff = new(false);
    /// <summary>Scenario 19: the scroller's content is invisible (opacity 0) — the ground the cue must dissolve into.</summary>
    public static readonly Signal<bool> ContentHidden = new(false);

    public static void ResetAll()
    {
        Tick.Value = 0; TickB.Value = 0; TickC.Value = 0;
        ScrollY.Value = 0f; RowX.Value = 0f;
        AcrylicEffects.Value = 0; AcrylicPlate.Value = true;
        EdgeCueOff.Value = false; ContentHidden.Value = false;
    }

    static readonly ColorF PageBg = ColorF.FromRgba(0x1A, 0x1C, 0x22);

    // A translucent coat stack: the C1 double-blend hairline is only VISIBLE where there is no opaque coat to hide it,
    // and Wavee's measured stack has none (rq0/177 opaque rect instances). Three `over` coats reproduce that.
    static Element Coat(float l, float t, float w, float h, byte r, byte g, byte b, byte a) => new BoxEl
    {
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
        Margin = new Edges4(l, t, 0f, 0f), Width = w, Height = h,
        Corners = CornerRadius4.All(6f),
        Fill = ColorF.FromRgba(r, g, b, a),
    };

    static ColorF Pulse(int v, byte baseR, byte baseG, byte baseB, byte alpha)
        => ColorF.FromRgba((byte)(baseR + (v & 1) * 60), baseG, baseB, alpha);

    public override Element Render()
    {
        int id = Scenario.Value;
        Element scene = id switch
        {
            0 => TwinAnimators(),
            1 => GlyphStraddle(),
            2 => StalePriorExtent(),
            3 => OpacityGroup(),
            4 => VideoHole(),
            5 => ThreeAnimators(),
            6 => EdgeFadeBand(),
            7 => BlurGroupStraddle(),
            8 => StencilWithSiblingLayer(nested: false),
            9 => StencilWithSiblingLayer(nested: true),
            10 => OffscreenDamage(),
            11 => ScrollList(),
            13 => AcrylicBudget(AcrylicEffects.Value, AcrylicPlate.Value),
            14 => FadeDistribute(),
            15 => GroupCache(),
            16 => FeatherProduct(),
            17 => StickyClipPage(grouped: true),
            18 => StickyClipPage(grouped: false),
            19 => ScrollEdge(EdgeCueOff.Value, ContentHidden.Value),
            20 => ScrollChrome(),
            21 => StickyClipPage(grouped: false, whileStuck: true),
            22 => ItemBandList(),
            _ => FeatherPanel(),
        };
        return new BoxEl
        {
            Grow = 1f, ZStack = true, Fill = PageBg,
            // KEYED per scenario, and that is load-bearing rather than tidy: without it the reconciler PATCHES one
            // scenario's tree into the next one positionally — same element types, same slots — and a bound channel
            // whose slot changed role silently keeps the old subscription. That reads as "the signal write produced no
            // work at all", which is exactly how it presented. A changed Key forces the remount.
            Children = [scene with { Key = $"identity-scenario-{id}" }],
        };
    }

    // ── 0 — C1: two independent animators whose 8-DIP-padded repaint bands land 0.4 DIP apart, over a 3-coat
    //    translucent stack. Their bands are separate on closed FLOAT intervals (Coalesce keeps them), so before the
    //    pixel-space fold they rounded OUT into a SHARED device column: cleared once, replayed twice, every coat in it
    //    blended twice. Bar A occupies y [100, 130); bar B starts at y 146.4 ⇒ padded bands touch at 138 vs 138.4.
    static Element TwinAnimators() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(60f, 60f, 420f, 240f, 0x30, 0x40, 0x70, 0x60),
            Coat(80f, 80f, 380f, 200f, 0x70, 0x30, 0x50, 0x55),
            Coat(96f, 92f, 340f, 170f, 0x20, 0x70, 0x60, 0x50),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(120f, 100f, 0f, 0f), Width = 260f, Height = 30f,
                Fill = Prop.Of(() => Pulse(Tick.Value, 0x90, 0x50, 0x30, 0xC0)),
            },
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(120f, 146.4f, 0f, 0f), Width = 260f, Height = 30f,
                Fill = Prop.Of(() => Pulse(TickB.Value, 0x30, 0x80, 0x90, 0xC0)),
            },
        ],
    };

    // ── 1 — I4: text runs deliberately straddling where a replay-rect edge falls. The mutated bar sits immediately
    //    above/below the runs, so the damage band's edge cuts THROUGH them and the decode-time cull has to decide
    //    whether each run is kept. An italic face and an emoji fallback are included because those are the two classes
    //    whose ink provably exceeds the run's declared node box.
    static Element GlyphStraddle() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(40f, 40f, 520f, 260f, 0x28, 0x30, 0x48, 0x70),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(60f, 70.4f, 0f, 0f), Width = 460f, Direction = 1, Gap = 2.6f,
                Children =
                [
                    new TextEl("Regular ascender/descender jgpqy") { Size = 15f, Color = ColorF.FromRgba(0xEE, 0xEE, 0xF2) },
                    // TIGHT line bounds: the reported line box is trimmed to cap-height..baseline, so ascenders and
                    // descenders rasterize OUTSIDE the run's declared Bounds — one of the two classes I4 names.
                    new TextEl("Tight bounds jgpqy AWAY") { Size = 17f, LineBounds = TextLineBounds.Tight, Color = ColorF.FromRgba(0xDD, 0xE4, 0xFF) },
                    // Colour-emoji FALLBACK: the fallback face is chosen for coverage, not metric compatibility, so a
                    // COLR/CBDT glyph can exceed the em box — the other class I4 names.
                    new TextEl("Emoji fallback \U0001F3B5 \U0001F50A \U0001F525") { Size = 19f, Color = ColorF.FromRgba(0xFF, 0xE8, 0xC0) },
                    // An explicit line height SMALLER than the font-natural box, stacked block-wise.
                    new TextEl("Tight line stacking, small") { Size = 11f, LineHeight = 9f, LineStacking = LineStacking.BlockLineHeight, Color = ColorF.FromRgba(0xC8, 0xD0, 0xE0) },
                ],
            },
            // The animator: a thin bar whose padded band's edge lands INSIDE the text block above.
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(60f, 118.7f, 0f, 0f), Width = 300f, Height = 6f,
                Fill = Prop.Of(() => Pulse(Tick.Value, 0x80, 0x40, 0x90, 0xB0)),
            },
        ],
    };

    // ── 2 — I3: an ancestor rebases its whole subtree with a TRANSFORM (the translated-span-copy path — no descendant
    //    is walked, so every descendant's stored extent keeps pre-translation coordinates), and only LATER does one row
    //    move on its own. The row's prior extent then describes a position 600 DIP away that no effect-halo constant
    //    can reach; the band it actually vacated must come from a fresh ancestor instead.
    static Element StalePriorExtent() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            new BoxEl
            {
                // The "viewport": a clipping box the track scrolls inside.
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(40f, 40f, 0f, 0f), Width = 520f, Height = 300f,
                Fill = ColorF.FromRgba(0x22, 0x26, 0x30), Corners = CornerRadius4.All(8f), ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        // The "track": one transform write moves it and every row under it.
                        ZStack = true, Width = 520f, Height = 1400f,
                        Transform = Prop.Of(() => Affine2D.Translation(0f, -ScrollY.Value)),
                        Children =
                        [
                            Coat(16f, 40f, 480f, 60f, 0x40, 0x48, 0x60, 0x90),
                            Coat(16f, 120f, 480f, 60f, 0x40, 0x48, 0x60, 0x90),
                            Coat(16f, 760f, 480f, 60f, 0x38, 0x50, 0x58, 0x90),
                            // The row that later moves on its own. At ScrollY 600 it presents at y = 80.
                            new BoxEl
                            {
                                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                                Margin = new Edges4(28f, 680f, 0f, 0f), Width = 300f, Height = 52f,
                                Corners = CornerRadius4.All(6f),
                                Fill = ColorF.FromRgba(0xC0, 0x70, 0x30, 0xE0),
                                Transform = Prop.Of(() => Affine2D.Translation(RowX.Value, 0f)),
                            },
                            Coat(16f, 860f, 480f, 60f, 0x38, 0x50, 0x58, 0x90),
                        ],
                    },
                ],
            },
        ],
    };

    // ── 3 — the LAYERED partial route (an opacity group), which the plan's 900-frame sessions never reached: the group
    //    RT is pool-leased, so the stream can only be replayed ONCE and the damage collapses to a single union rect.
    //    The mutated bar straddles the group's edge so the replay rect covers both inside and outside it.
    static Element OpacityGroup() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(40f, 40f, 500f, 280f, 0x30, 0x38, 0x50, 0x80),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(90f, 90f, 0f, 0f), Width = 320f, Height = 180f,
                ZStack = true, OpacityGroup = true, Opacity = 0.62f,
                Fill = ColorF.FromRgba(0x50, 0x30, 0x70, 0xB0), Corners = CornerRadius4.All(10f),
                Children =
                [
                    Coat(20f, 20f, 280f, 60f, 0xA0, 0x80, 0x40, 0xC0),
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(20f, 100f, 0f, 0f), Width = 280f, Height = 40f,
                        Fill = Prop.Of(() => Pulse(Tick.Value, 0x40, 0x90, 0x60, 0xD0)),
                    },
                ],
            },
            // …and a bar OUTSIDE the group, so the single union rect really does span the boundary.
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(60f, 286.3f, 0f, 0f), Width = 300f, Height = 26f,
                Fill = Prop.Of(() => Pulse(TickB.Value, 0x70, 0x50, 0x30, 0xC0)),
            },
        ],
    };

    // ── 4 — dimension E: a DrawVideo HOLE (dst' = dst × (1 − cov), i.e. an erase to premultiplied zero) partially
    //    overlapped by damage. The recorder inflates any damage touching a hole to re-punch the WHOLE hole; if that
    //    ever stopped happening, a partial frame would repaint half the hole and leave the other half opaque.
    static Element VideoHole() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(40f, 40f, 520f, 300f, 0x30, 0x34, 0x44, 0x90),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(120f, 90f, 0f, 0f), Width = 300f, Height = 170f,
                VideoHole = true, VideoSurfaceId = 1, Corners = CornerRadius4.All(4f),
            },
            // The animator overlaps only the hole's TOP-LEFT quadrant.
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(90f, 70.7f, 0f, 0f), Width = 180f, Height = 60f,
                Fill = Prop.Of(() => Pulse(Tick.Value, 0x60, 0x80, 0x40, 0x90)),
            },
        ],
    };

    // ── 5 — three simultaneous animators (playhead + equalizer + caret, structurally): a genuine 3-rect frame, which
    //    is where the replay budget, the coalescing waste heuristic and the per-pipe instance banks are all exercised
    //    at once. Two of the three are placed a fraction of a DIP apart so the pixel fold is live here too.
    static Element ThreeAnimators() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(30f, 30f, 560f, 320f, 0x2A, 0x32, 0x44, 0x88),
            Coat(50f, 50f, 520f, 120f, 0x50, 0x38, 0x60, 0x60),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(60f, 64f, 0f, 0f), Width = 420f, Height = 8f,
                Fill = Prop.Of(() => Pulse(Tick.Value, 0x80, 0x60, 0x40, 0xE0)),
            },
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(60f, 88.35f, 0f, 0f), Width = 120f, Height = 44f,
                Fill = Prop.Of(() => Pulse(TickB.Value, 0x30, 0x90, 0x70, 0xD0)),
            },
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(430f, 250.5f, 0f, 0f), Width = 3f, Height = 28f,
                Fill = Prop.Of(() => Pulse(TickC.Value, 0xE0, 0xE0, 0xE0, 0xFF)),
            },
        ],
    };

    // ── 6 — the PLAIN (σ = 0) EDGE FADE under a clamped replay. This is the class every scrolling list puts on screen
    //    (AutoEdgeFade), and vetoing it meant almost every frame in a real app took FullDirect no matter how small its
    //    damage was. Admitting it rests on a claim that only real pixels can settle: the strip restore reads the target
    //    it is about to write (snapshot D, then lerp(D, F, feather) back), and on a partial frame the strips extend
    //    past the replay rect. The claim is that writes are clip-intersected so nothing outside R is touched, and that
    //    inside R the snapshot is this frame's own freshly replayed backdrop.
    //
    //    So the mutated bars are placed to STRADDLE the faded edges: one inside the fade band on the left, one in the
    //    clear middle, and one crossing the right band. A fade that sampled a stale backdrop, or wrote outside its
    //    clip, shows up as a feathered column that differs from the full redraw.
    static Element EdgeFadeBand() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(30f, 30f, 560f, 320f, 0x28, 0x30, 0x40, 0x90),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(60f, 70f, 0f, 0f), Width = 460f, Height = 150f,
                ClipToBounds = true,
                Fill = ColorF.FromRgba(0x3A, 0x42, 0x58, 0xB0), Corners = CornerRadius4.All(8f),
                // Both horizontal edges feathered, sigma 0 — the strip-fade class, not the blurred one.
                EdgeFade = new EdgeFadeSpec(EdgeMask.Horizontal, 36f, FadeFalloff.Smoothstep, 1f),
                Children =
                [
                    // Inside the LEFT fade band: its pixels are a lerp against the backdrop, so a stale D shows here.
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(6f, 18f, 0f, 0f), Width = 54f, Height = 34f,
                        Fill = Prop.Of(() => Pulse(Tick.Value, 0x90, 0x60, 0x40, 0xE0)),
                    },
                    // Fully inside the clear middle: unfeathered, so it isolates the fade as the cause if it differs.
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(180f, 62.4f, 0f, 0f), Width = 120f, Height = 30f,
                        Fill = Prop.Of(() => Pulse(TickB.Value, 0x40, 0x90, 0x70, 0xD0)),
                    },
                    // CROSSING the right band, so one bar spans feathered and unfeathered pixels at once.
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(396f, 104.5f, 0f, 0f), Width = 80f, Height = 26f,
                        Fill = Prop.Of(() => Pulse(TickC.Value, 0xE0, 0xC0, 0x50, 0xFF)),
                    },
                ],
            },
        ],
    };

    // ── 7 — a σ > 0 SELF-BLUR group under a clamped replay. The class the driving app keeps on screen for the whole of
    //    playback (the blurred lyrics surface), and the veto that made every one of those frames repaint the window
    //    however little had changed.
    //
    //    Two claims can only be settled in real pixels, because the headless CPU reference models every PushLayer as
    //    flat alpha and has no Gaussian at all:
    //      1. the SOURCE is rendered over R ⊕ TapRadius(σ), so the taps near R's edge read real subtree pixels instead
    //         of the transparency that a plain clamp would leave just outside — a missing halo reads as a band along
    //         the replay rect's edge that composites too LIGHT;
    //      2. a dirty child inside the group damages its own band GROWN by the group's reach (§2b), so the blurred
    //         output's outer ring is repainted too — a missing inflation leaves a stale ring around each moved bar.
    //
    //    So the pulsing bars sit INSIDE the blurred container (that is what makes them enclosed-by-blur rather than
    //    self-blurred) and are placed hard against its edges: one overlapping the left edge, one in the middle, one
    //    crossing the right. Fractional margins keep the damage rects off whole-pixel boundaries, where a halo that is
    //    one pixel short still round-trips by luck.
    static Element BlurGroupStraddle() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(30f, 30f, 560f, 320f, 0x28, 0x30, 0x40, 0x90),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(60f, 70f, 0f, 0f), Width = 460f, Height = 150f,
                Fill = ColorF.FromRgba(0x3A, 0x42, 0x58, 0xB0), Corners = CornerRadius4.All(8f),
                // σ = 6 ⇒ TapRadius = ceil(3σ) = 18 physical px at down = 1: wide enough that a missing halo is many
                // pixels wrong, small enough that the inflated rect stays well inside the 900×640 surface.
                Blur = 6f,
                Children =
                [
                    // Overlapping the LEFT edge: its Gaussian support runs off the container, which is precisely where
                    // an un-inflated source would sample transparency.
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(-12.5f, 20.5f, 0f, 0f), Width = 60f, Height = 34f,
                        Fill = Prop.Of(() => Pulse(Tick.Value, 0x90, 0x60, 0x40, 0xE0)),
                    },
                    // Fully interior: isolates the blur as the cause if the straddling two differ and this one does not.
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(190f, 58.5f, 0f, 0f), Width = 110f, Height = 30f,
                        Fill = Prop.Of(() => Pulse(TickB.Value, 0x40, 0x90, 0x70, 0xD0)),
                    },
                    // CROSSING the right edge, so one bar spans blurred-inside and clipped-outside at once.
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(408.5f, 100.5f, 0f, 0f), Width = 78f, Height = 26f,
                        Fill = Prop.Of(() => Pulse(TickC.Value, 0xE0, 0xC0, 0x50, 0xFF)),
                    },
                ],
            },
        ],
    };

    // A top-level static stencil must not veto unrelated lyrics-sized damage. The nested variant also changes
    // pixels across both mask silhouettes at fractional coordinates, plus an independent sibling opacity lease.
    // No layer encloses a stencil and no stencil encloses a layer: those target-changing cases remain excluded.
    static readonly PathData IdentityHeart = PathDataParser.Parse(
        "M16 29 C7 21 2 15.5 2 10 A6.5 6.5 0 0 1 16 6.5 A6.5 6.5 0 0 1 30 10 C30 15.5 25 21 16 29 Z",
        PathContentEpoch.Mint(), FillRule.NonZero);

    // The raw dirty area exceeds the window, but only a narrow 100-DIP tail is visible. Policy must clip before
    // judging coverage; the fully offscreen second bar must contribute no replay area at all.
    static Element OffscreenDamage() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(30f, 30f, 760f, 360f, 0x28, 0x30, 0x40, 0x90),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(710.35f, -10000f, 0f, 0f), Width = 80f, Height = 10100f,
                Fill = Prop.Of(() => Pulse(Tick.Value, 0x90, 0x50, 0x30, 0xD0)),
            },
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(100f, -12000f, 0f, 0f), Width = 80f, Height = 40f,
                Fill = Prop.Of(() => Pulse(TickB.Value, 0x30, 0x80, 0x70, 0xC0)),
            },
        ],
    };

    static BoxEl IdentityHeartClip(float x, float y, float size, params Element[] children) => new()
    {
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
        Margin = new Edges4(x, y, 0f, 0f), Width = size, Height = size, ZStack = true,
        ClipPath = IdentityHeart, ClipPathRule = FillRule.NonZero,
        ClipPathViewBoxW = 32f, ClipPathViewBoxH = 32f,
        Children = children,
    };

    static Element StencilWithSiblingLayer(bool nested) => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(30f, 30f, 760f, 360f, 0x28, 0x30, 0x40, 0x90),
            IdentityHeartClip(60.35f, 60.65f, 240f,
                Coat(0f, 0f, 240f, 240f, 0x90, 0x30, 0x60, 0xB0),
                nested
                    ? IdentityHeartClip(22.45f, 15.35f, 210f,
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                            Margin = new Edges4(-8.5f, 40.4f, 0f, 0f), Width = 220f, Height = 12f,
                            Fill = Prop.Of(() => Pulse(Tick.Value, 0x30, 0x80, 0x70, 0xC0)),
                        },
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                            Margin = new Edges4(58.7f, 128.6f, 0f, 0f), Width = 110f, Height = 12f,
                            Fill = Prop.Of(() => Pulse(TickB.Value, 0x90, 0x60, 0x30, 0xD0)),
                        })
                    : Coat(16f, 48f, 210f, 110f, 0x30, 0x80, 0x70, 0xC0)),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(420.35f, 70.65f, 0f, 0f), Width = 250f, Height = 220f,
                ZStack = true, Blur = nested ? 0f : 6f, Opacity = nested ? .7f : 1f,
                Children =
                [
                    Coat(0f, 0f, 250f, 220f, 0x40, 0x50, 0x70, 0xB0),
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                        Margin = new Edges4(20.5f, 42.4f, 0f, 0f), Width = 100f, Height = 14f,
                        Fill = Prop.Of(() => Pulse(TickC.Value, 0x80, 0x50, 0x40, 0xD0)),
                    },
                ],
            },
        ],
    };

    // ── 11 — tile-scroll-identity: a scrolling list of translucent text rows over a coat, taller than several tiles, so
    //    scrolling it re-uses retained tiles AND rasters the ones entering; the probe compares that frame against a
    //    fresh full re-raster at the same offset.
    public const float ScrollListRowH = 37.5f;
    public const int ScrollListRows = 160;
    static Element ScrollList() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(20f, 20f, 820f, 580f, 0x24, 0x2C, 0x3C, 0xA0),
            new ScrollEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(40.5f, 40.25f, 0f, 0f), Width = 760f, Height = 540f,
                Content = new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children = ScrollRows(),
                },
            },
        ],
    };

    // ── 19 — scroll-edge-identity: scenario 11's geometry (a translucent coat, translucent rows, a fractional viewport
    //    edge) with the DEFAULT edge cues, scrolled past the runway so both edges are cued. The probe captures it as
    //    rendered (A), with the cue off (B) and with the content invisible (the ground, G), and holds every pixel of the
    //    viewport to A = G + (B − G)·f, f = EdgeFeatherMask.Evaluate of the viewport's analytic feather: the cue dissolves
    //    the content into WHATEVER lies behind it, at the edge's true position.
    public const float ScrollEdgeX = 40.5f, ScrollEdgeY = 40.25f, ScrollEdgeW = 760f, ScrollEdgeH = 540f;
    static Element ScrollEdge(bool cueOff, bool contentHidden) => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(20f, 20f, 820f, 580f, 0x24, 0x2C, 0x3C, 0xA0),
            new ScrollEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(ScrollEdgeX, ScrollEdgeY, 0f, 0f), Width = ScrollEdgeW, Height = ScrollEdgeH,
                SuppressScrollBar = true, EdgeCues = cueOff ? ScrollEdgeCues.None : ScrollEdgeCues.Auto,
                Content = new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Opacity = contentHidden ? 0f : 1f,
                    Children = ScrollRows(),
                },
            },
        ],
    };

    // ── 20 — scroll-chrome-identity: scenario 19's scroller with its overlay scrollbar pinned visible, scrolled just past
    //    the runway so the thumb sits at the top of its track INSIDE the top feather band. The chrome is drawn over the
    //    feather on every route: distributed (the thumb carries none of the fade), grouped (the thumb lifted out of the
    //    group surface) and the paint route (the feather closed before the chrome) — pixel-identical.
    static Element ScrollChrome() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(20f, 20f, 820f, 580f, 0x24, 0x2C, 0x3C, 0xA0),
            new ScrollEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(ScrollEdgeX, ScrollEdgeY, 0f, 0f), Width = ScrollEdgeW, Height = ScrollEdgeH,
                AlwaysShowScrollbar = true,
                Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = ScrollRows() },
            },
        ],
    };

    static Element[] ScrollRows()
    {
        var rows = new Element[ScrollListRows];
        for (int i = 0; i < rows.Length; i++)
        {
            byte shade = (byte)(0x30 + (i * 23) % 0x60);
            rows[i] = new BoxEl
            {
                Height = ScrollListRowH, Direction = 0, Gap = 10f, Padding = new Edges4(12f, 6f, 12f, 6f),
                Corners = CornerRadius4.All(5f), Fill = ColorF.FromRgba(shade, 0x40, 0x68, 0xB8),
                Children =
                [
                    new BoxEl { Width = 22f, Height = 22f, Corners = CornerRadius4.All(11f), Fill = ColorF.FromRgba(0xE0, (byte)(0x60 + i % 0x80), 0x50, 0xFF) },
                    new TextEl($"Row {i} — retained tiles scroll identity jgpqy") { Size = 14f, Color = ColorF.FromRgba(0xF0, 0xF0, 0xF4) },
                ],
            };
        }
        return rows;
    }

    // ── 12 — tile-feather-identity: an OPAQUE panel with an analytic top+bottom edge feather over the page ground. The
    //    probe predicts every pixel from EdgeFeatherMask's C# evaluator (the same function the composite shader runs).
    public const float FeatherX = 100f, FeatherY = 60f, FeatherW = 600f, FeatherH = 480f, FeatherBand = 48f;
    public static readonly ColorF FeatherColor = ColorF.FromRgba(0xE8, 0x9A, 0x3C);
    public static ColorF PageGround => PageBg;
    static Element FeatherPanel() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(FeatherX, FeatherY, 0f, 0f), Width = FeatherW, Height = FeatherH,
                Fill = FeatherColor,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Vertical, FeatherBand, FadeFalloff.Smoothstep, 1f),
            },
        ],
    };

    // ── 13 — tile-acrylic-budget-identity: an in-window frosted PLATE (the FlyoutSurface shape: an Acrylic spec + its
    //    Fallback as the authored fill) over saturated bars, with 0 or AcrylicBudgetEffects opacity-group effect slices
    //    painted BEFORE it far from the plate (past the effect budget, so the plate is the first candidate the budget would
    //    fold). A frost exists only as a composite Backdrop item: the plate's pixels must be identical with 0 and with
    //    AcrylicBudgetEffects effects, and must differ from the crisp page (a folded acrylic used to become a transparent
    //    hole — the page showing straight through).
    public const int AcrylicBudgetEffects = 20;
    public const float AcrylicPlateX = 470.5f, AcrylicPlateY = 180.5f, AcrylicPlateW = 360f, AcrylicPlateH = 260f;
    static readonly AcrylicSpec PlateAcrylic = new(ColorF.FromRgba(0x2C, 0x2C, 0x2C), 0.15f, 30f, 0.02f, 0.96f, ColorF.FromRgba(0x2C, 0x2C, 0x2C));

    static Element AcrylicBudget(int effects, bool plate)
    {
        var children = new List<Element>(8 + effects + 1);
        // saturated vertical bars under (and around) the plate: a frost blurs + luminosity-washes them, a hole shows them crisp
        ColorF[] bars = [ColorF.FromRgba(0xE0, 0x20, 0x30), ColorF.FromRgba(0x20, 0xC0, 0x40), ColorF.FromRgba(0x30, 0x50, 0xF0),
                         ColorF.FromRgba(0xF0, 0xD0, 0x20), ColorF.FromRgba(0xD0, 0x30, 0xD0), ColorF.FromRgba(0x20, 0xD0, 0xD0)];
        for (int i = 0; i < bars.Length; i++)
            children.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(400f + i * 80f, 120f, 0f, 0f), Width = 64f, Height = 400f, Fill = bars[i],
            });
        // the effect slices, far (> 150 DIP) from the plate so no backdrop reaches them: a row along the top-left
        for (int i = 0; i < effects; i++)
            children.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(20f + (i % 10) * 30f, 20f + (i / 10) * 30f, 0f, 0f), Width = 22f, Height = 22f,
                Fill = ColorF.FromRgba(0xC0, 0x90, 0x40), Opacity = 0.6f, OpacityGroup = true,
            });
        if (plate)
            children.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(AcrylicPlateX, AcrylicPlateY, 0f, 0f), Width = AcrylicPlateW, Height = AcrylicPlateH,
                Corners = CornerRadius4.All(8f), Acrylic = PlateAcrylic, Fill = PlateAcrylic.Fallback,
                Padding = Edges4.All(16f),
                Children = [new TextEl("Frosted plate") { Size = 15f, Color = ColorF.FromRgba(0xF0, 0xF0, 0xF4) }],
            });
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
    }

    // ── 14 — fade-distribute-identity: a vertical AutoEdgeFade page (no fill — distributable) over rows of cards, and
    //    beside it a horizontal AutoEdgeFade shelf (no fill — distributable), every scroller scrolled mid-way so both
    //    feathers of both axes are live. The probe compares the distributed route with the GroupFades knockout (every fade
    //    a group surface): ≤ 1/255 — ONE group surface's 8-bit quantization per pixel. (Nested fades are not composed
    //    here: the knockout would put a pixel through TWO quantized group surfaces, a reference error of its own; the
    //    exact product of two distributed feathers is held to the analytic evaluator by tile-feather-identity/product.)
    public const int FadeShelves = 5;
    static Element FadeDistribute() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(20f, 20f, 820f, 580f, 0x30, 0x3A, 0x50, 0xFF),
            new ScrollEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(40.5f, 40.25f, 0f, 0f), Width = 760f, Height = 400f,
                AutoEdgeFade = true, SuppressScrollBar = true,
                Content = new BoxEl { Direction = 1, Gap = 28f, Padding = new Edges4(0f, 24f, 0f, 24f), MinWidth = 0f, Children = FadeShelfRows() },
            },
            new ScrollEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(40.5f, 468.25f, 0f, 0f), Width = 760f, Height = 112f,
                Horizontal = true, AutoEdgeFade = true, SuppressScrollBar = true,
                Content = new BoxEl { Direction = 0, Gap = 14f, Children = FadeCards(FadeShelves) },
            },
        ],
    };

    static Element[] FadeCards(int s)
    {
        var cards = new Element[12];
        for (int i = 0; i < cards.Length; i++)
            cards[i] = new BoxEl
            {
                Width = 140f, Height = 112f, Corners = CornerRadius4.All(6f),
                Fill = ColorF.FromRgba((byte)(0x50 + (i * 37 + s * 23) % 0xA0), (byte)(0x40 + (i * 53) % 0x90), (byte)(0x70 + (s * 41) % 0x80)),
                Children = [new TextEl($"Card {s}.{i}") { Size = 13f, Color = ColorF.FromRgba(0xF4, 0xF4, 0xF6) }],
            };
        return cards;
    }

    static Element[] FadeShelfRows()
    {
        var rows = new Element[FadeShelves];
        for (int s = 0; s < rows.Length; s++)
            rows[s] = new BoxEl { Direction = 0, Gap = 14f, Height = 112f, MinWidth = 0f, Children = FadeCards(s) };
        return rows;
    }

    // ── 15 — group-cache-identity: a horizontal AutoEdgeFade shelf WITH a fill (its own paint overlaps its content in
    //    the band, so it can only composite as a GROUP) inside a vertical page. A page scroll moves the group rigidly — its
    //    retained surface is re-drawn (a cache HIT); the probe compares that frame with a forced full re-raster at the same
    //    offset (every tile re-rastered ⇒ a key miss ⇒ the group re-rendered): 0 px.
    static Element GroupCache() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(20f, 20f, 820f, 580f, 0x2A, 0x30, 0x44, 0xFF),
            new ScrollEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(40.5f, 40.25f, 0f, 0f), Width = 760f, Height = 540f, SuppressScrollBar = true,
                Content = new BoxEl
                {
                    Direction = 1, Gap = 24f, Padding = new Edges4(0f, 200f, 0f, 600f), MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Height = 60f, Fill = ColorF.FromRgba(0x60, 0x40, 0x30), Corners = CornerRadius4.All(6f) },
                        new ScrollEl
                        {
                            Horizontal = true, AutoEdgeFade = true, SuppressScrollBar = true, Height = 150f,
                            Fill = ColorF.FromRgba(0x3C, 0x46, 0x62),
                            Content = new BoxEl { Direction = 0, Gap = 12f, Padding = Edges4.All(10f), Children = GroupCacheCards() },
                        },
                        new BoxEl { Height = 60f, Fill = ColorF.FromRgba(0x30, 0x60, 0x40), Corners = CornerRadius4.All(6f) },
                    ],
                },
            },
        ],
    };

    static Element[] GroupCacheCards()
    {
        var cards = new Element[10];
        for (int i = 0; i < cards.Length; i++)
            cards[i] = new BoxEl
            {
                Width = 150f, Height = 130f, Corners = CornerRadius4.All(6f),
                Fill = ColorF.FromRgba((byte)(0x80 + (i * 29) % 0x70), 0x50, (byte)(0x90 + (i * 17) % 0x60)),
                Children = [new TextEl($"Tile {i} jgpq") { Size = 13f, Color = ColorF.FromRgba(0xF8, 0xF8, 0xF8) }],
            };
        return cards;
    }

    // ── 17 / 18 — stickyclip-identity: the artist page's sticky-clip shape at a FRACTIONAL band inset. A page scroller
    //    whose content is a ZStack of a translucent sticky-clipped WASH and a column [hero, MAGAZINE]; the magazine carries
    //    a top edge fade, is sticky-clipped at the same band and holds two AutoEdgeFade shelves. 17: the page fades too and
    //    the magazine has a heading of its own, so it composites as a GROUP; 18: no page fade and no own paint, so the
    //    magazine's fade is DISTRIBUTED onto its shelves. The probe compares the composite-time clip (the clip on the
    //    slice marker, applied at placement) with the StickyClipInPaint knockout (the clip baked into the recorded stream,
    //    the paint route): 0 px.
    //    21 — the same distributed page with the magazine's fade declared <c>WhileStuck</c> (Wavee's vertical detail
    //    table, RCA 2026-09-25 F(ii)): the composite plan applies the feather exactly while the clip is engaged, the paint
    //    route builds it from the recorded clip — at the engaged pose both must agree to the pixel.
    public const float StickyInset = 56.25f;
    static Element StickyClipPage(bool grouped, bool whileStuck = false)
    {
        var magazine = new Element[grouped ? 3 : 2];
        int m = 0;
        if (grouped)
            magazine[m++] = new BoxEl
            {
                Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, Height = 44f,
                Children =
                [
                    new BoxEl { Width = 120.5f, Height = 30.25f, Corners = CornerRadius4.All(5f), Fill = ColorF.FromRgba(0xD8, 0xA8, 0x40) },
                    new TextEl("Popular releases gjpq") { Size = 22f, Bold = true, Color = ColorF.FromRgba(0xF6, 0xF6, 0xF8) },
                ],
            };
        for (int s = 0; s < 2; s++)
            magazine[m++] = new ScrollEl
            {
                Horizontal = true, AutoEdgeFade = true, SuppressScrollBar = true, Height = 112f,
                Content = new BoxEl { Direction = 0, Gap = 14f, Children = FadeCards(s + 2) },
            };
        return new BoxEl
        {
            Grow = 1f, ZStack = true,
            Children =
            [
                Coat(20f, 20f, 820f, 580f, 0x2C, 0x32, 0x46, 0xFF),
                new ScrollEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(40.5f, 40.25f, 0f, 0f), Width = 760f, Height = 540f,
                    AutoEdgeFade = grouped, SuppressScrollBar = true,
                    Content = new BoxEl
                    {
                        ZStack = true, MinWidth = 0f,
                        Children =
                        [
                            new BoxEl { Height = 520f, Fill = ColorF.FromRgba(0x60, 0x30, 0x8C, 0xC8), HitTestVisible = false }.StickyClip(StickyInset),
                            new BoxEl
                            {
                                Direction = 1, MinWidth = 0f,
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Height = 260f, Direction = 1, Justify = FlexJustify.End, Padding = new Edges4(24f, 0f, 24f, 24f),
                                        Children = [new TextEl("Artist Name jgpq") { Size = 44f, Bold = true, Color = ColorF.FromRgba(0xF8, 0xF8, 0xF8) }],
                                    },
                                    new BoxEl
                                    {
                                        Direction = 1, Gap = 18f, Padding = new Edges4(16.5f, 8.25f, 16.5f, 40f),
                                        EdgeFade = new EdgeFadeSpec(EdgeMask.Top, 24f) { WhileStuck = whileStuck },
                                        Children = magazine,
                                    }.StickyClip(StickyInset),
                                    new BoxEl { Height = 900f },
                                ],
                            },
                        ],
                    },
                },
            ],
        };
    }

    // ── 22 — item-band-identity: Wavee's vertical detail-row list, reduced (RCA 2026-09-25 G). A virtual list whose
    //    items 0/1 are a sticky hero and chrome (the persistent prefix) and whose recyclable suffix is clipped by ONE
    //    viewport-fixed item band at a FRACTIONAL inset with a feather (ItemClipTopInset / ItemClipTopFadeBand). The probe
    //    jumps deep (past the local-extent window, so the arrange origin re-centres and rows sit at negative local offsets)
    //    and compares the tiled composite — the band clip on the band slice's marker, applied at the live pose — with the
    //    forced direct paint route: ≤ 1/255 (the feather).
    public const float ItemBandInset = 80.25f, ItemBandFade = 22f, ItemBandRow = 40f;
    public const int ItemBandCount = 1000;
    static Element ItemBandList() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            Coat(20f, 20f, 820f, 580f, 0x26, 0x2C, 0x3C, 0xFF),
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(40.5f, 40.25f, 0f, 0f), Width = 760f, Height = 540f,
                Children =
                [
                    FluentGpu.Controls.ItemsView.CreateBound(ItemBandCount,
                        scope =>
                        {
                            int initial = scope.Index.Peek();
                            return new BoxEl
                            {
                                Height = ItemBandRow, Direction = 0, AlignItems = FlexAlign.Center,
                                Padding = new Edges4(12.5f, 0f, 12.5f, 0f),
                                Fill = initial == 0 ? ColorF.FromRgba(0x8E, 0x30, 0xBE) : initial == 1 ? ColorF.FromRgba(0x2A, 0x9C, 0xC4)
                                    : ColorF.FromRgba(0x3A, 0x44, 0x58),
                                ScrollEffects = initial == 0 ? [new(ScrollEffect.Sticky(0f))]
                                    : initial == 1 ? [new(ScrollEffect.Sticky(ItemBandRow))] : [],
                                Children = [new TextEl(Prop.Of(() => "Row " + scope.Index.Value + " jgpq")) { Size = 15f, Color = ColorF.FromRgba(0xF4, 0xF4, 0xF6) }],
                            };
                        },
                        FluentGpu.Controls.RepeatLayout.Stack(ItemBandRow),
                        new FluentGpu.Controls.ListOptions
                        {
                            PersistentPrefixCount = 2,
                            Grow = 1f,
                            Scroll = new FluentGpu.Controls.ScrollOptions
                            {
                                ItemClipTopInset = ItemBandInset, ItemClipTopFadeBand = ItemBandFade, SuppressScrollBar = true,
                                // Wavee's vertical list: no stock scroll-edge cue (its bottom feather is not a row).
                                AutoEdgeFade = false, EdgeCues = ScrollEdgeCues.None,
                            },
                        }),
                ],
            },
        ],
    };

    // ── 16 — tile-feather-identity, the PRODUCT: an opaque panel with its own horizontal feather inside a box with a
    //    vertical feather (no paint of its own — distributable), so the panel's item carries BOTH feathers and the composite
    //    multiplies them. The probe predicts every pixel as ground + (colour − ground) · f_outer · f_inner from the C#
    //    evaluator: ≤ 1/255.
    public const float ProductBandOuter = 60f, ProductBandInner = 48f;
    static Element FeatherProduct() => new BoxEl
    {
        Grow = 1f, ZStack = true,
        Children =
        [
            new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(FeatherX, FeatherY, 0f, 0f), Width = FeatherW, Height = FeatherH,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Vertical, ProductBandOuter, FadeFalloff.Smoothstep, 1f),
                Children =
                [
                    new BoxEl
                    {
                        Width = FeatherW, Height = FeatherH, Fill = FeatherColor,
                        EdgeFade = new EdgeFadeSpec(EdgeMask.Horizontal, ProductBandInner, FadeFalloff.Smoothstep, 1f),
                    },
                ],
            },
        ],
    };
}
