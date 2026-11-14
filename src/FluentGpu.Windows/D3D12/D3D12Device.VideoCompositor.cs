using FluentGpu.Foundation;
using FluentGpu.Pal.Windows;
using TerraFX.Interop.Windows;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.WinRT.WinRT;

namespace FluentGpu.Rhi.D3D12;

// The Windows.UI.Composition compositor the CompositionVideoPresenters build their trees on. A WUC sprite cannot be
// parented under a DComp visual (E_NOINTERFACE both ways, measured), so the video lives on its own compositor, owned by a
// CompositorController: nothing reaches the screen until the render thread calls its Commit (CommitVideoComposition). Created
// lazily on the RENDER thread and confined to it; it does not depend on the D3D12 device, so a device recovery keeps it (only the
// presenters are rebuilt). Released with the device.
public sealed unsafe partial class D3D12Device
{
    // A CompositorController needs a DispatcherQueue on its thread (activation fails with E_ACCESSDENIED without one, measured). The
    // queue's hidden adapter window gets one coalesced wake message per batch of commits (100000 unpumped commits leave exactly one
    // message and no memory growth, measured); PumpVideoCompositorQueue removes it.
    private IDispatcherQueueController* _wucQueue;
    private ICompositorController* _wucController;
    private ICompositor* _wuc;
    private ICompositorInterop* _wucInterop;
    private ICompositorDesktopInterop* _wucDesktop;
    private bool _wucFailed;       // creation failed once: logged once, never retried; video surfaces then cannot be created
    private bool _wucCommitDue;    // a presenter wrote to the WUC tree since the controller's last Commit
    private uint _wucCommitFailedHr;

    internal ICompositor* VideoCompositor => _wuc;
    internal ICompositorInterop* VideoCompositorInterop => _wucInterop;
    internal ICompositorDesktopInterop* VideoCompositorDesktop => _wucDesktop;

    /// <summary>The video compositor, created on first use (render thread). False when the platform refused it (logged once).</summary>
    internal bool EnsureVideoCompositor()
    {
        AssertSubmitThread();
        if (_wuc != null) return true;
        if (_wucFailed) return false;
        _wucFailed = true;   // every early return below is a failure; cleared at the end
        DispatcherQueueOptions opts = default;
        opts.dwSize = (uint)sizeof(DispatcherQueueOptions);
        opts.threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT;
        opts.apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE;
        IDispatcherQueueController* dq;
        HRESULT hr = CreateDispatcherQueueController(opts, &dq);
        if ((int)hr < 0)
        {
            // A thread that already has a queue (a host whose render thread is the UI thread) refuses a second one; that queue is
            // pumped by its owner and the activation below still finds it. Any other refusal makes the activation fail and say so.
            Diag.Line($"[video.surface] [video.wuc] CreateDispatcherQueueController failed: 0x{(uint)hr:X8}");
            dq = null;
        }
        IInspectable* insp = null;
        const string name = "Windows.UI.Composition.Core.CompositorController";
        fixed (char* p = name)
        {
            HSTRING cls;
            hr = WindowsCreateString(p, (uint)name.Length, &cls);
            if ((int)hr >= 0)
            {
                hr = RoActivateInstance(cls, &insp);
                WindowsDeleteString(cls);
            }
        }
        ICompositorController* cc = null;
        ICompositor* comp = null;
        ICompositorInterop* interop = null;
        ICompositorDesktopInterop* desktop = null;
        if ((int)hr >= 0)
        {
            hr = insp->QueryInterface(__uuidof<ICompositorController>(), (void**)&cc);
            insp->Release();
        }
        if ((int)hr >= 0) hr = cc->get_Compositor(&comp);
        if ((int)hr >= 0) hr = ((IUnknown*)comp)->QueryInterface(__uuidof<ICompositorInterop>(), (void**)&interop);
        if ((int)hr >= 0) hr = ((IUnknown*)comp)->QueryInterface(__uuidof<ICompositorDesktopInterop>(), (void**)&desktop);
        if ((int)hr < 0)
        {
            Diag.Line($"[video.surface] [video.wuc] video compositor creation failed: 0x{(uint)hr:X8} (video surfaces cannot be created)");
            if (desktop != null) desktop->Release();
            if (interop != null) interop->Release();
            if (comp != null) comp->Release();
            if (cc != null) cc->Release();
            if (dq != null) dq->Release();
            return false;
        }
        _wucQueue = dq; _wucController = cc; _wuc = comp; _wucInterop = interop; _wucDesktop = desktop;
        _wucFailed = false;
        Diag.Line("[video.surface] [video.wuc] video compositor created: Windows.UI.Composition CompositorController on the render thread");
        return true;
    }

