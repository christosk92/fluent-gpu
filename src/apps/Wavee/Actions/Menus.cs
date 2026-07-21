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
        return new ContextMenuModel(primary, TrackRows(in ctx, showGoToAlbum));
    }

    /// <summary>The track menu's vertical rows only (also the batch bar's overflow source).</summary>
    public static IReadOnlyList<MenuFlyoutItem> TrackRows(in ActionContext ctx, bool showGoToAlbum = true)
    {
        var rows = new List<MenuFlyoutItem>(8) { AddToPlaylistItem(in ctx) };

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
    static void OpenPicker(ActionServices s, IReadOnlyList<Track> tracks)
    {
        if (s.Overlay is not { } overlay || s.Library is null || tracks.Count == 0) return;
        OverlayHandle? handle = null;
        handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Detail.AddToPlaylist);
            d.PrimaryText = "";                                   // rows act; the dialog only needs a dismiss
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = Embed.Comp(() => new PlaylistPickerPanel
            {
                GetTracks = () => tracks,
                Close = () => handle?.Close(),
            });
        });
    }

    // ── Containers ───────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A media-card menu inferred from the card's uri (cards carry only uri + title). Albums/artists/playlists
    /// get Primary [Play · Save/Follow] + rows [Follow/Unfollow (artist only), Open, Share ▸]; a track uri gets the thin
    /// track shape (no Track object → no album/artist rows); unknown schemes get no menu.</summary>
    public static ContextMenuModel? Card(ActionServices s, string uri, string name)
    {
        if (uri is not { Length: > 0 }) return null;
        if (uri.Contains(":track:", StringComparison.Ordinal)) return TrackUriCard(s, uri, name);

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
        var rows = new List<MenuFlyoutItem>(3);
        // Follow / Unfollow as a ROW on artist menus (Spotify shows it as a row, not only the strip toggle).
        if (target.Kind == TargetKind.Artist)
        {
            rows.Add(ContainerActions.SaveContext.ToMenuItem(ctx));
            rows.Add(ContainerActions.GoToArtistRadio.ToMenuItem(ctx));
        }
        rows.Add(ContainerActions.OpenItem.ToMenuItem(ctx));
        rows.Add(ShareItem(in ctx));
        return new ContextMenuModel(primary, rows);
    }

    /// <summary>A card that is a bare track URI (search top-hits): Play + Like + Copy link — no album/artist rows
    /// (the card model carries no Track).</summary>
    static ContextMenuModel TrackUriCard(ActionServices s, string uri, string name)
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
        });
    }

    /// <summary>The card attach helper for shared element factories: null when the action system isn't provided.</summary>
    public static MenuAttach? CardAttach(ActionServices? s, IOverlayService overlay, string uri, string name)
        => s is null ? null : new MenuAttach(overlay, () => Card(s, uri, name));

    /// <summary>A single-track attach (eager rows that DO carry the full Track — search "Songs", fallbacks).</summary>
    public static MenuAttach? TrackAttach(ActionServices? s, IOverlayService overlay, Track track)
        => s is null ? null : new MenuAttach(overlay, () => TrackContextMenu.BuildSingle(s, track));

    // ── Sidebar playlist row (rows-only vertical menu) ───────────────────────────────────────────────────────────────
    /// <summary>Play · Open · — · Rename (owner) · Visibility ▸ (owner, live) · Invite collaborators (owner, live) ·
    /// Share ▸ · — · Delete playlist (owner). Pin is reserved (no pin store yet).</summary>
    public static ContextMenuModel SidebarPlaylist(ActionServices s, PlaylistSummary p)
    {
        // Sidebar summaries carry only CanEdit/IsOwner — mapped onto the capabilities shape the actions gate on.
        var caps = new PlaylistCapabilities(
            CanView: true, CanEditItems: p.CanEdit, CanEditMetadata: p.IsOwner,
            IsCollaborative: p.CanEdit && !p.IsOwner, IsOwner: p.IsOwner,
            CanAdministratePermissions: p.IsOwner);
        var host = new PlaylistHost(p.Uri, caps, Array.Empty<PlaylistRowRef>());
        var ctx = new ActionContext(ActionTarget.ForPlaylist(p.Uri, p.Name, host), s);

        bool live = PlaylistInlineEdit.SpotifyEditsLive(s.Svc);
        var rows = new List<MenuFlyoutItem>(9)
        {
            ContainerActions.PlayContext.ToMenuItem(ctx),
            ContainerActions.OpenItem.ToMenuItem(ctx),
            MenuFlyoutItem.Separator,
        };
        if (p.IsOwner)
            rows.Add(ContainerActions.RenamePlaylist.ToMenuItem(ctx));
        if (p.IsOwner && live)
        {
            rows.Add(VisibilityItem(s, p.Uri));
            rows.Add(ContainerActions.InviteCollaborators.ToMenuItem(ctx));
        }
        rows.Add(ShareItem(in ctx));
        if (p.IsOwner)
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(ContainerActions.DeletePlaylist.ToMenuItem(ctx));
        }
        return new ContextMenuModel(rows);
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
        rows.Add(MenuFlyoutItem.Separator);
        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveUp),
            new IconRef { Glyph = Icons.ChevronUp, Font = Theme.IconFont }, moveUp is not null, moveUp ?? (() => { })));
        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveDown),
            new IconRef { Glyph = Icons.ChevronDown, Font = Theme.IconFont }, moveDown is not null, moveDown ?? (() => { })));
        rows.Add(MenuFlyoutItem.Separator);
        rows.Add(TrackActions.RemoveFromQueue.ToMenuItem(ctx));
        return new ContextMenuModel(primary, rows);
    }

    // ── Player-bar now playing ───────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The track menu for the now-playing cluster: Host = null → no Remove rows.</summary>
    public static ContextMenuModel NowPlaying(ActionServices s, Track track)
        => Tracks(new ActionContext(ActionTarget.ForNowPlaying(track), s));
}
