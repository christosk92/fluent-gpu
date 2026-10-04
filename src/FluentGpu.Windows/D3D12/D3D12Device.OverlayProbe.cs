using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

public sealed unsafe partial class D3D12Device
{
    // F249: what does the output the window sits on say about overlay planes? One IDXGIOutput3::CheckOverlaySupport per candidate format
    // (NV12, YUY2, BGRA: the formats Chromium's direct_composition_support.cc probes), asked about THIS device's queue. Cold path: it runs
    // on SamplePresentTopology's edge (first resolve, monitor change) only, never per present. A DXGI older than IDXGIOutput3 or a
    // failing call is "unprobed" (default caps), which keeps every format and placement decision on its old behaviour.
    private FluentGpu.Media.VideoOverlayCaps ProbeOverlaySupport(IDXGIOutput* output)
    {
        if (output == null || _queue == null) return default;
        IDXGIOutput3* out3 = null;
        if ((int)output->QueryInterface(__uuidof<IDXGIOutput3>(), (void**)&out3) < 0 || out3 == null) return default;
        try
        {
            IUnknown* concerned = (IUnknown*)_queue;
            uint nv12 = 0, yuy2 = 0, bgra = 0;
            // An errored call says nothing about the planes (DXGI may reject the queue as the concerned device): the whole probe stays
            // "unprobed" rather than reporting a confident "none" made from an error.
            if ((int)out3->CheckOverlaySupport(DXGI_FORMAT.DXGI_FORMAT_NV12, concerned, &nv12) < 0) return default;
            if ((int)out3->CheckOverlaySupport(DXGI_FORMAT.DXGI_FORMAT_YUY2, concerned, &yuy2) < 0) return default;
            if ((int)out3->CheckOverlaySupport(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, concerned, &bgra) < 0) return default;
            return FluentGpu.Media.VideoOverlayCaps.FromDxgiFlags(nv12, yuy2, bgra);
        }
        finally
        {
            out3->Release();
        }
    }

    // The note of the [d3d12.present] line for an output the render adapter owns: the probe's verdict, not an assumption.
    private static string OverlayNote(in FluentGpu.Media.VideoOverlayCaps caps)
        => !caps.Probed ? "overlay-unprobed"
         : caps.AnyPromotable ? "overlay-planes-reported;-promotion-needs---fg-video-overlay"
         : "no-overlay-planes-reported;-video-is-composed-by-DWM";
}
