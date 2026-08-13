using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

/// <summary>The one in-app drag discriminator for navigable resources and playable items. Targets decide capability
/// from <see cref="WaveeResourceDragPayload.Kind"/> instead of inventing surface-specific discriminators.</summary>
static class WaveeDragKinds
{
    public const string Resource = "wavee.resource";
}

/// <summary>A transport envelope shared by tabs, sidebar rows and track lists. Display/navigation identity is retained
/// separately from an optional ordered track snapshot; source playlist row refs make a same-playlist drop a move while
/// every other playlist target is a copy. The resolver is cold and runs only after a compatible drop.
/// <para><see cref="ArtUrl"/> is the drag CHIP's artwork and nothing else — a source fills it only where the cover is
/// already in hand (a sidebar row's entry). A track snapshot needs no help: the chip reads the first track's own image.
/// </para></summary>
sealed record WaveeResourceDragPayload(
    WaveeResourceKind Kind,
    string Id,
    string Uri,
    string Name,
    IReadOnlyList<Track>? Tracks = null,
    string? SourcePlaylistUri = null,
    IReadOnlyList<PlaylistRowRef>? SourceRows = null,
    Func<CancellationToken, Task<IReadOnlyList<Track>>>? TrackResolver = null,
    bool RootlistItem = false,
    string? ArtUrl = null)
{
    /// <summary>This payload's chip data (the engine-free resolution rules).</summary>
    public WaveeDragChipModel ChipModel() => WaveeDragChipModel.For(Name, ArtUrl, Tracks);

    public bool CanPin => Kind is WaveeResourceKind.Route or WaveeResourceKind.Playlist or WaveeResourceKind.Album
        or WaveeResourceKind.Artist or WaveeResourceKind.Show or WaveeResourceKind.Folder;

    public bool CanCopyTracks => Tracks is { Count: > 0 } || TrackResolver is not null;

    public Task<IReadOnlyList<Track>> ResolveTracksAsync(CancellationToken ct = default)
        => Tracks is { } tracks ? Task.FromResult(tracks)
            : TrackResolver is { } resolve ? resolve(ct)
            : Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>());

    public bool TryPin(out SidebarPinKind kind)
    {
        kind = Kind switch
        {
            WaveeResourceKind.Playlist => SidebarPinKind.Playlist,
            WaveeResourceKind.Album => SidebarPinKind.Album,
            WaveeResourceKind.Artist => SidebarPinKind.Artist,
            WaveeResourceKind.Show => SidebarPinKind.Show,
            WaveeResourceKind.Folder => SidebarPinKind.Folder,
            WaveeResourceKind.Route => SidebarPinKind.Route,
            _ => SidebarPinKind.Route,
        };
        return CanPin;
    }

    public static WaveeResourceDragPayload FromEntry(SidebarLibraryEntry entry, Services? svc, bool rootlistItem = false)
    {
        var kind = WaveeDragKindMap.Of(entry.Kind);
        return new(kind, entry.Id, entry.Uri, entry.Name,
            TrackResolver: ResolverFor(kind, entry.Uri, svc), RootlistItem: rootlistItem,
            ArtUrl: WaveeDragChipModel.ArtOf(entry.Cover));
    }

    public static WaveeResourceDragPayload FromDestination(SidebarDestination destination, ActionServices? acts)
    {
        var kind = destination.Kind switch
        {
            SidebarPinKind.Playlist => WaveeResourceKind.Playlist,
            SidebarPinKind.Album => WaveeResourceKind.Album,
            SidebarPinKind.Artist => WaveeResourceKind.Artist,
            SidebarPinKind.Show => WaveeResourceKind.Show,
            SidebarPinKind.Folder => WaveeResourceKind.Folder,
            _ => WaveeResourceKind.Route,
        };
        // A destination is a ROUTE record — it carries no cover (the tab strip never had one to show), so a tab drag's
        // chip falls back to the kind glyph tile. It carries no OWNERSHIP either, so rootlist membership is looked up
        // rather than assumed: that is what lets a tab dragged onto a sidebar folder file itself (see WaveeRootlist).
        return new(kind, destination.PinId, destination.Uri, destination.Name,
            TrackResolver: ResolverFor(kind, destination.Uri, acts?.Svc),
            RootlistItem: WaveeRootlist.IsMember(acts, kind, destination.Uri));
    }

    /// <summary>ONE track in hand — the payload carries the track itself, so every playlist destination can actually
    /// deposit it. A track payload without this list resolves nothing (<c>CanCopyTracks</c> false) and is inert on the
    /// exact surfaces a song drag exists for.</summary>
    public static WaveeResourceDragPayload ForTrack(Track track)
        => new(WaveeResourceKind.Track, track.Id is { Length: > 0 } id ? id : track.Uri, track.Uri, track.Title,
               new[] { track });

    /// <summary>A track SELECTION from a list that is not an editable playlist (search results, an album drawer, the
    /// top-tracks chart, a recommendation strip). No <c>SourcePlaylistUri</c>/<c>SourceRows</c>: with no membership
    /// rows behind it every destination correctly treats this as a COPY rather than a move.</summary>
    public static WaveeResourceDragPayload? ForTracks(IReadOnlyList<Track> tracks)
        => tracks.Count switch
        {
            0 => null,
            1 => ForTrack(tracks[0]),
            _ => new(WaveeResourceKind.Track, tracks[0].Uri, tracks[0].Uri,
                     Strings.Sidebar.SongCount(tracks.Count), tracks),
        };

    /// <summary>The navigable ENTITY a card or a hero cover stands for. The uri doubles as the identity (a card has no
    /// sidebar pin id), the resolver comes from the kind, and rootlist membership is LOOKED UP rather than guessed —
    /// a card never knows it on its own, and claiming it falsely would offer a folder a move it cannot perform.</summary>
    public static WaveeResourceDragPayload ForEntity(WaveeResourceKind kind, string uri, string name,
                                                     Image? cover, ActionServices? acts, string? artUrl = null)
        => new(kind, uri, uri, name,
               TrackResolver: ResolverFor(kind, uri, acts?.Svc),
               RootlistItem: WaveeRootlist.IsMember(acts, kind, uri),
               ArtUrl: artUrl ?? WaveeDragChipModel.ArtOf(cover));

    internal static Func<CancellationToken, Task<IReadOnlyList<Track>>>? ResolverFor(
        WaveeResourceKind kind, string uri, Services? svc)
    {
        if (svc is null || uri.Length == 0) return null;
        if (kind == WaveeResourceKind.Playlist)
            return async ct => (await svc.Library.GetPlaylistAsync(uri, ct).ConfigureAwait(false))?.Tracks
                ?? Array.Empty<Track>();
        if (kind == WaveeResourceKind.Album)
            return async ct => (await svc.Library.GetAlbumAsync(uri, ct).ConfigureAwait(false))?.Tracks
                ?? Array.Empty<Track>();
        // Deliberately NOT resolved:
        //  • ARTIST — locked product decision: an artist has no single obvious track set, so an artist dropped on a
        //    playlist is refused with a cue instead of silently depositing a guess. Future work: let the user CHOOSE
        //    what to deposit (top tracks / a discography picker) rather than the app inventing an answer.
        //  • SHOW / EPISODE — Wavee.Core models an episode as its own record, not a Track (no artists, no album ref),
        //    and a synthetic Track would be a fabrication that leaks into real playlist mutations. Adding episodes to
        //    playlists needs an Episode-aware deposit seam, not an adapter here.
        return null;
    }
}

