using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// One line (a text, or a row of content such as a line of artist links) that may be longer than its slot. It shows the start of the
/// line and, when the line does not fit, scrolls it now and then so it can be read in full. ONE behaviour for every theme (the owner's
/// marquee spec, audit B11, 2026-10-24, from MarqueeLabel's "continuous" type, the music-ticker bugs people report and WCAG 2.2.2); a
/// theme picks only its look (<see cref="Style"/>: type, ink, speed, gap, fade), never the logic:
/// <list type="number">
/// <item><b>At rest the START shows.</b> Only the trailing edge is cut, under a fade; the leading edge never fades at rest. With reduced
/// motion the line rests there for good and carries its full text in a tooltip.</item>
/// <item><b>Only an overflowing line moves.</b> A line that fits stays still: no fade, no motion, no frame. The slot and the run are
/// re-measured whenever the width or the text changes.</item>
/// <item><b>A pass is one seamless loop.</b> The run and a copy of it, one <see cref="Style.Gap"/> apart (3 em by default), travel left at a
/// constant <see cref="Style.Speed"/> (DIP per second, so a longer line takes longer, never faster). A pass ends exactly where it began, the
/// copy standing where the run stood, so there is never a jump back.</item>
/// <item><b>Pauses.</b> <see cref="Style.StartDelayMs"/> at the start before the first pass (after the line appears or its content
/// changes), <see cref="Style.PauseMs"/> at the start between passes.</item>
/// <item><b>Bounded.</b> <see cref="Style.Passes"/> passes after a content change, then the line rests at its start (no endless ticker:
/// WCAG 2.2.2). Hovering a line runs a fresh pass after <see cref="Style.HoverDelayMs"/> and keeps passing while the pointer stays; when
/// it leaves, the pass under way finishes and the line rests. Both edges fade only while the line is moving and cut on both sides.</item>
/// <item><b>One mover per surface.</b> Marquees that share a <see cref="Group"/> (a bar's title and artists) never move at once: the
/// lower <c>order</c> goes first, the next takes its turn when the line is free.</item>
/// <item><b>Restart on a real content change only</b>: a new text or a new <c>identity</c> (a new track of the same name); in a group a change
/// on any member renews the others' passes.
/// A re-render, a colour or theme tweak, or a resize that keeps the line overflowing keeps the phase; a resize that ends the overflow
/// glides the line home.</item>
/// <item><b>Unseen, it stops.</b> A covered, minimised or hidden window, a parked page or a collapsed subtree (and an
/// <c>allowed</c> gate that reads false) send the line home and hold its passes; when it is seen again it starts over from the start
/// pause. A line scrolled out of the window spends its passes unseen, on the timer alone (no row, no frame).</item>
/// <item><b>Free at rest, zero allocation while moving.</b> A pass is one keyframe row on the scroller's TranslateX (composited on the
/// render thread, frame-rate independent and sub-pixel); the edge fade follows it there (<see cref="EdgeFadeSpec.OverflowPeriod"/>). The
/// pauses are host timers, so between passes and at rest the line asks for no frame and holds no wake reason.</item>
/// </list>
/// </summary>
public static class Marquee
{
    /// <summary>A marquee's look and pace. The behaviour is the control's; a theme sets the type, the ink, <see cref="Speed"/>,
    /// <see cref="Gap"/> and <see cref="FadeBand"/> from its own tokens.</summary>
    public sealed record Style
    {
        /// <summary>The text size (DIP). In a <see cref="Content"/> marquee it sizes only the default <see cref="Gap"/>.</summary>
        public float FontSize { get; init; } = 14f;
        public ushort Weight { get; init; } = 400;
        /// <summary>The text colour, forwarded to the inner <c>TextEl.Color</c>: a static colour or a bind (it repaints without a render).</summary>
        public Prop<ColorF> Foreground { get; init; } = Prop.Of(static () => Tok.TextPrimary);
        public string? FontFamily { get; init; }
        /// <summary>The line height (DIP, NaN = the font's natural line), forwarded to the inner <c>TextEl.LineHeight</c>.</summary>
        public float LineHeight { get; init; } = float.NaN;
        /// <summary>The tracking (1/1000 em, negative = tighter), forwarded to the inner <c>TextEl.CharSpacing</c>.</summary>
        public float CharSpacing { get; init; }
        /// <summary>The pace of a pass, DIP per second, constant: calm enough to read along.</summary>
        public float Speed { get; init; } = 30f;
        /// <summary>The space (DIP) between the run and its copy. NaN = 3 em of <see cref="FontSize"/>.</summary>
        public float Gap { get; init; } = float.NaN;
        /// <summary>The edge fade's depth (DIP; at most 30 % of the slot). 0 = a hard edge.</summary>
        public float FadeBand { get; init; } = 12f;
        /// <summary>The edge fade's intensity 0..1 (1 = fades fully to transparent).</summary>
        public float FadeStrength { get; init; } = 1f;
        /// <summary>The hold at the start before the first pass, after the line appears or its content changes.</summary>
        public float StartDelayMs { get; init; } = 2500f;
        /// <summary>The hold at the start between two passes.</summary>
        public float PauseMs { get; init; } = 2000f;
        /// <summary>How many passes a content change earns before the line rests for good (hover still runs more).</summary>
        public int Passes { get; init; } = 2;
        /// <summary>The pointer over a resting, overflowing line: a fresh pass after this.</summary>
        public float HoverDelayMs { get; init; } = 450f;

