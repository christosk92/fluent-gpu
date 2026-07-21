using System;
using System.Runtime.Versioning;
using FluentGpu.WindowsApi.Media;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The System Media Transport Controls (SMTC) integration for Wavee: it mirrors the app's UNIFIED now-playing state
/// (the <see cref="PlaybackBridge"/> signals, which fold local-engine AND remote Connect playback into one truth) onto
/// the OS media surfaces — the Windows now-playing flyout, the lock screen, and the hardware media keys / headset
/// buttons — and routes the transport buttons the OS raises back into <see cref="IPlaybackPlayer"/> intents.
/// <para>
/// It reads the bridge's own UI-facing signals (never the engine <c>IMediaPlayer</c> directly): those signals are the
/// single source that is CORRECT whether Wavee is the active local player or a viewer of a remote Connect device, and
/// they are already marshalled onto the UI thread. This is why the SMTC source is the bridge and not the engine's
/// <c>NowPlaying</c> surface (which is dark whenever another Connect device owns playback).
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Activate"/> and the <c>OnStateChanged</c>/<c>OnPositionChanged</c> pushes are all
/// invoked on the UI thread that owns the window handle (from <see cref="PlaybackBridge"/>'s post-marshalled callbacks),
/// which is exactly what <see cref="SystemMediaControls"/> requires. The OS raises <c>ButtonPressed</c> on an arbitrary
/// worker thread, so the button dispatcher is set to the same UI-thread post delegate; the transport intent then runs
/// on the UI thread.
/// </para>
/// <para>
/// <b>Fail-soft.</b> If the platform refuses SMTC (older OS, interop failure) construction is swallowed and every push
/// becomes a no-op — the app keeps working with no OS media surface, never crashing.
/// </para>
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed class SystemMediaControlsBridge : IDisposable
{
    readonly PlaybackBridge _bridge;
    readonly IPlaybackPlayer _player;
    readonly Action<Action> _post;

    SystemMediaControls? _smtc;
    bool _disposed;

    // Edge-dedupe so a state tick that didn't change what the OS shows makes no WinRT call.
    string? _lastUri = null;
    MediaPlaybackStatus _lastStatus = (MediaPlaybackStatus)(-1);
    bool _lastCanNext = true, _lastCanPrev = true;
    long _lastTimelineSec = -1;

    public SystemMediaControlsBridge(PlaybackBridge bridge, IPlaybackPlayer player, Action<Action> post)
    {
        _bridge = bridge;
        _player = player;
        _post = post;
    }

    /// <summary>Acquire the SMTC for <paramref name="hwnd"/> (the real top-level window handle — pass
    /// <c>FluentApp.WindowHandle</c>) and wire the transport buttons. UI-thread only. A zero handle or an unsupported
    /// platform leaves the bridge inert (no OS surface, no throw). Idempotent — a second call is a no-op.</summary>
    public void Activate(nint hwnd)
    {
        if (_smtc is not null || _disposed || hwnd == 0) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(8, 0)) return;
        try
        {
            var smtc = SystemMediaControls.GetForWindow(hwnd);
            smtc.ButtonDispatcher = _post;   // OS worker thread → UI thread (the same post the bridge marshals on)
            smtc.ButtonPressed += OnButton;
            smtc.SetEnabledButtons(play: true, pause: true, next: true, previous: true);
            smtc.PlaybackRate = 1.0;         // must be > 0; Spotify content is normal speed (spoken-word rate is not surfaced here)
            smtc.IsEnabled = true;
            _smtc = smtc;
            // Seed the OS surface from whatever is playing right now (the bridge was activated first).
            OnStateChanged();
            OnPositionChanged(_bridge.PositionMs.Peek());
        }
        catch (Exception)
        {
            // Platform refused SMTC (older OS / interop) — stay inert; the app is unaffected.
            _smtc = null;
        }
    }

    /// <summary>Push the current now-playing metadata + play status + transport availability to the OS. Called from
    /// <see cref="PlaybackBridge"/> whenever the unified state changes (already on the UI thread). Only differences from
    /// the last push touch the OS.</summary>
    public void OnStateChanged()
    {
        if (_smtc is not { } smtc || _disposed) return;

        var track = _bridge.CurrentTrack.Peek();
        string uri = track?.Uri ?? "";
        if (!string.Equals(uri, _lastUri, StringComparison.Ordinal))
        {
            _lastUri = uri;
            try
            {
                if (track is null)
                {
                    smtc.ClearDisplay();
                }
                else
                {
                    string artist = track.Artists.Count > 0 ? track.Artists[0].Name : "";
                    string? album = string.IsNullOrEmpty(track.Album.Name) ? null : track.Album.Name;
                    string? art = string.IsNullOrEmpty(track.Image?.Url) ? null : track.Image!.Url;
                    smtc.UpdateDisplay(track.Title ?? "", artist, album, art);
                }
            }
            catch (Exception) { /* a bad art URL / transient WinRT failure must never break playback */ }
        }

        var status = track is null ? MediaPlaybackStatus.Closed
            : _bridge.IsBuffering.Peek() ? MediaPlaybackStatus.Changing
            : _bridge.IsPlaying.Peek() ? MediaPlaybackStatus.Playing
            : MediaPlaybackStatus.Paused;
        if (status != _lastStatus)
        {
            _lastStatus = status;
            try { smtc.SetPlaybackStatus(status); } catch (Exception) { }
        }

        bool canNext = _bridge.CanSkipNext.Peek(), canPrev = _bridge.CanSkipPrev.Peek();
        if (canNext != _lastCanNext || canPrev != _lastCanPrev)
        {
            _lastCanNext = canNext;
            _lastCanPrev = canPrev;
            try { smtc.SetEnabledButtons(play: true, pause: true, next: canNext, previous: canPrev); } catch (Exception) { }
        }
    }

    /// <summary>Push the timeline (position + duration) so the Win11 flyout / lock-screen scrub bar tracks playback.
    /// Called from <see cref="PlaybackBridge"/> on each position tick (UI thread); throttled to whole-second changes so
    /// it stays the ~1 Hz cadence the OS expects (SMTC seek-DRAG would need a position-change callback the current
    /// <see cref="SystemMediaControls"/> surface does not expose — read-only scrub only).</summary>
    public void OnPositionChanged(long positionMs)
    {
        if (_smtc is not { } smtc || _disposed) return;
        long dur = _bridge.DurationMs.Peek();
        if (dur <= 0) return;
        long pos = Math.Clamp(positionMs, 0, dur);
        long sec = pos / 1000;
        if (sec == _lastTimelineSec) return;
        _lastTimelineSec = sec;
        try { smtc.UpdateTimeline(TimeSpan.FromMilliseconds(pos), TimeSpan.FromMilliseconds(dur)); } catch (Exception) { }
    }

    // The OS/user pressed a transport button. Routed to the UI thread by ButtonDispatcher, then translated into a player
    // intent (the same intents the on-screen transport buttons fire). Seek is not carried by SMTC ButtonPressed.
    void OnButton(MediaButton button)
    {
        if (_disposed) return;
        switch (button)
        {
            case MediaButton.Play: _ = _player.ResumeAsync(); break;
            case MediaButton.Pause: _ = _player.PauseAsync(); break;
            case MediaButton.Next: _ = _player.NextAsync(); break;
            case MediaButton.Previous: _ = _player.PreviousAsync(); break;
            case MediaButton.Stop: _ = _player.PauseAsync(); break;   // no Stop verb in the player — pause is the closest
            default: break;   // Unknown (record/ff/rewind/channel) — ignore
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var smtc = _smtc;
        _smtc = null;
        if (smtc is not null)
        {
            try { smtc.ButtonPressed -= OnButton; } catch (Exception) { }
            try { smtc.IsEnabled = false; } catch (Exception) { }
            try { smtc.Dispose(); } catch (Exception) { }
        }
    }
}
