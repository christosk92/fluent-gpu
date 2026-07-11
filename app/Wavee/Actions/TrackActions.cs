using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

// The track-set action singletons (see AppAction for the model). Decision logic lives in ActionRules/SpotifyLink (pure,
// unit-tested engine-free); these adapt it onto the live services. Queue verbs are deliberately NOT gated on an active
// device: with no remote device the player fires the standard "choose a remote device" prompt and DetailQueueActions
// returns 0 → no success toast (prompt-to-fix, matching the batch bar's existing behavior).
public static class TrackActions
{
    public static readonly AppAction Play = new()
    {
        Id = ActionId.Play, IconKey = ActionIcons.Play,
        Label = static c => Loc.Get(Strings.Detail.Play),
        IsEnabled = static c => c.S.Svc is not null && c.Target.Count > 0,
        Execute = static c =>
        {
            var s = c.Target.Tracks;
            var p = c.S.Svc?.Player;
            if (p is null || s.Count == 0) return;
            _ = p.PlayTrackAsync(s[0]);
            for (int i = 1; i < s.Count; i++) _ = p.EnqueueAsync(s[i]);
        },
    };

    public static readonly AppAction PlayNext = new()
    {
        Id = ActionId.PlayNext, IconKey = ActionIcons.PlayNext,
        Label = static c => c.Target.Count > 1 ? Strings.Menu.PlayNextN(c.Target.Count) : Loc.Get(Strings.Detail.PlayNext),
        IsEnabled = static c => c.S.Svc is not null && c.Target.Count > 0,
        Execute = static c =>
        {
            int n = DetailQueueActions.PlayNext(c.S.Svc?.Player, c.Target.Tracks, c.Target.Count);
            if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
        },
    };

    public static readonly AppAction AddToQueue = new()
    {
        Id = ActionId.AddToQueue, IconKey = ActionIcons.Queue,
        // Language parity with the detail rail's "Play after" dropdown (same verb + same custom icon, app-wide).
        Label = static c => c.Target.Count > 1 ? Strings.Menu.AddToQueueN(c.Target.Count) : Loc.Get(Strings.Detail.PlayAfter),
        IsEnabled = static c => c.S.Svc is not null && c.Target.Count > 0,
        Execute = static c =>
        {
            int n = DetailQueueActions.AddToEnd(c.S.Svc?.Player, c.Target.Tracks, c.Target.Count);
            if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
        },
    };

    public static readonly AppAction ToggleLike = new()
    {
        Id = ActionId.ToggleLike, IconKey = ActionIcons.Heart,
        // Checked visual in the labeled context-menu strip: the two-tone accent-filled heart (no pill) — the player-bar
        // Like convention (PlayerBar.cs: liked ? Mdl.HeartFill : Icons.Heart + accent color). Glyph fallback pre-registry.
        CheckedIcon = IconRef.Themed("HeartFill", Mdl.HeartFill),
        // Multi: checked iff ALL saved; Execute then saves the rest (or unsaves all when everything was saved).
        IsChecked = static c => AllSaved(in c),
        // Short strip-friendly verb (user: Explorer strip labels are one word; "Save"/"Saved" is Spotify's compact form —
        // the equal-width strip columns would ellipsize "Save to Liked Songs").
        Label = static c => AllSaved(in c) ? Loc.Get(Strings.Menu.Saved) : Loc.Get(Strings.Menu.Save),
        IsEnabled = static c => c.S.Library is not null && c.Target.Count > 0,
        Execute = static c =>
        {
            var lib = c.S.Library;
            if (lib is null) return;
            bool save = !AllSaved(in c);
            foreach (var t in c.Target.Tracks)
                if (t.Uri.Length > 0) lib.SetSaved(t.Uri, save, t.Title);
        },
    };

    static bool AllSaved(in ActionContext c)
        => c.S.Library is { } lib && ActionRules.AllSaved(c.Target.Tracks, lib.IsSaved);