        internal float GapDip => float.IsNaN(Gap) ? 3f * FontSize : MathF.Max(0f, Gap);
    }

    public static readonly Style Default = new();

    /// <summary>The key of a marquee's moving strip (its presented x is the pass's offset): what a trace watches, and what tells a harness
    /// that a running row is a marquee's pass (bounded, but ambient like a loop: a few passes after a change, more under the pointer).</summary>
    public const string StripKey = "marquee-strip";

    /// <summary>Marquees on one surface (a bar's title and its artist line) that must never move at the same time. Create one per surface
    /// (a <c>UseMemo</c> / <c>UseRef</c> in the surface's component) and hand it to each line with its <c>order</c>: the lower order passes
    /// first; a line whose turn comes while another is moving waits for it, then passes after a short beat. A content change on any
    /// member restarts every member's passes (a new track re-announces the whole surface).</summary>
    public sealed class Group
    {
        readonly List<MarqueeRunner> _members = new(4);
        MarqueeRunner? _moving;

        internal void Join(MarqueeRunner r) { if (!_members.Contains(r)) _members.Add(r); }

        internal void Leave(MarqueeRunner r)
        {
            _members.Remove(r);
            Release(r);
        }

        /// <summary>Take the surface's one moving slot: false while another member moves.</summary>
        internal bool TryTake(MarqueeRunner r)
        {
            if (_moving is not null && _moving != r) return false;
            _moving = r;
            return true;
        }

        /// <summary>Free the slot (no-op unless <paramref name="r"/> holds it) and hand it to the waiting member of lowest order.</summary>
        internal void Release(MarqueeRunner r)
        {
            if (_moving != r) return;
            _moving = null;
            MarqueeRunner? next = null;
            foreach (var m in _members)
                if (m.IsWaiting && (next is null || m.Order < next.Order)) next = m;
            next?.Granted();
        }

        internal void TopUpOthers(MarqueeRunner r)
        {
            foreach (var m in _members)
                if (m != r) m.TopUp();
        }
    }

    /// <summary>A marquee line of text. It fills the width its parent gives it (cross-axis stretch in a column, <c>Grow</c> in a row).
    /// <paramref name="text"/> is a reactive channel: a plain string for a fixed or re-rendered label, a bind (<c>Prop.Of(() =&gt; …)</c> or
    /// a signal) for a live one such as a now-playing title. <paramref name="group"/> and <paramref name="order"/> stagger it with the
    /// surface's other marquees; <paramref name="allowed"/> is an optional gate (false = rest at the start, as when unseen);
    /// <paramref name="identity"/> names what the line states (a now-playing track): a new identity restarts the passes even when the
    /// words are the same (another track of the same name).</summary>
    public static BoxEl Of(Prop<string> text, Style? style = null, Group? group = null, int order = 0, IReadSignal<bool>? allowed = null,
                           long identity = 0)
        => Frame(new MarqueeProps(text, null, style ?? Default, group, order, allowed, identity));

