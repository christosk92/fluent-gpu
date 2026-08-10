using System;

namespace Wavee;

/// <summary>The controls the stage's identity column can FOLD away when it runs out of width. A folded control is never
/// LOST — it moves address into the compact header's "…" overflow, exactly like the player bar's own ladder.</summary>
[Flags]
public enum StageControl : byte
{
    None = 0,
    /// <summary>The shuffle satellite (32-DIP, latches accent).</summary>
    Shuffle = 1 << 0,
    /// <summary>The repeat satellite (32-DIP, latches accent).</summary>
    Repeat = 1 << 1,
    /// <summary>The volume row (thin slider over the scrim).</summary>
    Volume = 1 << 2,
    /// <summary>The output-device line under the volume row.</summary>
    OutputDevice = 1 << 3,
}

/// <summary>
/// The PURE space allocator behind the immersive STAGE — the fullscreen now-playing surface. It answers exactly one
/// question: at this surface width, is the stage in its WIDE two-region shape (a fixed identity + transport column
/// beside the pane region) or its COMPACT one-column shape (an identity HEADER row above a full-width pane)?
///
/// <para><b>One structure, one reflow flag.</b> This is the detail hero's rule, applied here: there are not two stage
/// layouts, there is one tree whose row direction, art size, transport sizes and folded-control set all fall out of
/// <see cref="Wide"/>. Everything a renderer needs is a field on this struct, so a call site never re-derives a
/// breakpoint (the failure that gave the player bar its own private threshold table before
/// <c>PlayerBarResponsiveLayout</c> existed).</para>
///
/// <para><b>Hysteresis.</b> <see cref="Resolve"/> takes the previously published layout: a DEMOTION (wide → compact) is
/// immediate, because nothing may clip while the window contracts, and a PROMOTION only lands once the width also clears
/// the threshold with <see cref="PromotionHysteresisW"/> of reserve. So dragging the window edge across the boundary
/// flips the stage exactly ONCE per crossing instead of thrashing the whole surface (which owns a mounted
/// <c>LyricsView</c> and a mounted queue, neither of which is cheap to rebuild).</para>
///
/// <para>Engine-free by construction (<c>System</c> only) so <c>StageLayoutTests</c> drives the real allocator rather
/// than a copy of its arithmetic — the <c>MergedChromeLayout</c> precedent.</para>
/// </summary>
public readonly record struct StageLayout(
    bool Wide,
    float ColumnWidth,
    float ColumnFalloff,
    float ArtSize,
    float PlayBox,
    float StepBox,
    float SatelliteBox,
    StageControl Folded)
{
    // ── the one threshold ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>At or above this surface width the stage is WIDE. Below it the identity column folds to a header row and
    /// the pane takes the whole surface. 600 is the width at which a 352-DIP column plus its falloff still leaves the
    /// pane region a readable measure; under it the pane would be narrower than the column beside it.</summary>
    public const float WideEnterW = 600f;

    /// <summary>The reserve a PROMOTION needs on top of <see cref="WideEnterW"/>. Demotion takes none.</summary>
    public const float PromotionHysteresisW = 40f;

    // ── the wide shape ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The identity column's DESIGNED width — what the user sees as "the column". Fixed, never fluid: the
    /// column carries a 300-square cover and a transport cluster whose sizes are authored, so stretching it would only
    /// stretch its whitespace.</summary>
    public const float WideColumnW = 352f;

    /// <summary>The gutter the column BOX reserves to the right of the designed column, so the pane region never starts
    /// flush against the column's type. It is layout width only (the column box is <see cref="LayoutWidth"/> wide and
    /// pads its content back to <see cref="WideColumnW"/>); the SHADE that deepens the scrim under the column is a
    /// separate, wider, full-bleed layer (<see cref="ColumnShadeW"/>) and is deliberately NOT this number.</summary>
    public const float WideColumnFalloffW = 120f;

    /// <summary>The column's internal gutter — the padding that puts the content back inside the designed
    /// <see cref="WideColumnW"/>. Authored here rather than in the renderer so <see cref="ColumnContentW"/> (and the
    /// volume track that spans it) is derived arithmetic rather than a second copy of the same number.</summary>
    public const float ColumnPadX = 24f;

    /// <summary>What the column's content actually gets: 352 − 2 × 24 = 304, which carries the 300 cover with a hairline
    /// to spare AND is the span every full-width row in the column (the seek block, the volume rail) must fill.</summary>
    public const float ColumnContentW = WideColumnW - 2f * ColumnPadX;

    /// <summary>The cover on the wide stage.</summary>
    public const float WideArtW = 300f;
    /// <summary>The cover in the compact header row (the app's <c>WaveeSize.Thumb64</c> rung, restated here to keep this
    /// file engine-free).</summary>
    public const float CompactArtW = 64f;

    /// <summary>The filled circular play/pause — the ONE filled control on the stage.</summary>
    public const float WidePlayBoxW = 56f;
    /// <inheritdoc cref="WidePlayBoxW"/>
    public const float CompactPlayBoxW = 40f;

    /// <summary>Previous / next.</summary>
    public const float WideStepBoxW = 40f;
    /// <inheritdoc cref="WideStepBoxW"/>
    public const float CompactStepBoxW = 32f;

    /// <summary>Shuffle / repeat — the transport's two SATELLITES. One rung below prev/next in both shapes, because they
    /// are modes rather than actions.</summary>
    public const float SatelliteBoxW = 32f;

    /// <summary>What the compact shape folds into the header's "…". Shuffle and repeat lose their satellites, and the
    /// volume row and the output-device line lose their space entirely — all four are still reachable, one tap deeper.</summary>
    public const StageControl CompactFold =
        StageControl.Shuffle | StageControl.Repeat | StageControl.Volume | StageControl.OutputDevice;

    // ── the scrim ladder ─────────────────────────────────────────────────────────────────────────────────────────────
    //
    // THE STAGE IS SINGLE-THEME: always art-dark, in BOTH themes. The room is lit by the playing track, like every
    // art-forward player, and every rung below is therefore theme-INVARIANT — there is no light arm to keep in sync.
    // (It used to have one: a WHITE base scrim in light theme, because LyricsView painted Tok.TextPrimary. That is what
    // made the surface a two-world collage — dark chrome veils in patches over a white ground, with the theme-invariant
    // white title landing on the near-white part and vanishing. The lyrics now paint the on-media ladder instead, which
    // is what lets the scrim be one thing.)
    //
    // The numbers live HERE, in the pure allocator, for the same reason the width ladder does: they are the contract the
    // tests drive. StageChrome turns them into the two GradientSpecs — it owns the spelling, not the values.
    //
    // EDGE-INVISIBILITY IS THE WHOLE MECHANISM. A veil is invisible iff it either (a) reaches its own boundary at alpha
    // 0 after a feather long enough that the ramp is below the eye's banding threshold, or (b) ends at a WINDOW edge,
    // where there is no "outside" to contrast with. Every stop below satisfies one of the two, and StageLayoutTests
    // asserts it in DIP rather than in stop fractions.

    /// <summary>The base scrim — what the artwork is dimmed to across the whole surface. One flat value, no theme
    /// branch: this is the ground everything else deepens FROM.</summary>
    public const float ScrimBaseA = 0.46f;

    /// <summary>The scrim at the very top of the body band, under the caption cluster.</summary>
    public const float ScrimTopA = 0.76f;

    /// <summary>…and at the very bottom, under the pivot band and the transport.</summary>
    public const float ScrimBottomA = 0.70f;

    /// <summary>Where the top deepening has fully resolved into <see cref="ScrimBaseA"/>, as a fraction of the body
    /// height. A FRACTION, not a DIP box, is what makes it edge-free by construction: at any window the feather is
    /// hundreds of DIP long, where the deleted 88-DIP top veil was a band you could point at.</summary>
    public const float ScrimTopStop = 0.22f;

    /// <summary>Where the bottom deepening starts, same units.</summary>
    public const float ScrimBottomStop = 0.62f;

    /// <summary>How much darker the scrim goes behind the identity column, on top of <see cref="ScrimBaseA"/>.</summary>
    public const float ColumnShadeA = 0.26f;

    /// <summary>How far the column shade keeps fading past <see cref="WideColumnW"/> before it reaches exactly 0. It is
    /// more than twice <see cref="WideColumnFalloffW"/> deliberately: the shade is a full-bleed PAINT layer with no
    /// layout consequence at all, so its feather is free, and a 260-DIP ramp to zero has no locatable edge.</summary>
    public const float ColumnShadeFalloffW = 260f;

    /// <summary>The column shade layer's width. Not a layout width — see <see cref="WideColumnFalloffW"/>.</summary>
    public const float ColumnShadeW = WideColumnW + ColumnShadeFalloffW;

    /// <summary>The stop at which the shade stops holding <see cref="ColumnShadeA"/> and starts feathering.</summary>
    public static float ColumnShadeHoldStop => WideColumnW / ColumnShadeW;

    /// <summary>One mid stop inside the feather so the ramp is a CURVE rather than a straight line — a linear alpha
    /// ramp is exactly the shape the eye resolves as a Mach band.</summary>
    public static float ColumnShadeMidStop => ColumnShadeHoldStop + 0.55f * (1f - ColumnShadeHoldStop);

    /// <summary>The mid stop's alpha, as a fraction of <see cref="ColumnShadeA"/>.</summary>
    public const float ColumnShadeMidFrac = 0.34f;

    /// <summary>How much darker the scrim goes under the QUEUE pane while it is up — its rows carry hover glass, and
    /// glass needs something under it. Feathers to 0 on the pane's left; its deep end is the window edge.</summary>
    public const float PaneShadeA = 0.24f;

    /// <summary>Where the queue pane's shade has finished coming up out of 0.</summary>
    public const float PaneShadeFeatherStop = 0.22f;

    /// <summary>The wide stage: nothing folded.</summary>
    public static readonly StageLayout WideStage = new(
        Wide: true, ColumnWidth: WideColumnW, ColumnFalloff: WideColumnFalloffW, ArtSize: WideArtW,
        PlayBox: WidePlayBoxW, StepBox: WideStepBoxW, SatelliteBox: SatelliteBoxW, Folded: StageControl.None);

    /// <summary>The compact stage: the identity column is a header row and <see cref="CompactFold"/> is in the "…".</summary>
    public static readonly StageLayout CompactStage = new(
        Wide: false, ColumnWidth: 0f, ColumnFalloff: 0f, ArtSize: CompactArtW,
        PlayBox: CompactPlayBoxW, StepBox: CompactStepBoxW, SatelliteBox: SatelliteBoxW, Folded: CompactFold);

    // ── derived reads the renderer uses ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The column BOX's width — the designed column plus the veil's falloff. 0 in the compact shape, where the
    /// identity is a full-width header row rather than a column.</summary>
    public float LayoutWidth => Wide ? ColumnWidth + ColumnFalloff : 0f;

    /// <summary>Where <see cref="ColumnWidth"/> falls inside <see cref="LayoutWidth"/> — the gradient stop at which the
    /// column's veil starts giving way to the base scrim. 0 when there is no column.</summary>
    public float VeilHoldStop => LayoutWidth > 0f ? ColumnWidth / LayoutWidth : 0f;

    /// <summary>Is this control ON the stage (rather than folded into the compact "…")?</summary>
    public bool Shows(StageControl c) => (Folded & c) == 0;

    /// <inheritdoc cref="Shows"/>
    public bool ShowSatellites => Shows(StageControl.Shuffle) && Shows(StageControl.Repeat);
    /// <inheritdoc cref="Shows"/>
    public bool ShowVolume => Shows(StageControl.Volume);
    /// <inheritdoc cref="Shows"/>
    public bool ShowDeviceLine => Shows(StageControl.OutputDevice);
    /// <summary>The compact shape is the only one with an overflow, and it always has one (four controls fold).</summary>
    public bool ShowOverflow => Folded != StageControl.None;

    /// <summary>A monotone "how much is the stage carrying" score — the comparand the narrowing-never-adds test asserts
    /// on. Every component is non-decreasing in width.</summary>
    internal int Richness => (Wide ? 1 : 0)
        + (ShowSatellites ? 1 : 0) + (ShowVolume ? 1 : 0) + (ShowDeviceLine ? 1 : 0)
        + (int)(ArtSize * 0.01f) + (int)(PlayBox * 0.1f) + (int)(StepBox * 0.1f);

    // ── resolution ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The seed (no previous layout ⇒ no promotion reserve) — the value the surface's signal is constructed
    /// with before its first viewport effect runs.</summary>
    public static StageLayout FromWidth(float width) => Resolve(width, null);

    /// <summary>The live resolve. <paramref name="previous"/> null = seed. Demotion immediate; promotion needs
    /// <see cref="PromotionHysteresisW"/> of reserve on top of <see cref="WideEnterW"/>.</summary>
    public static StageLayout Resolve(float width, StageLayout? previous = null)
    {
        width = MathF.Max(0f, width);
        bool candidate = width >= WideEnterW;
        bool wide = previous is { } old
            ? candidate && (old.Wide || width >= WideEnterW + PromotionHysteresisW)
            : candidate;
        return wide ? WideStage : CompactStage;
    }
}
