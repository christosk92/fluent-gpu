using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// Pins the fix for the video-load UI freeze (<c>docs/plans/...</c> "the video is so serene" plan, work item 1):
/// <c>VideoMediaEngine.Invoke&lt;T&gt;</c> marshals every <c>IMFMediaEngine</c> call from the caller (the UI thread,
/// 1-5x per pump while a video is opening) onto a dedicated MTA engine thread and waits for the answer. That wait
/// used to be a bare <c>done.Wait()</c> — unbounded, with no timeout and no cancellation — which queues behind
/// Media Foundation's internal engine lock (held for the whole of asynchronous source resolution) and, per this
/// file's own class doc comment, sits on a non-pumping wait on an OleInitialize'd STA: exactly the condition the
/// comment says DEADLOCKS MF's video-device setup. So a bare <c>.Wait()</c> here is not just a slow frame, it is a
/// documented path to a permanent hang.
///
/// <para>Source-scanned (the <c>StageLayoutTests</c> idiom) rather than exercised end-to-end: driving the real
/// <c>IMFMediaEngine</c> COM object needs a live Media Foundation session and a video source, which this managed
/// test project cannot stand up. The invariant that matters — "every wait on the invoke path is bounded" — is
/// fully visible in the source text, so that is what is pinned, with a comment explaining why an unbounded wait
/// specifically on THIS path is a deadlock hazard and not just a style nit.</para>
/// </summary>
public class VideoEngineInvokeTests
{
    /// <summary>No bare, unbounded <c>.Wait()</c> anywhere in the <c>Invoke&lt;T&gt;</c> method body — every
    /// <c>.Wait(</c> call on this seam must pass an argument (a timeout), so a future edit cannot silently
    /// reintroduce the UI-thread deadlock hazard by dropping the timeout argument back to a parameterless wait.
    /// Scoped to the method body (not the whole file) on purpose: <c>Initialize</c>'s own one-shot
    /// <c>_initDone.Wait()</c> — waiting for the engine thread to finish start-up before <c>Initialize</c>
    /// returns — is a different call, made once off the per-frame pump path, and is out of scope for this
    /// plan item.</summary>
    [Fact]
    public void Invoke_NeverBareWaits_OnTheEngineCallSeam()
    {
        string? path = FindVideoMediaEngineSource();
        if (path is null) { Assert.Skip("engine sources not present (binary-only run)"); return; }

        string[] method = InvokeMethodBody(File.ReadAllLines(path));
        Assert.True(method.Length > 0, "could not locate 'private T Invoke<T>(Func<T> f)' in VideoMediaEngine.cs");

        var bareWaits = new System.Collections.Generic.List<string>();
        for (int i = 0; i < method.Length; i++)
        {
            // A bare wait is "<something>.Wait()" with nothing between the parens — NOT "slot.Done.Wait(50)" or
            // "task.Wait(cts.Token)". Comments/prose may legitimately mention ".Wait()" (this file's own doc
            // comments do, describing the bug being fixed), so only code is scanned.
            string code = Code(method[i]);
            if (BareWait.IsMatch(code)) bareWaits.Add(method[i].Trim());
        }
        Assert.True(bareWaits.Count == 0,
            "VideoMediaEngine.Invoke<T> must never block the caller (often the UI thread, mid-video-load, "
            + "queued behind Media Foundation's engine lock — the STA deadlock this file's own class doc "
            + "comment documents) on an unbounded wait. Found bare .Wait() at:\n  "
            + string.Join("\n  ", bareWaits));
    }

    /// <summary>Extracts the <c>private T Invoke&lt;T&gt;(Func&lt;T&gt; f) { ... }</c> method body by brace
    /// balance, so the scan above cannot see unrelated waits elsewhere in the file (e.g. <c>Initialize</c>'s
    /// one-shot start-up wait).</summary>
    static string[] InvokeMethodBody(string[] lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains("private T Invoke<T>(Func<T> f)", System.StringComparison.Ordinal)) { start = i; break; }
        if (start < 0) return System.Array.Empty<string>();

        var body = new System.Collections.Generic.List<string>();
        int depth = 0; bool opened = false;
        for (int i = start; i < lines.Length; i++)
        {
            body.Add(lines[i]);
            foreach (char c in lines[i])
            {
                if (c == '{') { depth++; opened = true; }
                else if (c == '}') depth--;
            }
            if (opened && depth == 0) break;
        }
        return body.ToArray();
    }

    /// <summary>And the bounded timeout constant this test relies on actually exists and is used by the wait —
    /// pinning the shape of the fix, not just the absence of the bug.</summary>
    [Fact]
    public void Invoke_UsesABoundedTimeoutConstant()
    {
        string? path = FindVideoMediaEngineSource();
        if (path is null) { Assert.Skip("engine sources not present (binary-only run)"); return; }

        string text = File.ReadAllText(path);
        Assert.Contains("InvokeTimeoutMs", text);
        Assert.Matches(new Regex(@"\.Wait\(\s*InvokeTimeoutMs\s*\)"), text);
    }

    static readonly Regex BareWait = new(@"\.Wait\(\s*\)", RegexOptions.Compiled);

    /// <summary>Strips <c>//</c> line comments the same way the existing <c>StageLayoutTests.Code</c> helper does,
    /// so prose describing the old bug (which legitimately says "done.Wait()") cannot trip the scan.</summary>
    static string Code(string line)
    {
        int i = line.IndexOf("//", System.StringComparison.Ordinal);
        return i < 0 ? line : line[..i];
    }

    /// <summary>src/FluentGpu.Windows/Media/VideoMediaEngine.cs, located from THIS file's compile-time path (the
    /// <c>StageLayoutTests.AppSourceRoot</c> idiom, walked up one more level from src/apps to src/).</summary>
    static string? FindVideoMediaEngineSource([CallerFilePath] string here = "")
    {
        string? testsDir = Path.GetDirectoryName(here);              // .../src/apps/Wavee.Tests
        string? appsDir = testsDir is null ? null : Path.GetDirectoryName(testsDir);   // .../src/apps
        string? srcDir = appsDir is null ? null : Path.GetDirectoryName(appsDir);       // .../src
        if (srcDir is null) return null;

        string candidate = Path.Combine(srcDir, "FluentGpu.Windows", "Media", "VideoMediaEngine.cs");
        return File.Exists(candidate) ? candidate : null;
    }
}
