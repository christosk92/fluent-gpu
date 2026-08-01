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
public static class Menus
{
    const int MaxInlinePlaylists = 10;   // Add-to-playlist submenu cap; the rest via "More playlists…" → the picker

    // ── Track set (detail rows, batch bar, eager lists, queue, now-playing) ─────────────────────────────────────────
    /// <summary>The track(s) menu. Primary strip [Play · Play next · Add to queue · Like]; rows [Add to playlist ▸,
    /// Go to album (single), Go to artist(s) (single), View credits (single, primary-artist), Share ▸, — , Remove from
    /// this playlist (editable host) / Remove from queue (queue target)]. <paramref name="showGoToAlbum"/> is false on
    /// album detail pages (you are already there).</summary>
    public static ContextMenuModel Tracks(in ActionContext ctx, bool showGoToAlbum = true)
    {
        var primary = new[]
        {
            TrackActions.Play.ToBarCommand(ctx),
            TrackActions.PlayNext.ToBarCommand(ctx),
            TrackActions.AddToQueue.ToBarCommand(ctx),
            TrackActions.ToggleLike.ToBarCommand(ctx),
        };
        return new ContextMenuModel(primary, TrackRows(in ctx, showGoToAlbum), TrackHeader(ctx.Target.Tracks));
    }

