namespace FluentGpu.Media;

/// <summary>
/// Process-wide audio health counters for the frame ledger (FrameLedger's audio stream), sampled at its memory cadence:
/// <list type="bullet">
/// <item><b>Xruns</b> — APP side: the feed thread found a voice's ring empty (an <see cref="AudioFeedThread.XrunCount"/> incident and
/// the frames it lost). The producer (decode, fetch) lost its race.</item>
/// <item><b>Device underruns</b> — at the ENDPOINT: a mirror of <see cref="IBufferedAudioSink.DeviceUnderruns"/>. The device decides
/// (WASAPI: <c>DeviceUnderrunRule</c>, a running stream whose queue is empty at a write after a full buffer has gone through since its
/// start or reset) and reports each decision here as it counts it, so the ledger and <c>PcmAudioSession.DeviceUnderrunCount</c> read
/// one mechanism; this is the process-wide sum over every device. An underrun with no xrun beside it is downstream of the app's ring
/// (a late RT write, an audiodg stall); one with an xrun is the producer's.</item>
/// <item><b>Minimum padding</b> — the lowest queued frame count any write's first padding read saw in the window: how close the
/// endpoint came to dry.</item>
/// </list>
/// Producers are the audio RT threads: Interlocked adds and one compare-exchange min, no lock, no allocation — RT-legal.
/// <see cref="Sample"/> reads the cumulative counters and takes (resets) the windowed minimum.
/// </summary>
public static class AudioHealth
{
    private static long s_deviceWrites, s_underruns, s_xruns, s_xrunFrames;
    private static long s_paddingMin = long.MaxValue;
    private static int s_bufferFrames, s_rate;

    /// <summary>RT THREAD (a device sink's write): the endpoint queue's padding at the write's first read, and whether the device
    /// counted this write as an underrun (its own <see cref="IBufferedAudioSink.DeviceUnderruns"/> decision).</summary>
    public static void NoteDeviceWrite(int paddingFrames, int bufferFrames, int rate, bool deviceUnderrun)
    {
        Interlocked.Increment(ref s_deviceWrites);
        if (deviceUnderrun) Interlocked.Increment(ref s_underruns);
        Volatile.Write(ref s_bufferFrames, bufferFrames);
        Volatile.Write(ref s_rate, rate);
        long cur;
        while (paddingFrames < (cur = Volatile.Read(ref s_paddingMin))
               && Interlocked.CompareExchange(ref s_paddingMin, paddingFrames, cur) != cur) { }
    }

    /// <summary>RT THREAD (the feed thread): app-side underrun incidents and/or the frames they lost.</summary>
    public static void NoteXrun(int incidents, long frames)
    {
        if (incidents != 0) Interlocked.Add(ref s_xruns, incidents);
        if (frames != 0) Interlocked.Add(ref s_xrunFrames, frames);
    }

    /// <summary>Cumulative counters plus the minimum padding since the previous call (reset by it; -1 = no write in the window).</summary>
    public static AudioHealthSample Sample()
    {
        long min = Interlocked.Exchange(ref s_paddingMin, long.MaxValue);
        return new AudioHealthSample(Interlocked.Read(ref s_deviceWrites), Interlocked.Read(ref s_underruns),
            Interlocked.Read(ref s_xruns), Interlocked.Read(ref s_xrunFrames),
            min == long.MaxValue ? -1 : (int)min, Volatile.Read(ref s_bufferFrames), Volatile.Read(ref s_rate));
    }
}

/// <summary>One <see cref="AudioHealth.Sample"/>: cumulative device writes, device underruns, app-side xrun incidents and lost
/// frames; the window's minimum queued padding (frames, -1 = no write), the endpoint buffer size and its rate.</summary>
public readonly record struct AudioHealthSample(long DeviceWrites, long DeviceUnderruns, long Xruns, long XrunFrames,
    int PaddingMinFrames, int BufferFrames, int Rate);
