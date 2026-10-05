namespace FluentGpu.Media;

/// <summary>
/// Process-wide audio health counters, so a stutter can be placed: APP-side (the feed thread found its ring empty — an
/// <see cref="AudioFeedThread.XrunCount"/> incident, the producer lost its race) or DEVICE-side (the render endpoint's own buffer
/// ran dry between two of our writes — the RT thread or the OS audio engine was late, whatever the ring held). The frame ledger
/// samples them at its memory cadence (FrameLedger's audio stream).
/// <para>Producers are the audio RT threads (WASAPI <c>Write</c>, the feed thread's starve accounting): Interlocked adds and one
/// compare-exchange min, no lock, no allocation — RT-legal. <see cref="Sample"/> reads the cumulative counters and takes (resets)
/// the windowed minimum.</para>
/// </summary>
public static class AudioHealth
{
    private static long s_deviceWrites, s_deviceDry, s_xruns, s_xrunFrames;
    private static long s_paddingMin = long.MaxValue;
    private static int s_bufferFrames, s_rate, s_lastPadding;

    /// <summary>RT THREAD (the device sink's write): the endpoint buffer's queued frames just before a write. A started stream whose
    /// buffer was non-empty at the previous write and is EMPTY now drained between the two: the device played silence (a dry edge).</summary>
    public static void NoteDevicePadding(int paddingFrames, int bufferFrames, int rate, bool started)
    {
        Interlocked.Increment(ref s_deviceWrites);
        if (started && paddingFrames == 0 && s_lastPadding > 0) Interlocked.Increment(ref s_deviceDry);
        s_lastPadding = paddingFrames;
        s_bufferFrames = bufferFrames;
        s_rate = rate;
        long cur;
        while (paddingFrames < (cur = Volatile.Read(ref s_paddingMin))
               && Interlocked.CompareExchange(ref s_paddingMin, paddingFrames, cur) != cur) { }
    }

    /// <summary>The device stopped or was reset: the next empty buffer is not a dry edge.</summary>
    public static void NoteDeviceIdle() => s_lastPadding = 0;

    /// <summary>RT THREAD (the feed thread): one app-side underrun incident (<paramref name="frames"/> = 0) or its lost frames.</summary>
    public static void NoteXrun(int incidents, long frames)
    {
        if (incidents != 0) Interlocked.Add(ref s_xruns, incidents);
        if (frames != 0) Interlocked.Add(ref s_xrunFrames, frames);
    }

    /// <summary>Cumulative counters plus the minimum padding since the previous call (reset by it; -1 = no write in the window).</summary>
    public static AudioHealthSample Sample()
    {
        long min = Interlocked.Exchange(ref s_paddingMin, long.MaxValue);
        return new AudioHealthSample(Interlocked.Read(ref s_deviceWrites), Interlocked.Read(ref s_deviceDry),
            Interlocked.Read(ref s_xruns), Interlocked.Read(ref s_xrunFrames),
            min == long.MaxValue ? -1 : (int)min, Volatile.Read(ref s_bufferFrames), Volatile.Read(ref s_rate));
    }
}

/// <summary>One <see cref="AudioHealth.Sample"/>: cumulative device writes, device dry edges, app-side xrun incidents and lost
/// frames; the window's minimum queued padding (frames, -1 = no write), the endpoint buffer size and its rate.</summary>
public readonly record struct AudioHealthSample(long DeviceWrites, long DeviceDryEdges, long Xruns, long XrunFrames,
    int PaddingMinFrames, int BufferFrames, int Rate);
