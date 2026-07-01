using System.Collections.Generic;
using System.Linq;
using Wavee.Backend.Lyrics;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

// Pure-core tests for the lyrics feed: parsers (LRC/TTML), normalization, and the reranker decision engine.
// No network — the network sources are exercised separately with a fake ILyricHttp.
public class LyricsCoreTests
{
    // ── parsers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lrc_LineSynced_DropsMetadata_AndParsesTimes()
    {
        const string lrc = "[ti:Song]\n[ar:Artist]\n[by:someone]\n[00:01.50]First line\n[00:05.00]Second line\n";
        var doc = LyricsText.ParseLrc(lrc, "t1");

        Assert.True(doc.IsSynced);
        Assert.Equal(LyricsSyncKind.Line, doc.Sync);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(1500, doc.Lines[0].StartMs);
        Assert.Equal("First line", doc.Lines[0].Text);
        Assert.Equal(5000, doc.Lines[1].StartMs);
        Assert.Equal(5000, doc.Lines[0].EndMs);   // derived from next line start
    }

    [Fact]
    public void Lrc_Enhanced_ProducesSyllables()
    {
        const string lrc = "[00:00.00]<00:00.10>Hel<00:00.50>lo <00:00.90>world\n";
        var doc = LyricsText.ParseLrc(lrc, "t1");

        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        var line = doc.Lines[0];
        Assert.True(line.IsWordByWord);
        Assert.Equal("Hello world", line.Text);
        Assert.Equal(3, line.Syllables.Count);
        Assert.Equal(100, line.Syllables[0].StartMs);
        Assert.Equal(500, line.Syllables[0].EndMs);   // next syllable start
        Assert.Equal("world", line.Syllables[2].Text.Trim());
    }

    [Fact]
    public void Lrc_Offset_ShiftsTimestampsEarlier()
    {
        const string lrc = "[offset:500]\n[00:02.00]Line\n";
        var doc = LyricsText.ParseLrc(lrc, "t1");
        Assert.Equal(1500, doc.Lines[0].StartMs);   // 2000 - 500
    }

    [Fact]
    public void Ttml_WordSynced_ParsesSpans()
    {
        const string ttml =
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\">" +
            "<body><div>" +
            "<p begin=\"0:01.000\" end=\"0:03.000\"><span begin=\"0:01.000\" end=\"0:02.000\">Hello</span> <span begin=\"0:02.000\" end=\"0:03.000\">world</span></p>" +
            "<p begin=\"0:03.000\" end=\"0:05.000\">Second line</p>" +
            "</div></body></tt>";
        var doc = LyricsText.ParseTtml(ttml, "t1");

        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(1000, doc.Lines[0].StartMs);
        Assert.Equal(3000, doc.Lines[0].EndMs);
        Assert.True(doc.Lines[0].IsWordByWord);
        Assert.Equal(2, doc.Lines[0].Syllables.Count);
        Assert.Equal("Hello world", doc.Lines[0].Text);
        Assert.False(doc.Lines[1].IsWordByWord);   // plain <p>, no spans
    }

    [Fact]
    public void Credit_Lines_Detected_And_Normalize_Strips()
    {
        Assert.True(LyricsText.IsCreditLine("Lyrics by: Some Writer"));
        Assert.True(LyricsText.IsCreditLine("作词 : 林夕"));
        Assert.False(LyricsText.IsCreditLine("I've written you a love song"));   // 'written' but no separator → real lyric
        Assert.Equal("hello world", LyricsText.Normalize("Hello, World!"));
    }

    // ── reranker ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static LyricSyllable[] Words(params (long s, long e, string t)[] w)
        => w.Select(x => new LyricSyllable(x.s, x.e, x.t)).ToArray();

    static LyricsDocument LineDoc(string provider, params (long ms, string text)[] lines)
        => new("t1", true, lines.Select(l => new LyricLine(l.ms, l.text, System.Array.Empty<LyricSyllable>())).ToList(),
            LyricsSyncKind.Line, provider);

    static LyricsDocument WordDoc(string provider, params (long ms, string text)[] lines)
        => new("t1", true, lines.Select(l => new LyricLine(l.ms, l.text,
            Words((l.ms, l.ms + 400, l.text)), l.ms + 800, IsWordByWord: true)).ToList(),
            LyricsSyncKind.Syllable, provider);

    static LyricsCandidate Cand(string id, double prior, MatchBasis basis, LyricsDocument doc)
        => new(id, prior, basis, doc);

    static readonly (long, string)[] RealSong =
    {
        (1000, "hello darkness my old friend"),
        (5000, "ive come to talk with you again"),
        (9000, "because a vision softly creeping"),
        (13000, "left its seeds while i was sleeping"),
    };

    static LyricsDocument Reference() => LineDoc("spotify", RealSong);

    [Fact]
    public void Reranker_WrongSongWordSynced_LosesToCorrectLine()
    {
        var reference = Reference();
        // A grey (metadata-searched) source returned a WRONG song's karaoke; the sync gate must demote it so the correct
        // line lyric wins. (Identity/ISRC sources are exempt — see Reranker_IdentityWordSync_ExemptFromGate.)
        var wrong = Cand("netease", 1.0, MatchBasis.MetadataSearch, WordDoc("netease",
            (1000, "never gonna give you up"), (4000, "never gonna let you down"), (7000, "never gonna run around")));
        var correct = Cand("lrclib", 0.4, MatchBasis.MetadataSearch, LineDoc("lrclib", RealSong));

        var ranked = LyricsReranker.Rank(new[] { wrong, correct }, reference);

        Assert.NotNull(ranked.Winner);
        Assert.Equal("lrclib", ranked.Best!.ProviderId);   // wrong-song syllable demoted by the sync gate (text below the floor)
    }

