using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using Gen = FluentGpu.Interop.Generated;

using ColorF = FluentGpu.Foundation.ColorF;

namespace FluentGpu.Rhi.D3D12;

// ── BACKDROP PLANE (`--fg backdrop-plane`, a measurement SPIKE, default off) ──────────────────────────────────────────────
//
// The stage's ambient backdrop is a top-level low-resolution Direct item (LowResDown > 1) that changes every turn: its
// partial-present signature is `fresh`, so every frame takes the whole-frame route. Under the switch, when that item is the
// first non-hidden top-level item and covers the whole window, its low-res surface is copied every time it changes into a
// small opaque composition swapchain on its own DComp visual at the BOTTOM of the root (scaled d x d, LINEAR), and the main
// back-buffer composite writes TRANSPARENT over the item's region instead of upsampling it (a constant partial-present
// signature: the backdrop no longer dirties the UI frame). Acrylic mini-composites and group prepares keep sampling the real
// low-res surface (_planeMainPass is false there). The plane engages for the UI only after it has been in the tree with
// content for 3 turns (no flash), and stands down for the UI in the same turn the candidate disappears.
// Not handled (a spike): make-before-break on a size change, window capture / dissolve snapshots of the UI back buffer
// (they see the transparent hole), HDR, multi-window.
public sealed unsafe partial class D3D12Device
{
    private const ulong PlaneSignature = 0xBAC6_D50F_0000_0001UL;
    private const int PlaneWarmTurns = 3;
    private const int PlaneRemoveAfterTurns = 3;
    private const int PlaneCensusTurns = 600;

    private int _planeItem = -1;          // the item the main composite writes transparent this turn (-1 = none: draw normally)
    private bool _planeMainPass;          // true only while the main back-buffer composite (or its present-validate shadow) draws
    private IDXGISwapChain3* _planeSc;
    private readonly ID3D12Resource*[] _planeBuf = new ID3D12Resource*[(int)FRAME_COUNT];
    private IDCompositionVisual* _planeVisual;
    private IDCompositionVisual* _planeRoot;   // the root the visual is in (null = not in the tree)
    private int _planeTexW, _planeTexH, _planeL = int.MinValue, _planeT = int.MinValue, _planeD;
    private bool _planeFailed;            // creation failed: logged once, never engages until a device recovery
    private bool _planeHasContent;        // a plane present succeeded since the visual (re)entered the tree
    private bool _planeCopyDue;           // the next fed turn copies regardless of the surface key
    private int _planeSurface = -1;
    private ulong _planeKey;
    private int _planeWarm, _planeAbsent;
    private bool _planePresentPending;
    private string? _planeReason;
    private long _planeTurns, _planeEngagedTurns, _planeFed, _planePresents, _planePresentFail, _planeDmgSkipped;
    private uint _planePresentFailHr, _planeCommitFailHr, _planeMediaFailHr;

