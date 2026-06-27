using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using Wavee.Backend;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// The REAL hm:// dealer transport behind Wavee.Backend.ITransport: ONE WebSocket firehose (Events) + spclient HTTP
// (Request) over the shared pipeline. Connects wss://{dealer}/?access_token={token}, parses frames (DealerFrameParser),
// emits hm:// messages to subscribers, answers server pings + sends a 30s keepalive, and reconnects with a fresh token on
// any drop. The socket is live-only (the user's run verifies it); the frame decode + the routing it feeds are unit-tested.
public sealed class LiveDealerTransport : ITransport, IDisposable
{
    readonly string _dealerHost;
    readonly Func<CancellationToken, Task<string>> _accessToken;
    readonly IHttpExchange _spclient;
    readonly Func<string> _spclientBaseUrl;
    readonly Action<string>? _log;
    readonly SimpleSubject<WireEvent> _events = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    ClientWebSocket? _ws;
    Task? _loop;

    public LiveDealerTransport(string dealerHost, Func<CancellationToken, Task<string>> accessToken,
        IHttpExchange spclient, Func<string> spclientBaseUrl, Action<string>? log = null)
    {
        _dealerHost = dealerHost;
        _accessToken = accessToken;
        _spclient = spclient;
        _spclientBaseUrl = spclientBaseUrl;
        _log = log;
    }

    /// <summary>Start the connect + receive loop (idempotent). Reconnects with a fresh token on any drop.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public IObservable<WireEvent> Events(string topicPrefix) => new Filtered(_events, topicPrefix);

    public async Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var url = _spclientBaseUrl() + route;
        var method = body.IsEmpty ? "GET" : "POST";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!body.IsEmpty) headers["Content-Type"] = "application/protobuf";
        using var resp = await _spclient.SendAsync(new HttpReq(method, url, headers, body.IsEmpty ? null : body.ToArray()), ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
        return new Resp(resp.Status is >= 200 and < 300, ms.ToArray(), resp.Status);
    }

    public Task Reply(string requestId, ReadOnlyMemory<byte> body)
        => SendTextAsync("{\"type\":\"reply\",\"key\":\"" + requestId + "\",\"payload\":{\"success\":true}}");

    public Task Publish(ReadOnlyMemory<byte> putState) => Task.CompletedTask;   // device-state announce — not on the library firehose path

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var token = await _accessToken(ct).ConfigureAwait(false);
                using var ws = new ClientWebSocket();
                _ws = ws;
                await ws.ConnectAsync(new Uri($"wss://{_dealerHost}/?access_token={Uri.EscapeDataString(token)}"), ct).ConfigureAwait(false);
                _log?.Invoke("dealer connected (" + _dealerHost + ")");
                using var keepalive = StartKeepalive(ct);
                await ReceiveLoop(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log?.Invoke("dealer disconnected: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); } catch { return; }   // reconnect backoff
        }
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var frame = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                frame.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var f = DealerFrameParser.Parse(frame.GetBuffer().AsSpan(0, (int)frame.Length));
            if (f.Type == DealerFrameType.Ping) await SendTextAsync("{\"type\":\"pong\"}").ConfigureAwait(false);
            else if (f.Type == DealerFrameType.Message && f.Uri is { Length: > 0 } uri) _events.OnNext(new WireEvent(uri, f.Payload));
        }
    }

    IDisposable StartKeepalive(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(false);
                    await SendTextAsync("{\"type\":\"ping\"}").ConfigureAwait(false);
                }
            }
            catch { /* loop ends on disconnect/cancel */ }
        }, cts.Token);
        return cts;
    }

    async Task SendTextAsync(string json)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false); }
        catch { /* a failed send drops the connection → the receive loop exits and we reconnect */ }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _ws?.Dispose(); } catch { }
        _sendLock.Dispose();
        _cts.Dispose();
    }

    // Forward only the hm:// topics matching the requested prefix (the router subscribes "hm://").
    sealed class Filtered(IObservable<WireEvent> src, string prefix) : IObservable<WireEvent>
    {
        public IDisposable Subscribe(IObserver<WireEvent> observer) => src.Subscribe(new Obs(observer, prefix));
        sealed class Obs(IObserver<WireEvent> inner, string prefix) : IObserver<WireEvent>
        {
            public void OnNext(WireEvent e) { if (e.Topic.StartsWith(prefix, StringComparison.Ordinal)) inner.OnNext(e); }
            public void OnCompleted() => inner.OnCompleted();
            public void OnError(Exception ex) => inner.OnError(ex);
        }
    }
}
