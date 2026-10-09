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
    private double _lastTickMs = double.NaN;   // timer-clock stamp of the last Tick; NaN = the next Tick anchors the phase

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
        _lastTickMs = double.NaN;
        ref TextEditState tes = ref _scene.TextEditRef(textNode);
        tes.Flags |= TextEditState.CaretVisible | TextEditState.Focused;
        _scene.Mark(textNode, NodeFlags.PaintDirty);
    }

    /// <summary>Stop blinking for this editor (focus lost): caret hidden, <see cref="TextEditState.Focused"/> cleared.</summary>
    public void Blur(NodeHandle textNode)
    {
        if (textNode == _node) { _node = NodeHandle.Null; _elapsed = 0f; _lastTickMs = double.NaN; }
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
        _lastTickMs = double.NaN;   // re-anchor with the phase: idle time before this keystroke is not blink time
        ref TextEditState tes = ref _scene.TextEditRef(textNode);
        if ((tes.Flags & TextEditState.CaretVisible) == 0)
        {
            tes.Flags |= TextEditState.CaretVisible;
            _scene.Mark(textNode, NodeFlags.PaintDirty);
        }
    }

    /// <summary>Phase-7 tick on the host's TIMER clock (<paramref name="nowMs"/> = <c>HostTimerQueue.NowMs</c>: the
    /// monotonic wall clock on a real window, the accumulated fixed frame step headless), never the frame delta. The
    /// host sleeps <see cref="NextDueMs"/> of WALL time between blinks while the frame delta is clamped to 34 ms, so a
    /// delta-fed blinker gained 34 ms per ~500 ms wake and toggled only every ~4 s. The first tick after
    /// <see cref="Focus"/> anchors the phase. On each elapsed half-period the caret bit toggles; a long stall spanning
    /// several half-periods nets the parity (an even count is a visual no-op — no spurious repaint).</summary>
    public void Tick(double nowMs)
    {
        if (_node.IsNull) return;
        if (!_scene.IsLive(_node)) { _node = NodeHandle.Null; return; }   // dead node (subtree freed) → drop

        double last = _lastTickMs;
        _lastTickMs = nowMs;
        if (double.IsNaN(last) || nowMs <= last || _intervalMs <= 0f) return;
        double elapsed = _elapsed + (nowMs - last);
        if (elapsed < _intervalMs) { _elapsed = (float)elapsed; return; }
        long flips = (long)(elapsed / _intervalMs);          // a hidden/suspended window can return hours later: no loop
        _elapsed = (float)(elapsed - flips * (double)_intervalMs);
        if ((flips & 1) == 0) return;

        ref TextEditState tes = ref _scene.TextEditRef(_node);
        tes.Flags ^= TextEditState.CaretVisible;
        _scene.Mark(_node, NodeFlags.PaintDirty);
    }
}
