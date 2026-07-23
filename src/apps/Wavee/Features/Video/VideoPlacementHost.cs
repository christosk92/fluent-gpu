using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee.Features.Video;

/// <summary>
/// THE single owner of the detached pop-out video window's lifecycle. A controller leaf (renders empty) mounted in the
/// shell ZStack beside <see cref="InWindowVideoPip"/>. It watches the bridge's DERIVED placement state
/// (<see cref="PlaybackBridge.VideoActive"/> × <see cref="PlaybackBridge.VideoPlacement"/>) and opens / closes the
/// detached window to match — the ONE place that holds the <see cref="IDetachedVideoWindow"/> handle.
///
/// This is what structurally kills the split-ownership bugs: the player-bar only expresses intent (writes PreferVideo /
/// VideoPlacement), the surfaces (this window + <see cref="InWindowVideoPip"/>) only render from the derived state, and
/// no view holds a window handle it can desync from. Closing the detached window (OS chrome / Alt+F4 / programmatic)
/// fires <see cref="IDetachedVideoWindow.OnClosed"/>, which falls the placement back to the in-window PiP so the toggle
/// is never stuck "on" with no surface (bug 3).
/// </summary>
sealed class VideoPlacementHost : Component
{
    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var hooks = UseContext(InputHooks.Current);   // pop-out video: OpenDetachedWindow seam
        var handle = UseRef<IDetachedVideoWindow?>(null);   // the live detached window (null / !IsOpen = none)
        if (b is null) return new BoxEl();

        // Reactive reconcile: reads VideoActive() + VideoPlacement (signals), so this effect re-runs whenever the derived
        // placement changes and drives the detached window to match. Writes only the non-reactive handle ref here (no
        // self-trigger); the OnClosed fallback below writes VideoPlacement, which correctly re-runs this effect.
        UseSignalEffect(() =>
        {
            var live = handle.Value;
            bool alive = live is { IsOpen: true };
            // Single tested source of truth for the open/close decision (VideoActive × Detached × alive).
            var action = VideoPlacementLogic.DecideDetached(b.VideoActive(), b.VideoPlacement.Value, alive);

            if (action == VideoPlacementLogic.DetachedAction.Open)
            {
                // Resolve the now-playing track's playable source (Spotify manifest → PopOutVideoSource) is kicked by the
                // intent / RecomputeHasVideo; here we just open the detached, always-on-top window (its own AppHost +
                // composited swapchain + video presenter) bound to the shared source signal.
                // TODO(placement): clamp detached bounds to a visible monitor
                var win = hooks?.OpenDetachedWindow?.Invoke(new DetachedWindowRequest(
                    Loc.Get(Strings.Player.SwitchToVideo), new Size2(480, 270),
                    new PopOutVideoWindow { Source = b.PopOutVideoSource }, AlwaysOnTop: true));
                handle.Value = win;
                if (win is not null)
                    // Close-detached → fall back to the in-window PiP (bug 3). Fired exactly once on the UI thread when the
                    // window closes by ANY means, immediately before teardown. Because placement is a single derived truth,
                    // flipping it to PiP both hides the (now-gone) detached highlight and lights the PiP surface — the
                    // toggle can never be left stuck ON pointing at a window that no longer exists.
                    win.OnClosed = () =>
                    {
                        // Identity guard: if this dead window is no longer the current handle (a newer window B was opened
                        // in the same frame while A sat !IsOpen awaiting the reaper), A's stale callback must be a no-op —
                        // else it would clobber handle.Value (orphaning B) and spuriously fall placement back to PiP.
                        if (!ReferenceEquals(handle.Value, win)) return;
                        handle.Value = null;
                        // Single tested source of truth for the close→PiP fallback (bug 3).
                        if (VideoPlacementLogic.FallbackOnUserClose(b.VideoPlacement.Peek(), b.VideoActive()) is { } fallback)
                            b.VideoPlacement.Value = fallback;
                    };
            }
            else if (action == VideoPlacementLogic.DetachedAction.Close)
            {
                live!.OnClosed = null;   // an intentional (state-driven) close is not a user-close → no PiP fallback
                live.Close();
                handle.Value = null;
            }
        });

        // Unmount cleanup: the shell can swap this component out (e.g. logout) while the detached window is still open.
        // UseSignalEffect has no disposer, so without this the window would leak with an OnClosed pointing at a dead
        // component. Null OnClosed first so the (intentional) close never fires the PiP fallback.
        UseEffect(() => () =>
        {
            var h = handle.Value;
            handle.Value = null;
            if (h is not null)
            {
                h.OnClosed = null;
                h.Close();
            }
        }, DepKey.Empty);

        return new BoxEl();
    }
}