    /// <summary>The track menu's vertical rows only (also the batch bar's overflow source).</summary>
    public static IReadOnlyList<MenuFlyoutItem> TrackRows(in ActionContext ctx, bool showGoToAlbum = true)
    {
        var rows = new List<MenuFlyoutItem>(8) { AddToPlaylistItem(in ctx) };
        // MOVE is offered only where a source to move OUT of exists (an editable playlist context) — everywhere else
        // "move" and "add" would be the same verb twice.
        if (MoveToPlaylistItem(in ctx) is { } move) rows.Add(move);

        if (ctx.Target.Single is { } single)
        {
            if (showGoToAlbum && single.Album is { Uri.Length: > 0 })
                rows.Add(TrackActions.GoToAlbum.ToMenuItem(ctx));
            if (single.Artists.Count == 1)
                rows.Add(TrackActions.GoToArtist.ToMenuItem(ctx));
            else if (single.Artists.Count > 1)
                rows.Add(GoToArtistsItem(ctx.S, single.Artists));
            if (ActionRules.CanViewCredits(in ctx.Target))
                rows.Add(TrackActions.ViewCredits.ToMenuItem(ctx));
            if (ActionRules.CanStartTrackRadio(in ctx.Target))
                rows.Add(TrackActions.GoToSongRadio.ToMenuItem(ctx));
            if (VideoItem(in ctx) is { } video)
                rows.Add(video);
        }

        rows.Add(ShareItem(in ctx));

        bool removeRow = TrackActions.RemoveFromThisPlaylist.EnabledFor(ctx);
        bool removeQueue = ctx.Target.Kind == TargetKind.QueueEntry;
        if (removeRow || removeQueue) rows.Add(MenuFlyoutItem.Separator);
        if (removeRow) rows.Add(TrackActions.RemoveFromThisPlaylist.ToMenuItem(ctx));
        if (removeQueue) rows.Add(TrackActions.RemoveFromQueue.ToMenuItem(ctx));
        return rows;
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

    /// <summary>Multi-artist track → a "Go to artists" cascade, one row per artist.</summary>
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

    // ── Add to playlist ▸ (New playlist + up to 10 editable playlists + "More playlists…" → the picker) ─────────────
    static MenuFlyoutItem AddToPlaylistItem(in ActionContext ctx)
    {
        var s = ctx.S;
        var tracks = ctx.Target.Tracks;
        bool canAdd = s.Library is not null && tracks.Count > 0;

        var items = new List<MenuFlyoutItem>(MaxInlinePlaylists + 3)
        {
            new(Loc.Get(Strings.Detail.NewPlaylist), Icons.Add, canAdd, () => CreateAndAdd(s, tracks)),
        };

        // The same filter as PlaylistPickerPanel: editable, real (spotify:playlist:*) playlists.
        s.Store?.EnsurePlaylists();
        var pls = s.Store?.Playlists.Value.Peek();
        if (pls is { Count: > 0 })
        {
            int shown = 0;
            for (int i = 0; i < pls.Count && shown < MaxInlinePlaylists; i++)
            {
                var p = pls[i];
                if (!p.CanEdit || !p.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) continue;
                var uri = p.Uri;
                var name = p.Name;
                items.Add(new MenuFlyoutItem(name, null, canAdd, () => AddTo(s, uri, name, tracks)));
                shown++;
            }
        }

        items.Add(MenuFlyoutItem.Separator);
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MorePlaylists), null,
            canAdd && s.Overlay is not null, () => OpenPicker(s, tracks)));

        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Detail.AddToPlaylist), items, Icons.Add, enabled: canAdd);
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
        string source = host.PlaylistUri;

        var items = new List<MenuFlyoutItem>(MaxInlinePlaylists + 3)
        {
            new(Loc.Get(Strings.Detail.NewPlaylist), Icons.Add, true, () => CreateAndMove(s, tracks, host)),
        };

        s.Store?.EnsurePlaylists();
        var pls = s.Store?.Playlists.Value.Peek();
        if (pls is { Count: > 0 })
        {
            int shown = 0;
            for (int i = 0; i < pls.Count && shown < MaxInlinePlaylists; i++)
            {
                var p = pls[i];
                if (!p.CanEdit || !p.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) continue;
                if (string.Equals(p.Uri, source, StringComparison.Ordinal)) continue;   // moving into itself is a no-op
                var uri = p.Uri;
                var name = p.Name;
                items.Add(new MenuFlyoutItem(name, null, true, () => MoveTo(s, uri, name, tracks, host)));
                shown++;
            }
        }

        items.Add(MenuFlyoutItem.Separator);
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MorePlaylists), null, s.Overlay is not null,
            () => OpenPicker(s, tracks, Loc.Get(Strings.Menu.MoveToPlaylist),
                             (uri, name) => MoveTo(s, uri, name, tracks, host), source)));

        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Menu.MoveToPlaylist), items, Icons.Forward);
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
                Toast.Show(Strings.Menu.MovedToPlaylist(name), new ToastOptions
                {
                    Severity = InfoBarSeverity.Success,
                    ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => s.Go?.Invoke("pl:" + uri, name),
                });
            }
            catch (Exception ex) { Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error }); }
        }
    }

    static void CreateAndMove(ActionServices s, IReadOnlyList<Track> tracks, PlaylistHost host)
    {
        if (s.Library is not { } lib || tracks.Count == 0) return;
        string name = Loc.Get(Strings.Detail.NewPlaylist);
        _ = Run();
        async Task Run()
        {
            try
            {
                string uri = await lib.CreatePlaylistAsync(name).ConfigureAwait(false);
                MoveTo(s, uri, name, tracks, host);
            }
            catch (Exception ex) { Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error }); }
        }
    }

    /// <summary>Add to an existing playlist — the PlaylistPickerPanel.AddTo behavior verbatim (fire the write, toast
    /// with a Go-to-playlist action; failures surface through the activity log / fail-loud mutation seam).</summary>
    static void AddTo(ActionServices s, string uri, string name, IReadOnlyList<Track> tracks)
    {
        if (s.Library is not { } lib || tracks.Count == 0) return;
        _ = lib.AddTracksAsync(uri, tracks);
        Toast.Show(Strings.Detail.AddedToPlaylist(name), new ToastOptions
        {
            Severity = InfoBarSeverity.Success,
            ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => s.Go?.Invoke("pl:" + uri, name),
        });
    }

    /// <summary>"New playlist" — the PlaylistPickerPanel.CreateAndAdd behavior verbatim.</summary>
    static void CreateAndAdd(ActionServices s, IReadOnlyList<Track> tracks)
    {
        if (s.Library is not { } lib || tracks.Count == 0) return;
        string name = Loc.Get(Strings.Detail.NewPlaylist);
        _ = Run();
        async Task Run()
        {
            try
            {
                string uri = await lib.CreatePlaylistAsync(name).ConfigureAwait(false);
                await lib.AddTracksAsync(uri, tracks).ConfigureAwait(false);
                Toast.Show(Strings.Detail.AddedToPlaylist(name), new ToastOptions
                {
                    Severity = InfoBarSeverity.Success,
                    ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist), OnAction = () => s.Go?.Invoke("pl:" + uri, name),
                });
            }
            catch (Exception ex) { Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error }); }
        }
    }

    /// <summary>"More playlists…" — the full existing PlaylistPickerPanel, hosted in a centered ContentDialog (the
    /// originating menu is gone by invoke time, so there is no anchor rect to open a flyout at).</summary>
    static void OpenPicker(ActionServices s, IReadOnlyList<Track> tracks,
                           string? title = null, Action<string, string>? deposit = null, string? exclude = null)
    {
        if (s.Overlay is not { } overlay || s.Library is null || tracks.Count == 0) return;
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
    /// <summary>A media-card menu inferred from the card's uri (cards carry only uri + title). Albums/artists/playlists
    /// get Primary [Play · Save/Follow] + rows [Follow/Unfollow (artist only), Open, Pin/Unpin, Share ▸]; a track uri gets
    /// the thin track shape (no Track object → no album/artist rows, and no pin row — tracks are never pinnable);
    /// unknown schemes get no menu.</summary>
    public static ContextMenuModel? Card(ActionServices s, string uri, string name,
        Image? image = null, string? subtitle = null, bool circular = false)
    {
        if (uri is not { Length: > 0 }) return null;
        if (uri.Contains(":track:", StringComparison.Ordinal)) return TrackUriCard(s, uri, name, image, subtitle);

        bool liked = uri == "spotify:collection:tracks";
        ActionTarget target =
            uri.Contains(":album:", StringComparison.Ordinal) ? ActionTarget.ForAlbum(uri, name)
            : uri.Contains(":artist:", StringComparison.Ordinal) ? ActionTarget.ForArtist(uri, name)
            : uri.Contains(":playlist:", StringComparison.Ordinal) || liked ? ActionTarget.ForPlaylist(uri, name)
            : default;
        if (target.Kind == TargetKind.None) return null;

        var ctx = new ActionContext(target, s);
        var primary = liked
            ? new[] { ContainerActions.PlayContext.ToBarCommand(ctx) }   // Liked Songs can't be un-saved
            : new[] { ContainerActions.PlayContext.ToBarCommand(ctx), ContainerActions.SaveContext.ToBarCommand(ctx) };
        var rows = new List<MenuFlyoutItem>(5);   // Follow · ArtistRadio · Open · Pin/Unpin · Share
        // Follow / Unfollow as a ROW on artist menus (Spotify shows it as a row, not only the strip toggle).
        if (target.Kind == TargetKind.Artist)
        {
            rows.Add(ContainerActions.SaveContext.ToMenuItem(ctx));
            rows.Add(ContainerActions.GoToArtistRadio.ToMenuItem(ctx));
        }
        rows.Add(ContainerActions.OpenItem.ToMenuItem(ctx));
        // Pin/Unpin immediately after Open (F.5.3 / §3.2.11). A non-pinnable uri (a track card) gets no row at all —
        // decided in ONE place by SidebarPinId, never per menu. Liked Songs inherits it for free through its own arm above.
        if (PinActions.Row(in ctx) is { } pinRow) rows.Add(pinRow);
        rows.Add(ShareItem(in ctx));
        string kind = target.Kind switch
        {
            TargetKind.Album => "Album",
            TargetKind.Artist => "Artist",
            TargetKind.Playlist => "Playlist",
            _ => "",
        };
        return new ContextMenuModel(primary, rows,
            Header(image, uri, name, subtitle is { Length: > 0 } ? subtitle : kind, circular));
    }

    /// <summary>A card that is a bare track URI (search top-hits): Play + Like + Copy link — no album/artist rows
    /// (the card model carries no Track).</summary>
    static ContextMenuModel TrackUriCard(ActionServices s, string uri, string name, Image? image, string? subtitle)
    {
        var target = ActionTarget.ForTracks(new[]
        {
            new Track("", uri, name, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0L, false, null),
        });
        var ctx = new ActionContext(target, s);
        var primary = new[]
        {
            TrackActions.Play.ToBarCommand(ctx),
            TrackActions.PlayNext.ToBarCommand(ctx),
            TrackActions.AddToQueue.ToBarCommand(ctx),
            TrackActions.ToggleLike.ToBarCommand(ctx),
        };
        return new ContextMenuModel(primary, new[]
        {
            AddToPlaylistItem(in ctx),
            ShareItem(in ctx),
        }, Header(image, uri, name, subtitle is { Length: > 0 } ? subtitle : "Song"));
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
        var rows = new List<MenuFlyoutItem>(10)
        {
            ContainerActions.PlayContext.ToMenuItem(ctx),
            ContainerActions.OpenItem.ToMenuItem(ctx),
            MenuFlyoutItem.Separator,
        };
        if (PinActions.Row(in ctx) is { } pinRow) rows.Add(pinRow);
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
    /// <item><b>Folder</b> — Expand/Collapse (label switches) · Pin, and NOTHING else. Spotify folder create/rename/move/
    /// delete is deferred (locked decision 9) and must not appear, not even disabled: a greyed-out "Delete folder" is a
    /// promise we are not keeping. A folder has no uri, so there is nothing to play and nothing to share.</item>
    /// <item><b>App route</b> — Open · Pin.</item>
    /// </list>
    ///
    /// <paramref name="toggleFolder"/> is the surface's own expansion closure (null ⇒ the folder arm omits the
    /// expand/collapse row rather than showing a dead one).</summary>
    public static ContextMenuModel? SidebarEntry(ActionServices s, in SidebarLibraryEntry e,
        Action? toggleFolder = null, bool folderExpanded = false)
    {
        switch (e.Kind)
        {
            case SidebarEntryKind.Playlist:
                return new ContextMenuModel(SidebarPlaylistRows(s, e.Uri, e.Name, e.IsOwner, e.CanEdit),
                    header: Header(e.Cover, e.Uri, e.Name,
                        e.OwnerName is { Length: > 0 } owner ? owner : Loc.Get(Strings.Sidebar.V3.Kind.Playlist)));

            case SidebarEntryKind.Album:
            case SidebarEntryKind.Artist:
                // The album/artist arms ARE the card menu — same target kinds, same verbs, same pin placement.
                return Card(s, e.Uri, e.Name, e.Cover,
                    e.Creator is { Length: > 0 } ? e.Creator : null, e.Circular);

            case SidebarEntryKind.Show:
                return SidebarShowMenu(s, in e);

            case SidebarEntryKind.Folder:
                return SidebarFolderMenu(s, in e, toggleFolder, folderExpanded);

            case SidebarEntryKind.AppRoute:
                return SidebarRouteMenu(s, in e);

            case SidebarEntryKind.Track:
                return SidebarTrackMenu(s, in e);

            default:
                return null;
        }
    }

    /// <summary>Feed TRACK rows — the only producers are the track-yielding data sources (<c>wavee.queue</c>,
    /// <c>wavee.nowPlaying</c>, <c>wavee.artist.topTracks</c>); <c>SidebarProjection</c> never emits one. Play next ·
    /// Add to queue · — · Copy link.
    ///
    /// <para>Three deliberate ABSENCES. No pin row: locked decision 4 keeps tracks unpinnable and
    /// <c>SidebarPinId.FromEntry</c> refuses them, so a row here would be a promise the store rejects. No Open row: a
    /// track has no detail route (<c>RouteKey</c> is null by construction). No Play row: activating the row already
    /// plays it, and the queue verbs are the two things a click cannot do.</para></summary>
    static ContextMenuModel SidebarTrackMenu(ActionServices s, in SidebarLibraryEntry e)
    {
        var ctx = new ActionContext(ActionTarget.ForTracks([TrackFromEntry(in e)]), s);
        var rows = new List<MenuFlyoutItem>(4)
        {
            TrackActions.PlayNext.ToMenuItem(ctx),
            TrackActions.AddToQueue.ToMenuItem(ctx),
        };
        if (SpotifyLink.WebUrl(e.Uri) is { } url)
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.CopyLink), ActionIcons.Resolve(ActionIcons.Link),
                s.Clipboard is not null, () => CopyText(s, url, Strings.Menu.LinkCopied)));
        }
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

    /// <summary>Show / podcast rows. Built explicitly (no <c>TargetKind.Show</c> — see <see cref="SidebarEntry"/>).</summary>
    static ContextMenuModel SidebarShowMenu(ActionServices s, in SidebarLibraryEntry e)
    {
        string uri = e.Uri;
        string name = e.Name;
        string? route = e.RouteKey;
        var rows = new List<MenuFlyoutItem>(5)
        {
            new(Loc.Get(Strings.Detail.Play), ActionIcons.Resolve(ActionIcons.Play),
                s.Svc?.Player is not null && uri.Length > 0, () => { _ = s.Svc?.Player.PlayAsync(uri); }),
            new(Loc.Get(Strings.Menu.Open), ActionIcons.Resolve(ActionIcons.Open),
                s.Go is not null && route is { Length: > 0 }, () => s.Go?.Invoke(route!, name)),
        };
        if (PinActions.RowForEntry(s, in e) is { } pinRow) rows.Add(pinRow);
        if (SpotifyLink.WebUrl(uri) is { } url)
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.CopyLink), ActionIcons.Resolve(ActionIcons.Link),
                s.Clipboard is not null, () => CopyText(s, url, Strings.Menu.LinkCopied)));
        }
        return new ContextMenuModel(rows, Header(e.Cover, uri, name,
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

    /// <summary>Clipboard write + the shared "copied" toast/announcement (the <c>TrackActions.CopyLink</c> path, reused
    /// so the show arm cannot invent a second clipboard behaviour).</summary>
    static void CopyText(ActionServices s, string text, string toastLocKey)
    {
        if (s.Clipboard is not { } clip) return;
        try { clip.SetText(text); }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); return; }
        InputHooks.Current.Default.Announce?.Invoke(Loc.Get(Strings.Auth.Copied), false);
        Toast.Show(Loc.Get(toastLocKey), new ToastOptions { Severity = InfoBarSeverity.Success });
    }

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
    /// <summary>Primary [Play now · Like] + rows [Go to album, Go to artist(s), Copy link, — , Remove from queue].
    /// <paramref name="playNow"/> is the panel's skip-in-place; <paramref name="removeFromDisplay"/> the panel's
    /// remove closure (null when a remote viewer — the row renders disabled).</summary>
    public static ContextMenuModel QueueEntry(ActionServices s, QueueEntry entry, Action? removeFromDisplay, Action playNow,
        Action? moveUp = null, Action? moveDown = null)
    {
        var ctx = new ActionContext(ActionTarget.ForQueueEntry(entry, removeFromDisplay), s);
        var primary = new[]
        {
            new AppBarCommand(Icons.Play, Loc.Get(Strings.Menu.PlayNow), playNow),
            TrackActions.ToggleLike.ToBarCommand(ctx),
        };
        var rows = new List<MenuFlyoutItem>(9);
        var t = entry.Track;
        if (t.Album is { Uri.Length: > 0 })
            rows.Add(TrackActions.GoToAlbum.ToMenuItem(ctx));
        if (t.Artists.Count == 1)
            rows.Add(TrackActions.GoToArtist.ToMenuItem(ctx));
        else if (t.Artists.Count > 1)
            rows.Add(GoToArtistsItem(s, t.Artists));
        rows.Add(TrackActions.CopyLink.ToMenuItem(ctx));
        if (moveUp is not null || moveDown is not null)
        {
            rows.Add(MenuFlyoutItem.Separator);
            if (moveUp is not null)
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveUp),
                    new IconRef { Glyph = Icons.ChevronUp, Font = Theme.IconFont }, true, moveUp));
            if (moveDown is not null)
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveDown),
                    new IconRef { Glyph = Icons.ChevronDown, Font = Theme.IconFont }, true, moveDown));
        }
        if (removeFromDisplay is not null)
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(TrackActions.RemoveFromQueue.ToMenuItem(ctx));
        }
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

    static ContextMenuHeader Header(Image? image, string key, string title, string? subtitle, bool circular = false)
    {
        Element? leading = image is null ? null : Surfaces.Artwork(
            image, key.GetHashCode() & 0x7fffffff, 38f, 38f, circular ? 19f : 6f, decodePx: 76);
        return new ContextMenuHeader(leading, title, subtitle);
    }
}
