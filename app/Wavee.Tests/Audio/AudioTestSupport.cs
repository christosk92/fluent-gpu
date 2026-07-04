using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;

namespace Wavee.Tests.Audio;

/// <summary>Fakes + helpers for the audio resilience/error/UX tests.</summary>
static class A
{
    public static byte[] Bytes(byte seed, int n) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = (byte)(seed + i); return b; }
    public static byte[] Key16(byte seed) { var b = new byte[16]; for (int i = 0; i < 16; i++) b[i] = (byte)(seed * 31 + i); return b; }
    public static byte[] File20() => Bytes(5, 20);
    public static byte[] Gid16() => Bytes(9, 16);

    public static SessionContext Ctx() => new("acct", "US", "premium", "en", Tier.Premium, false);

    public static RuntimeManifestPack Pack(string sha256Hex, string? url = "https://cdn/pack.bin", string arch = "X64", string? comp = null) => new()
    {
        Id = "test", SpotifyVersion = "1.2.88.483", AppVersion = "128800483", RequestVersion = 5, Arch = arch,
        Url = url, Compression = comp, Sha256Hex = sha256Hex,
        PlayPlayTokenHex = Convert.ToHexString(new byte[16]), VmInitValueHex = Convert.ToHexString(new byte[16]),
        AnalysisBaseHex = "180000000", VmRuntimeInitVaHex = "180001000", VmObjectTransformVaHex = "180002000",
        RuntimeContextVaHex = "180003000", FillRandomBytesVaHex = "180004000", TriggerRipVaHex = "180005000",
        TriggerRipRegOffset = 0x88,
    };

    public static RuntimeAsset Asset() => new("C:\\fake\\Spotify.dll", Pack(Convert.ToHexString(new byte[32])).ToConfig(), "test");
}

sealed class FakeApKeySource : IAudioKeySource
{
    public int Calls;
    public byte[]? Key;          // non-null → success
    public Exception? Throw;     // non-null → throw (permanent-ish AP failure)
    public int DelayMs;
    public async Task<ReadOnlyMemory<byte>> GetKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        if (DelayMs > 0) await Task.Delay(DelayMs, ct).ConfigureAwait(false);
        if (Throw is not null) throw Throw;
        return Key is not null ? Key : ReadOnlyMemory<byte>.Empty;   // empty → resolver falls through to PlayPlay
    }
}

sealed class FakeLicense : ILicenseClient
{
    public int Calls;
    public byte[]? Obf;
    public AudioKeyFailureReason Reason = AudioKeyFailureReason.None;
    public Task<(ReadOnlyMemory<byte> Key, AudioKeyFailureReason Reason)> FetchObfuscatedKeyAsync(string fileIdHex, PlayPlayConfig config, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(((ReadOnlyMemory<byte>)(Obf ?? Array.Empty<byte>()), Reason));
    }
}

sealed class FakeDeriver : IPlayPlayKeyDeriver
{
    public int Calls;
    public byte[]? Key;
    public AudioKeyFailureReason Reason = AudioKeyFailureReason.None;
    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new PlayPlayDeriveResult(Key ?? default, Reason));
    }
}

sealed class RecordingAudioHost : IAudioHost
{
    public bool LoadFastStartCalled, PlayCalled, SupplyBodyCalled;
    public readonly TaskCompletionSource SupplyBodySignaled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Wavee.Core.SimpleSubject<AudioHostSignal> _sig = new();

    public void Load(in AudioStreamHandle s) { }
    public void LoadFastStart(in AudioFastStart s) => LoadFastStartCalled = true;
    public void SupplyBody(in AudioStreamHandle b) { SupplyBodyCalled = true; SupplyBodySignaled.TrySetResult(); }
    public void Play() => PlayCalled = true;
    public void Pause() { }
    public void Stop() { }
    public void Seek(long ms) { }
    public void SetVolume(double v) { }
    public long PositionMs => 0;
    public bool IsPlaying => PlayCalled;
    public bool IsBuffering => false;
    public IObservable<AudioHostSignal> Signals => _sig;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeFastResolver : IFastTrackResolver
{
    readonly FastStartPlan _plan;
    public int Calls;
    public FakeFastResolver(FastStartPlan plan) => _plan = plan;
    public Task<FastStartPlan> ResolveFastAsync(Wavee.Core.Track track, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(_plan);
    }
}

sealed class ThrowingResolver : ITrackResolver
{
    readonly AudioKeyFailureReason _reason;
    public int Calls;
    public ThrowingResolver(AudioKeyFailureReason reason) => _reason = reason;
    public Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        throw new AudioPlaybackException(_reason, "boom");
    }
}

sealed class FakeHttpExchange : IHttpExchange
{
    public Func<HttpReq, HttpResp> Responder = _ => new HttpResp(404, new Dictionary<string, string>(), Array.Empty<byte>());
    public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct) => Task.FromResult(Responder(req));
}

sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public Func<string, (HttpStatusCode Status, byte[] Body)> Responder = _ => (HttpStatusCode.NotFound, Array.Empty<byte>());
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (status, body) = Responder(request.RequestUri!.ToString());
        var resp = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        return Task.FromResult(resp);
    }
}

/// <summary>In-memory <see cref="IIpcChannel"/> — controllable replies/notifications/death for demux+recycle tests.</summary>
sealed class FakeChannel : IIpcChannel
{
    readonly Channel<(string, long, System.Text.Json.JsonElement?)> _in = System.Threading.Channels.Channel.CreateUnbounded<(string, long, System.Text.Json.JsonElement?)>();
    public readonly List<(string Type, long Id)> Sent = new();
    public Func<(string Type, long Id), (string, long, System.Text.Json.JsonElement?)?>? AutoReply;
    public bool ThrowOnRead;

    public Task SendAsync<T>(string type, long id, T payload, CancellationToken ct)
    {
        lock (Sent) Sent.Add((type, id));
        if (AutoReply?.Invoke((type, id)) is { } frame) _in.Writer.TryWrite(frame);
        return Task.CompletedTask;
    }

    public async Task<(string Type, long Id, System.Text.Json.JsonElement? Payload)> ReadAsync(CancellationToken ct)
    {
        if (ThrowOnRead) { await Task.Yield(); throw new IOException("pipe dead"); }
        return await _in.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    public void Push(string type, long id) => _in.Writer.TryWrite((type, id, null));
    public void Dispose() => _in.Writer.TryComplete();
}
