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
// overlay so it never blocks scrolling. Its own component so a pin toggle re-renders only this, not the whole page.
sealed class ArtistShyPill : Component
{
    readonly string _uri;
    readonly Loadable<Artist> _artist;
    readonly Signal<bool> _pinned;
    readonly Services _svc;
    public ArtistShyPill(string uri, Loadable<Artist> artist, Signal<bool> pinned, Services svc)
    { _uri = uri; _artist = artist; _pinned = pinned; _svc = svc; }

    static readonly LayoutTransition Presence = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(280f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: -12f, Sx: 0.96f, Sy: 0.96f, Opacity: 0f, Active: true, Blur: 3f),
        Exit: new EnterExit(Dy: -8f, Sx: 0.985f, Sy: 0.985f, Opacity: 0f, Active: true, Blur: 2f),
        ExitDynamics: TransitionDynamics.Tween(170f, Easing.SmoothOut));

    public override Element Render()
    {
        // Gate on Ready, not just pinned: the pill is a scrolled-past-hero affordance, so showing its real avatar +
        // monthly-listeners over the page's loading skeleton both leaks real data early and floats a solid card on top
        // of the shimmer grid (the "overlapping components" artifact). While Pending/Failed it stays hidden; on Ready,
        // if still scrolled past the hero, it animates in. KeepAlive owns page visibility and detaches this entire
        // subtree synchronously on navigation; page deactivation must not be converted into a local animated exit.
        return Flow.Show(
            () => _pinned.Value && _artist.State.Value == (byte)LoadState.Ready,
            new BoxEl
            {
                Animate = Presence,
                TransformOriginX = 0.5f,
                TransformOriginY = 0f,
                Children = [Embed.Comp(() => new ArtistShyPillSurface(_uri, _artist, _svc))],
            });
    }
}

// The live pill content is a child component so the reactive presence boundary can remain stable while artist data
// updates. The animated wrapper above owns insertion/removal; this component only renders the actual interactive card.
sealed class ArtistShyPillSurface : Component
{
    readonly string _uri;
    readonly Loadable<Artist> _artist;
    readonly Services _svc;
    public ArtistShyPillSurface(string uri, Loadable<Artist> artist, Services svc)
    { _uri = uri; _artist = artist; _svc = svc; }

    public override Element Render()
    {
        var a = _artist.Value.Value;                     // Loadable.Value is a Signal<Artist>; read its value
        // Match the page's cover-extracted accent (lifted) so the floating pill isn't default-blue over an accented page.
        ColorF accent = a.Palette is { } pal ? WaveePalette.Lift(WaveePalette.Accent(pal)) : Tok.AccentDefault;
        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.L, WaveeSpace.S),
            Corners = CornerRadius4.All(28f), Acrylic = Tok.AcrylicFlyout, Fill = Tok.FillLayerDefault, Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Children =
            [
                new BoxEl { Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), ClipToBounds = true,
                    Children = [ Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 40f, 40f, 20f, decodePx: 256) ] },
                new BoxEl { Direction = 1, Gap = 1f,
                    Children =
                    [
                        new TextEl(a.Name) { Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(Strings.Artist.MonthlyListeners(a.MonthlyListeners.ToString("N0"))) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ] },
                new BoxEl
                {
                    Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
                    Corners = CornerRadius4.All(18f), Padding = new Edges4(16f, 8f, 16f, 8f),
                    Fill = accent, HoverScale = 1.04f, PressScale = 0.97f,
                    OnClick = () => _ = _svc.Player.PlayAsync(_uri, 0),
                    Children = [ Icon(Icons.Play, 14f, WaveePalette.OnAccent(accent)), new TextEl(Loc.Get(Strings.Artist.Play)) { Size = 13f, Weight = 700, Color = WaveePalette.OnAccent(accent) } ],
                },
                Embed.Comp(() => new FollowButton(_uri)),
            ],
        };
    }
}
