using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Forms;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>An async field check that lands after the user typed again (inside the re-armed debounce, before the next
/// check cancels it) belongs to the older value: it must not set the server error or clear the validating flag.</summary>
public sealed class AsyncFieldStaleResultTests
{
    private static bool DrainOne(ConcurrentQueue<Action> q, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (q.TryDequeue(out var a)) { a(); return true; }
            Thread.Sleep(5);
        }
        return false;
    }

    [Fact]
    public void Older_values_verdict_is_dropped_after_a_newer_keystroke()
    {
        var rt = new ReactiveRuntime();
        var posts = new ConcurrentQueue<Action>();
        var postSig = new Signal<object?>((Action<Action>)(a => posts.Enqueue(a)));
        var c = new RenderContext
        {
            Runtime = rt,
            ResolveContextSignal = (_, ctx) => ReferenceEquals(ctx, HostDispatch.Post) ? postSig : null,
        };
        var bobStarted = new ManualResetEventSlim();
        var bobxStarted = new ManualResetEventSlim();
        var bob = new TaskCompletionSource<MsgId>();
        var bobx = new TaskCompletionSource<MsgId>();
        var value = new Signal<string>("");
        var opts = new FieldOptions<string>
        {
            AsyncDebounceMs = 500,
            Async = (v, _) =>
            {
                if (v == "bob") { bobStarted.Set(); return bob.Task; }
                bobxStarted.Set();
                return bobx.Task;
            },
        };
        c.BeginRender();
        var field = c.UseField(value, opts);
        rt.Flush();

        value.Value = "bob";
        rt.Flush();
        Assert.True(bobStarted.Wait(5000), "the check for 'bob' never started");

        value.Value = "bobx";                       // re-arms the debounce; 'bob' is still in flight
        rt.Flush();
        bob.SetResult(Msg.Literal("taken"));        // 'bob' answers inside the debounce window
        Assert.True(DrainOne(posts), "the 'bob' check posted nothing");
        rt.Flush();

        Assert.True(field.IsValidating.Value);      // 'bobx' has not been checked yet
        Assert.True(field.Error.Value.IsValid);     // and 'bob' being taken says nothing about 'bobx'

        Assert.True(bobxStarted.Wait(5000), "the check for 'bobx' never started");
        bobx.SetResult(MsgId.None);
        Assert.True(DrainOne(posts), "the 'bobx' check posted nothing");
        rt.Flush();
        Assert.False(field.IsValidating.Value);
        Assert.True(field.IsValid.Value);
    }
}
