using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
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
        Loc.Get(Strings.Settings.Choice.Hero),
    ];

    // The lyrics SECOND line. Ordered to match WaveeSettings.LyricsSecondaryLine (0 none · 1 translation · 2
    // romanization) so the SelectorBar index IS the stored value — the ThemeMode/RowDensity convention.
    static string[] LyricsSecondaryLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Off),
        Loc.Get(Strings.Settings.Choice.Translation),
        Loc.Get(Strings.Settings.Choice.Romanization),
    ];

    Element GeneralTab(Services? svc, Action<float>? requestTheme)
    {
        var settings = svc?.Settings;
        int themeMode = settings?.Get(WaveeSettings.ThemeMode) ?? 0;
        int density = Math.Clamp(_density.Value, 0, DensityLabels().Length - 1);
        int pageLayout = Math.Clamp(settings?.Get(WaveeSettings.DetailPageLayout) ?? 0, 0, PageLayoutLabels().Length - 1);
        int lyricsSecondary = Math.Clamp(settings?.Get(WaveeSettings.LyricsSecondaryLine) ?? 0, 0, LyricsSecondaryLabels().Length - 1);
        var languageOptions = LanguageOptions();
        int language = Math.Clamp(_language.Value, 0, languageOptions.Codes.Length - 1);

        Element AppearanceToggle(SettingKey<bool> key) => ToggleSwitch.Create(new Signal<bool>(settings?.Get(key) ?? false), onChange: _ =>
        {
            if (settings is null) return;
            settings.Set(key, !settings.Get(key));
            AppearancePrefs.Bump();
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

        // The scheme association is applied AT THE TOGGLE, not at next launch: a user who turns this on expects the very
        // next spotify: link to open here, and one who turns it off expects the scheme handed straight back.
        Element SpotifyLinksToggle() => ToggleSwitch.Create(
            new Signal<bool>(settings?.Get(WaveeSettings.HandleSpotifyLinks) ?? false), onChange: _ =>
            {
                if (settings is null) return;
                bool next = !settings.Get(WaveeSettings.HandleSpotifyLinks);
                settings.Set(WaveeSettings.HandleSpotifyLinks, next);
                DeepLink.SyncSpotifySchemeRegistration(next);
                Bump();
            }, style: SettingsCard.CompactToggleStyle());

        // (The "Use base Mica" row is gone, but NOT because the shell covers the backdrop — it does the opposite: the
        // authenticated shell is bare Mica with translucent content-layer rungs over it, so the DWM material is visible
        // through every chrome band. The row went away because MicaAlt is the one right answer for that stack and a
        // base/alt toggle is a choice with no good second option. WaveeSettings.WindowMaterialBaseMica + its Program.cs
        // seed stay — they still pick the material.)

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

        // Its own writer rather than an AppearanceToggle: the lyrics surfaces re-read this one under LyricsPrefs.Epoch
        // (which the rail/immersive header toggles also bump), not under AppearancePrefs — one setting, one epoch, so a
        // change from either place reaches both mounted surfaces on the same frame.
        void SetLyricsSecondary(int i)
        {
            LyricsPrefs.Set(settings, i);
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
            // The immersive-lyrics cover drift. A plain AppearanceToggle: its Bump() raises AppearancePrefs.Epoch, which
            // ImmersiveLyricsSurface reads, so flipping it starts/stops the drift on an OPEN surface — no restart.
            SettingsRow(Loc.Get(Strings.Settings.Appearance.LyricsBackdrop), Loc.Get(Strings.Settings.Appearance.LyricsBackdropSub),
                AppearanceToggle(WaveeSettings.LyricsAnimatedBackdrop), Icons.Brush),
            // …and the lyrics SECOND line, beside it: both are choices about the lyrics reading surface. The rail and
            // immersive headers offer the same three states as a cycling toggle when the document has the data; this row
            // is where the preference lives when it does not (and where a user goes looking for it).
            SettingsRow(Loc.Get(Strings.Settings.Appearance.LyricsSecondary), Loc.Get(Strings.Settings.Appearance.LyricsSecondarySub),
                SelectorBar.Create(LyricsSecondaryLabels(), new Signal<int>(lyricsSecondary), onChange: SetLyricsSecondary),
                Icons.Globe),
            DensityBlock(density, SetDensity),
            SettingsRow(Loc.Get(Strings.Settings.Appearance.PageLayout), Loc.Get(Strings.Settings.Appearance.PageLayoutSub),
                PageLayoutCards(pageLayout, SetPageLayout), Icons.List),
            // …and how far that page's art-derived TONE reaches. Beside the layout row because it is the same kind of
            // choice about the same surface. A plain AppearanceToggle: its Bump() raises AppearancePrefs.Epoch, which
            // every mounted DetailShell already reads, so flipping it re-solves an open page's ground with no restart.
            SettingsRow(Loc.Get(Strings.Settings.Appearance.PageTone), Loc.Get(Strings.Settings.Appearance.PageToneSub),
                AppearanceToggle(WaveeSettings.DetailPageToneHeroOnly), Icons.Brush),
            // The sidebar design is the last item of the Appearance group — beside the other layout choices, before the
            // Language header (§C6.3). A Component rather than an inline block: the card needs SidebarPreferences and the
            // nav action from CONTEXT, and GeneralTab runs only while the General tab is selected, so a hook added here
            // would be a conditional hook (it would vanish from the page's hook order the moment another tab renders).
            SettingsSectionHeader(Loc.Get(Strings.Settings.Sidebar.Title), Icons.SplitView),
            Embed.Comp(() => new SidebarSettingsCard()),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Links.Title), Icons.Link),
            SettingsRow(Loc.Get(Strings.Settings.Links.Spotify), Loc.Get(Strings.Settings.Links.SpotifySub),
                SpotifyLinksToggle(), Icons.Link),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Language.Title), Icons.Globe),
            SettingsRow(Loc.Get(Strings.Settings.Language.Label), Loc.Get(Strings.Settings.Language.RestartSub),
                ComboBox.Create(languageOptions.Labels, _language, width: 260f, isEnabled: settings is not null,
                    onChange: SetLanguage), Icons.Globe));
    }

    // ── the page-layout picker: the preview cards ARE the selector (a radio pair, PaletteRow-style) ─────────────────
    // Each card is a mini skeleton-bar wireframe of the page SYSTEM it selects — Automatic: a narrow metadata rail
    // (art + title/meta bars + a pill) BESIDE a column of full-width track rows (the rail-when-wide layout); Hero:
    // adaptive artwork + identity ABOVE the track rows at every width. The selected card lights
    // its blocks + border with the accent so the choice reads at a glance.
    static Element PageLayoutCards(int selected, Action<int> set)
    {
        Element Card(int value, string label, bool automatic)
        {
            bool on = selected == value;
            ColorF block = on ? Tok.AccentDefault : Tok.FillSubtleTertiary;
            ColorF faint = on ? Tok.AccentDefault with { A = 0.45f } : Tok.FillSubtleTertiary with { A = 0.7f };

            Element Bar(float w, float h) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(h / 2f), Fill = faint };
            Element RowBar() => new BoxEl { Height = 4f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(2f), Fill = faint };
            Element Art(float edge) => new BoxEl { Width = edge, Height = edge, Corners = CornerRadius4.All(4f), Fill = block, Shrink = 0f };
            Element Pill() => new BoxEl { Width = 24f, Height = 8f, Corners = CornerRadius4.All(Radii.Control), Fill = block };
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
                // Hero: an immersive artwork field and compact identity above the track rows.
                : new BoxEl
                {
                    Direction = 1, Gap = 5f, Grow = 1f, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Gap = 4f, AlignItems = FlexAlign.Stretch,
                            Children =
                            [
                                new BoxEl
                                {
                                    Height = 24f, AlignSelf = FlexAlign.Stretch,
                                    Corners = CornerRadius4.All(4f), Fill = block,
                                },
                                new BoxEl
                                {
                                    Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center,
                                    Children = [Bar(48f, 6f), Bar(28f, 4f), Pills()],
                                },
                            ],
                        },
                        RowBar(), RowBar(), RowBar(),
                    ],
                };

            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => set(value),
                Children =
                [
                    new BoxEl
                    {
                        // Drop 8f→7f when selected so the 1f→2f border growth draws inward and the wireframe stays put.
                        Width = 116f, Height = 84f, Padding = Edges4.All(on ? 7f : 8f),
                        Direction = 1, ClipToBounds = true,
                        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillSubtleSecondary,
                        BorderWidth = on ? 2f : 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                        HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
                        Children = [sketch],
                    },
                    new TextEl(label) { Size = 12f, LineHeight = 16f, Weight = (ushort)(on ? 600 : 400), Color = on ? Tok.TextPrimary : Tok.TextSecondary },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, Wrap = true, AlignItems = FlexAlign.Start,
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
                        BorderWidth = on ? 2f : 1f,
                        BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                        Children = on
                            ? [new TextEl(Icons.Accept) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextOnAccentPrimary }]
                            : [],
                    },
                    new TextEl(label) { Size = 12f, LineHeight = 16f, Weight = (ushort)(on ? 600 : 400), Color = on ? Tok.TextPrimary : Tok.TextSecondary },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Wrap = true,
            Children =
            [
                Swatch("warm", Loc.Get(Strings.Settings.Appearance.PaletteWarm), WaveeColors.PresetSwatch(Tok.WarmPalette)),
                Swatch("slate", Loc.Get(Strings.Settings.Appearance.PaletteSlate), WaveeColors.PresetSwatch(Tok.SlatePalette)),
                Swatch("neutral", Loc.Get(Strings.Settings.Appearance.PaletteNeutral), WaveeColors.PresetSwatch(Tok.NeutralPalette)),
                Swatch("accent", Loc.Get(Strings.Settings.Appearance.PaletteAccent), WaveeColors.PresetSwatch(Tok.AccentTintedPalette)),
            ],
        };
    }

    // ONE settings row, so it IS the card: SettingsCard already paints the group chrome (radius, fill, hairline).
    // The hand-built BoxEl that used to wrap it drew a second card around the first, doubling the stroke.
    Element DensityBlock(int density, Action<int> setDensity)
        => SettingsRow(Loc.Get(Strings.Settings.Appearance.RowDensity), Loc.Get(Strings.Settings.Appearance.RowDensitySub),
            DensityCards(density, setDensity), Icons.List, align: SettingsCard.ContentAlignment.Vertical);

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
                Height = rowHeight, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
                Corners = CornerRadius4.All(3f), Fill = faint,
                Children =
                [
                    new BoxEl { Width = rowHeight - 2f, Height = rowHeight - 2f, Corners = CornerRadius4.All(2f), Fill = block },
                    new BoxEl { Width = 42f, Height = 4f, Corners = CornerRadius4.All(2f), Fill = block },
                ],
            };

            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => set(value),
                Children =
                [
                    new BoxEl
                    {
                        Width = 116f, Height = 84f, Padding = Edges4.All(on ? 7f : 8f),
                        Direction = 1, Gap = 4f, Justify = FlexJustify.Center, ClipToBounds = true,
                        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillSubtleSecondary,
                        BorderWidth = on ? 2f : 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                        HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
                        Children = [MockRow(), MockRow(), MockRow()],
                    },
                    new TextEl(labels[value])
                        { Size = 12f, LineHeight = 16f, Weight = (ushort)(on ? 600 : 400), Color = on ? Tok.TextPrimary : Tok.TextSecondary },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, Wrap = true, AlignItems = FlexAlign.Start,
            Children = [Card(0), Card(1), Card(2), Card(3)],
        };
    }

    // ── the Sidebar group (§C6.3) ─────────────────────────────────────────────────────────────────────────────────────
    /// <summary>One grouped card (the <c>DensityBlock</c> idiom) holding the shared three-card design picker and — only
    /// while Wavee Curated is the active design — the "Customize sidebar" link row.
    ///
    /// <para>NESTED inside <see cref="SettingsPage"/> so it can use the page's own <c>SettingsRow</c>/<c>Divider</c>
    /// helpers (a sibling class could not), and a <see cref="Component"/> so it can take
    /// <see cref="SidebarPreferences"/>, <see cref="Services"/> and the nav action from CONTEXT rather than from frozen
    /// props — GeneralTab is not a render body of its own, so it cannot hold the hooks these need.</para>
    ///
    /// <para>No page-epoch <c>Bump()</c> is involved: the card subscribes to <c>prefs.Design</c> directly, so a switch
    /// made from the sidebar's own layout menu while this page is open re-renders the cards AND appears/disappears the
    /// link row live. Ctor-arg-free, so the frozen-props contract is trivially satisfied.</para></summary>
    sealed class SidebarSettingsCard : Component
    {
        public override Element Render()
        {
            var prefs = UseContext(SidebarPreferences.Slot);
            var svc = UseContext(Services.Slot);
            var go = UseContext(HistoryStore.NavCtx);
            var settings = svc?.Settings;

            // The LIVE design (a subscription when the service is present; the persisted value when the page is mounted
            // in isolation without one). Both paths coerce through the same table, so a hand-edited value cannot make the
            // picker show nothing selected.
            var design = prefs is not null
                ? prefs.Design.Value
                : SidebarDesignGating.ActiveDesign(settings);

            // Compact cards: this row shares a page column with the header/description block, and the compact
            // ladder keeps all three visible on a narrow window before the row has to wrap.
            Element picker = SidebarDesignPicker.Row(prefs, settings, compact: true);

            // Rendered only while Curated is active (§C6.3): the customizer edits the Curated document, so offering it
            // for Classic/Library would navigate to an editor for something the user is not looking at. The quick layout
            // menu's "Customize sidebar…" row is the path that switches first — this one never switches silently.
            if (!SidebarDesignGating.CanCustomize(design))
                // Nothing to group: ONE settings row already IS the card. The hand-built BoxEl this replaced painted a
                // second card (radius + fill + hairline) around a SettingsCard that carries its own.
                return SettingsRow(Loc.Get(Strings.Settings.Sidebar.Design), Loc.Get(Strings.Settings.Sidebar.DesignSub),
                    picker, Icons.SplitView, align: SettingsCard.ContentAlignment.Vertical);

            // Two rows ⇒ the engine's grouped-card control (the same SettingsExpander idiom SettingsPage.Playback.cs
            // uses), which owns the group chrome, the divider and the item indentation.
            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Settings.Sidebar.Design),
                Description = Loc.Get(Strings.Settings.Sidebar.DesignSub),
                HeaderIcon = Icons.SplitView,
                Content = picker,
                InitiallyExpanded = true,
                Items =
                [
                    SettingsItem(Loc.Get(Strings.Settings.Sidebar.Customize),
                        Loc.Get(Strings.Settings.Sidebar.CustomizeSub), control: null,
                        isClickEnabled: true, onClick: () => go(SidebarLayoutMenu.CustomizeRoute, null),
                        icon: Icons.Edit),
                ],
            });
        }
    }
}
