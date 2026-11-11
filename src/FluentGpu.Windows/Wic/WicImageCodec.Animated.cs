using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Media.Codecs.Wic;

/// <summary>
/// The animated half of the WIC leaf (<see cref="IAnimatedImageCodec"/>): an animated WebP played through Microsoft's WebP
/// decoder (the WebP Image Extension). That decoder hands every frame back as the FULL composited canvas (the ANMF sub-rect
/// blended over the frame before it), with its display time in <c>/ANMF/FrameDuration</c> and the loop count in
/// <c>/ANIM/LoopCount</c>, so playing it is "decode frame i, show it". It caches the canvas between calls: frame i after
/// i-1 costs one frame (1440x810, a full-motion test: p50 10 ms, p90 14 ms, max 23 ms on a Snapdragon X Elite), a cold jump
/// to the last of 180 frames costs all of them (4 s), hence the reader's in-order contract.
/// <para><b>GIF is left out on purpose.</b> WIC's GIF decoder returns each frame as its own sub-rect with a disposal method
/// (keep, restore to background, restore to previous) the caller must composite; a reader that skipped that would play a
/// GIF wrong, so a GIF keeps the static path (its first frame), exactly as before <c>ImageEl.Animate</c> existed.</para>
/// <para>Where the WebP Image Extension is missing WIC cannot open a WebP at all: <see cref="OpenAnimated"/> returns null and
/// the static decode fails as it always has, so the app's poster under the image is what shows.</para>
/// </summary>
public sealed unsafe partial class WicImageCodec
{
    // GUID_ContainerFormatWebp {E094B0E2-67F2-45B3-B0EA-115337CA7CF3}
    private static readonly Guid GUID_ContainerFormatWebp = new(0xE094B0E2, 0x67F2, 0x45B3, 0xB0, 0xEA, 0x11, 0x53, 0x37, 0xCA, 0x7C, 0xF3);

    public IAnimatedImageReader? OpenAnimated(byte* encoded, int length)
    {
        if (encoded == null || length <= 0) return null;
        IWICImagingFactory* factory = Factory();
        if (factory == null) return null;
        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        bool keep = false;
        try
        {
            if (factory->CreateStream(&stream).FAILED) return null;
            if (stream->InitializeFromMemory(encoded, (uint)length).FAILED) return null;
            if (factory->CreateDecoderFromStream((IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).FAILED) return null;
            Guid container;
            if (decoder->GetContainerFormat(&container).FAILED || container != GUID_ContainerFormatWebp) return null;
            uint frames = 0;
            if (decoder->GetFrameCount(&frames).FAILED || frames < 2) return null;
            IWICBitmapFrameDecode* first = null;
            uint w = 0, h = 0;
            if (decoder->GetFrame(0, &first).FAILED) return null;
            HRESULT size = first->GetSize(&w, &h);
            first->Release();
            if (size.FAILED || w == 0 || h == 0) return null;
            int loops = ReadLoopCount(decoder);
            keep = true;
            return new WicAnimatedReader(factory, stream, decoder, (int)w, (int)h, (int)frames, loops);
        }
        finally
        {
            if (!keep)
            {
                if (decoder != null) decoder->Release();
                if (stream != null) stream->Release();
            }
        }
    }

    private static int ReadLoopCount(IWICBitmapDecoder* decoder)
    {
        IWICMetadataQueryReader* meta = null;
        if (decoder->GetMetadataQueryReader(&meta).FAILED || meta == null) return 0;
        PROPVARIANT pv = default;
        try
        {
            fixed (char* name = "/ANIM/LoopCount")
                if (meta->GetMetadataByName(name, &pv).FAILED) return 0;
            return pv.vt switch
            {
                (ushort)VARENUM.VT_UI2 => pv.uiVal,
                (ushort)VARENUM.VT_UI4 => (int)Math.Min(pv.ulVal, int.MaxValue),
                _ => 0,
            };
        }
        finally
        {
            PropVariantClear(&pv);
            meta->Release();
        }
    }

    /// <summary>One open animation: the stream over the caller's bytes and the decoder that caches the canvas between frames.
    /// Created and used on the animation's worker thread only.</summary>
    private sealed class WicAnimatedReader : IAnimatedImageReader
    {
        private readonly IWICImagingFactory* _factory;   // the codec's shared factory (agile); not owned
        private IWICStream* _stream;
        private IWICBitmapDecoder* _decoder;

        public WicAnimatedReader(IWICImagingFactory* factory, IWICStream* stream, IWICBitmapDecoder* decoder, int w, int h, int frames, int loops)
        {
            _factory = factory; _stream = stream; _decoder = decoder;
            Width = w; Height = h; FrameCount = frames; LoopCount = loops;
        }

        public int Width { get; }
        public int Height { get; }
        public int FrameCount { get; }
        public int LoopCount { get; }

        public bool TryDecode(int index, Span<byte> dst, out int durationMs)
        {
            durationMs = 100;
            if (_decoder == null || (uint)index >= (uint)FrameCount) return false;
            long need = (long)Width * Height * 4;
            if (dst.Length < need) return false;
            using var region = FluentGpu.Foundation.CrashTrail.Enter(FluentGpu.Foundation.NativeRegion.WicDecode, default, index);
            IWICBitmapFrameDecode* frame = null;
            IWICFormatConverter* conv = null;
            try
            {
                if (_decoder->GetFrame((uint)index, &frame).FAILED) return false;
                uint w = 0, h = 0;
                if (frame->GetSize(&w, &h).FAILED || w != (uint)Width || h != (uint)Height) return false;
                durationMs = ReadDuration(frame);
                if (_factory->CreateFormatConverter(&conv).FAILED) return false;
                Guid pf = GUID_WICPixelFormat32bppPBGRA;
                if (conv->Initialize((IWICBitmapSource*)frame, &pf, WICBitmapDitherType.WICBitmapDitherTypeNone,
                        null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom).FAILED) return false;
                fixed (byte* d = dst)
                    if (conv->CopyPixels(null, (uint)(Width * 4), (uint)need, d).FAILED) return false;
                return true;
            }
            finally
            {
                if (conv != null) conv->Release();
                if (frame != null) frame->Release();
            }
        }

        private static int ReadDuration(IWICBitmapFrameDecode* frame)
        {
            IWICMetadataQueryReader* meta = null;
            if (frame->GetMetadataQueryReader(&meta).FAILED || meta == null) return 100;
            PROPVARIANT pv = default;
            try
            {
                fixed (char* name = "/ANMF/FrameDuration")
                    if (meta->GetMetadataByName(name, &pv).FAILED) return 100;
                long ms = pv.vt switch
                {
                    (ushort)VARENUM.VT_UI4 => pv.ulVal,
                    (ushort)VARENUM.VT_UI2 => pv.uiVal,
                    _ => 0,
                };
                return ms <= 0 ? 100 : (int)Math.Min(ms, 60_000);
            }
            finally
            {
                PropVariantClear(&pv);
                meta->Release();
            }
        }

        public void Dispose()
        {
            if (_decoder != null) { _decoder->Release(); _decoder = null; }
            if (_stream != null) { _stream->Release(); _stream = null; }
        }
    }
}
