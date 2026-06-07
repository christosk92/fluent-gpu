using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// Eases per-node hover/press progress (phase 7), so the recorder can cross-fade Fill/Border for the WinUI ~83ms brush
/// transition instead of an instant flag switch. State lives in the sparse <see cref="InteractionAnim"/> side-table; only
/// nodes currently transitioning are ticked. Zero work / zero alloc on a steady frame (<see cref="HasActive"/> == false).
/// </summary>
public sealed class InteractionAnimator
{
    private readonly SceneStore _scene;
    private readonly List<NodeHandle> _active = new();
    private readonly HashSet<int> _member = new();

    public InteractionAnimator(SceneStore scene) => _scene = scene;
    public bool HasActive => _active.Count > 0;

    public void SetHover(NodeHandle node, bool on)
    {
        if (node.IsNull || !_scene.IsLive(node)) return;
        ref InteractionAnim ia = ref _scene.InteractRef(node);
        ia.HoverTarget = on ? 1f : 0f;
        Arm(node);
    }

    public void SetPress(NodeHandle node, bool on)
    {
        if (node.IsNull || !_scene.IsLive(node)) return;
        ref InteractionAnim ia = ref _scene.InteractRef(node);
        ia.PressTarget = on ? 1f : 0f;
        Arm(node);
    }

    private void Arm(NodeHandle node)
    {
        if (_member.Add((int)node.Raw.Index)) _active.Add(node);
    }

    // Time-constant of the exponential approach. tau ≈ 15ms settles to the 0.004 threshold in ~83ms — i.e. WinUI's
    // ControlFasterAnimationDuration (0:0:0.083), which drives the ContentPresenter.BackgroundTransition for both the
    // PointerOver and Pressed brush cross-fades. (Frame-rate-independent; an exact fixed-duration ease is a later option.)
    private const float TauMs = 15f;

    public void Tick(float dtMs)
    {
        if (_active.Count == 0) return;
        // exponential approach toward the target; same 83ms feel for hover and press (WinUI uses one BackgroundTransition).
        float k = 1f - MathF.Exp(-dtMs / TauMs);
        float kh = k, kp = k;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            NodeHandle h = _active[i];
            if (!_scene.IsLive(h)) { Drop(i, h); continue; }
            ref InteractionAnim ia = ref _scene.InteractRef(h);
            ia.HoverT += (ia.HoverTarget - ia.HoverT) * kh;
            ia.PressT += (ia.PressTarget - ia.PressT) * kp;
            _scene.Mark(h, NodeFlags.PaintDirty);
            if (MathF.Abs(ia.HoverTarget - ia.HoverT) < 0.004f && MathF.Abs(ia.PressTarget - ia.PressT) < 0.004f)
            {
                ia.HoverT = ia.HoverTarget; ia.PressT = ia.PressTarget;
                Drop(i, h);
            }
        }
    }

    private void Drop(int i, NodeHandle h)
    {
        _member.Remove((int)h.Raw.Index);
        _active.RemoveAt(i);
    }
}
