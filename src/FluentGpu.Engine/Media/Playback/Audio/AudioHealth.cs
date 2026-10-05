namespace FluentGpu.Media;

/// <summary>
/// Process-wide audio health counters for the frame ledger (FrameLedger's audio stream), sampled at its memory cadence:
/// <list type="bullet">
/// <item><b>Xruns</b> — APP side: the feed thread found a voice's ring empty (an <see cref="AudioFeedThread.XrunCount"/> incident and
/// the frames it lost). The producer (decode, fetch) lost its race.</item>
/// <item><b>Buffer drained</b> — at the ENDPOINT: a write found the device's buffer empty (no padding) when the same device's previous
/// write had left audio queued, so the buffer ran dry between two of our writes. That is ambiguous on purpose: the RT thread may
/// have been late (scheduling, a long write), the feed may have had nothing to give (then an xrun is counted too), or the stream
/// may simply have run out at an end or a pause that did not stop the client. It says audio ran out at the endpoint, not whose fault
/// it was; read it beside the xruns and the minimum padding.</item>
/// <item><b>Minimum padding</b> — the lowest queued frame count any write saw in the window: how close the endpoint came to dry.</item>
/// </list>
/// The edge decision is the DEVICE's (it keeps its own previous padding, so two endpoints never mix); this class only counts.
/// Producers are the audio RT threads: Interlocked adds and one compare-exchange min, no lock, no allocation — RT-legal.
/// <see cref="Sample"/> reads the cumulative counters and takes (resets) the windowed minimum.
/// </summary>
public static class AudioHealth
{
    private static long s_deviceWrites, s_drained, s_xruns, s_xrunFrames;
    private static long s_paddingMin = long.MaxValue;
    private static int s_bufferFrames, s_rate;

    /// <summary>RT THREAD (a device sink's write): the endpoint buffer's queued frames just before the write, and whether the device
    /// judged this write a drained edge (started, empty now, non-empty at its own previous write).</summary>
    public static void NoteDeviceWrite(int paddingFrames, int bufferFrames, int rate, bool drainedEdge)
    {
        Interlocked.Increment(ref s_deviceWrites);
        if (drainedEdge) Interlocked.Increment(ref s_drained);
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
        return new AudioHealthSample(Interlocked.Read(ref s_deviceWrites), Interlocked.Read(ref s_drained),
            Interlocked.Read(ref s_xruns), Interlocked.Read(ref s_xrunFrames),
            min == long.MaxValue ? -1 : (int)min, Volatile.Read(ref s_bufferFrames), Volatile.Read(ref s_rate));
    }
}

/// <summary>One <see cref="AudioHealth.Sample"/>: cumulative device writes, buffer-drained edges, app-side xrun incidents and lost
/// frames; the window's minimum queued padding (frames, -1 = no write), the endpoint buffer size and its rate.</summary>
public readonly record struct AudioHealthSample(long DeviceWrites, long BufferDrainedEdges, long Xruns, long XrunFrames,
    int PaddingMinFrames, int BufferFrames, int Rate);
