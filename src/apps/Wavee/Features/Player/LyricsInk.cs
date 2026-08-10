using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>
/// WHICH INK a <see cref="LyricsView"/> paints with — the one seam between the two surfaces that mount the same view.
///
/// <para>The rail's lyrics panel sits on a THEME surface, so it takes the theme's text rungs and flips with them. The
/// immersive STAGE sits ON MEDIA — it is single-theme art-dark in both themes — so it takes
/// <see cref="WaveeOnMedia"/>'s theme-INVARIANT whites. Before this seam existed there was no choice to make: the view
/// always painted <c>Tok.TextPrimary</c>, which is precisely why the stage's base scrim had to flip white in light
/// theme to keep the lyrics readable, which is what forced every region of stage chrome to bring its own boxed dark
/// veil, which is what made the surface a two-world collage. One flag removes the whole chain.</para>
///
/// <para><b>It is a MODE, not a captured colour.</b> The struct holds a bool and resolves the token at the point of
/// consumption, so the rail's ink still re-reads on a live theme flip (a <c>ColorF</c> frozen into a component's
/// constructor would not — component props freeze at mount). The stage's answer does not depend on the theme at all,
/// so a flip is a no-op there by construction rather than by luck.</para>
/// </summary>
readonly record struct LyricsInk(bool OnMedia)
{
    /// <summary>The rail: theme rungs, flips with the theme.</summary>
    public static LyricsInk Theme => new(false);

    /// <summary>The stage: on-media whites, identical in both themes.</summary>
    public static LyricsInk Media => new(true);

    // ── ink ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A SUNG glyph, the active line, a lit interlude dot — the loudest thing on the surface.</summary>
    public ColorF Primary => OnMedia ? WaveeOnMedia.Ink : Tok.TextPrimary;

    /// <summary>An inactive line's text and the secondary (translation / romanization) layer.</summary>
    public ColorF Secondary => OnMedia ? WaveeOnMedia.InkSecondary : Tok.TextSecondary;

    /// <summary>Meta — the debug/status captions that are not lyric text.</summary>
    public ColorF Tertiary => OnMedia ? WaveeOnMedia.InkTertiary : Tok.TextTertiary;

    // ── the resync chip ──────────────────────────────────────────────────────────────────────────────────────────────
    // The one PLATE the reading surface owns (a detached-scroll "back to the song" pill). On media it takes the
    // on-media scrim ladder, because a theme plate under theme-invariant white ink is the same invisibility bug the ink
    // seam exists to remove — one surface, one ladder.

    /// <inheritdoc cref="Plate"/>
    public ColorF Plate => OnMedia ? WaveeOnMedia.ScrimRest : Tok.FillSolidBase with { A = 0.92f };
    /// <inheritdoc cref="Plate"/>
    public ColorF PlateHover => OnMedia ? WaveeOnMedia.ScrimHover : Tok.FillSubtleSecondary;
    /// <inheritdoc cref="Plate"/>
    public ColorF PlatePressed => OnMedia ? WaveeOnMedia.ScrimPressed : Tok.FillSubtleTertiary;
    /// <inheritdoc cref="Plate"/>
    public ColorF PlateStroke => OnMedia ? WaveeOnMedia.Stroke : Tok.StrokeCardDefault;

    /// <summary>The chip's progress ring: the accent on a theme plate, plain white on media (the stage spends its
    /// art-derived accent on exactly four jobs, and a resync spinner is not one of them).</summary>
    public ColorF RingFill => OnMedia ? WaveeOnMedia.Ink : Tok.AccentDefault;
    /// <inheritdoc cref="RingFill"/>
    public ColorF RingTrack => OnMedia
        ? WaveeOnMedia.Ink with { A = 0.30f }
        : Tok.StrokeControlDefault with { A = 0.55f };
}
