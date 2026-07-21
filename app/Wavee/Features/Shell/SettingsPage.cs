using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── Settings shell — tab strip + shared layout helpers; tab bodies live in partials + DiagnosticsPanel ─────────────
sealed partial class SettingsPage : Component
{
    const int TabGeneral = 0, TabPlayback = 1, TabStorage = 2, TabDiagnostics = 3, TabAbout = 4;
    const float SettingsContentMaxWidth = 1000f;
    const float SettingsCardSpacing = 4f;
    static readonly Edges4 SettingsSectionHeaderMargin = new(1f, 30f, 0f, 6f);
    static readonly string[] s_tabKeys = ["general", "playback", "storage", "diagnostics", "about"];

    readonly Signal<int> _tab = new(0);
    readonly Signal<int> _uiEpoch = new(0);

    IOverlayService? _overlay;

    void Bump() => _uiEpoch.Value = _uiEpoch.Peek() + 1;

    void ConfirmThen(string title, string body, string primaryText, Action onConfirm) =>
        SettingsShared.Confirm(_overlay, title, body, primaryText, onConfirm);

    static string[] TabLabels() =>
    [
        Loc.Get(Strings.Settings.Tabs.General),
        Loc.Get(Strings.Settings.Tabs.Playback),
        Loc.Get(Strings.Settings.Tabs.Storage),
        Loc.Get(Strings.Settings.Tabs.Diagnostics),
        Loc.Get(Strings.Settings.Tabs.About),
    ];

    static string[] ThemeLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.System),
        Loc.Get(Strings.Settings.Choice.Light),
        Loc.Get(Strings.Settings.Choice.Dark),
    ];

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var svc = UseContext(Services.Slot);
        var requestTheme = UseContext(ThemeControl.Request);
        var post = UsePost();
        var seeded = UseRef(false);
        _overlay = UseContext(Overlay.Service);

        UseEffect(() =>
        {
            if (seeded.Value || svc is null) return;
            seeded.Value = true;
            _density.Value = svc.Settings.Get(WaveeSettings.RowDensity);
            _quality.Value = Math.Clamp(svc.Settings.Get(WaveeSettings.PlaybackQuality), 0, 2);
            _eqPreset.Value = EqPresetIndex(svc.Settings.Get(WaveeSettings.EqualizerPreset));
            int crossMs = Math.Clamp(svc.Settings.Get(WaveeSettings.CrossfadeMs), 0, 12_000);
            _crossSecs.Value = crossMs / 1000.0;
            _crossSlider.Value = (float)(crossMs / 1000.0);
            _language.Value = LanguageIndex(svc.Settings.Get(WaveeSettings.UiCulture));
        }, DepKey.Empty);

        _ = _uiEpoch.Value;
        _ = PlayerBarPrefs.Epoch.Value;
        int tab = _tab.Value;

        UseEffect(() =>
        {
            if (tab == TabStorage && _storageLoad.Peek() == StorageLoadPhase.NotStarted)
                RefreshStorage(post);
        }, tab);

        Element body = tab switch
        {
            TabPlayback => PlaybackTab(svc),
            TabStorage => StorageTab(svc, post),
            TabDiagnostics => new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f,
                Children = [Embed.Comp(() => new DiagnosticsPanel(svc?.Settings))],
            },
            TabAbout => AboutTab(svc, hooks),
            _ => GeneralTab(svc, requestTheme),
        };

        Element content = tab == TabDiagnostics
            ? new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
                Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
                Children = [body],
            }
            : ScrollView(new BoxEl
            {
                Direction = 1,
                Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.XXL),
                Children = [SettingsContentColumn(body)],
            }) with { Grow = 1f, ScrollKey = "settings:" + s_tabKeys[tab], Key = "settings:scroll:" + s_tabKeys[tab] };

        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Children =
            [
                Header(),
                new BoxEl
                {
                    Direction = 1, Padding = new Edges4(WaveeSpace.L, 0f, WaveeSpace.L, 0f),
                    Children =
                    [
                        SelectorBar.Create(TabLabels(), _tab),
                        Divider(),
                    ],
                },
                content,
            ],
        };
    }

    static Element SettingsContentColumn(Element body) => new BoxEl
    {
        Direction = 1,
        MaxWidth = SettingsContentMaxWidth,
        AlignSelf = FlexAlign.Stretch,
        Children = [body],
    };

    static Element SettingsTabStack(params Element[] children) => new BoxEl
    {
        Direction = 1,
        Gap = SettingsCardSpacing,
        AlignSelf = FlexAlign.Stretch,
        Children = children,
    };

    static Element SettingsSectionHeader(string title, string? icon = null) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
        Margin = SettingsSectionHeaderMargin,
        Children = icon is null
            ? [BodyStrong(title)]
            : [Icon(icon, 16f, Tok.TextSecondary), BodyStrong(title)],
    };

    static Element Header() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.M),
        Children =
        [
            Icon(Icons.Settings, 22f, Tok.TextPrimary),
            WaveeType.PageHero(Loc.Get(Strings.Settings.Title)) with { Grow = 1f },
        ],
    };

    static Element SettingsRow(string label, string? sub, Element? control = null, string? icon = null,
                               SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                               bool isClickEnabled = false, Action? onClick = null, bool isEnabled = true)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = label,
            Description = sub,
            HeaderIcon = icon,
            Content = control,
            Alignment = align,
            IsClickEnabled = isClickEnabled,
            IsActionIconVisible = isClickEnabled,
            OnClick = onClick,
            IsEnabled = isEnabled,
        });

    static Element SettingsItem(string label, string? sub, Element? control = null,
                                SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                                bool isEnabled = true, bool isClickEnabled = false, Action? onClick = null,
                                string? icon = null)
        => SettingsExpander.Item(label, sub, control, align, isEnabled, isClickEnabled, onClick, icon);

    static Element StatPill(string value, string label) => new BoxEl
    {
        Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(8f, 3f, 8f, 3f), Corners = CornerRadius4.All(WaveeRadius.Pill),
        Fill = Tok.FillSubtleSecondary,
        Children =
        [
            new TextEl(value) { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary },
        ],
    };
}
