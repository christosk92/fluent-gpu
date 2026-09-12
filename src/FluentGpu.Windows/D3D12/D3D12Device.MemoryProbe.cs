using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

public sealed unsafe partial class D3D12Device
{
    // WindowsApp is already a friend assembly. No public RHI seam, environment flag, timer or production trim.
    // The observer runs synchronously on this diagnostic device's sole owner, only at cold startup cutpoints.
    private Action<string>? _memoryProbeObserver;
    private int _memoryProbeThread;

    internal void SetMemoryProbeObserver(Action<string> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (_device != null || _memoryProbeObserver is not null)
            throw new InvalidOperationException("Attach the memory probe once, before creating the native device.");
        _memoryProbeThread = Environment.CurrentManagedThreadId;
        _memoryProbeObserver = observer;
    }

    private void AssertMemoryProbeOwner()
    {
        if (_memoryProbeObserver is null || _memoryProbeThread != Environment.CurrentManagedThreadId || _signalDeviceLostInsteadOfThrow)
            throw new InvalidOperationException("Memory probe operations require the standalone device's sole owner thread.");
    }

    internal static (long bytes, int count) MemoryProbeTrackedTotals => D3D12MemoryDiagnostics.LiveTotals();

    // Existing renderer counters, read only AFTER submit by the standalone owner. These are not an exhaustive
    // native D3D call census: runs can split, multiple instances batch, and compositor passes bind independently.
    internal MemoryProbeWorkloadCounters MemoryProbeWorkload
    {
        get
        {
            AssertMemoryProbeOwner();
            return new(_lastRepaintRoute, _lastReplayRectCount, _lastRepaintFullReason,
                _frameSegments, _frameRuns, _frameClipOps, _frameLayerOps, _framePipeBinds, _frameScissorSets,
                _frameRectCount, _frameGlyphInstanceCount, _frameImageCount, _frameImageSkipped,
                _frameStencilClips, _opacity?.OpacityGroupsThisFrame ?? 0, _opacity?.BoundedOpacityGroupsThisFrame ?? 0,
                _opacity?.BlurGroupsThisFrame ?? 0,
                _opacity?.EdgeFadeGroupsThisFrame ?? 0, DroppedInstanceCount(), _frameTextCoverFlushes,
                _blurCacheHit, _blurCacheMiss, _opacity?.RtCreatesThisFrame ?? 0, _pathPipe?.DrawsThisFrame ?? 0);
        }
    }

    internal readonly record struct MemoryProbeWorkloadCounters(RepaintRoute Route, int ReplayRects,
        RepaintFullReason FullReason, int Segments, int Runs, int ClipOps, int LayerOps, int PipeBinds,
        int Scissors, int Rects, int GlyphInstances, int Images, int ImagesSkipped, int StencilClips,
        int OpacityGroups, int BoundedOpacityGroups, int BlurGroups, int EdgeFadeGroups, int DroppedInstances, int TextCoverFlushes,
        int BlurCacheHits, int BlurCacheMisses, int OpacityRtCreates, int PathDraws);

    internal MemoryProbeSampler? CreateMemoryProbeSampler()
    {
        AssertMemoryProbeOwner();
        EnsureAdapter3();
        return _adapter3 == null ? null : new MemoryProbeSampler(_adapter3);
    }

    /// <summary>All submitted use retires, not merely this swapchain's last frame. Call only between closed submissions.</summary>
    internal void MemoryProbeDrain()
    {
        AssertMemoryProbeOwner();
        if (_queue == null || _fence == null) throw new InvalidOperationException("Native submission objects are not initialized.");
        WaitForGpu();
    }

    /// <summary>Control for the list Reset required to detach a bank before replacement. Does not reset/replace its allocator.</summary>
    internal void MemoryProbeResetCommandList(int bank)
    {
        if ((uint)bank >= FRAME_COUNT) throw new ArgumentOutOfRangeException(nameof(bank));
        MemoryProbeDrain();
        Check(_cmdList->Reset(_allocators[bank], null), "MemoryProbe.CommandList.Reset(control)");
        Check(_cmdList->Close(), "MemoryProbe.CommandList.Close(control)");
        InvalidateCmdState();
    }

