using TerraFX.Interop.DirectX;

namespace FluentGpu.Rhi.D3D12;

public sealed unsafe partial class D3D12Device
{
    // F087: what does the output the window sits on say about overlay planes? The verdict comes from VideoOverlayProbe, which asks
    // IDXGIOutput3::CheckOverlaySupport with a D3D11 device: with THIS device's D3D12 queue as the concerned device the call fails
    // (E_NOINTERFACE on the Adreno X1-85), and with an ID3D12Device it reports every flag 0, so the old queue probe logged "unprobed"
    // on every window and promotion could never switch on. Cold path (SamplePresentTopology's edge only). It never creates a device
    // here: a monitor no video has met yet is "unprobed" on this line, and the probe's own [video.overlay] line carries the verdict.
    private FluentGpu.Media.VideoOverlayCaps ProbeOverlaySupport(IDXGIOutput* output)
    {
        if (output == null) return default;
        DXGI_OUTPUT_DESC od;
        if ((int)output->GetDesc(&od) < 0) return default;
        return FluentGpu.Media.Windows.VideoOverlayProbe.ForMonitor((nint)od.Monitor, requestIfMissing: false);
    }

    // The note of the [d3d12.present] line for an output the render adapter owns: the probe's verdict, not an assumption.
    private static string OverlayNote(in FluentGpu.Media.VideoOverlayCaps caps)
        => !caps.Probed ? "overlay-probed-when-a-video-first-plays-(see-[video.overlay])"
         : caps.AnyPromotable ? "overlay-planes-reported;-promotion-needs---fg-video-overlay"
         : "no-overlay-planes-reported;-video-is-composed-by-DWM";
}
