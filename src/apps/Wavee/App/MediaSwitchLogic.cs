namespace Wavee;

/// <summary>The kind of playable the app's ONE current media is. Milestone B makes the current media host swappable by
/// this kind: <see cref="Audio"/> runs the existing audio host, <see cref="Video"/> runs the new video-media host, and
/// <see cref="LocalFile"/> runs a local file through the audio host (it reports as audio to Connect). A video track is
/// a video regardless of where it came from, so <see cref="Video"/> takes precedence over <see cref="LocalFile"/>.</summary>
public enum PlayableKind { Audio, Video, LocalFile }

/// <summary>
/// The PURE, engine-free decision rules for the video-as-current-media host swap (Milestone B). Every function here
/// takes plain values (read at the call site) and returns a decision — no <c>Signal&lt;T&gt;</c>, no FluentGpu type,
/// nothing but <see cref="System"/> + the local <see cref="PlayableKind"/>/<see cref="SwitchAction"/> enums. This is the
/// SINGLE tested source of truth for the rules the imperative <c>PlaybackController</c> wiring will call later: which
/// kind a playable is, whether a switch reloads the current host or swaps hosts, whether a crossfade is allowed across a
/// boundary, what <c>track_player</c> Connect should report, and whether the outgoing host must be stopped first. It is
/// source-included into the engine-free unit-test project so the behavior is verifiable without a GPU or window.
/// </summary>
public static class MediaSwitchLogic
{
    /// <summary>Classify the current media into the ONE kind that selects its host. A video track is always
    /// <see cref="PlayableKind.Video"/> regardless of origin (video wins over local); otherwise a local file is
    /// <see cref="PlayableKind.LocalFile"/>; everything else is <see cref="PlayableKind.Audio"/>.</summary>
    /// <param name="isVideoTrack">Whether the current track is a music video (has a video the app will play as video).</param>
    /// <param name="isLocalFile">Whether the current track is a local file on disk.</param>
    public static PlayableKind KindOf(bool isVideoTrack, bool isLocalFile)
        => isVideoTrack ? PlayableKind.Video
         : isLocalFile ? PlayableKind.LocalFile
         : PlayableKind.Audio;

    /// <summary>What the current-media owner should do to honor a change from one playable to another.</summary>
    public enum SwitchAction
    {
        /// <summary>Same kind → the host is unchanged; just re-load the new playable onto the current host.</summary>
        LoadOnCurrent,
        /// <summary>Different kind → stop the outgoing host, swap the current host for the new kind's host, then load.</summary>
        SwapThenLoad,
    }

    /// <summary>Decide how to move the ONE current media from <paramref name="current"/> to <paramref name="next"/>:
    /// <see cref="SwitchAction.LoadOnCurrent"/> when the kind is unchanged (the same host reloads), else
    /// <see cref="SwitchAction.SwapThenLoad"/> (stop the old host, swap to the new kind's host, load the new).</summary>
    /// <param name="current">The current playable's kind.</param>
    /// <param name="next">The incoming playable's kind.</param>
    public static SwitchAction Decide(PlayableKind current, PlayableKind next)
        => current == next ? SwitchAction.LoadOnCurrent : SwitchAction.SwapThenLoad;

    /// <summary>Whether a crossfade / prepared-next transition is allowed across this boundary. Crossfade is an
    /// AUDIO-only, same-kind capability; every cross-kind boundary and every video boundary is a HARD CUT. So this is
    /// true iff both sides are <see cref="PlayableKind.Audio"/>.</summary>
    /// <param name="from">The outgoing playable's kind.</param>
    /// <param name="to">The incoming playable's kind.</param>
    public static bool AllowCrossfade(PlayableKind from, PlayableKind to)
        => from == to && from == PlayableKind.Audio;

    /// <summary>The Connect <c>track_player</c> metadata value for a playable of this kind: <c>"video"</c> for
    /// <see cref="PlayableKind.Video"/>, else <c>"audio"</c> (a <see cref="PlayableKind.LocalFile"/> plays through the
    /// audio host and therefore also reports <c>"audio"</c>).</summary>
    /// <param name="kind">The current playable's kind.</param>
    public static string TrackPlayer(PlayableKind kind)
        => kind == PlayableKind.Video ? "video" : "audio";

    /// <summary>Whether the outgoing host must be stopped BEFORE the new one starts. True on any kind change so two
    /// decoders never both output audio at once; false when the kind is unchanged (the same host reloads in place).</summary>
    /// <param name="current">The current playable's kind.</param>
    /// <param name="next">The incoming playable's kind.</param>
    public static bool ShouldStopOutgoingHost(PlayableKind current, PlayableKind next)
        => current != next;

    /// <summary>Whether the ONE current-media HOST INSTANCE actually changes across a boundary. <see cref="PlayableKind.Audio"/>
    /// and <see cref="PlayableKind.LocalFile"/> share the SAME (audio) host — only a <see cref="PlayableKind.Video"/> boundary
    /// flips to (or away from) the video host. The controller swaps hosts (and stops the outgoing one) exactly on this, so an
    /// Audio↔LocalFile change stays a same-host reload — keeping the audio fast-start / prepared-next path untouched — while
    /// every video boundary is a hard host swap. This is the swap TRIGGER, distinct from <see cref="Decide"/> (kind equality)
    /// and <see cref="ShouldStopOutgoingHost"/> (kind change): both of those treat Audio↔LocalFile as a change, but the host
    /// does not move for it. Note: on every boundary where the host changes, <see cref="ShouldStopOutgoingHost"/> is also true
    /// (a video boundary is always a kind change), so the two agree on the stop-first rule at a real host swap.</summary>
    /// <param name="current">The current playable's kind.</param>
    /// <param name="next">The incoming playable's kind.</param>
    public static bool HostChanges(PlayableKind current, PlayableKind next)
        => (current == PlayableKind.Video) != (next == PlayableKind.Video);
}
