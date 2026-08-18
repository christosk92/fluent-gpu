using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>A two-anchor active scroll interval — the authoring form of <see cref="FluentGpu.Animation.ScrollBind"/>'s
/// range. The reconciler bakes the anchors to two scroll-px bounds <c>(a,b)</c> (literal-px at reconcile; geometry
/// anchors at <c>ArrangeViewport</c>), from which the per-frame eval derives <c>t = clamp01((sample − a)/(b − a))</c>.
/// A <c>default</c> range (<see cref="HasValue"/> = false) means "the whole scroller" — <c>[0, maxOffset]</c>.</summary>
public readonly record struct ScrollRange
{
    public ScrollBindAnchor A { get; init; }
    public float Av { get; init; }
    public ScrollBindAnchor B { get; init; }
    public float Bv { get; init; }
    /// <summary>False for a <c>default</c> range (⇒ whole scroller); true once authored via a factory below.</summary>
    public bool HasValue { get; init; }

    /// <summary>Literal scroll-px window <c>[a, b]</c>.</summary>
    public static ScrollRange Px(float a, float b)
        => new() { A = ScrollBindAnchor.OffsetPx, Av = a, B = ScrollBindAnchor.OffsetPx, Bv = b, HasValue = true };
}

/// <summary>One authored scroll-driven binding on an element (the declarative form of <see cref="FluentGpu.Animation.ScrollBind"/>).
/// The generic, hookable scroll surface: bind any compositor property to a normalized scroll progress, pin it (sticky),
/// or stretch it (overscroll hero). The reconciler compiles each entry to a POD <see cref="FluentGpu.Animation.ScrollBind"/>
/// row evaluated allocation-free in the frame loop.</summary>
public readonly record struct ScrollBindDsl
{
    /// <summary>Which scroller scalar drives this binding (offset / overscroll band).</summary>
    public ScrollChannel From { get; init; }
    /// <summary>Which compositor property this binding writes (transform / opacity / clip / presented size).</summary>
    public BindSink To { get; init; }
    /// <summary>The active scroll interval; omit for the whole scroller (<c>[0, maxOffset]</c>).</summary>
    public ScrollRange Range { get; init; }
    /// <summary>Output value at progress 0.</summary>
    public float OutStart { get; init; }
    /// <summary>Output value at progress 1.</summary>
    public float OutEnd { get; init; }
    /// <summary>Clamp progress to [0,1] (default). Clear for an extrapolating parallax that keeps translating past the range.</summary>
    public bool Clamp { get; init; }
    /// <summary>Shaping applied to progress before the output lerp (0 = linear).</summary>
    public Easing Ease { get; init; }

    // ── shorthands for the two re-expressed legacy behaviors ──
    /// <summary>Sticky: pin this node at the viewport top at this inset (replaces the old <c>StickyTop</c>).</summary>
    public float? PinTop { get; init; }
    /// <summary>Overscroll hero: scale uniformly from origin (0.5,0) by the top overscroll band, cancelling the band's
    /// content shift (replaces the old <c>ScrollStretchHeader</c>). The hero authors <c>TransformOriginX=0.5, Y=0</c>.</summary>
    public bool StretchFromTop { get; init; }
    /// <summary>Sticky clip: hold this node's ClipRect TOP at the viewport top + this inset — the paint dual of
    /// <see cref="PinTop"/>. The node scrolls normally but its pixels STOP at the viewport-anchored line, so the
    /// page's real backdrop (Mica/tint) — not this node's content — shows behind chrome pinned on that line.
    /// Released (no clip) while the line sits at/above the node's top. This bind OWNS the node's ClipRect — do not
    /// combine with a <see cref="BindSink.ClipTop"/> bind on the same node.
    /// <see cref="OnFlag"/> observes the clip's engage/release edge (the :stuck analog).
    /// An <c>EdgeFade</c> authored on the SAME node feathers from the CLIP LINE while this bind is engaged (the recorder
    /// anchors the fade rect at the visible box), so the hard cut reads as a dissolve; gate the spec on <see cref="OnFlag"/>
    /// if the band must not feather the node's own edge once the clip releases.</summary>
    public float? ClipTopAtViewport { get; init; }

    // ── predicate channel hook (the CSS :stuck-style observable) ──
    /// <summary>Fires once per edge flip of the watched flag (UI-thread, never per-frame). For a <see cref="PinTop"/>
    /// bind it observes THIS node's pinned state; otherwise it observes <see cref="FlagBit"/> of the scroller's flags.</summary>
    public Action<bool>? OnFlag { get; init; }
    /// <summary>Which scroller flag bit <see cref="OnFlag"/> observes for a non-pin bind (e.g. <c>ScrollState.ScrolledFwdBit</c>,
    /// <c>MovingNowBit</c>). Ignored for a pin bind (it observes the node's own pinned transition).</summary>
    public byte FlagBit { get; init; }

    public ScrollBindDsl()
    {
        OutStart = 0f;
        OutEnd = 1f;
        Clamp = true;
    }
}

