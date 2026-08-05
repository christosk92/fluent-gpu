using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The cross-thread UI-post drain (<c>AppHost.Post</c> / <c>HostDispatch.Post</c> / <c>UsePost</c>) and the two bounds
/// that keep it from turning a backlog into a frozen UI thread.
///
/// The bug these pin: the drain used to sit BELOW <c>RunFrame</c>'s minimize gate, so a minimized app accumulated posts
/// for the entire minimize (an unbounded ConcurrentQueue) and then ran the WHOLE backlog in one drain on the restore
/// frame — which executes synchronously inside the WndProc's WM_SIZE. Thousands of queued actions, each paying a
/// cross-process RPC, is a multi-second "Not Responding" hang proportional to the minimize duration.
///
/// Driven through a real headless <c>AppHost</c>, not a mock: the drain's placement relative to the gates IS the
/// contract, and only <c>RunFrame</c> expresses it. The headless window's settable <see cref="WindowState"/> is the
/// only test seam used — everything else is the production loop.
/// </summary>
public sealed class UiPostDrainTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    private sealed class Fixture : IDisposable
    {
        public HeadlessPlatformApp App { get; }
        public HeadlessWindow Window { get; }
        public AppHost Host { get; }

        public Fixture()
        {
            var strings = new StringTable();
            App = new HeadlessPlatformApp();
            Window = new HeadlessWindow(new WindowDesc("ui-post-drain", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new EmptyRoot());
        }

        /// <summary>Put the host in the steady MINIMIZED state (the edge frame consumed). While minimized, <c>Paint</c>
        /// — and therefore Paint's own second drain — never runs, so each <c>RunFrame</c> performs EXACTLY ONE drain.
        /// That is what makes the per-drain ceiling observable.</summary>
        public void Minimize()
        {
            Window.State = WindowState.Minimized;
            Host.RunFrame();   // the minimize EDGE frame
            Host.RunFrame();   // steady state
        }

        public void Dispose()
        {
            Host.Dispose();
            App.Dispose();
        }
    }

    // ── A: the minimize gate must not strand posts ────────────────────────────────────────────────────────────────────

    /// <summary>THE FIX. A post made while minimized runs on the very next loop iteration — the one its own
    /// <c>Wake()</c> (PostMessage WM_NULL) already causes. The queue must therefore never grow across a minimize,
    /// however long it lasts.</summary>
    [Fact]
    public void PostsMadeWhileMinimized_DrainOnTheNextMinimizedFrame()
    {
        using var f = new Fixture();
        f.Minimize();

        int ran = 0;
        for (int i = 0; i < 25; i++)
        {
            f.Host.Post(() => ran++);
            f.Host.RunFrame();                                  // the iteration that post's Wake() causes
            Assert.Equal(i + 1, ran);                           // ran THIS frame, not banked for the restore
            Assert.Equal(0, f.Host.PendingUiPostCount);         // and the accumulator stays empty
        }
    }

    /// <summary>The restore frame must have nothing left to pay for. Before the fix this queue held one entry per post
    /// made during the whole minimize, all of it settled synchronously inside WM_SIZE.</summary>
    [Fact]
    public void RestoreFrame_HasNoBacklogToSettle()
    {
        using var f = new Fixture();
        f.Minimize();

        int ran = 0;
        for (int i = 0; i < 200; i++) { f.Host.Post(() => ran++); f.Host.RunFrame(); }
        Assert.Equal(200, ran);
        Assert.Equal(0, f.Host.PendingUiPostCount);

        f.Window.State = WindowState.Normal;
        f.Host.RunFrame();                                      // the restore frame: nothing queued ⇒ nothing to settle
        Assert.Equal(200, ran);
        Assert.Equal(0, f.Host.PendingUiPostCount);
    }

    // ── B: the per-drain ceiling ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A backlog deeper than the ceiling is sliced across frames, in FIFO order, and the host re-arms itself
    /// (<c>HasActiveWork</c>) so the remaining slices are actually produced. This is what converts any FUTURE
    /// accumulator bug from a freeze into a brief catch-up.</summary>
    [Fact]
    public void DeepBacklog_DrainsInCeilingSizedSlicesInOrder()
    {
        const int Total = 1000;
        using var f = new Fixture();
        f.Minimize();

        var order = new List<int>(Total);
        for (int i = 0; i < Total; i++) { int n = i; f.Host.Post(() => order.Add(n)); }
        Assert.Equal(Total, f.Host.PendingUiPostCount);

        f.Host.RunFrame();
        Assert.Equal(AppHost.MaxUiPostsPerDrain, order.Count);                       // exactly one ceiling-sized slice
        Assert.Equal(Total - AppHost.MaxUiPostsPerDrain, f.Host.PendingUiPostCount); // the rest is still queued
        Assert.True(f.Host.HasActiveWork);                                           // …and re-armed, so more frames come

        int frames = 1;
        while (f.Host.PendingUiPostCount > 0 && frames < 64) { f.Host.RunFrame(); frames++; }

        Assert.Equal(Total, order.Count);
        Assert.Equal(0, f.Host.PendingUiPostCount);
        // FIFO across the slice boundaries — a ceiling that reordered would be worse than the freeze.
        for (int i = 0; i < Total; i++) Assert.Equal(i, order[i]);
        // ceil(1000/256) = 4 frames. Pin it: a ceiling that silently stopped applying would drain in one.
        Assert.Equal((Total + AppHost.MaxUiPostsPerDrain - 1) / AppHost.MaxUiPostsPerDrain, frames);
    }

    // ── The standing anti-livelock guarantee ──────────────────────────────────────────────────────────────────────────

    /// <summary>An action that unconditionally re-Posts itself must not spin a single drain. The one-frame snapshot
    /// bound still gives the exact original guarantee — a lone self-re-poster runs ONCE per frame — and the ceiling
    /// bounds it a fortiori when many are queued.</summary>
    [Fact]
    public void SelfRePostingAction_CannotLivelockOneDrain()
    {
        using var f = new Fixture();
        f.Minimize();

        int runs = 0;
        Action? self = null;
        self = () => { runs++; f.Host.Post(self!); };
        f.Host.Post(self);

        f.Host.RunFrame();
        Assert.Equal(1, runs);                              // the re-post is deferred to a LATER frame, not spun here
        Assert.Equal(1, f.Host.PendingUiPostCount);
        f.Host.RunFrame();
        Assert.Equal(2, runs);
    }

    /// <summary>The same guarantee under load: a queue full of self-re-posters cannot exceed the ceiling in one drain,
    /// no matter how fast they refill it.</summary>
    [Fact]
    public void ManySelfRePostingActions_StillRespectTheCeiling()
    {
        using var f = new Fixture();
        f.Minimize();

        int runs = 0;
        Action? self = null;
        self = () => { runs++; f.Host.Post(self!); };
        for (int i = 0; i < AppHost.MaxUiPostsPerDrain * 3; i++) f.Host.Post(self);

        f.Host.RunFrame();
        Assert.Equal(AppHost.MaxUiPostsPerDrain, runs);
    }
}
