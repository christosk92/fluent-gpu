using System;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 6 · Sound &amp; storage (<c>data-step="6"</c>). Sound features use progressive disclosure:
/// enabling Crossfade or Equalizer opens that feature's editor directly beneath its own header. Storage retains the
/// same live settings writers as the shipped Settings page.</summary>
sealed class SetupSoundPage : Component
{
    static readonly string[] s_metaBudgetLabels = ["32 MB", "64 MB", "128 MB", "256 MB"];
    static readonly long[] s_metaBudgetBytes = [32L << 20, 64L << 20, 128L << 20, 256L << 20];
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

    readonly Signal<int> _epoch = new(0);
    void Bump() => _epoch.Value = _epoch.Peek() + 1;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var settings = svc?.Settings;
        var post = UsePost();
        _ = _epoch.Value;

        int quality = Math.Clamp(settings?.Get(WaveeSettings.PlaybackQuality) ?? 2, 0, 2);
        int meteredCap = Math.Clamp(settings?.Get(WaveeSettings.MeteredQualityCap) ?? 1, 0, 2);
        bool crossfadeOn = settings?.Get(WaveeSettings.CrossfadeEnabled) ?? false;
        bool eqOn = settings?.Get(WaveeSettings.EqualizerEnabled) ?? false;
        double crossSecs = Math.Clamp((settings?.Get(WaveeSettings.CrossfadeMs) ?? 5000) / 1000.0, 0, 12);
        int eqPreset = EqPresetIndex(settings?.Get(WaveeSettings.EqualizerPreset));
        float[] eqGains = PlaybackDsp.ReadEqGains(settings);
        int budgetMode = Math.Clamp(settings?.Get(WaveeSettings.AudioBodyCacheBudgetMode) ?? (int)AudioCacheBudgetMode.DriveShare, 0, 2);
        string audioDir = svc?.AudioBodyCache?.CurrentDirectory
            ?? AudioBodyDiskCache.ResolveDirectory(settings?.Get(WaveeSettings.AudioBodyCacheBasePath));
        int metaBudgetIndex = MetaBudgetIndex(settings?.Get(WaveeSettings.MetadataCacheBudgetBytes) ?? s_metaBudgetBytes[1]);

        Element body = SetupRows.Stack(
            SetupRows.Lead(Loc.Get(Strings.Setup.Sound.Lead)),

            SetupRows.SectionHeader(Loc.Get(Strings.Settings.Sound.Title), Icons.MusicNote),
            SetupRows.Row(Loc.Get(Strings.Settings.Playback.AudioQuality), Loc.Get(Strings.Settings.Playback.AudioSub),
                QualityCombo(settings, quality), Icons.MusicNote),
            SetupRows.Row(Loc.Get(Strings.Settings.Playback.MeteredQuality), Loc.Get(Strings.Settings.Playback.MeteredQualitySub),
                MeteredCombo(settings, meteredCap), Icons.Globe),
            CrossfadeGroup(settings, svc, crossfadeOn, crossSecs),
            EqualizerGroup(settings, svc, eqOn, eqPreset, eqGains),

            SetupRows.SectionHeader(Loc.Get(Strings.Settings.Tabs.Storage), Icons.Folder),
            BudgetRow(settings, svc, budgetMode),
            SetupRows.Row(Loc.Get(Strings.Settings.Storage.CacheLocation), audioDir,
                Button.Standard(Loc.Get(Strings.Settings.Storage.ChooseLocation),
                    () => SetupWrites.ChooseCacheLocation(svc, settings!, post)), Icons.Folder,
                isEnabled: settings is not null && svc?.AudioBodyCache is not null),
            SetupRows.Row(Loc.Get(Strings.Settings.Storage.MetadataBudget), Loc.Get(Strings.Settings.Storage.MetadataBudgetSub),
                MetaBudgetCombo(settings, svc, metaBudgetIndex), Icons.Document));

