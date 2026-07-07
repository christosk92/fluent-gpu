using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── Settings — a tabbed page: General · Playback · Storage & cache · Diagnostics · About ─────────────────────────────
// General/Playback surface the persisted WaveeSettings (theme/palette/density, runtime status, quality, volume);
// Diagnostics is the SESSION log viewer (the in-memory ring is per-launch; the wavee.log FILE keeps history across runs);
// Storage computes on-disk sizes per visit; About carries version, environment and licenses.
sealed class SettingsPage : Component
{
    const int TabGeneral = 0, TabPlayback = 1, TabStorage = 2, TabDiagnostics = 3, TabAbout = 4;
    static readonly string[] s_tabKeys = ["general", "playback", "storage", "diagnostics", "about"];
    static readonly string[] s_eqBandLabels = ["31 Hz", "63 Hz", "125 Hz", "250 Hz", "500 Hz", "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz"];
    static readonly string[] s_eqPresetIds = ["flat", "bass", "treble", "vocal", "radio", "proof"];
    static readonly float[][] s_eqPresetGains =
    [
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [6, 5, 4, 2, 0, 0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0, 1, 2, 3, 4, 5],
        [-2, -1, 0, 2, 4, 4, 2, 0, -1, -2],
        [0, 2, -2, 0, 0, 2, 4, 2, 2, 2],
        [12, -12, 12, -12, 12, -12, 12, -12, 12, -12],
    ];

    const int MaxVisibleRows = 500;
    static readonly string[] s_levelLabels = ["All", "Info+", "Warnings", "Errors"];
    static readonly string[] s_categories =
    [
        "All", "app", "auth", "connect", "dealer", "spclient", "sync", "mutation",
        "metadata", "playback", "audio", "lyrics", "ui", "engine", "crash", "log"
    ];

    readonly Signal<int> _tab = new(0);
    readonly Signal<int> _uiEpoch = new(0);          // bumped after any settings write → re-render with the new value

    // Diagnostics state
    readonly Signal<string> _search = new("");
    readonly Signal<int> _level = new(1);
    readonly Signal<int> _category = new(0);
    readonly Signal<int> _newestFirst = new(1);
    readonly Signal<int> _groupRepeats = new(1);
    readonly Signal<int> _refresh = new(0);

    // Diagnostics — session switching: 0 = the live ring; 1.. = past sessions parsed from the log file (newest first).
    readonly Signal<int> _session = new(0);
    readonly Signal<int> _diagVersion = new(0);        // bumped when discovery / a session load lands
    List<WaveeLogSessions.Info>? _sessions;            // null = not discovered yet
    bool _sessionsBusy;
    WaveeLogEntry[]? _sessionEntries;
    int _sessionLoaded;                                 // picker index _sessionEntries holds (0 = none)
    bool _sessionLoadBusy;

    // General state
    readonly Signal<int> _density = new(1);
    readonly Signal<int> _eqPreset = new(0);

    static readonly string[] s_bodyBudgetLabels = ["512 MB", "1 GB", "2 GB", "4 GB", "8 GB"];
    static readonly long[] s_bodyBudgetBytes = [512L << 20, 1L << 30, 2L << 30, 4L << 30, 8L << 30];

    // Storage state (snapshot computed off-thread per visit, published via post)
    sealed record StorageSnapshot(long LibraryDb, long Runtime, long Logs, int LogFiles, long Store,
        long AudioBody, long LicenseDb, long Total);
    readonly Signal<int> _storageVersion = new(0);
    StorageSnapshot? _storage;
    bool _storageBusy;

    // About state
    readonly Signal<int> _licOpen = new(-1);

    void Bump() => _uiEpoch.Value = _uiEpoch.Peek() + 1;

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

    static string[] DensityLabels() =>
    [
        Loc.Get(Strings.Settings.Choice.Compact),
        Loc.Get(Strings.Settings.Choice.Default),
        Loc.Get(Strings.Settings.Choice.Cozy),
        Loc.Get(Strings.Settings.Choice.Comfortable),
    ];

    static string[] EqPresetLabels() =>
    [
        Loc.Get(Strings.Settings.Sound.Presets.Flat),
        Loc.Get(Strings.Settings.Sound.Presets.Bass),
        Loc.Get(Strings.Settings.Sound.Presets.Treble),
        Loc.Get(Strings.Settings.Sound.Presets.Vocal),
        Loc.Get(Strings.Settings.Sound.Presets.Radio),
        Loc.Get(Strings.Settings.Sound.Presets.Proof),
    ];

    static string[] EqPresetDescriptions() =>
    [
        Loc.Get(Strings.Settings.Sound.Presets.FlatSub),
        Loc.Get(Strings.Settings.Sound.Presets.BassSub),
        Loc.Get(Strings.Settings.Sound.Presets.TrebleSub),
        Loc.Get(Strings.Settings.Sound.Presets.VocalSub),
        Loc.Get(Strings.Settings.Sound.Presets.RadioSub),
        Loc.Get(Strings.Settings.Sound.Presets.ProofSub),
    ];

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var svc = UseContext(Services.Slot);
        var requestTheme = UseContext(ThemeControl.Request);
        var post = UsePost();
        var timer = UseRef<Timer?>(null);
        var lastVersion = UseRef(-1L);
        var seeded = UseRef(false);

        // Seed the settings-backed local signals once (settings arrive via context, not the ctor).
        UseEffect(() =>
        {
            if (seeded.Value || svc is null) return;
            seeded.Value = true;
            _density.Value = svc.Settings.Get(WaveeSettings.RowDensity);
            _eqPreset.Value = EqPresetIndex(svc.Settings.Get(WaveeSettings.EqualizerPreset));
        });

        // Live log polling — only while the Diagnostics tab is showing THE LIVE SESSION (a past session is a static
        // read from the file; the ring's Version is cheap, but there is no reason to tick a hidden view at 750 ms).
        UseSignalEffect(() =>
        {
            bool diagnostics = _tab.Value == TabDiagnostics && _session.Value == 0;
            timer.Value?.Dispose();
            timer.Value = null;
            if (diagnostics)
            {
                timer.Value = new Timer(_ => post(() =>
                {
                    long v = WaveeLog.Instance.Version;
                    if (v == lastVersion.Value) return;
                    lastVersion.Value = v;
                    _refresh.Value = _refresh.Peek() + 1;
                }), null, 250, 750);
            }
            Reactive.OnCleanup(() => { timer.Value?.Dispose(); timer.Value = null; });
        });

        _ = _uiEpoch.Value;
        int tab = _tab.Value;

        // Storage sizes: recompute when the tab is (re)entered and no snapshot is held.
        UseEffect(() => { if (tab == TabStorage && _storage is null) RefreshStorage(post); }, tab);

        // Past-session discovery (once per page life) + lazy load of the picked session — both off-thread.
        UseEffect(() => { if (tab == TabDiagnostics) DiscoverSessions(post); }, tab);
        UseSignalEffect(() => { _ = _session.Value; EnsureSessionLoaded(post); });

        Element body = tab switch
        {
            TabPlayback => PlaybackTab(svc),
            TabStorage => StorageTab(svc, post),
            TabDiagnostics => DiagnosticsTab(hooks),
            TabAbout => AboutTab(svc, hooks),
            _ => GeneralTab(svc, requestTheme),
        };

