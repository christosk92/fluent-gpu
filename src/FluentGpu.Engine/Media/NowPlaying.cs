using System;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// The now-playing surface (spec §10) — SMTC-shaped, opt-in, orthogonal. Auto-populated from source metadata by default;
/// the SMTC bridge (an OS-services pillar) is JUST ANOTHER CONSUMER of the same headless player. macOS
/// <c>MPNowPlayingInfoCenter</c> binds identically.
/// </summary>
public sealed class NowPlaying
{
    /// <summary>Whether now-playing/SMTC publishing is enabled (auto-populated from source metadata by default).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The current metadata (Title/Artist/Album/multi-res Artwork).</summary>
    public MediaMetadata Metadata { get; set; } = MediaMetadata.Empty;

    /// <summary>The last position/duration/rate the engine published (kept fresh by the player).</summary>
    public (TimeSpan Position, TimeSpan Duration, double Rate) PositionState { get; private set; }

    /// <summary>Update the position state the OS reflects (the engine keeps this fresh).</summary>
    public void SetPositionState(TimeSpan position, TimeSpan duration, double rate) => PositionState = (position, duration, rate);

    /// <summary>Invoked when the OS/user requests "next".</summary>
    public Action? OnNextRequested { get; set; }
    /// <summary>Invoked when the OS/user requests "previous".</summary>
    public Action? OnPreviousRequested { get; set; }
    /// <summary>Invoked when the OS/user requests a seek.</summary>
    public Action<TimeSpan>? OnSeekRequested { get; set; }
}

/// <summary>The capability bitset (spec §10) — a live stream reports <see cref="MediaCommandFlags"/> without
/// <see cref="MediaCommandFlags.Seek"/>, and the seekbar disables itself off the signal with no per-backend branching.</summary>
[Flags]
public enum MediaCommandFlags : uint
{
    /// <summary>No commands available.</summary>
    None = 0,
    /// <summary>Play.</summary>
    Play = 1,
    /// <summary>Pause.</summary>
    Pause = 2,
    /// <summary>Seek to a position.</summary>
    Seek = 4,
    /// <summary>Seek backward (skip-back).</summary>
    SeekBackward = 8,
    /// <summary>Seek forward (skip-forward).</summary>
    SeekForward = 16,
    /// <summary>Change the playback rate.</summary>
    Rate = 32,
    /// <summary>Skip to next queue item.</summary>
    Next = 64,
    /// <summary>Skip to previous queue item.</summary>
    Previous = 128,
    /// <summary>Select an audio track.</summary>
    SelectAudioTrack = 256,
    /// <summary>Select a text track.</summary>
    SelectTextTrack = 512,
    /// <summary>Select a video quality/variant.</summary>
    SelectVideoQuality = 1024,
    /// <summary>Step a single frame.</summary>
    StepFrame = 2048,
    /// <summary>Enter picture-in-picture.</summary>
    PictureInPicture = 4096,
    /// <summary>Cast to a remote device.</summary>
    Cast = 8192
}

/// <summary>The capability bitset surface (spec §10). One control kit drives file/stream/live/cast backends and greys out
/// unsupported affordances generically off <see cref="Available"/>.</summary>
public sealed class MediaCommands
{
    private readonly Signal<MediaCommandFlags> _available;

    /// <summary>Create a command set with an initial availability bitset.</summary>
    public MediaCommands(MediaCommandFlags initial = MediaCommandFlags.None) => _available = new(initial);

    /// <summary>The available commands (bind the seekbar/transport off this).</summary>
    public IReadSignal<MediaCommandFlags> Available => _available;
    /// <summary>Test whether <paramref name="cmd"/> is available.</summary>
    public bool Can(MediaCommandFlags cmd) => (_available.Peek() & cmd) == cmd;
    /// <summary>Set the available command bitset (the backend is the sole writer).</summary>
    public void Set(MediaCommandFlags flags) => _available.Value = flags;
}
