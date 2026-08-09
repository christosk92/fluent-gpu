using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// Cover-colour leaves: Watch subscriptions that must NOT sit in a page <c>Render()</c>. A graded batch then
/// re-renders only these nodes (or paints via a Fill bind) — never the whole Artist/Detail/NowPlaying tree.
/// </summary>
static class CoverPaletteLeaves
{
    /// <summary>THE detail page's ground: one OPAQUE art-derived plane (<see cref="WaveePalette.PageTone"/>) behind
    /// both hero arms, carrying the blurred background extension and — in hero-only mode — the fade back to the neutral
    /// surface below the hero. It replaces the stack of top-anchored alpha washes the two arms used to paint.
    ///
    /// <para>The tone never leaves this leaf. It briefly had to: the sticky context band flattened its opaque material
    /// over the page's ground and needed to know what that ground was. The band paints NOTHING now — scrolled content
    /// is clipped at its lower edge and this plane simply shows through it — so the publication, and the signal it
    /// wrote, are gone.</para></summary>
    public static Element PageTonePlane(string? url, string? fallbackUrl, bool disabled, float heroBand,
                                        float pageHeight, bool heroOnly, Image? cover, string key)
        => Embed.Comp(new CoverPageTonePlane.Props(url, fallbackUrl, disabled, heroBand, pageHeight, heroOnly, cover),
                      () => new CoverPageTonePlane()) with { Key = key };

    /// <summary>Artist page blend wash (height follows the hero layout for the current width).</summary>
    public static Element ArtistBlendWash(string? url, float heroWidth, bool disabled, string key)
        => Embed.Comp(new CoverArtistBlendWash.Props(url, heroWidth, disabled),
                      () => new CoverArtistBlendWash()) with { Key = key };

    /// <summary>Full-bleed artist hero veil over photography.</summary>
    public static Element ArtistHeroVeil(string? url, ArtistHeroVeilAxis axis, float width, float height, string key)
        => Embed.Comp(new CoverKeyedVeil.Props(url, axis, width, height),
                      () => new CoverKeyedVeil()) with { Key = key };

    /// <summary>Publishes the page-scoped shell material tint when THIS cover is graded — no page re-render.</summary>
    public static Element ShellTint(string? url, bool ready, bool disabled, bool apply, object owner,
                                    Signal<ShellMaterialState>? slot, string key, string? fallbackUrl = null)
        => Embed.Comp(new CoverShellTintBinder.Props(url, fallbackUrl, ready, disabled, apply, owner, slot),
                      () => new CoverShellTintBinder()) with { Key = key };
}

/// <summary>The detail page's opaque art-derived ground. The <c>CoverColorPlane.Watch</c> subscription lives HERE, in
/// a leaf, never in a page <c>Render</c> — so a grading arriving mid-scroll re-renders this one node and nothing else;
/// the rail, the hero and the virtualized track list are untouched. The bound <c>Fill</c> keeps the brush itself on
/// the compositor, and <c>BrushTransitionMs</c> is what turns a grading arrival into a cross-fade rather than a snap.
///
/// <para>This plane is a PAGE-ROOT sibling of the scrolling page, never a child of it, which is what makes the sticky
/// context band's offset model work: the band paints nothing, content is clipped at its lower edge, and what shows in
/// that gap is this node — the record's own tone, with the blurred backdrop below at the top of the page.</para>
///
/// <para><b>The background extension</b> (the art melting into the surface) is the same node: a scaled, blurred copy of
/// the cover masked away over the hero band. It uses <c>ImageEl.BakedBlur</c> — the blur is baked ONCE per (source,
/// size, sigma) into a derived texture and the node is then an ordinary quad, so scrolling the page costs no Gaussian.
/// A <c>BoxEl.Blur</c> here would instead be a pooled offscreen RT plus a separable Gaussian with a ~3σ halo EVERY
/// frame, which is what that property exists for (animated softening) and not this.</para></summary>
sealed class CoverPageTonePlane : Component
{
    internal sealed record Props(string? Url, string? FallbackUrl, bool Disabled, float HeroBand, float PageHeight,
                                 bool HeroOnly, Image? Cover);

    /// <summary>Blur radius of the background extension, and the resolution it is baked at. Large sigma at half
    /// resolution is the cheap end of the bake and the only honest one at this scale — a lightly blurred cover reads as
    /// a mistake, not as a backdrop.</summary>
    const float BackdropSigmaDip = 72f, BackdropResolutionScale = 0.5f;
    const float BackdropSaturation = 1.35f;
    const float BackdropAlphaDark = 0.40f, BackdropAlphaLight = 0.45f;

