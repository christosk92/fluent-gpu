using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu;

/// <summary>
/// The scene half of <c>--repaint-identity</c> (gpu-renderer.md §13.1 / task-7 review §F): six deterministic sub-scenes,
/// each shaped so ONE scripted signal write produces the exact damage geometry a specific finding is about. Every
/// scenario is absolutely placed in a ZStack with FRACTIONAL margins, because the whole point of several of them is a
/// sub-device-pixel relationship between two damage bands — a flex-laid, integer-ish layout would quietly test nothing.
///
/// The scene must be STATIC apart from the scripted write: the harness renders the same state twice (once via the
/// partial route, once via a forced full repaint) and compares the two back buffers byte for byte, so any autonomous
/// motion — a hover fade, a shimmer, a crossfade — would show up as a false mismatch. Hence no hover/press fills, no
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

    public static void ResetAll()
    {
        Tick.Value = 0; TickB.Value = 0; TickC.Value = 0;
        ScrollY.Value = 0f; RowX.Value = 0f;
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
            _ => ThreeAnimators(),
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
}
