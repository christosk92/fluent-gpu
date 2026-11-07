using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The overlay-plane verdict per display output (F087), asked with a D3D11 device - the only "concerned device"
/// <c>IDXGIOutput3::CheckOverlaySupport</c> answers truthfully for. Measured on the Adreno X1-85 (31.0.148.0): the render device's
/// D3D12 queue fails it with E_NOINTERFACE (so the old probe logged <c>overlay[unprobed]</c> on every window, and overlay promotion
/// could never switch on), an <c>ID3D12Device</c> or an 11on12 device "succeeds" with every flag 0, and a real D3D11 device reports
/// NV12 = SCALING with BT.709 studio colour space supported - the planes dxdiag lists (MPO formats: NV12). Chromium probes with its
/// D3D11 device too.
/// <para><b>Which device.</b> The clear engine's own <see cref="MfVideoDevice"/> (<see cref="ProbeWith"/>, on its engine thread right
/// after bring-up: no extra device). Otherwise, only when the overlay switch asks a presenter whether it may promote
/// (<see cref="ForMonitor"/> with a miss), one background probe leases the same shared device, which then lingers for the next clear
/// engine. Never at startup and never for an audio-only session: no D3D11 device is made for this unless video is on screen.</para>
/// <para><b>Cost.</b> A table of at most <see cref="MaxOutputs"/> (HMONITOR, caps) pairs replaced whole by the probe (cold: once per
/// display configuration a video meets); <see cref="ForMonitor"/> is a lock-free scan, zero allocation, safe on the render thread.</para>
/// </summary>
internal static unsafe class VideoOverlayProbe
{
    private const int MaxOutputs = 8;

    private readonly struct Entry
    {
        public readonly nint Monitor;
        public readonly VideoOverlayCaps Caps;
        public Entry(nint monitor, VideoOverlayCaps caps) { Monitor = monitor; Caps = caps; }
    }

    private static Entry[] s_table = Array.Empty<Entry>();
    private static int s_requested;   // a background probe is queued or running (0/1)
    private static nint s_requestedMonitor;   // the monitor whose miss queued it (recorded as "no plane" if no output of the adapter shows it)
    private static readonly object s_probeGate = new();

    /// <summary>The verdict for the output showing <paramref name="monitor"/>; <c>default</c> (unprobed: nothing is promoted) when that
    /// monitor was never probed. A miss with <paramref name="requestIfMissing"/> queues one background probe. Any thread, no allocation
    /// on a hit.</summary>
    internal static VideoOverlayCaps ForMonitor(nint monitor, bool requestIfMissing)
    {
        if (monitor == 0) return default;
        Entry[] t = Volatile.Read(ref s_table);
        for (int i = 0; i < t.Length; i++)
            if (t[i].Monitor == monitor) return t[i].Caps;
        if (requestIfMissing && Interlocked.CompareExchange(ref s_requested, 1, 0) == 0)
        {
            Volatile.Write(ref s_requestedMonitor, monitor);
            ThreadPool.UnsafeQueueUserWorkItem(static _ => ProbeInBackground(), null);
        }
        return default;
    }

    private static void ProbeInBackground()
    {
        try
        {
            using var lease = MfVideoDevice.Acquire();
            if (lease == null) { Diag.Line("[video.overlay] probe skipped: no D3D11 video device"); return; }
            ProbeWith(lease.Device.D3d, "background");
            // A monitor no output of the render adapter shows (a cross-adapter window) is recorded as probed with no plane, so its
            // lookups hit instead of queueing a probe on every turn.
            nint mon = Volatile.Read(ref s_requestedMonitor);
            lock (s_probeGate)
            {
                Entry[] t = s_table;
                bool known = false;
                for (int i = 0; i < t.Length; i++) known |= t[i].Monitor == mon;
                if (!known && mon != 0 && t.Length < MaxOutputs)
                {
                    var grown = new Entry[t.Length + 1];
                    t.CopyTo(grown, 0);
                    grown[t.Length] = new Entry(mon, new VideoOverlayCaps(true, OverlayPlaneSupport.None, OverlayPlaneSupport.None, OverlayPlaneSupport.None));
                    Volatile.Write(ref s_table, grown);
                    Diag.Line("[video.overlay] the window's monitor is not an output of the render adapter: no plane (composed by DWM)");
                }
            }
        }
        catch (Exception e) { Diag.Line($"[video.overlay] probe failed: {e.Message}"); }
        finally { Volatile.Write(ref s_requested, 0); }
    }

    /// <summary>The clear engine's bring-up hook: probe with its own (already leased) device unless a probe already ran, so a warm rebuild
    /// costs one volatile read.</summary>
    internal static void ProbeIfEmpty(ID3D11Device* d3d)
    {
        if (Volatile.Read(ref s_table).Length == 0) ProbeWith(d3d, "clear-engine");
    }

