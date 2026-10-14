using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E7 (EP13) - persistent subtrees. A <see cref="Flow.Persist"/> slot's content survives the slot: a root swap re-parents it into
/// the new root (same component instance, same state, same video surface) and renders it once against the new providers; without
/// an adopter it stays parked, inactive and with its resources, for <see cref="Flow.PersistParkMs"/> on the host timer clock, then
/// unmounts. Under a frame freeze the parked clock stands still and the video release (if any) waits for the thaw.
/// Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class PersistSubtreeTests
{
    private static readonly Context<string> Variant = new("none");

    /// <summary>What the persisted leaf saw: instances, unmounts, renders, the provider value of its last render, its activation
    /// signal and the video token it acquired.</summary>
    private sealed class Probe
    {
        public int Instances, Unmounts, Renders;
        public string? Seen;
        public IReadSignal<bool>? Active;
        public int Token;
        public readonly List<int> Tokens = new();
    }

    private sealed class Tracker(Probe p) : IDisposable { public void Dispose() => p.Unmounts++; }

    private sealed class Leaf : Component
    {
        private readonly Probe _p;
        public Leaf(Probe p) { _p = p; p.Instances++; }

        public override Element Render()
        {
            _p.Renders++;
            UseDisposable(() => new Tracker(_p));
            _p.Seen = UseContext(Variant);
            _p.Active = UseIsActive();
            var video = UseVideoSurface();
            if (_p.Token != video.Token) { _p.Token = video.Token; _p.Tokens.Add(video.Token); }
            return new BoxEl { Width = 80f, Height = 45f, VideoHole = video.Token > 0, VideoSurfaceId = video.Token };
        }
    }

    /// <summary>The app root: <see cref="Shape"/> picks one of three structurally different trees, each providing <see cref="Variant"/>;
    /// <see cref="Present"/> false leaves the slot out (no adopter).</summary>
    private sealed class Root : Component
    {
        public readonly Probe Probe = new();
        public readonly Signal<int> Shape = new(0);
        public readonly Signal<bool> Present = new(true);
        public readonly Signal<string> Key = new("video");

        private Element Slot() => Flow.Persist(Key.Value, () => Embed.Comp(() => new Leaf(Probe)));

        public override Element Render()
        {
            int shape = Shape.Value;
            bool present = Present.Value;
            Element[] kids = present ? [Slot()] : [];
            return shape switch
            {
                // A column of boxes.
                0 => Ctx.Provide(Variant, "classic", new BoxEl { Width = 320f, Height = 240f, Children = [new BoxEl { Width = 100f, Height = 50f, Children = kids }] }),
                // A different root element type: RenderRootDiff removes the old root before it mounts this one.
                1 => new GridEl { Width = 320f, Height = 240f, Children = [Ctx.Provide(Variant, "zune", new BoxEl { Width = 200f, Height = 100f, Children = kids })] },
                // The keyed diff mounts the new keyed child before it removes the old one: the new slot waits, then adopts.
                _ => Ctx.Provide(Variant, "winui", new BoxEl
                {
                    Width = 320f, Height = 240f,
                    Children = [new BoxEl { Key = shape == 2 ? "left" : "right", Width = 100f, Height = 50f, Children = kids }],
                }),
            };
        }
    }

    private sealed class RecordingPresenter : IVideoPresenter
    {
        public readonly List<string> Calls = new();
        private uint _next = 1;
        public VideoSurfaceId CreateSurface() { Calls.Add("Create"); return new VideoSurfaceId(_next++); }
        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle) { Calls.Add("Bind"); return true; }
        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z) => Calls.Add("Place");
        public void SetVisible(VideoSurfaceId id, bool visible) => Calls.Add("Visible");
        public void Destroy(VideoSurfaceId id) => Calls.Add("Destroy");
        public void Commit() => Calls.Add("Commit");
        public int Count(string name) { int n = 0; foreach (var c in Calls) if (c == name) n++; return n; }
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly HeadlessGpuDevice Device = new();
        public readonly HeadlessWindow Window;
        public readonly Root Root = new();
        public readonly AppHost Host;
        public readonly RecordingPresenter Presenter = new();
        private bool _disposed;

        public Rig(bool renderThread = false)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(Strings), Strings, Root);
            if (renderThread) Host.InstallRenderThreadForTest();
            Device.VideoPresenterForTest = Presenter;
            Paint(3);
            // Give the leaf's surface a DComp handle and a place, so its release is a presenter Destroy.
            var reg = Host.VideoSurfacesForTest;
            reg.Bind(Root.Probe.Token, 0x1234);
            reg.Place(Root.Probe.Token, new RectF(10f, 20f, 80f, 45f));
            Paint(3);
        }

        public void Paint(int n = 1) { for (int i = 0; i < n; i++) Host.Paint(0); }

        /// <summary>Advance the host timer clock by <paramref name="ms"/> and paint (timers drain at the top of the frame).</summary>
        public void Elapse(double ms) { Host.AdvanceFrameClockForTest(ms); Paint(); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Host.Dispose();
            App.Dispose();
        }
    }

    [Fact]
    public void ARootSwap_ReparentsThePersistedSubtree_KeepsItsInstanceAndVideo_AndRendersItOnceAgainstTheNewProviders()
    {
        using var rig = new Rig();
        var p = rig.Root.Probe;
        Assert.Equal(1, p.Instances);
        Assert.Equal("classic", p.Seen);
        Assert.True(p.Token > 0);
        int token = p.Token;
        int rendersBefore = p.Renders;

        rig.Root.Shape.Value = 1;                       // a different root element type: the old root is removed, the new one mounted
        rig.Paint(2);

        Assert.Equal(1, p.Instances);                   // the same component instance: nothing remounted
        Assert.Equal(0, p.Unmounts);
        Assert.Equal("zune", p.Seen);                   // ... and it rendered against the new root's provider
        Assert.True(p.Renders > rendersBefore);
        Assert.Equal(token, p.Token);                   // the same video surface
        Assert.Single(p.Tokens);
        Assert.True(p.Active!.Peek());                  // active again in its new place
        Assert.Equal(0, rig.Presenter.Count("Destroy"));
        Assert.False(rig.Host.IsPersistParked("video"));
        Assert.Equal(1, rig.Host.Reconciler.PersistAdoptions);
        Assert.Equal(0, rig.Host.Reconciler.ParkedPersistCount);
    }

    [Fact]
    public void AKeyedMove_TheNewSlotMountsFirst_WaitsForTheOwner_AndAdoptsInTheSamePass()
    {
        using var rig = new Rig();
        var p = rig.Root.Probe;
        rig.Root.Shape.Value = 1;                       // provider → grid root: remount, adopted
        rig.Paint(2);
        rig.Root.Shape.Value = 2;                       // grid → provider root: remount, adopted again
        rig.Paint(2);
        Assert.Equal(2, rig.Host.Reconciler.PersistAdoptions);
        Assert.Equal("winui", p.Seen);

        rig.Root.Shape.Value = 3;                       // same root shape, the keyed child "left" → "right": mount-before-remove
        rig.Paint(2);

        Assert.Equal(1, p.Instances);
        Assert.Equal(0, p.Unmounts);
        Assert.Equal(3, rig.Host.Reconciler.PersistAdoptions);
        Assert.Single(p.Tokens);
        Assert.True(p.Active!.Peek());
        Assert.Equal(0, rig.Presenter.Count("Destroy"));
        Assert.Equal(0, rig.Host.Reconciler.ParkedPersistCount);
    }

    [Fact]
    public void WithoutAnAdopter_TheSubtreeStaysParkedInactiveWithItsVideo_ForTheParkTime_ThenUnmountsAndReleasesIt()
    {
        using var rig = new Rig();
        var p = rig.Root.Probe;

        rig.Root.Present.Value = false;                 // the slot leaves the tree; nothing adopts
        rig.Paint(2);
        Assert.True(rig.Host.IsPersistParked("video"));
        Assert.Equal(0, p.Unmounts);
        Assert.False(p.Active!.Peek());                 // parked: inactive (a video presenter hides its surface)
        Assert.Equal(0, rig.Presenter.Count("Destroy"));

        rig.Elapse(Flow.PersistParkMs - 100);
        Assert.True(rig.Host.IsPersistParked("video"));
        Assert.Equal(0, p.Unmounts);

        rig.Elapse(200);                                // past the park time: unmounted, the surface released
        rig.Paint(2);
        Assert.False(rig.Host.IsPersistParked("video"));
        Assert.Equal(1, p.Unmounts);
        Assert.Equal(1, rig.Presenter.Count("Destroy"));

        rig.Root.Present.Value = true;                  // a later slot builds fresh content
        rig.Paint(2);
        Assert.Equal(2, p.Instances);
    }

    [Fact]
    public void AParkedSubtree_IsAdoptedByALaterSlotWithinTheParkTime_AndItsExpiryTimerFiresIntoNothing()
    {
        using var rig = new Rig();
        var p = rig.Root.Probe;
        rig.Root.Present.Value = false;
        rig.Paint(2);
        rig.Elapse(4000);
        rig.Root.Shape.Value = 1;
        rig.Root.Present.Value = true;                  // adopted 4 s later, in a different tree
        rig.Paint(2);
        Assert.False(rig.Host.IsPersistParked("video"));
        Assert.Equal(1, p.Instances);
        Assert.Equal("zune", p.Seen);
        Assert.True(p.Active!.Peek());

        rig.Elapse(Flow.PersistParkMs);                 // the first park's timer fires: the adopted subtree is not touched
        rig.Paint(2);
        Assert.Equal(0, p.Unmounts);
        Assert.Equal(0, rig.Presenter.Count("Destroy"));
    }

    [Fact]
    public void AnotherKey_OnAReusedSlot_ParksTheOldSubtree_AndSwitchingBackAdoptsIt()
    {
        using var rig = new Rig();
        var p = rig.Root.Probe;
        rig.Root.Key.Value = "other";
        rig.Paint(2);
        Assert.True(rig.Host.IsPersistParked("video"));
        Assert.Equal(2, p.Instances);                   // "other" built its own content
        rig.Root.Key.Value = "video";
        rig.Paint(2);
        Assert.False(rig.Host.IsPersistParked("video"));
        Assert.True(rig.Host.IsPersistParked("other"));
        Assert.Equal(2, p.Instances);                   // the first content came back, no third instance
        Assert.Equal(0, p.Unmounts);
    }

    [Fact]
    public void WhileFrozen_TheParkClockStandsStill_AndTheVideoIsNeitherHiddenNorReleasedUnderTheHeldFrame()
    {
        using var rig = new Rig(renderThread: true);
        var p = rig.Root.Probe;
        rig.Host.FreezeWatchdogMs = int.MaxValue;       // this test holds the freeze across 30 s of host clock
        var token = rig.Host.Freeze();
        rig.Paint();                                    // the capture frame
        int visibleCalls = rig.Presenter.Count("Visible");

        rig.Root.Present.Value = false;                 // the apply flush drops the slot with no adopter
        rig.Paint(2);
        Assert.True(rig.Host.IsPersistParked("video"));
        for (int i = 0; i < 3; i++) rig.Elapse(Flow.PersistParkMs);   // 30 s of held frames 
        Assert.True(rig.Host.IsPersistParked("video"));   // timers are held: the park did not expire under the freeze
        Assert.Equal(0, rig.Presenter.Count("Destroy"));
        Assert.Equal(visibleCalls, rig.Presenter.Count("Visible"));   // no presenter call at all while held

        rig.Host.Thaw(token, DissolveSpec.Cut);
        rig.Paint(2);
        Assert.True(rig.Host.IsPersistParked("video"));   // the park time starts counting again after the thaw
        rig.Elapse(Flow.PersistParkMs + 100);
        rig.Paint(2);
        Assert.False(rig.Host.IsPersistParked("video"));
        Assert.Equal(1, p.Unmounts);
        Assert.Equal(1, rig.Presenter.Count("Destroy"));
    }

    [Fact]
    public void UnderAFreeze_AnAdoptedVideo_IsNeverReleased()
    {
        using var rig = new Rig(renderThread: true);
        var p = rig.Root.Probe;
        var token = rig.Host.Freeze();
        rig.Paint();
        rig.Root.Shape.Value = 1;                       // the transaction's root swap
        rig.Paint(3);
        rig.Host.Thaw(token, DissolveSpec.Cut);
        rig.Paint(3);
        Assert.Equal(1, p.Instances);
        Assert.Single(p.Tokens);
        Assert.Equal(0, rig.Presenter.Count("Destroy"));
        Assert.True(p.Active!.Peek());
    }

    [Fact]
    public void ADuplicateLiveKey_StopsWaitingAfterTheFrame_AndMountsItsOwnContent()
    {
        var probe = new Probe();
        var twin = new Signal<bool>(false);
        using var rig = new FreeRig(() => new BoxEl
        {
            Width = 320f, Height = 240f,
            Children = twin.Value
                ? [Flow.Persist("dup", () => Embed.Comp(() => new Leaf(probe))), Flow.Persist("dup", () => Embed.Comp(() => new Leaf(probe)))]
                : [Flow.Persist("dup", () => Embed.Comp(() => new Leaf(probe)))],
        }, twin);
        Assert.Equal(1, probe.Instances);
        twin.Value = true;
        rig.Paint(3);
        Assert.Equal(2, probe.Instances);               // the second slot waited one frame, then built its own
        twin.Value = false;
        rig.Paint(2);
        Assert.Equal(1, probe.Unmounts);                // the non-owner's content unmounts normally; it never parks
        Assert.Equal(0, rig.Host.Reconciler.ParkedPersistCount);
    }

    [Fact]
    public void DisposingTheHost_UnmountsParkedSubtrees()
    {
        var rig = new Rig();
        var p = rig.Root.Probe;
        rig.Root.Present.Value = false;
        rig.Paint(2);
        Assert.True(rig.Host.IsPersistParked("video"));
        rig.Dispose();
        Assert.Equal(1, p.Unmounts);
    }

    /// <summary>A rig around an arbitrary root render function.</summary>
    private sealed class FreeRig : IDisposable
    {
        private sealed class FnRoot(Func<Element> render, Signal<bool> dep) : Component
        {
            public override Element Render() { _ = dep.Value; return render(); }
        }

        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly HeadlessGpuDevice Device = new();
        public readonly AppHost Host;

        public FreeRig(Func<Element> render, Signal<bool> dep)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            window.Show();
            Host = new AppHost(App, window, Device, new HeadlessFontSystem(Strings), Strings, new FnRoot(render, dep));
            Paint(3);
        }

        public void Paint(int n = 1) { for (int i = 0; i < n; i++) Host.Paint(0); }
        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }
}
