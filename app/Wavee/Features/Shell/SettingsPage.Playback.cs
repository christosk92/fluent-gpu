using System;
using System.Globalization;
using System.Text;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

sealed partial class SettingsPage
{
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

    readonly Signal<int> _quality = new(2);
    readonly Signal<int> _eqPreset = new(0);
    readonly Signal<double> _crossSecs = new(5.0);

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

    Element PlaybackTab(Services? svc)
    {
        var settings = svc?.Settings;
        bool eqOn = settings?.Get(WaveeSettings.EqualizerEnabled) ?? false;
        bool crossOn = settings?.Get(WaveeSettings.CrossfadeEnabled) ?? false;
        float[] gains = ReadEqGains(settings);
        int preset = Math.Clamp(_eqPreset.Value, 0, s_eqPresetIds.Length - 1);

        Element Toggle(SettingKey<bool> key, bool pushDsp = false, bool bumpPlayerBar = false, bool bumpPlayback = false) =>
            ToggleSwitch.Create(settings?.Get(key) ?? true, () =>
            {
                if (settings is null) return;
                settings.Set(key, !settings.Get(key));
                if (pushDsp) PushDsp(svc);
                if (bumpPlayerBar) PlayerBarPrefs.Bump();
                if (bumpPlayback) PlaybackPrefs.Bump();
                Bump();
            }, style: SettingsCard.CompactToggleStyle());

        return SettingsTabStack(
            RuntimeCard(svc),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Playback.AudioTitle), Icons.MusicNote),
            SettingsRow(Loc.Get(Strings.Settings.Playback.AudioQuality), Loc.Get(Strings.Settings.Playback.AudioSub),
                QualityCombo(svc), Icons.MusicNote),
            SettingsRow(Loc.Get(Strings.Settings.Playback.RememberVolume), Loc.Get(Strings.Settings.Playback.RememberVolumeSub),
                Toggle(WaveeSettings.RememberVolume), Icons.Volume),
            SettingsRow(Loc.Get(Strings.Settings.Playback.Autoplay), Loc.Get(Strings.Settings.Playback.AutoplaySub),
                Toggle(WaveeSettings.AutoplayEnabled, bumpPlayback: true), Icons.Play),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Sound.Title), Icons.Tag),
            EqualizerGroup(svc, settings, eqOn, gains, preset),
            CrossfadeGroup(svc, settings, crossOn),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Playback.PlayerBar), Mdl.Pin),
            SettingsRow(Loc.Get(Strings.Settings.Playback.ShowRemaining), Loc.Get(Strings.Settings.Playback.ShowRemainingSub),
                Toggle(WaveeSettings.PlayerBarShowRemaining, bumpPlayerBar: true), Icons.Clock));
    }

    Element RuntimeCard(Services? svc)
    {
        var status = svc?.Playback.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable;

        void OpenSetup()
        {
            if (svc is { } s) s.Playback.OpenPlaybackRuntimeSetup.Value = s.Playback.OpenPlaybackRuntimeSetup.Peek() + 1;
        }

        if (status.IsReady)
        {
            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Settings.Common.Ready),
                Description = Loc.Get(Strings.Settings.Playback.RuntimeReadySub),
                HeaderIcon = Icons.StatusSuccess,
                Content = Button.Standard(Loc.Get(Strings.Settings.Common.Manage), OpenSetup),
                InitiallyExpanded = false,
                Items =
                [
                    RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailVersion),
                        status.PackId is { Length: > 0 } pack ? status.SpotifyVersion + " (" + pack + ")" : status.SpotifyVersion),
                    RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailArch), status.Arch?.ToString()),
                    RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailSignature), SetupBody.SignatureSummary(status)),
                    RuntimeInfoItem(Loc.Get(Strings.Playback.Runtime.DetailLocation), status.RuntimePath),
                ],
            });
        }

        if (status.Outcome == ProvisioningOutcome.NeverAttempted)
        {
            return InfoBar.Create(InfoBarSeverity.Informational,
                Loc.Get(Strings.Settings.Playback.RuntimeNotSetUp),
                Loc.Get(Strings.Settings.Playback.RuntimeNotSetUpSub),
                isClosable: false,
                actionButton: Button.Accent(Loc.Get(Strings.Playback.Runtime.SetUp), OpenSetup));
        }

        string detail = status.Outcome.ToUserMessage() ?? Loc.Get(Strings.Settings.Playback.RuntimeUnavailable);
        return InfoBar.Create(InfoBarSeverity.Error,
            Loc.Get(Strings.Settings.Common.Problem), detail,
            isClosable: false,
            actionButton: Button.Accent(Loc.Get(Strings.Settings.Common.RetrySetup), OpenSetup));
    }

    Element EqualizerGroup(Services? svc, IAppSettings? settings, bool eqOn, float[] gains, int preset)
    {
        var toggle = ToggleSwitch.Create(eqOn, () =>
        {
            if (settings is null) return;
            settings.Set(WaveeSettings.EqualizerEnabled, !settings.Get(WaveeSettings.EqualizerEnabled));
            PushDsp(svc);
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
                SettingsExpander.Item(Loc.Get(Strings.Settings.Sound.Preset), EqPresetDescriptions()[preset],
                    ComboBox.Create(EqPresetLabels(), _eqPreset, width: 200f, itemDescriptions: EqPresetDescriptions(),
                        isEnabled: eqOn && settings is not null,
                        onSelectionChanged: i => ApplyEqPreset(svc, settings, i))),
                SettingsExpander.Item(Loc.Get(Strings.Settings.Sound.Curve),
                    eqOn ? Loc.Get(Strings.Settings.Sound.CurveOn) : Loc.Get(Strings.Settings.Sound.CurveOff),
                    new BoxEl
                    {
                        Direction = 1, Gap = WaveeSpace.S,
                        Children =
                        [
                            WaveeEqualizerCurve.Create(gains, (band, gain) => SetEqBand(svc, settings, band, gain), eqOn && settings is not null),
                            new BoxEl
                            {
                                Direction = 0, Justify = FlexJustify.End,
                                Children =
                                [
                                    HyperlinkButton.Create(Loc.Get(Strings.Settings.Sound.ResetCurve),
                                        () => ResetEqCurve(svc, settings), isEnabled: eqOn && settings is not null),
                                ],
                            },
                        ],
                    },
                    alignment: SettingsCard.ContentAlignment.Vertical),
            ],
        });
    }

    Element CrossfadeGroup(Services? svc, IAppSettings? settings, bool crossOn)
    {
        var toggle = ToggleSwitch.Create(crossOn, () =>
        {
            if (settings is null) return;
            settings.Set(WaveeSettings.CrossfadeEnabled, !settings.Get(WaveeSettings.CrossfadeEnabled));
            PushDsp(svc);
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

        void Commit(double seconds)
        {
            if (settings is null) return;
            int ms = (int)MathF.Round((float)Math.Clamp(seconds, 0, 12) * 1000f);
            settings.Set(WaveeSettings.CrossfadeMs, ms);
            _crossSecs.Value = ms / 1000.0;
            PushDsp(svc);
            Bump();
        }

        var durationRow = new BoxEl
        {
            // NumberBox is a mounted component whose constructor fields intentionally freeze. Remount this small row when
            // the toggle flips so its enabled state follows crossfade immediately instead of staying at its first value.
            Key = crossOn ? "crossfade-duration-on" : "crossfade-duration-off",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Children =
            [
                Slider.Ranged((float)_crossSecs.Value, v => Commit(v),
                    new Slider.Options
                    {
                        Min = 0f, Max = 12f, Step = 0.5f, TickFrequency = 2f, IsThumbToolTipEnabled = true,
                        ThumbToolTipValueConverter = v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
                    },
                    length: 220f, isEnabled: crossOn && settings is not null),
                NumberBox.Create(value: _crossSecs, minimum: 0, maximum: 12, smallChange: 0.5,
                    spinButtonPlacementMode: NumberBoxSpinButtonPlacementMode.Compact, width: 96f,
                    formatter: v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
                    onValueChanged: (_, v) => Commit(v), isEnabled: crossOn && settings is not null),
            ],
        };

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.Sound.Crossfade),
            Description = Loc.Get(Strings.Settings.Sound.CrossfadeSub),
            HeaderIcon = Icons.Shuffle,
            Content = toggle,
            InitiallyExpanded = crossOn,
            Items =
            [
                SettingsExpander.Item(Loc.Get(Strings.Settings.Sound.CrossfadeDuration),
                    Strings.Settings.Sound.Seconds(((float)_crossSecs.Value).ToString("0.#", CultureInfo.InvariantCulture)),
                    durationRow),
            ],
        });
    }

    static Element RuntimeInfoItem(string label, string? value) => SettingsItem(label,
        string.IsNullOrWhiteSpace(value) ? null : value,
        isEnabled: !string.IsNullOrWhiteSpace(value));

    Element QualityCombo(Services? svc)
    {
        var settings = svc?.Settings;
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
        return ComboBox.Create(labels, _quality, width: 280f,
            itemDescriptions: descriptions, itemEnabled: enabled,
            isEnabled: settings is not null,
            onSelectionChanged: i =>
            {
                if (settings is null || i < 0 || i > 2) return;
                settings.Set(WaveeSettings.PlaybackQuality, i);
                Bump();
            });
    }

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

    void ResetEqCurve(Services? svc, IAppSettings? settings)
    {
        if (settings is null) return;
        settings.Set(WaveeSettings.EqualizerGains, SerializeEqGains(s_eqPresetGains[0]));
        _eqPreset.Value = 0;
        settings.Set(WaveeSettings.EqualizerPreset, s_eqPresetIds[0]);
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

    static void PushDsp(Services? svc)
    {
        if (svc?.LiveHost?.Connect.Audio?.Host is not Wavee.Backend.IAudioDspControl dsp)
            return;
        var settings = svc.Settings;
        dsp.SetEqualizer(settings.Get(WaveeSettings.EqualizerEnabled), ReadEqGains(settings));
        dsp.SetCrossfade(settings.Get(WaveeSettings.CrossfadeEnabled),
            Math.Clamp(settings.Get(WaveeSettings.CrossfadeMs), 0, 12_000));
    }
}
