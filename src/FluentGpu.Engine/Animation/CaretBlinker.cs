using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// Drives the focused editor's caret blink (the <see cref="RepeatTicker"/> phase-7 idiom): while an editable text node
/// is focused, the <see cref="TextEditState.CaretVisible"/> bit toggles every half-period (Win32
/// <c>GetCaretBlinkTime</c> semantics — the host passes the OS value; headless uses the fixed 500ms default) and the
/// node is marked PaintDirty so the recorder re-emits. One focused editor at a time (focus is singular). The host
/// includes <see cref="HasActive"/> in its work gate so the loop keeps ticking at blink granularity while an editor is
/// focused — and goes fully idle the moment it blurs. Zero alloc per tick (writes a POD row + a flag bit).
/// </summary>
public sealed class CaretBlinker
{
    private readonly SceneStore _scene;
    private NodeHandle _node;
    private float _intervalMs = DefaultBlinkMs;
    private float _elapsed;

    /// <summary>Default half-period (caret-on / caret-off time) — the Win32 <c>GetCaretBlinkTime</c> default.</summary>
    public const float DefaultBlinkMs = 500f;

    public CaretBlinker(SceneStore scene) => _scene = scene;

    /// <summary>An editor is focused → the frame loop must keep ticking (at blink granularity).</summary>
    public bool HasActive => !_node.IsNull;

    /// <summary>Milliseconds until the caret's next toggle — the blinker's own <c>Cadence</c> answer, the same shape
    /// as <see cref="AnimEngine.NextDueMs(double)"/>. <c>+∞</c> when no editor is focused (nothing to wake for);
    /// otherwise the remainder of the current half-period, <c>0</c> once it is owed. The host takes the min of this
    /// and the animation wake instead of pinning the loop to panel rate for a blinking caret — a 500ms half-period is
    /// two frames a second, not sixty.</summary>
    public float NextDueMs()
    {
        if (_node.IsNull || _intervalMs <= 0f) return float.PositiveInfinity;
        float remaining = _intervalMs - _elapsed;
        return remaining > 0f ? remaining : 0f;
    }

    /// <summary>Begin blinking for the (newly focused) editor's text node: caret shown, blink phase reset.
    /// <paramref name="blinkMs"/> is the half-period (<c>GetCaretBlinkTime</c>); ≤ 0 falls back to the default.</summary>
    public void Focus(NodeHandle textNode, float blinkMs = DefaultBlinkMs)
    {
        if (textNode.IsNull || !_scene.IsLive(textNode)) return;
        if (!_node.IsNull && _node != textNode) Blur(_node);   // singular focus: the previous editor stops blinking
        _node = textNode;
        _intervalMs = blinkMs > 0f ? blinkMs : DefaultBlinkMs;
        _elapsed = 0f;
        ref TextEditState tes = ref _scene.TextEditRef(textNode);
        tes.Flags |= TextEditState.CaretVisible | TextEditState.Focused;
        _scene.Mark(textNode, NodeFlags.PaintDirty);
    }

    /// <summary>Stop blinking for this editor (focus lost): caret hidden, <see cref="TextEditState.Focused"/> cleared.</summary>
    public void Blur(NodeHandle textNode)
    {
        if (textNode == _node) { _node = NodeHandle.Null; _elapsed = 0f; }
        if (textNode.IsNull || !_scene.IsLive(textNode) || !_scene.HasTextEdit(textNode)) return;
        ref TextEditState tes = ref _scene.TextEditRef(textNode);
        tes.Flags &= unchecked((byte)~(TextEditState.CaretVisible | TextEditState.Focused));
        _scene.Mark(textNode, NodeFlags.PaintDirty);
    }

    /// <summary>Blur whichever editor is blinking (window deactivated / overlay swallowed focus).</summary>
    public void BlurAll()
    {
        if (!_node.IsNull) Blur(_node);
    }

    /// <summary>An edit happened: the caret snaps visible and the blink phase restarts (WinUI/Win32 behavior —
    /// the caret never blinks away mid-typing).</summary>
    public void ResetBlink(NodeHandle textNode)
    {
        if (textNode.IsNull || textNode != _node) return;
        if (!_scene.IsLive(textNode)) { _node = NodeHandle.Null; return; }
        _elapsed = 0f;
        ref TextEditState tes = ref _scene.TextEditRef(textNode);
        if ((tes.Flags & TextEditState.CaretVisible) == 0)
        {
            tes.Flags |= TextEditState.CaretVisible;
            _scene.Mark(textNode, NodeFlags.PaintDirty);
        }
    }

    /// <summary>Phase-7 tick: accumulate; on each elapsed half-period toggle the caret bit. A slow frame spanning
    /// several half-periods nets the parity (an even count is a visual no-op — no spurious repaint).</summary>
    public void Tick(float dtMs)
    {
        if (_node.IsNull) return;
        if (!_scene.IsLive(_node)) { _node = NodeHandle.Null; return; }   // dead node (subtree freed) → drop

        _elapsed += dtMs;
        if (_intervalMs <= 0f || _elapsed < _intervalMs) return;
        int flips = 0;
        while (_elapsed >= _intervalMs) { _elapsed -= _intervalMs; flips++; }
        if ((flips & 1) == 0) return;

        ref TextEditState tes = ref _scene.TextEditRef(_node);
        tes.Flags ^= TextEditState.CaretVisible;
        _scene.Mark(_node, NodeFlags.PaintDirty);
    }
}
