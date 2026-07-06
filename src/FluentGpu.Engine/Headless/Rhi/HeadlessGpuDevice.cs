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
    private readonly List<DrawGlyphRunGradientCmd> _glyphGradients = new(16);
    private readonly List<ClipCmd> _clips = new(16);
    private readonly List<DrawImageCmd> _imageDraws = new(32);
    private readonly List<DrawRoundRectStrokeCmd> _strokes = new(16);
    private readonly List<DrawShadowCmd> _shadows = new(16);
    private readonly List<DrawArcCmd> _arcs = new(16);
    private readonly List<DrawPolylineStrokeCmd> _polylines = new(16);
    private readonly List<DrawGradientRectCmd> _gradients = new(16);
    private readonly List<DrawGradientStrokeCmd> _gradientStrokes = new(16);
    private readonly List<PushLayerCmd> _layers = new(8);
    private readonly List<DrawTabShapeCmd> _tabShapes = new(8);
    private readonly List<(int id, int w, int h)> _uploads = new(32);
    private readonly Dictionary<int, (int w, int h)> _resident = new(32);
    private readonly List<int> _evictions = new(16);

    public string BackendName => "Headless";
    public bool SupportsSecondarySwapchains => true;
    public int FrameCount { get; private set; }
    public ColorF LastClear { get; private set; }
    public IReadOnlyList<FillRoundRectCmd> LastRects => _rects;
    public IReadOnlyList<DrawGlyphRunCmd> LastGlyphs => _glyphs;
    /// <summary>Karaoke-wipe glyph runs drawn this frame (the soft-wipe op, A1).</summary>
    public IReadOnlyList<DrawGlyphRunGradientCmd> LastGlyphGradients => _glyphGradients;
    /// <summary>Every PushClip pushed this frame (for clip assertions; the recorder pre-intersects each one).</summary>
    public IReadOnlyList<ClipCmd> LastClips => _clips;
    /// <summary>Image quads drawn this frame (Ready==0 ⇒ placeholder shown while decode is in flight).</summary>
    public IReadOnlyList<DrawImageCmd> LastImages => _imageDraws;
    /// <summary>SDF outlines (focus rings / stroked borders) drawn this frame.</summary>
    public IReadOnlyList<DrawRoundRectStrokeCmd> LastStrokes => _strokes;
    /// <summary>The clip-stack depth at the moment each stroke was decoded (parallel to <see cref="LastStrokes"/>) —
    /// asserts a focus ring records OUTSIDE its ClipsToBounds node's own clip (depth of the parent context).</summary>
    public IReadOnlyList<int> LastStrokeClipDepths => _strokeClipDepth;
    private readonly List<int> _strokeClipDepth = new(16);
    /// <summary>Soft drop shadows drawn this frame.</summary>
    public IReadOnlyList<DrawShadowCmd> LastShadows => _shadows;
    /// <summary>Circular-arc strokes (ProgressRing) drawn this frame.</summary>
    public IReadOnlyList<DrawArcCmd> LastArcs => _arcs;
    /// <summary>Stroked polylines drawn this frame.</summary>
    public IReadOnlyList<DrawPolylineStrokeCmd> LastPolylines => _polylines;
    /// <summary>Gradient-filled rects drawn this frame.</summary>
    public IReadOnlyList<DrawGradientRectCmd> LastGradients => _gradients;
    /// <summary>Gradient-tinted border strokes (WinUI elevation borders) drawn this frame.</summary>
    public IReadOnlyList<DrawGradientStrokeCmd> LastGradientStrokes => _gradientStrokes;
    /// <summary>Layers pushed this frame — acrylic (Kind 0, blur/tint recipe fields), flat opacity groups (Kind 1,
    /// GroupAlpha), AND per-node self-blur groups (Kind 2, GroupAlpha + <see cref="PushLayerCmd.BlurSigma"/> — the
    /// Expressive Motion Kit) all ride the same opcode; assert on <see cref="PushLayerCmd.Kind"/>/<c>GroupAlpha</c>/
    /// <c>BlurSigma</c>.</summary>
    public IReadOnlyList<PushLayerCmd> LastLayers => _layers;
    /// <summary>WinUI selected-tab shapes drawn this frame (DrawTabShape — rounded-top + inverted bottom flares).</summary>
    public IReadOnlyList<DrawTabShapeCmd> LastTabShapes => _tabShapes;
    /// <summary>Push/pop balance check — must be 0 at end of a well-formed frame. Rounded (tier-2) clips are visible
    /// on <see cref="LastClips"/> entries via <see cref="ClipCmd.CornerRadius"/>/<c>RoundedRect</c>.</summary>
    public int ClipBalance { get; private set; }
    /// <summary>PushLayer/PopLayer balance check — must be 0 at end of a well-formed frame.</summary>
    public int LayerBalance { get; private set; }

    /// <summary>Every <see cref="UploadImage"/> this run (one entry per decode completion) — for upload assertions.</summary>
    public IReadOnlyList<(int id, int w, int h)> Uploads => _uploads;
    /// <summary>Currently-resident image ids → their uploaded dims (last upload wins; for residency assertions).</summary>
    public IReadOnlyDictionary<int, (int w, int h)> ResidentImages => _resident;

    public ISwapchain CreateSwapchain(in SwapchainDesc desc) => new HeadlessSwapchain(desc.SizePx);

    public void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h)
    {
        _uploads.Add((imageId, w, h));   // never cleared: uploads are one-shot per decode, so the log is the history
        _resident[imageId] = (w, h);
    }

    /// <summary>Image ids the residency manager evicted (GPU texture freed) — for eviction assertions.</summary>
    public IReadOnlyList<int> Evictions => _evictions;
    public void EvictImage(int imageId) { _resident.Remove(imageId); _evictions.Add(imageId); }

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)
    {
        _rects.Clear();   // retains capacity → no alloc after warmup
        _glyphs.Clear();
        _glyphGradients.Clear();
        _clips.Clear();
        _imageDraws.Clear();
        _strokes.Clear();
        _strokeClipDepth.Clear();
        _shadows.Clear();
        _arcs.Clear();
        _polylines.Clear();
        _gradients.Clear();
        _gradientStrokes.Clear();
        _layers.Clear();
        _tabShapes.Clear();
        LastClear = ctx.Clear;
        FrameCount++;
        int balance = 0;
        int layerBalance = 0;

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
                case DrawOp.DrawGlyphRunGradient:
                    _glyphGradients.Add(MemoryMarshal.Read<DrawGlyphRunGradientCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawGlyphRunGradientCmd>();
                    break;
                case DrawOp.PushClip:
                    _clips.Add(MemoryMarshal.Read<ClipCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<ClipCmd>();
                    balance++;
                    break;
                case DrawOp.PopClip:
                    balance--;
                    break;
                case DrawOp.DrawImage:
                    _imageDraws.Add(MemoryMarshal.Read<DrawImageCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawImageCmd>();
                    break;
                case DrawOp.DrawRoundRectStroke:
                    _strokes.Add(MemoryMarshal.Read<DrawRoundRectStrokeCmd>(drawList.Slice(pos)));
                    _strokeClipDepth.Add(balance);
                    pos += Unsafe.SizeOf<DrawRoundRectStrokeCmd>();
                    break;
                case DrawOp.DrawShadow:
                    _shadows.Add(MemoryMarshal.Read<DrawShadowCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawShadowCmd>();
                    break;
                case DrawOp.DrawArc:
                    _arcs.Add(MemoryMarshal.Read<DrawArcCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawArcCmd>();
                    break;
                case DrawOp.DrawPolylineStroke:
                    _polylines.Add(MemoryMarshal.Read<DrawPolylineStrokeCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawPolylineStrokeCmd>();
                    break;
                case DrawOp.DrawGradientRect:
                    _gradients.Add(MemoryMarshal.Read<DrawGradientRectCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawGradientRectCmd>();
                    break;
                case DrawOp.DrawGradientStroke:
                    _gradientStrokes.Add(MemoryMarshal.Read<DrawGradientStrokeCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawGradientStrokeCmd>();
                    break;
                case DrawOp.PushLayer:
                    _layers.Add(MemoryMarshal.Read<PushLayerCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<PushLayerCmd>();
                    layerBalance++;
                    break;
                case DrawOp.PopLayer:
                    pos += Unsafe.SizeOf<PopLayerCmd>();
                    layerBalance--;
                    break;
                case DrawOp.DrawTabShape:
                    _tabShapes.Add(MemoryMarshal.Read<DrawTabShapeCmd>(drawList.Slice(pos)));
                    pos += Unsafe.SizeOf<DrawTabShapeCmd>();
                    break;
                default:
                    return; // unknown opcode — stop (corrupt stream guard)
            }
        }
        ClipBalance = balance;
        LayerBalance = layerBalance;
    }

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx, ISwapchain target)
        => SubmitDrawList(drawList, sortKeys, in ctx);

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

    // Windowed desktop-acrylic popup chrome (the real D3D12 backend drives Windows.UI.Composition; headless captures the
    // parameters so the cross-seam wiring — content rect, open direction, closedRatio, corner — is verifiable).
    public PopupChromeMetrics? LastPopupChrome { get; private set; }
    public bool PopupOpenPlayed { get; private set; }
    public void ConfigurePopupChrome(in PopupChromeMetrics m) => LastPopupChrome = m;
    public void AnimatePopupOpen() => PopupOpenPlayed = true;
}
