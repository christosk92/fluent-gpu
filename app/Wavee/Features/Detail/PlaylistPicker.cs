using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The "Copy to playlist" / "Add to playlist" affordance: a standard button that opens an anchored, light-dismissable
// flyout with a search box + cover thumbnails, listing the user's EDITABLE playlists (owned + collaborator) plus a
// "New playlist" entry. Picking one adds THIS context's tracks and confirms with the existing success toast. Replaces
// the old silent "dump into a default My Playlist" path. Modelled on WaveeSidebar.SidebarCreateButton (the anchored
// Overlay.Service flyout) + FlyoutButton's standard-button chrome.

/// <summary>The anchored trigger button. Opens <see cref="PlaylistPickerPanel"/> below-left via the shared overlay
/// service; re-click / Escape / click-outside dismiss. Remount per context (a <c>Key</c> at the call site) so
/// <see cref="GetTracks"/> freezes fresh — never a stale frozen closure (component-props-freeze rule).</summary>
public sealed class PlaylistPickerButton : Component
{
    public string Label = "";
    public Func<IReadOnlyList<Track>> GetTracks = () => Array.Empty<Track>();

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var getTracks = GetTracks;

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new PlaylistPickerPanel { GetTracks = getTracks, Close = () => handle.Value?.Close() }),
                FlyoutPlacement.BottomLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        // Standard-button chrome (1:1 with FlyoutButton / Button.Standard: ControlAll corners, elevation border, the
        // three control fills, 11,5,11,6 padding, 32 min height).
        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 32f,
            Padding = new Edges4(11, 5, 11, 6),
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            HoverFill = Tok.FillControlSecondary,
            PressedFill = Tok.FillControlTertiary,
            ClipToBounds = true,
            Role = AutomationRole.Button,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [new TextEl(Label) { Size = 14f, Color = Tok.TextPrimary }],
        };
    }
}

/// <summary>The flyout content: a live search field + a "New playlist" row + the scrollable list of editable playlists.
/// Runs inside the open thunk, so it mounts fresh each open (always the current list). Picking a row (or "New playlist")
/// adds the tracks, dismisses, and shows the success toast with an "Open" action that navigates to the playlist.</summary>
public sealed class PlaylistPickerPanel : Component
{
    public required Func<IReadOnlyList<Track>> GetTracks;
    public required Action Close;

    public override Element Render()
    {
        var store = UseContext(LibraryStore.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        store?.EnsurePlaylists();

        var query = UseSignal("");
        string q = query.Value;                                                          // subscribe → re-filter on each keystroke
        var pls = store?.Playlists.Value.Value ?? Array.Empty<PlaylistSummary>();        // subscribe → the live playlist list
        var getTracks = GetTracks;
        var close = Close;

        // Add THIS context's tracks to an existing editable playlist, then confirm + dismiss.
        void AddTo(string uri, string name)
        {
            if (lib is null) return;
            _ = lib.AddTracksAsync(uri, getTracks());
            close();
            Toasts.Show(Strings.Detail.AddedToPlaylist(name), ToastSeverity.Success,
                actionLabel: Loc.Get(Strings.Detail.GoToPlaylist), onAction: () => go?.Invoke("pl:" + uri, name));
        }

        // Create a new playlist, add the tracks, then confirm + dismiss.
        void CreateAndAdd()
        {
            if (lib is null) return;
            string name = Loc.Get(Strings.Detail.NewPlaylist);
            string uri = lib.CreatePlaylist(name);
            _ = lib.AddTracksAsync(uri, getTracks());
            close();
            Toasts.Show(Strings.Detail.AddedToPlaylist(name), ToastSeverity.Success,
                actionLabel: Loc.Get(Strings.Detail.GoToPlaylist), onAction: () => go?.Invoke("pl:" + uri, name));
        }

        // Editable playlists only (owned + collaborator), filtered by the case-insensitive query.
        var rows = new List<Element>(pls.Count);
        for (int i = 0; i < pls.Count; i++)
        {
            var p = pls[i];
            if (!p.CanEdit) continue;
            if (q.Length > 0 && p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows.Add(PlaylistRow(p, () => AddTo(p.Uri, p.Name)));
        }

        Element list = rows.Count > 0
            ? new ScrollEl
            {
                ContentSized = true, MaxHeight = 360f,
                Content = new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() },
            }
            : new BoxEl
            {
                Height = 44f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
                Children = [new TextEl(Loc.Get(Strings.Detail.NoPlaylists)) { Size = 13f, Color = Tok.TextSecondary }],
            };

        return new BoxEl
        {
            Direction = 1, Width = 320f, Gap = WaveeSpace.XS, Padding = new Edges4(8f, 8f, 8f, 8f),
            Children =
            [
                Embed.Comp(() => new EditableText
                {
                    Placeholder = Loc.Get(Strings.Detail.FindPlaylist),
                    Width = 300f, Height = 32f, Text = query,
                }),
                NewPlaylistRow(CreateAndAdd),
                list,
            ],
        };
    }

    // A "New playlist" row: a ＋ tile (matching the 40px cover thumbnails) + label. Creates + adds on click.
    static Element NewPlaylistRow(Action onClick) => new BoxEl
    {
        Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = 10f,
        Padding = new Edges4(6f, 0f, 8f, 0f), Corners = CornerRadius4.All(4f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, OnClick = onClick,
        Children =
        [
            new BoxEl
            {
                Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(6f), Fill = Tok.FillSubtleSecondary,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Mdl.Add, 20f, Tok.TextSecondary)],
            },
            new TextEl(Loc.Get(Strings.Detail.NewPlaylist)) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    // One playlist row: cover thumbnail + name (+ "Collaborator" caption when the user can edit but doesn't own it).
    static Element PlaylistRow(PlaylistSummary p, Action onClick) => new BoxEl
    {
        Key = p.Uri,
        Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = 10f,
        Padding = new Edges4(6f, 0f, 8f, 0f), Corners = CornerRadius4.All(4f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, OnClick = onClick,
        Children =
        [
            Surfaces.Artwork(p.Cover, 0, 40f, 40f, 6f),
            new BoxEl { Direction = 1, Grow = 1f, Gap = 1f, Children = NameColumn(p) },
        ],
    };

    static Element[] NameColumn(PlaylistSummary p) =>
        p.CanEdit && !p.IsOwner
            ? new Element[]
              {
                  new TextEl(p.Name) { Size = 14f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                  new TextEl(Loc.Get(Strings.Detail.Collaborator)) { Size = 12f, Color = Tok.TextSecondary },
              }
            : new Element[]
              {
                  new TextEl(p.Name) { Size = 14f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
              };
}
