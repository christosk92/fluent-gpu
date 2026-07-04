using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.SpotifyLive.Audio;

/// <summary>IPC proxy to Wavee.AudioHost — implements two-phase instant-start + body supply.</summary>
public sealed class RemoteAudioHost : IAudioHost, IAsyncDisposable
{
    readonly AudioProcessManager _proc;
    readonly SimpleSubject<AudioHostSignal> _signals = new();
    readonly Action<string>? _log;
    long _positionMs;
    bool _playing, _buffering, _prebuffering;
    AudioHostSignalKind _lastKind = AudioHostSignalKind.Paused;

    public RemoteAudioHost(AudioProcessManager proc, Action<string>? log = null)
    {
        _proc = proc;
        _log = log;
        _proc.Notification += OnNotification;   // single-reader demux owns the pipe; we subscribe, never read it
    }

    public long PositionMs => _positionMs;
    public bool IsPlaying => _playing;
    public bool IsBuffering => _buffering || _prebuffering;
    public IObservable<AudioHostSignal> Signals => _signals;

    // Plain path (PlaybackController): no prefetched head — fast-start with an empty head, then immediately supply the
    // fully-resolved body (key + CDN) so the host fetches/decrypts/decodes. True instant-start uses LoadFastStart+SupplyBody.
    public void Load(in AudioStreamHandle stream) { var s = stream; _ = LoadAndSupplyAsync(s); }
    public void LoadFastStart(in AudioFastStart start) => _ = SendFastStart(start);
    public void SupplyBody(in AudioStreamHandle body) => _ = SendSupplyBody(body);
    public void Play() => _ = Send(IpcMessageTypes.Play, new EmptyPayload());
    public void Pause() => _ = Send(IpcMessageTypes.Pause, new EmptyPayload());
    public void Stop() => _ = Send(IpcMessageTypes.Stop, new EmptyPayload());
    public void Seek(long positionMs) => _ = Send(IpcMessageTypes.Seek, new SeekCommand { PositionMs = positionMs });
    public void SetVolume(double volume01) => _ = Send(IpcMessageTypes.SetVolume, new VolumeCommand { Volume = volume01 });

    async Task LoadAndSupplyAsync(AudioStreamHandle stream)
    {
        await SendFastStart(new AudioFastStart(stream.TrackUri, stream.FileIdHex, stream.Format, stream.DurationMs, stream.NormalizationGainDb, default)).ConfigureAwait(false);
        await SendSupplyBody(stream).ConfigureAwait(false);
    }

