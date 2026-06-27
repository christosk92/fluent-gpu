using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── ENGINE ③ — Transport (multiplexed channels + middleware pipeline) ────────────────────────────────────────────────
// The real impl owns the AP/Shannon socket (Mercury/AudioKey/packets), the dealer WebSocket (events + REQUEST→reply +
// PutState), and the HTTPS channels (spclient/pathfinder/extended-metadata/login5/client-token) under a uniform
// middleware stack (auth-token · client-token · 401-refresh · 429-backoff · market/locale-stamp). STUBBED now — no
// socket/credentials/protocol-mechanics are available in this environment; that is the deferred Spotify layer.

public enum Channel { ApMercury, Spclient, Pathfinder, ExtendedMetadata, DealerWs, Login5, ClientToken }

public readonly record struct Resp(bool Ok, byte[] Body, int Status);
public sealed record WireEvent(string Topic, byte[] Payload);

public interface ITransport
{
    Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default);
    IObservable<WireEvent> Events(string topicPrefix);   // dealer pushes by hm:// prefix
    Task Reply(string requestId, ReadOnlyMemory<byte> body);   // ③a — the dealer server→client REQUEST→reply primitive
    Task Publish(ReadOnlyMemory<byte> putState);               // ③b — device-state announce (Connect)
}

public sealed class StubTransport : ITransport
{
    readonly SimpleSubject<WireEvent> _events = new();
    int _requestCount;
    public int RequestCount => _requestCount;

    public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _requestCount);
        return Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));   // stub: the request "succeeds" with no body
    }

    public IObservable<WireEvent> Events(string topicPrefix) => _events;
    public Task Reply(string requestId, ReadOnlyMemory<byte> body) => Task.CompletedTask;
    public Task Publish(ReadOnlyMemory<byte> putState) => Task.CompletedTask;

    /// <summary>Test hook: simulate a dealer push (a live revalidation trigger).</summary>
    public void PushEvent(WireEvent e) => _events.OnNext(e);
}