    /// <summary>A presenter wrote to the WUC tree: the controller owes a Commit (render thread).</summary>
    internal void NoteVideoCompositorCommitDue() => _wucCommitDue = true;

    // CommitVideoComposition's WUC half: the controller's Commit, only when a presenter wrote since the last one. Non-throwing: a
    // failure leaves the compositor on its prior composition and is logged once per distinct HRESULT.
    private void CommitVideoCompositor()
    {
        if (!_wucCommitDue) return;
        _wucCommitDue = false;
        if (_wucController == null) return;
        // The freeze trail: the Commit that publishes a protected surface's bind / attach / detach to DWM is a timed step.
        long traceT0 = FluentGpu.Media.ProtectedVideoTrace.TakeCommitStamp() ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        HRESULT hr = _wucController->Commit();
        if (traceT0 != 0)
        {
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            FluentGpu.Media.ProtectedVideoTrace.Stamp($"wuc.commit.end hr=0x{(uint)hr:X8}", 0, end, end - traceT0);
        }
        if ((int)hr < 0 && _wucCommitFailedHr != (uint)hr)
        {
            _wucCommitFailedHr = (uint)hr;
            Diag.Line($"[video.surface] [video.wuc] controller Commit failed: 0x{(uint)hr:X8}");
        }
    }

    private const uint PmRemove = 0x0001;   // PM_REMOVE
    private bool _wucGeometryLastApply;     // the last video apply (any window) wrote placement geometry
    private uint _wucDrainLogged;           // drain counts above 2 already logged (bit n)

    /// <summary>Remove at most 8 messages from the render thread's queue. The DispatcherQueue's adapter window is woken by the
    /// controller's commits and nothing else posts to this thread (no PostThreadMessage in the engine; the render loop waits on
    /// kernel handles, never on its message queue). Draining on a turn that moves the picture doubled the hole/picture disagreement
    /// wherever the drain sat (measured: 0.39 % of frames one behind at the turn's start, 0.40 % right after the commit, 0.20 %
    /// untouched), so it runs only at the start of a video apply with NO geometry write pending (<paramref name="geometryPending"/>)
    /// that does not follow an apply which wrote geometry. Nothing piles up meanwhile: the compositor posts ONE coalesced wake
    /// however many commits are outstanding (measured after 10 to 100000 unpumped commits, private bytes flat). Only on a dedicated
    /// render thread: where the host renders on its UI thread that thread's own pump owns the queue.</summary>
    internal void PumpVideoCompositorQueue(bool geometryPending)
    {
        bool quiet = !geometryPending && !_wucGeometryLastApply;
        if (!quiet || _wucQueue == null || !_renderConfined) return;
        MSG msg;
        int n = 0;
        while (n < 8 && PeekMessageW(&msg, HWND.NULL, 0, 0, PmRemove))
        {
            DispatchMessageW(&msg);
            n++;
        }
        if (n > 2 && (_wucDrainLogged & (1u << n)) == 0)
        {
            _wucDrainLogged |= 1u << n;   // cold: proves the one-message bound in the field, once per distinct count
            Diag.Line($"[video.surface] [video.wuc] queue drained n={n}");
        }
    }