    public override Element Render()
    {
        var p = UseProps<Props>();

        // The Watch subscriptions, resolved ONCE per render and read here. Hoisting them out of the Fill closure
        // matters: Watch takes the plane's lock, and a bound brush is re-evaluated on the paint path. Render still has
        // to subscribe (not just the brush) because the ARRIVAL of a grading is what decides whether this node exists
        // at all — a page with no tone paints nothing and mounts no backdrop.
        var plane = SpotifyLive.CoverColorPlane.Current;
        _ = plane.Watch(p.Url).Value;
        if (p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal))
            _ = plane.Watch(fb).Value;

        ColorF? resolved = p.Disabled ? null : Resolve(p);
        if (p.Disabled || resolved is null)
            return new BoxEl { Grow = 1f, HitTestVisible = false };

        var kids = new List<Element>(2);
        if (Backdrop(p) is { } backdrop) kids.Add(backdrop);
        if (p.HeroOnly && HeroOnlyVeil(p) is { } veil) kids.Add(veil);

        return new BoxEl
        {
            ZStack = true, Grow = 1f, HitTestVisible = false,
            ClipToBounds = true, Corners = WaveeShell.ContentPaneCorners,
            // BOUND: the brush stays a compositor value, so a theme/preset re-fire lands without this subtree being
            // rebuilt, and the 250ms ramp cross-fades a grading arrival instead of snapping to it.
            Fill = Prop.Of(() => Resolve(p) ?? WaveeColors.ContentSurface),
            BrushTransitionMs = WaveeMotion.Standard,
            Children = kids.ToArray(),
        };
    }

    /// <summary>Cover grading → the page's ground. Cheap: two dictionary probes and the clamp; no subscription (the
    /// caller owns that), so it is safe on the paint path.</summary>
    static ColorF? Resolve(Props p)
        => WaveePalette.PageTone(Surfaces.SchemeFor(p.Url) ?? Surfaces.SchemeFor(p.FallbackUrl), Tok.Theme);

    /// <summary>The blurred, oversaturated cover behind the hero, masked to nothing by the hero's lower edge.</summary>
    static Element? Backdrop(Props p)
    {
        string? url = ImageSource.UrlFor(p.Cover, preferLargest: false);
        if (url is not { Length: > 0 }) return null;
        float band = DetailVerticalLayout.BackdropBandFor(p.HeroBand);
        // The mask reaches zero exactly at the band's lower edge — i.e. at the hero's own bottom — so the art has
        // already become the tone by the time the track list starts.
        float feather = band * DetailVerticalLayout.BackdropFadeFraction;
        // A ZStack child with an explicit Height and NO Width fills the stack's width and sits flush at the top —
        // exactly the band this wants, with no alignment authored.
        return new BoxEl
        {
            Height = band,
            ZStack = true, ClipToBounds = true, HitTestVisible = false,
            Opacity = Tok.Theme == ThemeKind.Light ? BackdropAlphaLight : BackdropAlphaDark,
            Children =
            [
                Ui.Image(url, ImageFit.Cover, aspect: float.NaN, decodePx: 512f, corners: 0f,
                    placeholder: ColorF.Transparent, blurHash: p.Cover?.BlurHash,
                    transition: ImageTransition.Fade(WaveeMotion.Standard)) with
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    BakedBlur = new BakedBlurSpec(BackdropSigmaDip, BackdropResolutionScale),
                    Saturation = BackdropSaturation,
                    Mask = new ImageMaskSpec(EdgeMask.Bottom, 0f, 0f, 0f, feather),
                },
            ],
        };
    }

    /// <summary>Hero-only mode: the tone survives the hero band and dissolves into the neutral content surface below
    /// it. The veil fades the neutral ground IN over the tone (rather than fading the tone OUT to nothing), so the
    /// composite stays opaque the whole way down — the plane's job is to BE the ground, not to tint one.</summary>
    static Element? HeroOnlyVeil(Props p)
    {
        float pageH = p.PageHeight > 1f ? p.PageHeight : 0f;
        if (pageH <= 1f) return null;
        float band = DetailVerticalLayout.BackdropBandFor(p.HeroBand);
        float start = Math.Clamp(band / pageH, 0.12f, 0.80f);
        float end = MathF.Min(1f, start + 0.22f);
        ColorF ground = WaveeColors.ContentSurface;
        return new BoxEl
        {
            HitTestVisible = false,   // sized by the ZStack (no explicit extent ⇒ full bleed)
            Gradient = GradientDown(
                new GradientStop(0f, ground with { A = 0f }),
                new GradientStop(start, ground with { A = 0f }),
                new GradientStop(end, ground),
                new GradientStop(1f, ground)),
        };
    }
}

