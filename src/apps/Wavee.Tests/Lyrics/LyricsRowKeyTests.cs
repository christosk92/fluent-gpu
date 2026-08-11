using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Wavee.Tests.Lyrics;

// A SOURCE guard (the StageLayoutTests idiom) for an invariant that has no runtime seam to assert against: the lyrics
// list's row key must carry the document epoch, not just the line index.
//
// The failure it prevents is silent and total. Rows are `Embed.Comp(() => new LyricLineView(...))`, so their props —
// including the per-line emphasis and glow SIGNAL OBJECTS — freeze at mount. With a bare index key, swapping a 62-line
// document for a 35-line one (a background upgrade promoting a richer provider) reconciles rows ll0..ll34 IN PLACE
// instead of remounting them, while PrepareDocument's !sameShape branch reallocates every one of those arrays. The
// surviving rows are then subscribed to orphaned signals and never re-report their scene handles, so the document
// freezes: every line stuck blurred, no line ever becoming active, for the rest of the track. It looks like a renderer
// bug and it is a keying bug.
public class LyricsRowKeyTests
{
    static string Source() => File.ReadAllText(
        Path.Combine(AppSourceRoot(), "Features", "Player", "LyricsView.cs"));

    [Fact]
    public void RowKeyIncludesTheDocumentEpoch()
    {
        string src = Source();

        // The key builder exists and folds in the epoch…
        Assert.Matches(new Regex(@"string RowKey\(int \w+\)\s*=>.*_docEpoch", RegexOptions.Singleline), src);
        // …and both the element key and the virtualizer's keyOf go through it.
        Assert.Contains("Key = RowKey(idx)", src);
        Assert.Contains("keyOf: RowKey", src);
    }

    [Fact]
    public void NoBareIndexRowKeySurvives()
    {
        string src = Source();

        // The exact shapes that reintroduce the bug.
        Assert.DoesNotContain("Key = \"ll\" + idx", src);
        Assert.DoesNotContain("keyOf: i => \"ll\" + i", src);
    }

    [Fact]
    public void OnlyANonSameShapeSwapBumpsTheEpoch()
    {
        string src = Source();

        // Keeping same-shape rows mounted (and their subscriptions live) is the entire point of that branch — bumping
        // the epoch there would undo it.
        Assert.Contains("if (!sameShape) { _layout = null; _docEpoch++; }", src);
        Assert.Equal(1, Regex.Matches(src, @"_docEpoch\+\+").Count);
    }

    /// <summary>src/apps/Wavee, located from THIS file's compile-time path (the StageLayoutTests idiom).</summary>
    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        string lyricsDir = Path.GetDirectoryName(here)!;          // …/Wavee.Tests/Lyrics
        string testsDir = Path.GetDirectoryName(lyricsDir)!;      // …/Wavee.Tests
        return Path.Combine(Path.GetDirectoryName(testsDir)!, "Wavee");
    }
}
