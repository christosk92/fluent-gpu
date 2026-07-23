namespace FluentGpu.Hosting;

/// <summary>How <see cref="AppHost"/> drives submit/present relative to the UI thread — the render-thread seam's mode
/// selector (design/subsystems/threading-render-seam.md). The integer values are load-bearing: they are recorded as the
/// device-lost snapshot's <c>RenderMode</c> field (0/1/2 = single/force-sync/async), so keep the ordering.
///
/// <list type="bullet">
/// <item><see cref="SingleThread"/> (0) — no render thread; the UI records, publishes, acquires, and submits inline on
/// itself (byte-identical to a direct submit). This is the deterministic path the headless VerticalSlice gates and every
/// Headless-window host run on, always. Never selected for a real windowed host by default.</item>
/// <item><see cref="ForceSync"/> (1) — a dedicated fgpu-render thread submits/presents, but the UI BLOCKS on it each frame
/// (<c>DrainSync</c>). No async overlap. Internal/test-only: reachable ONLY via the <c>AppHost</c> constructor override so
/// the seam tests and probes can exercise the threaded submit path without the async timeline. Nothing selects it by default.</item>
/// <item><see cref="Async"/> (2) — the render thread presents on its own timeline; the UI <c>WakeAsync</c>s and PROCEEDS
/// (the GPU fence-wait no longer bounds back to the UI thread). This is the DEFAULT for real windowed hosts.</item>
/// </list></summary>
internal enum RenderLoopMode
{
    SingleThread = 0,
    ForceSync = 1,
    Async = 2,
}
