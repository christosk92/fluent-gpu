using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class SubtitleParserTests
{
    [Fact]
    public void WebVtt_ParsesIdentifiersSettingsMarkupAndMinuteTimestamps()
    {
        const string vtt = "WEBVTT\n\nintro\n00:01.250 --> 00:03.500 align:center\n<v Speaker>Hello &amp; welcome</v>\n\n00:00:04.000 --> 00:00:05.000\nSecond";
        CueTrack track = SubtitleLoader.ParseWebVtt(vtt);

        Assert.Equal(2, track.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), track[0].Start);
        Assert.Equal("Hello & welcome", track[0].Text);
        track.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("Hello & welcome", track.ActiveCue.Peek()!.Value.Text);
        track.Advance(TimeSpan.FromSeconds(3.75));
        Assert.Null(track.ActiveCue.Peek());
    }

    [Fact]
    public void Srt_ParsesCommaMillisecondsAndMultilineCue()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:02,250\r\nFirst\r\nline\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nSecond";
        CueTrack track = SubtitleLoader.ParseSrt(srt);

        Assert.Equal(2, track.Count);
        Assert.Equal("First\nline", track[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2250), track[0].End);
    }

    // Windowed in-band cue fetch (WS-MediaUI #9): the fetch window is a few segments AROUND the playhead, NOT the whole
    // track upfront, and it ADVANCES as the playhead moves.
    private static readonly TimeSpan[] TenSegmentsAt6s = BuildSegments(10, 6);
    private static TimeSpan[] BuildSegments(int count, double eachSeconds)
    {
        var s = new TimeSpan[count];
        for (int i = 0; i < count; i++) s[i] = TimeSpan.FromSeconds(i * eachSeconds);
        return s;
    }

    [Fact]
    public void CueWindow_AtStart_DoesNotFetchWholeTrackUpfront()
    {
        // 10 six-second segments; at position 0 the window is only [0, ahead+1) — never the whole track.
        (int lo, int hi) = SubtitleLoader.CueWindow(TenSegmentsAt6s, TimeSpan.Zero, behind: 1, ahead: 3);
        Assert.Equal(0, lo);
        Assert.Equal(4, hi);                       // segments 0..3 (current + 3 ahead), NOT 0..9
        Assert.True(hi - lo < TenSegmentsAt6s.Length);
    }

    [Fact]
    public void CueWindow_AdvancesWithPlayhead()
    {
        // At ~30s the current segment is index 5 (30/6); the window rides ahead/behind it and drops the early tail.
        (int lo, int hi) = SubtitleLoader.CueWindow(TenSegmentsAt6s, TimeSpan.FromSeconds(31), behind: 1, ahead: 3);
        Assert.Equal(4, lo);                       // segment 5 minus 1 behind
        Assert.Equal(9, hi);                        // segment 5 plus 3 ahead (+1 exclusive)
        Assert.False(lo == 0);                      // early segments are NOT re-fetched / kept in the active window

        // Near the end the window clamps to the track bounds (no over-run).
        (int lo2, int hi2) = SubtitleLoader.CueWindow(TenSegmentsAt6s, TimeSpan.FromSeconds(600), behind: 1, ahead: 3);
        Assert.Equal(8, lo2);                       // last index 9 minus 1 behind
        Assert.Equal(10, hi2);                      // clamped to count
    }

    [Fact]
    public void CueWindow_EmptyTrack_IsEmptyWindow()
    {
        (int lo, int hi) = SubtitleLoader.CueWindow(ReadOnlySpan<TimeSpan>.Empty, TimeSpan.FromSeconds(5), 1, 3);
        Assert.Equal(0, lo);
        Assert.Equal(0, hi);
    }
}
