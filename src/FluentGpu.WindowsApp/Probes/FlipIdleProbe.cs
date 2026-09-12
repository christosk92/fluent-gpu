using System;
using System.Diagnostics;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Signals;
using FluentGpu.Scene;

namespace FluentGpu;

/// <summary>Real render-thread regression probe: twelve once-per-second keyed digit replacements, then five
/// seconds with no changes. Uses the production wait request; diagnostic deadlines only shorten infinite waits.
/// Samples before RunFrame so a diagnostic wake cannot hide pending feedback or an unreclaimed orphan.</summary>
internal static class FlipIdleProbe
{
    public static int Run(bool backstop = false, bool stress = false)
    {
        int result = 1;
        var scene = new FlipIdleScene();
        FluentApp.DiagnosticRun = (host, window, _) =>
        {
            result = Drive(host, window, scene, stress);
            if (result == 0 && backstop) result = DriveBackstop(host, window, scene);
            return true;
        };
        FluentAppHarness.Run(() => scene, new AppOptions
        {
            Title = "FluentGpu - repeated flip idle probe", Width = 520, Height = 240,
            Mica = false, WarmCadenceMs = 0f,
        });
        FluentApp.DiagnosticRun = null;
        return result;
    }

    private static int Drive(AppHost host, IPlatformWindow window, FlipIdleScene scene, bool stress)
    {
        host.RunFrame();
        if (!host.Animation.RenderOwnsCompositor)
        {
            Console.Error.WriteLine("[flip-idle] FAIL: render thread does not own compositor animation.");
            return 2;
        }
        int flipLimit = stress ? 48 : 12;
        double finishMs = (flipLimit + 5) * 1000 + 700;
        Console.Error.WriteLine($"[flip-idle] renderOwned=true; {flipLimit} replacements at 1 Hz, then 5 seconds idle; stress={stress}");
        var elapsed = Stopwatch.StartNew();
        int flips = 0, failures = 0, samples = 0;
        double nextFlip = 1000, nextSample = 700;
        bool endOfQuietWindow = false;
        ulong quietPresent = 0;
        int peakTracks = 0, peakOrphans = 0;
        bool stallPending = false;
        double nextPublication = double.PositiveInfinity;
        while (!window.IsClosed && elapsed.Elapsed.TotalMilliseconds < finishMs + 300)
        {
            double now = elapsed.Elapsed.TotalMilliseconds;
            if (now >= nextSample)
            {
                int tracks = host.Animation.TrackCount, orphans = host.Scene.OrphanCount;
                ulong present = host.PresentedSequence;
                bool quiet = tracks == 0 && orphans == 0;
                if (endOfQuietWindow) quiet &= present == quietPresent;
                Console.Error.WriteLine($"[flip-idle] {(quiet ? "PASS" : "FAIL")} t={now:0}ms flip={flips} " +
                    $"tracks={tracks} orphans={orphans} present={present} " +
                    $"quietDelta={(endOfQuietWindow ? present - quietPresent : 0)} wait={host.LastWaitKind}/{host.LastWaitMs}");
                if (!quiet) failures++;
                samples++;
                quietPresent = present;
                nextSample += endOfQuietWindow ? 750 : 250;
                endOfQuietWindow = !endOfQuietWindow;
            }
            if (flips < flipLimit && now >= nextFlip)
            {
                scene.Value.Value = ++flips;
                nextFlip += 1000;
                stallPending = stress && flips % 3 == 0;
                nextPublication = stress ? now + 20 : double.PositiveInfinity;
            }
            if (now >= finishMs) break;
            if (stress && now >= nextPublication && now < nextFlip - 750)
            {
                scene.Publication.Value++;
                nextPublication = now + 20;
            }
            if (now >= nextFlip - 750) nextPublication = double.PositiveInfinity;
            host.RunFrame();
            peakTracks = Math.Max(peakTracks, host.Animation.TrackCount);
            peakOrphans = Math.Max(peakOrphans, host.Scene.OrphanCount);
            if (stallPending && host.Scene.OrphanCount > 0)
            {
                // Explicit fault stimulus, not animation timing: let the render thread finish while UI feedback stalls.
                System.Threading.Thread.Sleep(300);
                stallPending = false;
            }
            var wait = host.WaitRequestWithDetached();
            double deadline = Math.Min(nextSample, flips < flipLimit ? nextFlip : finishMs);
            if (stress && nextPublication < nextFlip - 750) deadline = Math.Min(deadline, nextPublication);
            int remaining = Math.Max(0, (int)Math.Ceiling(deadline - elapsed.Elapsed.TotalMilliseconds));
            wait = wait with { TimeoutMs = wait.TimeoutMs < 0 ? remaining : Math.Min(remaining, wait.TimeoutMs) };
            window.WaitForWork(in wait);
        }
        bool exercised = flips == flipLimit && samples >= (flipLimit + 5) * 2 && peakTracks >= 4 && peakOrphans >= 1
            && host.PresentedSequence > 0;
        Console.Error.WriteLine($"[flip-idle] {(failures == 0 && exercised ? "PASS" : "FAIL")} FINAL " +
            $"flips={flips} samples={samples} peakTracks={peakTracks} peakOrphans={peakOrphans} failures={failures}");
        return failures == 0 && exercised ? 0 : 1;
    }

