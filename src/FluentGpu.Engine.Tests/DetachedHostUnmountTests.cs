using System;
using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Disposing a host unmounts its component tree: nothing ever disposed the reconciler's root effect, so a closed pop-out's
/// components kept their subscriptions on process-lifetime signals (and the shared MediaPlayer kept their handlers), which
/// pinned the whole dead host and scheduled re-renders into a dead runtime on every later write. Serial: it uses a
/// static signal as the process-lifetime subscription target and constructs several hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DetachedHostUnmountTests
{
    private static readonly Signal<int> s_global = new(0);   // stands in for Playback.Video.Player / Prefs.Epoch

    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    /// <summary>Reads the global signal (a render subscription), owns a nested child that does too, and registers a mount
    /// effect whose cleanup is the unmount probe (the MediaPlayerElement <c>PumpRequested -=</c> shape).</summary>
    private sealed class Probe(Action onMount, Action onCleanup) : Component
    {
        public override Element Render()
        {
            _ = s_global.Value;
            UseEffect(() => { onMount(); return onCleanup; }, DepKey.From(0));
            return Ui.VStack(0, Embed.Comp(() => new Leaf()));
        }
    }

    private sealed class Leaf : Component
    {
        public override Element Render()
        {
            _ = s_global.Value;
            return Ui.VStack(0);
        }
    }

    private static (AppHost Parent, AppHost Child, HeadlessWindow ChildWindow, IDisposable Cleanup) Build(Component childRoot)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var device = new HeadlessGpuDevice();
        var pw = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        pw.Show();
        var parent = new AppHost(app, pw, device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
        parent.RunFrame();
        var cw = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
        cw.Show();
        var child = new AppHost(app, cw, device, new HeadlessFontSystem(strings), strings, childRoot,
            images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
        parent.AdoptDetachedChild(child);
        return (parent, child, cw, new Teardown(parent, app));
    }

    private sealed class Teardown(AppHost parent, HeadlessPlatformApp app) : IDisposable
    {
        public void Dispose() { parent.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void ReapingAChild_RunsItsMountCleanups_AndReleasesItsGlobalSignalSubscriptions()
    {
        int mounted = 0, cleaned = 0;
        int baseline = s_global.SubscriberCount;
        var (parent, _, cw, td) = Build(new Probe(() => mounted++, () => cleaned++));
        using (td)
        {
            for (int i = 0; i < 3; i++) { parent.RunFrame(); parent.TickDetachedHosts(); }
            Assert.Equal(1, mounted);
            Assert.Equal(0, cleaned);
            Assert.True(s_global.SubscriberCount >= baseline + 2);   // root + nested child render-effects

            cw.IsClosed = true;
            parent.TickDetachedHosts();   // reap -> Dispose

            Assert.Equal(1, cleaned);
            Assert.Equal(baseline, s_global.SubscriberCount);   // nothing of the dead host stays on the process-lifetime signal
            s_global.Value++;                                    // a later write schedules nothing into the dead runtime
            parent.RunFrame();
        }
    }

    [Fact]
    public void DisposingAPrimaryHost_UnmountsItsRoot_BeforeTeardown()
    {
        int mounted = 0, cleaned = 0;
        int baseline = s_global.SubscriberCount;
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var win = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        win.Show();
        var host = new AppHost(app, win, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
            new Probe(() => mounted++, () => cleaned++));
        host.RunFrame();
        Assert.Equal(1, mounted);
        Assert.Equal(0, cleaned);

        host.Dispose();

        Assert.Equal(1, cleaned);
        Assert.Equal(baseline, s_global.SubscriberCount);
    }

    [Fact]
    public void AReapedChildHost_BecomesCollectable()
    {
        var weak = ReapOne(out var td);
        using (td)
        {
            for (int i = 0; i < 5 && weak.IsAlive; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
            }
            Assert.False(weak.IsAlive, "the reaped child host is still reachable (a subscription or handler outlived its unmount)");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReapOne(out IDisposable td)
    {
        var (parent, child, cw, teardown) = Build(new Probe(() => { }, () => { }));
        td = teardown;
        parent.RunFrame(); parent.TickDetachedHosts();
        cw.IsClosed = true;
        parent.TickDetachedHosts();
        return new WeakReference(child);
    }
}