    /// <summary>A marquee row of arbitrary content (a line of links). <paramref name="content"/> is drawn twice while the line moves (the
    /// run and its copy), so it must be cheap and keyed only inside itself; mount a component through <c>Embed.Comp(props, …)</c> so a
    /// re-render re-pushes its props. <paramref name="text"/> is the same line as plain words: its change is what restarts the passes, and it
    /// is the tooltip when the line cannot move.</summary>
    public static BoxEl Content(Element content, Prop<string> text, Style? style = null, Group? group = null, int order = 0,
                                IReadSignal<bool>? allowed = null, long identity = 0)
        => Frame(new MarqueeProps(text, content, style ?? Default, group, order, allowed, identity));

    static BoxEl Frame(MarqueeProps props) => new()
    {
        // A column: the line (the tooltip's wrapper, then the clip) stretches to the slot's width and grows into its height, so the text sits
        // centred in whatever height the caller gives the line, and the clip is that full height (descenders past a tight line box show).
        Direction = 1, MinWidth = 0f, Grow = 1f, Shrink = 1f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
        Children = [Embed.Comp(props, static () => new MarqueeView())],
    };
}

/// <summary>What a marquee is given (re-pushed every parent render: a new static text reaches the line).</summary>
internal sealed record MarqueeProps(Prop<string> Text, Element? Content, Marquee.Style Sty, Marquee.Group? Group, int Order,
                                    IReadSignal<bool>? Allowed, long Identity);

/// <summary>The line: the tooltip (only while it is cut and cannot move), the clip with its edge fade and hover, and the moving strip
/// (the run, the gap and, while the line may move, the copy). It re-renders on a measurement, a text, a visibility edge or a pass's start
/// and end, never per frame; the pass itself and its fade run on the render thread.</summary>
internal sealed class MarqueeView : Component
{
    const float MaxFadeViewportFraction = 0.3f;

