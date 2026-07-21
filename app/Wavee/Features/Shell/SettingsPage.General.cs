using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

sealed partial class SettingsPage
{
    readonly Signal<int> _density = new(1);
    readonly Signal<int> _language = new(0);

    static (string[] Codes, string[] Labels) LanguageOptions()
    {
        return (
            ["system", "en-US", "nl", "ko-KR"],
            [
                Loc.Get(Strings.Settings.Language.System),
                Loc.Get(Strings.Settings.Language.EnglishUs),
                Loc.Get(Strings.Settings.Language.Dutch),
                Loc.Get(Strings.Settings.Language.Korean),
            ]);
    }

    static int LanguageIndex(string culture)
    {
        var (codes, _) = LanguageOptions();
        for (int i = 0; i < codes.Length; i++)
            if (string.Equals(codes[i], culture, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    static string[] DensityLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Compact),
        Loc.Get(Strings.Settings.Choice.Default),
        Loc.Get(Strings.Settings.Choice.Cozy),
        Loc.Get(Strings.Settings.Choice.Comfortable),
    ];

    static string[] PageLayoutLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Automatic),
        Loc.Get(Strings.Settings.Choice.Stacked),
    ];

    Element GeneralTab(Services? svc, Action<float>? requestTheme)
    {
        var settings = svc?.Settings;
        int themeMode = settings?.Get(WaveeSettings.ThemeMode) ?? 0;
        int density = Math.Clamp(_density.Value, 0, DensityLabels().Length - 1);
        int pageLayout = Math.Clamp(settings?.Get(WaveeSettings.DetailPageLayout) ?? 0, 0, PageLayoutLabels().Length - 1);
        var languageOptions = LanguageOptions();
        int language = Math.Clamp(_language.Value, 0, languageOptions.Codes.Length - 1);

        Element AppearanceToggle(SettingKey<bool> key) => ToggleSwitch.Create(new Signal<bool>(settings?.Get(key) ?? false), onChange: _ =>
        {
            if (settings is null) return;
            settings.Set(key, !settings.Get(key));
            AppearancePrefs.Bump();
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

        void SetTheme(int mode)
        {
            WaveeTheme.ApplyThemeMode(mode, settings);
            requestTheme?.Invoke(250f);
            Bump();
        }

        void SetDensity(int i)
        {
            settings?.Set(WaveeSettings.RowDensity, i);
            _density.Value = i;
            Bump();
        }

        void SetPageLayout(int i)
        {
            settings?.Set(WaveeSettings.DetailPageLayout, i);
            DetailHeroPrefs.Bump();   // live-update any mounted (incl. KeepAlive-parked) detail page's rail↔hero choice
            Bump();
        }

        void SetLanguage(int i)
        {
            if (settings is null || (uint)i >= (uint)languageOptions.Codes.Length) return;
            settings.Set(WaveeSettings.UiCulture, languageOptions.Codes[i]);
            _language.Value = i;
            Bump();
        }

        return SettingsTabStack(
            SettingsSectionHeader(Loc.Get(Strings.Settings.Appearance.Title), Icons.Brush),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Theme), Loc.Get(Strings.Settings.Appearance.ThemeSub),
                SelectorBar.Create(ThemeLabels(), new Signal<int>(themeMode), onChange: SetTheme), Icons.Brush),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Palette), Loc.Get(Strings.Settings.Appearance.PaletteSub),
                PaletteRow(settings, requestTheme), Icons.Brush),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.DisableMarquee), Loc.Get(Strings.Settings.Appearance.DisableMarqueeSub),
                AppearanceToggle(WaveeSettings.DisableMarquee), Icons.Font),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.DisableColorWashes), Loc.Get(Strings.Settings.Appearance.DisableColorWashesSub),
                AppearanceToggle(WaveeSettings.DisableColorWashes), Icons.Brush),
            DensityBlock(density, SetDensity),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.PageLayout), Loc.Get(Strings.Settings.Appearance.PageLayoutSub),
                PageLayoutCards(pageLayout, SetPageLayout), Icons.List),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Language.Title), Icons.Globe),
            SettingsRow(Loc.Get(Strings.Settings.Language.Label), Loc.Get(Strings.Settings.Language.RestartSub),
                ComboBox.Create(languageOptions.Labels, _language, width: 260f, isEnabled: settings is not null,
                    onChange: SetLanguage), Icons.Globe));
    }

    // ── the page-layout picker: the preview cards ARE the selector (a radio pair, PaletteRow-style) ─────────────────
    // Each card is a mini skeleton-bar wireframe of the page SYSTEM it selects — Automatic: a narrow metadata rail
    // (art + title/meta bars + a pill) BESIDE a column of full-width track rows (the rail-when-wide layout); Stacked:
    // the vertical hero (art beside title/meta/pills) ABOVE the track rows, at every width. The selected card lights
    // its blocks + border with the accent so the choice reads at a glance.
    static Element PageLayoutCards(int selected, Action<int> set)
    {
        Element Card(int value, string label, bool automatic)
        {
            bool on = selected == value;
            ColorF block = on ? Tok.AccentDefault : Tok.FillSubtleTertiary;
            ColorF faint = on ? Tok.AccentDefault with { A = 0.45f } : Tok.FillSubtleTertiary with { A = 0.7f };

            Element Bar(float w, float h) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(h / 2f), Fill = faint };
            Element RowBar() => new BoxEl { Height = 5f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(2.5f), Fill = faint };
            Element Art(float edge) => new BoxEl { Width = edge, Height = edge, Corners = CornerRadius4.All(4f), Fill = block, Shrink = 0f };
            Element Pill() => new BoxEl { Width = 24f, Height = 9f, Corners = CornerRadius4.All(4.5f), Fill = block };
            Element SmallPill() => new BoxEl { Width = 20f, Height = 8f, Corners = CornerRadius4.All(4f), Fill = block };
            Element Pills() => new BoxEl { Direction = 0, Gap = 4f, Children = [Pill(), Pill()] };

            Element sketch = automatic
                // Automatic: a narrow LEFT rail column (art over title/meta bars + a pill) beside a RIGHT column of
                // full-width track rows — "side rail beside tracks" on a wide window.
                ? new BoxEl
                {
                    Direction = 0, Gap = 8f, Grow = 1f, AlignItems = FlexAlign.Stretch,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Gap = 4f, Shrink = 0f, Justify = FlexJustify.Center,
                            Children = [Art(20f), Bar(30f, 6f), Bar(22f, 4f), SmallPill()],
                        },
                        new BoxEl
                        {
                            Direction = 1, Gap = 5f, Grow = 1f, Justify = FlexJustify.Center,
                            Children = [RowBar(), RowBar(), RowBar(), RowBar()],
                        },
                    ],
                }
                // Stacked: the side-by-side hero (art beside title/meta/pills) above the track rows.
                : new BoxEl
                {
                    Direction = 1, Gap = 5f, Grow = 1f, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Start,
                            Children =
                            [
                                Art(26f),
                                new BoxEl { Direction = 1, Gap = 4f, Grow = 1f, Children = [Bar(58f, 6f), Bar(40f, 4f), Pills()] },
                            ],
                        },
                        RowBar(), RowBar(), RowBar(),
                    ],
                };

            return new BoxEl
            {
                Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => set(value),
                Children =
                [
                    new BoxEl
                    {
                        // Drop 10f→9f when selected so the 1f→2f border growth draws inward and the wireframe stays put.
                        Width = 116f, Height = 84f, Padding = Edges4.All(on ? 9f : 10f),
                        Direction = 1, ClipToBounds = true,
                        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillSubtleSecondary,
                        BorderWidth = on ? 2f : 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                        HoverScale = 1.02f, PressScale = 0.98f,
                        Children = [sketch],
                    },
                    new TextEl(label) { Size = 11f, Weight = (ushort)(on ? 600 : 400), Color = on ? Tok.TextPrimary : Tok.TextSecondary },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 0, Gap = 12f, Wrap = true, AlignItems = FlexAlign.Start,
            Children =
            [
                Card(DetailVerticalLayout.PageAuto, PageLayoutLabels()[0], automatic: true),
                Card(DetailVerticalLayout.PageHero, PageLayoutLabels()[1], automatic: false),
            ],
        };
    }

    Element PaletteRow(IAppSettings? settings, Action<float>? requestTheme)
    {
        string active = Tok.Palette.Id;

        Element Swatch(string id, string label, ColorF fill)
        {
            bool on = active == id;
            return new BoxEl
            {
                Direction = 1, Gap = 5f, AlignItems = FlexAlign.Center, Width = 56f,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => { WaveeTheme.ApplyPalette(id, settings); requestTheme?.Invoke(250f); Bump(); },
                Children =
                [
                    new BoxEl
                    {
                        Width = 30f, Height = 30f, Corners = CornerRadius4.All(15f), Fill = fill,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        BorderWidth = on ? 2.5f : 1f,
                        BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                        Children = on
                            ? [new TextEl(Icons.Accept) { Size = 13f, FontFamily = Theme.IconFont, Color = Tok.TextOnAccentPrimary }]
                            : [],
                    },
                    new TextEl(label) { Size = 11f, Weight = (ushort)(on ? 600 : 400), Color = on ? Tok.TextPrimary : Tok.TextSecondary },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, Wrap = true,
            Children =
            [
                Swatch("warm", Loc.Get(Strings.Settings.Appearance.PaletteWarm), WaveeColors.PresetSwatch(Tok.WarmPalette)),
                Swatch("slate", Loc.Get(Strings.Settings.Appearance.PaletteSlate), WaveeColors.PresetSwatch(Tok.SlatePalette)),
                Swatch("neutral", Loc.Get(Strings.Settings.Appearance.PaletteNeutral), WaveeColors.PresetSwatch(Tok.NeutralPalette)),
                Swatch("accent", Loc.Get(Strings.Settings.Appearance.PaletteAccent), WaveeColors.PresetSwatch(Tok.AccentTintedPalette)),
            ],
        };
    }

    Element DensityBlock(int density, Action<int> setDensity) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch,
        Corners = CornerRadius4.All(Radii.Card),
        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        ClipToBounds = true,
        Children =
        [
            SettingsRow(Loc.Get(Strings.Settings.Appearance.RowDensity), Loc.Get(Strings.Settings.Appearance.RowDensitySub),
                DensityCards(density, setDensity), Icons.List),
        ],
    };

    // Match the page-layout selector: the preview card itself is the radio control. The real density ordering is
    // compressed into each fixed-size wireframe, so the choice communicates row height before it is applied.
    static Element DensityCards(int selected, Action<int> set)
    {
        var labels = DensityLabels();

        Element Card(int value)
        {
            bool on = selected == value;
            ColorF block = on ? Tok.AccentDefault : Tok.FillSubtleTertiary;
            ColorF faint = on ? Tok.AccentDefault with { A = 0.45f } : Tok.FillSubtleTertiary with { A = 0.7f };
            float rowHeight = 8f + value * 3f;

            Element MockRow() => new BoxEl
            {
                Height = rowHeight, Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(3f, 0f, 3f, 0f),
                Corners = CornerRadius4.All(3f), Fill = faint,
                Children =
                [
                    new BoxEl { Width = rowHeight - 2f, Height = rowHeight - 2f, Corners = CornerRadius4.All(2f), Fill = block },
                    new BoxEl { Width = 42f, Height = 4f, Corners = CornerRadius4.All(2f), Fill = block },
                ],
            };

            return new BoxEl
            {
                Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => set(value),
                Children =
                [
                    new BoxEl
                    {
                        Width = 116f, Height = 84f, Padding = Edges4.All(on ? 9f : 10f),
                        Direction = 1, Gap = 4f, Justify = FlexJustify.Center, ClipToBounds = true,
                        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillSubtleSecondary,
                        BorderWidth = on ? 2f : 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                        HoverScale = 1.02f, PressScale = 0.98f,
                        Children = [MockRow(), MockRow(), MockRow()],
                    },
                    new TextEl(labels[value])
                        { Size = 11f, Weight = (ushort)(on ? 600 : 400), Color = on ? Tok.TextPrimary : Tok.TextSecondary },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 0, Gap = 12f, Wrap = true, AlignItems = FlexAlign.Start,
            Children = [Card(0), Card(1), Card(2), Card(3)],
        };
    }

}
