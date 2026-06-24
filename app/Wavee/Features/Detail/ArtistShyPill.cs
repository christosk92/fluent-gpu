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

    public override Element Render()
    {
        if (!_pinned.Value) return new BoxEl();          // not stuck → no pill, no hit area
        var a = _artist.Value.Value;                     // Loadable.Value is a Signal<Artist>; read its value
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
                    Fill = Tok.AccentDefault, HoverScale = 1.04f, PressScale = 0.97f,
                    OnClick = () => _ = _svc.Player.PlayAsync(_uri, 0),
                    Children = [ Icon(Icons.Play, 14f, Tok.TextOnAccentPrimary), new TextEl(Loc.Get(Strings.Artist.Play)) { Size = 13f, Weight = 700, Color = Tok.TextOnAccentPrimary } ],
                },
                Embed.Comp(() => new FollowButton(_uri)),
            ],
        };
    }
}