    public override Element Render()
    {
        var p = UseProps<MarqueeProps>();
        var sty = p.Sty;
        var hooks = UseContext(InputHooks.Current);
        var active = UseIsActive();
        var viewW = UseSignal(0f);      // the slot (the clip's width)
        var runW = UseSignal(0f);       // one run's natural width
        var copyAt = UseSignal(0f);     // where the copy starts in the strip: the pass's distance, as laid out
        var moving = UseSignal(false);  // a pass (or a glide home) is under way: the copy stays while it is
        var strip = UseRef(NodeHandle.Null);
        var clip = UseRef(NodeHandle.Null);
        var cell = UseRef<MarqueeRunner?>(null);
        bool mount = cell.Value is null;
        var run = cell.Value ??= new MarqueeRunner(moving, strip, clip);
        var timer = UseTimeout(run.Fire, 1f);
        if (mount) timer.Cancel();      // armed only by the runner
        run.Bind(Context.Anim, Context.Scene, timer, sty, p.Order);
        UseLayoutEffect(() =>
        {
            var g = p.Group;
            run.Group = g;
            g?.Join(run);
            return () => { g?.Leave(run); run.Group = null; };
        }, DepKey.FromRef(p.Group));

        string text = p.Text.Current() ?? string.Empty;
        float cw = viewW.Value, tw = runW.Value, at = copyAt.Value;
        bool overflow = cw > 0f && tw > cw + 0.5f;
        bool canMove = !Motion.ReducedMotion;
        bool seen = hooks.WindowOccluded?.Value != true && active.Value && (p.Allowed?.Value ?? true);
        bool copy = canMove && (overflow || moving.Value);
        float period = copy && at > tw ? at : 0f;
        int flags = (overflow ? 1 : 0) | (canMove ? 2 : 0) | (seen ? 4 : 0);
        long identity = p.Identity;
        UseLayoutEffect(() => run.Sync(text, identity, overflow, period, canMove, seen),
                        DepKey.Combine(DepKey.From(text.GetHashCode(), BitConverter.SingleToInt32Bits(period), flags, 0), DepKey.From(identity)));

        float band = overflow ? MathF.Min(sty.FadeBand, cw * MaxFadeViewportFraction) : 0f;
        EdgeFadeSpec? fade = band > 0.5f
            ? new EdgeFadeSpec(EdgeMask.Horizontal, band, FadeFalloff.Smoothstep, sty.FadeStrength)
            {
                OverflowTail = tw - cw, OverflowPeriod = period, OverflowRun = period > 0f ? tw : 0f,
            }
            : null;

        var first = new BoxEl
        {
            Shrink = 0f,
            OnBoundsChanged = b => { if (b.W != runW.Peek()) runW.Value = b.W; },
            Children = [p.Content ?? Glyphs(p)],
        };
        Element[] kids = copy
            ?
            [
                first,
                // The gap; in a slot wider than run + gap (the overflow ended under a pass, which then glides home) the rest of the slot, so the
                // copy never stands inside the slot at the start.
                new BoxEl { Width = MathF.Max(sty.GapDip, cw - tw), Shrink = 0f },
                new BoxEl
                {
                    Shrink = 0f,
                    OnBoundsChanged = b => { if (b.X != copyAt.Peek()) copyAt.Value = b.X; },
                    Children = [p.Content is { } c ? c with { } : Glyphs(p)],
                },
            ]
            : [first];

        var host = new BoxEl
        {
            // A one-layer stack whose layer measures unbounded: the line keeps its natural width (a trimmed name inside a content row
            // would otherwise ellipsize against the slot and never overflow), centred in the slot's height, cut by the clip.
            ZStack = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, ClipToBounds = true, EdgeFade = fade,
            HoverScopeTransparent = true,
            OnRealized = h => clip.Value = h,
            OnBoundsChanged = b => { if (b.W != viewW.Peek()) viewW.Value = b.W; },
            OnHoverMove = run.HoverEnter, OnPointerExit = run.HoverLeave,
            Children =
            [
                // The strip is the clip's first child: where the fade's overflow cue reads the posed translate. Keyed for the inspector's trace.
                new BoxEl
                {
                    Key = Marquee.StripKey, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MeasureUnboundedWidth = true,
                    OnRealized = h => strip.Value = h,
                    Children = kids,
                },
            ],
        };
        // Cut and unable to move (reduced motion): the whole line in a tooltip.
        return ToolTip.Wrap(host, (Prop<string?>)(overflow && !canMove && text.Length > 0 ? text : null), grow: 1f);
    }

    static TextEl Glyphs(MarqueeProps p) => new(p.Text)
    {
        Size = p.Sty.FontSize,
        LineHeight = p.Sty.LineHeight,
        CharSpacing = p.Sty.CharSpacing,
        Weight = p.Sty.Weight,
        Color = p.Sty.Foreground,
        FontFamily = p.Sty.FontFamily,
        Wrap = TextWrap.NoWrap,
        MaxLines = 1,
    };
}

/// <summary>A marquee's pass scheduler (UI thread). It never renders: it seeds one keyframe row per pass on the strip and arms the host
/// timer for the next step (the start delay, the pass's end, the pause), so a resting or pausing line costs no frame. States: Rest (at the
/// start, nothing armed), Delay / Pause (a timer to the next pass), Waiting (its group's line is busy), Pass (a row and a timer to its end),
/// Home (a glide back after the overflow ended mid-pass).</summary>
internal sealed class MarqueeRunner
{
    enum Phase : byte { Rest, Delay, Waiting, Pass, Pause, Home }

    /// <summary>The beat between one member's pass and the next member's, and the step between the members' first passes.</summary>
    const float HandoffMs = 700f, OrderStepMs = 120f;