    private static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Every composite under the switch, after PrepareTop and before PpComputeDirty: find the candidate, keep the
    /// plane's resources, feed it, and decide whether the UI writes the item transparent this turn.</summary>
    private void PlaneDecide(in CompositeFrame frame, D3D12Swapchain sc)
    {
        if (!EngineSwitches.BackdropPlane) return;
        _planeMainPass = false;
        int prev = _planeItem;
        _planeItem = -1;
        _planeTurns++;
        if (_planeTurns % PlaneCensusTurns == 0) PlaneCensus();

        string? reason = PlaneCandidate(in frame, sc, out int j, out CompositeKind firstKind);
        int d = 0, tw = 0, th = 0;
        PixelRect reg = default;
        if (reason is null)
        {
            d = frame.Items[j].LowResDown;
            reg = _itemRegion[j];
            tw = (reg.Right - reg.Left) / d; th = (reg.Bottom - reg.Top) / d;
            if (_planeSc != null && (tw != _planeTexW || th != _planeTexH || (_planeRoot != null && _planeRoot != sc.DcompRoot)))
            {
                Console.Error.WriteLine("[backdrop.plane] recreate texels=" + Inv(_planeTexW) + "x" + Inv(_planeTexH) + " -> " + Inv(tw) + "x" + Inv(th)
                    + " turn=" + Inv(_compositeTurn));
                if (prev >= 0) { PlaneLogDisengage("resize"); prev = -1; }
                PlaneDrop(sc, waitGpu: true);
            }
            if (_planeSc == null && !PlaneCreate(tw, th)) reason = "create-failed";
        }

        if (reason is not null)
        {
            _planeWarm = 0;
            if (prev >= 0) PlaneLogDisengage(reason);
            if (!string.Equals(reason, _planeReason, StringComparison.Ordinal))
            {
                _planeReason = reason;
                Console.Error.WriteLine("[backdrop.plane] no-candidate reason=" + reason + " kind=" + firstKind + " turn=" + Inv(_compositeTurn));
            }
            if (_planeRoot != null && ++_planeAbsent >= PlaneRemoveAfterTurns) PlaneDetach(sc);
            return;
        }
        _planeReason = null;
        _planeAbsent = 0;

        if (_planeRoot == null && !PlaneAttach(sc)) return;
        if (reg.Left != _planeL || reg.Top != _planeT || d != _planeD) PlanePlace(reg.Left, reg.Top, d);
        PlaneFeed(j, tw, th);

        if (_planeWarm >= PlaneWarmTurns)
        {
            _planeItem = j;
            _planeEngagedTurns++;
            if (prev < 0)
            {
                ref readonly CompositeItem it = ref frame.Items[j];
                Console.Error.WriteLine("[backdrop.plane] engage item=" + Inv(j) + " slice=" + Inv(it.SliceId) + " region=" + Inv(reg.Left) + "," + Inv(reg.Top)
                    + " " + Inv(reg.Right - reg.Left) + "x" + Inv(reg.Bottom - reg.Top) + " texels=" + Inv(tw) + "x" + Inv(th) + " d=" + Inv(d)
                    + " blend=" + Inv(it.BlendCopy) + " turn=" + Inv(_compositeTurn));
            }
        }
        if (_planeHasContent && _planeWarm < int.MaxValue) _planeWarm++;
    }

    private void PlaneLogDisengage(string reason)
        => Console.Error.WriteLine("[backdrop.plane] disengage turn=" + Inv(_compositeTurn) + " reason=" + reason);

    /// <summary>The first non-hidden top-level item (EraseVideoHole skipped), and the first condition it fails (null = it qualifies).</summary>
    private string? PlaneCandidate(in CompositeFrame frame, D3D12Swapchain sc, out int j, out CompositeKind firstKind)
    {
        j = -1; firstKind = default;
        if (!sc.Composited || sc.DcompBindPending || sc.DcompRoot == null) return "target-not-composited";
        if (_dcomp == null) return "no-dcomp";
        if ((_frameKnockouts & (GpuKnockouts.ForceFullDirect | GpuKnockouts.ClearOnly | GpuKnockouts.NoOcclusion | GpuKnockouts.FullPresent)) != 0) return "knockout";
        if (_planeFailed) return "create-failed";
        ReadOnlySpan<CompositeItem> items = frame.Items;
        int n = items.Length;
        int k = 0;
        while (k < n && (Hidden(k, n) || items[k].Kind == CompositeKind.EraseVideoHole))
            k = items[k].Kind == CompositeKind.Group ? Math.Min(n, k + 1 + items[k].GroupCount) : k + 1;
        if (k >= n) return "no-items";
        ref readonly CompositeItem it = ref items[k];
        firstKind = it.Kind;
        if (it.Kind != CompositeKind.Direct) return "kind";
        if (it.LowResDown <= 1) return "not-lowres";
        if (it.IsFeedback) return "feedback";
        if (it.BlurSigma > 0f) return "blur";
        if (it.Alpha < 1f) return "alpha";
        if (it.RoundClip.W > 0f) return "round-clip";
        if (!it.Feather.IsNone || !it.Feather2.IsNone) return "feather";
        // the low-res route samples source-over for BlendCopy 0 and BlendCopyWrite alike (DrawSurfaceItem): only SCREEN differs
        if (it.BlendCopy == CompositeItem.BlendScreen) return "blend-screen";
        if (it.SubPxX != 0f || it.SubPxY != 0f) return "subpx";
        if (_itemSurface[k] < 0) return "no-surface";
        if (_itemChunkCount[k] != 0) return "chunks";
        if (_itemDown[k] != it.LowResDown) return "down-mismatch";
        int w = (int)_w, h = (int)_h;
        PixelRect reg = _itemRegion[k];
        if (reg.Left > 0 || reg.Top > 0 || reg.Right < w || reg.Bottom < h) return "region-not-window";
        PixelRect occ = _occRect[k];
        if (occ.Left > 0 || occ.Top > 0 || occ.Right < w || occ.Bottom < h) return "not-opaque-window";
        if (!IsUnbounded(it.Clip) && (it.Clip.X > 0f || it.Clip.Y > 0f || it.Clip.Right < w || it.Clip.Bottom < h)) return "clip";
        j = k;
        return null;
    }

