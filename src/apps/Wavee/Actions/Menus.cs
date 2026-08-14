using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

// Per-entity menu composition — plain code over the AppAction singletons, run lazily inside the ContextMenu.Attach
// open-thunk (allocation at human rate, zero per-frame). Non-empty Primary → the Win11 Explorer command-bar body;
// rows-only → a plain vertical menu. ONE builder per entity; selection semantics live in TrackContextMenu.
//
// ── THE MENU GRAMMAR (D48) ──────────────────────────────────────────────────────────────────────────────────────────
// A right-click on the same KIND of thing offers the same verbs on every surface. Two menu builders for one kind that
// each grew their own subset is the defect this grammar exists to prevent (a search song offered no Go-to-album; a home
// mix card offered no Play-next/Add-to-playlist). The CORE set per kind is:
//
//   TRACK      Play · Play next · Play after · Save · Add to playlist ▸ · Go to album · Go to artist(s) · Share ▸
//   CONTAINER  Play · Play next · Play after · Save · Add to playlist ▸ · Pin · Open · Go to artist · Share ▸
//              (playlist / album / mix / generated; "Add to playlist" deposits the container's TRACKS)
//   SHOW       Play · Open · Pin · Share ▸       (a show has no Track set — see ContainerTracks)
//
// and the ORDER is fixed, whether the verbs render as a command strip or as plain rows:
//
//   transport (Play · Play next · Play after) → state (Save) → collection (Add to playlist · Move · Pin)
//   → navigation (Open · Go to album · Go to artist) → Share → surface extras → destructive LAST (behind a separator).
//
// Surface EXTRAS are additive and documented per builder (queue rows keep Move up/down + Remove from queue, sidebar
// rows keep Move up/down + Remove for navbar customization, an editable playlist keeps its owner block, a track keeps
// credits/song-radio/Video ▸). A core verb may be OMITTED only where the seam genuinely does not exist for that kind —
// and then with a comment naming the reason, never silently.
public static class Menus
{
    const int MaxInlinePlaylists = 10;   // Add-to-playlist submenu cap; the rest via "More playlists…" → the picker

    // ── Track set (detail rows, batch bar, eager lists, queue, now-playing) ─────────────────────────────────────────
    /// <summary>The track(s) menu. Primary strip [Play · Play next · Play after · Save]; rows per the grammar above.
    /// <paramref name="showGoToAlbum"/> is false on album detail pages (you are already there); <paramref name="extras"/>
    /// are the calling surface's own additions (the queue panel's Move up/down), inserted after Share and before the
    /// destructive block.</summary>
    public static ContextMenuModel Tracks(in ActionContext ctx, bool showGoToAlbum = true,
                                          IReadOnlyList<MenuFlyoutItem>? extras = null)
        => new(TrackTransportStrip(in ctx), TrackRows(in ctx, showGoToAlbum, extras), TrackHeader(ctx.Target.Tracks));

    /// <summary>The four transport/state verbs as a labeled command strip (the Explorer command-bar body).</summary>
    static AppBarCommand[] TrackTransportStrip(in ActionContext ctx) =>
    [
        TrackActions.Play.ToBarCommand(ctx),
        TrackActions.PlayNext.ToBarCommand(ctx),
        TrackActions.AddToQueue.ToBarCommand(ctx),
        TrackActions.ToggleLike.ToBarCommand(ctx),
    ];

    /// <summary>The same four verbs as plain ROWS — for the rows-only track menus (a 240-DIP sidebar pane is not where
    /// an Explorer-style labeled strip belongs, but the core verbs must still be there).</summary>
    static void AddTrackTransportRows(List<MenuFlyoutItem> rows, in ActionContext ctx)
    {
        rows.Add(TrackActions.Play.ToMenuItem(ctx));
        rows.Add(TrackActions.PlayNext.ToMenuItem(ctx));
        rows.Add(TrackActions.AddToQueue.ToMenuItem(ctx));
        rows.Add(TrackActions.ToggleLike.ToMenuItem(ctx));
    }

    /// <summary>The track menu's vertical rows only (also the batch bar's overflow source). Grammar order: collection →
    /// navigation → Share → extras → destructive.</summary>
    public static IReadOnlyList<MenuFlyoutItem> TrackRows(in ActionContext ctx, bool showGoToAlbum = true,
                                                         IReadOnlyList<MenuFlyoutItem>? extras = null)
    {
        var rows = new List<MenuFlyoutItem>(12) { AddToPlaylistItem(in ctx) };
        // MOVE is offered only where a source to move OUT of exists (an editable playlist context) — everywhere else
        // "move" and "add" would be the same verb twice.
        if (MoveToPlaylistItem(in ctx) is { } move) rows.Add(move);

        // Navigation. A multi-selection has no single album/artist to go to, so these are single-target rows.
        if (ctx.Target.Single is { } single)
        {
            if (showGoToAlbum && single.Album is { Uri.Length: > 0 })
                rows.Add(TrackActions.GoToAlbum.ToMenuItem(ctx));
            var nav = NavigableArtists(single.Artists);
            if (nav is { Count: 1 } one)
                // The action navigates to the PRIMARY artist; when the only navigable one is a secondary (the primary
                // came through name-only), the row has to carry that artist itself.
                rows.Add(ActionRules.CanGoToArtist(in ctx.Target)
                    ? TrackActions.GoToArtist.ToMenuItem(ctx)
                    : GoToArtistItem(ctx.S, one[0]));
            else if (nav is { Count: > 1 })
                rows.Add(GoToArtistsItem(ctx.S, nav));
        }

        rows.Add(ShareItem(in ctx));

        // Surface extras: the track kind's own (credits · song radio · Video ▸) then the caller's.
        if (ctx.Target.Single is not null)
        {
            if (ActionRules.CanViewCredits(in ctx.Target))
                rows.Add(TrackActions.ViewCredits.ToMenuItem(ctx));
            if (ActionRules.CanStartTrackRadio(in ctx.Target))
                rows.Add(TrackActions.GoToSongRadio.ToMenuItem(ctx));
            if (VideoItem(in ctx) is { } video)
                rows.Add(video);
        }
        if (extras is { Count: > 0 })
        {
            rows.Add(MenuFlyoutItem.Separator);
            for (int i = 0; i < extras.Count; i++) rows.Add(extras[i]);
        }

        bool removeRow = TrackActions.RemoveFromThisPlaylist.EnabledFor(ctx);
        bool removeQueue = ctx.Target.Kind == TargetKind.QueueEntry && ctx.Target.RemoveFromDisplay is not null;
        if (removeRow || removeQueue) rows.Add(MenuFlyoutItem.Separator);
        if (removeRow) rows.Add(TrackActions.RemoveFromThisPlaylist.ToMenuItem(ctx));
        if (removeQueue) rows.Add(TrackActions.RemoveFromQueue.ToMenuItem(ctx));
        return rows;
    }

