using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// Drives WinUI RepeatButton auto-repeat: while a repeat node is held, its click handler is invoked once on press, then
/// after an initial delay, then at a fixed interval. A host system armed by the dispatcher on press / released on up
/// (the <see cref="InteractionAnimator"/>/<see cref="ScrollAnimator"/> idiom), ticked once per frame in the host loop and
/// reported through <see cref="HasActive"/> so frames keep flowing while held — and stop the instant it's released
/// (no busy loop). Zero alloc on a steady frame (invokes the already-stored click <see cref="Action"/>).
/// </summary>
public sealed class RepeatTicker
{
    private readonly SceneStore _scene;
    private NodeHandle _node;
    private float _elapsed;
    private bool _repeating;   // false = in the initial-delay window; true = steady repeat

    public RepeatTicker(SceneStore scene) => _scene = scene;

    public bool HasActive => !_node.IsNull;

    // WinUI RepeatButton DP metadata defaults: Delay = 500ms, Interval = 33ms
    // (microsoft-ui-xaml dxaml\xcp\components\dependencyObject\DependencyProperty.cpp:714-720).
    private const float InitialDelayMs = 500f;
    private const float IntervalMs = 33f;

    /// <summary>Begin auto-repeat for <paramref name="node"/>: fire the click once now, then schedule the repeat.</summary>
    public void Arm(NodeHandle node)
    {
        if (node.IsNull || !_scene.IsLive(node)) return;
        _node = node;
        _elapsed = 0f;
        _repeating = false;
        Fire();
    }

    /// <summary>Stop auto-repeat (on pointer-up / drag-off) if <paramref name="node"/> is the active one.</summary>
    public void Disarm(NodeHandle node)
    {
        if (node == _node) _node = NodeHandle.Null;
    }

    public void Tick(float dtMs)
    {
        if (_node.IsNull) return;
        if (!_scene.IsLive(_node)) { _node = NodeHandle.Null; return; }

        _elapsed += dtMs;
        if (!_repeating)
        {
            if (_elapsed >= InitialDelayMs) { _elapsed -= InitialDelayMs; _repeating = true; Fire(); }
            return;
        }
        // Steady repeat — fire for every elapsed interval (handles a slow frame firing multiple times).
        while (_elapsed >= IntervalMs && !_node.IsNull)
        {
            _elapsed -= IntervalMs;
            Fire();
        }
    }

    private void Fire()
    {
        if (_node.IsNull || !_scene.IsLive(_node)) { _node = NodeHandle.Null; return; }
        _scene.GetClickHandler(_node)?.Invoke();
    }
}
