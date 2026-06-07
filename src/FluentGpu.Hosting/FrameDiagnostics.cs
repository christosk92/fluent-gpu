using FluentGpu.Hooks;

namespace FluentGpu.Hosting;

public static class FrameDiagnostics
{
    public static readonly Context<FrameStats> Current = new(default);
}
