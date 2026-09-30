using System.Buffers;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Forms;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Text;
using static FluentGpu.Dsl.Ui;
using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── ProgressBar stretch (Wavee Home redesign, engine item E5): width: float.NaN fills the parent-offered width
// instead of a fixed DIP value — the standard facet-switch busy-bar pattern (full-content-width, pinned under a
// dimmed page). Registered directly in SuiteRegistry (tag "controls") rather than from ControlsSuite.Run, so this
// file never needs to touch ControlsSuite.cs. No source-text tests: every check is scene/animation behaviour.

static partial class ControlsSuite
{
    internal static void ProgressStretchChecks(StringTable strings)
    {
        // ── stretch: width:NaN fills a pinned parent width, keeps sweeping, and re-arms the sweep extent after a
        // parent resize ─────────────────────────────────────────────────────────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("progress-stretch", new Size2(500, 120), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var containerWidth = new Signal<float>(260f);
            var fills = new List<NodeHandle>(2);
            var parts = new TemplateParts();
            parts[ProgressBar.PartFill] = b => b with { OnRealized = h => { if (!fills.Contains(h)) fills.Add(h); } };
            var probe = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = containerWidth.Value, Padding = Edges4.All(0f),
                    Children = [ProgressBar.Indeterminate(width: float.NaN, parts: parts)],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, probe);
            // Frame 1: the stretch wrapper isn't measured yet (Width=0, sweep parked). Frame 2: the mount-once
            // layout-effect seeds the measured-width signal, MarksStale. Frame 3: the component re-renders at the
            // real width and arms the sweep at that extent.
            host.RunFrame(); host.RunFrame(); host.RunFrame();

            var barRoot0 = fills.Count == 2 ? host.Scene.Parent(fills[0]) : NodeHandle.Null;
            float w0 = barRoot0.IsNull ? -1f : host.Scene.AbsoluteRect(barRoot0).W;
            bool fillsParent = fills.Count == 2 && Near(w0, 260f, 4f);   // UseMeasuredWidth quantum 4
            bool sweptAtStart = fills.Count == 2 && host.Animation.HasTracks(fills[0]) && host.Animation.HasTracks(fills[1]);

            containerWidth.Value = 140f;   // shrink the pinned parent — a resize
            probe.Context.Runtime!.Flush();
            host.RunFrame(); host.RunFrame(); host.RunFrame();

            var barRoot1 = fills.Count == 2 ? host.Scene.Parent(fills[0]) : NodeHandle.Null;
            float w1 = barRoot1.IsNull ? -1f : host.Scene.AbsoluteRect(barRoot1).W;
            bool followedResize = fills.Count == 2 && Near(w1, 140f, 4f) && !Near(w1, w0, 4f);
            bool sweptAfterResize = fills.Count == 2 && host.Animation.HasTracks(fills[0]) && host.Animation.HasTracks(fills[1]);

            Check("gate.progress.stretch width:NaN fills the pinned parent width, keeps sweeping, and re-arms the sweep extent after a parent resize",
                fillsParent && sweptAtStart && followedResize && sweptAfterResize,
                $"w0={w0:0.0} w1={w1:0.0} fills={fills.Count} sweptStart={sweptAtStart} sweptResize={sweptAfterResize}");
        }

        // ── the finite-width path is unchanged: a static width ignores the parent entirely, before and after the
        // parent resizes ────────────────────────────────────────────────────────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("progress-stretch-finite", new Size2(500, 120), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var containerWidth = new Signal<float>(260f);
            var fills = new List<NodeHandle>(2);
            var parts = new TemplateParts();
            parts[ProgressBar.PartFill] = b => b with { OnRealized = h => { if (!fills.Contains(h)) fills.Add(h); } };
            var probe = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = containerWidth.Value, Padding = Edges4.All(0f),
                    Children = [ProgressBar.Indeterminate(parts: parts)],   // DefaultWidth = 240, unaffected by the parent
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, probe);
            host.RunFrame();

            var barRoot0 = fills.Count == 2 ? host.Scene.Parent(fills[0]) : NodeHandle.Null;
            float w0 = barRoot0.IsNull ? -1f : host.Scene.AbsoluteRect(barRoot0).W;

            containerWidth.Value = 140f;
            probe.Context.Runtime!.Flush();
            host.RunFrame(); host.RunFrame();

            var barRoot1 = fills.Count == 2 ? host.Scene.Parent(fills[0]) : NodeHandle.Null;
            float w1 = barRoot1.IsNull ? -1f : host.Scene.AbsoluteRect(barRoot1).W;

            Check("gate.progress.stretch.finite a finite-width Indeterminate keeps its fixed 240px width regardless of the parent's width — the pre-stretch path is unchanged",
                w0 == 240f && w1 == 240f, $"w0={w0} w1={w1} fills={fills.Count}");
        }
    }
}
