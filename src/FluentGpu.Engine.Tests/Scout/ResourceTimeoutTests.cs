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

/// <summary>A loader or async validator that throws <see cref="OperationCanceledException"/> on its OWN deadline (an
/// HttpClient timeout, a linked CancelAfter) is a failure, not a supersession: only a cancel of the hook's token may be
/// dropped silently.</summary>
public sealed class ResourceTimeoutTests
{
    // UI-thread stand-in: completions queue here and the test thread drains them (signals stay single-threaded).
    private static Action<Action> Queue(ConcurrentQueue<Action> q) => a => q.Enqueue(a);

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
    public void Resource_loader_timeout_settles_failed_and_stops_fetching()
    {
        var posts = new ConcurrentQueue<Action>();
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.", new TimeoutException());
        var cell = new ResourceCell<int>
        {
            Loadable = Loadable<int>.Pending(0),
            Cts = new CancellationTokenSource(),
            Loader = _ => Task.FromException<int>(timeout),
            Post = Queue(posts),
        };
        cell.Reload(keepPrevious: false);

        Assert.True(DrainOne(posts), "a loader timeout posted no completion");
        Assert.True(cell.Loadable.IsFailed);
        Assert.False(cell.IsFetchingSig.Peek());
        Assert.Same(timeout, cell.LastError);
    }

    [Fact]
    public void Resource_superseded_load_is_still_dropped()
    {
        var posts = new ConcurrentQueue<Action>();
        var gate = new TaskCompletionSource();
        var cell = new ResourceCell<int>
        {
            Loadable = Loadable<int>.Pending(0),
            Cts = new CancellationTokenSource(),
            Loader = async ct => { await gate.Task.WaitAsync(ct).ConfigureAwait(false); return 1; },
            Post = Queue(posts),
        };
        cell.Reload(keepPrevious: false);
        cell.Loader = _ => Task.FromResult(2);
        cell.Refresh();                         // cancels the first load's token

        Assert.True(DrainOne(posts));
        Assert.Equal(2, cell.Loadable.Value.Peek());
        Thread.Sleep(50);
        Assert.True(posts.IsEmpty);             // the cancelled first load posted nothing
        Assert.Null(cell.LastError);
    }

    [Fact]
    public void Async_validator_timeout_clears_validating()
    {
        var rt = new ReactiveRuntime();
        var posts = new ConcurrentQueue<Action>();
        var postSig = new Signal<object?>(Queue(posts));
        var c = new RenderContext
        {
            Runtime = rt,
            ResolveContextSignal = (_, ctx) => ReferenceEquals(ctx, HostDispatch.Post) ? postSig : null,
        };
        var value = new Signal<string>("");
        var opts = new FieldOptions<string>
        {
            AsyncDebounceMs = 0,
            Async = (_, _) => Task.FromException<MsgId>(new TaskCanceledException("timed out", new TimeoutException())),
        };
        c.BeginRender();
        var field = c.UseField(value, opts);
        rt.Flush();

        value.Value = "taken@example.com";
        rt.Flush();
        Assert.True(field.IsValidating.Value);

        Assert.True(DrainOne(posts), "a validator timeout posted no completion");
        rt.Flush();
        Assert.False(field.IsValidating.Value);
    }
}
