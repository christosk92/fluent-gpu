using System;
using FluentGpu.WindowsApi.Power;
using Wavee.Core;
using Wavee.SpotifyLive;

namespace Wavee;

/// <summary>
/// Wavee's power-session policy: keep-awake while audio (or fullscreen video) is playing, pause + flush on suspend,
/// re-announce Connect on resume when reachable. Distinct from <see cref="AmbientPowerPolicy"/> (render cadence).
/// </summary>
/// <remarks>
/// <b>KeepAwake is per-thread.</b> Acquire and dispose the handle on the UI thread only — <c>SetThreadExecutionState</c>
/// is a per-calling-thread flag. OS <see cref="PowerSession.Suspending"/> / <see cref="PowerSession.Resumed"/> fire on
/// a power-broadcast worker; every handler hops through the stored <c>post</c> before touching playback or KeepAwake.
/// Fail-soft: nothing thrown out of an OS callback.
/// </remarks>
static class PowerBridge
{
    static readonly object Gate = new();
    static PlaybackBridge? _bridge;
    static IPlaybackPlayer? _player;
    static Action<Action>? _post;
    static Services? _services;
    static IDisposable? _subscription;
    static IDisposable? _keepAwake;
    static bool _awake;
    static bool _keepDisplay;
    static bool _attached;

    /// <summary>Composition-root install. Idempotent. Call from <see cref="WaveeApp"/> after <c>PlaybackBridge.Activate</c>.</summary>
    public static void Attach(PlaybackBridge bridge, Action<Action> post, Services services)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(services);
        lock (Gate)
        {
            if (_attached) return;
            _attached = true;
            _bridge = bridge;
            _player = bridge.Player;
            _post = post;
            _services = services;
        }

        NetworkPolicy.Install(services.Settings, post);

        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(8, 0))
            {
                PowerSession.Suspending += OnSuspending;
                PowerSession.Resumed += OnResumed;
                _subscription = PowerSession.Subscribe();
            }
        }
        catch
        {
            // Registration failed — playback still works; we just will not see suspend/resume.
            _subscription = null;
        }

        try
        {
            bool playing = bridge.IsPlaying.Peek();
            ApplyKeepAwake(playing, playing && IsFullscreenVideo(bridge, subscribe: false));
        }
        catch { }
    }

    /// <summary>No-op when <see cref="Attach"/> already ran (the WaveeApp composition root). Parameterless fallback.</summary>
    public static void TryInstallFromContext() { }

    /// <summary>
    /// Edge-triggered keep-awake from <see cref="PlaybackBridge.IsPlaying"/> + <see cref="PlaybackBridge.VideoSurface"/>.
    /// Call from an auto-tracked <c>UseEffect</c> so signal reads subscribe the effect, not a component render.
    /// </summary>
    public static void SyncFromSignals()
    {
        var bridge = _bridge;
        if (bridge is null) return;
        try
        {
            bool playing = bridge.IsPlaying.Value;
            ApplyKeepAwake(playing, playing && IsFullscreenVideo(bridge, subscribe: true));
        }
        catch { }
    }

    static bool IsFullscreenVideo(PlaybackBridge bridge, bool subscribe)
    {
        var s = subscribe ? bridge.VideoSurface.Value : bridge.VideoSurface.Peek();
        return s.Requested == SurfacePlacement.Fullscreen || s.Live == SurfacePlacement.Fullscreen;
    }

    static void ApplyKeepAwake(bool playing, bool keepDisplayOn)
    {
        if (!playing)
        {
            DropKeepAwake();
            return;
        }
        if (_awake && _keepDisplay == keepDisplayOn) return;
        DropKeepAwake();
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(8, 0)) return;
            _keepAwake = PowerSession.KeepAwake(keepDisplayOn);
            _awake = true;
            _keepDisplay = keepDisplayOn;
        }
        catch
        {
            _keepAwake = null;
            _awake = false;
            _keepDisplay = false;
        }
    }

    static void DropKeepAwake()
    {
        try { _keepAwake?.Dispose(); }
        catch { }
        _keepAwake = null;
        _awake = false;
        _keepDisplay = false;
    }

    static void OnSuspending()
    {
        try { _post?.Invoke(OnSuspendingUi); }
        catch { }
    }

    static void OnResumed()
    {
        try { _post?.Invoke(OnResumedUi); }
        catch { }
    }

    static void OnSuspendingUi()
    {
        try
        {
            DropKeepAwake();
            // Pause first so a later playback-snapshot writer sees Paused; then fsync the session document.
            if (_player is { } player)
            {
                try { _ = player.PauseAsync(); }
                catch { }
            }
            SessionSnapshotStore.FlushActive();
        }
        catch { }
    }

    static void OnResumedUi()
    {
        try
        {
            var bridge = _bridge;
            if (bridge is not null)
            {
                bool playing = bridge.IsPlaying.Peek();
                ApplyKeepAwake(playing, playing && IsFullscreenVideo(bridge, subscribe: false));
            }
            ReannounceConnect();
        }
        catch { }
    }

    /// <summary>
    /// TODO(T1.4): <c>DeviceStatePublisher.PublishAsync(PutStateReasonKind.NewConnection)</c> is private and
    /// <see cref="LiveConnect"/> does not expose the publisher. A resume re-announce is therefore not wired.
    /// When Connect grows a public <c>AnnounceNewConnection</c>, call it here from the UI thread.
    /// </summary>
    public static void ReannounceConnect()
    {
        // Reachable instance: Services.LiveHost.Connect — but the only public publish is RepublishPlayerState
        // (PlayerStateChanged), which is not a NewConnection announce. Intentionally not a substitute.
        _ = _services?.LiveHost?.Connect;
    }

    public static void Shutdown()
    {
        try { PowerSession.Suspending -= OnSuspending; } catch { }
        try { PowerSession.Resumed -= OnResumed; } catch { }
        try { _subscription?.Dispose(); } catch { }
        _subscription = null;
        DropKeepAwake();
        NetworkPolicy.Shutdown();
        lock (Gate)
        {
            _attached = false;
            _bridge = null;
            _player = null;
            _post = null;
            _services = null;
        }
    }
}
