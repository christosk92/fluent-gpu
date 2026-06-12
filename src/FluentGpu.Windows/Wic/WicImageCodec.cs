using System.Runtime.InteropServices;
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
public sealed unsafe class WicImageCodec : IImageCodec
{
    // CLSID_WICImagingFactory {CACAF262-9370-4615-A13B-9F5539DA4C0A}
    private static readonly Guid CLSID_WICImagingFactory = new(0xCACAF262, 0x9370, 0x4615, 0xA1, 0x3B, 0x9F, 0x55, 0x39, 0xDA, 0x4C, 0x0A);
    // GUID_WICPixelFormat32bppPBGRA {6FDDC324-4E03-4BFE-B185-3D77768DC910}
    private static readonly Guid GUID_WICPixelFormat32bppPBGRA = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x10);

    [ThreadStatic] private static nint _tlsFactory;       // IWICImagingFactory* per worker thread
    [ThreadStatic] private static bool _tlsComInit;

    private static IWICImagingFactory* Factory()
    {
        if (_tlsFactory != 0) return (IWICImagingFactory*)_tlsFactory;
        if (!_tlsComInit) { CoInitializeEx(null, (uint)(COINIT.COINIT_MULTITHREADED | COINIT.COINIT_DISABLE_OLE1DDE)); _tlsComInit = true; }
        Guid clsid = CLSID_WICImagingFactory;
        Guid iid = __uuidof<IWICImagingFactory>();
        IWICImagingFactory* f = null;
        HRESULT hr = CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&f);
        if (hr.FAILED) return null;
        _tlsFactory = (nint)f;
        return f;
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

            if (factory->CreateBitmapScaler(&scaler).FAILED) return false;
            if (scaler->Initialize((IWICBitmapSource*)frame, (uint)targetW, (uint)targetH,
                    WICBitmapInterpolationMode.WICBitmapInterpolationModeFant).FAILED) return false;

            if (factory->CreateFormatConverter(&conv).FAILED) return false;
            Guid pf = GUID_WICPixelFormat32bppPBGRA;
            if (conv->Initialize((IWICBitmapSource*)scaler, &pf, WICBitmapDitherType.WICBitmapDitherTypeNone,
                    null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom).FAILED) return false;

            uint stride = (uint)(targetW * 4);
            uint bytes = (uint)size;
            fixed (byte* d = dstBgra8)
                if (conv->CopyPixels(null, stride, bytes, d).FAILED) return false;

            w = targetW; h = targetH;
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
