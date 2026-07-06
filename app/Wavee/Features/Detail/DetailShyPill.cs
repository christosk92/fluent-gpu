using System.Collections.Generic;
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

sealed class DetailShyPill : Component
{
    readonly Loadable<DetailModel> _full;
    readonly DetailConfig _cfg;
    readonly DetailHandlers _h;
    readonly Signal<bool> _pinned;

    public DetailShyPill(Loadable<DetailModel> full, DetailConfig cfg, DetailHandlers h, Signal<bool> pinned)
    {
        _full = full;
        _cfg = cfg;
        _h = h;
        _pinned = pinned;
    }

    static readonly LayoutTransition Presence = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(280f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: -12f, Sx: 0.96f, Sy: 0.96f, Opacity: 0f, Active: true, Blur: 3f),
        Exit: new EnterExit(Dy: -8f, Sx: 0.985f, Sy: 0.985f, Opacity: 0f, Active: true, Blur: 2f),
        ExitDynamics: TransitionDynamics.Tween(170f, Easing.SmoothOut));

    public override Element Render()
    {
        var m = _full.Value.Value;
        return Flow.Show(
            () => _pinned.Value && _full.State.Value == (byte)LoadState.Ready,
            new BoxEl
            {
                Animate = Presence,
                TransformOriginX = 0.5f,
                TransformOriginY = 0f,
                Children = [Surface(m, _cfg, _h)],
            });
    }

    static Element Surface(DetailModel m, DetailConfig cfg, DetailHandlers h)
    {
        ColorF onAccent = WaveePalette.OnAccent(h.Accent);
        var children = new List<Element>(5)
        {
            new BoxEl
            {
                Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(8f), ClipToBounds = true,
                Children = [DetailRail.HeroArtwork(m, 40f)],
            },
            new BoxEl
            {
                Direction = 1, Gap = 1f, Width = 180f, MinWidth = 0f,
                Children =
                [
                    new TextEl(m.Title) { Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(m.MetaLine) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
            new BoxEl
            {
                Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
                Corners = CornerRadius4.All(18f), Padding = new Edges4(16f, 8f, 16f, 8f),
                Fill = h.Accent, HoverScale = 1.04f, PressScale = 0.97f,
                OnClick = h.PlayAll,
                Children = [Icon(Icons.Play, 14f, onAccent), new TextEl(Loc.Get(Strings.Detail.Play)) { Size = 13f, Weight = 700, Color = onAccent }],
            },
        };

        if (m.ContextUri is { Length: > 0 } uri && cfg.Heart != HeartMode.None)
            children.Add(Embed.Comp(() => new SaveButton(uri, 16f, 36f)));

        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.L, WaveeSpace.S),
            Corners = CornerRadius4.All(28f), Acrylic = Tok.AcrylicFlyout, Fill = Tok.FillLayerDefault, Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Children = children.ToArray(),
        };
    }
}