/// <summary>Is this resource one of the user's OWN rootlist items (a saved playlist, or a folder in their sidebar
/// tree)? Only a rootlist member can be FILED into a sidebar folder, and only the library store actually knows —
/// a home card, a search hit and a tab all carry a bare uri.
/// <para>The lookup is a linear scan of the already-loaded playlist summaries, run ONCE per gesture at drag promotion
/// (the payload factory is cold by contract), never per pointer move. It answers false when the store has not loaded,
/// which is the honest reading: "not known to be in the rootlist" must not present as "is".</para></summary>
static class WaveeRootlist
{
    public static bool IsMember(ActionServices? acts, WaveeResourceKind kind, string uri)
    {
        // A FOLDER only ever originates inside the sidebar tree, where the projection sets the flag directly; there is
        // no folder uri to look up here.
        if (kind != WaveeResourceKind.Playlist || uri.Length == 0) return false;
        if (acts?.Store is not { } store) return false;
        var playlists = store.Playlists.Value.Peek();
        for (int i = 0; i < playlists.Count; i++)
            if (string.Equals(playlists[i].Uri, uri, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Is this playlist one the user can WRITE to? The same linear scan of the loaded rootlist summaries as
    /// <see cref="IsMember"/>, reading the <c>CanEdit</c> the sidebar/menus already gate on
    /// (<c>Menus.SidebarPlaylistRows</c>, <c>PlaylistPicker</c>). Callers that hold a full <c>DetailModel</c> should use
    /// its <c>Capabilities.CanEditItems</c> instead — this exists for the surfaces that hold nothing but a uri (a TAB).
    /// <para>Not loaded ⇒ false, the same honest reading <see cref="IsMember"/> takes: "not known to be editable" must
    /// not present as "is". The cost of that is a tab that refuses a deposit until the rootlist has arrived, which is
    /// strictly better than one that accepts a drop the server will reject.</para></summary>
    public static bool CanEditPlaylist(ActionServices? acts, string? uri)
    {
        if (!TabDropRules.IsDepositablePlaylistUri(uri) || acts?.Store is not { } store) return false;
        var playlists = store.Playlists.Value.Peek();
        for (int i = 0; i < playlists.Count; i++)
            if (string.Equals(playlists[i].Uri, uri, StringComparison.Ordinal)) return playlists[i].CanEdit;
        return false;
    }
}

static class WaveeResourceDrag
{
    /// <summary>How long a drag has to rest on a container before it opens itself (a collapsed sidebar folder expands,
    /// a tab activates). 500ms is the platform convention shared by macOS spring-loaded folders and WinUI's
    /// hold-to-open surfaces — long enough that merely travelling ACROSS a folder never opens it.</summary>
    public const float SpringLoadMs = 500f;

    /// <summary>Unwrap either a plain source or an item owned by <see cref="Reorderable"/>.</summary>
    public static WaveeResourceDragPayload? Unwrap(object? payload) => payload switch
    {
        WaveeResourceDragPayload direct => direct,
        ReorderPayload { Item: WaveeResourceDragPayload wrapped } => wrapped,
        _ => null,
    };

    /// <summary>The app's chip DATA for a live drag — Wavee's whole contribution to the drag visual. The framework
    /// renders it (opaque compact card, art + title + subtitle, corner count badge and stacked backdrop for a
    /// multi-select, tilt, caption, not-allowed cue, cursor offset, window clamp); this decides only what it says.
    /// The resolution rules themselves live in the engine-free <see cref="WaveeDragChipModel"/>.</summary>
    public static DragChipSpec? Chip(DragState state)
    {
        // PHASE 3 — the sidebar customizer's palette chips. A SECOND kind, deliberately handled in the ONE resolver the
        // shell mounts: `DragPreviewLayer.Of` takes exactly one, so a surface whose kind is unknown here draws no moving
        // visual at all (the dnd skill's "two visuals for one gesture" pitfall, in its other direction) and — because the
        // chip is also the only caption surface — publishes neither its drop caption nor its refusal reason.
        //
        // The section-card REORDER band shares this kind but carries a `ReorderPayload` whose Item is null, so it falls
        // through to null here and keeps its deliberate ghost lift (SidebarPane.SectionReorder's remarks).
        if (string.Equals(state.Kind, SidebarEditPlan.SectionDragKind, StringComparison.Ordinal))
            return state.Payload is SidebarSectionDropPayload section
                ? new DragChipSpec(Title: section.Label, Glyph: CzGlyphs.ForKind(section.Kind), Count: 1,
                                   RestingCaption: Loc.Get(SidebarPaneLoc.EditDropHere))
                : null;

        if (!string.Equals(state.Kind, WaveeDragKinds.Resource, StringComparison.Ordinal)
            || Unwrap(state.Payload) is not { } payload) return null;
        var model = payload.ChipModel();
        return new DragChipSpec(
            ArtSource: model.ArtUrl, Title: model.Title, Subtitle: model.Subtitle,
            Count: model.Count, Glyph: GlyphFor(payload.Kind),
            // The RESTING verb — what the chip says while travelling, before anything accepts. Reported as part of
            // "drag & drop is unclear" (2026-08-10): the card showed a song title and an artist and never a verb, so the
            // gesture gave no evidence it was even armed for a playlist. A live target's caption supersedes it the moment
            // one accepts, and a refusal reason supersedes it when one refuses. Loc.Get is a table lookup of an interned
            // string — no interpolation, so this stays safe inside the 0-alloc frame region.
            RestingCaption: payload.CanCopyTracks ? Loc.Get(Strings.Drag.DragOntoPlaylist) : null);
    }

    /// <summary>The one preview mounted at the shell root (<c>DragPreviewLayer.Of</c>).</summary>
    public static readonly Func<DragState, Element?> Preview = DragChip.Resolve(Chip);

    /// <summary>The art-less fallback tile: the same kind glyphs the sidebar uses, so a cover-less drag still reads as
    /// "a playlist" / "an album" rather than as a generic note.</summary>
    static string GlyphFor(WaveeResourceKind kind) => kind switch
    {
        WaveeResourceKind.Playlist => Icons.MusicNote,
        WaveeResourceKind.Album => Icons.Album,
        WaveeResourceKind.Artist => Icons.Contact,
        WaveeResourceKind.Show or WaveeResourceKind.Episode => Icons.RadioTower,
        WaveeResourceKind.Folder => Icons.Folder,
        WaveeResourceKind.Route => Icons.Home,
        _ => Icons.MusicNote,
    };
}

/// <summary>The detail page's HERO cover as a drag source: dragging the artwork drags the whole entity the page is
/// about (drop it on a sidebar playlist to add its tracks, on a folder to file it, on the pin band to pin it).
/// <para>It coexists with the editable-cover FILE drop target underneath: those are opposite directions of two
/// different gestures — a press-and-drag on the cover LIFTS this payload, while an OS file drag hovering the cover
/// still targets the <c>DropKinds.Files</c> spec, which never accepts <see cref="WaveeDragKinds.Resource"/>. They also
/// live on different nodes (this on the framing box, the file target on the editable cover inside it).</para></summary>
static class WaveeDetailDrag
{
    public static DragSource? Hero(DetailModel m, ActionServices? acts)
    {
        if (m.ContextUri is not { Length: > 0 } uri) return null;
        var kind = WaveeDragKindMap.OfUri(uri);
        if (kind == WaveeResourceKind.Route) return null;   // nothing this payload could be dropped on
        return Drag.Source(WaveeDragKinds.Resource,
            () => WaveeResourceDragPayload.ForEntity(kind, uri, m.Title, m.Cover, acts));
    }
}

static class WaveeResourceDrop
{
    public static bool CanDepositTracks(object? payload)
        => WaveeResourceDrag.Unwrap(payload) is { CanCopyTracks: true };

    public static void MoveRootlist(ActionServices acts, object? payload,
                                    WaveeResourceDragPayload target, RootlistDropPlacement placement)
    {
        if (acts.Library is not { } lib || WaveeResourceDrag.Unwrap(payload) is not { RootlistItem: true } source) return;
        var sourceRef = RootRef(source);
        var targetRef = RootRef(target);
        if (sourceRef.Key.Length == 0 || targetRef.Key.Length == 0) return;
        _ = Run();

        async Task Run()
        {
            try { await lib.MoveRootlistItemAsync(sourceRef, targetRef, placement).ConfigureAwait(false); }
            catch (Exception ex) { acts.Post?.Invoke(() => PlaylistEditErrors.Toast(ex)); }
        }
    }

    static RootlistItemRef RootRef(WaveeResourceDragPayload payload)
        => payload.Kind == WaveeResourceKind.Folder
            ? new RootlistItemRef(SidebarPinId.FolderIdOf(payload.Id), IsFolder: true)
            : new RootlistItemRef(payload.Uri, IsFolder: false);

    /// <summary>Commit one playlist deposit. Same-playlist row drags move stable membership rows; every other resource
    /// resolves to an ordered track snapshot and copies it. A null insertion index means append (sidebar target).</summary>
    public static void DepositTracks(ActionServices acts, string targetUri, string targetName,
                                     object? payload, int? insertionIndex)
        => _ = DepositTracksAsync(acts, targetUri, targetName, payload, insertionIndex);

    /// <summary>Deposit without the built-in confirmation toast, for a caller that owns a BETTER one. A drop that CREATED
    /// the playlist it just filled wants "Open" (the new playlist needs a name), not the generic "Added to {name}" — and
    /// two toasts for one gesture reads as a bug.</summary>
    public static Task<bool> DepositTracksSilentAsync(ActionServices acts, string targetUri, object? payload,
                                                      int? insertionIndex)
        => DepositTracksAsync(acts, targetUri, "", payload, insertionIndex, toast: false);

    /// <summary>The awaitable deposit seam used by live insertion previews. The library mutation source publishes its
    /// optimistic snapshot synchronously before the returned network task completes, so callers can hand visual ownership
    /// to the real list immediately while still retaining an error-completion edge.
    /// <para>The result is "a mutation was issued and completed" — false for every no-op refusal below and for a failed
    /// commit. A live insertion preview keys its teardown on it: only a true can promise the membership snapshot that
    /// closes the gap, so the refusals must be answerable SYNCHRONOUSLY wherever possible (they are, except the empty
    /// track resolve) or the preview waits on a handoff that never comes.</para></summary>
    public static Task<bool> DepositTracksAsync(ActionServices acts, string targetUri, string targetName,
                                                object? payload, int? insertionIndex, bool toast = true)
    {
        if (acts.Library is not { } lib || WaveeResourceDrag.Unwrap(payload) is not { } resource)
            return Task.FromResult(false);
        if (!resource.CanCopyTracks) return Task.FromResult(false);
        bool sameList = insertionIndex is not null
            && string.Equals(resource.SourcePlaylistUri, targetUri, StringComparison.Ordinal)
            && resource.SourceRows is { Count: > 0 };
        // Dropping a playlist onto itself without membership refs would append a duplicate of the entire playlist.
        // Treat that ambiguous container-on-itself gesture as a no-op; track-row drags still move.
        if (!sameList && resource.Kind == WaveeResourceKind.Playlist
            && string.Equals(resource.Uri, targetUri, StringComparison.Ordinal))
            return Task.FromResult(false);
        return Run();

        async Task<bool> Run()
        {
            try
            {
                bool moved = false;
                if (sameList && insertionIndex is { } at && resource.SourceRows is { } rows)
                {
                    // `at` is the PRE-move insertion index ("insert before the row currently at this index") — the
                    // convention PlaylistMutationSource.BuildMoveOps / UserPlaylistSource.MoveRows both implement by
                    // discounting the rows removed above it. Pinned by MoveRowsConventionTests; do NOT pre-correct here.
                    await lib.MovePlaylistRowsAsync(targetUri, rows, at).ConfigureAwait(false);
                    moved = true;
                }
                else
                {
                    var tracks = await resource.ResolveTracksAsync().ConfigureAwait(false);
                    if (tracks.Count == 0) return false;
                    if (insertionIndex is { } insert)
                        await lib.InsertTracksAsync(targetUri, tracks, insert).ConfigureAwait(false);
                    else
                        await lib.AddTracksAsync(targetUri, tracks).ConfigureAwait(false);
                }

                if (!moved && toast)
                    acts.Post?.Invoke(() => Toast.Show(Strings.Detail.AddedToPlaylist(targetName),
                        new ToastOptions { Severity = InfoBarSeverity.Success }));
                return true;
            }
            catch (Exception ex)
            {
                acts.Post?.Invoke(() => PlaylistEditErrors.Toast(ex));
                return false;
            }
        }
    }
}
