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
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── Settings — a tabbed page: General · Playback · Storage & cache · Diagnostics · About ─────────────────────────────
// General/Playback surface the persisted WaveeSettings (theme/palette/density, runtime status, quality, volume);
// Diagnostics is the SESSION log viewer (the in-memory ring is per-launch; the wavee.log FILE keeps history across runs);
// Storage computes on-disk sizes per visit; About carries version, environment and licenses.
sealed class SettingsPage : Component
{
    const int TabGeneral = 0, TabPlayback = 1, TabStorage = 2, TabDiagnostics = 3, TabAbout = 4;
    static readonly string[] s_tabs = ["General", "Playback", "Storage & cache", "Diagnostics", "About"];
    static readonly string[] s_tabKeys = ["general", "playback", "storage", "diagnostics", "about"];
    static readonly string[] s_themeLabels = ["System", "Light", "Dark"];
    static readonly string[] s_densityLabels = ["Compact", "Default", "Cozy", "Comfortable"];
    static readonly string[] s_eqBandLabels = ["31 Hz", "63 Hz", "125 Hz", "250 Hz", "500 Hz", "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz"];

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

    // Storage state (snapshot computed off-thread per visit, published via post)
    sealed record StorageSnapshot(long LibraryDb, long Runtime, long Logs, int LogFiles, long Store, long Total);
    readonly Signal<int> _storageVersion = new(0);
    StorageSnapshot? _storage;
    bool _storageBusy;

    // About state
    readonly Signal<int> _licOpen = new(-1);

