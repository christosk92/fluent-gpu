namespace FluentGpu.Text.DirectWrite;

/// <summary>
/// SCAFFOLD (the reference Windows text backend — see design/subsystems/text.md). Will implement the text seam over
/// DirectWrite: itemize (BiDi/script/line-break) → font-fallback → shape, glyph rasterization into the GPU atlas,
/// and the callee-side <c>IDWriteTextAnalysisSource/Sink</c> CCWs. Headless font stub backs the slice today.
/// </summary>
public static class DirectWriteBackend;
