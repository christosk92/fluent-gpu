using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Notifications;
using Toast = FluentGpu.Controls.Toast;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The Notifications tab. Two global gates, then ONE DIAL PER TOPIC — the point of the page is that "notifications" is not
// a single switch. The centre's own filter pills are coarser than these dials on purpose (they group for browsing), so
// the dials key on NotifyTopic, which splits albums from episodes and concerts from followers: the two lumps that make
// people turn everything off instead of the one part that was too loud.
//
// Every dial is a LADDER (Off → In Wavee → Windows), never independent checkboxes: a banner without the in-app record is
// incoherent, because the banner disappears and the bell is the durable log.
sealed partial class SettingsPage
{
    Element NotificationsTab(Services? svc)
    {
        var settings = svc?.Settings;
        _ = NotificationPrefs.Epoch.Value;      // subscribe → the whole tab re-reads after any dial write
        var policy = NotificationPrefs.Policy(settings);
        var delivery = ToastDelivery();

        var children = new List<Element>();

        // The honesty layer FIRST: if Windows itself is suppressing us, every dial below is theatre until that is fixed.
        if (BlockedBanner(delivery) is { } blocked) children.Add(blocked);

        children.Add(SettingsSectionHeader(Loc.Get(Strings.Settings.Notify.DeliveryTitle), Icons.Bell));
        children.Add(SettingsRow(Loc.Get(Strings.Settings.Notify.Windows), Loc.Get(Strings.Settings.Notify.WindowsSub),
            Toggle(settings, WaveeSettings.NotifyWindows, afterWrite: ReconcileScheduled), Icons.Bell));
        children.Add(SettingsRow(Loc.Get(Strings.Settings.Notify.Sound), Loc.Get(Strings.Settings.Notify.SoundSub),
            Toggle(settings, WaveeSettings.NotifySound), Icons.Bell, isEnabled: policy.WindowsEnabled));
        children.Add(SettingsRow(Loc.Get(Strings.Settings.Notify.Quiet), Loc.Get(Strings.Settings.Notify.QuietSub),
            Toggle(settings, WaveeSettings.NotifyQuietEnabled, afterWrite: ReconcileScheduled), Icons.Moon,
            isEnabled: policy.WindowsEnabled));
        if (policy.WindowsEnabled && policy.Quiet.Enabled)
            children.Add(QuietRange(settings, policy));

        children.Add(SettingsSectionHeader(Loc.Get(Strings.Settings.Notify.TopicsTitle), Icons.Settings));
        children.Add(Hint(Loc.Get(Strings.Settings.Notify.TopicsHint)));
        if (!policy.WindowsEnabled)
            children.Add(InfoBar.Create(InfoBarSeverity.Informational,
                title: "", message: Loc.Get(Strings.Settings.Notify.WindowsOffHint), isClosable: false));
        foreach (var topic in NotificationPrefs.AllTopics)
            children.Add(TopicRow(settings, topic));

        children.Add(SettingsSectionHeader(Loc.Get(Strings.Settings.Notify.TryTitle), Icons.Play));
        children.Add(SettingsRow(Loc.Get(Strings.Settings.Notify.Test), Loc.Get(Strings.Settings.Notify.TestSub),
            Button.Standard(Loc.Get(Strings.Settings.Notify.TestButton), () => SendTest(policy)), Icons.Bell,
            isEnabled: policy.WindowsEnabled));

        return SettingsTabStack(children.ToArray());
    }

    // ── the per-topic dial ───────────────────────────────────────────────────────────────────────────────────────────

    Element TopicRow(IAppSettings? settings, NotifyTopic topic)
    {
        var level = NotificationPrefs.Level(settings, topic);
        // A topic that cannot reach Windows renders TWO segments, not three-with-one-dead: an unreachable switch teaches
        // the user the wrong thing about the product.
        bool canWindows = NotificationPolicy.CeilingFor(topic) == NotifyLevel.Windows;
        string[] labels = canWindows
            ? [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp), Loc.Get(Strings.Settings.Notify.LevelWindows)]
            : [Loc.Get(Strings.Settings.Notify.LevelOff), Loc.Get(Strings.Settings.Notify.LevelInApp)];

        var dial = SelectorBar.Create(labels, new Signal<int>((int)level), onChange: i =>
        {
            NotificationPrefs.SetLevel(settings, topic, (NotifyLevel)i);
            // A SCHEDULED topic dialled down must give back the toast the OS is already holding, not merely stop writing
            // new ones — the OS keeps its timer whether the app agrees or not.
            if (NotificationPolicy.IsScheduled(topic)) ReconcileScheduled();
            Bump();
        });

        return SettingsRow(Label(topic), Sub(topic), dial, Glyph(topic));
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
        // "Even when closed" is the property people are actually shopping for, so the scheduled topics say it out loud
        // rather than leaving the user to infer it from a docs page they will never read.
        return NotificationPolicy.IsScheduled(topic)
            ? sub + "  ·  " + Loc.Get(Strings.Settings.Notify.ClosedBadge)
            : sub;
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