    /// <summary>The artists a menu can actually navigate to: those carrying a uri. Several producers hand back a display
    /// NAME with no uri (a projected sidebar row, a search row without an artist link) — navigating those would land on
    /// an empty <c>artist:</c> route. Returns null when none qualify, so the row is absent rather than dead.</summary>
    static IReadOnlyList<ArtistRef>? NavigableArtists(IReadOnlyList<ArtistRef> artists)
    {
        int n = 0;
        for (int i = 0; i < artists.Count; i++) if (artists[i].Uri.Length > 0) n++;
        if (n == 0) return null;
        if (n == artists.Count) return artists;
        var kept = new List<ArtistRef>(n);
        for (int i = 0; i < artists.Count; i++) if (artists[i].Uri.Length > 0) kept.Add(artists[i]);
        return kept;
    }

    // ── Share ▸ (Copy link(s) + single-target Copy Spotify URI / Open in Spotify Web) ────────────────────────────────
    /// <summary>The Share submenu that replaces the bare Copy-link row app-wide (tracks, cards, sidebar playlist):
    /// Copy link(s) always; the raw-URI and web-player variants only when the target is a single shareable spotify
    /// entity (multi-select collapses to just "Copy links (N)" — the URI/web variants are single-target).</summary>
    static MenuFlyoutItem ShareItem(in ActionContext ctx)
    {
        var items = new List<MenuFlyoutItem>(3) { TrackActions.CopyLink.ToMenuItem(ctx) };
        if (SpotifyLink.SingleUri(in ctx.Target) is not null)
        {
            items.Add(TrackActions.CopySpotifyUri.ToMenuItem(ctx));
            items.Add(TrackActions.OpenInSpotifyWeb.ToMenuItem(ctx));
        }
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Menu.Share), items, ActionIcons.Resolve(ActionIcons.Share));
    }

    // ── Video ▸ (the user's local video-override curation for ONE playable) ─────────────────────────────────────────
    /// <summary>The Video submenu: "Attach video file…" when nothing is attached, else "Replace video file…" +
    /// "Locate video file…" (broken link only) + "Show in Explorer" (file present) + a separated "Remove video".
    /// Null — the row is absent entirely — for a multi-selection (one file, one playable) or on a backend with no
    /// curation service. Which rows exist is decided by <see cref="VideoOverrideUx.MenuFor"/>, which walks the SAME
    /// tier decision playback takes, so the menu can never disagree with what will actually play.</summary>
    static MenuFlyoutItem? VideoItem(in ActionContext ctx)
    {
        string? uri = ctx.Target.Single is { Uri.Length: > 0 } t ? t.Uri : null;
        var which = VideoOverrideUx.MenuFor(ctx.Target.Single is not null, uri, ctx.S.VideoOverrides);
        if (which == VideoMenuItems.None) return null;

        var items = new List<MenuFlyoutItem>(5);
        if ((which & VideoMenuItems.Attach) != 0) items.Add(VideoActions.AttachVideo.ToMenuItem(ctx));
        if ((which & VideoMenuItems.Replace) != 0) items.Add(VideoActions.ReplaceVideo.ToMenuItem(ctx));
        if ((which & VideoMenuItems.Locate) != 0) items.Add(VideoActions.LocateVideo.ToMenuItem(ctx));
        if ((which & VideoMenuItems.ShowInExplorer) != 0) items.Add(VideoActions.ShowVideoInExplorer.ToMenuItem(ctx));
        // Destructive last, behind a separator (the SidebarPlaylist Delete convention). No confirm dialog — detaching is
        // metadata-only, never touches the file, and the toast carries Undo.
        if ((which & VideoMenuItems.Remove) != 0)
        {
            items.Add(MenuFlyoutItem.Separator);
            items.Add(VideoActions.RemoveVideo.ToMenuItem(ctx));
        }
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.VideoOverride.MenuTitle), items,
            ActionIcons.Resolve(ActionIcons.Video));
    }

    /// <summary>Multi-artist track → a "Go to artists" cascade, one row per artist. Callers pass an already-filtered
    /// list (<see cref="NavigableArtists"/>): a uri-less artist has nowhere to go and must not get a row.</summary>
    static MenuFlyoutItem GoToArtistsItem(ActionServices s, IReadOnlyList<ArtistRef> artists)
    {
        var items = new MenuFlyoutItem[artists.Count];
        for (int i = 0; i < artists.Count; i++)
        {
            var a = artists[i];   // fresh capture per row — each navigates to its OWN artist
            items[i] = new MenuFlyoutItem(a.Name, null, s.Go is not null, () => s.Go?.Invoke("artist:" + a.Uri, a.Name));
        }
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Menu.GoToArtists), items, ActionIcons.Resolve(ActionIcons.Artist));
    }

    /// <summary>"Go to artist" for ONE named artist that is not the target's primary (so the action singleton, which
    /// always navigates to <c>Artists[0]</c>, cannot express it).</summary>
    static MenuFlyoutItem GoToArtistItem(ActionServices s, ArtistRef a)
        => new(Loc.Get(Strings.Detail.GoToArtist), ActionIcons.Resolve(ActionIcons.Artist),
               s.Go is not null, () => s.Go?.Invoke("artist:" + a.Uri, a.Name));

    // ── Add to playlist ▸ (New playlist + up to 10 editable playlists + "More playlists…" → the picker) ─────────────
    static MenuFlyoutItem AddToPlaylistItem(in ActionContext ctx)
    {
        var s = ctx.S;
        var tracks = ctx.Target.Tracks;
        bool canAdd = s.Library is not null && tracks.Count > 0;
        return PlaylistDepositItem(s, Loc.Get(Strings.Detail.AddToPlaylist), Icons.Add, canAdd,
            deposit: (uri, name) => AddTo(s, uri, name, tracks),
            createAndDeposit: () => CreateAndAdd(s, tracks),
            excludeUri: null, pickerTracks: tracks);
    }

    /// <summary>Add to playlist ▸ for a CONTAINER (album / playlist / mix card): the same submenu, depositing the
    /// container's TRACKS — resolved through the shared <see cref="ContainerTracks"/> seam, i.e. exactly what dropping
    /// this card onto that playlist deposits. Null for a kind with no track set (an artist, a show — see
    /// <see cref="ContainerTracks"/>), so the row is ABSENT rather than a disabled promise.</summary>
    static MenuFlyoutItem? ContainerAddToPlaylistItem(in ActionContext ctx)
    {
        var s = ctx.S;
        if (ContainerTracks.ResolverFor(in ctx) is not { } resolve) return null;
        bool canAdd = s.Library is not null;
        // Adding a playlist to ITSELF is the one deposit that is always a no-op duplicate — the same refusal
        // WaveeResourceDrop.DepositTracksAsync makes for the container-on-itself drop.
        string? exclude = ctx.Target.Kind == TargetKind.Playlist ? ctx.Target.Uri : null;
        return PlaylistDepositItem(s, Loc.Get(Strings.Detail.AddToPlaylist), Icons.Add, canAdd,
            deposit: (uri, name) => ContainerTracks.AddTo(s, uri, name, resolve),
            createAndDeposit: () => CreateAndDeposit(s, (uri, name) => ContainerTracks.AddTo(s, uri, name, resolve)),
            excludeUri: exclude, pickerTracks: Array.Empty<Track>());
    }

    /// <summary>The ONE playlist-deposit submenu shape behind Add-to-playlist (tracks), Add-to-playlist (container) and
    /// Move-to-playlist: New playlist + up to <see cref="MaxInlinePlaylists"/> editable playlists + "More playlists…" →
    /// the full picker. Only what a pick DOES differs, so only that is a parameter — the row filter (editable, real
    /// <c>spotify:playlist:*</c>, the same one <c>PlaylistPickerPanel</c> applies) lives here once.</summary>
    static MenuFlyoutItem PlaylistDepositItem(ActionServices s, string title, IconRef icon, bool canAdd,
                                              Action<string, string> deposit, Action createAndDeposit,
                                              string? excludeUri, IReadOnlyList<Track> pickerTracks)
    {
        var items = new List<MenuFlyoutItem>(MaxInlinePlaylists + 3)
        {
            new(Loc.Get(Strings.Detail.NewPlaylist), Icons.Add, canAdd, createAndDeposit),
        };

        s.Store?.EnsurePlaylists();
        // MOST-RECENTLY-FILED FIRST. The inline rows used to be rootlist order truncated to ten, which for anyone with
        // more than ten playlists is the same ten forever — very often not the one they are reaching for, so the common
        // case degraded into "More playlists… → type the name". PlaylistDepositTargets owns the order AND the eligibility
        // filter, shared with the picker and the tab drop rules (they used to be three separate copies).
        var ordered = PlaylistDepositTargets.Order(s.Store?.Playlists.Value.Peek(), RecentDeposits(s), excludeUri);
        int inline = Math.Min(ordered.Count, MaxInlinePlaylists);
        for (int i = 0; i < inline; i++)
        {
            var uri = ordered[i].Uri;
            var name = ordered[i].Name;
            items.Add(new MenuFlyoutItem(name, null, canAdd, () => deposit(uri, name)));
        }

        items.Add(MenuFlyoutItem.Separator);
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MorePlaylists), null,
            canAdd && s.Overlay is not null, () => OpenPicker(s, pickerTracks, title, deposit, excludeUri)));

        return MenuFlyoutItem.SubMenu(title, items, icon, enabled: canAdd);
    }

    /// <summary>The persisted most-recently-filed-into playlist uris (newest first), or empty when there is no settings
    /// seam — the ordering then falls back to plain rootlist order, which is exactly the previous behaviour.</summary>
    internal static IReadOnlyList<string> RecentDeposits(ActionServices s)
        => s.Svc is { } svc ? PlaylistDepositTargets.Parse(svc.Settings.Get(WaveeSettings.PlaylistDepositRecents))
                            : Array.Empty<string>();

    /// <summary>Record a successful deposit so it leads the list next time. Idempotent-ish: an unchanged MRU is not
    /// written, so re-filing into the playlist already at the front costs no storage write.</summary>
    internal static void RememberDeposit(ActionServices s, string uri)
    {
        if (s.Svc is not { } svc) return;
        string current = svc.Settings.Get(WaveeSettings.PlaylistDepositRecents);
        string next = PlaylistDepositTargets.Serialize(PlaylistDepositTargets.Remember(PlaylistDepositTargets.Parse(current), uri));
        if (!string.Equals(next, current, StringComparison.Ordinal))
            svc.Settings.Set(WaveeSettings.PlaylistDepositRecents, next);
    }

    /// <summary>The confirmation toast for a completed deposit. Its ONE action slot spends itself on <b>Undo</b>, not
    /// "Open": the user is mid-flow on the page they filed from and rarely wants to leave it, whereas the recoverable
    /// mistake — wrong playlist, wrong row, a multi-selection they had forgotten about — is both common and, until now,
    /// only recoverable by going to find the notification panel. A create-then-add toasts <b>Open</b> instead
    /// (see <see cref="CreateAndAdd"/>): you just made a playlist and probably want to name it.</summary>
    internal static void ToastDeposited(ActionServices s, string name, long activityId)
    {
        var nc = s.Svc?.Notifications;
        Toast.Show(Strings.Detail.AddedToPlaylist(name), new ToastOptions
        {
            Severity = InfoBarSeverity.Success,
            ActionLabel = nc is not null && activityId >= 0 ? Loc.Get(Strings.Notifications.Undo) : null,
            OnAction = nc is not null && activityId >= 0 ? () => _ = nc.UndoByIdAsync(activityId) : null,
        });
    }

    /// <summary>"New playlist" for a deposit whose payload is not in hand (a container): create, then run the same
    /// deposit the named rows run. The track path keeps <see cref="CreateAndAdd"/>, which can add inside one flow.</summary>
    static void CreateAndDeposit(ActionServices s, Action<string, string> deposit)
    {
        if (s.Library is not { } lib) return;
        string name = NextPlaylistName(s);
        var post = s.Post;
        _ = Run();
        async Task Run()
        {
            string uri;
            try { uri = await lib.CreatePlaylistAsync(name).ConfigureAwait(false); }
            catch (Exception ex)
            {
                ContainerActions.Post(post, () => PlaylistEditErrors.Toast(ex));
                return;
            }
            ContainerActions.Post(post, () => deposit(uri, name));
        }
    }

    // ── Move to playlist ▸ (same picker, MOVE semantics: deposit into the target, then drop the source rows) ────────
    /// <summary>The menu equivalent of dragging these rows into another playlist — the a11y/Pragmatic answer to a drag
    /// (an outcome-equivalent command, never a simulated one). Offered only when the tracks sit in an editable playlist
    /// whose membership rows we can actually remove; otherwise there is nothing to move OUT of and Add already covers it.
    /// <para>It is honestly a COPY-then-REMOVE, not an atomic server move: Spotify exposes no cross-playlist move op.
    /// The order is deliberate — a failed add leaves the source untouched, whereas removing first can lose the rows.</para></summary>
    static MenuFlyoutItem? MoveToPlaylistItem(in ActionContext ctx)
    {
        var s = ctx.S;
        var tracks = ctx.Target.Tracks;
        if (s.Library is null || tracks.Count == 0) return null;
        if (!ActionRules.CanRemoveFromPlaylist(ctx.Target.Host) || ctx.Target.Host is not { } host) return null;

        // moving into the source playlist is a no-op → it is excluded from the list entirely.
        return PlaylistDepositItem(s, Loc.Get(Strings.Menu.MoveToPlaylist), Icons.Forward, canAdd: true,
            deposit: (uri, name) => MoveTo(s, uri, name, tracks, host),
            createAndDeposit: () => CreateAndMove(s, tracks, host),
            excludeUri: host.PlaylistUri, pickerTracks: tracks);
    }

    static void MoveTo(ActionServices s, string uri, string name, IReadOnlyList<Track> tracks, PlaylistHost host)
    {
        if (s.Library is not { } lib || tracks.Count == 0 || host.Rows.Count == 0) return;
        if (string.Equals(uri, host.PlaylistUri, StringComparison.Ordinal)) return;
        // Undo payload for the remove half (uri/title/uid per row), exactly as RemoveFromThisPlaylist records it.
        var refs = new ActivityTrackRef[tracks.Count];
        for (int i = 0; i < refs.Length; i++)
            refs[i] = new ActivityTrackRef(tracks[i].Uri, tracks[i].Title, tracks[i].ContextUid);
        string from = host.PlaylistUri;
        var rows = host.Rows;
        _ = Run();

        async Task Run()
        {
            try
            {
                await lib.AddTracksAsync(uri, tracks).ConfigureAwait(false);
                await lib.RemovePlaylistRowsAsync(from, rows, refs).ConfigureAwait(false);
                RememberDeposit(s, uri);
                Toast.Show(Strings.Menu.MovedToPlaylist(name), new ToastOptions
                {
                    Severity = InfoBarSeverity.Success,
                    ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => s.Go?.Invoke("pl:" + uri, name),
                });
            }
            // Mapped, not raw: PlaylistEditErrors turns "offline", "revision conflict" and the rest into a sentence a
            // listener can act on, where ex.Message is engine prose.
            catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
        }
    }

    static void CreateAndMove(ActionServices s, IReadOnlyList<Track> tracks, PlaylistHost host)
    {
        if (s.Library is not { } lib || tracks.Count == 0) return;
        string name = NextPlaylistName(s);
        _ = Run();
        async Task Run()
        {
            try
            {
                string uri = await lib.CreatePlaylistAsync(name).ConfigureAwait(false);
                MoveTo(s, uri, name, tracks, host);
            }
            catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
        }
    }

    /// <summary>Add to an existing playlist. AWAITED, then confirmed — the old shape fired the write and toasted
    /// "Added to X" unconditionally, so a failed add (offline, revoked permissions, a rejected revision) reported
    /// SUCCESS and the only trace was an entry flipped to Failed in a panel nobody was looking at. That is a trust bug in
    /// the middle of the flow this whole pass is about, so the correct shape (await → map the exception through
    /// PlaylistEditErrors) is now shared by every add path.</summary>
    static void AddTo(ActionServices s, string uri, string name, IReadOnlyList<Track> tracks)
    {
        if (s.Library is not { } lib || tracks.Count == 0) return;
        var post = s.Post;
        _ = Run();
        async Task Run()
        {
            try
            {
                long id = await lib.AddTracksTrackedAsync(uri, tracks).ConfigureAwait(false);
                ContainerActions.Post(post, () =>
                {
                    RememberDeposit(s, uri);
                    ToastDeposited(s, name, id);
                });
            }
            catch (Exception ex) { ContainerActions.Post(post, () => PlaylistEditErrors.Toast(ex)); }
        }
    }

    /// <summary>"New playlist" for tracks in hand: create with the next unused "<c>My Playlist #N</c>" name, add, then
    /// toast with <b>Open</b> — the one deposit where leaving IS what the user wants next (the new playlist needs a name,
    /// and inline rename lives on its page). Every "New playlist" used to create another playlist literally called
    /// "New playlist", so a few of them were indistinguishable in the sidebar.</summary>
    static void CreateAndAdd(ActionServices s, IReadOnlyList<Track> tracks)
    {
        if (s.Library is not { } lib || tracks.Count == 0) return;
        string name = NextPlaylistName(s);
        var post = s.Post;
        _ = Run();
        async Task Run()
        {
            try
            {
                string uri = await lib.CreatePlaylistAsync(name).ConfigureAwait(false);
                await lib.AddTracksAsync(uri, tracks).ConfigureAwait(false);
                ContainerActions.Post(post, () =>
                {
                    RememberDeposit(s, uri);
                    Toast.Show(Strings.Detail.AddedToPlaylist(name), new ToastOptions
                    {
                        Severity = InfoBarSeverity.Success,
                        ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => s.Go?.Invoke("pl:" + uri, name),
                    });
                });
            }
            catch (Exception ex) { ContainerActions.Post(post, () => PlaylistEditErrors.Toast(ex)); }
        }
    }

    /// <summary>The name a one-click "New playlist" gets: the next unused "<c>{localized base} #N</c>". Nothing computed
    /// this before — every new playlist was called "New playlist" verbatim.</summary>
    internal static string NextPlaylistName(ActionServices s)
        => PlaylistDepositTargets.NextDefaultName(s.Store?.Playlists.Value.Peek(), Loc.Get(Strings.Sidebar.NewPlaylist));

    /// <summary>"More playlists…" — the full existing PlaylistPickerPanel, hosted in a centered ContentDialog (the
    /// originating menu is gone by invoke time, so there is no anchor rect to open a flyout at).
    /// <para>A <paramref name="deposit"/> override carries its own payload (a container resolves its tracks at deposit
    /// time), so the empty-track guard applies only to the in-hand path.</para></summary>
    static void OpenPicker(ActionServices s, IReadOnlyList<Track> tracks,
                           string? title = null, Action<string, string>? deposit = null, string? exclude = null)
    {
        if (s.Overlay is not { } overlay || s.Library is null || (deposit is null && tracks.Count == 0)) return;
        OverlayHandle? handle = null;
        handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = title ?? Loc.Get(Strings.Detail.AddToPlaylist);
            d.PrimaryText = "";                                   // rows act; the dialog only needs a dismiss
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = Embed.Comp(() => new PlaylistPickerPanel
            {
                GetTracks = () => tracks,
                Close = () => handle?.Close(),
                Deposit = deposit,
                ExcludeUri = exclude,
            });
        });
    }

    // ── Containers ───────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A media-card menu inferred from the card's uri (cards carry only uri + title) — the CONTAINER grammar:
    /// Primary [Play · Play next · Play after · Save/Follow] + rows [Follow (artist), Add to playlist ▸, Open, Pin/Unpin,
    /// Go to artist (album), Share ▸, artist radio (artist)].
    ///
    /// <para>Two kinds omit part of the core, each for a reason recorded at the seam rather than by silence:
    /// an ARTIST has no resolvable track set (locked decision on <see cref="ContainerTracks"/>), so it gets neither the
    /// queue verbs nor Add-to-playlist; a SHOW has no <c>Track</c> at all, so its arm is Play · Open · Pin · Share.
    /// A track uri gets the thin track shape (<see cref="TrackUriCard"/>); an EPISODE uri gets no menu, because there is
    /// no episode detail route to Open and no episode-aware deposit seam. Unknown schemes get no menu.</para></summary>
    public static ContextMenuModel? Card(ActionServices s, string uri, string name,
        Image? image = null, string? subtitle = null, bool circular = false)
    {
        if (uri is not { Length: > 0 }) return null;
        if (uri.Contains(":track:", StringComparison.Ordinal)) return TrackUriCard(s, uri, name, image, subtitle);
        if (uri.Contains(":show:", StringComparison.Ordinal)) return ShowCard(s, uri, name, image, subtitle);

        bool liked = uri == "spotify:collection:tracks";
        ActionTarget target =
            uri.Contains(":album:", StringComparison.Ordinal) ? ActionTarget.ForAlbum(uri, name)
            : uri.Contains(":artist:", StringComparison.Ordinal) ? ActionTarget.ForArtist(uri, name)
            : uri.Contains(":playlist:", StringComparison.Ordinal) || liked ? ActionTarget.ForPlaylist(uri, name)
            : default;
        if (target.Kind == TargetKind.None) return null;

        var ctx = new ActionContext(target, s);
        return new ContextMenuModel(ContainerStrip(in ctx, liked), ContainerRows(in ctx),
            Header(image, uri, name, subtitle is { Length: > 0 } ? subtitle : KindLabel(target.Kind), circular));
    }

    /// <summary>The container transport strip: Play · Play next · Play after · Save. The queue pair is present exactly
    /// when the kind HAS a track set (album/playlist — see <see cref="ContainerTracks"/>); Liked Songs drops Save
    /// because it cannot be un-saved.</summary>
    static AppBarCommand[] ContainerStrip(in ActionContext ctx, bool liked)
    {
        var strip = new List<AppBarCommand>(4) { ContainerActions.PlayContext.ToBarCommand(ctx) };
        if (ContainerTracks.CanResolve(in ctx))
        {
            strip.Add(ContainerActions.PlayContextNext.ToBarCommand(ctx));
            strip.Add(ContainerActions.AddContextToQueue.ToBarCommand(ctx));
        }
        if (!liked) strip.Add(ContainerActions.SaveContext.ToBarCommand(ctx));
        return strip.ToArray();
    }

    /// <summary>The container rows, in grammar order: state → collection → navigation → Share → surface extras. Shared
    /// by the card menu and the sidebar album/artist arms, so the two can never drift.</summary>
    static List<MenuFlyoutItem> ContainerRows(in ActionContext ctx)
    {
        var rows = new List<MenuFlyoutItem>(7);
        // Follow / Unfollow as a ROW on artist menus (Spotify shows it as a row, not only the strip toggle).
        if (ctx.Target.Kind == TargetKind.Artist) rows.Add(ContainerActions.SaveContext.ToMenuItem(ctx));
        if (ContainerAddToPlaylistItem(in ctx) is { } add) rows.Add(add);
        rows.Add(ContainerActions.OpenItem.ToMenuItem(ctx));
        // Pin/Unpin immediately after Open (F.5.3 / §3.2.11) — the one documented deviation from "collection before
        // navigation": pin answers "where does this live", which reads as part of Open. A non-pinnable uri (a track
        // card) gets no row at all — decided in ONE place by SidebarPinId, never per menu.
        if (PinActions.Row(in ctx) is { } pinRow) rows.Add(pinRow);
        // The album card's Go-to-artist: the artist is resolved at invoke (the card carries no artists) — see the action.
        if (ctx.Target.Kind == TargetKind.Album) rows.Add(ContainerActions.GoToAlbumArtist.ToMenuItem(ctx));
        rows.Add(ShareItem(in ctx));
        if (ctx.Target.Kind == TargetKind.Artist) rows.Add(ContainerActions.GoToArtistRadio.ToMenuItem(ctx));
        return rows;
    }

    static string KindLabel(TargetKind kind) => kind switch
    {
        TargetKind.Album => "Album",
        TargetKind.Artist => "Artist",
        TargetKind.Playlist => "Playlist",
        _ => "",
    };

    /// <summary>A SHOW / podcast card: Play · Open · Pin · Share ▸. Built from explicit rows rather than a new
    /// <c>TargetKind.Show</c> — the same explicit non-goal <see cref="SidebarEntry"/> records; if that kind ever lands,
    /// this arm and the sidebar's migrate onto it together. No track-set verbs: a show's items are episodes, and
    /// <c>Wavee.Core</c> models an episode as its own record, not a <c>Track</c>.</summary>
    static ContextMenuModel ShowCard(ActionServices s, string uri, string name, Image? image, string? subtitle)
        => new(ShowRows(s, uri, name, "show:" + uri,
                        PinActions.RowForId(s, SidebarPinId.FromUri(uri), SidebarPinKind.Show, uri, name)),
               Header(image, uri, name, subtitle is { Length: > 0 } ? subtitle : "Podcast"));

    /// <summary>The show row list, shared by the card arm and the sidebar arm. <paramref name="pinRow"/> is passed in
    /// because the two surfaces know their pin id differently (a card derives it from the uri, a projected row already
    /// IS one) — same rule, same toasts, both through <see cref="PinActions"/>.</summary>
    static List<MenuFlyoutItem> ShowRows(ActionServices s, string uri, string name, string? route, MenuFlyoutItem? pinRow)
    {
        var rows = new List<MenuFlyoutItem>(4)
        {
            new(Loc.Get(Strings.Detail.Play), ActionIcons.Resolve(ActionIcons.Play),
                s.Svc?.Player is not null && uri.Length > 0, () => { _ = s.Svc?.Player.PlayAsync(uri); }),
            new(Loc.Get(Strings.Menu.Open), ActionIcons.Resolve(ActionIcons.Open),
                s.Go is not null && route is { Length: > 0 }, () => s.Go?.Invoke(route!, name)),
        };
        if (pinRow is { } pin) rows.Add(pin);
        rows.Add(ShareItem(new ActionContext(LinkTarget(uri, name), s)));
        return rows;
    }

    /// <summary>A SHARE-ONLY target: it carries the uri the Share submenu links and nothing else. <see cref="TargetKind.None"/>
    /// is deliberate — no verb is enabled by it, and the three Share actions read only <c>Uri</c>/<c>Tracks</c>
    /// (<see cref="SpotifyLink"/>). This is how a show gets the app-wide Share submenu without inventing the
    /// <c>TargetKind.Show</c> the sidebar design lists as an explicit non-goal.</summary>
    static ActionTarget LinkTarget(string uri, string name)
        => new() { Kind = TargetKind.None, Tracks = Array.Empty<Track>(), Uri = uri, Name = name };

    /// <summary>A card that is a bare track URI: the full track strip [Play · Play next · Play after · Save] + rows
    /// [Add to playlist ▸, Share ▸, Go to song radio].
    ///
    /// <para><b>Go to album / Go to artist are absent here, and that is a LAST RESORT, not the shape a song menu should
    /// have.</b> They need the track's album/artist URIs and this model carries a uri and a title. A surface that can
    /// resolve the real <see cref="Track"/> must therefore attach <see cref="TrackAttach"/> (the full track menu)
    /// instead of a card menu — which is exactly what the search results page does with the track already sitting in its
    /// own <c>SearchResults.Tracks</c>. Song radio survives because it seeds off the <b>uri alone</b>.</para></summary>
    static ContextMenuModel TrackUriCard(ActionServices s, string uri, string name, Image? image, string? subtitle)
    {
        var target = ActionTarget.ForTracks(new[]
        {
            new Track("", uri, name, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0L, false, null),
        });
        var ctx = new ActionContext(target, s);
        var rows = new List<MenuFlyoutItem>(3) { AddToPlaylistItem(in ctx), ShareItem(in ctx) };
        if (ActionRules.CanStartTrackRadio(in ctx.Target)) rows.Add(TrackActions.GoToSongRadio.ToMenuItem(ctx));
        return new ContextMenuModel(TrackTransportStrip(in ctx), rows,
            Header(image, uri, name, subtitle is { Length: > 0 } ? subtitle : "Song"));
    }

    /// <summary>The card attach helper for shared element factories: null when the action system isn't provided.</summary>
    public static MenuAttach? CardAttach(ActionServices? s, IOverlayService overlay, string uri, string name,
        Image? image = null, string? subtitle = null, bool circular = false)
        => s is null ? null : new MenuAttach(overlay, () => Card(s, uri, name, image, subtitle, circular));

    /// <summary>A single-track attach (eager rows that DO carry the full Track — search "Songs", fallbacks).</summary>
    public static MenuAttach? TrackAttach(ActionServices? s, IOverlayService overlay, Track track)
        => s is null ? null : new MenuAttach(overlay, () => TrackContextMenu.BuildSingle(s, track));

    // ── Sidebar playlist row (rows-only vertical menu) ───────────────────────────────────────────────────────────────
    /// <summary>Play · Open · — · <b>Pin to sidebar / Unpin from sidebar</b> · Rename (owner) · Visibility ▸ (owner, live) ·
    /// Invite collaborators (owner, live) · Share ▸ · — · Delete playlist (owner).
    ///
    /// <para>The pin row sits IMMEDIATELY AFTER the first separator, before any owner-gated management verb (F.5.3):
    /// pinning is available to every playlist regardless of ownership, so placing it inside the owner block would make it
    /// look like part of it. It is gated on nothing but the pin store's presence.</para></summary>
    public static ContextMenuModel SidebarPlaylist(ActionServices s, PlaylistSummary p)
        => new(SidebarPlaylistRows(s, p.Uri, p.Name, p.IsOwner, p.CanEdit),
            header: Header(p.Cover, p.Uri, p.Name, p.OwnerName is { Length: > 0 } ? p.OwnerName : "Playlist"));

    /// <summary>The playlist row list itself, shared by <see cref="SidebarPlaylist"/> and <see cref="SidebarEntry"/>'s
    /// playlist arm — one builder, so the V3/Curated row menu and the Classic sidebar row menu can never drift.</summary>
    static List<MenuFlyoutItem> SidebarPlaylistRows(ActionServices s, string uri, string name, bool isOwner, bool canEdit)
    {
        // Sidebar summaries carry only CanEdit/IsOwner — mapped onto the capabilities shape the actions gate on.
        var caps = new PlaylistCapabilities(
            CanView: true, CanEditItems: canEdit, CanEditMetadata: isOwner,
            IsCollaborative: canEdit && !isOwner, IsOwner: isOwner,
            CanAdministratePermissions: isOwner);
        var host = new PlaylistHost(uri, caps, Array.Empty<PlaylistRowRef>());
        var ctx = new ActionContext(ActionTarget.ForPlaylist(uri, name, host), s);

        bool live = PlaylistInlineEdit.SpotifyEditsLive(s.Svc);
        // Rows-only surface → the transport verbs are ROWS (the container grammar's core set: Play · Play next ·
        // Play after · Save). They were missing here entirely: a sidebar playlist could be played but never queued.
        var rows = new List<MenuFlyoutItem>(14)
        {
            ContainerActions.PlayContext.ToMenuItem(ctx),
        };
        if (ContainerTracks.CanResolve(in ctx))
        {
            rows.Add(ContainerActions.PlayContextNext.ToMenuItem(ctx));
            rows.Add(ContainerActions.AddContextToQueue.ToMenuItem(ctx));
        }
        // Liked Songs can't be un-saved (the card arm's rule, applied in both places).
        if (!string.Equals(uri, SidebarPinId.LikedSongsUri, StringComparison.Ordinal))
            rows.Add(ContainerActions.SaveContext.ToMenuItem(ctx));
        if (ContainerAddToPlaylistItem(in ctx) is { } add) rows.Add(add);
        rows.Add(ContainerActions.OpenItem.ToMenuItem(ctx));
        rows.Add(MenuFlyoutItem.Separator);
        if (PinActions.RowForId(s, SidebarPinId.Canonical(uri), SidebarPinKind.Playlist, uri, name) is { } pinRow)
            rows.Add(pinRow);
        if (isOwner)
            rows.Add(ContainerActions.RenamePlaylist.ToMenuItem(ctx));
        if (isOwner && live)
        {
            rows.Add(VisibilityItem(s, uri));
            rows.Add(ContainerActions.InviteCollaborators.ToMenuItem(ctx));
        }
        rows.Add(ShareItem(in ctx));
        if (isOwner)
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(ContainerActions.DeletePlaylist.ToMenuItem(ctx));
        }
        return rows;
    }

    // ── Sidebar projected row (Library V3 + Curated) ─────────────────────────────────────────────────────────────────
    /// <summary>The menu EVERY V3/Curated row uses — one builder with a per-kind arm (§3.2.11), because a single entry
    /// record already carries the kind and every fact each arm needs. Rows-only (no command strip: a 240-DIP pane is not
    /// where an Explorer-style labeled strip belongs), destructive-last, with the pin row in the same place every arm.
    ///
    /// <list type="bullet">
    /// <item><b>Playlist</b> — the full <see cref="SidebarPlaylist"/> row list (Play · Open · — · Pin · owner block ·
    /// Share ▸ · — · Delete), so the two surfaces cannot drift.</item>
    /// <item><b>Album / Artist</b> — the container card model's rows: Play · Save/Follow (artist also gets its radio) ·
    /// Open · Pin · Share ▸.</item>
    /// <item><b>Show / podcast</b> — Play · Open · Pin · — · Copy link, built from explicit rows rather than a new
    /// <c>TargetKind.Show</c>. <b>Explicit non-goal</b> (§3.2.11): adding <c>TargetKind.Show</c> /
    /// <c>ActionTarget.ForShow</c>; if that lands later this arm migrates onto it.</item>
    /// <item><b>Folder</b> — Expand/Collapse (label switches) · Pin. Spotify folder create/rename/move/delete is deferred
    /// (locked decision 9) and must not appear, not even disabled. Navbar Move up/down (a pin-list or authored-list
    /// reorder, not folder CRUD) arrive through <paramref name="layoutExtras"/>. A folder has no uri, so there is
    /// nothing to play and nothing to share.</item>
    /// <item><b>App route</b> — Open · Pin.</item>
    /// </list>
    ///
    /// <paramref name="toggleFolder"/> is the surface's own expansion closure (null ⇒ the folder arm omits the
    /// expand/collapse row rather than showing a dead one).
    ///
    /// <para><paramref name="layoutExtras"/> are the pane's navbar-customization verbs (Move up / Move down / Remove),
    /// the same extras slot the queue row uses. Null when the row has no order of its own (a projected library leaf)
    /// and is not a hand-placed item the document can drop. Inserted after Share and before any trailing destructive
    /// block (Delete playlist), so drag is never the only way to reorder (P6).</para></summary>
    public static ContextMenuModel? SidebarEntry(ActionServices s, in SidebarLibraryEntry e,
        Action? toggleFolder = null, bool folderExpanded = false,
        IReadOnlyList<MenuFlyoutItem>? layoutExtras = null)
    {
        ContextMenuModel? menu = e.Kind switch
        {
            SidebarEntryKind.Playlist => new ContextMenuModel(SidebarPlaylistRows(s, e.Uri, e.Name, e.IsOwner, e.CanEdit),
                header: Header(e.Cover, e.Uri, e.Name,
                    e.OwnerName is { Length: > 0 } owner ? owner : Loc.Get(Strings.Sidebar.V3.Kind.Playlist))),

            // The album/artist arms ARE the card menu — same target kinds, same verbs, same pin placement.
            SidebarEntryKind.Album or SidebarEntryKind.Artist => Card(s, e.Uri, e.Name, e.Cover,
                e.Creator is { Length: > 0 } ? e.Creator : null, e.Circular),

            SidebarEntryKind.Show => SidebarShowMenu(s, in e),
            SidebarEntryKind.Folder => SidebarFolderMenu(s, in e, toggleFolder, folderExpanded),
            SidebarEntryKind.AppRoute => SidebarRouteMenu(s, in e),
            SidebarEntryKind.Track => SidebarTrackMenu(s, in e),
            _ => null,
        };
        return WithLayoutExtras(menu, layoutExtras);
    }

    /// <summary>Append navbar-customization extras after the entity verbs and before a trailing destructive group
    /// (the playlist owner's Delete). A layout-only menu (an action shortcut, a hand-placed track) is just the extras.
    /// Null extras, or an empty list, leave the menu unchanged — including a null menu, which still opens nothing.</summary>
    public static ContextMenuModel? WithLayoutExtras(ContextMenuModel? menu, IReadOnlyList<MenuFlyoutItem>? extras)
    {
        if (extras is not { Count: > 0 }) return menu;
        if (menu is not { } present) return new ContextMenuModel(CopyExtras(extras));

        var rows = new List<MenuFlyoutItem>(present.Rows.Count + extras.Count + 1);
        for (int i = 0; i < present.Rows.Count; i++) rows.Add(present.Rows[i]);

        int at = rows.Count;
        // A trailing separator + one command is the destructive-last block (Delete playlist). Insert extras in front
        // of that separator so Move up/down never land after a delete, and Remove stays with the extras rather than
        // swapping places with an owner verb.
        if (rows.Count >= 2 && rows[^2].IsSeparator) at = rows.Count - 2;

        if (at > 0 && !rows[at - 1].IsSeparator) rows.Insert(at++, MenuFlyoutItem.Separator);
        for (int i = 0; i < extras.Count; i++) rows.Insert(at++, extras[i]);
        return present with { Rows = rows };
    }

    static List<MenuFlyoutItem> CopyExtras(IReadOnlyList<MenuFlyoutItem> extras)
    {
        var rows = new List<MenuFlyoutItem>(extras.Count);
        for (int i = 0; i < extras.Count; i++) rows.Add(extras[i]);
        return rows;
    }

    /// <summary>Feed TRACK rows — the only producers are the track-yielding data sources (<c>wavee.queue</c>,
    /// <c>wavee.nowPlaying</c>, <c>wavee.artist.topTracks</c>); <c>SidebarProjection</c> never emits one. The TRACK
    /// grammar as plain rows (a 240-DIP pane is not where an Explorer-style labeled strip belongs): Play · Play next ·
    /// Play after · Save · then <see cref="TrackRows"/>.
    ///
    /// <para>Two deliberate ABSENCES survive the convergence. No pin row: locked decision 4 keeps tracks unpinnable and
    /// <c>SidebarPinId.FromEntry</c> refuses them, so a row here would be a promise the store rejects. No Open row: a
    /// track has no detail route (<c>RouteKey</c> is null by construction). The old "no Play row either — activating the
    /// row plays it" argument did NOT survive: it left this the one track menu in the app with a different core set, and
    /// the click affordance is not a reason to withhold the verb (every other track surface is clickable too).</para>
    ///
    /// <para>Go-to-album/artist self-select: a projected row carries an artist NAME with no uri (see
    /// <see cref="TrackFromEntry"/>), and <see cref="ActionRules.CanGoToArtist"/> / the album-uri check drop those rows
    /// rather than navigating to an empty route.</para></summary>
    static ContextMenuModel SidebarTrackMenu(ActionServices s, in SidebarLibraryEntry e)
    {
        var ctx = new ActionContext(ActionTarget.ForTracks([TrackFromEntry(in e)]), s);
        var rows = new List<MenuFlyoutItem>(12);
        AddTrackTransportRows(rows, in ctx);
        rows.AddRange(TrackRows(in ctx));
        return new ContextMenuModel(rows, Header(e.Cover, e.Uri, e.Name,
            e.FirstArtistName is { Length: > 0 } artist ? artist : e.Creator is { Length: > 0 } ? e.Creator : null));
    }

    /// <summary>The minimal <see cref="Track"/> the queue verbs need from a projected row. <c>SidebarSourceMap.FromTrack</c>
    /// keeps only what a ROW draws, so the original Track is gone by the time a menu opens — this rebuilds exactly the
    /// fields <c>DetailQueueActions.BuildMetadata</c> puts on the wire (title · artist · image) and leaves the rest at
    /// their honest zero: duration 0 and a blank album ref, because a projected row genuinely does not know them, and
    /// <c>Availability</c> unknown, because only <c>getAlbum</c>/<c>getTrack</c> carry a server verdict and inventing one
    /// here would make "nobody told us" indistinguishable from "confirmed playable".</summary>
    static Track TrackFromEntry(in SidebarLibraryEntry e) => new(
        Id: e.Id, Uri: e.Uri, Title: e.Name,
        Artists: e.FirstArtistName is { Length: > 0 } artist
            ? [new ArtistRef("", "", artist)]
            : Array.Empty<ArtistRef>(),
        Album: new AlbumRef("", "", ""),
        DurationMs: 0, IsExplicit: false, Image: e.Cover);

    /// <summary>Show / podcast rows — the SAME list a podcast CARD gets (<see cref="ShowRows"/>), so the sidebar arm and
    /// the card arm cannot drift. Built explicitly (no <c>TargetKind.Show</c> — see <see cref="SidebarEntry"/>). The
    /// pin row comes from the entry (its <c>Id</c> already is the pin id) rather than from the uri.</summary>
    static ContextMenuModel SidebarShowMenu(ActionServices s, in SidebarLibraryEntry e)
    {
        var rows = ShowRows(s, e.Uri, e.Name, e.RouteKey, PinActions.RowForEntry(s, in e));
        return new ContextMenuModel(rows, Header(e.Cover, e.Uri, e.Name,
            e.Publisher is { Length: > 0 } publisher ? publisher : Loc.Get(Strings.Sidebar.V3.Kind.Show)));
    }

    /// <summary>Playlist-folder rows: expand/collapse + pin. NO folder CRUD (locked decision 9).</summary>
    static ContextMenuModel SidebarFolderMenu(ActionServices s, in SidebarLibraryEntry e, Action? toggle, bool expanded)
    {
        var rows = new List<MenuFlyoutItem>(2);
        if (toggle is not null)
            rows.Add(new MenuFlyoutItem(
                Loc.Get(expanded ? Strings.Sidebar.Item.CollapseFolder : Strings.Sidebar.Item.ExpandFolder),
                new IconRef { Glyph = expanded ? Icons.ChevronUp : Icons.ChevronDown, Font = Theme.IconFont },
                true, toggle));
        if (PinActions.RowForEntry(s, in e) is { } pinRow) rows.Add(pinRow);
        return new ContextMenuModel(rows, Header(null, e.Id, e.Name,
            Strings.Sidebar.V3.ItemCount(e.ChildCount)));
    }

    /// <summary>Pinned/static app-route rows: Open · Pin.</summary>
    static ContextMenuModel SidebarRouteMenu(ActionServices s, in SidebarLibraryEntry e)
    {
        string? route = e.RouteKey;
        string name = e.Name;
        var rows = new List<MenuFlyoutItem>(2)
        {
            new(Loc.Get(Strings.Menu.Open), ActionIcons.Resolve(ActionIcons.Open),
                s.Go is not null && route is { Length: > 0 }, () => s.Go?.Invoke(route!, name)),
        };
        if (PinActions.RowForEntry(s, in e) is { } pinRow) rows.Add(pinRow);
        return new ContextMenuModel(rows, Header(null, e.Id, name, null));
    }

    // (The local clipboard helper the show / sidebar-track arms used for their bare "Copy link" row is gone: both arms
    // now carry the app-wide Share ▸ submenu, which runs the TrackActions.CopyLink path — one clipboard behaviour.)

    // Explicit absolute-state rows (not a toggle): the sidebar summary carries no live IsPublic, and a mis-checked
    // toggle would invert the user's intent. Each row SETS the named state.
    static MenuFlyoutItem VisibilityItem(ActionServices s, string uri)
    {
        var items = new[]
        {
            new MenuFlyoutItem(Loc.Get(Strings.Menu.MakePublic), null, true, () => ContainerActions.SetVisibility(s, uri, true)),
            new MenuFlyoutItem(Loc.Get(Strings.Menu.MakePrivate), null, true, () => ContainerActions.SetVisibility(s, uri, false)),
        };
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Menu.Visibility), items, ActionIcons.Resolve(ActionIcons.Globe));
    }

    // ── Queue entry ─────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The TRACK grammar with the queue's own extras. Primary [Play now · Play next · Play after · Save];
    /// rows = <see cref="TrackRows"/> (Add to playlist ▸ · Go to album · Go to artist(s) · Share ▸ · credits · song
    /// radio) + the queue extras [Move up, Move down] + the destructive [Remove from queue].
    ///
    /// <para>Before the convergence this menu offered Go-to-album/artist and a bare Copy link and nothing else — no
    /// Add-to-playlist, no Share submenu, no credits. Only <b>Play now</b> is queue-specific (a skip-in-place, which is
    /// not what <c>Play</c> means anywhere else), so it replaces Play in the strip and the panel's closure drives it.</para>
    /// <paramref name="playNow"/> is the panel's skip-in-place; <paramref name="removeFromDisplay"/> the panel's
    /// remove closure (null when a remote viewer — the row is then absent).</summary>
    public static ContextMenuModel QueueEntry(ActionServices s, QueueEntry entry, Action? removeFromDisplay, Action playNow,
        Action? moveUp = null, Action? moveDown = null)
    {
        var ctx = new ActionContext(ActionTarget.ForQueueEntry(entry, removeFromDisplay), s);
        var primary = new[]
        {
            new AppBarCommand(Icons.Play, Loc.Get(Strings.Menu.PlayNow), playNow),
            TrackActions.PlayNext.ToBarCommand(ctx),
            TrackActions.AddToQueue.ToBarCommand(ctx),
            TrackActions.ToggleLike.ToBarCommand(ctx),
        };
        List<MenuFlyoutItem>? extras = null;
        if (moveUp is not null)
            (extras ??= new List<MenuFlyoutItem>(2)).Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveUp),
                new IconRef { Glyph = Icons.ChevronUp, Font = Theme.IconFont }, true, moveUp));
        if (moveDown is not null)
            (extras ??= new List<MenuFlyoutItem>(2)).Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveDown),
                new IconRef { Glyph = Icons.ChevronDown, Font = Theme.IconFont }, true, moveDown));
        // TrackRows adds the Remove-from-queue row itself for a QueueEntry target with a remove closure.
        var rows = TrackRows(in ctx, showGoToAlbum: true, extras);
        var t = entry.Track;
        string section = entry.Provider switch
        {
            QueueProvider.Queue => "Next in queue",
            QueueProvider.Autoplay => "Autoplay",
            _ => "Next up",
        };
        string artists = DetailFormat.ArtistNames(t.Artists);
        return new ContextMenuModel(primary, rows,
            Header(t.Image, t.Uri, t.Title,
                artists.Length > 0 ? $"{artists} · {section}" : section));
    }

    // ── Player-bar now playing ───────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The track menu for the now-playing cluster: Host = null → no Remove rows.</summary>
    public static ContextMenuModel NowPlaying(ActionServices s, Track track)
        => Tracks(new ActionContext(ActionTarget.ForNowPlaying(track), s));

    static ContextMenuHeader TrackHeader(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 1)
        {
            var track = tracks[0];
            string artists = DetailFormat.ArtistNames(track.Artists);
            string subtitle = track.Album is { Name.Length: > 0 } album && artists.Length > 0
                ? $"{artists} · {album.Name}"
                : artists;
            return Header(track.Image, track.Uri, track.Title, subtitle);
        }

        var first = tracks.Count > 0 ? tracks[0] : null;
        string summary = first is null ? "" : first.Title;
        if (tracks.Count > 1) summary += $"  +{tracks.Count - 1} more";
        return Header(first?.Image, first?.Uri ?? "", $"{tracks.Count} songs selected", summary);
    }

    /// <summary>The ONE menu-header constructor — and therefore the ONE place a header string is made safe.
    ///
    /// <para><b>Every entity subtitle in this app can be an HTML FRAGMENT.</b> The Spotify mappers build card/row
    /// subtitles as markup ("Song • &lt;a href=&quot;spotify:artist:…&quot;&gt;Name&lt;/a&gt;" —
    /// <c>SpotifyExportMapper.ArtistLinks</c>) because the ROW renderers parse them with <c>RichText</c> into
    /// individually clickable artist links. A menu header is a plain <c>TextEl</c>, so a producer that forwards the same
    /// string verbatim renders the raw tag at the user (the reported defect: a search song's menu header read
    /// <c>Song • &lt;a href="spotify:artist:2Q3eZMfD…</c>).</para>
    ///
    /// <para>Fixing it at each producer is a rule every future producer has to remember, so it is fixed HERE instead:
    /// the header strips tags to text with the shared <c>SpotifyExportMapper.ToPlainText</c> walk (the same one the home
    /// card path uses), which early-outs on the common no-markup string without allocating. A header needs NAMES, not
    /// links.</para></summary>
    static ContextMenuHeader Header(Image? image, string key, string title, string? subtitle, bool circular = false)
    {
        Element? leading = image is null ? null : Surfaces.Artwork(
            image, key.GetHashCode() & 0x7fffffff, 38f, 38f, circular ? 19f : 6f, decodePx: 76);
        return new ContextMenuHeader(leading, PlainHeaderText(title) ?? title, PlainHeaderText(subtitle));
    }

    /// <summary>Flatten a possibly-HTML header string: strip tags, THEN decode entities — the order
    /// <c>RichText</c> itself uses, and the order the flattener's own docs demand (decoding first would turn an escaped
    /// <c>&amp;lt;a&amp;gt;</c> into a live tag the stripper then eats). Decoding matters because the mappers
    /// <c>Esc()</c> the names they embed, so an un-decoded header would read "AC&amp;amp;DC".</summary>
    static string? PlainHeaderText(string? text)
        => SpotifyExportMapper.HtmlText(SpotifyExportMapper.ToPlainText(text));
}
