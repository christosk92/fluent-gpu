namespace FluentGpu.Hosting.Threading;

/// <summary>Async device-lost recovery rendezvous (design/subsystems/threading-render-seam.md §9), Step 4. Coordinates
/// the pause→recover→resume handshake between the UI thread and the render thread when the GPU device is lost (TDR /
/// driver reset / hung). The device holds the actual lost-reason word (set on the render thread when a submit/present
/// fails, or when a bounded fence wait times out on a removed device); this class carries only the two-way request/done
/// flags. The reverse-direction volatiles (RecoverRequest UI→render, RecoverDone render→UI) are sanctioned by the
/// confinement table. Constructed only when the render thread exists (default/force-sync never lose-recover this way —
/// the single-thread path keeps its existing throw-on-loss behavior).</summary>
public sealed class DeviceLostCoordinator
{
    /// <summary>UI → render: 1 ⇒ the UI observed a lost device and asks the render thread to rebuild it. The UI stops
    /// publishing frames while this is set; the render thread parks-then-recovers on the next wake.</summary>
    public volatile int RecoverRequest;

    /// <summary>Render → UI: 1 ⇒ <c>RecoverDevice</c> completed. The UI clears both flags, re-realizes resident images,
    /// and resumes the (whole-tree-dirty) frame loop.</summary>
    public volatile int RecoverDone;
}
