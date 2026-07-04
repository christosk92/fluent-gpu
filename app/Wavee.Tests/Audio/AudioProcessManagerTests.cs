using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class AudioProcessManagerTests
{
    static (AudioProcessManager Mgr, Func<int> Calls) With(params FakeChannel[] channels)
    {
        int calls = 0;
        Func<CancellationToken, Task<IIpcChannel>> factory = _ =>
        {
            var c = channels[Math.Min(calls, channels.Length - 1)];
            calls++;
            return Task.FromResult<IIpcChannel>(c);
        };
        return (new AudioProcessManager(null, factory), () => calls);
    }

    [Fact]
    public async Task Request_MatchesReplyById()
    {
        var ch = new FakeChannel { AutoReply = s => (IpcMessageTypes.CommandResult, s.Id, null) };
        var (mgr, calls) = With(ch);

        var result = await mgr.RequestAsync("cmd", "payload", _ => "ok", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Single(ch.Sent);
        Assert.Equal(1, calls());
        await mgr.DisposeAsync();
    }

    [Fact]
    public async Task Notifications_RouteToEvent_NotToRequests()
    {
        var ch = new FakeChannel();
        var (mgr, _) = With(ch);
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        mgr.Notification += (type, _) => { if (type == IpcMessageTypes.StateUpdate) got.TrySetResult(type); };

        await mgr.EnsureStartedAsync(CancellationToken.None);
        ch.Push(IpcMessageTypes.StateUpdate, 0);   // host-initiated (id == 0)

        var type = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(IpcMessageTypes.StateUpdate, type);
        await mgr.DisposeAsync();
    }

    [Fact]
    public async Task WedgedRequest_TimesOut_RecyclesAndRetries()
    {
        var wedged = new FakeChannel();                                   // never replies → the first attempt times out
        var healthy = new FakeChannel { AutoReply = s => (IpcMessageTypes.CommandResult, s.Id, null) };
        var (mgr, calls) = With(wedged, healthy);

        var result = await mgr.RequestAsync("derive", "p", _ => "ok", TimeSpan.FromMilliseconds(150), CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls());   // recycled: a wedged native call can't be cancelled → kill+restart
        await mgr.DisposeAsync();
    }

    [Fact]
    public async Task PipeDeath_FailsInflight_ThenReconnectsAndRetries()
    {
        var dead = new FakeChannel { ThrowOnRead = true };               // read loop dies → pending fails
        var healthy = new FakeChannel { AutoReply = s => (IpcMessageTypes.CommandResult, s.Id, null) };
        var (mgr, calls) = With(dead, healthy);

        var result = await mgr.RequestAsync("cmd", "p", _ => "ok", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls());   // dropped the dead channel, reconnected
        await mgr.DisposeAsync();
    }

    [Fact]
    public async Task CallerCancellation_IsNotTreatedAsHostFault()
    {
        var ch = new FakeChannel();   // never replies
        var (mgr, calls) = With(ch);
        using var cts = new CancellationTokenSource(120);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mgr.RequestAsync("cmd", "p", _ => "ok", TimeSpan.FromSeconds(30), cts.Token));

        Assert.Equal(1, calls());   // caller cancel → no recycle/retry
        await mgr.DisposeAsync();
    }
}
