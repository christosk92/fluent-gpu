namespace FluentGpu.Rhi;

/// <summary>
/// Pure decision extracted from <c>D3D12Device</c>'s per-target stencil-clip DSV (CANDIDATE FIX, INCIDENT 2026-09:
/// production crash from an oversized DSV bound to an undersized detached-child target — see
/// <c>D3D12Device.EnsureStencilDsv</c>/<c>D3D12Swapchain.StencilDsv*</c> for the real GPU-backed implementation).
/// Extracted because the decision itself — "does this target's cached DSV still fit its target, exactly?" — needs
/// no GPU/COM/TerraFX to test, and the actual <c>D3D12Device</c> cannot run headlessly (VerticalSlice's transitive
/// closure stays TerraFX-free; see CLAUDE.md "no source-text tests" / "extract the decision into an engine-free pure
/// class" pattern — <c>LiveEdgeState</c>/<c>LiveRail</c>/<c>TimeFormat</c>/<c>PlayableLinks</c> are the precedent).
/// </summary>
public static class StencilDsvPolicy
{
    /// <summary>True when an existing DSV of size <paramref name="currentW"/>×<paramref name="currentH"/> may be
    /// reused for a target requesting <paramref name="requestedW"/>×<paramref name="requestedH"/>: it COVERS the
    /// request (at least as large on both axes, and not degenerate). The DSV is stored PER TARGET
    /// (<c>D3D12Swapchain.StencilDsv*</c>), which is what closed the incident — one window's DSV can no longer be
    /// bound with another window's target — so an oversized DSV on the SAME target is a valid reuse. It has to be:
    /// <c>EnsureStencilDsv</c> sizes the DSV to <c>max(active render target, swapchain)</c>, and an offscreen surface
    /// (an acrylic canvas, a blur RT with its halo) is routinely larger than the swapchain. The first cut of this
    /// policy demanded an EXACT match, which made that DSV "stale" on every submit — a <c>WaitForGpu()</c> + release +
    /// recreate per frame, one vsync of stall each (2026-09-22: 85 % of UI frames over budget from launch).</summary>
    public static bool Covers(int currentW, int currentH, int requestedW, int requestedH)
        => currentW > 0 && currentH > 0 && currentW >= requestedW && currentH >= requestedH;

    /// <summary>True when the cached DSV is too SMALL for the target (the target grew) — the only case that needs a
    /// fresh resource. The negation of <see cref="Covers"/>, spelled out so a call site reads as a decision.</summary>
    public static bool NeedsRecreate(int currentW, int currentH, int requestedW, int requestedH)
        => !Covers(currentW, currentH, requestedW, requestedH);
}