sealed class CoverArtistBlendWash : Component
{
    internal sealed record Props(string? Url, float HeroWidth, bool Disabled);

    public override Element Render()
    {
        var p = UseProps<Props>();
        if (p.Disabled) return new BoxEl();

        _ = SpotifyLive.CoverColorPlane.Current.Watch(p.Url).Value;
        var pagePal = Surfaces.SchemeFor(p.Url);
        bool light = Tok.Theme == ThemeKind.Light;
        ColorF wash = light
            ? (pagePal is { } wp ? WaveePalette.Lift(WaveePalette.Accent(wp)) : Tok.AccentDefault)
            : WaveePalette.BackgroundDark(pagePal ?? WaveePalette.Neutral);
        float heroWidth = p.HeroWidth;
        return new BoxEl
        {
            Height = ArtistHeroLayout.BlendBackdropHeightFor(heroWidth), HitTestVisible = false,
            Gradient = GradientDown(
                new GradientStop(0f, wash with { A = light ? 0.20f : 0.30f }),
                new GradientStop(ArtistHeroLayout.BlendBoundaryFor(heroWidth), wash with { A = light ? 0.06f : 0.08f }),
                new GradientStop(1f, wash with { A = 0f })),
        };
    }
}

/// <summary>Artist hero photography veil — cover-keyed so a late grading does not rebuild Banner.</summary>
sealed class CoverKeyedVeil : Component
{
    internal sealed record Props(string? Url, ArtistHeroVeilAxis Axis, float Width, float Height);

    public override Element Render()
    {
        var p = UseProps<Props>();
        _ = SpotifyLive.CoverColorPlane.Current.Watch(p.Url).Value;
        var pagePal = Surfaces.SchemeFor(p.Url);
        var chromePal = Surfaces.ChromeSchemeFor(p.Url);
        ColorF accent = chromePal is { } pal ? WaveePalette.ChromeAccent(pal) : Tok.AccentDefault;
        ColorF washAccent = pagePal is { } wp ? WaveePalette.Lift(WaveePalette.Accent(wp)) : accent;
        return new BoxEl
        {
            Width = p.Width, Height = p.Height, HitTestVisible = false,
            Gradient = Surfaces.ArtistHeroVeil(washAccent, p.Axis),
        };
    }
}

/// <summary>Shell material tint publisher. Watches one cover; writes <see cref="ShellMaterialState"/> without the page
/// Render subscribing to that Watch. Flat arm only (<c>Wash: null</c>) — detail/artist pages never publish the radial
/// three-layer wash, which belongs to Home.</summary>
sealed class CoverShellTintBinder : Component
{
    internal sealed record Props(string? Url, string? FallbackUrl, bool Ready, bool Disabled, bool Apply, object Owner,
                                 Signal<ShellMaterialState>? Slot);

    public override Element Render()
    {
        var p = UseProps<Props>();
        _ = SpotifyLive.CoverColorPlane.Current.Watch(p.Url).Value;
        if (p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal))
            _ = SpotifyLive.CoverColorPlane.Current.Watch(fb).Value;
        var coverArt = p.Ready ? Surfaces.SchemeFor(p.Url) : null;
        var artPalette = coverArt ?? (p.Ready ? Surfaces.SchemeFor(p.FallbackUrl) : null);
        // A low-alpha art tone published over the shell's deterministic opaque ground (not a backdrop scrim): it warms
        // the ground, never replaces it. Null ⇒ the bare ground.
        ColorF? shellTint = p.Disabled || !p.Apply || artPalette is not { } artScheme ? null
            : Tok.Theme == ThemeKind.Light
                ? WaveePalette.Lift(WaveePalette.ToColor(artScheme.TextBase)) with { A = 0.05f }
                : WaveePalette.TintedDark(artScheme) with { A = 0.14f };

        void SetTint(ColorF? color)
        {
            if (p.Slot is not null) p.Slot.Value = new ShellMaterialState(p.Owner, color, null);
        }
        void ClearTint()
        {
            if (p.Slot is not null && ReferenceEquals(p.Slot.Peek().Owner, p.Owner)) p.Slot.Value = default;
        }

        UseEffect(() => SetTint(shellTint),
            DepKey.From(HashCode.Combine(p.Url, shellTint.HasValue, shellTint.GetValueOrDefault(), Tok.Theme, p.Ready, p.Disabled, p.Apply)));
        UseActivation(onActivated: () => SetTint(shellTint), onDeactivated: ClearTint);
        UseEffect(() => (Action?)ClearTint, DepKey.Empty);

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}
