using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Lyrics;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

// LyricsDiskCache (wave H): the persistent half of the aggregator's winner cache — a versioned JSON envelope per track
// under a cache root, read through BEFORE the source fan-out so a previously-played track resolves offline.
//
// Every test injects its own temp directory (and, where TTL matters, its own clock); the real %LOCALAPPDATA% is never
// touched. The last two tests drive the cache THROUGH AggregatingLyricsProvider, which is what actually pins the
// offline promise: a second provider over the same directory must serve without calling a single source.
public class LyricsDiskCacheTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-lyrics-cache-tests", Guid.NewGuid().ToString("n"));

    public LyricsDiskCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    const long Now = 1753000000000L;
    LyricsDiskCache Cache(long? now = null, TimeSpan? negativeTtl = null, int maxFiles = LyricsDiskCache.DefaultMaxFiles)
        => new(_dir, nowUnixMs: () => now ?? Now, maxFiles: maxFiles, negativeTtl: negativeTtl);

    static LyricsDocument WordDoc(string trackId = "t1") => new(
        trackId, true,
        new List<LyricLine>
        {
            new(1000, "line one here",
                new List<LyricSyllable> { new(1000, 1400, "line"), new(1400, 1700, "one"), new(1700, 2200, "here") },
                EndMs: 2200, Translation: "eerste regel", Romanization: "rain wan hia", IsWordByWord: true),
            new(5000, "line two there", new List<LyricSyllable> { new(5000, 5800, "line two there") },
                EndMs: 5800, IsWordByWord: true),
        },
        LyricsSyncKind.Syllable, "amll", OffsetMsApplied: -500);

    static void AssertSameDocument(LyricsDocument expected, LyricsDocument actual)
    {
        Assert.Equal(expected.TrackId, actual.TrackId);
        Assert.Equal(expected.IsSynced, actual.IsSynced);
        Assert.Equal(expected.Sync, actual.Sync);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.OffsetMsApplied, actual.OffsetMsApplied);
        Assert.Equal(expected.Lines.Count, actual.Lines.Count);
        for (int i = 0; i < expected.Lines.Count; i++)
        {
            var e = expected.Lines[i];
            var a = actual.Lines[i];
            Assert.Equal(e.StartMs, a.StartMs);
            Assert.Equal(e.Text, a.Text);
            Assert.Equal(e.EndMs, a.EndMs);
            Assert.Equal(e.Translation, a.Translation);
            Assert.Equal(e.Romanization, a.Romanization);
            Assert.Equal(e.IsWordByWord, a.IsWordByWord);
            Assert.Equal(e.Syllables.Count, a.Syllables.Count);
            for (int s = 0; s < e.Syllables.Count; s++)
            {
                Assert.Equal(e.Syllables[s].StartMs, a.Syllables[s].StartMs);
                Assert.Equal(e.Syllables[s].EndMs, a.Syllables[s].EndMs);
                Assert.Equal(e.Syllables[s].Text, a.Syllables[s].Text);
            }
        }
    }

    // ── round trip ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_RestoresEveryFieldIncludingSyllablesAndSecondaryLines()
    {
        var cache = Cache();
        var doc = WordDoc();

        await cache.SaveAsync("t1", doc);
        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Hit, entry.Outcome);
        Assert.Equal(Now, entry.SavedAtUnixMs);
        AssertSameDocument(doc, entry.Document!);
    }

    [Fact]
    public async Task RoundTrip_SurvivesANewCacheInstance_AndTheFileIsOnDiskUnderTheTrackHash()
    {
        await Cache().SaveAsync("t1", WordDoc());

        Assert.True(File.Exists(Cache().PathFor("t1")));
        Assert.Equal(LyricsCacheOutcome.Hit, (await Cache().TryLoadAsync("t1")).Outcome);
    }

    [Fact]
    public async Task UnknownTrack_IsAMiss()
    {
        var entry = await Cache().TryLoadAsync("never-seen");
        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.Null(entry.Document);
    }

    // ── envelope discipline ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VersionMismatch_IsAMissAndDeletesTheFile()
    {
        var cache = Cache();
        await cache.SaveAsync("t1", WordDoc());
        string path = cache.PathFor("t1");

        string json = File.ReadAllText(path);
        string bumped = json.Replace("\"v\":" + LyricsDiskCache.SchemaVersion, "\"v\":999", StringComparison.Ordinal);
        Assert.NotEqual(json, bumped);   // the envelope really does carry the version we think it does
        File.WriteAllText(path, bumped);

        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.False(File.Exists(path));   // a superseded entry is dropped, not left to be re-read forever
    }

    [Fact]
    public async Task CorruptFile_IsAMissAndDeletesTheFile_WithoutThrowing()
    {
        var cache = Cache();
        string path = cache.PathFor("t1");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{\"v\":1,\"at\":123,\"doc\":{\"lines\":[{ truncated");

        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task EmptyDocument_IsAMissAndDeletesTheFile()
    {
        var cache = Cache();
        await cache.SaveAsync("t1", new LyricsDocument("t1", false, Array.Empty<LyricLine>(), LyricsSyncKind.None, "lrclib"));

        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.False(File.Exists(cache.PathFor("t1")));
    }

    // ── negative cache ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NegativeMarker_IsKnownMissingWhileFresh()
    {
        long now = Now;
        var cache = new LyricsDiskCache(_dir, nowUnixMs: () => now, negativeTtl: TimeSpan.FromDays(3));

        await cache.SaveAsync("t1", null);
        now += (long)TimeSpan.FromDays(2).TotalMilliseconds;
        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.KnownMissing, entry.Outcome);
        Assert.Null(entry.Document);
        Assert.Equal(Now, entry.SavedAtUnixMs);
        Assert.True(File.Exists(cache.PathFor("t1")));
    }

    [Fact]
    public async Task NegativeMarker_ExpiresToAMiss_AndIsDeletedSoTheNextPlayRefetches()
    {
        long now = Now;
        var cache = new LyricsDiskCache(_dir, nowUnixMs: () => now, negativeTtl: TimeSpan.FromDays(3));

        await cache.SaveAsync("t1", null);
        now += (long)TimeSpan.FromDays(4).TotalMilliseconds;
        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.False(File.Exists(cache.PathFor("t1")));
    }

    // ── LRU sweep ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_OverTheFileCap_TrimsToEightyPercent_OldestFirst()
    {
        const int cap = 20, written = 25, expected = 16;   // 80% of 20
        var cache = Cache(maxFiles: cap);
        var paths = new string[written];
        for (int i = 0; i < written; i++)
        {
            string id = "track-" + i;
            await cache.SaveAsync(id, WordDoc(id));
            paths[i] = cache.PathFor(id);
            // The save time IS the file's write time; stamp it explicitly so "oldest first" is deterministic.
            File.SetLastWriteTimeUtc(paths[i], new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }

        cache.Sweep();

        Assert.Equal(expected, Directory.EnumerateFiles(_dir, "*.json").Count());
        for (int i = 0; i < written - expected; i++) Assert.False(File.Exists(paths[i]), $"entry {i} should have been evicted");
        for (int i = written - expected; i < written; i++) Assert.True(File.Exists(paths[i]), $"entry {i} should have survived");
    }

    [Fact]
    public async Task Sweep_UnderTheCap_KeepsEverything()
    {
        var cache = Cache(maxFiles: 20);
        for (int i = 0; i < 5; i++) await cache.SaveAsync("track-" + i, WordDoc("track-" + i));

        cache.Sweep();

        Assert.Equal(5, Directory.EnumerateFiles(_dir, "*.json").Count());
    }

    [Fact]
    public async Task Sweep_IsArmedByTheFirstUse_AndOnlyOnce()
    {
        var cache = Cache(maxFiles: 20);
        Assert.True(cache.SweepInFlight.IsCompleted);   // nothing armed before first use

        await cache.SaveAsync("t1", WordDoc());
        await cache.SweepInFlight;                      // armed, and a read never waits on it in production

        Assert.True(File.Exists(cache.PathFor("t1")));
    }

    [Fact]
    public async Task Clear_DropsEveryEntryButLeavesForeignFilesAlone()
    {
        var cache = Cache();
        await cache.SaveAsync("t1", WordDoc("t1"));
        await cache.SaveAsync("t2", null);
        string foreign = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(foreign, "not ours");

        cache.Clear();

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.json"));
        Assert.True(File.Exists(foreign));
    }

    // ── through the aggregator: the actual offline promise ────────────────────────────────────────────────────────

    sealed class CountingSource : ILyricCandidateSource
    {
        readonly LyricsCandidate? _result;
        public int Calls;
        public CountingSource(string id, LyricsCandidate? result) { Id = id; _result = result; }
        public string Id { get; }
        public bool Enabled => true;
        public double Prior => 0.9;
        public Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(_result); }
    }

    static LyricsRequest Req() => new("t1", "spotify:track:t1", "Test Song", new[] { "Artist" }, "Album", 200000);

    async Task WaitForFile(string path, bool shouldExist)
    {
        for (int i = 0; i < 200 && File.Exists(path) != shouldExist; i++) await Task.Delay(10);
        Assert.Equal(shouldExist, File.Exists(path));
    }

    [Fact]
    public async Task Provider_SecondSession_ServesFromDisk_WithoutQueryingAnySource()
    {
        var doc = WordDoc();
        var first = new CountingSource("amll", new LyricsCandidate("amll", 0.9, MatchBasis.Identity, doc));
        var online = new AggregatingLyricsProvider(new ILyricCandidateSource[] { first }, (_, _) => Task.FromResult<LyricsRequest?>(Req()),
            diskCache: Cache());
        var live = await online.GetLyricsAsync("t1");
        Assert.NotNull(live);
        Assert.Equal(1, first.Calls);
        await WaitForFile(Cache().PathFor("t1"), shouldExist: true);

        // A fresh process (fresh provider, empty in-memory LRU) with NO working sources and NO resolvable request —
        // i.e. offline — still has the words.
        var offlineSource = new CountingSource("amll", null);
        var offline = new AggregatingLyricsProvider(new ILyricCandidateSource[] { offlineSource },
            (_, _) => Task.FromResult<LyricsRequest?>(null), diskCache: Cache());

        var restored = await offline.GetLyricsAsync("t1");

        Assert.NotNull(restored);
        AssertSameDocument(live!, restored!);   // exactly the document the online session won, syllables and all
        Assert.Equal(0, offlineSource.Calls);   // no fan-out at all
    }

    [Fact]
    public async Task Provider_NothingAnywhere_WritesANegativeMarker_AndTheNextSessionSkipsTheFanOut()
    {
        var missing = new CountingSource("amll", null);
        var provider = new AggregatingLyricsProvider(new ILyricCandidateSource[] { missing },
            (_, _) => Task.FromResult<LyricsRequest?>(Req()), diskCache: Cache());

        Assert.Null(await provider.GetLyricsAsync("t1"));
        Assert.Equal(1, missing.Calls);
        await WaitForFile(Cache().PathFor("t1"), shouldExist: true);

        var next = new CountingSource("amll", null);
        var second = new AggregatingLyricsProvider(new ILyricCandidateSource[] { next },
            (_, _) => Task.FromResult<LyricsRequest?>(Req()), diskCache: Cache());

        Assert.Null(await second.GetLyricsAsync("t1"));
        Assert.Equal(0, next.Calls);   // the negative marker answered inside its TTL
    }

    [Fact]
    public async Task Provider_ClearCache_ClearsDiskToo_SoTheNextRequestRefetches()
    {
        var doc = WordDoc();
        var src = new CountingSource("amll", new LyricsCandidate("amll", 0.9, MatchBasis.Identity, doc));
        var provider = new AggregatingLyricsProvider(new ILyricCandidateSource[] { src },
            (_, _) => Task.FromResult<LyricsRequest?>(Req()), diskCache: Cache());

        Assert.NotNull(await provider.GetLyricsAsync("t1"));
        await WaitForFile(Cache().PathFor("t1"), shouldExist: true);

        provider.ClearCache();

        Assert.False(File.Exists(Cache().PathFor("t1")));
        Assert.NotNull(await provider.GetLyricsAsync("t1"));
        Assert.Equal(2, src.Calls);   // memory AND disk were cleared, so the source ran again
    }
}
