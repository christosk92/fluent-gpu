using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;

namespace FluentGpu.Rhi.Headless;

/// <summary>
/// CPU/null RHI backend (the structural test path from validation.md). Decodes the POD DrawList into reusable
/// command lists for assertions — no GPU, and no per-frame managed allocation once warmed (lists keep capacity).
/// </summary>
public sealed class HeadlessGpuDevice : IGpuDevice
{
    private readonly List<FillRoundRectCmd> _rects = new(64);
    private readonly List<DrawGlyphRunCmd> _glyphs = new(64);

    public string BackendName => "Headless";
    public int FrameCount { get; private set; }
    public ColorF LastClear { get; private set; }
    public IReadOnlyList<FillRoundRectCmd> LastRects => _rects;
    public IReadOnlyList<DrawGlyphRunCmd> LastGlyphs => _glyphs;

    public ISwapchain CreateSwapchain(in SwapchainDesc desc) => new HeadlessSwapchain(desc.SizePx);

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)
    {
        _rects.Clear();   // retains capacity → no alloc after warmup
        _glyphs.Clear();
        LastClear = ctx.Clear;
        FrameCount++;

        int pos = 0;
        while (pos + sizeof(int) <= drawList.Length)
        {
            int op = MemoryMarshal.Read<int>(drawList.Slice(pos));
            pos += sizeof(int);
            switch ((DrawOp)op)
            {
                case DrawOp.FillRoundRect:
                    _rects.Add(MemoryMarshal.Read<FillRoundRectCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<FillRoundRectCmd>();
                    break;
                case DrawOp.DrawGlyphRun:
                    _glyphs.Add(MemoryMarshal.Read<DrawGlyphRunCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawGlyphRunCmd>();
                    break;
                default:
                    return; // unknown opcode — stop (corrupt stream guard)
            }
        }
    }

    public void Dispose() { }
}

public sealed class HeadlessSwapchain : ISwapchain
{
    public HeadlessSwapchain(Size2 size) => SizePx = size;
    public Size2 SizePx { get; private set; }
    public int PresentCount { get; private set; }
    public void Resize(Size2 px) => SizePx = px;
    public void Present() => PresentCount++;
    public void Dispose() { }
}
