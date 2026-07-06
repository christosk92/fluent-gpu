using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace FluentGpu.Hosting.Threading;

/// <summary>Deterministic single-writer thread-confinement guard — the render-thread seam's SAFE-by-construction backstop
/// (design/subsystems/threading-render-seam.md §1.2). Each thread that participates in the frame is BOUND ONCE to a role;
/// thread-confined SceneStore/RHI accessors open with <see cref="AssertUi"/> / <see cref="AssertRender"/>, so a call
/// wired onto the wrong thread throws deterministically (never a best-effort log) and cannot reach a green CI run.
///
/// The whole guard — asserts, binds, and the <c>[ThreadStatic]</c> role — is <c>[Conditional("FGGUARD")]</c>: FGGUARD is
/// defined in Debug / test / soak builds (see <c>src/Directory.Build.props</c>) and UNDEFINED in Release/Ship (AOT), so
/// the guard vanishes entirely from the shipping binary ("production safety == CI coverage").
///
/// Single-thread v1 (current): only the UI thread is bound (<see cref="FluentGpu.Hosting.AppHost.RunFrame"/>); the render
/// thread binds itself to <see cref="ThreadRole.Render"/> when the seam spawns it (a later landing step, gated on the
/// <c>seam.race</c> soak). Until then every AssertRender is unreached and every mutation is correctly on the UI thread.</summary>
public static class ThreadGuard
{
    public enum ThreadRole : byte { Unbound = 0, Ui = 1, Render = 2, Worker = 3 }

    // Set ONCE at thread role-binding; a genuine reassignment across roles is a bug (caught below). ThreadStatic ⇒ each
    // thread has its own slot; the field + all reads are erased from Release with the [Conditional] methods that touch it.
    [ThreadStatic] private static ThreadRole t_role;

    /// <summary>Bind the CURRENT thread to <paramref name="role"/>. Idempotent for the SAME role (the frame pump may call
    /// it every frame); a role REASSIGNMENT (Ui↔Render) is a confinement bug and throws. Erased from Release.</summary>
    [Conditional("FGGUARD")]
    public static void BindCurrent(ThreadRole role)
    {
        if (t_role != ThreadRole.Unbound && t_role != role) throw new ThreadConfinementViolation(role, t_role);
        t_role = role;
    }

    [Conditional("FGGUARD")] public static void AssertUi()     { if (t_role != ThreadRole.Ui)     ThrowWrongThread(ThreadRole.Ui); }
    [Conditional("FGGUARD")] public static void AssertRender() { if (t_role != ThreadRole.Render) ThrowWrongThread(ThreadRole.Render); }
    [Conditional("FGGUARD")] public static void AssertWorkerOrRender() { if (t_role is not (ThreadRole.Worker or ThreadRole.Render)) ThrowWrongThread(ThreadRole.Worker); }

    [DoesNotReturn] private static void ThrowWrongThread(ThreadRole expected) => throw new ThreadConfinementViolation(expected, t_role);
}

/// <summary>Thrown when a thread-confined accessor runs on the wrong thread. Deterministic; never swallowed.</summary>
public sealed class ThreadConfinementViolation(ThreadGuard.ThreadRole expected, ThreadGuard.ThreadRole actual)
    : System.InvalidOperationException($"Thread confinement violation: expected {expected} thread, got {actual}.");