    void Bump() => _uiEpoch.Value = _uiEpoch.Peek() + 1;

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
                        SelectorBar.Create(s_tabs, tab, i => _tab.Value = i),
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
            WaveeType.PageHero("Settings") with { Grow = 1f },
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
            Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f,
            Children = caption is { Length: > 0 }
                ? [new TextEl(title) { Size = 14f, Weight = 700, Color = Tok.TextPrimary },
                   new TextEl(caption) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap }]
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

        return Card(
            CardHeader("Appearance", "Theme, color palette and list density"),
            RowDivider(),
            SettingRow("Theme", "System follows the Windows setting live",
                SelectorBar.Create(s_themeLabels, themeMode, SetTheme)),
            RowDivider(),
            SettingRow("Palette", "Tints the shell surfaces and accent",
                PaletteRow(settings, requestTheme)),
            RowDivider(),
            SettingRow("Row density", "Height of rows in track lists",
                ComboBox.Create(s_densityLabels, _density, width: 170f,
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
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
            Children =
            [
                Swatch("warm", "Warm", WaveeColors.PresetSwatch(Tok.WarmPalette)),
                Swatch("slate", "Slate", WaveeColors.PresetSwatch(Tok.SlatePalette)),
                Swatch("neutral", "Neutral", WaveeColors.PresetSwatch(Tok.NeutralPalette)),
                Swatch("accent", "Accent", WaveeColors.PresetSwatch(Tok.AccentTintedPalette)),
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
                RuntimeCard(svc),
                Card(
                    CardHeader("Audio", "Streaming quality for local playback — applies from the next track"),
                    RowDivider(),
                    QualityOption(svc, 0, "Normal", "96 kbps · about 0.04 GB/hour"),
                    QualityOption(svc, 1, "High", "160 kbps · about 0.07 GB/hour"),
                    QualityOption(svc, 2, "Very High", "320 kbps · about 0.14 GB/hour"),
                    QualityComingSoon("Lossless", "Up to 24-bit/44.1 kHz · FLAC"),
                    RowDivider(),
                    SettingRow("Remember volume", "Restore the last volume level when Wavee starts",
                        ToggleSwitch.Create(settings?.Get(WaveeSettings.RememberVolume) ?? true, () =>
                        {
                            if (settings is null) return;
                            settings.Set(WaveeSettings.RememberVolume, !settings.Get(WaveeSettings.RememberVolume));
                            Bump();
                        }))),
                DspCard(svc),
                Card(
                    CardHeader("Player bar", null),
                    RowDivider(),
                    SettingRow("Show remaining time", "Count down time left instead of showing the track duration (clicking the time in the bar toggles it too)",
                        ToggleSwitch.Create(settings?.Get(WaveeSettings.PlayerBarShowRemaining) ?? true, () =>
                        {
                            if (settings is null) return;
                            settings.Set(WaveeSettings.PlayerBarShowRemaining, !settings.Get(WaveeSettings.PlayerBarShowRemaining));
                            PlayerBarPrefs.Bump();
                            Bump();
                        }))),
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

        var eqRows = new Element[10];
        for (int i = 0; i < eqRows.Length; i++)
            eqRows[i] = EqBandRow(svc, settings, gains, i);

        return Card(
            CardHeader("Sound", "Equalizer and transition controls for local playback"),
            RowDivider(),
            SettingRow("Equalizer", "10-band graphic EQ, +/-12 dB",
                ToggleSwitch.Create(eqOn, () =>
                {
                    if (settings is null) return;
                    settings.Set(WaveeSettings.EqualizerEnabled, !settings.Get(WaveeSettings.EqualizerEnabled));
                    PushDsp(svc);
                    Bump();
                })),
            InnerPanel(eqRows),
            RowDivider(),
            SettingRow("Crossfade", "Natural transition at the end of a prepared track",
                ToggleSwitch.Create(crossOn, () =>
                {
                    if (settings is null) return;
                    settings.Set(WaveeSettings.CrossfadeEnabled, !settings.Get(WaveeSettings.CrossfadeEnabled));
                    PushDsp(svc);
                    Bump();
                })),
            RowDivider(),
            SettingRow("Crossfade duration", (crossMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " seconds",
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
                    Children =
                    [
                        Button.Standard("-1s", () => UpdateCrossfadeDuration(svc, settings, -1000), isEnabled: settings is not null),
                        new TextEl(crossMs.ToString(CultureInfo.InvariantCulture) + " ms") { Size = 12f, Color = Tok.TextSecondary, Width = 68f },
                        Button.Standard("+1s", () => UpdateCrossfadeDuration(svc, settings, 1000), isEnabled: settings is not null),
                    ],
                }));
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

    void UpdateEqBand(Services? svc, IAppSettings? settings, int band, float delta)
    {
        if (settings is null || band < 0 || band >= 10) return;
        var gains = ReadEqGains(settings);
        gains[band] = Math.Clamp(gains[band] + delta, -12f, 12f);
        settings.Set(WaveeSettings.EqualizerGains, SerializeEqGains(gains));
        PushDsp(svc);
        Bump();
    }

    void UpdateCrossfadeDuration(Services? svc, IAppSettings? settings, int deltaMs)
    {
        if (settings is null) return;
        int next = Math.Clamp(settings.Get(WaveeSettings.CrossfadeMs) + deltaMs, 0, 30_000);
        settings.Set(WaveeSettings.CrossfadeMs, next);
        PushDsp(svc);
        Bump();
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

    Element RuntimeCard(Services? svc)
    {
        var status = svc?.Playback.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable;

        void OpenSetup()
        {
            if (svc is { } s) s.Playback.OpenPlaybackRuntimeSetup.Value = s.Playback.OpenPlaybackRuntimeSetup.Peek() + 1;
        }

        var kids = new List<Element>
        {
            CardHeader("Playback runtime", "Local audio decode and key-derivation runtime"),
            RowDivider(),
        };

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
    }

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
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.S, WaveeSpace.L, WaveeSpace.S),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true,
            OnClick = () => { settings?.Set(WaveeSettings.PlaybackQuality, value); Bump(); },
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = 1f, Grow = 1f, Basis = 0f,
                    Children =
                    [
                        new TextEl(title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                        new TextEl(caption) { Size = 12f, Color = Tok.TextSecondary },
                    ],
                },
                on
                    ? new TextEl(Icons.Accept) { Size = 14f, FontFamily = Theme.IconFont, Color = Tok.AccentDefault, Shrink = 0f }
                    : new BoxEl { Width = 14f },
            ],
        };
    }

    static Element QualityComingSoon(string title, string caption) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.S, WaveeSpace.L, WaveeSpace.S),
        Children =
        [
            new BoxEl
            {
                Direction = 1, Gap = 1f, Grow = 1f, Basis = 0f,
                Children =
                [
                    new TextEl(title) { Size = 14f, Weight = 600, Color = Tok.TextDisabled },
                    new TextEl(caption) { Size = 12f, Color = Tok.TextDisabled },
                ],
            },
            new BoxEl
            {
                Padding = new Edges4(8f, 2f, 8f, 3f), Corners = CornerRadius4.All(WaveeRadius.Pill),
                Fill = Tok.AccentSubtle,
                Children = [new TextEl("Coming soon") { Size = 11f, Weight = 600, Color = Tok.AccentDefault }],
            },
        ],
    };

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
        return new StorageSnapshot(library, runtime, logs, logFiles, store, library + runtime + logs + store);
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
                Toasts.Show(deleted > 0 ? $"Deleted {deleted} old log file{(deleted == 1 ? "" : "s")}" : "No old log files to delete",
                    ToastSeverity.Success);
                _storage = null;
                RefreshStorage(post);
            });
        });
    }

    Element StorageTab(Services? svc, Action<Action> post)
    {
        _ = _storageVersion.Value;   // subscribe → re-render when the off-thread snapshot lands
        var s = _storage;
        string root = AppDataRoot;

        Element StorageRow(string label, string sub, long? size, string folder, Element? extra = null)
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
                Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Wrap = true,
                Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
                Children = kids.ToArray(),
            };
        }

        var cold = svc?.RealStore as Wavee.Backend.Persistence.CachedStore;

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.L,
            Children =
            [
                Card(
                    CardHeader("On this PC", root,
                        s is { } snap ? StatPill(FmtBytes(snap.Total), "total") : StatPill("…", "total")),
                    RowDivider(),
                    StorageRow("Library database", "library.db — albums, artists, playlists, sync state",
                        s?.LibraryDb, root),
                    RowDivider(),
                    StorageRow("Playback runtime", "playplay\\runtimes — managed from the Playback tab",
                        s?.Runtime, Path.Combine(root, "playplay")),
                    RowDivider(),
                    StorageRow("Logs", s is { } sn ? $"logs\\wavee.log — {sn.LogFiles} file{(sn.LogFiles == 1 ? "" : "s")}, rolled at 10 MB, 7 kept" : "logs\\wavee.log",
                        s?.Logs, Path.Combine(root, "logs"),
                        Button.Standard("Delete old logs", () => DeleteOldLogs(post))),
                    RowDivider(),
                    StorageRow("Local store & history", "store.json, navigation history",
                        s?.Store, root)),
                Card(
                    CardHeader("In memory", "Working-set caches, trimmed automatically under memory pressure"),
                    RowDivider(),
                    SettingRow("Resident library cache",
                        cold is not null
                            ? $"Playlists kept warm for instant navigation — {FmtBytes(cold.ResidentMembershipBytes)} of {FmtBytes(cold.MaxResidentBytes)} cap ({cold.ResidentMembershipCount} of {cold.MaxResidentPlaylists} playlists)"
                            : "Playlists and detail pages kept warm for instant navigation",
                        Button.Standard("Release now", () =>
                        {
                            svc?.LibraryStore.ShedDetails(keep: 16);
                            Toasts.Show("Released cached detail pages", ToastSeverity.Success);
                            Bump();
                        }))),
            ],
        };
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