    readonly Signal<bool> _moving;
    readonly Ref<NodeHandle> _strip, _clip;
    AnimEngine? _anim;
    SceneStore? _scene;
    TimerHandle _timer;
    Marquee.Style _sty = Marquee.Default;
    internal Marquee.Group? Group;
    internal int Order;

    Phase _phase;
    string? _text;
    long _identity;
    bool _overflow, _canMove, _seen, _hovered;
    float _period;
    int _passesLeft;

    internal readonly Action Fire;
    internal readonly Action<Point2> HoverEnter;
    internal readonly Action HoverLeave;

    public MarqueeRunner(Signal<bool> moving, Ref<NodeHandle> strip, Ref<NodeHandle> clip)
    {
        _moving = moving;
        _strip = strip;
        _clip = clip;
        Fire = OnTimer;
        HoverEnter = _ => OnHover(true);
        HoverLeave = () => OnHover(false);
    }

    bool Live => _overflow && _canMove && _seen && _period > 0f;
    internal bool IsWaiting => _phase == Phase.Waiting;

    internal void Bind(AnimEngine? anim, SceneStore? scene, TimerHandle timer, Marquee.Style sty, int order)
    {
        _anim = anim;
        _scene = scene;
        _timer = timer;
        _sty = sty;
        Order = order;
    }

    /// <summary>The line's facts after a render's layout: its text and identity, whether it overflows, the pass distance (0 until the copy is
    /// laid out), whether motion is allowed and whether the line can be seen. Diffs them against the last and moves the state machine.</summary>
    internal void Sync(string text, long identity, bool overflow, float period, bool canMove, bool seen)
    {
        bool changed = !string.Equals(text, _text, StringComparison.Ordinal) || identity != _identity;
        float oldPeriod = _period;
        _text = text; _identity = identity; _overflow = overflow; _period = period; _canMove = canMove; _seen = seen;

        if (changed)
        {
            Restart();
            Group?.TopUpOthers(this);
            return;
        }
        if (!Live)
        {
            if (_phase == Phase.Pass)
            {
                _passesLeft++;                          // the interrupted pass is owed
                if (!seen) { Snap(); Stop(); }      // nobody sees it: straight home
                else GlideHome();                       // still on screen (the overflow or the motion ended): ease back to the start
            }
            else if (_phase != Phase.Home) Stop();
            return;
        }
        if (_phase == Phase.Pass && period != oldPeriod)
        {
            GlideHome();                                // the run was re-measured mid-pass (a theme's type): back to the start, then on
            return;
        }
        if (_phase == Phase.Rest && (_passesLeft > 0 || _hovered)) Arm(Phase.Delay, _sty.StartDelayMs + Order * OrderStepMs);
    }

    /// <summary>A new text or identity: back at its start, a fresh budget of passes after the start delay.</summary>
    void Restart()
    {
        if (_phase is Phase.Pass or Phase.Home) Snap();
        Stop();
        _passesLeft = _sty.Passes;
        if (Live) Arm(Phase.Delay, _sty.StartDelayMs + Order * OrderStepMs);
    }

    /// <summary>Another member of the group changed: this line's budget is renewed (a pass under way runs on).</summary>
    internal void TopUp()
    {
        _passesLeft = _sty.Passes;
        if (_phase == Phase.Rest && Live) Arm(Phase.Delay, _sty.StartDelayMs + Order * OrderStepMs);
    }

    /// <summary>The group's line is free and this member was waiting for it.</summary>
    internal void Granted()
    {
        if (_phase == Phase.Waiting) Arm(Phase.Delay, HandoffMs);
    }

    void OnHover(bool on)
    {
        if (_hovered == on) return;
        _hovered = on;
        if (on)
        {
            if (_phase == Phase.Rest && Live) Arm(Phase.Delay, _sty.HoverDelayMs);
        }
        else if (_passesLeft <= 0 && _phase is Phase.Delay or Phase.Pause or Phase.Waiting)
            Stop();                                     // the pointer left before the next pass: rest (a pass under way finishes)
    }

