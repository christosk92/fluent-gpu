using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

// The WaveeMusic-style right rail container: a header (panel title + close) over the active panel's content. Mounted by
// WaveeShell as the third child of the sidebar+content row; the
// shell animates the rail's width (and thus visibility). Reads ShellUi for the active mode.
sealed class RightRail : Component
{
    public override Element Render()
    {
        var ui = UseContext(ShellUi.Slot);
        if (ui is null) return new BoxEl();
        var mode = ui.Mode.Value;   // subscribe → swap the panel on a mode change
        bool floating = !ui.RailFits.Value;

        var header = new BoxEl
        {
            Direction = 0, Height = 48f, AlignItems = FlexAlign.Center, Gap = 4f,
            Padding = new Edges4(22f, 0f, 10f, 0f),
            Children =
            [
                new TextEl(Title(mode))
                {
                    Size = 16f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f,
                    Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                },
                CloseButton(() => ui.RailOpen.Value = false),
            ],
        };

        Element body = mode switch
        {
            RailMode.Lyrics => Embed.Comp(() => new LyricsView()),
            RailMode.Queue => Embed.Comp(() => new QueuePanel()),
            _ => Embed.Comp(() => new NowPlayingPanel()),
        };

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
            Fill = floating ? WaveeColors.RailOverlay : ColorF.Transparent,
            BorderColor = floating ? Tok.StrokeCardDefault : ColorF.Transparent,
            BorderWidth = floating ? 1f : 0f,
            Shadow = floating ? Elevation.Flyout : null,
            Children =
            [
                header,
                new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] },
            ],
        };
    }

    static string Title(RailMode m) => m switch
    {
        RailMode.Lyrics => Loc.Get(Strings.Player.Lyrics),
        RailMode.Queue => Loc.Get(Strings.Player.Queue),
        _ => Loc.Get(Strings.Player.NowPlaying),
    };

    static Element CloseButton(Action onClick) => new BoxEl
    {
        Width = 32f, Height = 32f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(WaveeRadius.Control),
        Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(Icons.ChromeClose) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
    };
}
