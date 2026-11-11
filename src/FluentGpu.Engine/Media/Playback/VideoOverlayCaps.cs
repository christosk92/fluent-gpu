using System;

namespace FluentGpu.Media;

/// <summary>What one display output reports for an overlay plane of one pixel format (<c>DXGI_OVERLAY_SUPPORT_FLAG</c>: the values
/// are the DXGI ones, so the Windows probe casts the flags straight in).</summary>
[Flags]
public enum OverlayPlaneSupport : byte
{
    None = 0,
    /// <summary>The format can scan out directly on a hardware plane.</summary>
    Direct = 1,
    /// <summary>The format can scan out on a hardware plane through the display's scaler.</summary>
    Scaling = 2,
}

/// <summary>
/// F087: the overlay-plane support one display output reported for NV12 / YUY2 / BGRA (<c>IDXGIOutput3::CheckOverlaySupport</c>, asked
/// with a D3D11 device by the Windows <c>VideoOverlayProbe</c>, once per output a video meets). It is the ONE verdict the
/// <c>[d3d12.present]</c> / <c>[video.overlay]</c> lines read, so a claim about direct scan-out is only ever made from a probe
/// result. A default value is "not probed".
/// The media engines' output format is NOT chosen from it: in windowless mode the Simple Video Renderer presents the decoder's NV12
/// itself (see <c>VideoMediaEngine.CreateEngine</c>).
/// </summary>
public readonly record struct VideoOverlayCaps(bool Probed, OverlayPlaneSupport Nv12, OverlayPlaneSupport Yuy2, OverlayPlaneSupport Bgra)
{
    /// <summary>Any probed format can take a hardware plane.</summary>
    public bool AnyPlane => Probed && (Nv12 != OverlayPlaneSupport.None || Yuy2 != OverlayPlaneSupport.None || Bgra != OverlayPlaneSupport.None);

    /// <summary>The caps from the raw <c>DXGI_OVERLAY_SUPPORT_FLAG</c> words of the three probed formats (bits other than DIRECT / SCALING are dropped).</summary>
    public static VideoOverlayCaps FromDxgiFlags(uint nv12, uint yuy2, uint bgra)
        => new(true, (OverlayPlaneSupport)(nv12 & 3u), (OverlayPlaneSupport)(yuy2 & 3u), (OverlayPlaneSupport)(bgra & 3u));

    /// <summary>The verdict for the log line: <c>nv12=direct+scaling yuy2=none bgra=direct</c>, or <c>unprobed</c>.</summary>
    public string Describe()
        => Probed ? $"nv12={Name(Nv12)} yuy2={Name(Yuy2)} bgra={Name(Bgra)}" : "unprobed";

    private static string Name(OverlayPlaneSupport s) => s switch
    {
        OverlayPlaneSupport.None => "none",
        OverlayPlaneSupport.Direct => "direct",
        OverlayPlaneSupport.Scaling => "scaling",
        _ => "direct+scaling",
    };
}