    /// <summary>Probe every output of <paramref name="d3d"/>'s adapter and replace the table (logs one line per output). Cold: a
    /// handful of DXGI calls; callers are the clear engine's bring-up thread and the background probe.</summary>
    internal static void ProbeWith(ID3D11Device* d3d, string via)
    {
        if (d3d == null) return;
        lock (s_probeGate)
        {
            long t0 = Stopwatch.GetTimestamp();
            IDXGIDevice* dxgiDevice = null;
            IDXGIAdapter* adapter = null;
            if ((int)d3d->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgiDevice) < 0 || dxgiDevice == null) return;
            try
            {
                if ((int)dxgiDevice->GetAdapter(&adapter) < 0 || adapter == null) return;
                Span<Entry> found = stackalloc Entry[MaxOutputs];
                int n = 0;
                for (uint i = 0; n < MaxOutputs; i++)
                {
                    IDXGIOutput* output = null;
                    if ((int)adapter->EnumOutputs(i, &output) < 0 || output == null) break;
                    try
                    {
                        DXGI_OUTPUT_DESC od;
                        if ((int)output->GetDesc(&od) < 0) continue;
                        VideoOverlayCaps caps = ProbeOutput(output, (IUnknown*)d3d, out string extra);
                        found[n++] = new Entry((nint)od.Monitor, caps);
                        Diag.Line($"[video.overlay] probe output={new string((char*)&od.DeviceName)} overlay[{caps.Describe()}] {extra} via={via}");
                    }
                    finally { output->Release(); }
                }
                Volatile.Write(ref s_table, found[..n].ToArray());
                Diag.Line($"[video.overlay] probe done outputs={n} ms={Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.0}");
            }
            finally
            {
                if (adapter != null) adapter->Release();
                dxgiDevice->Release();
            }
        }
    }

    // NV12 / YUY2 / BGRA through IDXGIOutput3 (the VideoOverlayCaps verdict), plus - for the log only - P010, whether NV12 BT.709 studio
    // range can stay YUV on the plane (IDXGIOutput4) and the output's hardware-composition support (IDXGIOutput6). An errored call
    // says nothing: the whole verdict stays "unprobed" rather than a confident "none" made from an error.
    private static VideoOverlayCaps ProbeOutput(IDXGIOutput* output, IUnknown* concerned, out string extra)
    {
        extra = "";
        IDXGIOutput3* out3 = null;
        if ((int)output->QueryInterface(__uuidof<IDXGIOutput3>(), (void**)&out3) < 0 || out3 == null) return default;
        try
        {
            uint nv12 = 0, yuy2 = 0, bgra = 0, p010 = 0;
            if ((int)out3->CheckOverlaySupport(DXGI_FORMAT.DXGI_FORMAT_NV12, concerned, &nv12) < 0) return default;
            if ((int)out3->CheckOverlaySupport(DXGI_FORMAT.DXGI_FORMAT_YUY2, concerned, &yuy2) < 0) return default;
            if ((int)out3->CheckOverlaySupport(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, concerned, &bgra) < 0) return default;
            out3->CheckOverlaySupport(DXGI_FORMAT.DXGI_FORMAT_P010, concerned, &p010);
            string cs709 = "?";
            IDXGIOutput4* out4 = null;
            if ((int)output->QueryInterface(__uuidof<IDXGIOutput4>(), (void**)&out4) >= 0 && out4 != null)
            {
                uint f = 0;
                cs709 = (int)out4->CheckOverlayColorSpaceSupport(DXGI_FORMAT.DXGI_FORMAT_NV12,
                    DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709, concerned, &f) < 0 ? "?" : (f & 1) != 0 ? "yes" : "no";
                out4->Release();
            }
            string hw = "?";
            IDXGIOutput6* out6 = null;
            if ((int)output->QueryInterface(__uuidof<IDXGIOutput6>(), (void**)&out6) >= 0 && out6 != null)
            {
                uint f = 0;
                if ((int)out6->CheckHardwareCompositionSupport(&f) >= 0)
                    hw = (f & 3) == 3 ? "fullscreen+windowed" : (f & 2) != 0 ? "windowed" : (f & 1) != 0 ? "fullscreen" : "none";
                out6->Release();
            }
            extra = $"p010={((p010 & 3) != 0 ? "yes" : "no")} nv12-bt709={cs709} hwcomp={hw}";
            return VideoOverlayCaps.FromDxgiFlags(nv12, yuy2, bgra);
        }
        finally { out3->Release(); }
    }
}
