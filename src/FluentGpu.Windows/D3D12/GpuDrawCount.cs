namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Always-on count of GPU draw calls recorded by the submit in progress: every <c>DrawInstanced</c> /
/// <c>DrawIndexedInstanced</c> any pipeline or compositor in this backend records increments <see cref="Frame"/> on the
/// same line. <see cref="D3D12Device"/> zeroes it at the top of each submit and publishes it in
/// <c>GpuFrameCounters.Draws</c>. A plain field, not <c>Diag.*</c> (compiled out of Release): command recording runs on
/// exactly one thread (the submit/present owner), so no interlocked op is needed.
/// </summary>
internal static class GpuDrawCount
{
    internal static int Frame;
}
