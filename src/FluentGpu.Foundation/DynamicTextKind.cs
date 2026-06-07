namespace FluentGpu.Foundation;

/// <summary>Retained text slots whose contents are refreshed by the host without re-rendering or relayout.</summary>
public enum DynamicTextKind : byte
{
    None = 0,
    FrameFps,
    FrameCommandCount,
    FrameDrawCount,
    FrameCullCount,
    FrameMs,
}
