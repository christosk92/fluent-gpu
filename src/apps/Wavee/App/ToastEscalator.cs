using System;
using System.Collections.Generic;
using FluentGpu.WindowsApi.Notifications;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// Escalates newly-arrived notification-centre rows into Windows toasts. ONE place for every live topic (new albums,
/// episodes, concerts, followers, app updates) because they all reach the app the same way — a feed refresh rebuilds the
/// centre — so a per-feature toast call site would be five copies of the same watermark bug.
/// </summary>
/// <remarks>
/// <b>The watermark is the whole design.</b> <see cref="WaveeSettings.NotifyLastToastedMs"/> holds the newest timestamp
/// already raised. Without it, every rebuild (and every relaunch) would re-toast the entire feed — the loudest possible
/// bug in a notification system. The watermark advances past everything CONSIDERED, not just what was raised, so a topic
/// the user has since silenced cannot come back as a backlog the moment they enable it.
///
/// <b>Bounded per rebuild.</b> A feed that returns a hundred new rows must not produce a hundred banners; past
/// <see cref="MaxPerRebuild"/> the rest are folded into one summary line. The Action Center is not a log — the in-app
/// centre is, and it already has every row.
///
/// <b>Never toast on first run.</b> A zero watermark means "we have never escalated"; the first rebuild only records
/// where the feed was, so enabling notifications does not immediately replay history.
/// </remarks>
static class ToastEscalator
{
    /// <summary>At most this many individual banners per rebuild; the remainder collapse into one summary toast.</summary>
    const int MaxPerRebuild = 3;

    const string Group = "wavee.live";

    /// <summary>Consider <paramref name="items"/> (the freshly rebuilt centre, newest first) for escalation. Cheap and
    /// synchronous on the UI thread: it walks the list once and only touches the OS for rows that pass every gate.
    /// <para>Returns the number of banners actually raised, so the "Send event" diagnostic can report what HAPPENED
    /// instead of re-deriving what it thinks should have happened.</para></summary>
    public static int Consider(IAppSettings? settings, IReadOnlyList<WaveeNotification> items)
    {
        if (settings is null || items.Count == 0) return 0;
        if (!ToastNotifier.IsSupported) return 0;

        var policy = NotificationPrefs.Policy(settings);
        long watermark = settings.Get(WaveeSettings.NotifyLastToastedMs);
        long newest = watermark;

        // First pass: find the newest real timestamp so the watermark advances even when nothing is raised.
        // AppUpdate pins itself at long.MaxValue to sort first — including that would poison the watermark forever.
        for (int i = 0; i < items.Count; i++)
        {
            long ts = TimestampOf(items[i]);
            if (ts > newest) newest = ts;
        }

        int raised = 0;
        bool firstRun = watermark <= 0;
        if (!firstRun && policy.WindowsEnabled)
        {
            var now = DateTimeOffset.Now;
            int suppressed = 0;
            for (int i = items.Count - 1; i >= 0; i--)      // oldest → newest, so a truncated burst keeps the FRESHEST
            {
                var n = items[i];
                long ts = TimestampOf(n);
                if (ts <= watermark || !n.IsUnread) continue;
                var topic = NotificationPrefs.TopicOf(n);
                if (!policy.RaisesToastNow(NotificationPrefs.Level(settings, topic), now)) continue;
                // An app update is STATE, not an event: the row persists for as long as the update is available and is
                // never read-gated, so a timestamp test alone would re-raise it on every rebuild. Escalate it only when
                // the state or version actually changed.
                if (n is AppUpdateNotification u && !UpdateChanged(u)) continue;
                if (raised >= MaxPerRebuild) { suppressed++; continue; }
                if (TryRaise(n, topic, policy)) raised++;
            }
            if (suppressed > 0) RaiseSummary(suppressed, policy);
        }

        if (newest > watermark) settings.Set(WaveeSettings.NotifyLastToastedMs, newest);
        return raised;
    }

