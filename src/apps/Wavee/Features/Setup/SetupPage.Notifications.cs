using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 7 · Notifications &amp; links (<c>data-step="7"</c>). The master Windows-notifications gate, the
/// three headline topic dials, a "More topics &amp; quiet hours" expander for the rest, then links + language.
///
/// <para><b>Follows the SHIPPED dial, not the prototype.</b> <c>SettingsPage.Notifications.cs</c>'s <c>TopicRow</c>
/// deliberately renders only TWO segments (Off/In Wavee) for a topic whose <c>NotificationPolicy.CeilingFor</c> is
/// not <c>Windows</c> — "an unreachable switch teaches the user the wrong thing about the product." The prototype's
/// mockup shows a disabled third segment for <c>LibraryActivity</c> instead; the shipped behaviour wins here too.</para>
///
/// <para>Quiet hours renders as the prototype's own single 3-option preset combo (Off / 23:00–08:00 / 00:00–07:00)
/// rather than the Settings tab's toggle + two independent hour combos — a genuine simplification for a first-run
/// screen, not a data-model fiction: all three presets are exact, real <c>(Enabled, FromHour, ToHour)</c> triples.</para></summary>
sealed class SetupNotificationsPage : Component
{
    static readonly (bool Enabled, int From, int To)[] s_quietPresets =
    [
        (false, 22, 8),
        (true, 23, 8),
        (true, 0, 7),
    ];

    readonly Signal<int> _epoch = new(0);
    void Bump() => _epoch.Value = _epoch.Peek() + 1;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var settings = svc?.Settings;
        _ = _epoch.Value;
        _ = NotificationPrefs.Epoch.Value;   // re-read after any dial write made elsewhere (the bell, a live escalation)

        bool windowsOn = settings?.Get(WaveeSettings.NotifyWindows) ?? false;
        bool linksOn = settings?.Get(WaveeSettings.HandleSpotifyLinks) ?? false;
        int language = LanguageIndex(settings?.Get(WaveeSettings.UiCulture) ?? "system");

        var children = new System.Collections.Generic.List<Element>
        {
            SetupRows.Lead(Loc.Get(Strings.Settings.Notify.TopicsHint)),
            SetupRows.Row(Loc.Get(Strings.Settings.Notify.Windows), Loc.Get(Strings.Settings.Notify.WindowsSub),
                ToggleSwitch.Create(new Signal<bool>(windowsOn), onChange: _ =>
                {
                    if (settings is null) return;
                    SetupWrites.SetNotifyWindows(!windowsOn, settings);
                    Bump();
                }, style: SettingsCard.CompactToggleStyle()), Icons.Bell),
        };

        if (!windowsOn)
            children.Add(InfoBar.Create(InfoBarSeverity.Informational, "", Loc.Get(Strings.Settings.Notify.WindowsOffHint),
                isClosable: false));

        children.Add(SetupRows.SectionHeader(Loc.Get(Strings.Settings.Notify.TopicsTitle), Icons.Settings));
        children.Add(TopicRow(settings, NotifyTopic.NewAlbums));
        children.Add(TopicRow(settings, NotifyTopic.ReleaseDrops));
        children.Add(TopicRow(settings, NotifyTopic.Concerts));
        children.Add(MoreTopicsGroup(settings));

        children.Add(SetupRows.SectionHeader(Loc.Get(Strings.Setup.Notifications.LinksLanguageGroup)));
        children.Add(SetupRows.Row(Loc.Get(Strings.Settings.Links.Spotify), Loc.Get(Strings.Settings.Links.SpotifySub),
            ToggleSwitch.Create(new Signal<bool>(linksOn), onChange: _ =>
            {
                if (settings is null) return;
                SetupWrites.SetHandleSpotifyLinks(!linksOn, settings);
                Bump();
            }, style: SettingsCard.CompactToggleStyle()), Icons.Link));
        children.Add(SetupRows.Row(Loc.Get(Strings.Settings.Language.Label), Loc.Get(Strings.Settings.Language.RestartSub),
            LanguageCombo(settings, language), Icons.Globe));

