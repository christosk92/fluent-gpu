namespace FluentGpu.Hosting.Threading;

public struct PresentCadenceInput
{
    public long TickSeq;            // the render display clock's seq at the top of this turn (0 = no clock)
    public long LastPresentedTickSeq;
    public bool HasFreshPublication; // seam has an unconsumed publication
    public bool MotionDue;           // compositor rows or a scroll lease want a tick
    public bool CreditHeld;          // present-slot credit in hand (ISO §4.2)
    public bool Unpaced;             // no waitable / no display clock (headless, RDP): never gate on the tick
}

public enum PresentVerdict : byte { Skip = 0, PresentFresh = 1, PresentMotion = 2 }

/// <summary>ONE present per compositor tick per window. A fresh publication that lands after this tick's present waits
/// for the next tick (DropOldest hands the newest one then — SceneFramePublisher.TryAcquire); a motion tick with no
/// fresh publication re-presents the retained scene with new poses/offsets. Unpaced targets present whenever they
/// have something (the credit alone throttles them).</summary>
public static class PresentCadence
{
    public static PresentVerdict Decide(in PresentCadenceInput x)
    {
        if (!x.HasFreshPublication && !x.MotionDue) return PresentVerdict.Skip;
        if (!x.Unpaced)
        {
            if (!x.CreditHeld) return PresentVerdict.Skip;
            if (x.TickSeq != 0 && x.TickSeq == x.LastPresentedTickSeq) return PresentVerdict.Skip;   // already presented this vblank
        }
        return x.HasFreshPublication ? PresentVerdict.PresentFresh : PresentVerdict.PresentMotion;
    }
}
