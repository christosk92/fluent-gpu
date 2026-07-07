using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

sealed partial class SettingsPage
{
    readonly Signal<int> _density = new(1);

    static string[] DensityLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Compact),
        Loc.Get(Strings.Settings.Choice.Default),
        Loc.Get(Strings.Settings.Choice.Cozy),
        Loc.Get(Strings.Settings.Choice.Comfortable),
    ];

    Element GeneralTab(Services? svc, Action<float>? requestTheme)
    {
        var settings = svc?.Settings;
        int themeMode = settings?.Get(WaveeSettings.ThemeMode) ?? 0;
        int density = Math.Clamp(_density.Value, 0, DensityLabels().Length - 1);

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

        return SettingsTabStack(
            SettingsSectionHeader(Loc.Get(Strings.Settings.Appearance.Title), Icons.Brush),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Theme), Loc.Get(Strings.Settings.Appearance.ThemeSub),
                SelectorBar.Create(ThemeLabels(), themeMode, SetTheme), Icons.Brush),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.Palette), Loc.Get(Strings.Settings.Appearance.PaletteSub),
                PaletteRow(settings, requestTheme), Icons.Brush),
            DensityBlock(density, SetDensity));
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

    Element DensityBlock(int density, Action<int> setDensity)
    {
        float rowH = TrackRow.RowHeightFor(density);
        string label = DensityLabels()[Math.Clamp(density, 0, DensityLabels().Length - 1)];

        return new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch,
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            ClipToBounds = true,
            Children =
            [
                SettingsRow(Loc.Get(Strings.Settings.Appearance.RowDensity), Loc.Get(Strings.Settings.Appearance.RowDensitySub),
                    SelectorBar.Create(DensityLabels(), density, setDensity), Icons.List),
                Divider(),
                new BoxEl
                {
                    Direction = 1, Gap = WaveeSpace.S,
                    Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
                            Children =
                            [
                                Icon(Icons.List, 14f, Tok.TextSecondary),
                                new TextEl(Loc.Get(Strings.Settings.Appearance.DensityPreviewTitle))
                                    { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                                new TextEl(Strings.Settings.Appearance.DensityPreviewSub(
                                    rowH.ToString("0", System.Globalization.CultureInfo.InvariantCulture), label))
                                    { Size = 12f, Color = Tok.TextSecondary, Grow = 1f },
                            ],
                        },
                        DensityPreviewRows(density),
                    ],
                },
            ],
        };
    }

    static Element DensityPreviewRows(int density)
    {
        float rowH = TrackRow.RowHeightFor(density);
        Element Mock(string label) => new BoxEl
        {
            Height = rowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Padding = new Edges4(WaveeSpace.M, 0f, WaveeSpace.M, 0f),
            Corners = CornerRadius4.All(WaveeRadius.Control), Fill = Tok.FillSubtleSecondary,
            Children =
            [
                new BoxEl { Width = rowH - 16f, Height = rowH - 16f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleTertiary },
                new TextEl(label) { Size = 13f, Color = Tok.TextPrimary, Grow = 1f },
            ],
        };

        return new BoxEl
        {
            Direction = 1, Gap = 4f,
            Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.S, WaveeSpace.S),
            Corners = CornerRadius4.All(WaveeRadius.Control),
            Fill = Tok.FillSubtleSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
            Children =
            [
                Mock(Loc.Get(Strings.Settings.Appearance.DensityPreview1)),
                Divider(),
                Mock(Loc.Get(Strings.Settings.Appearance.DensityPreview2)),
                Divider(),
                Mock(Loc.Get(Strings.Settings.Appearance.DensityPreview3)),
            ],
        };
    }
}
