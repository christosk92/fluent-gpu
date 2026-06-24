using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Media.Codecs.Wic;

/// <summary>
/// Windows <see cref="IImageCodec"/> leaf (media-pipeline.md §3): Windows Imaging Component constrained decode. Decodes
/// straight to the target bucket size via <c>IWICBitmapScaler</c> (a 3000px cover never materializes full-res in CPU
/// memory) and converts to <c>32bppPBGRA</c> (premultiplied BGRA — matches the engine's blend posture and the GPU
/// texture format). All WIC COM objects are created and used ENTIRELY on the calling worker thread (per-thread cached
/// factory) and never cross the scheduler seam — only POD pixels do (the FGCOM per-thread-COM rule).
/// </summary>
public sealed unsafe class WicImageCodec : IImageCodec, IDisposable
{
    // CLSID_WICImagingFactory {CACAF262-9370-4615-A13B-9F5539DA4C0A}
    private static readonly Guid CLSID_WICImagingFactory = new(0xCACAF262, 0x9370, 0x4615, 0xA1, 0x3B, 0x9F, 0x55, 0x39, 0xDA, 0x4C, 0x0A);
    // GUID_WICPixelFormat32bppPBGRA {6FDDC324-4E03-4BFE-B185-3D77768DC910}
    private static readonly Guid GUID_WICPixelFormat32bppPBGRA = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x10);

    // ONE process-wide WIC factory. IWICImagingFactory is free-threaded, so a single shared instance is safe across the
    // decode workers and is released deterministically in Dispose — replacing a per-thread factory that, on the
    // thread-pool decode workers, was never released (a finalizer-only native COM handle: harmless given the workers are
    // bounded, but a real residual). CoInitializeEx stays per-thread (a thread must init COM to use it; the pool decode
    // threads are MTA, so this is effectively a refcount no-op) — it has no per-call resource cost to reclaim.
    private nint _factory;                            // IWICImagingFactory*, lazily created, shared, agile
    private readonly object _factoryGate = new();
    [ThreadStatic] private static bool _tlsComInit;

    private IWICImagingFactory* Factory()
    {
        if (!_tlsComInit) { CoInitializeEx(null, (uint)(COINIT.COINIT_MULTITHREADED | COINIT.COINIT_DISABLE_OLE1DDE)); _tlsComInit = true; }
        nint cached = Volatile.Read(ref _factory);
        if (cached != 0) return (IWICImagingFactory*)cached;
        lock (_factoryGate)
        {
            if (_factory != 0) return (IWICImagingFactory*)_factory;
            Guid clsid = CLSID_WICImagingFactory;
            Guid iid = __uuidof<IWICImagingFactory>();
            IWICImagingFactory* f = null;
            if (CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&f).FAILED) return null;
            Volatile.Write(ref _factory, (nint)f);
            return f;
        }
    }

    /// <summary>Release the shared WIC factory. Invoked by <c>DecodeScheduler.Dispose</c> AFTER its workers are joined,
    /// so no decode is in flight on the factory.</summary>
    public void Dispose()
    {
        lock (_factoryGate)
        {
            if (_factory != 0) { ((IWICImagingFactory*)_factory)->Release(); _factory = 0; }
        }
    }

    public bool DecodeConstrained(ReadOnlySpan<byte> encoded, int targetW, int targetH, Span<byte> dstBgra8, out int w, out int h)
    {
        w = 0; h = 0;
        if (encoded.IsEmpty || targetW <= 0 || targetH <= 0) return false;
        long size = (long)targetW * targetH * 4;
        if (size > dstBgra8.Length) return false;

        IWICImagingFactory* factory = Factory();
        if (factory == null) return false;

        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICBitmapScaler* scaler = null;
        IWICFormatConverter* conv = null;
        try
        {
            if (factory->CreateStream(&stream).FAILED) return false;
            fixed (byte* p = encoded)
                if (stream->InitializeFromMemory(p, (uint)encoded.Length).FAILED) return false;

            if (factory->CreateDecoderFromStream((IStream*)stream, null,
                    WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).FAILED) return false;
            if (decoder->GetFrame(0, &frame).FAILED) return false;

            // Fit WITHIN the target box preserving the source aspect ratio (CONTAIN-into-target), so the decoded texture
            // keeps the true source proportions — ImageFit.Cover/Contain then crop/letterbox against the real ratio
            // instead of a source pre-squished to the box. A square source into a square target is unchanged. The decode
            // dims are returned (≤ target); DecodeScheduler already honours a smaller-than-target result.
            uint srcW = 0, srcH = 0;
            if (frame->GetSize(&srcW, &srcH).FAILED || srcW == 0 || srcH == 0) return false;
            double scale = Math.Min((double)targetW / srcW, (double)targetH / srcH);
            int outW = Math.Clamp((int)Math.Round(srcW * scale), 1, targetW);
            int outH = Math.Clamp((int)Math.Round(srcH * scale), 1, targetH);

            if (factory->CreateBitmapScaler(&scaler).FAILED) return false;
            if (scaler->Initialize((IWICBitmapSource*)frame, (uint)outW, (uint)outH,
                    WICBitmapInterpolationMode.WICBitmapInterpolationModeFant).FAILED) return false;

            if (factory->CreateFormatConverter(&conv).FAILED) return false;
            Guid pf = GUID_WICPixelFormat32bppPBGRA;
            if (conv->Initialize((IWICBitmapSource*)scaler, &pf, WICBitmapDitherType.WICBitmapDitherTypeNone,
                    null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom).FAILED) return false;

            uint stride = (uint)(outW * 4);
            uint bytes = (uint)(outW * outH * 4);
            fixed (byte* d = dstBgra8)
                if (conv->CopyPixels(null, stride, bytes, d).FAILED) return false;

            w = outW; h = outH;
            return true;
        }
        finally
        {
            if (conv != null) conv->Release();
            if (scaler != null) scaler->Release();
            if (frame != null) frame->Release();
            if (decoder != null) decoder->Release();
            if (stream != null) stream->Release();
        }
    }
}
