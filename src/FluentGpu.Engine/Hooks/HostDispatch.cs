namespace FluentGpu.Hooks;

/// <summary>
/// Ambient access to the host's cross-thread UI dispatch (the engine-owned replacement for hand-rolled
/// post-to-UI-thread plumbing). A component reads it with <c>UseContext(HostDispatch.Post)</c> — or the
/// <c>UsePost()</c> sugar — to obtain a delegate that runs an action on the UI thread on the next frame, safe to
/// call from ANY thread (OS callback, worker, agile-COM apartment). The poster enqueues onto the host and wakes a
/// blocked loop (<see cref="FluentGpu.Pal.IPlatformWindow.Wake"/>); the host drains the queue inside a reactive
/// <c>Batch</c> at the top of the next frame, so all posted signal writes coalesce into a single re-render.
/// <para>
/// This is the blessed answer to "I have data arriving off the UI thread" — it replaces the
/// <c>UseContext(FrameClock.Tick)</c>-to-drain-a-queue anti-pattern, which forced a full component re-render on every
/// frame just to poll. With <c>Post</c> the loop stays idle until something actually arrives, then runs exactly one
/// frame to apply it.
/// </para>
/// <para>
/// The default (no host / headless tests) is <see langword="null"/>; <c>UsePost()</c> then falls back to running the
/// action inline on the calling thread, which is correct for the synchronous headless harness (no cross-thread hop).
/// </para>
/// </summary>
public static class HostDispatch
{
    /// <summary>The host-provided UI-thread poster: <c>post(action)</c> schedules <c>action</c> to run on the UI thread
    /// on the next frame. Cross-thread safe. Null when no host published one (headless/test).</summary>
    public static readonly Context<System.Action<System.Action>?> Post = new(null);

    /// <summary>The same UI-thread poster as a process-static, for NON-component code (a module, a background service)
    /// that must marshal a signal write onto the UI thread but has no component context to resolve <see cref="Post"/>.
    /// The host sets it at construction and clears it on dispose; null when no host is running (headless/test → callers
    /// run inline). Resolved at CALL time, so a service constructed before the host still marshals correctly.</summary>
    public static volatile System.Action<System.Action>? Current;
}
