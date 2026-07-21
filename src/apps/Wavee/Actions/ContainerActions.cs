using System;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

// Container (album / artist / playlist / sidebar-playlist) action singletons. Owner-only playlist management reuses the
// existing flows verbatim: SettingsShared.Confirm for delete, LibraryBridge.UpdatePlaylistDetailsAsync(previousName:)
// for the undo-able rename, PlaylistInlineEdit.CopyContributorInviteAsync for invites.
public static class ContainerActions
{
    public static readonly AppAction PlayContext = new()
    {
        Id = ActionId.PlayContext, IconKey = ActionIcons.Play,
        Label = static c => Loc.Get(Strings.Detail.Play),
        IsEnabled = static c => c.S.Svc is not null && c.Target.Uri.Length > 0,
        Execute = static c =>
        {
            var p = c.S.Svc?.Player;
            if (p is null || c.Target.Uri.Length == 0) return;
            _ = p.PlayAsync(c.Target.Uri);
        },
    };

    /// <summary>Save album · follow artist · save playlist — one toggle over the saved-set (LibraryBridge).</summary>
    public static readonly AppAction SaveContext = new()
    {
        Id = ActionId.SaveContext, IconKey = ActionIcons.Heart,
        IsChecked = static c => c.S.Library?.IsSaved(c.Target.Uri) ?? false,
        // Short strip-friendly verbs (Explorer labeled-strip columns are one word — the equal-width columns ellipsize
        // "Save to Your Library"). Album/playlist: saved-state-aware Save/Saved (matches TrackActions.ToggleLike);
        // artist: Follow/Following.
        Label = static c =>
        {
            bool saved = c.S.Library?.IsSaved(c.Target.Uri) ?? false;
            return c.Target.Kind == TargetKind.Artist
                ? Loc.Get(saved ? Strings.Artist.Following : Strings.Artist.Follow)
                : Loc.Get(saved ? Strings.Menu.Saved : Strings.Menu.Save);
        },
        IsEnabled = static c => c.S.Library is not null && c.Target.Uri.Length > 0,
        Execute = static c => c.S.Library?.ToggleSaved(c.Target.Uri, c.Target.Name),
    };

    /// <summary>Start an artist radio (Apple-Music-style): resolves the artist seed → a radio playlist, parks it after
    /// the current track (never interrupting playback), and raises the "Radio started → Open playlist" toast. Artist
    /// target with a player present.</summary>
    public static readonly AppAction GoToArtistRadio = new()
    {
        Id = ActionId.GoToArtistRadio, IconKey = ActionIcons.Radio,
        Label = static c => Loc.Get(Strings.Menu.GoToArtistRadio),
        IsEnabled = static c => c.S.Svc is not null && ActionRules.CanStartArtistRadio(in c.Target),
        Execute = static c => RadioLaunch.Start(c.S.Svc?.Player, c.Target.Uri, c.Target.Name, c.S.Go),
    };

    /// <summary>Open the target's detail page (the card/sidebar "Open" row).</summary>
    public static readonly AppAction OpenItem = new()
    {
        Id = ActionId.OpenItem, IconKey = ActionIcons.Open,
        Label = static c => Loc.Get(Strings.Menu.Open),
        IsEnabled = static c => c.S.Go is not null && ActionRules.RouteFor(in c.Target) is not null,
        Execute = static c =>
        {
            if (ActionRules.RouteFor(in c.Target) is { } route)
                c.S.Go?.Invoke(route, c.Target.Name);
        },
    };

    /// <summary>Rename (owner): a small ContentDialog with a name field → the undo-able
    /// <c>UpdatePlaylistDetailsAsync(previousName:)</c> rename.</summary>
    public static readonly AppAction RenamePlaylist = new()
    {
        Id = ActionId.RenamePlaylist, IconKey = ActionIcons.Rename,
        Label = static c => Loc.Get(Strings.Menu.RenamePlaylist),
        IsEnabled = static c => c.S.Library is not null && c.S.Overlay is not null
                                && c.Target.Host is { Caps.CanEditMetadata: true },
        Execute = static c =>
        {
            if (c.S.Library is not { } lib || c.S.Overlay is not { } overlay) return;
            string uri = c.Target.Uri;
            string current = c.Target.Name;
            var text = new Signal<string>(current);
            ContentDialog.Show(overlay, d =>
            {
                d.Title = Loc.Get(Strings.Menu.RenamePlaylist);
                d.PrimaryText = Loc.Get(Strings.Menu.Rename);
                d.CloseText = Loc.Get(Strings.Auth.Cancel);
                d.DefaultButton = ContentDialog.DefaultBtn.Primary;
                d.Content = new BoxEl
                {
                    Direction = 1, MinWidth = 320f,
                    Children = [Embed.Comp(() => new EditableText { Text = text, Width = 320f, Height = 32f })],
                };
                d.PrimaryClick = () =>
                {
                    string next = text.Peek().Trim();
                    if (next.Length == 0 || string.Equals(next, current, StringComparison.Ordinal)) return;
                    _ = RunRename(lib, uri, next, current);
                };
            });
        },
    };

