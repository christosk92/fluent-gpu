using System;
using System.Collections.Concurrent;
using System.Threading;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// A/B listening: one stretch of audio rendered as several aligned takes (the same timeline with and without one lane, a hard cut
// and the finished mix), played on an aux output, the take switched AT THE PLAYHEAD with a short crossfade and never restarted.
// The control side (UI thread) posts commands; the aux thread applies them at its next block, so a switch lands within one
// block plus the crossfade. A take replaced while it plays (a re-render after an edit) crossfades into the new buffer at the same
// position. Steady state allocates nothing on the aux thread.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One take: interleaved PCM at <see cref="SampleRate"/> covering the timeline from <see cref="StartSeconds"/>; every take of a
/// player is on the same timeline, so a switch keeps the position.</summary>
/// <param name="Samples">Interleaved samples, <see cref="Channels"/> per frame.</param>
/// <param name="Channels">1 or more (a mono take plays on every output channel; extra channels are dropped).</param>
/// <param name="SampleRate">The take's rate; the player resamples it linearly to the device's.</param>
/// <param name="StartSeconds">Where on the timeline the take's first frame is (a take rendered for a stretch needs no audio before it).</param>
public sealed record AudioClip(float[] Samples, int Channels, int SampleRate, double StartSeconds = 0)
{
    /// <summary>The take's length in frames.</summary>
    public long Frames => Channels <= 0 ? 0 : Samples.Length / Channels;
    /// <summary>The take's length in seconds.</summary>
    public double Seconds => SampleRate <= 0 ? 0 : (double)Frames / SampleRate;
}

/// <summary>
/// Plays a stretch of one of several aligned takes and switches between them at the playhead (see the file header): an
/// <see cref="IAuxAudioGenerator"/> for an <see cref="AuxAudioOutput"/>. <see cref="Play"/> starts a take's stretch (looping or
/// once), <see cref="Select"/> switches the take without restarting, <see cref="SetTake"/> replaces a take (crossfading when it is the
/// one that sounds), <see cref="Stop"/> fades out. <see cref="Playing"/>, <see cref="Take"/> and <see cref="PositionSeconds"/> are read
/// on any thread (the playhead). Control methods: any thread, typically the UI thread.
/// </summary>
public sealed class ClipSwitchPlayer : IAuxAudioGenerator
{
    /// <summary>The most takes a player holds.</summary>
    public const int MaxTakes = 8;
    /// <summary>The default switch crossfade (ms): short enough to hear the difference at the playhead, long enough not to click.</summary>
    public const float DefaultCrossfadeMs = 50f;
    /// <summary>The fade at a stop and at a start (ms).</summary>
    public const float EdgeFadeMs = 12f;

    private enum Kind : byte { Play, Select, SetTake, Stop }

    private sealed record Cmd(Kind Kind, int Take, AudioClip? Clip, double From, double To, bool Loop);

    /// <summary>A reader on one take: where it is (in the take's frames) and its gain ramp.</summary>
    private struct Reader
    {
        public AudioClip? Clip;
        public double Pos;
        public float Gain, Step;   // gain now, its change per output frame (negative: fading out)
        public readonly bool Live => Clip is not null && (Gain > 0f || Step > 0f);
    }

    private readonly ConcurrentQueue<Cmd> _cmds = new();
    private readonly AudioClip?[] _takes = new AudioClip?[MaxTakes];
    private Reader _main, _fade;
    private double _from, _to;
    private bool _loop, _playing;
    private int _take;
    private float _xfadeMs = DefaultCrossfadeMs;

    private int _playingFlag, _takeFlag;
    private long _posBits;

    /// <summary>The switch crossfade in ms (default <see cref="DefaultCrossfadeMs"/>); read by the aux thread at the next switch.</summary>
    public float CrossfadeMs { get => Volatile.Read(ref _xfadeMs); set => Volatile.Write(ref _xfadeMs, Math.Clamp(value, 1f, 2000f)); }

    /// <summary>Is a stretch playing (its fade-out included)? Any thread.</summary>
    public bool Playing => Volatile.Read(ref _playingFlag) != 0;

    /// <summary>The take that sounds (or sounded last). Any thread.</summary>
    public int Take => Volatile.Read(ref _takeFlag);