    // ── the global gates ─────────────────────────────────────────────────────────────────────────────────────────────

    Element Toggle(IAppSettings? settings, SettingKey<bool> key, Action? afterWrite = null)
        => ToggleSwitch.Create(new Signal<bool>(settings?.Get(key) ?? false), onChange: _ =>
        {
            if (settings is null) return;
            settings.Set(key, !settings.Get(key));
            NotificationPrefs.Bump();
            afterWrite?.Invoke();
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

    Element QuietRange(IAppSettings? settings, in NotificationPolicy policy)
    {
        string[] hours = HourLabels();
        Element combo(SettingKey<int> key, int value) =>
            ComboBox.Create(hours, new Signal<int>(Math.Clamp(value, 0, 23)), width: 120f,
                isEnabled: settings is not null, onChange: h =>
                {
                    settings?.Set(key, h);
                    NotificationPrefs.Bump();
                    ReconcileScheduled();
                    Bump();
                });

        return SettingsRow(Loc.Get(Strings.Settings.Notify.QuietFrom) + " / " + Loc.Get(Strings.Settings.Notify.QuietTo),
            null,
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f,
                Children =
                [
                    combo(WaveeSettings.NotifyQuietFromHour, policy.Quiet.FromHour),
                    combo(WaveeSettings.NotifyQuietToHour, policy.Quiet.ToHour),
                ],
            },
            Icons.Clock);
    }

    // 00:00 … 23:00. Built from the invariant clock rather than a localized time format: the value IS an hour index, and
    // a format that reorders or 12-hours it would make the ComboBox index stop matching the stored hour.
    static string[] HourLabels()
    {
        var labels = new string[24];
        for (int h = 0; h < 24; h++) labels[h] = h.ToString("00") + ":00";
        return labels;
    }

    // ── status + test ────────────────────────────────────────────────────────────────────────────────────────────────

    static ToastDeliverySetting ToastDelivery()
    {
        try { return ToastNotifier.Default.Setting; }
        catch (Exception) { return ToastDeliverySetting.Unknown; }
    }

    /// <summary>The banner for an OS-side block. Null when Windows is happy (or when we cannot tell — an
    /// <see cref="ToastDeliverySetting.Unknown"/> read is not evidence of a problem, and crying wolf on an unregistered
    /// notifier would be a permanent false alarm).</summary>
    static Element? BlockedBanner(ToastDeliverySetting setting)
    {
        (string Title, string Body)? text = setting switch
        {
            ToastDeliverySetting.DisabledForApplication =>
                (Loc.Get(Strings.Settings.Notify.BlockedApp), Loc.Get(Strings.Settings.Notify.BlockedAppSub)),
            ToastDeliverySetting.DisabledForUser =>
                (Loc.Get(Strings.Settings.Notify.BlockedUser), Loc.Get(Strings.Settings.Notify.BlockedUserSub)),
            ToastDeliverySetting.DisabledByGroupPolicy =>
                (Loc.Get(Strings.Settings.Notify.BlockedPolicy), Loc.Get(Strings.Settings.Notify.BlockedPolicySub)),
            _ => null,
        };
        if (text is not { } t) return null;

        // Group policy is not something the user can click their way out of — offering the button would be a dead end.
        Element? action = setting == ToastDeliverySetting.DisabledByGroupPolicy
            ? null
            : Button.Standard(Loc.Get(Strings.Settings.Notify.OpenWindows),
                () => LoginView.OpenUrl("ms-settings:notifications"));

        return InfoBar.Create(InfoBarSeverity.Warning, t.Title, t.Body, isClosable: false, actionButton: action);
    }

    static void SendTest(in NotificationPolicy policy)
    {
        bool ok;
        try
        {
            var toast = ToastBuilder.Create()
                .Title(Loc.Get(Strings.Settings.Notify.Test))
                .Body(Loc.Get(Strings.Settings.Notify.TestSub))
                .Launch("wavee://open?route=settings")
                .Tag("notify-test");
            if (!policy.Sound) toast.Silent();
            ok = ToastNotifier.Default.Show(toast);
        }
        catch (Exception) { ok = false; }

        Toast.Show(Loc.Get(ok ? Strings.Settings.Notify.TestSent : Strings.Settings.Notify.TestFailed),
            new ToastOptions { Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning });
    }

    /// <summary>Re-derive the OS-held scheduled set after any change that could alter it (the master gate, the drops dial,
    /// quiet hours). Scheduled toasts outlive the process, so a settings change that only affected FUTURE writes would
    /// leave yesterday's entries to fire under the new, stricter settings.</summary>
    static void ReconcileScheduled()
    {
        ReleaseNotifier.RequestReconcile();
        DaylistNotifier.RequestReconcile();
    }

    static Element Hint(string text) => new BoxEl
    {
        MinWidth = 0f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
        Children = [Body(text) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 3 }],
    };
}
