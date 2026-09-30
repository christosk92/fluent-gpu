using FluentGpu.Foundation;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Scene;

/// <summary>Render-posed scroll offsets (scroll rework §5): the render thread's <see cref="ScrollPoser"/> writes one
/// row per covered viewport every tick (through the host's pose sink); the recorder reads them for the scrollbar
/// thumb, the auto edge fade and the edge cues so chrome never lags the content it decorates. A fixed table of at most
/// <see cref="PlanSlots.Capacity"/> rows — the render thread never grows it.</summary>
public sealed partial class SceneRecordingSnapshot
{
    /// <summary>The captured handle at node index <paramref name="idx"/> (Null when the index is not captured).</summary>
    public NodeHandle HandleAt(int idx) => (uint)idx < (uint)_handles.Length ? _handles[idx] : default;

    private struct PosedScrollRow { public int Node; public double Shown; }
    private readonly PosedScrollRow[] _posedScroll = new PosedScrollRow[PlanSlots.Capacity];
    private int _posedScrollCount;

    /// <summary>Starts a render tick's posed table (called from <c>BeginCompositorOverlay</c>).</summary>
    internal void BeginPosedScroll() => _posedScrollCount = 0;

    /// <summary>Records the poser's shown offset for viewport node <paramref name="vpNode"/> this tick.</summary>
    internal void SetPosedOffset(int vpNode, double shown)
    {
        for (int i = 0; i < _posedScrollCount; i++)
            if (_posedScroll[i].Node == vpNode) { _posedScroll[i].Shown = shown; return; }
        if (_posedScrollCount == _posedScroll.Length) return;
        _posedScroll[_posedScrollCount++] = new PosedScrollRow { Node = vpNode, Shown = shown };
    }

    /// <summary>The offset the recorder must draw chrome against: the poser's while one was posed this tick.</summary>
    public bool TryGetPosedOffset(NodeHandle vp, out double shown)
    {
        int node = (int)vp.Raw.Index;
        for (int i = 0; i < _posedScrollCount; i++)
        {
            if (_posedScroll[i].Node != node) continue;
            shown = _posedScroll[i].Shown;
            return true;
        }
        shown = 0.0;
        return false;
    }
}
