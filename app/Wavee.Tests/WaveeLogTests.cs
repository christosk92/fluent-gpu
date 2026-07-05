using System;
using System.IO;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class WaveeLogTests
{
    [Fact]
    public void Snapshot_RetainsNewestEntries_InOrder()
    {
        var log = new WaveeLog(ringCapacity: 3) { MinLevel = WaveeLogLevel.Trace, FileMinLevel = WaveeLogLevel.Critical };

        log.Info("app", "one");
        log.Info("app", "two");
        log.Info("app", "three");
        log.Info("app", "four");

        Assert.Equal(["two", "three", "four"], log.Snapshot().Select(e => e.Message).ToArray());
    }

    [Fact]
    public void MinLevel_FiltersRingAndFile()
    {
        var log = new WaveeLog { MinLevel = WaveeLogLevel.Warning };

        log.Info("app", "ignored");
        log.Warn("app", "kept");

        var snap = log.Snapshot();
        Assert.Single(snap);
        Assert.Equal("kept", snap[0].Message);
    }

    [Fact]
    public void StructuredEntry_FormatsFieldsOperationAndSecrets()
    {
        var log = new WaveeLog { MinLevel = WaveeLogLevel.Trace };

        log.Event(WaveeLogLevel.Info, "audio", "key.result", "resolved",
            operationId: "play-1",
            elapsedMs: 42,
            fields:
            [
                WaveeLogField.Of("file", "abc123"),
                WaveeLogField.Secret("token"),
                WaveeLogField.Of("reason", "NeverProvisioned"),
            ]);

        string formatted = log.Snapshot()[0].Format();
        Assert.Contains("key.result", formatted);
        Assert.Contains("op=play-1", formatted);
        Assert.Contains("elapsed=42ms", formatted);
        Assert.Contains("token=***", formatted);
        Assert.DoesNotContain("spotify-token", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileSink_WritesInfoPlusStructuredLines()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "wavee.log");
            var log = new WaveeLog();
            log.Configure(path, minLevel: WaveeLogLevel.Trace, fileMinLevel: WaveeLogLevel.Info);

            log.Debug("app", "debug ignored by file");
            log.Event(WaveeLogLevel.Info, "connect", "session.start", "started",
                fields: [WaveeLogField.Of("interactive", true)]);
            log.FlushForTests();

            string text = File.ReadAllText(path);
            Assert.StartsWith("seq=", text);
            Assert.Contains("I [connect] session.start", text);
            Assert.Contains("interactive=true", text);
            Assert.DoesNotContain("Z seq=", text);
            Assert.DoesNotContain("debug ignored by file", text);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void FileSink_RollsWhenCurrentFileIsTooLarge()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "wavee.log");
            File.WriteAllText(path, new string('x', 128));
            var log = new WaveeLog();
            log.Configure(path, minLevel: WaveeLogLevel.Info, fileMinLevel: WaveeLogLevel.Info, maxFileBytes: 32, retainedFiles: 2);

            log.Info("app", "after roll");
            log.FlushForTests();

            Assert.Contains(Directory.GetFiles(dir, "wavee-*.log"), File.Exists);
            Assert.Contains("after roll", File.ReadAllText(path));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Secret_ToString_RedactsValue()
    {
        var secret = new Secret("spotify-token");
        Assert.Equal("***", secret.ToString());
        Assert.Equal("spotify-token", secret.Reveal());
    }

    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
