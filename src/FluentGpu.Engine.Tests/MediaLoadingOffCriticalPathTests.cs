using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Media loading stays off the open's critical path (L2-10): a sidecar subtitle loads after <c>OpenAsync</c> has returned
/// (a slow host never holds the open, the load stops with the source that asked, and a request has its own timeout), and
/// the host's default-backend registrar seeds <c>MediaPlayer.Create()</c>/<c>Build()</c>. The subtitle host is a loopback
/// socket server that parks every response until the test lets it answer.
/// </summary>
[Collection(SerialTestCollection.Name)]   // HostDispatch.Current and the default registrar are process-static
public sealed class MediaLoadingOffCriticalPathTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private const string Vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nhello\n";

    private static async Task WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true within five seconds.");
            await Task.Delay(10);
        }
    }

    private static MediaPlayer NewPlayer(GatedMediaBackend backend)
        => MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();

    [Fact]
    public async Task SlowSidecarSubtitle_DoesNotHoldTheOpen_AndRegistersWhenItArrives()
    {
        using var poster = new HostPosterScope(null);
        using var server = new SlowSubtitleServer();
        var backend = new GatedMediaBackend();
        MediaPlayer player = NewPlayer(backend);
        MediaSource source = MediaSource.FromFile("a.mp4").WithExternalSubtitle(SubtitleSource.FromUri(server.Url));
        backend.Release("a.mp4");

        // The open completes while the subtitle host has not answered (it used to wait for the fetch, 100 s at worst).
        await player.OpenAsync(source).AsTask().WaitAsync(Limit);
        await server.Requested;   // the fetch really is in flight
        Assert.NotNull(player.Session);
        Assert.Equal(0, player.Tracks.Text.Count);

        server.Respond();
        await WaitFor(() => player.Tracks.Text.Count == 1);
        Assert.Equal(1, backend.SessionFor("a.mp4").StartCount);

        await player.DisposeAsync();
    }

    [Fact]
    public async Task ANewerOpen_StopsTheOlderSourcesSubtitleLoad_SoItNeverAddsATrack()
    {
        using var poster = new HostPosterScope(null);
        using var server = new SlowSubtitleServer();
        var backend = new GatedMediaBackend();
        MediaPlayer player = NewPlayer(backend);
        backend.Release("a.mp4");
        await player.OpenAsync(MediaSource.FromFile("a.mp4").WithExternalSubtitle(SubtitleSource.FromUri(server.Url)))
            .AsTask().WaitAsync(Limit);
        await server.Requested;

        backend.Release("b.mp4");
        await player.OpenAsync(MediaSource.FromFile("b.mp4")).AsTask().WaitAsync(Limit);

        server.Respond();            // A's subtitle host answers after B is live
        await Task.Delay(300);       // give a stale registration every chance to land
        Assert.Equal(0, player.Tracks.Text.Count);

        await player.DisposeAsync();
    }

    [Fact]
    public async Task SidecarSubtitleRequest_ThatExceedsItsBudget_FailsAsATimeout_NotACancellation()
    {
        using var server = new SlowSubtitleServer();
        using var client = new HttpClient();

        await Assert.ThrowsAsync<TimeoutException>(async () => await SubtitleLoader.LoadAsync(client,
            SubtitleSource.FromUri(server.Url), new NetworkOptions(ConnectTimeout: TimeSpan.FromMilliseconds(200)),
            CancellationToken.None));
    }

    [Fact]
    public void MediaHttp_IsOneSharedClient()
        => Assert.Same(MediaHttp.Shared, MediaHttp.Shared);

    [Fact]
    public async Task DefaultRegistrar_SeedsCreateAndBuild_ExplicitBackendWins_AndIsLazy()
    {
        using var poster = new HostPosterScope(null);
        var defaults = new GatedMediaBackend();
        int built = 0;
        MediaRouter.SetDefaultRegistrar(router => router.RegisterDefault(MediaKind.MfVideoOrFile, () => { built++; return defaults; }));
        try
        {
            MediaPlayer created = MediaPlayer.Create();
            Assert.Equal(0, built);   // a default backend is only constructed when its kind is first resolved
            defaults.Release("a.mp4");
            await created.OpenAsync(MediaSource.FromFile("a.mp4")).AsTask().WaitAsync(Limit);
            Assert.Equal(1, built);
            Assert.Equal(1, defaults.SessionFor("a.mp4").ConnectCount);

            MediaPlayer viaBuild = MediaPlayer.Build().Build();
            defaults.Release("b.mp4");
            await viaBuild.OpenAsync(MediaSource.FromFile("b.mp4")).AsTask().WaitAsync(Limit);
            Assert.Equal(2, built);   // each router resolves (and owns) its own default instance
            Assert.Equal(1, defaults.SessionFor("b.mp4").ConnectCount);

            var explicitBackend = new GatedMediaBackend();
            MediaPlayer explicitPlayer = NewPlayer(explicitBackend);
            explicitBackend.Release("c.mp4");
            await explicitPlayer.OpenAsync(MediaSource.FromFile("c.mp4")).AsTask().WaitAsync(Limit);
            Assert.Equal(1, explicitBackend.SessionFor("c.mp4").ConnectCount);
            Assert.Equal(2, built);   // an explicit registration wins: the default was never constructed for this router

            await created.DisposeAsync();
            await viaBuild.DisposeAsync();
            await explicitPlayer.DisposeAsync();
        }
        finally { MediaRouter.SetDefaultRegistrar(null); }
    }

    [Fact]
    public void Router_HasAndResolve_CoverDefaultsAndExplicitRegistrations()
    {
        var router = new MediaRouter();
        var explicitBackend = new GatedMediaBackend();
        var defaultBackend = new GatedMediaBackend();
        Assert.False(router.Has(MediaKind.PcmAudio));
        Assert.Null(router.Resolve(MediaKind.PcmAudio));

        router.RegisterDefault(MediaKind.PcmAudio, () => defaultBackend);
        Assert.True(router.Has(MediaKind.PcmAudio));
        Assert.Same(defaultBackend, router.Resolve(MediaKind.PcmAudio));

        router.Register(MediaKind.PcmAudio, explicitBackend);
        Assert.Same(explicitBackend, router.Resolve(MediaKind.PcmAudio));
        Assert.Throws<ArgumentException>(() => router.RegisterDefault(MediaKind.Auto, () => defaultBackend));
    }

    /// <summary>A loopback HTTP host that reads a request and then parks until <see cref="Respond"/>, answering with a
    /// small WebVTT file.</summary>
    private sealed class SlowSubtitleServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlowSubtitleServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/s.vtt";
        /// <summary>Completes once a request has been read.</summary>
        public Task Requested => _requested.Task.WaitAsync(Limit);
        public void Respond() => _gate.TrySetResult();

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    _ = ServeAsync(client);
                }
            }
            catch { /* the listener was stopped */ }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    NetworkStream stream = client.GetStream();
                    var buffer = new byte[4096];
                    var request = new StringBuilder();
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        int n = await stream.ReadAsync(buffer);
                        if (n == 0) return;
                        request.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    _requested.TrySetResult();
                    await _gate.Task;
                    byte[] body = Encoding.UTF8.GetBytes(Vtt);
                    byte[] head = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/vtt\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(body);
                    await stream.FlushAsync();
                }
            }
            catch { /* the client gave up (its request was cancelled) */ }
        }

        public void Dispose()
        {
            _listener.Stop();
            _gate.TrySetResult();
        }
    }
}