    private static int DriveBackstop(AppHost host, IPlatformWindow window, FlipIdleScene scene)
    {
        // Synthetic fault injection: a real scene orphan with compositor loops that will never report Done.
        // maxAgeMs=0 selects only the existing two-second wall-clock backstop, isolating its scheduling contract.
        var orphan = host.Scene.CreateNode(1);
        host.Scene.AppendChild(host.Scene.Root, orphan);
        host.Scene.Bounds(orphan) = new RectF(100, 20, 60, 60);
        host.Scene.Orphan(orphan);
        host.Animation.Keyframes(orphan, AnimChannel.Opacity,
            [new(0, 0), new(.5f, 1, Easing.Linear), new(1, 0, Easing.Linear)], 200, loop: true);
        host.Animation.Keyframes(orphan, AnimChannel.TranslateY,
            [new(0, 0), new(.5f, 14, Easing.Linear), new(1, 0, Easing.Linear)], 200, loop: true);
        host.Scene.Mark(host.Scene.Root, NodeFlags.PaintDirty);
        scene.Publication.Value++;
        var elapsed = Stopwatch.StartNew();
        bool deadlineArmed = false;
        ulong initialPresent = host.PresentedSequence;
        while (!window.IsClosed && elapsed.ElapsedMilliseconds < 4000)
        {
            host.RunFrame();
            if (!host.Scene.IsLive(orphan)) break;
            var wait = host.WaitRequestWithDetached();
            double age = elapsed.Elapsed.TotalMilliseconds;
            if (host.LastWaitKind == HostWaitKind.Idle && wait.TimeoutMs > 0 && wait.TimeoutMs <= 2100 - age)
                deadlineArmed = true;
            // The four-second watchdog is deliberately later than the engine's deadline. A broken host gets no
            // diagnostic frame at two seconds that could accidentally reclaim the orphan and mask the failure.
            int remaining = Math.Max(1, 4000 - (int)elapsed.ElapsedMilliseconds);
            wait = wait with { TimeoutMs = wait.TimeoutMs < 0 ? remaining : Math.Min(wait.TimeoutMs, remaining) };
            window.WaitForWork(in wait);
        }
        bool reclaimed = !host.Scene.IsLive(orphan) && host.Scene.OrphanCount == 0 && host.Animation.TrackCount == 0;
        bool timely = elapsed.ElapsedMilliseconds < 3000;
        Console.Error.WriteLine($"[flip-backstop] {(reclaimed && timely && deadlineArmed ? "PASS" : "FAIL")} " +
            $"elapsedMs={elapsed.ElapsedMilliseconds} deadlineArmed={deadlineArmed} reclaimed={reclaimed} " +
            $"tracks={host.Animation.TrackCount} orphans={host.Scene.OrphanCount} presents={host.PresentedSequence - initialPresent}");
        return reclaimed && timely && deadlineArmed ? 0 : 1;
    }

    private sealed class FlipIdleScene : Component
    {
        public readonly Signal<int> Value = new(0);
        public readonly Signal<int> Publication = new(0);

        public override Element Render()
        {
            int value = Value.Value;
            int publication = Publication.Value;
            return new BoxEl
            {
                Width = 80f, Height = 80f, ClipToBounds = true,
                Opacity = (publication & 1) == 0 ? 1f : .99f,
                Children =
                [
                    new BoxEl
                    {
                        Key = "digit-" + value, Width = 80f, Height = 80f,
                        Enter = new EnterExit(Dy: 14f, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Dy: -14f, Opacity: 0f, Active: true),
                        Transition = MotionTok.ControlFast,
                        Children = [new TextEl(value.ToString()) { Color = Tok.TextPrimary }],
                    },
                ],
            };
        }
    }
}