    /// <summary>The row's own timestamp, with the <c>long.MaxValue</c> SENTINEL folded to "now" — an update pins that
    /// value to sort to the top of the centre, which is a display concern, not a time.
    /// <para>Keyed on the sentinel, NOT on the type: folding every app-update row to a live "now" made it beat the
    /// watermark on every single rebuild, so it re-toasted forever (a simulated update carries a real timestamp and was
    /// re-raised by every unrelated rebuild — a banner storm).</para></summary>
    static long TimestampOf(WaveeNotification n) =>
        n.Timestamp == long.MaxValue ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : n.Timestamp;

    /// <summary>The (state, version) of the app update last raised as a banner. Process-lifetime, because that is the
    /// lifetime of the condition it describes.</summary>
    static string s_lastUpdateRaised = "";

    /// <summary>True when this update row differs from the one already banner-ed — the transition test that makes an
    /// app update fire once per change rather than once per rebuild.</summary>
    static bool UpdateChanged(AppUpdateNotification u)
    {
        string id = u.State.ToString() + ":" + (u.Version ?? "");
        if (string.Equals(id, s_lastUpdateRaised, StringComparison.Ordinal)) return false;
        s_lastUpdateRaised = id;
        return true;
    }

    static bool TryRaise(WaveeNotification n, NotifyTopic topic, in NotificationPolicy policy)
    {
        try
        {
            var (title, body, launch, image) = Present(n);
            if (title.Length == 0) return false;

            var toast = ToastBuilder.Create().Title(title);
            if (body.Length > 0) toast.Body(body);
            if (launch is { Length: > 0 }) toast.Launch(launch);
            if (!policy.Sound) toast.Silent();
            toast.Tag(TagFor(n)).Group(Group);
            if (image is { Length: > 0 })
            {
                // Remote art must become a local file: the unpackaged AUMID image path silently drops http(s).
                try { toast.AppLogo(ToastImageCache.Default.Localize(image), circle: topic == NotifyTopic.Followers); }
                catch (Exception) { /* art is optional; the text still says what happened */ }
            }
            return ToastNotifier.Default.Show(toast);
        }
        catch (Exception)
        {
            return false;   // a banner that fails is never worth failing a feed refresh over
        }
    }

    static void RaiseSummary(int more, in NotificationPolicy policy)
    {
        try
        {
            var toast = ToastBuilder.Create()
                .Title(more == 1 ? "1 more update in Wavee" : more + " more updates in Wavee")
                .Body("Open Wavee to see them.")
                .Launch("wavee://open?route=home")
                .Tag("live-summary").Group(Group);
            if (!policy.Sound) toast.Silent();
            ToastNotifier.Default.Show(toast);
        }
        catch (Exception) { }
    }

    /// <summary>Presentation for a banner. Deliberately NOT shared with the centre's row rendering: a toast has one line
    /// of title and one of body with no layout, so it needs its own (shorter) phrasing, and the centre must stay free to
    /// render rich rows.</summary>
    static (string Title, string Body, string? Launch, string? Image) Present(WaveeNotification n) => n switch
    {
        NewReleaseNotification r => (
            r.Name,
            r.Kind == NewReleaseKind.Episode ? "New episode — " + r.CreatorName : "New release — " + r.CreatorName,
            "wavee://play?ctx=" + Uri.EscapeDataString(r.Uri),
            r.ImageUrl),

        // The feed's title is already a finished sentence ("New Keenan Te show just announced near you"), server-localized
        // — so it is the title verbatim rather than something reworded on top of it.
        SocialNotification s => (
            s.Title,
            SpotifyUpdates.ActName(s) ?? "",
            s.ActionType == SocialActionType.Navigate && s.ActionUri is { Length: > 0 } u
                ? "wavee://open?route=" + Uri.EscapeDataString(u)
                : null,
            s.ImageUrl),

        AppUpdateNotification u => (
            u.State switch
            {
                AppUpdateState.Available => "Wavee " + (u.Version ?? "update") + " is available",
                AppUpdateState.Downloaded => "Wavee " + (u.Version ?? "update") + " is ready to install",
                AppUpdateState.Completed => "Wavee updated to " + (u.Version ?? "the latest version"),
                _ => "",
            },
            "", "wavee://open?route=settings", null),

        _ => ("", "", null, null),   // library activity never becomes a banner
    };

    static string TagFor(WaveeNotification n) => "live:" + n.Id;
}
