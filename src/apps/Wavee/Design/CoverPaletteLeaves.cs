using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// Cover-colour leaves: Watch subscriptions that must NOT sit in a page <c>Render()</c>. A graded batch then
/// re-renders only these nodes (or paints via a Fill bind) — never the whole Artist/Detail/NowPlaying tree.
/// </summary>
static class CoverPaletteLeaves
{
    static readonly LayoutTransition PaletteWashTransition = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(420f, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(320f, Easing.SmoothOut));

    /// <summary>Detail page wash (two-column <see cref="Surfaces.HeroWash"/> or vertical <see cref="Surfaces.DetailHeroWash"/>).</summary>
    public static Element DetailWash(string? url, string? fallbackUrl, bool immersive, bool vertical, bool disabled, string key)
        => Embed.Comp(new CoverKeyedWash.Props(url, fallbackUrl, immersive, vertical, disabled),
                      () => new CoverKeyedWash()) with { Key = key };

    /// <summary>Artist page blend wash (height follows the hero layout for the current width).</summary>
    public static Element ArtistBlendWash(string? url, float heroWidth, bool disabled, string key)
        => Embed.Comp(new CoverArtistBlendWash.Props(url, heroWidth, disabled),
                      () => new CoverArtistBlendWash()) with { Key = key };

    /// <summary>Full-bleed artist hero veil over photography.</summary>
    public static Element ArtistHeroVeil(string? url, ArtistHeroVeilAxis axis, float width, float height, string key)
        => Embed.Comp(new CoverKeyedVeil.Props(url, axis, width, height),
                      () => new CoverKeyedVeil()) with { Key = key };

    /// <summary>Publishes the page-scoped Mica tint when THIS cover is graded — no page re-render.</summary>
    public static Element ShellTint(string? url, bool ready, bool disabled, bool apply, object owner,
                                    Signal<ShellTintState>? slot, string key, string? fallbackUrl = null)
        => Embed.Comp(new CoverShellTintBinder.Props(url, fallbackUrl, ready, disabled, apply, owner, slot),
                      () => new CoverShellTintBinder()) with { Key = key };

    internal static LayoutTransition WashTransition => PaletteWashTransition;
}

/// <summary>Detail wash leaf: reads <see cref="SpotifyLive.CoverColorPlane.Watch"/> in its own Render so a colour
/// landing rebuilds only this box's Gradient.</summary>
sealed class CoverKeyedWash : Component
{
    internal sealed record Props(string? Url, string? FallbackUrl, bool Immersive, bool Vertical, bool Disabled);

    public override Element Render()
    {
        var p = UseProps<Props>();
        if (p.Disabled)
            return new BoxEl { Grow = 1f, HitTestVisible = false };

        _ = SpotifyLive.CoverColorPlane.Current.Watch(p.Url).Value;
        if (p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal))
            _ = SpotifyLive.CoverColorPlane.Current.Watch(fb).Value;

        var coverArt = Surfaces.SchemeFor(p.Url);
        var livePal = coverArt is null ? Surfaces.SchemeFor(p.FallbackUrl) : null;
        var art = coverArt ?? livePal;
        bool light = Tok.Theme == ThemeKind.Light;
        ColorF washColor = light
            ? (coverArt is { } cover ? WaveePalette.Lift(WaveePalette.Accent(cover))
                : livePal is { } lp ? WaveePalette.Accent(lp) : Tok.AccentDefault)
            : WaveePalette.BackgroundDark(art ?? WaveePalette.Neutral);

        return new BoxEl
        {
            ZStack = true, Grow = 1f, HitTestVisible = false,
            ClipToBounds = true, Corners = WaveeShell.ContentPaneCorners,
            Gradient = p.Vertical
                ? Surfaces.DetailHeroWash(washColor, p.Immersive)
                : Surfaces.HeroWash(washColor),
            Animate = CoverPaletteLeaves.WashTransition,
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

/// <summary>Shell Mica tint publisher. Watches one cover; writes <see cref="ShellTint"/> without the page Render
/// subscribing to that Watch.</summary>
sealed class CoverShellTintBinder : Component
{
    internal sealed record Props(string? Url, string? FallbackUrl, bool Ready, bool Disabled, bool Apply, object Owner,
                                 Signal<ShellTintState>? Slot);

    public override Element Render()
    {
        var p = UseProps<Props>();
        _ = SpotifyLive.CoverColorPlane.Current.Watch(p.Url).Value;
        if (p.FallbackUrl is { Length: > 0 } fb && !string.Equals(fb, p.Url, StringComparison.Ordinal))
            _ = SpotifyLive.CoverColorPlane.Current.Watch(fb).Value;
        var coverArt = p.Ready ? Surfaces.SchemeFor(p.Url) : null;
        var artPalette = coverArt ?? (p.Ready ? Surfaces.SchemeFor(p.FallbackUrl) : null);
        ColorF? micaTint = p.Disabled || !p.Apply || artPalette is not { } artScheme ? null
            : Tok.Theme == ThemeKind.Light
                ? WaveePalette.Lift(WaveePalette.ToColor(artScheme.TextBase)) with { A = 0.05f }
                : WaveePalette.TintedDark(artScheme) with { A = 0.14f };

        void SetTint(ColorF? color)
        {
            if (p.Slot is not null) p.Slot.Value = new ShellTintState(color, p.Owner);
        }
        void ClearTint()
        {
            if (p.Slot is not null && ReferenceEquals(p.Slot.Peek().Owner, p.Owner)) p.Slot.Value = default;
        }

        UseEffect(() => SetTint(micaTint),
            DepKey.From(HashCode.Combine(p.Url, micaTint.HasValue, micaTint.GetValueOrDefault(), Tok.Theme, p.Ready, p.Disabled, p.Apply)));
        UseActivation(onActivated: () => SetTint(micaTint), onDeactivated: ClearTint);
        UseEffect(() => (Action?)ClearTint, DepKey.Empty);

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }
}
