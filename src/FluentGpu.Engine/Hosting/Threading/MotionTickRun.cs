namespace FluentGpu.Hosting.Threading;

/// <summary>
/// The render thread's missed-motion-tick accounting (<c>RenderThread.MissedMotionTicks</c>), as a pure value: a RUN is
/// a stretch of presents paced on the display clock while render motion stays live. A paced present for tick <c>n</c>
/// after one for tick <c>m</c> in the same run skipped the <c>n − m − 1</c> compositor ticks between them — each one a
/// vblank the glass showed the previous frame for, charged to the producer. Render motion going idle
/// (<see cref="Break"/>) or an unpaced present ends the run, so the vblanks of an idle stretch are never charged to the
/// first present that follows it (they are idle, not missed). Zero allocation; render thread only.
/// </summary>
public struct MotionTickRun
{
    private long _lastTick;

    /// <summary>Render motion is not live: the next paced present starts a new run.</summary>
    public void Break() => _lastTick = 0;

    /// <summary>A present for display tick <paramref name="tickSeq"/> (0 = unpaced — it ends the run). Returns the ticks
    /// it skipped since the run's previous present (0 on the first present of a run).</summary>
    public long Presented(long tickSeq)
    {
        long missed = tickSeq != 0 && _lastTick != 0 && tickSeq > _lastTick + 1 ? tickSeq - _lastTick - 1 : 0;
        _lastTick = tickSeq;
        return missed;
    }
}
