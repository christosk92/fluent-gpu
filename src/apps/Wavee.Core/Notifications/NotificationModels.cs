using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>The four notification categories the center aggregates — the filter pills key on these.</summary>
public enum NotificationCategory { AppUpdate, Social, NewRelease, Activity }

/// <summary>A single notification-center row. Discriminated by concrete type; the UI switches on the runtime type and
/// keys its rows on <see cref="Id"/>. Presentation (strings/icons) lives ONLY in the UI — the domain carries data.</summary>
public abstract record WaveeNotification(string Id, long Timestamp, bool IsUnread)
{
    /// <summary>Which filter pill this row belongs to.</summary>
    public abstract NotificationCategory Category { get; }
}

/// <summary>An app-update notification — the state its action button maps to. (No updater ships yet; the seam is a stub.)</summary>
public sealed record AppUpdateNotification(long Timestamp, bool IsUnread,
        AppUpdateState State, string? Version, string? ReleaseNotesUrl, string? Error)
    : WaveeNotification("update", Timestamp, IsUnread)
{
    public override NotificationCategory Category => NotificationCategory.AppUpdate;
}

/// <summary>How a social notification's click resolves: an in-app route, or an external web page (users/concerts).</summary>
public enum SocialActionType { Navigate, NavigateWebview }

/// <summary>A Spotify social notification (followers, concerts, announcements) from the gander feed.</summary>
public sealed record SocialNotification(string Id, long Timestamp, bool IsUnread,
        string Title, string? ActionUri, SocialActionType ActionType, string? ImageUrl,
        IReadOnlyList<string> UserNames, string? StorageId)
    : WaveeNotification(Id, Timestamp, IsUnread)
{
    public override NotificationCategory Category => NotificationCategory.Social;
}

/// <summary>A "What's New" item from a followed artist/show.</summary>
public enum NewReleaseKind { Album, Episode }

public sealed record NewReleaseNotification(string Id, long Timestamp, bool IsUnread,
        NewReleaseKind Kind, string Uri, string Name, string? ImageUrl, string CreatorName,
        string? AlbumType, bool Played)
    : WaveeNotification(Id, Timestamp, IsUnread)
{
    public override NotificationCategory Category => NotificationCategory.NewRelease;
}

/// <summary>A local library-mutation activity entry (like/save/follow, playlist edits) — Undo-capable when invertible.</summary>
public sealed record ActivityNotification(ActivityEntry Entry)
    : WaveeNotification("act:" + Entry.Id, Entry.TimestampMs, !Entry.Read)
{
    public override NotificationCategory Category => NotificationCategory.Activity;
}
