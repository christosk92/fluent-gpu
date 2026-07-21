namespace Wavee;

// Stable action identities — the ONE part kept from a CommandManager design: a future shortcut map / command palette
// keys off these, and every AppAction carries its Id. Values are persisted-safe (append only; never reorder/reuse).
public enum ActionId : ushort
{
    None = 0,
    Play, PlayNext, AddToQueue, ToggleLike, AddToPlaylist, AddToDefaultPlaylist,
    GoToAlbum, GoToArtist, CopyLink, RemoveFromThisPlaylist, RemoveFromQueue, SelectAll,
    OpenItem, PlayContext, SaveContext /* save album · follow artist · save playlist */,
    RenamePlaylist, TogglePlaylistPublic, InviteCollaborators, DeletePlaylist,
    PinToSidebar,
    GoToSongRadio, GoToArtistRadio, ViewCredits, CopySpotifyUri, OpenInSpotifyWeb,
}
