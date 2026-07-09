using System;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>Maps playlist owner-mutation exceptions to localized user copy.</summary>
static class PlaylistEditErrors
{
    public static string UserMessage(Exception ex) => ex switch
    {
        NotSupportedException => Loc.Get(Strings.Detail.Edit.OfflineSpotifyEdits),
        InvalidOperationException io when io.Message.Contains("permission grant failed (400)")
            => Loc.Get(Strings.Detail.Edit.InviteGrantFailed),
        InvalidOperationException io when io.Message.Contains("permission grant failed")
            => Loc.Get(Strings.Detail.Edit.InviteGrantFailed),
        InvalidOperationException io when io.Message.Contains("permission base failed")
            => Loc.Get(Strings.Detail.Edit.VisibilityFailed),
        InvalidOperationException io when io.Message.Contains("rootlist revision conflict")
            => Loc.Get(Strings.Detail.Edit.RootlistConflict),
        InvalidOperationException io when io.Message.Contains("rootlist changes failed")
            => Loc.Get(Strings.Detail.Edit.RootlistConflict),
        _ => ex.Message,
    };

    public static void Toast(Exception ex) => Toasts.Show(UserMessage(ex), ToastSeverity.Critical);
}