    /// <summary>
    /// Diagnostic intervention, not a trim policy. A successful full-queue fence precedes replacement; resetting the
    /// CLOSED list onto the new bank drops its old recording references before releasing the old allocator.
    /// Compare against MemoryProbeResetCommandList, since list reset is a necessary additional operation.
    /// </summary>
    internal void MemoryProbeReplaceAllocator(int bank)
    {
        if ((uint)bank >= FRAME_COUNT) throw new ArgumentOutOfRangeException(nameof(bank));
        MemoryProbeDrain();
        ID3D12CommandAllocator* replacement = null;
        Check(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            __uuidof<ID3D12CommandAllocator>(), (void**)&replacement), "MemoryProbe.CreateCommandAllocator");
        var reset = _cmdList->Reset(replacement, null);
        if ((int)reset < 0)
        {
            replacement->Release(); // The old bank/list remain owned; abort the experiment rather than losing them.
            Check(reset, "MemoryProbe.CommandList.Reset(replacement)");
            return;
        }
        var retired = _allocators[bank];
        _allocators[bank] = replacement;
        SetName(replacement, $"FluentGpu.MemoryProbe.CommandAllocator[{bank}]");
        retired->Release();
        Check(_cmdList->Close(), "MemoryProbe.CommandList.Close(replacement)");
        InvalidateCmdState();
        // Fence numbering, per-bank fences, swapchain depth and upload-bank indices remain unchanged. The next
        // production submission resets this already-retired bank normally; no resources or instances are trimmed.
    }

    internal void MemoryProbeReplaceCommandList()
    {
        MemoryProbeDrain();
        ID3D12GraphicsCommandList* replacement = null;
        Check(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            _allocators[0], null, __uuidof<ID3D12GraphicsCommandList>(), (void**)&replacement), "MemoryProbe.CreateCommandList");
        var close = replacement->Close();
        if ((int)close < 0)
        {
            replacement->Release();
            Check(close, "MemoryProbe.CommandList.Close(new)");
            return;
        }
        var retired = _cmdList;
        _cmdList = replacement;
        SetName(replacement, "FluentGpu.MemoryProbe.CommandList");
        retired->Release();
        InvalidateCmdState();
    }

    internal readonly record struct MemoryProbeSample(
        bool LocalValid, ulong LocalUsage, ulong LocalBudget,
        bool NonLocalValid, ulong NonLocalUsage, ulong NonLocalBudget);

    /// <summary>
    /// Fresh per-process DXGI queries, including during no-submit idle and after device teardown. Retains ONLY one
    /// reference to the device-selected adapter (not the D3D12 device/queue). This deliberate observer lifetime must
    /// be stated in results; dispose it last. Pointers never leave this owner-thread-confined diagnostic object.
    /// </summary>
    internal sealed class MemoryProbeSampler : IDisposable
    {
        private IDXGIAdapter3* _adapter;
        private readonly int _thread = Environment.CurrentManagedThreadId;

        internal MemoryProbeSampler(IDXGIAdapter3* adapter)
        {
            _adapter = adapter;
            adapter->AddRef();
        }

        internal MemoryProbeSample Read()
        {
            AssertOwner();
            if (_adapter == null) throw new ObjectDisposedException(nameof(MemoryProbeSampler));
            DXGI_QUERY_VIDEO_MEMORY_INFO local = default, nonLocal = default;
            bool localValid = (int)_adapter->QueryVideoMemoryInfo(0,
                DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local) >= 0;
            bool nonLocalValid = (int)_adapter->QueryVideoMemoryInfo(0,
                DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &nonLocal) >= 0;
            return new(localValid, local.CurrentUsage, local.Budget, nonLocalValid, nonLocal.CurrentUsage, nonLocal.Budget);
        }

        public void Dispose()
        {
            AssertOwner();
            if (_adapter == null) return;
            _adapter->Release();
            _adapter = null;
        }

        private void AssertOwner()
        {
            if (_thread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("The DXGI memory observer must stay on its creating thread.");
        }
    }
}
