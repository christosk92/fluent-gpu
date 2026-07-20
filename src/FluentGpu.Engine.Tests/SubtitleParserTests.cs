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
}
