using System.Net.Http;
using Wavee.Backend.Audio;

namespace Wavee.AudioHost.Audio;

/// <summary>Host-side play engine: fetch → AES-CTR decrypt → decode (Vorbis via vendored NVorbis, FLAC via FlacBox) →
/// WASAPI. Minimal first cut (whole-file body); true instant-start-from-head and progressive/.enc caching are follow-ups.
/// Single decode thread paced by the blocking WASAPI Write; control (Play/Pause/Seek/Volume) is thread-safe through the
/// renderer's lock.</summary>
internal sealed class AudioPlayEngine : IDisposable
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly Action<string> _log;
    readonly WasapiRenderer _renderer = new();
    readonly object _gate = new();
    readonly Timer _tick;

    ISampleSource? _reader;
    Thread? _decodeThread;
    CancellationTokenSource? _cts;

    ReadOnlyMemory<byte> _pendingHead;
    string _format = "OggVorbis320";   // AudioFormat name string (over IPC); remembered from LoadFastStart (SupplyBody carries none)
    float _gainLinear = 1f;
    long _seekBaseMs;
    long _pendingSeekMs = -1;
    volatile bool _playing;
    volatile bool _prebuffering;
    volatile bool _buffering;

    public event Action<HostStateUpdate>? State;
    public event Action? TrackFinished;

    public AudioPlayEngine(Action<string> log)
    {
        _log = log;
        _tick = new Timer(_ => { if (_playing) RaiseState(); }, null, 1000, 1000);
    }

    public void LoadFastStart(LoadFastStartCommand cmd)
    {
        _pendingHead = string.IsNullOrEmpty(cmd.HeadBytesBase64) ? default : Convert.FromBase64String(cmd.HeadBytesBase64);
        _format = string.IsNullOrEmpty(cmd.Format) ? "OggVorbis320" : cmd.Format;
        _gainLinear = DbToLinear(cmd.NormalizationGainDb);
        _prebuffering = _pendingHead.Length > 0;
        _seekBaseMs = 0;
        RaiseState();
    }

    public void SupplyBody(SupplyBodyCommand cmd) => _ = StartBodyAsync(cmd);

    async Task StartBodyAsync(SupplyBodyCommand cmd)
    {
        try
        {
            StopDecode();
            var key = Convert.FromHexString(cmd.AesKeyHex);
            _buffering = true; _prebuffering = false; RaiseState();

            var stream = await SpotifyAudioStream.CreateAsync(
                _http, _pendingHead, cmd.HeadBoundary, key, cmd.CdnUrls, cmd.SizeBytes, CancellationToken.None).ConfigureAwait(false);
            var skip = new SkipStream(stream, SpotifyAesCtr.SpotifyHeaderSize);
            ISampleSource reader = _format is "Flac" or "Flac24"
                ? new FlacSampleSource(skip)
                : new VorbisSampleSource(skip);
            _log($"decode: {_format} → {(reader is FlacSampleSource ? "FLAC" : "Vorbis")} {reader.SampleRate}Hz {reader.Channels}ch");
            _renderer.Init(reader.SampleRate, reader.Channels);

            var cts = new CancellationTokenSource();
            lock (_gate) { _reader = reader; _seekBaseMs = 0; _cts = cts; }
            var thread = new Thread(() => DecodeLoop(reader, cts.Token)) { IsBackground = true, Name = "wavee-decode" };
            _decodeThread = thread;
            thread.Start();
            if (_playing) _renderer.Start();
        }
        catch (Exception ex) { _log("supply body failed: " + ex.Message); }
    }

    public void Play() { _playing = true; _renderer.Start(); RaiseState(); }
    public void Pause() { _playing = false; _renderer.Pause(); RaiseState(); }
    public void Stop() { _playing = false; StopDecode(); _seekBaseMs = 0; RaiseState(); }
    public void Seek(long ms) => Interlocked.Exchange(ref _pendingSeekMs, Math.Max(0, ms));
    public void SetVolume(double v) => _renderer.SetVolume((float)v);

    void DecodeLoop(ISampleSource reader, CancellationToken ct)
    {
        var buf = new float[16384];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long seek = Interlocked.Exchange(ref _pendingSeekMs, -1);
                if (seek >= 0)
                {
                    try { reader.SeekTo(TimeSpan.FromMilliseconds(seek)); } catch (Exception ex) { _log("seek failed: " + ex.Message); }
                    _renderer.Reset(); _seekBaseMs = seek; if (_playing) _renderer.Start();
                }

                int got = reader.ReadSamples(buf, 0, buf.Length);
                if (got <= 0) break;
                if (_gainLinear != 1f) for (int i = 0; i < got; i++) buf[i] *= _gainLinear;
                _prebuffering = false; _buffering = false;
                _renderer.Write(buf.AsSpan(0, got), ct);
            }

            if (!ct.IsCancellationRequested)   // natural EOF → drain, then report finished
            {
                while (!ct.IsCancellationRequested && _renderer.PlayedFrames < _renderer.ReleasedFrames) Thread.Sleep(20);
                _playing = false; RaiseState();
                TrackFinished?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log("decode loop error: " + ex.Message); }
    }

    void StopDecode()
    {
        CancellationTokenSource? cts; Thread? thread; ISampleSource? reader;
        lock (_gate) { cts = _cts; thread = _decodeThread; reader = _reader; _cts = null; _decodeThread = null; _reader = null; }
        try { cts?.Cancel(); } catch { }
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread) { try { thread.Join(500); } catch { } }
        try { _renderer.Reset(); } catch { }
        try { reader?.Dispose(); } catch { }
        cts?.Dispose();
    }

    public long PositionMs => _seekBaseMs + _renderer.PositionMs;

    void RaiseState() => State?.Invoke(new HostStateUpdate
    {
        IsPlaying = _playing,
        IsBuffering = _buffering,
        IsPrebuffering = _prebuffering,
        PositionMs = PositionMs,
    });

    static float DbToLinear(float db) => db == 0f ? 1f : (float)Math.Pow(10, db / 20.0);

    public void Dispose()
    {
        _tick.Dispose();
        StopDecode();
        _renderer.Dispose();
        _http.Dispose();
    }
}