        // Diagnostics pins its chrome (session card + command bar + filters) and scrolls ONLY the log rows in an inner
        // ScrollView; every other tab is a simple scrolling card stack.
        Element content = tab == TabDiagnostics
            ? new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
                Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
                Children = [body],
            }
            : ScrollView(new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.L,
                Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.XXL),
                Children = [body],
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
                        SelectorBar.Create(TabLabels(), tab, i => _tab.Value = i),
                        new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                    ],
                },
                content,
            ],
        };
    }

    // ── Diagnostics session plumbing ──────────────────────────────────────────────────────────────────────────────────

    void DiscoverSessions(Action<Action> post)
    {
        if (_sessions is not null || _sessionsBusy) return;
        _sessionsBusy = true;
        string? file = WaveeLog.Instance.FilePath;
        int pid = Environment.ProcessId;
        _ = Task.Run(() =>
        {
            var list = WaveeLogSessions.ListPastSessions(file, pid);
            post(() =>
            {
                _sessions = list;
                _sessionsBusy = false;
                _diagVersion.Value = _diagVersion.Peek() + 1;
            });
        });
    }

    void EnsureSessionLoaded(Action<Action> post)
    {
        int sel = _session.Peek();
        if (sel == 0 || _sessionLoadBusy || _sessions is not { } sessions || _sessionLoaded == sel) return;
        if (sel - 1 >= sessions.Count) return;
        var info = sessions[sel - 1];
        _sessionLoadBusy = true;
        _ = Task.Run(() =>
        {
            var entries = WaveeLogSessions.LoadSession(info);
            post(() =>
            {
                _sessionEntries = entries;
                _sessionLoaded = sel;
                _sessionLoadBusy = false;
                _diagVersion.Value = _diagVersion.Peek() + 1;
                EnsureSessionLoaded(post);   // the selection may have moved while this load ran
            });
        });
    }

    static string SessionLabel(WaveeLogSessions.Info s) =>
        (s.StartUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(s.StartUnixMs).ToLocalTime().ToString("MMM d · HH:mm", CultureInfo.InvariantCulture)
            : "pid " + s.Pid.ToString(CultureInfo.InvariantCulture))
        + " · " + s.EntryCount.ToString(CultureInfo.InvariantCulture) + " events";

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

    // ── shared card scaffolding ───────────────────────────────────────────────────────────────────────────────────────

    static Element Card(params Element[] kids) => new BoxEl
    {
        Direction = 1, Corners = CornerRadius4.All(WaveeRadius.Card),
        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        ClipToBounds = true,
        Children = kids,
    };

    static Element CardHeader(string title, string? caption, Element? trailing = null)
    {
        var stack = new BoxEl
        {
            Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children = caption is { Length: > 0 }
                ? [new TextEl(title) { Size = 14f, Weight = 700, Color = Tok.TextPrimary },
                   new TextEl(caption) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis }]
                : [new TextEl(title) { Size = 14f, Weight = 700, Color = Tok.TextPrimary }],
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
            Children = trailing is null ? [stack] : [stack, trailing],
        };
    }

    static Element RowDivider() => new BoxEl
    { Height = 1f, Margin = new Edges4(WaveeSpace.L, 0f, WaveeSpace.L, 0f), Fill = Tok.StrokeDividerDefault };

    static Element Section(string title, string? caption, params Element[] cards)
    {
        var kids = new List<Element>(cards.Length + 1)
        {
            new BoxEl
            {
                Direction = 1,
                Gap = 2f,
                Padding = new Edges4(2f, 0f, 2f, 0f),
                Children = caption is { Length: > 0 }
                    ? [new TextEl(title) { Size = 20f, Weight = 650, Color = Tok.TextPrimary },
                       new TextEl(caption) { Size = 12.5f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap }]
                    : [new TextEl(title) { Size = 20f, Weight = 650, Color = Tok.TextPrimary }],
            },
        };
        kids.AddRange(cards);
        return new BoxEl { Direction = 1, Gap = 8f, Children = kids.ToArray() };
    }

    static Element SettingsRow(string label, string? sub, Element control, string? icon = null,
                               SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right)
        => SettingsCard.Create(new SettingsCard.Options
        {
            Header = label,
            Description = sub,
            HeaderIcon = icon,
            Content = control,
            Alignment = align,
        });

    static Element SettingsGroup(string title, string? caption, string? icon, params Element[] items)
        => SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = title,
            Description = caption,
            HeaderIcon = icon,
            InitiallyExpanded = true,
            Items = items,
        });

    static Element SettingsItem(string label, string? sub, Element? control = null,
                                SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                                bool isEnabled = true, bool isClickEnabled = false, Action? onClick = null,
                                string? icon = null)
        => SettingsExpander.Item(label, sub, control, align, isEnabled, isClickEnabled, onClick, icon);

    static Element SettingRow(string label, string? sub, Element control) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.L, Wrap = true,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
        Children =
        [
            new BoxEl
            {
                Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 220f,
                Children = sub is { Length: > 0 }
                    ? [new TextEl(label) { Size = 14f, Color = Tok.TextPrimary },
                       new TextEl(sub) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap }]
                    : [new TextEl(label) { Size = 14f, Color = Tok.TextPrimary }],
            },
            control,
        ],
    };

    static Element KvRow(string label, string? value, bool mono = false) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = 96f, Shrink = 0f },
            new TextEl(string.IsNullOrEmpty(value) ? "—" : value)
            {
                Size = 12f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f,
                FontFamily = mono ? "Cascadia Code" : null,
            },
        ],
    };

    static Element InnerPanel(params Element[] kids) => new BoxEl
    {
        Direction = 1, Gap = 6f, Padding = Edges4.All(12f),
        Margin = new Edges4(WaveeSpace.L, 0f, WaveeSpace.L, WaveeSpace.M),
        Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(WaveeRadius.Control),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = kids,
    };

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

    static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee");

    static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"")
            { UseShellExecute = false });
        }
        catch { /* best-effort — a missing Explorer/path must not throw into the UI */ }
    }

    // ── General ───────────────────────────────────────────────────────────────────────────────────────────────────────

    Element GeneralTab(Services? svc, Action<float>? requestTheme)
    {
        var settings = svc?.Settings;
        int themeMode = settings?.Get(WaveeSettings.ThemeMode) ?? 0;

        void SetTheme(int mode)
        {
            WaveeTheme.ApplyThemeMode(mode, settings);
            requestTheme?.Invoke(250f);
            Bump();
        }

        return SettingsGroup(Loc.Get(Strings.Settings.Appearance.Title), Loc.Get(Strings.Settings.Appearance.Subtitle), Icons.Brush,
            SettingsItem(Loc.Get(Strings.Settings.Appearance.Theme), Loc.Get(Strings.Settings.Appearance.ThemeSub),
                SelectorBar.Create(ThemeLabels(), themeMode, SetTheme)),
            SettingsItem(Loc.Get(Strings.Settings.Appearance.Palette), Loc.Get(Strings.Settings.Appearance.PaletteSub),
                PaletteRow(settings, requestTheme)),
            SettingsItem(Loc.Get(Strings.Settings.Appearance.RowDensity), Loc.Get(Strings.Settings.Appearance.RowDensitySub),
                ComboBox.Create(DensityLabels(), _density, width: 170f,
                    onSelectionChanged: i => { settings?.Set(WaveeSettings.RowDensity, i); Bump(); })));
    }

    Element PaletteRow(IAppSettings? settings, Action<float>? requestTheme)
    {
        string active = Tok.Palette.Id;

        Element Swatch(string id, string label, ColorF fill)
        {
            bool on = active == id;
            return new BoxEl
            {
                Direction = 1, Gap = 4f, AlignItems = FlexAlign.Center, Width = 52f,
                Role = AutomationRole.Button, Focusable = true,
                OnClick = () => { WaveeTheme.ApplyPalette(id, settings); requestTheme?.Invoke(250f); Bump(); },
                Children =
                [
                    new BoxEl
                    {
                        Width = 28f, Height = 28f, Corners = CornerRadius4.All(14f), Fill = fill,
                        BorderWidth = on ? 2f : 1f,
                        BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                    },
                    new TextEl(label) { Size = 10f, Color = on ? Tok.TextPrimary : Tok.TextTertiary },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, Wrap = true,
            Children =
            [
                Swatch("warm", Loc.Get(Strings.Settings.Appearance.PaletteWarm), WaveeColors.PresetSwatch(Tok.WarmPalette)),
                Swatch("slate", Loc.Get(Strings.Settings.Appearance.PaletteSlate), WaveeColors.PresetSwatch(Tok.SlatePalette)),
                Swatch("neutral", Loc.Get(Strings.Settings.Appearance.PaletteNeutral), WaveeColors.PresetSwatch(Tok.NeutralPalette)),
                Swatch("accent", Loc.Get(Strings.Settings.Appearance.PaletteAccent), WaveeColors.PresetSwatch(Tok.AccentTintedPalette)),
            ],
        };
    }

    // ── Playback ──────────────────────────────────────────────────────────────────────────────────────────────────────

    Element PlaybackTab(Services? svc)
    {
        var settings = svc?.Settings;
        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.L,
            Children =
            [
                RuntimeGroup(svc),
                SettingsGroup(Loc.Get(Strings.Settings.Playback.AudioTitle), Loc.Get(Strings.Settings.Playback.AudioSub), Icons.MusicNote,
                    QualityOption(svc, 0, Loc.Get(Strings.Settings.Playback.QualityNormal), Loc.Get(Strings.Settings.Playback.QualityNormalSub)),
                    QualityOption(svc, 1, Loc.Get(Strings.Settings.Playback.QualityHigh), Loc.Get(Strings.Settings.Playback.QualityHighSub)),
                    QualityOption(svc, 2, Loc.Get(Strings.Settings.Playback.QualityVeryHigh), Loc.Get(Strings.Settings.Playback.QualityVeryHighSub)),
                    QualityComingSoon(Loc.Get(Strings.Settings.Playback.QualityLossless), Loc.Get(Strings.Settings.Playback.QualityLosslessSub)),
                    SettingsItem(Loc.Get(Strings.Settings.Playback.RememberVolume), Loc.Get(Strings.Settings.Playback.RememberVolumeSub),
                        ToggleSwitch.Create(settings?.Get(WaveeSettings.RememberVolume) ?? true, () =>
                        {
                            if (settings is null) return;
                            settings.Set(WaveeSettings.RememberVolume, !settings.Get(WaveeSettings.RememberVolume));
                            Bump();
                        }, style: SettingsCard.CompactToggleStyle())),
                    SettingsItem(Loc.Get(Strings.Settings.Playback.Autoplay), Loc.Get(Strings.Settings.Playback.AutoplaySub),
                        ToggleSwitch.Create(settings?.Get(WaveeSettings.AutoplayEnabled) ?? true, () =>
                        {
                            if (settings is null) return;
                            settings.Set(WaveeSettings.AutoplayEnabled, !settings.Get(WaveeSettings.AutoplayEnabled));
                            Bump();
                        }, style: SettingsCard.CompactToggleStyle()))),
                DspCard(svc),
                SettingsGroup(Loc.Get(Strings.Settings.Playback.PlayerBar), null, Icons.Clock,
                    SettingsItem(Loc.Get(Strings.Settings.Playback.ShowRemaining), Loc.Get(Strings.Settings.Playback.ShowRemainingSub),
                        ToggleSwitch.Create(settings?.Get(WaveeSettings.PlayerBarShowRemaining) ?? true, () =>
                        {
                            if (settings is null) return;
                            settings.Set(WaveeSettings.PlayerBarShowRemaining, !settings.Get(WaveeSettings.PlayerBarShowRemaining));
                            PlayerBarPrefs.Bump();
                            Bump();
                        }, style: SettingsCard.CompactToggleStyle()))),
            ],
        };
    }

    Element DspCard(Services? svc)
    {
        var settings = svc?.Settings;
        bool eqOn = settings?.Get(WaveeSettings.EqualizerEnabled) ?? false;
        bool crossOn = settings?.Get(WaveeSettings.CrossfadeEnabled) ?? false;
        int crossMs = Math.Clamp(settings?.Get(WaveeSettings.CrossfadeMs) ?? 5000, 0, 30_000);
        float[] gains = ReadEqGains(settings);

        int preset = Math.Clamp(_eqPreset.Value, 0, s_eqPresetIds.Length - 1);
        string[] presetLabels = EqPresetLabels();
        string[] presetDescriptions = EqPresetDescriptions();

        return SettingsGroup(Loc.Get(Strings.Settings.Sound.Title), Loc.Get(Strings.Settings.Sound.Subtitle), Icons.MusicNote,
            SettingsItem(Loc.Get(Strings.Settings.Sound.Equalizer), Loc.Get(Strings.Settings.Sound.EqualizerSub),
                ToggleSwitch.Create(eqOn, () =>
                {
                    if (settings is null) return;
                    settings.Set(WaveeSettings.EqualizerEnabled, !settings.Get(WaveeSettings.EqualizerEnabled));
                    PushDsp(svc);
                    Bump();
                }, style: SettingsCard.CompactToggleStyle())),
            SettingsItem(Loc.Get(Strings.Settings.Sound.Preset), presetDescriptions[preset],
                new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    Gap = WaveeSpace.S,
                    Wrap = true,
                    Children =
                    [
                        ComboBox.Create(presetLabels, _eqPreset, width: 180f,
                            isEnabled: settings is not null,
                            onSelectionChanged: i => ApplyEqPreset(svc, settings, i)),
                        Button.Standard(Loc.Get(Strings.Settings.Sound.Reset), () => ApplyEqPreset(svc, settings, 0), isEnabled: settings is not null),
                    ],
                }),
            SettingsItem(Loc.Get(Strings.Settings.Sound.Curve),
                eqOn ? Loc.Get(Strings.Settings.Sound.CurveOn) : Loc.Get(Strings.Settings.Sound.CurveOff),
                WaveeEqualizerCurve.Create(gains, (band, gain) => SetEqBand(svc, settings, band, gain), eqOn && settings is not null),
                SettingsCard.ContentAlignment.Vertical),
            SettingsItem(Loc.Get(Strings.Settings.Sound.Crossfade), Loc.Get(Strings.Settings.Sound.CrossfadeSub),
                ToggleSwitch.Create(crossOn, () =>
                {
                    if (settings is null) return;
                    settings.Set(WaveeSettings.CrossfadeEnabled, !settings.Get(WaveeSettings.CrossfadeEnabled));
                    PushDsp(svc);
                    Bump();
                }, style: SettingsCard.CompactToggleStyle())),
            SettingsItem(Loc.Get(Strings.Settings.Sound.CrossfadeDuration), Strings.Settings.Sound.Seconds((crossMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)),
                Slider.Ranged(crossMs / 1000f, seconds => SetCrossfadeDuration(svc, settings, (int)MathF.Round(seconds * 1000f)),
                    new Slider.Options
                    {
                        Min = 0f,
                        Max = 30f,
                        Step = 0.5f,
                        TickFrequency = 5f,
                        IsThumbToolTipEnabled = true,
                        ThumbToolTipValueConverter = v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
                    },
                    length: 260f,
                    isEnabled: settings is not null)));
    }

    Element EqBandRow(Services? svc, IAppSettings? settings, float[] gains, int band)
    {
        float gain = band >= 0 && band < gains.Length ? gains[band] : 0f;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
            Children =
            [
                new TextEl(s_eqBandLabels[band]) { Size = 12f, Color = Tok.TextSecondary, Width = 58f },
                new TextEl(FormatDb(gain)) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, Width = 54f },
                Button.Standard("-1", () => UpdateEqBand(svc, settings, band, -1f), isEnabled: settings is not null),
                Button.Standard("+1", () => UpdateEqBand(svc, settings, band, 1f), isEnabled: settings is not null),
            ],
        };
    }

    static string FormatDb(float gain) =>
        gain.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " dB";

    static float[] ReadEqGains(IAppSettings? settings)
    {
        var gains = new float[10];
        string raw = settings?.Get(WaveeSettings.EqualizerGains) ?? WaveeSettings.EqualizerGains.Default;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        for (int i = 0; i < gains.Length && i < parts.Length; i++)
        {
            if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                gains[i] = Math.Clamp(v, -12f, 12f);
        }

        return gains;
    }

    static string SerializeEqGains(float[] gains)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++)
        {
            if (i > 0) sb.Append(',');
            float gain = i < gains.Length ? Math.Clamp(gains[i], -12f, 12f) : 0f;
            sb.Append(gain.ToString("0.#", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    static int EqPresetIndex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        for (int i = 0; i < s_eqPresetIds.Length; i++)
            if (string.Equals(name, s_eqPresetIds[i], StringComparison.OrdinalIgnoreCase))
                return i;

        // Compatibility with earlier builds that stored the English display label.
        for (int i = 0; i < EqPresetLabels().Length; i++)
            if (string.Equals(name, EqPresetLabels()[i], StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    void ApplyEqPreset(Services? svc, IAppSettings? settings, int index)
    {
        if (settings is null) return;
        int idx = Math.Clamp(index, 0, s_eqPresetIds.Length - 1);
        _eqPreset.Value = idx;
        settings.Set(WaveeSettings.EqualizerPreset, s_eqPresetIds[idx]);
        settings.Set(WaveeSettings.EqualizerGains, SerializeEqGains(s_eqPresetGains[idx]));
        PushDsp(svc);
        Bump();
    }

    void SetEqBand(Services? svc, IAppSettings? settings, int band, float gain)
    {
        if (settings is null || band < 0 || band >= 10) return;
        var gains = ReadEqGains(settings);
        gains[band] = Math.Clamp(gain, -12f, 12f);
        settings.Set(WaveeSettings.EqualizerGains, SerializeEqGains(gains));
        PushDsp(svc);
        Bump();
    }

    void UpdateEqBand(Services? svc, IAppSettings? settings, int band, float delta)
    {
        if (settings is null || band < 0 || band >= 10) return;
        var gains = ReadEqGains(settings);
        SetEqBand(svc, settings, band, gains[band] + delta);
    }

    void SetCrossfadeDuration(Services? svc, IAppSettings? settings, int ms)
    {
        if (settings is null) return;
        int next = Math.Clamp(ms, 0, 30_000);
        settings.Set(WaveeSettings.CrossfadeMs, next);
        PushDsp(svc);
        Bump();
    }

    void UpdateCrossfadeDuration(Services? svc, IAppSettings? settings, int deltaMs)
    {
        if (settings is null) return;
        SetCrossfadeDuration(svc, settings, settings.Get(WaveeSettings.CrossfadeMs) + deltaMs);
    }

    static void PushDsp(Services? svc)
    {
        if (svc?.LiveHost?.Connect.Audio?.Host is not Wavee.Backend.IAudioDspControl dsp)
            return;

        var settings = svc.Settings;
        dsp.SetEqualizer(settings.Get(WaveeSettings.EqualizerEnabled), ReadEqGains(settings));
        dsp.SetCrossfade(settings.Get(WaveeSettings.CrossfadeEnabled),
            Math.Clamp(settings.Get(WaveeSettings.CrossfadeMs), 0, 30_000));
    }

    Element RuntimeGroup(Services? svc)
    {
        var status = svc?.Playback.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable;

        void OpenSetup()
        {
            if (svc is { } s) s.Playback.OpenPlaybackRuntimeSetup.Value = s.Playback.OpenPlaybackRuntimeSetup.Peek() + 1;
        }

        var items = new List<Element>();
        if (status.IsReady)
        {
            items.Add(SettingsItem(Loc.Get(Strings.Settings.Common.Ready),
                Loc.Get(Strings.Settings.Playback.RuntimeReadySub),
                Button.Standard(Loc.Get(Strings.Settings.Common.Manage), OpenSetup)));
            items.Add(RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailVersion),
                status.PackId is { Length: > 0 } pack ? status.SpotifyVersion + " (" + pack + ")" : status.SpotifyVersion));
            items.Add(RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailArch), status.Arch?.ToString()));
            items.Add(RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailSignature), SetupBody.SignatureSummary(status)));
            items.Add(RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailLocation), status.RuntimePath));
        }
        else if (status.Outcome == ProvisioningOutcome.NeverAttempted)
        {
            items.Add(SettingsItem(Loc.Get(Strings.Settings.Playback.RuntimeNotSetUp),
                Loc.Get(Strings.Settings.Playback.RuntimeNotSetUpSub),
                Button.Accent(Loc.Get(Strings.Playback.Runtime.SetUp), OpenSetup)));
        }
        else
        {
            string detail = svc?.Playback.RuntimeStatus.Value.Outcome.ToUserMessage()
                ?? Loc.Get(Strings.Settings.Playback.RuntimeUnavailable);
            items.Add(SettingsItem(Loc.Get(Strings.Settings.Common.Problem), detail,
                Button.Accent(Loc.Get(Strings.Settings.Common.RetrySetup), OpenSetup)));
        }

        return SettingsGroup(Loc.Get(Strings.Settings.Playback.RuntimeTitle),
            Loc.Get(Strings.Settings.Playback.RuntimeSub), Icons.Play, items.ToArray());
    }

    Element RuntimeCard(Services? svc)
    {
        var status = svc?.Playback.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable;

        void OpenSetup()
        {
            if (svc is { } s) s.Playback.OpenPlaybackRuntimeSetup.Value = s.Playback.OpenPlaybackRuntimeSetup.Peek() + 1;
        }

        return RuntimeCards(svc, status, OpenSetup);

        /*
        if (status.IsReady)
        {
            kids.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
                Children =
                [
                    InfoBadge.Icon(InfoBadgeSeverity.Success),
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f,
                        Children =
                        [
                            new TextEl("Ready") { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                            new TextEl("Audio keys are derived locally on this PC.")
                                { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                        ],
                    },
                    Button.Standard("Manage…", OpenSetup),
                ],
            });
            kids.Add(InnerPanel(
                KvRow("Version", status.PackId is { Length: > 0 } pack ? $"{status.SpotifyVersion} ({pack})" : status.SpotifyVersion),
                KvRow("Architecture", status.Arch?.ToString()),
                KvRow("Signature", SetupBody.SignatureSummary(status)),
                KvRow("Location", status.RuntimePath, mono: true)));
        }
        else if (status.Outcome == ProvisioningOutcome.NeverAttempted)
        {
            kids.Add(StatusRow(
                Icons.StatusInfo, Tok.TextTertiary,
                "Not set up",
                "Some tracks can't play without the local runtime. Setup downloads and verifies it automatically.",
                Button.Accent("Set up", OpenSetup)));
        }
        else
        {
            string detail = svc?.Playback.RuntimeStatus.Value.Outcome.ToUserMessage() ?? "The playback runtime is unavailable.";
            kids.Add(StatusRow(
                Icons.StatusError, Tok.SystemFillCritical,
                "Problem",
                detail,
                Button.Accent("Retry setup", OpenSetup)));
        }

        return Card(kids.ToArray());
        */
    }

    static Element RuntimeCards(Services? svc, PlaybackRuntimeStatus status, Action openSetup)
    {
        if (status.IsReady)
        {
            return new BoxEl
            {
                Direction = 1,
                Gap = 8f,
                Children =
                [
                    SettingsCard.Create(new SettingsCard.Options
                    {
                        Header = Loc.Get(Strings.Settings.Common.Ready),
                        Description = Loc.Get(Strings.Settings.Playback.RuntimeReadySub),
                        HeaderIcon = Icons.StatusSuccess,
                        Content = Button.Standard(Loc.Get(Strings.Settings.Common.Manage), openSetup),
                    }),
                    RuntimeInfoRow(Loc.Get(Strings.Playback.Runtime.DetailVersion),
                        status.PackId is { Length: > 0 } pack ? status.SpotifyVersion + " (" + pack + ")" : status.SpotifyVersion,
                        Icons.Document),
                    RuntimeInfoRow(Loc.Get(Strings.Playback.Runtime.DetailArch), status.Arch?.ToString(), Icons.Document),
                    RuntimeInfoRow(Loc.Get(Strings.Playback.Runtime.DetailSignature), SetupBody.SignatureSummary(status), Icons.Tag),
                    RuntimeInfoRow(Loc.Get(Strings.Playback.Runtime.DetailLocation), status.RuntimePath, Icons.Folder),
                ],
            };
        }

        if (status.Outcome == ProvisioningOutcome.NeverAttempted)
        {
            return SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(Strings.Settings.Playback.RuntimeNotSetUp),
                Description = Loc.Get(Strings.Settings.Playback.RuntimeNotSetUpSub),
                HeaderIcon = Icons.StatusInfo,
                Content = Button.Accent(Loc.Get(Strings.Playback.Runtime.SetUp), openSetup),
            });
        }

        string detail = svc?.Playback.RuntimeStatus.Value.Outcome.ToUserMessage()
            ?? Loc.Get(Strings.Settings.Playback.RuntimeUnavailable);
        return SettingsCard.Create(new SettingsCard.Options
        {
            Header = Loc.Get(Strings.Settings.Common.Problem),
            Description = detail,
            HeaderIcon = Icons.StatusError,
            Content = Button.Accent(Loc.Get(Strings.Settings.Common.RetrySetup), openSetup),
        });
    }

    static Element RuntimeInfoRow(string label, string? value, string icon) => SettingsCard.Create(new SettingsCard.Options
    {
        Header = label,
        Description = string.IsNullOrWhiteSpace(value) ? null : value,
        HeaderIcon = icon,
        IsEnabled = !string.IsNullOrWhiteSpace(value),
    });

    static Element RuntimeInfoItem(string label, string? value) => SettingsItem(label,
        string.IsNullOrWhiteSpace(value) ? null : value,
        isEnabled: !string.IsNullOrWhiteSpace(value));

    static Element StatusRow(string glyph, ColorF glyphColor, string heading, string body, Element action) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
        Children =
        [
            new TextEl(glyph) { Size = 18f, FontFamily = Theme.IconFont, Color = glyphColor, Shrink = 0f },
            new BoxEl
            {
                Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f,
                Children =
                [
                    new TextEl(heading) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                    new TextEl(body) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
            action,
        ],
    };

    Element QualityOption(Services? svc, int value, string title, string caption)
    {
        var settings = svc?.Settings;
        int selected = Math.Clamp(settings?.Get(WaveeSettings.PlaybackQuality) ?? 2, 0, 2);
        bool on = selected == value;
        return SettingsItem(title, caption,
            on
                ? new TextEl(Icons.Accept) { Size = 14f, FontFamily = Theme.IconFont, Color = Tok.AccentDefault, Shrink = 0f }
                : new BoxEl { Width = 14f },
            isClickEnabled: settings is not null,
            onClick: () => { settings?.Set(WaveeSettings.PlaybackQuality, value); Bump(); });
    }

    static Element QualityComingSoon(string title, string caption) => SettingsItem(title, caption,
        new BoxEl
        {
            Padding = new Edges4(8f, 2f, 8f, 3f), Corners = CornerRadius4.All(WaveeRadius.Pill),
            Fill = Tok.AccentSubtle,
            Children = [new TextEl(Loc.Get(Strings.Settings.Playback.ComingSoon)) { Size = 11f, Weight = 600, Color = Tok.AccentDefault }],
        },
        isEnabled: false);

    // ── Storage & cache ───────────────────────────────────────────────────────────────────────────────────────────────

    void RefreshStorage(Action<Action> post)
    {
        if (_storageBusy) return;
        _storageBusy = true;
        _ = Task.Run(() =>
        {
            var snap = ComputeStorage();
            post(() =>
            {
                _storage = snap;
                _storageBusy = false;
                _storageVersion.Value = _storageVersion.Peek() + 1;
            });
        });
    }

    static StorageSnapshot ComputeStorage()
    {
        string root = AppDataRoot;
        long library = 0, runtime = 0, logs = 0, store = 0;
        int logFiles = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "library.db*")) library += FileSize(f);
        }
        catch { }
        runtime = DirSize(Path.Combine(root, "playplay"));
        try
        {
            string logDir = Path.Combine(root, "logs");
            if (Directory.Exists(logDir))
                foreach (var f in Directory.EnumerateFiles(logDir)) { logs += FileSize(f); logFiles++; }
        }
        catch { }
        store += FileSize(Path.Combine(root, "store.json"));
        store += DirSize(Path.Combine(root, "WaveeMusic"));
        long audioBody = DirSize(AudioBodyDiskCache.DefaultDirectory());
        long licenseDb = FileSize(LicenseKeyDiskCache.DefaultDbPath());
        return new StorageSnapshot(library, runtime, logs, logFiles, store, audioBody, licenseDb,
            library + runtime + logs + store + audioBody + licenseDb);
    }

    static long FileSize(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : 0; }
        catch { return 0; }
    }

    static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) total += FileSize(f);
        }
        catch { }
        return total;
    }

    static string FmtBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1024L * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024L => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    static Element StorageRow(string label, string sub, long? size, string folder, Element? extra = null)
    {
        var kids = new List<Element>
        {
            new BoxEl
            {
                Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 200f,
                Children =
                [
                    new TextEl(label) { Size = 14f, Color = Tok.TextPrimary },
                    new TextEl(sub) { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Trim = TextTrim.CharacterEllipsis, MaxLines = 1 },
                ],
            },
            new TextEl(size is { } b ? FmtBytes(b) : "…") { Size = 13f, Color = Tok.TextSecondary, Shrink = 0f },
            HyperlinkButton.Create("Open folder", () => OpenFolder(folder)),
        };
        if (extra is not null) kids.Add(extra);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Gap = WaveeSpace.M, Wrap = true,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
            Children = kids.ToArray(),
        };
    }

    static Element StorageCard(string label, string sub, long? size, string folder, Element? extra = null)
    {
        var actions = new List<Element>
        {
            new TextEl(size is { } b ? FmtBytes(b) : "...") { Size = 13f, Color = Tok.TextSecondary, Shrink = 0f },
            HyperlinkButton.Create(Loc.Get(Strings.Settings.Storage.OpenFolder), () => OpenFolder(folder)),
        };
        if (extra is not null) actions.Add(extra);
        return SettingsItem(label, sub,
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Wrap = true,
                Children = actions.ToArray(),
            });
    }

    static int BodyBudgetIndex(long bytes)
    {
        int best = 3;
        long bestDiff = long.MaxValue;
        for (int i = 0; i < s_bodyBudgetBytes.Length; i++)
        {
            long diff = Math.Abs(s_bodyBudgetBytes[i] - bytes);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return best;
    }

    void DeleteOldLogs(Action<Action> post)
    {
        _ = Task.Run(() =>
        {
            int deleted = 0;
            try
            {
                string logDir = Path.Combine(AppDataRoot, "logs");
                if (Directory.Exists(logDir))
                    foreach (var f in Directory.EnumerateFiles(logDir, "wavee-*.log"))   // rolled files only — never the live wavee.log
                        { try { File.Delete(f); deleted++; } catch { } }
            }
            catch { }
            post(() =>
            {
                Toasts.Show(deleted > 0
                    ? Loc.Format("settings.storage.oldLogsDeleted", ("count", deleted))
                    : Loc.Get(Strings.Settings.Storage.NoOldLogsDeleted),
                    ToastSeverity.Success);
                _storage = null;
                RefreshStorage(post);
            });
        });
    }

    static string ResidentCacheDescription(Wavee.Backend.Persistence.CachedStore? cold)
    {
        if (cold is null) return Loc.Get(Strings.Settings.Storage.ResidentCacheSub);
        return Loc.Format("settings.storage.residentCacheStats",
            ("used", FmtBytes(cold.ResidentMembershipBytes)),
            ("cap", FmtBytes(cold.MaxResidentBytes)),
            ("count", cold.ResidentMembershipCount),
            ("max", cold.MaxResidentPlaylists));
    }

    Element StorageTab(Services? svc, Action<Action> post)
    {
        _ = _storageVersion.Value;   // subscribe → re-render when the off-thread snapshot lands
        var s = _storage;
        string root = AppDataRoot;

        var cold = svc?.RealStore as Wavee.Backend.Persistence.CachedStore;
        string residentCacheDescription = ResidentCacheDescription(cold);

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.L,
            Children =
            [
                SettingsGroup(Loc.Get(Strings.Settings.Storage.OnThisPc), s is { } snap ? root + " - " + FmtBytes(snap.Total) + " " + Loc.Get(Strings.Settings.Storage.Total) : root, Icons.Folder,
                    StorageCard(Loc.Get(Strings.Settings.Storage.Library), Loc.Get(Strings.Settings.Storage.LibrarySub),
                        s?.LibraryDb, root),
                    StorageCard(Loc.Get(Strings.Settings.Storage.Runtime), Loc.Get(Strings.Settings.Storage.RuntimeSub),
                        s?.Runtime, Path.Combine(root, "playplay")),
                    StorageCard(Loc.Get(Strings.Settings.Storage.Logs), s is { } sn ? sn.LogFiles == 1 ? Loc.Get(Strings.Settings.Storage.LogsSubOne) : Loc.Format("settings.storage.logsSub", ("count", sn.LogFiles)) : Loc.Get(Strings.Settings.Storage.LogsSubEmpty),
                        s?.Logs, Path.Combine(root, "logs"),
                        Button.Standard(Loc.Get(Strings.Settings.Storage.DeleteOldLogs), () => DeleteOldLogs(post))),
                    StorageCard(Loc.Get(Strings.Settings.Storage.LocalStore), Loc.Get(Strings.Settings.Storage.LocalStoreSub),
                        s?.Store, root)),
                PlaybackCacheCard(s, svc, post, settings: svc?.Settings),
                SettingsGroup(Loc.Get(Strings.Settings.Storage.Memory), Loc.Get(Strings.Settings.Storage.MemorySub), Icons.List,
                    SettingsItem(Loc.Get(Strings.Settings.Storage.ResidentCache),
                        residentCacheDescription, /*
                            ? $"Playlists kept warm for instant navigation — {FmtBytes(cold.ResidentMembershipBytes)} of {FmtBytes(cold.MaxResidentBytes)} cap ({cold.ResidentMembershipCount} of {cold.MaxResidentPlaylists} playlists)"
                            : Loc.Get(Strings.Settings.Storage.ResidentCacheSub), */
                        Button.Standard(Loc.Get(Strings.Settings.Storage.ReleaseNow), () =>
                        {
                            svc?.LibraryStore.ShedDetails(keep: 16);
                            Toasts.Show(Loc.Get(Strings.Settings.Storage.DetailsReleased), ToastSeverity.Success);
                            Bump();
                        }))),
            ],
        };
    }

    Element PlaybackCacheCard(StorageSnapshot? s, Services? svc, Action<Action> post, IAppSettings? settings)
    {
        string audioDir = AudioBodyDiskCache.DefaultDirectory();
        string licenseDb = LicenseKeyDiskCache.DefaultDbPath();
        var keyStats = svc?.AudioLicenseCache?.Stats();
        int budgetIdx = BodyBudgetIndex(settings?.Get(WaveeSettings.AudioBodyCacheBudgetBytes) ?? (4L << 30));

        return PlaybackCacheSection(s, svc, post, settings, audioDir, licenseDb, keyStats, budgetIdx);
    }

    /*
        return Card(
            CardHeader("Playback cache", "Encrypted CDN bodies and saved license keys — survives restarts"),
            RowDivider(),
            SettingRow("Cache encrypted audio", "Skip re-downloading track bodies you already streamed",
                ToggleSwitch.Create(settings?.Get(WaveeSettings.AudioBodyCacheEnabled) ?? true, () =>
                {
                    if (settings is null) return;
                    settings.Set(WaveeSettings.AudioBodyCacheEnabled, !settings.Get(WaveeSettings.AudioBodyCacheEnabled));
                    Bump();
                })),
            RowDivider(),
            SettingRow("Cache license keys", "Reuse obfuscated keys across sessions (self-heals if stale)",
                ToggleSwitch.Create(settings?.Get(WaveeSettings.AudioKeyCacheEnabled) ?? true, () =>
                {
                    if (settings is null) return;
                    settings.Set(WaveeSettings.AudioKeyCacheEnabled, !settings.Get(WaveeSettings.AudioKeyCacheEnabled));
                    Bump();
                })),
            RowDivider(),
            SettingRow("Audio body budget", "LRU-evicted when full — applies on next launch",
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
                    Children =
                    [
                        Button.Standard("Smaller", () => ShiftBodyBudget(svc, settings, -1), isEnabled: settings is not null && budgetIdx > 0),
                        new TextEl(s_bodyBudgetLabels[budgetIdx]) { Size = 12f, Color = Tok.TextSecondary, Width = 56f },
                        Button.Standard("Larger", () => ShiftBodyBudget(svc, settings, 1), isEnabled: settings is not null && budgetIdx < s_bodyBudgetLabels.Length - 1),
                    ],
                }),
            RowDivider(),
            StorageRow("Encrypted audio bodies", "Wavee\\Cache\\audio — sparse 64 KB CDN chunks",
                s?.AudioBody, audioDir,
                Button.Standard("Clear audio cache", () => ClearAudioBodyCache(svc, post))),
            RowDivider(),
            StorageRow("Saved license keys",
                keyStats is { } ks ? $"Wavee\\Cache\\audiokeys.db — {ks.Count} key{(ks.Count == 1 ? "" : "s")}" : "Wavee\\Cache\\audiokeys.db",
                s?.LicenseDb, Path.GetDirectoryName(licenseDb) ?? audioDir,
                Button.Standard("Clear saved keys", () => ClearLicenseKeys(svc, post))));
    }

    */

    Element PlaybackCacheSection(StorageSnapshot? s, Services? svc, Action<Action> post, IAppSettings? settings,
                                 string audioDir, string licenseDb, (int Count, long Bytes)? keyStats, int budgetIdx)
    {
        string licenseSub = keyStats is { } ks
            ? (ks.Count == 1
                ? Loc.Get(Strings.Settings.Storage.LicenseKeysCountOne)
                : Loc.Format("settings.storage.licenseKeysCount", ("count", ks.Count)))
            : Loc.Get(Strings.Settings.Storage.LicenseKeysSub);

        return SettingsGroup(Loc.Get(Strings.Settings.Storage.PlaybackCache), Loc.Get(Strings.Settings.Storage.PlaybackCacheSub), Icons.Download,
            SettingsItem(Loc.Get(Strings.Settings.Storage.CacheAudio), Loc.Get(Strings.Settings.Storage.CacheAudioSub),
                ToggleSwitch.Create(settings?.Get(WaveeSettings.AudioBodyCacheEnabled) ?? true, () =>
                {
                    if (settings is null) return;
                    settings.Set(WaveeSettings.AudioBodyCacheEnabled, !settings.Get(WaveeSettings.AudioBodyCacheEnabled));
                    Bump();
                }, style: SettingsCard.CompactToggleStyle())),
            SettingsItem(Loc.Get(Strings.Settings.Storage.CacheKeys), Loc.Get(Strings.Settings.Storage.CacheKeysSub),
                ToggleSwitch.Create(settings?.Get(WaveeSettings.AudioKeyCacheEnabled) ?? true, () =>
                {
                    if (settings is null) return;
                    settings.Set(WaveeSettings.AudioKeyCacheEnabled, !settings.Get(WaveeSettings.AudioKeyCacheEnabled));
                    Bump();
                }, style: SettingsCard.CompactToggleStyle())),
            SettingsItem(Loc.Get(Strings.Settings.Storage.BodyBudget), Loc.Get(Strings.Settings.Storage.BodyBudgetSub),
                new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    Gap = WaveeSpace.M,
                    Wrap = true,
                    Children =
                    [
                        new TextEl(Loc.Format("settings.storage.bodyBudgetValue", ("value", s_bodyBudgetLabels[budgetIdx])))
                            { Size = 12f, Color = Tok.TextSecondary, Width = 104f },
                        Slider.Ranged(budgetIdx, v => SetBodyBudgetIndex(svc, settings, (int)MathF.Round(v)),
                            new Slider.Options
                            {
                                Min = 0f,
                                Max = s_bodyBudgetLabels.Length - 1,
                                Step = 1f,
                                TickFrequency = 1f,
                                IsThumbToolTipEnabled = true,
                                ThumbToolTipValueConverter = v => s_bodyBudgetLabels[Math.Clamp((int)MathF.Round(v), 0, s_bodyBudgetLabels.Length - 1)],
                            },
                            length: 230f,
                            isEnabled: settings is not null),
                    ],
                }),
            StorageCard(Loc.Get(Strings.Settings.Storage.AudioBodies), Loc.Get(Strings.Settings.Storage.AudioBodiesSub),
                s?.AudioBody, audioDir,
                Button.Standard(Loc.Get(Strings.Settings.Storage.ClearAudio), () => ClearAudioBodyCache(svc, post))),
            StorageCard(Loc.Get(Strings.Settings.Storage.LicenseKeys), licenseSub,
                s?.LicenseDb, Path.GetDirectoryName(licenseDb) ?? audioDir,
                Button.Standard(Loc.Get(Strings.Settings.Storage.ClearKeys), () => ClearLicenseKeys(svc, post))));
    }

    void ShiftBodyBudget(Services? svc, IAppSettings? settings, int delta)
    {
        if (settings is null) return;
        int idx = BodyBudgetIndex(settings.Get(WaveeSettings.AudioBodyCacheBudgetBytes));
        idx = Math.Clamp(idx + delta, 0, s_bodyBudgetBytes.Length - 1);
        long bytes = s_bodyBudgetBytes[idx];
        settings.Set(WaveeSettings.AudioBodyCacheBudgetBytes, bytes);
        svc?.AudioBodyCache?.SetBudget(bytes);
        Bump();
    }

    void SetBodyBudgetIndex(Services? svc, IAppSettings? settings, int index)
    {
        if (settings is null) return;
        int idx = Math.Clamp(index, 0, s_bodyBudgetBytes.Length - 1);
        long bytes = s_bodyBudgetBytes[idx];
        settings.Set(WaveeSettings.AudioBodyCacheBudgetBytes, bytes);
        svc?.AudioBodyCache?.SetBudget(bytes);
        Bump();
    }

    void ClearAudioBodyCache(Services? svc, Action<Action> post)
    {
        _ = Task.Run(() =>
        {
            try { svc?.AudioBodyCache?.ClearAll(); }
            catch { try { if (Directory.Exists(AudioBodyDiskCache.DefaultDirectory())) foreach (var f in Directory.EnumerateFiles(AudioBodyDiskCache.DefaultDirectory())) File.Delete(f); } catch { } }
            post(() =>
            {
                Toasts.Show(Loc.Get(Strings.Settings.Storage.AudioCacheCleared), ToastSeverity.Success);
                _storage = null;
                RefreshStorage(post);
            });
        });
    }

    void ClearLicenseKeys(Services? svc, Action<Action> post)
    {
        _ = Task.Run(() =>
        {
            try { svc?.AudioLicenseCache?.ClearAll(); }
            catch { }
            post(() =>
            {
                Toasts.Show(Loc.Get(Strings.Settings.Storage.LicenseKeysCleared), ToastSeverity.Success);
                _storage = null;
                RefreshStorage(post);
            });
        });
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────────────────────────────────────────────

    readonly record struct LogRowData(WaveeLogEntry Entry, int Repeat);

    Element DiagnosticsTab(InputHooks hooks)
    {
        _ = _refresh.Value;
        _ = _diagVersion.Value;
        int session = _session.Value;
        bool live = session == 0;
        string search = _search.Value;
        int level = _level.Value;
        int category = _category.Value;
        bool newestFirst = _newestFirst.Value != 0;
        bool group = _groupRepeats.Value != 0;

        WaveeLogEntry[]? entries = live ? WaveeLog.Instance.Snapshot()
            : _sessionLoaded == session ? _sessionEntries : null;
        var visible = entries is null ? new List<LogRowData>() : Filter(entries, search, level, category, newestFirst, group);

        Element listBody = entries is null
            ? new BoxEl
            {
                Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = WaveeSpace.M,
                Children =
                [
                    ProgressRing.Indeterminate(),
                    new TextEl("Reading session from the log file…") { Size = 12f, Color = Tok.TextSecondary },
                ],
            }
            : ScrollView(LogList(visible)) with
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f,
                ScrollKey = "settings:logs:" + session,
                Key = "settings:logs:scroll:" + session,
            };

        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, Gap = WaveeSpace.L,
            Children =
            [
                SessionCard(entries, visible.Count, live),
                // The log card: pinned toolbar (command bar + filters) over the ONLY scrolling region — the rows.
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
                    Corners = CornerRadius4.All(WaveeRadius.Card),
                    Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    ClipToBounds = true,
                    Children =
                    [
                        Toolbar(hooks, visible, live),
                        new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                        listBody,
                    ],
                },
            ],
        };
    }

    Element SessionCard(WaveeLogEntry[]? entries, int visible, bool live)
    {
        int warnings = 0, errors = 0;
        int total = entries?.Length ?? 0;
        if (entries is not null)
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Level == WaveeLogLevel.Warning) warnings++;
                else if (entries[i].Level >= WaveeLogLevel.Error) errors++;
            }

        string title, sub;
        Element badge;
        if (live)
        {
            DateTimeOffset started;
            try { started = System.Diagnostics.Process.GetCurrentProcess().StartTime; }
            catch { started = DateTimeOffset.Now; }
            var up = DateTimeOffset.Now - started;
            string uptime = up.TotalHours >= 1
                ? $"{(int)up.TotalHours} h {up.Minutes} m"
                : up.TotalMinutes >= 1 ? $"{(int)up.TotalMinutes} min" : "just now";
            title = "This session";
            sub = $"Started {started.ToLocalTime():HH:mm} · running {uptime} · pid {Environment.ProcessId}";
            badge = InfoBadge.Icon(InfoBadgeSeverity.Success);
        }
        else
        {
            var info = _sessions is { } ss && session_InRange(ss) ? ss[_session.Peek() - 1] : null;
            title = "Past session";
            string when = info is null ? ""
                : info.StartUnixMs > 0
                    ? "Recorded " + DateTimeOffset.FromUnixTimeMilliseconds(info.StartUnixMs).ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)
                    : $"pid {info.Pid}";
            sub = (when.Length > 0 ? when + " · " : "") + "read-only from the log file (rolled at 10 MB, 7 kept)";
            badge = Icon(Icons.Clock, 16f, Tok.TextSecondary);
        }

        var items = new List<string>(1 + (_sessions?.Count ?? 0)) { "This session" };
        if (_sessions is { } list2) foreach (var s in list2) items.Add(SessionLabel(s));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Wrap = true,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                badge,
                new BoxEl
                {
                    Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 220f,
                    Children =
                    [
                        new TextEl(title) { Size = 14f, Weight = 700, Color = Tok.TextPrimary },
                        new TextEl(sub) { Size = 12f, Color = Tok.TextSecondary },
                    ],
                },
                StatPill(total.ToString(CultureInfo.InvariantCulture), "events"),
                StatPill(warnings.ToString(CultureInfo.InvariantCulture), "warnings"),
                StatPill(errors.ToString(CultureInfo.InvariantCulture), "errors"),
                StatPill(visible.ToString(CultureInfo.InvariantCulture), "shown"),
                ComboBox.Create(items, _session, width: 220f),
            ],
        };
    }

    bool session_InRange(List<WaveeLogSessions.Info> ss) => _session.Peek() - 1 >= 0 && _session.Peek() - 1 < ss.Count;

    // The WinUI CommandBar toolbar: search + category left, icon commands (labels revealed by the … More button) right.
    Element Toolbar(InputHooks hooks, List<LogRowData> visible, bool live)
    {
        var copy = visible.ToArray();
        var commands = new AppBarCommand[]
        {
            new(Icons.Copy, "Copy", () => hooks.Clipboard?.SetText(BuildCopyText(copy))),
            new("", "Clear view", () => { WaveeLog.Instance.ClearRing(); _refresh.Value = _refresh.Peek() + 1; }, Enabled: live),
            new(Icons.Folder, "Log folder", () => OpenFolder(Path.GetDirectoryName(WaveeLog.Instance.FilePath ?? "") ?? AppDataRoot)),
            AppBarCommand.Separator,
            new(Icons.Sort, "Newest first", () => _newestFirst.Value = _newestFirst.Peek() == 0 ? 1 : 0,
                Kind: AppBarCommandKind.ToggleButton, IsChecked: _newestFirst.Peek() != 0),
            new(Icons.List, "Group repeats", () => _groupRepeats.Value = _groupRepeats.Peek() == 0 ? 1 : 0,
                Kind: AppBarCommandKind.ToggleButton, IsChecked: _groupRepeats.Peek() != 0),
        };

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.S,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.S, WaveeSpace.M, WaveeSpace.S),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Wrap = true,
                    Children =
                    [
                        AutoSuggestBox.Create(
                            Array.Empty<string>(),
                            placeholder: "Filter logs",
                            grow: 1f,
                            text: _search,
                            onQuerySubmitted: q => _search.Value = q,
                            onTextChanged: q => _search.Value = q,
                            minHeight: 34f,
                            cornerRadius: WaveeRadius.Control),
                        ComboBox.Create(s_categories, _category, width: 150f),
                        CommandBar.Create(commands, Array.Empty<AppBarCommand>()),
                    ],
                },
                SelectorBar.Create(s_levelLabels, _level.Value, i => _level.Value = i),
            ],
        };
    }

    static Element LogList(List<LogRowData> visible)
    {
        if (visible.Count == 0)
            return new BoxEl
            {
                Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Gap = WaveeSpace.M, Padding = new Edges4(0f, 64f, 0f, 64f),
                Children =
                [
                    Icon(Icons.Search, 36f, Tok.TextTertiary),
                    WaveeType.PageHero("No matching logs"),
                ],
            };

        var rows = new Element[visible.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = LogRow(visible[i], i < rows.Length - 1);
        return new BoxEl { Direction = 1, Children = rows };
    }

    static string FmtTime(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    static Element LogRow(LogRowData row, bool divider)
    {
        var e = row.Entry;
        string fieldText = FieldText(e.Fields);
        var meta = new List<Element>
        {
            // Fixed-width time column so rows align; entries re-read from pre-timestamp log files show "—".
            new TextEl(e.UnixMs > 0 ? FmtTime(e.UnixMs) : "—")
                { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = 88f, Shrink = 0f },
            LevelPill(e.Level),
            new TextEl(e.Category) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" },
            new TextEl("#" + e.Sequence.ToString(CultureInfo.InvariantCulture)) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
        };
        if (e.EventId.Length > 0)
            meta.Add(new TextEl(e.EventId) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" });
        if (e.OperationId is { Length: > 0 } op)
            meta.Add(new TextEl("op=" + op) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" });
        if (row.Repeat > 1)
            meta.Add(new BoxEl
            {
                Padding = new Edges4(7f, 1f, 7f, 2f), Corners = CornerRadius4.All(WaveeRadius.Pill),
                Fill = Tok.FillSubtleSecondary,
                Children = [new TextEl("×" + row.Repeat.ToString(CultureInfo.InvariantCulture)) { Size = 10.5f, Weight = 700, Color = Tok.TextSecondary }],
            });

        var kids = new List<Element>
        {
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S, Wrap = true, Children = meta.ToArray() },
            new TextEl(e.Message) { Size = 13f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
        };
        if (fieldText.Length > 0)
            kids.Add(new TextEl(fieldText) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap });
        if (e.Exception is { Length: > 0 } ex)
            kids.Add(new TextEl(ex) { Size = 12f, Color = Tok.SystemFillCritical, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap });

        Element rowEl = new BoxEl
        {
            Direction = 1, Gap = 5f,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.S, WaveeSpace.L, WaveeSpace.S),
            Children = kids.ToArray(),
        };
        if (!divider) return rowEl;
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                rowEl,
                new BoxEl { Height = 1f, Margin = new Edges4(WaveeSpace.L, 0f, WaveeSpace.L, 0f), Fill = Tok.StrokeDividerDefault },
            ],
        };
    }

    static BoxEl LevelPill(WaveeLogLevel level)
    {
        var color = level switch
        {
            WaveeLogLevel.Critical or WaveeLogLevel.Error => Tok.SystemFillCritical,
            WaveeLogLevel.Warning => Tok.SystemFillCaution,
            WaveeLogLevel.Debug or WaveeLogLevel.Trace => Tok.TextTertiary,
            _ => Tok.AccentDefault,
        };
        return new BoxEl
        {
            Width = 58f, Height = 22f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(WaveeRadius.Pill),
            Fill = color with { A = 0.12f }, BorderWidth = 1f, BorderColor = color with { A = 0.38f },
            Children = [new TextEl(level.ToString().ToUpperInvariant()) { Size = 10f, Weight = 800, Color = color }],
        };
    }

    static List<LogRowData> Filter(WaveeLogEntry[] entries, string search, int level, int category, bool newestFirst, bool group)
    {
        var result = new List<LogRowData>(Math.Min(entries.Length, MaxVisibleRows));
        int start = newestFirst ? entries.Length - 1 : 0;
        int end = newestFirst ? -1 : entries.Length;
        int step = newestFirst ? -1 : 1;
        string cat = (uint)category < (uint)s_categories.Length ? s_categories[category] : "All";

        for (int i = start; i != end && result.Count < MaxVisibleRows; i += step)
        {
            var e = entries[i];
            if (!PassesLevel(e.Level, level)) continue;
            if (cat != "All" && !string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase)) continue;
            if (search.Length > 0 && !PassesSearch(e, search)) continue;

            // Collapse consecutive repeats (same level/category/event/message) into one row with a ×N pill — the
            // representative entry is the first encountered in walk order (the NEWEST when newest-first).
            if (group && result.Count > 0)
            {
                var last = result[^1].Entry;
                if (last.Level == e.Level && last.Category == e.Category && last.EventId == e.EventId && last.Message == e.Message)
                {
                    result[^1] = result[^1] with { Repeat = result[^1].Repeat + 1 };
                    continue;
                }
            }
            result.Add(new LogRowData(e, 1));
        }
        return result;
    }

    static bool PassesLevel(WaveeLogLevel level, int filter) => filter switch
    {
        1 => level >= WaveeLogLevel.Info,
        2 => level >= WaveeLogLevel.Warning,
        3 => level >= WaveeLogLevel.Error,
        _ => true,
    };

    static bool PassesSearch(WaveeLogEntry e, string q)
    {
        if (e.Category.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.EventId.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.Message.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.OperationId?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (e.Fields is { } fields)
            for (int i = 0; i < fields.Length; i++)
                if (fields[i].Name.Contains(q, StringComparison.OrdinalIgnoreCase) || fields[i].Value.Contains(q, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    static string FieldText(WaveeLogField[]? fields)
    {
        if (fields is not { Length: > 0 }) return "";
        var sb = new StringBuilder(fields.Length * 16);
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            fields[i].AppendTo(sb);
        }
        return sb.ToString();
    }

    static string BuildCopyText(IReadOnlyList<LogRowData> rows)
    {
        var sb = new StringBuilder(rows.Count * 96);
        for (int i = 0; i < rows.Count; i++)
        {
            var e = rows[i].Entry;
            sb.Append("seq=").Append(e.Sequence.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(e.Format());
            if (rows[i].Repeat > 1)
                sb.Append(" (repeated ").Append(rows[i].Repeat.ToString(CultureInfo.InvariantCulture)).Append("×)");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── About ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    static string AppVersion
    {
        get
        {
            string? v = typeof(SettingsPage).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(v)) return "dev";
            int plus = v.IndexOf('+');   // strip the SourceLink commit-hash suffix
            return plus > 0 ? v[..plus] : v;
        }
    }

    Element AboutTab(Services? svc, InputHooks hooks)
    {
        string version = AppVersion;
        string os = RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")";
        string dotnet = ".NET " + Environment.Version;
        var runtime = svc?.Playback.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable;

        string DiagInfo() =>
            $"Wavee {version}\n" +
            $"OS: {os}\n" +
            $"Engine: FluentGpu · {dotnet}\n" +
            $"Data folder: {AppDataRoot}\n" +
            $"Playback runtime: {runtime.Outcome}" +
            (runtime.IsReady ? $" — {runtime.SpotifyVersion} ({runtime.Arch})" : "");

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.L,
            Children =
            [
                Card(
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.L,
                        Padding = Edges4.All(WaveeSpace.L),
                        Children =
                        [
                            new BoxEl
                            {
                                Width = 56f, Height = 56f, Corners = CornerRadius4.All(12f),
                                Fill = Tok.AccentDefault, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Children = [new TextEl("W") { Size = 26f, Weight = 700, Color = Tok.TextOnAccentPrimary }],
                            },
                            new BoxEl
                            {
                                Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f,
                                Children =
                                [
                                    new TextEl("Wavee") { Size = 18f, Weight = 700, Color = Tok.TextPrimary },
                                    new TextEl($"{version} · FluentGpu engine · {dotnet}") { Size = 12f, Color = Tok.TextSecondary },
                                ],
                            },
                            Button.Standard("Copy info", () =>
                            {
                                hooks.Clipboard?.SetText(DiagInfo());
                                Toasts.Show("Diagnostics info copied", ToastSeverity.Success);
                            }),
                        ],
                    },
                    RowDivider(),
                    new BoxEl
                    {
                        Direction = 1, Gap = 6f,
                        Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.L),
                        Children =
                        [
                            KvRow("OS", os),
                            KvRow("Data folder", AppDataRoot, mono: true),
                        ],
                    }),
                LicensesCard(),
            ],
        };
    }

    // (name, license kind, notice) — Wavee's own MIT text in full; third-party components as short notices.
    static readonly (string Name, string Kind, string Body)[] s_licenses =
    [
        ("Wavee", "MIT",
            "Copyright (c) 2026 Christos Karapasias\n\n" +
            "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated " +
            "documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation " +
            "the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and " +
            "to permit persons to whom the Software is furnished to do so, subject to the following conditions:\n\n" +
            "The above copyright notice and this permission notice shall be included in all copies or substantial portions of " +
            "the Software.\n\n" +
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO " +
            "THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE " +
            "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF " +
            "CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER " +
            "DEALINGS IN THE SOFTWARE."),
        ("Google.Protobuf 3.35.0", "BSD-3-Clause",
            "Protocol Buffers runtime for C#. Copyright © Google LLC. Used for the Spotify wire protocol."),
        ("Microsoft.Data.Sqlite 10.0.8 · SQLitePCLRaw 3.0.0", "MIT",
            "SQLite data provider used for the library database. Copyright © .NET Foundation and contributors. " +
            "SQLite itself is public domain."),
        ("NVorbis (vendored)", "MIT",
            "Pure-managed Ogg Vorbis decoder. Copyright © Andrew Ward and contributors."),
        ("ZstdSharp.Port 0.8.6 · FlacBox 1.0.0 · ProtectedData 9.0", "MIT / BSD",
            "Zstandard decompression (© Oleg Stepanischev), FLAC decoding, and Windows DPAPI credential protection " +
            "(© .NET Foundation)."),
    ];

    Element LicensesCard()
    {
        int open = _licOpen.Value;
        var kids = new List<Element>
        {
            CardHeader("Licenses", "Wavee is MIT-licensed. Third-party components below."),
        };
        for (int i = 0; i < s_licenses.Length; i++)
        {
            int idx = i;
            var (name, kind, body) = s_licenses[i];
            bool isOpen = open == idx;
            kids.Add(RowDivider());
            kids.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Focusable = true,
                OnClick = () => _licOpen.Value = isOpen ? -1 : idx,
                Children =
                [
                    new TextEl(isOpen ? Icons.ChevronDown : Icons.ChevronRightMed)
                        { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, Shrink = 0f },
                    new TextEl(name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f },
                    new TextEl(kind) { Size = 12f, Color = Tok.TextSecondary },
                ],
            });
            if (isOpen)
                kids.Add(new BoxEl
                {
                    Padding = new Edges4(42f, 0f, WaveeSpace.L, WaveeSpace.M),
                    Children = [new TextEl(body) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap }],
                });
        }
        return Card(kids.ToArray());
    }
}