    /// <summary>Copy link(s) — also the container card/sidebar copy action (falls back to the container uri when the
    /// target carries no tracks). Multi → newline-joined.</summary>
    public static readonly AppAction CopyLink = new()
    {
        Id = ActionId.CopyLink, IconKey = ActionIcons.Link,
        Label = static c => c.Target.Count > 1 ? Strings.Menu.CopyLinks(c.Target.Count) : Loc.Get(Strings.Menu.CopyLink),
        IsEnabled = static c => c.S.Clipboard is not null && SpotifyLink.HasLink(in c.Target),
        Execute = static c =>
        {
            if (c.S.Clipboard is not { } clip || SpotifyLink.LinkText(in c.Target) is not { } text) return;
            try { clip.SetText(text); }
            catch (Exception ex) { PlaylistEditErrors.Toast(ex); return; }   // the PlaylistInlineEdit.Share precedent
            InputHooks.Current.Default.Announce?.Invoke(Loc.Get(Strings.Auth.Copied), false);
            Toasts.Show(Loc.Get(Strings.Menu.LinkCopied), ToastSeverity.Success);
        },
    };

    public static readonly AppAction GoToAlbum = new()
    {
        Id = ActionId.GoToAlbum, IconKey = ActionIcons.Album,
        Label = static c => Loc.Get(Strings.Menu.GoToAlbum),
        IsEnabled = static c => c.S.Go is not null && c.Target.Single is { Album.Uri.Length: > 0 },
        Execute = static c =>
        {
            if (c.Target.Single is not { Album: { Uri.Length: > 0 } album }) return;
            c.S.Go?.Invoke("album:" + album.Uri, album.Name);
        },
    };

    /// <summary>Single-artist direct navigation; multi-artist tracks get a submenu instead (Menus.Tracks).</summary>
    public static readonly AppAction GoToArtist = new()
    {
        Id = ActionId.GoToArtist, IconKey = ActionIcons.Artist,
        Label = static c => Loc.Get(Strings.Detail.GoToArtist),
        IsEnabled = static c => c.S.Go is not null && c.Target.Single is { Artists.Count: > 0 },
        Execute = static c =>
        {
            if (c.Target.Single is not { Artists.Count: > 0 } t) return;
            var a = t.Artists[0];
            c.S.Go?.Invoke("artist:" + a.Uri, a.Name);
        },
    };

    /// <summary>Start a song radio seeded by a single track (Apple-Music-style): resolves the seed → a radio playlist,
    /// parks it after the current track (never interrupting playback), and raises the "Radio started → Open playlist"
    /// toast. Single <c>spotify:track:*</c> target with a player present.</summary>
    public static readonly AppAction GoToSongRadio = new()
    {
        Id = ActionId.GoToSongRadio, IconKey = ActionIcons.Radio,
        Label = static c => Loc.Get(Strings.Menu.GoToSongRadio),
        IsEnabled = static c => c.S.Svc is not null && ActionRules.CanStartTrackRadio(in c.Target),
        Execute = static c =>
        {
            if (c.Target.Single is not { Uri.Length: > 0 } t) return;
            RadioLaunch.Start(c.S.Svc?.Player, t.Uri, t.Title, c.S.Go);
        },
    };

    /// <summary>View credits — a modal listing the track's contributors (performers / writers / producers) from the
    /// NPV enrichment fetch. Single track carrying a primary artist uri only (the fetch keys off artistUri+trackUri).</summary>
    public static readonly AppAction ViewCredits = new()
    {
        Id = ActionId.ViewCredits, IconKey = ActionIcons.Credits,
        Label = static c => Loc.Get(Strings.Menu.ViewCredits),
        IsEnabled = static c => c.S.Svc is not null && c.S.Overlay is not null && ActionRules.CanViewCredits(in c.Target),
        Execute = static c =>
        {
            if (c.S.Svc is not { } svc || c.S.Overlay is not { } overlay) return;
            if (c.Target.Single is not { Artists: { Count: > 0 } artists } t || artists[0].Uri.Length == 0) return;
            string artistUri = artists[0].Uri;
            string trackUri = t.Uri;
            string title = t.Title;
            var go = c.S.Go;
            ContentDialog.Show(overlay, d =>
            {
                d.Title = title;
                d.PrimaryText = "";                                 // display-only; the dialog just needs a dismiss
                d.CloseText = Loc.Get(Strings.Auth.Close);
                d.DefaultButton = ContentDialog.DefaultBtn.Close;
                d.Content = Embed.Comp(() => new TrackCreditsDialog(svc, artistUri, trackUri, go));
            });
        },
    };