    /// <summary>The plane's composition swapchain (the low-res texel size, opaque, FLIP_SEQUENTIAL) and its visual. Non-throwing:
    /// a failure logs once and latches <see cref="_planeFailed"/>.</summary>
    private bool PlaneCreate(int w, int h)
    {
        if (_planeFailed) return false;
        if (_device == null || _factory == null || _queue == null || _dcomp == null || w <= 0 || h <= 0) return false;
        IDXGISwapChain1* sc1 = null;
        IDXGISwapChain3* sc3 = null;
        IDCompositionVisual* visual = null;
        try
        {
            DXGI_SWAP_CHAIN_DESC1 sd = default;
            sd.Width = (uint)w;
            sd.Height = (uint)h;
            sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            sd.SampleDesc.Count = 1;
            sd.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
            sd.BufferCount = FRAME_COUNT;
            sd.Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH;
            sd.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            sd.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;
            if (PlaneFailed(_factory->CreateSwapChainForComposition((IUnknown*)_queue, &sd, null, &sc1), "CreateSwapChainForComposition")) return false;
            if (PlaneFailed(sc1->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)&sc3), "QI IDXGISwapChain3")) return false;
            for (uint b = 0; b < FRAME_COUNT; b++)
            {
                ID3D12Resource* buf = null;
                if (PlaneFailed(sc3->GetBuffer(b, __uuidof<ID3D12Resource>(), (void**)&buf), "GetBuffer"))
                {
                    PlaneReleaseBuffers();
                    return false;
                }
                _planeBuf[b] = buf;
            }
            if (PlaneFailed(_dcomp->CreateVisual(&visual), "CreateVisual")) { PlaneReleaseBuffers(); return false; }
            if (PlaneFailed(visual->SetContent((IUnknown*)sc3), "SetContent")) { PlaneReleaseBuffers(); return false; }
            // quality-only properties: a failure is logged and the plane still works
            PlaneOk(visual->SetBitmapInterpolationMode(DCOMPOSITION_BITMAP_INTERPOLATION_MODE.DCOMPOSITION_BITMAP_INTERPOLATION_MODE_LINEAR), "SetBitmapInterpolationMode(LINEAR)");
            PlaneOk(visual->SetBorderMode(DCOMPOSITION_BORDER_MODE.DCOMPOSITION_BORDER_MODE_HARD), "SetBorderMode(HARD)");
            _planeSc = sc3; sc3 = null;
            _planeVisual = visual; visual = null;
            _planeTexW = w; _planeTexH = h;
            _planeL = int.MinValue; _planeT = int.MinValue; _planeD = 0;
            _planeCopyDue = true;
            _planeHasContent = false;
            _planeWarm = 0;
            Console.Error.WriteLine("[backdrop.plane] create texels=" + Inv(w) + "x" + Inv(h) + " buffers=" + Inv(FRAME_COUNT)
                + " format=BGRA8 swap=FLIP_SEQUENTIAL alpha=IGNORE turn=" + Inv(_compositeTurn));
            return true;
        }
        finally
        {
            if (visual != null) visual->Release();
            if (sc3 != null) sc3->Release();
            if (sc1 != null) sc1->Release();
        }
    }

    private bool PlaneFailed(HRESULT hr, string what)
    {
        if ((int)hr >= 0) return false;
        _planeFailed = true;
        Console.Error.WriteLine("[backdrop.plane] " + what + " failed: 0x" + ((uint)hr).ToString("X8", CultureInfo.InvariantCulture) + "; the plane stays off");
        return true;
    }

    private static void PlaneOk(HRESULT hr, string what)
    {
        if ((int)hr < 0) Console.Error.WriteLine("[backdrop.plane] " + what + " failed: 0x" + ((uint)hr).ToString("X8", CultureInfo.InvariantCulture));
    }

    /// <summary>The visual at the very BOTTOM of the root's children (below the UI), then Commit. The video is not in this tree: its
    /// own window target sits beneath this whole DComp target, so while engaged this opaque plane hides any video under it.</summary>
    private bool PlaneAttach(D3D12Swapchain sc)
    {
        if (_planeVisual == null || sc.DcompRoot == null) return false;
        // AddVisual with a NULL reference: insertAbove=TRUE places the visual BELOW all its siblings (insertAbove=FALSE would
        // place it on top).
        HRESULT hr = sc.DcompRoot->AddVisual(_planeVisual, BOOL.TRUE, null);
        if ((int)hr < 0)
        {
            PlaneFailed(hr, "Root.AddVisual(plane, bottom)");
            return false;
        }
        _planeRoot = sc.DcompRoot;
        _planeHasContent = false;
        _planeWarm = 0;
        _planeCopyDue = true;   // the swapchain may hold a stale frame from before it left the tree
        _planeL = int.MinValue;   // re-place (and commit) below
        PlaneCommit();
        Console.Error.WriteLine("[backdrop.plane] attach turn=" + Inv(_compositeTurn));
        return true;
    }

    private void PlaneDetach(D3D12Swapchain sc)
    {
        if (_planeRoot != null && _planeRoot == sc.DcompRoot && _planeVisual != null)
        {
            sc.DcompRoot->RemoveVisual(_planeVisual);
            PlaneCommit();
        }
        _planeRoot = null;
        _planeHasContent = false;
        _planeWarm = 0;
        _planeAbsent = 0;
        Console.Error.WriteLine("[backdrop.plane] detach turn=" + Inv(_compositeTurn));
    }

    /// <summary>Map the w x h bitmap onto the window region: scale (d, d), then translate to its top-left.</summary>
    private void PlanePlace(int l, int t, int d)
    {
        if (_planeVisual == null) return;
        D2D_MATRIX_3X2_F m = default;
        m.m11 = d; m.m22 = d;
        m.dx = l; m.dy = t;
        PlaneOk(_planeVisual->SetTransform(&m), "SetTransform");
        _planeL = l; _planeT = t; _planeD = d;
        PlaneCommit();
    }

    private void PlaneCommit()
    {
        if (_dcomp == null) return;
        HRESULT hr = _dcomp->Commit();
        if ((int)hr < 0 && _planeCommitFailHr != (uint)hr)
        {
            _planeCommitFailHr = (uint)hr;
            Console.Error.WriteLine("[backdrop.plane] DComp Commit failed: 0x" + ((uint)hr).ToString("X8", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Copy the item's low-res texels into the plane's current back buffer when they changed (inside the composite's
    /// command list, outside any render pass); the present follows in the primary target's present (PlanePresent).</summary>
    private void PlaneFeed(int j, int w, int h)
    {
        int s = _itemSurface[j];
        bool need = _planeCopyDue || _itemRendered[j] || _planeSurface != s || _planeKey != _itemKey[j];
        if (!need || _planeSc == null) return;
        if (_surfaces!.ScratchW(s) < w || _surfaces.ScratchH(s) < h) return;   // cannot happen: the scratch holds the region
        uint bi = _planeSc->GetCurrentBackBufferIndex();
        if (bi >= FRAME_COUNT) return;
        ID3D12Resource* dst = _planeBuf[bi];
        if (dst == null) return;
        EndPassIfOpen();
        D3D12_RESOURCE_STATES srcState = _surfaces.ScratchState(s);
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        Barrier(dst, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
        D3D12_BOX box = default; box.right = (uint)w; box.bottom = (uint)h; box.back = 1;
        D3D12_TEXTURE_COPY_LOCATION dl = default;
        dl.pResource = dst; dl.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dl.Anonymous.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION sl = default;
        sl.pResource = _surfaces.ScratchResource(s); sl.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; sl.Anonymous.SubresourceIndex = 0;
        _cmdList->CopyTextureRegion(&dl, 0, 0, 0, &sl, &box);
        Rec(RecordedOp.CopyTexture, (uint)(nint)dst, (uint)(w * h));
        Barrier(dst, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        ScratchBarrier(s, srcState);
        _planeSurface = s;
        _planeKey = _itemKey[j];
        _planeCopyDue = false;
        _planePresentPending = true;
        _planeFed++;
    }

    /// <summary>The primary target's present, before its own Present (and before every stand-down / hold): present the
    /// plane's freshly copied buffer at sync interval 0. The composite's list was executed by EndRecording already.</summary>
    private void PlanePresent()
    {
        _planePresentPending = false;
        if (_planeSc == null) return;
        HRESULT hr = (HRESULT)Gen.IDXGISwapChainVtbl.Present(_planeSc, 0, 0);
        if ((int)hr < 0)
        {
            _planePresentFail++;
            _planeCopyDue = true;   // the written buffer was not shown: the next fed turn copies again
            if (_planePresentFailHr != (uint)hr)
            {
                _planePresentFailHr = (uint)hr;
                Console.Error.WriteLine("[backdrop.plane] Present failed: 0x" + ((uint)hr).ToString("X8", CultureInfo.InvariantCulture) + " turn=" + Inv(_compositeTurn));
            }
            return;
        }
        _planePresents++;
        if (_planeRoot != null) _planeHasContent = true;
    }

    private void PlaneCensus()
    {
        Console.Error.WriteLine("[backdrop.plane] census turns=" + Inv(_planeTurns) + " engaged=" + Inv(_planeEngagedTurns) + " fed=" + Inv(_planeFed)
            + " presents=" + Inv(_planePresents) + " presentFail=" + Inv(_planePresentFail) + " dmgSkipped=" + Inv(_planeDmgSkipped));
        if (_planeSc == null) return;
        IDXGISwapChainMedia* media = null;
        HRESULT hr = _planeSc->QueryInterface(__uuidof<IDXGISwapChainMedia>(), (void**)&media);
        if ((int)hr >= 0 && media != null)
        {
            DXGI_FRAME_STATISTICS_MEDIA st = default;
            hr = media->GetFrameStatisticsMedia(&st);
            media->Release();
            if ((int)hr >= 0)
            {
                Console.Error.WriteLine("[backdrop.plane] media compositionMode=" + Inv((int)st.CompositionMode) + " approvedPresentDuration=" + Inv(st.ApprovedPresentDuration)
                    + " presentCount=" + Inv(st.PresentCount));
                return;
            }
        }
        if (_planeMediaFailHr != (uint)hr)
        {
            _planeMediaFailHr = (uint)hr;
            Console.Error.WriteLine("[backdrop.plane] media stats unavailable: 0x" + ((uint)hr).ToString("X8", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The main composite's stand-in for the engaged item: TRANSPARENT copied over its region (the plane shows through).</summary>
    private void PlaneClearItem(in CompositeItem it, int i, int ox, int oy)
    {
        PixelRect r = _itemRegion[i];
        ItemScissor(in it, ox, oy);
        _compositor!.Begin(r.Left - ox, r.Top - oy, r.Right - ox, r.Bottom - oy);
        _compositor.Color(new ColorF(0f, 0f, 0f, 0f));
        _compositor.Draw(_cmdList, SliceCompositor.Pso.FillCopy, default);
    }

    private void PlaneReleaseBuffers()
    {
        for (int b = 0; b < _planeBuf.Length; b++)
            if (_planeBuf[b] != null) { _planeBuf[b]->Release(); _planeBuf[b] = null; }
    }

    /// <summary>Release the plane in a live run (a size change): out of the tree, GPU drained, everything released.</summary>
    private void PlaneDrop(D3D12Swapchain sc, bool waitGpu)
    {
        if (_planeRoot != null) PlaneDetach(sc);
        if (waitGpu)
        {
            try { WaitForGpu(); }
            catch (InvalidOperationException ex) { Console.Error.WriteLine("[backdrop.plane] GPU wait failed: " + ex.Message); }
        }
        PlaneReleaseAll();
    }

    private void PlaneReleaseAll()
    {
        if (_planeVisual != null) { _planeVisual->Release(); _planeVisual = null; }
        PlaneReleaseBuffers();
        if (_planeSc != null) { _planeSc->Release(); _planeSc = null; }
        _planeRoot = null;
        _planeItem = -1;
        _planeMainPass = false;
        _planeTexW = _planeTexH = 0;
        _planeL = _planeT = int.MinValue; _planeD = 0;
        _planeHasContent = false;
        _planePresentPending = false;
        _planeSurface = -1; _planeKey = 0;
        _planeWarm = 0; _planeAbsent = 0;
    }

    /// <summary>Device dispose / recovery (the DComp root is already gone, the GPU drained or lost): release everything and
    /// clear the failed latch so a recovered device gets a fresh attempt.</summary>
    private void ReleaseBackdropPlane()
    {
        PlaneReleaseAll();
        _planeFailed = false;
        _planeReason = null;
    }
}
