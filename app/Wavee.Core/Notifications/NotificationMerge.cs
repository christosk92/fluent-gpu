using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>The pure notification aggregation: merge the four category snapshots into one newest-first list + the total
/// unread count, applying the remote-feed last-seen watermarks. Engine-free so it is unit-testable on its own; the
/// <c>NotificationCenterBridge</c> is the thin reactive wrapper around it.</summary>
public static class NotificationMerge
{
    public static (IReadOnlyList<WaveeNotification> Items, int Unread) Build(
        AppUpdateNotification? update,
        IReadOnlyList<SocialNotification> social, long ganderSeenMs,
        IReadOnlyList<NewReleaseNotification> whatsNew, long whatsNewSeenMs,
        IReadOnlyList<ActivityEntry> activity)
    {
        var list = new List<WaveeNotification>(
            (update is null ? 0 : 1) + social.Count + whatsNew.Count + activity.Count);

        if (update is not null) list.Add(update);
        foreach (var s in social) list.Add(s with { IsUnread = s.IsUnread && s.Timestamp > ganderSeenMs });
        foreach (var n in whatsNew) list.Add(n with { IsUnread = n.IsUnread && n.Timestamp > whatsNewSeenMs });
        foreach (var e in activity) list.Add(new ActivityNotification(e));

        list.Sort(static (a, b) => b.Timestamp.CompareTo(a.Timestamp));

        int unread = 0;
        foreach (var x in list) if (x.IsUnread) unread++;
        return (list, unread);
    }
}
