using System;
using System.IO;
using Xunit;

namespace Wavee.Tests;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// SILENT MUTE LOSS ON VIDEO — regression suite.
//
// The bug: FluentVideoMediaHost declared `readonly bool _muted` and NOTHING ever assigned it (a live CS0649). Every player
// the host built was therefore hardwired unmuted through `built.SetMuted(_muted)`, so muting the app and then starting a
// music video played it at full volume — and there was no way to mute it, because mute only reaches the app through
// IAudioOutputDeviceControl, which only the AUDIO host implements.
//
// The fix has two halves, pinned here:
//   • FluentVideoMediaHost.SetMuted(bool)  — stores the intent AND forwards it to the live player, so the flag re-applied
//                                            at build time is real state (a player built after SetMuted(true) opens muted).
//   • LocalMediaOutputControl              — the composite the composition root hands to the picker service: every member
//                                            is the audio host's control verbatim EXCEPT SetOutputMuted, which fans out to
//                                            the video host too.
//
// SOURCE-PINNED, deliberately: FluentVideoMediaHost is not constructible headlessly (it needs the full MF/PlayReady stack,
// which is why the file is not source-included in this test assembly) — the same reason
// VideoLoadSupersessionTests.TeardownAsync_UnbindsSurface_BeforeDispose pins its ordering contract against the source text.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class VideoMuteTests
{
    // bin\<cfg>\<tfm> → src\apps\Wavee
    static readonly string AppRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Wavee"));

    static string AppSource(params string[] parts)
    {
        string path = Path.Combine(AppRoot, Path.Combine(parts));
        Assert.True(File.Exists(path), $"missing app source at {path}");
        return File.ReadAllText(path);
    }

    static string VideoHostSource() => AppSource("SpotifyLive", "Audio", "FluentVideoMediaHost.cs");

    // ── (1) the field itself: a readonly-never-assigned _muted is exactly the bug ──────────────────────────────────────
    [Fact]
    public void MutedField_IsAssignable_NotReadonlyNeverAssigned()
    {
        string src = VideoHostSource();
        Assert.DoesNotContain("readonly bool _muted", src, StringComparison.Ordinal);
        Assert.Contains("bool _muted;", src, StringComparison.Ordinal);
    }

    // ── (2) SetMuted stores the intent AND forwards it to the live player (the SetVolume shape) ────────────────────────
    [Fact]
    public void SetMuted_StoresIntent_AndForwardsToLivePlayer()
    {
        string src = VideoHostSource();
        int set = src.IndexOf("public void SetMuted(bool muted)", StringComparison.Ordinal);
        Assert.True(set >= 0, "FluentVideoMediaHost.SetMuted(bool) is missing");
        int end = src.IndexOf("── video-specific load", set, StringComparison.Ordinal);
        Assert.True(end > set, "could not delimit the SetMuted body");
        string body = src.Substring(set, end - set);
        Assert.Contains("_muted = muted;", body, StringComparison.Ordinal);          // stored → a later build re-applies it
        Assert.Contains("p.SetMuted(_muted)", body, StringComparison.Ordinal);       // forwarded → a LIVE player toggles now
    }

    // ── (3) a player built AFTER SetMuted(true) starts muted ──────────────────────────────────────────────────────────
    [Fact]
    public void BuiltPlayer_AdoptsCurrentMuteState()
    {
        string src = VideoHostSource();
        int build = src.IndexOf("async System.Threading.Tasks.Task BuildAndOpenAsync", StringComparison.Ordinal);
        Assert.True(build >= 0, "BuildAndOpenAsync not found");
        int publish = src.IndexOf("_pump.IsStale(epoch)", build, StringComparison.Ordinal);
        Assert.True(publish > build, "could not delimit the build section");
        string body = src.Substring(build, publish - build);
        // the mute intent is applied to the freshly-built player BEFORE it is published/opened — same as the volume
        Assert.Contains("built.SetVolume(_volume)", body, StringComparison.Ordinal);
        Assert.Contains("built.SetMuted(_muted)", body, StringComparison.Ordinal);
    }

    // ── (4) the fan-out composite: mute reaches BOTH hosts, everything else stays the audio host's ─────────────────────
    [Fact]
    public void LocalMediaOutputControl_FansMuteOut_ToBothHosts()
    {
        string src = VideoHostSource();
        int type = src.IndexOf("class LocalMediaOutputControl", StringComparison.Ordinal);
        Assert.True(type >= 0, "the LocalMediaOutputControl composite is missing");
        int mute = src.IndexOf("public void SetOutputMuted(bool muted)", type, StringComparison.Ordinal);
        Assert.True(mute > type, "LocalMediaOutputControl.SetOutputMuted is missing");
        string body = src.Substring(mute);
        Assert.Contains("_audio.SetOutputMuted(muted)", body, StringComparison.Ordinal);
        Assert.Contains("_video.SetMuted(muted)", body, StringComparison.Ordinal);
    }

    // ── (5) the routing leg: the composition root actually hands that composite to the picker service ──────────────────
    // Wiring the picker to `audio.Host` directly is what made the fan-out unreachable; pin the seam, not just the class.
    [Fact]
    public void CompositionRoot_WiresPicker_ThroughTheCompositeControl()
    {
        string live = AppSource("SpotifyLive", "LiveSessionHost.cs");
        Assert.Contains("connect.OutputDeviceControl is { } odc", live, StringComparison.Ordinal);
        Assert.DoesNotContain("audio.Host is IAudioOutputDeviceControl", live, StringComparison.Ordinal);

        string connect = AppSource("SpotifyLive", "LiveConnect.cs");
        Assert.Contains("new LocalMediaOutputControl(", connect, StringComparison.Ordinal);
    }
}
