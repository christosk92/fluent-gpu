using Wavee.Core;

namespace Wavee.Backend.Playlists;

/// <summary>Structured logging for Spotify playlist permission / rootlist owner mutations.</summary>
static class PlaylistMutationDiagnostics
{
    const string Category = "playlist-mutations";

    public static void PermissionGetFailed(string playlistUri, int status) =>
        WaveeLog.Instance.Warn(Category, "permission.base.get.failed", "GET permission/base failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status));

    public static void PermissionSetFailed(string playlistUri, int status, PlaylistPermissionLevel level) =>
        WaveeLog.Instance.Warn(Category, "permission.base.set.failed", "POST permission/base failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status),
            WaveeLogField.Of("level", level.ToString()));

    public static void PermissionGrantFailed(string playlistUri, int status) =>
        WaveeLog.Instance.Warn(Category, "permission.grant.failed", "POST permission-grant failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status));

    public static void ExtendFailed(string playlistUri, int status) =>
        WaveeLog.Instance.Warn(Category, "playlistextender.extend.failed", "POST playlistextender/extendp failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status));

    public static void PermissionConflict(string playlistUri) =>
        WaveeLog.Instance.Info(Category, "permission.base.conflict", "permission base revision conflict — retrying",
            WaveeLogField.Of("uri", playlistUri));

    public static void RootlistPostFailed(string playlistUri, int status, string op) =>
        WaveeLog.Instance.Warn(Category, "rootlist.changes.failed", "POST rootlist/changes failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status),
            WaveeLogField.Of("op", op));

    public static void RootlistConflict(string playlistUri) =>
        WaveeLog.Instance.Info(Category, "rootlist.revision.conflict", "rootlist revision conflict — rebased",
            WaveeLogField.Of("uri", playlistUri));

    public static void DeleteFailed(string playlistUri, Exception ex) =>
        WaveeLog.Instance.Error(Category, "playlist.delete.failed", "delete playlist failed", ex,
            WaveeLogField.Of("uri", playlistUri));
}