    /// <summary>The playhead in seconds on the takes' timeline (where the next block starts). Any thread.</summary>
    public double PositionSeconds => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _posBits));

    /// <summary>Hand <paramref name="clip"/> to slot <paramref name="take"/> (null empties it). When that take sounds, the player
    /// crossfades into the new buffer at the playhead.</summary>
    public void SetTake(int take, AudioClip? clip)
    {
        if ((uint)take >= MaxTakes) throw new ArgumentOutOfRangeException(nameof(take));
        _cmds.Enqueue(new Cmd(Kind.SetTake, take, clip, 0, 0, false));
    }

    /// <summary>Play take <paramref name="take"/> from <paramref name="fromSeconds"/> to <paramref name="toSeconds"/>, again from the
    /// start when <paramref name="loop"/>, else stop at the end. A stretch already playing crossfades into this one.</summary>
    public void Play(int take, double fromSeconds, double toSeconds, bool loop)
    {
        if ((uint)take >= MaxTakes) throw new ArgumentOutOfRangeException(nameof(take));
        Volatile.Write(ref _playingFlag, 1);
        Volatile.Write(ref _takeFlag, take);
        _cmds.Enqueue(new Cmd(Kind.Play, take, null, Math.Max(0, fromSeconds), Math.Max(fromSeconds, toSeconds), loop));
    }

    /// <summary>Switch to take <paramref name="take"/> at the playhead (a crossfade of <see cref="CrossfadeMs"/>); the stretch and the
    /// loop go on. Nothing plays: the take is remembered for the next <see cref="Play"/>.</summary>
    public void Select(int take)
    {
        if ((uint)take >= MaxTakes) throw new ArgumentOutOfRangeException(nameof(take));
        Volatile.Write(ref _takeFlag, take);
        _cmds.Enqueue(new Cmd(Kind.Select, take, null, 0, 0, false));
    }

    /// <summary>Fade out and stop.</summary>
    public void Stop()
    {
        Volatile.Write(ref _playingFlag, 0);
        _cmds.Enqueue(new Cmd(Kind.Stop, 0, null, 0, 0, false));
    }

    /// <inheritdoc/>
    public void Render(Span<float> interleaved, int frames, in AuxBlock block)
    {
        int ch = Math.Max(1, block.Channels);
        int rate = block.SampleRate > 0 ? block.SampleRate : 48_000;
        while (_cmds.TryDequeue(out var cmd)) Apply(cmd, rate);
        interleaved[..(frames * ch)].Clear();
        if (!_main.Live && !_fade.Live) { _playing = false; Publish(); return; }
        for (int i = 0; i < frames; i++)
        {
            // The loop's end: the old position rings on into the take's tail while the stretch starts again under a short crossfade.
            if (_playing && _main.Clip is { } clip && _main.Pos >= (_to - clip.StartSeconds) * clip.SampleRate)
            {
                if (_loop)
                {
                    _fade = _main;
                    _fade.Step = -Math.Max(_fade.Gain, 1e-3f) / Frames(EdgeFadeMs, rate);
                    _main.Pos = (_from - clip.StartSeconds) * clip.SampleRate;
                    _main.Gain = 0f;
                    _main.Step = 1f / Frames(EdgeFadeMs, rate);
                }
                else
                {
                    _main.Step = -Math.Max(_main.Gain, 1e-3f) / Frames(EdgeFadeMs, rate);
                    _playing = false;
                }
            }
            Mix(ref _main, interleaved, i, ch, rate);
            Mix(ref _fade, interleaved, i, ch, rate);
        }
        if (!_main.Live && !_fade.Live) _playing = false;
        Publish();
    }

    private void Publish()
    {
        double pos = _main.Clip is { SampleRate: > 0 } c ? _main.Pos / c.SampleRate + c.StartSeconds : _from;
        Interlocked.Exchange(ref _posBits, BitConverter.DoubleToInt64Bits(pos));
        if (!_playing && !_main.Live && !_fade.Live) Volatile.Write(ref _playingFlag, 0);
    }

    private static float Frames(float ms, int rate) => Math.Max(1f, ms * rate / 1000f);

    private void Apply(Cmd cmd, int rate)
    {
        switch (cmd.Kind)
        {
            case Kind.SetTake:
                _takes[cmd.Take] = cmd.Clip;
                if (_playing && cmd.Take == _take) CrossTo(cmd.Clip, Now(), rate, CrossfadeMs);
                break;
            case Kind.Select:
                if (cmd.Take == _take && _playing) break;
                _take = cmd.Take;
                if (_playing) CrossTo(_takes[_take], Now(), rate, CrossfadeMs);
                break;
            case Kind.Play:
                _take = cmd.Take;
                _from = cmd.From;
                _to = cmd.To;
                _loop = cmd.Loop;
                CrossTo(_takes[_take], _from, rate, _main.Live ? CrossfadeMs : EdgeFadeMs);
                _playing = _main.Clip is not null;
                break;
            case Kind.Stop:
                _playing = false;
                if (_main.Live) _main.Step = -Math.Max(_main.Gain, 1e-3f) / Frames(EdgeFadeMs, rate);
                if (_fade.Live) _fade.Step = -Math.Max(_fade.Gain, 1e-3f) / Frames(EdgeFadeMs, rate);
                break;
        }
    }

    /// <summary>The playhead on the timeline (s).</summary>
    private double Now() => _main.Clip is { SampleRate: > 0 } c ? _main.Pos / c.SampleRate + c.StartSeconds : _from;

    /// <summary>The sounding reader fades out where it is; <paramref name="clip"/> fades in at <paramref name="seconds"/>.</summary>
    private void CrossTo(AudioClip? clip, double seconds, int rate, float ms)
    {
        if (_main.Live)
        {
            _fade = _main;
            _fade.Step = -Math.Max(_fade.Gain, 1e-3f) / Frames(ms, rate);
        }
        _main = default;
        if (clip is null || clip.SampleRate <= 0) return;
        _main.Clip = clip;
        _main.Pos = (seconds - clip.StartSeconds) * clip.SampleRate;
        _main.Gain = 0f;
        _main.Step = 1f / Frames(ms, rate);
    }

    /// <summary>One output frame of a reader (linear interpolation at the device's rate), its gain ramp advanced.</summary>
    private static void Mix(ref Reader r, Span<float> dst, int frame, int ch, int rate)
    {
        if (r.Clip is not { } clip) return;
        if (r.Step != 0f)
        {
            r.Gain += r.Step;
            if (r.Gain >= 1f) { r.Gain = 1f; r.Step = 0f; }
            else if (r.Gain <= 0f) { r = default; return; }
        }
        long total = clip.Frames;
        long i0 = (long)r.Pos;
        if (i0 >= 0 && i0 < total)
        {
            float t = (float)(r.Pos - i0);
            long i1 = Math.Min(total - 1, i0 + 1);
            int cc = clip.Channels;
            float[] s = clip.Samples;
            for (int c = 0; c < ch; c++)
            {
                int k = cc == 1 ? 0 : Math.Min(c, cc - 1);
                float a = s[i0 * cc + k], b = s[i1 * cc + k];
                dst[frame * ch + c] += (a + (b - a) * t) * r.Gain;
            }
        }
        r.Pos += (double)clip.SampleRate / rate;
    }
}