    /// <summary>A video apply finished; <paramref name="wroteGeometry"/> = it wrote placement geometry (the next apply then does
    /// not drain the queue).</summary>
    internal void NoteVideoCompositorApply(bool wroteGeometry) => _wucGeometryLastApply = wroteGeometry;

    // ── the protected composition keeper ─────────────────────────────────────────────────────────────────────────────────────────
    // ProtectedCompositionKeeper (FluentGpu.Windows/Pal): one 16x16 hardware-protected swap chain in the MAIN window's video tree for
    // the life of the process, so DWM enters hardware-protected composition once (a 200-680 ms desktop freeze) instead of at every
    // protected video's arrival and departure. Independent of the D3D12 device (its own D3D11 device, built on the pool); attached on
    // a render turn. Lifetime (ProtectedKeeperPolicy): off; armed at launch (the first turn after the main window's first present) on
    // an adapter a previous run negotiated SL3000 on; armed at this run's first SL3000 negotiation; then held until exit, never dropped
    // on idle. Re-built (the new one swapped into the same sprite before the old one is released) when its adapter reports a hardware
    // content protection teardown (sleep / resume), its device is removed, or the main window's presenter was rebuilt (device loss on
    // the render side). A build that fails turns it off for the run, logged once: everything is then as before.

    private sealed class KeeperBuild
    {
        public ProtectedCompositionKeeper? Keeper;   // under the instance lock
        public string Failure = "";
        public string Why = "";
        public bool Done, Abandoned;
    }

    private const int KeeperMaxBuilds = 8;            // a keeper that keeps getting lost is not worth a build per loss
    private ProtectedCompositionKeeper? _keeper;      // the live keeper (render thread)
    private ProtectedCompositionKeeper? _keeperRetired;   // replaced by a rebuild: released on the turn after the commit that swapped it out
    private FluentGpu.Pal.Windows.CompositionVideoPresenter? _keeperHost;   // the presenter whose tree shows _keeper (null = not attached)
    private KeeperBuild? _keeperBuild;                // a pool build in flight, or done and not yet taken
    private bool _keeperOff;                          // a build or the attach failed: logged once, never retried
    private bool _keeperAdapterRead, _keeperRemembered;
    private ulong _keeperAdapterKey;
    private int _keeperBuilds;
    private long _keeperHealthDueQpc;
    // A display change (ProtectedKeeperPolicy.NoteDisplayChange) takes the keeper out of DWM's tree on the turn it arrives and keeps it
    // out until no display change came for this long: a hot-plugged output's link (and its HDCP) comes up in a few seconds, and DWM's
    // protected composition across it blacked out the whole desktop (2026-11-13). Re-armed by the first commit after the quiet ends.
    private const int KeeperDisplayQuietMs = 5000;
    private long _keeperQuietUntilQpc;
    private System.Collections.Generic.List<ProtectedCompositionKeeper>? _keeperDropped;   // out of the tree: released after that commit

