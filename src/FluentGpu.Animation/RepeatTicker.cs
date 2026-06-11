using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// Drives WinUI RepeatButton auto-repeat: while a repeat node is held, its click handler is invoked once on press, then
/// after an initial delay, then at a fixed interval. A host system armed by the dispatcher on press / released on up
/// (the <see cref="InteractionAnimator"/>/<see cref="ScrollAnimator"/> idiom), ticked once per frame in the host loop and
/// reported through <see cref="HasActive"/> so frames keep flowing while held — and stop the instant it's released
/// (no busy loop). Zero alloc on a steady frame (invokes the already-stored click <see cref="Action"/>).
/// Delay/Interval come from the node (<c>InteractionInfo.RepeatDelayMs/RepeatIntervalMs</c>; NaN/non-positive = the
/// WinUI DP defaults, validated positive like RepeatButton_Partial.cpp:149-182). While the held pointer leaves the
/// node the ticker PAUSES; re-entry resumes with a fresh initial delay and never an immediate re-fire
/// (RepeatButton_Partial.cpp:530-548, :565-574).
/// </summary>
public sealed class RepeatTicker
{
    private readonly SceneStore _scene;
    private NodeHandle _node;
    private float _elapsed;
    private bool _repeating;   // false = in the initial-delay window; true = steady repeat
    private bool _paused;      // held pointer is off the node — no ticking until Resume re-arms the delay

    public RepeatTicker(SceneStore scene) => _scene = scene;

    /// <summary>True while armed AND ticking — a paused ticker needs no frames (the resuming pointer move wakes it).</summary>
    public bool HasActive => !_node.IsNull && !_paused;

    // WinUI RepeatButton DP metadata defaults: Delay = 500ms, Interval = 33ms
    // (microsoft-ui-xaml dxaml\xcp\components\dependencyObject\DependencyProperty.cpp:714-720).
    private const float DefaultDelayMs = 500f;
    private const float DefaultIntervalMs = 33f;

    private float DelayFor(NodeHandle n)
    {
        float d = _scene.Interaction(n).RepeatDelayMs;
        return float.IsNaN(d) || d <= 0f ? DefaultDelayMs : d;
    }

    private float IntervalFor(NodeHandle n)
    {
        float v = _scene.Interaction(n).RepeatIntervalMs;
        return float.IsNaN(v) || v <= 0f ? DefaultIntervalMs : v;
    }

    /// <summary>Begin auto-repeat for <paramref name="node"/>: fire the click once now, then schedule the repeat.</summary>
    public void Arm(NodeHandle node)
    {
        if (node.IsNull || !_scene.IsLive(node)) return;
        _node = node;
        _elapsed = 0f;
        _repeating = false;
        _paused = false;
        Fire();
    }

    /// <summary>Stop auto-repeat (on pointer-up / key-up / cancel) if <paramref name="node"/> is the active one.</summary>
    public void Disarm(NodeHandle node)
    {
        if (node == _node) { _node = NodeHandle.Null; _paused = false; }
    }

    /// <summary>The held pointer left the armed node: stop ticking (idempotent; stays armed for a Resume).</summary>
    public void Pause(NodeHandle node)
    {
        if (node == _node) _paused = true;
    }

    /// <summary>The held pointer re-entered the armed node: restart with a FRESH initial delay — never an immediate
    /// re-fire (idempotent while already running).</summary>
    public void Resume(NodeHandle node)
    {
        if (node != _node || !_paused) return;
        _paused = false;
        _elapsed = 0f;
        _repeating = false;
    }

    public void Tick(float dtMs)
    {
        if (_node.IsNull || _paused) return;
        if (!_scene.IsLive(_node)) { _node = NodeHandle.Null; return; }

        _elapsed += dtMs;
        if (!_repeating)
        {
            float delay = DelayFor(_node);
            if (_elapsed >= delay) { _elapsed -= delay; _repeating = true; Fire(); }
            return;
        }
        // Steady repeat — fire for every elapsed interval (handles a slow frame firing multiple times).
        float interval = IntervalFor(_node);
        while (_elapsed >= interval && !_node.IsNull && !_paused)
        {
            _elapsed -= interval;
            Fire();
        }
    }

    private void Fire()
    {
        if (_node.IsNull || !_scene.IsLive(_node)) { _node = NodeHandle.Null; return; }
        _scene.GetClickHandler(_node)?.Invoke();
    }
}
