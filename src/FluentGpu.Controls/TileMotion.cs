using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The press lean of <see cref="TileMotion.Tilt"/>: the scale while pressed, the largest lean (a fraction of the box
/// the tile travels toward the press point, at most 2 DIP), and the release time.</summary>
public readonly record struct TiltSpec(float Scale = 0.97f, float MaxLean = 0.02f, float ReleaseMs = 120f)
{
    /// <summary>The stock lean. A struct's <c>new TiltSpec()</c> skips the primary constructor's defaults, so without this every tile tilted
    /// with the default spec shrank toward scale 0 for the 90 ms press ease (a quarter-size tile on the press frame) and snapped back.</summary>
    public TiltSpec() : this(0.97f, 0.02f, 120f) { }
}

/// <summary>Zune's tile motion in 2-D (the engine's transform is a 2x3 affine; perspective is out of scope, owner call 2 of the
/// pinned-Home plan).</summary>
public static class TileMotion
{
    /// <summary>Leans the child toward the press point while pressed: the tile shrinks to <see cref="TiltSpec.Scale"/> and
    /// travels 1-2 DIP toward the point, then eases back over <see cref="TiltSpec.ReleaseMs"/> when the press ends. Animated
    /// channels on the tile's own node (no relayout, no render per move: one render per press edge). It hears every press
    /// inside the tile, the tile's own buttons included. Reduced motion: none (the child is returned as is).</summary>
    public static Element Tilt(Element child, TiltSpec? spec = null)
        => Motion.ReducedMotion ? child : Embed.Comp(new TiltView.Props(child, spec ?? new TiltSpec()), () => new TiltView());

    /// <summary>Shows <paramref name="back"/> instead of <paramref name="front"/> when <paramref name="turned"/> is true: scale X
    /// 1 -> 0 -> 1 with the swap at the half (<paramref name="turnMs"/> to the back, <paramref name="backMs"/> to the front).
    /// Reduced motion: a cross-fade. One face is mounted at a time; the swap happens at the half, where the tile is edge-on.</summary>
    public static Element TurnOver(IReadSignal<bool> turned, Func<Element> front, Func<Element> back,
        float turnMs = 420f, float backMs = 360f)
        => Embed.Comp(new TurnOverView.Props(turned, front, back, turnMs, backMs), () => new TurnOverView());
}

internal sealed class TiltView : Component
{
    public sealed record Props(Element Child, TiltSpec Spec);

    readonly Signal<bool> _tilted = new(false);
    readonly Action<PointerEventArgs> _onPressed;
    readonly Action<NodeHandle> _onRealized;
    readonly Action<RectF> _onBounds;
    readonly Action _poll;
    NodeHandle _node;
    float _w, _h;
    TiltSpec _spec;
    InputHooks? _hooks;

    public TiltView()
    {
        _onPressed = OnPressed;
        _onRealized = h => _node = h;
        _onBounds = r => { _w = r.W; _h = r.H; };
        _poll = Poll;
    }

    public override Element Render()
    {
        var p = UseProps<Props>();
        _spec = p.Spec;
        _hooks = UseContext(InputHooks.Current);
        bool tilted = _tilted.Value;
        UseInterval(_poll, 32f, enabled: tilted);   // polls the press's end (no per-move work)
        return new BoxEl
        {
            Direction = 1, Grow = 1f,
            // a press LISTENER: notified of any press inside it without taking the hit from the tile's own controls
            HoverScopeTransparent = true,
            OnPointerPressed = _onPressed,
            OnRealized = _onRealized,
            OnBoundsChanged = _onBounds,
            Children = [p.Child],
        };
    }

    void OnPressed(PointerEventArgs e)
    {
        if (Context.Anim is not { } anim || Context.Scene is not { } s || _node.IsNull || !s.IsLive(_node) || _w <= 0f || _h <= 0f) return;
        float px = Math.Clamp(e.Local.X / _w - 0.5f, -0.5f, 0.5f), py = Math.Clamp(e.Local.Y / _h - 0.5f, -0.5f, 0.5f);
        float tx = px * MathF.Min(2f, _spec.MaxLean * _w * 2f), ty = py * MathF.Min(2f, _spec.MaxLean * _h * 2f);
        var press = MotionTokenDef.Eased(90f, Easing.FluentDecelerate);
        anim.SeedValue(_node, AnimChannel.ScaleX, _spec.Scale, press);
        anim.SeedValue(_node, AnimChannel.ScaleY, _spec.Scale, press);
        anim.SeedValue(_node, AnimChannel.TranslateX, tx, press);
        anim.SeedValue(_node, AnimChannel.TranslateY, ty, press);
        _tilted.Value = true;
    }

    void Poll()
    {
        if (_hooks?.IsPointerHeld?.Invoke() == true) return;
        Release();
    }

    void Release()
    {
        _tilted.Value = false;
        if (Context.Anim is not { } anim || Context.Scene is not { } s || _node.IsNull || !s.IsLive(_node)) return;
        var back = MotionTokenDef.Eased(_spec.ReleaseMs, Easing.FluentDecelerate);
        anim.SeedValue(_node, AnimChannel.ScaleX, 1f, back);
        anim.SeedValue(_node, AnimChannel.ScaleY, 1f, back);
        anim.SeedValue(_node, AnimChannel.TranslateX, 0f, back);
        anim.SeedValue(_node, AnimChannel.TranslateY, 0f, back);
    }
}

internal sealed class TurnOverView : Component
{
    public sealed record Props(IReadSignal<bool> Turned, Func<Element> Front, Func<Element> Back, float TurnMs, float BackMs);

    static readonly EnterExit Fade = new(Opacity: 0f, Active: true);
    readonly Signal<bool> _face = new(false);     // the face on show (true: the back)
    readonly Action<NodeHandle> _onRealized;
    readonly Action _half;
    readonly Action _onTurned;
    NodeHandle _node;
    bool _seeded;
    bool _target;
    float _halfMs;
    TimerHandle _timer;
    Props? _p;

    public TurnOverView()
    {
        _onRealized = h => _node = h;
        _half = Half;
        _onTurned = OnTurned;
    }

    public override Element Render()
    {
        var p = UseProps<Props>();
        _p = p;
        bool turned = p.Turned.Value;
        if (!_seeded) { _seeded = true; _target = turned; _face.Value = turned; }
        _timer = UseTimeout(_half, 0f);
        UseEffect(_onTurned, turned);
        bool face = _face.Value;
        if (Motion.ReducedMotion)
        {
            // a cross-fade: each face keyed in a children list with a fade in and out
            return new BoxEl
            {
                ZStack = true, Grow = 1f, OnRealized = _onRealized,
                Children = [face
                    ? new BoxEl { Key = "turn:back", Direction = 1, Grow = 1f, Enter = Fade, Exit = Fade, Children = [p.Back()] }
                    : new BoxEl { Key = "turn:front", Direction = 1, Grow = 1f, Enter = Fade, Exit = Fade, Children = [p.Front()] }],
            };
        }
        return new BoxEl
        {
            Direction = 1, Grow = 1f, OnRealized = _onRealized,
            Children = [face ? p.Back() : p.Front()],
        };
    }

    void OnTurned()
    {
        if (_p is null) return;
        bool turned = _p.Turned.Peek();
        if (turned == _target) return;
        _target = turned;
        if (Motion.ReducedMotion) { _face.Value = turned; return; }
        float total = turned ? _p.TurnMs : _p.BackMs;
        _halfMs = total * 0.5f;
        if (Context.Anim is { } anim && Context.Scene is { } s && !_node.IsNull && s.IsLive(_node))
            anim.SeedValue(_node, AnimChannel.ScaleX, 0f, MotionTokenDef.Eased(_halfMs, Easing.Linear));
        _timer.RestartIn(_halfMs);
    }

    void Half()
    {
        if (_p is null || _face.Peek() == _target) return;
        _face.Value = _target;   // swap at the half, edge-on
        if (Context.Anim is { } anim && Context.Scene is { } s && !_node.IsNull && s.IsLive(_node))
            anim.SeedValue(_node, AnimChannel.ScaleX, 1f, MotionTokenDef.Eased(_halfMs, Easing.FluentDecelerate), from: 0f);
    }
}
