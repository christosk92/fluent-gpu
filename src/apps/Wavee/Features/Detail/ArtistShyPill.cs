using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The floating "shy" artist pill — revealed once the hero scrolls past the viewport top (the sticky sentinel in the page
// body flips `pinned`). Kept SMALL (avatar + name + monthly listeners + Play + Follow) and rendered through a pass-through
// overlay so it never blocks scrolling.
//
// Reverted from a self-pinning ScrollBinds rewrite: moving the pill inline as a second PinTop=0 sibling of the hero
// caused a worse regression (the hero's own collapse got stuck, plus a broken empty card) that couldn't be fixed
// blind without live visual iteration. Back to the page-level-overlay + sentinel/Signal bridge — the last confirmed
// working positioning — but keeping the fixes that don't depend on positioning:
//
// Re-pushed live props (the WaveeEqualizerCurve/G4 idiom), NOT a routeKey-keyed Embed.Comp(factory): a keyed remount
// on every artist→artist hop would unmount the outgoing instance mid-exit-animation while a fresh one mounts at the
// same overlay slot, so both could render simultaneously for a frame span. One stable component instance across the
// page's lifetime; Uri/Artist updates flow through Props instead of a remount, so there is never more than one pill.
static class ArtistShyPill
{
    /// <summary>The compact header lane's height: a 36px avatar plus Spacing.XXS top and bottom. The old 56px surface
    /// forced every sticky facet header below a hero-sized blank strip across the whole viewport even though only the
    /// centred pill occupied it.</summary>
    public const float Height = 40f;
    /// <summary>The pill overlay slot's top padding on the artist page. This is also part of the sticky-header inset, so
    /// keep it to the smallest token that still detaches the acrylic capsule from the command chrome.</summary>
    public const float TopMargin = Spacing.XS;
    /// <summary>Vertical space the floating pill claims at the viewport top. ANYTHING that pins to the artist page's
    /// viewport top must start below this or it slides under the pill — which is what the sticky discography facet
    /// header did. Consumed by the overlay slot's padding AND by the header's PinTop, so the two cannot drift.</summary>
    public const float Clearance = TopMargin + Height;   // 44

    internal sealed record Props(string Uri, Loadable<Artist> Artist, Services Svc, Signal<bool> Pinned);

    public static Element Create(string uri, Loadable<Artist> artist, Services svc, Signal<bool> pinned)
        => Embed.Comp(new Props(uri, artist, svc, pinned), () => new ArtistShyPillCore());
}

sealed class ArtistShyPillCore : Component
{
    static readonly LayoutTransition Presence = new(
        TransitionChannels.Opacity,
        MotionTok.ControlNormal.ToDynamics(),
        Enter: new EnterExit(Dy: -Spacing.M, Sx: 0.96f, Sy: 0.96f, Opacity: 0f, Active: true, Blur: 3f),
        Exit: new EnterExit(Dy: -Spacing.S, Sx: 0.985f, Sy: 0.985f, Opacity: 0f, Active: true, Blur: 2f),
        ExitDynamics: MotionTok.ControlFast.ToDynamics());

    public override Element Render()
    {
        var p = UsePropsOrDefault<ArtistShyPill.Props>();
        if (p is null) return new BoxEl();
        // Watch this artist's OWN picture, so a LATE-landing grading recolours the pill's Play (Surface below derives the
        // chrome accent from it). The pill cannot ride the page's re-render for this: its props are re-pushed through an
        // EQUALITY-GATED update, so an unchanged Props record does not re-run this Render — without its own subscription
        // the pill would keep whatever accent existed on the frame it mounted (the semantic blue) while the hero beside
        // it repainted in the cover's hue. Same one-cover idiom as ArtistPage.cs / DetailShell.cs, never the global epoch.
        _ = SpotifyLive.CoverColorPlane.Current.Watch(ArtistPage.PaletteImageUrl(p.Artist.Value.Value)).Value;
        // Gate on Ready, not just pinned: the pill is a scrolled-past-hero affordance, so showing its real avatar +
        // monthly-listeners over the page's loading skeleton both leaks real data early and floats a solid card on top
        // of the shimmer grid. While Pending/Failed it stays hidden; on Ready, if still scrolled past the hero, it
        // animates in. KeepAlive owns page visibility and detaches this entire subtree synchronously on navigation;
        // page deactivation must not be converted into a local animated exit.
        return Flow.Show(
            () => p.Pinned.Value && p.Artist.State.Value == (byte)LoadState.Ready,
            new BoxEl
            {
                Animate = Presence,
                TransformOriginX = 0.5f,
                TransformOriginY = 0f,
                // A plain static builder, not a nested component with its own frozen constructor closure — reading
                // p.Artist.Value.Value here (inside this reactive Render()) means live artist-data updates (e.g. async
                // stats hydration) still reach the card with no remount, the same reactivity the original Loadable-based
                // design relied on, without needing a per-uri Key to avoid staleness.
                Children = [Surface(p.Uri, p.Artist.Value.Value, p.Svc)],
            });
    }

    static Element Surface(string uri, Artist a, Services svc)
    {
        // Match the page's cover-extracted CHROME accent exactly (ArtistPage.cs) — the pill's Play sits in the same
        // visual sentence as the hero's, so the two must not differ in chroma.
        ColorF accent = Surfaces.ChromeSchemeFor(ArtistPage.PaletteImageUrl(a)) is { } pal
            ? WaveePalette.ChromeAccent(pal)
            : Tok.AccentDefault;
        return new BoxEl
        {
            Direction = 0, Height = ArtistShyPill.Height, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.L, Spacing.XXS),
            Corners = Radii.FullAll, Acrylic = Tok.AcrylicFlyout, Fill = Tok.FillLayerDefault, Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Children =
            [
                new BoxEl { Width = 36f, Height = 36f, Shrink = 0f, Corners = Radii.Circle(36f), ClipToBounds = true,
                    Children = [ Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 36f, 36f, Radii.Full, decodePx: 256) ] },
                // The subline carries the upcoming release INSTEAD of the listener count while one is pending. The pill
                // exists precisely for the scrolled-past state, which is exactly when the hero's own countdown pill has
                // gone — so this is the only place the announcement survives, and it is worth more than a stat that has
                // not changed since the page loaded. This replaces rather than adds a third text fact, keeping the
                // compact 40-DIP lane quiet and single-purpose.
                new BoxEl { Direction = 1, Gap = 1f,
                    Children =
                    [
                        new TextEl(a.Name) { Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        a.Extras?.PreRelease is { IsUpcoming: true, ReleaseAt: { } due }
                            ? new TextEl(Loc.Get(Strings.Detail.PreReleaseEyebrow) + " · " + PreReleaseCountdown.Remaining(due - DateTimeOffset.UtcNow))
                                { Size = 12f, Weight = 600, Color = accent, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                            : new TextEl(Strings.Artist.MonthlyListeners(a.MonthlyListeners.ToString("N0")))
                                { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ] },
                // The WaveeCta media pill on the page's lifted cover accent. Its 32-DIP compact variant fits inside the
                // 40-DIP lane without making the overlay reserve hero-sized vertical space.
                WaveeCta.Accent(Loc.Get(Strings.Artist.Play), accent, () => _ = svc.Player.PlayAsync(uri, 0),
                    minHeight: 32f),
                Embed.Comp(() => new FollowButton(uri, a.Name)) with { Key = "artist-pill-follow:" + uri },
            ],
        };
    }
}
