using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace FluentGpu.Hosting.Threading;

/// <summary>Deterministic single-writer thread-confinement guard — the render-thread seam's SAFE-by-construction backstop
/// (design/subsystems/threading-render-seam.md §1.2). Each thread that participates in the frame is BOUND ONCE to a role;
/// thread-confined SceneStore/RHI accessors open with <see cref="AssertUi"/> / <see cref="AssertRender"/>, so a call
/// wired onto the wrong thread throws deterministically (never a best-effort log) and cannot reach a green CI run.
///
/// The ASSERTS are <c>[Conditional("FGGUARD")]</c>: FGGUARD is defined in Debug / test / soak builds (see
/// <c>src/Directory.Build.props</c>) and UNDEFINED in Release/Ship (AOT), so no assert reaches the shipping binary
/// ("production safety == CI coverage"). The role BINDS and the render-ownership bookkeeping are NOT conditional:
/// <c>[Conditional]</c> is decided by the CALLING assembly, so a conditional bind compiled out of one assembly (the
/// Engine's <c>RenderThread.Loop</c>) left an armed assert in another (<c>D3D12Device.AssertSubmitThread</c>) reading
/// <see cref="ThreadRole.Unbound"/> on the real render thread and throwing (crash 2026-09-23, pid 23876). One
/// thread-static write per frame is the whole Release cost.
///
/// Single-thread v1 (current): only the UI thread is bound (<see cref="FluentGpu.Hosting.AppHost.RunFrame"/>); the render
/// thread binds itself to <see cref="ThreadRole.Render"/> when the seam spawns it (a later landing step, gated on the
/// <c>seam.race</c> soak). Until then every AssertRender is unreached and every mutation is correctly on the UI thread.</summary>
public static class ThreadGuard
{
    public enum ThreadRole : byte { Unbound = 0, Ui = 1, Render = 2, Worker = 3 }

    // Set ONCE at thread role-binding; a genuine reassignment across roles is a bug (caught below). ThreadStatic ⇒ each
    // thread has its own slot. Written unconditionally (see the class summary); only the asserts that read it are erased.
    [ThreadStatic] private static ThreadRole t_role;

    /// <summary>Bind the CURRENT thread to <paramref name="role"/>. Idempotent for the SAME role (the frame pump may call
    /// it every frame); a role REASSIGNMENT (Ui↔Render) is a confinement bug and throws. Erased from Release.</summary>
    public static void BindCurrent(ThreadRole role)
    {
#if FGGUARD
        if (t_role != ThreadRole.Unbound && t_role != role) throw new ThreadConfinementViolation(role, t_role);
#endif
        t_role = role;
    }

    /// <summary>True when the CURRENT thread is the bound UI thread (a non-throwing probe for diagnostics that must count, not fail).</summary>
    internal static bool IsUiThread => t_role == ThreadRole.Ui;

    [Conditional("FGGUARD")] public static void AssertUi()     { if (t_role != ThreadRole.Ui)     ThrowWrongThread(ThreadRole.Ui); }
    [Conditional("FGGUARD")] public static void AssertRender() { if (t_role != ThreadRole.Render) ThrowWrongThread(ThreadRole.Render); }
    [Conditional("FGGUARD")] public static void AssertWorkerOrRender() { if (t_role is not (ThreadRole.Worker or ThreadRole.Render)) ThrowWrongThread(ThreadRole.Worker); }

    // Render OWNERSHIP (not identity): device-mutating calls (swapchain create/resize/dispose, WaitForGpu, capture) are
    // legal on the render thread OR on the UI thread while it holds the render loop parked (RenderThread.Quiesce …
    // Resume) or after it joined the loop (RenderThread.Dispose). Both are "the sole toucher of every ComPtr right
    // now"; SubmitDrawList/Present keep the strict AssertRender. Depth-counted so a park after a join is legal; a
    // nested park is NOT (RenderThread.Quiesce is not re-entrant) and the depth check throws before it can deadlock.
    [ThreadStatic] private static int t_renderOwnerDepth;
    [ThreadStatic] private static bool t_renderOwnerForever;   // set by RenderThread.Dispose on the joining thread

    public static void EnterRenderOwnership()
    {
#if FGGUARD
        if (t_role != ThreadRole.Ui) ThrowWrongThread(ThreadRole.Ui);
        if (t_renderOwnerDepth != 0 && !t_renderOwnerForever)
            throw new InvalidOperationException("Render ownership is not re-entrant: a nested RenderThread.Quiesce would deadlock.");
#endif
        t_renderOwnerDepth++;
    }

    public static void ExitRenderOwnership()
    {
#if FGGUARD
        if (t_renderOwnerDepth <= 0) throw new InvalidOperationException("ExitRenderOwnership without a matching Enter.");
#endif
        if (t_renderOwnerDepth > 0) t_renderOwnerDepth--;
    }

    /// <summary>The render loop was joined by the current (UI) thread: it owns the device from now on.</summary>
    public static void AdoptRenderOwnership() { if (t_role == ThreadRole.Ui) t_renderOwnerForever = true; }

    [Conditional("FGGUARD")]
    public static void AssertRenderOwner()
    {
        if (t_role == ThreadRole.Render) return;
        if (t_role == ThreadRole.Ui && (t_renderOwnerForever || t_renderOwnerDepth > 0)) return;
        ThrowWrongThread(ThreadRole.Render);
    }

    [DoesNotReturn] private static void ThrowWrongThread(ThreadRole expected) => throw new ThreadConfinementViolation(expected, t_role);
}

/// <summary>Thrown when a thread-confined accessor runs on the wrong thread. Deterministic; never swallowed.</summary>
public sealed class ThreadConfinementViolation(ThreadGuard.ThreadRole expected, ThreadGuard.ThreadRole actual)
    : System.InvalidOperationException($"Thread confinement violation: expected {expected} thread, got {actual}.");