    async Task SendFastStart(AudioFastStart s)
    {
        try
        {
            await _proc.EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false);
            var cmd = new LoadFastStartCommand
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                TrackUri = s.TrackUri,
                FileIdHex = s.FileIdHex,
                Format = s.Format.ToString(),
                DurationMs = s.DurationMs,
                NormalizationGainDb = s.NormalizationGainDb,
                HeadBytesBase64 = Convert.ToBase64String(s.HeadBytes.Span),
            };
            await _proc.RequestAsync(IpcMessageTypes.LoadFastStart, cmd, _ => true, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            _prebuffering = true;
            _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
        }
        catch (Exception ex) { _log?.Invoke("LoadFastStart failed: " + ex.Message); }
    }

    async Task SendSupplyBody(AudioStreamHandle h)
    {
        try
        {
            var cmd = new SupplyBodyCommand
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                FileIdHex = h.FileIdHex,
                AesKeyHex = Convert.ToHexStringLower(h.Key.Span),
                CdnUrls = h.CdnUrls ?? (string.IsNullOrEmpty(h.CdnUrl) ? [] : [h.CdnUrl]),
                HeadBoundary = h.HeadBoundary,
            };
            await _proc.RequestAsync(IpcMessageTypes.SupplyBody, cmd, _ => true, TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
            _prebuffering = false;
            _buffering = true;
            _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, _positionMs));
        }
        catch (Exception ex) { _log?.Invoke("SupplyBody failed: " + ex.Message); }
    }

    async Task Send<T>(string type, T payload)
    {
        try { await _proc.SendAsync(type, payload, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log?.Invoke($"{type} failed: " + ex.Message); }
    }

    // Host-initiated frames from the demuxing read loop (id == 0). Structural transitions fire the coarse signal;
    // steady-state playback position rides PositionTick (so the projection's buffering indicator doesn't thrash).
    void OnNotification(string type, System.Text.Json.JsonElement? payload)
    {
        try
        {
            switch (type)
            {
                case IpcMessageTypes.StateUpdate:
                    if (payload is null) return;
                    var st = payload.Value.Deserialize(AudioIpcJsonContext.Default.HostStateUpdate);
                    if (st is null) return;
                    _playing = st.IsPlaying; _buffering = st.IsBuffering; _prebuffering = st.IsPrebuffering; _positionMs = st.PositionMs;
                    var kind = _prebuffering ? AudioHostSignalKind.Prebuffering
                        : _buffering ? AudioHostSignalKind.Buffering
                        : _playing ? AudioHostSignalKind.Playing : AudioHostSignalKind.Paused;
                    if (kind == AudioHostSignalKind.Playing && _lastKind == AudioHostSignalKind.Playing)
                        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.PositionTick, _positionMs));
                    else
                        _signals.OnNext(new AudioHostSignal(kind, _positionMs));
                    _lastKind = kind;
                    break;
                case IpcMessageTypes.TrackFinished:
                    _playing = false;
                    _lastKind = AudioHostSignalKind.Ended;
                    _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, _positionMs));
                    break;
            }
        }
        catch (Exception ex) { _log?.Invoke("host notification parse failed: " + ex.Message); }
    }

    public async ValueTask DisposeAsync() => await _proc.DisposeAsync().ConfigureAwait(false);
}

/// <summary>IPC-backed PlayPlay key deriver (Step B).</summary>
public sealed class IpcPlayPlayKeyDeriver : IPlayPlayKeyDeriver
{
    readonly AudioProcessManager _proc;
    readonly AudioRuntimeStatusService _status;
    readonly Action<string>? _log;

    public IpcPlayPlayKeyDeriver(AudioProcessManager proc, AudioRuntimeStatusService status, Action<string>? log = null)
    {
        _proc = proc;
        _status = status;
        _log = log;
    }

    public async Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, CancellationToken ct = default)
    {
        try
        {
            var cmd = new DerivePlayPlayKeyCommand
            {
                CorrelationId = correlationId,
                ObfuscatedKeyHex = Convert.ToHexStringLower(obfuscatedKey.Span),
                ContentIdHex = Convert.ToHexStringLower(contentId.Span),
                SpotifyDllPath = spotifyDllPath,
                Config = config,
            };
            var result = await _proc.RequestAsync(IpcMessageTypes.DerivePlayPlayKey, cmd, p =>
            {
                if (p is null) return new DerivePlayPlayKeyResult { CorrelationId = correlationId, Reason = AudioKeyFailureReason.EmulationFault };
                return p.Value.Deserialize<DerivePlayPlayKeyResult>(AudioIpcJsonContext.Default.DerivePlayPlayKeyResult)
                       ?? new DerivePlayPlayKeyResult { CorrelationId = correlationId, Reason = AudioKeyFailureReason.EmulationFault };
            }, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);

            if (result.Reason is not null and not AudioKeyFailureReason.None)
            {
                _status.SetKeyFailure(result.Reason.Value, result.Detail);
                return new PlayPlayDeriveResult(default, result.Reason.Value, result.Detail);
            }
            if (string.IsNullOrEmpty(result.AesKeyHex) || result.AesKeyHex.Length != 32)
                return new PlayPlayDeriveResult(default, AudioKeyFailureReason.RotationDrift, result.Detail);
            return new PlayPlayDeriveResult(Convert.FromHexString(result.AesKeyHex), AudioKeyFailureReason.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke("PlayPlay derive IPC failed: " + ex.Message);
            return new PlayPlayDeriveResult(default, AudioKeyFailureReason.EmulationFault, ex.Message);
        }
    }
}
