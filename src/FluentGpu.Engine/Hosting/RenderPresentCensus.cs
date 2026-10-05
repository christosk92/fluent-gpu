using System.Globalization;
using System.Text;

namespace FluentGpu.Hosting;

/// <summary>What made a render-thread submit run (F242): the render-side half of "what drove this present".</summary>
internal enum RenderPresentCause
{
    /// <summary>A newly published UI scene was recorded and presented.</summary>
    Fresh,
    /// <summary>The retained scene re-recorded because a compositor animation row moved.</summary>
    Anim,
    /// <summary>A scroll pose (or a pose the slices could not honour) moved pixels.</summary>
    Scroll,
    /// <summary>An image crossfade advanced pixels under a byte-identical stream.</summary>
    Crossfade,
    /// <summary>Anything else that survived the elision gates.</summary>
    Other,
}

/// <summary>
/// The render thread's per-cause present tally for the always-on <c>[wake]</c> line (F242). The UI-side census
/// (<c>WakeDiagnostics</c>) sees why each UI frame ran; it cannot see the presents the render thread makes on its own while
/// motion is live (a compositor loop, a scroll poser, a crossfade), which is where most of a docked-video or lyrics window's
/// 40-120 presents a second come from while <c>reconciledOrLaidOut</c> reads ~1. Each submit that actually reaches the device
/// is noted once, with its <see cref="RenderPresentCause"/> and whether it RECORDED the scene or only re-composed retained
/// slices (<c>recordedPresents</c> vs <c>compositePresents</c>).
/// <para>Render thread writes (one <see cref="Note"/> per submit, plain increments published with a volatile write), the UI
/// thread reads once per report window through <see cref="AppendWindow"/>, which prints the delta since the last call. Zero
/// allocation on the write path.</para>
/// </summary>
internal sealed class RenderPresentCensus
{
    private const int CauseCount = 5;
    private static readonly string[] s_causeNames = ["fresh", "anim", "scroll", "crossfade", "other"];

    private readonly long[] _cause = new long[CauseCount];       // cumulative
    private readonly long[] _cause0 = new long[CauseCount];      // their values when the last report was written
    private long _recorded, _composite, _recorded0, _composite0;

    /// <summary>The ONE cause of a submit that is going to the device. A fresh publication wins; otherwise a moved compositor
    /// animation row, then a scroll pose (<paramref name="scrollMoved"/>: the poser moved or a record is required), then an image
    /// crossfade, else other. Pure (the unit tests pin the precedence).</summary>
    internal static RenderPresentCause Classify(bool fresh, bool animChanged, bool scrollMoved, bool crossfades)
    {
        if (fresh) return RenderPresentCause.Fresh;
        if (animChanged) return RenderPresentCause.Anim;
        if (scrollMoved) return RenderPresentCause.Scroll;
        if (crossfades) return RenderPresentCause.Crossfade;
        return RenderPresentCause.Other;
    }

    /// <summary>Render thread: one submit going to the device, for <paramref name="cause"/>; <paramref name="recorded"/> when it
    /// re-recorded the scene (false for a composite-only turn that only re-placed retained slices).</summary>
    public void Note(RenderPresentCause cause, bool recorded)
    {
        int i = (int)cause;
        System.Threading.Volatile.Write(ref _cause[i], _cause[i] + 1);
        if (recorded) System.Threading.Volatile.Write(ref _recorded, _recorded + 1);
        else System.Threading.Volatile.Write(ref _composite, _composite + 1);
    }

    /// <summary>UI thread, report cadence: append <c>renderPresents=N:cause×n,… recordedPresents=R compositePresents=C</c> for the
    /// submits since the previous call (<c>renderPresents=0</c> when none), and open the next window.</summary>
    public void AppendWindow(StringBuilder sb)
    {
        // ONE snapshot: every cumulative counter is read exactly once, and the printed window AND the rebase both come from
        // these locals, so a Note() landing mid-report is counted in the next window (never lost from both) and the printed
        // total equals the printed per-cause sum.
        Span<long> now = stackalloc long[CauseCount];
        long total = 0;
        for (int i = 0; i < CauseCount; i++)
        {
            now[i] = System.Threading.Volatile.Read(ref _cause[i]);
            total += now[i] - _cause0[i];
        }
        long recorded = System.Threading.Volatile.Read(ref _recorded), composite = System.Threading.Volatile.Read(ref _composite);
        sb.Append(CultureInfo.InvariantCulture, $" renderPresents={total}");
        if (total > 0)
        {
            sb.Append(':');
            bool first = true;
            for (int i = 0; i < CauseCount; i++)
            {
                long n = now[i] - _cause0[i];
                if (n == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(s_causeNames[i]).Append(CultureInfo.InvariantCulture, $"×{n}");
            }
        }
        sb.Append(CultureInfo.InvariantCulture, $" recordedPresents={recorded - _recorded0} compositePresents={composite - _composite0}");
        for (int i = 0; i < CauseCount; i++) _cause0[i] = now[i];
        _recorded0 = recorded;
        _composite0 = composite;
    }
}