    /// <summary>Copy the raw <c>spotify:{type}:{id}</c> uri (the Share submenu's URI variant — skips the WebUrl
    /// transform). Single spotify target only.</summary>
    public static readonly AppAction CopySpotifyUri = new()
    {
        Id = ActionId.CopySpotifyUri, IconKey = ActionIcons.CopyUri,
        Label = static c => Loc.Get(Strings.Menu.CopySpotifyUri),
        IsEnabled = static c => c.S.Clipboard is not null && SpotifyLink.SingleUri(in c.Target) is not null,
        Execute = static c =>
        {
            if (c.S.Clipboard is not { } clip || SpotifyLink.SingleUri(in c.Target) is not { } uri) return;
            try { clip.SetText(uri); }
            catch (Exception ex) { PlaylistEditErrors.Toast(ex); return; }
            InputHooks.Current.Default.Announce?.Invoke(Loc.Get(Strings.Auth.Copied), false);
            Toasts.Show(Loc.Get(Strings.Menu.UriCopied), ToastSeverity.Success);
        },
    };

    /// <summary>Open the target in the Spotify web player (the Share submenu's web variant). Single spotify target
    /// only; routes through the platform OpenUri hook (mirrors LoginView.OpenUrl).</summary>
    public static readonly AppAction OpenInSpotifyWeb = new()
    {
        Id = ActionId.OpenInSpotifyWeb, IconKey = ActionIcons.OpenWeb,
        Label = static c => Loc.Get(Strings.Menu.OpenInSpotifyWeb),
        IsEnabled = static c => SpotifyLink.SingleUri(in c.Target) is { } uri && SpotifyLink.WebUrl(uri) is not null,
        Execute = static c =>
        {
            if (SpotifyLink.SingleUri(in c.Target) is not { } uri || SpotifyLink.WebUrl(uri) is not { } url) return;
            InputHooks.Current.Default.OpenUri?.Invoke(url);
        },
    };

    public static readonly AppAction RemoveFromThisPlaylist = new()
    {
        Id = ActionId.RemoveFromThisPlaylist, IconKey = ActionIcons.Remove, Destructive = true,
        Label = static c => Loc.Get(Strings.Menu.RemoveFromThisPlaylist),
        IsEnabled = static c => c.S.Library is not null && ActionRules.CanRemoveFromPlaylist(c.Target.Host),
        Execute = static c =>
        {
            if (c.S.Library is not { } lib || c.Target.Host is not { } h || h.Rows.Count == 0) return;
            // Undo payload (uri/name/uid per removed track) so the notification-center undo can re-add them.
            var refs = new ActivityTrackRef[c.Target.Count];
            for (int i = 0; i < refs.Length; i++)
            {
                var t = c.Target.Tracks[i];
                refs[i] = new ActivityTrackRef(t.Uri, t.Title, t.ContextUid);
            }
            _ = lib.RemovePlaylistRowsAsync(h.PlaylistUri, h.Rows, refs);
        },
    };

    /// <summary>Queue-panel remove: Execute rides the panel's own remove closure (player call + optimistic
    /// display-list update) via <see cref="ActionTarget.RemoveFromDisplay"/> — never a second removal path.</summary>
    public static readonly AppAction RemoveFromQueue = new()
    {
        Id = ActionId.RemoveFromQueue, IconKey = ActionIcons.Remove, Destructive = true,
        Label = static c => Loc.Get(Strings.Menu.RemoveFromQueue),
        IsEnabled = static c => c.Target.RemoveFromDisplay is not null,
        Execute = static c => c.Target.RemoveFromDisplay?.Invoke(),
    };
}
