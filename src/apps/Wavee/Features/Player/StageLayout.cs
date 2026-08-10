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

    /// <summary>How far the column's dark veil keeps fading PAST <see cref="WideColumnW"/> before it reaches the base
    /// scrim. It is layout width (the column box is <see cref="LayoutWidth"/> wide and pads its content back to
    /// <see cref="WideColumnW"/>), because a gradient that ends exactly where the content ends reads as an edge.</summary>
    public const float WideColumnFalloffW = 120f;

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
