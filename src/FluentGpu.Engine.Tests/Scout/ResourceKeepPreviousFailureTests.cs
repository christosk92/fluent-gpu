using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>KeepPreviousData keeps the previous deps identity's value visible only WHILE the new identity loads: a failed
/// re-key load must surface Failed, never leave the old identity's value Ready as the new one's result. A same-identity
/// refresh failure still keeps its data (stale-while-revalidate).</summary>
public sealed class ResourceKeepPreviousFailureTests
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

    private static ResourceCell<string> ReadyA(ConcurrentQueue<Action> posts)
    {
        var cell = new ResourceCell<string>
        {
            Loadable = Loadable<string>.Pending(""),
            Cts = new CancellationTokenSource(),
            Options = new ResourceOptions { KeepPreviousData = true },
            Seed = "",
            Loader = _ => Task.FromResult("A"),
            Post = Queue(posts),
        };
        cell.Reload(keepPrevious: false);
        Assert.True(DrainOne(posts));
        Assert.True(cell.Loadable.IsReady);
        Assert.Equal("A", cell.Loadable.Value.Peek());
        return cell;
    }

    [Fact]
    public void Rekey_failure_with_keep_previous_surfaces_failed()
    {
        var posts = new ConcurrentQueue<Action>();
        var cell = ReadyA(posts);
        var notFound = new InvalidOperationException("404");
        cell.Loader = _ => Task.FromException<string>(notFound);
        cell.Reload(keepPrevious: true);                        // deps A -> B, A stays visible meanwhile
        Assert.True(cell.Loadable.IsReady);
        Assert.Equal("A", cell.Loadable.Value.Peek());

        Assert.True(DrainOne(posts), "B's failure posted no completion");
        Assert.True(cell.Loadable.IsFailed);
        Assert.Same(notFound, cell.Loadable.Error);
        Assert.Equal("", cell.Loadable.Value.Peek());          // B never carries A's value
        Assert.False(cell.IsFetchingSig.Peek());
        Assert.Same(notFound, cell.LastError);
    }

    [Fact]
    public void Refresh_failure_keeps_same_identity_data()
    {
        var posts = new ConcurrentQueue<Action>();
        var cell = ReadyA(posts);
        cell.Loader = _ => Task.FromException<string>(new InvalidOperationException("boom"));
        cell.Refresh();

        Assert.True(DrainOne(posts));
        Assert.True(cell.Loadable.IsReady);
        Assert.Equal("A", cell.Loadable.Value.Peek());
        Assert.NotNull(cell.LastError);
    }

    [Fact]
    public void Refresh_failure_after_successful_rekey_keeps_new_data()
    {
        var posts = new ConcurrentQueue<Action>();
        var cell = ReadyA(posts);
        cell.Loader = _ => Task.FromResult("B");
        cell.Reload(keepPrevious: true);
        Assert.True(DrainOne(posts));
        Assert.Equal("B", cell.Loadable.Value.Peek());

        cell.Loader = _ => Task.FromException<string>(new InvalidOperationException("boom"));
        cell.Refresh();
        Assert.True(DrainOne(posts));
        Assert.True(cell.Loadable.IsReady);                     // B's own data: stale-while-revalidate applies
        Assert.Equal("B", cell.Loadable.Value.Peek());
    }
}
