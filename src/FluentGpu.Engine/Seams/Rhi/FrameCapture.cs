namespace FluentGpu.Rhi;

public partial interface IGpuDevice
{
    /// <summary>Render-owner thread, right after a PRESENT: read the just-presented primary back buffer back to the CPU as
    /// tightly packed, top-down BGRA8 (docs/plans/evidence-diagnostics-implementation.md §A.6 — the evidence bundle's
    /// <c>frame.png</c>, the same readback <c>--screenshot</c> and <c>--repaint-identity</c> use). It stalls the GPU for
    /// that one turn and allocates the pixel buffer: on demand only, never on a steady-state frame. False (the default)
    /// when the backend has no back buffer to read (the headless model counts the request instead).</summary>
    bool TryCaptureBackBuffer(out byte[]? bgra, out int width, out int height)
    {
        bgra = null;
        width = height = 0;
        return false;
    }
}
