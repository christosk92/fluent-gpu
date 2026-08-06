using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

// The WaveeMusic-style right rail container: a header (panel title + close) over the active panel's content. Mounted by
// WaveeShell as the third child of the sidebar+content row; the
// shell animates the rail's width (and thus visibility). Reads ShellUi for the active mode.
sealed class RightRail : Component
{
    bool _motionSeeded;

    public override Element Render()
    {
        var ui = UseContext(ShellUi.Slot);
        // Unconditional (before the null-guard below): a hook taken after an early return would shift the hook order the
        // frame the shell context arrives. Only the lyrics header reads it.
        var svc = UseContext(Services.Slot);
        if (ui is null) return new BoxEl();
        bool open = ui.RailOpen.Value;
        float railWidth = ui.RailWidth.Value;
        var mode = ui.Mode.Value;   // subscribe → swap the panel on a mode change
        bool floating = !ui.RailFits.Value;
        bool nowPlaying = mode == RailMode.Details;

        // The shell keeps this panel at its final layout width. Animate the component host itself so open AND close retain
        // the fully-laid-out subtree while it slides through the shell's fixed clip; no width/layout writes occur per tick.
        // WAVEE_RAIL_BASELINE preserves the old wrapper-width Reflow path for the probe's same-build A/B.
        bool baseline = Diag.EnvFlag("WAVEE_RAIL_BASELINE");
        UseLayoutEffect(() =>
        {
            if (baseline || Context.Anim is not { } anim || Context.HostNode.IsNull) return;
            float x = open ? 0f : railWidth;
            if (!_motionSeeded)
            {
                _motionSeeded = true;
                // Seed closed off-canvas (or open in-place) without a startup fly-in.
                anim.Animate(Context.HostNode, AnimChannel.TranslateX, x, x, 1f, Easing.Linear);
            }
            else
            {
                // Same retained clip+translation choreography as WinUI SplitView. There is deliberately no opacity
                // track: a fading full-height panel is the source of the faint "ghost rail" during unrelated motion.
                // Sample the actually presented transform so a rapid reversal continues from the visible position.
                float from = Context.Scene is { } scene
                    ? scene.Paint(Context.HostNode).LocalTransform.Dx
                    : (open ? railWidth : 0f);
                anim.Animate(Context.HostNode, AnimChannel.TranslateX, from, x, 300f,
                    EasingSpec.CubicBezier(0f, 0.35f, 0.15f, 1f));
            }
        }, DepKey.From(HashCode.Combine(open, railWidth, baseline)));
        // TOP-LEFT rounded like the content card — the two sit as sibling cards across the 4px chrome gap.
        // Docked: Transparent — the shell spacer's ContentPaneMerged underlay already owns the band coat (three coats
        // → one). Floating: FileArea over the shell's FloatingChrome backing (B2), completing the same ladder.
        var corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f);

        Element surface = new BoxEl
        {
            Grow = 1f, Corners = corners, ClipToBounds = true,
            Fill = Prop.Of(() => ui.RailFits.Value ? ColorF.Transparent : WaveeColors.FileArea),
        };

        // Lyrics only: promote the panel to the fullscreen immersive surface (WaveeShell mounts it off this signal).
        // The rail is left exactly as it is underneath — the surface covers the shell rather than replacing the panel.
        Element[] headerKids = mode == RailMode.Lyrics
            ? LyricsHeaderKids(ui, svc?.Settings)
            : [TitleText(mode), CloseButton(() => ui.RailOpen.Value = false)];

