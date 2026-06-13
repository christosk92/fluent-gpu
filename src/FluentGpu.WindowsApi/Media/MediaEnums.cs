using System.Runtime.Versioning;

namespace FluentGpu.WindowsApi.Media;

/// <summary>
/// The playback state reported to the System Media Transport Controls (the OS media flyout / lock screen / hardware
/// media keys). Maps 1:1 to the WinRT <c>MediaPlaybackStatus</c> enum
/// (<c>TerraFX.Interop.WinRT.MediaPlaybackStatus</c>) driven through <c>ISystemMediaTransportControls::put_PlaybackStatus</c>.
/// A media app must keep this in sync with its transport state so the OS shows the right play/pause affordance and so
/// "now playing" surfaces light up.
/// </summary>
/// <remarks>See <see href="https://learn.microsoft.com/en-us/uwp/api/windows.media.mediaplaybackstatus">MediaPlaybackStatus</see>.</remarks>
[SupportedOSPlatform("windows8.0")]
public enum MediaPlaybackStatus
{
    /// <summary>The media session is closed — nothing is loaded (<c>MediaPlaybackStatus_Closed</c>, value 0).</summary>
    Closed = 0,

    /// <summary>The session is mid-transition (e.g. buffering / track change) (<c>MediaPlaybackStatus_Changing</c>, 1).</summary>
    Changing = 1,

    /// <summary>Playback is stopped (<c>MediaPlaybackStatus_Stopped</c>, 2).</summary>
    Stopped = 2,

    /// <summary>Media is playing (<c>MediaPlaybackStatus_Playing</c>, 3).</summary>
    Playing = 3,

    /// <summary>Playback is paused (<c>MediaPlaybackStatus_Paused</c>, 4).</summary>
    Paused = 4,
}

/// <summary>
/// A transport button the user pressed on a system media surface (media-key keyboard, headset, lock screen, the
/// volume/now-playing flyout). Delivered by <see cref="SystemMediaControls.ButtonPressed"/>, projected from the WinRT
/// <c>SystemMediaTransportControlsButton</c> enum carried on
/// <c>ISystemMediaTransportControlsButtonPressedEventArgs::get_Button</c>.
/// </summary>
/// <remarks>
/// These are FluentGpu-local values, NOT the raw WinRT ordinals — <see cref="SystemMediaControls"/> translates the
/// native <c>SystemMediaTransportControlsButton</c> with an explicit switch (the WinRT order is
/// Play=0,Pause=1,Stop=2,Record=3,FastForward=4,Rewind=5,Next=6,Previous=7,ChannelUp=8,ChannelDown=9, confirmed from
/// <c>TerraFX.Interop.WinRT.SystemMediaTransportControlsButton</c>). Only the buttons a typical music app handles are
/// surfaced; record / fast-forward / rewind / channel map to <see cref="Unknown"/> so a handler can ignore them
/// explicitly rather than receiving a silently dropped event. See
/// <see href="https://learn.microsoft.com/en-us/uwp/api/windows.media.systemmediatransportcontrolsbutton">SystemMediaTransportControlsButton</see>.
/// </remarks>
[SupportedOSPlatform("windows8.0")]
public enum MediaButton
{
    /// <summary>A button the app does not model (record/fast-forward/rewind/channel) — surfaced rather than dropped so a
    /// handler can ignore it explicitly.</summary>
    Unknown = 0,

    /// <summary>The play button.</summary>
    Play,

    /// <summary>The pause button.</summary>
    Pause,

    /// <summary>The stop button.</summary>
    Stop,

    /// <summary>The "previous track" button.</summary>
    Previous,

    /// <summary>The "next track" button.</summary>
    Next,
}