    [Fact]
    public void Reranker_LooseGate_AcceptsDivergentWordSync()
    {
        var reference = Reference();
        // SAME song, word-synced, but only partial text agreement (a romanized / differently-transcribed grey candidate):
        // 2 of 4 lines match → text ~0.5. The old 0.80 bar demoted this; the 0.15 floor keeps its sync tier.
        var syl = Cand("netease", 0.5, MatchBasis.MetadataSearch, WordDoc("netease",
            (1000, "hello darkness my old friend"), (5000, "ive come to talk with you again"),
            (9000, "totally unrelated filler line"), (13000, "another unrelated filler line")));

        var d = LyricsReranker.Rank(new[] { syl }, reference).All.Single(x => x.ProviderId == "netease");
        Assert.DoesNotContain("sync-gate:demoted", d.Reason);
    }

    [Fact]
    public void Reranker_LooseGate_StillDemotesWrongWordSync()
    {
        var reference = Reference();
        // Different song entirely → text ≈ 0 (below the floor) → demoted even though it is word-synced.
        var wrong = Cand("netease", 0.5, MatchBasis.MetadataSearch, WordDoc("netease",
            (1000, "never gonna give you up"), (4000, "never gonna let you down"), (7000, "never gonna run around")));

        var d = LyricsReranker.Rank(new[] { wrong }, reference).All.Single(x => x.ProviderId == "netease");
        Assert.Contains("sync-gate:demoted", d.Reason);
    }

    [Fact]
    public void Reranker_IdentityWordSync_ExemptFromGate()
    {
        var reference = Reference();
        // AMLL is identity-matched (the exact Spotify track) but its transcription diverges from Spotify's line lyric
        // (text ≈ 0). It must NOT be demoted — it IS the recording. (ISRC-matched Musixmatch is exempt the same way.)
        var amll = Cand("amll", 0.9, MatchBasis.Identity, WordDoc("amll",
            (1000, "totally different transcription aaa"), (5000, "totally different transcription bbb"),
            (9000, "totally different transcription ccc"), (13000, "totally different transcription ddd")));

        var d = LyricsReranker.Rank(new[] { amll }, reference).All.Single(x => x.ProviderId == "amll");
        Assert.DoesNotContain("sync-gate:demoted", d.Reason);
    }

    [Fact]
    public void Reranker_GloballyOffsetLrc_IsCorrectedAgainstReference()
    {
        var reference = Reference();
        // same song, every line +700ms late
        var offsetCand = Cand("lrclib", 0.4, MatchBasis.MetadataSearch,
            LineDoc("lrclib", RealSong.Select(l => (l.Item1 + 700, l.Item2)).ToArray()));

        var ranked = LyricsReranker.Rank(new[] { offsetCand }, reference);

        Assert.Equal(-700, ranked.Best!.AppliedOffsetMs);
        Assert.Equal(1000, ranked.Winner!.Lines[0].StartMs);   // pulled back onto the reference
        Assert.Equal(-700, ranked.Winner.OffsetMsApplied);
    }

    [Fact]
    public void Reranker_TimingFallback_TrustedDivergentText_IsCorrected()
    {
        var reference = Reference();
        var divergent = Cand("musixmatch", 0.9, MatchBasis.Isrc, WordDoc("musixmatch",
            (1500, "totally different transcription aaa"),
            (5500, "totally different transcription bbb"),
            (9500, "totally different transcription ccc"),
            (13500, "totally different transcription ddd")));

        var ranked = LyricsReranker.Rank(new[] { divergent }, reference);

        Assert.Equal(-500, ranked.Best!.AppliedOffsetMs);
        Assert.Contains("timing-fallback", ranked.Best.Reason);
        Assert.Equal(1000, ranked.Winner!.Lines[0].StartMs);
        Assert.Equal(1000, ranked.Winner.Lines[0].Syllables[0].StartMs);
        Assert.Equal(1400, ranked.Winner.Lines[0].Syllables[0].EndMs);
        Assert.Equal(-500, ranked.Winner.OffsetMsApplied);
    }

    [Fact]
    public void Reranker_CorrectWordSynced_BeatsSpotifyLine()
    {
        var reference = Reference();
        var amll = Cand("amll", 1.0, MatchBasis.Identity, WordDoc("amll", RealSong));   // same text + coherent timing
        var spotify = Cand("spotify", 0.5, MatchBasis.Identity, reference);

        var ranked = LyricsReranker.Rank(new[] { spotify, amll }, reference);

        Assert.Equal("amll", ranked.Best!.ProviderId);   // verified word-sync wins the sync tier
        Assert.Equal(LyricsSyncKind.Syllable, ranked.Winner!.Sync);
    }

    [Fact]
    public void Reranker_NoCandidates_ReturnsNull()
    {
        var ranked = LyricsReranker.Rank(System.Array.Empty<LyricsCandidate>(), null);
        Assert.Null(ranked.Winner);
    }
}
