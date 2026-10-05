using System;
using System.Threading;

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

/// <summary>The pixel format the media engines are asked to output into their windowless swap chain.</summary>
public enum VideoOutputFormat : byte
{
    /// <summary>B8G8R8A8 - what both engines have always forced: a video-processor NV12 -> BGRA pass per decoded frame.</summary>
    Bgra = 0,
    /// <summary>NV12 - the decoder's own format, the one a YUV overlay plane can take (F249).</summary>
    Nv12 = 1,
}

/// <summary>
/// F249: the overlay-plane support the render adapter's output reported for NV12 / YUY2 / BGRA, probed once per output by the
/// Windows device (<c>IDXGIOutput3::CheckOverlaySupport</c>) and published process-wide through <see cref="Publish"/>. It is the
/// ONE verdict the diagnostic line, the NV12 output switch (<see cref="ChooseOutputFormat"/>) and the overlay promotion
/// (<c>IVideoPresenter.SupportsOverlay</c>) read, so a claim about direct scan-out is only ever made from a probe result. A
/// default value is "not probed": nothing is promoted and the old BGRA output is kept.
/// </summary>
public readonly record struct VideoOverlayCaps(bool Probed, OverlayPlaneSupport Nv12, OverlayPlaneSupport Yuy2, OverlayPlaneSupport Bgra)
{
    /// <summary>NV12 can take a hardware plane (direct or scaled) - the precondition of choosing it as the engine output.</summary>
    public bool Nv12Promotable => Probed && Nv12 != OverlayPlaneSupport.None;

    /// <summary>Any probed format can take a hardware plane - the precondition of placing the video above the UI plane.</summary>
    public bool AnyPromotable => Probed && (Nv12 != OverlayPlaneSupport.None || Yuy2 != OverlayPlaneSupport.None || Bgra != OverlayPlaneSupport.None);

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

    /// <summary>The output format to ask a media engine for: NV12 only when the switch is on AND the probe says the output can put NV12
    /// on a plane, else BGRA (an unprobed output never changes the format).</summary>
    public static VideoOutputFormat ChooseOutputFormat(bool nv12Switch, in VideoOverlayCaps caps)
        => nv12Switch && caps.Nv12Promotable ? VideoOutputFormat.Nv12 : VideoOutputFormat.Bgra;

    // The latest probe, packed (bit 0 probed, bits 1-2 NV12, 3-4 YUY2, 5-6 BGRA) so a reader on another thread (a media engine
    // created off the render thread) sees one consistent verdict.
    private static int s_latest;

    /// <summary>The newest published verdict (<c>default</c> until a window's output has been probed). Any thread.</summary>
    public static VideoOverlayCaps Latest
    {
        get
        {
            int v = Volatile.Read(ref s_latest);
            return (v & 1) == 0 ? default
                : new VideoOverlayCaps(true, (OverlayPlaneSupport)((v >> 1) & 3), (OverlayPlaneSupport)((v >> 3) & 3), (OverlayPlaneSupport)((v >> 5) & 3));
        }
    }

    /// <summary>Publish the verdict of the output just probed (the Windows device, on its render thread). The newest probe wins: the
    /// engines read it when they are created, which is after the window's output was probed.</summary>
    public static void Publish(in VideoOverlayCaps caps)
        => Volatile.Write(ref s_latest, !caps.Probed ? 0 : 1 | ((int)caps.Nv12 << 1) | ((int)caps.Yuy2 << 3) | ((int)caps.Bgra << 5));
}
