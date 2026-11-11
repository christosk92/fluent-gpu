using FluentGpu.Foundation;
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
        HRESULT hr = _wucController->Commit();
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

    // After every presenter released its visuals (Dispose). The render loop has been joined by then, so the queue's thread is gone.
    private void ReleaseVideoCompositor()
    {
        if (_wucDesktop != null) { _wucDesktop->Release(); _wucDesktop = null; }
        if (_wucInterop != null) { _wucInterop->Release(); _wucInterop = null; }
        if (_wuc != null) { _wuc->Release(); _wuc = null; }
        if (_wucController != null) { _wucController->Release(); _wucController = null; }
        if (_wucQueue != null) { _wucQueue->Release(); _wucQueue = null; }
        _wucCommitDue = false;
    }
}