        Element body = SetupRows.Stack(children.ToArray());
        return SetupPageHost.Frame(SetupPage.Notifications, Loc.Get(Strings.Setup.Eyebrow.Notifications),
            Loc.Get(Strings.Settings.Notify.Title), body);
    }

    // ── the per-topic dial ───────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The three headline topics render as a <see cref="SettingsExpander"/> (per spec) rather than the
    /// shipped Settings tab's <c>SettingsCard</c> row — the wizard skips the "Send test event" affordance the
    /// shipped expander reveals behind its chevron (a diagnostics feature, out of scope for a first-run screen), so
    /// each one opens onto an intentionally empty items list.</summary>
    Element TopicRow(IAppSettings? settings, NotifyTopic topic)
    {
        var level = NotificationPrefs.Level(settings, topic);
        bool canWindows = NotificationPolicy.CeilingFor(topic) == NotifyLevel.Windows;
        string[] labels = canWindows
            ? [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp), Loc.Get(Strings.Settings.Notify.LevelWindows)]
            : [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp)];

        var dial = SelectorBar.Create(labels, new Signal<int>((int)level), onChange: i =>
        {
            if (settings is null) return;
            SetupWrites.SetTopicLevel(topic, (NotifyLevel)i, settings);
            Bump();
        });

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Label(topic),
            Description = Sub(topic),
            HeaderIcon = Glyph(topic),
            Content = dial,
        }) with { Key = "setup:notifications:topic:" + (int)topic };
    }

    Element MoreTopicsGroup(IAppSettings? settings)
    {
        int fromHour = settings?.Get(WaveeSettings.NotifyQuietFromHour) ?? 22;
        int toHour = settings?.Get(WaveeSettings.NotifyQuietToHour) ?? 8;
        bool quietOn = settings?.Get(WaveeSettings.NotifyQuietEnabled) ?? false;
        int preset = QuietPresetIndex(quietOn, fromHour, toHour);

        var quietRow = SetupRows.Item(Loc.Get(Strings.Settings.Notify.Quiet), Loc.Get(Strings.Settings.Notify.QuietSub),
            ComboBox.Create(
            [
                Loc.Get(Strings.Settings.Choice.Off),
                "23:00 – 08:00",
                "00:00 – 07:00",
            ], new Signal<int>(preset), width: 150f, isEnabled: settings is not null, onChange: i =>
            {
                if (settings is null || (uint)i >= (uint)s_quietPresets.Length) return;
                var p = s_quietPresets[i];
                SetupWrites.SetQuietHours(p.Enabled, p.From, p.To, settings);
                Bump();
            }), icon: Icons.Moon);

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Setup.Notifications.MoreTopics),
            Content = SetupRows.ValueTag(Strings.Setup.Notifications.MoreCount(5)),
            Items =
            [
                quietRow,
                TopicItem(settings, NotifyTopic.NewEpisodes),
                TopicItem(settings, NotifyTopic.Followers),
                TopicItem(settings, NotifyTopic.DaylistRefresh),
                TopicItem(settings, NotifyTopic.AppUpdates),
                TopicItem(settings, NotifyTopic.LibraryActivity),
            ],
        }) with { Key = "setup:notifications:more" };
    }

    Element TopicItem(IAppSettings? settings, NotifyTopic topic)
    {
        var level = NotificationPrefs.Level(settings, topic);
        bool canWindows = NotificationPolicy.CeilingFor(topic) == NotifyLevel.Windows;
        string[] labels = canWindows
            ? [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp), Loc.Get(Strings.Settings.Notify.LevelWindows)]
            : [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp)];

        var dial = SelectorBar.Create(labels, new Signal<int>((int)level), onChange: i =>
        {
            if (settings is null) return;
            SetupWrites.SetTopicLevel(topic, (NotifyLevel)i, settings);
            Bump();
        });

        return SetupRows.Item(Label(topic), Sub(topic), dial, icon: Glyph(topic));
    }

    static int QuietPresetIndex(bool enabled, int from, int to)
    {
        for (int i = 0; i < s_quietPresets.Length; i++)
            if (s_quietPresets[i].Enabled == enabled && (!enabled || (s_quietPresets[i].From == from && s_quietPresets[i].To == to)))
                return i;
        return 0;
    }

    Element LanguageCombo(IAppSettings? settings, int index)
    {
        string[] codes = ["system", "en-US", "nl", "ko-KR"];
        string[] labels =
        [
            Loc.Get(Strings.Settings.Language.System),
            Loc.Get(Strings.Settings.Language.EnglishUs),
            Loc.Get(Strings.Settings.Language.Dutch),
            Loc.Get(Strings.Settings.Language.Korean),
        ];
        return ComboBox.Create(labels, new Signal<int>(index), width: 220f, isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null || (uint)i >= (uint)codes.Length) return;
                SetupWrites.SetUiCulture(codes[i], settings);
                Bump();
            });
    }

    static int LanguageIndex(string culture)
    {
        string[] codes = ["system", "en-US", "nl", "ko-KR"];
        for (int i = 0; i < codes.Length; i++)
            if (string.Equals(codes[i], culture, System.StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    static string Label(NotifyTopic topic) => Loc.Get(topic switch
    {
        NotifyTopic.NewAlbums => Strings.Settings.Notify.NewAlbums,
        NotifyTopic.NewEpisodes => Strings.Settings.Notify.NewEpisodes,
        NotifyTopic.ReleaseDrops => Strings.Settings.Notify.ReleaseDrops,
        NotifyTopic.Concerts => Strings.Settings.Notify.Concerts,
        NotifyTopic.Followers => Strings.Settings.Notify.Followers,
        NotifyTopic.DaylistRefresh => Strings.Settings.Notify.Daylist,
        NotifyTopic.AppUpdates => Strings.Settings.Notify.AppUpdates,
        _ => Strings.Settings.Notify.LibraryActivity,
    });

    static string Sub(NotifyTopic topic)
    {
        string sub = Loc.Get(topic switch
        {
            NotifyTopic.NewAlbums => Strings.Settings.Notify.NewAlbumsSub,
            NotifyTopic.NewEpisodes => Strings.Settings.Notify.NewEpisodesSub,
            NotifyTopic.ReleaseDrops => Strings.Settings.Notify.ReleaseDropsSub,
            NotifyTopic.Concerts => Strings.Settings.Notify.ConcertsSub,
            NotifyTopic.Followers => Strings.Settings.Notify.FollowersSub,
            NotifyTopic.DaylistRefresh => Strings.Settings.Notify.DaylistSub,
            NotifyTopic.AppUpdates => Strings.Settings.Notify.AppUpdatesSub,
            _ => Strings.Settings.Notify.LibraryActivitySub,
        });
        return NotificationPolicy.IsScheduled(topic) ? sub + "  ·  " + Loc.Get(Strings.Settings.Notify.ClosedBadge) : sub;
    }

    static string Glyph(NotifyTopic topic) => topic switch
    {
        NotifyTopic.NewAlbums => Icons.Album,
        NotifyTopic.NewEpisodes => Icons.Microphone,
        NotifyTopic.ReleaseDrops => Icons.Heart,
        NotifyTopic.Concerts => Icons.Calendar,
        NotifyTopic.Followers => Icons.Bell,
        NotifyTopic.DaylistRefresh => Icons.Sun,
        NotifyTopic.AppUpdates => Icons.Download,
        _ => Icons.Clock,
    };
}
