using System;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The drag-to-resize grip on the sidebar's right-edge seam. Works with mouse AND touch: BoxEl.OnDrag uses the engine's
// eager pointer capture (the Slider/EditableText scrub default, which captures on touch-down too), so it keeps firing
// as the pointer leaves the thin grip; the OnDrag node's OnClick is its release/commit edge (the drag-end). Because the
// grip MOVES with the width, the true cursor window-X is reconstructed each frame as local.X + AbsoluteRect(grip).X.
//
// DETENT: above SnapThreshold the pane resizes 1:1 (clamped). Below it, the pane RESISTS (sticks) and the content fades
// to cue the impending collapse; only a FORCE-PUSH past the threshold actually collapses to the compact rail. Expanding
// out of compact needs a deliberate pull past a higher re-expand point (hysteresis) so it doesn't flicker at the seam.
sealed class SidebarResizeGrip : Component
{
    // Tuning (DIP) — all empirical, safe to tweak live.
    const float CompactWidth = 56f;     // the collapsed rail width (matches WaveeShell's pane)
    const float MinWidth = 240f, MaxWidth = 460f;
    const float SnapThreshold = 240f;   // = MinWidth: at/below this the pane enters the sticky resist zone
    const float ForcePush = 44f;        // push this far past the threshold (→ rawW < 196) to actually collapse
    const float Resist = 0.28f;         // residual shrink inside the resist zone (lower = stickier)
    const float ReExpand = 210f;        // from compact, pull rawW past this to re-expand (sits above the 196 collapse → hysteresis)
    const float MinFade = 0.35f;        // content-opacity floor at the collapse edge

    readonly Signal<bool> _compact, _dragging;
    readonly Signal<float> _width, _fade;
    readonly Action _onCommit;

    NodeHandle _self;
    float _startW, _startPx;
    bool _startedCompact;

    public SidebarResizeGrip(Signal<bool> compact, Signal<float> width, Signal<bool> dragging, Signal<float> fade, Action onCommit)
    {
        _compact = compact; _width = width; _dragging = dragging; _fade = fade; _onCommit = onCommit;
    }

    public override Element Render() => new BoxEl
    {
        // Wide (touch-friendly) hit area extending into the content side. It is deliberately PAINT-FREE: discovery is
        // via the SizeWE cursor, while the invisible lane can never show as a page-height strip over home/detail content.
        // Touch needs no visual hover affordance.
        // Grow=1f fills the parent's (literal column wrapper in WaveeShell) vertical main axis — an Embed.Comp host node
        // is a column that does NOT cross-stretch its child's HEIGHT, so without this the grip arranges to 0px tall and
        // is never hit-tested (no hover/cursor/drag). The wrapper's fixed Width=16 contains this Grow to the Y axis.
        Width = 16f, Grow = 1f, Direction = 1,
        Cursor = CursorId.SizeWE,
        OnRealized = h => _self = h,
        OnPointerDown = OnDown,
        OnDrag = OnMove,
        OnClick = OnReleased,                 // an OnDrag node's click handler IS its release/commit edge (the drag-end)
        OnDragCanceled = OnCanceled,
        Children = [],
    };

    void OnDown(Point2 local)
    {
        var scene = Context.Scene;
        if (scene is null || _self.IsNull || !scene.IsLive(_self)) return;
        // The first drag move can be batched with pointer-down in the same frame. Set the snap gate synchronously here
        // so ApplyProjections never sees the collapse spring for live width writes.
        Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, true);
        _dragging.Value = true;
        _startedCompact = _compact.Peek();
        _startW = _startedCompact ? CompactWidth : _width.Peek();
        _startPx = local.X + scene.AbsoluteRect(_self).X;
    }

    void OnMove(Point2 local)
    {
        var scene = Context.Scene;
        if (scene is null || _self.IsNull || !scene.IsLive(_self)) return;
        float px = local.X + scene.AbsoluteRect(_self).X;   // reconstruct true window-X (grip moves as the pane resizes)
        float rawW = _startW + (px - _startPx);

        if (_startedCompact)
        {
            // Currently collapsed: only a deliberate pull past the re-expand point opens it (hysteresis).
            if (rawW >= ReExpand)
            {
                _startedCompact = false;
                _compact.Value = false;
                _width.Value = Math.Clamp(rawW, MinWidth, MaxWidth);
                _fade.Value = 1f;
            }
            return;
        }

        if (rawW >= SnapThreshold)
        {
            _compact.Value = false;
            _width.Value = Math.Clamp(rawW, MinWidth, MaxWidth);
            _fade.Value = 1f;
            return;
        }

        // Resist zone: the pane sticks (shrinks only a little) and the content fades; force-push past → collapse.
        float into = SnapThreshold - rawW;                          // how far into the zone (>0)
        _width.Value = SnapThreshold - into * Resist;               // sticky width
        _fade.Value = Math.Clamp(1f - (into / ForcePush) * (1f - MinFade), MinFade, 1f);
        if (into >= ForcePush)
        {
            _compact.Value = true;
            _startedCompact = true;                                  // further drag in THIS gesture now uses re-expand
            _fade.Value = 1f;
        }
    }

    void OnReleased()
    {
        // Release geometry suppression before the discrete detent clamp so the final settle can use its authored recipe.
        Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, false);
        // A release inside the resist zone (didn't force-push to collapse) settles to the min expanded width rather than
        // persisting the slightly-sub-min sticky value.
        if (!_compact.Peek()) _width.Value = Math.Clamp(_width.Peek(), MinWidth, MaxWidth);
        _fade.Value = 1f;        // settle the content opacity (collapsed shows the full rail; expanded shows full content)
        _onCommit();             // persist the chosen width + collapsed state (drag-end)
        _dragging.Value = false;
    }

    void OnCanceled()
    {
        Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, false);
        _fade.Value = 1f;
        _dragging.Value = false;
    }
}