    /// <summary>Render thread, at the top of every <see cref="CommitVideoComposition"/> (so an attach it makes rides the commit right
    /// after). With no keeper wanted: three static reads. Steady state with one: a flag read and, once a second, the device-removed
    /// reason. No allocation outside a build, an attach, a loss or a display change.</summary>
    private void TickProtectedKeeper()
    {
        // A replaced keeper goes once its successor is in the tree (the commit that swapped them has run: it followed that attach).
        if (_keeperRetired != null && _keeperHost != null) { DestroyKeeper(_keeperRetired, "replaced"); _keeperRetired = null; }
        // Dropped keepers go once the commit that took them out of the tree has run (the previous tick's).
        if (_keeperDropped != null)
        {
            foreach (var k in _keeperDropped) DestroyKeeper(k, "display-change");
            _keeperDropped = null;
        }
        // Always taken, even with the keeper off: a pending change is the pre-turn's reason to commit.
        if (FluentGpu.Media.ProtectedKeeperPolicy.TryTakeDisplayChange(out long changedQpc))
        {
            DropKeeperForDisplayChange();
            _keeperQuietUntilQpc = changedQpc + KeeperDisplayQuietMs * System.Diagnostics.Stopwatch.Frequency / 1000;
        }
        if (_keeperQuietUntilQpc != 0)
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() < _keeperQuietUntilQpc) return;
            _keeperQuietUntilQpc = 0;
            if (!_keeperOff) Diag.Line("[video.surface] [keeper] displays quiet for " + KeeperDisplayQuietMs + " ms: re-armed");
        }
        if (_keeperOff) return;
        FluentGpu.Media.ProtectedKeeperSwitch sw = FluentGpu.Media.ProtectedKeeperPolicy.Switch;
        bool hw = FluentGpu.Media.ProtectedKeeperPolicy.HardwareDrmSeen;
        if (_keeper == null && _keeperBuild == null && sw != FluentGpu.Media.ProtectedKeeperSwitch.Launch
            && !(sw == FluentGpu.Media.ProtectedKeeperSwitch.Runtime && hw))
            return;   // nothing wants it: the default (ProtectedKeeperSwitch.Auto is off), the common case
        if (!_keeperAdapterRead)
        {
            _keeperAdapterRead = true;   // cold: one adapter lookup per run
            _keeperAdapterKey = FluentGpu.Pal.Windows.ProtectedCompositionKeeper.AdapterKeyFor(GpuAdapterInfo.CurrentAdapterLuid);
        }
        if (hw && !_keeperRemembered)
        {
            _keeperRemembered = true;   // SL3000 on this adapter: the app persists it, so the next launch arms the keeper at once
            if (_keeperAdapterKey != 0 && _keeperAdapterKey != FluentGpu.Media.ProtectedKeeperPolicy.RememberedAdapter)
            {
                try { FluentGpu.Media.ProtectedKeeperPolicy.Remember?.Invoke(_keeperAdapterKey); }
                catch (System.Exception e) { Diag.Line("[video.surface] [keeper] remember failed: " + e.Message); }
            }
        }
        if (_keeper == null && _keeperBuild == null && !FluentGpu.Media.ProtectedKeeperPolicy.Wanted(_keeperAdapterKey)) return;
        if (_primarySwapchain is not { Composited: true } primary) return;
        if (GetVideoPresenter(primary) is not FluentGpu.Pal.Windows.CompositionVideoPresenter presenter) return;

        // A finished build: the new keeper takes over (a replaced one stays in the tree until the swap below commits).
        if (_keeperBuild is { } b)
        {
            ProtectedCompositionKeeperResult(b, out bool done, out FluentGpu.Pal.Windows.ProtectedCompositionKeeper? built, out string failure);
            if (done)
            {
                _keeperBuild = null;
                if (built == null) { KeeperOff("the keeper could not be built: " + failure); return; }
                if (_keeper != null)
                {
                    if (_keeperRetired != null) DestroyKeeper(_keeperRetired, "replaced");   // two losses before one swap landed
                    _keeperRetired = _keeper;
                }
                _keeper = built;
                _keeperHost = null;   // attached (or swapped into the existing sprite) below
            }
        }

        // Health: a keeper whose protection was torn down, whose device was removed, or whose presenter was rebuilt is built anew.
        if (_keeper != null && _keeperBuild == null)
        {
            string? lost = null;
            if (_keeperHost != null && !ReferenceEquals(_keeperHost, presenter)) lost = "presenter-rebuilt";
            else if (_keeper.TornDown) lost = "hardware-teardown";
            else
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now >= _keeperHealthDueQpc)
                {
                    _keeperHealthDueQpc = now + System.Diagnostics.Stopwatch.Frequency;
                    int removed = _keeper.DeviceRemovedReason;
                    if (removed != 0) lost = "device-removed hr=0x" + ((uint)removed).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            if (lost != null)
            {
                FluentGpu.Media.ProtectedVideoTrace.EnsureProbe();
                FluentGpu.Media.ProtectedVideoTrace.Stamp("keeper.lost reason=" + lost);
                if (lost == "presenter-rebuilt") _keeperHost = null;   // the old tree is gone; the old keeper re-attaches meanwhile
                StartKeeperBuild("rebuild:" + lost);
                if (_keeperOff) return;
            }
        }
        if (_keeper == null)
        {
            if (_keeperBuild == null) StartKeeperBuild(FluentGpu.Media.ProtectedKeeperPolicy.WhyWanted(_keeperAdapterKey));
            return;
        }

        // Attach: the main window's first present must have bound its UI target (the keeper sits under it).
        if (_keeperHost == null && presenter.CanAttachSurfaces)
        {
            FluentGpu.Media.ProtectedVideoTrace.EnsureProbe();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!presenter.SetKeeperSurface(_keeper.SurfaceHandle))
            {
                KeeperOff("the main window's video tree refused the keeper's surface");
                return;
            }
            _keeperHost = presenter;
            long dur = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            FluentGpu.Media.ProtectedVideoTrace.Stamp("keeper.attach handle=0x" + _keeper.SurfaceHandle.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                + " hwnd=0x" + ((nint)primary.Hwnd).ToString("X", System.Globalization.CultureInfo.InvariantCulture), 0, 0, dur);
            FluentGpu.Media.ProtectedVideoTrace.RequestCommitStamp();   // the commit right after publishes it to DWM: the one entry hitch
        }
    }

    private static void ProtectedCompositionKeeperResult(KeeperBuild b, out bool done, out FluentGpu.Pal.Windows.ProtectedCompositionKeeper? keeper, out string failure)
    {
        lock (b)
        {
            done = b.Done;
            keeper = b.Keeper;
            failure = b.Failure;
            if (done) b.Keeper = null;   // handed over: the build no longer owns it
        }
    }

    // Render thread: queue one keeper build on the pool (D3D11 device + protected swap chain + two presents: tens of ms, never on
    // the render thread).
    private void StartKeeperBuild(string why)
    {
        if (++_keeperBuilds > KeeperMaxBuilds) { KeeperOff("built " + KeeperMaxBuilds + " times this run"); return; }
        var b = new KeeperBuild { Why = why };
        _keeperBuild = b;
        long luid = GpuAdapterInfo.CurrentAdapterLuid;
        FluentGpu.Media.ProtectedVideoTrace.EnsureProbe();   // the probe measures the attach that follows
        System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ => BuildKeeper(b, luid), null);
    }

    // Pool thread.
    private static void BuildKeeper(KeeperBuild b, long luid)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        FluentGpu.Pal.Windows.ProtectedCompositionKeeper? k;
        string failure;
        try { k = FluentGpu.Pal.Windows.ProtectedCompositionKeeper.TryCreate(luid, out failure); }
        catch (System.Exception e) { k = null; failure = e.GetType().Name + ": " + e.Message; }
        long dur = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (k != null)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            FluentGpu.Media.ProtectedVideoTrace.Stamp("keeper.create why=" + b.Why + " adapter=" + (k.AdapterKey >> 32).ToString("X4", ci) + ":"
                + (k.AdapterKey & 0xFFFFFFFF).ToString("X4", ci) + " handle=0x" + k.SurfaceHandle.ToString("X", ci), 0, 0, dur);
        }
        bool abandoned;
        lock (b)
        {
            abandoned = b.Abandoned;
            if (!abandoned) { b.Keeper = k; b.Failure = failure; }
            b.Done = true;
        }
        if (abandoned) { k?.Dispose(); return; }
        try { FluentGpu.Media.ProtectedKeeperPolicy.Wake?.Invoke(); } catch { }   // the next render turn attaches it
    }

    private void KeeperOff(string why)
    {
        _keeperOff = true;
        Diag.Line("[video.surface] [keeper] off for this run: " + why + " (protected video enters and leaves DWM's protected composition as before)");
        FluentGpu.Media.ProtectedVideoTrace.Stamp("keeper.off");
    }

    // Render thread, a display change: the keeper (and a replaced one, and a build in flight) leaves every tree that shows it on THIS
    // commit; the swap chains are released on the next tick. Nothing to do when no keeper exists.
    private void DropKeeperForDisplayChange()
    {
        if (_keeper == null && _keeperRetired == null && _keeperBuild == null) return;
        if (_keeperBuild is { } b)
        {
            FluentGpu.Pal.Windows.ProtectedCompositionKeeper? orphan = null;
            lock (b)
            {
                b.Abandoned = true;
                if (b.Done) { orphan = b.Keeper; b.Keeper = null; }
            }
            if (orphan != null) (_keeperDropped ??= new()).Add(orphan);
            _keeperBuild = null;
        }
        _keeperHost?.ClearKeeperSurface();
        if (_primarySwapchain is { Composited: true } primary
            && GetVideoPresenter(primary) is FluentGpu.Pal.Windows.CompositionVideoPresenter p && !ReferenceEquals(p, _keeperHost))
            p.ClearKeeperSurface();
        _keeperHost = null;
        if (_keeper != null) { (_keeperDropped ??= new()).Add(_keeper); _keeper = null; }
        if (_keeperRetired != null) { (_keeperDropped ??= new()).Add(_keeperRetired); _keeperRetired = null; }
        FluentGpu.Media.ProtectedVideoTrace.EnsureProbe();
        FluentGpu.Media.ProtectedVideoTrace.Stamp("keeper.drop reason=display-change");
        Diag.Line("[video.surface] [keeper] a display changed: out of DWM's tree until the displays are quiet for " + KeeperDisplayQuietMs + " ms");
    }

    private static void DestroyKeeper(FluentGpu.Pal.Windows.ProtectedCompositionKeeper k, string why)
    {
        FluentGpu.Media.ProtectedVideoTrace.Stamp("keeper.destroy reason=" + why);
        k.Dispose();
    }

    // Device teardown, after every presenter released its wrap of the keeper's surface (process exit: the one departure hitch).
    private void ReleaseProtectedKeeper()
    {
        if (_keeperBuild is { } b)
        {
            FluentGpu.Pal.Windows.ProtectedCompositionKeeper? orphan = null;
            lock (b)
            {
                b.Abandoned = true;
                if (b.Done) { orphan = b.Keeper; b.Keeper = null; }
            }
            orphan?.Dispose();
            _keeperBuild = null;
        }
        if (_keeperRetired != null) { DestroyKeeper(_keeperRetired, "replaced"); _keeperRetired = null; }
        if (_keeperDropped != null) { foreach (var k in _keeperDropped) DestroyKeeper(k, "display-change"); _keeperDropped = null; }
        if (_keeper != null) { DestroyKeeper(_keeper, "device-dispose"); _keeper = null; }
        _keeperHost = null;
    }

    // After every presenter released its visuals (Dispose). The render loop has been joined by then, so the queue's thread is gone.
    private void ReleaseVideoCompositor()
    {
        ReleaseProtectedKeeper();
        if (_wucDesktop != null) { _wucDesktop->Release(); _wucDesktop = null; }
        if (_wucInterop != null) { _wucInterop->Release(); _wucInterop = null; }
        if (_wuc != null) { _wuc->Release(); _wuc = null; }
        if (_wucController != null) { _wucController->Release(); _wucController = null; }
        if (_wucQueue != null) { _wucQueue->Release(); _wucQueue = null; }
        _wucCommitDue = false;
    }
}
