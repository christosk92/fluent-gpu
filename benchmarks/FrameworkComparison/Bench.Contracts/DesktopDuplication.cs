using System.Runtime.InteropServices;

namespace Bench.Contracts;

/// <summary>
/// DXGI Desktop Duplication of the monitor that shows the benchmark window: every frame DWM composes is delivered once,
/// with the QPC time it was presented (<c>DXGI_OUTDUPL_FRAME_INFO.LastPresentTime</c>). Small regions of a frame are
/// copied on the GPU into one staging texture and read back after the frame is released, so judging a frame costs a
/// fraction of a millisecond. (A GDI <c>BitBlt</c> of the screen DC, measured on the bench machine, blocks for one full
/// composition per call - 8.3 ms at 120 Hz, 16.7 ms for a full client - so it cannot keep up with one sample per frame.)
/// </summary>
internal sealed unsafe class DesktopDuplication : IDisposable
{
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private readonly int _stagingW, _stagingH;
    private nint _adapter, _output1, _device, _context, _dup, _staging, _frame;
    private RECT _desktop;
    private byte* _mapped;
    private int _pitch;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc
    {
        public fixed char DeviceName[32];
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public byte* Data; public uint RowPitch; public uint DepthPitch; }

    /// <param name="screenX">A screen point (physical px) on the monitor to duplicate (the window's client origin).</param>
    internal DesktopDuplication(int screenX, int screenY, int stagingW, int stagingH)
    {
        _stagingW = stagingW;
        _stagingH = stagingH;
        Guid iid = IID_IDXGIFactory1;
        Check(CreateDXGIFactory1(&iid, out nint factory), "CreateDXGIFactory1");
        try
        {
            for (uint a = 0; _output1 == 0; a++)
            {
                nint adapter;
                if (Call<uint, nint>(factory, 12, a, &adapter) != 0) break;   // IDXGIFactory1::EnumAdapters1
                for (uint o = 0; ; o++)
                {
                    nint output;
                    if (Call<uint, nint>(adapter, 7, o, &output) != 0) break;   // IDXGIAdapter::EnumOutputs
                    OutputDesc desc;
                    ((delegate* unmanaged[Stdcall]<nint, OutputDesc*, int>)Vt(output)[7])(output, &desc);   // GetDesc
                    RECT r = desc.DesktopCoordinates;
                    if (screenX >= r.Left && screenX < r.Right && screenY >= r.Top && screenY < r.Bottom)
                    {
                        Guid o1 = IID_IDXGIOutput1;
                        Check(QueryInterface(output, &o1, out _output1), "IDXGIOutput1");
                        _desktop = r;
                        _adapter = adapter;
                        Release(output);
                        break;
                    }
                    Release(output);
                }
                if (_adapter == 0) Release(adapter);
            }
        }
        finally { Release(factory); }
        if (_output1 == 0) throw new InvalidOperationException("No DXGI output contains the benchmark window.");

        nint device, context;
        int level;
        Check(D3D11CreateDevice(_adapter, 0, 0, 0, null, 0, 7, &device, &level, &context), "D3D11CreateDevice");
        _device = device;
        _context = context;
        CreateDuplication();

        var td = new Texture2DDesc
        {
            Width = (uint)stagingW, Height = (uint)stagingH, MipLevels = 1, ArraySize = 1,
            Format = 87 /* B8G8R8A8_UNORM */, SampleCount = 1, Usage = 3 /* STAGING */, CpuAccessFlags = 0x20000 /* READ */,
        };
        nint staging;
        Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)Vt(_device)[5])(_device, &td, null, &staging),
            "CreateTexture2D(staging)");
        _staging = staging;
    }

    private void CreateDuplication()
    {
        nint dup;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(_output1)[22])(_output1, _device, &dup), "DuplicateOutput");
        _dup = dup;
    }

    /// <summary>Wait for the next composed frame. False on timeout (nothing on the desktop changed), or when the
    /// duplication was lost and re-created; a pointer-only update is skipped (it is not a composed frame).</summary>
    internal bool Acquire(int timeoutMs, out long presentQpc, out uint accumulated)
    {
        presentQpc = 0;
        accumulated = 0;
        FrameInfo info;
        nint resource;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)Vt(_dup)[8])(_dup, (uint)timeoutMs, &info, &resource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return false;
        if (hr == DXGI_ERROR_ACCESS_LOST)
        {
            Release(_dup);
            _dup = 0;
            CreateDuplication();
            return false;
        }
        Check(hr, "AcquireNextFrame");
        if (info.LastPresentTime == 0)
        {
            Release(resource);
            ((delegate* unmanaged[Stdcall]<nint, int>)Vt(_dup)[14])(_dup);   // ReleaseFrame
            return false;
        }
        Guid iid = IID_ID3D11Texture2D;
        Check(QueryInterface(resource, &iid, out _frame), "ID3D11Texture2D");
        Release(resource);
        presentQpc = info.LastPresentTime;
        accumulated = info.AccumulatedFrames;
        return true;
    }

    /// <summary>Copy a screen rectangle (physical px) of the acquired frame to (dstX, dstY) of the staging texture.</summary>
    internal void Copy(int screenX, int screenY, int w, int h, int dstX, int dstY)
    {
        int x0 = Math.Max(screenX, _desktop.Left), y0 = Math.Max(screenY, _desktop.Top);
        int x1 = Math.Min(screenX + w, _desktop.Right), y1 = Math.Min(screenY + h, _desktop.Bottom);
        if (x1 <= x0 || y1 <= y0) return;
        dstX += x0 - screenX;
        dstY += y0 - screenY;
        if (dstX + (x1 - x0) > _stagingW) x1 = x0 + _stagingW - dstX;
        if (dstY + (y1 - y0) > _stagingH) y1 = y0 + _stagingH - dstY;
        var box = new Box
        {
            Left = (uint)(x0 - _desktop.Left), Top = (uint)(y0 - _desktop.Top), Front = 0,
            Right = (uint)(x1 - _desktop.Left), Bottom = (uint)(y1 - _desktop.Top), Back = 1,
        };
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Vt(_context)[46])(
            _context, _staging, 0, (uint)dstX, (uint)dstY, 0, _frame, 0, &box);   // CopySubresourceRegion
    }

    /// <summary>Release the frame back to DWM and map the staging copies for reading.</summary>
    internal void EndFrame()
    {
        Release(_frame);
        _frame = 0;
        ((delegate* unmanaged[Stdcall]<nint, int>)Vt(_dup)[14])(_dup);   // ReleaseFrame
        Mapped m;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, Mapped*, int>)Vt(_context)[14])(
            _context, _staging, 0, 1 /* READ */, 0, &m), "Map");
        _mapped = m.Data;
        _pitch = (int)m.RowPitch;
    }

    internal void Unmap()
    {
        if (_mapped == null) return;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Vt(_context)[15])(_context, _staging, 0);
        _mapped = null;
    }

    internal (byte R, byte G, byte B) Pixel(int x, int y)
    {
        x = Math.Clamp(x, 0, _stagingW - 1);
        y = Math.Clamp(y, 0, _stagingH - 1);
        byte* p = _mapped + (long)y * _pitch + x * 4;
        return (p[2], p[1], p[0]);
    }

    internal double[] Average(int x, int y, int w, int h)
    {
        double r = 0, g = 0, b = 0;
        for (int yy = y; yy < y + h; yy++)
        {
            byte* row = _mapped + (long)yy * _pitch + x * 4;
            for (int xx = 0; xx < w; xx++) { b += row[xx * 4]; g += row[xx * 4 + 1]; r += row[xx * 4 + 2]; }
        }
        double n = Math.Max(1, w * h);
        return [r / n, g / n, b / n];
    }

    public void Dispose()
    {
        Unmap();
        Release(_frame);
        Release(_staging);
        Release(_dup);
        Release(_context);
        Release(_device);
        Release(_output1);
        Release(_adapter);
        _frame = _staging = _dup = _context = _device = _output1 = _adapter = 0;
    }

    private static void** Vt(nint p) => *(void***)p;

    private static int Call<TA, TB>(nint self, int slot, TA a, TB* b) where TA : unmanaged where TB : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, TA, TB*, int>)Vt(self)[slot])(self, a, b);

    private static int QueryInterface(nint self, Guid* iid, out nint result)
    {
        nint r;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vt(self)[0])(self, iid, &r);
        result = r;
        return hr;
    }

    private static void Release(nint p)
    {
        if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vt(p)[2])(p);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{unchecked((uint)hr):X8}");
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(Guid* riid, out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* featureLevels,
        uint levels, uint sdkVersion, nint* device, int* featureLevel, nint* context);
}
