using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee.Features.Video;

/// <summary>
/// THE single owner of the detached pop-out video window's lifecycle. A controller leaf (renders empty) mounted in the
/// shell ZStack beside <see cref="InWindowVideoPip"/>. It watches the ONE resolved placement
/// (<see cref="PlaybackBridge.VideoPlacementNow"/>) and opens / closes the detached window to match — the ONE place
/// that holds the <see cref="IDetachedVideoWindow"/> handle.
///
/// This is what structurally kills the split-ownership bugs: the player bar only expresses intent, the surfaces
/// (this window + <see cref="InWindowVideoPip"/>) only render from the resolved placement, and no view holds a window
/// handle it can desync from. Closing the window by ANY means (OS chrome / Alt+F4 / programmatic) fires
/// <see cref="IDetachedVideoWindow.OnClosed"/>, which reports the close to the placement model — the model, not this
/// component, then decides that "closed the pop-out" means "keep watching in the mini player" rather than "off".
/// </summary>
/// <summary>Bumped whenever a detached-video-window PREFERENCE is written, so live surfaces re-read it. A
/// <c>SettingKey</c> is a registry entry with nothing to subscribe to, so the epoch is the subscription — the same
/// shape as <c>LyricsPrefs.Epoch</c>.</summary>
static class VideoWindowPrefs
{
    public static readonly FluentGpu.Signals.Signal<int> Epoch = new(0);

    /// <summary>Write the always-on-top preference and notify every live reader.</summary>
    public static void SetAlwaysOnTop(IAppSettings settings, bool onTop)
    {
        settings.Set(WaveeSettings.VideoWindowAlwaysOnTop, onTop);
        Epoch.Value++;
    }
}

sealed class VideoPlacementHost : Component
{
    const SurfacePlacement Owned = SurfacePlacement.Detached;   // the ONE placement this owner is responsible for

    /// <summary>The settings store (frozen at mount — a stable instance, so freezing is correct). Remembers where the
    /// user last put the pop-out window so it reopens there instead of jumping back to the default corner.</summary>
    public IAppSettings? Settings { get; init; }

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var hooks = UseContext(InputHooks.Current);   // pop-out video: OpenDetachedWindow seam
        var handle = UseRef<IDetachedVideoWindow?>(null);   // the live detached window (null / !IsOpen = none)
        if (b is null) return new BoxEl();

