using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu;

/// <summary>
/// Deterministic HEADLESS repro for the "Windows APIs" page hang (<c>--post-freeze-probe</c>). It drives a real
/// <see cref="AppHost"/> on the headless seam — the SAME engine frame loop the live gallery runs — with a component that
/// uses <c>UsePost()</c> (the engine's cross-thread UI marshal the migrated cards now depend on). It then posts an action
/// from a WORKER thread (exactly as the NetworkCard's <c>Task.Run</c> completion does) and pumps frames, asserting the
/// post is applied.
/// <para>
/// The bug this catches: <c>AppHost.Post</c> enqueues + <c>Wake()</c>s the loop, but the loop's idle gate
/// (<c>HasActiveWork</c> / <c>ComputeWakeReasons</c>) has NO term for a non-empty <c>_uiPosts</c> queue, and the drain
/// (<c>DrainUiPosts</c>) lives INSIDE <c>Paint</c> — which the idle gate skips. So a post that arrives while the loop is
/// idle wakes it, the wake message is consumed by the pump, the idle gate sees no work and returns BEFORE Paint, and the
/// post is stranded forever → the live cards freeze (stale UI, no re-render). Headless <c>WaitForWork</c> is a no-op so a
/// frame always runs, but the <c>HasActiveWork</c> early-return reproduces the stranding identically (Paint is skipped,
/// so DrainUiPosts never runs) — a faithful, OS-free repro.
/// </para>
/// <para>Exit codes: <c>0</c> = the post applied within the frame budget (fixed); <c>2</c> = the post was stranded
/// (reproduced); <c>3</c> = harness error.</para>
/// </summary>
internal static class PostFreezeProbe
{
    public static int Run(string[] args)
    {
        try
        {
            var strings = new StringTable();
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("post-freeze", new Size2(320, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new PostProbeComponent();
            using var host = new AppHost(app, window, device, fonts, strings, probe);

            // Frame 1: mount. The component captures its UsePost() delegate into PostProbeComponent.CapturedPost.
            host.RunFrame();
            if (PostProbeComponent.CapturedPost is null)
            {
                Console.WriteLine("[post-probe] FAIL: UsePost() never produced a poster (host did not publish HostDispatch.Post).");
                return 3;
            }
            Console.WriteLine($"[post-probe] mounted. initial label='{PostProbeComponent.Label.Peek()}'");

            // Settle: drain any mount-time work so the loop is genuinely IDLE (HasActiveWork == false) — the precondition
            // for the stranding race. A few no-op frames let mount effects/animations quiesce.
            for (int i = 0; i < 10; i++) host.RunFrame();
            Console.WriteLine($"[post-probe] idle. wakeReasons={host.CurrentWakeReasons}");

            // Post from a WORKER thread — the literal NetworkCard pattern (Task.Run completion → post(() => signal.Value = …)).
            // The post enqueues onto AppHost and Wake()s the (headless no-op) window; the next RunFrame must drain + apply it.
            const string Applied = "APPLIED-FROM-WORKER";
            var posted = new ManualResetEventSlim(false);
            var worker = new Thread(() =>
            {
                PostProbeComponent.CapturedPost!(() => PostProbeComponent.Label.Value = Applied);
                posted.Set();
            }) { IsBackground = true, Name = "post-probe-worker" };
            worker.Start();
            posted.Wait(2000);   // ensure the enqueue happened before we pump (the wake is already queued either way).

            // Pump a bounded number of frames. A correct host applies the post within ONE frame; the budget is generous.
            const int Budget = 60;
            int appliedFrame = -1;
            for (int f = 0; f < Budget; f++)
            {
                host.RunFrame();
                if (PostProbeComponent.Label.Peek() == Applied) { appliedFrame = f; break; }
            }

            string final = PostProbeComponent.Label.Peek();
            if (appliedFrame >= 0)
            {
                Console.WriteLine($"[post-probe] PASSED — post applied on frame {appliedFrame} (label='{final}').");
                return 0;
            }

            Console.WriteLine($"[post-probe] REPRODUCED (FREEZE) — post STRANDED after {Budget} frames; label still '{final}'.");
            Console.WriteLine($"[post-probe] wakeReasons at end={host.CurrentWakeReasons} (no term covers a pending _uiPosts queue → Paint/DrainUiPosts skipped).");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[post-probe] harness EXCEPTION: {ex}");
            return 3;
        }
    }
}

/// <summary>The probed component: reads <c>UsePost()</c> and renders a label signal. A worker posts a label change; the
/// engine loop must drain + apply it. Static handoff (single-instance headless probe) keeps the harness simple.</summary>
internal sealed class PostProbeComponent : Component
{
    /// <summary>The label the worker mutates via a post; its application proves the drain ran.</summary>
    public static readonly Signal<string> Label = new("INITIAL");
    /// <summary>The captured <c>UsePost()</c> delegate (the real <c>AppHost.Post</c> when a host published it).</summary>
    public static Action<Action>? CapturedPost;

    public override Element Render()
    {
        CapturedPost = UsePost();
        var label = UseContextSignal();
        return new BoxEl { Children = [Ui.Text(label)] };
    }

    // Subscribe this component to the Label signal so a post-driven write re-renders it (mirrors the card's UseSignal-read).
    private string UseContextSignal() => Label.Value;
}