/// <summary>The six real Wavee scroll effects (scroll-v3-plan §7.3), as extension methods that compile to the same
/// POD <see cref="ScrollBindDsl"/> rows their call sites author by hand today (<c>ArtistPage.Hero.cs</c>,
/// <c>DetailTracks.cs</c>, <c>ConcertFilterBar.cs</c>, <c>ArtistPage.cs</c>, <c>ArtistPage.AlbumExpand.cs</c>,
/// <c>ContextBand.cs</c>) — zero new engine ops. Each recipe returns a NEW <see cref="Element"/> record with the
/// row(s) appended to <see cref="Element.ScrollBinds"/> (records are immutable; existing rows are preserved). Allocate
/// freely — these run at authoring/render time, never per-frame.
/// <para><b>Signatures widened beyond the plan's shorthand where the cited call sites need it</b> (documented per
/// recipe below): <see cref="Collapse"/> takes an explicit <c>fromH</c> (the PresentedH row's OutStart genuinely needs
/// a number — both cited sites pass one, sourced from a local the caller already has); <see cref="ClipBelow"/> gained
/// an optional <c>onFlag</c> (the AlbumExpand.cs site observes the clip engage/release edge); <see cref="Reveal"/>
/// takes an explicit <c>revealStart</c> (both cited sites derive it per-page, e.g. <c>ArtistHeroLayout.CompactRevealStart</c>)
/// and reads <c>Motion.ReducedMotion</c> internally instead of a caller-supplied token (matching
/// <c>MotionHooks.UseEntrance</c>'s convention and <c>ContextBand.RevealBinds</c>'s own current behavior) rather than
/// branching on it — "reduced motion is a VALUE" per the rework's rule, so the row shape never changes, only the
/// translate amplitude collapses to 0.</para></summary>
public static class ScrollRecipes
{
    private static Element Append(this Element el, params ScrollBindDsl[] rows)
        => el with { ScrollBinds = [.. el.ScrollBinds, .. rows] };

    /// <summary>iOS/Spotify overscroll hero: scale uniformly from the top, cancelling the pull-band's content shift.
    /// Compiles to ONE row (<see cref="ScrollBindDsl.StretchFromTop"/>) — <c>ArtistPage.Hero.cs:107</c>.</summary>
    public static Element StretchFromTop(this Element el) => el.Append(new ScrollBindDsl { StretchFromTop = true });

    /// <summary>Parallax translate: this node moves <c>overPx * fraction</c> over the first <c>overPx</c> of scroll
    /// (a background photo drifting slower than the fold). Compiles to ONE <c>Offset→TransY</c> row —
    /// <c>ArtistPage.Hero.cs:108-114</c>.</summary>
    public static Element ParallaxY(this Element el, float fraction, float overPx) => el.Append(new ScrollBindDsl
    {
        From = ScrollChannel.Offset, To = BindSink.TransY,
        Range = ScrollRange.Px(0f, overPx),
        OutStart = 0f, OutEnd = overPx * fraction, Ease = Easing.Linear,
    });

