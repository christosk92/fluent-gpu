using System;
using System.IO;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 4 · Appearance (<c>data-step="4"</c>). The three high-signal choices are presented as visual
/// examples; lower-frequency appearance options live together under Other. Every choice writes through
/// <see cref="SetupWrites"/> immediately so the shell behind the setup plate remains the live preview.</summary>
sealed class SetupAppearancePage : Component
{
    const float PreviewBodyHeight = Spacing.XXXL * 2f + Spacing.M;
    const float PreviewChromeHeight = Spacing.XL;
    const float PreviewDividerHeight = 1f;
    const float PreviewRailWidth = Spacing.XXL;
    const float PreviewStageInset = Spacing.XS;

    static readonly string[] s_densityPreviewFiles =
    [
        "density-compact.png",
        "density-default.png",
        "density-cozy.png",
        "density-comfortable.png",
    ];

    static string[] ThemeLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.System),
        Loc.Get(Strings.Settings.Choice.Light),
        Loc.Get(Strings.Settings.Choice.Dark),
    ];

    static string[] WindowMaterialLabels() =>
    [
        Loc.Get(Strings.Settings.Appearance.MaterialMica),
        Loc.Get(Strings.Settings.Appearance.MaterialMicaAlt),
    ];

    static string[] DensityLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Compact),
        Loc.Get(Strings.Settings.Choice.Default),
        Loc.Get(Strings.Settings.Choice.Cozy),
        Loc.Get(Strings.Settings.Choice.Comfortable),
    ];

    static string[] DensityDescriptions() =>
    [
        Loc.Get(Strings.Settings.Appearance.DensityCompactSub),
        Loc.Get(Strings.Settings.Appearance.DensityDefaultSub),
        Loc.Get(Strings.Settings.Appearance.DensityCozySub),
        Loc.Get(Strings.Settings.Appearance.DensityComfortableSub),
    ];

    static string[] PageLayoutLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Automatic),
        Loc.Get(Strings.Settings.Choice.Hero),
    ];

    static string[] PageLayoutDescriptions() =>
    [
        Loc.Get(Strings.Setup.Appearance.PageLayoutAutomaticSub),
        Loc.Get(Strings.Setup.Appearance.PageLayoutHeroSub),
    ];

    // A fresh Signal<T> is seeded from the setting on each render. The page epoch owns the render edge after a write;
    // the setting remains the source of truth and there is no mirror written during render.
    readonly Signal<int> _epoch = new(0);
    void Bump() => _epoch.Value = _epoch.Peek() + 1;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var requestTheme = UseContext(ThemeControl.Request);
        var settings = svc?.Settings;
        _ = _epoch.Value;

        int themeMode = Math.Clamp(settings?.Get(WaveeSettings.ThemeMode) ?? 0, 0, ThemeLabels().Length - 1);
        int density = Math.Clamp(settings?.Get(WaveeSettings.RowDensity) ?? 1, 0, DensityLabels().Length - 1);
        int pageLayout = Math.Clamp(settings?.Get(WaveeSettings.DetailPageLayout) ?? 0, 0, PageLayoutLabels().Length - 1);
        int windowMaterial = (settings?.Get(WaveeSettings.WindowMaterialBaseMica) ?? true) ? 0 : 1;
        bool heroOnly = settings?.Get(WaveeSettings.DetailPageToneHeroOnly) ?? false;
        bool hideTrackArtwork = settings?.Get(WaveeSettings.HideTrackArtwork) ?? false;

        Element body = new BoxEl
        {
            Direction = 1,
            Gap = Spacing.L,
            AlignSelf = FlexAlign.Stretch,
            Children =
            [
                SetupRows.Lead(Loc.Get(Strings.Setup.Appearance.Lead)),
                ChoiceSection(
                    Loc.Get(Strings.Setup.Appearance.RowsTitle),
                    Loc.Get(Strings.Setup.Appearance.RowsSub),
                    Icons.RowSize,
                    new BoxEl
                    {
                        Direction = 1,
                        Gap = Spacing.M,
                        Children =
                        [
                            DensityChoices(density, i =>
                            {
                                if (settings is null) return;
                                SetupWrites.SetRowDensity(i, settings);
                                Bump();
                            }),
                            SetupRows.Row(
                                Loc.Get(Strings.Settings.Appearance.HideTrackArtwork),
                                Loc.Get(Strings.Settings.Appearance.HideTrackArtworkSub),
                                ArtworkCheckBox(settings, hideTrackArtwork),
                                Icons.MusicNote),
                        ],
                    }),
                ChoiceSection(
                    Loc.Get(Strings.Setup.Appearance.DetailTitle),
                    Loc.Get(Strings.Setup.Appearance.DetailSub),
                    Icons.List,
                    PageLayoutChoices(pageLayout, i =>
                    {
                        if (settings is null) return;
                        SetupWrites.SetDetailPageLayout(i, settings);
                        Bump();
                    })),
                SetupRows.SectionHeader(
                    Loc.Get(Strings.Setup.Appearance.OtherTitle),
                    Icons.Brush,
                    Loc.Get(Strings.Setup.Appearance.OtherSub)),
                SetupRows.Row(
                    Loc.Get(Strings.Settings.Appearance.PageTone),
                    Loc.Get(Strings.Settings.Appearance.PageToneSub),
                    FlagToggle(settings, WaveeSettings.DetailPageToneHeroOnly, heroOnly),
                    Icons.Brush),
                SetupRows.Row(
                    Loc.Get(Strings.Settings.Appearance.Theme),
                    Loc.Get(Strings.Settings.Appearance.ThemeSub),
                    SelectorBar.Create(ThemeLabels(), new Signal<int>(themeMode), onChange: i =>
                    {
                        if (settings is null) return;
                        SetupWrites.SetThemeMode(i, settings, requestTheme);
                        Bump();
                    }),
                    Icons.Brush),
                SetupRows.Row(
                    Loc.Get(Strings.Settings.Appearance.WindowMaterial),
                    Loc.Get(Strings.Settings.Appearance.WindowMaterialSub),
                    SelectorBar.Create(WindowMaterialLabels(), new Signal<int>(windowMaterial), onChange: i =>
                    {
                        if (settings is null) return;
                        SetupWrites.SetWindowMaterial(i, settings);
                        Bump();
                    }),
                    Icons.BackToWindow),
                VisualEffectsGroup(settings),
            ],
        };

        return SetupPageHost.Frame(SetupPage.Appearance, Loc.Get(Strings.Setup.Eyebrow.Appearance),
            Loc.Get(Strings.Settings.Appearance.Title), body);
    }

    static Element ChoiceSection(string title, string description, string icon, Element choices) => new BoxEl
    {
        Direction = 1,
        Gap = Spacing.L,
        AlignSelf = FlexAlign.Stretch,
        MinWidth = 0f,
        Children =
        [
            SetupRows.SectionHeader(title, icon, description),
            choices,
        ],
    };

    static Element DensityChoices(int selected, Action<int> set)
    {
        var labels = DensityLabels();
        var descriptions = DensityDescriptions();

        Element Card(int value, bool on) => RichChoice(
            on,
            DensityPreview(value, on),
            labels[value],
            descriptions[value]);

        return WaveePicker.Strip(labels.Length, selected, Card, set);
    }

    static Element PageLayoutChoices(int selected, Action<int> set)
    {
        var labels = PageLayoutLabels();
        var descriptions = PageLayoutDescriptions();

        Element Card(int value, bool on) => RichChoice(
            on,
            PageLayoutPreview(value, on),
            labels[value],
            descriptions[value]);

        return WaveePicker.Strip(labels.Length, selected, Card, set);
    }

    static Element RichChoice(bool on, Element preview, string title, string description)
    {
        var card = WaveePicker.Card(on, WaveePicker.Pane,
            preview,
            WaveePicker.Label(title, on, 13f),
            new TextEl(description)
            {
                Size = 11f,
                LineHeight = 15f,
                Color = Tok.TextTertiary,
                Wrap = TextWrap.Wrap,
                MaxLines = 3,
                Trim = TextTrim.WordEllipsis,
                AlignSelf = FlexAlign.Stretch,
            });

        return card with
        {
            Fill = on ? Tok.AccentSubtle : Tok.FillCardSecondary,
            HoverFill = on ? WaveeColors.SelectedHover : Tok.FillSubtleSecondary,
            PressedFill = on ? Tok.AccentSubtle : Tok.FillSubtleTertiary,
        };
    }

    static Element PreviewWindow(bool on, Element content)
    {
        float stageWidth = WaveePicker.Pane.Width - 2f * WaveePicker.Pane.Inset;
        float windowWidth = stageWidth - 2f * PreviewStageInset;
        var ink = WaveePicker.Ink.For(on);

        Element CaptionButton(string glyph) => new BoxEl
        {
            Width = Spacing.XL,
            Height = PreviewChromeHeight,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children = [Icon(glyph, Spacing.S, Tok.TextTertiary)],
        };

        Element RailButton(string glyph, bool active = false) => new BoxEl
        {
            Width = Spacing.L,
            Height = Spacing.L,
            Shrink = 0f,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Fill = active ? ink.Faint : ColorF.Transparent,
            Children = [Icon(glyph, Spacing.S * 2f, active ? ink.Block : Tok.TextTertiary)],
        };

        Element window = new BoxEl
        {
            Width = windowWidth,
            Height = PreviewChromeHeight + PreviewDividerHeight + PreviewBodyHeight,
            Shrink = 0f,
            Direction = 1,
            ClipToBounds = true,
            Corners = Radii.ControlAll,
            Fill = Tok.FillSolidBase,
            BorderWidth = 1f,
            BorderColor = on ? Tok.AccentDefault : Tok.StrokeCardDefault,
            Shadow = Elevation.Card,
            Children =
            [
                new BoxEl
                {
                    Height = PreviewChromeHeight,
                    Shrink = 0f,
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(Spacing.S, 0f, 0f, 0f),
                    Fill = on ? Tok.AccentSubtle : Tok.FillSolidBaseAlt,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = Spacing.S,
                            Height = Spacing.S,
                            Shrink = 0f,
                            Corners = Radii.ControlAll,
                            Fill = ink.Block,
                        },
                        new BoxEl
                        {
                            Width = Spacing.XXXL,
                            Height = Spacing.XS,
                            Margin = new Edges4(Spacing.S, 0f, 0f, 0f),
                            Corners = Radii.PillAll,
                            Fill = ink.Faint,
                        },
                        new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f },
                        CaptionButton(Icons.ChromeMinimize),
                        CaptionButton(Icons.ChromeMaximize),
                        CaptionButton(Icons.ChromeClose),
                    ],
                },
                new BoxEl
                {
                    Height = PreviewDividerHeight,
                    Shrink = 0f,
                    AlignSelf = FlexAlign.Stretch,
                    Fill = on ? ink.Faint : Tok.StrokeDividerDefault,
                },
                new BoxEl
                {
                    Height = PreviewBodyHeight,
                    Shrink = 0f,
                    Direction = 0,
                    ClipToBounds = true,
                    Fill = Tok.FillSolidBase,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = PreviewRailWidth,
                            Height = PreviewBodyHeight,
                            Shrink = 0f,
                            Direction = 1,
                            Gap = Spacing.XS,
                            AlignItems = FlexAlign.Center,
                            Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
                            Fill = on ? Tok.AccentSubtle : Tok.FillSolidBaseAlt,
                            Children =
                            [
                                RailButton(Icons.Home, active: true),
                                RailButton(Icons.Search),
                                RailButton(Icons.MusicNote),
                                new BoxEl { Grow = 1f, Basis = 0f, MinHeight = 0f },
                                RailButton(Icons.Settings),
                            ],
                        },
                        new BoxEl
                        {
                            Width = PreviewDividerHeight,
                            Height = PreviewBodyHeight,
                            Shrink = 0f,
                            Fill = Tok.StrokeDividerDefault,
                        },
                        new BoxEl
                        {
                            Grow = 1f,
                            Basis = 0f,
                            MinWidth = 0f,
                            Height = PreviewBodyHeight,
                            ClipToBounds = true,
                            Fill = Tok.FillSolidBase,
                            Children = [content],
                        },
                    ],
                },
            ],
        };

        return new BoxEl
        {
            Width = stageWidth,
            Height = PreviewChromeHeight + PreviewDividerHeight + PreviewBodyHeight + 2f * PreviewStageInset,
            Shrink = 0f,
            AlignSelf = FlexAlign.Stretch,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Padding = new Edges4(PreviewStageInset, PreviewStageInset, PreviewStageInset, PreviewStageInset),
            Corners = Radii.ControlAll,
            Fill = on ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
            Children = [window],
        };
    }

    static Element DensityPreview(int value, bool on)
    {
        float width = WaveePicker.Pane.Width - 2f * (WaveePicker.Pane.Inset + PreviewStageInset)
            - PreviewRailWidth - PreviewDividerHeight;
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "setup", s_densityPreviewFiles[value]);
        var screenshot = Image(path, width, PreviewBodyHeight, Radii.Control, Tok.FillSolidBase,
            transition: ImageTransition.None) with { Fit = ImageFit.Contain };
        return PreviewWindow(on, screenshot);
    }

    static Element PageLayoutPreview(int value, bool on)
    {
        var ink = WaveePicker.Ink.For(on);

        Element Bar(float width, float height, bool strong = false) => new BoxEl
        {
            Width = width,
            Height = height,
            Corners = Radii.PillAll,
            Fill = strong ? ink.Block : ink.Faint,
        };

        Element TrackLine() => new BoxEl
        {
            Height = Spacing.M,
            Direction = 0,
            Gap = Spacing.XS,
            AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Width = Spacing.M, Height = Spacing.M, Corners = Radii.ControlAll, Fill = ink.Faint },
                new BoxEl { Height = Spacing.XS, Grow = 1f, Basis = 0f, MinWidth = 0f, Corners = Radii.PillAll, Fill = ink.Faint },
                new BoxEl { Width = Spacing.XXL, Height = Spacing.XS, Corners = Radii.PillAll, Fill = ink.Faint },
            ],
        };

        Element TrackList(params Element[] rows) => new BoxEl
        {
            Direction = 1,
            Gap = Spacing.XS,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            Justify = FlexJustify.Center,
            Children = rows,
        };

        Element art = Surfaces.Artwork(FakeData.Cover(6, 96), 6,
            value == DetailVerticalLayout.PageAuto ? WaveeSize.Thumb32 : WaveeSize.Thumb40,
            value == DetailVerticalLayout.PageAuto ? WaveeSize.Thumb32 : WaveeSize.Thumb40,
            Radii.Control, decodePx: 96);

        Element content = value == DetailVerticalLayout.PageAuto
            ? new BoxEl
            {
                Direction = 0,
                Gap = Spacing.S,
                Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                Children =
                [
                    new BoxEl
                    {
                        Width = WaveeSize.Thumb48,
                        Shrink = 0f,
                        Direction = 1,
                        Gap = Spacing.XS,
                        Justify = FlexJustify.Center,
                        Children = [art, Bar(Spacing.XXXL + Spacing.S, Spacing.XS, true), Bar(Spacing.XXL, Spacing.XS)],
                    },
                    TrackList(TrackLine(), TrackLine(), TrackLine()),
                ],
            }
            : new BoxEl
            {
                Direction = 1,
                Gap = Spacing.XS,
                Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0,
                        Gap = Spacing.S,
                        AlignItems = FlexAlign.Center,
                        Children =
                        [
                            art,
                            new BoxEl
                            {
                                Direction = 1,
                                Gap = Spacing.XS,
                                Grow = 1f,
                                Basis = 0f,
                                MinWidth = 0f,
                                Children = [Bar(Spacing.XXXL * 2f, Spacing.S, true), Bar(Spacing.XXXL + Spacing.S, Spacing.XS)],
                            },
                        ],
                    },
                    TrackList(TrackLine(), TrackLine()),
                ],
            };

        return PreviewWindow(on, content);
    }

    Element FlagToggle(IAppSettings? settings, SettingKey<bool> key, bool value)
        => ToggleSwitch.Create(new Signal<bool>(value), onChange: _ =>
        {
            if (settings is null) return;
            SetupWrites.SetAppearanceFlag(key, !value, settings);
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

    Element ArtworkCheckBox(IAppSettings? settings, bool value)
        => CheckBox.Create("", new Signal<bool>(value), onChange: next =>
        {
            if (settings is null) return;
            SetupWrites.SetAppearanceFlag(WaveeSettings.HideTrackArtwork, next, settings);
            Bump();
        }, style: CheckBox.DefaultStyle with { MinWidth = Spacing.XXXL, MinHeight = Spacing.XXXL });

    Element VisualEffectsGroup(IAppSettings? settings)
    {
        bool noMarquee = settings?.Get(WaveeSettings.DisableMarquee) ?? false;
        bool noWash = settings?.Get(WaveeSettings.DisableColorWashes) ?? false;
        bool lyricsBackdrop = settings?.Get(WaveeSettings.LyricsAnimatedBackdrop) ?? true;
        int off = (noMarquee ? 1 : 0) + (noWash ? 1 : 0) + (lyricsBackdrop ? 0 : 1);

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Appearance.EffectsTitle),
            Description = Loc.Get(Strings.Settings.Appearance.EffectsSub),
            HeaderIcon = Icons.Brush,
            Content = SetupRows.ValueTag(off == 0
                ? Loc.Get(Strings.Settings.Appearance.EffectsAllOn)
                : Strings.Settings.Appearance.EffectsDisabled(off)),
            Items =
            [
                SetupRows.Item(Loc.Get(Strings.Settings.Appearance.DisableMarquee), Loc.Get(Strings.Settings.Appearance.DisableMarqueeSub),
                    FlagToggle(settings, WaveeSettings.DisableMarquee, noMarquee), icon: Icons.Font),
                SetupRows.Item(Loc.Get(Strings.Settings.Appearance.DisableColorWashes), Loc.Get(Strings.Settings.Appearance.DisableColorWashesSub),
                    FlagToggle(settings, WaveeSettings.DisableColorWashes, noWash), icon: Icons.Brush),
                SetupRows.Item(Loc.Get(Strings.Settings.Appearance.LyricsBackdrop), Loc.Get(Strings.Settings.Appearance.LyricsBackdropSub),
                    FlagToggle(settings, WaveeSettings.LyricsAnimatedBackdrop, lyricsBackdrop), icon: Icons.Brush),
            ],
        }) with { Key = "setup:appearance:effects" };
    }
}
