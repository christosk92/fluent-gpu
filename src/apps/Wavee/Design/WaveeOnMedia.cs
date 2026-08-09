using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>The ONE on-media colour ladder: everything that paints ON TOP of artwork or video — the scrim plates behind
/// a floating "…"/FAB/chip, the hairline that rings them, the ink on them, the dim under a blurred stand-in backdrop,
/// and the light-theme on-media button ramp.
///
/// <para><b>Why this file exists.</b> <c>MediaCard</c> alone hand-rolled 36 <c>ColorF.FromRgba</c> literals for this
/// one job, including THREE near-duplicate scrim ladders that disagreed with each other by 10–50 alpha steps for
/// visually identical affordances:</para>
/// <list type="bullet">
///   <item>the corner "…" / cover-action FAB — 185 · (20,20,20)@225 · 245</item>
///   <item>the inline "…" on a dark card — 132 · 190 · 220</item>
///   <item>the card save/follow button — 120 · 184 · 218</item>
/// </list>
/// <para>They are collapsed here into ONE ladder built from the MIDDLE of the three (132 · 190 · 220), except that the
/// REST rung takes the engine's own <see cref="Tok.MediaScrim"/> (black @ 0.55 = 140) rather than minting an app-local
/// near-duplicate 8 alpha steps away — the engine already owns "the chip/pill/FAB scrim plate floated over media", and
/// an app token that shadows it by 3% is exactly the drift this convergence exists to remove. The visible consequence
/// is that the corner "…" and the cover FAB got LIGHTER (185 → 140) and the save button slightly darker (120 → 140):
/// three affordances that sit on the same artwork now share one plate instead of advertising three.</para>
///
/// <para><b>Theme invariance is the point, not an oversight.</b> Ink over artwork is theme-INVARIANT
/// (<c>theming.md</c>'s leaf-value rule; the engine publishes <c>Tok.OnMedia*</c> as literal whites for this reason),
/// so these plates stay dark and this ink stays white in light mode too. The one place a THEME does change the answer —
/// the persistent light FAB on the editorial card — is expressed as a ramp OFF <see cref="Tok.OnMediaPrimary"/> rather
/// than as three hardcoded greys, so a palette preset that ever re-tints the on-media whites carries the whole button
/// with it.</para></summary>
public static class WaveeOnMedia
{
    // ── Scrim plates (a small dark surface floated over art: the "…" corner, a cover FAB, a kind chip, the eq pill) ──

    /// <summary>The resting plate. The engine's canonical on-media plate (<see cref="Tok.MediaScrim"/>, black @ 0.55).
    /// Collapses the old 185 / 142 / 132 / 150 / 120 rest fills.</summary>
    public static ColorF ScrimRest => Tok.MediaScrim;

    /// <summary>Hover. The middle of the three old ladders' hover rungs — (20,20,20)@225 · 190 · 184 → black @ 190/255.
    /// Black, not the old (20,20,20): a 20-per-channel lift under a 0.75 alpha is invisible over artwork and existed in
    /// exactly one of the three ladders.</summary>
    public static readonly ColorF ScrimHover = ColorF.FromRgba(0, 0, 0, 190);

    /// <summary>Pressed. The middle of 245 · 220 · 218.</summary>
    public static readonly ColorF ScrimPressed = ColorF.FromRgba(0, 0, 0, 220);

    /// <summary>The full-cover hover veil — a whole square of artwork dimmed so a centred FAB reads on it. A different
    /// role from <see cref="ScrimRest"/> (which is a small plate), so it keeps its own, lighter value: black @ 110/255.
    /// Collapses the row-FAB veil and the track-row buffering veil, which were the same literal already.</summary>
    public static readonly ColorF CoverScrim = ColorF.FromRgba(0, 0, 0, 110);

    /// <summary>The hairline that rings every on-media plate. The middle of the old 70 / 58 / 55, i.e. white @ 58/255 —
    /// which is also the value <c>CardLibraryAction</c>'s on-dark ring and the artist pick's hairline already used, so
    /// two of the three sites are unchanged. Doubles as the faint on-media RULE (the countdown ring's track, which was
    /// 64 — within 6 alpha steps and the same "barely-there white line over art" job).</summary>
    public static readonly ColorF Stroke = ColorF.FromRgba(255, 255, 255, 58);

    // ── Ink (the engine's on-media tiers, reused verbatim — white @ 1.0 / 0.80 / 0.60) ──────────────────────────

    /// <summary>Primary on-media ink: titles, chip labels, glyphs, the swept countdown arc. Collapses the old
    /// 224 / 225 / 230 / 235 near-whites, which were four ways of writing "white" over a dark scrim.</summary>
    public static ColorF Ink => Tok.OnMediaPrimary;

    /// <summary>Secondary on-media ink: eyebrows and subtitles over art. White @ 0.80 (204); collapses the old 200.</summary>
    public static ColorF InkSecondary => Tok.OnMediaSecondary;

    /// <summary>Tertiary on-media ink: captions / meta over art. White @ 0.60.</summary>
    public static ColorF InkTertiary => Tok.OnMediaTertiary;

    // ── Backdrop treatments ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The dim laid over a BAKED-BLUR derivative so it reads as a backdrop rather than as a stretched square
    /// (the editorial card's frosted band, the artist pick's stand-in banner). Those two disagreed by 0.03 alpha for no
    /// stated reason (0.45 / 0.42); one value now.</summary>
    public static readonly ColorF BackdropDim = ColorF.FromRgba(8, 8, 10) with { A = 0.45f };

    /// <summary>The hover spotlight's inner stop — a soft white bloom under the pointer on an editorial cover.</summary>
    public static readonly ColorF SpotlightInner = ColorF.FromRgba(255, 255, 255, 46);
    /// <summary>The hover spotlight's mid stop (it falls to transparent at the edge).</summary>
    public static readonly ColorF SpotlightMid = ColorF.FromRgba(255, 255, 255, 20);

    // ── The LIGHT on-media button ramp (the editorial card's persistent play FAB) ───────────────────────────────
    // Derived from the on-media ink rather than hardcoded greys, so a palette preset that ever re-tints the on-media
    // whites carries the button's whole ramp with it. The old literals were 255 / 235 / 215 with (12,12,14) ink; the
    // fractions below reproduce them to within one 8-bit step (255·0.92 = 234.6, 255·0.843 = 215.0).

    const float LightHoverDrop = 0.08f;
    const float LightPressedDrop = 0.157f;

    /// <summary>Rest fill of a light-on-media button: the on-media ink itself.</summary>
    public static ColorF LightButton => Tok.OnMediaPrimary;
    /// <summary>Hover fill (≈ #EBEBEB).</summary>
    public static ColorF LightButtonHover => ColorRamp.Darken(Tok.OnMediaPrimary, LightHoverDrop);
    /// <summary>Pressed fill (≈ #D7D7D7).</summary>
    public static ColorF LightButtonPressed => ColorRamp.Darken(Tok.OnMediaPrimary, LightPressedDrop);
    /// <summary>The glyph ON a light on-media button. The engine's opaque media stage (#0A0A0A) — the old literal was
    /// (12,12,14), two steps away and one more hand-mixed near-black.</summary>
    public static ColorF LightButtonInk => Tok.MediaStage;
}
