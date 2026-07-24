namespace Wavee;

/// <summary>Where the now-playing music video plays. Exactly TWO placements exist today because those are the only two
/// video surfaces the app actually has: the in-window picture-in-picture (<see cref="Features.Video.InWindowVideoPip"/>)
/// and the detached always-on-top pop-out window (<see cref="Features.Video.PopOutVideoWindow"/>). Docked (inline in the
/// now-playing view) and Fullscreen are RESERVED for when those surfaces exist — do not add them here until then, so the
/// enum can never hold a placement with no surface to honor it.</summary>
public enum VideoPlacement { InWindowPip, Detached }

/// <summary>
/// The PURE, engine-free decision rules for video placement/lifecycle (Milestone A). Every function here takes plain
/// values (read from the bridge's signals at the call site) and returns a decision — no <c>Signal&lt;T&gt;</c>, no
/// FluentGpu type, nothing but <see cref="System"/> + the <see cref="VideoPlacement"/> enum. This is the SINGLE tested
/// source of truth for the boolean rules that <c>PlaybackBridge</c> and <c>VideoPlacementHost</c> used to inline; both
/// now delegate here so the behavior is verifiable by an engine-free unit-test project (source-includes this file).
/// </summary>
public static class VideoPlacementLogic
{
    /// <summary>The one predicate every video surface + player-bar highlight reads: a video should be live iff the user
    /// prefers video, the current track HAS a video, and the video is not dismissed for this track. Reads only plain
    /// values (the caller supplies the current signal snapshots), so it is pure and directly testable.</summary>
    /// <param name="preferVideo">The sticky "watch video" intent (carries across tracks).</param>
    /// <param name="hasVideo">Whether the current track has an accompanying music video.</param>
    /// <param name="trackGen">The current monotonic per-track generation.</param>
    /// <param name="dismissedForTrackGen">The track generation the user dismissed the PiP for (-1 = not dismissed).</param>
    public static bool VideoActive(bool preferVideo, bool hasVideo, long trackGen, long dismissedForTrackGen)
        => preferVideo && hasVideo && dismissedForTrackGen != trackGen;

    /// <summary>The async-resolve fence: an in-flight video resolve may only publish its result if the generation it
    /// captured when it started is still the current generation. A track change bumps the generation, so a resolve for
    /// a superseded track is dropped instead of overwriting the current track's source with a stale video.</summary>
    /// <param name="capturedGen">The resolve generation captured when the async resolve began.</param>
    /// <param name="currentGen">The bridge's current resolve generation at publish time.</param>
    public static bool ShouldPublishResolve(long capturedGen, long currentGen)
        => capturedGen == currentGen;

    /// <summary>What the detached-window owner should do to reconcile the live window with the derived placement.</summary>
    public enum DetachedAction
    {
        /// <summary>The window already matches the desired state — do nothing.</summary>
        None,
        /// <summary>No detached window is alive but one should be — open it.</summary>
        Open,
        /// <summary>A detached window is alive but should not be — close it.</summary>
        Close,
    }

    /// <summary>Decide whether the detached pop-out window must be opened, closed, or left alone, given the current
    /// derived placement and whether a window is already alive. Open iff a detached video is wanted and none is alive;
    /// Close iff a window is alive and a detached video is no longer wanted; otherwise None.</summary>
    /// <param name="videoActive">The result of <see cref="VideoActive(bool,bool,long,long)"/>.</param>
    /// <param name="placement">The single owned placement state.</param>
    /// <param name="windowAlive">Whether the detached window currently exists and is open.</param>
    public static DetachedAction DecideDetached(bool videoActive, VideoPlacement placement, bool windowAlive)
    {
        bool wantDetached = videoActive && placement == VideoPlacement.Detached;
        if (wantDetached && !windowAlive) return DetachedAction.Open;
        if (windowAlive && !wantDetached) return DetachedAction.Close;
        return DetachedAction.None;
    }

    /// <summary>When the user closes the detached window by any means, decide the placement fallback so the toggle is
    /// never left stuck "on" with no surface (bug 3): fall back to the in-window PiP iff we were in the detached
    /// placement and the video is still active; otherwise return null (no placement change).</summary>
    /// <param name="placement">The placement at the moment the window closed.</param>
    /// <param name="videoActive">Whether the video is still active (per <see cref="VideoActive(bool,bool,long,long)"/>).</param>
    /// <returns>The placement to switch to, or null to leave the placement unchanged.</returns>
    public static VideoPlacement? FallbackOnUserClose(VideoPlacement placement, bool videoActive)
        => placement == VideoPlacement.Detached && videoActive ? VideoPlacement.InWindowPip : null;
}