        return SetupPageHost.Frame(SetupPage.Sound, Loc.Get(Strings.Setup.Eyebrow.Sound),
            Loc.Get(Strings.Settings.Sound.Title), body);
    }

    Element QualityCombo(IAppSettings? settings, int quality)
    {
        string[] labels =
        [
            Loc.Get(Strings.Settings.Playback.QualityNormal),
            Loc.Get(Strings.Settings.Playback.QualityHigh),
            Loc.Get(Strings.Settings.Playback.QualityVeryHigh),
            Loc.Get(Strings.Settings.Playback.QualityLossless),
        ];
        string[] descriptions =
        [
            Loc.Get(Strings.Settings.Playback.QualityNormalSub),
            Loc.Get(Strings.Settings.Playback.QualityHighSub),
            Loc.Get(Strings.Settings.Playback.QualityVeryHighSub),
            Loc.Get(Strings.Settings.Playback.QualityLosslessSub),
        ];
        bool[] enabled = [true, true, true, false];
        return ComboBox.Create(labels, new Signal<int>(quality), width: 280f,
            itemDescriptions: descriptions, itemEnabled: enabled, isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null) return;
                SetupWrites.SetPlaybackQuality(i, settings);
                Bump();
            });
    }

    Element MeteredCombo(IAppSettings? settings, int meteredCap)
    {
        string[] labels =
        [
            Loc.Get(Strings.Settings.Playback.QualityNormal),
            Loc.Get(Strings.Settings.Playback.QualityHigh),
            Loc.Get(Strings.Settings.Playback.QualityVeryHigh),
        ];
        return ComboBox.Create(labels, new Signal<int>(meteredCap), width: 280f, isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null) return;
                SetupWrites.SetMeteredQualityCap(i, settings);
                Bump();
            });
    }

    /// <summary>The switch owns the disclosure. A state-key remounts the small group so an off→on transition opens
    /// the duration editor without asking for a second click.</summary>
    Element CrossfadeGroup(IAppSettings? settings, Services? svc, bool crossfadeOn, double crossSecs)
    {
        var toggle = ToggleSwitch.Create(new Signal<bool>(crossfadeOn), onChange: _ =>
        {
            if (settings is null) return;
            SetupWrites.SetCrossfadeEnabled(!crossfadeOn, settings, svc);
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

        Element duration = ComboBox.Create(["2 s", "5 s", "8 s", "12 s"], new Signal<int>(SecondsIndex(crossSecs)),
            width: 120f, isEnabled: crossfadeOn && settings is not null, onChange: i =>
            {
                if (settings is null) return;
                double[] options = [2, 5, 8, 12];
                SetupWrites.SetCrossfadeSeconds(options[Math.Clamp(i, 0, options.Length - 1)], settings, svc);
                Bump();
            });

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Sound.Crossfade),
            Description = Loc.Get(Strings.Settings.Sound.CrossfadeSub),
            HeaderIcon = Icons.Shuffle,
            Content = toggle,
            InitiallyExpanded = crossfadeOn,
            Items =
            [
                SetupRows.Item(Loc.Get(Strings.Settings.Sound.CrossfadeDuration),
                    Strings.Settings.Sound.Seconds(crossSecs.ToString("0.#", CultureInfo.InvariantCulture)),
                    duration, icon: Icons.Clock),
            ],
        }) with { Key = crossfadeOn ? "setup:sound:crossfade:on" : "setup:sound:crossfade:off" };
    }

    /// <summary>The header owns the only Equalizer label and toggle. Enabling it opens the actual preset and curve
    /// editor in place; disabling it closes those controls as one predictable action.</summary>
    Element EqualizerGroup(IAppSettings? settings, Services? svc, bool eqOn, int preset, float[] gains)
    {
        var toggle = ToggleSwitch.Create(new Signal<bool>(eqOn), onChange: _ =>
        {
            if (settings is null) return;
            SetupWrites.SetEqualizerEnabled(!eqOn, settings, svc);
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Sound.Equalizer),
            Description = Loc.Get(Strings.Settings.Sound.EqualizerSub),
            HeaderIcon = Icons.Tag,
            Content = toggle,
            InitiallyExpanded = eqOn,
            Items =
            [
                SetupRows.Item(Loc.Get(Strings.Settings.Sound.Preset), EqPresetDescriptions()[preset],
                    ComboBox.Create(EqPresetLabels(), new Signal<int>(preset), width: 220f,
                        itemDescriptions: EqPresetDescriptions(), isEnabled: eqOn && settings is not null,
                        onChange: i =>
                        {
                            if (settings is null) return;
                            int next = Math.Clamp(i, 0, s_eqPresetIds.Length - 1);
                            SetupWrites.SetEqualizerPreset(s_eqPresetIds[next], s_eqPresetGains[next], settings, svc);
                            Bump();
                        })),
                SetupRows.Item(Loc.Get(Strings.Settings.Sound.Curve),
                    Loc.Get(eqOn ? Strings.Settings.Sound.CurveOn : Strings.Settings.Sound.CurveOff),
                    new BoxEl
                    {
                        Direction = 1,
                        Gap = Spacing.S,
                        Children =
                        [
                            WaveeEqualizerCurve.Create(gains, (band, gain) =>
                            {
                                if (settings is null) return;
                                SetupWrites.SetEqualizerBand(band, gain, settings, svc);
                                Bump();
                            }, eqOn && settings is not null),
                            new BoxEl
                            {
                                Direction = 0,
                                Justify = FlexJustify.End,
                                Children =
                                [
                                    HyperlinkButton.Create(Loc.Get(Strings.Settings.Sound.ResetCurve), () =>
                                    {
                                        if (settings is null) return;
                                        SetupWrites.SetEqualizerPreset(s_eqPresetIds[0], s_eqPresetGains[0], settings, svc);
                                        Bump();
                                    }, isEnabled: eqOn && settings is not null),
                                ],
                            },
                        ],
                    },
                    align: SettingsCard.ContentAlignment.Vertical),
            ],
        }) with { Key = eqOn ? "setup:sound:eq:on" : "setup:sound:eq:off" };
    }

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

    static int EqPresetIndex(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        for (int i = 0; i < s_eqPresetIds.Length; i++)
            if (string.Equals(id, s_eqPresetIds[i], StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    static int SecondsIndex(double seconds)
    {
        double[] options = [2, 5, 8, 12];
        int best = 1;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < options.Length; i++)
        {
            double diff = Math.Abs(options[i] - seconds);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return best;
    }

    Element BudgetRow(IAppSettings? settings, Services? svc, int budgetMode)
    {
        string caption = budgetMode switch
        {
            (int)AudioCacheBudgetMode.FixedBytes => Loc.Get(Strings.Settings.Storage.BodyBudgetSub),
            (int)AudioCacheBudgetMode.Unlimited => Loc.Get(Strings.Settings.Storage.UnlimitedReserve),
            _ => Loc.Get(Strings.Settings.Storage.AutoTenPercent) + " — " + Loc.Get(Strings.Settings.Storage.BodyBudgetSub),
        };

        return SetupRows.Row(Loc.Get(Strings.Settings.Storage.BodyBudget), caption,
            SelectorBar.Create(
            [
                Loc.Get(Strings.Settings.Storage.FixedSize),
                Loc.Get(Strings.Settings.Storage.DriveShare),
                Loc.Get(Strings.Settings.Storage.Unlimited),
            ], new Signal<int>(budgetMode), onChange: mode =>
            {
                if (settings is null) return;
                SetupWrites.SetAudioBodyCacheBudgetMode(mode, settings, svc);
                Bump();
            }), Icons.Download);
    }

    Element MetaBudgetCombo(IAppSettings? settings, Services? svc, int index)
        => ComboBox.Create(s_metaBudgetLabels, new Signal<int>(index), width: 120f, isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null || i < 0 || i >= s_metaBudgetBytes.Length) return;
                SetupWrites.SetMetadataCacheBudgetBytes(s_metaBudgetBytes[i], settings, svc);
                Bump();
            });

    static int MetaBudgetIndex(long bytes)
    {
        int idx = Array.IndexOf(s_metaBudgetBytes, bytes);
        return idx >= 0 ? idx : 1;
    }
}
