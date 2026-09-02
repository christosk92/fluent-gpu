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
    /// <summary>Actual successful-present cadence (<see cref="FluentGpu.Hosting.AppHost.PresentFps"/>) — distinct
    /// from <see cref="FrameFps"/>, which counts paint turns including skip-submit frames never shown on screen.</summary>
    FramePresentFps,
}