    void OnTimer()
    {
        switch (_phase)
        {
            case Phase.Delay:
            case Phase.Pause:
                StartPass();
                break;
            case Phase.Pass:
                Group?.Release(this);
                if (Live && (_hovered || _passesLeft > 0)) Arm(Phase.Pause, _sty.PauseMs);
                else Settle();
                break;
            case Phase.Home:
                Settle();
                break;
        }
    }

    void StartPass()
    {
        if (!Live || (!_hovered && _passesLeft <= 0) || _anim is null || _strip.Value.IsNull) { Stop(); return; }
        float ms = _period / MathF.Max(1f, _sty.Speed) * 1000f;
        if (!OnScreen())
        {
            // Scrolled out of the window (a list's now-playing row in the overscan): the pass is spent unseen, on the timer alone, so the
            // budget still runs out and nothing is drawn or woken per frame for a line nobody can see.
            if (_passesLeft > 0) _passesLeft--;
            _phase = Phase.Pass;
            _timer.RestartIn(ms);
            return;
        }
        if (Group is { } g && !g.TryTake(this)) { _phase = Phase.Waiting; return; }
        if (_passesLeft > 0) _passesLeft--;
        // Run and copy travel one period left; the last key puts the run back where the copy now stands (the same pixels), so the pass
        // ends at the start with no jump. One-shot: the row frees itself when it lands.
        _anim.Keyframes(_strip.Value, AnimChannel.TranslateX,
        [
            new Keyframe(0f, 0f, Easing.Linear),
            new Keyframe(1f, -_period, Easing.Linear),
            new Keyframe(1f, 0f, Easing.Linear),
        ], ms);
        if (!_moving.Peek()) _moving.Value = true;
        _phase = Phase.Pass;
        _timer.RestartIn(ms);
    }

    /// <summary>Whether any of the line's clip lies inside the window (its laid-out, scrolled rect against the root's).</summary>
    bool OnScreen()
    {
        if (_scene is not { } scene || _clip.Value.IsNull || !scene.IsLive(_clip.Value) || scene.Root.IsNull) return true;
        RectF r = scene.AbsoluteRect(_clip.Value);
        RectF win = scene.Bounds(scene.Root);
        return r.W > 0f && r.Right > win.X && r.X < win.Right && r.Bottom > win.Y && r.Y < win.Bottom;
    }

    /// <summary>Ease back to the start from wherever the pass stands (the overflow ended, or the run was re-measured mid-pass).</summary>
    void GlideHome()
    {
        Group?.Release(this);
        float from = 0f;
        if (_anim is not null && !_strip.Value.IsNull && _anim.TryGetTrackValue(_strip.Value, AnimChannel.TranslateX, out float x)) from = x;
        float dist = MathF.Abs(from);
        if (dist < 0.5f || _anim is null) { Settle(); return; }
        float ms = Math.Clamp(dist / MathF.Max(1f, _sty.Speed * 4f) * 1000f, 120f, 450f);
        _anim.Keyframes(_strip.Value, AnimChannel.TranslateX, [new Keyframe(0f, from, Easing.Linear), new Keyframe(1f, 0f, Easing.SmoothOut)], ms);
        _phase = Phase.Home;
        _timer.RestartIn(ms);
    }

    /// <summary>Straight to the start: the line is unseen, or its text changed under it.</summary>
    void Snap()
    {
        if (_anim is not null && !_strip.Value.IsNull) _anim.SeedEased(_strip.Value, AnimChannel.TranslateX, 0f, 0f, 1f, Easing.Linear);
    }

    /// <summary>At rest at the start; the next pass waits for the budget or the pointer (a glide home re-arms here when one is owed).</summary>
    void Settle()
    {
        Stop();
        if (Live && (_passesLeft > 0 || _hovered)) Arm(Phase.Delay, _sty.PauseMs);
    }

    void Stop()
    {
        _timer.Cancel();
        if (_phase is Phase.Pass or Phase.Home) Group?.Release(this);
        _phase = Phase.Rest;
        if (_moving.Peek()) _moving.Value = false;
    }

    void Arm(Phase phase, float ms)
    {
        _phase = phase;
        _timer.RestartIn(ms);
    }
}
