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
    internal sealed record Props(string Uri, Loadable<Artist> Artist, Services Svc, Signal<bool> Pinned);

    public static Element Create(string uri, Loadable<Artist> artist, Services svc, Signal<bool> pinned)
        => Embed.Comp(new Props(uri, artist, svc, pinned), () => new ArtistShyPillCore());
}

sealed class ArtistShyPillCore : Component
{
    static readonly LayoutTransition Presence = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(280f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: -12f, Sx: 0.96f, Sy: 0.96f, Opacity: 0f, Active: true, Blur: 3f),
        Exit: new EnterExit(Dy: -8f, Sx: 0.985f, Sy: 0.985f, Opacity: 0f, Active: true, Blur: 2f),
        ExitDynamics: TransitionDynamics.Tween(170f, Easing.SmoothOut));

    public override Element Render()
    {
        var p = UsePropsOrDefault<ArtistShyPill.Props>();
        if (p is null) return new BoxEl();
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
        // Match the page's cover-extracted accent (lifted) so the floating pill isn't default-blue over an accented page.
        ColorF accent = Surfaces.SchemeFor(a.Image?.Url) is { } pal ? WaveePalette.Lift(WaveePalette.Accent(pal)) : Tok.AccentDefault;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.L, Spacing.S),
            Corners = CornerRadius4.All(28f), Acrylic = Tok.AcrylicFlyout, Fill = Tok.FillLayerDefault, Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Children =
            [
                new BoxEl { Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), ClipToBounds = true,
                    Children = [ Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 40f, 40f, 20f, decodePx: 256) ] },
                // The subline carries the upcoming release INSTEAD of the listener count while one is pending. The pill
                // exists precisely for the scrolled-past state, which is exactly when the hero's own countdown pill has
                // gone — so this is the only place the announcement survives, and it is worth more than a stat that has
                // not changed since the page loaded. The row is ~56px with an avatar, name, Play and Follow already in
                // it, so this replaces rather than adds.
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
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Corners = CornerRadius4.All(18f), Padding = new Edges4(16f, 8f, 16f, 8f),
                    Fill = accent, HoverScale = 1.04f, PressScale = 0.97f,
                    OnClick = () => _ = svc.Player.PlayAsync(uri, 0),
                    Children = [ Icon(Icons.Play, 14f, ColorContrast.PickContrast(accent)), new TextEl(Loc.Get(Strings.Artist.Play)) { Size = 13f, Weight = 700, Color = ColorContrast.PickContrast(accent) } ],
                },
                Embed.Comp(() => new FollowButton(uri, a.Name)) with { Key = "artist-pill-follow:" + uri },
            ],
        };
    }
}