    /// <summary>Pin-and-shrink: hold this node at the viewport top (<see cref="ScrollBindDsl.PinTop"/>, inset 0) while
    /// its <see cref="BindSink.PresentedH"/> eases from <paramref name="fromH"/> to <paramref name="toH"/> over the
    /// first <paramref name="overPx"/> of scroll (a hero collapsing into compact chrome). Compiles to TWO rows — the
    /// hero root: <c>ArtistPage.Hero.cs:202-213</c>, <c>DetailTracks.cs:1456-1461</c> (<c>VerticalHeroBinds</c>; that
    /// site's <c>expandedHeight</c> local is this <paramref name="fromH"/> — both cited sites already have the value
    /// in scope, ArtistPage.Hero's IS the node's own declared <c>Height</c>).</summary>
    public static Element Collapse(this Element el, float fromH, float toH, float overPx) => el.Append(
        new ScrollBindDsl { PinTop = 0f },
        new ScrollBindDsl
        {
            From = ScrollChannel.Offset, To = BindSink.PresentedH,
            Range = ScrollRange.Px(0f, overPx),
            OutStart = fromH, OutEnd = toH,
        });

    /// <summary>Sticky: pin this node at the viewport top at <paramref name="top"/> inset; <paramref name="onStuck"/>
    /// observes the pinned/unpinned edge (CSS <c>:stuck</c>). Compiles to ONE <see cref="ScrollBindDsl.PinTop"/> row —
    /// <c>DetailTracks.cs:1463-1467</c>, <c>ConcertFilterBar.cs:103</c>, <c>ArtistPage.cs:274</c>.</summary>
    public static Element Sticky(this Element el, float top = 0f, Action<bool>? onStuck = null)
        => el.Append(new ScrollBindDsl { PinTop = top, OnFlag = onStuck });

    /// <summary>Sticky clip: hold this node's ClipRect top at the viewport top + <paramref name="insetPx"/> (chrome
    /// pinned on that line shows the real backdrop, not this node's content, sliding through it);
    /// <paramref name="onFlag"/> observes the clip engage/release edge. Compiles to ONE
    /// <see cref="ScrollBindDsl.ClipTopAtViewport"/> row — <c>ArtistPage.AlbumExpand.cs:596</c>, <c>ArtistPage.cs:303,324</c>.</summary>
    public static Element ClipBelow(this Element el, float insetPx, Action<bool>? onFlag = null)
        => el.Append(new ScrollBindDsl { ClipTopAtViewport = insetPx, OnFlag = onFlag });

    /// <summary>Arrival reveal: opacity 0→1 with a small upward settle, ramped over
    /// <c>[revealStart, revealStart + overPx]</c> — reversible, tracks the finger rather than snapping at a threshold.
    /// Compiles to TWO rows (Opacity always; TransY always present too, but its amplitude collapses to 0 under
    /// <c>Motion.ReducedMotion</c> — reduced motion is a VALUE here, never a branch, so the row shape never changes) —
    /// <c>ContextBand.cs:169-186</c> (<c>RevealBinds</c>).</summary>
    public static Element Reveal(this Element el, float revealStart, float overPx, float dy)
    {
        bool reduced = FluentGpu.Dsl.Motion.ReducedMotion;
        return el.Append(
            new ScrollBindDsl
            {
                From = ScrollChannel.Offset, To = BindSink.Opacity,
                Range = ScrollRange.Px(revealStart, overPx),
                OutStart = 0f, OutEnd = 1f, Ease = Easing.Linear,
            },
            new ScrollBindDsl
            {
                From = ScrollChannel.Offset, To = BindSink.TransY,
                Range = ScrollRange.Px(revealStart, overPx),
                OutStart = reduced ? 0f : dy, OutEnd = 0f, Ease = Easing.Linear,
            });
    }
}