        // Reactive reconcile: reads the resolved placement (a signal), so this effect re-runs whenever it changes and
        // drives the window to match. It reports reality back through SetVideoSurfaceLive, which converges after exactly
        // one extra pass (the reconcile then decides "nothing to do").
        UseSignalEffect(() =>
        {
            var live = handle.Value;
            bool alive = live is { IsOpen: true };
            var action = PlacementCore.DecideOwned(b.VideoPlacementNow(), Owned, alive);

            if (action == MountAction.Open)
            {
                // The window hosts its own AppHost + composited swapchain + video presenter, bound to the shared source
                // signal AND the shared player signal (context does not cross the AppHost boundary, so both are handed in
                // as frozen signal props; the window PRESENTS the backend-owned player rather than building its own).
                // Reopen where the user left it. The host clamps a restored rect into the nearest visible monitor's work
                // area, so a position remembered on a display that is now unplugged still lands somewhere reachable —
                // and that clamp is never written back, so plugging the display in again restores the real position.
                var restored = default(RectF);
                if (Settings is { } st &&
                    PlacementPersistence.TryLoadRect(st.Get(WaveeSettings.VideoWindowRect),
                        out float rx, out float ry, out float rw, out float rh))
                    restored = new RectF(rx, ry, rw, rh);

                bool onTop = Settings?.Get(WaveeSettings.VideoWindowAlwaysOnTop) ?? true;
                var win = hooks?.OpenDetachedWindow?.Invoke(new DetachedWindowRequest(
                    WindowTitle(b), new Size2(480, 270),
                    // Bridge/Settings are handed in as frozen props for the same reason Player is: app context does not
                    // cross the AppHost boundary (each detached window builds its own ambient map), but the bridge
                    // OBJECT crosses fine. That is what puts the placement rows on the pop-out's own ⋯ menu — without
                    // them the window can present video it has no way to move.
                    new PopOutVideoWindow
                    {
                        Source = b.PopOutVideoSource, Player = b.VideoPlayer, Bridge = b, Settings = Settings,
                    }, AlwaysOnTop: onTop,
                    InitialBoundsPx: restored));
                handle.Value = win;
                if (win is null)
                {
                    // The platform refused a second window. That is a placement that is not actually available, so report
                    // it as a close: the model falls back to the mini player instead of leaving the button lit pointing at
                    // a window that was never created (the dead-click).
                    b.NotifyVideoSurfaceClosed(Owned);
                    return;
                }
                win.OnClosed = () =>
                {
                    // Identity guard: if this dead window is no longer the current handle (a newer window B was opened in
                    // the same frame while A sat !IsOpen awaiting the reaper), A's stale callback must not clobber B's
                    // handle. The MODEL guards the placement half of the same race (a close for a placement that is no
                    // longer resolved is inert); this guards the handle half, which the model cannot see.
                    if (!ReferenceEquals(handle.Value, win)) return;
                    handle.Value = null;
                    b.SetVideoSurfaceLive(Owned, mounted: false);
                    b.NotifyVideoSurfaceClosed(Owned);
                };
                // Persist the window's SETTLED position (the host debounces — one call per move/resize gesture, not one
                // per pixel), so "where I put it" survives a restart.
                if (Settings is { } save)
                    win.BoundsChanged = r => save.Set(WaveeSettings.VideoWindowRect,
                        PlacementPersistence.SaveRect(r.X, r.Y, r.W, r.H));
                b.SetVideoSurfaceLive(Owned, mounted: true);
            }
            else if (action == MountAction.Close)
            {
                live!.OnClosed = null;   // a state-driven close is not a user-close → it must not trigger the fallback
                live.Close();
                handle.Value = null;
                b.SetVideoSurfaceLive(Owned, mounted: false);
            }
        });

        // Keep the OS window title on the CONTENT: a pop-out that still says the previous song in the taskbar and Alt+Tab
        // is the same "frozen at open" staleness as reusing a button label for a window title. Reads CurrentTrack, so it
        // re-runs on every track change; a no-op when no window is open.
        UseSignalEffect(() =>
        {
            var title = WindowTitleOf(b.CurrentTrack.Value);
            if (handle.Value is { IsOpen: true } live) live.SetTitle(title);
        });

        // Always-on-top follows the preference LIVE, not just at open: a user who turns it off while the window is up
        // means "get out of the way now", and making them close and reopen the window to apply it would be the same
        // frozen-at-open staleness the title effect above exists to avoid. Keyed on the prefs epoch (a SettingKey has
        // nothing to subscribe to on its own — the same idiom LyricsPrefs uses).
        UseSignalEffect(() =>
        {
            _ = VideoWindowPrefs.Epoch.Value;   // subscribe → re-apply when the toggle is written
            if (Settings is not { } st) return;
            if (handle.Value is { IsOpen: true } live) live.SetTopmost(st.Get(WaveeSettings.VideoWindowAlwaysOnTop));
        });

        // Unmount cleanup: the shell can swap this component out (e.g. logout) while the window is still open.
        // UseSignalEffect has no disposer, so without this the window would leak with an OnClosed pointing at a dead
        // component. Null OnClosed first so the (intentional) close never reports a user-close.
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

    // A real window title — what the OS shows in the taskbar and Alt+Tab. It is the CONTENT (the track), not the name of
    // the button that opened it; reusing a command label ("Switch to video") as a window title was one of the small
    // things that made the feature feel bolted on. Set at open time (the seam has no SetTitle yet — M4).
    static string WindowTitle(PlaybackBridge b) => WindowTitleOf(b.CurrentTrack.Peek());

    static string WindowTitleOf(Wavee.Core.Track? t)
        => string.IsNullOrWhiteSpace(t?.Title) ? Loc.Get(Strings.Player.NowPlaying) : t!.Title;
}
