using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The notification-center bridge: merges the four category snapshots (app-update, gander social, what's-new, local
/// activity) into one reactive <see cref="Items"/> list + the bell's <see cref="UnreadCount"/> + the active
/// <see cref="Filter"/>. Subscribes each source's Changed stream and does a full <see cref="Rebuild"/> on the UI thread
/// (≤ ~290 items — a full rebuild is cheap). Read-state: activity uses its per-entry read flag; the remote feeds gate
/// their server "new" flag against the persisted last-seen keys, advanced on panel open, PLUS the per-item
/// <see cref="MarkRead"/> set for rows marked seen one at a time on other surfaces. Provided once at the app root
/// via <see cref="Slot"/>; follows the <see cref="LibraryBridge"/>/<see cref="FriendsBridge"/> pattern end to end.
/// </summary>
public sealed class NotificationCenterBridge
{
    public static readonly Context<NotificationCenterBridge?> Slot = new(null);

    readonly ActivityLog _log;
    readonly ISpotifyNotificationsService _social;
    readonly IWhatsNewService _whatsNew;
    readonly IAppUpdateService _update;
    readonly IAppSettings _settings;
    readonly ActivityUndoExecutor _undo;
    readonly List<IDisposable> _subs = [];
    Action<Action>? _post;
    bool _active;

    /// <summary>Every notification across the four categories, newest-first. The panel filters this by <see cref="Filter"/>.</summary>
    public Signal<IReadOnlyList<WaveeNotification>> Items { get; } = new(Array.Empty<WaveeNotification>());
    /// <summary>Badge-eligible unread count. Local Activity entries remain in the panel but do not increment the bell.</summary>
    public Signal<int> UnreadCount { get; } = new(0);
    /// <summary>The active filter pill (null = All).</summary>
    public Signal<NotificationCategory?> Filter { get; } = new(null);
    /// <summary>Bumped by the panel's 30-second tick while open so relative times recompute per render.</summary>
    public Signal<long> NowTick { get; } = new(0);
    /// <summary>The gander feed's coarse state — lets the panel tell "failed/offline/loading" apart from genuinely empty.</summary>
    public Signal<NotificationFeedState> SocialState { get; } = new(NotificationFeedState.Idle);
    /// <summary>The what's-new feed's coarse state (same purpose as <see cref="SocialState"/>).</summary>
    public Signal<NotificationFeedState> WhatsNewState { get; } = new(NotificationFeedState.Idle);

    public NotificationCenterBridge(ActivityLog log, ISpotifyNotificationsService social, IWhatsNewService whatsNew,
        IAppUpdateService update, IAppSettings settings, ActivityUndoExecutor undo)
    {
        _log = log;
        _social = social;
        _whatsNew = whatsNew;
        _update = update;
        _settings = settings;
        _undo = undo;
    }

    /// <summary>Subscribe the four Changed streams → a full rebuild on the UI thread. Idempotent; call once from a mount
    /// effect with <c>UsePost()</c>.</summary>
    public void Activate(Action<Action> post)
    {
        if (_active) return;
        _active = true;
        _post = post;
        _subs.Add(_log.Changed.Subscribe(_ => post(Rebuild)));
        _subs.Add(_social.Changed.Subscribe(_ => post(Rebuild)));
        _subs.Add(_whatsNew.Changed.Subscribe(_ => post(Rebuild)));
        _subs.Add(_update.Changed.Subscribe(_ => post(Rebuild)));
        Rebuild();
    }

    /// <summary>Panel opened: refetch the remote feeds if stale + advance the last-seen keys (clears remote unread).</summary>
    public void OnPanelOpened()
    {
        _social.EnsureFresh();
        _whatsNew.EnsureFresh();
        AdvanceRemoteLastSeen();
        Rebuild();
    }

    public void SetFilter(NotificationCategory? category) => Filter.Value = category;

    /// <summary>Mark everything read — the activity read flag + advance the remote last-seen keys. (Update clears via its own action.)</summary>
    public void MarkAllRead()
    {
        _log.MarkAllRead();
        AdvanceRemoteLastSeen();
        Rebuild();
    }

    /// <summary>Mark ONE remote notification read, durably. This is the whole per-item read seam: every surface that
    /// shows a notification row outside this panel (today: Home's what's-new timeline) calls it, and the state lands in
    /// the SAME store the panel and the bell badge read back through <see cref="Rebuild"/> — there is no second
    /// read-state anywhere. A no-op for ids already covered by a watermark or by a previous call, so a repeat click
    /// costs nothing and never republishes.</summary>
    public void MarkRead(string? id)
    {
        if (id is not { Length: > 0 }) return;
        string current = _settings.Get(WaveeSettings.NotificationsReadIds);
        string next = NotificationReadIds.Add(current, id);
        if (string.Equals(next, current, StringComparison.Ordinal)) return;
        _settings.Set(WaveeSettings.NotificationsReadIds, next);
        Rebuild();
    }

    /// <summary>Clear the local activity history (only the Activity category).</summary>
    public void ClearActivity()
    {
        _log.Clear();
        Rebuild();
    }

    /// <summary>Undo an invertible activity entry; a failed undo surfaces a caution toast.</summary>
    public async Task UndoAsync(ActivityEntry entry)
    {
        bool ok = await _undo.UndoAsync(entry).ConfigureAwait(false);
        if (!ok && _post is { } post)
            post(() => Toast.Show(Loc.Get(Strings.Notifications.UndoFailed), new ToastOptions { Severity = InfoBarSeverity.Warning }));
    }

    /// <summary>Undo by ACTIVITY ID — the seam a CONFIRMATION TOAST uses. Undo already existed, but only as a button in
    /// the notification panel, which is the wrong place to look for it: a filing mistake ("wrong playlist", "wrong row")
    /// is noticed immediately, while the user is still on the page they filed from, and the escape has to be in the
    /// confirmation itself. An unknown, aged-out or already-undone id is a NO-OP rather than an error — the panel may have
    /// undone it first, and a toast that has been sitting on screen must not fail loudly for being late.</summary>
    public Task UndoByIdAsync(long id)
    {
        if (id < 0) return Task.CompletedTask;
        var entries = _log.Snapshot;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Id == id)
                return entries[i].IsUndoable ? UndoAsync(entries[i]) : Task.CompletedTask;
        return Task.CompletedTask;
    }

    void AdvanceRemoteLastSeen()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _settings.Set(WaveeSettings.NotificationsGanderLastSeenMs, now);
        _settings.Set(WaveeSettings.NotificationsWhatsNewLastSeenMs, now);
        // A watermark advance subsumes every individually-marked id, so the per-item set is dropped rather than kept
        // growing beside a watermark that already covers it. This is what bounds it in practice.
        if (_settings.Get(WaveeSettings.NotificationsReadIds).Length > 0)
            _settings.Set(WaveeSettings.NotificationsReadIds, "");
    }

    // Full merge of the four snapshots → Items + UnreadCount, both on the UI thread. The merge itself is the pure,
    // engine-free NotificationMerge.Build (unit-tested); this method just supplies the live snapshots + settings.
    void Rebuild()
    {
        long ganderSeen = _settings.Get(WaveeSettings.NotificationsGanderLastSeenMs);
        long whatsNewSeen = _settings.Get(WaveeSettings.NotificationsWhatsNewLastSeenMs);

        // App update pinned at the top (state-driven, unread until acknowledged).
        AppUpdateNotification? update = _update.Current != AppUpdateState.None
            ? new AppUpdateNotification(long.MaxValue, IsUnread: true, _update.Current, _update.Version, _update.ReleaseNotesUrl, _update.Error)
            : null;

        var (items, unread) = NotificationMerge.Build(
            update, _social.Snapshot, ganderSeen, _whatsNew.Snapshot, whatsNewSeen, _log.Snapshot,
            _settings.Get(WaveeSettings.NotificationsReadIds));

        Items.Value = items;
        UnreadCount.Value = unread;
        SocialState.Value = _social.State;
        WhatsNewState.Value = _whatsNew.State;
    }
}