    static async Task RunRename(LibraryBridge lib, string uri, string next, string previous)
    {
        try { await lib.UpdatePlaylistDetailsAsync(uri, next, null, null, previousName: previous).ConfigureAwait(false); }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
    }

    /// <summary>Copy a contributor-invite link to the clipboard (owner, live Spotify session only) — reuses
    /// <see cref="PlaylistInlineEdit.CopyContributorInviteAsync"/> verbatim (a minimal DetailModel carries the uri;
    /// the web-url fallback inside resolves the share link).</summary>
    public static readonly AppAction InviteCollaborators = new()
    {
        Id = ActionId.InviteCollaborators, IconKey = ActionIcons.People,
        Label = static c => Loc.Get(Strings.Detail.Edit.InviteCollaborators),
        IsEnabled = static c => c.S.Library is not null
                                && c.Target.Host is { Caps.IsOwner: true }
                                && PlaylistInlineEdit.SpotifyEditsLive(c.S.Svc),
        Execute = static c =>
        {
            if (c.S.Library is not { } lib || c.Target.Uri.Length == 0) return;
            _ = RunInvite(lib, c.Target.Uri);
        },
    };

    static async Task RunInvite(LibraryBridge lib, string uri)
    {
        if (await PlaylistInlineEdit.CopyContributorInviteAsync(lib, DetailModel.Empty with { ContextUri = uri }).ConfigureAwait(false))
            Toast.Show(Loc.Get(Strings.Menu.LinkCopied), new ToastOptions { Severity = InfoBarSeverity.Success });
    }

    /// <summary>Delete playlist (owner) — destructive, behind the existing confirm dialog
    /// (<see cref="SettingsShared.Confirm"/>, the OwnerOverflowMenu precedent).</summary>
    public static readonly AppAction DeletePlaylist = new()
    {
        Id = ActionId.DeletePlaylist, IconKey = ActionIcons.Delete, Destructive = true,
        // Overlay required: Confirm(null overlay) would run the delete WITHOUT confirmation — never allow that path.
        IsEnabled = static c => c.S.Library is not null && c.S.Overlay is not null
                                && c.Target.Host is { Caps.IsOwner: true },
        Label = static c => Loc.Get(Strings.Detail.Edit.DeletePlaylist),
        Execute = static c =>
        {
            if (c.S.Library is not { } lib || c.S.Overlay is not { } overlay || c.Target.Uri.Length == 0) return;
            string uri = c.Target.Uri;
            SettingsShared.Confirm(overlay,
                Loc.Get(Strings.Detail.Edit.DeletePlaylist),
                Loc.Get(Strings.Detail.Edit.DeletePlaylistConfirm),
                Loc.Get(Strings.Detail.Edit.DeletePlaylist),
                () => _ = RunDelete(lib, uri));
        },
    };

    static async Task RunDelete(LibraryBridge lib, string uri)
    {
        try { await lib.DeletePlaylistAsync(uri).ConfigureAwait(false); }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
    }

    /// <summary>Set playlist visibility to an ABSOLUTE state (public/private). Explicit rows instead of a toggle: the
    /// sidebar summary carries no live IsPublic, and a mis-checked toggle would invert the user's intent.</summary>
    internal static void SetVisibility(ActionServices s, string uri, bool isPublic)
    {
        if (s.Library is not { } lib) return;
        _ = Run();
        async Task Run()
        {
            try { await lib.SetPlaylistVisibilityAsync(uri, isPublic).ConfigureAwait(false); }
            catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
        }
    }
}
