using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>An async command's op that times out on its own (an <see cref="OperationCanceledException"/> while the
/// command's token is live, e.g. an HttpClient timeout) failed: onError must hear about it. Only the command's own
/// Cancel / supersede stays silent.</summary>
public sealed class AsyncCommandSelfTimeoutTests
{
    // Runs posted completions until `done` holds (or the timeout passes) — the op may finish on a pool thread.
    private static void DrainUntil(ConcurrentQueue<Action> q, Func<bool> done, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            if (q.TryDequeue(out var a)) a(); else Thread.Sleep(5);
        }
        while (q.TryDequeue(out var rest)) rest();
    }

    [Fact]
    public void A_self_timeout_reaches_onError()
    {
        var posts = new ConcurrentQueue<Action>();
        var cmd = new AsyncCommand(a => posts.Enqueue(a));
        Exception? err = null;
        cmd.Run(_ => Task.FromException(new TaskCanceledException("timed out")), ex => err = ex);
        DrainUntil(posts, () => !cmd.IsRunningNow);
        Assert.False(cmd.IsRunningNow);
        Assert.IsType<TaskCanceledException>(err);
    }

    [Fact]
    public void Cancel_stays_silent()
    {
        var posts = new ConcurrentQueue<Action>();
        var cmd = new AsyncCommand(a => posts.Enqueue(a));
        var gate = new TaskCompletionSource();
        Exception? err = null;
        cmd.Run(ct => gate.Task.WaitAsync(ct), ex => err = ex);
        cmd.Cancel();
        DrainUntil(posts, () => !cmd.IsRunningNow);
        Assert.False(cmd.IsRunningNow);
        Assert.Null(err);
    }

    [Fact]
    public void A_keyed_self_timeout_reaches_onError()
    {
        var posts = new ConcurrentQueue<Action>();
        var cmds = new AsyncCommandSet<int>(a => posts.Enqueue(a));
        Exception? err = null;
        cmds.Run(7, _ => Task.FromException(new TaskCanceledException("timed out")), ex => err = ex);
        DrainUntil(posts, () => !cmds.IsRunningNow(7));
        Assert.False(cmds.IsRunningNow(7));
        Assert.IsType<TaskCanceledException>(err);
    }

    [Fact]
    public void A_keyed_cancel_stays_silent()
    {
        var posts = new ConcurrentQueue<Action>();
        var cmds = new AsyncCommandSet<int>(a => posts.Enqueue(a));
        var gate = new TaskCompletionSource();
        Exception? err = null;
        cmds.Run(7, ct => gate.Task.WaitAsync(ct), ex => err = ex);
        cmds.Cancel(7);
        DrainUntil(posts, () => !cmds.IsRunningNow(7));
        Assert.False(cmds.IsRunningNow(7));
        Assert.Null(err);
    }
}