        var header = new BoxEl
        {
            Direction = 0, Height = 36f, AlignItems = FlexAlign.Center, Gap = 4f,
            Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f),
            Children = headerKids,
        };

        Element body = mode switch
        {
            // PARK the rail's lyrics engine while the immersive surface is up: it is fully occluded, and two live
            // LyricsView documents would each run a 16 ms ticker, a DoF ramp and a handoff cascade for nothing. The
            // visibility gate is the same one the immersive surface uses, so exactly one of them is ever ticking.
            RailMode.Lyrics => Embed.Comp(() => new LyricsView(
                visible: () => ui.RailOpen.Value && !ui.ImmersiveLyrics.Value)),
            RailMode.Queue => Embed.Comp(() => new QueuePanel()),
            RailMode.Friends => Embed.Comp(() => new FriendsPanel()),
            _ => Embed.Comp(() => new NowPlayingPanel()),
        };

        // Now-playing: no title chrome; the inset artwork and sections fill the rounded rail surface.
        if (nowPlaying)
        {
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, HitTestVisible = baseline || open,
                Corners = corners,
                BorderColor = floating ? Tok.StrokeCardDefault : ColorF.Transparent,
                BorderWidth = floating ? 1f : 0f,
                Shadow = floating ? Elevation.Flyout : Elevation.Card,
                Children =
                [
                    surface,
                    new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, HitTestVisible = baseline || open,
            Corners = corners,
            BorderColor = floating ? Tok.StrokeCardDefault : ColorF.Transparent,
            BorderWidth = floating ? 1f : 0f,
            Shadow = floating ? Elevation.Flyout : Elevation.Card,
            Children =
            [
                surface,
                new BoxEl
                {
                    Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
                    Children = [header, new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] }],
                },
            ],
        };
    }

    // The lyrics header: title · (secondary-line toggle) · expand · close.
    //
    // The secondary-line toggle is rendered ONLY when the document on screen actually carries a translation or a
    // romanization (LyricsPrefs.Available, published once per doc by LyricsView) — a permanently-present control that
    // does nothing on nine tracks out of ten is noise. Both signal reads are SUBSCRIPTIONS: Available so the button
    // appears the moment a document with the data lands, Epoch so the glyph's on/off state re-reads after a write from
    // here, from the immersive surface, or from the Settings picker.
    static Element[] LyricsHeaderKids(ShellUi ui, IAppSettings? settings)
    {
        int available = LyricsPrefs.Available.Value;
        _ = LyricsPrefs.Epoch.Value;
        int secondary = LyricsPrefs.Clamp(settings?.Get(WaveeSettings.LyricsSecondaryLine) ?? LyricsPrefs.None);

        Element expand = HeaderButton(Icons.FullScreen, Loc.Get(Strings.Player.ExpandLyrics),
            () => ui.ImmersiveLyrics.Value = true);
        Element close = CloseButton(() => ui.RailOpen.Value = false);
        if (available == 0) return [TitleText(RailMode.Lyrics), expand, close];

        return
        [
            TitleText(RailMode.Lyrics),
            // `active` is "a second line is actually on screen", not merely "the mode is non-zero": a persisted
            // romanization preference over a document that only carries a translation renders nothing, and an accented
            // glyph claiming otherwise would be the misleading half of the state.
            HeaderButton(Icons.Globe, LyricsPrefs.Tooltip(secondary),
                () => LyricsPrefs.Set(settings, LyricsPrefs.Next(secondary, available)),
                active: (available & LyricsPrefs.BitFor(secondary)) != 0),
            expand,
            close,
        ];
    }

    static Element TitleText(RailMode mode) => new TextEl(Title(mode))
    {
        Size = 14f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f,
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    // A glyph button in the panel header — the CloseButton shape, with a tooltip because its glyph is not universal.
    // `active` is the STATEFUL variant (the secondary-line toggle): the accent tint is the only affordance a 32 DIP
    // glyph has to say "this is currently on", and the tooltip carries which layer it is.
    static Element HeaderButton(string glyph, string tip, Action onClick, bool active = false) => ToolTip.Wrap(new BoxEl
    {
        Width = 32f, Height = 32f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new TextEl(glyph)
            {
                Size = 12f, FontFamily = Theme.IconFont,
                Color = active ? Tok.AccentTextPrimary : Tok.TextSecondary,
                HoverColor = active ? Tok.AccentTextPrimary : Tok.TextPrimary,
            },
        ],
    }.Interactive(Interaction.Subtle), tip);

    static string Title(RailMode m) => m switch
    {
        RailMode.Lyrics => Loc.Get(Strings.Player.Lyrics),
        RailMode.Queue => Loc.Get(Strings.Player.Queue),
        RailMode.Friends => Loc.Get(Strings.Friends.Title),
        _ => Loc.Get(Strings.Player.NowPlaying),
    };

    static Element CloseButton(Action onClick) => new BoxEl
    {
        Width = 32f, Height = 32f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(Icons.ChromeClose) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
    }.Interactive(Interaction.Subtle);
}
